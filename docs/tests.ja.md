# テスト一覧・実行方法・環境要件

> **道順**: [docs/README.md](README.md) › **本書**
>
> **この doc が正である範囲**: **テストのハブ** — どのスイートが何を見るか、件数、実行方法、環境要件。
> **件数の正は本書**で、各ランナーの README は操作の詳細を持つ。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../tests/linux/README.md](../tests/linux/README.md) | Linux 各スイートの**操作詳細**と内訳 |
> | [../tests/windows/README.md](../tests/windows/README.md) | Windows 各スイートの操作詳細 |
> | [../tests/citus/README.md](../tests/citus/README.md) / [../tests/docker/README.md](../tests/docker/README.md) | Citus / docker ランナー |
> | [Mount.md](Mount.md) / [Assign.md](Assign.md) | テストがカバーする**操作の一覧** |
> | [design/support_for_citus.md](design/support_for_citus.md) | Citus テストが検証する設計 |
> | [next.md](next.md) | 未着手のテスト項目 |

pgfs の全テストの **ハブドキュメント**。「どんなテストがあるか / どう実行するか / 何の環境が要るか」をここに集約する。各ランナーの細かいオプション (flow.ps1 のパラメータ等) は各ディレクトリの README を正とし、ここからリンクする。

最終更新: **2026-09-20** (handle-context 段階 B の回帰)。**2026-09-19** の記録は下の各行が持つ。同日の**前半**は文書とスクリプトの静的照合のみ (テスト未実行)。**後半に Linux / Windows 双方を実機で走らせた**:

- **Linux** (dev サーバ・Citus rf=2): ラウンド A 修正後に **4 スイートを再走して緑** — e2e を `write_back_metadata` off / on 両方 (43 passed + 1 skip)・`writeback.sh` 8/8・`wbmeta.sh` 17/17・`negcache.sh` 7/7。
- **Windows** (windows_client + Dokan 2.3.1): **7 スイート = 計 75 件**を実機で走らせた — e2e 41 / cross-client 10 / write-back 6 / metadata write-back 5 (4 passed + 1 skip) / control-plane 7 / **prune 4** / **write-back cross-client 2**。**`write_back` / `write_back_metadata` を on にした受入も完了**している (専用スイートがマウントを自分で張り替える)。各スイートの結果と環境は下表。

- **2026-09-20 (handle-context 段階 B)**: **Windows 5 スイートを再走して全緑** — e2e 35 / **cross-client 9** (段階 B の 2 件を追加) / write-back 6 / metadata write-back 4 passed + 1 skip / control-plane 6。Linux 側も 7 スイート全緑 (**crossclient 10**)。 **⑥ (`HandleTable.Count` の status 露出) を入れたあと同じ 5 スイートをもう一度再走して全緑** — Core を触ったため。
- **2026-09-20 (append の末尾を Core が決めるようにした)**: `Api.AppendData` の投入後、**Windows 5 スイートをもう一度全走して全緑** (e2e 35 / cross-client 9 / write-back 6 / metadata write-back 4+1skip / control-plane 6)。**Windows 側は段階 B で既に閉じていた**ので新規テストは足していない (再現テストは Linux 側が持つ)。
- **2026-09-20 (`st_size` の巻き戻り修正)**: 書き込み経路の `st_size` を `GREATEST` で単調にしたあと、**Windows 5 スイートを全走して全緑** (e2e 35 / cross-client 9 / write-back 6 / metadata write-back 4+1skip / control-plane 6)。**新規の `test_x_concurrent_append_keeps_all_bytes` は登録していない** — 巻き戻りは消えたが**並行追記で 10 バイト消える**ぶんが残っており (案 ③ 待ち)、**赤いテストを常設しない**ため。関数は残してあるので ③ と同じコミットで有効にする。
- **2026-09-20 (append の末尾を tx の中で確定・案 ③)**: **Windows 5 スイート全走で全緑** (e2e 35 / **cross-client 10** / write-back 6 / metadata write-back 4+1skip / control-plane 6)。**`test_x_concurrent_append_keeps_all_bytes` を有効化** — 対応前 −4,194,304 / 単調化のみ −10 / 現行 **0** で、**3 回連続緑**。
- **2026-09-21 (handle-context C-1)**: **Windows 5 スイート全緑** (e2e 35 / cross-client 10 / write-back 6 / metadata write-back 4+1skip / **control-plane 7**)。新規 `test_cp_open_inodes_counted` は **`OpenHandle` を外したビルドに当てて落ちること**を確認済み。
- **2026-09-21 (`FileIndex` を data_id 由来に)**: **Windows 5 スイート全緑** (**e2e 36** / cross-client 10 / write-back 6 / metadata write-back 4+1skip / control-plane 7)。新規 `test_file_index_is_data_id_and_stable` は **`inode.Id` を返すビルドに当てて落ちること**を確認済み。`test_cp_open_inodes_counted` は**ベースライン差分**で見るように直した (絶対値 0 前提だと先行テストのハンドルがスナップショットに残っているだけで落ちる)。
- **2026-09-21 (handle-context C-2 の Core)**: **Windows 5 スイート全緑** (**e2e 37** / cross-client 10 / write-back 6 / metadata write-back 4+1skip / control-plane 7)。新規 `test_rename_over_open_victim_keeps_body` は **修正前ビルドに当てて落ちること**を確認済み。**`unlink` 側のテストは作ったが捨てた** — Windows は最後のハンドルまで削除を降ろさないので**修正前でも緑**になり、検出力がゼロだったため。
- **2026-09-21 (C-3 `pgfsctl prune`)**: **自動テストは無い** (残骸を作るのに psql が要るので Linux 側のスイート向き)。**実機で全経路を手で確認済み** — dry-run が古い `mounts` 104 行と墓標 1 件を正しく分け、**live なマウントがある状態の `--apply` はデータ側を飛ばし**、止めてから撃つと人工の孤児 data と `.fuse_hidden` を消した (DB で検証)。
- **2026-09-21 (Windows の prune スイート新設)**: **孤児 data の「本物」を作れるのは Windows だけ** (Linux の libfuse は open 中の `unlink` を改名にすり替えるので `Api.DeleteInode` が呼ばれない)。**C-2 の保持を外したビルドに当てて `孤児 data が増えていない (0 → 0)` で落ちること**を確認済み。あわせて **`kill -9` 直後の 90 秒は死んだ行が live に見えて prune のデータ側が止まる**のを実測し、**同一ホストなら pid の生存も見る**ようにした。
- **2026-09-21 (full docker ランナーを現行 HEAD で再走)**: **50 passed / 0 failed** (`tests/docker/run.sh`・単一 PG・linux_client)。**件数は 48 → 50 が実測**。1 回目は `test_ino_namespace_split` が落ちたが **FS ではなくテストの問題**で、**コンテナに `python3` が無く比較が空になっていた** (値 9223372036854775934 は最上位ビットが立っている = 正しい)。**bash だけで比べる `ge_2pow63` に置き換えて 50/50**。
- **2026-09-21 (docker を使う Citus スイートを現行 HEAD で再走)**: **`test_matrix.sh` 18/18** / **`race_multinode.sh` 4/4** (linux_client・`citusdata/citus:latest`)。**多ノード Citus 上の e2e は 50/50**、`pgfs_lock` は **rows=309 / 72kB**。**どちらも最初は落ちたが、原因は 2 つともテスト側の古さ** — **`lock` の非分散化と `mounts` の追加が反映されておらず dist=5/local=1 を期待していた** (現行は **dist=4 (inode/data/data_chunk/audit) / local=3 (lock/settings/mounts)**)。**`pg_tables` はパーティション親 (`pgfs_audit` = relkind `p`) を返さない**ので、テーブル存在の確認は `pg_class` で数えるよう直した。
- **2026-09-21 (`SetFileAttributes` の ReadOnly が mode を作り直す件を修正)**: **Windows e2e 38/38** / cross-client 10/10 / prune 2/2。新規 `test_readonly_dir_keeps_execute` は **古い実装 (0444/0644/0755 へ作り直す) に当てて `ReadOnly を付けたらディレクトリの実行 (traverse) 権が消えた` で落ちること**を確認済み。DB でも **0755 → (RO) → 0555 → (解除) → 0755** と完全に往復することを確認した。
- **2026-09-21 (ternary を switch 式へ・22 箇所)**: **Windows e2e 41/41** /
  **control-plane 7/7** / **prune 4/4**。**件数は変わっていない** (挙動を変えないリファクタなので新規テストは無し)。
  回すスイートは**触った場所で選んだ** — `StatusCommand` / `PruneCommand` がいちばん変わったので
  control-plane と prune を足した。`dotnet build` は 0 エラーで、**触った 5 プロジェクトと `Api.cs` に
  警告が増えていない**ことも確認している。

