# PGFS DDL

PostgreSQL Filesystem (PGFS) のスキーマ / テーブル定義を **テーブル単位**に分割した DDL ファイル群です。

このディレクトリの DDL は **参照用 / 手動構築用**で、通常運用では [mkfs.pgfs](../../src/mkfs/) が同等のテーブルを動的に作成します。詳細な仕様は [database.ja.md](../database.ja.md) を参照。

## ファイル一覧

| ファイル | スコープ | 内容 |
|---|---|---|
| [pgfs_database.sql](pgfs_database.sql) | クラスタ | ロール `pgfs`、テーブルスペース `pgfs`、データベース `pgfs` を作成。スーパーユーザーで実行。 |
| [pgfs_schema.sql](pgfs_schema.sql) | データベース | スキーマ `pgfs` を作成。PGFS ユーザーで対象 DB に接続して実行。 |
| [pgfs_inode.sql](pgfs_inode.sql) | テーブル | inode テーブル + ルート inode (`id = 0`) の投入 + インデックス。 |
| [pgfs_data.sql](pgfs_data.sql) | テーブル | データ本体の参照管理テーブル (`id` BIGSERIAL)。 |
| [pgfs_data_chunk.sql](pgfs_data_chunk.sql) | テーブル | データ本体の bytea チャンク管理。`(data_id, chunk_index)` で PK。 |
| [pgfs_lock.sql](pgfs_lock.sql) | テーブル | cross-client 排他制御用 lock token テーブル。`target_id` 単独 PK ([docs/support_for_citus.ja.md](../support_for_citus.ja.md))。 |
| [pgfs_settings.sql](pgfs_settings.sql) | テーブル | 設定値ストア (`Pgfs.Lib.Config` の `SaveTo=Db` 永続化先)。`(scope, key)` で PK のフラット形。 |
| [pgfs_audit.sql](pgfs_audit.sql) | テーブル | 監査ログ。メタデータ変更を `occurred_at` 月次 RANGE パーティション (DEFAULT 無し、月パーティションはアプリが ensure) に記録。`(occurred_at, id)` で PK。`audit.enabled` (mkfs `--audit`) で opt-in ([docs/audit-log.ja.md](../audit-log.ja.md))。 |

## 実行順序

スーパーユーザーで:

```bash
sudo -u postgres mkdir -p '/var/lib/pgfs'
psql -U postgres -h 127.0.0.1 -p 5432 postgres -f pgfs_database.sql
```

PGFS ユーザーで対象 DB (`pgfs`) に接続して:

```bash
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_schema.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_inode.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data_chunk.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_lock.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_settings.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_audit.sql
```

## 現行実装との対応

DDL 内のカラム名・型・インデックス構成は [src/mkfs/src/Initializer.cs](../../src/mkfs/src/Initializer.cs) と同期されています。

| DDL ファイル | 対応する Initializer メソッド |
|---|---|
| `pgfs_inode.sql` | `CreateInodeTableAsync` + `InsertRootInodeAsync` |
| `pgfs_data.sql` | `CreateDataTableAsync` |
| `pgfs_data_chunk.sql` | `CreateDataChunkTableAsync` |
| `pgfs_lock.sql` | `CreateLockTableAsync` |
| `pgfs_settings.sql` | `CreateSettingsTableAsync` + `PopulateSettingsRowsAsync` |
| `pgfs_audit.sql` | `CreateAuditTableAsync` (月パーティションはアプリの `Api.EnsureAuditPartition` が作る) |

スキーマ名 / プレフィックスは mkfs では設定で変えられる:
- スキーマ名 — `--schema` (既定 `public`)
- テーブルプレフィックス — `--prefix` (既定 `pgfs_`)

DDL ファイルでは `pgfs.pgfs_*` (スキーマ `pgfs` / プレフィックス `pgfs_`) でハードコードしている。

## マウント用 `pgfs.toml`

上の DDL を手動で流して作った場合、`mkfs.pgfs` が行う 2 つの工程が走らない:

1. **ファイル保管の設定 (`SaveTo=File`) を `pgfs.toml` に書き出す** — mount.pgfs / assign.pgfs は接続先・スキーマ等をここから読む。
2. **DB 保管の設定 (`SaveTo=Db`) を `pgfs_settings` に投入する** — `pgfs_settings.sql` は空テーブルを作るだけ。

なので手動構成では、少なくとも `pgfs.toml` を自分で用意する必要がある。特に **この DDL は `pgfs.pgfs_*` (スキーマ `pgfs`) を前提**にしているので、`database.schema = "pgfs"` を明示しないと既定の `public` を見に行って噛み合わない。

mount を実行するディレクトリ (または `setting.search_path` のいずれか) に置く `pgfs.toml` の例:

```toml
[database]
connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SSL Mode=Prefer"
schema = "pgfs"          # DDL が pgfs.pgfs_* で作るため public ではなく pgfs
prefix = "pgfs_"
tablespace = "pg_default"
tablespace_path = ""
retry_max_attempts = 5
retry_initial_delay_ms = 200
retry_max_delay_ms = 2000

[mount]
mount_point = "/mnt/pgfs"   # Windows は "P:" 等
cache_max_entries = 1024

[logging]
level = "information"
output = "stderr"

[file_system]
cluster_size = 4096
default_chunk_size = 1048576
max_file_size = 1099511627776
```

DB 保管の設定は `pgfs.toml` ではなく `pgfs_settings` の行で持つ。手動 DDL では空のままなので、既定で良ければ省略可、変えたい (特に **監査ログを有効化したい**) なら INSERT する:

| (scope, key) | 既定 | 備考 |
|---|---|---|
| `(mount, fallback_uname)` | `nobody` | 名前解決失敗時の逃げ先 |
| `(mount, fallback_gname)` | `nogroup` | 同上 |
| `(file_system, version)` | `1.0.0` | mkfs 時凍結用 |
| `(file_system, volume_label)` | `pgfs` | Windows ドライブ名 |
| `(audit, enabled)` | `false` | 監査ログ ([../audit-log.ja.md](../audit-log.ja.md))。使うなら `true` |

例 (監査ログを有効化。`value` は JSONB なので bool は `true`/`false`):

```sql
INSERT INTO pgfs.pgfs_settings (scope, key, value, created_by, updated_by)
VALUES ('audit', 'enabled', 'true'::jsonb, 'manual', 'manual')
ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value
;
```

> 手動 DDL は学習・確認用。通常は `mkfs.pgfs` を使えば上記 1・2 も含めて一括で整う (`--audit` で監査ログも初期化される)。

## 関連ドキュメント

- [database.ja.md](../database.ja.md) — DB スキーマ設計の詳細仕様
- [Mkfs.ja.md](../Mkfs.ja.md) — mkfs CLI の挙動と DDL 適用フロー
