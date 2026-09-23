# The review record of the metadata write-back (rounds A and B)

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) ›
> [metadata-write-back.md](metadata-write-back.md) › **this document**
>
> **What this document is the source of truth for**: **the chronological record** of the findings of the
> adversarial reviews of 1e stage 2 and of the fixes for them. The as-built of round A (A-1 to A-10) and round B
> (B-1 to B-13) is authoritative here.
> **A new review result and its fixes are appended to this document.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [metadata-write-back.md](metadata-write-back.md) | **The settled design and the implementation status.** "How it works now" goes there |
> | [write-back.md](write-back.md) | Deferring the writes of the file contents (1d). **The two-phase live disable** comes from a review, so it is in this document |
> | [windows-parity.md](windows-parity.md) | The Windows-side acceptance of the same findings (the wiring of B-1 / B-7 / B-9 / B-12) |
> | [../tests.md](../tests.md) | The counts of the regression tests and how to run them |

## The results of the adversarial review of stage 2 (**A-1 to A-10 and B-1 to B-13 were all addressed**)

> The findings from two lenses (**durability, crash consistency and POSIX** / **concurrency, state transitions
> and operations**) plus writing the tests, with the duplicates folded together. **Round A (A-1 to A-10) was
> fixed** (the round A fixes section below), and **B-1 to B-13 were addressed too**
> (each B-N section below is the as-built). They affect only the
> `write_back_metadata = on` path (there is zero regression with the default off = confirmed by the e2e suite at
> 43+1 skip and writeback.sh 8/8).
> **The top 5 were confirmed by the reviewers against the code** (CONFIRMED).

### Round A (the safety side; to be fixed first)

