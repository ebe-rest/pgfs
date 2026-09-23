# The lifecycle of `data_id` - hardlink sharing and fixing the stability of `st_ino`

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **Status**: the design is agreed; for the implementation see the implementation-status section.
>
> **What this document is the source of truth for**: the invariant that `data_id` is "the file's body id",
> **settled at create time and never released until the final unlink**. The lazy creation of the
> `{prefix}data` row (no row = the contents are empty), sharing a body through hardlinks, distributing a
> `truncate 0` to every link, and `st_ino` being stable because it comes from `data_id` all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [database.md](database.md) | **The schemas themselves** of `{prefix}inode` / `{prefix}data` / `{prefix}data_chunk` (the columns, the constraints, the distribution keys) |
> | [metadata-write-back.md](metadata-write-back.md) | The design of metadata write-back. The ledger of pending inodes and **how a reserved id is used** |
> | [write-back.md](write-back.md) | The dirty ledger of data write-back. Where the unflushed dirty data a `truncate 0` discards lives |
> | [cache.md](cache.md) | How cache invalidation keyed on `data_id` works |
> | [../tests.md](../tests.md) | The list of tests, how to run them and the counts. This document says **what each test protects** |

## The background - three defects from one root

pgfs expresses "a hardlink = several inodes pointing at the same `data_id`".
In other words **`data_id` is the file's body id**. But in the implementation, allocating and releasing
`data_id` was tied to **per-link operations rather than to the file's lifetime**, which produced the three
defects below (all reproduced on real hardware).

| # | The symptom | Reproduced on real hardware |
|---|---|---|
| 1 | **`truncate 0` deletes the shared body, leaving the sibling with lost data and a dangling reference** | `echo HELLO > t1; ln t1 t2; truncate -s 0 t1` -> the shared data row is DROPped and **t2 survives holding both its `data_id` and `st_size=6`**. `cat t2` gives 6 NUL bytes. Even after `rm t1`, t2's `st_nlink` stays 2 |
| 2 | **A hardlink to an empty file is not a link** | Right after `: > e1; ln e1 e2`, **`st_nlink` is 1 on both and their `st_ino` differ**. While `data_id` is NULL there is **no way to express the link relationship** |
| 3 | **Writing the contents changes `st_ino`** | `: > e1` gives `st_ino = 144591` (= `inode.id`), and `echo HELLO > e1` gives `st_ino = 9223372036854900517` (= from `data_id`). The same after a `truncate 0` and a rewrite. **`find -samefile` / `rsync -H` / `tar` / a backup's deduplication all misbehave** |

The cause is the same for all of them: **`data_id` had become "an id that exists only while there are contents"**:

- The `newLength == 0` branch of `Api.TruncateData` did `DropAllChunks` plus **`DropDataRow`** plus
  **`ClearInodeDataId` (only its own inode)** plus `UpdateSizeInTx` (its own again)
- `Api.CreateFile` created the inode with `data_id: null` and **did not allocate one until the first write's `EnsureDataRow`**
- `st_ino` in `src/fuse/src/FileSystem.cs` is `data_id | 2^63` when `DataId != null` and `inode.Id` when it is null

## The decision

**`data_id` is the file's body id: it is settled at create time and not released until the final unlink.**
However **the creation of the `{prefix}data` row stays lazy** (no extra INSERT at create time).
`st_ino` stays **derived from `data_id`** (this decision is what makes it stable as a consequence).

### The new invariant

> **A regular file's `inode.data_id` is settled at create time and is immutable until the final unlink.**
> **The `{prefix}data` row is created lazily, and "no row" is interpreted as "the contents are empty".**

Directories and symlinks carry no `data_id`, as before (their `st_ino` comes from `inode.Id`).

The invariant **rides the existing machinery as-is**:

- `IdReservation` ([IdReservation.cs](../../src/core/src/Api/IdReservation.cs)) already provides **allocation
  with no database round trip** (in blocks, doubling from 64 to 4096; measured at 4.0 ms for 1000). The inode
  INSERT of a create already takes a `@data_id` column, so **passing the allocated value adds no round trip**
- `EnsureDataRow` already has the branch **"a reserved data_id exists but there is no row" -> `CreateDataRowWithId`**
- `EnsureDirtyFile` treats the same state as "reserved and unflushed"
- `ReadData` returns early when `offset >= inode.Size`, so an empty file (with no row) never touches the data
  row (the same path that closed "a read of a reserved data_id gives `-EIO`" in stage 2 of metadata write-back)

## What changes

| Target | The change |
|---|---|
| `Api.CreateFile` | `dataId: null` becomes **an id allocated from the reservation**. **On an allocation failure it falls back to null** so the create itself still succeeds (a create is not dropped on an existing FS or a transient database failure) |
| `Api.TruncateData` (`newLength == 0`) | **Stop** doing `DropDataRow` and `ClearInodeDataId`. It closes on `DropAllChunks` plus `total_size = 0` on the data row |
| The same | **Distribute `st_size = 0` to every link** - `WHERE data_id = @shared_data_id` only when `st_nlink > 1` (the precedent is `UpdateInodeSizeAndMtimeInTx`; restricting it to `st_nlink > 1` is because it becomes multi-shard on Citus) |
| `Api.CreateHardLink` | When `source.DataId == null` (= an empty file created before this change), **allocate inside the tx and attach it to the source first**, then link. That fixes #2 on an existing FS too |
| `DirtyNamespace.TruncateToZero` / `Api.TruncatePendingToZeroLocked` | **Stop** setting `Inode.DataId = null` (set only `Size = 0`). It is consistent with "no row = empty" |
| `src/fuse/src/FileSystem.cs` (`st_ino`) | **No change.** It becomes stable because `data_id` is now immutable |
| `src/dokan` | **No change** (`FileMode.Create` / `Truncate` go through the same `TruncateData`) |

### The options not taken

| Option | Why it was rejected |
|---|---|
| **A. Merely stop deleting the data row on truncate** | It fixes #1 and `st_nlink`, but **#2 (hardlinks to empty files) and #3 (the instability of `st_ino`) remain**. All three share a root, so closing them together is cheaper |
| **B. Re-point the siblings at a new row when it is released** | inode is distributed on `parent_id`, so **the siblings are on other shards** = a cross-shard UPDATE. On top of that it would unwind the convention `LinkDataIdInTx` created ("touch the inode shard at the end of the tx", to break the 40P01 cycle). The result is the same as A |
| **C. NULL out the siblings' `data_id` as well** | **It does not fix it.** The next write has `EnsureDataRow` create a new row and it splits again, with `st_nlink` still broken |
| **D. INSERT the `{prefix}data` row at create time** | Every create gains an INSERT. **It collides head-on with metadata write-back's "a create is pending with zero database round trips"**. Allocating only the id in advance (= this decision) gets the same effect |
| **Add a stable id column on the inode side for `st_ino`** | It could be made independent of `data_id`'s lifetime, but it needs **a DDL change plus a migration for existing FSs**. Not a change to make right before v0.2.0. **Kept as an option for v0.3 and later** (see the remaining points) |

## The interaction with write-back and metadata write-back

- **`truncate 0` also discards a sibling's unflushed dirty data.** `PrepareTruncateWriteBack` calls
  `DiscardDirtyData(dataId)`, and the ledger holds "one data_id = one DirtyFile", so
  **the unflushed work for the shared body disappears for every link**.
  **That is the intended behaviour** (it is a truncate of a shared body, so it is correct under POSIX).
- **Keep the order "`MarkSyncOnClose` after `PrepareTruncateWriteBack`"** (the former calls `FlushInode` and
  clears the mark). Breaking it makes `test_meta_truncate_syscall_close_is_synchronous` in `wbmeta.sh` fail.
  The mark is placed on **both the inode and the data key** by A-4 to A-7, so `data_id` no longer disappearing
  **changes the lifetime of the data-side key** (= the mark's lifetime lines up with the inode's).
