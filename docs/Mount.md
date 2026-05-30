# mount.pgfs specification

Specification for `mount.pgfs`, the Linux / macOS tool that **mounts a PGFS filesystem via FUSE**.

This document describes the specification as implemented in [src/mount/](../src/mount/); it is a separate executable from the Windows [Pgfs.Assign](../src/assign/) (DokanNet version). All the shared logic is centralized in [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs).

A Japanese translation is available in [Mount.ja.md](Mount.ja.md).

## Role

It presents a PostgreSQL database initialized as PGFS (built by [docs/Mkfs.md](Mkfs.md)) as a Linux/macOS directory tree in user space.

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ^v Npgsql + Dapper
    Pgfs.Lib.Api.Api (cross-platform)
        ^v
    Pgfs.Mount.FileSystem : Tmds.Fuse.FuseFileSystemBase (Linux/macOS-specific)
        ^v FUSE
    Linux kernel / macOS macFUSE
        ^v
    cd /mnt/pgfs && ls
```

## Build and run

```bash
# Build
dotnet build src/mount/Mount.csproj

# Run (default: connect to localhost:5432 and mount at /mnt/pgfs)
sudo dotnet run --project src/mount

# A different mount point
sudo dotnet run --project src/mount -- -m /mnt/myfs

# Explicit connection string
sudo dotnet run --project src/mount -- \
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    -m /mnt/pgfs

# Help
dotnet run --project src/mount -- --help
```

> **Unmount**: `fusermount3 -u /mnt/pgfs` (Linux) or Ctrl+C (LazyUnmount).

### Unmounting after the process crashes

If the mount.pgfs process crashes or is killed with SIGKILL, **only the kernel-side mount entry is left behind**. The `mount` command shows it like this, and you cannot remount until it is cleared:

```bash
$ mount | grep pgfs
/dev/fuse on /mnt/pgfs type fuse (rw,nosuid,nodev,relatime,user_id=1000,group_id=1000)

$ ls /mnt/pgfs
ls: cannot access '/mnt/pgfs': Transport endpoint is not connected
```

In that case, **unmount manually**:

```bash
# Usually this is enough (same as a normal unmount)
fusermount3 -u /mnt/pgfs

