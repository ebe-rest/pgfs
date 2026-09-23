# v0.2.0 (B): the design of the in-house FUSE binding

> **Route**: [docs/README.md](../README.md) › [v0.2.0-plan.md](v0.2.0-plan.md) › **this document**
>
> **What this document is the source of truth for**: the design and the as-built of the **in-house binding**
> to libfuse3 (`Pgfs.Fuse`). Resolving the symbols with `dlopen` plus `dlvsym` (symbol versioning), the struct
> layouts (`fuse_config` / `fuse_file_info` and so on), how far the op table is wired, the design judgements in
> section 4 and the differences from the plan all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [v0.2.0-plan.md](v0.2.0-plan.md) | **The parent plan** (decision 1 the in-house binding / decision 2 splitting the Lib / settling where A moves to) |
> | [../architecture.md](../architecture.md) | The project structure, the dependencies and the build procedure of Core/Fuse/Dokan |
> | [fstab-support.md](fstab-support.md) | Starting through `mount(8)` / fstab, and propagating the `-o` options from there |
> | [../Mount.md](../Mount.md) | The user-facing CLI contract and the classification of the mount options |
> | [windows-parity.md](windows-parity.md) | The design for taking the same features to the Dokan side |
> | [../tests.md](../tests.md) | The list of the e2e tests and how to run them (the substance this document's verification policy rests on) |
>
> The provenance and where the implementation lives (not documents, so not in the table): the original fork =
> [ebe-rest/Tmds.Fuse](https://github.com/ebe-rest/Tmds.Fuse) (clean tip `d454274`). `LibFuse.cs` /
> `LibFuse.structs.cs` / `FuseMount.cs` / `IFuseFileSystem.cs` were ported into
> [src/fuse/src/](../../src/fuse/src/) preserving the behaviour (the credits are in
> [NOTICES.md](../../src/fuse/NOTICES.md)). pgfs's FS implementation is
> [src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs) (25 ops, path-based).

> **Implemented, with the e2e suites green on both operating systems.** This document is the
> detailed design (B) of decision 1 (the in-house FUSE binding) of [v0.2.0-plan.md](v0.2.0-plan.md). For the
> implementation status see the implementation-status section directly below.
> A (splitting the Lib) is closed as a design. This document builds a table of **the whole public libfuse3 API
> against the places pgfs uses**, and on top of it designs "a minimal P/Invoke binding to libfuse of our own"
> (= the replacement for the `vendor/Tmds.Fuse` fork). It enumerates by verb (function) and separates the
> structs into section 2 by reference. **The design judgements (1) to (6) in section 4 are settled.**

## Implementation status (implemented, with the e2e suites green on both operating systems)

The Lib was split into `Pgfs.Core` /
`Pgfs.Fuse` / `Pgfs.Dokan`, the libfuse binding was **ported in house preserving the behaviour** from the old
`vendor/Tmds.Fuse` fork (the namespace `Tmds.Fuse` -> `Pgfs.Fuse`), and the submodule was retired (the credits
are in [../src/fuse/NOTICES.md](../../src/fuse/NOTICES.md)).

- **The full solution builds green on Windows** (Core/Fuse/Dokan plus the three thin executables).
- **The full Linux e2e suite PASSes 36/36** (docker with a single PG: mkfs -> mount -> the tests. hardlink =
  use_ino / fallback = fuse_get_context / the whole of xattr-ACL green = the binding is proven equivalent).
- **The Windows Dokan e2e suite PASSes 27/27** (assign.pgfs mounted against the server, including the
  Get/SetFileSecurity projection and df).

**The post-green cleanup (applied 2026-06-07, re-verified green on both operating systems)**:
- ✅ **(3) a typed `fuse_config`**: the magic poke at offset 64 was retired in favour of
  `((fuse_config*)cfg)->use_ino = 1`. The struct was added to `LibFuse.structs.cs` (the libfuse 3.x layout,
  use_ino@64). **Verified through the Linux hardlink e2e suite (36/36)**.
- ✅ **The KEEPCACHE bit of `FuseFileInfo`**: corrected from `3` to `4` (`1<<2`) (writepage=1 / direct_io=2 /
  keep_cache=4). pgfs does not read that bit, so it is a latent fix.
- ✅ **`PosixAcl` moved from Core to Fuse**: completing the separation of the canonical form (PgfsAcl = Core)
  from the projection (PosixAcl = Fuse) (Core was not referencing PosixAcl).
- ⏸ **The argument order of symlink was left alone**: the swap in the binding is **a deliberate adaptation**
  that turns libfuse's `(content, linkpath)` into the intuitive managed `SymLink(path = where the link is,
  target = its content)`, and is not a bug (there is a comment in `FileSystem.cs`). It was therefore judged not
  to need fixing.
- ⬜ **Remaining: splitting `ServiceResolver` with `#if WINDOWS`** (the native getservbyname = ws2_32 or libc).
  It is a purity improvement for a rare service-name-to-port fallback, so Core is left compiled per operating
  system (the feature is complete).

**The as-built differences from the design (against the plan's 3-3 / 3-8 / section 4)**:
- **The op wiring = 30 ops plus `init` = 31** (the `ops.*` wiring in
  [FuseMount.cs](../../src/fuse/src/FuseMount.cs)). 3-3 and 4#5 planned "simplify to 25 plus init (leaving
  `flush` / `fsync` / `fsyncdir` / `access` / `fallocate` null)", but **preserving the behaviour was preferred
  and the same 31 as the fork are wired** (those 5 ops return ENOSYS). The "25 plus init only" of 1-B / 2 / 3-3
  / 4#5 is the planned figure; what pgfs implements meaningfully is 25 ops (= agreeing with the ground truth of
  section 0).
