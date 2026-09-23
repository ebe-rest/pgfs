# write-back - deferring the writes of the file contents (Phase 1d)

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **this document**
>
> **What this document is the source of truth for**: the design, the implementation status and the record of
> changes of the write-back cache for the file contents (`{prefix}data_chunk`). How a dirty chunk is
> represented, the granularity of a flush, reserving `data_id` in blocks, the `mount.write_back` knobs and the
> two-phase live disable all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [metadata-write-back.md](metadata-write-back.md) | Deferring the writes of the **metadata** (1e). The ledger of pending inodes and the namespace |
> | [cache.md](cache.md) | The caches on the **read** side (1a / 1b / 1c) |
> | [control-plane.md](control-plane.md) | The route that switches `mount.write_back` at runtime, and how the statistics are published |
> | [performance.md](performance.md) | The measured numbers (6.1x and the rest) and the whole list of improvement candidates |
> | [settings-matrix.md](settings-matrix.md) | The defaults and the reload policy of `mount.write_back*` |
> | [../Mount.md](../Mount.md) | The user-facing behaviour and the durability contract |
> | [runtime-control-plane.md](runtime-control-plane.md) | The structure of the whole operations phase (the hub) |

## The design

### The settled design - the write-back cache: **assemble a chunk in memory and write it exactly once**

> What follows is **the design as settled before the implementation**. For the differences from what is in the
> tree now see the 1d implementation-status section below. **The source of the win was measured
> again** with a raw-SQL benchmark, and the design decisions were settled.
> [performance.md](performance.md) is the source of truth for the measured values.

#### A correction to the starting point - the main cause is not "the number of txs" but "growing the same row over and over"

The first version of this memo said "Citus is slow because one FS operation is one distributed transaction, so
**reducing the number of txs** is the only road". **A follow-up measurement corrected it** (for the details see
[performance.md, the real main cause](performance.md)):

| The shape of writing 1 MiB of file data | A single PG | Citus rf=2 |
|---|---|---|
| **The shape today**: 128 KiB x 8 partial UPSERTs, each in its own tx | 48.9 ms | **621.9 ms** (= 1.61 MB/s, which agrees with the 1.62 measured through rsync) |
| **write-back**: assemble 1 MiB in memory -> one statement, one tx per file | 7.5 ms | **77.1 ms** (12.98 MB/s) |
| The ratio | **6.5x** | **8.1x** |

* The breakdown: **the amplification contributes 7.5x** (480.8 ms for 8 partial UPSERTs against the same row
  versus 63.7 ms for writing 8 separate rows), against **a 17% contribution from the number of commits**
  (12.6 ms/commit x 8). **The bulk of the win is removing the amplification.**
* rsync was slow because it "grows a 1 MiB chunk with 8 UPSERTs of 128 KiB each" = **bytea is subject to TOAST,
  so even a partial update rewrites the whole TOAST chain every time**. `dd bs=1M` was fast because it was
  already writing in one go.
* **write-back is therefore not a Citus-only countermeasure.** A single PG gets 6.5x too.

#### The decisions

| # | The question | The decision | The grounds |
|---|---|---|---|
| 1 | How a dirty chunk is represented | **A full chunk buffer plus a list of written ranges (extents)** | It is the substance of removing the amplification. The read path stays `ContentCache.Get` (zero branches) |
| 2 | The shape of the SQL at flush time | **One dirty chunk = one statement.** A full replacement `payload = $1` when it covers the whole chunk, and **the same overlay UPSERT as today, once** when it covers part of it | Against an existing 1 MiB row, an overlay is 24.8 ms, a full replacement 29.4 ms and a full read 0.4 ms. **There is no need to read it just to turn it into a full replacement** |
| 3 | Allocating `data_id` | **Reserving blocks in advance** (below) | It is what lets even a new file key its dirty state on `data_id` = the premise of decision 1 holds. The synchronous tx disappears |
| 4 | The granularity of a flush | **Per file by default.** Batching across files is **a future knob** (implemented but off by default) | Batching across files adds only **+8.8%** (63.6 -> 58.0 ms/MiB). It does not pay for the wider loss window and the longer lock hold |
| 5 | Optimizing by aligning the shard groups (grouping by the starting directory and so on) | **Not taken** (kept as a design record) | **The number of participants in the 2PC = the number of workers touched**, bounded by the number of nodes. Aligning drops PREPARE from 3 to 2, but **the difference in time is below the measurement limit**. Re-evaluate if the number of workers grows |