- **The `ReserveDirtyFile` path of `EnsureDirtyFile` becomes a fallback for existing FSs only**
  (newly created files already carry a `data_id`). **Do not remove it.**
- **Do not remove B-10's `chunk_size` cross-check (the Rekey check in `EnsureFlushDataRow`).**
  The Rekey path is taken less often, but the cross-client contention path remains.

## How existing FSs are treated

- **A body that has already split cannot be repaired** (the lost chunks cannot be recovered). **No repair
  command is written** (the defect is closed as of v0.2.0; a body that had already split before that has to be recreated).
- **A file created before this change with a NULL `data_id`** joins the new invariant either
  (1) when the first write allocates one through `EnsureDataRow` as before, or
  (2) when `CreateHardLink` allocates and attaches one at link time.
  **`st_ino` changes exactly once at that moment** (the same as the existing behaviour).

## Testing

It is not timing-dependent, so **automated tests are written**.

| Test | Contents |
|---|---|
| `test_truncate_keeps_hardlink_shared` (Linux e2e) | `echo A > f; ln f g; echo B > g` -> `cat f` gives `B` / `st_nlink` is 2 on both / the same after a remount |
| `test_truncate_zero_updates_all_links` (Linux e2e) | After `truncate -s 0 f`, **the sibling g's `st_size` is 0 as well** |
| `test_empty_file_hardlink_shares` (Linux e2e) | `: > e1; ln e1 e2` -> `st_nlink` is 2 on both and their `st_ino` agree |
| `test_ino_stable_across_write` (Linux e2e) | The `st_ino` of `: > e1` and the `st_ino` after `echo HELLO > e1` agree |
| `test_truncate_zero_then_rewrite` (Windows e2e) | After `truncate 0` and a rewrite, the size and the hash agree |
| `test_file_index_is_data_id_and_stable` (Windows e2e) | **`FileIndex` comes from data_id (the top bit is set) and does not change across the first write**. **"Dokan does not expose `st_ino` / `nlink`" turned out to be wrong** - `ByHandleFileInformation.FileIndex` and `NumberOfLinks` are exactly that |

## Implementation status

- **The design was agreed and the implementation completed** (0 build errors). What went in is below.

| The change | Where |
|---|---|
| Allocate a `data_id` at create time and put it on the inode (without creating the row; falling back to null on an allocation failure) | `Api.CreateFile` / the new `Api.RentDataId` |
| Stop doing `DropDataRow` / `ClearInodeDataId` on `truncate 0`; close on `DropAllChunks` plus `total_size = 0` | `Api.TruncateData` / the new `Api.ResetDataTotalSizeInTx` |
| Distribute `st_size` to every link when there are hardlinks (**a non-zero truncate too**) | The new `Api.UpdateTruncatedSizeInTx` / the new `Api.UpdateSizeForAllLinksInTx` |
| Invalidate the siblings' `InodeCache` entries by `data_id` (pinned = pending ones are left alone) | The new `InodeCache.InvalidateByDataId` |
| Compatibility with older FSs: allocate and attach a `data_id` when hardlinking a source that has none | The new `Api.AttachDataIdForLinkInTx` (from `Api.CreateHardLink`) |
| Stop setting `Inode.DataId = null` on the pending side too | `DirtyNamespace.TruncateToZero` / `Api.TruncatePendingToZeroLocked` |
| `st_ino` (derived from `data_id`) | **No change** - it is stable because `data_id` is now immutable |
| `src/dokan` | **No change** |

