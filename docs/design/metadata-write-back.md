# metadata write-back - deferring the writes of the metadata (Phase 1e)

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **this document**
>
> **What this document is the source of truth for**: the **settled design** of deferring the writes of the
> metadata (create / the attributes / rename) and the **implementation status (stages 1 / 1.5 / 2)**.
> The pending-born principle, the ledger of pending inodes (`DirtyNamespace`), the three synchronization
> heuristics, the invariants of the flush tx and the floor of the error reporting all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | **The results of the adversarial reviews and the record of the fixes** (rounds A and B-1 to B-13). Every chronological as-built goes there |
> | [write-back.md](write-back.md) | Deferring the writes of the **file contents** (1d). 1e assumes it |
> | [data-id-lifecycle.md](data-id-lifecycle.md) | The lifetime of `data_id` (settled at create, immutable until unlink). It bears on 1e's reserved ids |
> | [audit-log.md](audit-log.md) | How the audit rows are written. 1e "captures at the time of the operation and writes at the flush" |
> | [control-plane.md](control-plane.md) | Switching `mount.write_back_metadata` at runtime, and the statistics |
> | [../Mount.md](../Mount.md) | The user-facing behaviour, and **the losses that were written down as a contract** |
> | [runtime-control-plane.md](runtime-control-plane.md) | The structure of the whole operations phase (the hub) |

## The design

### The settled design - metadata write-back: **one file = one tx, on the pending-born principle**

> What follows is **the design and the projection as settled**. Stage 2 is implemented now, so
> for the current differences, the measurements and the unfixed points see the as-built and review sections
> below. It is an extension of 1d (the write-back of the file contents).
> The design was settled with the findings of two adversarial review lenses (concurrency and cross-client
> consistency / data safety, the POSIX contract and the audit) folded in.
> **The projection benchmark showed that the win is confined to "small files x Citus"** (below; the source of
> truth is [performance.md, the 1e projection benchmark](performance.md)) - it does not pay off the way 1d does,
> and whether to implement it was a judgement call.

#### The aim - pushing rsync's remaining cost of about 2,100 metadata txs into the background

In the 1d measurements rsync (599 MB / 745 files) stopped at 1.4x. The remaining cost is the write-through txs
of `create` / `chmod` / `utimens` / `rename` (about 2,100 of them), and they block the foreground (rsync's
system calls) one at a time. This design folds one rsync file (create temp -> write -> close -> chmod ->
utimens -> rename) into **one single background flush tx** (the inode INSERT plus the data row plus the chunks
plus the attributes plus the audit).

* The number of txs: about 2,845 (about 2,100 metadata plus 745 close flushes) -> **about 750** (the per-file
  flush follows decision 4 of 1d).
* What helps more than the reduction in the number is that **the database round trips disappear from the
  foreground** (both close and rename return immediately, and the flush is pipelined in the background).
* ~~The expectation is **2 to 4x**~~ -> **the projection benchmark has been run (2026-08-10; the source of truth
  is [performance.md, the 1e projection benchmark](performance.md))**:
  **3.7x on the SQL side and about 2.1x projected onto the real world for 4 KB files on Citus rf=2**
  (because a fixed FUSE-side cost of about 10.5 ms/file remains).
  **At 768 KB it is 1.21x** (the data writes dominate), **the projection onto the reference workload
  (the rsync of 599 MB / 745 mixed files) is about 1.1x**, and **on a single local PG the absolute difference
  is 1.3 ms/file, which barely shows in the wall clock**. The design-time claim that "the metadata txs
  dominate" was an overestimate (they were about 12 s out of 179 s in reality). **1e is a countermeasure for
  "many small files x Citus / a high-latency database"**, and it does not have the general cost-effectiveness
  of 1d (6.1x) - the priority of implementing it is judged on that premise.

#### The decisions

| # | The question | The decision | The grounds |
|---|---|---|---|
| 1 | The scope of the deferral | **Pure pending-born**: only the operations against an inode this mount created and has not flushed yet (= pending) are deferred. **Every operation against a persisted inode stays write-through** (including chmod and utimens) | Deferring the attributes of a persisted inode (dirty-attrs) creates "a chmod that silently vanishes with rows=0 when it crosses another client's rename (= a DELETE+INSERT that moves the shard)" and "deferring a chmod 600 = a cross-client permission window". At the same time rsync does not need it - close no longer flushes, so the temp file is still pending when its attributes are changed and rides the coalescing. **Nothing to gain, only risk** |
| 2 | The durability contract of close | **When metadata=on, close (the FUSE `Flush`) does not flush synchronously** (it only marks). fsync and fsyncdir are the only hard barriers | Without loosening this, rename falls back to write-through and it stops at 2 txs per file = half the benefit. It becomes weaker than NFS's close-to-open, which is taken on with **an opt-in knob plus a written contract** (below) |
| 3 | The synchronization heuristics | **Three exceptions are built in from the start**: (a) rename-over-existing, (b) the close of a persisted file opened with O_TRUNC, (c) an O_EXCL create | They close by default the same shape of hole that ext4's delayed allocation hit in 2009 (below) |
| 4 | Resolving a name collision | A directory **adopts the existing id and re-points its own pending subtree at it (the inode version of Rekey)** / a file does **an explicit DELETE plus INSERT plus records the conflict in the audit**. `ON CONFLICT DO NOTHING` is forbidden | DO NOTHING drops only the inode row and orphans the data row and the chunks. Materializing two directories at once makes the loser's already-flushed subtree invisible in its entirety, so the Rekey is mandatory |
| 5 | The lock hierarchy | The order **NSGate (serializing the namespace flushes) -> DirtyFile.Gate -> the database tx** is fixed on every path. A write-through operation that refers to a pending inode also takes part in the NSGate | Without a prescribed order, the background flush (NSGate->Gate) and an fsync (Gate->NSGate) wait on each other and the whole mount hangs unrecoverably |
| 6 | The audit | **Captured at the time of the operation and batch-INSERTed in the flush tx**. `occurred_at` is the time of the operation, and even a pure cancellation records the pair | See the audit section below |
| 7 | The knobs | `mount.write_back_metadata` (bool, default **false**, requires `mount.write_back = true`) | It controls the blast radius independently of the data write-back |

#### Where the line is drawn between the operations

