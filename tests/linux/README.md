# pgfs Linux e2e tests

> For the full list of tests, environment requirements, and the docker-integration analysis, see [docs/tests.md](../../docs/tests.md) (the hub). This README covers the operational details of the runners in this directory (`e2e.sh` / `flow.ps1` / `run.cmd`).

End-to-end tests that exercise the implemented features ([docs/Mount.md](../../docs/Mount.md)) against a PGFS mounted with mount.pgfs (Linux).

## Files

| File | Contents |
|---|---|
| [e2e.sh](e2e.sh) | bash test body (runs on Linux) |
| [run.cmd](run.cmd) | run the tests **only** (assumes already mounted) |
| [flow.ps1](flow.ps1) | **full flow**: rsync -> publish -> mount -> test -> unmount (PowerShell) |
| [flow.cmd](flow.cmd) | cmd wrapper for `flow.ps1` |

## Scenario policy

- At the start, `rm -rf` then `mkdir` `$MOUNT_ROOT/test`
- All tests run only under `$MOUNT_ROOT/test/`
- Each test uses a unique prefix (`t01_` / `t02_` ...) so they do not interfere
- At the end (whether passing or failing), `rm -rf` `$MOUNT_ROOT/test`

## Coverage

Exercises every FUSE operation marked ✅ in [docs/Mount.md](../../docs/Mount.md).

| Category | Tests |
|---|---|
| **Directory operations** | mkdir/rmdir, nested directories, 100-file directory, rmdir rejects non-empty |
| **Basic file operations** | touch/unlink, small write/read, append (O_APPEND), overwrite with O_TRUNC |
| **Data I/O (bytea)** | 2 MiB round-trip (spanning chunks), truncate shrink/grow/zero |
| **Rename** | plain mv, mv into a subdirectory |
| **Permissions** | chmod (file/directory), chown to self |
| **Symbolic links** | relative path, dangling, absolute path |
| **Hard links** | basic, survival after one is removed, across a subdirectory |
| **xattr** | set/get, list, remove, overwrite |
| **Metadata** | StatFS (df), utime (touch -d / UTIME_NOW) |
| **Concurrency** | parallel writes to different files, parallel reads from one file, parallel mkdir |
| **Name resolution fallback** | a direct DB INSERT creates an inode with a non-existent uname/gname and `stat` is expected to return `nobody`/`nogroup` (via psql over `ssh pgsql_server`; SKIP if unreachable) |

## Usage

### A. Full flow (recommended)

`flow.cmd` or `flow.ps1` runs `rsync -> publish -> mount -> test -> unmount` in one go.

```cmd
tests\linux\flow.cmd
```

From PowerShell:

```powershell
.\tests\linux\flow.ps1
```

Options (same for either invocation):

| Option | Behavior |
|---|---|
| `xattr` (positional) | test-name filter. e.g. `flow.cmd xattr` runs only the 4 xattr tests |
| `-NoSync` | skip rsync (already synced) |
| `-NoBuild` | skip `dotnet publish` |
| `-NoMount` | assume mounted, run tests only |
| `-KeepMounted` | do not unmount after the tests (for investigation) |

Combinable:

```cmd
tests\linux\flow.cmd -NoBuild              REM rsync + mount + test + unmount
tests\linux\flow.cmd -NoSync -NoBuild      REM mount + test + unmount
tests\linux\flow.cmd hardlink -KeepMounted REM keep the mount up after the hardlink tests
```

To change the target host or paths, use environment variables or PowerShell parameters:

```powershell
.\tests\linux\flow.ps1 -Remote other-host -MountPoint /tmp/m
```

### B. Tests only (assumes already mounted)

When mount.pgfs is already running in another terminal:

```cmd
tests\linux\run.cmd
tests\linux\run.cmd xattr
```

### C. Manual flow (each step individually)

The variables below show the meaning of each setting. Set `$REMOTE` (host) / `$REMOTE_REPO` (remote checkout dir) / `$REMOTE_DOTNET` / `$REMOTE_SETTING_FILE` / `$MOUNT_POINT` for your environment.

```cmd
REM 1. sync
bash -c "rsync -avz --delete ./ $REMOTE:$REMOTE_REPO/"

REM 2. build
bash -c "ssh $REMOTE $REMOTE_DOTNET publish $REMOTE_REPO/pgfs.sln"

REM 3. (another terminal) mount
bash -c "ssh $REMOTE $REMOTE_REPO/bin/Publish/mount.pgfs --setting-file $REMOTE_SETTING_FILE --mount-point $MOUNT_POINT"

REM 4. test
tests\linux\run.cmd

REM 5. unmount
bash -c "ssh $REMOTE fusermount3 -u $MOUNT_POINT"
```

