# The mkfs.pgfs specification

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **the specification of `mkfs.pgfs`** - the CLI options,
> **the defaults table of the settings that can be given from mkfs**, the TOML format and the flow of applying
> the DDL. **This is the source of truth for the defaults of what can be given from mkfs**, and the excerpt
> tables on the Mount / Assign side defer to it.
>
> **[design/settings-matrix.md](design/settings-matrix.md) is the source of truth for the complete table of all
> the settings.** This document lists **only what is meaningful from mkfs's CLI**, and
> **what only takes effect at mount time** (`mount.max_write` /
> `mount.foreground` / `database.retry_*` / `database.notify_enabled` and so on)
> **is deliberately left out**.
> **"Not here" does not mean "it does not exist"**, so look at the matrix when checking every setting
> (what once said "the defaults table of every setting" was narrowed to match reality).
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [design/database.md](design/database.md) | The database schema design (what the tables mean) |
> | [ddl/README.md](ddl/README.md) | The DDL itself, per table |
> | [design/settings-matrix.md](design/settings-matrix.md) | **The matrix** of every setting (where it is stored, the reload policy) |
> | [Mount.md](Mount.md) / [Assign.md](Assign.md) | The specifications of the mounting side. The defaults defer to this document |
> | [design/support_for_citus.md](design/support_for_citus.md) | The design of `--citus` / `--shard-count` / `--rf` |

The specification of `mkfs.pgfs`, the tool that initializes a PGFS filesystem on a PostgreSQL database.

This document gathers **the specification as settled in the current implementation
([src/mkfs/](../src/mkfs/), based on `Pgfs.Core.Config`)**. The provisional and unsupported parts of the
implementation are in the "the provisional implementations" section.

## Its role

It creates the following, idempotently, on a PostgreSQL database, as PGFS requires:

1. The PGFS user
2. The tablespace (only when one is given)
3. The database
4. The schema
5. The tables `{prefix}inode`, `{prefix}data`, `{prefix}data_chunk`, `{prefix}lock`, `{prefix}settings`,
   `{prefix}audit` and `{prefix}mounts` (**7 of them**), plus the `{prefix}statfs()` function when `--statfs` is
   given
6. The root directory inode (`id = 0`)
7. Persisting the settings that have `SaveTo=DB` (an UPSERT into `{prefix}settings`)
8. The settings file `pgfs.toml` (writing out the settings that have `SaveTo=File`)

**Creating the tables and so on is idempotent** and skips the `CREATE` when it already exists. **The settings are
not idempotent, though** - even without `--clean`, the settings stored in `{prefix}settings` (`audit.enabled` /
`app.plperlu` / `app.statfs` / `database.citus` / the tablespace / `file_system.*` / `fallback_*`) are **overwritten
with the CLI values of that run or the defaults**, and `pgfs.toml` is rewritten too (the settings of the existing
database are not read). When re-running it against an existing filesystem, **give every option it was created with**,
or run only the DDL you need directly ([CHANGELOG.md](../CHANGELOG.md), the known limitations).

## How it is run

The entry point is [src/mkfs/src/Program.cs](../src/mkfs/src/Program.cs).

```pwsh
# a development build (bin/Debug/mkfs.pgfs.{dll,exe})
dotnet build src/mkfs/Mkfs.csproj
# running during development
dotnet run --project src/mkfs -- -f pgfs.toml [options]
# or the built executable
./bin/Debug/mkfs.pgfs -f pgfs.toml [options]

# a self-contained publish (bin/Publish/mkfs.pgfs[.exe] - single-file, the host RID automatically)
dotnet publish src/mkfs/Mkfs.csproj -c Release
```

The precedence (the last wins): **the default < the database < the settings file (TOML) < the command line**.

In the implementation, [`ConfigLoader`](../src/core/src/Config/ConfigLoader.cs) runs the phases in the order
CLI -> TOML -> the database -> the default and merges them in one go. With `--clean` it deliberately ignores the
TOML through `skipToml = true`.

## The command-line options

They are defined in the `CliOptions` of each `Field<T>` in
[src/core/src/Config/Schema.cs](../src/core/src/Config/Schema.cs). The table below is an excerpt.

### The connection (the PGFS user)