# If you hit a permission error, force it
fusermount3 -uz /mnt/pgfs    # -z = lazy (detaches even if busy, on next use)
# or
sudo umount /mnt/pgfs
sudo umount -l /mnt/pgfs     # lazy
```

Recovery is complete once `/dev/fuse on .../mnt/pgfs` disappears from `mount`. Then you can remount as usual with `mount.pgfs -m ...`.

### Prerequisites

- **.NET 10 SDK**
- **libfuse3** / **fusermount3** installed
  - Ubuntu/Debian: `sudo apt install fuse3 libfuse3-3`
  - Arch: `sudo pacman -S fuse3`
  - Checked at startup via `Fuse.CheckDependencies()`; exits if missing.
- The mount point created in advance
  - `sudo mkdir -p /mnt/pgfs && sudo chown $USER /mnt/pgfs`
- The DB side initialized by [docs/Mkfs.md](Mkfs.md).

### Behavior on Windows

It still builds on Windows (for cross-compilation), but running it hits the `OperatingSystem.IsWindows()` guard at the start, prints an error pointing to `pgfs.assign` (the DokanNet version), and exits. To mount on Windows, use [src/assign/](../src/assign/).

## Configuration

The configuration model shares [`Pgfs.Lib.Config.RootConfig`](../src/lib/src/Config/RootConfig.cs). The same TOML settings file and the same command-line options as Mkfs apply. See [docs/Mkfs.md](Mkfs.md) for details.

What mount.pgfs uses in particular:

| Setting | Argument option | Default |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Linux/macOS: `/mnt/pgfs` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `warning` |
| `database.notify_enabled` | `--notify` | `false`. Recommended when multiple clients mount the same PG/pgfs. See "Cross-client change notification" below. |

`pgfs.toml` uses the same TOML format as Mkfs.

## Implemented FUSE operations

[src/mount/src/FileSystem.cs](../src/mount/src/FileSystem.cs) extends [`Tmds.Fuse.FuseFileSystemBase`](https://github.com/tmds/Tmds.Fuse) and overrides the following.

| Operation | Status | Notes |
|---|---|---|
| `GetAttr` | done | inode -> `stat` struct mapping. |
| `OpenDir` | done | Existence + directory check only. |
| `ReadDir` | done | `.` / `..` + child inode enumeration. |
| `ReleaseDir` | done | no-op. |
| `MkDir` | done | `Api.CreateDirectory`. |
| `RmDir` | done | `Api.DeleteInode` after an emptiness check. |
| `Create` | done | `Api.CreateFile` (empty file). |
| `Unlink` | done | `Api.DeleteInode` (also frees data with no remaining references). |
| `Rename` | done | `Api.Rename` (handles RENAME_NOREPLACE). |
| `ChMod` | done | `Api.UpdateMode`, preserving the type bits. |
| `Chown` | done | Resolves uid/gid -> uname/gname via `UserResolver`, then `Api.UpdateOwner`. |
| `Truncate` | done | `Api.TruncateData` (also truncates the bytea chunks). |
| `UpdateTimestamps` | done | Writes only `mtime` to the DB; ignores `atime` (requirement). |
| `Open` | done | Existence check only. |
| `Release` | done | no-op. |
| `Read` | done | `Api.ReadData` (via bytea chunks; holes are zero-filled). |
| `Write` | done | `Api.WriteData` (via bytea chunks; creates the data row on first write). |
| `StatFS` | done | Uses `pg_database_size` as the actual usage. |
| `GetXAttr` | done | Reads from `pgfs_inode.xattrs` JSONB via `->>`, decoding the value from Base64. |
| `SetXAttr` | done | Merges JSONB with `||`, honoring `XATTR_CREATE` / `XATTR_REPLACE` flags. |
| `ListXAttr` | done | Enumerates with `jsonb_object_keys`, returns in NUL-terminated form. |
| `RemoveXAttr` | done | Deletes from JSONB with the `-` operator. |
| `SymLink` | done | `Api.CreateSymlink` (`S_IFLNK | 0777`, stored in the `link_target` column). |
| `ReadLink` | done | Returns `inode.LinkTarget`, NUL-terminated. |
| `Link` | done | `Api.CreateHardLink` (adds an inode with the same `data_id`; updates `st_nlink` across all links). |

There are currently no unimplemented operations (none return `-ENOSYS`).

## Cross-client change notification (Notify)

Enabling `database.notify_enabled=true` receives other clients' writes via PostgreSQL `LISTEN` / `NOTIFY` and invalidates the local `InodeCache` ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs)). When multiple `mount.pgfs` / `pgfs.assign` share-mount the same PG/pgfs, a writing client's changes become visible in other clients' `stat` / `ls`.

**Linux-side limitation**: Tmds.Fuse's high-level API exposes nothing equivalent to libfuse's low-level `fuse_lowlevel_notify_inval_*`, so it **cannot actively invalidate the kernel inode/dentry cache**. Practical impact:

- Thanks to `attr_timeout=0` (the default), active `stat`-type queries always go FUSE -> our `InodeCache` -> DB and see the latest value.
- However, **passive subscribers** such as `inotify` are not notified (updates that happen via FUSE — not only those on another machine — do not fire the kernel `fsnotify`). The expectation is to use active polling like `watch -n 1 ls`.
- If low-level notify is patched into the vendored Tmds.Fuse in the future, inotify could be wired up too (not supported yet).

For the detailed spec / payload / receive handling, see "Cross-client change notification (Notify)" in [history.md](history.md).

## Architecture

### Layers

```
+---------------------------------------------------------------+
| Pgfs.Mount.Program           -- arg parsing / OS check / mount |
+---------------------------------------------------------------+
| Pgfs.Mount.FileSystem        -- FUSE callbacks (Linux)         |
|   - FillStat / ResolveOwner    * Linux-specific                |
|   - CurrentUserNames           * applicable on Windows too     |
+---------------------------------------------------------------+
| Pgfs.Lib.Api.Api             -- DB operations (cross-platform) |
|   - GetByPath / ListChildren / CreateDirectory / ...           |
+---------------------------------------------------------------+
| Pgfs.Lib.Api.InodeCache      -- inode memory cache             |
+---------------------------------------------------------------+
| Pgfs.Lib.Utility.Pg          -- Npgsql + Dapper wrapper        |
+---------------------------------------------------------------+
```

### Parts shareable with Windows

Logic in FileSystem.cs that can be reused on the Windows (DokanNet) side, [`Pgfs.Assign`](../src/assign/), is marked with a `// Windows-shareable:` comment.

