# The changelog

> **Route**: [docs/README.md](docs/README.md) › **this document**
>
> **What this document is the source of truth for**: the user-facing differences **per release tag**. A change
> that requires a migration, a known limitation and a fix for something that could have corrupted data are
> always written here. **Internal refactors do not go in.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [docs/history.md](docs/history.md) | **How** the completed items with no document of their own came about |
> | Each `docs/design/*.md` | The design and the as-built per feature |

This file records the changes **per release tag**. The granularity is "a difference a user can see", and
internal refactors are not listed as a rule ([docs/history.md](docs/history.md) and the design documents are
authoritative for how things came about).

## [Unreleased] v0.2.0

> **The difference from v0.1.0**. The pillars are (1) rebuilding the project structure (bringing
> libfuse in house), (2) the operations phase = the caches / write-back / `pgfsctl` / the GUI, (3) raising the
> Windows implementation and (4) fixing the correctness around hardlinks.

### ⚠ Changes that require a migration

- **The `vendor/Tmds.Fuse` submodule was retired.** The libfuse binding was brought in house into `Pgfs.Fuse`
  ([src/fuse/](src/fuse/)), so **`git clone --recurse-submodules` is no longer needed**.
  If you keep using a v0.1.0 clone, it can be detached with `git submodule deinit -f vendor/Tmds.Fuse`.
  The credits point at the originals in [src/fuse/NOTICES.md](src/fuse/NOTICES.md).
  **On Linux, libfuse3 (`libfuse3.so.3`) is needed at runtime** - it is `dlopen`ed, so install your
  distribution's `fuse3` equivalent.
- **One schema was added (`{prefix}mounts`).** It is a volatile registry of the running mounts and the
  foundation of `pgfsctl status` and `pgfsctl prune`. **On an existing filesystem, run
  [docs/ddl/pgfs_mounts.sql](docs/ddl/pgfs_mounts.sql)** (reading `pgfs.pgfs_mounts` as your own
  `<schema>.<prefix>mounts`). **On Citus, also run
  `SELECT citus_add_local_table_to_metadata('<schema>.<prefix>mounts');`** after it (a local table registered in
  the metadata rather than distributed, like `{prefix}lock` and `{prefix}settings`).
  **Do not use a re-run of `mkfs` for this** - even without `--clean` it **overwrites the settings stored in the
  database** (`audit.enabled` / `app.plperlu` / `database.citus` / the tablespace / `file_system.*` and so on)
  **with the CLI values of that run or the defaults**, and rewrites `pgfs.toml` too (see the known limitations
  below). Until it is run, the mount merely warns and skips registering, with no effect on reading or writing.
  **`pgfsctl prune`, though, cannot decide which mounts are alive, so it leaves the side that removes data alone
  even with `--force`.**

### Added

- **`pgfsctl`** - the CLI of the runtime control plane ([docs/Pgfsctl.md](docs/Pgfsctl.md)).
  - `pgfsctl config get / list / set` - **it can be applied live to a running mount**
    (the control channel is always on, so it reaches a mount started without `--notify` too).
  - `pgfsctl status [--json]` - the list of what is running in the cluster / the FS statistics / the running
    processes' cache statistics and effective settings.
  - `pgfsctl prune [--apply]` - **it cleans up what an abnormal exit left behind**: the stale rows of
    `{prefix}mounts`, the data rows referenced by no inode, and libfuse's `.fuse_hidden*` remnants.
    **The default is a dry run.** **The side that removes data is not touched at all if even one mount is
    alive** (overridable with `--force`, after stopping every mount). A mount on the same host is judged
    alive by its pid, and a mount on another host is presumed alive until it is past the grace period
    (`--mounts-older-than`, 3600 seconds by default, **600 seconds at the least**), so a mount whose heartbeat is
    merely late is never treated as dead. **When `{prefix}mounts` cannot be read (an existing filesystem that has
    not been migrated, say), which mounts are alive cannot be decided, so the side that removes data is left alone
    even with `--force`.** Only a name of libfuse's exact form (`.fuse_hidden` plus 16 hex digits) counts as a
    remnant; a file a user named `.fuse_hidden_notes.txt` is left alone. **A row that recorded a loss (a
    gravestone) is not removed.** A mount registers itself again at the heartbeat interval when its registry row
    has disappeared. The safety valves are in [docs/Pgfsctl.md](docs/Pgfsctl.md).
