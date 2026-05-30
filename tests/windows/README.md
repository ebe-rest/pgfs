# pgfs Windows e2e tests

> For the full list of tests, environment requirements, and the docker-integration analysis, see [docs/tests.md](../../docs/tests.md) (the hub). This README covers the operational details of the runners in this directory (`e2e.ps1` / `flow.ps1` / `run.cmd`).

End-to-end tests that exercise the implemented features ([docs/Assign.md](../../docs/Assign.md)) against a PGFS mounted with pgfs.assign (Windows / Dokan).

The Windows counterpart of the Linux suite ([tests/linux/](../linux/README.md)), with nearly the same structure. It adds Windows-specific operations (ReadOnly / Hidden / System / Archive attributes, volume info, wildcard search), and excludes operations that do not exist on Windows (POSIX symlink / hardlink / chmod / chown / arbitrary xattr APIs).

## Files

| File | Contents |
|---|---|
| [e2e.ps1](e2e.ps1) | test body (PowerShell) |
| [run.cmd](run.cmd) | run the tests **only** (assumes already mounted) |
| [flow.ps1](flow.ps1) | **full flow**: (optional build) -> mount -> test -> unmount |
| [flow.cmd](flow.cmd) | cmd wrapper for `flow.ps1` |

## Scenario policy

- At the start, recursively delete then create `$MountRoot\test`
- All tests run only under `$MountRoot\test\`
- Each test uses a unique prefix (`t01_` / `t02_` ...) so they do not interfere
- At the end (whether passing or failing), recursively delete `$MountRoot\test`

## Coverage

Exercises the Dokan operations marked ✅ / ⚠️ in [docs/Assign.md](../../docs/Assign.md).

| Category | Tests |
|---|---|
| **Directory operations** | mkdir/rmdir, nested directories, 100-file directory, rmdir rejects non-empty |
| **Basic file operations** | new/del, small write/read, append, overwrite via FileMode.Create (O_TRUNC equivalent) |
| **Data I/O (bytea)** | 2 MiB round-trip (spanning chunks), truncate shrink/grow/zero |
| **Rename** | Move-Item, Move-Item into a subdirectory |
| **Attributes / time** | SetFileAttributes (ReadOnly / Hidden / System / Archive), xattr `user.win_attrs` round-trip, dotfile fallback, un-hide persistence, SetFileTime (LastWriteTime) |
| **Volume / pattern** | drive info via GetVolumeInformation, FindFilesWithPattern (`-Filter`) |
| **Concurrency** | parallel writes to different files, parallel reads from one file, parallel mkdir |

## Known assign-side issues

Issues found while building out the tests. The e2e suite works around them (calling the .NET API directly), but they are tracked separately for a fix:

| Issue | State | Note |
|---|---|---|
| `Copy-Item` (CopyFileEx) raises `IOException` on a 2 MiB file | ⚠️ worked around | The round-trip succeeds via a direct `[System.IO.File]::WriteAllBytes`. One of the auxiliary APIs CopyFileEx calls (GetFileSecurity / FindStreams / attribute queries) most likely returns an unexpected response through Dokan. `test_large_file_round_trip` passes via WriteAllBytes. Supporting the CopyFileEx path is a separate task |
| `dokanctl /u` fails with "Admin rights required" | OK (worked around) | By Dokan's design `dokanctl /u` requires administrator privileges. `flow.ps1` falls back to `Process.Kill()` (the unmount itself succeeds and the test results are unaffected) |

## Status

The suite has 24 tests and passes 24/24 against a single PostgreSQL and a single-node Citus.

## Not covered

The ❌ items in the TODO table of [docs/Assign.md](../../docs/Assign.md) are unimplemented and out of scope:

- GetFileSecurity / SetFileSecurity (NotImplemented -> kernel default ACL)
- Alternate Data Streams (`file.txt:stream`)
- xattr (DokanNet has no xattr API)
- symlink / hardlink (not supported by DokanNet's `IDokanOperations2`)
- POSIX-compatible range locks
- Junction (reparse points)

For the remaining list of unimplemented features, see [docs/next.md](../../docs/next.md).

## Usage

### A. Full flow (recommended)

`flow.cmd` or `flow.ps1` runs `mount -> test -> unmount` in one go.

```cmd
tests\windows\flow.cmd
```

From PowerShell:

```powershell
.\tests\windows\flow.ps1
```

Options (same for either invocation):

| Option | Behavior |
|---|---|
| `pattern` (positional) | test-name filter. e.g. `flow.cmd concurrent` runs only the concurrency tests |
| `-Build` | run `dotnet publish pgfs.sln -c Release` first (default: skip) |
| `-NoMount` | assume mounted, run tests only (no mount operation at all) |
| `-KeepMounted` | do not unmount after the tests (for investigation) |

> **Difference from the Linux version**: the Linux `flow.ps1` builds by default, but the Windows one skips the build by default. On Windows you build from the IDE / `dotnet build` on the development machine, so there is no need to rebuild every time. Pass `-Build` only when you want to build explicitly.

Combinable:

```cmd
tests\windows\flow.cmd -Build              REM publish first, then run
tests\windows\flow.cmd concurrent          REM only tests containing "concurrent"
tests\windows\flow.cmd -KeepMounted        REM keep the mount up after the tests
```

To change the mount point or settings file, call PowerShell directly:

```powershell
.\tests\windows\flow.ps1 -MountPoint R: -SettingFile C:\path\to\pgfs.toml
```

### B. Tests only (assumes already mounted)

When `assign.pgfs.exe` is running in another terminal:

```cmd
tests\windows\run.cmd
tests\windows\run.cmd concurrent
```

### C. Manual flow (each step individually)

```cmd
REM 1. build (optional -- skip if already built in the IDE)
dotnet publish pgfs.sln -c Release

