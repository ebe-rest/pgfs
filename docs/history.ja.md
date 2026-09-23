# プロジェクト履歴

> **道順**: [docs/README.ja.md](README.ja.md) › **本書**
>
> **この doc が正である範囲**: **専用 doc を持たない**完了項目の経緯アーカイブ。
> 「なぜそう決めたか」が残っていないと困るが、機能 doc を 1 本立てるほどではないものを置く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [README.ja.md](README.ja.md) | **全 doc の索引**。入口はここ |
> | [next.ja.md](next.ja.md) | **次に何を着手するか**。完了したらここへ移すか、feature doc へ移す |
> | 各 `design/*.md` | **専用 doc がある**機能の設計・as-built・変更記録。そちらが正 |
>
> **書き足すとき**: 専用 doc がある機能なら**そちらの §変更記録**へ。無い場合だけここへ追記する。

過去の大型修正・現行設計に至る決定の経緯を残すドキュメント。振り返りが必要そうなものだけを残す。専用 feature doc を持たない完了項目の経緯はここに集約する (feature doc がある項目はそちらが正)。

> **パスについて**: 各エントリは **記録時点の事実** として残す方針。v0.2.0 再編より前のエントリは旧構造のパス (`src/lib/` / `vendor/Tmds.Fuse`) を含むが、当時の正しい記述としてそのまま保持している (現行コードは `src/core/` ほか)。新規エントリは記録時点の現行パスで書く。

---

## inode UPDATE の router 化 — Citus の分散デッドロック解消 (完了)

`{prefix}inode` の UPDATE が `WHERE id = @id` だけだったため、分散キー (`parent_id`) を含まず **Citus が全 shard に配っていた** (`EXPLAIN` の Task Count = shard 数)。実害は (a) 1 メタデータ更新が「shard 数 × placement 数」のリモート文になる (b) shard ロックの取得順が非決定的で並行時に `40P01 distributed deadlock` が起きる、の 2 つ。**700 ファイルの `rsync -a` (毎ファイル chmod + utime) で実際に 2 件落ちた** (rsync exit 23)。Linux e2e 38 件は並行度が低く露見しなかった。

対策は inode を更新する 9 経路すべてで WHERE に `parent_id` を含めること。分散キーは `Api.ResolveParentId` が InodeCache → 無ければ DB 1 読みで解決するので、FUSE / Dokan 側のシグネチャは不変。詳細は [support_for_citus.ja.md §UPDATE は必ず分散キーを WHERE に含める](design/support_for_citus.ja.md)。

**効果**: デッドロックは消えた (rsync の Error 0 件) が、**スループットは +9% にとどまった** (Citus rf=2 で 2.08 → 2.27 MB/s。単一 PG は 13.46 MB/s)。ベンチと原因分析 (1 FS 操作 = 1 分散トランザクション × 2PC が主因 / `max_write` 128 KiB が最大のレバー) は [performance.ja.md](design/performance.ja.md) に記録した。

## 排他制御を `{prefix}lock` に集約 + replication factor から独立させた (完了)

Citus の shard 複製数 (`citus.shard_replication_factor`) が 1 以外だと **行ロックが使えない**問題への対応。設計の正は [support_for_citus.ja.md §排他制御と replication factor](design/support_for_citus.ja.md)。

**発端**: 共有 Citus クラスタ (dev サーバ) の既定が `shard_replication_factor = 3` で、そこに pgfs を分散すると `SELECT … FOR UPDATE` が `0A000 could not run distributed query with FOR UPDATE/SHARE commands` で落ちる。分散キー等値フィルタを付けても回避できない (rf > 1 は statement-based replication で placement 間の結果がズレ得るため Citus が拒否する)。同一テーブルを rf=1 で作り直すと通ることを A/B で確認した。

**判断**: rf ごとにロック機構を切り替えるのは筋が悪い。**ロック対象のテーブルだけ非分散**にして rf から独立させる。

* `{prefix}lock` を `create_distributed_table('…','target_id')` から **`citus_add_local_table_to_metadata`** (coordinator に 1 コピー) へ変更。
* **行ロックを `{prefix}lock` だけに集約**: `{prefix}inode` へ打っていた `FOR UPDATE` 2 箇所 (`Api.Rename` の旧行取得 / `Api.EnsureDataRow` の data 行作成の直列化) を撤去し、直前の `LockInodes` / `LockInode` (= `{prefix}lock`) に任せた。`EnsureDataRow` には `LockInode` を新規に追加 (target_id は負値 = 正の data_id より先に取るので昇順規律も保たれる)。
* 結果として `{prefix}inode` / `data` / `data_chunk` / `audit` の rf は自由に選べる (rf=1 は RAID0 相当、rf=N は N 重ミラー相当という純粋なストレージ冗長の選択)。

**なぜ参照テーブルではなく citus local か** (実測で比較):

| | 行ロック | 別ノード入口どうしの直列化 | ロック 100 回 (coordinator 入口) | placement |
|---|---|---|---|---|
| 分散 rf=1 | ✅ | — | 32 ms | 1 |
| 分散 rf≥2 | ❌ | — | — | rf |
| **citus local** (採用) | ✅ | ✅ 2.0 秒待つ | **11 ms** | 1 |
| 参照テーブル | ✅ | ✅ 2.0 秒待つ | 61 ms | 全ノード |

advisory lock はノードローカルな PG の状態なので他ノードから見えないが、**citus local table は実在する 1 行**なので、どのノードを入口にしても Citus がその 1 行へルーティングする。Citus 11+ は全ノードが metadata を持ちクエリ入口になれる (= multi-coordinator 相当) ので、worker `citus-test1` が保持中に worker `citus-test2` が 2 秒待つことを実測で確認した。参照テーブルでも成立するがロックのたびに全 placement を触るので 5.5 倍遅く、placement が 1 つ欠けるとロック自体が失敗する。トレードオフとして**ロックは coordinator の 1 行に集中する** (従来 doc の「target_id 分散で worker に分散」は撤回)。

