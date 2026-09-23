# The architecture

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **the structure of the project** (the split into Core /
> Fuse / Dokan plus the thin executables, the file structure inside Core, the dependencies between the
> assemblies) and **how to build and run it**.
> The first page to look at when finding "which code is where".
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [Mkfs.md](Mkfs.md) / [Mount.md](Mount.md) / [Assign.md](Assign.md) / [Pgfsctl.md](Pgfsctl.md) | **The specification** of each CLI (the options and the behaviour) |
> | [design/database.md](design/database.md) | The database schema |
> | [design/coding-style.md](design/coding-style.md) | The coding conventions |
> | [design/performance.md](design/performance.md) | The performance measurements and the improvement candidates |
> | [design/support_for_citus.md](design/support_for_citus.md) | Citus (horizontal distribution) support |
> | [design/fuse-binding.md](design/fuse-binding.md) | The structure of the in-house FUSE binding |
> | [next.md](next.md) / [history.md](history.md) | What to do next / how the completed work came about |

A document gathering pgfs's solution structure, its package dependencies, the file structure inside Core and how
to build and run it.

## The solution structure

The solution [pgfs.sln](../pgfs.sln) has 8 projects (v0.2.0 split the old `Lib` by the OS mechanism layer).

| The project | The path | Its role | The platform |
|---|---|---|---|
| **Core** | [src/core/](../src/core/) | The core library, independent of the OS and the mechanism (Models / Api / Config / Logging / Collections / Utility / Objects). No `#if` | Cross-platform |
| **Fuse** | [src/fuse/](../src/fuse/) | The FS mechanism layer for Linux/macOS: the in-house libfuse binding plus the FUSE FileSystem plus the PosixAcl projection | Linux / macOS |
| **Dokan** | [src/dokan/](../src/dokan/) | The FS mechanism layer for Windows: the Dokan FileSystem plus the Windows SID/ACL projection plus the WindowsUserResolver | Windows |
| **Mkfs** | [src/mkfs/](../src/mkfs/) | The CLI that initializes the tables and the rest on the PostgreSQL side (a thin exe -> Core) | Cross-platform |
| **Mount** | [src/mount/](../src/mount/) | The mount tool for Linux/macOS (a thin exe -> Fuse plus Core) | Linux / macOS |
| **Assign** | [src/assign/](../src/assign/) | The mount tool for Windows (a thin exe -> Dokan plus Core) | Windows |
| **Ctl** | [src/ctl/](../src/ctl/) | The runtime control-plane CLI `pgfsctl` (the config / status subcommands; a thin exe -> Core only) | Cross-platform |
| **Gui** | [src/gui/](../src/gui/) | The operations GUI `pgfsgui` (an Avalonia desktop app; a thin front for status/config -> Core only. **Phase 5, being implemented**) | Cross-platform |

The dependencies run one way: **the tools (mkfs/mount/assign) -> the mechanism layer (Fuse/Dokan) -> Core ->
PostgreSQL**. `pgfsctl` (the CLI) and `pgfsgui` (the Avalonia GUI) are administration tools that skip the
mechanism layer and depend on **Core only** (they need neither FUSE nor Dokan, so they run on both operating
systems). The design of the split is in [v0.2.0-plan.md](design/v0.2.0-plan.md) /
[fuse-binding.md](design/fuse-binding.md), and `pgfsctl` / `pgfsgui` are in
[control-plane.md](design/control-plane.md) / [gui.md](design/gui.md).

Every TargetFramework is **net10.0**. `PublishAot` and `PublishTrimmed` are disabled on the CLI executables
(Gui does not carry the same publish settings) because Dapper, Tomlyn, the in-house binding and DokanNet depend
on dynamic code generation. `ImplicitUsings` and `Nullable` are enabled too. Per-OS `DefineConstants`
(`WINDOWS` / `LINUX` / `MACOS`) are defined.

### The namespaces

