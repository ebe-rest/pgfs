# fstab support (Linux)

> **Route**: [docs/README.md](../README.md) › [../Mount.md](../Mount.md) › **this document**
>
> **What this document is the source of truth for**: the specification for starting `mount.pgfs` through
> `/etc/fstab` and `mount(8)`. The helper calling convention (the positional arguments and the helper-context
> decision), the intake for `-o`, the child-process separation (the `MOUNTED` signal scheme) and the
> daemonization, installing `/sbin/mount.pgfs`, and the real-hardware verification of mounting at boot all
> belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../Mount.md](../Mount.md) | **The current CLI contract and the classification and defaults of the `-o` options.** What an individual option means is authoritative there |
> | [fuse-binding.md](fuse-binding.md) | The design of the libfuse binding itself (dlopen / `fuse_config.use_ino` / the op wiring) |
> | [settings-matrix.md](settings-matrix.md) | The complete list of the settings (where this document's excerpt table comes from) and the defaults |
> | [../architecture.md](../architecture.md) | The structure of the whole Config model and the build and layout |
>
> Where the settings model is implemented: [src/core/src/Config/](../../src/core/src/Config/) - the methods of
> `ConfigLoader.cs` this document refers to are linked in the text, and the settings are defined in
> [Schema.cs](../../src/core/src/Config/Schema.cs).

A specification memo for making `mount.pgfs` callable from `/etc/fstab`. The binary name `mount.pgfs` already
matches the `/sbin/mount.<type>` convention that Linux's `mount(8)` looks for, so what has to be supported comes
down to two things: **the intake for the calling convention** and **the daemonization**.

The status: **complete (mounting at boot was verified on real hardware on 2026-06-02)** - the positional
arguments, the `-o` parser, the child-process separation (the `MOUNTED` signal scheme), installing to
`/sbin/mount.pgfs`, mounting through `mount -t pgfs` and `mount -t fuse.pgfs`, **access from a non-root user**
(`-o allow_other`) and unmounting have all been confirmed. **Mounting at boot is confirmed too**: merely
rebooting the Linux client had `/mnt/pgfs` mounted automatically from the fstab line (it appears in `findmnt` as
`/dev/fuse … default_permissions,allow_other`, and an ordinary user can read the entries with
`ls -al /mnt/pgfs`). The libfuse binding has been **brought in house** into `Pgfs.Fuse`
([src/fuse/](../../src/fuse/)) (derived from the old Tmds.Fuse fork; see the
[NOTICES](../../src/fuse/NOTICES.md)). Arbitrary `-o` options can be passed to libfuse through
`MountOptions.Options`, and `use_ino` is set with `fuse_config.use_ino=1` in the init callback.

---

> **The static comparison of 2026-09-19**: this document contains the original design and past real-hardware
> records. The daemon separation is implemented now. [Mount.md](../Mount.md) is the source of truth for the
> current option handling. **Both the leftover forwarding of `-o max_write` and the ignoring of the safety and
> sync options were dealt with** (the latter by making it explicit with a warning).
> **Mounting at boot and applying the OS flags remain unverified.**

## The motivation (at the point the design was started)

Today `mount.pgfs` is a single-file self-contained binary produced by `dotnet publish`, but the only way to
start it is manually with `mount.pgfs -c <conn> -m <mountpoint>`. The aim is to write it into `/etc/fstab` and
bring it up at boot or with the short command `mount /mnt/pgfs`.

As a side effect, integrating with a `systemd` `mountpoint.mount` unit or with autofs becomes natural too (both
call `/sbin/mount.<type>` internally).

---

## The helper calling convention of `mount(8)`

When processing `mount -t pgfs <source> <target> -o <opts>` or an fstab line, `mount(8)` calls the helper like
this:

```text
/sbin/mount.pgfs <source> <target> [-i] [-f] [-n] [-s] [-v] [-N <ns>] [-t <type>] [-o <opts>]
```

For the meaning of each flag and how mount.pgfs treats it, see
[the argument correspondence between mount(8) and mount.pgfs](#the-argument-correspondence-between-mount8-and-mountpgfs).

The helper is expected to **exit once the mount is complete** (so that mtab can be updated). Sitting in the
foreground means the `mount` command never returns.

---

## The flow of the arguments when mounting through `mount(8)`

The reason writing an fstab entry lets it be invoked with **only the mount point**, as in
`sudo mount /mnt/pgfs`, is that `mount(8)` expands it in between. `mount.pgfs` itself does not read the fstab
(= the standard division of responsibility for a FUSE helper).

### An example fstab entry

```text
postgresql://pgfs@pgsql_server/pgfs   /mnt/pgfs   pgfs   _netdev,allow_other,default_permissions,cache-max-entries=4096   0 0
```

| The column | What it is | The value |
|---|---|---|
| 1 | The source (the fs spec) | `postgresql://pgfs@pgsql_server/pgfs` |
| 2 | The target (the mount point) | `/mnt/pgfs` |
| 3 | The type | `pgfs` |
| 4 | The options | `_netdev,allow_other,default_permissions,cache-max-entries=4096` |
| 5 | dump (unused) | 0 |
| 6 | pass (unused) | 0 |

### The execution flow

When the user types `sudo mount /mnt/pgfs`:

1. **`mount(8)`** interprets the argument `/mnt/pgfs` as "the mount point in the fstab's second column"
2. It scans `/etc/fstab` for a line whose second column matches -> it hits the entry above
3. It reads the first column (the source), the third (the type) and the fourth (the options)
4. The type is `pgfs`, so it forks and execs `/sbin/mount.pgfs` as the helper
5. **The argv `mount.pgfs` receives is fully expanded**:
   ```text
   /sbin/mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs -o _netdev,allow_other,default_permissions,cache-max-entries=4096
   ```
   (`mount(8)`'s own `-n`, `-v`, `-f` and so on are appended if they were given)

Inside `mount.pgfs` the positional[0] / positional[1] / `-o` parsers quietly pass the values through. The
contents of `-o` are expanded by `ParseDashOOptions` (`_netdev` is ignored, `allow_other` is forwarded to
libfuse through the `FuseFlags`, and `cache-max-entries=4096` overrides `mount.cache_max_entries`).

At that point [`IsMountHelperContext`](../../src/core/src/Config/ConfigLoader.cs) confirms that the parent's
comm is `mount` and that positionals exist, returns **true**, and enables the `MountHelperFlagsNoValue` /
`MountHelperFlagsWithValue` (= the silent swallowing exclusive to a helper context).

### Invoking `mount.pgfs` directly without going through `mount(8)`

Typing `sudo mount.pgfs /mnt/pgfs` **does not work**, because `mount.pgfs` on its own does not read the fstab:

- positional[0] = `/mnt/pgfs` -> it does not start with `postgresql:`, so it is
  **interpreted as a settings file path** ([the positional arguments](#1-accepting-the-positional-arguments-source-and-target))
- `LoadFromFile` tries to open `/mnt/pgfs` as TOML -> it fails because it is a directory, or is silently ignored
  because it does not exist
- It attempts a PG connection with `database.connection` unset -> it hits the default (localhost:5432) and fails

To make it work with only the mount point, **always go through `mount(8)`** (`sudo mount /mnt/pgfs`, or
`sudo mount -a` for the whole fstab).

---

## The argument correspondence between mount(8) and mount.pgfs

`mount.pgfs` accepts both (a) being called as a `mount(8)` helper and (b) being run directly by the user.
The same short form can mean different things, so at startup the context is decided with
[`IsMountHelperContext`](../../src/core/src/Config/ConfigLoader.cs) and mount(8)'s internal flags are silently
skipped only in a helper context.

### Deciding the helper context

It is a helper context if **both** of the following are true:

1. Some element of `args` does not begin with `-` (= a positional argument exists)
2. The parent process's comm (on Linux, `/proc/<ppid>/comm`) is `"mount"`

If either is false it is treated as a direct invocation and the `MountHelperFlagsNoValue` /
`MountHelperFlagsWithValue` are not applied (= `-f` and `-s` take effect as their real short forms). On anything
other than Linux, getting the parent process comes up empty so it is always treated as a direct invocation
(= the same code runs safely in `assign.pgfs` (Windows), which has no helper-context mechanism).

### How the arguments `mount(8)` passes to the helper are handled

| The `mount(8)` argument | What it means to `mount(8)` | In a helper context | On a direct invocation | Notes |
|---|---|---|---|---|
| **positional[0]** (= the fstab's first column `<source>`) | The FS's "data source" | Starting with `postgresql:` -> `database.connection`, otherwise -> `setting.file` (a TOML path) | The same | Both meanings have been supported ([the positional arguments](#1-accepting-the-positional-arguments-source-and-target)) |
| **positional[1]** (= the fstab's second column `<target>`) | The mount point | Flows into `mount.mount_point` (only when it is unset) | The same | |
| `-o <opts>` (= the fstab's fourth column) | Comma-separated options | Split on commas by [`ParseDashOOptions`](../../src/core/src/Config/ConfigLoader.cs) -> matched against `Field.CliOptions` / `Field.EffectiveDashOName` / `allow_other` and the like go to `MountConfig.FuseFlags` / `_netdev`, `noauto`, `noatime` and so on are ignored | The same | For the details see [the -o parser](#2-the--o-keyvalflag-parser) |
| `-i, --internal-only` | An instruction not to call the helper | Silently skipped | An unknown-option warning (mount.pgfs has no flag assigned to it) | mount(8) does not actually pass it to the helper, so it is a safety net |
| `-f, --fake` | A dry run | Silently skipped -> it actually mounts (TODO: honour fake) | Effective as the short form of `setting.file` | |
| `-n, --no-mtab` | Do not update `/etc/mtab` | Silently skipped | An unknown-option warning | With FUSE the mtab is written by fusermount3 |
| `-s, --sloppy` | Ignore unknown options | Silently skipped | Effective as the short form of `database.schema` | The direct side warns and continues on an unknown option anyway, so it is effectively sloppy by default |
| `-v, --verbose` | Verbose logging | Silently skipped | An unknown-option warning | TODO: promote `-v` to the equivalent of `--log-level debug` |
| `-N <namespace>` | A different mount namespace | Silently skipped (the value too) | An unknown-option warning plus the value may become a positional | Switching namespaces is the parent's job |
| `-t <type>` | The FS type | Silently skipped (the value too) | The same | It is already decided by the helper's name |

### The flags specific to `mount.pgfs` (= they never arrive through `mount(8)`)

[settings-matrix.md](settings-matrix.md) has the complete list; only the main ones are excerpted:

| The `mount.pgfs` flag | What it maps to | Notes |
|---|---|---|
| `-c <conn>` / `--connection` / `--connection-string` | `database.connection` | Use this when passing the kv form (`Host=...;Port=...`) |
| `-m <path>` / `--mount-point` | `mount.mount_point` | The same as positional[1] |
| `-f <path>` / `--setting-file` / `--setting` | `setting.file` | A TOML path. **`-f` is only effective on a direct invocation** (in a helper context it is silently swallowed as `--fake`). positional[0] (non-`postgresql:`) means the same |
| `-s <name>` / `--schema` / `--schema-name` | `database.schema` | **`-s` is only effective on a direct invocation** (in a helper context it is `--sloppy`). `--schema` always works |
| `-x` / `--prefix` | `database.prefix` | The table name prefix |
| `--cache-max-entries <N>` | `mount.cache_max_entries` | |
| `--self-uname <name>` / `--self-gname <name>` | `mount.self_uname` / `self_gname` | Its own identity (v0.2.1 and later; the old `--fallback-uname` / `--fallback-gname` were removed) |
| `--log-level <level>` / `--log-output <spec>` | `logging.level` / `logging.output` | |
| `--foreground` | `mount.foreground` | Suppresses the daemonization. It deliberately has no short form `-f` because of the collision with setting.file (see [the short-form collisions](#the-short-form-collisions)) |
| `--retry-max-attempts` / `--retry-initial-delay-ms` / `--retry-max-delay-ms` | `database.retry_*` | Retrying a transient error when opening the connection |
| `-?` / `-h` / `--help` | `help` | Show the usage and exit immediately |

### The short-form collisions

Where a short form from `mount(8)` has the same spelling as a `mount.pgfs`-specific flag:

| The short form | What it means to `mount(8)` | What it means to `mount.pgfs` | In a helper context | On a direct invocation |
|---|---|---|---|---|
| `-f` | `--fake` (a dry run) | The short form of `setting.file` | Silently swallowed (mount(8) wins) | Effective as `--setting-file` |
| `-s` | `--sloppy` | The short form of `database.schema` | Silently swallowed (mount(8) wins) | Effective as `--schema` |
| `-n` `-v` `-i` `-N` `-t` | mount(8)'s internal flags | (Unassigned) | Silently swallowed | An unknown-option warning |

`mount.foreground` used to have `-f` too, but because of the overlap with setting.file (which one wins depends
on the order the Fields are declared) it was removed from its CliOptions (it takes only
`--foreground`). `-f` is now the short form **for setting.file only**.

### The processing order of `ConfigLoader.ParseCli`

The arguments are scanned one at a time and settled by the first branch that matches:

1. **Only in a helper context**, `MountHelperFlagsNoValue.Contains(arg)` -> a silent continue
   (`-i` `-f` `-n` `-s` `-v`)
2. **Only in a helper context**, `MountHelperFlagsWithValue.Contains(arg)` -> skip the next token too and
   continue (`-N` `-t`)
3. `arg == "-o"` -> the next token is delegated to `ParseDashOOptions`
4. `all.FirstOrDefault(s => s.Options.Contains(arg, ...))` matches a `mount.pgfs`-specific flag
5. Not found by 4 and it does not begin with `-` -> consumed as a positional (positional[0] / [1])
6. Not found by 4 and it begins with `-` -> an unknown-option warning

The helper-context decision is precomputed exactly once at the head of the `ConfigLoader` constructor
(= it looks at the whole of `args`, deciding whether positionals exist with
`args.Any(a => !a.StartsWith('-'))` and then reading the parent process name).

---

## The implementation changes needed

### 1. Accepting the positional arguments (source and target)

[`ConfigLoader.ParseCli`](../../src/core/src/Config/ConfigLoader.cs) picks up to 2 positionals:

| The position | Its role | What it maps to |
|---|---|---|
| The 1st | The source | Starting with `postgresql:` -> `Database.Connection.Value`, otherwise -> `Setting.File.Value` (= the settings file path). Either is applied only when it has not been loaded. An explicit `-c` / `-f` / `-o connection=` wins |
| The 2nd | The target (the mount point) | `Mount.MountPoint.Value` (only when it is empty; an explicit `-m` or `-o mount_point=` wins) |

The heuristic for telling the source apart:
- `postgresql://user@host:port/dbname` -> treated as a URL-form connection string and flowed into the
  `Connection`
- `/etc/pgfs.toml` / `pgfs.toml` and the like -> treated as a settings file path and flowed into
  `Setting.File` (and `LoadFromFile` then reads the TOML)
- To pass a connection string in the kv form (`Host=...;Port=...`), be explicit with `-c`

That makes 3 ways of invoking it equivalent:

```bash
# (a) as before: the connection URL as the source
mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs

# (b) a settings file as the source (with the connection and mount_point in the TOML)
mount.pgfs /etc/pgfs.toml /mnt/pgfs
mount.pgfs /etc/pgfs.toml       # the mount_point comes from the TOML too

# (c) being explicit with -f / -m (either way)
mount.pgfs -f /etc/pgfs.toml -m /mnt/pgfs
```

#### The format of the connection column

The `postgresql://` URL form is standard. Note that including a comma in the host name breaks the parser. When
it cannot be told apart from the `,` of the fstab's fourth column, escape through `-o connection=...`.

### 2. The `-o key=val,flag,...` parser

It is merged with the "the mount option `-o`" remaining work. The implementation approach:

```csharp
// take the token after the "-o" in args,
// split it on commas -> split each element on "=" -> the key and the value
// map the key onto the Setting matching s.Options and flow the value in

// for example: -o noatime,_netdev,allow_other,connection=postgres://...,cache-max-entries=4096
//
//   noatime              -> a kernel-side flag. Not passed to FUSE and ignored (or passed to the mount syscall flags)
//   _netdev              -> a hint for fstab (wait for the network at boot). Ignoring it is fine
//   allow_other          -> a FUSE flag. Forwarded to Pgfs.Fuse's MountOptions
//   connection=...       -> written back into Database.Connection.Value
//   cache-max-entries=4096 -> written back into Mount.CacheMaxEntries.Value
```

The mapping table (initial):

| The `-o` key | What it maps to / how it is treated |
|---|---|
| `connection=...` | `Database.Connection.Value` |
| `super_connection=...` | `Database.SuperConnection.Value` (whether mount uses it needs consideration; it is basically mkfs-only) |
| `mount_point=...` | `Mount.MountPoint.Value` |
| `cache-max-entries=...` | `Mount.CacheMaxEntries.Value` |
| `log-level=...` | `Logging.Level.Value` |
| `setting-file=...` | `Setting.File.Value` |
| `allow_other` | The FUSE `MountOptions` (on the Pgfs.Fuse side) |
| `default_permissions` | The FUSE `MountOptions` |
| `ro` / `rw` | The FUSE `MountOptions` |
| `nosuid` / `nodev` / `noexec` | Kernel-side flags (the parser ignores them. Their application to the real mount is unverified and not guaranteed to be problem-free) |
| `_netdev` / `noauto` / `user` / `users` | fstab-only flags, ignored by the helper |
| `noatime` / `relatime` / `atime` | The atime policy (to be implemented if an `atime` column is ever added to the inode) |

An unknown `-o` key is pushed onto `Warnings` by `ConfigLoader.ParseDashOOptions` and flows out to the
equivalent of `Console.Error` (it is not dropped silently).

### 3. The daemonization

`mount(8)` waits for the helper to exit. The [`Program.RunFuseMountAsync`](../../src/mount/src/Program.cs) at
the point the design was started blocks on `await fuseMount.WaitForUnmountAsync()`, so through fstab the `mount`
command would never return.

#### The options

**A. Use libfuse3's automatic daemonize**
- libfuse3 `fork()`s and daemonizes unless the option `-f` (foreground) is passed
- How Tmds.Fuse 0.1.0-190711 exposes that flag was not investigated. If `MountOptions` has the corresponding
  property, it is the least effort
- To investigate: the Tmds.Fuse source / the behaviour of `Pgfs.Fuse.Fuse.Mount`

**B. Start a child process and have the parent exit (the sshfs approach)**
- Start a child with `Process.Start(the executable itself, the original args plus "--foreground-internal")`
- The parent reads a little of the child's stdout/stderr, waits for the "mount succeeded" signal and exits
- The child persists on `WaitForUnmountAsync` as usual
- It is complete within .NET. Compatibility with Windows, which has no `fork()`, is kept too (irrelevant since
  Windows uses `Assign`, but still)
- **Recommended**

**C. Fall back to generating a systemd unit**
- `mount.pgfs` emits a unit such as `/run/systemd/generator/mnt-pgfs.mount` and exits
- The substance is a resident service such as `pgfs-mountd.service`
- The design is overblown. It does not complete within fstab alone, so it is rejected

**Approach B** was taken.

#### Pseudocode for starting the child process

```csharp
// the parent (the one called from mount(8))
if (!args.Contains("--foreground-internal")) {
    var childArgs = args.Concat(new[] { "--foreground-internal" }).ToArray();
    var childProc = new Process {
        StartInfo = new ProcessStartInfo {
            FileName = Environment.ProcessPath!,  // the executable itself
            Arguments = string.Join(" ", childArgs.Select(QuoteArg)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
    };
    childProc.Start();

    // wait for the "MOUNTED" signal from the child (a 30-second timeout)
    var line = await childProc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (line == "MOUNTED") {
        return 0;  // the parent exits, returning success to mount(8)
    } else {
        // drain the child's stderr and then exit with a failure
        var err = await childProc.StandardError.ReadToEndAsync();
        Console.Error.Write(err);
        return childProc.ExitCode != 0 ? childProc.ExitCode : 1;
    }
}

// the child (with --foreground-internal)
// ... the usual mount handling ...
using var fuseMount = Pgfs.Fuse.Fuse.Mount(mountPoint, fileSystem, mountOptions);
Console.WriteLine("MOUNTED");  // tell the parent it succeeded
Console.Out.Flush();
// switch stdout/stderr to /dev/null or syslog and persist
await fuseMount.WaitForUnmountAsync();
```

### 4. Unmounting

What was mounted through fstab is removed with `umount /mnt/pgfs`. That is the symmetric command of `mount(8)`,
and without a `/sbin/umount.<type>` it unmounts through FUSE (`fusermount3 -u`).

There is no need to implement a separate `umount.pgfs` today. Once `fusermount3 -u` cuts the kernel-side mount,
the child process's `WaitForUnmountAsync` returns and the child exits naturally.

---

## Installing

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs

# if it should also be found under the name fuse.pgfs (for mount -t fuse.pgfs ... or writing fuse.pgfs in the fstab)
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
```

An installer (an `install.sh`, a Makefile, an RPM or a DEB) is for the future. By hand is fine for now.

Putting `user_allow_other` in `/etc/fuse.conf` lets an ordinary user mount with `allow_other`:

```text
# /etc/fuse.conf
user_allow_other
```

---

## Example fstab entries

### A system mount (root mounts it at boot)

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  pgfs  noatime,_netdev,allow_other,default_permissions,cache-max-entries=8192  0 0
```

- Putting `_netdev` in makes the mount run after the network comes up (mandatory when PG is remote)
- `0 0` is no dump and no fsck

### A user mount

```fstab
postgresql://pgfs@localhost/myfs  /home/me/pgfs  pgfs  user,noauto,allow_other,default_permissions  0 0
```

- `noauto` means it is not mounted at boot
- `user` lets an ordinary user (the one who wrote the fstab line) do `mount /home/me/pgfs`
- Using `allow_other` needs `user_allow_other` in `/etc/fuse.conf`. **Always pair `allow_other` with
  `default_permissions`** - pgfs does not decide access by itself (it leaves that to the kernel), so without it the
  mode is not enforced and every local user can read and write every file (forgetting it gives a warning at startup)

### The fuse.pgfs form

With the `/sbin/mount.fuse.pgfs` symlink in place:

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  fuse.pgfs  noatime,_netdev  0 0
```

`mount(8)` has a fallback that looks for `mount.fuse.<name>` when it sees a `fuse.*` type, so this works too.

---

## The test procedure (after implementing)

1. Build the binary with `dotnet publish -c Release`
2. `sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs`
3. `sudo mkdir -p /mnt/pgfs`
4. Confirm a manual mount:
   ```bash
   sudo mount -t pgfs "postgresql://pgfs@localhost/myfs" /mnt/pgfs \
       -o allow_other,default_permissions,cache-max-entries=4096
   ```
5. Confirm the entry with `mount | grep pgfs`
6. Confirm it can be read with `ls /mnt/pgfs`
7. Confirm unmounting with `sudo umount /mnt/pgfs`
8. Add the line to `/etc/fstab` and confirm the boot-equivalent behaviour with `sudo mount -a`
9. Reboot and confirm that the mount at boot works - **done (see below)**

### The real-hardware verification of mounting at boot (done 2026-06-02)

An fstab line was added on the Linux client and it was **rebooted**, after which `/mnt/pgfs` was mounted
automatically with no extra command:

```text
$ findmnt | grep pgfs
└─/mnt/pgfs   /dev/fuse   fuse   rw,nosuid,nodev,relatime,user_id=0,group_id=0,default_permissions,allow_other

$ ls -al /mnt/pgfs/
total 4
drwxr-xr-x 1 root root    0 May 28 18:22 .
drwxr-xr-x 5 root root 4096 Jun  2 20:23 ..
drwxrwxr-x 1 root root    0 Jun  3  2026 aaaa
drwxrwxr-x 1 user user    0 Jun  3  2026 bbbb
```

- `default_permissions,allow_other` appear in `findmnt` = the `-o` options of the fstab's fourth column were
  expanded correctly at boot too
- An ordinary user can read the entries with `ls -al` = `allow_other` plus `user_allow_other` in
  `/etc/fuse.conf` are taking effect
- The child-process separation (approach B) returns success to `mount(8)` even in the systemd boot sequence

### A smoke test through a direct invocation

The flow was verified by hitting the binary directly before installing `/sbin/mount.pgfs`:

```bash
# positionals plus -o, no -f -> the default child-process separation mode
bin/Publish/mount.pgfs \
    "Host=pgsql_server;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer" \
    /mnt/pgfs \
    -o cache-max-entries=2048,allow_other,_netdev,noatime \
    --log-level info
# -> the parent exits 0 in 1.4 seconds (after receiving the MOUNTED signal from the child)
mountpoint -q /mnt/pgfs   # -> 0 (mounted)
ps -ef | grep "Publish/mount.pgfs"     # -> the child is alive with --foreground-internal
ls /mnt/pgfs              # -> the directory contents are visible
fusermount3 -u /mnt/pgfs  # -> unmounted (the child exits naturally)
```

`-o allow_other` / `-o attr_timeout=N` and so on go to libfuse through `Pgfs.Fuse`'s `MountOptions.Options`.
The fstab and kernel-side hints such as `_netdev` and `noatime` are ignored silently.
An unknown `-o` key warns on stderr.

### A smoke test through `mount(8)` (with sudo)

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs

# the fstab forms (either works)
sudo mount -t pgfs       -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs
sudo mount -t fuse.pgfs  -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs

mountpoint -q /mnt/pgfs   # -> 0 (mounted)
ls -al /mnt/pgfs           # -> readable by an ordinary user too (`-o allow_other`)
# Note: the above is the record from real hardware as it was. **When using it as an example, add `default_permissions`**
# (without it the mode is not enforced).
# Note: the code of the time also handed the URL form straight to Npgsql, so **the URL form cannot have worked** (found in
# a later investigation and fixed so that the URL form is interpreted; why the record and the actual run disagree is unconfirmed).
sudo umount /mnt/pgfs      # -> unmounted
```

A note: at first `mount -t pgfs` failed with `the FUSE dependency was not found`. The cause was that the
environment `mount(8)` gives the helper is stripped, PATH included (measured: only the 11 of LANG, LOGNAME, PWD,
SHLVL, SUDO_*, TERM, USER and _, with no PATH). `Pgfs.Fuse`'s `HasFusermount` looks for `fusermount3` on
`$PATH`, so it returned false and `CheckDependencies` failed. It was resolved by having the head of
`Main` in `Mount/Program.cs` fill in `/usr/local/sbin:...:/bin` when PATH is empty.

## The libfuse binding (in house, derived from the old Tmds.Fuse fork)

In v0.2.0 the P/Invoke binding to libfuse was **brought in house into `Pgfs.Fuse`
([src/fuse/](../../src/fuse/))**. Previously, rather than the upstream `tmds/Tmds.Fuse 0.1.0-190711-50`
(unmaintained since 2019, whose `MountOptions` had only `SingleThread`), the active fork
[`securefolderfs-community/Tmds.Fuse`](https://github.com/securefolderfs-community/Tmds.Fuse) was forked again
into `ebe-rest/Tmds.Fuse` and referenced as the `vendor/Tmds.Fuse` submodule, but **the submodule was retired**
and the whole binding (`LibFuse.cs` / `FuseMount.cs` / `IFuseFileSystem.cs` and so on) was ported into
`src/fuse/src/` preserving the behaviour. The credits are in
[src/fuse/NOTICES.md](../../src/fuse/NOTICES.md), and [fuse-binding.md](fuse-binding.md) is the source of truth
for the overall picture of the ops and the symbols and for the known hacks.

That resolved the following all at once:

| The problem | How it was resolved |
|---|---|
| A root mount plus non-root access with `-o allow_other` | Propagated to libfuse through the fork's `MountOptions.Options` |
| Passing `attr_timeout=0` to disable the kernel attribute cache | The same (Mount.Program adds a default `attr_timeout=0`) |
| `st_ino` agreeing between hardlinks through `use_ino` | A downstream patch sets `fuse_config.use_ino=1` in init |

Confirmed on real hardware: after `sudo mount -t pgfs ...`, `ls -la /mnt/pgfs` from an ordinary user exits 0 and
`stat -c '%i' file3` returns `9223372036854775813` (= `0x8000_0000_0000_0005`, data_id 5 plus the high bit).

### Maintaining the binding

The binding in `src/fuse/src/` is edited and built as ordinary C# source (it is not a submodule). When libfuse's
layout changes, revisit `FuseMount.cs`'s `Init` (`fuse_config.use_ino`) and the op table of `FuseOperations`.
For the details see [fuse-binding.md](fuse-binding.md).

> **No submodule is needed** (since v0.2.0): neither a `git submodule` operation nor a
> `--recurse-submodules` clone is required. The libfuse binding is in the repository at `src/fuse/`.
> `libfuse3.so.3` itself is not bundled and is `dlopen`ed at runtime (the user provides it through `fuse3` or
> the like).

## The known constraints

### Mounting at boot (an `/etc/fstab` line plus a reboot) - verified (2026-06-02)

It was confirmed on real hardware that merely rebooting the Linux client mounts `/mnt/pgfs` automatically from
the fstab line (see
[the real-hardware verification of mounting at boot](#the-real-hardware-verification-of-mounting-at-boot-done-2026-06-02)).
With that, the core of the fstab support plus its verification are all closed.
