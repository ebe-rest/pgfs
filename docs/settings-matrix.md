# Settings matrix

> The authoritative declaration of each setting is collected in [src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs) (the static `Field<T>` descriptor `Schema.<Scope>.<Key>`). This document is a cross-cutting overview of "CLI flag / TOML key / DB storage / default / read timing". **If it disagrees with the code, Schema is authoritative.** A future option is to replace this with generation from Schema.

Related: [docs/Mkfs.md](Mkfs.md) (the mkfs.pgfs CLI list) / [docs/Mount.md](Mount.md) / [docs/Assign.md](Assign.md) / [docs/fstab-support.md](fstab-support.md) (options via `-o key=val,...`)

---

## Legend

- **key**: the hierarchical key in the `pgfs_settings` table / TOML (`scope.key`). Matches the code's `Field.Scope` / `Field.Key`
- **CLI**: the accepted arguments. If there are several, the most common one is bold
- **TOML**: read from `pgfs.toml` (= `SaveTo = File`)
- **DB**: persisted as a `pgfs_settings(scope, key, value)` row (= `SaveTo = Db`)
- **default**: the return of `Field.DefaultFn()`
- **type**: the C# type of the value (the T of `Field<T>`)
- **read timing**: when the code reads it from `RootConfig` (at startup / at mkfs / dynamic)
- **notes**: constraints, dependencies, known pitfalls

## Precedence

[`ConfigLoader`](../src/lib/src/Config/ConfigLoader.cs) unifies CLI / TOML / DB / Default in one pass. The precedence is CLI > TOML > DB > Default. The loading order runs `CLI → TOML → DB`, realized by the "skip if a higher source already set the value" approach. `AssignPositional` / `ParseDashOOptions` also "apply only when not yet loaded", so an explicit `-c` / `-m` / `-f` takes precedence over a positional.

---

## 1. Root (`-` / `--` direct, no parent)

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `help` | `-?` `-h` **`--help`** | ❌ | ❌ | false | bool | right after CLI parse | show usage → exit immediately |
| `clean` | **`--clean`** | ❌ | ❌ | false | bool | top of mkfs `Initializer.cs` | DROP DATABASE → recreate. Intentionally has no short form (to prevent accidental runs) |

