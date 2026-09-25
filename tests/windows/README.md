# pgfs Windows e2e tests

> **Route**: [docs/README.md](../../docs/README.md) › [docs/tests.md](../../docs/tests.md) (the test hub) › **this document**
>
> For the full list of tests, environment requirements, and the docker-integration analysis, see [docs/tests.md](../../docs/tests.md) (the hub). This README covers the operational details of the runners in this directory (`e2e.ps1` / `flow.ps1` / `run.cmd`).

End-to-end tests that exercise the implemented features ([docs/Assign.md](../../docs/Assign.md)) against a PGFS mounted with assign.pgfs (Windows / Dokan).

The Windows counterpart of the Linux version ([tests/linux/](../linux/README.md)), with much the same test structure. It adds the Windows-specific operations (the ReadOnly / Hidden / System / Archive attributes, the volume information, wildcard searching), and leaves out the native symlink / hardlink / arbitrary xattr and the POSIX chmod/chown that pgfs's Dokan adapter does not expose. That is not to say Windows itself has no linking.

## Files

| File | Contents |
|---|---|
| [e2e.ps1](e2e.ps1) | The test body (PowerShell) |
| [run.cmd](run.cmd) | Runs **only** the tests (assuming it is already mounted) |
| [flow.ps1](flow.ps1) | **The whole flow**: (an optional build) -> mount -> test -> unmount |
| [flow.cmd](flow.cmd) | The cmd wrapper around `flow.ps1` |
| [crossclient.ps1](crossclient.ps1) | The exclusion and visibility tests with **two mounts at once** (it mounts both itself and cleans them up) |
| [writeback.ps1](writeback.ps1) | The durability tests with **write-back on** (it mounts itself, force-terminates, remounts and checks the contents) |
| [wbmeta.ps1](wbmeta.ps1) | The contract tests of **metadata write-back plus the B-1 knob** (the occupant is made with a second mount) |
| [control_plane.ps1](control_plane.ps1) | The acceptance of **`pgfsctl config` / `status`** (live application, the effective values, the two-phase flip) |
| [prune.ps1](prune.ps1) | The end-to-end run of **`pgfsctl prune`** (C-2's retention -> kill -> orphan data -> the cleanup), and **not deleting a file a user named `.fuse_hidden*`**. **It mounts and kills on its own** |
| [permissions.ps1](permissions.ps1) | **The permission decision** (`app.enforce_permissions`, v0.2.1). **Run it in an elevated shell**; it plants files owned by someone else and checks them with **a restricted token**. It mounts `P:` on its own and puts the settings back at the end |
| [wbcross.ps1](wbcross.ps1) | The contract for **another mount writing the same body while a write-back mount is holding dirty data** (two mounts). That the truncate does not roll back, plus **that the trampling happens per chunk** (the latter is **a contract test pinning down the current behaviour, not the desirable one**) |

## The cross-client tests (two mounts)

```powershell
pwsh -NoProfile -File tests\windows\crossclient.ps1              # mounts on P: and R: and runs everything
pwsh -NoProfile -File tests\windows\crossclient.ps1 -MountB S:   # give a free drive letter
pwsh -NoProfile -File tests\windows\crossclient.ps1 -NoNotify    # without notify (the visibility tests are SKIPped)
```

It mounts the same DB-FS from two `assign.pgfs` processes and looks at **the contracts a single mount cannot show**:

- **Exclusion** (runs regardless of notify): a simultaneous `CREATE_NEW` always has exactly one success (and the winner's contents are not corrupted) / a simultaneous `mkdir` leaves one body
- **Visibility** (runs only when started with `--notify`): one side's create / overwrite / delete / replacement rename is visible from the other
- **Handle context** (handle-context stage B, `--notify` required): an open **append handle** sees the other mount's growth (not seeing it **overwrites and corrupts what the other side wrote** = the reproduction of problem 2) / it keeps pointing at the first inode across a rename plus a re-create under the same name (the guard for problem 1)

**In a setup with `database.notify_enabled` false** (the default up to v0.2.0; `--no-notify` since v0.2.1)
another mount's changes **are invisible by design**
(each mount's `InodeCache` / read cache is independent, and an invalidation only arrives through LISTEN/NOTIFY).
So the script passes `--notify` to the mounts it starts itself, and when it cannot (reusing an existing mount, or `-NoNotify`)
it **SKIPs** the visibility tests. Measured: without notify all four of them showed the stale state.

**Give drive letters that are free** (if the default `R:` is taken, Dokan fails to start with
`Something's wrong with the Dokan driver`).

### Things to watch when adding a handle-context test

- **.NET's `FileMode.Append` cannot be used** - the FileStream seeks to the end itself and **writes at an explicit offset**,
  so it comes down to Dokan with `WriteToEndOfFile = false`. To look at **the freshness of the attributes the handle holds**,
  open Win32 `CreateFileW` with **`FILE_APPEND_DATA` alone** and let the OS decide the end (the script does this through P/Invoke).
- **`[uint64]0x8000000000000000` cannot be written** - PowerShell **reads a hex literal as signed**
  (`0x80000000` is Int32's -2147483648, and `0x8000000000000000` is Int64's minimum), so the cast to uint falls over.
  **Use `[Convert]::ToUInt64("8000000000000000", 16)`, or append `L` to make it an Int64 first and then cast.**
  It was hit three times.
- **Make the change from the other mount.** Within the same mount **the same `Inode` instance** in the `InodeCache`
  is updated and the handle's copy becomes fresh along with it, which reduces **the detection power to zero**.
- **Observe the length through the enumeration (`Get-ChildItem -Filter`).** `Get-Item` / `Test-Path` can be
  answered by the Windows client-side FCB (the same reason as `Wait-Gone`).

## The permission decision tests

```powershell
pwsh -NoProfile -File tests\windows\permissions.ps1                    # the 13 cases other than sticky
pwsh -NoProfile -File tests\windows\permissions.ps1 -StickyDir P:\      # sticky too (pass a root-owned 1777 dir)
pwsh -NoProfile -File tests\windows\permissions.ps1 -AssignBinary <other build's exe>   # e.g. a pre-fix build
```

- **It creates no users.** The setup is done from an elevated shell through Set-Acl (the reverse projection of
  SetFileSecurity), passing **the Administrator SID** (= pgfs's `root`) as the owner and **Administrators**
  (= `root`) / **Users** (= `users`) as the group. The checking operations are done from C# with **a restricted
  token that has Administrators made deny-only** (`CreateRestrictedToken`) (a PowerShell script block run
  during impersonation can move to another thread, so each operation lives in C#). **Opening directly from the
  elevated shell passes straight through and checks nothing.**
- **Sticky cannot be set from Windows.** It is checked only when a root-owned `1777` directory (for example the
  FS root after `chmod 1777` from Linux) is passed to `-StickyDir`.
- **A file nobody can write (all w cleared) looks "read-only" on Windows and cannot be deleted** (because
  Windows refuses to delete a read-only file; in v0.2.0 someone else's `0644` looked like that too). The cleanup
  deletes after putting the owner back to yourself (`Clear-TestRoot`).

## Do not go green on zero tests

**Every suite exits 1 when "not a single test ran" or "not a single test passed (= everything was skipped)".**
A typo in `-Filter` or a missing prerequisite **passing green** would **look like a pass while nothing was checked**.
**Some of them being skipped still counts as success**, as before (there are tests that are always skipped in some environments).

**The same guard is in place on the Linux side** ([tests/linux/README.md](../linux/README.md), the section on not going green on zero tests).
Note, however, that **what happens when `psql` cannot be resolved differs per suite** (measured on the Linux side) -
`negcache.sh` / `prune.sh` / `handles.sh` **`exit 2` at the prerequisite check** (they do not quietly go green), while
`wbmeta.sh` **does run but its database-side checks fall to `skip` individually and it looks green**.
Either way, giving `PGFS_PSQL=/usr/local/pgsql/bin/psql` explicitly is the safe move.
It is the shape that upholds **"it does not fall over" and "it was checked" being different things** by construction.

## The traps hit while writing the tests (Windows)

### With `FileStream`'s default buffer, what you thought you wrote never reaches the FS

**`FileStream`'s default `bufferSize` is 4096, so a `Write` smaller than that piles up in .NET's buffer and no
`WriteFile` is issued until `Dispose` / `Flush`.** In other words **the FS callback was never called**.

**A test of the shape "hold dirty data with the handle still open" stops working entirely because of this.**
While verifying write-back, four bytes were written and the handle supposedly held, but the write actually went
down at the moment of `Dispose` = **after the other mount's operation**, and **it went green without the conflict
under test ever happening** (three scenarios in a row gave false negatives during the H-2 measurements, and one of
them was even reported as "it does not reproduce").

**`Write`'s return value, or `written=4`, is no evidence that it arrived.** It reports success for merely entering .NET's buffer.

What to do:

- **Hit `CreateFileW` + `WriteFile` directly through P/Invoke** (with no buffer in between).
- **Confirm from the log that it arrived before moving on to the next step** - with `database`'s setting at
  `level = "all"`, `WriteFileProxy : \<name> Return : Success NumberOfBytesWritten : N` is emitted, so **count the
  entries before and after the step**. If it did not grow, **treat it as INCONCLUSIVE rather than going green**.
```powershell
$before = @(Select-String -Path $LogFile -Pattern "WriteFileProxy : \\$name Return").Count
# ... write through P/Invoke ...
$after  = @(Select-String -Path $LogFile -Pattern "WriteFileProxy : \\$name Return").Count
if ($after -le $before) { Fail "the write did not reach the FS (the scenario does not hold)"; return }
```

**The Linux side has a trap of the same shape** - bash's `exec 8> file` **carries `O_TRUNC`**, so the file goes to
0 the instant it is opened and the scenario collapses ([tests/linux/README.md](../linux/README.md)).
**The shared lesson is "what you thought you wrote never reached the FS", and both of them tip in the direction of a green test.**

### A broken observer also gives "0 events" - put a negative control first

**"Not a single event arrived" does not distinguish "the FS is not notifying" from "we cannot receive them".**

While measuring the kinds of events a remote change produces with a FileSystemWatcher, **even local operations gave 0**.
The cause was that the block of `Register-ObjectEvent -Action` **does not see the caller's script scope**, so it simply
could not write into `$script:Events` (**use `$global:`**).
**Reporting it as it stood would have produced the false conclusion "pgfs emits no events".**

**What to do is to measure something that certainly emits events first.** Watch an ordinary NTFS location
(under `$env:TEMP`) with the same watcher and **abort the measurement if not a single event arrives there** (`exit 2`). Measure pgfs only once it does.

```powershell
$ctlEvents = Phase "NTFS: create file" { Set-Content (Join-Path $ctl "c.txt") "x" }
if ($ctlEvents -eq 0) { Say "0 events on the control = the watcher's wiring is broken. Aborting the measurement" "Red"; exit 2 }
```

**This is the observer-side version of "confirm it arrived before moving on".** The same hole exists on the writing
side (the `FileStream` above) and on the observing side, and **both of them tip in the direction of the test looking clean**.

### Do not make the caller write the options that set up the premise

**`--write-back-interval-ms 0` was forgotten four times in one day** (the three H-2 scenarios plus the `exit 4` measurement).
The default is 1000 ms, so **the background flush writes the dirty data out first and the conflict under test never happens**.
**All four times it looked like the false conclusion "it does not reproduce".**

**Care does not stop it, so it is baked into a helper and the caller never writes it**:

```powershell
# wbcross.ps1
function Start-MountHoldingDirty($point) {
	return Start-Mount $point "--write-back --write-back-interval-ms 0"
}
```

**If "the premise for this test to hold" ends up being hand-written as an option string every time, that is the hole.**
The Linux side bakes it into `mount_a_writeback()` in `crossclient.sh` for the same reason.

## Scenario policy

- At the start, recursively delete then create `$MountRoot\test`
- All tests run only under `$MountRoot\test\`
- Each test uses a unique prefix (`t01_` / `t02_` ...) so they do not interfere
- At the end (whether passing or failing), recursively delete `$MountRoot\test`

## Coverage

Exercises the Dokan operations marked ✅ / ⚠️ in [docs/Assign.md](../../docs/Assign.md).

| Category | Tests |
|---|---|
| **Directory operations** | mkdir/rmdir, nested directories, a 100-file directory, refusing a non-empty rmdir |
| **File basics** | new/del, a small write/read, append, overwriting with FileMode.Create (the equivalent of O_TRUNC) |
| **Data I/O (bytea)** | a 2 MiB round trip (spanning chunks), truncate shrinking/growing/zeroing |
| **Renaming** | Move-Item, a Move-Item into a subdirectory |
| **Owner / group** | the owner of a new file is **the requesting account**, and the group is **inherited from the parent directory** |
| **CopyFileEx** | a 2 MiB copy in both directions with `Copy-Item` matches by hash (the same path as a copy in Explorer) |
| **byte-range locks** | `FileStream.Lock` is **enforced by the driver** within one mount (the second one fails, and it can be taken after `Unlock`) |
| **Attributes / times** | SetFileAttributes (ReadOnly / Hidden / System / Archive), the xattr `user.win.attrs` round trip, the dot-file fallback, un-hiding persisting, SetFileTime (LastWriteTime) |
| **Volume / patterns** | the drive information through GetVolumeInformation, FindFilesWithPattern (`-Filter`) |
| **Windows basics** | `CreateNew` exclusion (the second one fails and the winner's contents are not corrupted), `FileStreamOptions.PreallocationSize` does not grow EOF, a rename onto the same path is a no-op |
| **Concurrency** | parallel writes to different files, parallel reads from the same file, parallel mkdir |

## The shell it runs in (pwsh is preferred)

`flow.cmd` / `run.cmd` **use pwsh (PowerShell 7) when it is available**. On some machines Windows PowerShell 5.1
fails to load `Microsoft.PowerShell.Security` / `Microsoft.PowerShell.Utility`
(`TypeData "System.Security.AccessControl.ObjectSecurity": The member ... is already present`),
and then `Get-Acl` / `Get-FileHash` are unusable and **three tests FAIL for reasons unrelated to pgfs**
(`test_getfilesecurity_projection` / `test_setfilesecurity_roundtrip` / `test_concurrent_reads_same_file`).
`e2e.ps1` itself is written to run on 5.1 as well, except that
`test_allocation_size_does_not_extend_eof`, which uses `PreallocationSize`, needs .NET 6+ and is SKIPped on 5.1.

## The metadata write-back / B-1 knob tests (two mounts)

```powershell
pwsh -NoProfile -File tests\windows\wbmeta.ps1            # the 4 tests with A=P:(defer) / B=R:(write-through)
pwsh -NoProfile -File tests\windows\wbmeta.ps1 -MountB S:
```

It looks at the contracts of `mount.write_back_metadata` and `mount.write_back_metadata_exclusive_create = defer`.
**Where the Linux version makes "the occupant" with a psql fault injection, this one makes it with a real second mount.**

| Test | What it looks at |
|---|---|
| `test_meta_defer_exclusive_within_mount` | Even under defer, `CreateNew` exclusion within one mount is kept |
| `test_meta_close_no_flush_loses_content` | A file that was only closed is lost to a forced termination (the contract) |
| `test_meta_defer_conflict_does_not_clobber_occupier` | **The B-1 requirement**: the loser's flush fails and the occupant's contents are untouched |
| `test_meta_error_state_blocks_persisted_delete` | **B-7**: something persisted cannot be deleted during the error state (the file survives and **the caller gets the failure too**). It consults `Api.CanDestroy` in `DeleteFile` and refuses |
| `test_meta_defer_conflict_recovers_by_unlink` | **SKIP**: Windows defers `DeleteFile`, so the recovery cannot be observed in this environment (the latch bug on the Core side is fixed) |

**The order of the tests matters**: once the collision test latches, the creates and writes that follow return `-EIO`, so the contract tests that do not latch come first.

## The visibility of a delete (why `Assert-Absent` uses the enumeration plus an open)

A delete in pgfs actually happens **at Dokan's `Cleanup`**, and the DELETE in the database (Citus) takes about 100 ms
on top of that. Furthermore **the Windows client-side FCB cache** can keep returning the name after it is gone from
the database (the case where another process - an antivirus or an indexer - had it open concurrently. Measured at more
than 10 seconds with 0 queries to the FS).
So `Assert-Absent` uses a bounded 10-second retry and looks, in order, at **the parent directory's enumeration, then at
an open if it is still there** (a failing open means delete-pending = it is treated as deleted on the pgfs side).
Writing a single `Test-Path` is certain to be flaky, so do not.
**Even so, `test_touch_unlink` FAILs about one time in three** (it is in the enumeration and can be opened).
The delete on the pgfs side has been confirmed in the database, and the cause is the client-side cache. It is treated as a known intermittent FAIL.

## Known problems on the Assign side

Issues found while building out the tests. The e2e suite works around them (calling the .NET API directly), but they are tracked separately for a fix:

| Issue | State | Note |
|---|---|---|
| A 2 MiB file raises an `IOException` with `Copy-Item` (CopyFileEx) | ✅ resolved | **The cause was a SELECT-then-INSERT race in `Api.EnsureChunk`** (CopyFileEx's concurrent WriteFile INSERTed the same `(data_id, chunk_index)` twice and died on the PK constraint). Fixed by moving to `INSERT ... ON CONFLICT DO NOTHING` ([history.md](../../docs/history.md)). **Only this table row had been left behind.** It was confirmed not to reproduce at 2 MiB / 8 MiB, in both directions, on overwrite, with `robocopy /COPYALL` or on a tree copy, and the real path was put back into the tests as `test_copy_item_round_trip` |
| `dokanctl /u` fails with "Admin rights required" | worked around | By Dokan's design `dokanctl /u` requires administrator rights. `flow.ps1` falls back to `Process.Kill()` (a forced termination, so write-back's unflushed data can be lost. It cannot be used to verify a clean exit or durability) |

## The recorded results and what is not verified

**The counts and the results on real hardware are owned by [docs/tests.md](../../docs/tests.md)**. They are not written
here - adding one test to a suite would mean fixing both, and **one of them would rot for certain**. In fact a stale
"e2e 34 tests / cross-client 6/6 / metadata write-back 3 passed" was found still sitting here (the measured values were
e2e 38 / cross-client 10 / wbmeta 5).
**What this doc holds is only how to run things and what each suite is aiming at** (the same shape as the Linux README).
**Everything this section used to carry as "unverified" has been closed by measurement.**

(**exit 4 has been seen to fire for real** - with metadata write-back's `defer`, give A a pending create,
**have B occupy the same name with `CreateNew`**, then **stop it cleanly** with `dokanctl /u` and it returns **exit 4**.
**Stopping without leaving anything unflushed gives exit 0**, so it is confirmed as a contract.
`--write-back-interval-ms 0` is essential; left at the default 1000 ms **the background flush materializes the pending entry first and no collision happens**.
The details are in "a clean stop and the loss report" in [windows-parity.md](../../docs/design/windows-parity.md).)
(**The kinds of events Explorer / FileSystemWatcher produce have been measured too** - **nothing originating from
another mount becomes an event**. Local operations produce `Created`/`Changed`/`Renamed`/`Deleted`. The details are in the Notify row of [Assign.md](../../docs/Assign.md).)
(The acceptance with `write_back` / `write_back_metadata` turned on **is complete on real hardware** - [writeback.ps1](writeback.ps1) 6/6 and
[wbmeta.ps1](wbmeta.ps1) 4 passed + 1 skip. The skip is the one whose recovery cannot be observed because Windows defers `DeleteFile`.)
The Windows acceptance criteria for the caching, write-back and live settings that arrived later on Linux are in the [Windows rollout design](../../docs/design/windows-parity.md).

## Not covered

The ❌ items in the TODO table of [docs/Assign.md](../../docs/Assign.md) are unimplemented and out of scope:

- Strict access denial through named ACLs, and default ACL inheritance (the projection and reverse projection of GetFileSecurity / SetFileSecurity are covered by the existing tests)
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
=== assign.pgfs ===
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
Results: 27 passed, 0 failed, 0 skipped (out of 27)

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
| 0 | every test passed |
| 1 | at least one failed |
| 2 | `MountRoot` does not exist (it is not mounted) |
| 3 | `TestRoot` cannot be created (the mount is not writable) |
| 11 | `assign.pgfs.exe` was not found (flow only) |
| 99 | an exception before the tests ran (flow only) |

## Prerequisites

- **The .NET 10 SDK** (only to build)
- The **Dokan 2.x** driver (`DokanSetup_redist.exe` from the [Dokan releases](https://github.com/dokan-dev/dokany/releases))
  - flow.ps1 finds `dokanctl.exe` automatically under `C:\Program Files\Dokan\Dokan Library-*\`
  - when it is not found it falls back to `Stop-Process` (the unmount may be unclean)
- The mount point (`P:` and so on) does not collide with an existing drive
- PGFS has been initialized in PostgreSQL ([docs/Mkfs.md](../../docs/Mkfs.md))
- `pgfs.toml` carries the database connection information

## The write-back tests (durability)

```powershell
pwsh -NoProfile -File tests\windows\writeback.ps1                     # data write-back on (6 tests)
pwsh -NoProfile -File tests\windows\writeback.ps1 -WriteBackMetadata  # metadata write-back on as well
pwsh -NoProfile -File tests\windows\writeback.ps1 -MountPoint S:
```

On a mount with `mount.write_back` **enabled**, it verifies the durability contract by
"**force-terminate the process -> remount -> is the content still there**" (the counterpart of the Linux version [writeback.sh](../linux/writeback.sh)).
`Stop-Process -Force` is the equivalent of `kill -9`; neither `Cleanup` nor `Dispose` runs.

| Test | What it looks at |
|---|---|
| `test_wb_flush_survives_kill` | It survives once `FlushFileBuffers` (`FileStream.Flush($true)`) has run |
| `test_wb_close_survives_kill` | It survives once the handle has been closed (**the contract changes with metadata on**, so nothing is asserted there) |
| `test_wb_writethrough_survives_kill` | `FILE_FLAG_WRITE_THROUGH` survives being killed with neither a flush nor a close |
| `test_wb_unflushed_is_lost_without_barrier` | **The negative control**: a write that did not go through a barrier is lost |
| `test_wb_graceful_unmount_persists` | A clean unmount writes out whatever is unflushed and ends with **exit 0** |
| `test_wb_large_write_flush_roundtrip` | 3 MiB (spanning chunks) matches by hash after the flush |

**The mount is established with `--write-back --write-back-interval-ms 0`.** Without turning the background flush's
time trigger off, the negative control becomes a race against "whether a second passes" and the whole suite stops being able to verify the contract.

**Free the target mount point before running it** (this script mounts and remounts on its own, so an existing mount gives exit 2).

## The tests that force-terminate leave rows in `{prefix}mounts`

`writeback.ps1` / `wbmeta.ps1` use `Stop-Process -Force` (= the equivalent of `kill -9`) in order to measure durability.
`Api.Dispose` is not reached then, so **the mount registration row (`{prefix}mounts`) is not DELETEd and survives**
(a different thing from the "gravestone" a clean unmount with a loss leaves; this one is just a leftover).

Once they pile up, `pgfsctl status` becomes unreadable (measured: 38 rows piled up and buried Layer 1). Cleaning up is an explicit operational act:

```sql
-- Do not delete the gravestones (stats->>'unflushedLoss' > 0). Take a grace period from the heartbeat so a running mount is not caught.
DELETE FROM <schema>.<prefix>mounts
 WHERE COALESCE((stats->>'unflushedLoss')::int, 0) = 0
   AND heartbeat_at < (now() AT TIME ZONE 'UTC') - INTERVAL '10 minutes';
```

Automating the reaper is an item in [docs/next.md](../../docs/next.md).

**About how processes are killed**: the scripts in this directory take down **only the PID they started themselves**,
obtained from `Start-Process -PassThru`, with `Stop-Process -Id` / `Process.Kill()`.
They never search by command line or process name (that would catch unrelated processes).

## The control-plane tests (`pgfsctl config` / `status`)

```powershell
pwsh -NoProfile -File tests\windows\control_plane.ps1
pwsh -NoProfile -File tests\windows\control_plane.ps1 -Filter reload
```

| Test | What it looks at |
|---|---|
| `test_cp_config_list_and_get` | `config list` / `config get` return the known keys |
| `test_cp_status_json_shape` | The live row of `status --json` carries **the effective settings** (`mode=dokan`, including the B-1 knob) |
| `test_cp_live_reload_reflected` | A `config set` of a Live item **takes effect on the running mount** |
| `test_cp_invalid_enum_rejected` | A value outside the permitted set is rejected at `set` time and the effective value does not change |
| `test_cp_write_back_live_flip` | Data is untouched across `mount.write_back`'s **live on -> off (the two-phase flip)** |
| `test_cp_metadata_flip_completes` | **The metadata write-back flip runs to completion** - turn it off while holding 300 pending entries, and the intake reopens / pending is 0 / all 300 files are untouched |

**Starting the mount with `--no-notify`** (on by default since v0.2.1, so it is turned off explicitly) is the
deliberate point. The control channel's LISTEN is always ON, so
`config set` reaches a mount with `database.notify_enabled = false` too. That is checked at the same time.

**The application goes through the heartbeat snapshot**, so it takes **up to one period (30 seconds by default)** to appear in `status`'s effective values.
The default wait is `-ReflectTimeoutSec 75`.

**These tests change settings**, so whatever they change is always put back in a `finally` (the FS this `pgfs.toml`
points at is used for operational testing, and leaving `audit.enabled` off would stop the auditing).

### The practice for the flip test (kept in line with the Linux side)

`test_cp_metadata_flip_completes` follows the Linux side's practice of **widening the window before observing**:

1. Stop the background flush's time trigger with `mount.write_back_interval_ms = 0`
2. Build up a few hundred pending entries (materializing them takes time, so the first phase lasts seconds)
3. Issue the `config set` (**it returns without waiting for an ack**, so polling can start right after)
4. Poll `status --json` every 0.5 seconds

**Whether the first phase (intake closed) can be caught is timing-dependent**, so it is only recorded when observed,
and what is asserted is **completion (enabled=false and effective=false and the intake reopened and pending 0)**.
"A pending entry can be created after the flip completes" (B-9 (1)) cannot be forced deterministically from a shell, so **no test is written for it** -
atomicity is upheld by Core inside the ledger lock, and the Linux side made the same call.
