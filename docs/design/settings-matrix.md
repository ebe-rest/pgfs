# The matrix of the settings

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **a list** that looks across every setting by
> "the key / the CLI / the TOML / where it is stored in the database / the default / the type / when it is
> read", plus the resolution precedence, the classification of the reload policy (Live / NextMount / Format),
> the aggregation by lifecycle and the known problems such as the short-form collisions. The declaration of a
> setting itself is authoritative in [Schema.cs](../../src/core/src/Config/Schema.cs), and
> **the user-facing defaults table is authoritative in [../Mkfs.md](../Mkfs.md)**.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../Mkfs.md](../Mkfs.md) | The CLI list of `mkfs.pgfs` and **the defaults table** (plus the TOML example). When a default changes, fix that too |
> | [../Mount.md](../Mount.md) / [../Assign.md](../Assign.md) | Each tool's CLI specification and the behaviour as the user sees it |
> | [../Pgfsctl.md](../Pgfsctl.md) | How to use `pgfsctl config get / set / list` |
> | [fstab-support.md](fstab-support.md) | The options that come through `-o key=val,...`, plus the decision logic and the processing order of the short-form collisions |
> | [control-plane.md](control-plane.md) | How the reload policy works and the design of the live application (the side that moves the settings) |
> | [settings-and-plperlu.md](settings-and-plperlu.md) | The design history and the decisions of the settings-scope reorganization (`app.*` / making the database authoritative) |

> **🗒 The old `Pgfs.Core.Models.*Settings` tree was removed**. The true declaration of
> each setting is gathered in [src/core/src/Config/Schema.cs](../../src/core/src/Config/Schema.cs) (the static
> `Field<T>` descriptors `Schema.<Scope>.<Key>`). This document is for looking across
> "the CLI flag / the TOML key / where it is stored in the database / the default / when it is read" in one
> list. **If it disagrees with the code, the Schema wins.** There is a proposal to replace it later with
> something generated from the Schema.

> It was compared statically against the current code. No default changed. Live is a
> classification of how it is applied. **The live switching of write-back waits for the drain through the
> two-phase flip** (fixed), so **it was confirmed on real hardware that switching loses no data** -
> the regressions are `test_cp_write_back_live_flip` / `test_cp_metadata_flip_completes` in the
> `control_plane` suite ([tests.md](../tests.md) is the source of truth for the counts).

---

## The key to the tables

- **The key**: the hierarchical key (`scope.key`) in the `pgfs_settings` table and the TOML. It matches the
  code's `Field.Scope` / `Field.Key`
- **The CLI**: the arguments accepted. Where there are several, the most common one is in bold
- **TOML**: it is read from `pgfs.toml` (= `SaveTo = File`)
- **DB**: it is persisted as a `pgfs_settings(scope, key, value)` row (= `SaveTo = Db`)
- **The default**: the return value of `Field.DefaultFn()`
- **The type**: the C# type of the value (the T of `Field<T>`)
- **When it is read**: when the code reads it from the `RootConfig` (at startup / at mkfs time / dynamically)
- **Notes**: the constraints, the dependencies and the known traps

## The precedence

[`ConfigLoader`](../../src/core/src/Config/ConfigLoader.cs) merges the CLI, the TOML, the database and the
defaults in one go. The precedence is CLI > TOML > the database > the default. The phases run in the order
`CLI -> TOML -> the database`, realized by "skip if a higher source has already put a value in".
`AssignPositional` / `ParseDashOOptions` also only apply "when it has not been loaded", so an explicit
`-c` / `-m` / `-f` takes precedence over a positional.

---