| The operation | How it is treated |
|---|---|
| `create` / `mkdir` / `symlink` | **Deferred** - the inode id is reserved through `IdReservation` (a second one, for `{prefix}inode.id`) and registered in the ledger and in the InodeCache as a pending inode. Nothing is INSERTed into the database |
| `chmod` / `chown` / `utimens` against a pending inode | **Coalesced** - overwritten in the pending state. Zero txs |
| A `rename` of a pending inode (with **no** existing target at the destination) | **Coalesced** - only the name and the parent are re-pointed |
| An `unlink` / `rmdir` of a pending inode | **A pure cancellation** - nothing is written to the database (the audit keeps the pair; below) |
| A rename / unlink / rmdir / chmod / chown / utimens / xattr / hardlink against a persisted inode | **write-through (unchanged)** |
| A write-through operation that refers to a pending one (a rename whose destination is a pending dir, a hardlink whose source is pending, and so on) | **The referent is materialized in the same tx** and then the operation runs |

#### The three synchronization heuristics (the lesson from ext4)

Putting close-no-flush in bare creates the structure of "**destroy the old immediately and defer the new**" -
the destructive operations on the write-through side (deleting the target of a rename-replace, an O_TRUNC) hit
the database at once while the new contents wait for the interval to be persisted, so a crash **loses both the
old and the new** (the same shape as ext4's delayed-allocation accident of 2009).

| # | The exception | What it is |
|---|---|---|
| a | **rename-over-existing** (the target really is in the database) | "Delete the target plus materialize the source plus flush the source's dirty data plus the audit" is **run synchronously in a single tx**. It protects the tmp+rename pattern of an editor saving, of `sed -i` and of dpkg. A rename with no target stays coalesced (a new tree from rsync rides that path) |
| b | A persisted file opened with **O_TRUNC** | The old chunks are gone at open time, so deferring the close leaves zero-length garbage after a crash -> **a synchronous flush at close** |
| c | An **O_EXCL create** | mkdir and an O_EXCL create are **locking primitives** such as git's `index.lock`. Deferring them lets two clients both succeed at taking the lock (a repository-corrupting class of bug) -> **a synchronous materialize**. It also closes the check-then-act window within a single client (the existence check crossing the completion of a flush) |

#### The pending ledger and visibility (within one's own client)

* A **DirtyNamespace ledger** (a working name) is held alongside the `DirtySet`. That ledger is the only source
  of truth for a pending inode, and the InodeCache stays a view that can always be restored by "re-merging the
  database and the ledger". It carries **an index from parent_id to the pending children, for fsyncdir**
  (`DirtySet` is keyed on data_id and has no reverse lookup from the parent).
* Pinning in the InodeCache is confined to "**do not evict the byId entry with the LRU**", and
  **a NOTIFY invalidation passes through for every kind** (pinning the merged children list too would make
  another client's changes invisible forever).
* `ListChildren` merges the database result with the pending children from the ledger and calls `PutChildren`.
  **It is guarded by the ledger generation** (if the generation has changed between the snapshot and the Put,
  it does not Put - the same technique as the ContentCache). The merge dedupes by name and prefers the pending
  one (which prevents a duplicate appearance right after a flush commits).
* `IsDirectoryEmpty` (rmdir) is **required to merge the pending children too** (looking only at the database
  lets a dir with pending children be removed).
* A ledger entry has the state machine **`Dirty -> Flushing -> Persisted`**. A flush writes only the snapshot
  taken at its start.
  ~~`FlushingRedirty` (the state that pushes a change arriving during Flushing to the next round)~~ ->
  **it was not adopted in the implementation**: coalescing during `Flushing` is refused, and the caller waits
  on the NSGate for the flush to finish and falls back to write-through (for the reason and the price see
  difference 1 of the 1e implementation status below). Therefore every "is coalesced" in this section and in
  the table of decision 1 is to be read as **"coalesced only while `Dirty`; write-through during `Flushing`"**.
* **After a materialize the entry stays in the ledger a while in the "persisted" state** (it is not Forgotten
  at once), to **narrow** the check-then-act window at the moment a create's existence check (overlay -> the
  database) crosses the completion of a flush. But this is **best-effort and not a basis for correctness** -
  the remnants are thinned out by a count ceiling, and the implementation review found no "correctness that
  depends on a Persisted remnant" either (an existence check can be resolved on the database side even with no
  overlay). The guarantee for O_EXCL is carried by the synchronous materialize (heuristic c).

#### The invariants of the flush tx

1. **Verifying that the ancestors are alive**: pending ancestor dirs are INSERTed in the same tx in
   parent-to-child order, and a persisted ancestor is confirmed alive by taking `{prefix}lock` (-inode_id).
   **Because the design draws no foreign keys, this is the only defence against "another client's rmdir x
   one's own pending child" creating an unreachable orphan inode.** If the parent is gone, the pending entry is
   discarded (= treated as "it never happened", the same as after a crash) plus a statistic plus a log
   (never silently).
2. **The inode INSERT comes before the data row and is always in the same tx.** The existing flush on the data
   side has a path that "discards the dirty data and commits if the inode is gone", and letting a pending inode
   through it by mistake silently throws away data that is as good as fsynced.
3. Name collisions are as in decision 4. **On a Rekey, the target_id of the pending audit rows, the ledger, the
   cache keys and the lock targets are all re-pointed too.**
