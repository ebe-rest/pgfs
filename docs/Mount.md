# The mount.pgfs specification

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **the specification of `mount.pgfs` (Linux/macOS)** - the
> CLI and the mount options (the classification of `-o`), the list of the FUSE operations that are implemented,
> **the durability contract when write-back is enabled** (what can be lost) and how the stop signals are
> handled. **The behaviour as the user sees it is authoritative here.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [Mkfs.md](Mkfs.md) | **The defaults** of the settings. The tables here are excerpts |
> | [design/write-back.md](design/write-back.md) / [design/metadata-write-back.md](design/metadata-write-back.md) | **The design and the implementation status** of write-back. This document holds only the contract |
> | [design/fuse-binding.md](design/fuse-binding.md) | The internals of the FUSE binding |
> | [design/fstab-support.md](design/fstab-support.md) | Starting through `/etc/fstab` / `mount(8)` |
> | [design/handle-context.md](design/handle-context.md) | Unifying the handle context (what `fh` means) |
> | [Assign.md](Assign.md) | The same layer on the Windows (Dokan) side |

The specification of `mount.pgfs`, the Linux / macOS tool that **mounts a PGFS filesystem through FUSE**.

This document gathers the specification as settled in the current implementation
([src/mount/](../src/mount/)), and is a separate executable from [Pgfs.Assign](../src/assign/) (the DokanNet
build) for Windows. Everything shared is gathered in
[`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs).

## Its role

It shows a PostgreSQL database initialized as PGFS (built as in [docs/Mkfs.md](Mkfs.md)) to userspace as a
Linux/macOS directory tree.

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Core.Api.Api (cross-platform)
        ↑↓
    Pgfs.Fuse.FileSystem : Pgfs.Fuse.FuseFileSystemBase (Linux/macOS-specific)
        ↑↓ FUSE
    the Linux kernel / macFUSE on macOS
        ↑↓
    cd /mnt/pgfs && ls
```

## Building and running

```bash
# build
dotnet build src/mount/Mount.csproj

# start (the default: connect to localhost:5432 and mount at /mnt/pgfs)
sudo dotnet run --project src/mount

# a different mount point
sudo dotnet run --project src/mount -- -m /mnt/myfs

# an explicit connection string
sudo dotnet run --project src/mount -- \
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    -m /mnt/pgfs

# the help
dotnet run --project src/mount -- --help
```

> **Unmounting**: `fusermount3 -u /mnt/pgfs` (Linux) or Ctrl+C (a LazyUnmount).

### Unmounting after the process has crashed

If the mount.pgfs process crashes or is SIGKILLed, **only the kernel-side mount entry is left behind**. It looks
like this under the `mount` command, and it cannot be remounted as it is:

```bash
$ mount | grep pgfs
/dev/fuse on /mnt/pgfs type fuse (rw,nosuid,nodev,relatime,user_id=1000,group_id=1000)

$ ls /mnt/pgfs
ls: cannot access '/mnt/pgfs': Transport endpoint is not connected
```

**Unmount it by hand** in that case:

```bash
# usually this is enough (the same as an ordinary unmount)
fusermount3 -u /mnt/pgfs

# force it if a permission error appears
fusermount3 -uz /mnt/pgfs    # -z = lazy (detached on the next use even if busy)
# or
sudo umount /mnt/pgfs
sudo umount -l /mnt/pgfs     # lazy
```

Recovery is complete once `/dev/fuse on .../mnt/pgfs` is gone from the `mount` command. After that it can be
remounted as usual with `mount.pgfs -m ...`.

### The prerequisites

- **The .NET 10 SDK**
- **libfuse3** and **fusermount3** installed
  - Ubuntu/Debian: `sudo apt install fuse3 libfuse3-3`
  - Arch: `sudo pacman -S fuse3`
  - It is checked at startup with `Fuse.CheckDependencies()` and it exits if anything is missing
- The mount point created in advance
  - `sudo mkdir -p /mnt/pgfs && sudo chown $USER /mnt/pgfs`
- The database side initialized as in [docs/Mkfs.md](Mkfs.md)

### The behaviour on Windows

It is made to build on Windows (for cross-compilation). Running it hits `OperatingSystem.IsWindows()` at the
head, emits an error directing the user to `assign.pgfs` (the DokanNet build) and exits. To mount on Windows,
use [src/assign/](../src/assign/).

## The settings

The settings model shares [`Pgfs.Core.Config.RootConfig`](../src/core/src/Config/RootConfig.cs). The same TOML
settings file ([pgfs.toml.example](../pgfs.toml.example)) and the same command-line options as Mkfs are
available. For the details see [docs/Mkfs.md](Mkfs.md).

What mount.pgfs uses in particular:

| The setting | The argument | The default |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Linux/macOS: `/mnt/pgfs` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `mount.max_write` | `--max-write` | `0` = left to libfuse's negotiation (measured, it goes up to the kernel limit of 1 MiB by default). The maximum bytes of one FUSE WRITE request. It is separate from the flush tx granularity under write-back. It is for lowering it, or pinning it across libfuse versions (`-o max_write` is accepted too, but libfuse3 refuses it as a mount option, so **pgfs routes it into this Field and sets it in the init callback**) |
| `logging.level` | `--log-level` | `information` |
| `database.notify_enabled` | `--notify` | `false`. Recommended when several clients mount the same PG/pgfs. For the details see "the notification of another client's changes" below |

The same TOML as Mkfs can be used for the `pgfs.toml`.

## write-back (`mount.write_back` / `mount.write_back_metadata`)

> A comparison against the current code: 2026-09-19 (after the round A fixes). The 4 points once listed here
> (a dirty loss through a hardlink / the synchronous-close mark / failure detection / the wait deadline) were
> **fixed in round A on 2026-09-19** and verified on the dev server on real hardware
> ([the round A fixes](design/metadata-write-back-reviews.md)). **B-1 to B-13 were addressed too.**
> What follows explains the behaviour and the intent of the implementation and does not guarantee
> durability in every scenario. For the limitations that remain, see
> [CHANGELOG.md, the known limitations](../CHANGELOG.md).

The speed-up option of accumulating the writes in memory and writing them "one file = one transaction".
**Both are off by default**, and with them off it is the write-through as before (each write's transaction is
committed synchronously). The source of truth for the design is
[write-back.md](design/write-back.md) / [metadata-write-back.md](design/metadata-write-back.md), and every
setting is in [settings-matrix.md](design/settings-matrix.md).

| The setting | The argument | The default | What it means |
|---|---|---|---|
| `mount.write_back` | `--write-back` | `false` | write-back the data (the chunks). It flushes on an `fsync`, a `close`, the time, the dirty ceiling or the unmount |
| `mount.write_back_max_bytes` | `--write-back-max-bytes` | `64 MiB` | The dirty-byte threshold at which a flush starts. It does not guarantee staying under the ceiling if that fails |
| `mount.write_back_interval_ms` | `--write-back-interval-ms` | `1000` | The interval of the background flush. `0` disables the time trigger |
| `mount.write_back_metadata` | `--write-back-metadata` | `false` | write-back **the metadata too** (it requires `write_back`). **The durability contract of `close` changes** - see below |
| `mount.write_back_metadata_exclusive_create` | `--write-back-metadata-exclusive-create` | `write_through` | How a create with `O_EXCL` / `CREATE_NEW` is treated. `defer` makes `rsync` and `cp` **3.28x** faster at the price of **losing the cross-client exclusion** - see below |
| `mount.write_back_max_inodes` | `--write-back-max-inodes` | `4096` | The pending-inode-count threshold at which a flush starts. On going past the deadline it warns and carries on over the ceiling |
| `mount.write_back_flush_timeout_ms` | `--write-back-flush-timeout-ms` | `30000` | The retry deadline for the back-pressure and the unmount. **The deadline is checked inside one sweep too**, but a database call in flight cannot be aborted so it is not a strict ceiling (`0` = try one round only) |

**The bool flags take no value.** Writing `--write-back true` makes the `true` fall into a positional argument,
so pass them **as bare flags** (`--write-back --write-back-metadata`).

### Do not write the same file from several mounts with `mount.write_back` on (a contract)

**While a mount with `mount.write_back = true` is holding unflushed data, a write to the same body from another
mount disappears at the flush.** That is because the dirty buffer is held as **the complete image of a chunk**
and the flush writes that whole image back.

- **What disappears is whole chunks, not individual bytes**, and the target is
  **every chunk the write-back side has ever read** (the default chunk is 1 MiB).
  **Even regions that were not written are taken with it.**
- **`--notify` does not prevent it.** The notification drops the content cache, but
  **the dirty state is deliberately left alone**.
- **The rewinding of `st_size` is closed**. Before that, a flush rewound another mount's `truncate`
  and the bytes that should have been gone could be read. **The size is correct now, but the trampling of the
  bytes remains.**

**In a configuration where the same file is written from several mounts, leave `write_back` off (the
default).** Reading and writing from a single mount, and a configuration where **each mount writes different
files**, are unaffected.
[design/write-back.md, the cross-client contract](design/write-back.md) is the source of truth for the mechanism
and the measurements.

### What is lost by turning `mount.write_back_metadata` on (a contract)

`write_back_metadata = true` means "hold the inodes this mount created (create / mkdir / symlink) in an
in-memory ledger rather than writing them to the database, and fold
`create -> write -> close -> chmod -> utimens -> rename` into one tx".
**`close(2)` stops flushing synchronously and only `fsync(2)` / `fsyncdir(2)` are hard barriers.**
The 5 things that are lost concretely are below, and **if that is unacceptable, leave it off** (that is also why
it is off by default). The past figure of about 2.1x was a projection, and the measured record of stage 2 is
`rsync` 1.00x and a combined 1.30x for a non-`O_EXCL` create (**both, before the A-10 fix**;
it has not been re-measured since overwriting a persisted entry went back to a synchronous close). For the
details see [performance.md](design/performance.md).

| The item | What is lost |
|---|---|
| **The durability of close** | **Only for a file this mount newly created (pending-born)**, a crash before the flush even after a `close` (the background attempt interval is 1000 ms by default, but there is no time limit on the loss window when something fails) **loses the whole file** (it is not that the contents become empty, but that the file never existed). Only what was `fsync`ed or `fsyncdir`ed survives. **An overwrite of an existing file is written out at the `close`** (below) |
| **Causality between files** | Because a delete or a rename (write-through) **overtakes** a pending create, a crash during an operation touching several files (`git checkout`, `rsync --delete` and so on) can leave "neither the old nor the new" = a state that appears in no serial history. **The materialization of a pending inode is one tx** (the final name, the attributes, the data that flush took in, and the audit). With `rm f; cp new f` there is also a window of losing the new after removing the old |
| **Visibility to other clients** | A pending inode is **invisible to other clients** until the flush (the background flush's delay, a database failure or interval=0 mean there is no guaranteed time limit on it becoming visible). A tool that uses `mkdir` as a locking primitive can have two clients succeed at once. **A create with `O_EXCL` is created synchronously under the default (`write_through`)** so the exclusion is kept (for `git index.lock` and the like). **Setting `mount.write_back_metadata_exclusive_create = defer` loses that guarantee too** (only the exclusion within one mount remains) - see below. When two same-name creates collide across clients it is last-flush-wins (the collision is recorded in the audit plus the statistics) |
| **`du` / `df` / `status`** | The pending share does not appear in the database-side aggregates until the flush (the `df` and the used of `pgfsctl status`). This client's own `stat` and `du` use the in-memory values too |
| **The relation to the existing tests** | The "it survives even if the process is killed after the close" test of [tests/linux/writeback.sh](../tests/linux/writeback.sh) is **the contract with `write_back_metadata` off**. With it on, "what was fsynced survives / what was only closed may disappear" is the correct behaviour |

**The atomicity of a single file only improves for the pending-born (the files this mount created).**
An overwrite of a body already in the database (persisted) can be split across several flush txs, so a crash can
leave a chimera with "a new first half and an old second half". **A synchronous-close mark is therefore put on
as soon as a write to a persisted inode starts, and that `close` is upgraded to a synchronous flush**.
= What loses the durability of `close` with `write_back_metadata = on` is **a newly created file**, and an
overwrite of an existing file is written out at the `close` just as it is with it off.

**The exceptions where the contract does not loosen (the synchronization heuristics)** - the following are
implemented to avoid the accident shaped like "destroy the old immediately and defer the new":

1. **rename-over-existing** (`mv new existing` / an editor saving / `sed -i` / dpkg): "deleting the replaced
   target plus materializing the new file plus the data plus the audit" is **run synchronously in a single tx**.
2. **A file whose old chunks were discarded by `O_TRUNC` / `truncate(2)`**: a synchronous-close mark is put on
   **both the inode and the data body**, and that close is upgraded to a synchronous flush. The mark is cleared
   **only when something unflushed was actually written out**, so it protects even when the truncate and the
   append are on different fds, as in `truncate -s 0 f; cmd >> f`, and through a hardlink sibling.
   When the marks reach the ceiling (4096) they are not discarded but **every close is upgraded to
   synchronous** as a degradation.
3. **An `O_EXCL` create**: it is a locking primitive, so it is not deferred but created synchronously, leaving
   the exclusion decision to the database's unique constraint.
4. **An overwrite of a persisted inode**: as soon as a write to a body already in the
   database starts, a synchronous-close mark is put on and that `close` is upgraded to a synchronous flush
   (= close-no-flush is confined to newly created files).

### `mount.write_back_metadata_exclusive_create`

The knob for whether a create with `O_EXCL` / `CREATE_NEW` becomes pending. **The default `write_through` is
the behaviour of 3 above** (a synchronous create) and there is no need to change it.

| The value | The speed | The exclusion |
|---|---|---|
| **`write_through` (the default)** | 1e has no effect on `rsync` or `cp` (measured 1.00x) | The cross-client `O_EXCL` exclusion **works** |
| `defer` | `rsync` gets **3.28x** (37.1 -> 11.3 ms/file; see [performance.md](design/performance.md)) | The cross-client exclusion is **lost**. Only the exclusion within one mount remains |

Both `rsync` and `cp` open a new destination with `O_CREAT|O_EXCL`, so with the default nothing of 1e's folding
takes effect on a representative bulk copy. `defer` makes it take effect at the price of accepting
**both clients' exclusive creates succeeding**.

- **Do not use `defer` if several clients use the same FS.** A tool that uses a lock file
  (`git index.lock` and the like) has two machines both decide "I took it".
- **The exclusion within one mount is kept** (the pending ledger rejects a `(parent, name)` collision with
  `EEXIST`).
- **It does not silently overwrite on a collision.** The side that reached the database first (the winner) is
  untouched, and the side flushing later (the loser) keeps failing its flush, with `fsync` returning `-EIO` and
  a red line in `pgfsctl status`.
  **The recovery is deleting that file on the loser's side** (it is a cancellation of a pending entry so it does
  not touch the database). Deleting it clears the error state too and later writes and creates get through.
- Starting with `defer` while other running mounts are in `{prefix}mounts` gives **a warning** (it does not
  refuse).

### The error reporting paths (to make up for close no longer returning -EIO)

1. **The `close` of a file that latched a flush failure retries the synchronous flush** and returns `-EIO` if
   the retry fails too (only the close of a clean file merely marks).
2. **The unmount retries the flush up to `mount.write_back_flush_timeout_ms`.** If it cannot be written out
   within the deadline, **what is being lost (the path, the id, the size, the error) is enumerated in an `Error`
   log** (the first 32; the rest is emitted not as "and N others" but with **the total, the split of dirs and
   files and the logical byte count**) and `mount.pgfs` is designed to exit with **exit code 4**.
   **But exit 4 does not reach the caller** - the daemonized parent returned `0` at the point the mount was
   established and exited first. Instead, **the loss is left on the database side**: the
   `{prefix}mounts` row is not DELETEd but left as a gravestone with `unflushedLoss` / `endedAt`
   (**UTC with a trailing `Z`**; the log lines' times are local, so without the marker it looks like a different
   run offset by the timezone) on its `stats`, and with `audit.enabled` an audit row with
   `op = writeback_loss` is written too. **The next mount warns on the parent process's stderr**, and it shows
   in red in `pgfsctl status` too. The gravestone **does not go away automatically**, so once it has been seen,
   remove it with
   `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0`.
   **The deadline is checked inside one sweep too**, but one transaction in flight cannot be aborted so it can
   overshoot the given time somewhat. The loss log is at most 32 entries each for the pending inodes and the
   dirty data plus the audit row count, not a complete listing. The exit code 4 at the end does not reach the
   exit code of the daemonizing parent. `fusermount3 -u` and `umount(8)` complete on the kernel side so the FS
   cannot refuse with EBUSY (refusing would create "a mount that can never be unmounted") - the log and the exit
   code are the last reporting path.
3. **5 consecutive flush failures put the mount into an error state**, which refuses with `-EIO` not only new
   writes and creates but **also the operations that destroy a persisted body (`unlink` / `rmdir` /
   `truncate` / a replacing rename)**. **The operations that merely throw away this mount's
   unflushed work (deleting or truncating a pending entry) get through**, so the path of deleting a colliding
   pending entry to recover is not blocked. **`rm -rf` partially fails on a directory mixing pending and
   persisted entries** (not silently removing what is being held was given priority) (so as not to keep piling
   up data that has succeeded but can never be flushed). `!! write-back ERROR STATE` shows in red in Layer 3 of
   `pgfsctl status`. **The consecutive-failure counter is per flush target**, so another file's success cannot
   hide a permanent failure. It clears **when not one target is at the threshold any more**.
   The error state itself is a state of the whole mount.
4. **The back-pressure blocks.** When the dirty bytes or the pending inodes go over the ceiling, writes and
   creates wait until it is back under the ceiling or `mount.write_back_flush_timeout_ms` is used up.
   **The deadline is checked inside one sweep too** (fixed), but going past it warns and carries on
   over the ceiling, so it is not a strict time or memory limit.

### The staging of the stop signals

The flush at unmount persists for up to `mount.write_back_flush_timeout_ms` (30000 ms) by default, during which
`mount.pgfs` looks like "a process that will not stop". **The stop signals (SIGTERM / SIGINT) advance a stage
with each one** (SIGTERM and SIGINT share the same counter).

| The count | What it does |
|---|---|
| The 1st | A graceful unmount. It tries to write out whatever is unflushed up to the deadline |
| The 2nd | **Abandons the persistence of the flush.** It gives up at the next deadline check and **exits 4 after emitting the loss report** (it waits a little, since one transaction in flight is not aborted) |
| The 3rd and later | Back to the default behaviour (an immediate exit). **Neither the loss report nor the `{prefix}mounts` gravestone remains** |

> **When stopping it with systemd**: make `TimeoutStopSec` **longer than
> `mount.write_back_flush_timeout_ms`**. Too short and a SIGKILL comes right after the SIGTERM, which loses this
> staging and the loss report alike (for example, `write_back_flush_timeout_ms = 30000` goes with
> `TimeoutStopSec=60s`).

## The FUSE operations that are implemented

[src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) inherits
[`Pgfs.Fuse.FuseFileSystemBase`](../src/fuse/src/FuseFileSystemBase.cs) and overrides the following.

| The operation | Supported | Notes |
|---|---|---|
| `GetAttr` | ✅ | Maps the inode into a `stat` struct |
| `OpenDir` | ✅ | Checks the existence and the kind, and saves the inode id in `fi.fh` |
| `ReadDir` | ✅ | `.` and `..` plus enumerating the child inodes |
| `ReleaseDir` | ✅ | A no-op |
| `MkDir` | ✅ | `Api.CreateDirectory` |
| `RmDir` | ✅ | `Api.DeleteInode` after the emptiness check |
| `Create` | ✅ | `Api.CreateFile` (an empty file) |
| `Unlink` | ✅ | `Api.DeleteInode` (releasing the bytea chunks and the data row too if it was the last data reference) |
| `Rename` | ⚠️ | `Api.Rename` (a replacement is in the same tx). Any flag other than `RENAME_NOREPLACE` is refused with `-EINVAL`. **The exchange of `RENAME_EXCHANGE` itself is unimplemented** |
| `ChMod` | ✅ | `Api.UpdateMode`, preserving the kind bits |
| `Chown` | ✅ | `Api.UpdateOwner` after resolving the uid/gid to a uname/gname with the `UserResolver` |
| `Truncate` | ✅ | `Api.TruncateData` (trimming the bytea chunks too) |
| `UpdateTimestamps` | ✅ | Only the `mtime` is reflected in the database; the `atime` is ignored (by requirement) |
| `Open` | ✅ | Checks the existence and the kind, does an `O_TRUNC` when needed, and saves the inode id in `fi.fh` |
| `Flush` | ✅ | `Api.CloseInode` at close. With the metadata write-back enabled it is asynchronous as a rule |
| `FSync` | ✅ | `Api.FlushInode`. Returns a failure as `-EIO` |
| `FSyncDir` | ✅ | `Api.FlushDirectory`. Materializes its own pending ancestors and the pending children directly under it |
| `Release` | ✅ | Best-effort handling through `CloseInode`. It is not a path for returning an error to the application |
| `Read` | ✅ | `Api.ReadData` (through the bytea chunks, with holes zero-filled) |
| `Write` | ✅ | `Api.WriteData` (through the bytea chunks, creating the data row automatically on the first write) |
| `StatFS` | ✅ | Through `Api.GetStatFs`. If mkfs `--statfs` created `{prefix}statfs()` (plperlu), it is the tablespace's **real free disk space**, otherwise the nominal capacity (`max_file_size` - `pg_database_size`). For the details see [docs/df-support.md](design/df-support.md) |
| `GetXAttr` | ✅ | Fetched from the parallel arrays `xattr_names` / `xattr_values` (the cache is an in-memory search; the database is `xattr_values[array_position(xattr_names,@name)]`). The value is bytea-transparent. `system.posix_acl_access` is a special case (see the ACL section) |
| `SetXAttr` | ✅ | Replaces the existing index or appends at the end in a single UPDATE (`array_position` plus slicing, atomically), honouring the `XATTR_CREATE` / `XATTR_REPLACE` flags. `system.posix_acl_access` is a special case |
| `ListXAttr` | ✅ | Enumerates the `xattr_names` as they are and returns them NUL-terminated |
| `RemoveXAttr` | ✅ | Removes the name's index from both arrays in a single UPDATE (a slice concatenation, atomically). `system.posix_acl_access` empties the named entries (the equivalent of `setfacl -b`) |
| POSIX ACLs (`system.posix_acl_access`) | ✅ | Round-trips with setfacl/getfacl. The `st_mode`'s three basic classes plus the canonical ACL (the named entries of `user.pgfs_acl`) <-> the ACL binary. The mask is computed automatically, and with no named entries it is ENODATA. It shares the same canonical store as the Windows DACL (see the ACL section / [permission-interop.md](design/permission-interop.md)) |
| `SymLink` | ✅ | `Api.CreateSymlink` (`S_IFLNK \| 0777`, stored in the `link_target` column) |
| `ReadLink` | ✅ | Returns the `inode.LinkTarget` NUL-terminated |
| `Link` | ⚠️ | `Api.CreateHardLink`. Sharing an existing data_id is implemented. **The null data_id of an empty file, the dirty loss and the splitting of the sharing by `truncate` / `O_TRUNC` were fixed** ([data-id-lifecycle.md](design/data-id-lifecycle.md)). **What remains is only that the attributes (the mode and the owner) are not shared between siblings**, and `Api.UpdateMode` updates just its own inode row ([CHANGELOG.md](../CHANGELOG.md), the known limitations) |

The key: ✅ handled, ⚠️ with limitations or defects, ❌ unimplemented (`-ENOSYS`). It is not a classification of
what has been verified on real hardware.

This table is a list of the main operations, not a correspondence table of every FUSE operation. `Access` and
`FAllocate` stay at the base's `-ENOSYS`. A distributed byte-range lock and the like are unimplemented too, and
are separate from the `pgfs_lock` used for the database updates.

## The notification of another client's changes (Notify)

Enabling it with `database.notify_enabled=true` receives other clients' writes through PostgreSQL's `LISTEN` /
`NOTIFY` and invalidates the local `InodeCache`
([src/core/src/Api/NotifyChannel.cs](../src/core/src/Api/NotifyChannel.cs)). When several `mount.pgfs` or
`assign.pgfs` instances share the same PG/pgfs, a writing client's change is reflected in the other clients'
`stat` and `ls`.

**The constraint on the Linux side**: the current `Pgfs.Fuse` does not register an OS notification bridge. On
receipt it invalidates the `InodeCache` and the `ContentCache`, but there is no implementation that actively
invalidates the kernel's page and dentry caches.

- `attr_timeout=0` is a setting for the kernel attribute cache. It is not an instruction to re-fetch Core's
  cache from the database every time.
- A change from another client needs `database.notify_enabled=true` on both the sending and the receiving side
  of the data notifications.
- **When the send queue overflows, one "discard the whole cache" is sent**. When the send
  queue (1024 entries) is full, notifications are dropped and **those notifications never arrive**. A positive
  entry in the `InodeCache` has **no TTL**, so unless it is dropped **the receiver keeps returning a stale value
  forever**. At the moment something is dropped, it is folded into one "we do not know which id, so discard
  everything", and **it is always sent the next time the send worker runs** (at shutdown it sends the backlog
  before finishing too). The receiver discards **only the clean entries** of the `InodeCache` and the
  `ContentCache` - **the unflushed dirty data and the metadata write-back's pending inodes (which have no row in
  the database) are kept**. Discarding is safe for the discarding side, so firing it generously breaks nothing.