REM 2. (another terminal) mount
bin\Publish\assign.pgfs.exe -f pgfs.toml -m P:

REM 3. test
tests\windows\run.cmd

REM 4. unmount
"C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe" /u P:
```

## Filtering tests

Passing a filter string as the first argument runs only tests whose name contains it:

```cmd
tests\windows\run.cmd concurrent     # 3 concurrency tests
tests\windows\run.cmd truncate       # 3 truncate tests
tests\windows\run.cmd rename         # 2 rename tests
```

## Environment variables / parameters

### run.cmd (tests only)

| Variable | Default |
|---|---|
| `MOUNT_ROOT` | `P:\` |

Example:

```cmd
set MOUNT_ROOT=R:\
tests\windows\run.cmd
```

### flow.ps1 (full flow)

Configurable via both CLI parameters and environment variables (precedence: **CLI arg > env var > default**). `MOUNT_ROOT` is shared with `run.cmd`.

| Parameter | Env var | Default | Purpose |
|---|---|---|---|
| `-MountPoint` | `MOUNT_ROOT` | `P:` | mount point passed to assign.pgfs (`P:` or `P:\` both accepted) |
| `-AssignBinary` | `ASSIGN_BINARY` | `bin\Publish\assign.pgfs.exe` | full path to assign.pgfs.exe |
| `-SettingFile` | `ASSIGN_SETTING_FILE` | `pgfs.toml` | full path to the settings file |
| `-Filter` (pos 0) | - | (none) | test-name filter |
| `-Build` | - | (off) | run `dotnet publish` first |
| `-NoMount` | - | (off) | assume mounted, tests only |
| `-KeepMounted` | - | (off) | do not unmount after the tests |

## Running directly from PowerShell

```powershell
.\tests\windows\e2e.ps1 -MountRoot P:\
.\tests\windows\e2e.ps1 -MountRoot P:\ -Filter concurrent
```

## Example output

```
=== pgfs.assign ===
  starting: ...\bin\Publish\assign.pgfs.exe -f ...\pgfs.toml -m P:
  mount pid=12345
  mounted at P:\

=== e2e tests ===
=== pgfs Windows e2e tests ===
Mount root: P:\
Test root:  P:\test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...

===========================================
Results: 24 passed, 0 failed, 0 skipped (out of 24)

=== unmount ===
  ...\dokanctl.exe /u P:
  unmounted
  mount log: tests\windows\mount.log (123 lines)

  ALL PASSED
```

## Logs

While `flow.ps1` runs, the stdout / stderr of `assign.pgfs.exe` is saved to (gitignore recommended):

- `tests\windows\mount.log` — stdout (trace SQL log etc.)
- `tests\windows\mount.err.log` — stderr

## Exit codes

| Code | Meaning |
|---|---|
| 0 | all tests passed |
| 1 | one or more failed |
| 2 | `MountRoot` does not exist (not mounted) |
| 3 | `TestRoot` could not be created (mount not writable) |
| 11 | `assign.pgfs.exe` not found (flow only) |
| 99 | exception before the tests ran (flow only) |

## Prerequisites

- **.NET 10 SDK** (build time only)
- **Dokan 2.x** driver (`DokanSetup_redist.exe` from the [Dokan releases](https://github.com/dokan-dev/dokany/releases))
  - flow.ps1 auto-detects `dokanctl.exe` under `C:\Program Files\Dokan\Dokan Library-*\`
  - if not found, it falls back to `Stop-Process` (the unmount may be unclean)
- the mount point (`P:` etc.) does not collide with an existing drive
- PGFS is initialized in PostgreSQL ([docs/Mkfs.md](../../docs/Mkfs.md))
- `pgfs.toml` holds the DB connection info