4. `created_at` / `st_ctime` are **given the time of the operation explicitly** (never left to the database
   DEFAULT = the time of the flush; the same policy as 1d's mtime).

#### Making fsync / fsyncdir complete barriers (a precondition)

The durability contract of this design rests entirely on "if you fsync, it survives", so the following are
**mandatory requirements, not extensions**:

* `FlushInode` is widened to "the pending ancestors (parent to child) plus the pending inode itself plus the
  coalesced attributes and renames plus the dirty data plus the audit, in one tx". **An inode with no data
  (`DataId == null`) is materialized too** (today it returns immediately = the fsync of an empty file or a
  directory lies).
* **An override of `FSyncDir` is added** (today it is the base's -ENOSYS; since the kernel treats an ENOSYS as
  a success from then on, the standard "fsync the file then fsync the dir" is silently disabled).
* The fail-open in `FlushPath` (not being able to look up the inode gives a success with 0 = a lying fsync) is
  closed by **a reverse lookup on the data_id put into `fi.fh` at open time** (Phase 0).

#### Error reporting and the floor of durability

To make up for no longer being able to return -EIO from close, the reporting path is secured at four levels
(preventing an application that does not fsync - rsync - from losing data while exiting 0):

1. **The close of a file with a latched error is downgraded to a synchronous flush plus a report** (only the
   close of a clean file merely marks).
2. **An unmount retries with a deadline, and refuses with something like EBUSY if anything is left.** Today's
   "warn and carry on" in `FlushAll` turns into "the file itself disappears behind one log line" when
   metadata=on, so it is not acceptable.
   -> **as-built: refusing with EBUSY is not achievable** (`fusermount3 -u` and `umount(8)` complete on the
   kernel side and the FUSE daemon has no veto). It was replaced with "a deadlined retry plus an enumeration of
   what is being lost plus exit code 4" (difference 6 of stage 2 in the 1e implementation status below).
3. **N consecutive flush failures put the mount into an error state**: new writes are blocked and it shows in
   red in `pgfsctl status` through the heartbeat stats. The representative case is a permanent failure of the
   audit partition ensure (the mount role having its CREATE privilege revoked and the like) - what
   write-through would have made obvious with an immediate -EIO on the operation turns, under write-back, into
   "an operation that already succeeded can never be flushed".
4. **The back-pressure must be real**: going over the ceiling on the number of pending inodes
   (`mount.write_back_max_inodes`) "blocks writes and creates until a flush succeeds or it times out".
   A one-shot scheme that goes round once and returns keeps breaking through the ceiling if the flushes keep
   failing. A 40P01 distributed deadlock ([tests.md, the known flakes](../tests.md)) is retried with
   exponential backoff as a flush failure.

There are two preconditions at the wiring level: **a SIGTERM handler** (today it is `Console.CancelKeyPress` =
SIGINT only, so a restart through systemd never runs FlushAll; it is added with `PosixSignalRegistration`, and
on a host that shares the machine with PG the shutdown order is written down in the operations document) and
**the live off of `write_back_metadata` being two-phase** (stop accepting new pending entries -> FlushAll ->
switch the mode; in one phase, a create running concurrently with the flip is left as a pending entry with
nobody to flush it).

#### The audit

* At the time of the operation, **`fuse_get_context` (the IP/uid/uname/domain) and the time of the operation
  are captured** and held on the pending op, and they are batch-INSERTed in the flush tx. Calling `WriteAudit`
  as it is from the background flush thread turns occurred_at and the caller entirely into the flush side
  (the background thread, the flush connection) and the audit lies.
* The partition ensure is called **with the month of each row's occurred_at** (an error latch can leave pending
  entries that straddle a month).
* **Even a pure cancellation (create -> unlink) leaves the create/delete pair riding along in the next flush tx
  when `audit.enabled`.** Without it, "create, let someone read it and delete it inside the interval" becomes
  an evasion channel with zero audit trail.
* The audit `id` (allocated at flush time) and the `occurred_at` (the time of the operation) can be out of
  order with each other -> **"the audit is read by occurred_at"** is written down in
  [audit-log.md](audit-log.md) (in the implementation turn).

#### The losses that are written down as a contract (documentation required)

> ✅ **The user-facing description was put in [Mount.md, the write-back section](../Mount.md)**
> (stage 2). The table below is its origin. The reason it is off by default, the exceptions of the
> synchronization heuristics and the error reporting paths are in the same section.

| The item | What it is |
|---|---|
| The durability of close | Even after a close, the file **disappears whole** within a window of the interval (1000 ms by default) plus the flush time. Only fsync and fsyncdir are hard |
| Causality between files | Because a write-through delete or rename **overtakes** a pending create, a crash during a multi-file operation (`git checkout`, `rsync --delete` and the like) can leave "neither the old nor the new" = a state that is in no serial history. The atomicity of a single file improves, in exchange (the final name, the final attributes, the data and the audit all appear atomically, and no zero-length temp file is left behind) |
| Visibility to other clients | Invisible until the flush (the window is at most the interval plus the back-pressure). A cross-client race of two creates with the same name is last-flush-wins (the conflict is recorded in the audit and the statistics). O_EXCL keeps its guarantee because it materializes synchronously |
| du / df / status | A pending entry is not reflected until the flush. The `st_blocks` of `GetAttr` returns an estimate from the dirty buffer length (avoiding an inconsistent stat that returns st_size with st_blocks=0) |
| The relation to the existing tests | The close-durability tests of [tests/linux/writeback.sh](../../tests/linux/writeback.sh) are **kept as the regression for metadata=off**. For the on mode, separate tests are added for "what was fsynced survives / what was only closed disappears, as contracted" |

#### The precondition: Phase 0 (fixing existing bugs; worth doing independently of this design) - ✅ implemented

**Holes in the current code** that the design review found. They are worth fixing even without metadata
write-back, so they went first.
**All four are implemented and verified on real hardware** (Linux e2e **43 passed / 1 skip (44 tests)** plus
writeback.sh 8/8 plus a manual check of SIGTERM):

| # | What it is | Where | as-built |
|---|---|---|---|
| 0-1 | **rename-over-existing is not atomic**: the FUSE layer deleted the target with `DeleteInode` in **a separate tx** first and then renamed (a crash window plus an immediate DROP of the old chunks, even under write-through) | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) `Rename` | An `Api.Rename(id, parent, name, replaceTarget)` overload was added, putting **the deletion of the replaced target (through `DeleteInodeInTx`, factored out for use inside a tx) and the rename in the same tx**. The occupier is re-read inside the tx and the operation aborts if a different inode has taken its place (preventing a collateral deletion). The source's dirty data is persisted with `FlushInode` before the replacement (the first half of heuristic a). **The Dokan `MoveFile` was switched to the same overload too** (build only; the Windows e2e suite was not run). The regression test is `test_rename_replace_existing` (including the recovery of the nlink of a hardlink sibling) |
| 0-2 | **The fail-open of `FlushPath`**: not being able to look up the inode returns a success with 0 = the fsync lies when it overlaps another client's rename | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) `FlushPath` | Open/Create put **the inode id into `fi.fh`**, and when the path cannot be looked up it is reverse-looked-up by id and flushed. It returns 0 only when neither works (already unlinked = the dirty data has been discarded) |
| 0-3 | **FlushAll does not run on SIGTERM** (`Console.CancelKeyPress` = only SIGINT is wired) | [Program.cs](../../src/mount/src/Program.cs) | `PosixSignalRegistration.Create(SIGTERM, ...)` connects it to the same graceful unmount as SIGINT (LazyUnmount -> FlushAll -> deregistering from mounts). Confirmed on real hardware: kill -TERM gives the log, the unmount and the DELETE of the mounts row |
| 0-4 | **`FSyncDir` is not wired** (-ENOSYS -> the kernel treats it as a successful no-op from then on) | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) | The override was added (the metadata is write-through today, so the body is `return 0`). In 1e proper this becomes the flush point for the pending children |