- **The write-back cache** (`mount.write_back`, **off by default**). Measured at **6.1x** with `dd bs=128k` and
  1.4x with `rsync`. [docs/design/write-back.md](docs/design/write-back.md) is the source of truth.
- **The metadata write-back** (`mount.write_back_metadata`, **off by default**). It stops the synchronous flush
  at close and writes the pending inodes together in one tx. `fsync` / `fsyncdir` become the only hard barriers.
  The synchronization heuristics (rename-over-existing / `O_TRUNC` / `O_EXCL`) are built in.
- **The caches** - the inode LRU (`mount.cache_max_entries`) / the content cache
  (`mount.cache_data_max_bytes`) / the negative cache (`mount.negative_cache_ttl_ms`).
- **A GUI (Avalonia)** - a read-only dashboard. The connection bar / Mounts / Filesystem / Process detail /
  Config. **It cannot change the settings yet** (see the known limitations below).
- **Windows (assign.pgfs)**:
  - A new inode's **owner is derived from the requesting account** (the group is inherited from the parent
    directory).
  - The **byte-range locks** were left to the driver (they used to "always succeed" = lying that a lock that was
    never taken had been taken).
  - `FILE_FLAG_WRITE_THROUGH` is wired as a complete barrier on every WriteFile.
  - Unmounting while leaving something unflushed is reported with **exit 4**.
  - An unusable mount point (a drive letter in use and so on) is rejected **before starting**, with the free
    candidates listed.
- **Staging the stop signals** (shared by mount.pgfs and assign.pgfs). The 1st = a request to unmount, the
  **2nd = abandon the wait for the flush, enumerate what is being lost and exit 4**, and the 3rd and later = an
  immediate exit.

### Changed

- **The project was split into four** - `Pgfs.Core` (the shared SQL with zero OS branching) / `Pgfs.Fuse`
  (Linux) / `Pgfs.Dokan` (Windows) / the thin executables.
  [docs/design/v0.2.0-plan.md](docs/design/v0.2.0-plan.md) and
  [docs/design/fuse-binding.md](docs/design/fuse-binding.md) are the sources of truth.
- **The timestamps were unified on UTC** (a `TIMESTAMP` column is always stored as UTC).
- **`st_blocks` now returns the occupied bytes** (`du` sees the real size).
- The documents were arranged under `docs/design/`. The index is
  [docs/README.md](docs/README.md).

### Fixed

- **An open file was identified by its path**, so a rename or a recreation of the name while it was open
  landed the later reads and writes on the wrong file. An open handle is now looked up by the inode id
  settled at open time. The design and the measurements are in
  [docs/design/handle-context.md](docs/design/handle-context.md).
- **A connection string in the URL form (`postgresql://user:pass@host:port/db`) could not start anything.**
  The documentation and the fstab examples assumed the URL form, but Npgsql does not interpret URLs, so writing one
  in `-c` or in the first column of fstab **did not even start**, failing with `Format of the initialization string
  does not conform to specification` / `Couldn't set postgresql://...` (the latter message **carries the password in
  plain text**). The libpq connection URI format (percent-encoding / several hosts / `[::1]` for IPv6 / query
  parameters such as `?sslmode=`) is now interpreted and converted to the kv form. **A query parameter that cannot be
  interpreted (a misspelt `sslmod=` and the like) is not dropped silently; the start stops, naming the parameter.**
  The startup log, the header comment of a generated `pgfs.toml` and `pgfsctl config list` hide the password of the
  URL form too.
- **The bundled settings sample did not work as it was**.
  [pgfs.toml.example](pgfs.toml.example) wrote the connection in a **table form** such as
  `database.connection.host = "..."`, but **the settings file only interprets the two levels of
  `scope.key = value`**. Copying it mangled the connection string and the startup failed with
  **a `Format of the initialization string does not conform to specification` exception that points at nothing**.
  The sample was fixed to **a one-line connection string**, and the claim in
  [docs/Mkfs.md](docs/Mkfs.md) that it "can be written per item in the settings file" was removed.
  **If a table form is written, the value is not taken and it warns "write it on one line"**, so even with the
  old sample already copied it is clear what is wrong.
