# PGFS DDL

DDL files for the PostgreSQL Filesystem (PGFS) schema / table definitions, split **per table**.

The DDL in this directory is **for reference / manual setup**; in normal operation [mkfs.pgfs](../../src/mkfs/) creates the equivalent tables dynamically. For the detailed specification, see [database.md](../database.md).

## File list

| File | Scope | Contents |
|---|---|---|
| [pgfs_database.sql](pgfs_database.sql) | cluster | Creates the role `pgfs`, the tablespace `pgfs`, and the database `pgfs`. Run as a superuser. |
| [pgfs_schema.sql](pgfs_schema.sql) | database | Creates the schema `pgfs`. Connect to the target DB as the PGFS user and run it. |
| [pgfs_inode.sql](pgfs_inode.sql) | table | The inode table + the root inode (`id = 0`) seed + indexes. |
| [pgfs_data.sql](pgfs_data.sql) | table | Reference management for file bodies (`id` BIGSERIAL). |
| [pgfs_data_chunk.sql](pgfs_data_chunk.sql) | table | bytea chunk management for file bodies. PK on `(data_id, chunk_index)`. |
| [pgfs_lock.sql](pgfs_lock.sql) | table | Lock-token table for cross-client mutual exclusion. PK on `target_id` alone ([docs/support_for_citus.md](../support_for_citus.md)). |
| [pgfs_settings.sql](pgfs_settings.sql) | table | Settings store (the `SaveTo=Db` persistence target for `Pgfs.Lib.Config`). Flat `(scope, key)` PK. |
| [pgfs_audit.sql](pgfs_audit.sql) | table | Audit log. Records metadata changes into an `occurred_at` monthly RANGE partition (no DEFAULT; the app ensures the monthly partition). PK on `(occurred_at, id)`. Opt-in via `audit.enabled` (mkfs `--audit`) ([docs/audit-log.md](../audit-log.md)). |

## Run order

As a superuser:

```bash
sudo -u postgres mkdir -p '/var/lib/pgfs'
psql -U postgres -h 127.0.0.1 -p 5432 postgres -f pgfs_database.sql
```

As the PGFS user, connected to the target DB (`pgfs`):

```bash
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_schema.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_inode.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_data_chunk.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_lock.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_settings.sql
psql -U pgfs -h 127.0.0.1 -p 5432 pgfs -f pgfs_audit.sql
```

## Correspondence with the implementation

The column names, types, and index layout in the DDL are kept in sync with [src/mkfs/src/Initializer.cs](../../src/mkfs/src/Initializer.cs).

| DDL file | Corresponding Initializer method |
|---|---|
| `pgfs_inode.sql` | `CreateInodeTableAsync` + `InsertRootInodeAsync` |
| `pgfs_data.sql` | `CreateDataTableAsync` |
| `pgfs_data_chunk.sql` | `CreateDataChunkTableAsync` |
| `pgfs_lock.sql` | `CreateLockTableAsync` |
| `pgfs_settings.sql` | `CreateSettingsTableAsync` + `PopulateSettingsRowsAsync` |
| `pgfs_audit.sql` | `CreateAuditTableAsync` (the monthly partition is created by the app's `Api.EnsureAuditPartition`) |

The schema name / prefix can be changed in mkfs via settings:
- schema name — `--schema` (default `public`)
- table prefix — `--prefix` (default `pgfs_`)

The DDL files hard-code `pgfs.pgfs_*` (schema `pgfs` / prefix `pgfs_`).

## `pgfs.toml` for mounting

If you create the tables by running the DDL above by hand, the two steps that `mkfs.pgfs` performs do not run:

1. **Writing the file-stored settings (`SaveTo=File`) to `pgfs.toml`** — mount.pgfs / assign.pgfs read the connection target, schema, etc. from here.
2. **Inserting the DB-stored settings (`SaveTo=Db`) into `pgfs_settings`** — `pgfs_settings.sql` only creates an empty table.

So in a manual setup you must at least prepare `pgfs.toml` yourself. In particular, **this DDL assumes `pgfs.pgfs_*` (schema `pgfs`)**, so unless you set `database.schema = "pgfs"` explicitly it will look at the default `public` and not match.

Example `pgfs.toml` to place in the directory where you run mount (or one of the `setting.search_path` locations):

```toml
[database]
connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SSL Mode=Prefer"
schema = "pgfs"          # the DDL creates pgfs.pgfs_*, so pgfs, not public
prefix = "pgfs_"
tablespace = "pg_default"
tablespace_path = ""
retry_max_attempts = 5
retry_initial_delay_ms = 200
retry_max_delay_ms = 2000

[mount]
mount_point = "/mnt/pgfs"   # on Windows, "P:" etc.
cache_max_entries = 1024

[logging]
level = "information"
output = "stderr"

[file_system]
cluster_size = 4096
default_chunk_size = 1048576
max_file_size = 1099511627776
```

DB-stored settings are held as rows in `pgfs_settings`, not in `pgfs.toml`. In a manual DDL setup they stay empty, so you can omit them if the defaults are fine; INSERT them if you want to change something (in particular **to enable the audit log**):

| (scope, key) | Default | Note |
|---|---|---|
| `(mount, fallback_uname)` | `nobody` | Fallback when name resolution fails |
| `(mount, fallback_gname)` | `nogroup` | Same |
| `(file_system, version)` | `1.0.0` | Frozen at mkfs time |
| `(file_system, volume_label)` | `pgfs` | Windows drive label |
| `(audit, enabled)` | `false` | Audit log ([../audit-log.md](../audit-log.md)). Set `true` to use it |

Example (enable the audit log; `value` is JSONB, so a bool is `true`/`false`):

```sql
INSERT INTO pgfs.pgfs_settings (scope, key, value, created_by, updated_by)
VALUES ('audit', 'enabled', 'true'::jsonb, 'manual', 'manual')
ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value
;
```

> Manual DDL is for learning / inspection. Normally, using `mkfs.pgfs` sets up everything including steps 1 and 2 above in one go (`--audit` also initializes the audit log).

## Related documents

- [database.md](../database.md) — the detailed DB schema design specification
- [Mkfs.md](../Mkfs.md) — mkfs CLI behavior and the DDL application flow
