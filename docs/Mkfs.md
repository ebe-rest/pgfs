# mkfs.pgfs specification

Specification for `mkfs.pgfs`, the tool that initializes a PGFS filesystem on a PostgreSQL database.

This document describes the specification as implemented in [src/mkfs/](../src/mkfs/) (built on `Pgfs.Lib.Config`). Provisional and unsupported items are listed under "Provisional implementation".

A Japanese translation is available in [Mkfs.ja.md](Mkfs.ja.md).

## Role

It idempotently creates everything PGFS needs in a PostgreSQL database:

1. The PGFS user.
2. The tablespace (only when specified).
3. The database.
4. The schema.
5. Tables: `{prefix}inode`, `{prefix}data`, `{prefix}data_chunk`, `{prefix}settings`.
6. The root directory inode (`id = 0`).
7. Persistence of `SaveTo=DB` settings (UPSERT into `{prefix}settings`).
8. The settings file `pgfs.toml` (writing out `SaveTo=File` settings).

**Every step is idempotent**: anything that already exists has its `CREATE` skipped. Re-running is non-destructive.

## Execution

The entry point is [src/mkfs/src/Program.cs](../src/mkfs/src/Program.cs).

```pwsh
# Development build (bin/Debug/mkfs.pgfs.{dll,exe})
dotnet build src/mkfs/Mkfs.csproj
# Run during development
dotnet run --project src/mkfs -- [options]
# Or the built executable
./bin/Debug/mkfs.pgfs [options]

# Self-contained publish (bin/Publish/mkfs.pgfs[.exe] — single-file, host RID auto-detected)
dotnet publish src/mkfs/Mkfs.csproj -c Release
```

Precedence (last wins): **defaults < DB < settings file (TOML) < command-line arguments**.

Internally, [`ConfigLoader`](../src/lib/src/Config/ConfigLoader.cs) runs the CLI -> TOML -> DB -> defaults sources and merges them in one pass. With `--clean`, `skipToml = true` deliberately ignores the TOML.

## Command-line options

These are defined by the `CliOptions` of each `Field<T>` in [src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs). The tables below are an excerpt.

### Connection (PGFS user)

| Option | Meaning | Default |
|---|---|---|
| `-c`, `--connection`, `--connection-string` | Connection string for the target DB as the PGFS user (Npgsql format). | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |

### Connection (superuser)

| Option | Meaning | Default |
|---|---|---|
| `--su`, `--super`, `--super-connection`, `--super-connection-string` (and `--super-user-...` aliases) | Connection string for the maintenance DB (template1) as a superuser. | `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1;SslMode=Prefer` |

> **Inheritance when `--super` is omitted**: the super connection is used for destructive operations such as DROP / CREATE DATABASE. If `--super` is omitted it would otherwise fall back to the default `localhost`, so pointing `--connection` at a remote host could accidentally DROP/CREATE a database on a *different* server (localhost). To prevent this, **when `--super` is not given, the Host / Port / SslMode of `--connection` are inherited into the super connection**, so super and user always point at the same server (the credentials and maintenance DB stay at the super defaults: postgres / template1). When super credentials differ per server, specifying `--super` takes full precedence. The startup log shows the actual targets on the `targets: user = ... , super = ...` line, followed by `user connection:` / `super connection:` lines (both with the password masked).