- The root: `Pgfs.*`
- The core library: `Pgfs.Core.{Api, Models, Config, Logging, Collections, Objects, Utility}`
- The mechanism libraries: `Pgfs.Fuse` (Linux/macOS FUSE) / `Pgfs.Dokan` (Windows)
- The executables: `Pgfs.Mkfs`, `Pgfs.Mount`, `Pgfs.Assign`, `Pgfs.Ctl` (producing `pgfsctl`), `Pgfs.Gui`
  (producing `pgfsgui`, Avalonia) - `pgfsctl` and `pgfsgui` are deliberate exceptions to the `{role}.pgfs`
  convention (admin tool names)

### Where the output goes

Every project builds into the same directory under [bin/](../bin/)
(`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>`).

### The assembly names

The namespaces are PascalCase such as `Pgfs.Core` and `Pgfs.Mkfs` (`RootNamespace`). The output assembly names
are unified as **lowercase with dots** (`AssemblyName`): `core.pgfs.dll`, `fuse.pgfs.dll`, `dokan.pgfs.dll`,
`mkfs.pgfs.{dll,exe}`, `mount.pgfs.{dll,exe}`, `assign.pgfs.{dll,exe}`. **The exception**: the control-plane CLI
is `pgfsctl.{dll,exe}` (a single systemctl-like word, deliberately breaking the `{role}.pgfs` convention;
[control-plane.md, Phase 3](design/control-plane.md)). For the details of the naming convention see
[fuse-binding.md, section 3-8](design/fuse-binding.md).

---

## The package dependencies

| The package | What it is for | Where it is used |
|---|---|---|
| `Npgsql` 9.0.4 | The PostgreSQL client | Every project |
| `Dapper` 2.1.66 | A lightweight ORM | Core |
| `Tomlyn` 0.20.0 | The TOML settings file | Core |
| `Tmds.LibC` 0.5.0 | The libc primitives (stat / statvfs / timespec / dlopen / errno) | Fuse |
| `DokanNet` 2.3.0.3 | DokanNet for Windows | Dokan |
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` 12.0.5 | The read-only operations GUI | Gui |

The libfuse binding was **brought in house into `Pgfs.Fuse`** in v0.2.0 (the old
`securefolderfs-community/Tmds.Fuse` fork was ported preserving the behaviour, and the `vendor/Tmds.Fuse`
submodule was retired). `libfuse3.so.3` is not bundled but linked dynamically with `dlopen` at runtime (LGPL;
the user provides it). The credits are in [src/fuse/NOTICES.md](../src/fuse/NOTICES.md) and the design is in
[fuse-binding.md](design/fuse-binding.md).

---

## The file structure (Core)

### Models ([src/core/src/Models/](../src/core/src/Models/))

Where the entity POCOs corresponding to one database row live. The settings models (the old `*Settings.cs` set)
were **removed** and moved into [Config](#config-srccoresrcconfig).

**The database entities**: `Base.cs`, `Inode.cs`, `Data.cs`, `Chunk.cs`
- The audit fields (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`) are shared through `Base`
  (both lowercase and uppercase properties are exposed for Dapper)
- `Inode.cs` / `Data.cs` / `Chunk.cs` are the POCOs for one row of `pgfs_inode` / `pgfs_data` /
  `pgfs_data_chunk` respectively

