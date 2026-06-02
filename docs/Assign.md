# pgfs.assign specification

Specification for `pgfs.assign`, the Windows tool that **mounts a PGFS filesystem via DokanNet**.

This document describes the specification as implemented in [src/assign/](../src/assign/); it is the counterpart of the Linux/macOS [Pgfs.Mount](../src/mount/) (Tmds.Fuse version). All the shared logic is centralized in [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs), and Mount/Assign are purely OS-specific adapters.

A Japanese translation is available in [Assign.ja.md](Assign.ja.md).

## Role

It mounts a PostgreSQL database initialized as PGFS (built by [docs/Mkfs.md](Mkfs.md)) onto a Windows drive or directory, so Explorer and applications can access it as an ordinary filesystem.

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ^v Npgsql + Dapper
    Pgfs.Lib.Api.Api (cross-platform)
        ^v
    Pgfs.Assign.FileSystem : DokanNet.IDokanOperations2 (Windows-specific)
        ^v Dokan2 driver
    Windows kernel
        ^v
    access via P:\ or C:\mnt\pgfs etc.
```

## Build and run

```pwsh
# Build
dotnet build src\assign\Assign.csproj

# Run (default: connect to localhost:5432 and mount at P:\)
dotnet run --project src\assign

# A different mount point (drive letter)
dotnet run --project src\assign -- -m R:

# A different mount point (directory)
dotnet run --project src\assign -- -m C:\mnt\pgfs

# Explicit connection string
dotnet run --project src\assign -- `
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" `
    -m P:

# Help
dotnet run --project src\assign -- --help
```

> **Unmount**: request unmount with Ctrl+C, or use `dokanctl /u <mountpoint>` from another terminal.

### Prerequisites

- **.NET 10 SDK**
- **The Dokan 2.x kernel driver** installed in advance
  - Install `DokanSetup_redist.exe` from [Dokan releases](https://github.com/dokan-dev/dokany/releases).
  - If the Dokan driver is not visible at startup, it fails with a `DokanException`.
- The mount point not colliding with an existing drive
  - For a drive letter: `P:` etc. must be free.
  - For a directory: it must be an **empty directory** on NTFS.
- The DB side initialized by [docs/Mkfs.md](Mkfs.md).

### Behavior on Linux / macOS

It still builds on Linux/macOS (for cross-compilation), but running it hits the `OperatingSystem.IsWindows()` guard at the start, prints an error pointing to `mount.pgfs` (the Tmds.Fuse version), and exits.

## Configuration

The configuration model shares [`Pgfs.Lib.Config.RootConfig`](../src/lib/src/Config/RootConfig.cs). The same TOML settings file and the same command-line options as Mkfs / Mount apply. See [docs/Mkfs.md](Mkfs.md) for details.

What pgfs.assign uses in particular:

| Setting | Argument option | Default |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Windows: `P:` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `warning` |

## Implemented Dokan operations

[src/assign/src/FileSystem.cs](../src/assign/src/FileSystem.cs) implements [`DokanNet.IDokanOperations2`](https://github.com/dokan-dev/dokan-dotnet) and handles the following callbacks.

| Operation | Status | Notes |
|---|---|---|
| `Mounted` / `Unmounted` | done | Logs mount completion / release. |
| `GetVolumeInformation` | done | Volume label, `FileSystemFeatures`. |
| `GetDiskFreeSpace` | done | Uses `pg_database_size` as the actual usage. |
| `CreateFile` | done | Handles all `FileMode` values: `CreateNew` / `Create` / `Open` / `OpenOrCreate` / `Truncate` / `Append`. |
| `Cleanup` | done | Follows the Windows convention of actually deleting when `info.DeletePending`. |
| `CloseFile` | done | Clears `info.Context`. |
| `GetFileInformation` | done | inode -> `ByHandleFileInformation`. |
| `FindFiles` / `FindFilesWithPattern` | done | `Api.ListChildren` + filter with `DokanHelper.DokanIsNameInExpression`. `ShortFileName` is always `default`, left to the Windows kernel (8.3 symbolic access is out of scope). |
| `ReadFile` | done | `Api.ReadData` (via bytea chunks). |
| `WriteFile` | done | `Api.WriteData`; append mode when `info.WriteToEndOfFile`. |
| `FlushFileBuffers` | done | no-op, since the PostgreSQL side is already persisted by commit. |
| `SetFileAttributes` | done | `ReadOnly` is reflected into st_mode's write bits; `Hidden` / `System` / `Archive` are stored in an xattr (`user.win.attrs`) as JSON `{hidden,system,archive}`. |
| `SetFileTime` | done | Only `lastWriteTime` is written to the DB. `atime` is ignored per requirement. |
| `DeleteFile` / `DeleteDirectory` | done | Per the Windows convention, only checks deletability; the actual deletion happens in `Cleanup`. |
| `MoveFile` | done | Handles the `replace` flag, same-kind check, empty-directory check. |
| `SetEndOfFile` | done | `Api.TruncateData` (truncates the bytea chunks together). |
| `SetAllocationSize` | done | Provisionally the same behavior as `SetEndOfFile`. |
| `LockFile` / `UnlockFile` | done (simple) | Delegated to the kernel via `DokanOptions.UserModeLock`; always returns Success here. |
| `GetFileSecurity` / `SetFileSecurity` | done | Projects/reverse-projects between the inode (uname/gname/st_mode + canonical ACL) and a Windows security descriptor: owner/group -> SID, mode -> owner/group/Everyone allow ACEs, named entries via `user.pgfs_acl`. Deny ACEs / ACE ordering / inheritance are dropped in projection. See [permission-interop.md](permission-interop.md). |
| `FindStreams` | not done | Alternate Data Streams not supported; returns `NotImplemented`. |

## Architecture

```
+---------------------------------------------------------------+
| Pgfs.Assign.Program          -- arg parsing / OS check / mount |
+---------------------------------------------------------------+
| Pgfs.Assign.FileSystem       -- Dokan callbacks (Windows)      |
|   - SplitParent / NormalizePath   * OS-independent             |
|   - DoCreate / Resolve            * OS-independent             |
|   - ToAttributes (FileSystemUtils) * Windows-specific          |
+---------------------------------------------------------------+
| Pgfs.Assign.WindowsUserResolver -- SID <-> NTAccount (Windows) |
+---------------------------------------------------------------+
| Pgfs.Lib.Api.Api             -- DB operations (cross-platform) |
|   - GetByPath / ListChildren / CreateDirectory / ...           |
|   - ReadData / WriteData / TruncateData                        |
|   - CreateSymlink / CreateHardLink                             |
|   - GetXAttr / SetXAttr / ListXAttr / RemoveXAttr              |
+---------------------------------------------------------------+
| Pgfs.Lib.Api.InodeCache      -- inode memory cache             |
+---------------------------------------------------------------+
| Pgfs.Lib.Utility.Pg          -- Npgsql + Dapper wrapper        |
+---------------------------------------------------------------+
```

### Symmetry with Mount

| Item | Mount (Linux) | Assign (Windows) |
|---|---|---|
| Mount library | Tmds.Fuse | DokanNet 2.3 |
| Parent callback type | `FuseFileSystemBase` | `IDokanOperations2` |
| User resolution | `UserResolver` (libc getpwnam) | `WindowsUserResolver` (NTAccount/SID) |
| Error codes | POSIX errno (`-ENOENT` etc.) | `DokanResult.FileNotFound` etc. |
| Path separator | fixed `/` | normalize `\` to `/` before Api |
| Deletion timing | `Unlink` deletes immediately | `Cleanup` deletes when it sees `DeletePending` |
| Append mode | offset argument | `info.WriteToEndOfFile` |
| ACL | st_mode + canonical ACL (via `system.posix_acl_access`) | st_mode + canonical ACL (SD projection via `Get/SetFileSecurity`) |

Both just call [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs), so the actual DB operations are shared.

## Design decisions / provisional implementation

### Name normalization + well-known principal mapping

Owner / group / principal names are normalized **on store, on match, and on caller-name** ([NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs): fullwidth ASCII -> halfwidth + domain stripping `\` / `@` + lowercasing). The DB **stores Linux names**, and the well-known names are converted bidirectionally between Windows and Linux via the [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) mapping: `root` <-> `Administrator(s)` / `nobody` / `nogroup` <-> `NT AUTHORITY\ANONYMOUS LOGON` / `other` <-> `Everyone`. This lets files created on Windows resolve to the same name on Linux, and treats case and fullwidth/halfwidth as equivalent (the design of record is [permission-interop.md](permission-interop.md)).

### `Hidden` / `System` / `Archive` attributes

There is no Linux-side equivalent, so they are stored in the `user.win.attrs` key of `pgfs_inode.xattrs` JSONB as JSON `{hidden,system,archive}` (bool). Being in the `user.` namespace, it is also visible from Linux `getfattr`. `compressed` is future work. Even when all OFF, **the xattr is not deleted; the JSON is written**.

The decision in `GetFileInformation` / `FindFiles` is two-valued:
- **xattr present** (even if the value is 0) = the Windows side has touched `SetFileAttributes` at least once -> trust the xattr value as-is.
- **xattr absent** = created on Linux and never touched by Windows -> apply the "leading dot = Hidden" heuristic as a fallback.

A "delete the xattr when the mask is 0" design would hit a **UX bug where un-hiding a dotfile in Windows Explorer deletes the xattr, then the heuristic revives on the next read and re-hides it**, so the presence/absence of the xattr is kept as a "has it been touched" flag.

`ReadOnly` continues to be managed via st_mode's write bits (so it is visible bidirectionally with the Linux side). Implemented as `WinAttrsXattrKey` (= `user.win.attrs`) / `LoadWinAttrs` / `SaveWinAttrs` in [src/assign/src/FileSystemUtils.cs](../src/assign/src/FileSystemUtils.cs).

### Alternate Data Streams (ADS)

NTFS `file.txt:stream`-style alternate streams are not supported. `FindStreams` returns `NotImplemented`. The Linux-side xattr-equivalent feature could be mapped to ADS instead of the xattr API (which DokanNet lacks), but for now it is left going through xattr without being exposed.

### ACL (GetFileSecurity / SetFileSecurity)

POSIX is canonical and the Windows ACL is implemented as a **projection view** (the design of record is [permission-interop.md](permission-interop.md)).

- **GetFileSecurity (read)**: projects the inode onto a Windows security descriptor ([FileSystemUtils.BuildSecurity](../src/assign/src/FileSystemUtils.cs)). Owner/group are mapped uname/gname -> SID, and the DACL is composed from `st_mode`'s owner/group/Everyone allow ACEs plus the canonical ACL (`user.pgfs_acl`) named entries. Explorer's "Security" tab and `icacls` reflect the POSIX permissions.
- **SetFileSecurity (write)**: reverse-projects the received SD ([FileSystemUtils.ApplySecurity](../src/assign/src/FileSystemUtils.cs)). Owner/group SID -> name (if a group SID arrives as the owner, it is routed to `owner=nobody` / `group=that`), the DACL's three base classes -> `st_mode`, named -> `user.pgfs_acl`.
- **Dropped in projection**: deny ACEs / ACE ordering / inheritance flags are not adopted because they have no POSIX equivalent (an exact Windows <-> Windows ACL match is not guaranteed).
- The Linux side round-trips the same canonical store via `system.posix_acl_access` (setfacl/getfacl) ([Mount.md](Mount.md)).

### LockFile / UnlockFile

Returns `Success` with `DokanOptions.UserModeLock` set. This makes the Dokan kernel hold the range locks itself, never reaching PostgreSQL advisory locks. POSIX-compatible locking is planned for later.

### Mount point

- **Drive letter** (`P:`, `R:`, etc.): specify a free letter. The `MountManager` option lets Dokan manage it.
- **Directory path** (`C:\mnt\pgfs`): specify an existing **empty directory** on NTFS. While mounted, that directory's original contents are hidden.

### CreateFile return values

- `Open`: absent -> `FileNotFound`, or `PathNotFound` (when a directory is requested).
- `OpenOrCreate`: existing -> `AlreadyExists` (set in info.Context); absent -> create and `Success`.
- `Create`: existing -> discard contents and `AlreadyExists`; absent -> create and `Success`.
- `CreateNew`: existing -> `FileExists`; absent -> create and `Success`.
- `Truncate`: absent -> `FileNotFound`; directory -> `AccessDenied`; file -> discard contents and `Success`.
- `Append`: absent -> create; existing -> open as-is (Dokan sets `WriteToEndOfFile` on the next WriteFile).

## Known limitations / TODO

| Item | Status | Notes |
|---|---|---|
| Data I/O (Read/Write) | done | Uses `Api.ReadData` / `WriteData` as the shared base. |
| SID <-> uname/gname resolution | done | NTAccount.Translate in [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs). |
| Chunk reduction on Truncate | done | `Api.TruncateData` (shared with Mount). |
| ACL (Get/Set FileSecurity) | done | POSIX canonical, Windows projection view. SD <-> st_mode + canonical ACL (`user.pgfs_acl`). owner=group is routed to nobody/that. Deny / inheritance are dropped in projection. See [permission-interop.md](permission-interop.md). Remaining: strict named-ACL enforcement (pending requirements). |
| Hidden / System / Archive attributes | done | Stored in the xattr `user.win.attrs` as JSON `{hidden,system,archive}`. `ReadOnly` uses st_mode's write bits (as before). A dotfile created on Linux is shown Hidden via the heuristic fallback. |
| Alternate Data Streams | not done | NTFS ADS not supported. |
| POSIX-compatible range lock | not done | Left to the kernel via `DokanOptions.UserModeLock`. |
| Junction (reparse points) | not done | The DB schema has an `is_junction` column. Referenced only from Mount. The Assign side does not support it. |
| Notify (cross-client change notification) | done | Enabled with `database.notify_enabled=true` (CLI `--notify`). Receives other clients' writes via PostgreSQL LISTEN/NOTIFY, invalidates the local `InodeCache`, then [FileSystem.PropagateRemoteChange](../src/assign/src/FileSystem.cs) asks Explorer to redraw via `DokanInstance.NotifyUpdate(WindowsPath)`. For the payload spec etc., see "Cross-client change notification (Notify)" in [history.md](history.md). |
| Reconnection on connection failure | done | Wraps `Pg.OpenConnection`-family calls with [Retry](../src/lib/src/Utility/Retry.cs). Exponential backoff, tuned by `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms`. Exceptions mid-query are not retried because of idempotency concerns. |
| Fallback for uname / gname absent on the OS | done | On `NTAccount.Translate` failure, resolves the SID of `mount.fallback_uname` / `mount.fallback_gname` (stored in the DB; defaults `nobody` / `nogroup`) and returns it. `nobody` / `nogroup` resolve to `NT AUTHORITY\ANONYMOUS LOGON` via the well-known mapping. If that itself cannot be resolved to a SID, hardcodes `WellKnownSidType.AnonymousSid` and logs a warning. Implemented in [src/assign/src/WindowsUserResolver.cs](../src/assign/src/WindowsUserResolver.cs). |

## Verification scenario (Windows)

```pwsh
# 1. Confirm the Dokan2 driver is installed
sc query dokan2
# OK if STATE is RUNNING