The design takes a single connection string. Individual `--host`, `--port`, `--username`, `--password` flags are **not implemented for now** (use `database.connection.host` etc. in the settings file instead — see [§Provisional implementation](#provisional-implementation)).

### Schema and tables

| Option | Meaning | Default |
|---|---|---|
| `-s`, `--schema`, `--schema-name` | Schema name. | `public` |
| `-x`, `--prefix`, `--table-prefix`, `--table-name-prefix` | Table name prefix. A trailing `_` is added automatically if missing. | `pgfs_` |
| `--tablespace`, `--tablespace-name` | Tablespace name. | `pg_default` |
| `--tablespace-path` | Directory path when creating a new tablespace. | (empty) |
| `--citus` | Register the tables as Citus distributed tables. See [docs/support_for_citus.md](support_for_citus.md). | `false` |
| `-w`, `--worker`, `--workers` | Comma-separated `host[:port]` Citus worker nodes (e.g. `--worker "w1:5432,w2:5432"`). Empty means a single-node setup (coordinator only). Used with `--citus`. | (empty) |

### Filesystem settings

These are also saved into the `pgfs_settings` table (and, since they also carry `SaveTo=File`, written into `pgfs.toml`).

| Option | Meaning | Default |
|---|---|---|
| `--volume-label` | Volume label. | `pgfs` |
| `--cluster-size` | Cluster size (bytes). | `4096` |
| `--default-chunk-size` | bytea chunk size (bytes). | `1048576` (1 MiB) |
| `--max-file-size` | Maximum file size (bytes; `-1` for unlimited). | `1099511627776` (1 TiB) |
| `--version` | Filesystem version. | `1.0.0` |

### Mount settings

| Option | Meaning | Default |
|---|---|---|
| `-m`, `--mount-point` | Mount point. | Linux/macOS: `/mnt/pgfs` / Windows: `P:` |
| `--cache-max-entries` | inode cache entry limit. | `1024` |

### Logging

| Option | Meaning | Default |
|---|---|---|
| `--log-level`, `--log-min-level`, `--min-log-level` | Minimum log level. | `Warning` |
| `--log-output` | Log output target (`stdout`/`stderr`/`file`). | `stderr` |

### Settings file

| Option | Meaning | Default |
|---|---|---|
| `-f`, `--setting`, `--setting-file` | Settings file path. | `pgfs.toml` |
| `--setting-path`, `--setting-search-path`, `--setting-file-path`, `--setting-file-search-path` | Settings file search paths (multiple allowed). | current -> `~/.config/pgfs` -> `~/.config` -> `~` -> `LocalAppData/pgfs` -> `AppData/pgfs` |

### Other

| Option | Meaning |
|---|---|
| `-?`, `-h`, `--help` | Show help and exit. |
| `--clean` | Ignore the existing `pgfs.toml`, `DROP DATABASE`, then recreate. No short form. The tablespace and role (PGFS user) are not dropped, so the owner and tablespace survive the recreation. If other clients are connected, they are forcibly disconnected with `pg_terminate_backend`. |

## Settings file (pgfs.toml)

TOML format. Hierarchy is expressed with dot notation.

```toml
# database section
database.tablespace = "pg_default"
database.schema = "public"
database.prefix = "pgfs_"

# Connection strings are serialized as JSON strings
database.connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer"
database.super_connection = "Host=localhost;Port=5432;Username=postgres;Database=template1;SslMode=Prefer"

# file_system section
file_system.cluster_size = 4096
file_system.default_chunk_size = 1048576
file_system.max_file_size = 1099511627776

# mount section
mount.mount_point = "/mnt/pgfs"
mount.cache_max_entries = 1024

# logging section
logging.level = "warning"
```

## DB schema details

With [docs/database.md](database.md) as the source of truth, the tables mkfs actually creates are as follows.

### `{prefix}inode`

| Column | Type | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | PK |
| `parent_id` | `BIGINT` | NOT NULL | UK1, IX1 |
| `name` | `TEXT` | NOT NULL | UK1 |
| `uname` | `TEXT` | NOT NULL | IX2 |
| `gname` | `TEXT` | NOT NULL | IX3 |
| `st_mode` | `INTEGER` | NOT NULL | |
| `st_nlink` | `INTEGER` | NOT NULL | `1` |
| `st_size` | `BIGINT` | NOT NULL | `0` |
| `st_mtime` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `st_ctime` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `link_target` | `TEXT` | NULL | |
| `is_junction` | `BOOLEAN` | NOT NULL | `FALSE` |
| `data_id` | `BIGINT` | NULL | |
| `xattrs` | `JSONB` | NOT NULL | `'{}'::JSONB` |
| `created_at` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `created_by` | `TEXT` | NOT NULL | |
| `updated_at` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `updated_by` | `TEXT` | NOT NULL | |

PK = `(parent_id, id)`, UK1 = `(parent_id, name)`, IX = `id` / `uname` / `gname`.

The PK is the composite `(parent_id, id)` to satisfy Citus's constraint that a unique constraint must include the distribution column `parent_id`. id is BIGSERIAL and globally unique (the sequence lives on the coordinator), so `(parent_id, id)` is effectively unique on id alone. A standalone `id` INDEX is kept for `WHERE id = @id` lookups. A standalone `parent_id` INDEX is not created because it is covered by the PK's leftmost prefix.

The root inode is inserted as `id = 0, parent_id = 0, name = '/', st_mode = 16877 (=0o40755)` via `ON CONFLICT (parent_id, name) DO NOTHING` (a standalone `(id)` UK cannot exist under the Citus constraint, so the `(parent_id, name)` UK is the target).

### `{prefix}data`

| Column | Type | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | PK |
| `chunk_size` | `INTEGER` | NOT NULL | |
| `total_size` | `BIGINT` | NOT NULL | |
| `created_at` / `created_by` / `updated_at` / `updated_by` | audit | NOT NULL | |

### `{prefix}data_chunk`

| Column | Type | NULL | DEFAULT |
|---|---|---|---|
| `data_id` | `BIGINT` | NOT NULL | PK |
| `chunk_index` | `INTEGER` | NOT NULL | PK |
| `payload` | `BYTEA` | NOT NULL | |
| 4 audit columns | | NOT NULL | |

PK = `(data_id, chunk_index)`. File contents are stored as `bytea` chunks, which keeps each chunk on a single Citus shard (see [docs/support_for_citus.md](support_for_citus.md)).

### `{prefix}lock`

The cross-client mutual-exclusion lock token table.

| Column | Type | NULL | DEFAULT |
|---|---|---|---|
| `target_id` | `BIGINT` | NOT NULL | PK |

PK = `target_id` only. No audit columns (it holds lock tokens, not data).

### `{prefix}settings`

A flat `(scope, key)` PK. [`ConfigStore.Save<T>`](../src/lib/src/Config/ConfigStore.cs) UPSERTs here and [`ConfigStore.LoadAll`](../src/lib/src/Config/ConfigStore.cs) reads from here.

| Column | Type | NULL | DEFAULT |
|---|---|---|---|
| `scope` | `TEXT` | NOT NULL | PK |
| `key` | `TEXT` | NOT NULL | PK |
| `value` | `JSONB` | NOT NULL | `'null'::JSONB` |
| 4 audit columns | | NOT NULL | |

PK = `(scope, key)`. One row = the value of one `Field<T>`, with `value` in native JSONB form (string -> `"..."` / number -> `42` / bool -> `true`/`false`).

## Citus distribution (`--citus` / `--worker`)

Running mkfs with `--citus` enables the Citus extension and registers each table as a distributed table:

| Table | Distribution | Distribution key | Colocation |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | default group |
| `pgfs_data` | distributed | `id` | default group |
| `pgfs_data_chunk` | distributed | `data_id` | co-located with `pgfs_data` (one file = one shard) |
| `pgfs_lock` | distributed | `target_id` | default group |
| `pgfs_settings` | local + metadata | — | metadata-registered only, via `citus_add_local_table_to_metadata` |

Adding `--worker host[:port],...` also bootstraps the pgfs DB + Citus extension on those workers and registers them from the coordinator via `citus_add_node`.

### Examples

```bash
# Single-node setup (no workers; the coordinator holds shards itself)
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres"

# Multi-node setup (coordinator + worker1 + worker2)
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

### Constraints

- `--citus` x `--tablespace != pg_default` cannot be combined (rejected pre-flight).
- `--clean --citus` only DROPs worker DBs on **the workers specified by `--worker`**. To remove a worker from the topology you must DROP the pgfs DB on that worker manually.
- In a multi-node setup, the worker PG instances must also have `shared_preload_libraries = 'citus'` configured.

### Behavior details / verification

The step structure of EnsureDatabaseAsync (worker bootstrap -> coordinator DB -> Citus topology), the idempotency rule "skip every Citus mutating operation when the DB already exists", and the various gotchas are documented authoritatively in **[docs/support_for_citus.md](support_for_citus.md)**. Verification scripts live in [tests/citus/README.md](../tests/citus/README.md) (`multinode_probe.sh` / `test_matrix.sh` / `race_multinode.sh` / `verify.sql`).

## Idempotency

Every `CREATE` first issues an existence-check SQL before running.

| Target | Existence check | Behavior when present |
|---|---|---|
| User | `pg_user.usename` | Skip (does not change the password). |
| Tablespace | `pg_tablespace.spcname` | Skip. |
| Database | `pg_database.datname` | Skip. |
| Schema | `pg_namespace.nspname` | Skip. |
| Table | `pg_class + pg_namespace` | Skip. |
| Root inode | `INSERT ... ON CONFLICT (parent_id, name) DO NOTHING` | Not inserted. |
| Settings row | `INSERT ... ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value` | Value is overwritten. |

### Recreation with `--clean`

With `--clean`, **`DROP DATABASE`** runs before database creation, followed by the full set of `CREATE`s.

- **Dropped**: the target database (along with its `pgfs_*` tables / root inode / settings rows / bytea chunks).
- **Not dropped**: the tablespace, the PGFS user (role), the superuser connection info.
- **Settings file**: the existing `pgfs.toml` is **not read** (it starts "as if the file does not exist"). Only CLI arguments apply, and a fresh `pgfs.toml` is written on exit.
- **Other client connections**: forcibly disconnected with `pg_terminate_backend` before the DROP. Stop any mounted mount.pgfs / pgfs.assign beforehand.

## Linux-specific notes

mkfs is essentially cross-platform, but the following assume **execution on Linux**.

- **Preparing the tablespace directory**: `CREATE TABLESPACE ... LOCATION '<path>'` requires a directory that the PostgreSQL server process (usually the `postgres` user) can read and write. On Linux you must run the following with `sudo` beforehand:
  ```bash
  sudo mkdir -p /var/lib/pgfs
  sudo chown postgres:postgres /var/lib/pgfs
  sudo chmod 0700 /var/lib/pgfs
  ```
  This depends on the machine's OS / layout, so mkfs does not automate it. On macOS the `postgres` user's uid/gid differ. On Windows you would need to configure NTFS ACLs, which mkfs does not handle yet.
- **The default mount point** is `/mnt/pgfs` (the Linux/macOS default of [`Schema.Mount.MountPoint`](../src/lib/src/Config/Schema.cs)). On Windows it is `P:`.
- **The root inode's `st_mode = 16877 (0o40755)`** represents a POSIX directory + `rwxr-xr-x`. Windows junctions are distinguished by the `is_junction` column ([docs/database.md](database.md)).

Everything else (connection strings, SQL queries, table definitions, etc.) is cross-platform.

## Provisional implementation

| Item | Current implementation | Reason / status |
|---|---|---|
| Settings file format | TOML (`pgfs.toml`) | The current Lib uses Tomlyn. INI etc. are not supported. |
| Connection string input | Consolidated into `-c <Npgsql connection string>` | No individual `-h`/`-p`/`-U`/`-w` (could be added as Fields under `Schema.Database.*`). |
| Password prompt | Not implemented | For now, pass it via the connection string or the `PGPASSWORD` environment variable. |
| `-o` (output settings file) | Not implemented; shares `-f` and writes back to `Setting.Path` | Provisional. A dedicated option may come later. |
| `-v` (verbose logging) | Not implemented (use `--log-level debug`) | Provisional. An alias may come later. |
| AOT publish | Set to `PublishAot=false` | Dapper / Tomlyn rely on reflection, so AOT publish is not possible. Explicitly disabled in every project. |

## Known limitations / TODO

- **Connection-failure retry**: not implemented. A single failure exits. Exponential backoff via Polly is in the requirements, but was judged unnecessary for mkfs and is left out for now.
- **`PGPASSWORD` environment variable**: not used. It must be written directly in the Npgsql connection string.
- **Settings validation**: not implemented. A negative `cluster_size` or an invalid `mount_point` passes through.
- **Error messages on insufficient privileges**: the raw Npgsql exception simply propagates.
- **Verification on non-Linux platforms**: macOS / Windows have not been tested on real hardware. Linux first, then the others.

## Internal structure

[src/mkfs/src/](../src/mkfs/src/) consists of two files.

- **[Program.cs](../src/mkfs/src/Program.cs)**: builds a `RootConfig` from CLI / TOML / defaults via `ConfigLoader` (with `skipToml: true` under `--clean`) -> `Initializer.InitializeAsync` -> writes the SaveTo=File values to the TOML.
- **[Initializer.cs](../src/mkfs/src/Initializer.cs)**: implements each step in "Role" above. All SQL is issued via `Pg.ExecuteAsync` / `Pg.QueryAsync`. Table creation is centralized in the `CreateTableAsync` helper. SaveTo=Db values are UPSERTed into `pgfs_settings` via `ConfigStore.Save<T>`.

The configuration model reuses `RootConfig` / `Schema` from [src/lib/src/Config/](../src/lib/src/Config/) directly. No mkfs-specific configuration classes are created.

## References

- [docs/database.md](database.md) — DB schema design.