#### The settings that are newly needed

| The key | What it means | The default |
|---|---|---|
| `mount.write_back_metadata` | Enabling / disabling the metadata write-back. It requires `mount.write_back = true` (turning it on alone gives a warning and is disabled) | **`false`** |
| `mount.write_back_max_inodes` | The ceiling on the number of pending inodes (going over it applies blocking back-pressure) | 4096 |

When they are added, they are reflected in [settings-matrix.md](settings-matrix.md), [Mkfs.md](../Mkfs.md) and
[pgfs.toml.example](../../pgfs.toml.example) (in the implementation turn). The number of pending inodes, the
number of name collisions and the error state are added to the Layer 3 status.

#### The order of implementation

1. **Phase 0** (the four rows above; an independent commit, with the existing e2e suite as the regression)
2. **The projection benchmark** - reproduce the "one file, one tx" shape in raw SQL and take a projected figure
   for the equivalent of rsync's 745 files
3. The body: the ledger plus the visibility (the ListChildren merge / IsDirectoryEmpty / the pinning) ->
   the flush tx (the invariants plus the Rekey) -> the three synchronization heuristics -> the floor of the
   errors (the error state / the back-pressure / the two-phase flip) -> the tests


## Implementation status (as-built)

### By stage (as-built, stages 1 and 1.5, to 11)

> Of step 3 of the order of implementation, **everything up to "the ledger plus the visibility -> the flush tx"
> is implemented** (stage 1), followed by the fixes that fold in the findings of three adversarial review
> lenses plus a code review (stage 1.5).
> **This section is the record as of stages 1 and 1.5**; for the three synchronization heuristics, the
> close-no-flush, the floor of the errors and the two-phase flip, **the stage 2 as-built below is the source of
> truth**.
> Verification: build 0 errors / Linux e2e **43 passed + 1 skip** (`write_back_metadata` both off and on) /
> [writeback.sh](../../tests/linux/writeback.sh) 8/8 / [wbmeta.sh](../../tests/linux/wbmeta.sh) 4/4.

#### The main files added and changed

| The file | Its role |
|---|---|
| [DirtyNamespace.cs](../../src/core/src/Api/DirtyNamespace.cs) (new) | The ledger of pending inodes (`byId` plus the `parent_id -> name` index / the state machine / the row snapshots / the audit rows / the generation counter / the statistics) |
| [Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs) (new, a partial of `Api`) | Making an entry pending / coalescing / the pure cancellation / the materialize / the flush tx (the invariants plus resolving a name collision plus the Rekey) / capturing the audit |
| [Api.cs](../../src/core/src/Api/Api.cs) | Dispatching each operation, the `ListChildren` merge, `IsDirectoryEmpty`, `FlushInode` / `FlushDirectory`, the statistics and the effective config |
| [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) | The pinning (only byId is exempt from eviction) plus the pending-resolution hooks (by id / by parent+name) |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | `FSyncDir` implemented for real (with the `fi.fh` reverse lookup) / `OpenDir` puts the dir's inode id into `fi.fh` |
| [Schema.cs](../../src/core/src/Config/Schema.cs) | `mount.write_back_metadata` / `mount.write_back_max_inodes` |
| [wbmeta.sh](../../tests/linux/wbmeta.sh) (new, 4 tests) | Tests dedicated to the metadata write-back |

#### The differences from the design (as-built)

1. **`FlushingRedirty` was dropped from the state machine.** The design said "a flush writes only the snapshot
   taken at its start, and a rename and the like arriving during Flushing go to the next round", but the
   implementation **refuses to coalesce during `Flushing`** (`CoalesceResult.Busy`) and has **the caller wait on
   the NSGate for the flush to finish and fall back to write-through**. The state machine has the three states
   `Dirty -> Flushing -> Persisted`.
   * The reason: re-applying (applying the difference with a write-through after the commit) takes the shape of
     **opening another tx while holding `DirtyFile.Gate`**, which is a breeding ground for the reverse edge
     (`Gate -> NSGate`) of the lock hierarchy `NSGate -> Gate -> tx`. On top of that it is never hit once in the
     e2e suite on real hardware = it becomes code that cannot be tested.
   * The price is only "one extra tx when a chmod/utimens/rename arrives during a flush". Against the lifetime
     of a pending entry (an interval of 1000 ms by default), the window during a flush is tens of ms, so the
     real harm is small.
   * A side effect: every description **that assumes coalescing always succeeds** (the table of decision 1 and
     the pending-ledger-and-visibility section above) is to be read as "coalesced only while Dirty;
     write-through during Flushing".
2. **Decision 2 (close does not flush synchronously) was not implemented in stage 1** -> **it is implemented in
   stage 2** (the stage 2 section of the 1e implementation status below). close-no-flush comes as a set with the
   hole that the synchronization heuristics (a)(b)(c) are supposed to close, and putting only one side in would
   mean deliberately opening the "lose both the old and the new" window, so all four went in together.
   -> **As of stage 1 the performance win of 1e has not appeared** (rsync's chmod and utimens come after the
   close = against a persisted inode, so they do not ride the coalescing). The ledger, the visibility, the flush
   tx, the ancestor chain and the pure cancellation are fully exercised through `mkdir` and `symlink`
   (which have no close).
3. **The flush tx looks only at "the row snapshot fixed at its start".** The lock targets, the liveness check of
   the ancestors, the search for the occupier of `(parent, name)` and the INSERT all come from the snapshot, and
   no live `Inode` is read. Reading the live one can cross a coalesced rename and **INSERT under a different
   parent that was neither locked nor checked for liveness**.
4. **A name collision with a pending sibling is EEXIST.** The design did not spell it out, but falling back to
   write-through finds no row in the database for the pending sibling, so the `ON CONFLICT` does not fire and
   **two inodes with the same name** are created, and a later flush DELETEs the winner's row, its data row and
   all of its chunks (a destruction that is impossible in principle under write-through).