| # | The finding | Where | Severity |
|---|---|---|---|
| A-1 | **A write through a hardlink sibling disappears silently without needing a crash.** `DirtyFile.InodeId` is fixed to "the inode that wrote first" (`init`), yet the flush looks only at the liveness of that inode and discards the dirty data if it is gone. `echo A > f; ln f g; echo B > g; rm f` **loses B and makes `cat g` return A** (the close returns 0 and there is no reporting point). Under 1d the close flushed synchronously so it needed a race, but **with close-no-flush it loses deterministically**. As part of the same sibling hole, the `st_size` and `st_mtime` are written to the wrong inode too (writing to `g` and `fsync(g)` still leaves `g`'s size stale) | `Api.cs:2271-2277` / `:2288` / `DirtySet.cs:210` | **Critical** |
| A-2 | **The error state never fires in real operation.** `consecutiveFlushFailures` is **a single counter for the whole process** and **the data-side flushes hit the same counter** (`Api.cs:2251,2256`). Even with one permanently failing entry, every other success resets it and it never reaches 5 in a row. -> Reporting path (3) dies entirely. **The soundness of the back-pressure (4) depends entirely on (3)**, so fixing (3) fixes (4) | `Api.WriteBackMetadata.cs:677-698` | **Critical** |
| A-3 | **A `RENAME_EXCHANGE` turns into a replacement and destroys data.** The FUSE layer looks only at `RENAME_NOREPLACE` (=1), and an exchange request is **committed atomically in a single tx** as "delete the target plus move the source" (with errno 0). It existed before stage 2, but making it a single tx made it more irreversible. **It can be closed by simply returning `-EINVAL` for an unsupported flag** | `src/fuse/src/FileSystem.cs:418-465` | **Critical** |
| A-4 | **The synchronous-close mark (heuristic b) leaks and is disabled by the ceiling.** `MarkSyncOnClose` is in one place (`TruncateData`) and `ClearSyncOnClose` in one place (`FlushInode`), and **it is not cleared on a delete, a pure cancellation, a replacement or a discard**. Inode ids are not reused, so it grows monotonically, and **on reaching the ceiling of 4096 no truncate is marked from then on = (b) is silently disabled** | `Api.WriteBackMetadata.cs:717-734` / `Api.cs:2077,2462` | **High** |
| A-5 | **The granularity of the mark is wrong.** What a `truncate` destroys is the chunks **of a data_id**, but the mark is per inode, so **a close through a hardlink sibling does not see the mark**. The judgement "per inode, not per fd" (difference 3) was right, but the correct granularity is **`DirtyFile` (per data)** | `Api.WriteBackMetadata.cs:741-751` | Medium |
| A-6 | **The mark is cleared after the flush, so it also clears the marks for the dirty data that piled up during the flush** (it should be capture-and-clear) | `Api.cs:2069-2078` | Medium |
| A-7 | **`truncate -s 0 f; cmd >> f` leaves zero-length garbage** (reproduced by a test; the only FAIL in `wbmeta.sh`). coreutils' `truncate` does open -> `ftruncate` -> **close**, so the mark is consumed by that close and the following append on a different fd goes back to being deferred. **It is the very "destroy the old immediately and defer the new" window that stage 2 declared it would close.** It shares a root with A-4/A-5/A-6 (it closes if the mark is kept "until that inode is next written and flushed") | `test_meta_truncate_syscall_close_is_synchronous` | **High** |
| A-8 | **The deadline is checked outside the sweep, so it can block for hours.** `ApplyInodeBackPressure` / `FlushAllForShutdown` **fire a flush at every pending entry first** and then look at the deadline. A permanent failure of the audit partition ensure is one DDL with `lock_timeout = 5s` = at worst 5 seconds per entry, and **the failure is not cached** so it is fired again every time -> with 4096 pending entries, **round 1 alone is about 5.7 hours** = the design goal of "a deadline was set because blocking forever is indistinguishable from a hang" is broken. The unmount is the same shape, so systemd's `TimeoutStopSec` SIGKILLs it -> **every pending entry is lost with neither a loss report nor exit 4** | `Api.WriteBackMetadata.cs:644-667` / `:768-782` / `Api.cs:2607-2637` | **High** |
| A-9 | **`(the counter, the error state)` is not atomic**, so there is an order that falls into "the counter is 0 but the error state is ON" = a permanent read-only state it cannot get out of by itself (the only thing that clears it is a successful flush, and it blocks the very writes that would produce one) | `Api.WriteBackMetadata.cs:677-711` | High |
| A-10 | **Overwriting an existing file can still be "torn" by a crash after the close.** The contract table in `Mount.md` says "the atomicity of a single file improves", but that only holds for **files born pending**. An overwrite of a persisted inode is partially committed in a separate tx every interval, so it can become **a chimera with a new first half and an old second half** (the contract table does not cover it). **Technically it is closed by "putting the synchronous-close mark on as soon as a write to a persisted inode starts"** = confining close-no-flush to new creations. **The win is on the create side, so the performance is not expected to drop** (to be measured) | `docs/Mount.md:133-139` / `Api.cs:2093-2106,2217-2233` | **High** |

### The round A fixes (as-built)

> What actually went in against the findings above. **The table of findings itself is kept as a record**
> (what was fixed cannot be read without what was happening before it was fixed; the line numbers in the table
> are as they were then = they point at the state before the fixes).
> The verification environment was **the dev server on real hardware** (the database / the schema `pgfs_test` /
> Citus rf=2, with no docker).
> The results were the Linux e2e suite (43 passed + 1 skip both off and on), `writeback.sh` 8/8,
> `wbmeta.sh` **17/17** (16 -> 17 with the regression test for A-1 added) and `negcache.sh` 7/7.

| # | What went in | Where |
|---|---|---|
| A-1 | `DirtyFile.InodeId` was changed from `init` to `set`, and the flush tx now **looks for a sibling inode referring to the same data body and re-points at it if the fixed inode is gone** (`ResolveFlushSibling`). The re-pointing goes back to the retry loop of `FlushLocked` through a `FlushRetargetException` and **opens the tx again** (to observe the lock order inode -> data, it does not add another inode's lock behind a data lock it already holds). Only when not one referring inode is left does it discard as before. Along with it, **a flush of an inode with `st_nlink > 1` distributes the size and mtime to every link with `WHERE data_id = ...`** (so that a stat through any link gives the same value; the same shape as the `st_nlink` +1 of `CreateHardLink`. On Citus it becomes a multi-shard UPDATE, so only when `st_nlink > 1`) | `Api.cs` `FlushTransaction` / `UpdateInodeSizeAndMtimeInTx` / `HasHardLinkSiblings` / `DirtySet.cs` |
| A-2 / A-9 | (**with a follow-up fix** - see the A-2 / A-9 follow-up section) The consecutive-failure counter went from **one for the whole process to one per flush target** (`flushFailuresByTarget`, keyed with the same sign convention as `LockTargets`: an inode is negative and a data id positive). It is no longer reset by another success, and **a single permanently failing entry reaches the threshold of 5 by itself**. The counter and the error state move together under `flushFailureGate`, and **the clearing condition is derived from the state of the counters ("not one target has reached the threshold")**, so there is no order in which "the counters are empty but the error state remains" | `Api.WriteBackMetadata.cs` `NoteFlushFailure` / `NoteFlushSuccess` |
| A-3 | FUSE's `Rename` now returns **`-EINVAL` if any flag other than `RENAME_NOREPLACE` is set** (it checks the flags at the head and then does nothing). It closes the destruction where a `RENAME_EXCHANGE` (2) or `RENAME_WHITEOUT` (4) request is committed in a single tx as "delete the target plus move the source" | `src/fuse/src/FileSystem.cs` `Rename` |
| A-4 / A-5 | The synchronous-close mark goes from **per inode to both the inode and the data body** (the same sign convention). With the inode side alone a close through a sibling does not see the mark, and with the data side alone what a `truncate 0` removes along with the data row cannot be followed, so both are needed. **When the ceiling of 4096 is reached, the mark is not silently dropped: `syncOnCloseOverflow` is raised and every close is upgraded to synchronous** (fail-safe; degrading to the 1d behaviour at a cost in performance is better than a data-safety mechanism being quietly disabled). **The mark is cleared on a delete and a pure cancellation too**, so it does not grow monotonically | `Api.WriteBackMetadata.cs` `MarkSyncOnClose` / `AddSyncOnCloseKey` / `ClearSyncOnClose` / `RequiresSyncClose` |
| A-6 / A-7 | The condition for clearing the mark was changed to **"only when something unflushed was actually written out"** (`FlushInode` takes `HasUnflushed` before the flush and looks at it after the flush too). (1) A close that wrote nothing does not clear it -> the mark of `truncate -s 0 f; cmd >> f` is no longer consumed by the truncate side's close (**the only FAIL in `wbmeta.sh` is resolved**). (2) If dirty data that piled up during the flush is still there, the mark stays too | `Api.cs` `FlushInode` / `HasUnflushed` |
| A-8 | The deadline check moved **inside the sweep**. `FlushAll(DateTime? deadline)` was added and the deadline is checked inside `FlushAllPendingInodes`, the data loop and `ApplyInodeBackPressure`. Even with a failure that is slow per entry (the 5-second `lock_timeout` of the audit partition ensure), round 1 does not run to hours | `Api.cs` `FlushAll` / `Api.WriteBackMetadata.cs` `ApplyInodeBackPressure` / `FlushAllForShutdown` |
| A-10 | **The synchronous-close mark is put on as soon as a write to a persisted inode (a body already in the database) starts** = close-no-flush is confined to **pending-born**. It closes the window where overwriting an existing file is partially committed in a separate tx every interval and becomes a chimera with a new first half and an old second half. The win of close-no-flush is on the create side, so a bulk copy is not expected to slow down -> **measured on 2026-09-19** ([performance.md, the effect of A-10](performance.md)). **One-off overwrites show no difference in total** (below the measurement limit). The price concentrates on **a workload that keeps reopening and overwriting one file**, where the ratio against no A-10 is **125x**, but **that value is exactly the same as the default with metadata off** (112.8 ms/close) = it is not made slower than the default | `Api.cs` `WriteDataBuffered` / `IsPendingBorn` |
| (added) | **A no-op success check for source == replaceTarget in `Api.Rename`.** A replacement "deletes the target and then UPDATEs the source", so with the same target it deletes itself. On Linux the VFS rejects `rename("a","a")` so it is unreachable, but **the Dokan path has no such protection**, and Core is made the last breakwater (at the request of the Dokan side) | `Api.cs` `Rename` |

### Round B (the knob plus the rest)

| # | The finding | Severity |
|---|---|---|
| B-1 | **Add the knob `mount.write_back_metadata_exclusive_create` (`write_through` / `defer`, `write_through` by default)** (decided). With `defer`, an `O_EXCL` create becomes pending too. The exclusion within one mount is kept because the ledger's `TryAdd` returns EEXIST on a (parent, name) collision, and what is lost is only the cross-client exclusion. **When an inode born under `defer` hits a name collision at flush time, it must not remove the other side under last-flush-wins** - having told the application "I alone created it", silently DELETE+INSERTing the other side (which might be a lock file) is the worst outcome, so it gets **the same error latch as a mismatched-kind collision**. The aim and the measurements are in [performance.md](performance.md) | A feature |
| B-2 | ✅ **Fixed** (the B-2 as-built below). **The loss report and exit 4 reach nobody with the default way of starting.** When it daemonizes, the parent `return 0`s after reading `PGFS_MOUNTED_OK`, and the child `Console.SetOut/SetError(TextWriter.Null)`s before `return 4`. On top of that the `Dispose` DELETEs the `{prefix}mounts` row, so **not a trace is left in the database either** -> leave the loss in the database (an audit row `op=writeback_loss` or an `unflushedLoss` in the mounts row) plus warn at startup | High |
| B-3 | ✅ **Fixed** (the B-3 as-built below). **`create -> read -> unlink` goes through with zero trace.** The audit pair of a pure cancellation is only on an in-memory queue, so a `kill -9` or a PG failure loses it = the evasion channel the design's audit section said it would explicitly close is back. The counts are small, so **writing it through at once when `audit.enabled`** is appropriate | High |
| B-4 | ✅ **Resolved** (B-4 below). **Turning `audit.enabled` off live makes every later unmount hit the deadline and exit 4.** `FlushOrphanAudits` returns early on `!auditEnabled` while the queue is still counted in `UnflushedCount()`, so **a "the following will be lost" report appears with zero actual loss** and systemd marks it failed | Medium |
| B-5 | ✅ **Fixed** (the B-5 as-built below). **The error state is reported through the heartbeat, so a failure that cannot write to the database leaves `status` showing a stale green.** Put the seconds since the heartbeat next to the red line plus write immediately on a transition | Medium |
| B-6 | ✅ **Fixed** (the B-6 as-built below). **The loss report cuts off at 32 entries and has no paths** (if the parent is pending too there is no row in the database and the path cannot be restored). Assemble the path plus emit "and N others (with the breakdown)" | Medium |
| B-7 | ✅ **Fixed** (the B-7 as-built below). **The error state does not stop a destructive operation** (`TruncateData` / `DeleteInode` / a replacing rename all get through). It becomes a state of "no new data is accepted but the old data keeps being deleted" | Medium |
| B-8 | ✅ **Fixed** (the B-8 as-built below). **A live settings change occupies the NOTIFY listener thread for up to `flush_timeout_ms`** (the two-phase flip's `FlushAll` and the back-pressure run on the spot). During that time **not one invalidation from another client is processed**. A heavy live application belongs on a dedicated worker | Medium |
| B-9 | ✅ **Fixed** (the B-9 as-built below). **A check-then-act remains in the two-phase flip** (the `MetadataWriteBack` decision and the `TryAdd` are not indivisible), so a pending entry can be born after the flip has completed. The intake flag is passed into `TryAdd` and judged inside the ledger lock. Along with it, **the effective mode (`intakeClosed`) does not show in the status** (it shows `on` during the flip) | Medium |
| B-10 | ✅ **Fixed** (the B-10 as-built below). **The Rekey of `EnsureFlushDataRow` does not check for a `chunk_size` mismatch** (`DirtyFile.ChunkSize` is `init`). If the winner row's chunk_size differs, the chunk boundaries shift and the data is destroyed. The probability is low (it presupposes a different `default_chunk_size` between clients) | Medium |
| B-11 | ✅ **Fixed** (the B-11 as-built below). **`NoteFlushSuccess` comes after the post-commit cleanup**, so an exception in the `Notify` and so on says "the flush failed" although it has committed, and the counter is not reset either. Move it to right after `tx.Commit()`. Along with it, **move the `Notify` outside the NSGate** (a hung database jams every metadata operation) | Low |
| B-12 | ✅ **Fixed** (the B-12 as-built below). **SIGTERM is always ignored**, so sending a second one during a long shutdown flush has no effect and only SIGKILL is left. The second and later ones are upgraded to "give up at once, emit the loss report and exit", plus the guidance `TimeoutStopSec > write_back_flush_timeout_ms` goes into the documents | Low |
| B-13 | ✅ **Done** (B-13 below). The documents: ~~`Mount.md`'s "the close of that fd is synchronous" disagrees with the as-built~~ (**`Mount.md` was updated to the as-built together with the A-4 to A-7 fixes**) / state the loss report's "at most 32" / add the concrete example `rm f; cp new f` to "causality between files" / state in `audit-log.md` that **the audit is not durable while write-back is enabled** | Low |

### The settled design plus the as-built of B-1 (**implemented**)

> The design of the knob `mount.write_back_metadata_exclusive_create`. **It was implemented as designed on
> 2026-09-19 and verified on the dev server on real hardware** (the differences are in the as-built differences
> below). Measured, **`rsync` is 3.28x**
> ([performance.md, the measurements of B-1 `defer`](performance.md)).
> The aim is to make selectable - **with the exclusion being lost stated explicitly** - a way out of
> "because `rsync` and `cp` use `O_CREAT|O_EXCL`, heuristic (c) drops everything to write-through and 1e's
> folding never happens once" ([performance.md, the effect of 1e stage 2](performance.md)).

##### The values and the default

| The value | What it means |
|---|---|
| **`write_through` (the default)** | The same as today. A create with `O_EXCL` is not made pending but created synchronously, leaving the exclusion decision to the database's unique constraint |
| `defer` | A create with `O_EXCL` becomes pending too. **The exclusion within one mount is kept and only the cross-client exclusion is lost** |

**The default does not change.** The cross-client exclusion is a step down that only someone who can say "it is
fine to lose it" should take, and taking it by default makes a filesystem where two clients can hold
`git index.lock` at the same time.

##### The type and the reload policy

- **`EnumField`** (a record holding an array of permitted values whose `Parse` throws on anything else) is added
  to `Field.cs`. Every entry point - the CLI, the TOML and the database - goes through `Parse`, so
  **the validation lives in one place**. A `StringField` plus a check at application time would make it possible
  to create a setting that "goes into the database but fails at mount time".
- The field definition: `Scope = "mount"` / `Key = "write_back_metadata_exclusive_create"` /
  `CliOptions = ["--write-back-metadata-exclusive-create"]` / `SaveTo = File` /
  `AppliesTo = Mount | Assign` / `DefaultFn = () => "write_through"`.
- **`Reload = Live`.** **No two-phase flip is needed** the way `write_back_metadata` itself needs one - this
  knob only changes where *creates yet to come* go, and does not change how the pending entries already in the
  ledger are treated (see "the mark is settled at create time" below). Neither a drain nor an intake stop is
  needed.

##### The mark is settled at create time (cutting off the race with a live change)

**`BornExclusive` (a bool, `init`)** is added to `PendingInode`, **burning in at creation time whether the
pending entry was made with `O_EXCL`**. The knob must not be re-read at flush time - re-reading it means that
the moment an inode created under `defer` is switched to `write_through` before its flush, it turns into "it is
fine to remove the other side under last-flush-wins", and **the promise made to the application that "you alone
created it" is broken after the fact**.

The path: `FileSystem.Create` (`fi.flags & O_EXCL`) -> `Api.CreateFile(..., exclusive)` -> `Api.InsertInode`.
The branch in `InsertInode` changes as follows.

```
today: if (MetadataWriteBack && !exclusive)                      → pending
B-1  : if (MetadataWriteBack && (!exclusive || ExclusiveDefer))  → pending  (exclusive is passed as far as TryAdd)
```

##### What is kept and what is lost

- **Kept (within one mount)**: `DirtyNamespace.TryAdd` returns null on a `(parent, name)` collision and the
  caller turns it into **EEXIST**. This does not change at all under `defer`. As long as the same mount is used,
  the semantics of `O_EXCL` are fully preserved.
- **Lost (across clients)**: another mount cannot see a pending inode (there is no row in the database), so
  **both `O_EXCL` creates return success**. That is exactly the price of `defer`, and it must not be hidden.

##### What happens on a collision (**the Windows-side acceptance point**)

A branch is added to `AdoptOrRemoveOccupant`, which resolves a name collision in the flush tx, so that
**it does not remove the occupier when `BornExclusive`** (the same treatment as a mismatched kind or a non-empty
directory = **throw and error-latch**).

| The situation | The behaviour |
|---|---|
| The winner (the side that flushed first) | **Untouched.** The row is in the database with its contents and attributes intact |
| The loser (the side that flushes later) | The flush keeps failing. The pending entry stays in the ledger and **its contents never reach the database** |
| The state of the loser's mount | Once the failures for that target reach `FlushFailureStateThreshold` (5) it goes into **the error state** = new writes and creates give `-EIO` and Layer 3 of `pgfsctl status` shows red. **Because A-2 separated the counters per target, the threshold is reached even while other files are succeeding** (this is where doing round A first shows) |
| The operational means of recovery | **`unlink` that file on the loser's side.** Deleting a pending inode is the pure cancellation of `CancelPendingInode`, so it leaves the ledger without touching the database and the error latch clears too (the delete path does not go through `ThrowIfWriteBackErrorState`, so it can be run during the error state) |

**No audit row appears.** The flush tx rolls back, so the audit cannot be written in that tx.
The fact of the collision shows in `DirtyNamespace.CountConflict`'s statistics, the Error log and the loss
report at unmount. (Leaving it in the audit needs the same mechanism as B-3's "write the orphan audit rows at
once" = a separate item.)

##### Where it sits, and the mechanisms that make operators notice

This knob is **"a tuning for a bulk copy where it is known to be single-client operation"**.
**Do not use `defer` if the same FS is used from several mounts.** The Windows-side confirmation (2026-09-19)
established that the only thing actually acting as cross-client mutual exclusion is
**the database unique constraint of `CREATE_NEW`** (because the Dokan adapter does not enforce the share mode
and `LockFile` / `UnlockFile` always return success; see [windows-parity.md](windows-parity.md)). `defer` is the
operation of pulling out that last remaining one.

Two things are therefore included in the implementation.

1. **It shows in the effective settings of `pgfsctl status`.** This is **not automatic** - `Api.BuildConfigJson`
   does not walk the schema but is **a hand-written dictionary**, so without explicitly adding the one line
   `["mount.write_back_metadata_exclusive_create"]` it does not appear in Layer 3 (the same treatment as the
   other `write_back_*`). It is the first place operations looks, so it is mandatory.
2. **When it starts with `defer`, a Warning is issued if another live mount is in `{prefix}mounts`.**
   The evidence (the registration and the heartbeat of `{prefix}mounts`) is held by Core, so it goes
   **on the Core side (the startup handling of `Api`)**. That way the same warning appears whether it starts
   from Dokan or from FUSE, and it does not go on the adapter side.
   **It does not enforce** (refusing the startup would stop legitimate single-client operation over a leftover
   registration row).

##### Out of scope

- **`mkdir` is not covered.** `Api.CreateDirectory` is still fixed at `exclusive: false` and this knob does not
  change it (making it synchronous means no pending directory is born and the ancestor-chain INSERT becomes
  unreachable - stage 2 as-built difference 5).
  A tool that uses `mkdir` as a locking primitive has already lost the cross-client exclusion
  **the moment `write_back_metadata = on`**. That is a property that predates this knob and is already written
  in the visibility line of [Mount.md](../Mount.md).
- The handling of creates other than `O_EXCL` (`plain_create` / `O_TRUNC`) does not change.

##### The documents to update along with the implementation

`settings-matrix.md` (the matrix of every setting) / the defaults table and the TOML example of `Mkfs.md` /
`pgfs.toml.example` / **the visibility line** of the "what is lost" table in [Mount.md](../Mount.md) (it says
`O_EXCL` is created synchronously so the exclusion is kept, so the exception for `defer` is added) /
[Pgfsctl.md](../Pgfsctl.md) (because another item shows in the status).

##### The as-built differences (what the implementation turned up)

There is exactly one difference from the design; everything else went in as designed.

1. **The "another running mount" decision of the `defer` warning is naive.** It only counts the rows of
   `{prefix}mounts`, so **it counts the leftover rows that an abnormal exit (a `kill -9` and so on) did not
   DELETE**. In fact, on the dev environment where tests `kill -9` repeatedly, it said "there are 91 other
   running mounts" (the actual number running was 1).
   **It ought to be narrowed by the freshness of the heartbeat**, but cleaning up the leftovers of
   `{prefix}mounts` has not been started at all (the status display has the same problem), so
   **fixing this item alone would not be consistent**. It is handled together with the leftover cleanup.
   An excessive warning does no real harm (it does not refuse), so it is left as it is.

The verification (the dev server on real hardware, the schema `pgfs_test`, Citus rf=2):
the Linux e2e suite is **43 passed + 1 skip in all three of `write_back_metadata` off / on / on+defer**,
`writeback.sh` 8/8, `wbmeta.sh` **20/20** (17 -> 20) and `negcache.sh` 7/7.

##### The test plan

Three cases in `tests/linux/wbmeta.sh`. The cross-client case can be made deterministically **without standing
up two mounts, by fault injection that INSERTs the colliding row directly with psql** (the same trick as the
existing `test_meta_error_state_blocks_and_clears`).

1. `test_meta_exclusive_create_defer_is_pending` - that an `O_EXCL` create under `defer` has no row in the
   database right after the close (= it became pending). It pairs with the default `write_through`'s
   `test_meta_exclusive_create_is_write_through`.
2. `test_meta_exclusive_create_defer_same_mount_eexist` - that **a second `O_EXCL` create within the same mount
   is EEXIST even under `defer`** (the regression for "only the cross-client one was lost").
3. `test_meta_exclusive_create_defer_conflict_latches` - INSERT a row with the same name with psql while it is
   pending -> prompt a flush -> that **the occupier's row is not removed** plus that the loser enters the error
   state plus that **an unlink recovers it**.

Re-measuring the performance (an `rsync` under `defer`) is separate, after the implementation. The measurement
conventions follow "the measurement premises" of
[performance.md, the effect of 1e stage 2](performance.md) (**wait for the daemon to disappear** /
**check the integrity with md5 every time**).

### The B-12 as-built (**implemented**)

> **A second stop signal aborts the persistence of the flush.**

**The problem**: `mount.pgfs`'s SIGTERM / SIGINT handler **always swallowed them, however many arrived**
(`ctx.Cancel = true` / `e.Cancel = true`). The shutdown flush persists for the default
`mount.write_back_flush_timeout_ms` = 30000 ms, during which it looks like "a process that will not stop".
An operator who cannot wait is left with **only SIGKILL**, and SIGKILL leaves **neither a loss report nor the
`{prefix}mounts` gravestone (B-2)** - the information disappears exactly when it is most wanted.

**The fix** (`src/mount/src/Program.cs`): the stop signals are counted and treated in stages. SIGINT and SIGTERM
use **the same counter** (to an operator they are the same "stop", and it would be surprising for sending two of
different kinds not to advance the stage).

| The count | What it does |
|---|---|
| The 1st | A graceful unmount as before (`LazyUnmount`) |
| The 2nd | **`Api.AbandonFlush()`** aborts the persistence plus another `LazyUnmount`. It breaks out at the next deadline check and **emits the loss report before** exiting 4 |
| The 3rd and later | It stops swallowing the signal (`Cancel = false`) = .NET's default immediate exit. By this point the reporting path is given up |

`Api.AbandonFlush()` only raises `flushAbandoned` (volatile) and **does not interrupt the tx in flight**
(interrupting it could leave the database half-done). What it affects is **the deadline check**, and for that
`DeadlineReached` was changed from a `static` to an instance method so that it unconditionally returns
"the deadline is reached" when `flushAbandoned` is raised. The checks in `FlushAllForShutdown` /
`FlushAllPendingInodes` / the data loop of `FlushAll` / `ApplyInodeBackPressure` **all go through the same
function**, so the abort takes effect at whatever stage it is in. `ReportUnflushedLoss` is always reached on the
abort path too (**the 2nd press means "stop waiting", not "throw it away silently"**).

**The flag stays raised.** There is no reason to resume persisting after being told "do not wait any more".

**The systemd guidance**: `TimeoutStopSec` should be **longer than `mount.write_back_flush_timeout_ms`**.
Too short and systemd fires a SIGKILL right after the SIGTERM, which loses this staging and the loss report
alike. (For example, `write_back_flush_timeout_ms = 30000` goes with `TimeoutStopSec=60s`.)

**No automated test was written.** Making "a second signal in the middle of a long shutdown flush"
deterministic requires artificially slowing down the flush, and only a timing-dependent test could be written
(the same judgement as B-8 and B-9 (1)).

**It was confirmed by hand on real hardware** (the dev server, the schema `pgfs_test`, Citus rf=2,
2026-09-19): started with
`--write-back --write-back-metadata --write-back-interval-ms 600000 --write-back-flush-timeout-ms 600000`,
1200 empty files were created (= holding pending entries with no background flush coming), then
`kill -TERM` and another `kill -TERM` a second later. The log said

```
[Information] SIGTERM received. Attempting to unmount.
[Warning]     SIGTERM received twice. Abandoning the wait for the flush (whatever is unflushed will be lost).
[Warning]     write-back: an abort was requested, so the flush at unmount is stopped (1154 remaining)
[Error]       write-back: 1154 unflushed entries could not be flushed within the deadline. …
```

and **it aborted 4 ms after the second press and the process was gone within a second** (the deadline was set to
600 seconds). The gravestone of B-2 in `{prefix}mounts` carried `unflushedLoss: 1154` with a `lost` array
including the paths, confirming that **the report survives even on the abort path**.

### The Windows wiring of B-12 (**implemented**)

> **Making the second stop signal work on Windows too.** Core's `Api.AbandonFlush()` is called from
> `assign.pgfs`.

**What digging in turned up**: on Windows it was not that "the second was swallowed" but that
**there was no handler during the flush**. The subscription to `Console.CancelKeyPress` existed only inside
`Pgfs.Dokan.FileSystem.Run()` and was released in the `finally`. The first press returns from `Run()` at once,
so **the longest stretch - the shutdown flush of `api.Dispose()` (the default
`mount.write_back_flush_timeout_ms` of 30 seconds plus the retries) - was unguarded**, and a Ctrl+C arriving
there gave .NET's default immediate exit. In other words, an ending equivalent to Linux's SIGKILL (with neither
a loss report nor the `{prefix}mounts` gravestone (B-2) nor exit 4) **was happening on two Ctrl+Cs**.

**The fix**: the subscription was **lifted into the tool executable (`src/assign/src/Program.cs`)** so that one
handler covers from the mount until `api.Dispose()` completes. The number of stages, their meaning and the log
wording were made identical to `mount.pgfs` (Linux):

| The count | What it does |
|---|---|
| The 1st | A request to unmount (`FileSystem.RequestStop()`) |
| The 2nd | **`Api.AbandonFlush()`** aborts the persistence. It breaks out at the next deadline check and **emits the loss report before** exiting 4 |
| The 3rd and later | It stops swallowing and returns to .NET's default (an immediate exit). By this point the reporting path is given up |

Along with it, `FileSystem.SignalStop` became a **public `RequestStop()`** (it does nothing from the second call
on, so calling it after disposal is harmless = the handler does not have to care whether it is alive), and the
subscription was taken out of `Run()`. On the exception path too, `api.Dispose()` is fired **before the
subscription is released** (the disposal of `using var apiLifetime` comes after the `finally`, so only that
stretch would go back to having no handler).

**The Windows-specific pitfalls** (where it does not line up with Linux):

- **The console's X button, a logoff and a shutdown do not go through this handler.** The `CTRL_CLOSE_EVENT`
  family is cut off by Windows in **about 5 seconds**, which does not fit the default 30-second flush deadline.
  There is nothing to tune that corresponds to Linux's `TimeoutStopSec`, so **if a long flush is expected, the
  operational rule is to stop it with Ctrl+C rather than the X button**.
- **`Stop-Process` is a `TerminateProcess`** and runs neither the handler nor the Dispose (= the equivalent of
  `kill -9`).
- **Ctrl+Break comes to the same handler** (`ConsoleSpecialKey.ControlBreak`). That `e.Cancel = true` works for
  Break too was confirmed by measurement.

**No automated test was written** (the same judgement as Linux's B-12 - making it deterministic requires
artificially slowing down the flush, and only a timing-dependent test could be written). In addition, on Windows
it was measured that **a Ctrl+C fired with `AttachConsole` plus
`GenerateConsoleCtrlEvent(CTRL_C_EVENT)` does not reach the target** (`GenerateConsoleCtrlEvent` returns true
and the target's console attaches correctly, yet the target does not react).
**A `CTRL_BREAK_EVENT` does get through**, so the confirmation on real hardware was done with that. Automating
it would have to start from that difference.

**It was confirmed by hand on real hardware** (the Windows machine / Dokan 2.3.1 / PG = the `pgfs` schema on the
server, Citus rf=2): started with
`--write-back --write-back-metadata --write-back-interval-ms 600000 --write-back-flush-timeout-ms 600000`
(= no background flush coming), 1200 empty files were created to accumulate pending entries, then Ctrl+Break
twice.

```
[Information] Ctrl+Break received. Attempting to unmount.
[Warning]     Ctrl+Break received twice. Abandoning the wait for the flush (whatever is unflushed will be lost).
[Warning]     write-back: an abort was requested, so the flush at unmount is stopped (1057 remaining)
[Error]       write-back: 1057 unflushed entries could not be flushed within the deadline. The following will be lost…
[Error]         a pending file about to be lost: /abandon_probe/f00168 (id:9443 size:0 state:Dirty error:(none))
[Information] exited (code 4)
```

**It exited 152 ms after the second press (22:45:47.370)** (the deadline was set to 600 seconds), and the
gravestone in `{prefix}mounts` carried `unflushedLoss: 1057` (which `pgfsctl status` picks up as
`!! write-back UNFLUSHED LOSS`).
**exit 4 reaches the caller on Windows too** (assign is a foreground process).

### The B-11 as-built (**implemented**)

> **Settling the success right after the commit** plus **moving the notification (`pg_notify`) outside the
> NSGate.**

##### (1) Settling the success right after the commit

**The problem**: in both `FlushLocked` and `FlushPendingChain`, `NoteFlushSuccess` came
**after the post-commit cleanup (the Rekey, the cache invalidation, the notification)**. An exception in the
cleanup gave a "the flush failed" log **although it had committed**, and in `FlushLocked` it went as far as
`NoteFlushFailure`. **A situation where the cleanup fails permanently could manufacture a failure with no
substance: 5 rounds into the error state = new writes give `-EIO`.**

**The fix**: on both paths, **`NoteFlushSuccess` moved to right after the commit (before the cleanup)**.
`FlushLocked` had the range of its `try` **narrowed to the commit** and the cleanup moved outside the `catch`
(so that the structure guarantees an exception in the cleanup never goes through `NoteFlushFailure`).

##### (2) Moving the notification outside the NSGate

**The problem**: `Notify` is a **database round trip** through `pg_notify`. The post-commit cleanup of a
metadata flush (`FinishPendingFlush`) calls it **while still holding the NSGate**, so for as long as the
database is hung, the NSGate is held and **every metadata operation jams with it**.

**The fix**: the sending side of the notification went onto **a dedicated worker plus a queue** (the same shape
as B-8's control worker). `Notify` only pushes a `NotifyMessage`, and only the worker thread touches the
database. **With a single consumer the send order is preserved.** The send is after the commit on every path, so
the order "the notification goes out before the commit" is never created.

- **The queue is bounded (1024) and drops when it is full.** The producer may be holding the NSGate or a
  `DirtyFile.Gate`, so **it must never be made to wait**. A dropped notification only leaves another client's
  cache stale (`NotifyChannel.Publish` itself is designed to swallow failures), and dropping when it is jammed
  is the right thing. The drops are left in a warning with a running total (thinned to once every 10 seconds).
- `Dispose` closes the queue and waits for the worker (up to 2 seconds) **before closing the `notifyChannel`**,
  so that the backlog is all fired.

**A caution for regressions**: the notification is asynchronous now, so **the delay before it reaches another
client grows by the worker's scheduling**. The cross-client visibility tests already have waits so they are
unaffected, but do not write a test that assumes "an operation is immediately visible on another client".

### The B-10 as-built (**implemented**)

> **Do not write if the `chunk_size` of the flush target differs from the local division.**

**The problem**: the Rekey of `EnsureFlushDataRow` (the path that loses the race for a data_id and re-points at
the winner's row) **did not look at the winner row's `chunk_size`**. The local dirty data is already divided by
`DirtyFile.ChunkSize`, and `ChunkSize` is `init` so it cannot be adjusted afterwards. UPSERTing per
`chunk_index` into a row with different boundaries **overwrites a different position with a different length** =
silent data destruction.

**The fix**: before re-pointing, the winner row's `chunk_size` is read and, if it differs from the local one, a
`FlushChunkSizeMismatchException` is thrown to **fail the flush** (the dirty data stays in hand). The same check
was put on the "the row already exists" path too (that row may have been created by a write-through or by
another client; the `chunk_size` has already been read, so there is no extra cost).

**Failing rather than discarding is the point.** It is a configuration mismatch so a retry will not fix it, but
raising it into consecutive failures and then the error state to **make operations notice** is better than
breaking it silently (the message says "make `file_system.default_chunk_size` consistent").

**No automated test was written.** `chunk_size` is a per-row column of `{prefix}data` while
`file_system.default_chunk_size` is FS-wide (stored in the database), so **standing up clients with different
divisions against the same FS at once** cannot be arranged from a shell. It was agreed with the Windows side
that it is "watched on the Linux side; Windows only runs the regression of the existing suites".

### B-13 (**the documents**)

| The target | What went in |
|---|---|
| [Mount.md](../Mount.md), the write-back section | That the loss report's **ceiling of 32 is per pending and per dirty** (the rest is a breakdown summary) / the concrete example **`rm f; cp new f`** under "causality between files" / the staging of the stop signals (B-12) and the `TimeoutStopSec` guidance |
| [audit-log.md](audit-log.md) | **The audit is not durable while write-back is enabled** (the audit rows of a pending inode only enter the database in the flush tx, so whatever was not `fsync`ed is lost along with the operation in a crash. An `op = writeback_loss` is a record that something was lost, not a record of the operation itself) |
| ~~`Mount.md`'s "the close of that fd is synchronous"~~ | Updated to the as-built together with the A-4 to A-7 fixes |

### Making the live disable of the data write-back two-phase (**implemented**)

> B-9 made only the metadata side two-phase, so **the data side (`mount.write_back`) was brought into the same
> shape**. The original finding was "the live off of the data write-back drops the mode before the drain
> succeeds".

**The problem**: the live off of `mount.write_back` was **one phase** (assign `WriteBack = false` and then
`FlushAll`). It has two holes.

1. **A write running concurrently with the flip falls into the window of "the mode is off yet it piles up dirty
   data"** (the same shape as the metadata side's B-9 (1)).
2. **When a flush fails, the dirty data is left behind.** The background loop `RunFlushLoopAsync` stops the data
   flush with `if (!Mount.WriteBack) { continue; }`, so **no flush is ever triggered again**.
   On top of that, `UseChunkCache` is `contentCache.Enabled || Mount.WriteBack`, so with
   `mount.cache_data_max_bytes = 0` **a `read` on the same mount returns the stale contents from the database**
   (there is newer dirty data in memory, but it is not consulted).

**The fix**: `ApplyWriteBackLive(bool)` was added and the off was made three stages.

| The phase | What it does |
|---|---|
| (1) | `dataIntakeClosed = true` - later `WriteData` calls flow to **write-through**. `PublishStatsNow` at once |
| (2) | `FlushAll(deadline)` writes out the dirty data in hand (the deadline = `mount.write_back_flush_timeout_ms`, so that one settings change does not block forever) |
| (3) | `WriteBack = false` then `dataIntakeClosed = false`, in that order. **If any dirty data is left, `dataDrainPending` is raised.** `PublishStatsNow` at once |

**`dataDrainPending` is the point.** While it is raised, the state is "the setting is off but only the drain
continues": (1) the background loop keeps turning `FlushIdle(TimeSpan.Zero)`, (2) `UseChunkCache` stays true and
(3) `FlushBeforeMetadataWrite` / `PrepareTruncateWriteBack` keep working as before.
`ClearDrainIfEmpty` lowers it once the dirty data has drained. **"Do not turn only the setting off and leave the
dirty data behind"** is the difference from B-9, and it is left in an `Error` log too.

The `write-back` line of `pgfsctl status` also shows **`(intake closed = effectively off)`** /
**`(draining = there is unflushed data)`** (the snapshot keys are `intakeClosed` / `effective` /
`drainPending`).

**The test**: `test_live_off_flushes_and_publishes_effective_mode` in
[writeback.sh](../../tests/linux/writeback.sh) (the 9th of 8 -> 9). **Confirmed to FAIL against a build put back
to one phase.**

**A pitfall hit while writing the test**: **with 1d alone, the flip cannot be fired from a shell while holding
dirty data.** The `close` is a flush trigger, and **a FUSE `Flush` flies on every close of a duplicated fd**, so
repeating `exec 9> f; printf ... >&9` **runs a flush every time** (measured: 8192 `printf`s gave
`flushes = 8192` and took 10 minutes). The test therefore **uses the metadata write-back alongside it to
accumulate 400 pending inodes and deliberately lengthen the second phase's `FlushAll`**.

### The B-9 as-built (**implemented**)

> **Crushing the check-then-act** of the two-phase flip plus **showing the effective mode in the status**.

##### (1) Deciding the intake stop inside the ledger lock

**The problem**: **the flip can complete** between `InsertInode` reading `MetadataWriteBack` (= the intake-stop
flag plus the setting) and reaching `TryAdd`, so **a pending entry can be born after the flip has finished**.

**The fix**: the intake-stop flag moved from a `volatile bool` on `Api` into **`DirtyNamespace` (under the
ledger lock)**, and **`TryAdd` makes the decision inside the lock and rejects**. The flip side takes the same
lock through `SetIntakeClosed`, so the decision and the registration become indivisible and the window is gone.

So that the caller can tell why `TryAdd` returned null, it now returns a `PendingAddResult`
(`Added` / `NameConflict` / `IntakeClosed`). **A name collision is EEXIST and an intake stop is a write-through
fallback**, and mixing them up breaks one or the other (dropping a name collision to write-through creates two
inodes with the same name - the destruction that stage 1.5 closed).

##### (2) Showing the effective mode in the status

**`intakeClosed`** and **`effective`** were added to `{prefix}mounts.stats.writeBackMetadata`, and
`pgfsctl status` says `write-back(m): on (intake closed = effectively off)`.

**Along with it, the heartbeat is written immediately on a transition** (the same thinking as B-5). The stats
only reach the database on the heartbeat (30 seconds), so without it **the status tells the lie "on" for the
whole flip** - **the information one most wants to see when the flip is long is exactly what cannot be seen
then**.

**The test**: `test_meta_live_flip_publishes_effective_mode` (the 27th). By **deliberately slowing the flip
down** (making it hold 800 pending entries; on Citus one entry is a little over 10 ms, so the first phase lasts
several seconds), the intake stop showing in the status is observed deterministically. Against the pre-fix code
it FAILs with "the lie of on for the whole flip".

**No automated test was written for the check-then-act of (1) itself.** There is no way to hit the window
deterministically from a shell and only a timing-dependent test could be written (the same judgement as B-8).
The indivisibility is guaranteed structurally.

**A pitfall hit while writing the test**: the state was fetched with a scalar select, so together with
**a fresh heartbeat row another test had left** in `{prefix}mounts`, several rows came back and the comparison
could never hold (it failed only on a full run). It was fixed to **look with a `count` at whether at least one
matching row exists**. The same trap had been hit in B-5's test and made a count, and it was written back here.

### The B-8 as-built (**implemented**)

> Getting a heavy live settings change **off the NOTIFY listener thread**.

**The problem**: the `reload` / `set` / `ping` control messages were **executed as they were on the NOTIFY
callback**. The two-phase flip of `write_back_metadata` (the `FlushAll` plus the back-pressure) takes up to
`write_back_flush_timeout_ms`, during which **not one invalidation from another client is processed**.

**The fix**: a single-consumer worker (`BlockingCollection<ControlWork>` plus a `Task`) was put on `Api` and
**only the control messages** are routed to it. **The data-change notifications (the invalidations) stay
inline** - they are light and latency-sensitive.

* **The order is preserved** (a single consumer). Two `set`s are applied in the order they arrive.
* **The contract does not change.** `pgfsctl config set` never took an ack and only reports the number fired as
  a "most recent" value.
* **The worker is never let die on an exception** (it swallows and carries on). Letting it die would make every
  later live change ineffective.
* It warns when the queue goes past `ControlQueueWarnDepth` (32).
* `Dispose` does a `CompleteAdding` and joins for up to 2 seconds. An enqueue after it is closed is silently
  dropped (it is shutting down).

##### About the tests (written honestly)

**No new automated test was added.** There is no way to observe "the listener is not occupied" deterministically
from a shell, and only a timing-dependent test could be written. **The property is guaranteed structurally**
(no heavy application exists on the callback path). The regression is watched by the existing live-settings
tests (`wbmeta.sh`'s `test_meta_live_off_flushes_pending_then_write_through` and `negcache.sh`'s
`test_live_reload_off_clears`).

Two measurements that were thrown out are also recorded: (1) the response time of an `ls` right after a
`config set` was measured, but **the FUSE request path was always separate from the listener thread** so it
proves nothing; (2) the thread ids in the log were compared before and after the fix, but **thread ids change
from run to run** so they are no evidence for an A/B.

### The B-7 as-built (**implemented**)

> Stopping **destructive operations** during the error state. But the split is not "make unlink an exception"
> but **"an operation that throws away a pending entry was never destructive in the first place"**.

##### The rule

| The operation | During the error state | The reason |
|---|---|---|
| An unlink / rmdir of a pending inode | **Allowed** | A pure cancellation = **it does not touch the database at all**. The pending count drops so the situation improves. **This is B-1's means of recovery** |
| A truncate of a pending entry | **Allowed** | It only throws away the dirty data in memory |
| An unlink / rmdir of a **persisted** inode | **Stopped** (`-EIO`) | "No new data is accepted yet the old data keeps being deleted" is exactly this |
| A truncate of a **persisted** entry | **Stopped** | Shrinking destroys the existing chunks and growing is new data. Neither is accepted |
| A rename-over-existing (**the target is persisted**) | **Stopped** | What is replaced disappears |

The decision is in one place, `ThrowIfErrorStateBlocksDestroy(target, what)`, called at the entry to
`DeleteInode` / `TruncateData` / `Rename` (with a replacement). With `mount.write_back` off it passes straight
through (the writes go directly to the database so there is no reason to stop them; the same escape hatch as
`ThrowIfWriteBackErrorState`).

##### The accepted side effects (stated in the documents)

* **`rm` / `rmdir` / `truncate` can return `-EIO`.** It is new behaviour on both Linux and Windows.
* **`rm -rf` partially fails on a directory mixing pending and persisted entries** (the pending children are
  removed and it stops at a persisted child). It looks half-done, but **"do not silently remove what is being
  held" was given priority**.

##### Why "unlink is an exception" was not chosen

It collides head-on with B-1's means of recovery (unlinking the loser of a collision), so **it is a place where
one is tempted to make an exception**. But what is being recovered is **by definition pending** (it collides
precisely because it is not in the database yet), so organizing it as **"an operation that throws away a pending
entry was never destructive"** removes the need for an exception. Stacking up exceptions makes "why is only this
one allowed" unreadable later.

##### `Api.CanDestroy` (added, at the Windows side's request)

The decision was also exposed as **a side-effect-free predicate**. `ThrowIfErrorStateBlocksDestroy` now just
calls it.

**Why it is needed**: Dokan's `Cleanup` is `void` and cannot return `Api.DeleteInode`'s exception to the caller,
so **`Remove-Item` returns successfully although nothing was removed** (the same lie as a fail-open). To refuse
at `DeleteFile` / `SetEndOfFile` / `MoveFile`, which can return a value, there has to be a way to ask before
acting.

**The same decision must not be assembled on the adapter side.** All that is visible from outside is
`WriteBackErrorState`, and **"whether it is pending" is Core's internal state**. Judging on the error state
alone **stops even the deletion of a pending entry and blocks B-1's means of recovery** (the shape hit once in
`SetFileAttributes`).

**The test**: `test_meta_error_state_blocks_destroy_but_allows_cancel` (the 26th).
**That the unlink and truncate of a persisted entry are stopped** and **that the unlink of a pending entry gets
through** are watched in the same test (with only one of them, getting the rule backwards still comes out
green). Measured: `rm: cannot remove '...': Input/output error` / truncate `errno=5` / the `rm` of a pending
entry succeeds / the create after it succeeds too.

### The B-6 as-built (**implemented**)

> Giving the loss report **paths** plus **a breakdown of what was cut off**.

| Before | After |
|---|---|
| `id:93519 parent:93518 name:f0.txt kind:file size:3 …` | `a pending file about to be lost: /mp2/many/f0.txt (id:93519 size:3 state:Dirty error:…)` |
| Silently cut off at 32 | `… and 9 others (41 pending in total = 1 dir / 40 files, 120 logical bytes)` |
| The dirty data had no path | `dirty data about to be lost: /mp2/many/f0.txt (data_id:… chunks:1 …)` plus `… and 8 others (40 files / 40 chunks of dirty data in total, 120 logical bytes)` |

* **The path is assembled with `InodeCache.TryGetPath`.** A pending inode is pinned, so it and its pending
  ancestors are certain to be in the cache, but **a persisted ancestor that has fallen out of the LRU cannot be
  resolved**. In that case it becomes `?/name` so that **it is clear that it could not be restored** (saying it
  could not be resolved is better than emitting a plausible but false path).
* **The breakdown does not stop at "and N others".** The total, the split of dirs and files and the logical byte
  count are all emitted (a rounded number cannot be reconciled or corrected by whoever receives it).
* **The records on the database side (B-2's gravestone `stats.lost` and the audit's
  `writeback_loss.detail.lost`) hold the same strings.** What operations actually looks at is the latter rather
  than the log, so the same thing goes in both. The dirty data goes in separately as `lostData` / `lost_data`.
* The formatting was put on the `Api` side. `DirtyNamespace` / `DirtySet` **only return a snapshot under the
  lock**, and the path resolution (which touches the `InodeCache`) happens outside the lock (touching the cache
  from inside the ledger lock could create a path where the lock order is inverted).

**The test**: `test_meta_loss_report_has_paths_and_breakdown` (the 25th). **Colliding a pending directory with a
mismatched kind** makes the pending children under it unable to be materialized as well, so one injection
creates 41 losses. The verification reads B-2's gravestone (`stats->'lost'`) - it avoids digging through a log
file and checks **the very record on the database side that operations actually looks at**.

### The B-5 as-built (**implemented**)

> Writing an error-state transition into `{prefix}mounts` **without waiting for the heartbeat period
> (30 seconds)**, and putting **the freshness of that information** next to it on the `pgfsctl status` side.

##### What cannot be fixed (said first)

**With a failure that cannot write to the database at all, the transition obviously cannot be written
immediately either.** In that case `status` keeps showing the last value that arrived (= a stale green). That
cannot be avoided in principle, so it was addressed on the side of **making the reader notice it is stale**.

##### What went in

| The path | What it is |
|---|---|
| **Writing immediately on a transition** | A `WriteHeartbeat()` is fired the moment it **enters or clears** the error state. **It is called outside the lock** - doing database I/O under `flushFailureGate` would stop the failure-counter updates too when the database jams. `NoteFlushFailure` / `NoteFlushSuccess` / `ClearFlushFailure` therefore receive "did it transition" as a bool and write after leaving the lock |
| **Freshness next to the red line** | It says `!! write-back ERROR STATE (since ...) **[from a heartbeat 42s ago]**`. The error state only arrives through the heartbeat, so if the heartbeat is old, so is this red line |
| **Making stale red** | The `[stale]` on the host line of Layer 3 became `[stale: heartbeat 5m ago]` and **red**. With a failure that cannot write to the database it looks green with no red line, so knowing "these numbers are old" is the only clue |

##### The two pitfalls hit in the tests (both defects in the tests)

1. **`mount_pid` was grabbing the calling shell.** `pgrep -f "mount\.pgfs .*$SETTING_FILE"` also matches a shell
   containing the same string, and in practice 3 things matched and `head -1` returned **the shell's pid**.
   `unmount_crash` `kill -9`s that, so it **could have taken out an unrelated process**.
   It was changed to `pgrep -x mount.pgfs` (the process name) (in all three of `wbmeta.sh` / `negcache.sh` /
   `writeback.sh`). It is exactly the same `pgrep -f` trap that was hit in B-1's performance measurement, and
   **the same trap was buried in the tests too**.
2. **A directory's id was looked up by name alone.** The `~/mnt/pgfs/b5` made during the manual verification and
   the test's `~/mnt/pgfs/wbmeta/b5` had the same name, giving 2 rows, and `limit 1` grabbed the former so the
   injection had no effect and **it failed only on a full run**. It was changed to look it up with the parent
   restricted to `TEST_ROOT`.

### B-4 (**resolved as a side effect of B-3, plus fixing a latent trap**)

**The symptom had already stopped occurring.** B-4 was the finding that "the orphan audit queue is not written
when `audit.enabled = false` yet is counted in `UnflushedCount()`, so **the unmount hits the deadline, emits a
loss report and exits 4 with zero actual loss**", but **B-3 removed the queue's only producer
(`QueueCancelAudits`)**, so the queue is always empty and this path is never entered.
**This was confirmed by reading the code** (the remaining caller is only `FlushOrphanAudits` re-queueing its own
failures = self-referential, so it is empty forever).

The hole was still there as a construction, though, and was fixed: the `if (!this.auditEnabled) { return; }` at
the head of `FlushOrphanAudits` was removed. There are two reasons.

1. The rows queued there **were captured while the audit was enabled**, so turning it off afterwards is no
   reason not to write them. The moment a path that queues is added in the future, B-4 comes back.
2. Worse, it makes **`config set audit.enabled false` a means of erasing evidence that has already been
   captured**. The capturing side is still stopped by `auditEnabled` as before, so turning it off does stop the
   audit from growing.

**The test**: `test_meta_audit_live_off_does_not_fake_loss` (the 23rd). Turn the audit on and "create and
delete", turn it off live with `pgfsctl config set audit.enabled false`, "create and delete" once more and
unmount cleanly, then detect a fake loss by **there being no B-2 gravestone**. It cannot happen structurally
today, so it is **a regression guard against reopening the same hole when a path that queues is added in the
future**.

### The B-3 as-built (**implemented**)

> Writing the audit pair of a pure cancellation **to the database on the spot**. It actually closes the evasion
> channel the design's audit section said it would close, that "`create -> read -> unlink` goes through with
> zero trace".

**What was happening before the fix (measured)**: with `write_back_metadata = on` plus `audit.enabled`,
"create, read and delete" within the interval left the audit rows **only piled on the in-memory orphan queue**.
Measured on the dev server, there were **already 0 audit rows before the `kill -9`**, and 0 after it too.
In other words, without even waiting for a crash, **dropping it before the background flush runs leaves no
trace**.

**The fix**: `CancelPendingInode` writes the create/delete pair **synchronously in one tx** through
`WriteCancelAudits`. The count is only "files created and deleted within the interval", so the cost of making it
synchronous is effectively zero.

What was decided:

* **If it cannot be written, the cancellation itself fails** (an exception -> `-EIO`). It is the same
  "if the audit cannot be kept, the operation does not happen" policy as the write-through audit, and if the
  database is down, the unlink of a persisted inode fails likewise, so the behaviour is consistent.
* **It is called before `Forget`ting from the ledger.** Removing it first would leave the state of "it is gone
  and there is no trace" if the write failed.
* **The partition ensure is outside the tx.** On Citus, DDL inside a distributed write tx is refused, and
  putting it inside the tx jams the whole mount waiting on `lock_timeout` (following the finding of stage 1.5 as
  it is).
* The orphan queue (`QueueOrphanAudits` / `FlushOrphanAudits`) itself **is kept**. But **this fix removed the
  path that queues onto it** - `QueueCancelAudits` was its only producer, so the current caller is only
  `FlushOrphanAudits` re-queueing its own failed writes
  (**it was first written here that "there is still a path with no synchronization point", which was an error
  written without checking**; it was corrected when B-4 was looked at). It is kept as the
  receptacle for the re-queueing, and as the landing place for a path with no synchronization point if one is
  added in the future.

**The test**: `test_meta_cancel_audit_is_written_immediately` in `tests/linux/wbmeta.sh` (the 22nd).
**Checking that the 2 create/delete rows are in the database before the unmount** is the crux: a test that only
looks after a `kill -9` cannot tell it apart from "the background flush simply got there in time".
The test turns `audit.enabled` on temporarily and puts it back (it skips on an FS with no audit table).

### The A-2 / A-9 follow-up fix: the failure counter of a target that is gone (**found on real Windows hardware**)

> The "per-flush-target consecutive-failure counter" that went in with round A **had a place where it was not
> cleared**. It was **found in the two-real-mount verification of the Dokan side** and reproduced on
> Linux with the same procedure.

**The symptom**: a pending entry error-latched by a name collision under `defer` **does not recover on an
`unlink`**. The pending entry itself goes and the unmount's exit goes back from 4 to 0, yet
**the error state does not clear and later creates stay `-EIO`**.

**The cause**: the condition for clearing the error state is derived from the state of the counters
("not one target has reached the threshold") (the A-9 fix), yet **the counter was not cleared when the target
disappeared**. The counter of a target that can never succeed again stays at the threshold and the clearing
condition never holds. It is **exactly the same shape of omission** as A-4's "the synchronous-close mark is not
cleared on a delete or a cancellation" (the discipline of "when the target is gone, clear the mark too" had gone
into only one of them).

**The fix**: `ClearFlushFailure(inodeId, dataId)` was added and is called on **every path where a target
disappears** - the pure cancellation (`CancelPendingInode`) / the discard (`FinishPendingDiscard`) /
discarding dirty data (`DiscardDirtyData`) / discarding a flush because the inode is gone (`FlushLocked`) /
a write-through delete (`DeleteInodeThrough`). The clearing decision was factored into
`ReevaluateErrorStateLocked` and shared with the success path.

**The lesson about the tests**: the existing `test_meta_exclusive_create_defer_conflict_latches`
**only looked at the `unlink` returning 0**, so it let this defect through.
**The recovery is now watched by "the next operation getting through"** (a create during the latch is refused ->
`unlink` -> the create gets through). Along with it, `fsync` is now fired until the error-state threshold
(5 in a row on the same target) is passed (with only one, it latches but does not enter the error state and the
recovery assertion means nothing).

### The B-2 as-built (**implemented**)

> **Leaving the loss on the database side and warning on the terminal at the next mount.** exit 4 itself cannot
> be made to arrive, so the reporting path was moved to the database and the log.

##### What could not be fixed (said first)

**exit 4 cannot be made to reach the caller.** A daemonized `mount.pgfs` has its parent **exit first** with `0`
at the point the mount is established, so when the child returns `4` at an unmount hours later, there is nobody
waiting. There is nothing to be done here other than "provide a different path that does arrive", and the three
below are that substitute.

##### What went in

| The path | What it is |
|---|---|
| **The gravestone in `{prefix}mounts`** | An unmount with a loss **does not DELETE its row but leaves it**. `ended` / `endedAt` / `unflushedLoss` / `lost` (up to 32 entries) go on the `stats`. **No column is added** - the DDL of this table is concentrated in mkfs and adding a column would require a re-mkfs on an existing FS. `stats` is JSONB so it works on an existing FS as it is |
| **The audit row `op = writeback_loss`** | One row per unmount. The subject is the mount itself, so `target_id` is null and the mountpoint goes in `name`. The count, the breakdown and the `timeout_ms` go in `detail`. **It is not written if `audit.enabled` is off**, in which case the gravestone is the only clue |
| **A warning at the next mount** | **On the parent process's (pre-fork) stderr.** The child runs after throwing away stdout/stderr, so the child's log reaches nobody unless `--log-output` was given. The only thing with a terminal is the parent before the fork (`StatusAdmin.WarnPastLossToConsole`). The Api emits the same content in an Error log too, so it lands in the log file if there is one |
| **`pgfsctl status`** | The gravestone shows as `ENDED` in the `LIVE` column, followed in red by `!! write-back UNFLUSHED LOSS (N mount(s))` and the breakdown |

##### Why the gravestone is not removed automatically

Because **the record of an accident that has not been read disappearing on its own is the worst outcome**.
Removing it is left to an explicit operational action:

```sql
DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0;
```

Gravestones only accumulate **when a loss happens**, so they grow differently from the stale rows left by an
abnormal exit (`kill -9`) (cleaning up the stale rows is a separate matter, sharing a root with B-1's `defer`
warning being excessive).

##### The test

`test_meta_loss_is_recorded_in_db` in `tests/linux/wbmeta.sh` (the 21st). A row with the same name and a
mismatched kind is put in with psql to make the flush fail permanently, and it is unmounted with a short
deadline. It watches for the presence of the gravestone, the `unflushedLoss` / `endedAt`,
**the warning appearing on the next mount's parent stderr** and **the warning going away when the gravestone is
removed**.

### The defences that could not be broken (confirmed with the two lenses)

* The lock hierarchy `NSGate -> DirtyFile.Gate -> the database tx` **has no reverse edge in stage 2 either**.
  `AssertNsGateHeld` is in every new `*Locked` method without exception.
* Heuristic (a) **cannot create a loss window either as a single tx with a pending source or as 2 txs with a
  persisted source** (the order is "commit the new data -> commit the target deletion plus the rename", so the
  old target is alive at every interruption point).
* Making (c) write-through is correct ("make it pending and materialize at once" would make the file/file
  collision resolution of the flush tx **succeed both O_EXCL creates**).
* None of the flush tx's 6 invariants (a fixed snapshot / a parent-to-child INSERT / a liveness check bypassing
  the cache / a single ascending batch of `{prefix}lock` / the audit ensure outside the tx / memory reflected
  only after the commit) can be circumvented.
* The `fi.fh` reverse lookups of `FSyncDir` / `FlushPath`, the triple emptiness check of `rmdir`, no sticking in
  the `Flushing` state, and the FUSE workers already being joined at unmount time.