- **When a change notification did not arrive, another client kept returning a stale value forever**.
  A notification whose sending failed (a database blip / a payload over `pg_notify`'s limit of 8000 bytes), and the
  notifications sent while the receiver's LISTEN connection was down, **never arrive**. A receiver's inode cache has
  **no TTL**, so it **kept returning a stale size or stale contents** until the parent was looked up again with an
  `ls` or it fell out of the cache. The side that could not send now folds it into one "discard the whole cache"
  and always sends it with the next send (including the backlog at shutdown), and a receiver **drops its clean
  caches by itself when it re-establishes LISTEN**. Only the clean entries are dropped; the unflushed dirty data
  and the metadata write-back's pending inodes are kept.
- **An overwrite after another mount shrank the file made the written bytes unreadable**.
  After another mount did a `truncate`, **an overwrite that does not extend the size** (a write with no
  `O_TRUNC`) left **the `st_size` unupdated and the written bytes unreachable** (`stat` said 0 and `cat`
  returned empty, while the chunks stayed in the database). The cause was **using the local cache to decide the
  size**. The same happened in the window before the notification arrived even with
  `database.notify_enabled` on. **Reading and writing from a single mount is unaffected.**
- **The body sharing of hardlinks was broken** (the details are in
  [docs/design/data-id-lifecycle.md](docs/design/data-id-lifecycle.md)). Three defects came from one root:
  - Zeroing with a `truncate` or a `>` **removed the shared body, leaving the sibling link with lost data and a
    broken reference** (`cat` returned NULs and the `st_nlink` broke too).
  - **A hardlink to an empty file was not a link** (both stayed at an `st_nlink` of 1).
  - **Writing the contents changed the `st_ino`** (making `rsync -H`, `find -samefile` and `tar` misbehave).
- **A file could be created under a deleted directory.** Creating one after another client removed the parent
  left **an orphan unreachable from the FS** that could only be cleaned up with SQL.
- **The size and the modification time did not propagate between hardlink siblings** (both within one mount and
  across mounts; on the write-through path - the write-back flush path still has a gap, see the known limitations).
- Windows: the exclusion of a simultaneous `CREATE_NEW` is now decided by the database's unique constraint
  (it used to be a local non-existence check, which could not win against another mount).
- Windows: **the audit log's caller was always empty** (`GetRequestor` succeeds only inside `CreateFile`).

### The known limitations

- **Do not write the same file from several mounts with `mount.write_back` on.**
  While a write-back mount is holding unflushed data, a write to the same body from another mount
  **disappears at the flush** (measured on both Linux and Windows on 2026-09-21). That is because the dirty
  buffer is held as **the complete image of a chunk** and the flush writes the whole of it back.
  **The size itself is never rewound** (a `truncate` by another mount keeps its effect on the `st_size`), but the
  bytes inside the chunks the write-back side holds are overwritten at the flush. So **bytes another mount removed
  with `truncate` can be read back with their old contents once the file is extended later** (not zero-filled, and
  `du` counts them too). **Do not rely on this setup for a `truncate` meant to erase something sensitive.**
  **What disappears is whole chunks, not individual bytes**, and the target is
  **every chunk the write-back side has ever read** (1 MiB by default). **`--notify` does not prevent it**
  (the notification drops the content cache but the dirty state is deliberately left alone).
  **It is off by default**, so it has no effect unless it was turned on explicitly. Reading and writing from a
  single mount, and a configuration where each mount writes different files, are unaffected.
  For the details see [docs/Mount.md](docs/Mount.md), the section on not writing the same file from several
  mounts with `mount.write_back` on.
- **A record of exiting with something unflushed (a gravestone) can only be removed with SQL.** When a mount
  ends with write-back unable to write things back, the `{prefix}mounts` row is **not removed** and records how
  much was lost (its `endedAt` is UTC and carries a trailing `Z`, while the times on the log lines are local,
  so compare them with that in mind). That is **deliberate** - so that the record of an accident does not
  disappear silently, and **`pgfsctl prune` does not remove it either**. But **the only way to remove it is
  `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0`**, so
  **in an environment with no psql (typically Windows) it cannot be tidied up and the same warning keeps
  appearing in the log at every mount**. **The warning itself is not a defect** (it is the record of a past
  loss). Running the `DELETE` above after checking the contents stops it. A way to remove one by name is a
  future matter.
- **There is a ceiling on an append's indivisibility** - an append lands at "the end as it is now", and
  **under write-through no bytes are lost even in parallel with another mount** (the end is settled inside the
  write transaction). But **one whole `write(2)` is not indivisible**: a write past the `max_write` (negotiated
  per environment; 1 MiB on the measured host) is split into several callbacks and
  **another mount's append can slip into the gaps** (no bytes are lost, but a contiguous region is not
  guaranteed). **With write-back enabled it is not guaranteed** - the peer's bytes are not in the database yet,
  so it cannot be made indivisible in principle. **An application that seeks by itself before writing (.NET's
  `FileMode.Append` and so on) is out of scope.** For the details see
  [docs/Mount.md, the append contract](docs/Mount.md).
