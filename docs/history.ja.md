# 設計判断

現行設計に至る主要な設計判断と、採用しなかった代替案の理由を残すドキュメント。時系列ではなくトピック単位で整理する。

---

## ストレージ: bytea チャンク (Large Object ではなく)

ファイル本体は PostgreSQL の Large Object ではなく、`pgfs_data` 1 行 + `pgfs_data_chunk` 複数行 (1 行 = 1 bytea = 1 チャンク、デフォルト chunk_size = 1 MiB) で保存する。

**なぜ Large Object ではなく bytea か**: `pg_largeobject` は PostgreSQL のシステムカタログなので `create_distributed_table` の対象にできない。LO だと worker を増やしても本体は永久に coordinator 1 台に集中し、容量の壁を越えられない。bytea はユーザーテーブルの列なので Citus で分散可能。副次的に、単 PG 運用でも partial-write が多くないワークロードではやや速い (LO の `lo_seek+lo_read` 2 RTT が `substring(payload from N for M)` 1 RTT になる)。

判断:
- **各チャンクの payload 長 = 「これまで書き込まれたバイト数」**。「全チャンクを chunk_size に固定 (zero パディング込み)」案も検討したが、storage 効率と `du` の accuracy を取って可変長を選択。
- **WriteData は 1 SQL/チャンクの upsert で完結**: `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END` の 3 ケース分岐 (中央 overlay / 末尾上書き / 0 パディング + 連結)。並行 WriteFile race は PG の行ロックで自動直列化。
- **`repeat(bytea, integer)` は PG に存在しない** (`repeat(text, integer)` だけ) — 0 パディングは `decode(repeat('00', N), 'hex')` で組み立てる。

実装の中核とセマンティクスは [support_for_citus.ja.md §ストレージ](support_for_citus.ja.md) を正とする (DDL / SQL パターン / TOAST partial detoast)。

廃案:
- **LO を維持して worker に分散・複製**: 独自 LO テーブルを Citus 分散するのは「ページサイズ可変の bytea」と等価なので意味なし。
- **チャンク payload を chunk_size 固定 (zero パディング込み)**: 「du の数字が見たまま」を取りたかったので可変長を選んだ。

---

## Citus による水平分散

`mkfs --citus` は `create_distributed_table` で 4 テーブルを分散し、`pgfs_settings` を local 配置にする。**1 ノード構成 (worker なし)** と **多ノード構成 (coordinator + N workers, `--worker host[:port],...`)** の両方を mkfs だけで立ち上げ可能。

分散戦略・Citus 制約の対応・mkfs フラグ・冪等性保証の詳細は [support_for_citus.ja.md §分散](support_for_citus.ja.md) を正とする (PK 複合化 / FOR UPDATE に分散キー必須 / DO UPDATE 句の IMMUTABLE 制限 / cross-shard rename DELETE+INSERT / coordinator 登録 + shouldhaveshards の独立判定 など)。「ユーザー判断」レベルの判断:

- **「単 PG モードだけ id 単独 UK を張る」分岐は採用しない**: 単 PG / Citus でスキーマがズレる、in-place 移行が UK で詰まる、application 起動時バリデーションは遅くなるだけで体感的な利益が無い、という理由で両モード共通で UK なし。BIGSERIAL の sequence + tx 規律で一意性を保つ。「将来念のため id 一意性を担保しよう」と再提案する前にこの判断を確認すること (詳細は support_for_citus.ja.md)。
- **`SetupCitusDistributionAsync` を解体し責務を分散**: クラスタトポロジ設定は `EnsureDatabaseAsync` 内側へ、per-table の `create_distributed_table` / `citus_add_local_table_to_metadata` は各 `CreateXxxTableAsync` の内側へ。`CreateTableAsync` を `Task<bool>` に変更してテーブル新規作成時のみ distribute が走るようにした (DB 既存時の Citus mutate skip と整合)。
- **各 worker への `citus_set_coordinator_host` は不要**: [multinode_probe.sh](../tests/citus/multinode_probe.sh) で「`citus_add_node` の auto-sync で worker 側 `pg_dist_node` に coordinator が自動同期される」ことを実機確認した。