**併せて mkfs に 3 オプション追加**: `--shard-count` / `--shard-replication-factor` (`--rf`) は `create_distributed_table` を呼ぶセッションに `SET` を**同一コマンドで**連結して発行する (別呼び出しでは接続プールから別接続を引く可能性がある)。`--distribute-existing` は既存テーブルも Citus 化する明示フラグ (`pg_dist_partition` 判定で冪等)。既存 DB では topology 系 mutate を触らない従来の安全弁は維持した。

**検証** (dev サーバ / 共有 Citus 13.1 クラスタ + 実 PG 17.5):

* `mkfs --citus --rf 2 --shard-count 8` で `pgfs_test` を分散 → 4 テーブル distributed (8 shard × 2 placement) + `lock`/`settings`/`mounts` local → **Linux e2e 37 passed / 0 failed / 1 skipped (38 件)**。
* 非 Citus (単一 PG) でも **37 passed / 0 failed / 1 skipped** = ロック集約の回帰なし。
* `--distribute-existing`: 3 MiB のファイルを入れた非 Citus FS を in-place で分散 → **md5 不変**。2 回目の実行は 7 テーブルすべて「既に Citus 管理下 — スキップ」。

## 実占有バイトと `st_blocks` — スパースファイル対応 (完了)

`du` が**スパースファイルで実体の何百倍も**報告していた問題の修正。規約の正は [database.ja.md §実占有バイトと st_blocks](design/database.ja.md)。

**症状**: `truncate -s 1G` したファイルはチャンク行 0 = 実占有 0 なのに、`du` が 1.0G と答える。[src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) が `st_blocks` を `st_size` から機械的に出していたため。`df` (statfs) は plperlu の実測なので正しく、ズレていたのは `du` / `tar --sparse` 等が見る `st_blocks` だけ。

**同時に見つかった不整合**: `pgfs_data.total_size` は「論理的な合計サイズ」と定義されていたのに、`INSERT` 時にリテラル `0` を入れたあと**更新も参照もされていなかった** (全行 0)。Layer 2 の `used` は `sum(length(payload))` から出していたので実害は無かったが、列が死んでいた。

**採った設計**: `total_size` を **実占有バイト (全チャンクの `length(payload)` 合計)** に再定義して維持し、`st_blocks = ceil(total_size / 512)` にする。**スキーマ変更なし**を選んだ理由は、`mkfs` の冪等化が「既存テーブルへの列追加」をしない (存在すれば何もしない) ため、新列を足すと既存 FS が mount 不能になること。

* **維持**: チャンク payload を変える 3 経路 (UPSERT / 末端切り詰め / 末尾削除) が payload 長の delta を返し、1 操作 = 1 回の `UPDATE … total_size = GREATEST(0, total_size + delta) … RETURNING total_size` で反映。再集計方式は大きいファイルで二次的に遅くなるので採らない。
* **読み出し**: inode 行に無い値なので `Api.GetOccupiedBytes` が 1 行引いてメモリ上の `Inode` にキャッシュ。ディレクトリ列挙は `ListChildren` が `id = ANY(…)` で 1 クエリ先読みして getattr の N+1 を回避。Citus では inode ↔ data が非コロケーション (分散キーが `parent_id` と `id`) のため **JOIN は使わない**。
* **既存 FS**: `total_size` が 0 のままだと `du` が 0 になるので、backfill SQL を database.md に載せた (1 回流せば以後は自動維持)。

**実測 (dev サーバ / 実 PG 17.5)**: `truncate -s 1G` → `du` 0 / apparent 1.0G、末尾 4KiB 書き込み → `du` 1.0M (チャンク 1 本分のみ実体化・穴は埋まらない)、通常 3MiB ファイル → `du` 3.0M、ハードリンクは `du` が `st_ino` で重複排除。cold `ls -l` でも先読みが効いて追加往復なし。

**回帰テスト**: `test_sparse_du_blocks` ([tests/linux/e2e.sh](../tests/linux/e2e.sh))。`st_blocks` を `st_size` 由来に戻す突然変異で FAIL することを確認済み。

## タイムスタンプの UTC 統一 (完了)

`TIMESTAMP` 列 (`st_mtime` / `st_ctime` / `created_at` / `updated_at` / `occurred_at` / `started_at` / `heartbeat_at`) に**ローカル時刻が保存されていた**バグの修正。規約の正は [database.ja.md §タイムスタンプ規約](design/database.ja.md)。

**症状**: FUSE が返す mtime/ctime が、ホストの UTC オフセット分ずれる (JST なら +9h)。読み側 ([src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) の `DateTime.SpecifyKind(..., DateTimeKind.Utc)`) は最初から「DB の値は UTC」前提だったのに対し、書き側が 2 経路ともローカル時刻を入れていた。

1. **SQL 経路**: `timestamp without time zone` 列に対する素の `current_timestamp` は**セッション TimeZone のローカル壁時計**。列 DEFAULT ([src/mkfs/src/Initializer.cs](../src/mkfs/src/Initializer.cs)) と `SET st_mtime = current_timestamp` 群 ([src/core/src/Api/Api.cs](../src/core/src/Api/Api.cs)) が該当 → `current_timestamp AT TIME ZONE 'UTC'` に統一。DB 側で経過時間を計算する `StatusAdmin.ListMounts` の `now()` も `now() AT TIME ZONE 'UTC'` に合わせた (列が UTC になったため)。
2. **パラメータ経路**: `Kind=Utc` の `DateTime` を Npgsql に渡すと **`timestamptz` として送られ、PG が `timestamp` 列へ代入する際にセッション TimeZone でキャスト**する。つまり `DateTime.UtcNow` を渡していた箇所 (`ConfigStore.SaveJson` / `WriteFullChunk` / `UpdateTimestamps` の `@mtime` / 監査 `occurred_at`) も保存値はローカルだった → `Pg.UtcNow` / `Pg.ToDbUtc()` ([src/core/src/Utility/Pg.cs](../src/core/src/Utility/Pg.cs)) で **UTC 壁時計 + `Kind=Unspecified`** に正規化してから渡す。

**なぜ長く気付かなかったか**: (a) docker e2e はコンテナが UTC なのでオフセット 0、(b) 同一マウントセッション中は `InodeCache` 上の値 (Kind を落とさず保持) が返るので、作成直後の `stat` は正しく見える。露見するのは**再マウント後・キャッシュ退避後・他クライアントからの参照**、つまり DB から読み戻した時だけ。JST ホスト (dev サーバ) で実 PG に対して回して初めて出た。