## 1. The root (written directly as `-` / `--`, with no parent)

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `help` | `-?` `-h` **`--help`** | ❌ | ❌ | false | bool | Right after the CLI parse | Show the usage and exit immediately |
| `clean` | **`--clean`** | ❌ | ❌ | false | bool | At the head of mkfs's `Initializer.cs` | DROP DATABASE then recreate. It deliberately has no short form (to prevent running it by mistake) |
| `purge` | **`--purge`** | ❌ | ❌ | false | bool | mkfs's `Initializer.TeardownAsync` | **Added in v0.2.1**. Remove and stop there (the database at the `-f` connection target plus the database of the same name on every Citus worker). exit 2 together with `--clean`. No short form |
| `yes` | **`--yes`** `-y` | ❌ | ❌ | false | bool | mkfs's `Initializer.ConfirmTeardownAsync` | **Added in v0.2.1**. Skips the confirmation of `--clean` / `--purge`. When non-interactive and it is not given, exit 3 |
| `now` | **`--now`** | ❌ | ❌ | false | bool | mkfs's `Initializer.WaitUntilNoConnectionsAsync` | **Added in v0.2.1**. Disconnects what is connected (live mounts / other connections) without waiting |

## 2. `setting.*` - where the settings file itself lives

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `setting.file` | **`-f`** `--setting` `--setting-file` plus **positional[0] (when it is not `postgresql:`)** | ❌ | ❌ | `pgfs.toml` | string | At the start of `LoadFromFile` | It also takes positional[0] when that does not start with `postgresql:`. The short form `-f` is **only effective on a direct invocation** (in a helper context it is silently swallowed as `mount(8)`'s `--fake`; see the short-form collisions in [docs/fstab-support.md](fstab-support.md)) |
| `setting.search_path` | `--setting-path` `--setting-search-path` `--setting-file-path` `--setting-file-search-path` | ❌ | ❌ | `.` / `$HOME/.config/pgfs` / `$HOME/.config` / `$HOME` / `$LOCALAPPDATA/pgfs` / `$APPDATA/pgfs` | List&lt;string&gt; | The search in `LoadFromFile` when `setting.file` is not an absolute path | The OS-standard directories |

## 3. `logging.*`

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `logging.level` | `--log-level` `--log-min-level` `--min-log-level` | ✅ | ❌ | `Information` | `Level.Enum` | Applied to `Logger.MinLevel` at startup and on a Live set | `all` `trace` `debug` `information` `warning` `error` `critical` `none` |
| `logging.output` | `--log-output` | ✅ | ❌ | `stderr` | `SettingLoggingOutput` | Applied to `Logger.Output` through [LogSink.Configure](../../src/core/src/Logging/LogSink.cs) at startup and on a Live set | `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`. The cycle is `none\|hourly\|daily\|monthly`, a `*` in the pattern expands to the date (hourly=`yyyyMMddHH` / daily=`yyyyMMdd` / monthly=`yyyyMM` / none=empty) and `~` expands to the home directory. For example `daily:~/pgfs/log/pgfs-*.log`. **A Warning or above also goes to stderr in a terse form (`pgfs: [Warning] ...`, with no timestamp) unless the effective sink is already stderr** (so that a warning is not missed with a file or stdout sink). Likewise **the startup banner (the program name plus the version plus the copyright) and the process liveness markers (`started` / `exited`) always go to stderr** (`Logger.Lifecycle`, so that the start and the exit can be followed even when the log is a file). The interpreted parameters (`param: scope.key = value`, with the Password of a connection string masked) and the warnings about unknown options are emitted at startup too |

## 4. `mount.*`

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `mount.mount_point` | **`-m`** `--mount-point` plus **positional[1]** | ✅ | ❌ | `/mnt/pgfs` (Linux/macOS) / `P:` (Windows) | string | Just before entering the FUSE loop in `mount.pgfs Program.Main`, and when DokanNet starts in `assign.pgfs` | |
| `mount.max_write` | `--max-write` | ✅ | ❌ | `0` | int | The maximum bytes of one FUSE WRITE request (`fuse_conn_info.max_write`). `0` = left to libfuse's negotiation | mount (FUSE) only. **Measured, libfuse3 negotiates up to the kernel limit of 1 MiB by default**, so this is the knob for lowering it or pinning it across versions. `-o max_write` is refused by libfuse3, so it is set in init. See [performance.md](performance.md) |
| `mount.cache_max_entries` | `--cache-max-entries` | ✅ | ❌ | 1024 | int | The metadata ceiling of the `InodeCache` (byId is authoritative; going over evicts by LRU and cascades the cleanup of byPath and childrenByParent) | The count ceiling for the inode metadata. The details are in [cache.md, 1a](cache.md) |
| `mount.cache_data_max_bytes` | `--cache-data-max-bytes` | ✅ | ❌ | 67108864 (64 MiB) | long | The byte budget of the `ContentCache`'s read cache for the bodies (going over evicts by LRU). `0` disables it | The in-memory read cache of the file bodies (`data_chunk`). The details are in [cache.md, 1b](cache.md) |
| `mount.negative_cache_ttl_ms` | `--negative-cache-ttl-ms` | ✅ | ❌ | 0 (disabled) | int | Caches a negative lookup (ENOENT) for the TTL to save a database round trip. `0` disables it | This client's own create/rename invalidates it at once (safe for a single client). **Another client's new file is invisible for up to the TTL** (with `notify_enabled` the notification invalidates it at once). The measurements are in [performance.md, the negative lookup cache](performance.md) |
| `mount.write_back` | `--write-back` | ✅ | ❌ | false | bool | Accumulates the writes in memory and flushes them **one file = one transaction** (triggered by an `fsync` / a `close` / the time / the dirty ceiling / the unmount) | Measured at **6.1x with `dd bs=128k` and 1.4x with rsync** (Citus rf=2). Removing the amplification is the substance. **Whatever is unflushed is lost in a crash**, so it is off by default. The details are in [write-back.md](write-back.md) |
| `mount.write_back_max_bytes` | `--write-back-max-bytes` | ✅ | ❌ | 67108864 (64 MiB) | long | The dirty-byte threshold at which a flush starts. Going over it makes the writing side attempt a flush (it does not guarantee staying under the ceiling if that fails) | **A separate account** from `cache_data_max_bytes` (dirty data is not subject to LRU eviction). It only means anything when `write_back` is enabled |
| `mount.write_back_interval_ms` | `--write-back-interval-ms` | ✅ | ❌ | 1000 | int | A file that has stayed dirty longer than this is flushed in the background. `0` disables the time trigger | It is the interval at which a background flush is attempted, not an upper bound on the loss window when the database fails or there is contention. It only means anything when `write_back` is enabled |
| `mount.write_back_metadata` | `--write-back-metadata` | ✅ | ❌ | false | bool | The metadata (`create`/`mkdir`/`symlink` plus the attribute changes and renames **against those**) also accumulates as pending and is flushed **one file = one tx** | **It requires `mount.write_back = true`** (turning it on alone gives a warning and is disabled). A metadata operation against a persisted inode stays write-through (its body is the data write-back's business). Measured at rsync 1.00x and a combined 1.30x for a non-O_EXCL create (**a value from before the A-10 fix**). The review findings (A-1 to A-10 and B-1 to B-13) were all addressed; it is off by default. The details are in [metadata-write-back.md](metadata-write-back.md) |
| `mount.write_back_metadata_exclusive_create` | `--write-back-metadata-exclusive-create` | ✅ | ❌ | `write_through` | enum (`write_through` / `defer`) | Whether a create with `O_EXCL` / `CREATE_NEW` becomes pending. With `defer`, 1e's folding helps `rsync` too (measured at **3.28x**) | **`defer` loses the cross-client exclusion** (another mount cannot see a pending entry, so an exclusive create from two clients both succeed). The exclusion within one mount is kept by the ledger. The loser of a collision error-latches at the flush and does not remove the occupier. It is for a bulk copy known to be single-client operation. It only means anything when `write_back_metadata` is enabled |
| `mount.write_back_max_inodes` | `--write-back-max-inodes` | ✅ | ❌ | 4096 | int | The pending-inode-count threshold at which a flush starts. Going over it makes the creating side attempt a flush (it can go over the threshold if that fails) | With many small files the count grows before the bytes do, so it is bounded by count separately from `write_back_max_bytes`. It only means anything when `write_back_metadata` is enabled |
| `mount.write_back_flush_timeout_ms` | `--write-back-flush-timeout-ms` | ✅ | ❌ | 30000 | int | The retry deadline for the back-pressure and the unmount. **The deadline is checked inside one sweep too**, but a database call in flight cannot be aborted so it is not a strict time limit | `0` = do not wait (try one round and carry on = the behaviour up to 1d). A back-pressure that goes past the deadline warns and carries on. Anything left at unmount time is emitted in an Error log, up to 32 entries per kind, and the real mount process of `mount.pgfs` exits with **exit 4** (not the exit code of the daemonizing parent) (`fusermount3 -u` completes on the kernel side, so the FS cannot refuse it with EBUSY). It only means anything when `write_back` is enabled |
| `mount.self_uname` | `--self-uname` | ✅ | ❌ | `""` (unset) | string | When the `UserResolver` / `WindowsUserResolver` is built (NextMount) | Since v0.2.1. **The name this client calls itself (a supplement)**: this name is given to what the user of the process running the mount creates (overriding whether or not the name can be looked up). It is not used for the requests of other users. This name is shown as one's own uid / SID. Part of the role of the old `mount.fallback_uname` (retired) |
| `mount.self_gname` | `--self-gname` | ✅ | ❌ | `""` (unset) | string | As above | Since v0.2.1. The group version of the name this client calls itself. When set, the gname of what it creates becomes this (it takes precedence over the inheritance from the parent on Windows / the primary group on Linux) |
| `mount.foreground` | **`--foreground`** | ❌ | ❌ | false | bool | The child-process separation decision of `mount.pgfs Program.Main` | true pins it to the foreground and false daemonizes it. It deliberately has no short form `-f` because of the collision with setting.file and the MountHelperFlagsNoValue (see the known problems) |

## 5. `file_system.*` - frozen at mkfs time, read-only afterwards

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `file_system.version` | `--fs-version` (renamed from `--version` in v0.2.1) | ❌ | ✅ | `1.0.0` | string | (Nothing reads it in the code today) | For freezing at mkfs time. Room for a future compatibility check |
| `file_system.volume_label` | `--volume-label` | ❌ | ✅ | `pgfs` | string | `assign.pgfs`'s `GetVolumeInformation` | The drive name on Windows |
| `file_system.unknown_name` | (none; effectively fixed) | ❌ | ✅ | `(unknown)` | string | Put in by mkfs at creation / at mount and assign startup (Format) | Since v0.2.1. The name written to the database when the creator's name is unknown (the requester cannot be obtained on Windows / a SID or uid other than one's own cannot be looked up as a name). Both the uname and the gname. It is a name that does not exist on the host, so the OS screens show it as the overflowuid / a well-known SID. The plan is to make it settable from mkfs / pgfsctl eventually |
| `file_system.root_access` | `--root-access` | ❌ | ❌ | `owner` | enum (`owner` / `everyone`) | mkfs's `Initializer.InsertRootInodeAsync` (**only when the root is newly created**) | Since v0.2.1. `owner` = `root:root 0755` / `everyone` = `root:root 1777` (sticky). An mkfs-only action (SaveTo=None). It has no effect on an existing root and prints a Warning |
| `file_system.cluster_size` | `--cluster-size` | ❌ | ✅ | 4096 | long | `Api.StatFs` (`f_bsize`) | In bytes. **Made database-authoritative** (SaveTo went File -> Db), because a value differing between clients causes accidents. It is not written into the generated toml |
| `file_system.default_chunk_size` | `--default-chunk-size` | ❌ | ✅ | 1048576 (1 MiB) | long | (The `chunk_size` column of pgfs_data is the truth for the chunk size today, so the setting is used only as the initial value when a new data row is created) | **Made database-authoritative.** A differing value can corrupt data through a disagreement over the chunk boundaries |
| `file_system.max_file_size` | `--max-file-size` | ❌ | ✅ | 1099511627776 (1 TiB) | long | `Api.StatFs` (`f_blocks`) and others | **Made database-authoritative** |

## 6. `database.*`

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `database.connection` | **`-c`** `--connection` `--connection-string` plus **positional[0] (when it starts with `postgresql:`)** | ✅ | ❌ | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` | `NpgsqlConnectionStringBuilder` (parsed and formatted by `ConnectionField`) | The database access of every process | Mandatory for mkfs, mount and assign alike |
| `database.super_connection` | `-su` `--su` `--super` `--super-connection` `--super-connection-string` `--super-user` `--super-user-connection` `--super-user-connection-string` | ❌ | ❌ | `Host=localhost;...Username=postgres;Password=postgres;Database=template1;...` | As above | Creating the DB / ROLE / EXTENSION in mkfs's `Initializer` | It is `SaveTo=None` and is written neither to a file nor to the database (for the safety of the credentials). **When `--super` is not given explicitly, the Host/Port/SslMode of `database.connection` are inherited** so that super and user point at the same server (preventing the accident where the user points at a remote server while super DROPs/CREATEs a different database on localhost). The super credentials and the maintenance database stay at the defaults (postgres / template1). When it is given explicitly, that value is respected entirely |
| `database.schema` | **`-s`** `--schema` `--schema-name` | ✅ | ❌ | `public` | string | Qualifying every SQL statement in `Api` | The short form `-s` is **only effective on a direct invocation** (in a helper context it is silently swallowed as `mount(8)`'s `--sloppy`; see the short-form collisions in [docs/fstab-support.md](fstab-support.md)) |
| `database.prefix` | **`-x`** `--prefix` `--table-prefix` `--table-name-prefix` | ✅ | ❌ | `pgfs_` | string | Generating the table names (`pgfs_inode` and so on) | The trailing `_` is normalized through `Database.GetPrefix()` |
| `database.tablespace` | `--tablespace` `--tablespace-name` | ❌ | ❌ | `pg_default` | string | mkfs's `Initializer.EnsureTablespaceAsync` (the coordinator plus every worker) | **It can be customized on Citus too**. The per-table clause was dropped in favour of inheriting through `CREATE DATABASE WITH TABLESPACE`. **Made database-authoritative** (it is FS identity information that should not be rewritten from a settings file; it is not written into the generated toml). **Not stored in the database since v0.2.1** (an instruction only for creation; on an existing FS it is taken from what is actually in the database). **Giving `--citus` on an existing database does not make it Citus if it is not** (a Warning; v0.2.1) |
| `database.tablespace_path` | `--tablespace-path` | ❌ | ❌ | `""` | string | mkfs's `Initializer.EnsureTablespaceAsync` | An empty string means it is not created. When it is given and `app.plperlu` allows it, plperlu auto-mkdirs it (owned by postgres, 0700). **Made database-authoritative**. **Not stored in the database since v0.2.1** (an instruction only for creation; on an existing FS it is taken from what is actually in the database) |
| `database.retry_max_attempts` | `--retry-max-attempts` | ✅ | ❌ | 5 | int | `Retry.Configure` at startup and on a Live set | This setting is for retrying the connection open. create/write/flush have their own limited tx retry for 40P01/40001 |
| `database.retry_initial_delay_ms` | `--retry-initial-delay-ms` | ✅ | ❌ | 200 | int | As above | The initial value of the exponential backoff |
| `database.retry_max_delay_ms` | `--retry-max-delay-ms` | ✅ | ❌ | 2000 | int | As above | The backoff ceiling |
| `database.notify_enabled` | `--notify` `--notify-enabled` / the negation `--no-notify` | ✅ | ❌ | **`true`** (since v0.2.1; `false` up to v0.2.0) | bool | Selects sending and receiving the data-change notifications at startup (the control LISTEN is always started) | The notification of another client's changes (LISTEN/NOTIFY). It is effectively mandatory with several mounts, so the default was made on. To save the one LISTEN connection and the `pg_notify` when running a single mount, use `--no-notify`. Given to mkfs, it is written into the toml for distribution. The details are in [docs/Mount.md](../Mount.md) / [docs/Assign.md](../Assign.md) |
| `database.citus` | **`--citus`** | ❌ | ❌ | `false` | bool | Whether mkfs's `Initializer` registers the PGFS tables as Citus-distributed | **Made database-authoritative** (SaveTo went File -> Db). A bool for confirming after the fact that "this FS has been made Citus". mount and assign do not branch on it. The details are in [docs/support_for_citus.md](support_for_citus.md). **Not stored in the database since v0.2.1** (an instruction only for creation; on an existing FS it is taken from what is actually in the database) |
| `database.workers` | `-w` `--worker` `--workers` | ✅ | ❌ | An empty list | List&lt;string&gt; (a Field) | The Citus worker bootstrap of mkfs | `host[:port],...`. It is not a live-change target for mount or assign. **It only takes effect when the database is created** (v0.2.1; given for an existing database it prints a Warning, and no roles / tablespaces are created on the workers either) |
| `database.shard_count` | `--shard-count` | ❌ | ❌ | `0` | int | mkfs sets `citus.shard_count` to this before `create_distributed_table` (`0` = follow the cluster default) | mkfs only. It is a session GUC, so it does not affect other applications sharing the same database. The details are in [support_for_citus.md](support_for_citus.md). **Not stored in the database since v0.2.1** (an instruction only for creation; on an existing FS it is taken from what is actually in the database) |
| `database.shard_replication_factor` | `--shard-replication-factor`, `--rf` | ❌ | ❌ | `0` | int | How many nodes one shard is placed on (`0` = follow the cluster default) | mkfs only. **It is a choice about storage redundancy** and does not affect the exclusion (`{prefix}lock` is not distributed). **Not stored in the database since v0.2.1** (an instruction only for creation; on an existing FS it is taken from what is actually in the database) |
| `database.distribute_existing` | `--distribute-existing` | ❌ | ❌ | `false` | bool | Makes existing tables Citus too when used with `--citus` | An mkfs-only action flag (SaveTo=None). By default only newly created tables are made Citus |

## 7. `audit.*` - the audit log

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `audit.enabled` | **`--audit`** | ❌ | ✅ | `false` | bool | Initialized by mkfs, read at startup plus a Live set/reload | Records the metadata changes in `{prefix}audit`. It is Db + Live and can be changed with `pgfsctl config set audit.enabled true/false`. The details are in [docs/audit-log.md](audit-log.md) |

## 8. `app.*` - the application behaviour (the df mode / the plperlu gate)

| The key | The CLI | TOML | DB | The default | The type | When it is read | Notes |
|---|---|---|---|---|---|---|---|
| `app.statfs` | **`--statfs`** `--statfs-mode` | ❌ | ✅ | `auto` | string (`auto`/`require`/`nominal`) | Used by mkfs to decide whether to create or remove `{prefix}statfs()` (plperlu), plus saved in `pgfs_settings`. `Api.GetStatFs` skips the server query when it is `nominal` | **The scope and key were moved from `statfs.mode` to `pgfs.statfs` to `app.statfs`** (the C# stays `Schema.Statfs.Mode`). The mode in which `df` returns the real free space. `auto` = measured if plperlu is there and nominal if not, `require` = plperlu is mandatory (mkfs fails without it), `nominal` = always the nominal capacity. Whether plperlu may be used is gated higher up by `app.plperlu`. The details are in [docs/df-support.md](df-support.md) / [docs/settings-and-plperlu.md](settings-and-plperlu.md) |
| `app.plperlu` | **`--plperlu [true\|false]`** `--allow-plperlu` (bare) / `--deny-plperlu` (bare, the negation) | ❌ | ✅ | `true` | bool | The higher gate on whether mkfs may use plperlu for the statfs measurement function and the tablespace auto-mkdir | Allowing the untrusted plperlu. `require` plus `deny` is a contradiction and an mkfs error. auto plus deny is equivalent to nominal. The matrix is in [docs/settings-and-plperlu.md](settings-and-plperlu.md) |
| `app.enforce_permissions` | (none; `pgfsctl config set`) | ❌ | ✅ | `true` | bool | assign's `CreateFile` / `MoveFile` ([FileSystem.Access.cs](../../src/dokan/src/FileSystem.Access.cs)) | **Added in v0.2.1**. Whether Windows (Dokan) evaluates the POSIX permissions (the mode plus the canonical ACL). Dokan does not evaluate with the SD, so assign looks at them itself. `false` is the same "no evaluation" as v0.2.0. **It has no effect on Linux** (the kernel's evaluation through `default_permissions`). mkfs writes no row (the default `true` when there is none). The design is in [permission-interop.md, the decision on Windows](permission-interop.md) |

---

## The reload policy (Live / NextMount / Format)

Each `Field` holds a `Field.Reload` (an `enum ReloadPolicy`) that decides **how a `pgfsctl config set` is
applied while running** (the source of truth for the design is
[control-plane.md, the settled Phase 3 design](control-plane.md)). This is the single truth of "can it be
changed while mounted".

| The kind | What it means | The fields |
|---|---|---|
| **Live** | Applied to a running mount at once (`config set`) | `logging.level` / `logging.output` / `database.retry_max_attempts` / `database.retry_initial_delay_ms` / `database.retry_max_delay_ms` / `mount.cache_max_entries` / `mount.cache_data_max_bytes` / `mount.negative_cache_ttl_ms` / `mount.write_back` / `mount.write_back_max_bytes` / `mount.write_back_interval_ms` / `mount.write_back_metadata` / `mount.write_back_max_inodes` / `mount.write_back_flush_timeout_ms` / `mount.write_back_metadata_exclusive_create` / `app.statfs` / `app.enforce_permissions` / `audit.enabled` |
| **NextMount** (the default) | Applied on a remount | `mount.mount_point` / `mount.max_write` / `mount.self_uname` / `mount.self_gname` / `database.connection` / `database.schema` / `database.prefix` / `database.notify_enabled` / `app.plperlu` (the creation-only instructions `database.tablespace` / `tablespace_path` / `citus` / `workers` / `shard_count` / `shard_replication_factor` are not stored, so they are outside reload; since v0.2.1) |
| **Format** | mkfs only; immutable afterwards | `file_system.version` / `volume_label` / `cluster_size` / `default_chunk_size` / `max_file_size` |

> **This table lists only the fields with `SaveTo != None`.** **The 7 that are CLI-only (`SaveTo=None`)** -
> `help` / `clean` / `setting.file` / `setting.search_path` / `mount.foreground` /
> `database.super_connection` / `database.distribute_existing` - **are stored neither in a settings file nor in
> the database**, so the question "can it be changed while running" does not arise for them.
> **Cross-checked against all 44 fields, those 7 being absent from the table is intentional** (confirmed by a
> mechanical comparison on 2026-09-21. The same comparison gives the same 7, so it is written down here).

How a `config set` is applied branches on `(SaveTo, Reload)` (the matrix is in control-plane.md):

- **Db + Live** (`audit.enabled` / `app.statfs` / `app.enforce_permissions`): written to `pgfs_settings`
  (persisted) plus a `set` NOTIFY
  (live at once).
- **File + Live** (logging / cache / retry / write-back): the `set` NOTIFY only = **an ephemeral live
  application** (no database row is created; to persist it, edit your own `pgfs.toml`).
- **Db + NextMount**: written to `pgfs_settings` (applied at the next mount).
- **File + NextMount**: a `config set` is not possible (a remote toml cannot be touched) -> it directs you to
  edit your own `pgfs.toml`.
- **Format / None**: a `config set` is refused.

> The control messages arrive regardless of `notify_enabled` (Phase 3's P3-0: the control LISTEN is always on
> and `notify_enabled` gates only the data-change notifications).

## The aggregation by lifecycle

### "The settings frozen at mkfs time" (= fixed when the filesystem is created)

- `file_system.version`
- `file_system.volume_label`
- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`
- `database.tablespace` / `database.tablespace_path` / `database.prefix` / `database.schema`

The values of `audit.enabled` and `app.statfs` can be changed, being Db + Live. But creating and removing the
server-side statfs function is mkfs's business, and a Live set runs no DDL. The tablespace, the schema and so on
are not Format but are not settings that restructure an existing FS either.
- `database.citus` (stored in the database, `--citus`. It went File -> Db)
- `app.plperlu` (stored in the database, `--plperlu` / `--allow-plperlu` / `--deny-plperlu`. The plperlu
  permission gate)
- **The database-authoritative FS size settings**: `file_system.cluster_size` / `default_chunk_size` /
  `max_file_size` were also moved into the database (see section 5 above; they are not written
  into the generated toml)

Rewriting these after mkfs gives no guarantee that a later mount obeys them (especially where a database column
such as `pgfs_data.chunk_size` is the truth).

### "Read when the mount starts; how it is treated afterwards depends on the reload policy"

They are all resolved into the `RootConfig` at startup, but their mutability while running follows the reload
policy above:

- **NextMount (applied on a remount)**: `mount.mount_point` / `mount.foreground` / `database.connection`
- **Live (applied while running through a `config set`)**: `logging.level` / `logging.output` /
  `database.retry_*` / `mount.cache_max_entries` / `mount.cache_data_max_bytes` (see the table above for every
  Live item)
- `mount.fallback_uname` / `mount.fallback_gname` were **retired in v0.2.1** (giving them prints a dedicated
  Warning). A name that does not exist on this host is shown as the OS's overflowuid / overflowgid (on Windows
  `ANONYMOUS LOGON`), and when the name is unknown, `file_system.unknown_name` is written to the database

