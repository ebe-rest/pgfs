# 次にやること

次に何を着手するかの一覧。作業が完了するたびに更新してコミットに含めること。

**重複を許可するルール (このファイルだけ)**: 他ドキュメントの内容を要約して再掲してもよい。リンクで済むなら短く、判断材料が必要なら詳しく書く。

英語版は [next.md](next.md) を参照してください。

---

## 🟢 機能追加 (要件にあるが未実装)

| # | 項目 | 内容 | 規模感 |
|---|---|---|---|
| 1 | **ACL / 権限の Linux↔Windows 相互運用** (主目的達成、残項目あり) | 設計の正は [docs/permission-interop.ja.md](permission-interop.ja.md) / 図は [permission-interop-diagram.html](permission-interop-diagram.html)。PGFS は名前ベース ACL ストアで、POSIX mode が正準・Windows は投影ビュー。名前ハンドリング (正規化 [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs) / ドメイン除去 / principal マッピング / `user.win.attrs` JSON / ReadOnly を st_mode で表現) と ACL 本体 (正準モデル [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs)、Windows `GetFileSecurity`/`SetFileSecurity` 投影、Linux POSIX ACL [PosixAcl](../src/lib/src/Models/PosixAcl.cs) `system.posix_acl_access` ⇄ mode + `user.pgfs_acl`) は実装 + 回帰済み。**残**: named ACL の厳密 enforce (要件が出てから)、`system.posix_acl_default` の Windows 継承変換、cross-OS 往復の自動テスト | 中 |
| 2 | **Junction** (Assign 側) | `pgfs_inode.is_junction` 列は既にあり、Linux 側は symlink で代替。Windows 側で junction 作成 / 解決 / 削除を IDokanOperations に実装 | 中 |
| 3 | **ADS (Alternate Data Streams)** (Windows) | NTFS 互換の `:streamname` を Dokan 経由で。データモデル拡張必要 | 大 |

## 🟡 運用・検証

テスト全体の一覧 / 環境要件 / docker 統合の現状分析は [docs/tests.ja.md](tests.ja.md) を参照。

| # | 項目 | 内容 |
|---|---|---|
| 4 | **テストの docker 統合** (単一 PG 完了 / 多ノードは残) | 単一 PG の Linux e2e フル docker 化は完了 ([tests/docker/](../tests/docker/README.md))。残るは多ノード Citus + race + audit を同じ枠に拡張 (`race_multinode.sh` が雛形)。Windows e2e は Dokan がカーネルドライバなので対象外。分析は [docs/tests.ja.md §docker 統合の検討](tests.ja.md#docker-統合の検討) |
| 5 | **多ノード Citus 上の Windows e2e** | クロスクライアント排他制御の残検証 ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) では Linux のみ走らせた。Windows e2e を docker Citus 経由で走らせる枠は未作成 |
| 6 | **Linux flow.cmd の harness 出力問題** | PowerShell tool の `Write-Host` が長時間 run で背景化されると stdout に届かない件。flow.ps1 を `Write-Output` 主体に書き換えるか、`*-Information` 経路を使うと harness で見えるようになる |

## 🔵 コード品質・規約

| # | 項目 | 内容 | 規模感 |
|---|---|---|---|
| 7 | **コーディング規約 v4 への追従** | `else` 句 ~20 + 三項演算子 ~20、計約 40 箇所。書き換えパターンは `FirstList.cs` (switch 式・タプル分解・Try* 抽出) と [Api.cs](../src/lib/src/Api/Api.cs) (Logger ガードのブレース化) 参照。詳細は [coding-style.ja.md](coding-style.ja.md) | 各ファイル単位で小 |

## 🟣 性能改善 — [docs/performance.ja.md](performance.ja.md)

