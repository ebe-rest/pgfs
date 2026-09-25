# The tests: the list, how to run them and the environment requirements

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **the hub for the tests** - which suite watches what, the
> counts, how to run them and the environment requirements.
> **This document is the source of truth for the counts**, and each runner's README holds the operational
> details.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../tests/linux/README.md](../tests/linux/README.md) | **The operational details** and the breakdown of each Linux suite |
> | [../tests/windows/README.md](../tests/windows/README.md) | The operational details of each Windows suite |
> | [../tests/citus/README.md](../tests/citus/README.md) / [../tests/docker/README.md](../tests/docker/README.md) | The Citus and docker runners |
> | [Mount.md](Mount.md) / [Assign.md](Assign.md) | **The list of operations** the tests cover |
> | [design/support_for_citus.md](design/support_for_citus.md) | The design the Citus tests verify |
> | [next.md](next.md) | The test items not yet started |

The **hub document** for every pgfs test. "What tests exist / how to run them / what environment they need" is
gathered here. The detailed options of each runner (the parameters of flow.ps1 and so on) are authoritative in
each directory's README and linked from here.

Last updated: **2026-09-20** (the regression for handle-context stage B). The record of **2026-09-19** is held by
the individual lines below. The **first half** of that day was a static comparison of the documents and the
scripts only (no tests were run). **In the second half both Linux and Windows were run on real hardware**:

- **Linux** (the dev server, Citus rf=2): after the round A fixes, **4 suites were re-run green** - the e2e suite
  with `write_back_metadata` both off and on (43 passed + 1 skip), `writeback.sh` 8/8, `wbmeta.sh` 17/17 and
  `negcache.sh` 7/7.
- **Windows** (the Windows machine plus Dokan 2.3.1): **7 suites = 75 cases in total** were run on real
  hardware - e2e 41 / cross-client 10 / write-back 6 / metadata write-back 5 (4 passed + 1 skip) /
  control-plane 7 / **prune 4** / **write-back cross-client 2**. **Acceptance with `write_back` /
  `write_back_metadata` turned on is complete too** (the dedicated suites remount by themselves). Each suite's
  result and environment are in the table below.

- **2026-09-20 (handle-context stage B)**: **5 Windows suites re-run all green** - e2e 35 /
  **cross-client 9** (the 2 cases of stage B added) / write-back 6 / metadata write-back 4 passed + 1 skip /
  control-plane 6. The Linux side is green across all 7 suites too (**crossclient 10**).
  **After adding (6) (exposing `HandleTable.Count` in the status), the same 5 suites were re-run once more and
  were all green** - because Core was touched.
- **2026-09-20 (having Core decide where an append goes)**: after `Api.AppendData` went in,
  **the 5 Windows suites were run again in full and were all green** (e2e 35 / cross-client 9 / write-back 6 /
  metadata write-back 4+1 skip / control-plane 6). **The Windows side had already closed it in stage B**, so no
  new test was added (the reproduction test is on the Linux side).
- **2026-09-20 (fixing the rewinding of `st_size`)**: after making the `st_size` of the write path monotonic
  with `GREATEST`, **the 5 Windows suites were run in full and were all green** (e2e 35 / cross-client 9 /
  write-back 6 / metadata write-back 4+1 skip / control-plane 6). **The new
  `test_x_concurrent_append_keeps_all_bytes` was not registered** - the rewinding is gone but
  **a concurrent append still loses 10 bytes** (awaiting proposal (3)), and **a red test is not left standing**.
  The function is kept, so it is enabled in the same commit as (3).
- **2026-09-20 (settling where an append goes inside the tx, proposal (3))**: **the 5 Windows suites run in
  full, all green** (e2e 35 / **cross-client 10** / write-back 6 / metadata write-back 4+1 skip /
  control-plane 6). **`test_x_concurrent_append_keeps_all_bytes` was enabled** - -4,194,304 before anything,
  -10 with only the monotonic fix, and **0** now, **green three times in a row**.
- **2026-09-21 (handle-context C-1)**: **the 5 Windows suites all green** (e2e 35 / cross-client 10 /
  write-back 6 / metadata write-back 4+1 skip / **control-plane 7**). The new `test_cp_open_inodes_counted` was
  **confirmed to fail against a build with `OpenHandle` removed**.