**The audit and ACL types**: `AuditOp.cs`, `AuditContext.cs`, `PgfsAcl.cs` (the canonical ACL)
- `AuditOp.cs` (the op string constants) / `AuditContext.cs` (the caller's ambient context) - the audit log
  ([audit-log.md](design/audit-log.md))
- `PgfsAcl.cs` is the canonical ACL document (the JSON of the `user.pgfs_acl` xattr: the named `entries[]` and
  `default[]`). It is the common canonical model between a Windows DACL and a Linux POSIX ACL
- Note: the POSIX ACL binary codec `PosixAcl.cs` **was moved into `Pgfs.Fuse`
  ([src/fuse/src/PosixAcl.cs](../src/fuse/src/PosixAcl.cs)) in v0.2.0** (separating the canonical form in Core
  from the OS projection in the Fuse mechanism layer). The design is in
  [permission-interop.md](design/permission-interop.md)

**The enums around LoggingOutput**: `SettingLoggingKind.cs` (Flags: None/Stderr/Stdout/File),
`SettingLoggingCycle.cs` (None/Hourly/Daily/Monthly), `SettingLoggingOutput.cs` (a POCO tying the two together)
- They stay in Models because [`LoggingOutputField`](../src/core/src/Config/Field.cs) references them as types
  (the candidate destination would be `Pgfs.Core.Logging`, but these files describe "the format of the log"
  rather than "the log output itself", so where they are now is the safer place)

> **🗒 Every old `*Settings.cs` was removed**: the 13 files `RootSettings.cs` /
> `MountSettings.cs` / `DatabaseSettings.cs` / `FileSystemSettings.cs` / `LoggingSettings.cs` /
> `SettingSettings.cs` / `Setting.cs` / `Settings.cs` / `BoolSetting.cs` / `SettingStorage.cs` /
> `BeforeChangeEventArgs.cs` / `DatabaseConnectionSetting.cs` / `Primitive.cs` were deleted. `Base.cs` was also
> rewritten into a minimal form with the `BaseProvider<A>` / `Statics` / `ChangingEventArgs` /
> `Created` / `Updated` machinery removed.

### Config ([src/core/src/Config/](../src/core/src/Config/))

The successor to the old `Settings` tree. It consists of **static `Field<T>` descriptors plus mutable POCOs plus
`ConfigLoader` (which merges the CLI/TOML/database/defaults) plus `ConfigStore` (the database I/O)**.

- **`Field<T>` (an abstract base plus derived types)**: the descriptor for one setting. It holds the Scope, the
  Key, the CliOptions, the DashOName, the SaveTo (`None`/`File`/`Db`), the DefaultFn and the Comment, and per
  type a `Parse(string) -> T` and a `Format(T) -> string`. The derived types are `StringField` / `IntField` /
  `LongField` / `BoolField` / `StringListField` / `LogLevelField` / `LoggingOutputField` / `ConnectionField`.
  For generating the help it also holds `AppliesTo` (a `[Flags] enum Tool` saying which tools' `--help` it
  appears in; `All` by default) and `ArgName` (an override for the value placeholder).
- **`Schema` (a static class)**: every `Field<T>` is declared in a nested static class, as in
  `Schema.Mount.MountPoint`. `Schema.AllFields` enumerates them all automatically through reflection (a new
  Field is included automatically).
- **`HelpText` (a static class)**: the `--help` text is generated from `Schema.AllFields` (walking the
  `CliOptions` / the `Comment` / the type / the default). Each Program (mkfs/mount/assign) passes only the
  introduction and its own tool-specific footer prose, and the option list is generated. `Field.AppliesTo`
  selects per tool (a help-only filter that does not interfere with the CLI parsing). The hand-written
  `ShowHelp` is gone.
- **The POCOs**: `MountConfig` / `DatabaseConfig` / `FileSystemConfig` / `LoggingConfig` / `SettingFileConfig`.
  The properties are `{ get; set; }`. They are used for changing the Live settings, although the volume_label
  cannot be changed because it is Format.
  The aggregate `RootConfig` gathers them all and holds `Help` and `Clean` directly.
- **`ConfigLoader`**: it merges the CLI, the TOML, the database and the defaults in one go to assemble a
  `RootConfig`. The phases are **CLI -> TOML -> the database** in that order, with "skip if something higher has
  already put it in" (the priority is `CLI > TOML > the database > the default`). The TOML path is searched for
  internally from the CLI-resolved `setting.file`. Only in a `mount(8)` helper context (the parent comm is
  `mount` AND there are positional arguments) does it silently swallow `-i -f -n -s -v -N -t`.
- **`ConfigStore`**: the database read and write of `(scope, key) -> value` against the flat
  `pgfs_settings(scope, key, value)`. `LoadAll` is a plain `SELECT scope, key, value FROM table` of everything
  plus an allow-list filter. `Save<T>` UPSERTs with `INSERT ... ON CONFLICT (scope, key) DO UPDATE`.
  The JSON representation goes through `Field<T>.FormatJson(value)` (Int/Long/Bool are native JSON, and anything
  else is `Format(value)` encoded as a JSON string).

The convention for persisting a change: right after the caller does `config.X.Y = newValue;`, it explicitly
calls `store.Save(Schema.X.Y, newValue)` (no setter hook was put in, to keep the Loader and the Store separate).
The concrete cases are to be gathered into thin wrappers such as a future `Api.SetVolumeLabel(string)`.

### Api ([src/core/src/Api/](../src/core/src/Api/))

- `Api.cs` is the public API of the filesystem operations (the inode CRUD, the data I/O through bytea chunks,
  xattr, symlinks, hardlinks, the volume information). It is `IDisposable` and takes the OS notification bridge
  through the `OsBridge` property.
- **The cross-client exclusion**: the private helpers `LockTargets` / `LockData(dataId)` /
  `LockInode(inodeId)` / `LockInodes(params long[])` take a `SELECT ... FOR UPDATE` row lock on `pgfs_lock`.
  Each mutating method (`WriteData` / `TruncateData` / `ReleaseData` / `Update{Mode,Owner,Size,Timestamps}` /
  `Rename` / `DeleteInode` / `CreateHardLink`) takes the appropriate lock at its head, and it is released
  automatically at the end of the tx. Taking several locks is fixed in ascending target_id order to avoid a
  deadlock. For the details see [support_for_citus.md, Phase 3](design/support_for_citus.md).
- `InodeCache.cs` is the in-memory cache of the inodes plus the SELECT/INSERT through Dapper. It can be searched
  on two routes, `byId` and `byPath`. The root is fixed at `id = 0`. `TryGetPath(id, out path)` resolves a full
  path by walking the parent chain (used on the Notify path).
- `Mode.cs` holds the POSIX `st_mode` constants (S_IFDIR, S_IRWXU and so on).
- `ContentCache.cs` / `DirtySet.cs`: the read LRU of the bodies and the dirty chunks. `DirtyNamespace.cs` /
  `IdReservation.cs` / `Api.WriteBackMetadata.cs`: the pending inodes, the id reservation, the metadata
  materialization, the audit and the drain at exit. For the implementation contracts and the unfixed points see
  [metadata-write-back.md](design/metadata-write-back.md) /
  [metadata-write-back-reviews.md](design/metadata-write-back-reviews.md).
- `NotifyChannel.cs` / `RemoteChangeInfo.cs`: the control LISTEN is always started and only the data-change
  notifications are controlled by `database.notify_enabled`. On receipt the InodeCache and the ContentCache are
  invalidated according to the inode/path/parent and the data_id. Active invalidation towards the kernel from
  Linux is not implemented. `attr_timeout=0` alone does not guarantee the freshness of Core, of the body or of
  another mount.
- `HandleTable.cs` / `OpenFileContext.cs` / `OpenInodes.cs` / `Api.Handle.cs`: **the handle context**
  (handle-context stages A to C). `HandleTable` issues a unique `fh` per open (starting at 1, monotonically
  increasing, never reused), and `OpenFileContext` holds "the inode id settled at that open". `OpenInodes` holds
  **the reference count of the bodies**, and the final release in `Api.Handle.cs` drops a body whose name is
  gone (`DropOrphanData`). The design and the as-built are in [handle-context.md](design/handle-context.md).
- `PruneAdmin.cs`: **cleaning up what an abnormal exit left behind** (the substance of `pgfsctl prune`). It
  removes the old rows of `{prefix}mounts`, the orphan data and libfuse's `.fuse_hidden*`, **with a different
  liveness decision per kind**. The specification is in [Pgfsctl.md, prune](Pgfsctl.md).
- `ConfigAdmin` (Config) / `StatusAdmin` (Api): the administration logic shared by pgfsctl and the GUI. The
  mount registration, the 30-second heartbeat and the snapshot of the effective settings and the statistics are
  Api's responsibility.

**An implementation note on the data I/O (bytea chunks)**: the body of each inode is one `pgfs_data` row plus
several `pgfs_data_chunk` rows (one row = one bytea = one chunk, with a default chunk_size of 1 MB). The large
object version was replaced with bytea in **Citus Phase 1** (for the details see
[docs/support_for_citus.md](design/support_for_citus.md)). The write-through WriteData is complete in one upsert
per chunk (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`, with the CASE
covering the three cases of "a central overlay / overwriting the tail / zero padding plus concatenation"), and a
concurrent WriteFile race is serialized automatically by PG's row lock. ReadData fetches the full chunk if the
content cache or write-back is enabled, and fetches only the range needed with
`substring(payload from N for M)` when both are off. write-back gathers the dirty chunks in memory and saves
them in a per-file tx at the flush. The payload length of each chunk is equal to "the number of bytes written so
far" (following the large object semantics). The zero padding is `decode(repeat('00', N), 'hex')` (because
`repeat(bytea, integer)` does not exist in PG).

### Logging ([src/core/src/Logging/](../src/core/src/Logging/))

- An in-house logger implementation (not `Microsoft.Extensions.Logging`).
- A static API through `Logger.Default`. `Level.Enum` is
  `All/Trace/Debug/Information/Warning/Error/Critical/None`. The destination is `Logger.Output`
  (`Action<string>`) and the minimum level is `Logger.MinLevel`.
- **Applying the destination setting**: each Program.cs sets `Logger.MinLevel = config.Logging.MinLevel` and
  `Logger.Output = LogSink.Create(config.Logging.Output)` at startup. If `logging.output` is `stdout`, `stderr`
  or `none` it is the corresponding sink, and if it is of the form `<cycle>:<dir>/<pattern>` it is
  [RotatingFileSink](../src/core/src/Logging/RotatingFileSink.cs) (date rotation plus `~` home expansion plus
  `*` -> a date stamp plus automatic directory creation plus AutoFlush). With nothing configured the default is
  `stderr`. The conversion is in [LogSink.Create](../src/core/src/Logging/LogSink.cs).
- **A guard clause is mandatory on a hot path**: `Logger.Trace(...)` takes `params object?[]`, which allocates
  an array and boxes. Anywhere called 1000+ times a second must check first, as in
  `if (Logger.IsTraceEnabled) { Logger.Trace(...); }`. `IsTraceEnabled` / `IsDebugEnabled` / `IsEnabled(level)`
  are provided.

### Utility ([src/core/src/Utility/](../src/core/src/Utility/))

- **`PathParser.cs`** breaks a path into the drive, the root and the name elements, and can rebuild it with a
  different separator, find the position of a wildcard and insert before or after. Each part is evaluated
  lazily through `Lazy<>`. A comparatively well-written component. **A caution**: `PathParser.FromPath(path)`
  uses the OS default separator (`Path.DirectorySeparatorChar`). The paths flowing into `Api` and `InodeCache`
  are always normalized to `/`, so when calling it inside the library, always be explicit with
  `PathParser.FromPath(path, "/")`.
- **`Pg.cs`** is the wrapper around Dapper plus Npgsql (`Query`, `QueryAsync`, `Execute`, `ExecuteAsync`). It
  caches an `NpgsqlDataSource` per connection string (the same connection string returns the same data source;
  the pool is managed inside the data source). `QuoteIdentifier` / `QuoteLiteral` escape. The SQL goes to
  `Logger.Trace` (with an early-return guard on `Logger.IsTraceEnabled` inside `TraceQuery`, so that
  `Regex.Replace` and `JsonSerializer.Serialize` are skipped when Trace is disabled). It provides the
  `Pg.OpenConnection` plus `using var tx = conn.BeginTransaction()` pattern as well as a
  `Pg.WithTransaction<T>` helper (when handling a `Span<byte>` it is a ref struct and cannot go in a lambda, so
  `OpenConnection` is used directly).
- **`ServiceResolver.cs`** converts between a service name and a port through `/etc/services` or the Win32
  `getservbyname`.
- **`NameNormalizer.cs`** normalizes an owner/group/principal name (full-width ASCII -> half-width, plus
  stripping the domain at `\` or `@`, plus lowercasing). It is applied to the stored names, the comparisons and
  the caller names alike, so that case and width are treated the same on both operating systems
  ([permission-interop.md](design/permission-interop.md), decisions [1] and [2]).
- `Indexer.cs` (`ReadOnlyIndexer<,>` / `ReadOnlyIndexer<,,>`, used by `PathParser` and `Pg`), `Fn.cs`,
  `String.cs`, `Json.cs` and `Retry.cs` are assorted helpers.

### Collections ([src/core/src/Collections/](../src/core/src/Collections/))

- `RichDictionary<K,V>` is a `Dictionary` extension with an `Added` event and support for a `GetOrAdd<W>`
  derived type
- `FirstList<T>` is an advanced list made of a linked list of `Memory<T>` segments. Used by `Inode.Children`
- `ComparerToEqualityComparer<T>` is used inside `FirstList`

### Objects ([src/core/src/Objects/](../src/core/src/Objects/))

- `Extensions.cs` holds the extensions `object.To<T>()`, `As<T>()`, `Is<T>()` and so on. It uses
  **C# 14's (net10.0's) `extension` syntax**.

---

## The settings file

[pgfs.toml.example](../pgfs.toml.example) is the sample. It is TOML in section form (`[database]` / `[mount]` /
`[logging]` / ...). The search path at runtime is defined in
[`Schema.Setting.SearchPath`](../src/core/src/Config/Schema.cs) (see [Config](#config-srccoresrcconfig) above).

---

## Building and running

### The output paths

The single-file publish in the table below covers Mkfs / Mount / Assign / Ctl. The distribution settings for Gui
are not in place, and a solution-wide publish does not necessarily give it the same layout or form. Building and
publishing it was not verified this time.

Every project outputs under [bin/](../bin/) through the
`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` setting. Debug / Release / Publish land
in separate places:

| The command | Where it goes | What is there |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `core.pgfs.dll` plus `{mkfs,mount,assign}.pgfs.{dll,exe}` plus the dependency dlls (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | The same in Release (`DebugType=embedded`, framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | The **single-file self-contained** executables `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs` and `pgfsctl` (the host OS's RID automatically, about 38 MB each) |

The CLI publish sets `PublishAot=false` / `PublishTrimmed=false` (AOT is impossible because Dapper, Tomlyn, the
in-house libfuse binding and DokanNet depend on dynamic code generation and P/Invoke).
`UseCurrentRuntimeIdentifier` and `SelfContained` are enabled only in a Target with `_IsPublishing=true` so that
they do not apply to `dotnet build -c Release` (to stop 200 framework dlls lining up at build time).

```pwsh
# Build the whole solution
dotnet build pgfs.sln

# Build one project
dotnet build src/core/Core.csproj
dotnet build src/mkfs/Mkfs.csproj

# A Release publish (single-file for the host OS)
dotnet publish src/mkfs/Mkfs.csproj -c Release
# Or the whole solution
dotnet publish pgfs.sln -c Release
```

**The .NET 10 SDK is required** (the `extension` syntax and so on are used).

---