**Decided during the implementation**: distributing `st_size` to every link **was applied to non-zero
truncates as well**. The design only looked at `truncate 0`, but `truncate -s 3` has the same defect of
leaving the sibling holding a stale `st_size`. The check reuses `HasHardLinkSiblings` (the existing helper
that looks only at the cached `st_nlink`), and **when `st_nlink == 1` it is a single-row UPDATE as before**,
so Citus gains no multi-shard work.

**A hole found and closed during the implementation**: `PrefetchOccupiedBytes` skipped an inode with no
`{prefix}data` row using `continue` and left `OccupiedBytesLoaded` unset. Previously an empty file meant
`data_id` was null, so an earlier branch settled it at 0, but **giving a file a `data_id` at create time drops
every not-yet-written file into here** - that is, `ls -l` would have gained one `GetOccupiedBytes` round trip
per empty file. Applying the new invariant (**no row = the contents are empty**) settles it at 0.

### The gap found in the Linux regression - **`truncate` was fixed but `write` was not**

The FUSE side's regression on real hardware failed the two new e2e tests. The symptom was
**"the chunks are shared, yet the reader is cut off by a stale `st_size` and the contents look empty"**:

```
: > a; ln a b       -> a and b share a data_id / st_nlink 2 / st_size 0   <- correct so far
echo "shared" > a   -> a: st_size=7 / b: st_size=0 (the same data_id)
cat b               -> empty
```

The cause was that distributing `st_size` to every link **had only been added to `truncate`**, and **an
ordinary write needs the same distribution**. Each of the two paths had a hole:

| Path | The hole | The fix |
|---|---|---|
| write-through | `FinishWriteInodeInTx` was still a single-row UPDATE | Call the new `PropagateWriteToLinksInTx` at the end of the tx (`WHERE data_id = @data_id AND id <> @id`, only when `st_nlink > 1`) |
| write-back | Because of `if (sharedDataId != null && linkDataId == null)` in `UpdateInodeSizeAndMtimeInTx`, **the distribution does not apply when the data row is INSERTed in the same tx (= the first write to that body)** | Linking and distributing are not mutually exclusive, so in a tx that folds in the link clause, **the single-row UPDATE is followed by one more statement for the distribution** |

The `st_nlink > 1` check gained **an overload of `HasHardLinkSiblings` that prefers the `Inode` in hand**
(the write and truncate paths have just read the target row, so it does not look like 1 even if it has fallen
out of the cache).

### The multi-shard search on `rm` was put back

The `SELECT id FROM inode WHERE data_id = @data_id` in `DeleteInodeInTx` used to be skipped entirely for a
file with a NULL `data_id` (= never written), but **settling `data_id` at create time made it run on every
unlink**. `data_id` is not the inode's distribution key, so on Citus it is multi-shard, and it tells under an
`rm -rf`-heavy load.

