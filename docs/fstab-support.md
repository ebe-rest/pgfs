# /etc/fstab support (Linux)

How `mount.pgfs` integrates with `/etc/fstab` and `mount(8)`. The binary name `mount.pgfs` already matches the `/sbin/mount.<type>` convention that Linux's `mount(8)` looks for, so the only things that matter are **accepting the helper calling convention** and **daemonizing**.

A Japanese translation is available in [fstab-support.ja.md](fstab-support.ja.md).

`-o allow_other` and the other FUSE options are passed to libfuse via the `MountOptions.Options` of the active fork [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) (`securefolderfs-community/Tmds.Fuse`), which also sets `use_ino` in the init callback through a downstream patch.

---

## Motivation

Beyond the manual `mount.pgfs -c <conn> -m <mountpoint>` invocation, you can write a line in `/etc/fstab` and bring the filesystem up at boot, or with the short `mount /mnt/pgfs`. As a side effect this also enables `systemd` `*.mount` units and autofs, since both call `/sbin/mount.<type>` internally.

---

## The `mount(8)` helper calling convention

When processing `mount -t pgfs <source> <target> -o <opts>` or an fstab line, `mount(8)` invokes the helper like this:

```text
/sbin/mount.pgfs <source> <target> [-i] [-f] [-n] [-s] [-v] [-N <ns>] [-t <type>] [-o <opts>]
```