## 2. `setting.*` — where the settings file itself lives

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `setting.file` | **`-f`** `--setting` `--setting-file` + **positional[0] (other than postgresql:)** | ❌ | ❌ | `pgfs.toml` | string | at the start of `LoadFromFile` | positional[0] also flows in when it does not start with `postgresql:`. The short form `-f` is **valid only in direct execution** (in helper context it is silently swallowed as `mount(8)`'s `--fake` — [docs/fstab-support.md §Short-form collisions](fstab-support.md#short-form-collisions)) |
| `setting.search_path` | `--setting-path` `--setting-search-path` `--setting-file-path` `--setting-file-search-path` | ❌ | ❌ | `.` / `$HOME/.config/pgfs` / `$HOME/.config` / `$HOME` / `$LOCALAPPDATA/pgfs` / `$APPDATA/pgfs` | List&lt;string&gt; | the search in `LoadFromFile` when `setting.file` is not an absolute path | the OS-standard directory set |

## 3. `logging.*`

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `logging.level` | `--log-level` `--log-min-level` `--min-log-level` | ✅ | ❌ | `Information` | `Level.Enum` | reflected into `Logger.MinLevel` at startup | `all` `trace` `debug` `information` `warning` `error` `critical` `none` |
| `logging.output` | `--log-output` | ✅ | ❌ | `stderr` | `SettingLoggingOutput` | reflected into `Logger.Output` at startup via [LogSink.Configure](../src/lib/src/Logging/LogSink.cs) | `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`. cycle = `none\|hourly\|daily\|monthly`; a `*` in the pattern expands to a date (hourly=`yyyyMMddHH` / daily=`yyyyMMdd` / monthly=`yyyyMM` / none=empty); `~` expands to home. e.g. `daily:~/pgfs/log/pgfs-*.log`. **Warning and above also go to stderr in a terse format (`pgfs: [Warning] ...`, no timestamp) unless the effective sink is already stderr** (so warnings are not missed even with a file/stdout sink). Likewise, **the startup banner (program name + version + Copyright) and the process life/death markers (`started`/`exited`) always go to stderr** (`Logger.Lifecycle`, so startup/shutdown can be tracked even when logs go to a file). At startup it also prints the resolved parameters (`param: scope.key = value`, with the connection-string Password masked) and unknown-option warnings |

## 4. `mount.*`

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `mount.mount_point` | **`-m`** `--mount-point` + **positional[1]** | ✅ | ❌ | `/mnt/pgfs` (Linux/macOS) / `P:` (Windows) | string | just before entering the FUSE loop in `mount.pgfs Program.Main`, at DokanNet startup in `assign.pgfs` | |
| `mount.cache_max_entries` | `--cache-max-entries` | ✅ | ❌ | 1024 | int | `InodeCache` initialization (the `capacity` of the 3 LRUs) | shared by the path cache / id cache / children cache |
| `mount.fallback_uname` | `--fallback-uname` | ❌ | ✅ | `nobody` | string | construction of `UserResolver` / `WindowsUserResolver` | the fallback when the OS cannot resolve a uname. One value for the whole FS (= stored in DB) |
| `mount.fallback_gname` | `--fallback-gname` | ❌ | ✅ | `nogroup` | string | same | |
| `mount.foreground` | **`--foreground`** | ❌ | ❌ | false | bool | the child-process detach decision in `mount.pgfs Program.Main` | true keeps the foreground, false daemonizes. It intentionally has no short form `-f` due to the collision with [setting.file and the mount helper no-value flags](#known-issues) |

## 5. `file_system.*` — frozen at mkfs, readonly afterward

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `file_system.version` | `--version` | ❌ | ✅ | `1.0.0` | string | (no reference in the current code) | for freezing at mkfs. Room for a future compatibility check |
| `file_system.volume_label` | `--volume-label` | ❌ | ✅ | `pgfs` | string | `assign.pgfs`'s `GetVolumeInformation` | the Windows drive name |
| `file_system.cluster_size` | `--cluster-size` | ❌ | ✅ | 4096 | long | `Api.StatFs` (`f_bsize`) | in bytes. **DB-authoritative** (SaveTo=Db), because a mismatch across clients is dangerous. Not written to the generated toml |
| `file_system.default_chunk_size` | `--default-chunk-size` | ❌ | ✅ | 1048576 (1 MiB) | long | (currently chunk_size treats the `chunk_size` column of pgfs_data as authoritative, so the config value is used only as the initial value when a new data row is created) | **DB-authoritative**. A mismatch can corrupt data via inconsistent chunk-boundary interpretation |
| `file_system.max_file_size` | `--max-file-size` | ❌ | ✅ | 1099511627776 (1 TiB) | long | `Api.StatFs` (`f_blocks`) and others | **DB-authoritative** |

## 6. `database.*`

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `database.connection` | **`-c`** `--connection` `--connection-string` + **positional[0] (starting with postgresql:)** | ✅ | ❌ | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` | `DatabaseConnectionSetting` (an `NpgsqlConnectionStringBuilder` wrapper) | DB access in every process | required in all of mkfs / mount / assign |
| `database.super_connection` | `-su` `--su` `--super` `--super-connection` `--super-connection-string` `--super-user` `--super-user-connection` `--super-user-connection-string` | ❌ | ❌ | `Host=localhost;...Username=postgres;Password=postgres;Database=template1;...` | same | mkfs `Initializer`'s DB / ROLE / EXTENSION creation | `SaveTo=None` writes to neither file nor DB (credential safety). **When `--super` is not specified, it inherits the Host/Port/SslMode of `database.connection`**, so super and user point at the same server (preventing the accident where the user points at a remote but super DROP/CREATEs a different localhost DB). The super credentials / maintenance DB stay at their defaults (postgres / template1). When specified, that value is fully respected |
| `database.schema` | **`-s`** `--schema` `--schema-name` | ✅ | ❌ | `public` | string | qualification of all SQL in `Api` | the short form `-s` is **valid only in direct execution** (in helper context it is silently swallowed as `mount(8)`'s `--sloppy` — [docs/fstab-support.md §Short-form collisions](fstab-support.md#short-form-collisions)) |
| `database.prefix` | **`-x`** `--prefix` `--table-prefix` `--table-name-prefix` | ✅ | ❌ | `pgfs_` | string | table-name generation (`pgfs_inode` etc.) | the trailing `_` is normalized via `Database.GetPrefix()` |
| `database.tablespace` | `--tablespace` `--tablespace-name` | ❌ | ✅ | `pg_default` | string | mkfs `Initializer.EnsureTablespaceAsync` (coordinator + every worker) | **Custom allowed even on Citus**. Drops the per-table clause and inherits via `CREATE DATABASE WITH TABLESPACE`. **DB-authoritative** (fs-identifying info not to be overridden via config file; not in the generated toml) |
| `database.tablespace_path` | `--tablespace-path` | ❌ | ✅ | `""` | string | mkfs `Initializer.EnsureTablespaceAsync` | empty string means do not create a new one. When given, plperlu auto-mkdir (owned by postgres 0700) if `app.plperlu` is allowed. **DB-authoritative** |
| `database.retry_max_attempts` | `--retry-max-attempts` | ✅ | ❌ | 5 | int | `Retry.Configure` in the `Api` ctor | retries only on connection open (not during query execution) |
| `database.retry_initial_delay_ms` | `--retry-initial-delay-ms` | ✅ | ❌ | 200 | int | same | the initial value of the exponential backoff |
| `database.retry_max_delay_ms` | `--retry-max-delay-ms` | ✅ | ❌ | 2000 | int | same | the backoff ceiling |
| `database.notify_enabled` | `--notify` `--notify-enabled` | ✅ | ❌ | `false` | bool | starts `NotifyChannel` in the `Api` ctor | notify other clients of changes (LISTEN/NOTIFY). Recommended OFF for single-client use. Details in [docs/Mount.md](Mount.md) / [docs/Assign.md](Assign.md) |
| `database.citus` | **`--citus`** | ❌ | ✅ | `false` | bool | whether mkfs `Initializer` registers the PGFS tables as Citus distributed tables | **DB-authoritative** (SaveTo=Db). A bool to check after the fact that "this FS is Citus-ified". mount/assign do not branch on it. Details in [docs/support_for_citus.md](support_for_citus.md) |

## 8. `audit.*` — audit log

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `audit.enabled` | **`--audit`** | ❌ | ✅ | `false` | bool | mkfs stores it into `pgfs_settings` → the `Api` ctor reads it and enables the hook on each mutating operation | records metadata changes into `{prefix}audit`. The same "frozen at mkfs, stored in DB" item as `mount.fallback_*`. Details in [docs/audit-log.md](audit-log.md) |

## 9. `app.*` — application behavior (df mode / plperlu gate)

| Key | CLI | TOML | DB | Default | Type | Read timing | Notes |
|---|---|---|---|---|---|---|---|
| `app.statfs` | **`--statfs`** `--statfs-mode` | ❌ | ✅ | `auto` | string (`auto`/`require`/`nominal`) | used by mkfs to branch on creating/dropping `{prefix}statfs()` (plperlu) + stored in `pgfs_settings`. `Api.GetStatFs` skips the server query when `nominal` | C# reference is `Schema.Statfs.Mode`. The mode in which `df` reports real disk free space. `auto`=measure if plperlu present / nominal otherwise, `require`=plperlu required (mkfs fails if absent), `nominal`=always nominal capacity. `app.plperlu` is the upper gate for whether plperlu may be used. See [docs/df-support.md](df-support.md) / [docs/settings-and-plperlu.md](settings-and-plperlu.md) |
| `app.plperlu` | **`--plperlu [true\|false]`** `--allow-plperlu` (bare) / `--deny-plperlu` (bare, negated) | ❌ | ✅ | `true` | bool | the upper gate for whether mkfs may use plperlu for the statfs measuring functions / tablespace auto-mkdir | permission for untrusted plperlu. `require`+`deny` is a contradiction → mkfs error. auto+deny is equivalent to nominal. The matrix is in [docs/settings-and-plperlu.md](settings-and-plperlu.md) |

---

## Aggregation by lifecycle

### "Settings frozen at mkfs" (= fixed at filesystem-creation time)

- `file_system.version`
- `file_system.volume_label`
- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`
- `database.tablespace` / `database.tablespace_path` / `database.prefix` / `database.schema`
- `audit.enabled` (stored in DB, `--audit`; a management tool to toggle it is planned)
- `app.statfs` (stored in DB, `--statfs`; freezes at mkfs whether the `{prefix}statfs()` functions exist)
- `database.citus` (stored in DB, `--citus`)
- `app.plperlu` (stored in DB, `--plperlu`/`--allow-plperlu`/`--deny-plperlu`; the plperlu permission gate)
- **DB-authoritative FS size keys**: `file_system.cluster_size` / `default_chunk_size` / `max_file_size` are also DB-stored (see §5 above; not written to the generated toml)

Even if rewritten after mkfs, a subsequent mount is not guaranteed to follow them (especially when a DB-side column such as `pgfs_data.chunk_size` is authoritative).

### "Read at mount startup, immutable afterward"

- `mount.mount_point`
- `mount.cache_max_entries`
- `mount.fallback_uname` / `mount.fallback_gname`
- `mount.foreground`
- `logging.level` / `logging.output`
- `database.connection`
- `database.retry_*`

→ **In the current code, there is no setting that is rewritten during a mount.** This is the rationale for deciding that immutable POCOs suffice in the config model.

### "Referenced only at mkfs, not persisted"

- `clean` (CLI)
- `database.super_connection` (only at mkfs; credentials are not stored)
- `help`

---

## Known issues

### Silent swallowing of `-f` / `-s` in helper context

The `mount(8)` helper's internal flags (`-i` `-f` `-n` `-s` `-v` `-N <ns>` `-t <type>`) are silently skipped at the start of `ParseArguments` **only when launched in helper context** (context-dependent). The decision is the AND condition "the parent process comm is `mount` and positional arguments are present". In direct execution this swallowing does not apply, and `-f` is valid as the short form of `setting.file` and `-s` as that of `database.schema`.

For details (the full short-form list, the decision logic, the processing order), see [docs/fstab-support.md §Short-form collisions](fstab-support.md#short-form-collisions) and [§`ConfigLoader.ParseCli` processing order](fstab-support.md#configloaderparsecli-processing-order).

`mount.foreground` is not subject to the collision because `-f` is removed from its Options (`--foreground` only). `-f` is the short form **dedicated to setting.file**.

### Persistence of the `database.super_connection` value

Because of `SaveTo=None`, it is discarded after mkfs and must be specified again on a re-mkfs. This is intentional (credential safety).

### How `file_system.default_chunk_size` is used

The chunk operations in `Api` treat the `pgfs_data.chunk_size` column (= the value stored on each data row) as authoritative. The config value is used only as the initial value when INSERTing a new `data` row, and does not affect existing files.
