# 次にやること

次に何を着手するかの一覧。作業が完了するたびに更新してコミットに含めること。

**重複を許可するルール (このファイルだけ)**: 他ドキュメントの内容を要約して再掲してもよい。リンクで済むなら短く、判断材料が必要なら詳しく書く。

---

## 🟢 機能追加 (要件にあるが未実装)

| # | 項目 | 内容 | 規模感 |
|---|---|---|---|
| 1 | **Mount オプション `-o`** | mount.pgfs の `-o key=val,flag,...` を Linux 側でフル対応 (Assign 側にも検討余地)。コアパーサは [ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs) に既にあるので、残るは「未知 key の警告整流」と「`-o ro` 等の典型 mount option 互換マップ」 | 小〜中 |
| 2 | **xattr バイト列透過** | 現状は JSONB 内で Base64 経由 ([Api.cs](../src/lib/src/Api/Api.cs) `EncodeXattrValue`/`DecodeXattrValue`)。SELinux 等の任意バイナリ xattr が来たときの分岐 + テストケース追加。ACL を xattr で配送する経路 (POSIX ACL を `system.posix_acl_access` xattr で配送 — [permission-interop.ja.md](permission-interop.ja.md) 参照) の前提でもある | 中 (Schema 改修あり) |
| 3 | **ACL / 権限の Linux↔Windows 相互運用** | 設計の正は [docs/permission-interop.ja.md](permission-interop.ja.md) / 図は [permission-interop-diagram.html](permission-interop-diagram.html)。PGFS は名前ベース ACL ストアで、POSIX mode が正準・Windows は投影ビュー。名前ハンドリングの即時項目は実装 + 回帰済み (名前正規化 [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs) / ドメイン除去 / principal マッピング / `user.win.attrs` JSON / ReadOnly を st_mode で表現)。ACL 本体も実装済み: 正準モデル ([PgfsAcl](../src/lib/src/Models/PgfsAcl.cs))、Windows `GetFileSecurity` 読み投影、`SetFileSecurity` 逆投影 + owner=group ルーティング、Linux POSIX ACL 経路 ([PosixAcl](../src/lib/src/Models/PosixAcl.cs)、`system.posix_acl_access` ⇄ mode + `user.pgfs_acl`)。回帰: Windows 26/26・Linux 35/35・race 4/4。保留: named ACL の厳密 enforce (要件が出てから保留)、`system.posix_acl_default` の Windows 継承変換、cross-OS 往復の自動テスト | 中 |
| 4 | **Junction** (Assign 側) | `pgfs_inode.is_junction` 列は既にあり、Linux 側は symlink で代替。Windows 側で junction 作成 / 解決 / 削除を IDokanOperations に実装 | 中 |
| 5 | **ADS (Alternate Data Streams)** (Windows) | NTFS 互換の `:streamname` を Dokan 経由で。データモデル拡張必要 | 大 |

## 🟡 運用・検証

テスト全体の一覧 / 環境要件 / docker 統合の現状分析は [docs/tests.ja.md](tests.ja.md) を参照。

| # | 項目 | 内容 |
|---|---|---|
| 6 | **/etc/fstab 起動時マウント** 手動検証 | `/etc/fstab` 行 + 再起動 / `sudo mount -a` で自動マウントすることを実機確認 (コア実装は済み、詳細は [fstab-support.ja.md](fstab-support.ja.md)) |
| 7 | **テストの docker 統合** | ホスト依存 (ssh linux_client / pgsql_server) を docker に寄せて再現性を上げる。Linux e2e + Citus 系はフル docker 化可能 (`race_multinode.sh` が雛形)、Windows e2e は Dokan がカーネルドライバなので対象外。分析は [docs/tests.ja.md §docker 統合の検討](tests.ja.md#docker-統合の検討) |
| 8 | **多ノード Citus 上の Windows e2e** | クロスクライアント排他制御の残検証 ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) では Linux 35/35 のみ走らせた。Windows 26/26 を docker Citus 経由で走らせる枠は未作成 |
| 9 | **Linux flow.cmd の harness 出力問題** | PowerShell tool の `Write-Host` が長時間 run で背景化されると stdout に届かない件。flow.ps1 を `Write-Output` 主体に書き換えるか、`*-Information` 経路を使うと harness で見えるようになる |

## 🔵 コード品質・規約

| # | 項目 | 内容 | 規模感 |
|---|---|---|---|
| 10 | **ヘルプ自動生成** | `Schema.AllFields` の `CliOptions` / `Comment` を walk して `--help` テキストを組み立てる。手書きヘルプ ([src/mount/src/Program.cs](../src/mount/src/Program.cs) `ShowHelp`) を廃止 | 小 |
| 11 | **`Field` self-check** | 起動時に `Schema.*` 宣言と `ConfigLoader.Resolve` 参照の差分を warning ログに出す (reflection で `Schema` を走査して比較) | 小 |
| 12 | **CLI 引数の二重 parse** | BuildRootConfig が CLI を 2 度 parse している (lite + full)。`ConfigLoader.WithStore(store)` のような fluent API で 1 度に統合できる余地あり | 中 |