- **2026-09-21 (making the `FileIndex` come from the data_id)**: **the 5 Windows suites all green**
  (**e2e 36** / cross-client 10 / write-back 6 / metadata write-back 4+1 skip / control-plane 7). The new
  `test_file_index_is_data_id_and_stable` was **confirmed to fail against a build that returns `inode.Id`**.
  `test_cp_open_inodes_counted` was changed to look at **the difference from a baseline** (assuming an absolute
  0 makes it fail merely because an earlier test's handles are still in the snapshot).
- **2026-09-21 (the Core of handle-context C-2)**: **the 5 Windows suites all green** (**e2e 37** /
  cross-client 10 / write-back 6 / metadata write-back 4+1 skip / control-plane 7). The new
  `test_rename_over_open_victim_keeps_body` was **confirmed to fail against the pre-fix build**.
  **A test on the `unlink` side was written and thrown away** - Windows does not push a delete down until the
  last handle, so it was **green even before the fix** and had zero discriminating power.
- **2026-09-21 (C-3 `pgfsctl prune`)**: **there is no automated test** (making the remnants needs psql, so it
  suits a Linux-side suite). **Every path was confirmed by hand on real hardware** - the dry run separated 104
  old `mounts` rows from 1 gravestone correctly, **an `--apply` with a live mount present skipped the data
  side**, and firing it after stopping removed the artificial orphan data and the `.fuse_hidden` (verified in
  the database).
- **2026-09-21 (a new prune suite on Windows)**: **only Windows can make "real" orphan data** (on Linux, libfuse
  substitutes a rename for an `unlink` while open, so `Api.DeleteInode` is never called).
  **Confirmed to fail against a build with C-2's retention removed**, with
  `the orphan data did not grow (0 -> 0)`. Along with it, it was measured that **for 90 seconds right after a
  `kill -9` the dead row looks live and prune's data side stops**, and it was changed to **look at the liveness
  of the pid too when it is the same host**.
- **2026-09-21 (re-running the full docker runner against the current HEAD)**: **50 passed / 0 failed**
  (`tests/docker/run.sh`, a single PG, on the Linux client). **The count measured is 48 -> 50.** On the first
  run `test_ino_namespace_split` failed, but **it was the test's problem, not the FS's** - **the container has
  no `python3` and the comparison came out empty** (the value 9223372036854775934 has its top bit set = it is
  correct). **It was replaced with a `ge_2pow63` that compares in bash alone, giving 50/50.**
- **2026-09-21 (re-running the Citus suites that use docker against the current HEAD)**:
  **`test_matrix.sh` 18/18** and **`race_multinode.sh` 4/4** (on the Linux client,
  `citusdata/citus:latest`). **The e2e suite on multi-node Citus is 50/50**, and `pgfs_lock` is
  **rows=309 / 72 kB**. **Both failed at first, and in both cases the cause was the tests being out of date** -
  **they had not picked up `lock` becoming non-distributed and `mounts` being added, and expected dist=5 /
  local=1** (it is now **dist=4 (inode/data/data_chunk/audit) / local=3 (lock/settings/mounts)**).
  **`pg_tables` does not return a partition parent (`pgfs_audit` with relkind `p`)**, so the check for a table's
  existence was changed to count from `pg_class`.
- **2026-09-21 (fixing `SetFileAttributes`'s ReadOnly rebuilding the mode)**: **Windows e2e 38/38** /
  cross-client 10/10 / prune 2/2. The new `test_readonly_dir_keeps_execute` was **confirmed to fail against the
  old implementation (which rebuilt it as 0444/0644/0755)** with
  `setting ReadOnly removed the directory's execute (traverse) permission`. In the database it was confirmed to
  make the complete round trip **0755 -> (RO) -> 0555 -> (cleared) -> 0755**.
- **2026-09-21 (turning the ternaries into switch expressions; 22 places)**:
  **Windows e2e 41/41** / **control-plane 7/7** / **prune 4/4**. **The counts have not changed** (it is a
  behaviour-preserving refactor, so no new tests). The suites to run were **chosen by what was touched** -
  `StatusCommand` and `PruneCommand` changed the most, so control-plane and prune were added. `dotnet build`
  gives 0 errors, and it was confirmed that **no warnings were added in the 5 projects touched or in
  `Api.cs`**.

**The 3 that use docker (the full docker e2e / the Citus matrix / the multi-node race) were re-run against the
current HEAD on 2026-09-21 too** (the lines above). **They run on the Linux client** - the dev server has no
docker, so they cannot be run from there structurally.

> **The background (why this document was made)**: the test environments vary by host (a Windows host -> ssh to
> the Linux client / the PG server), so this is a stocktake to consider **whether they can be moved onto docker
> for reproducibility**. The analysis of dockerizing is in
> [the consideration of consolidating on docker](#the-consideration-of-consolidating-on-docker) at the end.

---

## The list of tests

| The suite | The count | What it watches | Where | The detailed README |
|---|---|---|---|---|
| **Linux e2e** | **50** | The main operations of mount.pgfs (FUSE) confirmed through real FS operations (including POSIX ACLs setfacl/getfacl / a binary round trip of xattr / a UTC round trip of the timestamps / the du of a sparse file / the consistency of a partial chunk overwrite / **the replacement of a rename-over-existing** / **the namespace split of `st_ino`** (a file = `data_id | 2^63`, a directory = `inode.Id`; the same formula as Windows's `FileIndex`)) | [tests/linux/e2e.sh](../tests/linux/e2e.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **write-back dedicated** | 9 | The durability contract of the write-back cache (`mount.write_back`). **It remounts by itself**, so it is a separate script from the e2e suite (surviving a `kill -9` after an fsync/close / the flush of a clean unmount / the back-pressure / the visibility of dirty data with the read cache disabled / a partial overwrite after a remount / **the two-phase flip of the live disable**) | [tests/linux/writeback.sh](../tests/linux/writeback.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **metadata write-back dedicated** | **27** | The contract of the metadata write-back (`mount.write_back_metadata`). **It remounts by itself and checks the database directly with psql.** The 4 of stage 1 (the consistency of the pending ledger) plus the 13 from stage 2 on = **3 for the durability contract** (an `fsync` survives / **a `close` alone disappears** = it is the contract, so "it disappears" is asserted. **That contract applies only to files this mount created** (an overwrite of an existing file is written out at the close) / an `fsyncdir` persists the pending entries directly under it) plus **5 for the synchronization heuristics** ((a) a rename-over-existing loses neither the old nor the new, x a pending source and an O_EXCL source; (b) the close of a truncated inode is synchronous, x `O_TRUNC` and `truncate(2)`; (c) an `O_EXCL` create is write-through) plus **1 regression** (a read on a still-reserved data_id does not give `-EIO`) plus **1 for the two-phase flip** plus **2 for the floor of the errors** (the blocking back-pressure / the error state's `-EIO` plus the red in `status` plus the automatic clearing. **With a fault injection that inserts a row with the same name and a mismatched kind with psql**) plus **1 for hardlink siblings** (added, the regression for A-1) plus **3 for the B-1 knob** (`defer` makes it pending / within one mount it is EEXIST / a collision does not remove the occupier but error-latches, and an unlink recovers it) plus **1 for B-2, the loss recorded in the database** (the gravestone survives / the next mount warns on the parent's stderr / removing the gravestone removes the warning) plus **1 for B-3, writing the cancellation audit at once** (the 2 create/delete rows are in the database before the unmount) plus **1 for B-4, not manufacturing a loss** (a clean unmount after turning the audit off live leaves no gravestone) plus **1 for B-5, writing the error state at once** (it lands in mounts without waiting for the heartbeat period, and so does the clearing) plus **1 for B-6, the loss report** (with the paths plus the breakdown of what was cut off) plus **1 for B-7, blocking a destructive operation** (an unlink/truncate of a persisted entry is stopped and an unlink of a pending one gets through) plus **1 for B-9, the effective mode** (the intake stop during the two-phase flip shows in the status) | [tests/linux/wbmeta.sh](../tests/linux/wbmeta.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **startup (Linux)** | 10 | The contracts of `mount.pgfs` at startup (it does not forward `-o max_write` to libfuse / it warns about an `-o` it cannot apply / `started (pid N)` is the real daemon / the fallback for an unresolved uname or gname / **it does not forward a key that breaks libfuse** / **the bundled sample `pgfs.toml.example` can be read exactly as written** - only the connection is swapped for this environment and it really mounts. **It fails if a three-level key comes back**) | [tests/linux/startup.sh](../tests/linux/startup.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **cross-client dedicated (Linux)** | **19** | The same DB-FS is mounted **twice** and the visibility is watched (**that a change of the root (`/`)'s mode is visible to the peer** (confirmed to fail with "still 755" against a build from before the fix) / a create / a delete / an overwrite / a replacing rename / **the propagation of `st_size` to hardlink siblings** / the cross-client exclusion of `O_EXCL` / **that all 4 creation paths give ENOENT after the parent is removed** / **the 2 cases of handle-context stage B** = a still-open fd points at the first inode across a rename plus a recreation of the name, and the guard for a write across a data re-pointing / **that an `O_APPEND` append lands at the real end** = the reproduction test for proposal (2). **The crux is not slipping a `stat` in on A's side**, because doing so updates the kernel's `i_size` and it passes even before the fix / **that no bytes are lost when another mount interleaves with an append larger than `max_write`** = the regression for proposal (3). **What was promised is only the total byte count**, not the order. **It fails with "expected N / actual N-M", as a difference**) / **2 cases that `.fuse_hidden*` does not appear in an enumeration** = making a real remnant and watching, as a pair, that **it does not appear in another mount's `ls`** and that **that fd can still read**, plus that **a name in a different format (one a user gave) is not hidden**). **That a dropped notification can be recovered from by "discarding the whole cache"** (the regression for M-2. **The database is rewritten directly with psql to create the state of "no notification arrived"**, and `{"r":true}` is fired with `pg_notify`. The overflow itself needs 1024 entries, so only the receiving side is watched) / **that a write-back flush does not rewind another mount's `truncate`** (the regression for `DirtyFile.WriteEnd`. **Only A is remounted with `--write-back --write-back-interval-ms 0`**. Without turning off the time trigger the window closes by itself and it is green even before the fix) / **that after another mount shrinks it, an overwrite that does not extend it leaves no bytes unreachable** (the regression for H-1. **The crux is remounting with notify OFF so that A's cache stays stale**; with it ON the truncate notification drops the cache and the window closes. **The decision is not made from B's stat** - B holding its own 0 with notify OFF is by design, so the authoritative value in the database is checked with psql). **Both mounts are started with `--notify`** (with the default off, another mount's changes are invisible. **Only the 2 cases that need notify OFF are remounted at the end**) | [tests/linux/crossclient.sh](../tests/linux/crossclient.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **handle-leak dedicated (Linux)** | **11** | The leak regression for the handle table (`HandleTable`). It looks at `handles : N open / peak M` in `pgfsctl status` Layer 3 and confirms that **every handle opened is returned** (idle is 0 / opens and closes balance / **duplicating an fd and closing only one leaves the other alive** / readdir balances / 0 after a mixed workload / peak is monotonic). **The snapshot is fired with a ping control NOTIFY**, so psql is required. **Only Linux can verify the value** (Dokan does not go through the table, so Windows is always `0 open / peak 0`). It also holds **4 cases for the body count of stage C-1** (3 separate files / **opening the same file 3 times still gives 1** / **a dup'd fd is 1 too** / a directory's fd takes it up and closing brings it back). The body count is read from the **JSON** of `{prefix}mounts.stats`, and **the text output (`N open / peak M / K inodes`) is watched by one dedicated case** (looking only at the JSON would not notice the rendering breaking) | [tests/linux/handles.sh](../tests/linux/handles.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **`prune` dedicated (Linux)** | **11** | The contract of `pgfsctl prune` (cleaning up what an abnormal exit left behind). **The default is a dry run** / **it does not remove a gravestone (`unflushedLoss > 0`)** / **the live gate** (it does not touch the data side while a mount is alive, and the old rows of mounts may be removed = the liveness decision differs per kind) / **that a living mount that merely dropped its heartbeat is not treated as dead** (the regression for H-3. Its own registration row's heartbeat is pushed back 10 minutes to create the blank band of "90 s < x < 3600 s". **The daemon rewrites it every 30 seconds, so it is fired immediately after pushing it back**) / that the old mounts rows, the orphan data and the `.fuse_hidden*` really are removed / **that a file a user named `.fuse_hidden...` themselves is not removed** (`test_prune_keeps_user_named_fuse_hidden` - the enumeration side decides strictly so it is visible in `ls`. **Removing on a prefix match would remove something that is visible**) (**the `.fuse_hidden` is fired right after a `kill -9`** - judging liveness by the heartbeat alone means **the 90 seconds right after a crash cannot be cleaned**, so this pins down that **the liveness of the pid on the same host is looked at too**). **The orphan data is made artificially with psql** (it is not born naturally on Linux, because libfuse substitutes a rename for an unlink while open; it is born on Dokan's delete-on-close path) | [tests/linux/prune.sh](../tests/linux/prune.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **negative cache dedicated** | 7 | The visibility contract of the negative lookup cache (`mount.negative_cache_ttl_ms`). **It remounts by itself and stands in for another client with a direct psql INSERT** (one's own create/mkdir/rename is visible at once / another client's is invisible within the TTL and visible after it / a readdir and a live reload sweep the markers) | [tests/linux/negcache.sh](../tests/linux/negcache.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **Linux e2e (full docker)** | 50 | The Linux e2e suite above, complete in **a single PG plus a mount container** (no ssh to the Linux client, no dependence on the host's dotnet) | [tests/docker/run.sh](../tests/docker/run.sh) | [tests/docker/README.md](../tests/docker/README.md) |
| **Windows e2e** | 42 | The main operations of assign.pgfs (Dokan) confirmed through real FS operations (the ACL projection Get/SetFileSecurity / df GetDiskFreeSpace / the 3 Windows basics added on, = the exclusive CreateNew, allocation not extending the EOF, and a rename onto the same target) / **the `FileIndex` comes from the data_id and is immutable from birth** (handle-context, added) / **a handle held open on the side overwritten by a rename can keep reading the body** (handle-context C-2, added) / **setting ReadOnly on a directory does not drop the execute (traverse) permission** (added) / **3 cases for the namespace policy** ([namespace-policy.md](design/namespace-policy.md)) = **the reserved names and the trailing spaces and dots are rejected at the Windows entry point** (both are **measured through `\\?\`** - on a plain Win32 path `NUL` resolves to the NUL device and trailing spaces and dots are dropped by the normalization, so **they never reach the FS**) / **`.fuse_hidden*` does appear in the enumeration on Windows, can be removed with `Remove-Item` and lets the parent be rmdir'd** | [tests/windows/e2e.ps1](../tests/windows/e2e.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows cross-client** | 11 | The exclusion (a simultaneous `CREATE_NEW` / a simultaneous `mkdir`) and the visibility (a create / an overwrite / a delete / a replacing rename being visible to the peer) when the same DB-FS is **mounted by two assign.pgfs instances** / **the 2 cases of handle-context stage B** = a still-open append handle seeing the peer mount's growth (the reproduction of problem 2) plus pointing at the first inode across a rename plus a recreation of the name (the guard for problem 1). The visibility cases run only when it is started with `--notify` and are SKIPped otherwise / **that no bytes are lost in a concurrent append** (appending from two mounts at once and asserting the total byte count; the order is not watched) | [tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows permission decision** | 14 | The contract of `app.enforce_permissions` (v0.2.1). **Without creating any users**, an elevated shell plants files owned by someone else through SetFileSecurity (owner Administrator = `root`, groups Administrators / Users), and they are checked with **a restricted token that has Administrators disabled**. Only other's r / no other / a named user / the owning group / **once the owner matches, only the owner's bits decide (the POSIX order)** / a create needs w on the parent / a chmod of your own file passes but a chown does not / the destination's parent of a rename / **sticky (when `-StickyDir` is given)** / elevation passes straight through / **off -> on live** / **even someone else's `0644` can be deleted with w on the parent + the read-only attribute is set only when nobody can write** (both confirmed to fail against a build from before the fix). **Confirmed that the 7 refusal cases fail against a build from before the fix** (`-AssignBinary` points it at another build) | [tests/windows/permissions.ps1](../tests/windows/permissions.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows write-back** | 6 | The durability contract of assign.pgfs with `mount.write_back` **turned on**. **It remounts by itself, kills the process and remounts** (after a FlushFileBuffers / after a close / **WRITE_THROUGH** survive, and **without a barrier it is lost** = the negative control; a clean unmount exits 0 having written everything out; a 3 MiB hash matches) | [tests/windows/writeback.ps1](../tests/windows/writeback.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows metadata write-back** | 5 | The contracts of `mount.write_back_metadata` plus the B-1 knob (`write_back_metadata_exclusive_create`) with **two real mounts** (A=`defer` / B=write-through). The exclusion within one mount is kept even under defer / the contents are lost under close-no-flush / **the occupier's contents are not removed (the requirement of B-1)** / the recovery through an unlink / **B-7's blocking of a delete during the error state** | [tests/windows/wbmeta.ps1](../tests/windows/wbmeta.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows control-plane** | 7 | Including **that the number of open bodies (`handles.inodes`) appears in the status** (handle-context C-1). The Windows acceptance of `pgfsctl config` / `status`. `config list` / `get` / **a live `set` reaching a running mount** (it arrives through the control channel even without notify) / **the effective settings** appearing on the live row of `status --json` / an EnumField rejecting a value outside the permitted set at `set` time / **the data write-back surviving a live on -> off (the two-phase flip) with the data intact** / **the metadata write-back's flip completing** (turned off while holding 300 pending entries, confirmed through the intake resuming, the pending count reaching 0 and the files being intact) | [tests/windows/control_plane.ps1](../tests/windows/control_plane.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows prune** | 4 | Watching end to end **whether C-2's body retention really does produce a remnant and whether `pgfsctl prune` can clean it up**. Overwrite the victim with a rename while it is open -> **kill without letting it close** -> orphan data remains -> `prune --apply` removes it. **The database is not looked at; the decision is made from what `prune --json` declares** (Windows has no psql, and "does what it says it found really get removed" is the contract itself). **2 regressions of "do not remove a user's file" were added** - a `.fuse_hidden_notes.txt` (the looseness of a prefix match) and a `.fuseXhidden<16 hex>` (an unescaped `_` in a LIKE) are created, and it is watched that **the scan does not pick them up** and that **they survive with their contents through an `--apply`**. **Confirmed to fail on both against the pre-fix HEAD** (the scan went from 0 to 2 and the `--apply` really removed them with `fuse_hidden_deleted = 2`) | [tests/windows/prune.ps1](../tests/windows/prune.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Windows write-back cross-client** | 2 | The contract for **when another mount writes the same body while a write-back mount is holding dirty state**. (1) **That a flush does not rewind another mount's `truncate`** (fixed on the Core side; before the fix the `st_size` was measured going back to 32) / (2) **that bytes another mount wrote into a chunk A has read are erased by A's flush** (**a contract test pinning "this is how it is now", not the desirable behaviour**. Once it is closed, **the test is rewritten** - the source of truth is [write-back.md, the cross-client contract](design/write-back.md)). **`--write-back-interval-ms 0` and writing through a P/Invoke are mandatory**, and **proceed only after confirming that the `WriteFileProxy` count in the log has grown** (with `FileStream`'s 4096-byte buffer the write does not reach the FS and it is green even before the fix) | [tests/windows/wbcross.ps1](../tests/windows/wbcross.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **The control plane** | Per scenario | A reload with notify ON, a config set with notify OFF, and the registration, statistics and snapshot of status | [control_plane.sh](../tests/docker/control_plane.sh) / [control_plane_ctl.sh](../tests/docker/control_plane_ctl.sh) / [status.sh](../tests/docker/status.sh) | [tests/docker/README.md](../tests/docker/README.md) |
| **The Citus mkfs matrix** | 27 | 18 cases of the behaviour of the combinations of `mkfs --citus / --worker / --clean` (new / keeping an existing one / rebuilding) + 2 cases of **passing instructions that only apply when creating, against an existing database** (`--citus` on a non-Citus database -> it does not become Citus / `--worker` on an existing database -> nothing is created on the workers) + 1 case of **`--clean` alone also dropping the workers' databases** + 6 cases of **`--purge` / the confirmation (`--yes`) / what is connected (`--now`)** (`--clean --purge` is exit 2 / non-interactive without `--yes` is exit 3 / with a live mount or other connections nothing is dropped without `--now` / an interactive n / y through `script`) | [tests/citus/test_matrix.sh](../tests/citus/test_matrix.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **The Citus multi-node probe** | 13 sections | A one-off probe confirming the behaviour of Citus itself (auto-sync / DDL propagation / shard placement and so on) | [tests/citus/multinode_probe.sh](../tests/citus/multinode_probe.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **The Citus multi-node race** | 4 | Phase 3's exclusion plus a multi-node e2e run, on multi-node Citus with 2 mount clients | [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **The audit log dedicated** | 12 | The behaviour specific to the audit log on multi-node Citus with 1 mount client (each op recorded / the caller_* / the partitions being created automatically = the mechanism across a month boundary / 0 rows with `audit.enabled=false`) | [tests/citus/audit.sh](../tests/citus/audit.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **df (statfs) dedicated** | 7 | The worker aggregation of `pgfs_statfs()` plus the 3 modes `require` / `auto` / `nominal`, on multi-node Citus (coord + worker1, a custom image with plperl). R5 proves the aggregation mechanism | [tests/citus/statfs.sh](../tests/citus/statfs.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus verify** | SQL diagnostics | The `citus_tables` / the distribution keys / the shard placement / EXPLAIN after a single-node Citus setup | [tests/citus/verify.sql](../tests/citus/verify.sql) | [tests/citus/README.md](../tests/citus/README.md) |

### The categories of the Linux e2e suite

Directory operations / file basics / data I/O (bytea) / renaming (**including a replacing over-existing = the
tmp+rename pattern**) / permissions (chmod/chown) / symlinks / hardlinks / xattr (**including a binary round
trip with NULs and high bytes**) / **POSIX ACLs (setfacl/getfacl)** / metadata (StatFS / utime /
**a UTC round trip of the timestamps** / **the du of a sparse file = st_blocks**) /
**the consistency of chunk writes (a partial overwrite / an append across a close / read-after-write while
unflushed / a truncate discarding the dirty data)** / concurrency / the name-resolution fallback. The coverage is
the ✅ operations of [docs/Mount.md](Mount.md).

### The categories of the Windows e2e suite

Directory operations / file basics / data I/O (bytea) / truncate / renaming / the attributes
(ReadOnly/Hidden/System/Archive) / the volume and the patterns / **the owner (the requesting account) and the
group (inherited from the parent)** / **CopyFileEx (a bidirectional Copy-Item)** / **byte-range locks (enforced
by the driver)** / **the Windows basics (the exclusive CreateNew / allocation / a rename onto the same
target)** / concurrency. Windows has link features too, but pgfs's current Dokan adapter has no entry point for
native links or arbitrary xattr so they are out of scope, and Windows-specific cases were added instead.
The coverage is the ✅/⚠️ operations of [docs/Assign.md](Assign.md).

**Running the suites leaves rows in `{prefix}mounts`**: per round it is **writeback +4 / wbmeta +1 / e2e ±0**.
That is because some suites **deliberately cause an abnormal exit** to watch the durability; it is not a fault.
Left alone, Layer 1 of `pgfsctl status` becomes unreadable, so they are removed from time to time - but
**exclude the gravestones (a non-zero `stats->>'unflushedLoss'`)**: a gravestone is the only record of what was
lost, and removing it makes it untraceable.

**The shell they run in**: `flow.cmd` / `run.cmd` **prefer pwsh (PowerShell 7) if it is present**.
Windows PowerShell 5.1 fails to load `Microsoft.PowerShell.Security` / `Utility` in some environments
(duplicate TypeData), leaving `Get-Acl` / `Get-FileHash` unusable and **3 cases FAILing for reasons on the test
side**. With pwsh those same 3 PASS.

### The results recorded in the existing documents (not from this re-run)

| The suite | The result | The verification environment |
|---|---|---|
| Linux e2e | **43 passed / 0 failed / 1 skipped (44 cases)** | Green on the dev server's Citus rf=2 (**re-run green with `write_back_metadata` both off and on after the round A fixes of 2026-09-19**. It first appeared on 2026-08-10, after Phase 0's rename atomicity plus adding `test_rename_replace_existing`). In the 43-case era it was 42/43 and **green in both `write_back` on and off modes**. The skip is `test_fallback_uname_gname` (the PG server is unreachable). In the 38-case era it was 37/38 (after the UTC timestamps plus the occupied-bytes fix), and in the 36-case era 36/36 (the Linux client) |
| write-back dedicated | **8/8 PASS** | The dev server's Citus rf=2 (2026-07-25, 2026-08-12 and green on the re-run **after the round A fixes of 2026-09-19**). It was measured that the data survives a `kill -9` after an `fsync` or a `close`. `test_close_survives_crash` is the regression for **the 1d contract with `write_back_metadata` off**, so it is kept as it is |
| metadata write-back dedicated | **27 passed / 0 failed (27 cases)** | The dev server's Citus rf=2 (after the round A fixes plus B-1 to B-9. Run with `audit.enabled = true`). The FAIL of `test_meta_truncate_syscall_close_is_synchronous` was resolved by the A-6/A-7 fixes, and the A-1 regression test `test_meta_hardlink_sibling_survives_unlink` was added. **Without passing `PGFS_PSQL=<the psql path>`, the 7 database-checking cases are skipped** (the `psql` in this environment is an alias and is not expanded in a non-interactive shell) |
| negative cache dedicated | **7/7 PASS** | The dev server's Citus rf=2 (2026-08-10 and green on the re-run **after the round A fixes of 2026-09-19**). Including the live reload (`pgfsctl config set` -> a TTL of 0 clears everything) |
| Linux e2e (full docker) | **50/50 ALL PASSED** | A single PG (postgres:17) plus a mount container, verified on real hardware on the Linux client (**re-run against the current HEAD on 2026-09-21**). **The container has no `python3`** - depending on it means "counting a correct FS as broken", so the test side must be written in bash alone. Running the `CACHE_MAX_ENTRIES=8` variant makes the new tests go down the database read-back path too |
| Windows control-plane | **7/7 PASS** | **`test_cp_open_inodes_counted` was added, making it 7 cases** (0 before opening -> 3 with 3 open -> 0 after closing; confirmed to fail against a build with `OpenHandle` removed). The key-existence check of `stats.handles` is in `test_cp_status_json_shape` too (`open` / `peak` are the FUSE-side values so they are 0 on Windows, while `inodes` works on both). The Windows machine against the server's `pgfs` schema. **A `pgfsctl config set` was confirmed to arrive even when mounted without notify** (the control channel's LISTEN is always on). It is reflected through the heartbeat snapshot, so it takes **up to one period (30 seconds)**. The settings are put back at the end of the tests. **Thanks to B-5's write-on-transition heartbeat, the flip's first phase (the intake stop) could actually be observed** |
| Windows permission decision | **14/14 PASS** (2026-09-25) (with `-StickyDir P:\`) | The Windows machine plus Dokan 2.3.1 against the `pgfs` schema of the server's development database (its root set to `1777`). **Confirmed that against a build from before the fix, given through `-AssignBinary`, the 7 refusal cases fail with "it was `ok`"**. On the same day Windows e2e 42/42, cross-client 11/11, control-plane 7/7, write-back 6/6, metadata write-back 4+1 skip (as before), prune 4/4, wbcross 2/2 and the Linux docker e2e 50/50 were green too |
| Windows metadata write-back | **4 passed / 1 skipped (5 cases)** | Two mounts, P:(defer) plus R:(write-through), on the Windows machine against the server's `pgfs` schema. **B-1's requirement (not removing the occupier) is demonstrated.** **B-7's blocking of a delete during the error state is demonstrated too** (`Api.CanDestroy` wired into 5 places in Dokan). The SKIP is `test_meta_defer_conflict_recovers_by_unlink` - **the latch bug on the Core side is fixed**, but **Windows defers the `DeleteFile` until the last handle is released**, so the delete request does not reach the FS while the test waits and **the recovery cannot be observed in this environment** (measured: the clearing was pushed back to 16 s, then 41 s, then 63 s the more it polled). The contract is verified on the Linux side. As a by-product it was confirmed that **exit 4 reaches the caller on Windows** |
| Windows write-back | **6/6 PASS** | The Windows machine with `--write-back --write-back-interval-ms 0` (no time trigger for the background flush = keeping the negative control's teeth), against the server's `pgfs` schema. **Acceptance with metadata write-back on was not done** (only the shape for running it with `-WriteBackMetadata` is prepared) |
| Windows cross-client | **10/10 PASS** | Two mounts on P: and R: on the Windows machine (both with `--notify`), against the server's `pgfs` schema. **A simultaneous `CREATE_NEW` gave exactly one success in all 6 rounds.** Without `--notify`, the 4 visibility cases "stay stale" = as specified for notify off (treated as SKIP). **The visibility of a delete became stable after it was changed to fire a `NotifyDelete`** (before that it FAILed once in three). **The 2 cases of handle-context stage B plus the 1 concurrent-append case were added, making it 10/10.** `test_x_append_handle_sees_peer_growth` was confirmed to fail against the stage A build (a loss of 11 bytes -> **6 bytes**) |
| Windows e2e | **38/38** | The Windows machine plus Dokan 2.3.1 against the server's `pgfs` schema (Citus rf=2, audit on). **`test_file_index_is_data_id_and_stable` was added, making it 36 cases.** In the old 34-case era, **the cause of the intermittent FAIL was the FCB cache on the Windows client** - although the delete was confirmed in the database's log, a file another process held a handle to could be enumerated and opened for over 10 seconds (once in three; for the details see [windows-parity.md, the measurements of the visibility of a delete](design/windows-parity.md)). The old 27/27 was 2026-06-03 |
| Linux e2e (Citus rf=2) | **42 passed / 0 failed / 1 skipped (43 cases)** | An FS distributed with `mkfs --citus --rf 2 --shard-count 8` on the dev server's shared Citus 13.1 cluster (a coordinator plus 3 workers), mounted through FUSE and run. The verification after `{prefix}lock` was turned into a Citus local table to gather the row locks in one place |
| The Citus mkfs matrix | **27/27 PASS** (2026-09-25; 18 plus 9 added) / 18/18 (re-run 2026-09-21) | Citus 14.0.0 on docker on the Linux client |
| The Citus multi-node race | **4/4 PASS** | The docker Citus on the Linux client (**re-run against the current HEAD on 2026-09-21**). The e2e suite on multi-node Citus is **50/50** / the md5s match over 4 rounds of a concurrent write race / 20x2 concurrent mkdirs always give EEXIST on one side / `pgfs_lock` rows=309, 72 kB |
| The audit log dedicated | **12/12 PASS** | Citus on docker on the Linux client |

The Windows-side write-back contracts (data and metadata) and the loss report at exit (exit 4) were
**verified** on 2026-09-19 by adding the dedicated suites
([writeback.ps1](../tests/windows/writeback.ps1) / [wbmeta.ps1](../tests/windows/wbmeta.ps1)).
**The cross-client CreateNew contention is verified in [crossclient.ps1](../tests/windows/crossclient.ps1) too**
(exactly one success in all 6 rounds). What remains unverified on the Windows side are the two of
**name resolution in a domain environment** (`CORP\alice` and `LOCAL\alice` flattening into the same `alice`)
and **the remaining work on the visibility of a delete** (the intermittent FAIL from the client's FCB cache),
and [the Windows parity design, the implementation status](design/windows-parity.md) is the source of truth for
the as-built. The acceptance criteria to be added are in
[the Windows parity design](design/windows-parity.md). Each runner's operation is authoritative in its own
directory's README.

---

## How to run them

### The Linux e2e suite

```cmd
REM The whole flow (rsync -> publish -> mount -> test -> unmount)
tests\linux\flow.cmd
REM Filtering by test name
tests\linux\flow.cmd xattr
REM The tests only (assuming it is already mounted)
tests\linux\run.cmd
```

Directly on a Linux host:

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

The options (`-NoSync` / `-NoBuild` / `-NoMount` / `-KeepMounted`) and the environment variables
(`PGFS_TEST_PG_EXEC` and so on) are in [tests/linux/README.md](../tests/linux/README.md).

### The Windows e2e suite

```cmd
REM The whole flow (mount -> test -> unmount; the build is skipped by default)
tests\windows\flow.cmd
REM Publishing first
tests\windows\flow.cmd -Build
REM The tests only (assuming it is already mounted)
tests\windows\run.cmd
```

```powershell
# cross-client (two mounts at once; it mounts P: and R: itself and cleans up afterwards)
pwsh -NoProfile -File tests\windows\crossclient.ps1
pwsh -NoProfile -File tests\windows\crossclient.ps1 -MountB S:   # give a free drive letter

# the permission decision (in an elevated shell; it mounts P: itself). sticky only when a root-owned 1777 dir is given
pwsh -NoProfile -File tests\windows\permissions.ps1 -StickyDir P:\
```

For the details see [tests/windows/README.md](../tests/windows/README.md). `flow.cmd` / `run.cmd` **use pwsh if
pwsh is present**.

### The Citus family (all run in bash on the Linux client)

```bash
bash tests/citus/test_matrix.sh        # the 18-case mkfs matrix
bash tests/citus/multinode_probe.sh    # the probe of Citus itself
bash tests/citus/race_multinode.sh     # Phase 3's exclusion plus a multi-node e2e
bash tests/citus/audit.sh              # the audit log (each op recorded / the caller_* / the partitions / enabled=false)
```

```cmd
REM Diagnosing a single-node Citus setup (a Windows host -> ssh to the PG server)
tests\citus\verify.cmd
```

For the details see [tests/citus/README.md](../tests/citus/README.md).

---

## The environment requirements

The environment each suite needs **today**. Material for considering dockerization.

| The suite | The host it runs on | The database | The mount layer | docker | Dependence on a remote host |
|---|---|---|---|---|---|
| Linux e2e | Windows -> ssh to Linux | PG (the fallback test additionally needs psql to be reachable) | libfuse3 | None | **The Linux client** (the build plus running the mount) |
| Windows e2e | Windows, locally | PG | The Dokan 2.x driver | None | None (complete locally) |
| The Citus mkfs matrix | ssh to Linux (bash) | **Citus on docker** (coord + worker1) | None (mkfs only) | **Yes** | The Linux client (the docker daemon) |
| The Citus multi-node probe | ssh to Linux (bash) | **Citus on docker** (coord + worker) | None | **Yes** | The Linux client |
| The Citus multi-node race | ssh to Linux (bash) | **Citus on docker** (coord + worker1) | libfuse3 (the mount starts **on the host**) | The database only | The Linux client |
| The audit log dedicated | ssh to Linux (bash) | **Citus on docker** (coord + worker1) | libfuse3 (the mount starts **on the host**) | The database only | The Linux client |
| Citus verify | Windows -> ssh to the PG server | **Single-node Citus on the PG server** (real hardware) | None | None | **The PG server** |

### The common prerequisites

- **The .NET 10 SDK** (at build time). The remote one is given by the `REMOTE_DOTNET` environment variable
  (the default is per host).
- **PostgreSQL 17+** (the Citus tests use an image with the Citus extension, `citusdata/citus:latest`).
- **Linux**: libfuse3 plus the `attr` package (`getfattr` / `setfattr` for the xattr tests; if it is not
  installed the xattr tests are SKIPped).
- **Windows**: the Dokan 2.x driver (`DokanSetup_redist.exe`).
- **The Citus family**: a docker daemon (each script starts it with `sudo systemctl start docker` even from a
  stopped state and puts it back through a trap).
- Remote execution requires **a passwordless SSH key login** (to the Linux client and the PG server).

### Do not go green on zero cases (2026-09-21)

**Every suite on both Linux and Windows `exit 1`s if "not one case ran" or "not one case PASSed".**
A typo in a filter (`TEST_FILTER` / `-Filter`) or a missing prerequisite that leaves **everything skipped with
an exit 0** would **look like a green pass while nothing was checked**. Skipping only some is still treated as a
success, as before.
For the details see the corresponding section in
[tests/linux/README.md](../tests/linux/README.md) / [tests/windows/README.md](../tests/windows/README.md).

### Overriding through environment variables

**Every hard-coded host name, path, port and credential can be overridden with an environment variable**
(the structure of the tests itself was not changed). When dockerizing or changing hosts, point them elsewhere
with environment variables rather than editing the scripts. The precedence is
**a CLI argument > an environment variable > the default**.

| The target | The main environment variables |
|---|---|
| Linux flow.ps1 / run.cmd | `REMOTE` / `REMOTE_REPO` / `MOUNT_POINT` / `REMOTE_SETTING_FILE` / `REMOTE_DOTNET` / `REMOTE_MOUNT_BINARY` |
| Windows flow.ps1 / run.cmd | `MOUNT_ROOT` / `ASSIGN_BINARY` / `ASSIGN_SETTING_FILE` |
| Linux e2e.sh (the fallback test) | `PGFS_TEST_PG_EXEC` (replaces the whole psql invocation) / `TEST_FILTER` |
| The Citus `*.sh` | `PGFS_PROBE_IMAGE` / `COORD_NAME` / `WORKER1_NAME` / `COORD_PORT` / `WORKER1_PORT` / `SUPER_USER` / `SUPER_PASSWORD` / `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` / `MKFS_BIN` / `MOUNT_BIN` / `PGFS_TEST_LOG` (the details are in [tests/citus/README.md](../tests/citus/README.md)) |
| Citus verify.cmd | `VERIFY_REMOTE` (the PG server by default) |

The full list of variables for each suite is in its directory's README.

### The hard-coded assumptions that remain (the ones an environment variable cannot absorb)

- The race in `dotnet publish`'s parallel restore caused by the Linux client's own configuration, where
  **the remote build target is behind a symlink**. It is worked around by building
  `-p:RestoreDisableParallel=true` into flow.ps1 (expected to be resolved by dockerizing).
- The single-node Citus setup on real hardware that Citus verify assumes (the target can be changed with
  `VERIFY_REMOTE`, but that host has to have Citus set up).

### ~~A known flake~~ -> ✅ cured at the root: the 40P01 of `test_concurrent_writes_diff_files`

**Only on Citus rf=2**, `test_concurrent_writes_diff_files`, which does 5 concurrent create+writes, failed with
`40P01 distributed deadlock` **15 to 20% of rounds** (quantified 2026-07-25; the same rate before and after the
write-back implementation = pre-existing, and it does not appear on a single PG).
**On 2026-08-10 the cause was identified and cured with a two-stage fix**:

* **The cause** (identified by measurement with 200 ms sampling of `citus_lock_waits`): Citus at rf >= 2
  serializes changes to the same shard per shard, so the tx of a write-through or a flush becomes a
  hold-and-wait that **grabs the inode shard at the head with `UPDATE inode SET data_id` (EnsureDataRow) and
  then waits for the chunk/data shard**, forming a cycle with a tx waiting in the reverse order (the most
  frequent wait was "UPDATE inode waiting on UPDATE data").
  `{prefix}lock` is innocent (no cycle can be formed from a single-row lock on the coordinator - as designed).
* **Fix 1 - normalizing the shard-touch order**: the data_id link was folded into the size/mtime UPDATE at the
  end of the tx, so that every write tx touches the shards in the order **chunk/data -> inode**
  (the link integration of `FinishWriteInodeInTx` / `UpdateInodeSizeAndMtimeInTx` / `LinkDataIdInTx`).
  Measured, the deadlocks themselves went from **33 in 60 rounds to 2 in 120 rounds (about 99% fewer)**.
* **Fix 2 - a bounded retry of 40P01/40001** (the safety net): the self-contained txs of create / write / flush
  are re-run up to 4 times with a linear backoff plus jitter (the victim has rolled back entirely, so it is
  safe). It keeps residual cycles inside Citus, such as at the COMMIT stage of a 2PC, from becoming a
  user-visible error.
* **The verification**: the reproduction loop gave **120 rounds write-through plus 60 rounds write-back = 0
  fails** (the pre-fix expectation was about 27 fails). The 2 residual deadlocks were absorbed by the retry
  (observable as a Warning in the mount log). The regressions were green: the e2e suite in both modes 43/44
  (the 1 skip is environmental) plus writeback.sh 8/8 plus negcache.sh 7/7.
* **The theoretical cycles that remain** (unobserved; the retry catches them): `DeleteInode` still goes
  inode -> chunk/data (a delete is already serialized per file by pgfs_lock, and crossing a concurrent
  create+write is rare). The multi-shard `WHERE data_id` UPDATE of a hardlink is the same.
  If they are observed, either the retry is widened or the same order normalization is applied.

---

## The consideration of consolidating on docker

> An analysis of where things stand against the motivation of "would it not be easier to put everything on
> docker". **Not started** (tied to the operational items in [docs/next.md](next.md)).

### What is already dockerized

The databases of the Citus family are already on docker (`citusdata/citus:latest` started as coord/worker with
`--network host`). `race_multinode.sh` is a hybrid of **the database on docker plus the mount on the host**.

### The Linux side can be fully dockerized -> **the single-PG configuration is implemented** ([tests/docker/](../tests/docker/README.md))

Moving `race_multinode.sh`'s mount from the host into a container makes the Linux e2e suite complete in
**a PG container plus a mount.pgfs container**. **The single-PG (non-Citus) configuration was implemented as
[tests/docker/](../tests/docker/README.md)** (a multi-stage SDK build plus docker-compose):

- **The mount.pgfs container** ([Dockerfile.mount](../tests/docker/Dockerfile.mount)): FUSE is mounted inside
  the container with `cap_add SYS_ADMIN` / `devices /dev/fuse` / `security_opt apparmor:unconfined`. A
  multi-stage build publishes `mount.pgfs` and `mkfs.pgfs` self-contained and COPYs them into a debian-slim with
  fuse3 plus attr/acl/psql.
- **The e2e suite runs inside the container**: showing the FUSE mount to the host (through mount namespace
  propagation) is fragile, so `e2e.sh` runs inside the mount container (with the mount point inside the
  container too). `tests/linux/` is brought in as a read-only bind mount (so editing a test needs no rebuild).
- **The fallback test**: [run.sh](../tests/docker/run.sh) passes
  `PGFS_TEST_PG_EXEC="psql -h coord ..."` and the database is manipulated directly with psql over the compose
  network.

That removed **the dependence on ssh to the Linux client and the symlink race workaround** for the single-PG
configuration. **It was verified on real hardware on the Linux client on 2026-06-02 with 35/35 PASS** (the
direct database INSERT path of `test_fallback_uname_gname` passed through psql over the compose network too).
What remains is fully dockerizing multi-node Citus plus the race plus the audit
([docs/next.md](next.md); `race_multinode.sh` is the template).

> **A gotcha (pinning the runtime base)**: `debian:stable-slim` currently points at Debian 13 (trixie), where
> libfuse 3.17 has bumped the SONAME to `libfuse3.so.4`. Pgfs.Fuse (the in-house binding) dlopens
> `libfuse3.so.3`, so on a trixie base `CheckDependencies` fails with "libfuse not found".
> [Dockerfile.mount](../tests/docker/Dockerfile.mount) works around it by
> **pinning to `debian:bookworm-slim` (Debian 12, libfuse 3.14 = `libfuse3.so.3`)**.

### The Windows side cannot be dockerized

Dokan is **a Windows kernel driver**, and even a Windows container cannot provide the equivalent of a FUSE mount
(Dokan's user-mode API presupposes the kernel driver). The Windows e2e suite must stay **directly on a Windows
host**. It is out of scope for dockerization.

### The recommendation for now

1. ~~Put the Linux e2e suite in `tests/docker/`~~ -> **the single-PG configuration is implemented**
   ([tests/docker/](../tests/docker/README.md)). Next is extending multi-node Citus plus the race plus the audit
   into the same frame (`race_multinode.sh` is the template).
2. Make `verify`'s hard-coded PG server overridable with an environment variable so it can be pointed at a
   docker Citus too (the same trick as the Linux e2e suite's `PGFS_TEST_PG_EXEC`).
3. Leave the Windows e2e suite assuming a host, and state that it is out of scope for dockerization.

---