- **The file structure = 3 files laid out flat**: `LibFuse.cs` (resolving the symbols) /
  `LibFuse.structs.cs` (gathering the structs plus the op delegate types) / `FuseMount.cs` (the op wiring plus
  the lifecycle). Rather than the three-way `Native/{LibFuse,FuseStructs,FuseOperations}` split proposed in
  3-8, the structs and the ops were put together in `LibFuse.structs.cs` (within the latitude 3-8 gave, that
  "whether it is `Native/` or flat is at the implementer's discretion").

## The purpose and the scope

- To hold **a minimal P/Invoke binding to libfuse of our own** inside `Pgfs.Fuse` and retire the
  `vendor/Tmds.Fuse` submodule and fork ([v0.2.0-plan.md decision 1](v0.2.0-plan.md)).
- **The high-level API (`fuse.h`) only.** The low-level / session API (`fuse_lowlevel.h`) is not used (the
  current fork is the same).
- **libfuse's C ABI is an interface** (it is only bound to; the source is not vendored). `libfuse.so` is the
  user's to provide (LGPL, dynamically linked with `dlopen`).

## 0. The measured surface of the current fork (the ground truth for the replacement)

The result of measuring `vendor/Tmds.Fuse` (the minimum set the replacement has to reproduce):

- **Resolving the symbols**: `dlopen("libfuse3.so.3", RTLD_NOW)` -> **a versioned `dlvsym`** (the default tag
  `"FUSE_3.0"`, with `"FUSE_3.1"` for `fuse_new` alone). `[DllImport]` is not used. The libc primitives depend
  on `Tmds.LibC` 0.5.0 (`dlopen` / `dlvsym` / `stat` / `statvfs` / `timespec` / `mode_t` / errno).
- **10 libfuse symbols are used** (the table in 1-A). `fuse_main` / `fuse_opt_parse` / `fuse_parse_cmdline` /
  `fuse_session_*` are not used.
- **31 of the 40 slots of `fuse_operations` are wired** (30 ops plus `init`), 9 null. What pgfs implements
  meaningfully is **25 ops** (the remaining `flush` / `fsync` / `fsyncdir` / `access` / `fallocate` are wired
  but return ENOSYS).
- **managed to native**: typed delegates plus `Marshal.GetFunctionPointerForDelegate`, passing `&ops` (a
  stack-local `fuse_operations`) to `fuse_new` with `op_size = sizeof(...)`.
- **errno**: a negative errno is returned directly, and each callback is wrapped in
  `try { … } catch { return -EIO; }`. Only `release` is void -> 0.
- **The known patches**: (a) writing `fuse_config.use_ino=1` at offset 64 in `init` / (b) `fuse_get_context`
  (uid/gid/pid) / (c) the verbatim `-o` passthrough of `MountOptions.Options`.

---

## 1. The whole public libfuse3 API against the places pgfs uses (by verb)

The key - **pgfs**: ✅ used / ➖ wired only (the body is ENOSYS) / ✗ not used.
**In house**: have it = ○ / do not = ✗ / conditional = △.

### 1-A. The high-level lifecycle and setup (`fuse.h`, `fuse_common.h`)