- **Path splitting** (`SplitParent`): OS-independent if the separator is parameterized. Mount fixes it to `/`.
- **Deciding a new inode's uname/gname** (`CurrentUserNames`): obtaining `Environment.UserName` is the same on both OSes. Only the getuid()/getgid() part is Linux-specific.
- **Byte-span path -> string conversion** (`PathToString`): the UTF-8 decode itself is OS-independent.

Conversely, the **Linux-specific** parts are:

- The parts dealing with `Tmds.Linux.stat` / `statvfs` / `mode_t` / `uid_t` / `gid_t` / `timespec` (`FillStat`, `StatFS`).
- The `getuid()` / `getgid()` calls (via `Tmds.Linux.LibC`).
- Linux-specific flags such as `RENAME_NOREPLACE`.

On the Windows side these are replaced by NTSTATUS / FileAttributes / DOKAN_FILE_INFO, etc.

## Design decisions / notes

### Threading

It returns `SupportsMultiThreading => true`, and Tmds.Fuse invokes the FUSE callbacks on multiple threads. `InodeCache` is synchronized with `lock(this)`, and `Pg.cs` relies on the `NpgsqlDataSource` connection pool. Each `Api` method is a single SQL operation and therefore atomic.

However, a "combination of multiple operations" (`MkDir`'s parent-existence check -> CreateDirectory) is **not atomic**. If two `MkDir`s race on the same path, the DB's `UNIQUE(parent_id, name)` lets only one succeed; the other gets `-EEXIST` because `Api.CreateDirectory` returns null. This is reasonable behavior.

### Handling of credentials

- **A new inode's uname/gname**: uses `Environment.UserName`. The requirement says the effective user name from `getuid()`, but `Environment.UserName` is effectively equivalent.
- **uid/gid resolution**: [`UserResolver`](../src/mount/src/UserResolver.cs) P/Invokes libc `getpwnam` / `getpwuid` / `getgrnam` / `getgrgid`. Results are cached in memory, so repeated `ls -al` over large directories does not re-hit libc.
- **`Chown`**: resolves the given uid/gid to names with `UnameOf`/`GnameOf` and rewrites the DB via `Api.UpdateOwner`. Honors the `uid == 0xFFFFFFFF (-1)` convention (= do not change).

### Data I/O (Read / Write)

The PGFS data body is split into `pgfs_data` + `pgfs_data_chunk` (one row = one bytea), and read/write from the Mount side is **implemented**:

- `Read`: `Api.ReadData` does a partial read from the bytea chunks via `substring(payload from N for M)`, zero-filling holes (benefiting from PG 13+ partial TOAST detoast).
- `Write`: `Api.WriteData` creates chunk rows on demand and extends `st_size` after writing. It is one upsert per chunk (`INSERT ... ON CONFLICT DO UPDATE SET payload = CASE ... END`); concurrent write races are serialized automatically by PG row locks.
- `Truncate` (and `Open(O_TRUNC)`): `Api.TruncateData` truncates the last chunk's payload with `substring` (or extends it with zero padding) and DELETEs unneeded chunks.
- `Create`: creates an empty-file inode. The chunk rows are created on the first `Write`.

> The data body is stored as bytea chunks so it can be distributed under Citus (one file = one shard); see [docs/support_for_citus.md](support_for_citus.md).

### Extended attributes / symbolic links / hard links

All implemented:

- Extended attributes are stored Base64-encoded in `pgfs_inode.xattrs` JSONB (`Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr`). `GetXAttr` / `ListXAttr` parse the cached `Inode.Xattrs` JSON locally (to mitigate SELinux's frequent `security.selinux` probes).
- Symbolic links are stored in the `link_target` column (`Api.CreateSymlink`). `ReadLink` returns NUL-terminated.
- Hard links create multiple inodes sharing the same `data_id` (`Api.CreateHardLink`). `st_nlink` is updated consistently across all links.

### `st_atime`

Requirement: the last-access time is not stored, and the same value as `st_mtime` is returned. The implementation matches (`s.st_atim = mtime.ToTimespec()` in `FillStat`).

### Mount options

The default FUSE options are `attr_timeout=0` (disables the kernel attr cache so a `stat` right after `ln` sees the fresh `st_nlink`) plus any flags received via fstab `-o ...`. Last-wins applies, so a user `-o` override of the same key takes effect.

### Shutdown

On `Ctrl+C` it attempts `LazyUnmount` (equivalent to `fusermount3 -uz`). If the process is killed, unmount manually with `fusermount3 -u <mountpoint>`.

## Known limitations / TODO

| Item | Status | Notes |
|---|---|---|
| Data I/O (Read/Write) | done | `Api.ReadData` / `Api.WriteData` over bytea chunks. |
| Bidirectional `uid/gid` <-> `uname/gname` resolution | done | libc P/Invoke in [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs). |
| `Chown` applying uname/gname | done | Calls `Api.UpdateOwner`. Honors the `uid == -1` = do-not-change convention. |
| Extended attributes (xattr) | done | `Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr`. Values stored Base64 in JSONB. |
| Symbolic links | done | `Api.CreateSymlink` / `ReadLink`. |
| Hard links | done | `Api.CreateHardLink`. `Unlink` updates `st_nlink` on the remaining inodes. |
| Chunk reduction on Truncate | done | `Api.TruncateData` DELETEs chunks beyond the new size and trims the tail chunk's payload. |
| macOS verification | not done | Depends on Tmds.Fuse's macOS support. Requires macFUSE. |
| Access check (`Access`) | not done | For now, passing `default_permissions` at mount time lets the kernel decide. |
| Mount option `-o` | partial | Flags received via fstab `-o ...` are forwarded; a curated set is still being expanded. |
| Reconnection on connection failure | done | Wraps `Pg.OpenConnection`-family calls with [Retry](../src/lib/src/Utility/Retry.cs). Exponential backoff, tuned by `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms`. Exceptions mid-query are not retried because of idempotency concerns. |
| Fallback for uname / gname absent on the OS | done | On `getpwnam` / `getgrnam` failure, returns the uid/gid resolved from `mount.fallback_uname` / `mount.fallback_gname` (stored in the DB; defaults `nobody` / `nogroup`). If the fallback name itself cannot be resolved, hardcodes uid=65534 (the NFS nobody convention) and logs a warning. Implemented in [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs). |
| Read/Write streaming | partial | Currently each chunk is upserted/read independently. For heavy I/O there is room to batch multiple chunks per round-trip. |
| Binary xattr values | partial | Stored Base64 in JSONB (a JSON string cannot carry raw bytes). Transparent through the OS getxattr/setxattr, but reading it directly in SQL shows the Base64 form. |

## Verification scenario (Linux)

```bash
# 1. Initialize PGFS on PostgreSQL (see [docs/Mkfs.md](Mkfs.md))
dotnet run --project src/mkfs

# 2. Prepare the mount point
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# 3. Mount
dotnet run --project src/mount -- -m /mnt/pgfs

# 4. Access from another terminal: metadata
ls -la /mnt/pgfs                # root directory
mkdir /mnt/pgfs/hello           # create a directory
ls -la /mnt/pgfs                # hello is visible
rmdir /mnt/pgfs/hello           # remove it
touch /mnt/pgfs/empty.txt       # create an empty file
chmod 600 /mnt/pgfs/empty.txt   # change permissions
chown $USER:$USER /mnt/pgfs/empty.txt  # change owner
rm /mnt/pgfs/empty.txt          # remove the file

# 5. Data I/O
echo "hello world" > /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # -> hello world
truncate -s 5 /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # -> hello

# 6. Symbolic link
ln -s /etc/hostname /mnt/pgfs/host
readlink /mnt/pgfs/host         # -> /etc/hostname

# 7. Hard link
echo data > /mnt/pgfs/orig.txt
ln /mnt/pgfs/orig.txt /mnt/pgfs/copy.txt
ls -l /mnt/pgfs                 # both show st_nlink=2
rm /mnt/pgfs/orig.txt
cat /mnt/pgfs/copy.txt          # -> data (the data is still alive)

# 8. Extended attributes
setfattr -n user.tag -v hello /mnt/pgfs/copy.txt
getfattr -d /mnt/pgfs/copy.txt  # -> user.tag="hello"

# 9. Unmount
fusermount3 -u /mnt/pgfs
# or Ctrl+C in the terminal running mount.pgfs

# If the process crashed, a manual unmount is needed:
mount | grep pgfs                # if /dev/fuse on /mnt/pgfs is left behind
fusermount3 -u /mnt/pgfs
# or sudo umount /mnt/pgfs
```

## References

- [docs/database.md](database.md) — DB schema design.
- [docs/Mkfs.md](Mkfs.md) — the initializer tool's spec.
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) — the shared API.
- [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) — the inode cache.
- [Tmds.Fuse](https://github.com/tmds/Tmds.Fuse) — the FUSE library.
