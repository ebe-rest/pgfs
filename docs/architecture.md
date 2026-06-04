# Architecture

A document collecting the solution layout, dependency packages, the file structure inside Lib, and the build/run procedures of pgfs.

## Solution layout

The solution [pgfs.sln](../pgfs.sln) has 4 projects.

| Project | Path | Role | Platform |
|---|---|---|---|
| **Lib** | [src/lib/](../src/lib/) | core library (Models / Api / Logging / Collections / Utility / Objects) | cross-platform |
| **Mkfs** | [src/mkfs/](../src/mkfs/) | CLI that initializes the PostgreSQL-side tables etc. | cross-platform |
| **Mount** | [src/mount/](../src/mount/) | mount tool for Linux / macOS (uses Tmds.Fuse) | Linux / macOS |
| **Assign** | [src/assign/](../src/assign/) | mount tool for Windows (uses DokanNet) | Windows |

All TargetFrameworks are **net10.0**. `PublishAot` / `PublishTrimmed` are enabled (except for Lib). `ImplicitUsings` and `Nullable` are enabled. Per-OS `DefineConstants` (`WINDOWS` / `LINUX` / `MACOS`) are defined.

### Namespaces

- root: `Pgfs.*`
- library: `Pgfs.Lib.{Api, Models, Logging, Collections, Objects, Utility}`
- executables: `Pgfs.Mkfs`, `Pgfs.Mount`, `Pgfs.Assign`

### Output location

All projects build into the same directory under [bin/](../bin/) (`<BaseOutputPath>$(SolutionDir)bin\</BaseOutputPath>`).

### Assembly names

Namespaces are PascalCase such as `Pgfs.Lib`, `Pgfs.Mkfs`. The output assembly names are unified to **lowercase + dot-separated**: `lib.pgfs.dll`, `mkfs.pgfs.{dll,exe}`, `mount.pgfs.{dll,exe}`, `assign.pgfs.{dll,exe}`. This is set in the csproj via `<AssemblyName>`.

---

## Dependency packages

| Package | Use | Used by |
|---|---|---|
| `Npgsql` 9.0.4 | PostgreSQL client | all projects |
| `Dapper` 2.1.66 | lightweight ORM | Lib |
| `Tomlyn` 0.20.0 | TOML configuration file | Lib |
| `Tmds.Fuse` (fork) | Linux/macOS FUSE | Mount |
| `DokanNet` 2.3.0.3 | Windows DokanNet | Assign |

`Tmds.Fuse` is not upstream `tmds/Tmds.Fuse 0.1.0-190711-50`, but the **`securefolderfs-community/Tmds.Fuse` fork**, held as a submodule in [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) and referenced via `ProjectReference` (for the added `MountOptions.Options` and the `use_ino` downstream patch). See [fstab-support.md §Adopting the Tmds.Fuse fork](fstab-support.md) for the background.

---

## File structure (Lib)

### Models ([src/lib/src/Models/](../src/lib/src/Models/))