5. **A name collision is resolved only between entries of the same kind.** The dir/dir case (adopting the
   existing id = Rekey) and the file/file case (an explicit DELETE plus INSERT) of decision 4 stand.
   **A mismatched kind** (a pending dir against an existing file and so on) **and a non-empty directory** are
   **refused with an error latch** (a throw), because neither a collateral deletion nor throwing data away is
   choosable.
   Memory is not rewritten inside the tx, so the Rekey, forgetting the replaced entry and the notifications are
   stacked on `PendingFlushOutcome` and applied **after the commit** (rewriting inside the tx leaves, after a
   rollback, "a pending entry holding the id of a directory that really exists", whose `rmdir` then succeeds as
   a pure cancellation without touching the database and the directory comes back on the next `ls`).
6. **The ensure of the audit partition was moved outside the flush tx.** The design only said "ensure with the
   month of each row's occurred_at", but **calling it from inside the tx hangs the whole mount**.
   Measured with `pg_locks` (PG 17.5 on the dev server): the INSERT of an audit row takes a
   `RowExclusiveLock` on the parent table, and **while that tx is open, a `CREATE TABLE ... PARTITION OF` in
   another session blocks**. `EnsureAuditPartition` fires its DDL **on a separate connection in autocommit**
   because of a Citus restriction, so calling it while one's own tx holds the parent **splits the wait graph
   across both sides, PG's deadlock detector cannot find the cycle, and with the default `lock_timeout = 0`
   it waits forever**. And this flush is under the NSGate, so **nothing but `kill -9` recovers it**.
   The remedy is (1) work out the set of months of the audit rows to be written and **ensure them all before
   opening the tx**, and (2) put `SET lock_timeout = '5s'` on the DDL (so that a broken convention throws
   instead of hanging).
   -> **This pitfall is not specific to 1e**, so it was recorded in [audit-log.md](audit-log.md) as well.
7. **The `created_at` of the `{prefix}data` row is given the time of the operation explicitly too**
   (invariant 4 applied to the data row as well).
8. **The Persisted remnants are best-effort** (already reflected in the pending-ledger-and-visibility section
   above). Once they go over the count ceiling (4096) they are thinned out oldest first.
9. **The relation between `cache_max_entries` and `write_back_max_inodes`**: a pending inode is pinned and
   cannot be evicted by the LRU, so **the effective cache ceiling is `cache_max_entries` plus the number of
   pending entries**. When `cache_max_entries` (1024 by default) is smaller than `write_back_max_inodes`
   (4096 by default), it reaches a state of "even throwing away everything unpinned does not get under the
   ceiling", and every put sorts the whole of `byId` and keeps destroying the children-list cache
   (measured: with 2000 pending entries, `evictions=979` / `childrenLists=0`, and 500 mkdirs went from 0.76 to
   0.98 seconds).
   -> The eviction is **cut off at `byId.Count - pinned.Count <= the low-water mark`**, the root is explicitly
   exempted from eviction, and **a warning is issued at startup and on a live change** (measured after the fix:
   `evictions=0`, no degradation, 0.68 seconds).
10. **Every audit row is touched through an API under the ledger lock** (Add / enumerate / Clear). Sharing an
    unsynchronized `List<T>` between the operation thread, the background flush and an fsync turns
    **a legitimate fsync into -EIO** with `Collection was modified`, and an Add running alongside a Clear drops
    audit rows. **Count ceilings** of 256 per entry and 8192 for the orphan queue were added too (so that
    repeatedly touching a pending entry with a latched error does not turn into a huge INSERT in one tx).
11. **The orphan audit rows (the create/delete pair of a pure cancellation) are drained on fsync, fsyncdir, the
    background loop and unmount.** The background loop drains the orphan audit rows **regardless of the state of
    `write_back`** (otherwise `write_back_interval_ms = 0` emits not one row until the unmount).
12. **The emptiness check of `rmdir` is rebuilt inside the tx too** (`AssertDirectoryEmptyInTx`). If a child is
    INSERTed between the check outside the tx and the DELETE, an unreachable orphan subtree is left behind (and
    under 1e the window is longer because pending entries linger). `CancelPendingInode` re-checks the pending
    children too and does not cancel if it is not empty.
13. **A discard (the ancestor was gone) gives `-EIO` on a synchronous trigger.** A `PendingDiscardedException`
    is thrown, and only the background flush path (`TryFlushPending`) downgrades it to a warning. Silently
    returning 0 makes the fsync lie.
14. **`FSyncDir` comes with a `fi.fh` reverse lookup.** `OpenDir` puts the dir's inode id into `fi.fh`, and when
    the path cannot be looked up it is reverse-looked-up by id. When another client's rename puts it in the
    state of "the dir and the pending children are alive but they cannot be looked up by the old path", looking
    only at the path returns a success without flushing a single pending child (the same shape of hole as the
    one closed on the file side in `FlushPath` in Phase 0-2).

#### The tests (done) and the ones that could not be placed

* [wbmeta.sh](../../tests/linux/wbmeta.sh) **4 tests** (new; it remounts by itself and checks the database
  directly with psql): the EEXIST of a create with the same name as a pending sibling / `inode.data_id` and the
  `{prefix}data` row being consistent after a `truncate 0` of a pending entry (plus `du` 0) / the eviction not
  running away when the pins of the pending entries overflow `cache_max_entries` (judged by `evictions`) /
  a pending create always showing up in a concurrent `ls`.
  **The discriminating power, measured**: the latter two FAIL when the corresponding fix is removed
  (`an orphan reference with 0 data rows` / `evictions=139`).
* **Two cases where a regression test could not be placed** (both fixes are kept):
  (1) the race of "a create with the same name as a pending sibling slipping through to write-through" - the
  window that gets past the existence check of the FUSE layer (`GetByPath`) does not reproduce even with an
  8-thread barrier, and even if it were hit, two dirs self-heal by adopting the existing id at flush time and
  two files are indistinguishable from the shell from "the loser of a same-name race disappears".
  (2) the ListChildren guard on the ledger generation - because `InsertInodePending` invalidates the children
  every time, it does not reproduce over 60 rounds of a concurrent `ls` even without the generation.
  -> **Neither can be judged through the filesystem, and a unit test layer is where they really belong**
  (this repository has no unit test project).

### By stage (as-built, stage 2)