| The function | The C signature (abbreviated) | pgfs | In house | Notes |
|---|---|---|---|---|
| `fuse_new` | `fuse* (fuse_args*, const fuse_operations*, size_t, void*)` | ✅ | ○ | The **`FUSE_3.1`** version. The substance of registering the op table |
| `fuse_mount` | `int (fuse*, const char*)` | ✅ | ○ | Connecting to the mount point |
| `fuse_loop` | `int (fuse*)` | ✅ | ○ | The single-threaded event loop |
| `fuse_loop_mt` | `int (fuse*, int clone_fd)` | ✅ | ○ | Multi-threaded (pgfs's FileSystem has `SupportsMultiThreading=true`) |
| `fuse_unmount` | `void (fuse*)` | ✅ | ○ | Teardown |
| `fuse_destroy` | `void (fuse*)` | ✅ | ○ | Teardown |
| `fuse_exit` | `void (fuse*)` | ✅ | ○ | Breaking out of the loop on a forced unmount |
| `fuse_get_context` | `fuse_context* (void)` | ✅ | ○ | uid/gid (the creator-owner plus the audit). **Resolved optionally** (it can mount without it) |
| `fuse_main` (`fuse_main_real`) | A macro -> `int (argc, argv, ops, size, priv)` | ✗ | ✗ | Not needed, because pgfs assembles `fuse_new` plus `fuse_mount` plus `fuse_loop` directly |
| `fuse_get_session` | `fuse_session* (fuse*)` | ✗ | ✗ | Getting the low-level session. Not needed |
| `fuse_daemonize` | `int (int foreground)` | ✗ | ✗ | pgfs daemonizes with its own child-process separation (on the mount executable side) |
| `fuse_set_signal_handlers` / `fuse_remove_signal_handlers` | `int/void (fuse_session*)` | ✗ | ✗ | They assume a low-level session. pgfs handles Ctrl+C on the .NET side |
| `fuse_lib_help` | `void (fuse_args*)` | ✗ | ✗ | The help is pgfs's own (`HelpText`) |
| `fuse_getgroups` | `int (int size, gid_t list[])` | ✗ | ✗ | Enumerating the supplementary groups. Not used |
| `fuse_interrupted` | `int (void)` | ✗ | ✗ | Detecting an interrupt. Not used |
| `fuse_invalidate_path` | `int (fuse*, const char*)` | ✗ | △ | Invalidating the kernel cache. **Room to consider with the Notify integration later** (today only the InodeCache is invalidated) |
| `fuse_clean_cache` / `fuse_start_cleanup_thread` / `fuse_stop_cleanup_thread` | - | ✗ | ✗ | Managing the remember cache. Not used |
| `fuse_apply_conn_info_opts` / `fuse_parse_conn_info_opts` | - | ✗ | ✗ | Applying the options of the conn_info. Not used |
| `fuse_version` / `fuse_pkgversion` | `int/const char* (void)` | ✗ | △ | For diagnostics. **It would be fine to include** (useful for judging the dependency and for the log) |

### 1-B. The high-level operations - every op of `struct fuse_operations` (libfuse 3.x, in declaration order)

42 slots (3.10+). The current fork's struct declares as far as `fallocate` (#40) and cuts it off safely with
`op_size`.

| # | The op | The C signature (abbreviated) | Wired in the fork | Implemented by pgfs | In house | The Api call / notes |
|---|---|---|---|---|---|---|
| 1 | `getattr` | `(path, stat*, ffi*)` | ✅ | ✅ | ○ | `Api.GetByPath` -> `FillStat`. `st_ino` is the DataId plus the sign namespace (depends on use_ino) |
| 2 | `readlink` | `(path, buf, size)` | ✅ | ✅ | ○ | `inode.LinkTarget` |
| 3 | `mknod` | `(path, mode, dev)` | null | ✗ | ✗ | An ordinary file goes through `create`. FIFOs and devices are not needed |
| 4 | `mkdir` | `(path, mode)` | ✅ | ✅ | ○ | `Api.CreateDirectory` |
| 5 | `unlink` | `(path)` | ✅ | ✅ | ○ | `Api.DeleteInode` |
| 6 | `rmdir` | `(path)` | ✅ | ✅ | ○ | `Api.IsDirectoryEmpty` plus `DeleteInode` |
| 7 | `symlink` | `(target, path)` | ✅ | ✅ | ○ | `Api.CreateSymlink`. **The fork had the arguments the other way round -> the in-house one puts them in the C order (target, linkpath)** |
| 8 | `rename` | `(path, newpath, flags)` | ✅ | ✅ | ○ | `RENAME_NOREPLACE`=1 is handled |
| 9 | `link` | `(oldpath, newpath)` | ✅ | ✅ | ○ | `Api.CreateHardLink` |
| 10 | `chmod` | `(path, mode, ffi*)` | ✅ | ✅ | ○ | `Api.UpdateMode`, keeping the type bits |
| 11 | `chown` | `(path, uid, gid, ffi*)` | ✅ | ✅ | ○ | `-1` (uint.Max) = no change. `UserResolver` -> `Api.UpdateOwner` |
| 12 | `truncate` | `(path, off, ffi*)` | ✅ | ✅ | ○ | `Api.TruncateData` |
| 13 | `open` | `(path, ffi*)` | ✅ | ✅ | ○ | `O_TRUNC` (0x200) is handled. `fi.fh` is not used |
| 14 | `read` | `(path, buf, size, off, ffi*)` | ✅ | ✅ | ○ | `Api.ReadData`. Returns a non-negative byte count |
| 15 | `write` | `(path, buf, size, off, ffi*)` | ✅ | ✅ | ○ | `Api.WriteData` |
| 16 | `statfs` | `(path, statvfs*)` | ✅ | ✅ | ○ | `Api.GetStatFs` (the df support) |
| 17 | `flush` | `(path, ffi*)` | ➖ | ✗ | △ | The writes go to the database synchronously, so there is no buffer. **No need to wire it** (null gives ENOSYS, harmlessly) |
| 18 | `release` | `(path, ffi*)` | ✅ | ✅ (a no-op) | ○ | void -> 0. `fi.fh` is not used |
| 19 | `fsync` | `(path, datasync, ffi*)` | ➖ | ✗ | △ | The same as above. **No need to wire it** |
| 20 | `setxattr` | `(path, name, val, size, flags)` | ✅ | ✅ | ○ | `XATTR_CREATE/REPLACE`. `system.posix_acl_access` is a special case |
| 21 | `getxattr` | `(path, name, buf, size)` | ✅ | ✅ | ○ | The size-probe protocol. Composing the ACL |
| 22 | `listxattr` | `(path, list, size)` | ✅ | ✅ | ○ | NUL-concatenated plus the size probe |
| 23 | `removexattr` | `(path, name)` | ✅ | ✅ | ○ | Clearing the ACL is a special case |
| 24 | `opendir` | `(path, ffi*)` | ✅ | ✅ | ○ | An existence check only. `fi.fh` is not used |
| 25 | `readdir` | `(path, buf, filler, off, ffi*, flags)` | ✅ | ✅ | ○ | `Api.ListChildren`. `.`, `..` and the children through the filler. The offset is ignored |
| 26 | `releasedir` | `(path, ffi*)` | ✅ | ✅ (a no-op) | ○ | |
| 27 | `fsyncdir` | `(path, datasync, ffi*)` | ➖ | ✗ | △ | **No need to wire it** |
| 28 | `init` | `(conn_info*, config*)` | ✅ | ✗ (inside the fork) | ○ | **Mandatory, for the use_ino patch**. pgfs's FS does not override it; it is handled inside the fork |
| 29 | `destroy` | `(private_data)` | null | ✗ | ✗ | The teardown is on the .NET side. Not needed |
| 30 | `access` | `(path, mask)` | ➖ | ✗ | ✗ | **Delegated to the kernel with `default_permissions`.** No need to wire it |
| 31 | `create` | `(path, mode, ffi*)` | ✅ | ✅ | ○ | `Api.CreateFile`. `fi.fh` is not set |
| 32 | `lock` | `(path, ffi*, cmd, flock*)` | null | ✗ | ✗ | POSIX locks. Not used |
| 33 | `utimens` | `(path, timespec[2], ffi*)` | ✅ | ✅ | ○ | `UTIME_OMIT/NOW`. The atime is ignored -> `Api.UpdateTimestamps` |
| 34 | `bmap` | `(path, blocksize, idx*)` | null | ✗ | ✗ | For block devices. Not needed |
| 35 | `ioctl` | `(path, cmd, arg, ffi*, flags, data)` | null | ✗ | ✗ | Not used |
| 36 | `poll` | `(path, ffi*, pollhandle*, reventsp)` | null | ✗ | ✗ | Not used |
| 37 | `write_buf` | `(path, bufvec*, off, ffi*)` | null | ✗ | ✗ | Zero-copy writes. `write` is enough |
| 38 | `read_buf` | `(path, bufvecp, size, off, ffi*)` | null | ✗ | ✗ | Zero-copy reads. `read` is enough |
| 39 | `flock` | `(path, ffi*, op)` | null | ✗ | ✗ | BSD locks. Not used |
| 40 | `fallocate` | `(path, mode, off, len, ffi*)` | ➖ | ✗ | △ | **No need to wire it** (null gives ENOSYS) |
| 41 | `copy_file_range` | `(in, fi_in, off_in, out, fi_out, off_out, len, flags)` | - | ✗ | ✗ | Not in the fork's struct. A future op |
| 42 | `lseek` | `(path, off, whence, ffi*)` | - | ✗ | ✗ | Not in the fork's struct. SEEK_DATA/HOLE. A future op |

**The ops wired in house = the 25 pgfs implements plus `init` (use_ino)**:
`getattr, readlink, mkdir, unlink, rmdir, symlink, rename, link, chmod, chown, truncate, open, read, write,
statfs, release, setxattr, getxattr, listxattr, removexattr, opendir, readdir, releasedir, create, utimens`
plus `init`.
**Left null (libfuse returns ENOSYS itself or the kernel handles it)**: `mknod, flush, fsync, fsyncdir,
destroy, access, lock, bmap, ioctl, poll, write_buf, read_buf, flock, fallocate, copy_file_range, lseek`.

### 1-C. Parsing the options (`fuse_opt.h`)

| The function | pgfs | In house | Notes |
|---|---|---|---|
| `fuse_opt_add_arg` | ✅ | ○ | Pushes `""` and `-o<opts>` onto the arg vector |
| `fuse_opt_free_args` | ✅ | ○ | Teardown |
| `fuse_opt_parse` / `fuse_opt_add_opt` / `fuse_opt_insert_arg` / `fuse_opt_match` | ✗ | ✗ | pgfs has already parsed them with its own `ConfigLoader` / `ParseDashOOptions`. Only the `-o` string is handed to libfuse |
| `struct fuse_opt` | ✗ | ✗ | The option template. Not used |
| `struct fuse_args` | ✅ (indirectly) | ○ | Typed in section 2. `fuse_opt_add_arg` and `fuse_new` refer to it |

### 1-D. The low-level / session API (`fuse_lowlevel.h`) - none of it is used by pgfs

`struct fuse_lowlevel_ops` (lookup/forget/setattr/... about 40 ops) / `fuse_session_new` /
`fuse_session_mount` / `fuse_session_loop[_mt]` / `fuse_session_unmount` / `fuse_session_destroy` /
`fuse_session_exit/reset/exited/fd` / `fuse_session_process_buf` / `fuse_reply_*` (err/entry/attr/buf/...) /
`fuse_lowlevel_notify_*` (inval_inode/inval_entry/store/retrieve/poll) / `fuse_req_ctx/userdata/interrupted` -
**all ✗**. The high-level API is used, so the low-level layer is never touched. It is not held in house either.

### 1-E. Buffers, notifications, logging and signals (`fuse_common.h`, `fuse_log.h`)

| The function / type | pgfs | In house | Notes |
|---|---|---|---|
| `fuse_buf` / `fuse_bufvec` / `fuse_buf_size` / `fuse_buf_copy` | ✗ | ✗ | Not needed, because `write_buf` / `read_buf` are not used |
| `fuse_notify_poll` / `fuse_pollhandle_destroy` | ✗ | ✗ | poll is not used |
| `fuse_log` / `fuse_set_log_func` / `fuse_log_enable_syslog` / `enum fuse_log_level` | ✗ | △ | libfuse's internal log. pgfs has its own Logger. **There is a way to take it in and pipe it into that sink** (low priority) |
| `fuse_set_signal_handlers` / `fuse_remove_signal_handlers` | ✗ | ✗ | They assume a session. Handled on the .NET side |
| `fuse_loop_config` (`fuse_loop_cfg_*`) | ✗ | △ | The configuration of `fuse_loop_mt`. The defaults are enough today |

### 1-F. The context

| The function / type | pgfs | In house | Notes |
|---|---|---|---|
| `fuse_get_context` -> `struct fuse_context` | ✅ | ○ | The uid/gid are used for the creator-owner and the audit. The pid is received but not used. **Valid only while a callback is running** |

---

## 2. The struct surface (only what the verbs in use refer to)

The key - **The type definition**: a C# type is defined in house = ○ / untyped (a raw `IntPtr` plus an offset)
= △ / from libc = libc.

| The struct | Its origin | Touched by pgfs | The type definition | Layout caveats |
|---|---|---|---|---|
| `fuse_args` | fuse_opt.h | Indirectly | ○ | `int argc; char** argv; int allocated;` |
| `fuse_operations` | fuse.h | - | ○ | **The order of the function pointers is the ABI.** The declaration order is observed strictly and `op_size=sizeof` is passed. The wiring is only the 25 plus init of 1-B; the remaining slots are `IntPtr.Zero` |
| `fuse_file_info` | fuse_common.h | ✅ | ○ | The `flags` and the bitfield word. **The fork's `KEEPCACHE=3` is doubtful -> re-verify the bit position against the real header (`fuse_common.h`)**. pgfs reads only the `flags` (O_TRUNC) and does not use `fh` |
| `fuse_context` | fuse.h | ✅ | ○ (partial) | `fuse* fuse; uid_t uid; gid_t gid; pid_t pid; void* private_data; mode_t umask;`. It is enough to be able to read the leading uid/gid/pid |
| `fuse_config` | fuse.h | ✅ (in one place) | △ | **It only writes `use_ino = 1` at offset 64.** No type is raised; it is a raw poke. **Offset 64 is stable in libfuse 3.x, but re-confirm it against the real header.** The alternative: type `fuse_config` properly and write through `cfg->use_ino` (removing the fragile offset dependency) - chosen in 3-6 |
| `fuse_conn_info` | fuse_common.h | ✗ | △ | It is received in `init(conn,cfg)` but pgfs does not touch it. Passed straight through as a raw `IntPtr` |
| `stat` | sys/stat.h | ✅ | libc/○ | Filled in fully in `getattr`. `st_ino` / `st_mode` / `st_nlink` / `st_uid` / `st_gid` / `st_size` / `st_atim` / `st_mtim` / `st_ctim` / `st_blocks` and the rest |
| `statvfs` | sys/statvfs.h | ✅ | libc/○ | In `statfs`: `f_bsize` / `f_frsize` / `f_blocks` / `f_bfree` / `f_bavail` / `f_files` / `f_ffree` / `f_favail` / `f_namemax` |
| `timespec` | time.h | ✅ | libc/○ | The atime and mtime of `utimens`. Deciding on `UTIME_OMIT` / `UTIME_NOW` |
| `mode_t`/`uid_t`/`gid_t`/`pid_t`/`off_t`/`size_t` | sys/types.h | ✅ | libc/○ | Scalars. **The fork's infinite-recursion bug of `mode_t -> uint` (Tmds.LibC 0.3.0) is avoided by defining them ourselves** |

**Where `stat` / `statvfs` / `timespec` come from** is decided in 3-7 (whether Tmds.LibC stays or it becomes our
own P/Invoke).

---

## 3. The design of the in-house binding (the proposals plus the trade-offs)

### 3-1. How the symbols are resolved

| The option | The method | The advantages | The disadvantages |
|---|---|---|---|
| **(A) `dlopen` plus `dlvsym` (following the current one)** ★ recommended | Open `libfuse3.so.3` at runtime and resolve with the version | **It handles symbol versioning** (`fuse_new`=`FUSE_3.1`, the rest `FUSE_3.0`). This is **mandatory**: a bare `dlsym` or `DllImport` picks up the default version, and grabbing the wrong version of `fuse_new` is an ABI mismatch | The conversion from a function pointer to a delegate has to be done by hand |
| (B) `[DllImport]` / `[LibraryImport]("libfuse3.so.3")` | P/Invoke directly | Concise to write; the marshalling is generated | **The version cannot be specified the way `dlvsym` does** -> the concern of not grabbing the versioned symbol of `fuse_new` correctly. Loading the fixed `.so.3` name also needs adjusting |
| (C) `NativeLibrary.Load` plus `GetExport` | A .NET API | More flexible than DllImport | `GetExport` **does not handle versioned symbols** (the same hole as (B)) |

-> **(A) is recommended**: the reason the current one chose `dlvsym` (symbol versioning) is essential. The
`dlopen` / `dlvsym` of `libc` themselves follow the libc dependency policy of 3-7.

### 3-2. The managed-to-native bridge

| The option | The method | The advantages | The disadvantages |
|---|---|---|---|
| **(A) Delegates plus `GetFunctionPointerForDelegate` (following the current one)** | Each op is an instance method -> a delegate -> a function pointer | **The closure over the `FileSystem` instance is captured as it is** (pgfs is path-based and does not use private_data). The implementation is straightforward | The delegates have to be kept alive from the GC (solved by holding them in a field) |
| (B) `[UnmanagedCallersOnly]` statics plus a `delegate*` table | The static function pointers of .NET 5+ | Lighter thunks; friendly to AOT | **Being static, the instance cannot be captured directly** -> a GCHandle has to be put in the `private_data` of `fuse_new` and restored in each callback from `fuse_get_context()->private_data`. pgfs is one mount and one FS, so a static field would do, but it is more design |

-> **(A) is recommended, following the current one.** pgfs is one process, one mount, one FS, and the current
delegate approach is the cheapest. AOT was never the policy (`PublishAot=false`), so (B)'s advantage is thin.

### 3-3. How far the op table is wired

- **26 are wired = the 25 implemented ops plus `init`** (in bold in 1-B). `init` is wired **only for use_ino**
  (pgfs's FS does not override the op).
- **16 are left null** (1-B). libfuse returns ENOSYS or the kernel handles it. `access` is delegated through
  `default_permissions`, so it is **deliberately not wired**.
- The current fork wires `flush` / `fsync` / `fsyncdir` / `access` / `fallocate` too and returns ENOSYS, but
  **the in-house one makes them null and simplifies** (identical behaviour, less code).

### 3-4. The errno and exception conventions

- **Returning a negative errno directly** is followed. Each callback is wrapped in
  `try { … } catch (Exception e) { Log(e); return -EIO; }`.
- `release` / `releasedir` are void on the FS side -> the callback returns 0.
- The errno constants follow the libc policy of 3-7 (`Tmds.LibC`'s constants or our own consts).

### 3-5. The passthrough of the mount options

- The arg vector is built with `fuse_opt_add_arg`: `[0]=""`, `[1]="-o" + the joined string`.
- The join is **`attr_timeout=0` (the default) plus `MountConfig.FuseFlags` (from fstab or the CLI)**, as today.
- **`use_ino` is not passed with `-o`** (it is on by default in libfuse3, and passing it makes `fuse_new` fail
  with an unknown option) -> it is secured by `fuse_config.use_ino=1` in `init`.

### 3-6. The known hacks and fixes being carried over (how they are handled in house)

| The item | The current fork | The policy in house |
|---|---|---|
| `fuse_config.use_ino=1` | A raw poke at offset 64 | **(B) taken**: type `fuse_config` properly and write through `cfg->use_ino`. The fragile offset dependency is removed. It needs every field of `fuse_config` typed (verified against the real `fuse.h`) |
| `fuse_get_context` | Optional resolution plus a partial mirror | Followed. A `fuse_context` that reads the uid/gid/pid is typed |
| **The argument order of `symlink`** | (linkname, target), **the other way round** | **Put into the C order `(target, linkpath)`** = fixing the fork's bug |
| **The bitfield of `FuseFileInfo`** | `KEEPCACHE=3` is doubtful | **Re-confirm the bit position against the real `fuse_common.h`** and correct it. pgfs reads only the `flags`, so the real harm is small, but it is defined correctly |
| `mode_t -> uint` | `Unsafe.As` to work around the recursion bug of Tmds.LibC 0.3.0 | **Not needed if `mode_t` is defined in house** (a plain uint32) |

### 3-7. The libc dependency (whether `Tmds.LibC` stays)

The libc features depended on = `dlopen` / `dlvsym`, the types such as `stat` / `statvfs` / `timespec` /
`mode_t`, the errno constants and `UTIME_NOW` / `UTIME_OMIT`.

| The option | What it is | The advantages | The disadvantages |
|---|---|---|---|
| **(A) Keep `Tmds.LibC` (MIT)** ★ recommended (provisionally) | The NuGet dependency as it is | The accurate layouts of `stat` / `statvfs` / `timespec` and the errno constants come **already proven**. The main purpose of going in house (an in-house binding to FUSE) is achieved | One managed dependency remains, against the ideal of "single file, minimal dependencies" (though MIT poses no problem for publication) |
| (B) Our own P/Invoke to libc | `dlopen` / `dlvsym` and the needed types defined in house | Completely dependency-free | The effort and the risk of raising the per-architecture layouts of `stat` / `statvfs` (x86-64 / ARM64 / glibc / musl) accurately by hand are large |

-> **(A) is recommended, provisionally.** The point of going in house is "an in-house binding to libfuse", and
the libc primitives are a separate matter. (B) carries a high risk in porting the `stat` layout, so it is
considered as a separate task once the in-house FUSE layer has settled.

### 3-8. The file structure inside `Pgfs.Fuse`

The in-house binding is divided as follows (following the current fork's `LibFuse.cs` /
`LibFuse.structs.cs` / `FuseMount.cs` while tidying them):

| The file | Its responsibility |
|---|---|
| `Native/LibFuse.cs` | Resolving the symbols with `dlopen` / `dlvsym` plus holding the function pointers of the 10 in use (versioned: FUSE_3.0/3.1) |
| `Native/FuseStructs.cs` | The C# type definitions of `fuse_args` / `fuse_operations` / `fuse_file_info` / `fuse_context` / `fuse_config` |
| `Native/FuseOperations.cs` | The op delegate types (25 plus init) and building the `fuse_operations` table (`GetFunctionPointerForDelegate`) |
| `FuseMount.cs` | The lifecycle of `fuse_new` -> `fuse_mount` -> `fuse_loop[_mt]` -> the teardown, plus the errno wrapping plus use_ino (`init`) and get_context |
| `IFuseFileSystem.cs` / `FuseFileSystemBase.cs` | The managed FS interface (25 ops; it follows the current fork's API closely to minimize the changes to pgfs's `FileSystem.cs`) |
| `MountOptions.cs` / `FuseFileInfo.cs` / `DirectoryContent.cs` / `ReadDirFlags.cs` / `TimespecExtensions.cs` | The supporting types (ported and tidied from the current fork) |

Note: whether it goes into a `Native/` sub-namespace or stays flat can be decided at implementation time.

---

## 4. The design judgements

1. ✅ **Resolving the symbols = (A), following `dlopen` plus `dlvsym`** (3-1). Handling the symbol versioning
   (FUSE_3.0 / fuse_new=FUSE_3.1) is essential.
2. ✅ **The bridge = (A), following the delegates plus `GetFunctionPointerForDelegate`** (3-2). It is the
   cheapest for pgfs with its one process, one mount and one FS (AOT was disabled by policy anyway).
3. ✅ **`use_ino` = (B), typing `fuse_config` and writing `cfg->use_ino`** (3-6). The fragile offset-64 poke is
   retired and every field of `fuse_config` is typed properly (verified against the real `fuse.h`).
4. ✅ **The libc dependency = (A), keeping `Tmds.LibC` (MIT), provisionally** (3-7). The proven `stat` /
   `statvfs` / `timespec` / errno are used. Our own libc is a separate task once the FUSE layer has settled.
5. ✅ **How far the op table is wired = the 25 implemented ops plus `init` only** (3-3). No slots are made now
   for the future ops (`copy_file_range` / `lseek`) (YAGNI; they are added when they are needed).
6. ✅ **The file structure inside `Pgfs.Fuse` = the structure of 3-8**.
   `Native/{LibFuse,FuseStructs,FuseOperations}` plus `FuseMount` plus `IFuseFileSystem` /
   `FuseFileSystemBase` plus the supporting types. Whether `Native/` becomes a sub-namespace is at the
   implementer's discretion.

**-> (1) to (6) are all settled. (B) is closed as a design. Next is the implementation phase.**

## 5. The verification policy

- That the in-house binding is **behaviourally equivalent** to the fork is confirmed with the full e2e suite:
  Linux 36/36 plus the multi-node race plus the audit (the `fuse_get_context` path) plus xattr/ACL (the
  `getxattr` size probe, `system.posix_acl_access`).
- The use_ino regression: after `ln a b`, `stat -c '%i'` agreeing between the hardlinks, plus `st_nlink` being
  reflected immediately with `attr_timeout=0`.
- The fix to the argument order of symlink is confirmed with a real `ln -s` test (that the fork's bug is fixed
  in the in-house one).

## 6. The mirror of `fuse_config` and how to check the offsets

The `fuse_config` in [LibFuse.structs.cs](../../src/fuse/src/LibFuse.structs.cs) is **a partial mirror of
libfuse's struct** that `FuseMount.Init` lays over the native pointer and writes through. **It is copied only as
far as the fields that are needed**, so check the offsets before touching anything beyond that.
**Being off by one field means stepping on `show_help` or the `modules` pointer.**

### The offsets settled by measurement (x86-64 SysV, libfuse **3.10.2**)

What is installed on this machine is **`fuse3-3.10.2-9.el9`** (`/usr/lib64/libfuse3.so.3 ->
libfuse3.so.3.10.2`).

| Offset | The type | The field |
|---:|---|---|
| 0-20 | int / unsigned int x6 | `set_gid` / `gid` / `set_uid` / `uid` / `set_mode` / `umask` |
| 24 / 32 / 40 | double | `entry_timeout` / `negative_timeout` / `attr_timeout` |
| 48 / 52 / 56 | int | `intr` / `intr_signal` / `remember` |
| 60 | int | `hard_remove` |
| **64** | int | **`use_ino`** (the value this mirror has been writing all along) |
| 68 / 72 / 76 / 80 / 84 | int | `readdir_ino` / `direct_io` / `kernel_cache` / `auto_cache` / `ac_attr_timeout_set` |
| 88 | double | `ac_attr_timeout` |
| **96** | int | **`nullpath_ok`** |
| 100 / 104 / 112 | int / `char*` / int | `show_help` / `modules` / `debug` |

> ⚠ **3.10 has no `no_rofd_flush`.** That field **came in with 3.15**, so
> **counting with a header from 3.15 or later shifts everything after `ac_attr_timeout`.**
> **Always count with the header of the version that is installed.**

### How to check it (possible even without the header)

This machine **does not have `fuse3-devel` installed and has no `/usr/include/fuse3/fuse.h`**. Even so, the
chain can be verified by **writing and reading back**. Inside `Init`:

1. **Can the defaults libfuse put in be read?** - `intr_signal` should be **`SIGUSR1` (10)**. It is the value
   libfuse puts in at `fuse_new` and pgfs does not overwrite it.
2. **Can the value we passed be read?** - `attr_timeout` should be **0**. That is **the value pgfs passed with
   `-o attr_timeout=0`**, so **reading it means the positions are right as far as the three doubles** = the
   first half of the chain is connected. `entry_timeout` reads back libfuse's default of **1**.
3. Then write, and **read it back to see whether it is what was expected**.

**The measured log** (2026-09-21):

```
intr_signal=10  entry_timeout=1  attr_timeout=0  remember=0  ac_attr_timeout_set=0
hard_remove=1   use_ino=1        nullpath_ok=1
```

**Step 2 is what makes this procedure work.** With step 1 alone, "libfuse's default happened to be there"
cannot be ruled out, but **if the value we passed can be read back, that position really is that field**.

### The runtime safety valve (put it in together with anything you raise)

**Before writing beyond the verified first half, look at the fingerprint.** Confirm
`intr_signal == SIGUSR1` and, if it differs, **write nothing beyond that point and only warn**. If the mirror
has drifted from the real thing, **that catches it before stepping on a pointer**.

> **Note**: `nullpath_ok` is **not raised today**. The reason, and the story of trying to raise it and backing
> out, are in [handle-context.md, why `hard_remove` is not raised](handle-context.md).
> **This section is a record of the offsets and of the verification procedure**, and does not mean "it should be
> raised". It is kept so that **the arithmetic does not have to be redone** when a move to the low-level API is
> considered.