## 🟣 性能改善 — [docs/performance.ja.md](performance.ja.md)

| # | 項目 | 内容 |
|---|---|---|
| 13 | **UserResolver キャッシュ** | uid/gid ↔ uname/gname の解決を `getpwnam` ごとに呼んでいる箇所のキャッシュ |
| 14 | **path traversal cross-shard hop 計測** | Citus 多ノードで `parent_id` 分散による traversal コスト。InodeCache ヒット率含めて実測 |
| 15 | **bytea partial read 実測** | PG 13+ の partial TOAST detoast が想定通り効くか。chunk_size の引き上げ余地 |

## ⚪ 将来検討 (設計記録レベル、優先度低)

| # | 項目 | 内容 |
|---|---|---|
| 16 | **`pgfs_inode_lock` 分離** | 排他制御は単一 `pgfs_lock` + 符号 namespace で開始。inode lock が支配的 workload で非対称コスト ([support_for_citus.ja.md §排他制御](support_for_citus.ja.md)) が見えてきたら別テーブル化 |
| 17 | **multi-coordinator HA Citus 検討** | Enterprise Citus を視野に入れるか |
| 18 | **`pgfs_inode` を id 分散にする案** | `(parent_id, name)` UK を application 層で保証する代替案。cross-shard rename コストが問題になったら再検討 |

---

## 既に入っているもの (本リストに関連するもののみ)

本リストの前提・周辺になっている完了項目だけ残す。背景の設計判断は [history.ja.md](history.ja.md) を参照。

- **Linux↔Windows 権限/ACL 相互運用**: PGFS は名前ベース ACL ストアで、POSIX mode が正準・Windows は投影ビュー。名前ハンドリング (正規化 / ドメイン除去 / principal マッピング / `user.win.attrs` / ReadOnly を st_mode で表現) に加え、ACL 本体経路 (正準 `PgfsAcl`、Windows `GetFileSecurity`/`SetFileSecurity` 投影、Linux POSIX ACL を `system.posix_acl_access` 経由) を実装 + 回帰済み (Windows 26/26・Linux 35/35・race 4/4)。詳細は [permission-interop.ja.md](permission-interop.ja.md)。**→ 機能追加 #2 / #3 の前提**
- **Citus 対応** (分散 + クロスクライアント排他制御): Large Object → bytea 化、`mkfs --citus [--worker ...]`、`pgfs_lock` + `SELECT FOR UPDATE` 排他制御、多ノード Citus 上で Linux e2e 35/35 + 並行 write/mkdir race + lock 累積妥当。詳細は [support_for_citus.ja.md](support_for_citus.ja.md)、検証は [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)。**→ 性能改善 #14 / #15 の前提**
- **設定モデル**: 旧 `*Settings.cs` ツリーを削除、`Pgfs.Lib.Config` (`Field<T>` + `ConfigLoader` + `ConfigStore`) に統一。詳細は [architecture.ja.md §Config](architecture.ja.md)。**→ コード品質 #10/#11/#12 の前提**
- **監査ログ**: chmod / chown / 削除 / リネーム / 作成 / ハードリンクを専用テーブル `{prefix}audit` (`occurred_at` 月次 RANGE パーティション) に 1 操作 = 1 行で記録、操作と同一 tx。on/off は `audit.enabled` (mkfs `--audit` で初期化)。詳細は [audit-log.ja.md](audit-log.ja.md)。
- **Notify (LISTEN/NOTIFY)**: cross-client 変更通知 ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs))、`database.notify_enabled` opt-in。**→ 多 client 運用の前提**
- **接続 retry**: [Retry.cs](../src/lib/src/Utility/Retry.cs) で transient 失敗 (PG 再起動 / ネット瞬断) を指数バックオフ再試行。**→ 運用上の前提**
- **uname/gname fallback**: mount.fallback_uname / fallback_gname、既定 nobody/nogroup。
- **/etc/fstab コア対応**: 位置引数 / `-o` パーサ / 自動デーモン化 / `sudo mount -t pgfs` 経由動作確認済み。**→ 運用 #6 の前提** (残るは起動時マウントの実機確認のみ)。
- **ログ出力先**: `logging.output = "daily:~/pgfs/log/pgfs-*.log"` とローテーションを [LogSink](../src/lib/src/Logging/LogSink.cs) + [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs) で `Logger.Output` に配線。
- **`ConfigLoader.Warnings`**: 未知オプション (`--cutus` 等のタイポ) を `Warnings` に積んで起動時にログ。併せて解釈済みパラメータの `param: scope.key=value` ダンプ (Password マスク) と Warning 以上の stderr terse 出力。
- **Tmds.Fuse フォーク**: `attr_timeout=0` / `use_ino` / `allow_other` を `securefolderfs-community/Tmds.Fuse` フォークで実現。詳細は [fstab-support.ja.md](fstab-support.ja.md)。