> The scope of stage 2 = putting in **close-no-flush plus the three synchronization heuristics plus the
> four-level floor of the errors plus the two-phase live off, all at once**. Putting in close-no-flush alone
> would mean deliberately opening the "destroy the old immediately and defer the new" window, so the set of four
> is a precondition.
> Verification: build 0 errors (31 warnings = the baseline) / Linux e2e **43 passed + 1 skip**
> (`write_back_metadata` both off and on) / [writeback.sh](../../tests/linux/writeback.sh) 8/8 /
> [wbmeta.sh](../../tests/linux/wbmeta.sh) 4/4 plus the manual scenarios (below).
> **The automated tests for stage 2 were added** (wbmeta.sh, 16 tests; see the stage 2 test
> section below).

#### The main additions and changes

| The file | Its role |
|---|---|
| [Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs) | The two-phase flip / the error state / deciding on and marking a synchronous close / the blocking back-pressure / the deadlined retry and the loss report on unmount / the single tx of rename-over-existing / materializing a pending sibling before a write-through create |
| [Api.cs](../../src/core/src/Api/Api.cs) | `CloseInode` (the single entry point for a close) / the `exclusive` branch of a create / the synchronous-close mark of `TruncateData` / the heuristic-a hook of `Rename` / resolving the chunk size in `ReadData` / blocking writes and creates in the error state / the statistics and the effective config |
| [DirtyNamespace.cs](../../src/core/src/Api/DirtyNamespace.cs) | `HasErrorLatch` / `DescribePending` / fixing `TryReindex` so that it does not lose to a Persisted remnant |
| [DirtySet.cs](../../src/core/src/Api/DirtySet.cs) | `DescribeDirty` (for the loss report) |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | `Flush` / `Release` routed through `CloseInode` / the `O_EXCL` decision in `Create` / the preprocessing of `Rename` moved into `PrepareRenameReplace` |
| [FileSystem.cs (dokan)](../../src/dokan/src/FileSystem.cs) | `Cleanup` routed through `CloseInode` / `FlushFileBuffers` calling `FlushDirectory` for a dir / the preprocessing of `MoveFile` moved into `PrepareRenameReplace` (build only) |
| [Program.cs (mount)](../../src/mount/src/Program.cs) | An explicit `Dispose` after the unmount -> **exit 4** if anything is left unflushed |
| [StatusCommand.cs](../../src/ctl/src/StatusCommand.cs) | Displaying the error state in red |
| [Schema.cs](../../src/core/src/Config/Schema.cs) | `mount.write_back_flush_timeout_ms` added (30000 by default) |
| [Mount.md](../Mount.md) | **The user-facing contract** (the write-back section - what is lost / the three exceptions / the four reporting paths / why it is off by default) |

#### The differences from the design and the judgements (stage 2)

1. **close-no-flush writes neither the data nor the metadata.** The decision is concentrated in
   `Api.CloseInode`, and the FUSE `Flush` / `Release` and the Dokan `Cleanup` delegate to it. When
   `write_back_metadata` is false, the 1d behaviour (a synchronous flush at close) is **kept strictly**
   (the close-durability tests of writeback.sh are that contract).
2. **Heuristic (a) really could be made a single tx** - but **only when the source is pending**.
   The mechanism is "re-point it at the replacement name on the ledger first (zero txs) and then flush it
   synchronously". A pending entry has no row in the database, so the rename is nothing but *the choice of the
   name at INSERT time*, and the file/file collision resolution of the flush tx (the explicit DELETE plus
   INSERT) becomes **an atomic replacement** as it is. Measured (on the dev server): one `mv tmp target` makes
   the replaced row, its data row and its chunks disappear, and the new inode plus the data row plus the chunk
   plus 4 audit rows appear in one tx.
   * **A persisted source is still 2 txs** (`FlushInode` -> the rename). But the order is "commit the new data
     -> commit the target deletion plus the rename", so **there is no loss window** (even on a crash the old
     target is alive and the source remains under its old name). What is lost is only the atomicity (the moment
     when the temp file is visible) and the speed of one tx.
   * **Replacing a directory is out of scope** (a collateral deletion of a non-empty dir, and dir-against-dir
     needing the separate judgement of adopting the existing id). What an editor saving, `sed -i`, dpkg and
     rsync hit is only the replacement of a file.
   * As a side effect, **`TryReindex` losing to a `Persisted` remnant** was fixed. Before the fix, a rename to
     "a name this mount had materialized once" always fell back to write-through and (a) **never fired once on
     real hardware**. The remnants are "best-effort narrowing of the existence-check window", not a basis for
     correctness (the row is in the database, so `ls` and `stat` can be resolved on the database side), so when
     one gets in the way it is taken out of the ledger and the operation carries on.
   * The audit was made **one deletion = one row**. `writeAudit: false` was added to `DeleteInodeInTx`, and the
     delete of a replacement writes only the capture-time side (`reason: write_back_metadata_rename_replace` /
     `replaced_by`) (writing both emits the same deletion twice, and the background flush thread cannot get the
     caller anyway).
   * **Invalidating the cache of the hardlink siblings** of the inode removed by the replacement was added
     (stage 1 threw it away with `out _` = `st_nlink` went stale).
3. **The mark of heuristic (b) is per inode, not per fd.** The design said "look at `fi.flags & O_TRUNC` in
   `Open` and set the mark", but the implementation sets it **inside `Api.TruncateData`**. An open with
   `O_TRUNC`, a `truncate(2)` and an `ftruncate(2)` all go through the same path, so one place catches them all
   (FUSE sometimes sends the O_TRUNC as a separate syscall, which an implementation that looks at fi misses).
   The mark is cleared by a successful flush, with a ceiling of 4096 entries.
   -> It is wider than the design (a shrinking truncate is covered too), which is deliberate because what is
   lost is the same.
4. **Heuristic (c) is a write-through create, not "make it pending and materialize at once".** With an
   immediate materialize, if another client has already INSERTed the same name, the collision resolution of the
   flush tx (file/file = DELETE plus INSERT) kicks in and **both O_EXCL creates succeed** = the meaning of the
   lock is gone. Leaving the decision to the database's unique constraint (= `ON CONFLICT DO NOTHING` returning
   0 rows -> EEXIST) is the only correct shape.