テスト結果:
- mkfs マトリックステスト ([test_matrix.sh](../tests/citus/test_matrix.sh)): 3 initial × 6 target = **18/18 PASS** (Citus 14.0.0 docker, linux_client)
- 単 PG モード / 1 ノード Citus (pgsql_server, Citus 13.1.1): Linux 34/34・Windows 24/24 ALL PASSED

廃案:
- `pgfs_inode` を `id` 分散にして UK は別レイヤ保証 — `(parent_id, name)` UK が壊れる、application 層 race 防止が複雑化。
- `pgfs_settings` を distributed テーブル化 — 設定は coordinator が読み書きするだけなので不要。

---

## クロスクライアント排他制御

`pgfs_lock(target_id BIGINT PK)` 上の行ロックを使った cross-client 排他制御を [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) に組み込み。設計の詳細・組み込み箇所・lock 取得 SQL は [support_for_citus.ja.md §排他制御](support_for_citus.ja.md) を正とする。判断:

- **「自作 TTL テーブル + heartbeat」「`pg_advisory_xact_lock`」を採用しなかった理由**: TTL race / coordinator-local 制約 / Citus 分散できないため (詳細は support_for_citus.ja.md の案比較表)。
- **namespace 分離を「別テーブル」ではなく「target_id の符号」で実装**: Citus の単一カラム分散制約があるため `pgfs_lock(target_id BIGINT PK)` の 1 列構成にせざるを得ず、namespace は値の符号 (`+data_id` / `-inode_id`) で分離。「pgfs_inode_lock を別テーブルに切り出して parent_id 分散 + co-located にする」案は inode lock が支配的 workload で非対称コストが見えてきたら再編する余地として残す。
- **lock 対象範囲は docs テーブル通り**: WriteData/TruncateData/ReleaseData (data lock) / Update{Mode,Owner,Size,Timestamps} (inode lock) / Rename / DeleteInode / CreateHardLink (multi inode lock)。SetXAttr/RemoveXAttr/CreateFile/CreateDirectory/CreateSymlink は意図的に対象外 (単一 UPDATE が atomic / 親 lock を取らない理由は docs の「lock 対象外の判断」セクション)。
- **`Pg.Execute` の単発書き込みを tx ベースに昇格**: UpdateMode/Owner/Size/Timestamps の 4 メソッドは旧 `Pg.Execute(...)` の一文 SQL 構造で lock を持てなかった。`using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE + Commit に変換。
- **`LockTargets` の SQL は 2 文に分けた**: `INSERT ... ON CONFLICT DO NOTHING` + `SELECT ... FOR UPDATE`。CTE で 1 文に畳む案はシンプルさ優先で採用せず (各文とも target_id 単独 WHERE なので Citus でも単一 shard 完結)。
- **`Rename` の hintParentId stale race の許容**: cached.ParentId 取得 → LockInodes の間に他 client が rename を完了する可能性があるが、stale 検出は後続の `SELECT ... WHERE parent_id = @hint AND id = @id FOR UPDATE` が空を返す → false で抜ける動線で吸収 (誤った old parent ロックは tx 終了で自動解放)。

**検証** ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)、4/4 PASS):
- 多ノード Citus 上で Linux e2e 34/34 通過 (cross-shard hop + lock が機能している総合確認)
- 並行 write race (urandom vs zero、8MiB × 4 round) で両 client から見える md5/size が常に一致
- 並行 mkdir 同名 (20 round × serial + parallel) で常にちょうど 1 client が EEXIST
- pgfs_lock 累積: 4 テスト後 rows=178 / size=768kB (設計値「1 行 ~50 bytes、100 万行で 100MB 以下」と整合)

**副産物**: [tests/linux/e2e.sh](../tests/linux/e2e.sh) の `pg_exec` (test_fallback_uname_gname 用) に `PGFS_TEST_PG_EXEC` 環境変数オーバライドを追加。race_multinode.sh は `docker exec -i $COORD_NAME psql ...` をセットして渡す。

---

## 設定モデル

設定は **静的 `Field<T>` 記述子 + mutable POCO + `ConfigLoader` (CLI/TOML/DB/Default 統合) + `ConfigStore` (DB I/O)** で、[src/lib/src/Config/](../src/lib/src/Config/) 配下。Schema は [src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs) (reflection で全 Field を自動列挙)。

- **POCO で十分 (immutable)**: 現状のコード上、マウント中に書き換える設定は存在しない ([settings-matrix.ja.md](settings-matrix.ja.md) のライフサイクル集約参照)。これが「賢い」変更追跡ツリーではなく素朴な mutable POCO で済むと判断した論拠。
- **`pgfs_settings` はフラット `(scope, key, value)` PK**: フラットキーは recursive CTE を不要にする (旧来の階層 `(id, parent_id, key, value)` 形は読み取りに recursive CTE が要った)。ルート inode が `id = 0` を使うのも同じ理由 — BIGSERIAL は 0 を返さないので自己参照 `(id=0, parent_id=0)` 行が実在行と衝突せず、cycle 検出も不要。
- **読み込み順 CLI → TOML → DB**: 「上位ソースが既に値を入れていれば skip」方式で実現し、priority (CLI > TOML > DB > Default) を担保。chicken-and-egg (`setting.file` が TOML パスを決める) は ConfigLoader 内部で解決し、`RootConfig` は二段構築 (store=null の lite Loader で `--help` 先行判定 → `--help` 時に DB 接続しない → full Loader)。
- **永続化は明示**: `config.X.Y = newValue;` の直後に `store.Save(Schema.X.Y, newValue)` を呼ぶ。setter フックは入れない (Loader/Store 分離のため)。`Save<T>` は `INSERT ... ON CONFLICT (scope, key) DO UPDATE` で UPSERT、JSON 表現は `Field<T>.FormatJson(value)` 経由 (Int/Long/Bool はネイティブ、それ以外は `JsonSerializer.Serialize(Format(value))`)。

詳細は [architecture.ja.md §Config](architecture.ja.md)。

---

## Tmds.Fuse のフォーク

本家 `tmds/Tmds.Fuse 0.1.0-190711-50` (2019 年から更新無し) を、活発フォーク `securefolderfs-community/Tmds.Fuse` (.NET 10) に切り替え。[vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) に submodule で取り込み、[Mount.csproj](../src/mount/Mount.csproj) は ProjectReference 参照、`pgfs.sln` に追加 (Release/Debug の config 伝播のため)。

これにより本家版で踏んでいた制約 3 件を解消:
- **`MountOptions.Options` がない** → フォークが追加。`-o allow_other,attr_timeout=0,...` を libfuse に伝えられる。
- **`attr_timeout=0` を渡せない** → 既定で渡すように。kernel attr キャッシュ無効化 → `ln a b` 直後の `stat a` が即座に新しい `st_nlink` を返す。
- **libfuse 3 で `-o use_ino` が `unknown option` で拒否される** → フォーク内 [vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs) の `Init` callback で `fuse_config.use_ino` (offset 64) を 1 に書く downstream パッチ。これでハードリンクの `stat -c '%i'` が一致。

`Mount/Program.cs` で FUSE オプションを組み立て: 既定 `attr_timeout=0` + 受け取った `FuseFlags` を `,` で結合し `Tmds.Fuse.MountOptions.Options` にセット。詳細は [fstab-support.ja.md §Tmds.Fuse のフォーク採用](fstab-support.ja.md)。

---

## /etc/fstab と mount(8) 統合

[`ConfigLoader.ParseCli`](../src/lib/src/Config/ConfigLoader.cs) に位置引数 (source=connection or setting.file / target=mount-point) と `-o key=val,flag,...` パーサを内蔵。位置引数 1 つ目は `postgresql:` で始まれば `database.connection` (URL 形)、それ以外は `setting.file` (TOML パス) と分岐するヒューリスティック (`mount.pgfs postgresql://... /mnt/pgfs` と `mount.pgfs /etc/pgfs.toml /mnt/pgfs` を共存可能に。kv 形は判別不能なので `-c` 必須)。