| The option | What it is | The default |
|---|---|---|
| `-c`, `--connection`, `--connection-string` | The string for connecting to the target database as the PGFS user (the Npgsql form) | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |

### The connection (the superuser)

| The option | What it is | The default |
|---|---|---|
| `--su`, `--super`, `--super-connection`, `--super-connection-string` (plus the `--super-user-...` aliases) | The string for connecting to the maintenance database (template1) as a superuser | `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1;SslMode=Prefer` |

> **Inheriting the target when `--super` is not given explicitly**: the super connection is used for destructive
> operations such as DROP and CREATE DATABASE. Omitting `--super` makes it fall back to the default
> `localhost`, so pointing `--connection` at a remote server could cause the accident of
> "DROPping and CREATEing a database on a different server (localhost)". To prevent that,
> **when `--super` is not given explicitly, the Host / Port / SslMode of `--connection` are inherited into the
> super connection**, so that super and the user always point at the same server (the credentials and the
> maintenance database stay at super's defaults of postgres and template1). If the super credentials differ per
> server, giving `--super` explicitly makes that value take full precedence. The actual targets and connection
> strings can be checked in the `targets: user = ... , super = ...` line at the head of the startup log and the
> following `user connection:` / `super connection:` lines (the Password is masked in both).

The design is that the connection string is given as one thing. Individual `--host`, `--port`, `--username` and
`--password` **are not implemented for now**.

> ⚠ **There is no per-item form on the settings-file side either.** This used to say "use the per-item
> `database.connection.host` and so on that can be written in the settings file", and
> [pgfs.toml.example](../pgfs.toml.example) was in that shape too, but **the implementation cannot read it** -
> the settings file **only interprets the two levels of `scope.key = value`**, so
> `database.connection.host = "..."` is treated as a table, turns into a connection string and died with
> `Format of the initialization string does not conform to specification` (measured and fixed on 2026-09-21).
> **Write the connection as a one-line connection string** (either the kv form or the URL form). If a table
> form is written, **it is ignored with a warning**.

### The schema and the tables

| The option | What it is | The default |
|---|---|---|
| `-s`, `--schema`, `--schema-name` | The schema name | `public` |
| `-x`, `--prefix`, `--table-prefix`, `--table-name-prefix` | The table name prefix. A trailing `_` is added if it is missing | `pgfs_` |
| `--tablespace`, `--tablespace-name` | The tablespace name (it can be customized on Citus too, through the `CREATE DATABASE WITH TABLESPACE` inheritance scheme) | `pg_default` |
| `--tablespace-path` | The directory path when creating a new tablespace. With `--allow-plperlu` (the default), mkfs creates the directory automatically through plperlu, owned by postgres at 0700 | (empty) |
| `--citus` | Register the tables as Citus distributed tables. For the details see [docs/support_for_citus.md](design/support_for_citus.md) | `false` |
| `-w`, `--worker`, `--workers` | The Citus worker nodes as a comma-separated `host[:port]` (for example `--worker "w1:5432,w2:5432"`). Empty means a single-node configuration (the coordinator only). Used with `--citus` | (empty) |
| `--shard-count` | Citus's `citus.shard_count` (the shard count of a distributed table). `0` follows the cluster default | `0` |
| `--shard-replication-factor`, `--rf` | How many nodes one shard is placed on (= the storage redundancy; `1` means no replication). `0` follows the cluster default. The exclusion is gathered in the undistributed `{prefix}lock`, so this value does not affect the locking mechanism | `0` |
| `--distribute-existing` | With `--citus`, make the **existing** tables Citus too (distributed or registered in the metadata). The default is "only the tables newly created" | `false` |

### The filesystem settings (stored in the database, common to every client)

These are FS identity information, so they are **saved in `pgfs_settings`** (SaveTo=Db) and
**are not written out to the generated `pgfs.toml`**.
In particular, a divergence in the `cluster_size` or the `default_chunk_size` between clients can corrupt data,
so a single source in the database is correct (moved from File to Db; for the details see
[settings-and-plperlu.md](design/settings-and-plperlu.md)).

| The option | What it is | The default |
|---|---|---|
| `--volume-label` | The volume label | `pgfs` |
| `--cluster-size` | The cluster size (in bytes) | `4096` |
| `--default-chunk-size` | The bytea chunk size (in bytes) | `1048576` (1 MiB) |
| `--max-file-size` | The maximum file size (in bytes; `-1` for unlimited) | `1099511627776` (1 TiB) |
| `--fs-version` | The filesystem version (renamed from `--version` in v0.2.1) | `1.0.0` |

**The permissions of the root directory** (since v0.2.1) are an mkfs-only action that is kept neither in the
database nor in the toml:

| The option | What it is | The default |
|---|---|---|
| `--root-access owner\|everyone` | The permissions of the root directory (inode 0) **when it is newly created**. `owner` = `root:root 0755` (Administrators can write from Windows) / `everyone` = `root:root 1777` (anyone can write, and only the creator can delete = the same sticky bit as `/tmp`; Everyone can write from Windows). **If the root already exists it is left unchanged and a Warning is printed** (to change it, mount and `sudo chmod`). Windows does not enforce the mode, so the sticky bit only has an effect on Linux | `owner` |

### The feature flags (stored in the database, common to every client; changed after creation with `pgfsctl config set`)

These are saved in `pgfs_settings` and read from the database by mount and assign at startup (they are not
written into the TOML).

| The option | What it is | The default |
|---|---|---|
| `--audit` | Record the metadata changes in `{prefix}audit`. For the details see [docs/audit-log.md](design/audit-log.md) | `false` |
| `--statfs`, `--statfs-mode` | The mode for `df` (statfs) reporting the real free space. `auto` (measured if plperlu is there, nominal if not) / `require` (plperlu is mandatory; mkfs fails without it) / `nominal` (always the nominal capacity, with no function created). The stored key is `app.statfs`. For the details see [docs/df-support.md](design/df-support.md) | `auto` |
| `--plperlu` `[true\|false]` / `--allow-plperlu` / `--deny-plperlu` | Permission to use the untrusted plperlu (the higher gate, `app.plperlu`). `require` plus deny is a contradiction and an error, and auto plus deny is equivalent to nominal. The tablespace auto-mkdir is under this gate too. For the details see [settings-and-plperlu.md](design/settings-and-plperlu.md) | `true` (allow) |

### The mount settings

| The option | What it is | The default |
|---|---|---|
| `-m`, `--mount-point` | The mount point | Linux/macOS: `/mnt/pgfs` / Windows: `P:` |
| `--cache-max-entries` | The count ceiling of the inode metadata cache | `1024` |
| `--cache-data-max-bytes` | The byte budget of the read cache for the file bodies (data_chunk) (`0` disables it) | `67108864` (64 MiB) |

### The logging

| The option | What it is | The default |
|---|---|---|
| `--log-level`, `--log-min-level`, `--min-log-level` | The minimum log level | `Information` |
| `--log-output` | The log destination (`stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`. **There is no literal `file`** - file output is written with a cycle, as in `daily:~/pgfs/log/pgfs-*.log`. The cycle is `none`/`hourly`/`daily`/`monthly`) | `stderr` |

### The settings file

| The option | What it is | The default |
|---|---|---|
| `-f`, `--setting`, `--setting-file` | **The location of the settings file (mandatory for mkfs, since v0.2.1)**. If it exists it is read and written back there at the end; if not it is created. With `--clean` it is overwritten without being read | (mandatory) |
| `--setting-path`, `--setting-search-path`, `--setting-file-path`, `--setting-file-search-path` | The search path for the settings file. **mkfs does not use it** (giving it prints a Warning; it is for mount / assign / pgfsctl) | - |

### The rest

| The option | What it is |
|---|---|
| `-?`, `-h`, `--help` | Show the help and exit |
| `--version` | Show the program's version and exit (since v0.2.1; the same for mount / assign / pgfsctl) |
| `--clean` | Ignore any existing `pgfs.toml` and `DROP DATABASE` before recreating. No short form. The tablespace and the role (the PGFS user) are not discarded, so the owner and the tablespace are kept across the recreation. **Before removing anything it checks for connections and asks for confirmation** (see "The confirmation before removing" below; since v0.2.1) |
| `--purge` | **Remove and stop there** (since v0.2.1). `DROP DATABASE` the database at the connection target of the `-f` settings file and, on Citus, the database of the same name on every worker. **Nothing is recreated and no settings file is written**. It cannot be given together with `--clean` (exit 2). See "`--purge`" below |
| `--yes`, `-y` | **Skip the confirmation** of `--clean` / `--purge` (for scripts). **When non-interactive (stdin is not a terminal) and it is not given, nothing is removed and the exit code is 3** |
| `--now` | With `--clean` / `--purge`, **disconnect what is connected (live mounts / other connections) without waiting, and go on**. Without it, an interactive run keeps asking "retry?", and a non-interactive run exits with 3 |

## The settings file (pgfs.toml)

### What goes into the toml that mkfs writes out for distribution (since v0.2.1)

At the end, mkfs writes out a `pgfs.toml` to hand out to the other clients. **Only these two kinds of item go
into it**:

1. **The core of the connection** - `database.connection` (the Password is written as it is; it is masked in the
   mkfs command line in the leading comment) / `database.schema` / `database.prefix`. Always written.
2. **The `SaveTo=File` items given explicitly on the CLI or in the TOML that was read** - including the items for
   mount / assign such as `--notify` / `--no-notify`, `-m` (mount_point) and `--write-back`.
   **Even an item that has no effect on mkfs itself stays in the toml if it is given explicitly** (the lines
   marked `[written to pgfs.toml for mount/assign]` in `mkfs --help`).
   **Only `database.workers` is an exception and is not written** (it is mkfs-only and means nothing to the
   clients it is handed to, and writing it would make a later mkfs without `--clean` register the workers on
   its own).

**Items left at their defaults are not written**. Writing them would pin the default on the clients, and they
would not follow a default changed in a later version (for example, v0.2.1 changed the default of
`database.notify_enabled` to true, but a toml written by the v0.2.0 mkfs that contains `false` does not follow).
**`mount.mount_point` is also written only when given explicitly** - a toml written on Linux can be handed to
Windows without rewriting it (the mount target is each OS's default, `/mnt/pgfs` / `P:`).

The `SaveTo=Db` items (the tablespace / the file_system size settings / audit / statfs / plperlu and others)
have the database as the source of truth, so they do not appear in the toml.

**The only place it is written to is the location given with `-f`** (since v0.2.1; mandatory). Up to v0.2.0 it
looked for a toml along the default search path (the current directory -> `~/.config/pgfs` -> ... ->
`LocalAppData/pgfs`) and **wrote back to where it was found**, so it could read and overwrite the toml used by
a resident mount (for example `%LOCALAPPDATA%\pgfs\pgfs.toml`).

### The format

TOML, with the hierarchy expressed in dot notation.

```toml
# the database section
database.tablespace = "pg_default"
database.schema = "public"
database.prefix = "pgfs_"

# the connection string is serialized as a JSON string
database.connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer"
database.super_connection = "Host=localhost;Port=5432;Username=postgres;Database=template1;SslMode=Prefer"

# the file_system section
file_system.cluster_size = 4096
file_system.default_chunk_size = 1048576
file_system.max_file_size = 1099511627776

# the mount section
mount.mount_point = "/mnt/pgfs"
mount.cache_max_entries = 1024
mount.cache_data_max_bytes = 67108864
mount.negative_cache_ttl_ms = 0
mount.write_back = false
mount.write_back_max_bytes = 67108864
mount.write_back_interval_ms = 1000
mount.write_back_metadata = false
mount.write_back_metadata_exclusive_create = "write_through"
mount.write_back_max_inodes = 4096
mount.write_back_flush_timeout_ms = 30000

# the logging section
logging.level = "information"
```

## The database schema in detail

With [docs/database.md](design/database.md) as the source of truth, the tables mkfs actually creates are as
follows.

### `{prefix}inode`

| The column | The type | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | Part of the composite PK |
| `parent_id` | `BIGINT` | NOT NULL | UK1, IX1 |
| `name` | `TEXT` | NOT NULL | UK1 |
| `uname` | `TEXT` | NOT NULL | IX2 |
| `gname` | `TEXT` | NOT NULL | IX3 |
| `st_mode` | `INTEGER` | NOT NULL | |
| `st_nlink` | `INTEGER` | NOT NULL | `1` |
| `st_size` | `BIGINT` | NOT NULL | `0` |
| `st_mtime` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `st_ctime` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `link_target` | `TEXT` | NULL | |
| `is_junction` | `BOOLEAN` | NOT NULL | `FALSE` |
| `data_id` | `BIGINT` | NULL | |
| `xattr_names` | `TEXT[]` | NOT NULL | `'{}'::TEXT[]` |
| `xattr_values` | `BYTEA[]` | NOT NULL | `'{}'::BYTEA[]` |
| `created_at` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `created_by` | `TEXT` | NOT NULL | |
| `updated_at` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `updated_by` | `TEXT` | NOT NULL | |

PK = `(parent_id, id)`, UK1 = `(parent_id, name)`, and the indexes are `id` / `uname` / `gname`.

The PK is the composite `(parent_id, id)` to satisfy the Citus restriction (a unique constraint has to include
the distribution key `parent_id`). The id is a BIGSERIAL and globally unique (the sequence is on the
coordinator), so `(parent_id, id)` is effectively unique by the id alone. A separate INDEX on `id` alone is held
for `WHERE id = @id` searches. An INDEX on `parent_id` alone is not created because the PK's leftmost prefix
serves.

The root inode is inserted as `id = 0, parent_id = 0, name = '/', st_mode = 16877 (= 0o40755)` with
`ON CONFLICT (parent_id, name) DO NOTHING` (a UK on `(id)` alone cannot be created under the Citus restriction,
so the `(parent_id, name)` UK is the target).

### `{prefix}data`

| The column | The type | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | PK |
| `chunk_size` | `INTEGER` | NOT NULL | |
| `total_size` | `BIGINT` | NOT NULL | |
| `created_at` / `created_by` / `updated_at` / `updated_by` | For auditing | NOT NULL | |

### `{prefix}data_chunk`

| The column | The type | NULL | DEFAULT |
|---|---|---|---|
| `data_id` | `BIGINT` | NOT NULL | PK |
| `chunk_index` | `INTEGER` | NOT NULL | PK |
| `payload` | `BYTEA` | NOT NULL | |
| The 4 audit columns | | NOT NULL | |

PK = `(data_id, chunk_index)`. The old design used large objects (`lo_oid OID` plus `pg_largeobject`), but as a
precondition of the Citus distribution it was **replaced with bytea in Phase 1** (for the details
see [docs/support_for_citus.md](design/support_for_citus.md)).

### `{prefix}lock`

Created in advance in Citus Phase 2. The lock token table for the cross-client exclusion. It was
connected to the Api's row locks in Phase 3.

| The column | The type | NULL | DEFAULT |
|---|---|---|---|
| `target_id` | `BIGINT` | NOT NULL | PK |

PK = `target_id` alone. No audit columns (it is a lock token, not data).

### `{prefix}settings`

The PK is a flat `(scope, key)`.
[`ConfigStore.Save<T>`](../src/core/src/Config/ConfigStore.cs) UPSERTs into it and
[`ConfigStore.LoadAll`](../src/core/src/Config/ConfigStore.cs) reads from it.

| The column | The type | NULL | DEFAULT |
|---|---|---|---|
| `scope` | `TEXT` | NOT NULL | PK |
| `key` | `TEXT` | NOT NULL | PK |
| `value` | `JSONB` | NOT NULL | `'null'::JSONB` |
| The 4 audit columns | | NOT NULL | |

PK = `(scope, key)`. One row is one `Field<T>`'s value, and the `value` is in its native JSONB representation
(a string -> `"..."` / a number -> `42` / a bool -> `true`/`false`).

## The Citus distribution (`--citus` / `--worker`)

Running mkfs with `--citus` enables the Citus extension and registers the target tables as follows (lock,
settings and mounts are not distributed):

| The table | The distribution | The distribution key | The colocation |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | The default group |
| `pgfs_data` | distributed | `id` | The default group |
| `pgfs_data_chunk` | distributed | `data_id` | Colocated with `pgfs_data` (one file = one shard) |
| `pgfs_lock` | local plus metadata | - | One copy for the exclusion, on the coordinator |
| `pgfs_audit` | distributed | `occurred_at` | Monthly partitions |
| `pgfs_mounts` | local plus metadata | - | The registry of the running mounts and assigns |
| `pgfs_settings` | local plus metadata | - | Only registered in the metadata with `citus_add_local_table_to_metadata` |

Using `--worker host[:port],...` as well bootstraps the pgfs database plus the Citus extension on those workers
too and registers them from the coordinator with `citus_add_node`.

The shard count and the replication count can be given with `--shard-count` /
`--shard-replication-factor` (`--rf`) (both `0` = follow the cluster default). The values take effect only in
the session that calls `create_distributed_table`, so they do not affect the distributed tables of other
applications sharing the same database. **The replication count is a choice about storage redundancy** and does
not affect the exclusion (`{prefix}lock` = an undistributed Citus local table).

Running `--citus` against an existing schema later makes **only the tables newly created** Citus by default. To
distribute the existing tables too, give `--distribute-existing` explicitly (it is a heavy operation that
redistributes the data into the shards, so it is behind an explicit flag; it is idempotent).

### Examples

```bash
# a single-node configuration (no workers; the coordinator holds the shards itself)
mkfs.pgfs -f pgfs.toml --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres"

# a multi-node configuration (a coordinator plus worker1 plus worker2)
mkfs.pgfs -f pgfs.toml --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

### The constraints

- `--citus` and a custom `--tablespace` **can coexist**. Through the
  `CREATE DATABASE WITH TABLESPACE` inheritance scheme, the tablespace is created on the coordinator plus every
  worker. The LOCATION directory is created automatically by plperlu, owned by postgres at 0700, with
  `--allow-plperlu` (allow by default) when `--tablespace-path` is given. For the details see
  [settings-and-plperlu.md](design/settings-and-plperlu.md).
- **The workers that `--clean` removes are decided by what is actually in the database (`pg_dist_node`)**
  (since v0.2.1). Even without `--worker`, the database of the same name is removed on every worker of the
  current configuration (up to v0.2.0 only the workers given with `--worker` were covered, and forgetting it
  left the database behind on the workers).
  **If even one worker cannot be reached, it stops without removing anything** (the node names in
  `pg_dist_node` are the names as seen from the coordinator, so they have to be reachable from the host that
  runs mkfs). Take an unused worker out first with `citus_remove_node` on the coordinator.
  `--worker` is relied on only when the coordinator's database no longer exists.
- In a multi-node configuration the PG on the worker side also needs `shared_preload_libraries = 'citus'` set.

### The behaviour in detail and the verification

**[docs/support_for_citus.md](design/support_for_citus.md) is the source of truth** for the phase structure of
EnsureDatabaseAsync (the worker bootstrap -> the coordinator database -> the Citus topology), the idempotency
rules such as "skip every Citus-related mutation when the database exists", and the various stumbling blocks.
The verification scripts are in [tests/citus/README.md](../tests/citus/README.md) (`multinode_probe.sh` /
`test_matrix.sh` / `race_multinode.sh` / `verify.sql`).

## The idempotency

Every `CREATE` issues an existence-check SQL statement beforehand.

| The subject | The existence check | What happens when it exists |
|---|---|---|
| The user | `pg_user.usename` | Skipped (the password is not changed). On the workers it is ensured only when the database is created (`--clean` or no database) |
| The tablespace | `pg_tablespace.spcname` | Skipped. **Ensured only when the database is created** (an existing database does not use it) |
| The database | `pg_database.datname` | Skipped |
| The schema | `pg_namespace.nspname` | Skipped |
| A table | `pg_class` plus `pg_namespace` | Skipped |
| The root inode | `INSERT ... ON CONFLICT (parent_id, name) DO NOTHING` | Not inserted |
| A settings row | Rows in `pgfs_settings` mean an existing FS | **The FS-specific settings take the value in the database** (since v0.2.1; see "Running it again on an existing FS" below). Only the items added in later versions are added |

### Running it again on an existing FS (since v0.2.1)

The settings fall into three groups: **client-specific** (the toml - the connection target, `mount.*`, notify,
logging, retries) / **FS-specific** (the database - `audit.enabled`, `app.statfs`, `app.plperlu`,
`file_system.*`) / **instructions only for creation** (kept nowhere - `--clean`, `--root-access`, `--worker`,
`--citus`, `--shard-count`, `--rf`, `--tablespace`, `--tablespace-path`).

- **The FS-specific settings are decided once, when the FS is created**. Running mkfs again on an existing FS
  without `--clean` uses the values in the database, and a different value given on the CLI is not applied;
  a Warning is printed instead ("this has no effect on an existing FS; use `pgfsctl config set` to change
  it"). Up to v0.2.0 they were silently overwritten by the CLI or the defaults.
- **The instructions only for creation**: `--tablespace` / `--tablespace-path` / `--worker` have no effect on an
  existing database and print a Warning. **For an existing database nothing is created on the workers or in
  the tablespace** (v0.2.1; before, the user and the tablespace were ensured before the database existence
  check, so roles were created on every `--worker` node even for an existing database).
  **Whether it is Citus is decided by what is actually in the database** (whether the `citus` extension is
  there), **in both directions** - a database that is already Citus distributes the tables added later even
  without `--citus`, and **giving `--citus` to a database that is not Citus does not make it Citus** (a
  Warning; to make it Citus, recreate it with `--clean`. Before, it tried to distribute the tables of a new
  schema and failed). Workers are added with `citus_add_node` on the coordinator. The tablespace that the
  statfs function measures is also taken from what is actually in the database (`pg_database`).
- `--distribute-existing` is an instruction for an existing FS, so it takes effect as before.

### Recreating with `--clean`

With `--clean`, a **`DROP DATABASE`** runs before the database creation and then the series of `CREATE`s.

- **What is removed**: the target database (along with the `pgfs_*` tables, the root inode, the settings rows
  and the bytea chunks inside it). On Citus, **also the database of the same name on every worker in
  `pg_dist_node`** (see the constraints of the Citus distribution above)
- **What is not removed**: the tablespace, the PGFS user (the role) and the superuser connection details
- **The settings file**: any existing `pgfs.toml` is **not read** (it starts as if the file did not exist). Only
  the CLI arguments apply, and a new `pgfs.toml` is written out at the end.
- **Other clients' connections**: **they are counted and shown, and nothing is removed until they are gone**
  (since v0.2.1; see "The confirmation before removing" below). Up to v0.2.0 they were silently disconnected
  with `pg_terminate_backend`. To disconnect them without waiting, use `--now`.

### `--purge` (remove and stop there; since v0.2.1)

This is `--clean` without the "recreate". **What it removes is the connection target of the `-f` settings file**
(exit 2 if there is no settings file. The toml actually in use has to be given, so that a mistyped connection
string does not remove a different FS).

- **What is removed**: the target database. On Citus, also the database of the same name on every worker in
  `pg_dist_node` (see the constraints of the Citus distribution).
- **What is not removed**: the role, the tablespace and the settings file (the `-f` toml).
- Given together with `--clean`, it is unclear which was meant, so it does nothing and exits with 2.
- Before removing, what will be removed can be checked with the `target` / `citus` of
  `pgfsctl status -f <the same toml>` ([Pgfsctl.md](Pgfsctl.md)).

```bash
pgfsctl status -f /etc/pgfs.toml          # check the target (host:port/db) and the citus nodes
mkfs.pgfs --purge -f /etc/pgfs.toml       # what is connected -> the confirmation (y/N) -> remove
```

### The confirmation before removing (`--clean` / `--purge`; since v0.2.1)

The order is **look up the configuration in the database -> can the workers be reached -> what is connected ->
the confirmation -> remove**. **`--now` takes out only "what is connected", and `--yes` only "the
confirmation"** (to take out both, `--yes --now`).

1. **What is connected** is counted and shown as a list:
   - **Live mounts** - the rows whose heartbeat is within 90 seconds in the `*mounts` table of every schema in
     the target database (the same as live in `pgfsctl status`). The host and the mountpoint are shown.
   - **Other connections** - the client backends in `pg_stat_activity` (excluding itself = application_name
     `mkfs.pgfs`, and Citus's internal connections). On Citus, also the database of the same name on each
     worker. The count per application_name is shown.

   | Given | Interactive (with a terminal) | Non-interactive |
   |---|---|---|
   | With `--now` | Disconnect and go on (`pg_terminate_backend` at the time of removing) | The same |
   | Without `--now` | **Keep asking "retry? (y/n)"**. y counts again, and 0 moves on. n does nothing and exits with 3 | Do nothing and exit with 3 |

   Between the retries, the mounts can be taken down (`umount` / `dokanctl /u`) or the other side checked with
   `pgfsctl status`.
2. **The confirmation**: the connection target (`host:port/db`), **every schema in the database** (`--clean` /
   `--purge` remove the whole database, so schemas other than the one given are removed too) and the Citus
   workers are shown, with "remove? (y/N)".
   With `--yes` they are only shown. **Non-interactive without `--yes` removes nothing and exits with 3**.
3. Remove.

**The exit codes**: 0 = success / 1 = failure / 2 = a wrong argument (`--clean --purge` / `--purge` with no
settings file) / **3 = aborted (nothing removed)**.

## The Linux-specific points

mkfs is basically cross-platform, but the following **presume it runs on Linux**.

- **Preparing the tablespace directory**: `CREATE TABLESPACE ... LOCATION '<path>'` requires a directory the
  PostgreSQL server process (usually the `postgres` user) can read and write. On Linux the following has to be
  done with `sudo` beforehand:
  ```bash
  sudo mkdir -p /var/lib/pgfs
  sudo chown postgres:postgres /var/lib/pgfs
  sudo chmod 0700 /var/lib/pgfs
  ```
  That handling differs by the machine's OS and layout, so mkfs does not automate it. On macOS the `postgres`
  user's uid and gid differ. On Windows the NTFS ACL has to be set, which the current mkfs does not support.
- **The default mount point** is `/mnt/pgfs` (the Linux/macOS default of
  [`Schema.Mount.MountPoint`](../src/core/src/Config/Schema.cs)). On Windows it is `P:`.
- **The root inode's `st_mode = 16877 (0o40755)`** expresses a POSIX directory plus `rwxr-xr-x`. A Windows
  junction is distinguished by the `is_junction` column by design
  ([docs/database.md](design/database.md)).

Everything else (the connection strings, the SQL queries, the table definitions and so on) is cross-platform.

## The provisional implementations

| The item | The current implementation | The reason / what is provisional |
|---|---|---|
| The settings file format | TOML (`pgfs.toml`) | The current library uses Tomlyn. INI and the like are not supported |
| How the connection string is given | Unified into `-c <an Npgsql connection string>` | There are no individual `-h`/`-p`/`-U`/`-w` (they could be supported by adding Fields to `Schema.Database.*`) |
| A password prompt | Unimplemented | For now it is expected to come through the connection string or the `PGPASSWORD` environment variable |
| `-o` (the output settings file) | Unimplemented; it doubles with `-f` and writes back into `Setting.Path` | Provisional. A dedicated option comes later |
| `-v` (verbose logging) | Unimplemented (`--log-level debug` substitutes) | Provisional. The alias comes later |
| An AOT publish | Set to `PublishAot=false` | An AOT publish is impossible because of Dapper's and Tomlyn's reflection dependence. It is explicitly disabled in every project |

## The known limitations and TODOs

- **A retry mechanism on a failed connection**: unimplemented. One failure and it exits. The requirements
  mention an exponential backoff through Polly, but it was judged unnecessary for mkfs and is not added for now.
- **Using the `PGPASSWORD` environment variable**: unimplemented. It has to be written into the Npgsql
  connection string directly.
- **Validating the setting values**: unimplemented. A negative `cluster_size` or an invalid `mount_point` gets
  through.
- **The error message when the privileges are insufficient**: an Npgsql exception simply flows through.
  Translating it into a plain message is unimplemented.
- **Confirming it works outside Linux**: there has been no real-hardware test on macOS or Windows. It is being
  done in order after making it work on Linux.

## The internal structure

[src/mkfs/src/](../src/mkfs/src/) consists of these 2 files:

- **[Program.cs](../src/mkfs/src/Program.cs)**: assembles a `RootConfig` from the CLI, the TOML and the defaults
  with the `ConfigLoader` (with `skipToml: true` under `--clean`) -> `Initializer.InitializeAsync` -> writes the
  SaveTo=File values out to the TOML.
- **[Initializer.cs](../src/mkfs/src/Initializer.cs)**: implements each step of "its role" above. All the SQL is
  issued through `Pg.ExecuteAsync` / `Pg.QueryAsync`. Creating a table is gathered in the `CreateTableAsync`
  helper. The SaveTo=Db values are UPSERTed into `pgfs_settings` with `ConfigStore.Save<T>`.

The settings model uses the `RootConfig` / `Schema` of [src/core/src/Config/](../src/core/src/Config/) as they
are. No mkfs-specific settings class is created.
