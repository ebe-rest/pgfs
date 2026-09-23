# The assign.pgfs specification

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: the **user-facing specification** of `assign.pgfs`
> (Windows / Dokan) - the CLI, the prerequisites, the operations that are supported and the known limitations.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [design/windows-parity.md](design/windows-parity.md) | **The design and the as-built.** Why it was implemented that way |
> | [Mount.md](Mount.md) | The document in the same position for the Linux side (`mount.pgfs` / FUSE) |
> | [Mkfs.md](Mkfs.md) | The defaults table of the settings (the Assign/Mount excerpts defer to it) |
> | [tests.md](tests.md) | The counts of the Windows suites and how to run them |

The specification of `assign.pgfs`, the Windows tool that **mounts a PGFS filesystem through DokanNet**.

This document gathers the specification as settled in the current implementation
([src/assign/](../src/assign/)), and is the counterpart of [Pgfs.Mount](../src/mount/) (the FUSE build) for
Linux/macOS. Everything shared is gathered in [`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs), with Mount and
Assign confined to being the OS-specific adapters.

> A static comparison against the current code plus **verification on real Windows hardware**: 2026-09-19. With
> the default settings (write-back off), **e2e 30/30 plus cross-client 6/6** are green.
> **Acceptance with write-back (data and metadata) turned on was completed**
> ([writeback.ps1](../tests/windows/writeback.ps1) 6/6 plus
> [wbmeta.ps1](../tests/windows/wbmeta.ps1) 4 passed + 1 skip). For the feature differences, the implementation
> candidates, the recommended approach and the acceptance criteria see
> [the Windows parity design](design/windows-parity.md).

## Its role

It mounts a PostgreSQL database initialized as PGFS (built as in [docs/Mkfs.md](Mkfs.md)) onto a Windows drive
or directory, so that Explorer and applications can access it as an ordinary filesystem.

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Core.Api.Api (cross-platform)
        ↑↓
    Pgfs.Dokan.FileSystem : DokanNet.IDokanOperations2 (Windows-specific)
        ↑↓ the Dokan2 driver
    the Windows kernel
        ↑↓
    accessed at P:\ or C:\mnt\pgfs and so on
```

## Building and running

```pwsh
# build
dotnet build src\assign\Assign.csproj

# start (the default: connect to localhost:5432 and mount at P:\)
dotnet run --project src\assign

# a different mount point (a drive letter)
dotnet run --project src\assign -- -m R:

# a different mount point (a directory)
dotnet run --project src\assign -- -m C:\mnt\pgfs

# an explicit connection string
dotnet run --project src\assign -- `
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" `
    -m P:

# the help
dotnet run --project src\assign -- --help
```

> **Unmounting**: Ctrl+C requests an unmount. `dokanctl /u <mountpoint>` in another terminal works too.
>
> **Ctrl+C changes meaning from the second press** (the same contract as mount.pgfs):
>
> | The count | What it does |
> |---|---|
> | The 1st | A request to unmount. It tries to write out whatever write-back has unflushed, up to the deadline (`--write-back-flush-timeout-ms`, 30 seconds by default) |
> | The 2nd | **Abandons the wait for the flush.** Whatever could not be written is lost, but **the log of what was lost and exit 4 remain** |
> | The 3rd and later | An immediate exit (the reporting path is given up too) |
>
> The console's X button, a logoff and `Stop-Process` do not go through this staging (Windows cuts them off in
> about 5 seconds, and `Stop-Process` terminates the process immediately). **If it has to be stopped while
> holding unflushed work, use Ctrl+C.**

### The prerequisites

- **The .NET 10 SDK**
- **The Dokan 2.x kernel driver** installed in advance
  - Install `DokanSetup_redist.exe` from the
    [Dokan releases](https://github.com/dokan-dev/dokany/releases)
  - If the Dokan driver is not visible at startup, it dies with a `DokanException`
- The mount point not colliding with an existing drive (**assign checks before starting** and
  exits with a list of free candidates if it is in use)
  - With a drive letter: `P:` and so on has to be free. **Note that a CD-ROM with no media or an unconnected
    removable drive also occupies a letter** (the check is through the `DriveInfo` listing)
  - With a directory: that path has to be an **empty directory** on NTFS
- The database side initialized as in [docs/Mkfs.md](Mkfs.md)

### The behaviour on Linux / macOS

It is made to build on Linux/macOS (for cross-compilation). On an ordinary start, after building the settings,
an `OperatingSystem.IsWindows()` guard emits an error directing the user to `mount.pgfs` (the FUSE build) and
exits.

## The settings

The settings model shares [`Pgfs.Core.Config.RootConfig`](../src/core/src/Config/RootConfig.cs). The same TOML
settings file ([pgfs.toml.example](../pgfs.toml.example)) and the same command-line options as Mkfs and Mount
are available. For the details see [docs/Mkfs.md](Mkfs.md).

What assign.pgfs uses in particular:

| The setting | The argument | The default |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Windows: `P:` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `information` |
| `database.notify_enabled` | `--notify` | `false` (opting into the data-change notifications. It invalidates the Core cache and calls NotifyUpdate. **It is effectively mandatory for running several mounts at once** - see the note below) |

### How the Linux additions apply

`mount.cache_data_max_bytes` (64 MiB), `mount.negative_cache_ttl_ms` (0), `mount.write_back` (false),
`mount.write_back_metadata` (false) and their ceilings, intervals and timeouts all use the shared Schema and
Api. For the defaults, where they are stored and the Live policy see
[settings-matrix.md](design/settings-matrix.md).
`pgfsctl config` / `status`, the mounts registration and the 30-second heartbeat are shared implementations too.

**With metadata on, the CloseInode of Cleanup does not flush synchronously as a rule either.** An explicit
FlushFileBuffers calls FlushInode / FlushDirectory. Cleanup is void so it cannot return a failure and leaves it
in the log.
**Fixed**: CreateNew passes `exclusive=true` to Core (the collision decision is the database's
unique constraint; measured with two simultaneous mounts, there is always exactly one success). Anything left
unwritten at exit is reported with **exit 4** by looking at `UnflushedAtShutdown` after `api.Dispose()` (the
same contract as mount.pgfs). **The staging of the stop signal was wired too** - the second Ctrl+C
calls `Api.AbandonFlush()`, abandons the persistence and finishes with the loss report plus exit 4 (the Windows
wiring of B-12). **`FileOptions.WriteThrough` is wired too** (a complete barrier on every WriteFile; verified in
[writeback.ps1](../tests/windows/writeback.ps1)).
**A ruled-on specification**: `mkdir` stays `exclusive: false` (because **making it synchronous means no pending
directory is born and the ancestor-chain INSERT becomes unreachable**). **By default the database's unique
constraint rejects a same-name mkdir**, but **turning `write_back_metadata` on leaves room for a collision to
turn into a success through the pending adoption** (the B-1 knob covers `O_EXCL` creates and not mkdir). For the
details see [windows-parity.md, `mkdir` is not made synchronous](design/windows-parity.md).
~~Windows acceptance with `write_back_metadata` on is unverified~~ -> **accepted on 2026-09-19 through
[wbmeta.ps1](../tests/windows/wbmeta.ps1)**
(the data write-back = `mount.write_back` on is verified at 6/6).

> **⚠ Add `--notify` if several mounts are used at once.** Each mount's `InodeCache` and read cache are
> independent, and another mount's changes only travel through LISTEN/NOTIFY. With the default
> (`database.notify_enabled=false`),
> **a create, an overwrite, a delete or a replacing rename done by another mount is never visible**
> (measured with two mounts on 2026-09-19; with `--notify` it follows within a few hundred ms).
> The exclusion (a `CREATE_NEW` or `mkdir` collision) is decided by the database's unique constraint, so it
> works correctly with or without notify.
> The test is [tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1).

## The Dokan operations that are implemented

[src/dokan/src/FileSystem.cs](../src/dokan/src/FileSystem.cs) implements
[`DokanNet.IDokanOperations2`](https://github.com/dokan-dev/dokan-dotnet) and handles the following callbacks.

| The operation | Supported | Notes |
|---|---|---|
| `Mounted` / `Unmounted` | ✅ | Logs the mount and the unmount |
| `GetVolumeInformation` | ✅ | The volume label and the `FileSystemFeatures` |
| `GetDiskFreeSpace` | ✅ | Through `Api.GetStatFs`. If mkfs `--statfs` created `{prefix}statfs()` (plperlu), it is the server-side **real free disk space**, otherwise the nominal capacity (`max_file_size` - `pg_database_size`). For the details see [docs/df-support.md](design/df-support.md) |
| `CreateFile` | ✅ | Handles all of `FileMode`'s `CreateNew` / `Create` / `Open` / `OpenOrCreate` / `Truncate` / `Append` |
| `Cleanup` | ⚠️ | When it is not a delete it is CloseInode, and on a DeletePending it is the real delete (plus **a `NotifyDelete` to prompt the Windows-side cache invalidation**). A flush or delete failure is only logged and cannot be returned to the caller |
| `CloseFile` | ✅ | Clears the `info.Context` |
| `GetFileInformation` | ✅ | The inode -> a `ByHandleFileInformation` |
| `FindFiles` / `FindFilesWithPattern` | ✅ | `Api.ListChildren` plus a filter through `DokanHelper.DokanIsNameInExpression`. The `ShortFileName` is always `default`, left to the Windows kernel (8.3 symbolic access is out of scope) |
| `ReadFile` | ✅ | `Api.ReadData` (through the bytea chunks) |
| `WriteFile` | ✅ | `Api.WriteData`, in append mode when `info.WriteToEndOfFile`. **A handle with `FILE_FLAG_WRITE_THROUGH` gets a complete barrier on every write** (`Api.FlushInode`) |
| `FlushFileBuffers` | ✅ | FlushInode for a file and FlushDirectory for a directory. A failure is an Error. **If the target cannot be resolved it is FileNotFound** (the unconditional-Success fail-open was closed) |
| `SetFileAttributes` | ✅ | `ReadOnly` is reflected in the write bits of the st_mode, and `Hidden` / `System` / `Archive` are saved in the xattr (`user.win.attrs`) as the JSON `{hidden,system,archive}`. **A request with no substantive change succeeds without touching the database** (it prevents the ReadOnly clearing that `Remove-Item` calls before a delete from blocking the deletion of an unwritable inode) |
| `SetFileTime` | ✅ | Only the `lastWriteTime` is reflected in the database. The `atime` is ignored by requirement |
| `DeleteFile` / `DeleteDirectory` | ✅ | As Windows expects, only the deletability check. The real delete happens in `Cleanup` |
| `MoveFile` | ✅ | The `replace` flag, the same-kind check and the empty-directory check. **A rename onto the same path or the same inode is a no-op success** (it does not enter the replacement deletion) |
| `SetEndOfFile` | ✅ | `Api.TruncateData` (trimming the bytea chunks too) |
| `SetAllocationSize` | ✅ | **Separated from the EOF**: below the current EOF it truncates, and at or above it **succeeds without changing the logical contents**. It does not actually reserve capacity, and there is no path for returning an AllocationSize in the `ByHandleFileInformation` |
| `LockFile` / `UnlockFile` | ✅ | **`UserModeLock` was removed** - a byte-range lock is **enforced by the Dokan driver in the kernel**. The callbacks used to always return Success and lie that "a lock that was never taken had been taken". **A range lock across clients (between separate mounts) is a separate design** |
| `GetFileSecurity` / `SetFileSecurity` | ✅ | Projects and reverse-projects between the inode (uname/gname/st_mode plus the canonical ACL) and a Windows SD. The owner and group -> SIDs, the mode -> allow ACEs for owner/group/Everyone, and the named ones -> `user.pgfs_acl`. Deny, the ACE order and the inheritance are dropped in the projection. For the details see [permission-interop.md](design/permission-interop.md) |
| `FindStreams` | ❌ | Alternate Data Streams are unsupported; it returns an empty collection |

The key: ✅ handled, ⚠️ with limitations or defects, ❌ unimplemented. It is not a classification of what has
been verified on real hardware.

## The architecture

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Assign.Program        ── argument parsing, OS check, mounting │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Dokan.FileSystem      ── the Dokan callbacks (Windows) │
│   - SplitParent / NormalizePath   * OS-independent       │
│   - DoCreate / Resolve            * OS-independent       │
│   - ToAttributes (FileSystemUtils) * Windows-specific    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Dokan.WindowsUserResolver ── SID <-> NTAccount resolution (Windows-specific) │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.Api          ── the database operations (cross-platform) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
│   - ReadData / WriteData / TruncateData                 │
│   - CreateSymlink / CreateHardLink                      │
│   - GetXAttr / SetXAttr / ListXAttr / RemoveXAttr       │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.InodeCache   ── the in-memory inode cache  │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Utility.Pg       ── the Npgsql + Dapper wrapper │
└─────────────────────────────────────────────────────────┘
```

### The symmetry with Mount

| The item | Mount (Linux) | Assign (Windows) |
|---|---|---|
| The mounting library | libfuse (the in-house binding) | DokanNet 2.3 |
| The parent callback type | `FuseFileSystemBase` | `IDokanOperations2` |
| The user resolution | `UserResolver` (libc's getpwnam) | `WindowsUserResolver` (NTAccount/SID) |
| The error codes | POSIX errnos (`-ENOENT` and so on) | `DokanResult.FileNotFound` and so on |
| The path separator | Fixed at `/` | `\` is normalized to `/` before the Api |
| When a delete happens | `Unlink` deletes at once | `Cleanup` deletes after seeing the `DeletePending` |
| The append mode of a write | The offset argument | `info.WriteToEndOfFile` |
| The ACLs | The st_mode plus the canonical ACL (through `system.posix_acl_access`) | The st_mode plus the canonical ACL (projected into an SD by `Get/SetFileSecurity`) |

Both merely call [`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs), so the real database operations are shared.

## The design decisions and the provisional implementations

### The name normalization plus the well-known principal mapping

An owner / group / principal name is normalized **when it is stored, when it is compared and when it is the
caller's name** ([NameNormalizer](../src/core/src/Utility/NameNormalizer.cs): full-width ASCII to half-width,
plus stripping the domain at `\` or `@`, plus lowercasing). The database **stores the Linux names**, and the
well-known names between Windows and Linux are converted in both directions by the mapping in
[WindowsUserResolver](../src/dokan/src/WindowsUserResolver.cs): `root` <-> `Administrator(s)` /
`nobody` and `nogroup` <-> `NT AUTHORITY\ANONYMOUS LOGON` / `other` <-> `Everyone`. That lets a file created on
Windows resolve to the same name on Linux, with the case and the width treated the same (the source of truth for
the design is [permission-interop.md](design/permission-interop.md)).

### The `Hidden` / `System` / `Archive` attributes

There is no corresponding concept on the Linux side, so they are saved as the byte string of the JSON
`{hidden,system,archive}` (bools) in the xattr `user.win.attrs` (the parallel arrays
`xattr_names` / `xattr_values` of `pgfs_inode`; the values are bytea; see
[xattr-bytea.md](design/xattr-bytea.md)). Being in the `user.` namespace, it is visible from Linux's `getfattr`
too. `compressed` is for the future. Even when it is empty (everything off), **the xattr is not removed and the
JSON is written**.

The decision in `GetFileInformation` and the `FindFiles` family is binary:
- **The xattr is there** (even with a value of 0) = `SetFileAttributes` has been touched from Windows at least
  once -> trust the xattr value as it is
- **The xattr is not there** = it was created on Linux and Windows has never touched it -> apply the heuristic
  "a leading dot = Hidden" as a fallback

A design of "remove the xattr when the mask is 0" would hit the **UX bug** where, right after un-hiding a
dotfile in Windows Explorer, the xattr disappears, the heuristic comes back on the next read and
**it goes back to Hidden**. The presence of the xattr is therefore kept as the flag for "has it been touched".

`ReadOnly` continues to be managed through the write bits of the st_mode (visible in both directions with the
Linux side). The implementation is `WinAttrsXattrKey` (= `user.win.attrs`) / `LoadWinAttrs` / `SaveWinAttrs` in
[src/dokan/src/FileSystemUtils.cs](../src/dokan/src/FileSystemUtils.cs).

### Alternate Data Streams (ADS)

NTFS's alternate streams in the `file.txt:stream` form are unsupported. `FindStreams` returns empty with
`NotImplemented`. Mapping the equivalent of the Linux side's xattr onto ADS rather than an xattr API (DokanNet
has none) is an option, but for now it is left unexposed and goes through the xattr.

### The ACLs (GetFileSecurity / SetFileSecurity)

POSIX is canonical and a Windows ACL is implemented as **a projected view** (the source of truth for the design
is [permission-interop.md](design/permission-interop.md)).

- **GetFileSecurity (the read)**: the inode is projected into a Windows security descriptor
  ([FileSystemUtils.BuildSecurity](../src/dokan/src/FileSystemUtils.cs)). The owner and group go from
  uname/gname to SIDs, and the DACL is composed from the `st_mode`'s owner/group/Everyone allow ACEs plus the
  named entries of the canonical ACL (`user.pgfs_acl`). Explorer's Security tab and `icacls` reflect the POSIX
  permissions.
- **SetFileSecurity (the write)**: the received SD is reverse-projected
  ([FileSystemUtils.ApplySecurity](../src/dokan/src/FileSystemUtils.cs)). The owner and group SIDs go to names
  (if a group SID arrives as the owner it is routed to `owner=nobody` plus that group), the DACL's three basic
  classes go to the `st_mode`, and the named ones go to `user.pgfs_acl`.
- **What is dropped in the projection**: deny ACEs, the ACE order and the inheritance flags are not taken
  because they have no POSIX equivalent (a complete ACL match between Windows and Windows is not guaranteed).
- On the Linux side, `system.posix_acl_access` (setfacl/getfacl) round-trips with the same canonical store
  ([Mount.md](Mount.md)).

### LockFile / UnlockFile

`DokanOptions.UserModeLock` is the setting that **handles LockFile / UnlockFile in userspace**. pgfs holds no
ledger of range locks, so leaving it on had the callbacks always return Success and
**lie that a lock that was never taken had been taken**. **The option was removed and it was
changed to leaving it to the Dokan driver** (DokanNet's definition: "Enable Lockfile/Unlockfile operations.
Otherwise Dokan will take care of it."). It was confirmed on real hardware that
**a byte-range lock within one mount is enforced** (`test_byte_range_lock_enforced`: the `Lock` of a second
handle gives an `IOException`, and it can be taken after the `Unlock`). If a callback is called after all, it
returns `NotImplemented` (it does not return a false success).

**A range lock between separate mounts (across clients) is unsupported.** The driver only sees inside its own
mount, and a distributed lock including deadlines, process death and lease recovery is a separate design
([the Windows parity design](design/windows-parity.md)). The database's `pgfs_lock` is for the tx updates and is
separate from an application's range lock.

### The mount point

- **A drive letter** (`P:`, `R:` and so on): give a free letter. The MountManager may assign a different letter
  on a collision. **The notification target was fixed** (the real mount point reported by Mounted
  is held and an absolute path is passed to NotifyUpdate). **The setting value and the registration row in
  `{prefix}mounts` keep the requested value and are not updated** (unfixed). Giving a letter that is already in
  use makes Dokan die with `Something's wrong with the Dokan driver` (with no pre-check)
- **A directory path** (`C:\mnt\pgfs`): give an existing **empty directory** on NTFS. While it is mounted, that
  directory's real contents are hidden

### The names that cannot be created

**Only when creating from Windows are the following names rejected with `STATUS_OBJECT_NAME_INVALID`.**
**An existing one can be opened** - so as not to make a name created from Linux unreadable on Windows.

| The name rejected | The reason (measured) |
|---|---|
| **The MS-DOS device names** `CON` / `PRN` / `AUX` / `NUL` / `COM1`-`COM9` / `LPT1`-`LPT9` (with an extension too; `CON.txt` is rejected as well) | **Creating one `NUL` makes that directory impossible to remove with `Remove-Item -Recurse`** (`ERROR_INVALID_FUNCTION`). Recovering it needs an individual delete through `\\?\` |
| **A trailing space or dot** (`trail ` / `trail.`) | **Win32's normalization drops the tail, so giving `dot.` silently returns the contents of `dot`.** **It is not an error**, so the user cannot tell that they read the wrong data |

**Some of them never reach the FS from a plain Win32 path in the first place** - `NUL` is resolved to the NUL
device by the Win32 layer and pgfs is not involved (a write succeeds but no file is created). **What really gets
created goes through `\\?\`**, so that is where it is rejected.

**`.fuse_hidden<16 hex digits>` is not hidden on Windows** (the one place the behaviour differs from Linux).
It is a mechanism for hiding the remnants libfuse creates, but **libfuse does not run on Windows so no remnant
is born**, and hiding them would leave only "**it is not in the enumeration and it cannot be removed**".
**[namespace-policy.md](design/namespace-policy.md) is the source of truth for the policy.**

### The return values of CreateFile

- `Open`: it does not exist -> `FileNotFound`, or `PathNotFound` when a directory was requested
- `OpenOrCreate`: it exists -> `AlreadyExists` (set on info.Context); it does not -> create it and `Success`
- `Create`: it exists -> discard the contents and `AlreadyExists`; it does not -> create it and `Success`
- `CreateNew`: it exists -> `FileExists`; it does not -> create it and `Success`. **The creation is
  `Api.CreateFile(exclusive: true)`**, so even a simultaneous create with another mount has only one win through
  the database's unique constraint (the loser gets `AlreadyExists`)
- `Truncate`: it does not exist -> `FileNotFound`; a directory -> `AccessDenied`; a file -> discard the contents
  and `Success`
- `Append`: it does not exist -> create it; it does -> open it as it is (Dokan raises `WriteToEndOfFile` on the
  next WriteFile)

## The known limitations and TODOs

| The item | The status | Notes |
|---|---|---|
| The data I/O (Read/Write) | ✅ | Uses `Api.ReadData` / `WriteData` as the shared foundation |
| append (`FILE_APPEND_DATA`) | ✅ | **Core decides where the end is** (`Api.AppendData`). Dokan only says "write at the end" through `WriteToEndOfFile`, so the FS writes at **the end it resolved again** (up to stage A it used the stale `Inode.Size` the handle held and **overwrote and erased what another mount had added** - `test_x_append_handle_sees_peer_growth` in [crossclient.ps1](../tests/windows/crossclient.ps1) reproduced it as a loss from 11 to 6 bytes). **[Mount.md, the append contract](Mount.md) is the source of truth for the limit of the indivisibility** (only under write-through, and only when it fits in one callback). **On Windows one `WriteFile` arrives as one callback without being split** (measured 2026-09-21) - at 64 KB, 1 MB, 16 MB and **64 MB** alike, the `NumberOfBytesToWrite` came through in one row. It was the same on all 4 paths - the raw Win32 `WriteFile`, `FILE_FLAG_WRITE_THROUGH`, `FILE_APPEND_DATA` and .NET's `FileStream` - with `NoCache=False, PagingIo=False` (a synchronous descent that is neither through the cache nor paging I/O). **Linux splits at `max_write`, so the limit of an append's indivisibility differs by OS** - do not carry Windows's limit over to Linux |
| SID <-> uname/gname resolution | ✅ | NTAccount.Translate in [WindowsUserResolver](../src/dokan/src/WindowsUserResolver.cs) |
| Reducing the chunks on a truncate | ✅ | `Api.TruncateData` (shared with Mount) |
| The ACLs (Get/SetFileSecurity) | ✅ | POSIX canonical with a Windows projected view. The SD <-> the st_mode plus the canonical ACL (`user.pgfs_acl`). owner = a group goes to nobody plus that group. Deny and the inheritance are dropped in the projection. For the details see [permission-interop.md](design/permission-interop.md). Remaining: the strict enforcement of a named ACL (awaiting a requirement) |
| The Hidden / System / Archive attributes | ✅ | Saved in the xattr `user.win.attrs` as the JSON `{hidden,system,archive}`. `ReadOnly` is the write bits of the st_mode (as before). A dotfile created on Linux shows as Hidden through the heuristic fallback |
| Alternate Data Streams | ❌ | NTFS's ADS is unsupported |
| An application's range lock | ⚠️ | **Within one mount the driver enforces it** (`UserModeLock` was removed). **Between separate mounts it is unsupported.** It is separate from Core's tx exclusion (`pgfs_lock`) |
| Creating a native symlink or hardlink, and junctions | ❌ | **A PoC was done on 2026-09-19: with the current binding neither creating nor reading works.** `IDokanOperations2` has no link callbacks and there is no entry point for getting or setting the reparse data. Measured, `mklink /H`, `/J`, `/D` and `File.CreateSymbolicLink` all fail, and **a symlink of Linux origin disappears from the enumeration and opens as an empty file when named directly**. For the details see [windows-parity.md, the reachability PoC for native links](design/windows-parity.md) |
| Notify (the notification of another client's changes) | ⚠️ | **Fixed**: an absolute path prefixed with the real mount point reported by `Mounted` (`P:\dir\file`) is passed, and the bool return is used in the failure log. **Something that is gone gets a `NotifyDelete`** (a `NotifyUpdate` is treated as an attribute change and the entry stays in the peer's cache with `Test-Path` returning true). The kind is tried as a file then a directory (the payload has no op or kind). **The visibility across two mounts was measured** (a create, an overwrite, a delete and a replacing rename follow within a few hundred ms; see [crossclient.ps1](../tests/windows/crossclient.ps1)). **Another mount's changes do not become FileSystemWatcher or Explorer events** (measured 2026-09-21). **A local operation produces plenty** (`Created` / `Changed` / `Renamed` / `Deleted` - the driver generates them), yet **not one comes from another mount**. In other words `NotifyUpdate` / `NotifyDelete` **work as a cache invalidation** (the visibility is measured as above) but **produce no `ReadDirectoryChangesW` notification**. **Reading again shows the new value, but a window that is already open does not refresh** (an F5 is needed). That is to say **Windows has no path by which "a notification fixes the display" at all**, which is a different layer from what [Mount.md](Mount.md) says about "when everything is discarded there is nothing to hand to the OS" (**even if there were, the display would not be fixed**). See the [official DokanInstance API](https://dokan-dev.github.io/dokan-dotnet-doc/html/class_dokan_instance.html) and [the parity design](design/windows-parity.md) |
| Reconnecting after a failed connection | ✅ | The `Pg.OpenConnection` family is wrapped in [Retry](../src/core/src/Utility/Retry.cs). Exponential backoff, tuned with `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms`. It does not retry arbitrary queries. Separately, Core's create / write / flush have a bounded tx retry for 40P01 and 40001 |
| The fallback for a uname or gname that does not exist on the OS | ✅ | When `NTAccount.Translate` fails, `mount.fallback_uname` / `mount.fallback_gname` (stored in the database, `nobody` / `nogroup` by default) are resolved to SIDs and returned. `nobody` / `nogroup` resolve to `NT AUTHORITY\ANONYMOUS LOGON` through the well-known mapping. If even that cannot be resolved to a SID, `WellKnownSidType.AnonymousSid` is hardcoded and a warning is logged. The implementation is [src/dokan/src/WindowsUserResolver.cs](../src/dokan/src/WindowsUserResolver.cs) |

### The additional unfixed points (**stocktaken**; the struck-through ones are fixed)

- ~~DoCreate uses the process's default owner/group rather than the requestor's~~ -> **fixed**.
  The uname is the requestor's resolved User SID, the gname is **inherited from the parent directory**, and a
  failure gives `fallback_*` plus a Warning. The audit subject was fixed the same day to being settled in
  CreateFile and held on the handle (the Windows-origin audit rows from before that have NULL `caller_uname` and
  `caller_domain`). **Remaining**: same-named users in a domain environment (`CORP\alice` and `LOCAL\alice`)
  **cannot be told apart as owners** - because `NameNormalizer` dropping the domain part is **the
  specification** (**it does survive in the audit's `caller_domain`**). The write-up is in
  [permission-interop.md, the domain drops out of the owner and survives in the audit](design/permission-interop.md).
  **Unverified in a real domain environment.**
- ~~The Inode held on the Context is not re-fetched after a remote cache invalidation~~ -> **fixed** (handle-context stage B; `Resolve` looks it up again by `InodeId` every time).
- ~~The FileIndex is inode.Id so it does not agree between hardlink siblings~~ -> **fixed**. It was
  made `data_id | 0x8000_0000_0000_0000` = **the same formula as FUSE's `st_ino`**
  ([windows-parity.md](design/windows-parity.md)).
- ~~Assign does not look at UnflushedAtShutdown after the Dispose and returns 0 on the normal path~~ ->
  **fixed (exit 4)**. That a successful Cleanup is not a durability guarantee for the application
  is unchanged, though.
- ~~SetFileAttributes rebuilds the whole permission as 0444 / 0644 / 0755 when ReadOnly changes~~ ->
  **fixed**. **It touches only the write bits** (setting it drops 0222, clearing it restores the
  owner's 0200). Before that, **setting ReadOnly on a directory made it 0444 and dropped the x bit** (the
  Windows side holds no mode so it cannot notice). It was measured that
  **a Linux mount with `-o default_permissions` can then no longer traverse it** (on a default mount the mode is
  not enforced, so a broken mode merely remains quietly). **0755 / 0644 / 0750 make a complete round trip.**
  **The remaining trade-off**: a mode that had write on the group or other loses those two across the round trip
  (there is nowhere on the POSIX side to remember the original write bits; it does not happen for a mode created
  from Windows).

The detailed grounds and the implementation proposals are gathered in
[the Windows parity design](design/windows-parity.md).

## A scenario for checking it works (assuming Windows)

```pwsh
# 1. confirm the Dokan2 driver is installed
sc query dokan2
# the STATE being RUNNING is good

# 2. initialize PGFS in PostgreSQL (see [docs/Mkfs.md](Mkfs.md))
dotnet run --project src\mkfs

# 3. mount
dotnet run --project src\assign -- -m P:

# 4. access from another terminal (PowerShell)
Get-ChildItem P:\                           # the root directory
New-Item -ItemType Directory P:\hello       # create a directory
Get-ChildItem P:\                           # hello is visible
Remove-Item P:\hello                        # remove it
New-Item -ItemType File P:\empty.txt        # an empty file
"hello world" | Out-File -FilePath P:\test.txt -Encoding utf8
Get-Content P:\test.txt                     # -> hello world
Move-Item P:\test.txt P:\renamed.txt        # rename
Remove-Item P:\renamed.txt                  # remove

# 5. unmount
# Ctrl+C in assign.pgfs's terminal
# or in another terminal:
dokanctl /u P:
```

## References to things outside the documents

> The table under the H1 is the source of truth for where to go among the documents. Only references to
> **things that are not documents** go here.

- [src/core/src/Api/Api.cs](../src/core/src/Api/Api.cs) the shared API
- [Dokan](https://dokan-dev.github.io/) the Dokan driver
- [DokanNet](https://github.com/dokan-dev/dokan-dotnet) the C# binding