**回帰テスト**: `test_timestamp_utc_roundtrip` ([tests/linux/e2e.sh](../tests/linux/e2e.sh)) — FS 生成 mtime/ctime が現在時刻の近傍にあること + `touch -d` の明示値が epoch 一致で読み戻せること。ダミー 20 個で InodeCache を押し出すので、`CACHE_MAX_ENTRIES=8` 変種 ([tests/docker/run.sh](../tests/docker/run.sh)) では DB 読み戻し経路も踏む。読み側を `DateTimeKind.Local` に壊す突然変異テストで、テストが実際に落ちることを確認済み。

## 設定 Config まわりの仕上げ 

`Pgfs.Core.Config` (`Field<T>` / `ConfigLoader` / `Schema`) 上の 3 つの仕上げ。いずれも専用 feature doc を持たないのでここに記録する (実装の正は [src/core/src/Config/](../src/core/src/Config/) のコード本体)。

### CLI 引数の二重 parse 解消 (#15)

mount/assign の `BuildRootConfig` は ConfigStore (DB 由来設定) の接続情報を CLI/TOML から得る必要があるため二段構築するが、旧実装は **lite Loader と full Loader を別々に建て、CLI パースと TOML 読込を 2 回**走らせていた (結果は同じだが重複作業)。これを **同じ Loader インスタンスを使い回し、[ConfigLoader.WithStore](../src/core/src/Config/ConfigLoader.cs) (新設 fluent API) で Phase 3 (DB) だけ後付け**する形に統一: `loader = new ConfigLoader(args, AllFields, null); var db = loader.BuildDatabaseConfig(); ...; loader.WithStore(store).BuildRootConfig();`。ctor の Phase 3 ブロックは `ApplyStoreFields(store)` に**逐語抽出**し、ctor (store != null) と `WithStore` の両方から呼ぶ (store 付き ctor 経路は後方互換で不変、mkfs は store=null の単発なので影響なし)。「DB は CLI/TOML が未設定のキーだけ埋める」上位ソース優先の不変条件は維持。**検証**: throwaway probe (Core.csproj 参照、DB 不要) で (A) fresh Loader 直 `BuildRootConfig()` と (B) `BuildDatabaseConfig()`→同一 Loader で `BuildRootConfig()` が `DescribeProvided`・全解決値・警告数すべて一致を PASS (mount/assign の e2e がこのパスを毎回踏むので回帰でも回収)。

### `Field` self-check (#13)

設定 Field の「宣言したが配線忘れ」を起動時に検知するガードレール。[ConfigLoader.Resolve](../src/core/src/Config/ConfigLoader.cs) が触れた `FullKey` を `resolvedFullKeys` に記録し、公開メソッド [ConfigLoader.UnresolvedFields](../src/core/src/Config/ConfigLoader.cs) が `Schema.AllFields` (nested static class を reflection 列挙) との差分 (= 一度も Resolve されていない Field) を返す。`BuildRootConfig` は全ツール共通で全サブ Config を建てる単一経路なので、その末尾で `UnresolvedFields()` が非空なら各 Field を Warning に積む (`ConfigLoader.Warnings` 経由で起動時ログ + stderr に出る。tool フィルタ不要)。**検証**: throwaway probe で (1) `BuildRootConfig()` 後は AllFields=31 / 未解決=0 / 警告=0 (フル配線で誤検知なし)、(2) `BuildMountConfig()` のみだと未解決=26 で `database.connection` を含む (= 配線漏れを正しく検出) を PASS。新 Field 追加時に POCO/Resolve 配線を忘れると次回起動で自動的に気づける。

### ヘルプ自動生成 (#12)

3 つの手書き `ShowHelp` (mkfs / mount / assign の各 `Program.cs`) を廃止し、[HelpText.Build](../src/core/src/Config/HelpText.cs) が [Schema.AllFields](../src/core/src/Config/Schema.cs) の `CliOptions` / `Comment` / 型 / 既定値を walk して `--help` を組み立てる。各 Program はイントロ文 (Usage) とフッター文 (fstab 経由・アンマウント手順等のツール固有散文) だけ渡す。ツール別の出し分けは [Field.AppliesTo](../src/core/src/Config/Field.cs) (`[Flags] enum Tool`)。help 専用フィルタなので CLI パースには影響しない (mount が `--citus` を黙って受理する挙動は不変)。値プレースホルダは型から導出 (`<n>` / `<connstr>` / `<level>` 等)、`Field.ArgName` で明示上書き可。bool フラグと接続文字列 (秘匿) は既定値を非表示。

---

## Citus Phase 3: pgfs_lock + SELECT FOR UPDATE 排他制御 (完了)

Phase 2 で先行作成した `pgfs_lock(target_id BIGINT PK)` 上の行ロックを使った cross-client 排他制御を [src/lib/src/Api/Api.cs](../src/core/src/Api/Api.cs) に組み込み。**設計の詳細・組み込み箇所・lock 取得 SQL の実装メモは [support_for_citus.ja.md §Phase 3](design/support_for_citus.ja.md) を正とする**。ここでは決定の経緯のみ:

- **「自作 TTL テーブル + heartbeat」「`pg_advisory_xact_lock`」を採用しなかった理由**: TTL race / coordinator-local 制約 / Citus 分散できないため (詳細は support_for_citus.md Phase 3 案比較表)。
- **namespace 分離を「別テーブル」ではなく「target_id の符号」で実装**: Citus の単一カラム分散制約があるため `pgfs_lock(target_id BIGINT PK)` の 1 列構成にせざるを得ず、namespace は値の符号 (`+data_id` / `-inode_id`) で分離。「pgfs_inode_lock を別テーブルに切り出して parent_id 分散 + co-located にする」案は inode lock が支配的 workload で非対称コストが見えてきたら再編する余地として残す。
- **lock 対象範囲は docs テーブル通り**: WriteData/TruncateData/ReleaseData (data lock) / Update{Mode,Owner,Size,Timestamps} (inode lock) / Rename / DeleteInode / CreateHardLink (multi inode lock)。SetXAttr/RemoveXAttr/CreateFile/CreateDirectory/CreateSymlink は意図的に対象外 (単一 UPDATE が atomic / 親 lock を取らない理由は docs の「lock 対象外の判断」セクション)。
- **`Pg.Execute` の単発書き込みを tx ベースに昇格**: UpdateMode/Owner/Size/Timestamps の 4 メソッドは旧 `Pg.Execute(...)` の一文 SQL 構造で lock を持てなかった。`using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE + Commit に変換。
- **`LockTargets` の SQL は 2 文に分けた**: `INSERT ... ON CONFLICT DO NOTHING` + `SELECT ... FOR UPDATE`。CTE で 1 文に畳む案はシンプルさ優先で採用せず (各文とも target_id 単独 WHERE なので Citus でも単一 shard 完結)。
- **`Rename` の hintParentId stale race の許容**: cached.ParentId 取得 → LockInodes の間に他 client が rename を完了する可能性があるが、stale 検出は後続の `SELECT ... WHERE parent_id = @hint AND id = @id FOR UPDATE` が空を返す → false で抜ける動線で吸収 (誤った old parent ロックは tx 終了で自動解放)。

**検証** ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)、2026-05-26 4/4 PASS):
- 多ノード Citus 上で Linux e2e 34/34 通過 (= Phase 2 の cross-shard hop + Phase 3 lock が機能している総合確認)
- 並行 write race (urandom vs zero、8MiB × 4 round) で両 client から見える md5/size が常に一致
- 並行 mkdir 同名 (20 round × serial + parallel) で常にちょうど 1 client が EEXIST
- pgfs_lock 累積: 4 テスト後 rows=178 / size=768kB (設計値「1 行 ~50 bytes、100 万行で 100MB 以下」と整合)

**副産物**: [tests/linux/e2e.sh](../tests/linux/e2e.sh) の `pg_exec` (test_fallback_uname_gname 用、旧 pgsql_server ハードコード ssh+psql) に `PGFS_TEST_PG_EXEC` 環境変数オーバライドを追加。race_multinode.sh は `docker exec -i $COORD_NAME psql ...` をセットして渡す。

---

## Citus Phase 2: 分散テーブル化 + mkfs --citus + 多ノード対応 (完了)

Phase 1 (bytea 化) で `pg_largeobject` 依存を外した上で、`create_distributed_table` で 4 テーブル分散 + `pgfs_settings` を local 配置にする `mkfs --citus` を実装。**1 ノード構成 (worker なし)** と **多ノード構成 (coordinator + N workers, `--worker host[:port],...`)** の両方を mkfs だけで立ち上げ可能。

**設計反復の経緯** (3 回):
- 初版: `SetupCitusDistributionAsync` を facade レベルに切り出し、`InitializeAsync` で if 分岐
- ユーザー指摘で「テーブルスペース指定不可 + DB 既存時の read-only / `EnsureDatabaseAsync` への統合 / per-table の create_distributed_table」に再設計
- ユーザー追加指摘で「`CREATE SCHEMA` は伝搬で十分 / 各 worker への `citus_set_coordinator_host` も必要かも」に修正 → 最終的に [multinode_probe.sh](../tests/citus/multinode_probe.sh) で「`citus_add_node` の auto-sync で worker 側 pg_dist_node に coordinator が自動同期される」ことを実機確認 → **Phase 4 (per-worker citus_set_coordinator_host) は不要** と確定して削除

**分散戦略・Citus 制約の対応・mkfs フラグ・冪等性保証の詳細は [support_for_citus.ja.md §Phase 2](design/support_for_citus.ja.md) を正とする** (PK 複合化 / FOR UPDATE に分散キー必須 / DO UPDATE 句の IMMUTABLE 制限 / cross-shard rename DELETE+INSERT / coordinator 登録 + shouldhaveshards の独立判定など)。ここでは「方針の決定」レベルの 2 点のみ:

- **「単 PG モードだけ id 単独 UK を張る」分岐は採用しない**: 単 PG / Citus でスキーマがズレる、in-place 移行が UK で詰まる、application 起動時バリデーションは遅くなるだけで体感的な利益が無い、という理由で両モード共通で UK なし。BIGSERIAL の sequence + tx 規律で一意性を保つ。「将来念のため id 一意性を担保しよう」と再提案する前にこの判断を確認すること (詳細は support_for_citus.md Phase 2 つまずきポイント 1)。
- **`SetupCitusDistributionAsync` を解体し責務を分散**: クラスタトポロジ設定は `EnsureDatabaseAsync` 内側へ、per-table の `create_distributed_table` / `citus_add_local_table_to_metadata` は各 `CreateXxxTableAsync` の内側へ。`CreateTableAsync` を `Task<bool>` に変更してテーブル新規作成時のみ distribute が走るようにした (DB 既存時の Citus mutate skip と整合)。

**テスト結果**:
- mkfs マトリックステスト ([test_matrix.sh](../tests/citus/test_matrix.sh)): 3 initial × 6 target = **18/18 PASS** (Citus 14.0.0 docker, linux_client, 2026-05-26)
- 単 PG モード / 1 ノード Citus (pgsql_server, Citus 13.1.1): Linux 34/34・Windows 24/24 ALL PASSED
- 多ノード Citus 上での Linux e2e は Phase 3 と一緒に [race_multinode.sh](../tests/citus/race_multinode.sh) で実施 → 上 §Phase 3 参照

**廃案** (検討したが採用しなかったもの):
- `pgfs_inode` を `id` 分散にして UK は別レイヤ保証 — `(parent_id, name)` UK が壊れる、application 層 race 防止が複雑化
- `pgfs_settings` を distributed テーブル化 — 設定は coordinator が読み書きするだけなので不要

---

## Citus Phase 1: Large Object → bytea 化 (完了)

容量スケール (Citus 分散) の前提として、ファイル本体ストレージを PG の Large Object → bytea カラムに置き換えた。**Citus 単体ではなく単 PG 運用でも完結する独立した変更** (LO の `lo_seek+lo_read` 2 RTT が `substring(payload from N for M)` 1 RTT に減るので、partial-write が多くないワークロードでは bytea の方がやや速い)。

**なぜ必要か**: `pg_largeobject` は PG のシステムカタログなので `create_distributed_table` の対象にできない。worker を増やしても LO 本体は永久に coordinator 1 台に集中し、容量の壁を越えられない。bytea はユーザーテーブルの列なので Citus で分散可能。

**実装の中核とセマンティクスは [support_for_citus.ja.md §Phase 1](design/support_for_citus.ja.md) を正とする** (DDL / SQL パターン / TOAST partial detoast)。ここでは設計判断の経緯のみ:

- **各チャンクの payload 長 = 「これまで書き込まれたバイト数」** (LO セマンティクスをそのまま踏襲)。「全チャンクを chunk_size に固定 (zero パディング込み)」案も検討したが、storage 効率と du の accuracy を取って可変長を選択。
- **WriteData は 1 SQL/チャンクの upsert で完結**: `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END` の 3 ケース分岐。並行 WriteFile race は PG の行ロックで自動直列化 (旧 LO 版の `lo_create + ON CONFLICT DO NOTHING + 孤児 lo_unlink` 救済が不要に)。
- **`repeat(bytea, integer)` は PG に存在しない** (`repeat(text, integer)` だけ) — 0 パディングは `decode(repeat('00', N), 'hex')` で組み立てる。最初 `repeat('\x00'::bytea, N)` で書いて初回 e2e で発覚した。

**テスト結果**: Linux 34/34、Windows 24/24 ALL PASSED。

**廃案**:
- **「LO を維持して worker に分散・複製」**: 独自 LO テーブルを Citus 分散するのは「ページサイズ可変の bytea」と等価なので意味なし。
- **チャンク payload を chunk_size 固定 (zero パディング込み)**: 「du の数字が見たまま」を取りたかったので可変長を選んだ。

---

## モデルクラス整理リファクタ

旧 `Pgfs.Lib.Models.*Settings` ツリー (`Setting` / `Settings` / `RootSettings` / `BaseProvider<A>` / `ChangingEventArgs` / `JSON_INVALID` 番兵 / `Statics.GetNextId()` の負番 ID 等の「賢い」仕組み) を、**静的 `Field<T>` 記述子 + mutable POCO + `ConfigLoader` (CLI/TOML/DB/Default を統合) + `ConfigStore` (DB I/O)** に置き換えた。最終形は [src/lib/src/Config/](../src/core/src/Config/) 配下。Schema は [src/lib/src/Config/Schema.cs](../src/core/src/Config/Schema.cs) (reflection で全 Field を自動列挙)。

| 段階 | 概要 |
|---|---|
| **1** `MountConfig` | `Pgfs.Lib.Config` namespace 新設。`Field<T>` 静的記述子 + POCO + `ConfigLoader` パターンで `mount.*` スコープ (mount_point / cache_max_entries / fallback_uname / fallback_gname / foreground) を Config 化。`Api(RootSettings)` → `Api(RootSettings, MountConfig)`、旧 `Api.LoadMountFallbackSettingsFromDb` は `ConfigStore.LoadAll` 経由に置換。旧 `RootSettings` は他スコープのためにそのまま残存。 |
| **2** `FileSystemConfig` + `RootConfig` 集約 | `Schema.FileSystem.*` (version / volume_label / cluster_size / default_chunk_size / max_file_size) と `LongField` を追加。サブ Config が増えるたびに `Api` の ctor 引数が膨らむのを避けるため、集約 `RootConfig` を導入し `Api(RootSettings, RootConfig)` に変更。`Schema.AllFields` を reflection で自動列挙 (新しい Field を増やしたら自動で含まれる)。 |
| **3** `SettingFileConfig` + phase 順序 CLI→TOML→DB に再編 | setting.* (file / search_path) を Config 化。chicken-and-egg (`setting.file` が TOML パスを決める) を ConfigLoader 内部で解決。phase を **CLI → TOML → DB** に並び替え + 「上位が既に入れていれば skip」方式で priority (CLI > TOML > DB > Default) を担保。`StringListField` 追加 (comma-separated)、`TomlArray` の再帰的 comma-join 対応。 |
| **4** `LoggingConfig` | logging.* (level / output) を Config 化。`LogLevelField` (`Level.Parse` に委譲) と `LoggingOutputField` (`stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`) を新設。`logging.level` の Default を旧 Warning → **Information** に変更。**`Logger.MinLevel = config.Logging.MinLevel` を mount/assign の Main で実体に反映** したので、設定が初めて効くようになった (旧 LoggingSettings 時代は宣言だけで実体に届いていなかった)。テストで SQL Trace ログを取るため `tests/linux/flow.ps1` に `--log-level trace` を追加。 |
| **5** `DatabaseConfig` + mount/assign から旧 `RootSettings` を完全に外す | database.* を Config 化、`ConnectionField` (`NpgsqlConnectionStringBuilder`、kv 形 / URL 形両方を ctor に通す) を新設。`Schema.Root.Help` / `Schema.Root.Clean` (scope=`root`) を追加し `RootConfig.Help` / `RootConfig.Clean` で受け取る形に。`MountConfig.FuseFlags` 追加 (`-o allow_other` 等のバッファ)。**Api(RootConfig)** に変更、`InodeCache(RootConfig)` に変更。mount/assign の `Program.cs` から `new RootSettings()` / `ParseArguments` / `LoadFromFile` を完全に削除。Help チェックは store=null の lite Loader で先行判定 (`--help` 時に DB 接続を試みない)。Config 構築自体は二段 (lite → store 構築 → full) で chicken-and-egg を解消。 |
| **6** `pgfs_settings` フラット化 + mkfs を `RootConfig` ベースに移行 + `ConfigStore.Save<T>` 本実装 | `pgfs_settings` を旧 `(id, parent_id, key, value)` 階層形 → `(scope, key, value)` フラット PK に置き換え (DB 再構築必須、`mkfs --clean` で対応)。`ConfigStore` を書き直し: `LoadAll` は単純全件読み + 許可リストフィルタ、`Save<T>` は `INSERT ... ON CONFLICT (scope, key) DO UPDATE` で UPSERT。`Field<T>.FormatJson(value)` を新設し IntField/LongField/BoolField で override (JSON ネイティブ表現)、それ以外は `JsonSerializer.Serialize(Format(value))`。`Schema.Database.SuperConnection` を `SaveTarget.None` で追加し `DatabaseConfig.SuperConnection` プロパティに反映 (mkfs だけが読む)。`ConfigLoader(..., skipToml)` 引数を追加して `--clean` 時の TOML 読み飛ばしに使う。**mkfs を全面書き直し**: `Initializer(RootSettings)` → `Initializer(RootConfig)`、TOML 書き出しは `Tomlyn.Toml.FromModel(TomlTable)` を mkfs 内で組み立て、`PopulateSettingsRows` を `store.Save(Schema.X.Y, config.X.Y)` の明示列挙に変更 (mount.fallback_uname / fallback_gname / file_system.version / file_system.volume_label の 4 件)。 |
| **7** 旧 `Models/*Settings.cs` の完全削除 | `RootSettings.cs` / `MountSettings.cs` / `DatabaseSettings.cs` / `FileSystemSettings.cs` / `LoggingSettings.cs` / `SettingSettings.cs` / `Setting.cs` / `Settings.cs` / `BoolSetting.cs` / `SettingStorage.cs` / `BeforeChangeEventArgs.cs` / `DatabaseConnectionSetting.cs` / `Primitive.cs` の 13 ファイルを削除。`Base.cs` を `Inode`/`Data`/`Chunk` が必要とする `id` / `created_at` / `created_by` / `updated_at` / `updated_by` の 5 列だけ持つ最小形に簡素化 (`BaseProvider<A>` / `ChangingEventArgs` / `Created`/`Updated` / `OnIdChanging` / `Statics` 撤去)。`InodeCache.cs` のルート inode 投入時に書いていた死 property `Created = true` / `Updated = true` も削除。`Models/` に残ったのは DB 行モデル (`Inode.cs` / `Data.cs` / `Chunk.cs` / `Base.cs`) と `LoggingOutputField` が型として参照する Enum 群 (`SettingLoggingOutput.cs` / `SettingLoggingCycle.cs` / `SettingLoggingKind.cs`) の 7 ファイルだけ。 |

各段階の完了時点で Linux 34/34、Windows 24/24 ALL PASSED を維持。

---

## アクティブな設計判断 / 現行インフラ

### Tmds.Fuse の差し替え

本家 `tmds/Tmds.Fuse 0.1.0-190711-50` (2019 年から更新無し) を、活発フォーク `securefolderfs-community/Tmds.Fuse` (2026-03 最終更新、.NET 10) に切り替え。[vendor/Tmds.Fuse/](../src/fuse/NOTICES.md) に submodule で取り込み、[Mount.csproj](../src/mount/Mount.csproj) は ProjectReference 参照、`NuGet.Config` (MyGet feed) を削除、`pgfs.sln` に vendor csproj を追加 (Release/Debug の config 伝播のため)。

これにより本家版で踏んでいた制約 3 件を解消:
- **`MountOptions.Options` がない** → フォークが追加。`-o allow_other,attr_timeout=0,...` を libfuse に伝えられる。
- **`attr_timeout=0` を渡せない** → 既定で渡すように。kernel attr キャッシュ無効化 → `ln a b` 直後の `stat a` が即座に新しい `st_nlink` を返す (`sleep 1.1` workaround 撤去済み)。
- **libfuse 3 で `-o use_ino` が `unknown option` で拒否される** → フォーク内 [vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs](../src/fuse/src/FuseMount.cs) の `Init` callback で `fuse_config.use_ino` (offset 64) を 1 に書く downstream パッチを当てた。これでハードリンクの `stat -c '%i'` が一致 (`test_hardlink_basic` の inode 等価アサート復活)。

`Mount/Program.cs` に FUSE オプション組み立てロジックを追加: 既定 `attr_timeout=0` + 受け取った `FuseFlags` を `,` で結合し `Tmds.Fuse.MountOptions.Options` にセット。

### `/etc/fstab` 対応

[`ConfigLoader.ParseCli`](../src/core/src/Config/ConfigLoader.cs) に位置引数 (source=connection or setting.file / target=mount-point) と `-o key=val,flag,...` パーサを内蔵。位置引数 1 つ目は `postgresql:` で始まれば `database.connection` (URL 形)、それ以外は `setting.file` (TOML パス) と分岐するヒューリスティック (`mount.pgfs postgresql://... /mnt/pgfs` と `mount.pgfs /etc/pgfs.toml /mnt/pgfs` を共存可能に。kv 形は判別不能なので `-c` 必須)。

fstab/mount(8) helper 由来の無関係なフラグ (`-i`, `-f`, `-n`, `-s`, `-v`, `-N`, `-t`, `_netdev`, `noauto`, `noatime`, ...) は **helper context 限定** で silent に読み飛ばす。helper context 判定は (a) `args` に positional があり (b) 親プロセスの comm (`/proc/<ppid>/comm`) が `"mount"` の AND 条件。Linux 以外では常に直接実行扱い (`assign.pgfs` でも同じコードが安全に動く)。これにより `-f` (= setting.file 短縮形) / `-s` (= database.schema 短縮形) は **直接実行時のみ** 効く。

子プロセス分離による自動デーモン化 (MOUNTED シグナル方式、子の stdout 1 行目に `PGFS_MOUNTED_OK` を出し、親はそれを読んだら exit して mount(8) を解放、`--foreground` で前景固定)。

**fstab 経由マウント時の PATH 剥がし問題**: `mount(8)` は helper を呼ぶ際に env から PATH を完全に剥がす (実測値で 8〜11 個の env しか残らない: LANG, LOGNAME, PWD, SHLVL, SUDO_*, TERM, USER, _)。Tmds.Fuse の `HasFusermount` は `$PATH` で `fusermount3` を探すため、`CheckDependencies` が false を返し即終了していた。`Mount/Program.cs` の `Main` 冒頭で PATH 空のときに `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin` を補うようにして解消 (子プロセスにも `Process.Start(UseShellExecute=false)` 経由で継承)。

詳細仕様は [fstab-support.ja.md](design/fstab-support.ja.md) を参照。実機 `sudo mount -t pgfs -o allow_other ...` で root mount + 非 root ユーザーアクセス + umount まで通過確認済み。

### 接続失敗時の再試行 (Polly 相当)

Polly を入れず軽量自作 ([src/lib/src/Utility/Retry.cs](../src/core/src/Utility/Retry.cs)) で `Pg.OpenConnection` / `OpenConnectionAsync` を包む。transient 判定は `SocketException` / `TimeoutException` / `PostgresException` の SqlState 一覧 (`57P03` / `57P01` / `57P02` / `08000` / `08003` / `08006` / `08001` / `08004` / `53300`) / その他 `NpgsqlException`。

**クエリ実行中の例外は再試行しない** (書き込み idempotency が壊れるため、再試行は接続オープン時点のみに限定)。指数バックオフは `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` (既定 5 / 200ms / 2000ms) で制御、`Api` ctor が読み込んで `Retry.Configure` でグローバルに適用。

### 他クライアント変更通知 (Notify)

PostgreSQL `LISTEN` / `NOTIFY` を使った cross-client change propagation。新規ファイル [src/lib/src/Api/NotifyChannel.cs](../src/core/src/Api/NotifyChannel.cs) + [src/lib/src/Api/RemoteChangeInfo.cs](../src/core/src/Api/RemoteChangeInfo.cs)。

- **オプトイン**: `database.notify_enabled` (default false)。CLI `--notify`、TOML `[database] notify_enabled = true`、`-o notify-enabled` のどれでも有効化。専用 connection 1 本 + 各書き込みに `SELECT pg_notify(...)` が乗るオーバヘッドがあるため、1 クライアント運用では OFF が望ましい。
- **チャンネル名**: `{schema}_{prefix}notify` (例 `pgfs_pgfs_notify`)。同一 DB 内に複数 pgfs インスタンスがあっても衝突しない。
- **ペイロード**: JSON `{"s":<sender>,"i":[ids],"p":[parent_ids],"x":[path_prefixes]}` (8000 byte 制限内)。Sender id は 8 文字 hex、自己メッセージは受信側でフィルタ。
- **書き込み連動**: `InsertInode` / `DeleteInode` / `UpdateMode|Owner|Size|Timestamps` / `Rename` / `WriteData` / `TruncateData` / `SetXAttr|RemoveXAttr` / `CreateHardLink` の末尾に `this.Notify(...)` を埋め込み。トランザクション内ではなく外で publish (commit 後)、`Pg.Execute` 経由の autocommit。
- **受信側の処理**: `OnRemoteChange` (Api 内) が ① `InodeCache.TryGetPath` で path を解決 → ② `InodeCache.Invalidate` / `InvalidateChildren` / `InvalidatePrefix` を適用 → ③ `Api.OsBridge` が登録されていれば pre-resolved path 付きで呼び出し。
- **OS 通知ブリッジ**:
  - **Assign (Windows)**: [FileSystem.PropagateRemoteChange](../src/dokan/src/FileSystem.cs) で `DokanInstance.NotifyUpdate(WindowsPath)` を呼び、Explorer に再描画依頼。`/` → `\` 変換は `ToWindowsPath` ヘルパ。
  - **Mount (Linux)**: Tmds.Fuse 高レベル API には `fuse_lowlevel_notify_inval_*` 相当が無いため OS ブリッジ未実装。`attr_timeout=0` のおかげで `stat` / `ls` 等の **アクティブな問い合わせ** は最新値を返す (InodeCache 経由で DB ヒット)。**`inotify` などのパッシブ subscriber には伝わらない** ことが制約として残る。低レベル API への移行 (or vendored Tmds.Fuse への notify patch 追加) は将来の課題。
- **LISTEN の確立は同期**: `NotifyChannel.Start()` 内で接続オープン + `LISTEN` 発行 + Information ログまでを呼び出しスレッドで完了。その後の wait ループだけバックグラウンド (`Task.Run`)。`mount.pgfs` 起動時の MOUNTED シグナル前にログが出るので、`-f` 経由 / fstab 経由のどちらでも `tests/linux/mount.log` で起動確認できる。
- **接続切断時の再接続**: wait ループ内で例外を catch して 2 秒待機 → 新規接続 → 再 `LISTEN`。`Pg.OpenConnection` 系の指数バックオフ retry とは別系統 (LISTEN 専用 connection は long-lived のため独自管理)。

検証: Linux 34/34 / Windows 24/24 ALL PASSED (`--notify` 有効でも回帰無し)。`tests/linux/mount.log` に `NotifyChannel: LISTEN pgfs_pgfs_notify (sender=...)` の Information ログが出ることを確認。**クロスクライアント実動作 (mount.pgfs を 2 台 + 一方の write が他方に届く) の自動テストは未整備** (e2e harness が 1 mount 前提のため)、手動検証で補う方針。

### OS に存在しない uname/gname のフォールバック

`mount.fallback_uname` (既定 `nobody`) / `mount.fallback_gname` (既定 `nogroup`) を `SaveTarget.Db` で実装 (セキュリティ設定なので FS 全体で 1 値持つ意図)。Linux `UserResolver` / Windows `WindowsUserResolver` のコンストラクタを `(string fallbackUname, string fallbackGname)` ベースに書き換え、起動時に fallback 名を OS API (`getpwnam` / `NTAccount.Translate`) で解決して内部 cache。失敗時の最終 hardcode は Linux uid=65534 (NFS の `nobody` 慣習値) / Windows `WellKnownSidType.AnonymousSid`、warning ログを出す。

これまで「実行プロセスの uid に化ける」「他人のファイルが自分のものに見える」セキュリティ事故になっていた挙動を解消。Linux e2e に `test_fallback_uname_gname` を追加 (DB 直接 INSERT で bogus uname の inode を作り、stat で `nobody`/`nogroup` を確認、DELETE クリーンアップ)。

### Windows e2e のテスト基盤

[tests/windows/](../tests/windows/README.ja.md) に `e2e.ps1` (テスト本体) / `flow.ps1` (mount → test → unmount の全フロー) / `flow.cmd` (cmd ラッパー) / `run.cmd` (テストだけ) を新設。Linux 版と対称な構造で 24 件: mkdir/file 基本/データ I/O (LO チャンクまたぎ)/truncate/rename/ReadOnly/Hidden+System+Archive xattr 往復/ドットファイル heuristic/dotfile un-hide 永続化/LastWriteTime/ボリューム情報/FindFilesWithPattern/並行アクセス。

POSIX 専用 (symlink / hardlink / chmod / chown / xattr API 公開) は DokanNet 非対応のため対象外、代わりに Windows 固有 (SetFileAttributes / SetFileTime / FindFilesWithPattern) を追加。

**Hidden / System / Archive 属性の xattr 永続化**: `pgfs_inode.xattrs` JSONB の `user.win_attrs` キーに `FileAttributes` のマスク値 (Hidden\|System\|Archive のみ) を 4-byte little-endian int で保存。**マスクが空 (全 OFF) でも xattr は削除せず 4 バイト 0 を書き込む**: 「xattr 有り = Windows 側で SetFileAttributes を触った」「xattr 無し = まだ触っていない」の二値で扱い、xattr 無しのときだけ「先頭ドット = Hidden」heuristic を fallback として適用。マスク 0 で削除する設計だと「ドットファイルを Explorer で un-hide → 次回 heuristic 復活でまた Hidden に戻る」UX バグを踏む。`ReadOnly` は引き続き st_mode の write ビット。実装は [src/assign/src/FileSystemUtils.cs](../src/dokan/src/FileSystemUtils.cs) の `WinAttrsXattrKey` / `LoadWinAttrs` / `SaveWinAttrs`。

### 書き込み path のレース修正

- **`Api.EnsureChunk`** の SELECT-then-INSERT race: Dokan が並行 WriteFile を発行すると、同じ `(data_id, chunk_index)` を 2 回 INSERT しようとして PK 制約 `pk_pgfs_data_chunk` で死ぬ (`HResult=0x8007013D` = `ERROR_MR_MID_NOT_FOUND` でエラー本文未展開、Windows で `Copy-Item` 2 MiB ファイルが `IOException` で落ちる症状)。`INSERT ... ON CONFLICT (data_id, chunk_index) DO NOTHING` に変更し、負けた tx の orphan LO は `lo_unlink` で回収、`FindChunkOid` で勝者の OID を取り直す形に修正。**Linux で踏まなかったのは FUSE のキャッシュ動作で並行 WriteData が起きにくいだけで、Linux 側にも潜在する race だった可能性が高い**。
- **`Api.EnsureDataRow`** にも同パターンの race が残っていたので予防的に修正: インメモリの `inode.DataId` が null と見える 2 つ以上の並行 tx が両方 `INSERT pgfs_data` → 後勝ちの `UPDATE inode SET data_id` で敗者の data 行が宙に浮き、そこへ書いたチャンクが到達不能になる silent なリーク + データ消失バグ。`pgfs_data.id` は serial で衝突しないため派手に死なないので、テストが通っていても実は壊れていたかもしれない。修正は `SELECT data_id FROM inode WHERE id = @id FOR UPDATE` で inode 行をロックして tx 間を直列化し、ロック取得後に DB の真の `data_id` を読み直す形 (キャッシュではなく DB を真とする)。`UPDATE` が 0 行に当たったら inode が並行に消えたと判断して throw → tx rollback で孤児 data 行を防ぐ。

### Linux e2e: symlink / hardlink 関連の修正

- **`Mount.FileSystem.SymLink(path, target)` の引数解釈を入れ替え**: フォーク前 Tmds.Fuse 0.1 の `FuseMount.Symlink(path*, path*)` は libfuse 由来 `(target, linkname)` を逆順に詰め替えて `IFuseFileSystem.SymLink` に渡してくる (DLL の IL 検証で確認: `ldarg.2 → ToSpan → ldarg.1 → ToSpan → callvirt SymLink`)。当方の override は arg1 をリンク内容、arg2 をリンク作成先と誤読していたため INSERT が一度も走らなかった。
- **`Api.DeleteInode` / `Api.CreateHardLink` でハードリンク兄弟 inode のキャッシュ無効化**: 従来は source/対象 inode しか `inodeCache.Invalidate` しておらず、3 つ以上のハードリンクや `rm a` 後の `stat b` で古い `st_nlink` を返していた。tx 内で `SELECT id FROM inode WHERE data_id = @did` で兄弟 ID を取得し、commit 後に全員 `Invalidate` する。

### `LoadFromDatabase` の recursive CTE 無限ループバグ → 解消

旧 `RootSettings.LoadFromDatabase` で root の `Id` / `ParentId` を `1` にしていたとき、BIGSERIAL の第 1 要素 id=1 が `(id=1, parent_id=1)` という自己参照行になり、recursive 部 `ON s.parent_id = st.id` が同じ行を無限に joins していた。root を id=0/parent_id=0 化することで解消 (BIGSERIAL は 0 を返さないので self-loop 行が原理的に存在し得ない、cycle 検出無しで安全な CTE が書ける)。`pgfs_inode` のルート (`id = 0`) とも慣習が揃った。

**この問題は `pgfs_settings` のフラット化で根本的に消滅** (`(scope, key)` PK には parent_id が存在しないため recursive CTE 自体不要)。本記述は当時の経緯メモ。

---

## コーディング規約 v4 の確立

[docs/coding-style.ja.md](design/coding-style.ja.md) を新設し、`.editorconfig` にブレース必須 + `this.` qualifier 強制を追加。条件分岐ルール (本体は「末尾フロー脱出 + 任意の文 N 個」または「単一文のみ」、`else` 禁止、ternary 禁止、多分岐は `switch`、ログ出力は副作用としてカウントしない) を v4 として明文化。`FirstList.cs` を v4 規約に追従 (FindSegmentNode 書き換え、InsertNodeFirst/Last の Try* 抽出)。`Api.cs` の Logger ガード 26 箇所をブレース化。

整理で旧 `Models/*Settings.cs` が削除されたので、そこに残っていた `else` / 三項演算子の大半も連れて消えた。