#### Decisions 1+2: how a dirty chunk is represented and the shape of a flush

No third cache is created. **A dirty state is added to the `ContentCache` entry, a dirty field is added to the
`InodeCache` entry, and one `DirtySet` is held that ties the two together.**

```
DirtySet (one per mount)
├─ files: data_id → FileDirty {
│      chunks: chunk_index → { byte[] buf, List<(off,len)> written, bool full }
│      truncateTo?      // a truncate is held as the boundary "drop every chunk past here"
│      occupiedDelta    // Σ (the change in payload length) - folded into one UPDATE at flush time
│      state            // Clean | Dirty | Flushing | FlushingRedirty
│      error?           // latched if a flush fails (returned on the next fsync/flush/release)
│  }
└─ inodes: inode_id → { size?, mtime?, ctime? }
```

* **Why the `written` extents are held**: to tell "a full chunk was assembled" from "only part of it was
  written", so that the flush can **reproduce today's semantics**. A full replacement when it is the whole
  chunk, one overlay when it is part of it. Without them, a partial write into a sparse region turns a
  **hole (no row)** into a **zero-filled chunk**, which changes the meaning of `total_size` / `st_blocks`
  ([database.md, the occupied bytes and st_blocks](database.md)).
* **The database is not read even when the chunk is not in hand on a partial write.** The extents are there, so
  writing an overlay is enough (a read is a cheap 0.4 ms, but **not reading at all is simpler**).
* **The dirty state of one file is always flushed in one tx.** No intermediate state where the size and the
  contents disagree is created.
* **Ordering**: a `truncate` is treated as a boundary that cuts the extent log (with only a final-state map,
  the order of truncate -> write breaks).
* `occupiedDelta` **can be settled at flush time without a `SELECT length(payload)`** - the old payload length
  is in hand. Today's per-chunk `SELECT length(payload)` ([Api.cs](../../src/core/src/Api/Api.cs)
  `WriteChunkSlice`) disappears.

#### Decision 3: reserving `data_id` in blocks in advance

Today `EnsureDataRow` allocates a `data_id` by INSERTing into `{prefix}data` in **a synchronous tx** on the
first write (on Citus that is an inode lock plus an INSERT plus an inode UPDATE, tens of ms). write-back wants
**to key the dirty state on `data_id`** (the premise of decision 1), so **the allocation must cost no database
round trip**.

`{prefix}data.id` is a `BIGSERIAL` = a sequence, so **a batch of them can be taken in advance** (measured: it
works on a Citus distributed sequence too):

| The method | Measured | Serialization |
|---|---|---|
| `SELECT nextval(seq) FROM generate_series(1,1000)` | **4.0 ms / 1000 ids** | **Not needed** (each nextval is atomic; the ids only have to be unique, not contiguous) |
| Reserving `[min,max)` with `WITH a AS (SELECT n, nextval(seq) min) SELECT min, setval(seq, min+n)` | 1.4 ms / 500 ids | **Needed** (concurrent reservations overlap) |

**The former is taken (no lock, N unique ids).** There is no need for a contiguous range, and it is just a
`long[]`, so even ten thousand of them is 80 KB. The reservation **grows with demand** (when it runs out, take
more next time, up to a ceiling - the standard trick of reserving ids in blocks and handing them out from
memory). **If it is exhausted, take more synchronously** (= at worst, the same cost as today).

* **The INSERT of the `{prefix}data` row itself rides along in the flush tx** (INSERT with the reserved id
  given explicitly). -> The "+1 tx on the first write" of a new file disappears.
* **Unused reserved ids are thrown away** (at process exit). It only leaves holes in the sequence; harmless.
* **Contention across clients is guarded by the same procedure as today**: inside the flush tx, take the inode
  lock and re-read `data_id`, and **if another client has already settled a different `data_id`, move your own
  chunks over to it** (the payload is in hand, so moving them is possible). The same semantics as today's
  "lost race -> use the existing one" in `EnsureDataRow`.
* The same machinery can later be used for `{prefix}inode.id` too (= the groundwork for making `create`
  write-back; not done this time).

