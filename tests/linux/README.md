# pgfs Linux e2e tests

> **Route**: [docs/README.md](../../docs/README.md) › [docs/tests.md](../../docs/tests.md) (the test hub) › **this document**
>
> For the full list of tests, environment requirements, and the docker-integration analysis, see [docs/tests.md](../../docs/tests.md) (the hub). This README covers the operational details of the runners in this directory (`e2e.sh` / `flow.ps1` / `run.cmd`).

End-to-end tests that exercise the implemented features ([docs/Mount.md](../../docs/Mount.md)) against a PGFS mounted with mount.pgfs (Linux).

## Files

| File | Contents |
|---|---|
| [e2e.sh](e2e.sh) | The bash test body (run on Linux, **assuming it is already mounted**) |
| [writeback.sh](writeback.sh) | **Dedicated to write-back** (9 tests). It **mounts and remounts on its own** in order to look at `mount.write_back`'s durability contract |
| [wbmeta.sh](wbmeta.sh) | **Dedicated to metadata write-back** (**27 tests**, all green after the round A fixes and B-1 to B-9). The contract of `mount.write_back_metadata`. It mounts and remounts on its own, and checks the database side directly with psql |
| [startup.sh](startup.sh) | **Dedicated to the start-up contracts** (10 tests). The treatment of `-o` / `started (pid N)` / the fallback of owner resolution. It mounts and remounts on its own |
| [prune.sh](prune.sh) | **Dedicated to `pgfsctl prune`** (11 tests). Cleaning up what an abnormal exit left behind. **psql is required** (to make the artificial leftovers). It mounts and remounts on its own and also makes a real `.fuse_hidden` leftover with `kill -9` |
| [handles.sh](handles.sh) | **Dedicated to handle leaks** (**11 tests**). It reads the `handles` line of `pgfsctl status` and confirms that every borrowed handle comes back. **psql is required** (to fire a ping that makes a snapshot be written). It mounts and remounts on its own |
| [crossclient.sh](crossclient.sh) | **Dedicated to cross-client** (**18 tests**). Mounts the same DB-FS **twice** and looks at visibility (the Linux version of Windows's [crossclient.ps1](../windows/crossclient.ps1)). It establishes both mounts itself |
| [negcache.sh](negcache.sh) | **Dedicated to the negative lookup cache** (7 tests). The visibility contract of `mount.negative_cache_ttl_ms`. It mounts and remounts on its own, and a direct psql INSERT stands in for the other client |
| [run.cmd](run.cmd) | Runs **only** the tests (assuming it is already mounted) |
| [flow.ps1](flow.ps1) | **The whole flow**: rsync -> publish -> mount -> test -> unmount (PowerShell) |
| [flow.cmd](flow.cmd) | The cmd wrapper around `flow.ps1` |

## Do not go green on zero tests

**Every suite exits 1 when "not a single test ran" or "not a single test passed"**
(the same shape as the section on not going green on zero tests in Windows's [tests/windows/README.md](../windows/README.md)).
It is the script's way of upholding that **"it did not fall over" and "it was checked" are different things**.

Two measured holes were closed. **Both of them exited 0 before they were closed**:

| Shape | Before | Now |
|---|---|---|
| A typo in `TEST_FILTER` | `TEST_FILTER=zzz_nonexistent bash tests/linux/startup.sh` -> `0 passed, 0 failed, 0 skipped (out of 0)` / **exit 0** | `not a single test ran (does TEST_FILTER='zzz_nonexistent' match nothing?)` / **exit 1** |
| Everything skipped for a missing prerequisite | the database-dependent tests of `wbmeta.sh` without `PGFS_PSQL` -> `0 passed, 0 failed, 1 skipped` / **exit 0** | `not a single test passed (1 skipped = a missing environment, not a passing test)` / **exit 1** |

**Some of them being skipped still counts as success**, as before (there are tests that are always skipped in
some environments, such as `test_fallback_uname_gname` in `e2e.sh`. 49 passed / 1 skipped -> exit 0).

### The psql prerequisite differs per suite

In an environment where `psql` **exists only as an alias in the interactive shell**, a plain `psql` cannot be
resolved from `bash tests/linux/*.sh`. **What happens then differs per suite**, so always passing
`PGFS_PSQL=/usr/local/pgsql/bin/psql` is the safe move:

| Suite | When `psql` cannot be resolved |
|---|---|
| `prune.sh` / `handles.sh` / `negcache.sh` | **`exit 2` at the prerequisite check** (not a single test runs. It does not quietly go green) |
| `wbmeta.sh` | **There is no prerequisite check.** It does run, but **the database-side checks fall to `skip` individually** - it looks green overall while only the FS side has been checked |
| `e2e.sh` / `writeback.sh` / `crossclient.sh` / `startup.sh` | They do not use `psql` (`startup.sh` skips only the one fallback test) |

### Using writeback.sh

Unlike `e2e.sh` it **mounts and remounts on its own** (to `kill -9` after an fsync/close and see whether the data survives).
Point it at an FS that may be destroyed (it only touches what is under `$MOUNT_ROOT/wbtest`).

```bash
bash tests/linux/writeback.sh
PGFS_SETTING_FILE=~/pgfs_test.toml MOUNT_ROOT=~/mnt/pgfs bash tests/linux/writeback.sh
TEST_FILTER=fsync bash tests/linux/writeback.sh
```

Environment variables: `PGFS_SETTING_FILE` (default `$HOME/pgfs_test.toml`) / `MOUNT_ROOT` (default `$HOME/mnt/pgfs`) /
`PGFS_BIN` (default `./bin/Debug`) / `TEST_FILTER`.

`e2e.sh` itself **should give the same result in both `write_back` on and off**, so run it both ways
(the only difference is whether `--write-back` is passed at mount time).

> ⚠ **A bool flag consumes no value.** Writing `--write-back true` makes `true` fall through as a positional
> argument, and with two of them positional[1] turns into `mount.mount_point` and the mount fails (`fuse: failed to
> access mountpoint true`). **Always pass them as bare flags** (`--write-back --write-back-metadata`).

### The traps hit while writing the tests (Linux)

Two **shapes in which the test lies** were actually hit, so they are recorded here.

- **Even `psql -At` brings the `INSERT 0 1` command tag along.** Using the return of `returning id` as-is
  gives `"NNN\nINSERT 0 1"` and makes the following `where id = ...` **a syntax error**.
  `q()` swallows the error and returns an empty string, so it **misjudges it as "there is no row"**.
  **Pipe it through `| head -1`.** The real harm showed up in `prune.sh` - **the live-gate test gave a false
  FAIL** and **the orphan-data deletion test gave a false PASS**. **The latter is the dangerous one** (it moves on as though it had passed).
- **Do not take your own daemon's pid with `pgrep -x mount.pgfs | head -1`.** It grabs a daemon running on a
  different mount point (crossclient's B side, or one left behind), and **every `status` read after that is
  someone else's row entirely**. `handles.sh` hit this and **6 tests turned into mysterious FAILs**.
  **Look it up from `{prefix}mounts` as "the newest row for this mount point".**

- **"While holding dirty data" cannot be produced from bash.** A test that measures write-back cross-client
  needs to **move the other side without letting the write flush**, and both of bash's natural spellings break:
  - **`exec 8> file` carries O_TRUNC.** The file goes to 0 the instant it is opened, and **the whole scenario collapses while still reporting green**.
  - **`exec 8<> file` plus `printf >&8` does not truncate, but the redirection closes the duplicated fd.**
    **A flush is per file**, so that close **flushes all of the dirty data**.
  - **Opening the same file to check is the same trap.** Merely slipping in `head -c 4 "$f"` erased the dirty
    data and it was misjudged as "no dirty data could be produced". **Check for unflushed data through the other mount and the database only.**
  - **The way around it is to `os.open(path, os.O_WRONLY)` plus `os.pwrite` from python and wait while holding the fd**
    (`test_xc_writeback_flush_does_not_undo_remote_truncate` in `crossclient.sh` is the worked example).
  - The Windows side hit **the same shape** (with .NET `FileStream`'s default 4096B buffer, a small write does
    not reach the FS until `Dispose`). The details are in the section on the traps hit while writing the tests
    (Windows) in [tests/windows/README.md](../windows/README.md). **The shared lesson is "what you thought you
    wrote never reached the FS", and both of them tip in the direction of a green test.**
- **On Citus, `inode` and `data_chunk` cannot be joined.** Their distribution keys differ, so it is rejected
  with `complex joins are only supported when all distributed tables are co-located ...`.
  `q()` swallows the error and **returns an empty string**, giving the shape "the premise check always fails /
  always passes". **Fetch it in two queries** (look up the `data_id` and then the chunks. `PruneAdmin.ScanOrphanData`
  splits into two queries for the same reason).

**Both of them are the "the test failed yet the code is right" shape**, which eats the most time.
**When one fails, firing the same thing by hand first** is the quicker route.

### Using startup.sh

It looks at `mount.pgfs`'s **start-up contracts**. It mounts and remounts on its own.

```bash
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/startup.sh
```

Environment variables: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` /
`PGFS_PSQL` (`PSQL` works too; only the fallback test uses it. Without it, only that one test is skipped).

The breakdown:

| Test | What it looks at |
|---|---|
| `test_dash_o_max_write_maps_to_field` | `-o max_write=N` is **not forwarded** to libfuse (forwarding it fails `fuse_new` and takes the whole mount down) |
| `test_dash_o_unapplied_options_warn` | `noexec` / `sync` / `dirsync` **warn**. `nosuid` / `nodev` / `relatime` **do not** (they would be noise) |
| `test_dash_o_fuse_breaking_options_are_not_forwarded` | `max_read` / `max_readahead` are not forwarded to libfuse. **Mounting successfully is not enough**: it waits 2 seconds and checks it is **still alive** (`max_read` reports success and then the session dies and it disappears with exit 0) |
| `test_started_pid_is_the_daemon` | `started (pid N)` is **the real daemon's** pid (not the pre-fork parent) |
| `test_unknown_owner_falls_back` | An unresolvable uname/gname falls back (65534) = `getpw*_r`'s "not found" is not mistaken for an error |

### Using crossclient.sh

It mounts the same DB-FS **twice** (`MOUNT_ROOT` = A / `MOUNT_ROOT2` = B) and confirms that one side's changes
are visible from the other. It only touches what is under `xctest` on each mount.

```bash
bash tests/linux/crossclient.sh
MOUNT_ROOT2=~/mnt/pgfs2 PGFS_XC_WAIT=20 bash tests/linux/crossclient.sh
TEST_FILTER=hardlink bash tests/linux/crossclient.sh
```

Environment variables: `PGFS_SETTING_FILE` / `MOUNT_ROOT` (A, default `$HOME/mnt/pgfs`) /
**`MOUNT_ROOT2`** (B, default `$HOME/mnt/pgfs2`) / `PGFS_BIN` / `TEST_FILTER` /
**`PGFS_XC_WAIT`** (the upper bound in seconds for waiting on visibility, default 10).

> ⚠ **Both mounts are started with `--notify`.** Cross-client visibility depends entirely on
> `database.notify_enabled`, and with the default (false) another mount's creations / overwrites /
> deletions / replacement renames **stay invisible indefinitely** (the same note as in [Assign.md](../../docs/Assign.md)).
> The script passes it itself, so the caller needs no configuration.

The breakdown:

| Group | Count | Tests |
|---|---|---|
| Visibility | 4 | `test_xc_create_visible` / `test_xc_delete_visible` / `test_xc_overwrite_visible` / `test_xc_rename_replace_visible` |
| **Hardlink body sharing** | 2 | `test_xc_hardlink_size_propagates` / `test_xc_hardlink_truncate_propagates` - **the regression guard for the fix that puts the distributed sibling ids into the notification** ([data-id-lifecycle.md](../../docs/design/data-id-lifecycle.md)). **Stat it on B first to put it into the cache, then write on A** - the crux; without that, B re-reads the database and goes straight past the defect |
| Exclusion | 1 | `test_xc_exclusive_create_races` - fires a same-named `O_EXCL` from A and B at once and checks that **exactly one succeeds**, over 5 rounds |
| **Defence against orphaning** | 1 | `test_xc_create_under_removed_parent_fails` - a child cannot be created under a directory another client deleted, through **all four paths: create / mkdir / ln -s / ln**. It goes as far as checking **that it is ENOENT** ("as long as it fails" would pass on EEXIST too, and that was actually missed once). **This one test alone re-establishes the mounts with notify OFF** (with ON, the rmdir notification drops A's cache and the window closes) |

**Visibility is always waited on by polling** (`wait_until`). Asserting without waiting misjudges "slow" as
"broken" - the Windows side actually hit two false detections of that shape.

**The notification channel is checked up front at start-up** (`preflight_notify`). Running the visibility tests
while it is not connected fails four of them together, but the symptom only says "not visible", so **a
disconnected notification gets misread as a visibility bug** (which the Windows side actually hit).
**Do not look at `connected` alone** - the control channel's LISTEN is always established regardless of
`database.notify_enabled`, so **`connected: true` holds even with notify OFF** (measured).
**Look at `data_enabled` as well.** In an environment where psql cannot be resolved the check is skipped
(the tests themselves still run).

### Using negcache.sh

Dedicated to the negative lookup cache (`mount.negative_cache_ttl_ms`). It mounts and remounts on its own, and
**a direct psql INSERT stands in for the other client**. It only touches what is under `$MOUNT_ROOT/negcache`.

```bash
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/negcache.sh
```

Environment variables: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` plus
**`PGFS_PSQL`** (`PSQL` works too).

> ⚠ In an environment where `psql` is **only on the interactive shell's PATH** (an alias, or a PATH addition in
> `.bashrc`), this script - which runs non-interactively - cannot resolve it, the prerequisite check prints
> `cannot connect to the database with psql` and it **ends with exit 0 without running a single test**. Give the path explicitly.

### Using wbmeta.sh

Dedicated to metadata write-back (`mount.write_back_metadata`). Like `writeback.sh` it mounts and remounts,
and on top of that it **checks the consistency on the database side (the result of materializing the pending
entries) directly with psql**. It only touches what is under `$MOUNT_ROOT/wbmeta`.

```bash
bash tests/linux/wbmeta.sh
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/wbmeta.sh
TEST_FILTER=truncate bash tests/linux/wbmeta.sh
```

Environment variables: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` plus
**`PGFS_PSQL`** (default `psql`. The database-checking tests are skipped when it cannot be resolved).

The breakdown (27 tests):

| Group | Count | Tests |
|---|---|---|
| Stage 1 (the consistency of the pending ledger) | 4 | `test_pending_same_name_eexist` / `test_pending_truncate_zero_consistency` / `test_pending_pin_does_not_blow_cache` / `test_pending_mkdir_visible_in_ls` |
| The durability contract (on) | 3 | `test_meta_fsync_survives_crash` / `test_meta_close_only_is_lost_on_crash` (**asserts that it "disappears"**) / `test_meta_fsyncdir_persists_pending_children` |
| Heuristic (a) rename-over-existing | 2 | `test_meta_rename_over_existing_pending_source` / `..._excl_source` |
| Heuristic (b) truncate -> a synchronous close | 2 | `test_meta_otrunc_close_is_synchronous` / `test_meta_truncate_syscall_close_is_synchronous` (**turned green by the round A fixes**; before that it was a reproduction test for an unfixed defect) |
| Heuristic (c) O_EXCL is write-through | 1 | `test_meta_exclusive_create_is_write_through` |
| Regression (a read with the data_id only reserved) | 1 | `test_meta_read_after_close_no_flush` |
| B-9 (the effective mode) | 1 | `test_meta_live_flip_publishes_effective_mode` (the two-phase flip's intake-closed appears in status. It holds 800 pending entries in order to **slow the flip down deliberately**) |
| B-7 (blocking destructive operations) | 1 | `test_meta_error_state_blocks_destroy_but_allows_cancel` (**a persisted unlink/truncate is stopped** and **a pending unlink goes through**, both in the same test) |
| B-6 (the loss report) | 1 | `test_meta_loss_report_has_paths_and_breakdown` (paths plus the truncation breakdown. It **collides a pending directory with a different kind** to produce 41 losses from one injection) |
| B-5 (writing the error state immediately) | 1 | `test_meta_error_state_is_published_immediately` (it lands in `{prefix}mounts` without waiting out the 30-second heartbeat period, and so does the release) |
| B-4 (not fabricating a loss) | 1 | `test_meta_audit_live_off_does_not_fake_loss` (no B-2 gravestone appears on a clean unmount after audit was turned off live. **It cannot arise structurally as things stand, so it is a regression guard**) |
| B-3 (writing the cancellation audit immediately) | 1 | `test_meta_cancel_audit_is_written_immediately` (**before anything is unmounted** the create/delete rows are in the database. It turns `audit.enabled` on temporarily and puts it back) |
| B-2 (recording the loss in the database) | 1 | `test_meta_loss_is_recorded_in_db` (**the gravestone survives** / a warning on the **parent's** stderr at the next mount / deleting the gravestone removes the warning) |
| The B-1 knob (`write_back_metadata_exclusive_create`) | 3 | `test_meta_exclusive_create_defer_is_pending` / `..._same_mount_eexist` / `..._conflict_latches` (the points are **not deleting the occupant** and **recovering with an unlink**) |
| Hardlink siblings (A-1 of round A) | 1 | `test_meta_hardlink_sibling_survives_unlink` (**a write through a sibling does not disappear silently on an unlink**. It writes with `>>` and slips `ln` / `rm` in while the fd is open) |
| The two-phase flip / the error floors | 3 | `test_meta_live_off_flushes_pending_then_write_through` / `test_meta_backpressure_blocking_inodes` / `test_meta_error_state_blocks_and_clears` |

Things to watch:

- **Producing a pending entry needs "a create with neither `O_EXCL` nor `O_TRUNC`".** `cp` opens a new
  destination with `O_CREAT|O_EXCL` (confirmed with strace) so heuristic (c) makes it write-through, and
  `> file` carries `O_TRUNC`. The tests use python's `os.open(p, O_CREAT|O_WRONLY)`
  (`plain_create`) and `mkdir`.
- `test_meta_error_state_blocks_and_clears` **INSERTs "a same-named row of a different kind" directly with psql**
  to make the flush fail permanently (a fault injection). The row is marked with `created_by = 'wbmeta-test'`
  and **always deleted by the EXIT trap (`cleanup_injection`)**. To check by hand after it was killed halfway:
  `select * from <schema>.<prefix>inode where created_by = 'wbmeta-test'`.
- Firing `insert ... returning` with `psql -At` also brings the `INSERT 0 1` status line to stdout, so when the
  value is needed it is wrapped in a CTE (`with ins as (insert ... returning id) select id from ins`).

## The approach to the scenarios

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
Results: 35 passed, 0 failed, 0 skipped (out of 35)

=== unmount ===
  unmounted
  mount log: tests\linux\mount.log (6 lines)

  ALL PASSED
```

## Where things stand

**The counts and the latest results are owned by [docs/tests.md](../../docs/tests.md)** (keeping a second copy here would rot one of them for certain).
As of the latest run the e2e is **43 passed / 0 failed / 1 skipped (44 tests)** and `wbmeta.sh` is **27/27**.

An earlier record: at one point there were 35 tests with **35 passed / 0 failed / 0 skipped** (single PG mode +
single-node Citus + multi-node Citus on docker, 35/35 in every one. The POSIX ACL `test_posix_acl_named_user` was added).
The multi-node Citus verification goes through [tests/citus/race_multinode.sh](../citus/README.md).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every test passed |
| 1 | at least one failed / **not a single test ran** / **not a single test passed** (the section above on not going green on zero tests) |
| 2 | `MOUNT_ROOT` does not exist (it is not mounted) |
| 3 | `TEST_ROOT` cannot be created (the mount is not writable) |

## Required packages

The xattr tests use the `attr` package (`getfattr` / `setfattr`). If it is not installed, the xattr tests are skipped with a `SKIP` notice:

```bash
sudo apt install attr   # Debian/Ubuntu
sudo dnf install attr   # Fedora/RHEL
```

## Not covered

The ❌ items in the TODO table of [docs/Mount.md](../../docs/Mount.md) are unimplemented and out of scope:

- Verification on macOS (depending on libfuse's macOS support)
- The Access check (the `Access` operation)
- The mount option `-o` (full support)

For the remaining list of unimplemented features, see [docs/next.md](../../docs/next.md).