-> Before Phase 3 (control-plane.md) the premise was "there is no setting that is rewritten while mounted", but
**the introduction of `pgfsctl config set` plus the Live reload means the Live items are rewritten while
running** (the corresponding properties of the `RootConfig` are mutable and `Api.ApplySingleLive` replaces
them). NextMount and Format are immutable as before.

### "Read only at mkfs time and not persisted"

- `clean` (the CLI)
- `database.super_connection` (only at mkfs time; the credentials are not saved)
- `help`

---

## The known problems

### `-f` and `-s` being silently swallowed in a helper context

The internal flags of the `mount(8)` helper (`-i` `-f` `-n` `-s` `-v` `-N <ns>` `-t <type>`) are silently
skipped at the head of `ParseArguments` **only when it was started in a helper context** (changed to be
context-dependent). The decision is the AND of "the parent process's comm is `mount`" and "there
are positional arguments". On a direct invocation that swallowing does not apply, and `-f` is the short form of
`setting.file` while `-s` is the short form of `database.schema`.

For the details (the list of every short form, the decision logic and the processing order) see the sections on
the short-form collisions and on the processing order of ParseArguments in
[docs/fstab-support.md](fstab-support.md).

`mount.foreground` had `-f` removed from its Options, so it is not part of the collision
(`--foreground` only). `-f` is the short form **for setting.file only**.

### Persisting the value of `database.super_connection`

Being `SaveTo=None`, it is discarded after mkfs and has to be given again on a re-mkfs. That is deliberate (for
the safety of the credentials).

### How `file_system.default_chunk_size` is used

The chunk operations of `Api` take the `pgfs_data.chunk_size` column (= the value saved on each data row) as the
truth. The setting is used only as the initial value when a new `data` row is INSERTed, and it does not affect
existing files.