#### The operations put on write-back, and the ones not put on it

| The operation | How it is treated | Why |
|---|---|---|
| The file contents (`write`) / the size change of a `truncate` | **write-back** | They are the most numerous, and FUSE allows the delay up to `fsync` |
| `st_size` / `st_mtime` / `st_ctime` / `total_size` | **write-back** (together with the contents) | They become inconsistent if they are not visible together with the contents |
| `create` / `unlink` / `rename` / `mkdir` / `link` / `symlink` | **write-through (unchanged)** -> **extended to deferral in 1e, for pending-born ones only (settled; see [metadata-write-back.md](metadata-write-back.md))** | They bear directly on the visibility of the namespace, the audit log and the semantics of `{prefix}lock`. They are also infrequent |
| `chmod` / `chown` / `xattr` | **write-through (unchanged)** -> **write-through is kept even in 1e for a persisted inode** (they are coalesced for a pending inode) | Permissions should not be deferred |

**No effect on the audit log**: what [audit-log.md](audit-log.md) records is only the metadata operations
(create/delete/rename/chmod/chown/hardlink), and all of them stay on the write-through side.

#### Guaranteeing consistency and durability (the substance of the implementation)

1. **A read sees the dirty state**: the dirty payload sits in the same `ContentCache` entry, so the read path
   sees the latest with no change at all (= read-after-write holds). The stale guard through `Generation`
   keeps working as it is.
2. **A synchronous flush on `fsync` / `Flush`(close) / `release`** <- **a precondition: these are currently not
   implemented.** [src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs) does not override `FSync` /
   `Flush` and returns the base's ([FuseFileSystemBase.cs](../../src/fuse/src/FuseFileSystemBase.cs)) `-ENOSYS`.
   **The kernel treats an ENOSYS from fsync as a success and as "no_fsync from here on"**, which is harmless
   while everything is write-through, but **the moment write-back is turned on, `fsync(2)` starts lying**.
   The wiring on the binding side already exists ([FuseMount.cs](../../src/fuse/src/FuseMount.cs) `_fsync` /
   `_flush`), so **implementing the overrides is a mandatory item of 1d**. On the Dokan side
   `FlushFileBuffers` / `Cleanup` / `CloseFile` are already there, so the receiving end is in place
   ([src/dokan/src/FileSystem.cs](../../src/dokan/src/FileSystem.cs)).
3. **The triggers of a flush**: (a) going over the dirty-byte ceiling, (b) a time window,
   (c) `fsync` / `Flush` / `release`, (d) unmount / `Api.Dispose`, (e) a control message from `pgfsctl`
   (optional).

   > **⚠ The (e) that was originally there - "when a NOTIFY from another client brings that `data_id`, flush
   > first and then invalidate" - is not implemented. The implementation and the document were
   > compared, and this line was the one that was dropped.**
   > `Api.OnRemoteChange` only calls `contentCache.InvalidateData(dataId)` and **deliberately leaves the dirty
   > state alone**.
   >
   > **It was confirmed by measurement that adding it would not fix the cross-client trampling either.** The
   > order in which A writes its own image back after B has written does not change, so **flushing earlier
   > leaves the final byte sequence the same**. Fixing it requires **changing what is flushed, not flushing
   > earlier** (see the cross-client contract below).
> ### The cross-client contract (measured on both operating systems on 2026-09-21)
>
> **A dirty buffer is the complete image of a chunk** (the (3) of `Api.LoadChunkBase` - on a partial write the
> full chunk is read to serve as the base). **A flush writes that whole image back** (`WriteFullChunkFlush`).
> Therefore **while a mount with write-back enabled is holding dirty state, a write to the same body from
> another mount disappears at the flush**. What disappears is **whole chunks, not individual bytes**, and the
> target is **every chunk A has ever read**.
>
> Measured (A = `write_back` on, B = write-through; the default 1 MiB chunk):
>
> | The scenario | The result |
> |---|---|
> | B overwrites the whole file -> A flushes | **B's write disappears entirely** |
> | B writes a region A has not written -> A flushes | **B's write disappears** (it is inside A's image) |
> | A writes 4 bytes across a 1 MiB boundary -> B writes another chunk -> A flushes | **B's write disappears across the two chunks (2 MiB) that were straddled** |
>
> **Only the rewinding of `st_size` was closed,** (`DirtyFile.WriteEnd`). Before that,
> **a flush rewound another mount's `truncate` and the bytes that should have been gone could be read again**.
>
> **The trampling of the bytes is unfixed.** Closing it requires holding the dirty state as
> **the byte ranges actually written** rather than as a complete image, which is a design change.
> **Until then the contract is "do not use write-back if the same file is written from several mounts".**