5. **`mkdir` stays deferred (pending)** - the reviewer's ruling (that mkdir should materialize synchronously
   too) was **argued against**. The reasons: (1) making `mkdir` synchronous means **a pending directory can
   never come into existence in principle**, which makes the same-tx INSERT of the ancestor chain (invariant 1)
   and the adoption of an existing id for dir/dir (the Rekey of decision 4) - the core of 1e - into
   **unreachable code** (leaving code that cannot be tested is the same judgement as dropping `FlushingRedirty`
   in stage 1); (2) a `mkdir` lock is a convention rather than a POSIX guarantee, and the locking primitives
   that actually exist (git's `index.lock`, dpkg, `lockfile`) are almost all `O_EXCL` creates; (3) even if it
   fails, the filesystem is not damaged (dir/dir self-heals by adopting the existing id at flush time, and what
   is lost is only the mutual exclusion at the application layer); (4) the current regression test (the pin
   verification in [wbmeta.sh](../../tests/linux/wbmeta.sh)) is written with a pending directory.
   **It was aligned with the table in the section on where the line is drawn (mkdir = deferred), and of the
   description in the synchronization-heuristics table that "mkdir and an O_EXCL create are locking
   primitives", the mkdir half was not taken.**
   The loss is written down in the contract table of [Mount.md](../Mount.md) as "a tool that uses `mkdir` as a
   lock can have two clients succeed at once".
   **Switching it is one line** (`exclusive: false` -> `true` in `Api.CreateDirectory`), so that is the only
   place to touch if the policy changes.
6. **The implemented shape of the four-level floor of the errors**:
   | # | The detection | The blocking | The report |
   |---|---|---|---|
   | (1) The error latch | `DirtyFile.Error` / `PendingInode.Error` / the error state (`RequiresSyncClose`) | None | The close is **downgraded to a synchronous flush** and gives `-EIO` (only the close of a clean file merely marks) |
   | (2) The unmount | `Api.Dispose` -> `FlushAllForShutdown` looks at `UnflushedCount()` | Retries with exponential backoff up to the deadline of `mount.write_back_flush_timeout_ms` | If anything is left, **up to 32 "pending inodes and dirty data about to be lost" are enumerated** in an Error log plus `mount.pgfs` **exits 4** |
   | (3) The error state | 5 consecutive flush failures (`NoteFlushFailure`; **a discard does not count**) | `-EIO` at the head of `WriteData` and `InsertInode` | An Error log plus `writeBack.errorState` in the heartbeat stats -> **a red `!! write-back ERROR STATE`** in `pgfsctl status`. One successful flush clears it automatically |
   | (4) The back-pressure | `PendingCount > write_back_max_inodes` | **Blocks until it is back under the ceiling** (the same setting is the deadline; it backs off exponentially when there is no progress) | Going over the deadline gives a warning and carries on (blocking forever is indistinguishable from the mount hanging) |
   * **The design's "the unmount refuses with something like EBUSY" is not achievable**, so it took the shape of
     (2) above: `fusermount3 -u` and `umount(8)` **complete the unmount on the kernel side** and the FUSE daemon
     has no veto. An implementation that keeps waiting only creates "a mount that can never be unmounted", so it
     was replaced with **a deadline plus an enumeration of what is being lost plus an exit code** (observable
     through `--foreground` or systemd).
   * (3) surfaces not only the "permanent failure of the audit partition ensure" that the design raised but also
     **the "infinite retry of a mismatched-kind collision or a non-empty-dir collision" that stage 1.5 left
     behind** (confirmed on real hardware: create a mismatched-kind row with psql, fsync x 5 -> the red display
     plus new writes giving `-EIO` -> delete the row and fsync -> cleared automatically).
7. **The live off is two-phase.** `metadataIntakeClosed` (volatile) is raised to make `MetadataWriteBack`
   effectively false (= new creates go write-through), the pending entries in hand are written out with
   `FlushAll`, and then `config.Mount.WriteBackMetadata` is dropped.
8. **Two holes closed along the way** (ones that close-no-flush makes easier to fall into):
   * **The window where a write-through create or hardlink crushes a pending sibling**:
     `MaterializePendingChildLocked` materializes the pending entry occupying `(parent, name)` first, and
     **holds the NSGate until the INSERT completes** (the fallback when an id reservation fails, a create during
     the two-phase flip, `O_EXCL` and `ln` all apply).
   * **`ReadData` failing on a reserved data_id**: it called `LoadChunkSize` (`QuerySingle`) with no
     `{prefix}data` row and turned it into `-EIO`. Under 1d the close always flushes, so it never showed, but
     with close-no-flush "reading a file that has been closed but has no row in the database" becomes
     **an everyday occurrence** (measured: 12 tests FAILed). `ResolveChunkSizeForRead` resolves it in the order
     database -> ledger -> the default.
9. **One new setting**: `mount.write_back_flush_timeout_ms` (30000 by default). The blocking ceiling of the
   back-pressure and the flush deadline of the unmount were made **the same knob** (both are "how long it is
   acceptable to wait for a flush to succeed", so there is no reason to separate them).

#### The scenarios confirmed by hand (the dev server, the schema `pgfs_test`)

With `--write-back --write-back-metadata --write-back-interval-ms 0` (the background flush disabled = the
judgements are deterministic), the behaviour through the filesystem was compared against the database state in
psql. Automating the tests starts from this list:

1. **close-no-flush**: `echo hello > f` -> no inode row in the database even after the close / `cat` can read it
   -> the row appears on `fsync`.
2. **(a) rename-over-existing**: a persisted target plus a pending temp -> one `mv` swaps exactly one row
   (the old data row and chunks are gone and the new total_size is 11), and the log says it materialized in a
   single tx. The audit has 4 rows - create(temp) / rename / delete(reason=rename_replace) - with the caller's
   uname being the operator.
3. **(b) O_TRUNC**: open a persisted file with `O_TRUNC`, write and **only close** -> the `st_size` in the
   database is updated at once (= downgraded to a synchronous flush) plus the downgrade is recorded in the log.
4. **(c) O_EXCL**: the row is in the database right after an `O_EXCL` create (before the fsync). An ordinary
   create and a `mkdir` have no row and do show up in `ls`.
5. **The two-phase off**: `pgfsctl config set mount.write_back_metadata false` -> all 3 pending entries appear
   in the database.
6. **The error state**: create a row with the same name and a mismatched kind with psql -> 5 fsyncs give `-EIO`
   -> new writes give `-EIO` too -> a red ERROR STATE (with the reason) in `pgfsctl status` -> delete the row
   and fsync -> it clears and the writes come back.
7. **The back-pressure**: create 40 files with `--write-back-max-inodes 8` -> it does not hang and finishes in
   0.37 seconds, with 40 entries in `ls` and 33 rows in the database (the remaining 8 or fewer are pending).
8. **The unmount**: in the normal case every pending entry lands in the database and it exits 0.
   **When pending entries that cannot be flushed are left** (`--write-back-flush-timeout-ms 2000` plus a
   mismatched-kind collision), it persists for 2 seconds and then enumerates "the pending inodes about to be
   lost: id/parent/name/size/error" and "the dirty data about to be lost" in an Error log and **exits 4**.
9. **A hardlink does not crush a pending name**: an `ln` onto a pending name gives EEXIST.
10. **status**: `write-back(m): on / pending N of 4096 inodes / … / conflict / cancel / discard` plus
    `mount.write_back_flush_timeout_ms` in the effective config.

#### The stage 2 tests (added, wbmeta.sh from 4 to 16 tests)

The manual scenarios above were automated in [wbmeta.sh](../../tests/linux/wbmeta.sh) (the breakdown table is in
[tests/linux/README.md](../../tests/linux/README.md)). The result **at the time was 15 passed / 1 failed**
(plus the regressions: [writeback.sh](../../tests/linux/writeback.sh) 8/8, and the Linux e2e suite 43 passed +
1 skip with `write_back_metadata` both off and on).

> **✅ That 1 failure was resolved by A-7.** `wbmeta.sh` is now **green on all 27 tests**
> (confirmed by a re-run on 2026-09-21). What closed it is
> [A-6 / A-7 in metadata-write-back-reviews.md](metadata-write-back-reviews.md), which
> **changed the condition for clearing the mark to "only when something unflushed was actually written out"** -
> a close that wrote nothing does not clear it, so **the close on the `truncate` side no longer consumes the
> mark**.
> What follows is the record of **what that defect was**.

* **The 1 FAIL at the time was a reproduction test for an unfixed defect** -
  `test_meta_truncate_syscall_close_is_synchronous`. The mark of heuristic (b) is set per inode in
  `Api.TruncateData`, but **what consumes the mark is "the close of the fd that issued the truncate"**, so
  `truncate -s 0 f; cmd >> f`, where the `truncate(2)` and the write are on different fds (coreutils'
  `truncate` does open -> ftruncate -> **close**), slips straight through. Measured: `truncate -s 0` makes the
  `st_size` in the database **0 synchronously**, and the following append stays pending -> a `kill -9` leaves
  **zero-length garbage** (both the old and the new are lost).
  The very window that the scope of stage 2 forbade - "destroy the old immediately and defer the new" - is open
  on this path.
  -> The proposed remedies: **keep the mark on the inode until a flush succeeds** rather than clearing it on one
  close, or move the truncate itself onto the pending side. **It is also possible to read it as being within
  the contract of close-no-flush (a close is not a barrier)**, so a ruling was needed on whether the zero-length
  garbage is acceptable.
  -> **Settled**: A-6 / A-7 in the round A fixes below changed it to "the mark is cleared only
  when something unflushed was actually written out" and closed the window (it was not accepted). This test
  turned green and `wbmeta.sh` became 17/17.
* Incidental facts the tests turned up (they affect how the tests are written):
  * **`cp` opens a new destination with `O_CREAT|O_EXCL`** (confirmed with strace) -> heuristic (c) makes it
    write-through and it does not become pending. `> file` comes with `O_TRUNC`.
    **To create a pending entry, use a create with neither `O_EXCL` nor `O_TRUNC`**
    (python's `os.open(p, O_CREAT|O_WRONLY)`) or a `mkdir`.
  * When the destination of an `mv` **does not exist**, (a) does not fire and it stays pending (no row appears
    in the database) = the evidence for the discriminating power of the test for (a).
* **What was not automated**: the latter half of manual scenario 8 (the unmount deadline plus the loss report
  plus exit 4). `mount.pgfs` forks itself and becomes a daemon, so **a test script cannot get its exit code**
  (the same through `ssh dev sudo` or through mount(8)). It could be obtained by starting it with
  `--foreground` and holding the PID, but that requires building a separate mechanism in the script for the
  unmount and the rendezvous, so it has not been started.

#### The holes that remain after stage 2
* **A rename-over-existing with a persisted source is 2 txs** (there is no loss window, but it is not atomic).
  Making it one tx requires rebuilding it so that the data flush rides in the rename tx.
* **A rename-over-existing of a directory is out of scope for the single tx** (it stays 2 to 3 write-through
  txs). Replacing a pending target is 3 txs too ("materialize the target and then delete it") - it ought to be
  foldable into a pure cancellation plus a coalesced rename, but that is not implemented.
* **The back-pressure on the data side (`write_back_max_bytes`) is still one-shot.** Only the count side was
  made blocking (so as not to change the 1d behaviour). The byte ceiling has the same property of being broken
  through repeatedly when the flushes fail.
* **The threshold of 5 for the error state is a constant** (not a setting). Hitting it deliberately from a test
  requires creating a colliding row with psql.
* The extra lock taken on an adoption (dir/dir adopting an existing id) comes after the single ascending batch
  acquisition (it is left to the 40P01 retry).
* **The Dokan side is verified on real hardware in the on mode too** - `CloseInode` /
  `FlushDirectory` / `PrepareRenameReplace` are wired, and on top of the regression with the default off,
  **[wbmeta.ps1](../../tests/windows/wbmeta.ps1) passes the on mode with two real mounts**
  (4 passed + 1 skip; the skip is observing the recovery through an `unlink`, which cannot be observed in this
  environment because Windows defers the `DeleteFile`). [tests.md](../tests.md) is the source of truth for the
  counts.
* The cross-client visibility of `mkdir` (the ruling of difference 5 above).


## The record of changes

The chronological record is held by [metadata-write-back-reviews.md](metadata-write-back-reviews.md)
(the record of the fixes from rounds A and B of the adversarial reviews runs to over 700 lines, so it was carved
out).
**Every other chronological entry goes here.**

- [runtime-control-plane.md](runtime-control-plane.md) had swollen to 1,802 lines, so it was
  split by feature and this document was carved out of it. The contents are as they were before the split.
  The review record was further split out into
  [metadata-write-back-reviews.md](metadata-write-back-reviews.md).