**docker を使う 3 本 (full docker e2e / Citus matrix / race multinode) も 2026-09-21 に現行 HEAD で再走済み**である (上の行)。**実行場所は linux_client** — dev サーバには docker が無いので、そこからは構造的に回せない。

> **背景 (このドキュメントを作った理由)**: テスト環境がホスト依存 (Windows ホスト → ssh で linux_client / pgsql_server) でばらついているので、**docker に寄せて再現性を上げられないか** を検討するための棚卸し。docker 化の分析は末尾の [§docker 統合の検討](#docker-統合の検討) を参照。

---

## テスト一覧

| スイート | 件数 | 何を見るか | 場所 | 詳細 README |
|---|---|---|---|---|
| **Linux e2e** | **50** | mount.pgfs (FUSE) の主要オペレーションを実 FS 操作で確認 (POSIX ACL setfacl/getfacl / xattr バイナリ往復 / timestamp UTC 往復 / スパース du / チャンク部分上書きの整合 / **rename-over-existing の置換** / **`st_ino` の名前空間分離** (ファイル = `data_id | 2^63` / ディレクトリ = `inode.Id`。Windows の `FileIndex` と同じ式) 含む) | [tests/linux/e2e.sh](../tests/linux/e2e.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **write-back 専用** | 9 | write-back キャッシュ (`mount.write_back`) の耐久性契約。**マウントを自分で張り替える**ので e2e とは別スクリプト (fsync/close 後の `kill -9` 耐性 / 正常 unmount の flush / back-pressure / read キャッシュ無効時の dirty 可視性 / 再マウント後の部分上書き / **ライブ無効化の二相 flip**) | [tests/linux/writeback.sh](../tests/linux/writeback.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **メタデータ write-back 専用** | **27** | メタデータ write-back (`mount.write_back_metadata`) の契約。**マウントを自分で張り替え、DB 側は psql で直接確認する**。ステージ 1 の 4 件 (pending 台帳の整合) + ステージ 2 以降の 13 件 = **耐久性契約 3** (`fsync` は残る / **`close` だけは消える** = 契約なので「消えること」を assert。**この契約が効くのはこのマウントが新規作成したファイルだけ** (既存ファイルの上書きは close で書き切る) / `fsyncdir` は直下の pending を永続化) + **同期化ヒューリスティック 5** ((a) rename-over-existing で旧も新も失わない × pending / O_EXCL source、(b) truncate 済み inode の close は同期 × `O_TRUNC` / `truncate(2)`、(c) `O_EXCL` create は write-through) + **回帰 1** (予約 data_id のままの read が `-EIO` にならない) + **二相 flip 1** + **エラーの底 2** (ブロッキング back-pressure / エラーステートの `-EIO` + `status` 赤 + 自動解除。**psql で種別違いの同名行を入れる fault injection** 付き) + **ハードリンク兄弟 1** (追加・A-1 の回帰) + **B-1 ノブ 3** (`defer` で pending になる / 同一マウント内は EEXIST / 衝突で占有者を消さず error latch し unlink で回復) + **B-2 喪失の DB 記録 1** (墓標が残る / 次回マウントの親 stderr に警告 / 墓標を消すと警告も消える) + **B-3 取り消し監査の即書き 1** (unmount 前の時点で DB に create/delete の 2 行がある) + **B-4 偽の喪失を作らない 1** (audit を live off した後の正常 unmount で墓標ができない) + **B-5 エラーステートの即書き 1** (heartbeat 周期を待たずに mounts へ載る / 解除も即) + **B-6 喪失レポート 1** (パス付き + 打ち切りの内訳) + **B-7 破壊操作の遮断 1** (persisted の unlink/truncate は止まり pending の unlink は通る) + **B-9 実効モード 1** (二相 flip の受付停止中が status に出る) | [tests/linux/wbmeta.sh](../tests/linux/wbmeta.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **起動まわり (Linux)** | 10 | `mount.pgfs` の起動時の契約 (`-o max_write` を libfuse へ転送しない / 適用できない `-o` は警告する / `started (pid N)` が実デーモン / 未解決 uname・gname の fallback / **libfuse を壊すキーを転送しない** / **同梱サンプル `pgfs.toml.example` がそのままの書き方で読めること** — 接続だけこの環境向けに差し替えて実際にマウントする。**3 段のキーが戻ったら落ちる**) | [tests/linux/startup.sh](../tests/linux/startup.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **cross-client 専用 (Linux)** | **18** | 同じ DB-FS を **2 マウント**して可視性を見る (作成 / 削除 / 上書き / 置換 rename / **ハードリンク兄弟の st_size 伝播** / `O_EXCL` の cross-client 排他 / **親を消された後の 4 経路の作成が ENOENT になること** / **handle-context 段階 B の 2 件** = 開いたままの fd が rename + 同名再作成を跨いでも最初の inode を指すこと + data 付け替えを跨いだ write のガード / **`O_APPEND` の追記が本当の末尾に着くこと** = 案 ② の再現テスト。**A 側で `stat` を挟まないのが要点**で、挟むとカーネルの `i_size` が更新されて修正前でも通る / **`max_write` 超えの追記に他マウントが割り込んでもバイトが失われないこと** = 案 ③ の回帰。**守ると言ったのは総バイト数だけ**で順序は見ない。**落ち方が「期待 N / 実際 N−M」と差分で出る**) / **`.fuse_hidden*` が列挙に出ないこと 2 件** = 本物の残骸を作って**他マウントの `ls` に出ない**ことと**その fd がまだ読める**ことを対で見る + **書式違い (利用者が作った名前) は隠さない**こと)。**通知を取りこぼしたときに「キャッシュ全破棄」で復帰できること** (M-2 の回帰。**psql で直接 DB を書き換えて「通知が来なかった」状態を作り**、`pg_notify` で `{"r":true}` を撃つ。溢れそのものは 1024 件積む必要があるので受け手側だけを見る) / **write-back の flush が他マウントの `truncate` を巻き戻さないこと** (`DirtyFile.WriteEnd` の回帰。**A だけ `--write-back --write-back-interval-ms 0` で張り直す**。時間トリガを切らないと窓が勝手に閉じて修正前でも緑になる) / **他マウントが縮めた後の「伸ばさない上書き」でバイトが到達不能にならないこと** (H-1 の回帰。**notify OFF で張り直して A のキャッシュを古いまま残す**のが要点で、ON だと truncate 通知でキャッシュが落ちて窓が閉じる。**B の stat では判定しない** — notify OFF の B が自分の 0 を持ち続けるのは仕様なので、psql で DB の権威値を見る)。**両マウントを `--notify` で起動する** (既定 off では他マウントの変更が見えない。**notify OFF が要る 2 件だけ最後に張り直す**) | [tests/linux/crossclient.sh](../tests/linux/crossclient.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **ハンドルリーク専用 (Linux)** | **11** | ハンドル表 (`HandleTable`) のリーク回帰。`pgfsctl status` Layer 3 の `handles : N open / peak M` を見て、**開いたハンドルが必ず返っている**ことを確認する (アイドルは 0 / open・close が釣り合う / **fd を複製して片方だけ閉じても残ったほうが生きている** / readdir が釣り合う / 混在ワークロード後に 0 / peak は単調増加)。**スナップショットは制御 NOTIFY の ping で撃たせる**ので psql 必須。**値を検証できるのは Linux だけ** (Dokan は表を通さないので Windows は常に `0 open / peak 0`)。**段階 C-1 の実体カウント 4 件**も持つ (別々の 3 ファイル / **同じファイルを 3 本開いても 1** / **dup した fd も 1** / ディレクトリの fd で上がって閉じて戻る)。実体カウントは `{prefix}mounts.stats` の **JSON** から読み、**テキスト出力 (`N open / peak M / K inodes`) は専用の 1 件**で見る (JSON だけ見ていると描画が壊れても気づかないため) | [tests/linux/handles.sh](../tests/linux/handles.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **`prune` 専用 (Linux)** | **11** | `pgfsctl prune` (異常終了が残したものの掃除) の契約。**既定が dry-run** / **墓標 (`unflushedLoss > 0`) を消さない** / **live ゲート** (生きているマウントが居ればデータ側に触らない・mounts の古い行は消してよい = 種類ごとに live 判定が違う) / **heartbeat を落としただけの生きているマウントを死んだ扱いにしないこと** (H-3 の回帰。自分の登録行の heartbeat を 10 分前へ倒して「90 秒 < x < 3600 秒」の空白帯を作る。**デーモンは 30 秒ごとに書き直すので倒したらすぐ撃つ**) / 古い mounts 行・孤児 data・`.fuse_hidden*` が実際に消えること / **利用者が自分で `.fuse_hidden...` という名前を付けたファイルは消さないこと** (`test_prune_keeps_user_named_fuse_hidden` — 列挙側は厳密判定なので `ls` に見えている。**接頭辞一致で消すと見えているものが消える**) (**`.fuse_hidden` は `kill -9` の直後にそのまま撃つ** — heartbeat だけで live を判定していると**クラッシュ直後の 90 秒が掃除できない**ので、**同一ホストの pid 生存も見ている**ことをここで固定する)。**孤児 data は psql で人工的に作る** (Linux では自然に生まれない — libfuse が open 中の unlink を改名にすり替えるため。生まれるのは Dokan の delete-on-close 経路) | [tests/linux/prune.sh](../tests/linux/prune.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **negative cache 専用** | 7 | negative lookup キャッシュ (`mount.negative_cache_ttl_ms`) の可視性契約。**マウントを自分で張り替え、他クライアントを psql 直接 INSERT で代用** (自分の create/mkdir/rename は即時可視 / 他クライアントは TTL 内不可視 → TTL 後可視 / readdir と live reload がマーカーを掃除) | [tests/linux/negcache.sh](../tests/linux/negcache.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **Linux e2e (full docker)** | 50 | 上記 Linux e2e を **単一 PG + mount コンテナ**で完結 (ssh linux_client / ホスト dotnet 非依存) | [tests/docker/run.sh](../tests/docker/run.sh) | [tests/docker/README.md](../tests/docker/README.md) |
| **Windows e2e** | 42 | assign.pgfs (Dokan) の主要オペレーションを実 FS 操作で確認 (ACL 投影 Get/SetFileSecurity / df GetDiskFreeSpace /, 追加の Windows 基礎 3 件 = 排他 CreateNew / allocation が EOF を伸ばさない / 同一対象 rename 含む)  / **`FileIndex` が data_id 由来で誕生から不変** (handle-context, 追加)  / **rename で上書きされた側を開いたままのハンドルが実体を読み続けられる** (handle-context C-2, 追加)  / **ディレクトリに ReadOnly を付けても実行 (traverse) 権が落ちない** (追加)  / **名前空間の方針 3 件** ([namespace-policy.md](design/namespace-policy.md)) = **予約名と末尾の空白・ドットを Windows の入口で弾く** (どちらも **`\\?\` 経由で測る** — 素の Win32 パスだと `NUL` が NUL デバイスに解決され、末尾の空白・ドットは正規化で落ちて **FS に届かない**) / **`.fuse_hidden*` が Windows では列挙に出て `Remove-Item` で消せて親も rmdir できる** | [tests/windows/e2e.ps1](../tests/windows/e2e.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows cross-client** | 11 | 同じ DB-FS を **2 つの assign.pgfs でマウント**したときの排他 (同時 `CREATE_NEW` / 同時 `mkdir`) と可視性 (create / 上書き / delete / 置換 rename が相手から見える) / **handle-context 段階 B の 2 件** = 開いたままの append ハンドルが相手マウントの伸長を見ていること (問題 2 の再現) + rename + 同名再作成を跨いでも最初の inode を指すこと (問題 1 のガード)。可視性は `--notify` 付きで起動したときのみ実行し、無い構成では SKIP  / **並行追記でバイトが失われないこと** (2 マウントから同時に追記して総バイト数を assert。順序は見ない) | [tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows write-back** | 6 | `mount.write_back` を **on にした** assign.pgfs の耐久性契約。**マウントを自分で張り替え、プロセスを強制終了して再マウントする** (FlushFileBuffers 後 / close 後 / **WRITE_THROUGH** は残る、**バリア無しは失われる** = negative control、正常アンマウントは exit 0 で書き切る、3 MiB のハッシュ一致) | [tests/windows/writeback.ps1](../tests/windows/writeback.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows metadata write-back** | 5 | `mount.write_back_metadata` + B-1 ノブ (`write_back_metadata_exclusive_create`) の契約を **実 2 マウント**で (A=`defer` / B=write-through)。defer でも同一マウント内の排他は維持 / close-no-flush で内容が失われる / **占有者の内容を消さない (B-1 の要件)** / unlink での回復 / **B-7 のエラーステート中の削除ブロック** | [tests/windows/wbmeta.ps1](../tests/windows/wbmeta.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows control-plane** | 7 | **開いている実体の数 (`handles.inodes`) が status に出ること** (handle-context C-1) を含む。 `pgfsctl config` / `status` の Windows 受入。`config list` / `get` / **live `set` が稼働中のマウントに効く** (notify 無しでも制御チャネル経由で届く) / `status --json` の live 行に**実効設定**が出る / EnumField が許可外の値を `set` 時に弾く / **data write-back の live on → off (二相 flip) を跨いでデータが無傷** / **metadata write-back の flip が完走する** (pending 300 件を抱えた状態で off にして、受付再開・pending 0・ファイル無傷まで確認) | [tests/windows/control_plane.ps1](../tests/windows/control_plane.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows prune** | 4 | **C-2 の実体保持が本当に残骸を生み、`pgfsctl prune` が掃除できるか**を通しで見る。victim を開いたまま rename で上書き → **close させずに kill** → 孤児 data が残る → `prune --apply` で消える。**DB は覗かず `prune --json` の申告だけで判定する** (Windows に psql が無いのと、「見つけたと言うものが本当に消えるか」が契約そのものだから)。**2026-09-21 に「利用者のファイルを消さない」回帰を 2 件追加** — `.fuse_hidden_notes.txt` (接頭辞一致の緩さ) と `.fuseXhidden<16 hex>` (LIKE の `_` 未エスケープ) を作り、**scan が拾わないこと**と**`--apply` を通しても中身ごと残ること**を見る。**修正前の HEAD に当てて 2 件とも落ちることを確認済み** (scan が 0 → 2 件拾い、`--apply` が `fuse_hidden_deleted = 2` で実際に消した) | [tests/windows/prune.ps1](../tests/windows/prune.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows write-back cross-client** | 2 | **write-back のマウントが dirty を抱えている間に他マウントが同じ実体を書いたとき**の契約。①**他マウントの `truncate` を flush が巻き戻さないこと** (Core 側で修正済。修正前は `st_size` が 32 に戻ることを実測) / ②**A が読んだことのあるチャンクに他マウントが書いたバイトは A の flush で消えること** (**望ましい挙動ではなく「いまはこうなる」を固定する契約テスト**。塞いだら**テストのほうを書き換える** — 正は [write-back.md §cross-client の契約](design/write-back.md))。**`--write-back-interval-ms 0` と P/Invoke での書き込みが必須**で、**ログの `WriteFileProxy` が増えたことを確認してから進む** (`FileStream` の 4096B バッファだと書き込みが FS に届かず、修正前でも緑になる) | [tests/windows/wbcross.ps1](../tests/windows/wbcross.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **制御プレーン** | シナリオ別 | notify ON の reload、notify OFF の config set、status の登録・統計・snapshot | [control_plane.sh](../tests/docker/control_plane.sh) / [control_plane_ctl.sh](../tests/docker/control_plane_ctl.sh) / [status.sh](../tests/docker/status.sh) | [tests/docker/README.md](../tests/docker/README.md) |
| **Citus mkfs マトリックス** | 18 | `mkfs --citus / --worker / --clean` の組合せ挙動 (新規 / 既存維持 / 再構築) | [tests/citus/test_matrix.sh](../tests/citus/test_matrix.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus multinode probe** | 13 セクション | Citus 仕様の挙動確認 (auto-sync / DDL 伝搬 / shard 配置 等) の one-off probe | [tests/citus/multinode_probe.sh](../tests/citus/multinode_probe.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus race multinode** | 4 | 多ノード Citus + 2 mount client での Phase 3 排他制御 + 多ノード e2e | [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **監査ログ専用** | 12 | 多ノード Citus + 1 mount client で監査ログ固有の振る舞い (各 op 記録 / caller_* / パーティション自動作成 = 月跨ぎ機構 / `audit.enabled=false` で 0 行) | [tests/citus/audit.sh](../tests/citus/audit.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **df (statfs) 専用** | 7 | 多ノード Citus (coord+worker1, plperl 入り自前イメージ) で `pgfs_statfs()` の worker 集約 + `require`/`auto`/`nominal` 3 モード。R5 で集約機構を証明 | [tests/citus/statfs.sh](../tests/citus/statfs.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus verify** | SQL 診断 | 1 ノード Citus セットアップ後の `citus_tables` / 分散キー / shard 配置 / EXPLAIN | [tests/citus/verify.sql](../tests/citus/verify.sql) | [tests/citus/README.md](../tests/citus/README.md) |

### Linux e2e (48 件) のカテゴリ

ディレクトリ操作 / ファイル基本 / データ I/O (bytea) / 名前変更 (**over-existing の置換 = tmp+rename パターン含む**) / 権限 (chmod/chown) / シンボリックリンク / ハードリンク / xattr (**バイナリ NUL/高位バイト往復含む**) / **POSIX ACL (setfacl/getfacl)** / メタデータ (StatFS/utime/**timestamp UTC 往復**/**スパースファイルの du = st_blocks**) / **チャンク書き込みの整合 (部分上書き / close を挟んだ追記 / 未 flush の read-after-write / truncate で dirty を捨てる)** / 並行性 / 名前解決 fallback。カバー範囲は [docs/Mount.md](Mount.md) の ✅ オペレーション。

### Windows e2e (35 件) のカテゴリ

ディレクトリ操作 / ファイル基本 / データ I/O (bytea) / truncate / 名前変更 / 属性 (ReadOnly/Hidden/System/Archive) / ボリューム・パターン / **所有者 (要求元アカウント) / グループ (親から継承)** / **CopyFileEx (Copy-Item の双方向コピー)** / **byte-range lock (ドライバ強制)** / **Windows 基礎 (排他 CreateNew / allocation / 同一対象 rename)** / 並行性。Windows にもリンク機能はあるが、pgfs の現在の Dokan アダプタに native link / 任意 xattr の入口がないため対象外、代わりに Windows 固有を追加。カバー範囲は [docs/Assign.md](Assign.md) の ✅/⚠️ オペレーション。

**スイートを回すと `{prefix}mounts` に行が残る**: 1 周あたり **writeback +4 / wbmeta +1 / e2e ±0**。
耐久性を見るために**異常終了を意図的に起こす**スイートがあるためで、異常ではない。放置すると
`pgfsctl status` の Layer 1 が読めなくなるので適宜消すが、**墓標 (`stats->>'unflushedLoss'` が非 0) は
除外**すること — 墓標は「何が失われたか」の唯一の記録で、消すと追えなくなる。

**実行シェル**: `flow.cmd` / `run.cmd` は **pwsh (PowerShell 7) があればそちらを優先**する。Windows PowerShell 5.1 は環境によって `Microsoft.PowerShell.Security` / `Utility` のロードに失敗し (TypeData 重複)、`Get-Acl` / `Get-FileHash` が使えず **テスト側の都合で 3 件 FAIL する**ため。pwsh なら同じ 3 件が PASS する。

### 既存文書に記録された結果（今回の再実行ではない）

| スイート | 結果 | 検証環境 |
|---|---|---|
| Linux e2e | **43 passed / 0 failed / 1 skipped (44 件)** | dev サーバの Citus rf=2 上で緑 (**2026-09-19 のラウンド A 修正後に `write_back_metadata` の off / on 両方で再走して緑**。初出は 2026-08-10・Phase 0 の rename 原子化 + `test_rename_replace_existing` 追加後)。43 件時代は 42/43 で **`write_back` on / off の両モードで緑** (2026-07-25)。skip は `test_fallback_uname_gname` (`ssh pgsql_server` 不到達)。38 件時代は 37/38 (timestamp UTC + 実占有バイト修正後)、36 件時代は 36/36 (linux_client) |
| write-back 専用 | **8/8 PASS** | dev サーバの Citus rf=2 (2026-07-25・2026-08-12・**2026-09-19 のラウンド A 修正後**の再走で緑)。`fsync`/`close` 後に `kill -9` してもデータが残ることを実測。`test_close_survives_crash` は **`write_back_metadata` off の 1d 契約**の回帰なのでそのまま維持する |
| メタデータ write-back 専用 | **27 passed / 0 failed (27 件)** | dev サーバの Citus rf=2 (ラウンド A 修正 + B-1〜B-9 後。`audit.enabled = true` で実行)。`test_meta_truncate_syscall_close_is_synchronous` の FAIL は A-6/A-7 の修正で解消し、A-1 の回帰テスト `test_meta_hardlink_sibling_survives_unlink` を追加した。**DB 確認テストは `PGFS_PSQL=/usr/local/pgsql/bin/psql` を渡さないと 7 件が skip になる** (この環境の `psql` は alias で、非対話シェルでは展開されないため) |
| negative cache 専用 | **7/7 PASS** | dev サーバの Citus rf=2 (2026-08-10・**2026-09-19 のラウンド A 修正後**の再走で緑)。live reload (`pgfsctl config set` → TTL 0 で全クリア) 含む |
| Linux e2e (full docker) | **50/50 ALL PASSED** | 単一 PG (postgres:17) + mount コンテナ、linux_client 上で実機検証 (**2026-09-21 に現行 HEAD で再走**)。**コンテナに `python3` は無い** — 依存すると「正しい FS を落ちていると数える」ので、テスト側は bash だけで書くこと。`CACHE_MAX_ENTRIES=8` 変種で回すと新テストが DB 読み戻し経路も踏む |
| Windows control-plane | **7/7 PASS** | **2026-09-21 に `test_cp_open_inodes_counted` を追加して 7 件** (開く前 0 → 3 本開いて 3 → 閉じて 0。`OpenHandle` を外したビルドで落ちることを確認済み)。`stats.handles` のキー存在チェックも `test_cp_status_json_shape` にある (`open` / `peak` は FUSE 側の値なので Windows では 0、`inodes` は両 OS で動く)。 windows_client → pgsql_server の `pgfs` スキーマ。**notify を付けずにマウントしても `pgfsctl config set` が届く**ことを確認 (制御チャネルの LISTEN は常時 ON)。反映は heartbeat スナップショット経由なので**最大 1 周期 (30 秒)** かかる。設定はテスト終了時に元へ戻す。**B-9 の遷移時 heartbeat のおかげで、flip の第 1 相 (受付停止中) を実際に観測できた** |
| Windows metadata write-back | **4 passed / 1 skipped (5 件)** | | windows_client で P:(defer) + R:(write-through) の 2 マウント、pgsql_server の `pgfs` スキーマ。**B-1 の要件 (占有者を消さない) は実証**。**B-7 のエラーステート中の削除ブロックも実証済** (`Api.CanDestroy` を Dokan の 5 箇所に配線)。SKIP は `test_meta_defer_conflict_recovers_by_unlink` — **Core 側の latch バグは 6755c8d で修正済**だが、**Windows は最後のハンドルが解放されるまで `DeleteFile` を遅らせる**ため、テストが待つ間に削除要求が FS に届かず**この環境では回復を観測できない** (実測: 解除まで 16 秒 → 41 秒 → 63 秒とポーリングするほど後ろへ押される)。Linux 側で契約は検証済み。副産物として **Windows では exit 4 が呼び出し元に届く**ことを確認 |
| Windows write-back | **6/6 PASS** | windows_client で `--write-back --write-back-interval-ms 0` (背景 flush の時間トリガ無し = negative control の teeth を確保)、pgsql_server の `pgfs` スキーマ。**metadata write-back on の受入は未実施** (`-WriteBackMetadata` で回せる形だけ用意) |
| Windows cross-client | **10/10 PASS** | windows_client で P: と R: に 2 マウント (どちらも `--notify`)、pgsql_server の `pgfs` スキーマ。**同時 `CREATE_NEW` は 6 ラウンドとも成功 1 件**。`--notify` を外すと可視性 4 件は「古いまま見える」= notify off の仕様どおり (SKIP 扱い)。**削除の可視性は `NotifyDelete` を撃つように直してから安定**した (それ以前は 3 回中 1 回 FAIL) **2026-09-20 に handle-context 段階 B の 2 件 + 並行追記の 1 件を追加して 10/10**。`test_x_append_handle_sees_peer_growth` は段階 A のビルドに当てて落ちること (11 バイト → **6 バイト**の損失) を確認済み |
| Windows e2e | **38/38** | windows_client + Dokan 2.3.1 → pgsql_server の `pgfs` スキーマ (Citus rf=2・audit on)。**`test_file_index_is_data_id_and_stable` を追加して 36 件**。旧 34 件時代の**間欠 FAIL の原因は Windows クライアント側の FCB キャッシュ** — DB の削除はログで確認済みなのに、別プロセスがハンドルを持っていたファイルが 10 秒以上列挙・open できる (3 回に 1 回。詳細は [windows-parity.md §削除の可視性の実測](design/windows-parity.md))。旧 27/27 は 2026-06-03 |
| Linux e2e (Citus rf=2) | **42 passed / 0 failed / 1 skipped (43 件)** | dev サーバの共有 Citus 13.1 クラスタ (coordinator + worker 3 台) に `mkfs --citus --rf 2 --shard-count 8` で分散した FS を FUSE マウントして実行。`{prefix}lock` を citus local に変えて行ロックを 1 箇所へ集約した後の検証 |
| Citus mkfs マトリックス | **18/18 PASS** (2026-09-21 再走) | Citus 14.0.0 docker on linux_client |
| Citus race multinode | **4/4 PASS** | linux_client の docker Citus (**2026-09-21 に現行 HEAD で再走**)。多ノード Citus 上の e2e **50/50** / 並行 write race 4 ラウンドで md5 一致 / 並行 mkdir 20×2 で常に片方 EEXIST / `pgfs_lock` rows=309・72kB |
| 監査ログ専用 | **12/12 PASS** | Citus docker on linux_client |

Windows 側の write-back 契約 (data / metadata) と終了時喪失報告 (exit 4) は、専用スイート ([writeback.ps1](../tests/windows/writeback.ps1) / [wbmeta.ps1](../tests/windows/wbmeta.ps1)) を足して**検証済**である。
**cross-client の CreateNew 競合も [crossclient.ps1](../tests/windows/crossclient.ps1) で検証済** (6 ラウンドとも成功 1 件)。残る Windows 側の未検証項目は
**ドメイン環境での名前解決** (`CORP\alice` と `LOCAL\alice` が同じ `alice` に潰れる) と **削除の可視性の残件** (クライアント FCB キャッシュ由来の間欠 FAIL) の 2 つで、as-built は [Windows 展開設計 §実装ステータス](design/windows-parity.md) が正。追加予定の受入条件は [Windows 展開設計](design/windows-parity.md)。各ランナーの操作方法は各ディレクトリ README を正とする。

---

## 実行方法

### Linux e2e

```cmd
REM 全フロー (rsync → publish → mount → test → unmount)
tests\linux\flow.cmd
REM テスト名フィルタ
tests\linux\flow.cmd xattr
REM テストだけ (マウント済み前提)
tests\linux\run.cmd
```

Linux ホスト上で直接:

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

オプション (`-NoSync` / `-NoBuild` / `-NoMount` / `-KeepMounted`) と環境変数 (`PGFS_TEST_PG_EXEC` 等) は [tests/linux/README.md](../tests/linux/README.md)。

### Windows e2e

```cmd
REM 全フロー (mount → test → unmount、ビルドは既定スキップ)
tests\windows\flow.cmd
REM 先に publish してから
tests\windows\flow.cmd -Build
REM テストだけ (マウント済み前提)
tests\windows\run.cmd
```

```powershell
# cross-client (2 マウント同時。自分で P: と R: にマウントして片付けるまで行う)
pwsh -NoProfile -File tests\windows\crossclient.ps1
pwsh -NoProfile -File tests\windows\crossclient.ps1 -MountB S:   # 空いているドライブレターを指定
```

詳細は [tests/windows/README.md](../tests/windows/README.md)。`flow.cmd` / `run.cmd` は **pwsh があれば pwsh** を使う。

### Citus 系 (すべて linux_client 上の bash で実行)

```bash
bash tests/citus/test_matrix.sh        # mkfs 18 ケースマトリックス
bash tests/citus/multinode_probe.sh    # Citus 仕様 probe
bash tests/citus/race_multinode.sh     # Phase 3 排他制御 + 多ノード e2e
bash tests/citus/audit.sh              # 監査ログ専用 (各 op 記録 / caller_* / パーティション / enabled=false)
```

```cmd
REM 1 ノード Citus 構成診断 (Windows ホスト → ssh pgsql_server)
tests\citus\verify.cmd
```

詳細は [tests/citus/README.md](../tests/citus/README.md)。

---

## 環境要件

各スイートが **今** 必要としている環境。docker 化の検討材料。

| スイート | 実行ホスト | DB | マウント層 | docker | リモートホスト依存 |
|---|---|---|---|---|---|
| Linux e2e | Windows → ssh Linux | PG (fallback テストは別途 psql 到達が必要) | libfuse3 | なし | **linux_client** (ビルド + mount 実行) |
| Windows e2e | Windows ローカル | PG | Dokan 2.x ドライバ | なし | なし (ローカル完結) |
| Citus mkfs マトリックス | ssh Linux (bash) | **docker Citus** (coord + worker1) | なし (mkfs のみ) | **あり** | linux_client (docker daemon) |
| Citus multinode probe | ssh Linux (bash) | **docker Citus** (coord + worker) | なし | **あり** | linux_client |
| Citus race multinode | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount は **ホスト** で起動) | DB のみ docker | linux_client |
| 監査ログ専用 | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount は **ホスト** で起動) | DB のみ docker | linux_client |
| Citus verify | Windows → ssh pgsql_server | **1 ノード Citus on pgsql_server** (実機) | なし | なし | **pgsql_server** |

### 共通の前提

- **.NET 10 SDK** (ビルド時)。リモートは `REMOTE_DOTNET` 環境変数で指定 (デフォルトはホスト個別)。
- **PostgreSQL 17+** (Citus テストは Citus 拡張入りイメージ `citusdata/citus:latest`)。
- **Linux**: libfuse3 + `attr` パッケージ (xattr テスト用 `getfattr`/`setfattr`、未インストールなら xattr テストは SKIP)。
- **Windows**: Dokan 2.x ドライバ (`DokanSetup_redist.exe`)。
- **Citus 系**: docker daemon (停止状態からでも各スクリプトが `sudo systemctl start docker` で起動し、trap で元に戻す)。
- リモート実行は **SSH 鍵でパスワード無しログイン可能** であること (linux_client / pgsql_server)。

### 0 件で緑にしない (2026-09-21)

**Linux / Windows のどのスイートも、「1 件も走らなかった」「1 件も PASS しなかった」なら `exit 1`** になる。
フィルタのタイポ (`TEST_FILTER` / `-Filter`) や前提不足で**全件 skip のまま exit 0** になると、
**緑で通過したように見えて何も確かめていない**ため。一部だけ skip は従来どおり成功扱い。
詳細は [tests/linux/README.md](../tests/linux/README.md) / [tests/windows/README.md](../tests/windows/README.md) の
各 §0 件で緑にしない。

### 環境変数によるオーバライド (整備)

ホスト名・パス・ポート・認証情報などの **ハードコードはすべて環境変数で上書き可能** にした (テスト構成自体は変えていない)。docker 化やホスト変更時はスクリプトを編集せず環境変数で向け先を変える。優先順位は **CLI 引数 > 環境変数 > 既定**。

| 対象 | 主な環境変数 |
|---|---|
| Linux flow.ps1 / run.cmd | `REMOTE` / `REMOTE_REPO` / `MOUNT_POINT` / `REMOTE_SETTING_FILE` / `REMOTE_DOTNET` / `REMOTE_MOUNT_BINARY` |
| Windows flow.ps1 / run.cmd | `MOUNT_ROOT` / `ASSIGN_BINARY` / `ASSIGN_SETTING_FILE` |
| Linux e2e.sh (fallback テスト) | `PGFS_TEST_PG_EXEC` (psql 呼び出し全文を差し替え) / `TEST_FILTER` |
| Citus `*.sh` | `PGFS_PROBE_IMAGE` / `COORD_NAME` / `WORKER1_NAME` / `COORD_PORT` / `WORKER1_PORT` / `SUPER_USER` / `SUPER_PASSWORD` / `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` / `MKFS_BIN` / `MOUNT_BIN` / `PGFS_TEST_LOG` (詳細は [tests/citus/README.md](../tests/citus/README.md)) |
| Citus verify.cmd | `VERIFY_REMOTE` (既定 pgsql_server) |

各スイートの完全な変数一覧は各ディレクトリ README 参照。

### 残るハードコード前提 (env で吸収しきれないもの)

- **リモートビルド先が symlink 越し**になる linux_client 固有の構成によって `dotnet publish` の並列 restore で race が出る件。`-p:RestoreDisableParallel=true` を flow.ps1 に組み込んで回避済み (docker 化で解消する想定)。
- **Citus verify** の前提となる実機 1 ノード Citus セットアップ自体 (`VERIFY_REMOTE` で接続先は変えられるが、そのホストに Citus がセットアップ済みである必要)。

### ~~既知のフレーク~~ → ✅ 根治済み: `test_concurrent_writes_diff_files` の 40P01

**Citus rf=2 上でのみ**、5 並行の create+write を行う `test_concurrent_writes_diff_files` が
`40P01 distributed deadlock` で **15〜20%/周** 落ちていた (定量化。write-back 実装前後で
同率の pre-existing・単一 PG では出ない)。**原因特定 + 2 段の修正で根治**:

* **原因** (`citus_lock_waits` の 200ms サンプリングで実測特定): rf ≥ 2 の Citus は同一 shard への変更を
  shard 単位で直列化するため、write-through / flush の tx が**冒頭の `UPDATE inode SET data_id`
  (EnsureDataRow) で inode shard を掴んだまま chunk/data shard を待つ** hold-and-wait になり、
  逆順で待つ tx と閉路を作っていた (待ちの最多は「UPDATE inode ← UPDATE data に待たされる」)。
  `{prefix}lock` は無実 (coordinator 1 行ロックに閉路は構成できない — 設計どおり)。
* **修正 1 — shard 接触順の正規化**: data_id リンクを tx 末尾の size/mtime UPDATE に畳み、
  すべての書き込み tx が **chunk/data → inode の順**で shard を触るようにした
  (`FinishWriteInodeInTx` / `UpdateInodeSizeAndMtimeInTx` の link 統合 / `LinkDataIdInTx`)。
  実測で deadlock 発生自体が **33 件/60 周 → 2 件/120 周 (≈99% 減)**。
* **修正 2 — 40P01/40001 の bounded リトライ** (安全網): create / write / flush の自己完結 tx を
  最大 4 回・線形バックオフ + ジッタで再実行 (victim は全 rollback 済みなので安全)。
  2PC の COMMIT 段など Citus 内部の残余閉路もユーザー可視のエラーにしない。
* **検証**: 再現ループ **write-through 120 周 + write-back 60 周 = 0 fail** (修正前の期待値 ≈27 fail)。
  残余 deadlock 2 件はリトライが吸収 (mount ログの Warning で観測可能)。e2e 両モード 43/44 (skip 1 は環境)
  + writeback.sh 8/8 + negcache.sh 7/7 回帰緑。
* **残余の理論閉路** (未観測・リトライが受け皿): `DeleteInode` は inode → chunk/data の順のまま
  (削除は pgfs_lock で同一ファイル直列化済み・並行 create+write との交差は稀)。
  hardlink の `WHERE data_id` multi-shard UPDATE ([next.md #24 ③](next.md)) も同様。
  観測されたらリトライ対象を広げるか同じ順序正規化を適用する。

---

## docker 統合の検討

> 「すべて docker にした方がやりやすいのでは」という動機に対する現状分析。**未着手** ([docs/next.md](next.md) の運用項目に紐づく)。

### 既に docker 化されている部分

Citus 系の DB は既に docker (`citusdata/citus:latest` を `--network host` で coord/worker 起動)。`race_multinode.sh` は **DB を docker + mount をホスト** のハイブリッド。

### Linux 側はフル docker 化できる → **単一 PG 構成は実装済み** ([tests/docker/](../tests/docker/README.md))

`race_multinode.sh` の mount をホストではなくコンテナに移せば、Linux e2e は **PG コンテナ + mount.pgfs コンテナ** で完結する。**単一 PG (非 Citus) 構成を [tests/docker/](../tests/docker/README.md) として実装** (multi-stage SDK ビルド + docker-compose):

- **mount.pgfs コンテナ** ([Dockerfile.mount](../tests/docker/Dockerfile.mount)): `cap_add SYS_ADMIN` / `devices /dev/fuse` / `security_opt apparmor:unconfined` で FUSE をコンテナ内マウント。多段ビルドで `mount.pgfs`/`mkfs.pgfs` を self-contained publish → fuse3 + attr/acl/psql 入り debian-slim に COPY。
- **e2e はコンテナ内で実行**: FUSE マウントをホストに見せる方式 (mount namespace 伝播) は脆いので、`e2e.sh` を mount コンテナ内で走らせる (マウントポイントもコンテナ内)。`tests/linux/` は read-only bind mount で持ち込み (テスト編集時の再ビルド不要)。
- **fallback テスト**: `PGFS_TEST_PG_EXEC="psql -h coord ..."` を [run.sh](../tests/docker/run.sh) が渡し、compose network 越しの psql で DB 直接操作。

これにより単一 PG 構成では **ssh linux_client 依存と symlink race 回避策が不要** になった。**2026-06-02 に linux_client 上で実機検証し 35/35 PASS** (`test_fallback_uname_gname` の DB 直接 INSERT 経路も compose network 越し psql で通過)。残るは多ノード Citus + race + audit のフル docker 化 ([docs/next.md](next.md) #9、雛形は `race_multinode.sh`)。

> **gotcha (runtime base の固定)**: `debian:stable-slim` は現在 Debian 13 (trixie) を指し、libfuse 3.17 が SONAME を `libfuse3.so.4` に bump している。Pgfs.Fuse (内製 binding) は `libfuse3.so.3` を dlopen するため trixie ベースだと `CheckDependencies` が「libfuse 未検出」で落ちる。[Dockerfile.mount](../tests/docker/Dockerfile.mount) は **`debian:bookworm-slim` (Debian 12, libfuse 3.14 = `libfuse3.so.3`) に固定**して回避している。

### Windows 側は docker 化できない

Dokan は **Windows カーネルドライバ** で、Windows コンテナでも FUSE 相当のマウントを提供できない (Dokan のユーザモード API はカーネルドライバ前提)。Windows e2e は **Windows ホスト直** が必須のまま。docker 化の対象外。

### 当面のおすすめ

1. ~~Linux e2e を `tests/docker/` に置く~~ → **単一 PG 構成は実装済み** ([tests/docker/](../tests/docker/README.md))。次は Citus 多ノード + race + audit を同じ枠に拡張 (`race_multinode.sh` が雛形)。
2. `verify` の pgsql_server ハードコードも docker Citus に向けられるよう env オーバライド化 (Linux e2e の `PGFS_TEST_PG_EXEC` と同じ手口)。
3. Windows e2e はホスト前提のまま、docker 化のスコープ外と明記。

---