4. **Deferred error reporting**: with write-back, `WriteData` can no longer return a database error.
   **The error is latched per file and returned as `-EIO` on the next `fsync` / `Flush` / `release`**
   (the same contract as NFS). The latch is cleared once it has been returned.
5. **Concurrency**: `SupportsMultiThreading => true`, so **a write can arrive for the same file during a
   flush**. A state machine `Clean -> Dirty -> Flushing -> (a write arrives) FlushingRedirty -> Dirty` is held,
   and **a flush writes only the snapshot of the dirty state as of its start; whatever arrives during Flushing
   goes to the next flush**.
6. **Lock ordering**: when batching across files is enabled, **pass every target to a single `LockTargets`**
   ([Api.cs](../../src/core/src/Api/Api.cs) - it sorts its input ascending, which fixes the order). Taking them
   file by file can deadlock with several clients.
7. **Back-pressure**: the dirty state is **a separate account** from the read budget (`cache_data_max_bytes`)
   (dirty data cannot be dropped, so it is not subject to the LRU). **When the ceiling is reached, writes block**
   and wait for a flush (it must not grow without bound).
8. **Whatever is unflushed is lost on a crash** (that is the nature of write-back). The loss window is cut
   explicitly by the byte ceiling and the time window, and the defaults are conservative. That is why the
   default of `mount.write_back` is `false`.
9. **The semantics of `{prefix}lock` are unchanged.** One flush takes the lock on the target `data_id` / inode
   once, so **the number of lock acquisitions also drops to the number of flushes**.

#### The settings that are newly needed

| The key | What it means | The default |
|---|---|---|
| `mount.write_back` | Enabled / disabled | **`false`** (turning it on by default is to be considered after verification) |
| `mount.write_back_max_bytes` | The dirty-byte ceiling (going over it flushes and applies back-pressure) | 64 MiB |
| `mount.write_back_interval_ms` | The time window (0 = no time trigger) | 1000 |
| `mount.write_back_batch_files` | The ceiling on the number of files folded into one tx (**1 = per file = the default**) | 1 |

The additions are reflected in [settings-matrix.md](settings-matrix.md), the defaults table of
[Mkfs.md](../Mkfs.md) and [pgfs.toml.example](../../pgfs.toml.example) as well.
**The dirty bytes, the number of dirty files, the number of flushes and the number of failed flushes** are
published in the Layer 3 status (see the Phase 4 section) (added to the snapshot in `{prefix}mounts.stats`).


## Implementation status (as-built)


**Implemented and verified on real hardware** (the dev server / Citus rf=2, 8 shards). The differences from the
design and the measured values are recorded below.

#### Measured (on real Citus rf=2 hardware, a 20 MiB `dd conv=fsync`)

| The workload | write_back off | write_back on | The ratio |
|---|---|---|---|
| `dd bs=128k` (a run of sub-chunk writes = the shape that amplifies) | 12.29 / 12.63 s | **2.07 / 2.02 s** | **6.1x** |
| `dd bs=1M` (a shape that does not amplify to begin with) | 5.65 s | **2.02 s** | **2.8x** |
| `rsync -a` (599 MB / 745 files) | 263 s (2.27 MB/s) | **179 s (3.19 MB/s)** | **1.4x** |

* **Even `dd bs=1M` gains 2.8x** because write-back also folds "the per-chunk lock acquisition /
  `SELECT chunk_size` / `SELECT length(payload)` / the `total_size` UPDATE / the inode UPDATE" into one per file
  (a part that the raw-SQL projection did not show).
* **rsync topping out at 1.4x is as expected** - `create` / `chmod` / `chown` / `utimens` / `rename` stay
  write-through as designed, and rsync's cost is dominated by those (about 2,100 metadata txs).
  **Making the metadata side write-back is a separate future theme** (the block-reservation machinery for ids
  applies to `{prefix}inode.id` unchanged).