- **Re-synchronizing after a disconnected notification is unverified** (the re-synchronization above is about
  "the sender noticed that it dropped something"; **if the receiver had dropped its LISTEN, nobody can notice**).
- **Recovering the cache and recovering the display are different things.** Receiving a discard-everything drops
  the `InodeCache` and the `ContentCache`, so **opening it again gives the correct value**. But
  **there is nothing to hand to the path that tells the OS "this changed" (`Api.OsBridge`) on a
  discard-everything** - "not knowing which id changed" is what a discard-everything means. Linux (Mount) does
  not register an OS notification bridge, so that path does not exist at all.
  **And on Windows (Assign) the display would not be fixed even if it could be handed over** -
  **another mount's changes do not become FileSystemWatcher or Explorer events** (measured on real hardware on
  2026-09-21: a local operation produces events, yet not one comes from a remote one).
  **Windows has no path by which "a notification fixes the display" at all**, so, quite apart from a
  discard-everything, **a window that is already open keeps its stale display**. [Assign.md](Assign.md) is the
  source of truth.
- There is no handling that notifies a remote change to inotify. It cannot be lumped together as "FUSE does not
  fire" even for the notifications from local VFS operations. Nor does merely adding a cache-invalidation API
  guarantee remote inotify. See the
  [fsnotify design notes of libfuse](https://github.com/libfuse/libfuse/wiki/Fsnotify-and-FUSE). The behaviour
  per notification kind on real pgfs hardware is unverified.
- The control LISTEN (`set` / `reload` / `ping`) starts regardless of `notify_enabled`. It is separate from
  opting into the data-change notifications.

For the detailed specification, the payload and the receiving side see "the notification of another client's
changes (Notify)" in [history.md](history.md).

## The architecture

### The layers

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Mount.Program         ── argument parsing, OS check, mounting │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Fuse.FileSystem       ── the FUSE callbacks (Linux) │
│   - FillStat / ResolveOwner    * Linux-specific          │
│   - CurrentUserNames           * applicable on Windows too │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.Api          ── the database operations (cross-platform) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.InodeCache   ── the in-memory inode cache  │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Utility.Pg       ── the Npgsql + Dapper wrapper │
└─────────────────────────────────────────────────────────┘
```

### The parts that can be shared with Windows

The logic inside FileSystem.cs marks the parts that can be reused on the Windows (DokanNet) side
[`Pgfs.Assign`](../src/assign/) with a `// Windows-shareable:` comment.

- **Splitting the path** (`SplitParent`): OS-independent once the separator is a parameter. Mount fixes it at
  `/`.
- **Deciding a new inode's uname/gname** (`CurrentUserNames`): the policy of making the caller the owner can be
  shared. FUSE has `fuse_get_context` and Dokan the requesting token as the candidates.
- **Converting a byte-string path to a string** (`PathToString`): the UTF-8 decode itself is OS-independent.

Conversely, what is **Linux-specific**:

- The parts handling `Tmds.Linux.stat` / `statvfs` / `mode_t` / `uid_t` / `gid_t` / `timespec`
  (`FillStat`, `StatFS`)
- The calls to `getuid()` / `getgid()` (through `Tmds.Linux.LibC`)
- Linux-specific flags such as `RENAME_NOREPLACE`

They are replaced with NTSTATUS / FileAttributes / DOKAN_FILE_INFO and so on on the Windows side.

## The design decisions, the differences and the provisional implementations

### The threading

It returns `SupportsMultiThreading => true`, and Pgfs.Fuse's binding calls the FUSE callbacks on several
threads. The `InodeCache` synchronizes with `lock(this)` and `Pg.cs` leaves it to the `NpgsqlDataSource`'s
connection pool. The Api's updates are handled by txs containing several SQL statements and by the write-back
ledgers. It does not mean the whole callback is atomic in a single SQL statement.

But "a combination of several operations" (`MkDir`'s parent existence check -> CreateDirectory) is
**not atomic**. When two `MkDir`s arrive for the same path at once, the database's `UNIQUE(parent_id, name)`
lets exactly one succeed, and the other has `Api.CreateDirectory` return null and gives `-EEXIST`. That is
reasonable behaviour.

### How the credentials are handled

- **A new inode's uname/gname**: the uid/gid of `Fuse.TryGetCallerContext` are resolved to names. When they
  cannot be obtained it falls back to the process-derived defaults.
- **The uid/gid resolution**: [`UserResolver`](../src/fuse/src/UserResolver.cs) caches the forward and reverse
  results in a `ConcurrentDictionary`. On a cache miss, the libc calls are **all 4 of `getpwnam_r` /
  `getpwuid_r` / `getgrnam_r` / `getgrgid_r`, the `_r` versions** (switched from the non-reentrant ones).
- **`Chown`**: the given uid/gid are resolved to names with `UnameOf` / `GnameOf` and the database is rewritten
  with `Api.UpdateOwner`. The convention `uid == 0xFFFFFFFF (-1)` (= do not change) is honoured.

### The data I/O (Read / Write)

PGFS's bodies are designed to be split into `pgfs_data` plus `pgfs_data_chunk` (one row = one bytea), and
reading and writing from the Mount side are **implemented**:

- `Read` is handled by `Api.ReadData`. With the read cache enabled or write-back enabled it reads full chunks
  and slices them in memory. With both disabled it is a partial read through
  `substring(payload from N for M)`. Holes are zero-filled.
- `Write` is handled by `Api.WriteData`, which with write-back disabled creates the chunk rows as needed and
  extends the `st_size` after writing. It completes in one upsert per chunk
  (`INSERT ... ON CONFLICT DO UPDATE SET payload = CASE ... END`), and a concurrent write race is serialized
  automatically by PG's row lock.
- `Truncate` (and `Open(O_TRUNC)`) has `Api.TruncateData` trim the last chunk's payload with a `substring`
  (or extend it with zero padding) and DELETE the chunks that are no longer needed.
- `Create` creates the inode of an empty file. The chunk rows are created on the first `Write`.

> For how the move to bytea came about see [docs/support_for_citus.md](design/support_for_citus.md) (Citus
> Phase 1). The old design's large objects (`pg_largeobject`) cannot be distributed by
> Citus, hence the migration.

#### How `.fuse_hidden*` looks (when an open file is deleted)

**`unlink`ing an open file has libfuse substitute a rename to a hidden name** (`.fuse_hidden` plus 16 hex
digits). It is how POSIX's "the name goes but the fd lives" is realized, and **in pgfs the hidden name becomes a
real file in the database**.

- **They are removed from the enumeration (`ls` / `readdir`)** - without that they
  **appear in other mounts' `ls` (including Windows)**.
  The decision matches **libfuse's format strictly**, so a name a user gave such as `.fuse_hidden...` is not
  hidden.
- **They are visible when named by path directly** (a `stat .fuse_hiddenXXXX` works). That is needed so that
  **the very mount keeping that fd alive can look up its own hidden file**.
- **`kill -9`ing the daemon leaves them with their contents** (the cleanup is in the memory of the dead
  process). The cleanup is **[`pgfsctl prune`](Pgfsctl.md)**.
- **An `rmdir` can give `ENOTEMPTY` although nothing is in the enumeration** (a parent with a hidden file left
  in it). **When it looks empty but cannot be removed**, fire `prune`.

> **It was decided not to take `hard_remove = 1` (= realizing the POSIX semantics in pgfs itself).** The reason,
> and how far it was tried and where it dead-ended, are recorded with the measurements in
> [handle-context.md, why `hard_remove` is not raised](design/handle-context.md).

#### The append (`O_APPEND`) contract

**An append lands at "the end as it is now" and no bytes are lost even in parallel with another mount**
(under write-through).

pgfs **does not use the offset the OS decided**. Using it **overwrites and erases the peer's bytes**:

- **Linux**: the kernel decides it from its own `i_size` and sends it down. It does not know another mount's
  append so it is stale (measured: A's append after B extended it by 6 bytes came down at `off=5` and B's 6
  bytes were lost).
- **Windows**: Dokan only says `WriteToEndOfFile` so pgfs decides. When it used the stale `Inode.Size` the
  handle held, the same loss appeared (11 -> 6 bytes).

**The end is settled inside the write transaction** (`Api.AppendData` -> `WriteDataThrough(appendAtEnd)`).
Once `LockData` is taken, **the writers to the same body are serialized**, so the `st_size` read there is
**the real end including the previous commit**. **No row lock was added** - it is a plain SELECT so it creates
no deadlock edge and does not break the Citus shard-touch convention (the inode is touched once at the end of
the tx).
**It was confirmed on real Citus rf=2 hardware that the peer's commit is visible after the wait.**

**The range of the guarantee**:

| | The guarantee |
|---|---|
| **write-through (`mount.write_back` = off)** | **No bytes are lost**, in parallel with another mount and even when it is split past `max_write` (each callback re-takes the end inside the tx) |
| **With write-back enabled** | **Not guaranteed.** There is no tx, and **the peer mount's bytes are not in the database yet**, so there is no way in principle to know "the real end". **The local dirty size is the authority** |

> **One whole `write(2)` is not indivisible.** A write past `max_write` is split by FUSE into several callbacks
> (the negotiated value varies by environment; on the measured host it was **1 MiB**, and a 4 MiB append became
> 1 MiB x 4), so **another mount's append can slip into the gaps**. Nothing is lost when it does (each callback
> writes on from the previous commit), but **one `write(2)` is not guaranteed to be a contiguous region**.
> A local FS holds `i_rwsem` for the whole of one `write(2)` so it is contiguous, but
> **a FUSE callback is an independent request and pgfs cannot create that guarantee** (even putting "this
> `write(2)` is still going" on the handle would leave the lock behind if the process died midway).

> **That is a property of the FUSE side, and Windows (Dokan) does not split** (measured on real Windows hardware
> on 2026-09-21). At 64 KB, 1 MB, 16 MB and **64 MB** alike, one `WriteFile` arrived as **one callback**
> (the same on all 4 paths: the raw Win32 `WriteFile`, `FILE_FLAG_WRITE_THROUGH`, `FILE_APPEND_DATA` and .NET's
> `FileStream`; with `NoCache=False, PagingIo=False` = a synchronous descent that is neither through the cache
> nor paging I/O).
> **In other words "the ceiling at which one write becomes indivisible" differs by OS** - do not carry the
> `max_write` story above over to Windows, nor Windows's 64 MB over to Linux. The Dokan-side as-built is
> authoritative in the append row of [Assign.md](Assign.md).

**Measured** (appending from two mounts at once: A does 4 MiB in one go while B interleaves 10 bytes x 12):

| | The result |
|---|---|
| Before anything | **-4,194,304 bytes** (the `st_size` rewound and the chunks were in the database but unreachable from the FS) |
| Only making `st_size` monotonic | Linux -40 / Windows -10 bytes (the rewinding was gone; **what remained was deciding the append target outside the tx**) |
| **Settling the end inside the tx (current)** | **0 (green on both operating systems)** |

The regressions are the `..._concurrent_append_keeps_all_bytes` family in
[tests/linux/crossclient.sh](../tests/linux/crossclient.sh) and
[tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1). **The order (how it interleaves) is not
watched** - it depends on the timing of the interruption and is unstable, so **only the total byte count that
was promised in the contract** is asserted.

> **.NET's `FileMode.Append`, and an application that seeks by itself before writing, are out of scope for this
> contract.** The FileStream **seeks to the end at open time and writes at an explicit offset**, so it is
> **the offset the application decided for itself**. The FS merely writes where it was told and does not know
> whether that is the end. Use the shell's `>>` (a plain `O_APPEND`) or Win32's `FILE_APPEND_DATA`.

### The extended attributes, the symlinks and the hardlinks

All implemented:

- The extended attributes are stored in the parallel arrays `pgfs_inode.xattr_names TEXT[]` plus
  `xattr_values BYTEA[]` (`Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr`, with the values held
  faithfully as bytea). `GetXAttr` and `ListXAttr` search the cached `Inode.xattr_names` / `xattr_values`
  in memory (against SELinux's frequent `security.selinux` probing). The design is in
  [xattr-bytea.md](design/xattr-bytea.md).
- A symlink is stored in the `link_target` column (`Api.CreateSymlink`). `ReadLink` returns it NUL-terminated.
- A hardlink creates several inodes sharing the same `data_id` (`Api.CreateHardLink`). The `st_nlink` is updated
  in sync across every link.

### The POSIX ACLs (`system.posix_acl_access`)

They round-trip with `setfacl` and `getfacl`. The getxattr and setxattr of `system.posix_acl_access` are a
special case, converting between the ACL binary and
**the `st_mode`'s three basic classes plus the canonical ACL document (the named entries of `user.pgfs_acl`)**
(`BuildPosixAccessAcl` / `SetPosixAccessAcl` in
[src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs), with the codec in
[PosixAcl](../src/fuse/src/PosixAcl.cs)).

- The entry order is USER_OBJ -> USER* -> GROUP_OBJ -> GROUP* -> MASK -> OTHER. The mask is recomputed each time
  as the union of the group_obj and every named entry.
- A "minimal ACL" with no named entries returns ENODATA, following the convention of getfacl deriving it from
  the mode.
- This canonical store is shared with the Windows DACL (`Get/SetFileSecurity`). The design of POSIX canonical
  plus a Windows projected view is in [permission-interop.md](design/permission-interop.md).
  `system.posix_acl_default` is passed through as it is today (a round trip within Linux only).

### `st_atime`

The requirement: the last access time is not held and the same value as `st_mtime` is returned. The
implementation does exactly that (`s.st_atim = mtime.ToTimespec()` in `FillStat`).

### The mount options (`-o key=val,flag,...`)

`-o` is accepted through `mount -t pgfs`, through fstab and on a direct start alike. The parsing is done by
[ConfigLoader.ParseDashOOptions](../src/core/src/Config/ConfigLoader.cs), which classifies each key into
exactly one of the following **classes** (that method is the source of truth for the compatibility map). For the
`mount(8)` helper calling convention, the fstab entry format and the details of mounting automatically at boot,
see [fstab-support.md](design/fstab-support.md).

| The class | Examples | How it is treated |
|---|---|---|
| **(1) FUSE passthrough** | `allow_other` `allow_root` `default_permissions` `ro` `auto_unmount` `kernel_cache` `auto_cache` / with values: `umask=022` `uid=` `gid=` `fsname=` `subtype=` `entry_timeout=` `attr_timeout=` | Forwarded verbatim to libfuse ([Program.RunFuseMountAsync](../src/mount/src/Program.cs) concatenates them after `attr_timeout=0`, so the last wins and it can be overridden) |
| **(2) Accepted and ignored** | `rw` `nonempty` `direct_io` `defaults` `nofail` `noauto` `_netdev` `user(s)` `owner` `group` the `noatime` family `nostrictatime` `lazytime`/`nolazytime` `mand`/`nomand` `iversion`/`noiversion` `comment=` `nosuid`/`nodev` `exec`/`async` and so on | Kernel mount-layer or fstab conventions that **mean the same thing if they are dropped**. They are **not passed** to FUSE (accepted silently). `nosuid` and `nodev` are **always added by fusermount3 for an unprivileged mount**, so giving them changes nothing (confirmed in `/proc/self/mountinfo` on real hardware) |
| **(2″) Accepted but not applicable** | `noexec` `suid` `dev` `sync` `dirsync` / **`max_read=` `max_readahead=`** | Ignored **with a Warning**. They **change the meaning if they are dropped**, so it does not stay silent. Confirmed on real hardware: even with `-o noexec` it does not appear in `/proc/self/mountinfo` and **a script on that mount could be executed** = the execution restriction is not in effect. `suid` and `dev` point the other way: even when asked for, fusermount3 forces `nosuid,nodev` |
| **(2′) Userspace prefixes** | `x-systemd.automount` `x-systemd.requires=` `x-gvfs-show` `x-mount.mkdir` | The `x-` prefix is ignored wholesale like (2) (fstab extensions interpreted by systemd, gvfs and the like) |
| **(3) pgfs settings** | `-o schema=foo` `-o cache-max-entries=2048` | `-` and `_` are normalized and it flows into the settings [Field](../src/core/src/Config/Field.cs) (matched by `Scope.Key` or the dash-o name) |
| **(4) Unknown** | `-o allwo_other` (a typo) | Ignored with **a Warning log** (so that it is noticed at startup) |
| **(5) Unsupported mount operations** | `remount` `bind` `rbind` `move` | Ignored with **a dedicated Warning** ("unsupported in pgfs"). It is distinguished from a typo (4) to prevent the misunderstanding of "I thought I remounted" |

The points:
- `-o ro` has the kernel make the mount `MS_RDONLY` through libfuse and the kernel rejects the writes (no FS
  layer change is needed). `rw` is the default so it is ignored.
- (1) is the list of keys the current parser forwards, not a guarantee that every libfuse3 accepts them.
  `fuse_new` fails on an unknown option, so values such as `nonempty` (removed in libfuse3) and
  `direct_io` (moved in libfuse3 from being mount-wide to the per-file `fi->direct_io`) are swallowed in (2)
  rather than (1).
- **The list in (1) was checked one by one on real hardware on 2026-09-20** (this server, libfuse3 3.10.2).
  Everything that remains was confirmed to "mount successfully when given, and stay". (`allow_other` and
  `allow_root` need `user_allow_other` in `/etc/fuse.conf`, so they fail as a non-root user in this environment
  - which is as specified.)
- **`-o max_read=N` and `-o max_readahead=N` were taken out of (1)**. `max_readahead` makes
  `fuse_new` fail with `fuse: unknown option(s)`, like `max_write`. **`max_read` is worse**: the mount succeeds
  and returns success to the parent, and then **the session ends and the process disappears with exit 0**
  (the same at 4096, 65536, 131072 and 1048576 = it does not depend on the value). **Seen from fstab it is the
  hardest kind of breakage to notice: "the mount succeeded but nothing is mounted".** **The cause is not
  established**, but it must not be passed with the current binding, so it was moved to (2″) with a warning.
- **`-o max_write=N` flows into (3) rather than (1)**. libfuse **does not accept `max_write`
  as a mount option** (it is set in the init callback), and forwarding it gives `fuse: unknown option(s)` ->
  `fuse_new` fails and **the mount itself dies** (confirmed on real hardware). It now flows into
  `mount.max_write`, so `-o max_write=65536`, `--max-write 65536` and the TOML's `mount.max_write` all give the
  same result.
- `defaults`, `nofail` and `x-systemd.*` are the classics one hits in a real fstab. They are swallowed by
  (2)/(2′) with no warning.
- `Pgfs.Fuse.MountOptions` itself holds only `SingleThread`, which is fixed at `false` (multi-threaded) today.

### The shutdown

On receiving `Ctrl+C` or `SIGTERM` it attempts a `LazyUnmount` and, after the FUSE loop ends, handles whatever
is unflushed with `Api.Dispose`. For the report of what was left unwritten and the limits of the exit code see
the write-back section above. If it did not go through the clean exit path (a `SIGKILL` and so on), unmount it
by hand with `fusermount3 -u <mountpoint>`.

## The known limitations and TODOs

| The item | The status | Notes |
|---|---|---|
| The data I/O (Read/Write) | ✅ | `Api.ReadData` / `Api.WriteData` implemented over the `bytea` chunks of `pgfs_data_chunk` (migrated from large objects in Phase 1) |
| The bidirectional `uid/gid` <-> `uname/gname` resolution | ✅ | A libc P/Invoke in [src/fuse/src/UserResolver.cs](../src/fuse/src/UserResolver.cs) |
| Reflecting the uname/gname of `Chown` | ✅ | It calls `Api.UpdateOwner`. The convention that `uid == -1` means no change is honoured |
| The extended attributes (xattr) | ✅ | `Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr` implemented. The values are held faithfully as bytea in **the parallel arrays `xattr_names TEXT[]` plus `xattr_values BYTEA[]`** (any byte string including NULs round-trips untouched). The design is in [xattr-bytea.md](design/xattr-bytea.md) |
| Symlinks | ✅ | `Api.CreateSymlink` / `ReadLink` implemented |
| Hardlinks | ✅ | `Api.CreateHardLink` implemented. `Unlink` updates the `st_nlink` of the remaining inodes |
| Reducing the chunks on a truncate | ✅ | `Api.TruncateData` removes the `bytea` chunk rows beyond the new size and trims the last chunk with a `substring`/`overlay` (migrated from large objects in Phase 1) |
| The POSIX ACLs (setfacl/getfacl) | ✅ | `system.posix_acl_access` <-> the `st_mode` plus the canonical ACL (`user.pgfs_acl`). It shares the same canonical store as the Windows DACL. For the details see "the POSIX ACLs" above and [permission-interop.md](design/permission-interop.md). The strict enforcement of a named ACL awaits a requirement |
| Confirming it works on macOS | ❌ | It depends on libfuse's macOS support. macFUSE is needed |
| The access check (`Access`) | ❌ | For now it is expected that passing `default_permissions` at mount time has the kernel decide |
| The mount option `-o` | ✅ | `-o key=val,flag,...` is classified (FUSE passthrough / accepted and ignored plus the `x-` prefix / pgfs settings / unknown = a Warning / an unsupported mount operation = an explicit Warning). For the details see "the mount options" above. The implementation is [ConfigLoader.ParseDashOOptions](../src/core/src/Config/ConfigLoader.cs) |
| Reconnecting after a failed connection | ✅ | The `Pg.OpenConnection` family is wrapped in [Retry](../src/core/src/Utility/Retry.cs). Exponential backoff, tuned with `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms`. It does not retry arbitrary queries. Separately, create / write / flush have a bounded tx retry for `40P01` and `40001` ([support_for_citus.md](design/support_for_citus.md)) |
| The fallback for a uname or gname that does not exist on the OS | ✅ | When `getpwnam` / `getgrnam` fail, the uid/gid resolved from `mount.fallback_uname` / `mount.fallback_gname` (stored in the database, `nobody` / `nogroup` by default) are returned. If the fallback names themselves cannot be resolved, uid=65534 (NFS's conventional nobody) is hardcoded and a warning is logged. The implementation is [src/fuse/src/UserResolver.cs](../src/fuse/src/UserResolver.cs), verified by `test_fallback_uname_gname` in the Linux e2e suite |
| Caching and batching the reads and writes | ⚠️ | The bytea chunk scheme. The read cache and the per-file write-back are implemented. Read-ahead and a flush batch across files are unimplemented |
| The binary representation of an xattr value | ✅ | **The raw byte string is held faithfully** in `xattr_values BYTEA[]` (migrated from the old Base64 plus JSONB). Any byte string including NULs round-trips untouched and it is directly visible as a bytea in SQL too. The design and the verification are in [xattr-bytea.md](design/xattr-bytea.md) |

## A scenario for checking it works (assuming Linux)

```bash
# 1. initialize PGFS in PostgreSQL (see [docs/Mkfs.md](Mkfs.md))
dotnet run --project src/mkfs

# 2. prepare the mount point
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# 3. mount
dotnet run --project src/mount -- -m /mnt/pgfs

# 4. access from another terminal: the metadata
ls -la /mnt/pgfs                # the root directory
mkdir /mnt/pgfs/hello           # create a directory
ls -la /mnt/pgfs                # hello is visible
rmdir /mnt/pgfs/hello           # remove it
touch /mnt/pgfs/empty.txt       # create an empty file
chmod 600 /mnt/pgfs/empty.txt   # change the permissions
chown $USER:$USER /mnt/pgfs/empty.txt  # change the owner
rm /mnt/pgfs/empty.txt          # remove the file

# 5. the data I/O
echo "hello world" > /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # -> hello world
truncate -s 5 /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # -> hello

# 6. symlinks
ln -s /etc/hostname /mnt/pgfs/host
readlink /mnt/pgfs/host         # -> /etc/hostname

# 7. hardlinks
echo data > /mnt/pgfs/orig.txt
ln /mnt/pgfs/orig.txt /mnt/pgfs/copy.txt
ls -l /mnt/pgfs                 # both have st_nlink=2
rm /mnt/pgfs/orig.txt
cat /mnt/pgfs/copy.txt          # -> data (the data is still alive)

# 8. extended attributes
setfattr -n user.tag -v hello /mnt/pgfs/copy.txt
getfattr -d /mnt/pgfs/copy.txt  # -> user.tag="hello"

# 9. unmount
fusermount3 -u /mnt/pgfs
# or Ctrl+C in the terminal running mount.pgfs

# if the process crashed, unmount it by hand:
mount | grep pgfs                # if /dev/fuse on /mnt/pgfs is left
fusermount3 -u /mnt/pgfs
# or sudo umount /mnt/pgfs
```