| # | 項目 | 内容 |
|---|---|---|
| 8 | **path traversal cross-shard hop 計測** | Citus 多ノードで `parent_id` 分散による traversal コスト。InodeCache ヒット率含めて実測 |
| 9 | **bytea partial read 実測** | PG 13+ の partial TOAST detoast が想定通り効くか。chunk_size の引き上げ余地 |
| 10 | **FUSE `writeback_cache` 有効化** | write-through を write-back に。小さい逐次 write の PG ラウンドトリップ削減。`attr_timeout=0`/notify 一貫性/クラッシュ時の喪失範囲を要検証。[performance.ja.md](performance.ja.md) #8 |

## ⚪ 将来検討 (設計記録レベル、優先度低)

| # | 項目 | 内容 |
|---|---|---|
| 11 | **`pgfs_inode_lock` 分離** | 排他制御は単一 `pgfs_lock` + 符号 namespace で開始。inode lock が支配的 workload で非対称コスト ([support_for_citus.ja.md §排他制御](support_for_citus.ja.md)) が見えてきたら別テーブル化 |
| 12 | **multi-coordinator HA Citus 検討** | Enterprise Citus を視野に入れるか |
| 13 | **`pgfs_inode` を id 分散にする案** | `(parent_id, name)` UK を application 層で保証する代替案。cross-shard rename コストが問題になったら再検討 |

---

## 既に入っているもの (本リストに関連するもののみ)

本リストの前提・周辺になっている完了項目だけ残す。背景の設計判断は [history.ja.md](history.ja.md) を参照。