- **A delete is deferred on Windows** - even after `Remove-Item` returns, Windows does not tell the filesystem
  about the delete until the last handle closes. pgfs removes it as soon as it receives it and notifies
  (measured at 0.95 seconds to land on another mount). NTFS behaves the same way; it is not specific to pgfs.
- **`--notify` is effectively mandatory for several mounts on Windows** - with the default
  (`database.notify_enabled = false`), another mount's changes are never visible.
- **Native links (hardlinks, junctions and symlinks) can be neither created nor read on Windows** - the current
  Dokan binding has no entry point. A symlink of Linux origin disappears from the Windows enumeration.
  **But a hardlink created on Linux is judged to be "the same file" from Windows too**
  (the `FileIndex` was made to come from the `data_id`, the same formula as the `st_ino`).
- **The permissions, the owner and an explicit timestamp change are not shared between hardlink siblings** -
  under POSIX, links pointing at the same body share the `st_mode` / `st_uid` / `st_gid` / `st_mtime`, but
  pgfs holds **an independent row per link**. **The contents, the size and the `st_ino` are shared**
  (corrected by making the `data_id` immutable) and **an `st_mtime` update from a write is
  distributed to the siblings**, but **a `chmod` / `chown` / `touch` (`utimens`) only affects the link it was
  fired at**. There are two effects:
  - **Setting one to `0600` still lets the other name open it at the original permissions.**
    **Do not use a hardlink as a permission boundary.**
  - **A time you thought you updated with `touch` stays stale on the sibling**, so
    **a tool that judges by the timestamp, such as `make` or a backup, wrongly concludes "it has not been
    updated" when it looks at the sibling's name.**
- **ADS (Alternate Data Streams) is unsupported.**
- **An exchanging rename (`RENAME_EXCHANGE`) is unsupported** - a rename giving `RENAME_EXCHANGE` or
  `RENAME_WHITEOUT` to Linux's `renameat2(2)` is **refused with `EINVAL`**. What is supported is
  `RENAME_NOREPLACE` only. **It is deliberately not silently degraded into an ordinary replacement**, because
  **an operation meant as an exchange being committed as a replacement irreversibly removes what was being
  swapped in**. An ordinary rename with no flag and `RENAME_NOREPLACE` work as before.
- **The audit record of a cancelled pending metadata change may not survive if the process dies before it is
  written back** - when the metadata write-back (`mount.write_back_metadata`, **off by default**) has a pending
  change and **an operation cancels it** (creating and removing something at once, say), the audit rows go onto
  a queue. **Only the create/delete pair of a cancellation is written synchronously** and survives, but
  **the rest of the queue waits to be written back**, so **an abnormal exit while it is waiting loses those
  audit rows**. **An operation succeeding does not mean its audit row was persisted at that point.**
  **It does not happen with the default (off).** For the details see
  [docs/design/audit-log.md](docs/design/audit-log.md).