* Against the raw-SQL projection of 8.1x, the measured figure is 6.1x (`bs=128k`). The difference is the FUSE
  round trips and the synchronous flush of `fsync`.

#### The differences from the design (as-built)

1. **A dirty chunk holds not "a list of written ranges (extents)" but only "the valid length (`Len`) and the
   length in the database (`PrevLength`)".** As a result of keeping a full chunk image, the information needed
   came down to "how far it is valid" and "how many bytes it is in the database" (the semantics of holes and of
   `total_size` can be reproduced from those two values).
2. **A partial write to an existing chunk reads once from the database** (the design memo said "it is not
   read"). Without the read the dirty buffer is not "the complete image of a chunk", and **the read path returns
   zeros for the ranges that were not written** (measured at 0.4 ms, so the cost is negligible). It is not read
   when `offsetInChunk == 0 && len >= chunkSize`. The regression tests are `test_partial_chunk_overwrite` and
   `test_partial_overwrite_after_remount`.
3. **The flush SQL is one statement per chunk, a "full replacement of the payload"**. Except that when the
   database side is longer than what is in hand (another client extended it), a `CASE` that falls back to an
   `overlay` was added so that the tail is kept.
4. **write-back works even with the read cache disabled (`cache_data_max_bytes = 0`).** The dirty state is a
   separate account from the read budget and is not subject to LRU eviction, and the read path switches to
   going through full chunks on `UseChunkCache` (= the read cache is enabled **or** write-back is enabled).
   The regression test is `test_dirty_visible_without_read_cache`.
5. **`truncate` / `unlink` / `release` "discard" rather than "flush".** The body is going away, so writing it is
   wasted. A `truncate` discards when it is to 0, and flushes first and then hands over to the existing path
   when it is not.
6. **A flush happens before `utimens` / `UpdateSize`.** It prevents the dirty `st_mtime` from landing afterwards
   and overwriting an explicitly set value (`FlushBeforeMetadataWrite`).
7. **`st_mtime` records the time of the write** (not the time of the flush), so that memory and the database
   agree.
8. **`write_back_batch_files` was not implemented** (the design said "implemented but off by default"). Batching
   across files adds +8.8%, which was judged not to pay for the wider loss window and the longer lock hold. If
   it becomes necessary, widen `FlushTransaction` to handle several `DirtyFile`s (the locks must be folded into
   a single `LockTargets`).

#### A bug found and fixed during the implementation (with a regression test)

**Overwriting a whole chunk that is not in the cache double-counted the occupied bytes and doubled `du`.**

* The cause: a whole-chunk overwrite has the optimization of not reading the payload from the database, and at
  that point **"the payload length in the database" was taken to be 0**, so the difference at flush time became
  `the new length - 0` and was added to `{prefix}data.total_size` in full (a 2 MiB file showed as 4 MiB under
  `du`).
* The fix: on a whole-chunk overwrite, **read only `length(payload)`** instead of the payload
  (`Api.LoadChunkBase` -> `LoadChunkLength`; `length(bytea)` only reads the raw size from the TOAST pointer and
  does not detoast). A partial write reads the full chunk, so its length is used.
* **It does not show while the chunk is in the cache** (the length is carried over on the clean -> dirty
  promotion). The regression test therefore has to **remount to empty the cache**, so it went not into the e2e
  suite but into [writeback.sh](../../tests/linux/writeback.sh) as
  `test_full_chunk_overwrite_du_after_remount` (confirmed to fail with `du grew from 2097152 to 4194304` when
  the fix is removed).

#### The main files added and changed

| The file | Its role |
|---|---|
| [DirtySet.cs](../../src/core/src/Api/DirtySet.cs) (new) | The per-file ledger of unflushed work (`DirtyFile` = the flush gate / the set of dirty chunks / the size and mtime / the pending data row / the error latch) |
| [IdReservation.cs](../../src/core/src/Api/IdReservation.cs) (new) | Reserving blocks of `{prefix}data.id` in advance (N `nextval`s in one statement, no lock, doubling growth) |
| [ContentCache.cs](../../src/core/src/Api/ContentCache.cs) | The dirty buffers (`WriteDirty` / `SnapshotDirty` / `MarkFlushed` / `DiscardDirty` / `RekeyData`); dirty data is not subject to eviction and is accounted separately |
| [Api.cs](../../src/core/src/Api/Api.cs) | `WriteDataBuffered` / `FlushInode` / `FlushData` / `FlushAll` / `FlushTransaction` / the back-pressure / the background flush loop / flushing everything on `Dispose` |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | The overrides of `Flush` / `FSync` (**newly implemented**; previously the base's `-ENOSYS`) plus the safety net in `Release` |
| [FileSystem.cs (dokan)](../../src/dokan/src/FileSystem.cs) | `FlushFileBuffers` turned into a real flush, plus a flush in `Cleanup` |
| [StatusCommand.cs](../../src/ctl/src/StatusCommand.cs) | A `write-back` line in Layer 3 (the dirty bytes / chunks / files, the number of flushes and of failures) |

#### Testing (done)

* **[tests/linux/e2e.sh](../../tests/linux/e2e.sh) was run on real hardware in both modes**: `write_back` off
  gives **42 passed / 1 skip** and on gives **42 passed / 1 skip** (the count grew from 38 to 43; the skip is
  the environment-induced `test_fallback_uname_gname`).
* **5 tests were added to the e2e suite** (consistency tests that should give the same result in both modes):
  `test_partial_chunk_overwrite` (a partial overwrite of a chunk does not damage what is around it) /
  `test_full_chunk_overwrite_du` (a whole-chunk overwrite damages neither the contents, the size nor the
  occupancy) / `test_append_after_close` (an append across a close neither double-counts nor loses occupied
  bytes) / `test_write_read_without_sync` (read-after-write while unflushed) /
  `test_truncate_discards_unflushed` (discarded dirty data does not come back).
* **[tests/linux/writeback.sh](../../tests/linux/writeback.sh) was newly added** (8 tests). It is a separate
  script from the e2e suite because **it remounts by itself**: surviving a `kill -9` after an `fsync` /
  surviving a `kill -9` after a `close` / the flush of a clean unmount / the back-pressure (a 64 KiB ceiling) /
  the visibility of dirty data with the read cache disabled / a partial overwrite after a remount (the seed
  path) / **a whole-chunk overwrite after a remount does not double-count `du`** (the regression for the bug
  above) / the write-back display of `pgfsctl status`.
* **How to measure the performance**: measure with `dd bs=128k` or rsync. **`dd bs=1M` lines up with the chunk
  boundaries so no amplification happens, and it understates the improvement.**

#### The work that remains

1. **Add a `WRITE_BACK` injection point to the docker runner (`tests/docker/run.sh`)** so that both modes run on
   a single PG too (the same shape as `CACHE_DATA_MAX_BYTES`). Not done, because this environment has no docker.
2. **Verification on the Windows (Dokan) side** - **the off mode has been green on real hardware since
   2026-09-19** (Windows e2e 30/30 x3 plus cross-client 6/6 x2, Citus rf=2 on the server).
   **Acceptance of the on mode, which actually goes through `FlushFileBuffers` / `Cleanup`, is complete too** -
   [writeback.ps1](../../tests/windows/writeback.ps1) passes **with a negative control** (re-run on 2026-09-21:
   6/6; the same suite also watches writes without a barrier being lost).
   The Windows as-built is in [windows-parity.md, the implementation status](windows-parity.md).
3. **An automated test that `-EIO` is returned when a flush fails** - it needs a shape that writes with the
   database down, so it is not in place (the path itself is implemented).
4. **A cross-client last-flush-wins test** (B writes the same file while A is still unflushed).
5. **Making the metadata operations write-back** (the road to making the rsync-shaped workloads faster still) ->
   **the design is settled; [metadata-write-back.md](metadata-write-back.md) is the source of truth**.


## The record of changes

The chronological record goes here (the two chapters above are the source of truth for the design and the as-built).

- [runtime-control-plane.md](runtime-control-plane.md) had swollen to 1,802 lines, so it was
  split by feature and this document was carved out of it. The contents are as they were before the split.
  The as-built of **the two-phase live disable** was in the review section of 1e before the split,
  so it remains on the [metadata-write-back-reviews.md](metadata-write-back-reviews.md) side.