**`RETURNING st_nlink` was added to the DELETE, and the sibling search is skipped entirely when the
`st_nlink` immediately before the delete is 1 or less** (the new `ReleaseOrRelinkDataInTx`). **Not using the
cache for the check** is the crux: it looks at the database value in the same tx, so a hardlink another client
created is not missed (missing it would delete the sibling's body = exactly the defect being fixed). When
`st_nlink` is 2 or more but there is no sibling (an nlink broken by the old defect), the body is released as before.

### Dropping the siblings' cache, and moving the distribution check onto the database's `st_nlink`

On the second Linux pass only `test_empty_file_hardlink_shares` remained. **The database was distributing
correctly (`st_size=7` on both `a` and `b`), and only `stat b` across the FS returned 0** - that is,
**the sibling's `InodeCache` was not being invalidated**. The `truncate` side called `InvalidateByDataId`, but
**the write path never called it once**.

| Path | The cleanup that was added |
|---|---|
| write-through | `InvalidateHardLinkSiblings` (= `InodeCache.InvalidateByDataId`) after the commit |
| write-back (the flush) | `InvalidateByDataId` at the same place |

**At the same time the distribution check moved from "the cached `st_nlink`" to "the database's `st_nlink`".**
A stale cache showing 1 skips the distribution entirely, which **goes straight back to the defect this
document is fixing**. The value comes free from the `RETURNING st_nlink` of an UPDATE that is already running:

| The check | Before | Now |
|---|---|---|
| The distribution on truncate | `HasHardLinkSiblings` (the cache) | The `RETURNING st_nlink` of `UpdateSizeInTx` |
| The distribution on write / invalidating the siblings' cache | (the distribution used the cache; there was no invalidation) | The `RETURNING st_nlink` of `FinishWriteInodeInTx` |
| Releasing the body on a delete | A multi-shard sibling search | `DELETE ... RETURNING st_nlink` |

**Every check about a body shared through `data_id` now uses the database's value.** Not one round trip was
added. The `Inode`-preferring overload of `HasHardLinkSiblings` became unnecessary and was removed
(the `HasHardLinkSiblings(long)` on the flush path stays as A-1's existing design; see the remaining points).

### Verification

- **0 build errors.**
- **Every suite green on real Windows hardware** (Dokan 2.3.1 / PG on the server, Citus rf=2, audit on):
  e2e **35/35** (including the new `test_truncate_zero_then_rewrite`) / cross-client **6/6** /
  write-back **6/6** / metadata write-back **4 passed + 1 skip** (the known SKIP) / control-plane **6/6**.
- **The Linux regression on real hardware is the FUSE side's job** (e2e off/on plus `writeback.sh` plus
  `wbmeta.sh` plus `negcache.sh`). The four new tests (`test_truncate_keeps_hardlink_shared` /
  `test_truncate_zero_updates_all_links` / `test_empty_file_hardlink_shares` / `test_ino_stable_across_write`)
  **can only be confirmed on Linux** (because Dokan does not expose `st_ino` / `st_nlink`). **Those four are
  the heart of this fix.**


### Windows was brought onto the same formula

**Dokan's `ByHandleFileInformation.FileIndex` was still `inode.Id`**, so **hardlink siblings were judged to be
"different files"** (measured: two names pointing at the same `data_id` gave `28639` and `28640`). It was
changed to `data_id | 0x8000_0000_0000_0000` = **the same formula as FUSE's `st_ino`**, so that
**the same file returns the same value on both operating systems**.

The reason #2 (hardlinks to empty files) and #3 (`st_ino` changing when the contents are written) are closed
on Linux - **that `data_id` is settled at create time and immutable until the final unlink** - applies to
Windows unchanged. It was also confirmed by measurement that **an empty file's `FileIndex` does not change
across the first write**.

The as-built note is in [windows-parity.md, on making Windows's `FileIndex` come from `data_id`](windows-parity.md).

## The remaining points

- **`st_mode` / `uname` / `gname`, and the `st_mtime` of an explicit `utimens`, are not distributed to
  hardlink siblings** (confirmed by measurement). **Under POSIX a hardlink points at the same inode, so
  sharing these is the correct behaviour.**

  **Paths that distribute and paths that do not are mixed**, so do not confuse them:

  | Path | Does it distribute | Implementation |
  |---|---|---|
  | **write (`st_size` + `st_mtime`)** | **Yes** | `Api.UpdateInodeSizeAndMtimeInTx` (passes `sharedDataId` when `HasHardLinkSiblings`) |
  | **`truncate` (`st_size`)** | **Yes** | `Api.UpdateSizeForAllLinksInTx` / `UpdateTruncatedSizeInTx` |
  | **`chmod` (`st_mode`)** | **No** | `Api.UpdateMode` |
  | **`chown` (`uname` / `gname`)** | **No** | `Api.UpdateOwner` |
  | **`utimens` / `touch` (`st_mtime`)** | **No** | `Api.UpdateTimestamps` |

  Measured (on the dev server, on the current HEAD):

  ```
  echo hello > a; ln a b       -> a and b both mode=644 / nlink=2 / size=6 / the mtimes agree
  echo more >> a               -> the size and mtime move together on a and b     <- distributed
  chmod 600 a                  -> a mode=600 / **b mode=644**                     <- not distributed
  touch -d '2020-01-02 ...' a  -> a mtime=2020-01-02 / **b mtime unchanged**      <- not distributed
  ```

  **Saying "`st_mtime` is not shared" overstates it** - it is distributed on a write.
  **What is not distributed is an explicit change (`utimens`) only.**

  The evidence is that [Api.UpdateMode](../../src/core/src/Api/Api.cs) /
  [Api.UpdateOwner](../../src/core/src/Api/Api.cs) / [Api.UpdateTimestamps](../../src/core/src/Api/Api.cs)
  **update a single row with `WHERE parent_id = @parent_id AND id = @id`**. **The same shape as the `st_size`
  distribution (`Api.UpdateSizeForAllLinksInTx`) is needed. What needs fixing is those three; the write paths
  are already correct.**

  **This is not a deliberate departure but something unimplemented.** The reason this document's change
  section gave for distributing `st_size` to every link (**the sibling holds a stale value**) applies
  unchanged, and **no document records a decision not to distribute**.
  It is the same shape as noticing during the implementation that "only `truncate 0` was being looked at,
  while `truncate -s 3` has the same defect" and generalizing - **except that the generalization stopped
  inside `st_size`.**

  **There are two effects on users, and different people read each.**
  **(1) Permissions**: **`chmod` only applies to one of the names**, so **using a hardlink as a permission
  boundary leaves the body you thought you restricted openable at its original permissions through the other name.**
  **(2) Timestamps**: **a time set explicitly with `touch` does not reach the sibling**, so
  **`make` or a backup looking at the sibling's name wrongly concludes "it has not been updated".**
  The user-facing wording is in [CHANGELOG.md, the known limitations](../../CHANGELOG.md).

  **Because it needs no preconditions, it is heavier than the trampling** (which needs `write_back` on, and
  that is off by default) - **it happens on a default configuration from a plain `chmod`.**

  **The shape of the fix**: widen to `WHERE data_id = @shared_data_id` only when `st_nlink > 1`
  (**the precedent is `UpdateSizeForAllLinksInTx`**; restricting it to `st_nlink > 1` is because
  **it becomes multi-shard on Citus**).
  **Distribute `st_ctime` along with it.** **The same distribution is needed on metadata write-back's ledger
  side (`CoalesceMode` / `CoalesceOwner` / `CoalesceTimestamps`)** - that side coalesces on the ledger without
  opening a tx, so **fixing only the database side loses the distribution while write-back is on**.
  **Pair the regression with the `truncate` precedent** (put the `chmod` / `chown` / `touch` versions next to
  `test_truncate_zero_updates_all_links`).

- **Whether `st_ino`'s stability should keep depending on `data_id`'s immutability.** This decision makes it
  depend on it. Making it independent means a stable id column on the inode side (a DDL change plus a
  migration for existing FSs) - **v0.3 and later**.
- **A directory's `st_ino`** stays derived from `inode.Id`, in a separate namespace from `data_id` (split by
  the sign bit). This change does not touch it.
- **Another mount's sibling cache is not dropped.** A write notifies
  `Notify(inodeIds: [self], dataIds: [data_id])` and **does not carry the siblings' inode ids**. The receiving
  side discards the content cache by `dataIds`, but `InodeCache` only invalidates its own entry, so
  **statting a hardlink sibling from another mount can return a stale `st_size`**. Closing it would naturally
  mean having the receiving side look at `dataIds` and fire `InvalidateByDataId` (the scan is O(the number of
  cache entries), so measure before firing it on every data write). There is no automated test yet.
- **Only the distribution check on the flush (write-back) path still depends on the cache**
  (`HasHardLinkSiblings(long)`). It is A-1's existing design, and moving it onto `RETURNING st_nlink` too
  would line everything up.
