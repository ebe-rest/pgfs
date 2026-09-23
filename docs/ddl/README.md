# The PGFS DDL

> **Route**: [docs/README.md](../README.md) › [design/database.md](../design/database.md) › **this document**
>
> **What this document is the source of truth for**: **the DDL itself, per table** (the list of `pgfs_*.sql`
> and what each file creates). **It does not describe what the columns mean or why they are designed that
> way** - database.md owns that.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../design/database.md](../design/database.md) | **The schema design** (what the columns mean, the timestamp convention, the occupied bytes) |
> | [../Mkfs.md](../Mkfs.md) | The side that **applies** the DDL (how the mkfs CLI behaves) |
> | [../design/support_for_citus.md](../design/support_for_citus.md) | Turning them into distributed tables (which ones `create_distributed_table` takes, and the distribution keys) |

The schema and table definitions of the PostgreSQL Filesystem (PGFS), split into DDL files **per table**.

The DDL here is **for reference and for building by hand**; in normal operation
[mkfs.pgfs](../../src/mkfs/) creates the equivalent tables dynamically. For the detailed specification see
[database.md](../design/database.md).

## The files

| File | Scope | Contents |
|---|---|---|
| [pgfs_database.sql](pgfs_database.sql) | The cluster | Creates the role `pgfs`, the tablespace `pgfs` and the database `pgfs`. Run as a superuser. |
| [pgfs_schema.sql](pgfs_schema.sql) | The database | Creates the schema `pgfs`. Run as the PGFS user, connected to the target database. |
| [pgfs_inode.sql](pgfs_inode.sql) | A table | The inode table, the insert of the root inode (`id = 0`) and the indexes. |
| [pgfs_data.sql](pgfs_data.sql) | A table | The reference-management table of the file bodies (`id` BIGSERIAL). |
| [pgfs_data_chunk.sql](pgfs_data_chunk.sql) | A table | The bytea chunk management of the file bodies. The PK is `(data_id, chunk_index)`. It moved from a large object to bytea for Citus ([docs/support_for_citus.md](../design/support_for_citus.md)). |
| [pgfs_lock.sql](pgfs_lock.sql) | A table | The lock-token table for cross-client exclusion. The PK is `target_id` alone. It was created ahead of time for Citus and is now wired to the Api's row locks ([docs/support_for_citus.md](../design/support_for_citus.md)). |
| [pgfs_settings.sql](pgfs_settings.sql) | A table | The settings store (where `Pgfs.Core.Config`'s `SaveTo=Db` persists). A flat form with `(scope, key)` as the PK. |
| [pgfs_audit.sql](pgfs_audit.sql) | A table | The audit log. It records metadata changes into monthly `occurred_at` RANGE partitions (with no DEFAULT; the application ensures the month partition). The PK is `(occurred_at, id)`. Opt-in through `audit.enabled` (mkfs `--audit`) ([docs/audit-log.md](../design/audit-log.md)). |
| [pgfs_mounts.sql](pgfs_mounts.sql) | A table | The registry of running mounts (volatile). mount/assign INSERTs at start-up, heartbeats periodically and DELETEs on exit. **Only an unmount that left a loss behind is not DELETEd; it stays as a gravestone** (`stats.unflushedLoss`, B-2). The PK is `mount_id` alone ([docs/design/runtime-control-plane.md](../design/runtime-control-plane.md)). |

## The order to run them in

As a superuser:

```bash
sudo -u postgres mkdir -p '/var/lib/pgfs'
psql -U postgres -h 127.0.0.1 -p 5432 postgres -f pgfs_database.sql
```

Then, as the PGFS user connected to the target database (`pgfs`):

```bash
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_schema.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_inode.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data_chunk.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_lock.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_settings.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_audit.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_mounts.sql
```

## How they map onto the current implementation

The column names, types and index layout in the DDL are kept in step with
[src/mkfs/src/Initializer.cs](../../src/mkfs/src/Initializer.cs).

| DDL file | The matching Initializer method |
|---|---|
| `pgfs_inode.sql` | `CreateInodeTableAsync` + `InsertRootInodeAsync` |
| `pgfs_data.sql` | `CreateDataTableAsync` |
| `pgfs_data_chunk.sql` | `CreateDataChunkTableAsync` |
| `pgfs_lock.sql` | `CreateLockTableAsync` |
| `pgfs_settings.sql` | `CreateSettingsTableAsync` + `PopulateSettingsRowsAsync` |
| `pgfs_audit.sql` | `CreateAuditTableAsync` (the month partitions are created by the application's `Api.EnsureAuditPartition`) |
| `pgfs_mounts.sql` | `CreateMountsTableAsync` (register/heartbeat/deregister are `Api.TryRegisterMount` / `Dispose`) |

The schema name and the prefix are configurable in mkfs:
- The schema name - `--schema` (default `public`)
- The table prefix - `--prefix` (default `pgfs_`)

The DDL files hard-code `pgfs.pgfs_*` (the schema `pgfs` with the prefix `pgfs_`).

## The `pgfs.toml` for mounting

When the DDL above is applied by hand, two of the steps `mkfs.pgfs` performs do not happen:

1. **Writing the file-stored settings (`SaveTo=File`) out into `pgfs.toml`** - mount.pgfs / assign.pgfs read the connection, the schema and so on from there.
2. **Inserting the DB-stored settings (`SaveTo=Db`) into `pgfs_settings`** - `pgfs_settings.sql` only creates an empty table.

So a hand-built configuration needs at least a `pgfs.toml` of your own. In particular, **this DDL assumes
`pgfs.pgfs_*` (the schema `pgfs`)**, so without stating `database.schema = "pgfs"` it looks at the default
`public` and nothing lines up.

An example `pgfs.toml`, placed in the directory the mount runs from (or on one of the `setting.search_path` entries):

```toml
[database]
connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SSL Mode=Prefer"
schema = "pgfs"          # pgfs rather than public, because the DDL creates pgfs.pgfs_*
prefix = "pgfs_"
retry_max_attempts = 5
retry_initial_delay_ms = 200
retry_max_delay_ms = 2000

[mount]
mount_point = "/mnt/pgfs"   # on Windows, "P:" and the like
cache_max_entries = 1024

[logging]
level = "information"
output = "stderr"

```

The DB-stored settings live as rows in `pgfs_settings` rather than in `pgfs.toml`. With the hand-applied DDL
that table stays empty, so they can be omitted if the defaults are fine; INSERT them if you want to change
something (in particular **to enable the audit log**):

| (scope, key) | Default | Note |
|---|---|---|
| `(mount, fallback_uname)` | `nobody` | The fallback when name resolution fails |
| `(mount, fallback_gname)` | `nogroup` | The same |
| `(file_system, version)` | `1.0.0` | Frozen at mkfs time |
| `(file_system, volume_label)` | `pgfs` | The Windows drive name |
| `(file_system, cluster_size)` | `4096` | The block size shared by the FS |
| `(file_system, default_chunk_size)` | `1048576` | The chunk size of new data |
| `(file_system, max_file_size)` | `1099511627776` | Used for the nominal capacity and so on |
| `(database, tablespace)` / `(database, tablespace_path)` | `pg_default` / an empty string | The mkfs database-build settings. Separate from the mount settings in the TOML |
| `(audit, enabled)` | `false` | The audit log ([../design/audit-log.md](../design/audit-log.md)). `true` to use it |

An example (enabling the audit log; `value` is JSONB, so a bool is `true`/`false`):

```sql
INSERT INTO pgfs.pgfs_settings (scope, key, value, created_by, updated_by)
VALUES ('audit', 'enabled', 'true'::jsonb, 'manual', 'manual')
ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value
;
```

> The hand-applied DDL is for learning and for checking. Normally `mkfs.pgfs` sets all of this up in one go,
> steps 1 and 2 included (`--audit` initializes the audit log as well).