- **Mount オプション `-o`**: `-o key=val,flag,...` を [ConfigLoader.ParseDashOOptions](../src/lib/src/Config/ConfigLoader.cs) で分類 (FUSE passthrough / 受理して無視 + `x-` 接頭辞 / pgfs 設定 / 未知=Warning / 非対応マウント操作=明示 Warning)。詳細は [Mount.ja.md §マウントオプション](Mount.ja.md)。Assign 側 `-o` は将来検討。
- **xattr バイト列透過**: `pgfs_inode.xattrs JSONB`+Base64 を `xattr_names TEXT[]` + `xattr_values BYTEA[]` 並行配列に置換し、値を bytea で忠実保持 (NUL/高位バイトも無加工往復、符号化判定ヒューリスティック廃止)。詳細は [xattr-bytea.ja.md](xattr-bytea.ja.md)。**→ 機能追加 #1 (ACL) の前提**
- **df 対応 (statfs 実空き容量)**: `df` がテーブルスペースの実ディスク空きを返す。plperlu の素 `CREATE FUNCTION` + `SECURITY DEFINER` (mkfs 純 SQL、Citus は `run_command_on_all_nodes`/`_workers`)、3 段フォールバック、mkfs `--statfs <auto\|require\|nominal>` (`app.statfs`, `SaveTo=Db`)。Citus 多 worker 集約 + 専用テスト ([statfs.sh](../tests/citus/statfs.sh)) 検証済み。正は [df-support.ja.md](df-support.ja.md)。
- **設定スコープ再編 + plperlu ゲート + Citus カスタム tablespace**: 新スコープ `app` + `app.plperlu` (plperlu 上位ゲート)、statfs モードを `app.statfs` に、`database.citus`/`file_system` サイズ系を DB 権威化 (生成 toml 非出力)、Citus×カスタム tablespace 禁止ガード撤廃 + plperlu auto-mkdir。正は [settings-and-plperlu.ja.md](settings-and-plperlu.ja.md)、横串は [settings-matrix.ja.md](settings-matrix.ja.md)。
- **ヘルプ自動生成**: 手書き `ShowHelp` を廃止し、[HelpText.Build](../src/lib/src/Config/HelpText.cs) が [Schema.AllFields](../src/lib/src/Config/Schema.cs) を walk して `--help` を組み立てる。ツール別の出し分けは [Field.AppliesTo](../src/lib/src/Config/Field.cs)。
- **`Field` self-check**: [ConfigLoader.Resolve](../src/lib/src/Config/ConfigLoader.cs) が触れた Field を記録し、[ConfigLoader.UnresolvedFields](../src/lib/src/Config/ConfigLoader.cs) が `Schema.AllFields` との差分 (= 配線忘れ) を起動時 Warning に積む。
- **CLI 引数の二重 parse 解消**: mount/assign の `BuildRootConfig` が lite+full の 2 Loader で CLI/TOML を二度パースしていたのを、同一 Loader 使い回し + [ConfigLoader.WithStore](../src/lib/src/Config/ConfigLoader.cs) による Phase 3 後付けに統一 (CLI/TOML パースは 1 回)。
- **テストの docker 統合 (単一 PG)**: [tests/docker/](../tests/docker/README.md) で `coord` (postgres) + `mount` (FUSE マウント) の 2 コンテナにより Linux e2e をコンテナ内実行。ssh/ホスト dotnet 非依存。**→ 運用・検証 #4 の前提**
- **UserResolver キャッシュ**: 両 OS の解決器が双方向 `ConcurrentDictionary` + `GetOrAdd` でキャッシュ済み。`getpwnam`/`getgrnam`/`getpwuid`/`getgrgid` は (key,結果) ごとに 1 回。詳細は [performance.ja.md](performance.ja.md) #1。
- **Linux↔Windows 権限/ACL 相互運用**: PGFS は名前ベース ACL ストアで、POSIX mode が正準・Windows は投影ビュー。名前ハンドリングと ACL 本体経路を実装 + 回帰済み。詳細は [permission-interop.ja.md](permission-interop.ja.md)。**→ 機能追加 #1 の前提**
- **Citus 対応** (分散 + クロスクライアント排他制御): Large Object → bytea 化、`mkfs --citus [--worker ...]`、`pgfs_lock` + `SELECT FOR UPDATE` 排他制御、多ノード Citus 上で Linux e2e + 並行 write/mkdir race + lock 累積妥当。詳細は [support_for_citus.ja.md](support_for_citus.ja.md)、検証は [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)。**→ 性能改善 #8/#9 の前提**
- **設定モデル**: 旧 `*Settings.cs` ツリーを削除、`Pgfs.Lib.Config` (`Field<T>` + `ConfigLoader` + `ConfigStore`) に統一。詳細は [architecture.ja.md §Config](architecture.ja.md)。
- **監査ログ**: chmod / chown / 削除 / リネーム / 作成 / ハードリンクを専用テーブル `{prefix}audit` (`occurred_at` 月次 RANGE パーティション) に 1 操作 = 1 行で記録、操作と同一 tx。on/off は `audit.enabled` (mkfs `--audit` で初期化)。詳細は [audit-log.ja.md](audit-log.ja.md)。
- **Notify (LISTEN/NOTIFY)**: cross-client 変更通知 ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs))、`database.notify_enabled` opt-in。**→ 多 client 運用の前提**
- **接続 retry**: [Retry.cs](../src/lib/src/Utility/Retry.cs) で transient 失敗 (PG 再起動 / ネット瞬断) を指数バックオフ再試行。**→ 運用上の前提**
- **uname/gname fallback**: mount.fallback_uname / fallback_gname、既定 nobody/nogroup。
- **/etc/fstab 対応**: 位置引数 / `-o` パーサ / 自動デーモン化 / `sudo mount -t pgfs` 経由動作確認済み + **起動時マウント (fstab 行 + 再起動) も実機検証済み**。詳細は [fstab-support.ja.md](fstab-support.ja.md)。
- **ログ出力先**: `logging.output = "daily:~/pgfs/log/pgfs-*.log"` とローテーションを [LogSink](../src/lib/src/Logging/LogSink.cs) + [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs) で `Logger.Output` に配線。
- **`ConfigLoader.Warnings`**: 未知オプション (`--cutus` 等のタイポ) を `Warnings` に積んで起動時にログ。併せて解釈済みパラメータの `param: scope.key=value` ダンプ (Password マスク) と Warning 以上の stderr terse 出力。
- **Tmds.Fuse フォーク**: `attr_timeout=0` / `use_ino` / `allow_other` を `securefolderfs-community/Tmds.Fuse` フォークで実現。詳細は [fstab-support.ja.md](fstab-support.ja.md)。