# 2. Initialize PGFS on PostgreSQL (see [docs/Mkfs.md](Mkfs.md))
dotnet run --project src\mkfs

# 3. Mount
dotnet run --project src\assign -- -m P:

# 4. Access from another terminal (PowerShell)
Get-ChildItem P:\                           # root directory
New-Item -ItemType Directory P:\hello       # create a directory
Get-ChildItem P:\                           # hello is visible
Remove-Item P:\hello                        # remove it
New-Item -ItemType File P:\empty.txt        # empty file
"hello world" | Out-File -FilePath P:\test.txt -Encoding utf8
Get-Content P:\test.txt                     # -> hello world
Move-Item P:\test.txt P:\renamed.txt        # rename
Remove-Item P:\renamed.txt                  # remove

# 5. Unmount
# Ctrl+C in the pgfs.assign terminal
# or from another terminal:
dokanctl /u P:
```

## References

- [docs/database.md](database.md) — DB schema design.
- [docs/Mkfs.md](Mkfs.md) — the initializer tool's spec.
- [docs/Mount.md](Mount.md) — the Linux/macOS mount tool.
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) — the shared API.
- [Dokan](https://dokan-dev.github.io/) — the Dokan driver.
- [DokanNet](https://github.com/dokan-dev/dokan-dotnet) — the C# bindings.