Where the entity POCOs corresponding to one DB row live. The configuration models live in [Config](#config-srclibsrcconfig).

**DB entities**: `Base.cs`, `Inode.cs`, `Data.cs`, `Chunk.cs`
- the audit fields (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`) are shared in `Base` (it exposes both lowercase and uppercase properties for Dapper)
- `Inode.cs` / `Data.cs` / `Chunk.cs` are the POCOs representing one row of `pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` respectively

**Audit / ACL models**: `AuditOp.cs`, `AuditContext.cs`, `PgfsAcl.cs`, `PosixAcl.cs`
- `AuditOp.cs` (op string constants) / `AuditContext.cs` (caller ambient context) — for the audit log ([audit-log.md](audit-log.md))
- `PgfsAcl.cs` the canonical ACL document (the JSON of the `user.pgfs_acl` xattr: named `entries[]` / `default[]`). The common canonical model bridging Windows DACL ⇔ Linux POSIX ACL
- `PosixAcl.cs` the codec for the Linux `system.posix_acl_access` xattr binary (Parse/Build of the header + entry array). Design in [permission-interop.md](permission-interop.md)

**LoggingOutput-related enums**: `SettingLoggingKind.cs` (Flags: None/Stderr/Stdout/File), `SettingLoggingCycle.cs` (None/Hourly/Daily/Monthly), `SettingLoggingOutput.cs` (a POCO bundling the previous two)
- these stay in Models because [`LoggingOutputField`](../src/lib/src/Config/Field.cs) references them as a type (a possible move target is `Pgfs.Lib.Logging`, but this file describes "the log format", not "the log output itself", so the current location is the safer choice)

### Config ([src/lib/src/Config/](../src/lib/src/Config/))

The successor to the old `Settings` tree. Composed of **static `Field<T>` descriptors + mutable POCOs + `ConfigLoader` (unifying CLI/TOML/DB/Default) + `ConfigStore` (DB I/O)**.

- **`Field<T>` (abstract base + derived types)**: the descriptor for a single setting. It carries Scope / Key / CliOptions / DashOName / SaveTo (`None`/`File`/`Db`) / DefaultFn / Comment, and per type a `Parse(string) → T` / `Format(T) → string`. Derived types: `StringField` / `IntField` / `LongField` / `BoolField` / `StringListField` / `LogLevelField` / `LoggingOutputField` / `ConnectionField`. For help generation it also carries `AppliesTo` (`[Flags] enum Tool`, which tools' `--help` it appears in / default `All`) and `ArgName` (overrides the value placeholder).
- **`Schema` (static class)**: declares every `Field<T>` in nested static classes such as `Schema.Mount.MountPoint`. `Schema.AllFields` enumerates all fields automatically via reflection (a newly added Field is included automatically).
- **`HelpText` (static class)**: generates the `--help` text from `Schema.AllFields` (walking `CliOptions` / `Comment` / type / default value). Each Program (mkfs/mount/assign) passes only an intro line + tool-specific footer prose; the option list is auto-generated. `Field.AppliesTo` selects which options appear per tool (a help-only filter, non-interfering with CLI parsing). The hand-written `ShowHelp` has been retired.
- **POCOs**: `MountConfig` / `DatabaseConfig` / `FileSystemConfig` / `LoggingConfig` / `SettingFileConfig`. Properties are `{ get; set; }` (mutable, in anticipation of future dynamic rewrites such as volume_label). The aggregate `RootConfig` bundles them all + holds `Help` / `Clean` directly.
- **`ConfigLoader`**: assembles a `RootConfig` by unifying CLI / TOML / DB / Default in one pass. The order is **CLI → TOML → DB**, with "skip if a higher source already set it" (priority `CLI > TOML > DB > Default`). The TOML path is searched internally from the CLI-resolved `setting.file`. Only in the `mount(8)` helper context (parent comm = `mount` AND positionals present) does it silently swallow `-i -f -n -s -v -N -t`.
- **`ConfigStore`**: DB read/write of `(scope, key) → value` against the flat `pgfs_settings(scope, key, value)`. `LoadAll` is a simple `SELECT scope, key, value FROM table` full read + allow-list filter. `Save<T>` UPSERTs with `INSERT ... ON CONFLICT (scope, key) DO UPDATE`. The JSON representation goes through `Field<T>.FormatJson(value)` (Int/Long/Bool are native JSON; everything else encodes `Format(value)` as a JSON string).

Persistence convention on change: right after the caller does `config.X.Y = newValue;`, it explicitly calls `store.Save(Schema.X.Y, newValue)` (no setter hook is installed, to keep Loader/Store separated). The concrete pattern will be collected into thin wrappers such as a future `Api.SetVolumeLabel(string)`.

### Api ([src/lib/src/Api/](../src/lib/src/Api/))

- `Api.cs` the public filesystem-operation API (inode CRUD, data I/O via bytea chunks, xattr, symlink, hard link, volume info — all implemented). `IDisposable`; receives an OS notification bridge through the `OsBridge` property.
- **cross-client locking**: the private helpers `LockTargets` / `LockData(dataId)` / `LockInode(inodeId)` / `LockInodes(params long[])` take `SELECT ... FOR UPDATE` row locks on `pgfs_lock`. Each mutating method (`WriteData` / `TruncateData` / `ReleaseData` / `Update{Mode,Owner,Size,Timestamps}` / `Rename` / `DeleteInode` / `CreateHardLink`) takes the appropriate lock at the top, released automatically when the tx ends. Multiple lock acquisition is fixed in ascending target_id order to avoid deadlock. Details in [support_for_citus.md §locking](support_for_citus.md).
- `InodeCache.cs` an in-memory cache of inodes + SELECT/INSERT via Dapper. Searchable by `byId` / `byPath`. The root is fixed at `id = 0`. `TryGetPath(id, out path)` resolves a full path by walking the parent chain (used on the Notify path).
- `Mode.cs` POSIX `st_mode` constants (S_IFDIR, S_IRWXU etc.)
- `NotifyChannel.cs` / `RemoteChangeInfo.cs` notify other clients of changes (active only when `database.notify_enabled=true`). It streams `{inode_ids, parent_ids, path_prefixes}` from the writing client to receivers via PostgreSQL `LISTEN` / `NOTIFY`. The receiver invalidates `InodeCache`, and on Assign additionally asks Explorer to repaint via `DokanInstance.NotifyUpdate`. Mount (Linux) leaves the OS bridge unimplemented due to a constraint of the Tmds.Fuse high-level API (`attr_timeout=0` disables the kernel attr cache + InodeCache invalidation alone makes stat/ls return the latest).

**Data I/O (bytea chunks) implementation note**: each inode's data body is 1 `pgfs_data` row + multiple `pgfs_data_chunk` rows (1 row = 1 bytea = 1 chunk, default chunk_size = 1MB). WriteData completes with one upsert SQL per chunk (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`, the CASE handling the 3 cases of "central overlay / tail overwrite / zero-pad + concat"); concurrent WriteFile races are serialized automatically by the PG row lock. ReadData uses `substring(payload from N for M)` to leverage PG 13+'s partial TOAST detoast. Each chunk's payload length equals "the number of bytes written so far". Zero padding uses `decode(repeat('00', N), 'hex')` (because `repeat(bytea, integer)` does not exist in PG).

### Logging ([src/lib/src/Logging/](../src/lib/src/Logging/))

- An in-house logger implementation (not `Microsoft.Extensions.Logging`).
- A static API via `Logger.Default`. `Level.Enum` is `All/Trace/Debug/Information/Warning/Error/Critical/None`. The output target is `Logger.Output` (`Action<string>`), the minimum level is `Logger.MinLevel`.
- **Applying the output-target config**: each Program.cs sets `Logger.MinLevel = config.Logging.MinLevel` and `Logger.Output = LogSink.Create(config.Logging.Output)` at startup. If `logging.output` is `stdout`/`stderr`/`none`, the respective sink; if of the form `<cycle>:<dir>/<pattern>`, the [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs) (date rotation + `~` home expansion + `*`→date stamp + automatic directory creation + AutoFlush). The default with no config is `stderr`. The conversion is in [LogSink.Create](../src/lib/src/Logging/LogSink.cs).
- **Guard clause mandatory on hot paths**: `Logger.Trace(...)` is `params object?[]`, which allocates an array and boxes. At sites called 1000+/sec, always test first with e.g. `if (Logger.IsTraceEnabled) { Logger.Trace(...); }`. `IsTraceEnabled` / `IsDebugEnabled` / `IsEnabled(level)` are provided.

### Utility ([src/lib/src/Utility/](../src/lib/src/Utility/))

- **`PathParser.cs`** decomposes a path into drive / root / name elements, and can rebuild with a different separator, detect wildcard positions, and insert before/after. Each part is lazily evaluated with `Lazy<>`. A relatively well-built component. **Caution**: `PathParser.FromPath(path)` uses the OS default separator (`Path.DirectorySeparatorChar`). Paths flowing to `Api` / `InodeCache` are always normalized to `/`-separated, so when calling it inside Lib always be explicit with `PathParser.FromPath(path, "/")`.
- **`Pg.cs`** a Dapper + Npgsql wrapper (`Query`, `QueryAsync`, `Execute`, `ExecuteAsync`). Caches an `NpgsqlDataSource` per connection string (the same connection string returns the same data source; pooling is managed inside the data source). `QuoteIdentifier` / `QuoteLiteral` escape. SQL appears in `Logger.Trace` (guarded by an early return on `Logger.IsTraceEnabled` inside `TraceQuery`, skipping `Regex.Replace` / `JsonSerializer.Serialize` when Trace is off). It provides the `Pg.OpenConnection` + `using var tx = conn.BeginTransaction()` pattern and a `Pg.WithTransaction<T>` helper (when dealing with `Span<byte>`, which is a ref struct and cannot be captured in a lambda, use `OpenConnection` directly).
- **`ServiceResolver.cs`** service-name ↔ port conversion via `/etc/services` or Win32 `getservbyname`.
- **`NameNormalizer.cs`** normalizes owner/group/principal names (fullwidth ASCII → halfwidth + domain stripping of `\` and `@` + lowercasing). Applied to stored names, matching, and caller names so both OSes treat case and width identically (see [permission-interop.md](permission-interop.md)).
- `Indexer.cs` (`ReadOnlyIndexer<,>` / `ReadOnlyIndexer<,,>` used by `PathParser`, `Pg`), `Fn.cs`, `String.cs`, `Json.cs`, `Retry.cs` — various helpers.

### Collections ([src/lib/src/Collections/](../src/lib/src/Collections/))

- `RichDictionary<K,V>` a `Dictionary` extension with an `Added` event and `GetOrAdd<W>` derived-type support
- `FirstList<T>` an advanced list built from a linked list of `Memory<T>` segments. Used in `Inode.Children`
- `ComparerToEqualityComparer<T>` used internally by `FirstList`

### Objects ([src/lib/src/Objects/](../src/lib/src/Objects/))

- `Extensions.cs` extensions like `object.To<T>()`, `As<T>()`, `Is<T>()`. Uses the **C# 14 (net10.0) `extension` syntax**.

---

## Configuration file

[pgfs.toml.example](../pgfs.toml.example) is a sample. TOML format, in sections (`[database]` / `[mount]` / `[logging]` / ...). The runtime search path is defined in [`Schema.Setting.SearchPath`](../src/lib/src/Config/Schema.cs) (see [§Config](#config-srclibsrcconfig) above).

---

## Build and run

### Output paths

All projects output under [bin/](../bin/) via the `<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` setting. Debug / Release / Publish are placed separately:

| Command | Output | Contents |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `lib.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + dependency dlls (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | the Release version of the above (`DebugType=embedded`, framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | the 3 **single-file self-contained** executables `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs` (host OS RID auto, ~38 MB each) |

`PublishAot=false` / `PublishTrimmed=false` are set on all projects (AOT is not possible because Dapper / Tomlyn / Tmds.Fuse / DokanNet depend on dynamic code generation). `UseCurrentRuntimeIdentifier` and `SelfContained` are enabled only in a Target with `_IsPublishing=true`, so they do not apply at `dotnet build -c Release` time (to avoid laying out 200 framework dlls at build time).

```pwsh
# build the whole solution
dotnet build pgfs.sln

# build individually
dotnet build src/lib/Lib.csproj
dotnet build src/mkfs/Mkfs.csproj

# Release publish (single-file for the host OS)
dotnet publish src/mkfs/Mkfs.csproj -c Release
# or the whole solution at once
dotnet publish pgfs.sln -c Release
```

**.NET 10 SDK required** (uses the `extension` syntax etc.).

---

## Related

- [Mkfs.md](Mkfs.md) — the `mkfs.pgfs` specification
- [Mount.md](Mount.md) — the `mount.pgfs` (Linux/macOS) specification
- [Assign.md](Assign.md) — the `pgfs.assign` (Windows) specification
- [database.md](database.md) — the DB schema
- [coding-style.md](coding-style.md) — the coding conventions
- [performance.md](performance.md) — performance improvement candidates
- [support_for_citus.md](support_for_citus.md) — the design notes for Citus (horizontal distribution)
- [next.md](next.md) — the list of what to do next
- [history.md](history.md) — the decisions that led to the current design