fstab/mount(8) helper 由来の無関係なフラグ (`-i`, `-f`, `-n`, `-s`, `-v`, `-N`, `-t`, `_netdev`, `noauto`, ...) は **helper context 限定** で silent に読み飛ばす。helper context 判定は (a) `args` に positional があり (b) 親プロセスの comm (`/proc/<ppid>/comm`) が `"mount"` の AND 条件。Linux 以外では常に直接実行扱い。これにより `-f` (= setting.file 短縮形) / `-s` (= database.schema 短縮形) は **直接実行時のみ** 効く。

子プロセス分離による自動デーモン化 (子の stdout 1 行目に `PGFS_MOUNTED_OK` を出し、親はそれを読んだら exit して mount(8) を解放、`--foreground` で前景固定)。

**fstab 経由マウント時の PATH 剥がし問題**: `mount(8)` は helper を呼ぶ際に env から PATH を完全に剥がす。Tmds.Fuse の `HasFusermount` は `$PATH` で `fusermount3` を探すため、`CheckDependencies` が false を返し即終了していた。`Mount/Program.cs` の `Main` 冒頭で PATH 空のときに `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin` を補うようにして解消 (子プロセスにも `Process.Start(UseShellExecute=false)` 経由で継承)。

詳細仕様は [fstab-support.ja.md](fstab-support.ja.md)。実機 `sudo mount -t pgfs -o allow_other ...` で root mount + 非 root ユーザーアクセス + umount まで通過確認済み。