See [The mount(8) <-> mount.pgfs argument mapping](#the-mount8---mountpgfs-argument-mapping) for what each flag means and how mount.pgfs treats it.

The helper is expected to **exit once the mount completes** (so mtab can be updated). If it sits in the foreground, the `mount` command never returns.

---

## Argument flow when mounted via `mount(8)`

Being able to write an fstab line and bring it up with just the **mount target** (`sudo mount /mnt/pgfs`) works because `mount(8)` expands it in between. `mount.pgfs` itself does not read fstab (the standard FUSE-helper division of responsibility).

### Example fstab entry

```text
postgresql://pgfs@pgsql_server/pgfs   /mnt/pgfs   pgfs   _netdev,allow_other,cache-max-entries=4096   0 0
```

| Column | Meaning | Value |
|---|---|---|
| 1 | source (fs spec) | `postgresql://pgfs@pgsql_server/pgfs` |
| 2 | target (mount point) | `/mnt/pgfs` |
| 3 | type | `pgfs` |
| 4 | options | `_netdev,allow_other,cache-max-entries=4096` |
| 5 | dump (unused) | 0 |
| 6 | pass (unused) | 0 |

### Execution flow

When the user types `sudo mount /mnt/pgfs`:

1. **`mount(8)`** interprets the argument `/mnt/pgfs` as "the mount point in fstab column 2".
2. It scans `/etc/fstab` for a line whose column 2 matches -> hits the entry above.
3. It reads column 1 (source) / column 3 (type) / column 4 (options).
4. Since the type is `pgfs`, it fork+execs `/sbin/mount.pgfs` as the helper.
5. **The argv `mount.pgfs` receives is fully expanded**:
   ```text
   /sbin/mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs -o _netdev,allow_other,cache-max-entries=4096
   ```
   (along with mount(8)'s `-n` `-v` `-f` etc. if they were specified).

Inside `mount.pgfs`, the positional[0] / positional[1] / `-o` parser quietly route the values. The `-o` contents are expanded by `ParseDashOOptions` (`_netdev` is ignored, `allow_other` is forwarded to libfuse via `FuseFlags`, `cache-max-entries=4096` overrides `mount.cache_max_entries`).

At this point [`IsMountHelperContext`](../src/lib/src/Config/ConfigLoader.cs) returns **true** by confirming the parent's comm is `mount` and that positionals exist, enabling `MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` (i.e. the helper-context-only silent swallowing).

### Invoking `mount.pgfs` directly, without `mount(8)`

Typing `sudo mount.pgfs /mnt/pgfs` **does not work**, because `mount.pgfs` alone does not read fstab:

- positional[0] = `/mnt/pgfs` -> does not start with `postgresql:`, so it is **interpreted as a settings file path** ([§Positional arguments](#1-accepting-positional-arguments-source--target)).
- `LoadFromFile` tries to open `/mnt/pgfs` as TOML -> fails because it is a directory, or is silently ignored as non-existent.
- It then tries to connect to PG with `database.connection` unset -> hits the default (localhost:5432) and fails.

To bring it up with just the mount target, **always go through `mount(8)`** (`sudo mount /mnt/pgfs`, or `sudo mount -a` for the whole fstab).

---

## The mount(8) <-> mount.pgfs argument mapping

`mount.pgfs` accepts both (a) being called as a `mount(8)` helper and (b) being run directly by a user. Because the same short form can mean different things, at startup [`IsMountHelperContext`](../src/lib/src/Config/ConfigLoader.cs) decides the context, and only in helper context does it silently skip mount(8)'s internal flags.

### Helper-context detection

It is helper context only if **both** are true:

1. Some `arg` does not start with `-` (i.e. a positional argument exists).
2. The parent process's comm (Linux: `/proc/<ppid>/comm`) is `"mount"`.

If either is false it is treated as direct invocation and `MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` are not applied (i.e. `-f` / `-s` act as their genuine short forms). On non-Linux, the parent-process lookup comes up empty, so it is always treated as direct invocation (so the same code runs safely even in `pgfs.assign` (Windows), which has no helper-context mechanism).

### How the arguments mount(8) passes to the helper are handled

| `mount(8)` argument | `mount(8)` meaning | Helper context | Direct invocation | Notes |
|---|---|---|---|---|
| **positional[0]** (= fstab column 1 `<source>`) | the FS "data source" | starts with `postgresql:` -> `database.connection`; otherwise -> `setting.file` (TOML path) | same | dual meaning ([§Positional arguments](#1-accepting-positional-arguments-source--target)) |
| **positional[1]** (= fstab column 2 `<target>`) | mount point | routed to `mount.mount_point` (only if unset) | same | |
| `-o <opts>` (= fstab column 4) | comma-separated options | [`ParseDashOOptions`](../src/lib/src/Config/ConfigLoader.cs) splits on commas -> matches against `Field.CliOptions` / `Field.EffectiveDashOName`; `allow_other` etc. -> `MountConfig.FuseFlags`; `_netdev` / `noauto` / `noatime` etc. -> ignored | same | details [§-o parser](#2--o-keyvalflag-parser) |
| `-i, --internal-only` | do not call the helper | silently skipped | unknown-option warning (no mount.pgfs-specific flag assigned) | mount(8) does not actually pass this to the helper, so this is a safety net |
| `-f, --fake` | dry-run | silently skipped -> actually still mounts (TODO: honor fake) | valid as the short form of `setting.file` | |
| `-n, --no-mtab` | do not update `/etc/mtab` | silently skipped | unknown-option warning | under FUSE, mtab is written by fusermount3 |
| `-s, --sloppy` | ignore unknown options | silently skipped | valid as the short form of `database.schema` | the direct side already warns + continues on unknown options, so it is effectively sloppy by default |
| `-v, --verbose` | verbose logging | silently skipped | unknown-option warning | TODO: promote `-v` to `--log-level debug`-equivalent |
| `-N <namespace>` | a different mount namespace | silently skipped (value too) | unknown-option warning + the value may become a positional | namespace switching is on the parent side |
| `-t <type>` | FS type | silently skipped (value too) | same | already determined by the helper name |

### mount.pgfs-specific flags (= not passed via mount(8))

[settings-matrix.md](settings-matrix.md) is exhaustive; only the main ones here:

| `mount.pgfs` flag | Maps to | Notes |
|---|---|---|
| `-c <conn>` / `--connection` / `--connection-string` | `database.connection` | use this to pass kv form (`Host=...;Port=...`) |
| `-m <path>` / `--mount-point` | `mount.mount_point` | synonymous with positional[1] |
| `-f <path>` / `--setting-file` / `--setting` | `setting.file` | TOML path. **`-f` is valid only on direct invocation** (in helper context it is swallowed as `--fake`). positional[0] (non-`postgresql:`) means the same |
| `-s <name>` / `--schema` / `--schema-name` | `database.schema` | **`-s` is valid only on direct invocation** (in helper context it is `--sloppy`). `--schema` is always OK |
| `-x` / `--prefix` | `database.prefix` | table name prefix |
| `--cache-max-entries <N>` | `mount.cache_max_entries` | |
| `--fallback-uname <name>` / `--fallback-gname <name>` | `mount.fallback_uname` / `_gname` | the escape when uname cannot be resolved by the OS |
| `--log-level <level>` / `--log-output <spec>` | `logging.level` / `logging.output` | |
| `--foreground` | `mount.foreground` | suppress daemonization. It [deliberately has no short form](#short-form-collisions) because `-f` collides with setting.file |
| `--retry-max-attempts` / `--retry-initial-delay-ms` / `--retry-max-delay-ms` | `database.retry_*` | transient-error retry on connection open |
| `-?` / `-h` / `--help` | `help` | show usage -> exit immediately |

### Short-form collisions

Where a `mount(8)`-derived short form has the same spelling as a `mount.pgfs`-specific flag:

| Short form | `mount(8)` meaning | `mount.pgfs` meaning | Helper-context behavior | Direct-invocation behavior |
|---|---|---|---|---|
| `-f` | `--fake` (dry-run) | short form of `setting.file` | silently swallowed (mount(8) wins) | valid as `--setting-file` |
| `-s` | `--sloppy` | short form of `database.schema` | silently swallowed (mount(8) wins) | valid as `--schema` |
| `-n` `-v` `-i` `-N` `-t` | mount(8)'s various internal flags | (unassigned) | silently swallowed | unknown-option warning |

`mount.foreground` is received only as `--foreground` (no short form), because a `-f` short form would overlap with setting.file (and which wins would depend on Field declaration order). `-f` is reserved as the **setting.file-only** short form.

### `ConfigLoader.ParseCli` processing order

It scans the arguments one at a time and commits at the first matching branch:

1. **only in helper context** `MountHelperFlagsNoValue.Contains(arg)` -> silent continue (`-i` `-f` `-n` `-s` `-v`).
2. **only in helper context** `MountHelperFlagsWithValue.Contains(arg)` -> skip the next token too, continue (`-N` `-t`).
3. `arg == "-o"` -> delegate the next token to `ParseDashOOptions`.
4. `all.FirstOrDefault(s => s.Options.Contains(arg, ...))` matches a `mount.pgfs`-specific flag.
5. not found in 4 and does not start with `-` -> consume as a positional (positional[0] / [1]).
6. not found in 4 and starts with `-` -> unknown-option warning.

The helper-context decision is precomputed once at the start of the `ConfigLoader` constructor (it inspects all of `args`, so it decides positional presence with `args.Any(a => !a.StartsWith('-'))` and then reads the parent process name).

---

## Implementation

### 1. Accepting positional arguments (source / target)

[`ConfigLoader.ParseCli`](../src/lib/src/Config/ConfigLoader.cs) picks up to two positionals:

| Position | Role | Maps to |
|---|---|---|
| 1st | source | starts with `postgresql:` -> `Database.Connection.Value`; otherwise -> `Setting.File.Value` (= settings file path). Either applies only when not already loaded; explicit `-c` / `-f` / `-o connection=` wins |
| 2nd | target (mount point) | `Mount.MountPoint.Value` (only if empty; explicit `-m` or `-o mount_point=` wins) |

source-detection heuristic:
- `postgresql://user@host:port/dbname` -> treated as a URL-form connection string, routed to `Connection`.
- `/etc/pgfs.toml` / `pgfs.toml` etc. -> treated as a settings file path, routed to `Setting.File` (then `LoadFromFile` reads the TOML).
- to pass a connection string in kv form (`Host=...;Port=...`), specify it explicitly with `-c`.

This makes three invocation styles equivalent:

```bash
# (a) as before: a connection URL as source
mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs

# (b) a settings file as source (with connection / mount_point in the TOML)
mount.pgfs /etc/pgfs.toml /mnt/pgfs
mount.pgfs /etc/pgfs.toml       # mount_point also from the TOML

# (c) explicit -f / -m (either)
mount.pgfs -f /etc/pgfs.toml -m /mnt/pgfs
```

#### Connection column format

The `postgresql://` URL form is standard. Note that a comma in the host name breaks the parser; if it becomes indistinguishable from the `,` in fstab column 4, escape it via `-o connection=...`.

### 2. `-o key=val,flag,...` parser

```text
Take the token after "-o" in args, split on commas -> split each element on "=" -> key/value.
Map the key to a Setting whose s.Options matches, and feed in the value.

e.g. -o noatime,_netdev,allow_other,connection=postgres://...,cache-max-entries=4096

  noatime              -> kernel-side flag. Not passed to FUSE, ignored (or passed to mount syscall flags).
  _netdev              -> an fstab hint (wait for the network at boot). Safe to ignore.
  allow_other          -> a FUSE flag. Forwarded to Tmds.Fuse's MountOptions.
  connection=...       -> written back into Database.Connection.Value.
  cache-max-entries=4096 -> written back into Mount.CacheMaxEntries.Value.
```

Mapping table:

| `-o` key | Maps to / treatment |
|---|---|
| `connection=...` | `Database.Connection.Value` |
| `super_connection=...` | `Database.SuperConnection.Value` (whether mount uses it is open; basically mkfs-only) |
| `mount_point=...` | `Mount.MountPoint.Value` |
| `cache-max-entries=...` | `Mount.CacheMaxEntries.Value` |
| `log-level=...` | `Logging.Level.Value` |
| `setting-file=...` | `Setting.File.Value` |
| `allow_other` | FUSE `MountOptions` (Tmds.Fuse side) |
| `default_permissions` | FUSE `MountOptions` |
| `ro` / `rw` | FUSE `MountOptions` |
| `nosuid` / `nodev` / `noexec` | kernel-side flags (currently safe to ignore) |
| `_netdev` / `noauto` / `user` / `users` | fstab-only flags, ignored in the helper |
| `noatime` / `relatime` / `atime` | atime policy (to be implemented if an `atime` inode column is ever added) |

An unknown `-o` key is pushed to `Warnings` by `ConfigLoader.ParseDashOOptions` and surfaced to `Console.Error` (not silently dropped).

### 3. Daemonization

`mount(8)` waits for the helper to exit, but [`Program.RunFuseMountAsync`](../src/mount/src/Program.cs) blocks on `await fuseMount.WaitForUnmountAsync()`, so via fstab the `mount` command would never return. mount.pgfs therefore detaches a child process (the sshfs approach):

- The parent (called from mount(8)) starts a child with `Process.Start(self, original args + "--foreground-internal")`.
- The parent reads a little of the child's stdout and exits once it sees the "mount succeeded" signal (the `PGFS_MOUNTED_OK` line).
- The child mounts as usual and stays alive on `WaitForUnmountAsync`.

This is self-contained in .NET and keeps Windows compatibility (Windows uses `Assign`, so it does not matter there, but the same code path stays safe). `--foreground` skips the child-detach and runs the FUSE loop in the foreground — recommended for tests / manual use.

A systemd-generator approach (emitting a `*.mount` unit and exiting, backed by a resident service) was considered but rejected as too heavyweight and not self-contained within fstab.

### 4. Unmount

Something mounted via fstab is removed with `umount /mnt/pgfs`. This is mount(8)'s symmetric command; without a `/sbin/umount.<type>`, it unmounts via FUSE (`fusermount3 -u`). No separate `umount.pgfs` is needed: once `fusermount3 -u` severs the kernel-side mount, the child's `WaitForUnmountAsync` returns and the child exits naturally.

---

## Installation

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs

# To also resolve via the fuse.pgfs name (mount -t fuse.pgfs ... or `fuse.pgfs` in fstab)
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
```

A proper installer (`install.sh`, Makefile, RPM, DEB) is future work; manual install is fine for now.

Adding `user_allow_other` to `/etc/fuse.conf` lets a regular user mount with `allow_other`:

```text
# /etc/fuse.conf
user_allow_other
```

---

## fstab entry examples

### System mount (root mounts at boot)

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  pgfs  noatime,_netdev,allow_other,cache-max-entries=8192  0 0
```

- `_netdev` makes the mount run after the network comes up (required for a remote PG).
- `0 0` means no dump / no fsck.

### User mount

```fstab
postgresql://pgfs@localhost/myfs  /home/me/pgfs  pgfs  user,noauto,allow_other  0 0
```

- `noauto` so it does not mount at boot.
- `user` lets a regular user (the one who wrote the fstab line) run `mount /home/me/pgfs`.
- `allow_other` requires `user_allow_other` in `/etc/fuse.conf`.

### fuse.pgfs form

If you placed the `/sbin/mount.fuse.pgfs` symlink:

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  fuse.pgfs  noatime,_netdev  0 0
```

`mount(8)` has a fallback of looking for `mount.fuse.<name>` when it sees a `fuse.*` type, so this works too.

---

## Verification

```bash
# Build the binary
dotnet publish -c Release
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
sudo mkdir -p /mnt/pgfs

# Mount via mount(8) (either type works)
sudo mount -t pgfs       -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs
sudo mount -t fuse.pgfs  -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs

mountpoint -q /mnt/pgfs   # -> 0 (mounted)
ls -al /mnt/pgfs          # -> readable even as a regular user (`-o allow_other`)
sudo umount /mnt/pgfs     # -> unmount
```

Direct invocation (before installing `/sbin/mount.pgfs`) also exercises the flow:

```bash
# positional args + -o, no -f -> default child-detach mode
bin/Publish/mount.pgfs \
    "Host=pgsql_server;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer" \
    /mnt/pgfs \
    -o cache-max-entries=2048,allow_other,_netdev,noatime \
    --log-level info
# -> the parent exits 0 shortly after receiving the MOUNTED signal from the child
mountpoint -q /mnt/pgfs                # -> 0 (mounted)
ps -ef | grep "Publish/mount.pgfs"     # -> the child is alive with --foreground-internal
ls /mnt/pgfs                           # -> directory contents are visible
fusermount3 -u /mnt/pgfs               # -> unmount (the child exits naturally)
```

`-o allow_other` / `-o attr_timeout=N` etc. reach libfuse via the `MountOptions.Options` of [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/). The `_netdev` / `noatime` fstab/kernel-side hints are silently ignored. An unknown `-o` key warns to stderr.

> A `mount(8)`-launched helper has its env stripped down to almost nothing including PATH (in practice only ~11 vars: LANG, LOGNAME, PWD, SHLVL, SUDO_*, TERM, USER, _). Tmds.Fuse's `HasFusermount` searches `$PATH` for `fusermount3`, so without PATH `CheckDependencies` returns false. `Mount/Program.cs`'s `Main` supplies a minimal PATH at the start when it is empty.

---

## The Tmds.Fuse fork and downstream patch

The original `tmds/Tmds.Fuse 0.1.0-190711-50` has not been updated since 2019 and its `MountOptions` exposes only `SingleThread`, so it cannot pass `-o allow_other` etc. to libfuse. The active fork [`securefolderfs-community/Tmds.Fuse`](https://github.com/securefolderfs-community/Tmds.Fuse) (net10.0 target) is itself forked into [`ebe-rest/Tmds.Fuse`](https://github.com/ebe-rest/Tmds.Fuse) and vendored as a submodule under [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/). The fork adds `MountOptions.Options` (an arbitrary `-o` string), but on libfuse 3 passing `-o use_ino` through `fuse_new` is rejected as unknown (libfuse 3 sets `fuse_config.use_ino` in the init callback instead), so a downstream patch in [vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs)'s `Init(IntPtr conn, IntPtr cfg)` callback writes `*(int*)(cfg + 64) = 1` (offset 64 is the position of `fuse_config.use_ino` on libfuse 3.x, stable since 3.0).

This resolves several things at once:

| Need | How it is resolved |
|---|---|
| `-o allow_other` for a root mount + non-root access | propagated to libfuse via the fork's `MountOptions.Options` |
| pass `attr_timeout=0` to disable the kernel attr cache | same (Mount.Program adds default `attr_timeout=0`) |
| `use_ino` for matching `st_ino` across hard links | the downstream patch sets `fuse_config.use_ino=1` in init |

### Submodule setup / tracking

```bash
# Fresh clone (the downstream patch comes automatically)
git clone --recurse-submodules https://github.com/ebe-rest/pgfs.git
# Initialize an existing clone
git submodule update --init --recursive
```

To track `ebe-rest/Tmds.Fuse` against upstream (`securefolderfs-community/Tmds.Fuse`):

```bash
cd vendor/Tmds.Fuse
# First time only: add the upstream remote (a fresh clone has origin = ebe-rest only)
git remote add upstream https://github.com/securefolderfs-community/Tmds.Fuse.git
# Fetch upstream and rebase the downstream patch on top
git fetch upstream
git rebase upstream/master
# Reflect into the fork
git push origin master --force-with-lease
# In the parent repo, record the new submodule SHA
cd ../..
git add vendor/Tmds.Fuse
git commit -m "Update Tmds.Fuse fork from upstream"
```

If a conflict arises around `FuseMount.cs`'s `Init` during the rebase, resolve it keeping the `fuse_config.use_ino=1` write. Confirm the Linux e2e still passes.

If upstream accepts a PR, the downstream patch can be dropped.

---

## Known limitations

### Boot-time mount (an `/etc/fstab` line + reboot) is unverified

Manual `sudo mount -t pgfs ...` works, but the path where the fstab line is processed by `mount -a` at system startup has not been exercised by hand. Verifying the `_netdev` behavior of waiting for a remote PG is also left to on-hardware verification.

---

## Related

- The Mount spec -> [Mount.md](Mount.md)
- The configuration model in general -> [src/lib/src/Config/](../src/lib/src/Config/) and [architecture.md](architecture.md)
- The current behavior of ConfigLoader / Schema -> [src/lib/src/Config/ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs) / [Schema.cs](../src/lib/src/Config/Schema.cs)