- **On Linux, exiting with something unflushed is not communicated through the exit code** - `mount.pgfs`
  **exits 4 when it unmounts with something unflushed**. But **with the default through `mount(8)` or
  `/etc/fstab` (daemonized), the parent process returns `0` at the point the mount is established and exits
  first**, so **that `4` does not reach the caller**. **It only arrives when started with `--foreground`.**
  **There are 4 alternative reporting paths**: the record left in `{prefix}mounts` (the gravestone) / the audit
  row (`op = writeback_loss`) / **the warning at the next mount** / the loss display of `pgfsctl status`.
  **On Windows (`assign.pgfs`) the `exit 4` arrives as it is.** But **all 4 paths write to the database**, so
  **when the reason the flush failed was the database itself, none of them can be written** (the loss survives only
  as an Error line in the log; the default log output is stderr, which reaches nobody when daemonized). When using
  write-back, keep a file log with `--log-output` as well.
- **The GUI is read-only** - use `pgfsctl config set` to change a setting.
- **Re-running `mkfs` overwrites the settings stored in the database** - even without `--clean`, it rewrites
  `audit.enabled` / `app.plperlu` / `app.statfs` / `database.citus` / the tablespace / `file_system.*` /
  `fallback_*` **with the CLI values of that run or the defaults**, and replaces `pgfs.toml` with a generated one.
  Unless every option it was created with is given again, **the audit stops silently and the plperlu that was
  refused gets installed**. To change an existing filesystem, do not run `mkfs`; run the DDL you need directly
  (the migration above).
- **`-o allow_other` alone does not enforce permissions** - pgfs does not decide access by itself and leaves that to
  the kernel through `default_permissions`. **When giving `allow_other`, give `default_permissions` too** (without it
  every local user can read and write every file). Forgetting it gives a warning at startup.
- **The control channel has no authentication** - the NOTIFY that `pgfsctl config set` / `status` use can be fired
  by **any role that can connect to the same database**, so anyone who can connect can change the Live settings of
  every mount (`audit.enabled` and so on) without leaving a trace in `{prefix}settings`. **Restrict the right to
  connect to the database itself to the people allowed to operate the mounts.**
- **`pgfsctl config set` applies to every mount at once** - the target mount cannot be chosen. In particular,
  **turning `mount.write_back` on live makes every running mount write-back, which walks straight into "do not
  write the same file from several mounts" above**. It also answers "applies on the next mount" for **items only
  mkfs uses** such as `database.tablespace*` / `database.citus` / `app.plperlu` / `database.shard_*`, but they take
  effect nowhere.
- **The write-back flush decides the distribution of size and time to hardlink siblings by the `st_nlink` in the
  local cache** - when the entry has fallen out of the cache, or the link was made on another mount and the cache is
  stale, the siblings are not updated (the write-through path decides by the database's value, so it does not miss).
- **Windows does not tell names apart by case** - the database and Linux do, so when Linux creates both `README` and
  `readme`, a wildcard search from Windows matches both and can mix up which one is opened or removed. **Do not let
  names that differ only by case coexist.**
- **Editing an ACL from Windows garbles the owners / groups / named ACL entries Windows cannot resolve** - every
  unresolved principal is projected as `ANONYMOUS LOGON`, and SetFileSecurity rewrites the mode and the named ACL,
  so **the group bits on the Linux side and `setfacl` entries can change**. Where the user names of both operating
  systems are not aligned, **change permissions from the Linux side.**
- **Some names cannot cross operating systems** - a Linux name that is not valid UTF-8 is squashed to U+FFFD and
  **collides with another name** (unpacking a Shift_JIS zip, say). A name with `:` or `\` cannot be opened from
  Windows or turns into a different name. One name longer than 1024 bytes in UTF-8 **makes `ls` of that directory
  fail with EIO on Linux** (Windows can create it).
- macOS is unsupported.

## [v0.1.0] - 2026-06-06

The first public release. The three pillars `mkfs.pgfs` / `mount.pgfs` (Linux, FUSE) / `assign.pgfs`
(Windows, Dokan), storing the data in bytea chunks, the audit log, the Citus horizontal distribution, the
interoperability of the POSIX and Windows ACLs, the `df` support, the byte-string transparency of the xattr and
the `/etc/fstab` support.