## Filtering tests

Passing a filter string as the first argument runs only tests whose name contains it:

```cmd
tests\linux\run.cmd xattr          # 4 xattr tests
tests\linux\run.cmd hardlink       # 3 hard-link tests
tests\linux\run.cmd concurrent     # 3 concurrency tests
```

## Environment variables / parameters

### run.cmd (tests only)

| Variable | Default |
|---|---|
| `REMOTE` | `linux_client` |
| `REMOTE_REPO` | `~/project/pgfs_cs` (host-specific) |
| `MOUNT_POINT` | `~/mnt/pgfs` (host-specific) |

Example:

```cmd
set REMOTE=other-host
set MOUNT_POINT=/tmp/pgfs_mount
tests\linux\run.cmd
```

### flow.ps1 (full flow)

Configurable via both CLI parameters and environment variables (precedence: **CLI arg > env var > default**). Variables shared with `run.cmd` use the same names.

| Parameter | Env var | Default | Purpose |
|---|---|---|---|
| `-Remote` | `REMOTE` | `linux_client` | SSH target |
| `-RemoteRepo` | `REMOTE_REPO` | `~/project/pgfs_cs` | remote repository path |
| `-MountBinary` | `REMOTE_MOUNT_BINARY` | `${RemoteRepo}/bin/Publish/mount.pgfs` | mount.pgfs binary path |
| `-MountPoint` | `MOUNT_POINT` | `~/mnt/pgfs` | mount point |
| `-SettingFile` | `REMOTE_SETTING_FILE` | `~/pgfs.toml` | settings file path |
| `-DotnetPath` | `REMOTE_DOTNET` | `~/dotnet/10.0.300/dotnet` | dotnet binary |
| `-Filter` (pos 0) | - | (none) | test-name filter |
| `-NoSync` | - | - | skip rsync |
| `-NoBuild` | - | - | skip publish |
| `-NoMount` | - | - | assume mounted, tests only |
| `-KeepMounted` | - | - | do not unmount after the tests |

Example (point at another host via env vars):

```powershell
$env:REMOTE = "other-host"; $env:MOUNT_POINT = "/tmp/pgfs_mount"
.\tests\linux\flow.ps1 -NoBuild
```

## Running directly on Linux

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

### Environment variables

| Variable | Purpose |
|---|---|
| `TEST_FILTER` | run only tests whose name partially matches |
| `PGFS_TEST_PG_EXEC` | override the full psql invocation used by `test_fallback_uname_gname` (default: invoke the source-built psql on pgsql_server over ssh). To point at docker Citus, set `docker exec -i <container> psql ...`. Used by [tests/citus/race_multinode.sh](../citus/race_multinode.sh) |

## Example output

```
=== mount.pgfs ===
  mounted at /mnt/pgfs

=== e2e tests ===
=== pgfs Linux e2e tests ===
Mount root: /mnt/pgfs
Test root:  /mnt/pgfs/test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...
PASS: test_concurrent_mkdir_diff_dirs
PASS: test_fallback_uname_gname

===========================================
Results: 34 passed, 0 failed, 0 skipped (out of 34)

=== unmount ===
  unmounted
  mount log: tests\linux\mount.log (6 lines)

  ALL PASSED
```

The suite has 34 tests and passes 34/34 against a single PostgreSQL, a single-node Citus, and a multi-node Citus (on docker). Multi-node Citus verification runs through [tests/citus/race_multinode.sh](../citus/README.md).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | all tests passed |
| 1 | one or more failed |
| 2 | `MOUNT_ROOT` does not exist (not mounted) |
| 3 | `TEST_ROOT` could not be created (mount not writable) |

## Required packages

The xattr tests use the `attr` package (`getfattr` / `setfattr`). If it is not installed, the xattr tests are skipped with a `SKIP` notice:

```bash
sudo apt install attr   # Debian/Ubuntu
sudo dnf install attr   # Fedora/RHEL
```

## Not covered

The ❌ items in the TODO table of [docs/Mount.md](../../docs/Mount.md) are unimplemented and out of scope:

- macOS verification (depends on Tmds.Fuse macOS support)
- Access checks (the `Access` operation)
- Mount options `-o` (full support)

For the remaining list of unimplemented features, see [docs/next.md](../../docs/next.md).