---

## 接続失敗時の再試行

Polly を入れず軽量自作 ([src/lib/src/Utility/Retry.cs](../src/lib/src/Utility/Retry.cs)) で `Pg.OpenConnection` / `OpenConnectionAsync` を包む。transient 判定は `SocketException` / `TimeoutException` / `PostgresException` の SqlState 一覧 (`57P03` / `57P01` / `57P02` / `08000` / `08003` / `08006` / `08001` / `08004` / `53300`) / その他 `NpgsqlException`。

**クエリ実行中の例外は再試行しない** (書き込み idempotency が壊れるため、再試行は接続オープン時点のみに限定)。指数バックオフは `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` (既定 5 / 200ms / 2000ms) で制御、`Api` ctor が読み込んで `Retry.Configure` でグローバルに適用。

---

## クロスクライアント変更通知

PostgreSQL `LISTEN` / `NOTIFY` を使った cross-client change propagation ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs) + [src/lib/src/Api/RemoteChangeInfo.cs](../src/lib/src/Api/RemoteChangeInfo.cs))。

- **オプトイン**: `database.notify_enabled` (default false)。CLI `--notify`、TOML `[database] notify_enabled = true`、`-o notify-enabled` のどれでも有効化。専用 connection 1 本 + 各書き込みに `SELECT pg_notify(...)` が乗るオーバヘッドがあるため、1 クライアント運用では OFF が望ましい。
- **チャンネル名**: `{schema}_{prefix}notify` (例 `pgfs_pgfs_notify`)。同一 DB 内に複数 pgfs インスタンスがあっても衝突しない。
- **ペイロード**: JSON `{"s":<sender>,"i":[ids],"p":[parent_ids],"x":[path_prefixes]}` (8000 byte 制限内)。Sender id は 8 文字 hex、自己メッセージは受信側でフィルタ。
- **書き込み連動**: `InsertInode` / `DeleteInode` / `UpdateMode|Owner|Size|Timestamps` / `Rename` / `WriteData` / `TruncateData` / `SetXAttr|RemoveXAttr` / `CreateHardLink` の末尾に `this.Notify(...)` を埋め込み。トランザクション内ではなく外で publish (commit 後)、`Pg.Execute` 経由の autocommit。
- **受信側の処理**: `OnRemoteChange` (Api 内) が ① `InodeCache.TryGetPath` で path を解決 → ② `InodeCache.Invalidate` / `InvalidateChildren` / `InvalidatePrefix` を適用 → ③ `Api.OsBridge` が登録されていれば pre-resolved path 付きで呼び出し。
- **OS 通知ブリッジ**:
  - **Assign (Windows)**: [FileSystem.PropagateRemoteChange](../src/assign/src/FileSystem.cs) で `DokanInstance.NotifyUpdate(WindowsPath)` を呼び、Explorer に再描画依頼。`/` → `\` 変換は `ToWindowsPath` ヘルパ。
  - **Mount (Linux)**: Tmds.Fuse 高レベル API には `fuse_lowlevel_notify_inval_*` 相当が無いため OS ブリッジ未実装。`attr_timeout=0` のおかげで `stat` / `ls` 等の **アクティブな問い合わせ** は最新値を返す (InodeCache 経由で DB ヒット)。**`inotify` などのパッシブ subscriber には伝わらない** ことが制約として残る。低レベル API への移行 (or vendored Tmds.Fuse への notify patch 追加) は将来の課題。
- **LISTEN の確立は同期**: `NotifyChannel.Start()` 内で接続オープン + `LISTEN` 発行 + Information ログまでを呼び出しスレッドで完了。その後の wait ループだけバックグラウンド (`Task.Run`)。`mount.pgfs` 起動時の MOUNTED シグナル前にログが出るので、`-f` 経由 / fstab 経由のどちらでも `tests/linux/mount.log` で起動確認できる。
- **接続切断時の再接続**: wait ループ内で例外を catch して 2 秒待機 → 新規接続 → 再 `LISTEN`。`Pg.OpenConnection` 系の指数バックオフ retry とは別系統 (LISTEN 専用 connection は long-lived のため独自管理)。

検証: Linux 34/34 / Windows 24/24 ALL PASSED (`--notify` 有効でも回帰無し)。`tests/linux/mount.log` に `NotifyChannel: LISTEN pgfs_pgfs_notify (sender=...)` の Information ログが出ることを確認。**クロスクライアント実動作 (mount.pgfs を 2 台 + 一方の write が他方に届く) の自動テストは未整備** (e2e harness が 1 mount 前提のため)、手動検証で補う方針。

---

## OS に存在しない uname/gname のフォールバック

`mount.fallback_uname` (既定 `nobody`) / `mount.fallback_gname` (既定 `nogroup`) を `SaveTarget.Db` で実装 (セキュリティ設定なので FS 全体で 1 値持つ意図)。Linux `UserResolver` / Windows `WindowsUserResolver` のコンストラクタを `(string fallbackUname, string fallbackGname)` ベースに書き換え、起動時に fallback 名を OS API (`getpwnam` / `NTAccount.Translate`) で解決して内部 cache。失敗時の最終 hardcode は Linux uid=65534 (NFS の `nobody` 慣習値) / Windows `WellKnownSidType.AnonymousSid`、warning ログを出す。

これまで「実行プロセスの uid に化ける」「他人のファイルが自分のものに見える」セキュリティ事故になっていた挙動を解消。Linux e2e に `test_fallback_uname_gname` を追加 (DB 直接 INSERT で bogus uname の inode を作り、stat で `nobody`/`nogroup` を確認、DELETE クリーンアップ)。

---

## Windows 属性の永続化

Windows e2e ([tests/windows/](../tests/windows/README.ja.md)) は Linux 版と対称な構造で 24 件。POSIX 専用 (symlink / hardlink / chmod / chown / xattr API 公開) は DokanNet 非対応のため対象外、代わりに Windows 固有 (SetFileAttributes / SetFileTime / FindFilesWithPattern) を追加。

**Hidden / System / Archive 属性の xattr 永続化**: `pgfs_inode.xattrs` JSONB の `user.win_attrs` キーに `FileAttributes` のマスク値 (Hidden\|System\|Archive のみ) を 4-byte little-endian int で保存。**マスクが空 (全 OFF) でも xattr は削除せず 4 バイト 0 を書き込む**: 「xattr 有り = Windows 側で SetFileAttributes を触った」「xattr 無し = まだ触っていない」の二値で扱い、xattr 無しのときだけ「先頭ドット = Hidden」heuristic を fallback として適用。マスク 0 で削除する設計だと「ドットファイルを Explorer で un-hide → 次回 heuristic 復活でまた Hidden に戻る」UX バグを踏む。`ReadOnly` は引き続き st_mode の write ビット。実装は [src/assign/src/FileSystemUtils.cs](../src/assign/src/FileSystemUtils.cs) の `WinAttrsXattrKey` / `LoadWinAttrs` / `SaveWinAttrs`。

---

## 書き込み path のレース防止

- **`Api.EnsureDataRow`**: インメモリの `inode.DataId` が null と見える 2 つ以上の並行 tx が両方 `INSERT pgfs_data` → 後勝ちの `UPDATE inode SET data_id` で敗者の data 行が宙に浮き、そこへ書いたチャンクが到達不能になる silent なリーク + データ消失バグ (`pgfs_data.id` は serial で衝突しないため派手に死なず、テストが通っていても壊れている可能性があった)。修正は `SELECT data_id FROM inode WHERE id = @id FOR UPDATE` で inode 行をロックして tx 間を直列化し、ロック取得後に DB の真の `data_id` を読み直す形 (キャッシュではなく DB を真とする)。`UPDATE` が 0 行に当たったら inode が並行に消えたと判断して throw → tx rollback で孤児 data 行を防ぐ。
- **チャンク書き込みの idempotency**: チャンク upsert は `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE` なので、チャンク境界をまたぐ並行 WriteFile (Windows `Copy-Item` / CopyFileEx が発行する) でも PK 制約に違反しない。PG の行ロックが writer を直列化する。

---

## Linux e2e: symlink / hardlink 関連の修正

- **`Mount.FileSystem.SymLink(path, target)` の引数解釈**: フォークの `FuseMount.Symlink(path*, path*)` は libfuse 由来 `(target, linkname)` を逆順に詰め替えて `IFuseFileSystem.SymLink` に渡してくる。当方の override は arg1 をリンク内容、arg2 をリンク作成先と誤読していたため INSERT が一度も走らなかった。正しい順序に修正。
- **`Api.DeleteInode` / `Api.CreateHardLink` でハードリンク兄弟 inode のキャッシュ無効化**: 従来は source/対象 inode しか `inodeCache.Invalidate` しておらず、3 つ以上のハードリンクや `rm a` 後の `stat b` で古い `st_nlink` を返していた。tx 内で `SELECT id FROM inode WHERE data_id = @did` で兄弟 ID を取得し、commit 後に全員 `Invalidate` する。

---

## コーディング規約

[docs/coding-style.ja.md](coding-style.ja.md) がコーディング規約の正。`.editorconfig` でブレース必須 + `this.` qualifier 強制。条件分岐ルール (本体は「末尾フロー脱出 + 任意の文 N 個」または「単一文のみ」、`else` 禁止、ternary 禁止、多分岐は `switch`、ログ出力は副作用としてカウントしない) をそこに明文化。
