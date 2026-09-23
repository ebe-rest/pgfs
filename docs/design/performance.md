# Candidates for improving the performance

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: the **measured values and the improvement candidates** for
> the performance. The numbers measured, the conditions they were measured under, what can be said from them
> (and the conclusions that were corrected later), and **the measurement conventions** (wait for the daemon to
> disappear / check the integrity with md5 every time / read `xact_commit` only as a relative figure) all belong
> here. The design of a mechanism itself is authoritative in its own document.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [write-back.md](write-back.md) | **The design and the implementation status** of the data write-back (1d). The numbers are here, the mechanism is there |
> | [metadata-write-back.md](metadata-write-back.md) | The settled design, the stages and the invariants of the metadata write-back (1e) |
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | The review findings of 1e (round A, B-1 onwards) and the record of the fixes. **The grounds** for A-10 and B-1 are there |
> | [cache.md](cache.md) | The design of the read-side caches (the inode LRU / content / negative) |
> | [support_for_citus.md](support_for_citus.md) | The design of the Citus distribution and how a distributed deadlock is handled |
> | [control-plane.md](control-plane.md) | The route for switching the knobs used in the measurements and how the statistics are published (`pgfsctl config` / `status`) |
> | [settings-matrix.md](settings-matrix.md) | The defaults and the reload policy of the knobs turned here (`mount.*`) |
> | [../tests.md](../tests.md) | The list of the test suites, their counts and the environment requirements (the measurements here assume the same mount procedure) |

The FUSE / DokanNet callbacks are a hot path called between 100 and 10000 times a second. What follows are the
investigated improvements that promise an effect, in order of priority:

1. ~~**A uid/gid cache in `UserResolver`**~~ ✅ **Already implemented (confirmed 2026-06-04)** -
   [src/fuse/src/UserResolver.cs](../../src/fuse/src/UserResolver.cs) holds four `ConcurrentDictionary`
   instances, `unameToUid` / `gnameToGid` / `uidToUname` / `gidToGname`, through `GetOrAdd`, and calls
   `getpwnam` / `getgrnam` / `getpwuid` / `getgrgid` **exactly once per (name/id, result) pair** (the fallback
   names are resolved in advance in the ctor). The Assign side,
   [WindowsUserResolver.cs](../../src/dokan/src/WindowsUserResolver.cs), is cached too with four of them:
   `unameToSid` / `gnameToSid` / `sidToUname` / `sidToGname`. **The only uncached syscall is `IsGroupSid` ->
   `LookupAccountSid`, but its caller is
   [FileSystemUtils.ApplySecurity](../../src/dokan/src/FileSystemUtils.cs) (= SetFileSecurity = the cold path
   used only on a chmod/chown) and the hot `BuildSecurity` (GetFileSecurity) does not call it**, so the ROI of
   caching it further is low. The original document's "a getpwnam may be called on every `GetAttr`" had already
   been resolved in the implementation.
2. **Turning `InodeCache`'s `lock(this)` into a `ConcurrentDictionary`** -
   [src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs) protects both the byId and byPath
   dictionaries with a single `lock(this)`, and there are cases where a database query runs inside the `lock`.
   Lock contention is inevitable with multi-threaded FUSE. Replace it with a `ConcurrentDictionary` and change
   the design so that the database fetch happens outside the lock.
   **ROI: high / effort: high (a Lazy pattern is needed to suppress duplicate queries)**
3. **Making `PathParser`'s five `Lazy<T>`s eager** -
   [src/core/src/Utility/PathParser.cs](../../src/core/src/Utility/PathParser.cs) creates five `Lazy<>`s on
   every `FromPath`. Adding an eager constructor for the hot path would reduce the allocations per path.
   **ROI: medium / effort: medium**
4. **Replacing Dapper with a raw `NpgsqlDataReader` (for the hot SELECTs only)** - hand-writing the reader only
   for the hot queries such as the inode fetch of
   [src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs) and `Api.ListChildren` would avoid the
   reflection mapping. **ROI: high / effort: high (maintainability drops)**
5. **Handling `Encoding.UTF8.GetString(path)` as a `Span<byte>`** -
   [src/fuse/src/FileSystem.cs:75](../../src/fuse/src/FileSystem.cs#L75) `PathToString`. Making the key of
   `InodeCache.byPath` a `byte[]` hash would skip the UTF-8 decode. It has a large design impact, so it comes
   last. **ROI: medium / effort: very high**
6. **Removing `Inode.Children` (`FirstList<Inode>`) or making it a `List<T>`** - it is effectively unreferenced
   yet a `new FirstList<Inode>()` (a 1377-line class) runs on every inode creation. It could reduce the
   constructor cost of `Inode`. **ROI: low / effort: low**
7. **Caching the deserialization in the `Setting<T>.Value` getter** - the settings are read only at startup, so
   it is not a hot path. **ROI: low / effort: low**
8. **Enabling FUSE's `writeback_cache` (`FUSE_CAP_WRITEBACK_CACHE`)** - pgfs does not enable writeback_cache
   today and runs on FUSE's default write-through. As a result **every application `write()` produces a FUSE
   WRITE request -> [FileSystem.Write](../../src/fuse/src/FileSystem.cs#L565) -> a synchronous bytea chunk write
   to PG through `Api.WriteData`**, and the PG round trips grow linearly on a workload with many small
   sequential writes. Raising `-o writeback_cache` makes **the kernel's page cache buffer and coalesce the dirty
   pages and a write-back thread flush them together, deferred**, which reduces the number of WRITEs to the
   daemon and to PG (what makes it asynchronous is that kernel mechanism, not pgfs). **The caveats (to be
   verified)**: (a) with writeback_cache on, the kernel manages the size and mtime while dirty, so its
   interaction with `attr_timeout=0` (set so that a hardlink's st_nlink is reflected at once) has to be checked;
   (b) the consistency of when another client's write becomes visible across clients (notify); (c) the widened
   range of unflushed data lost in a crash. **ROI: medium to high (when writes are frequent) / effort: medium
   (raise the flag on `fuse_config` or `conn->want` in the init callback of the Pgfs.Fuse binding, plus verify
   the consistency)**

Implementing without a benchmark, the order **1 -> 6 -> 3** carries the least risk. 2 and 4, if done properly,
should be measured with BenchmarkDotNet first. 8 is worth measuring if there is a write-heavy workload (it has
little effect if reads dominate).

## Measured: an `rsync -avh` of 598 MB / 700 files (2026-07-25, the dev server)

The result of copying the same source (the pgfs repository itself) with the same command. The single PG is
PostgreSQL 17.5 on localhost, and Citus is a shared cluster on the same LAN (a coordinator plus 3 workers,
8 shards).

| The configuration | The throughput | The time | Notes |
|---|---|---|---|
| A single PG | **13.46 MB/s** | 44 s | After adding the UTC timestamps, the occupied bytes and the lock aggregation. Before adding them it was 14.08 MB/s = **the cost of those additions is within the measurement noise** |
| Citus rf=1 | **3.11 MB/s** | 3m12s | The coordinator's commit count = **34,517** (about 49 tx per file) |
| Citus rf=2 | **2.27 MB/s** | 4m23s | The difference from rf=1 is the doubling of the 2PC participants |
| Citus rf=2 (before the UPDATEs became router queries) | 2.08 MB/s | 4m48s | Plus **2 distributed deadlocks** (see [support_for_citus.md](support_for_citus.md)) |

The read side (cold): an 83 MB file from a single PG through `md5sum` gives **231 MB/s**, and the whole tree
(598 MB) on Citus rf=2 gives **about 35 MB/s**.

### The breakdown of the latency (where the time goes)

> ⚠ **The conclusion of this subsection was corrected later.** It concluded here that "the main cause = the
> workers' commit latency x the number of 2PC participants", but a follow-up measurement established that
> **the main cause is the read-modify-write amplification of the chunk rows** (the contribution of the number of
> commits is 17%). Read "the real main cause is not the 2PC but ..." below first. The measured values themselves
> stand (`dd bs=1M` is **a shape where no amplification happens**, so it could not explain the slowness of an
> rsync that does amplify).

The result of measuring `dd bs=1M count=20 conv=fsync` (= writes in the same 1 MiB unit as pgfs's chunk size)
three times each, compared against **raw SQL that does not go through pgfs**:

| What was measured | A single PG | Citus rf=2 | The ratio |
|---|---|---|---|
| pgfs's 1 MiB chunk write (= 1 FS operation = 1 tx) | **15 to 22 ms** (64 MB/s) | **276 to 280 ms** (3.6 MB/s) | **about 17x** |
| A raw 1 MiB bytea INSERT (no pgfs, from psql) | **1.8 ms** | **53 ms** | **about 29x** |
| One local commit (`INSERT` x 50) | the coordinator **0.07 ms** | a worker **10 ms** | **about 150x** |

* **The root is the commit latency of the worker nodes.** The coordinator is 0.07 ms/commit while a worker is
  **10 ms/commit** (`synchronous_commit=on` / `fsync=on` / `wal_sync_method=fdatasync` are the same on every
  node, so the difference is the storage performance). A 2PC synchronizes the WAL twice per participant, at
  PREPARE and at COMMIT PREPARED, so with 2 workers **that alone is a floor of 40 ms**.
* **It is 29x even without pgfs**, so the difference is not the shape of pgfs's SQL but **the cost of a
  distributed write on the cluster itself**. pgfs additionally piles several statements per FS operation into
  the same tx (taking the lock / the inode UPDATE / the data row / the chunk UPSERT / updating the occupied
  bytes), which is why it stays at 17x.
* Lowering the rf reduces the participants and helps (3.11 MB/s at rf=1 against 2.27 MB/s at rf=2).

### FUSE's `max_write` - the expected effect was not there (a negative result)

The hypothesis was "libfuse's default is 128 KiB, so raising it to 1 MiB would cut the write txs to an eighth",
and `fuse_conn_info.max_write` was made settable, but **a trace showed that 1 MiB WRITEs were already arriving
by default** (`UPSERT data_chunk … len:1048576` x 20 per 20 MiB; `--max-write 0` and
`--max-write 1048576` were completely identical). **libfuse3 negotiates up to the kernel limit
(FUSE_MAX_PAGES = 1 MiB)**, so the premise of the hypothesis was wrong.

* The implementation was kept as a knob for **fixing or lowering it explicitly**, and **the default is `0` =
  left to libfuse's negotiation (behaviour unchanged)**.
* A by-product finding: **libfuse3 does not accept `-o max_write=…`** (`fuse_new` returns 0 and the mount
  fails). The only way to set it is to write into `fuse_conn_info` in the init callback.
* What remains in the direction of reducing the write txs is item 8 above, `writeback_cache` (the kernel
  coalesces the dirty pages), and the write-back cache of Phase 1d. **The latter changes the structure "one FUSE
  write = one tx" itself, so there is still room there.**
* On the cluster side, for test purposes, setting the workers' `synchronous_commit` to `off` or `local` would
  remove the 2PC's fsyncs and should change things a lot (unverified).

### The real main cause is not the 2PC but **the read-modify-write amplification of the chunk rows** (established by a follow-up measurement on 2026-07-25)

At the time "the breakdown of the latency" was written, **the main cause was concluded to be "1 FS operation = 1
distributed transaction" = the cost of the 2PC**.
Afterwards, **re-measuring by reproducing the current shape of `WriteData` in raw SQL showed the main cause to
be something else**.
All of what follows is measured in psql on the dev server, normalized to **the time taken to write 1 MiB of file
data**.

**Why 128 KiB units**: the rsync measurement is about 4,700 chunk txs for 598 MB = **one FUSE WRITE is about
128 KiB**.
`dd bs=1M` arrives as 1 MiB (see the max_write item), but if the application's `write()` is 128 KiB it arrives
as 128 KiB. pgfs then **grows a 1 MiB chunk row with 8 UPSERTs of 128 KiB each** (the `overlay` / concatenation
of [WriteChunkSlice](../../src/core/src/Api/Api.cs)). **bytea at 1 MiB is subject to TOAST, so even a partial
update rewrites the whole TOAST chain every time** = writing a 1 MiB file causes an average of 4.5 MiB of row
rewriting.

| The shape (writing 1 MiB of file data) | A single PG (not distributed) | Citus rf=2 |
|---|---|---|
| **The shape today**: 128 KiB x 8 partial UPSERTs, each in its own tx | **48.9 ms** (20.4 MB/s) | **621.9 ms** (**1.61 MB/s**) |
| **write-back**: assemble 1 MiB in memory -> write it in one statement, one tx per file | **7.5 ms** (133.7 MB/s) | **77.1 ms** (12.98 MB/s) |
| The ratio | **6.5x** | **8.1x** |

* The Citus "shape today" lines up **the actual SQL as it is**: taking `{prefix}lock` (an INSERT plus a
  `FOR UPDATE`) / `SELECT chunk_size` / `SELECT length(payload)` / the chunk UPSERT / the `total_size` UPDATE /
  the inode UPDATE. The resulting **1.61 MB/s agrees with the measured 1.62 to 2.27 MB/s of the rsync** -> the
  model explains the real thing.
* **write-back is not a Citus-only countermeasure.** A single PG gets 6.5x too. The amplification comes from the
  nature of TOAST and has nothing to do with the distribution.

The breakdown (Citus rf=2, per 1 MiB):

| What was measured | The time | What it means |
|---|---|---|
| 8 partial UPSERTs of 128 KiB against the same row, **in 1 tx** | 480.8 ms | **The amplification only** (one commit) |
| 8 writes of 128 KiB split across 8 rows, 1 tx | 63.7 ms | **No amplification** (writing the same 1 MiB) |
| The 8 above in separate txs | 581.7 ms | The amplification plus 8 commits |

* **The contribution of the amplification = 480.8 / 63.7 ≈ 7.5x.**
  **The contribution of the number of commits = 581.7 - 480.8 ≈ 101 ms (12.6 ms/commit) = 17% of the total.**
  -> "Reducing the number of txs" alone only takes 17%. **The bulk of the win is stopping the growing of the
  same row over and over.**
* The cost of a single statement (against an existing 1 MiB row, Citus rf=2): **a SELECT of the full payload =
  0.4 ms** / **replacing 128 KiB with an overlay = 24.8 ms** / **replacing the whole 1 MiB payload = 29.4 ms**.
  -> **A read is nearly free and the cost of a write is the number of them.** Even for a partial write, folding
  it into one at the flush keeps an overlay cheap enough (there is no need to read it just to turn it into a
  full replacement).

### The number of 2PC participants is bounded by the number of nodes (= it does not grow when txs are folded)

The result of counting `PREPARE TRANSACTION` with `citus.log_remote_commands`:

| The shape of the tx | The number of PREPAREs |
|---|---|
| A tx touching only one shard group (the same for 1 row or 6) | **2** (= the placements at rf=2 = 2 nodes) |
| A tx touching 6 ids spread over 8 shards | **3** (= all 3 workers) |

* **The number of participants = the number of worker nodes touched**, and it does not grow with the number of
  shards or of statements. With 3 workers the ceiling is 3.
* Therefore **packing work into one tx barely increases the fixed cost of the 2PC**. Measured,
  "40 x 1 MiB in one tx" gives 56.8 ms/MiB (spread over 8 shards) against 59.2 ms/MiB (aligned to one shard),
  and **the difference is below the measurement limit** (the PREPAREs drop from 3 to 2 but they run in parallel
  so it does not show in the time).
* -> **Optimizing by aligning the shard groups (grouping by the starting directory, say) is meaningless at this
  cluster size.** `{prefix}inode` / `data` / `data_chunk` are in the same colocation group (measured
  `colocationid=10`, 8 shards), so "matching the data_id to the directory's inode shard" is
  **technically possible** (the mapping can be obtained with `get_shard_id_for_distribution_column`).
  **It would be worth re-evaluating if the workers grow to the point where the number of PREPAREs grows in
  proportion to the participants** - but that would come with the side effect of the data skewing per directory.
* Folding across files into one tx also gains **only +8.8%** (63.6 ms/MiB per file -> 58.0 ms/MiB with all files
  in one tx).

### What was learned

* **Turning the UPDATEs into router queries worked for "structurally resolving the deadlocks" but only adds +9%
  to the throughput.**
* **The main cause is the read-modify-write amplification of the chunk rows** (above). **The contribution of the
  2PC and the number of commits is 17%**, and the original conclusion that "the main cause is 1 FS operation = 1
  distributed transaction" **was an overestimate**. Citus being slower than a single PG is a fact (a worker's
  commit of 10 ms, and the 1 MiB transfer x the number of placements), but **stopping the amplification speeds
  up a single PG and Citus by the same ratio**.
* **Raising the write granularity came to nothing** (see `max_write` above; libfuse3 was already negotiating up
  to 1 MiB). But **the reason it came to nothing is consistent with the above too**: `dd` was already arriving
  as 1 MiB = no amplification was happening, which is why it was fast. rsync was slow because it arrived as
  128 KiB and amplified.
* What works next is **the write-back cache of Phase 1d** ([write-back.md](write-back.md)).
  The projected values are, as in the table above, **6.5x on a single PG and 8.1x on Citus rf=2**.
  Item 8 above, `writeback_cache` (on the kernel side), is a separate axis of "coalescing small writes" and can
  be used together with pgfs's own write-back.
* **A metadata-heavy workload such as rsync varies a lot from run to run** (2.27 and 1.62 MB/s on the same
  configuration). Comparing on the latency of a single operation such as a 1 MiB `dd` write is more
  reproducible. **But `dd bs=1M` is a shape where no amplification happens, so it is unsuited to measuring the
  effect of write-back** - measure with `dd bs=128k` or with a shape like rsync where "sub-chunk writes keep
  coming".

## Measured: the projection benchmark of the metadata write-back (1e) (2026-08-10, the dev server)

**The gate before implementing** the design ([metadata-write-back.md](metadata-write-back.md)). Two shapes were
reproduced in raw SQL with pgbench (-c 1 = a single client, equivalent to rsync; 120 to 200 files x 2 rounds
each):

* **The shape today** = write_back(1d)=on with the metadata write-through. One file = **5 txs**
  (create / the close flush / chmod / utimens / a rename within the same parent). The `{prefix}lock` INSERT plus
  `SELECT FOR UPDATE` are reproduced as implemented too.
* **The 1e shape** = the coalesced final state in **1 tx** (3 locks plus the ancestor liveness check plus the
  inode INSERT (the final name and attributes) plus the data INSERT plus the chunk INSERT).

| The scenario | The shape today | The 1e shape | The ratio |
|---|---|---|---|
| **Citus rf=2, 4 KB per file** | 28.8 ms/file | **7.8 ms/file** | **3.7x** |
| Citus rf=2, 768 KB per file | 74.9 ms/file | 61.9 ms/file | 1.21x |
| A single PG (localhost), 4 KB | 2.0 ms/file | 0.74 ms/file | 2.7x (an absolute difference of 1.3 ms) |

The anchor from a real mount (an `rsync -a` of 4 KB x 300 files, write_back=on, Citus rf=2): **39.3 ms/file**.
The difference from the model's 28.8 ms is about **10.5 ms/file of fixed cost in FUSE, the mount process and
rsync itself**.

### What was learned (the material for the 1e gate)

1. **1e's win concentrates on "small files x Citus (a configuration with an RTT to the workers)".** The absolute
   metadata saving is **about 21 ms/file** (Citus rf=2):
   * A 4 KB file: 3.7x on the SQL side -> projected onto the real world, 39.3 -> about 18 ms/file = **about
     2.1x**
   * A 768 KB file: the data write dominates, giving **1.21x**
   * Projected onto the reference workload (599 MB / 745 mixed files, an rsync of 179 s): 21 ms x 745 ≈ 15.6 s
     saved = **about 1.1x**
2. **On a single local PG it barely shows in the wall clock** (an absolute difference of 1.3 ms/file against
   10.5 ms/file of fixed cost on the FUSE side). **Unlike 1d, 1e is a countermeasure for "Citus / a
   high-latency database"** - 1d removes amplification so it gained 6.5x even on a single PG, while 1e reduces
   tx round trips so it does not help when the database is close.
3. **The earlier guess that "what is left of rsync is dominated by the metadata txs" was an
   overestimate.** In reality it is about 2,100 txs x ~5.8 ms ≈ 12 s (out of 179 s). The rest is the data flush
   (an estimated 46 s) and the fixed cost on the FUSE and client sides.
4. Even with 1e implemented, the floor for small files is **decided by the fixed cost on the FUSE side (about
   10.5 ms/file)**. What works next is investigating the breakdown of that (lookup / getattr / the syscall round
   trips).
5. An incidental measurement: an `rm -rf` through a real mount (a write-through unlink) is about **9 ms/file**.

How to reproduce the benchmark: the bench scripts are disposable (run against `pgfs_test` with
`created_by='bench'` and deleted afterwards; the non-distributed comparison creates a `pgfs_bench` schema and
drops it). The payload is a random bytea built from concatenated md5s (so that compression does not make it
unfairly fast).

## Measured: isolating the bottleneck (2026-08-10, the dev server, Citus rf=2, write_back=on)

The question left by the 1e projection benchmark - "where does the time of the reference rsync actually go" -
was isolated with **the deltas of `active_time` / `xact_commit` in `pg_stat_database`** (pg_stat_statements is
not preloaded and cannot be used) plus an SQL census from the trace log plus separating the workloads.

| The experiment | WALL | SQL (active_time) | commits |
|---|---|---|---|
| An rsync of the pgfs repository (841 files / 600 MB) | 103.1 s | **98.5 s (96%)** | 24,271 (**28.9/file**) |
| Re-rsyncing the same contents (a no-op, the cache warm) | 0.7 s | ≈0 | 2 |
| A 1 GiB zero file (without `--sparse`) | 104.1 s | 100.8 s | 2,170 |
| A 1 GiB zero file (with `--sparse`) | **1.9 s** | 0.1 s | 30 |
| A single 64 MB file | 6.5 s | 6.2 s | 80 (about 1.2/MiB) |
| 4 KB x 10 (measured waiting for the background flush to finish) | 0.5 s | ≈0.4 s | 253 (**25.3/file**) |

### What was learned

1. **The bottleneck is on the database side, at 96% of the wall clock.** What the previous section estimated as
   "about 10.5 ms/file of fixed cost on the FUSE and client sides" was in substance mostly
   **the autocommit SELECTs (= database round trips)** (a misattribution, corrected).
2. **One small file = about 25 txs, measured** (5 times the design model's 5 txs). The identified breakdown:
   **the negative lookup `(parent_id, name)` SELECTs, about 6.4** (rsync's lstat / before and after a create /
   the check on the rename destination. **An ENOENT is not cached**, so it goes to the database every time) plus
   4 metadata writes plus 1 flush. **The remaining about 14 txs/file are unidentified** - identifying them needs
   pg_stat_statements preloaded (`shared_preload_libraries` currently has only citus; it is a shared database,
   so it is a request to the administrator). The candidates: the occupancy SUM of a getattr / the re-fetches
   caused by `attr_timeout=0` / the re-lookups before and after a rename.
3. **A large file is about 100 ms/MiB (a throughput ceiling of about 10 MB/s) and about 1.2 tx/MiB.** A single
   64 MB file becomes 80 txs because it is the same size as `write_back_max_bytes` (64 MiB by default) and
   **the back-pressure fires a run of piecemeal flushes** (the behaviour is as designed, but the granularity
   needs rethinking). Even with zero-filled data it stays at 100 ms/MiB = TOAST compression brings almost no
   benefit.
4. **A sparse file goes from 104 s to 1.9 s with `--sparse`.** A 1 GiB sparse file was sitting in the reference
   workload (179 s), so if it was transferred without `--sparse` that is the main cause of the overestimate.
5. **A warmed-up read side costs nothing** (a no-op rsync of 0.7 s, SQL 0). The InodeCache plus the kernel cache
   are working.

### What works next, in order (likely cheaper than 1e itself)

1. **A negative lookup cache** - cache the ENOENTs with a short TTL (plus a NOTIFY invalidation). It can remove
   almost all of the about 6.4 tx/file. The implementation is just adding a "non-existence marker" to the
   InodeCache, orders of magnitude smaller than 1e's ledger.
2. **Identifying the about 14 unidentified tx/file** - asking the administrator to preload pg_stat_statements is
   the shortest route. Until then, widening the Pg-layer logging in the trace log to every kind of autocommit
   SELECT is another way.
3. **The flush granularity for large files** - turning the back-pressure's "one chunk at a time" into "a decent
   block". Raising the default of `write_back_max_bytes` / batching the flush unit.
4. **1e (the metadata write-back)** - what it can remove is 4 out of the 25 txs (the metadata writes, about
   21 ms/file). After removing 1 to 3 above, its relative effect goes up (the floor drops).

A measurement note: the deltas of `pg_stat_database` can pick up the noise of other sessions connected at the
time (a personal database here, so it is negligible). The statistics lag, so **take the snapshot 3 to 4 seconds
after the rsync finishes** (without waiting, the background flush and the statistics fall out and it looks about
a sixth of what it is - which was actually hit).

## Measured: the negative lookup cache (implemented 2026-08-10) plus a correction to the tx accounting

### The correction - most of the "about 14 unidentified tx/file" was not hidden queries but the accounting of the 2PC

The previous section wrote "one small file is about 25 txs (about 14 unidentified)" on an `xact_commit` basis,
but a control experiment in raw SQL (issuing the equivalent of a chmod as 1 tx from psql -> `xact_commit` goes
**+2**) established that **on Citus one write tx is accounted as about 2 commits in the coordinator's
statistics** (the 2PC participant backends / citus_internal's COMMIT PREPARED). Recounted:

- **The real txs as the client sees them are about 11 to 12 per file** = the negative lookups about 6.4 plus the
  4 metadata writes plus 1 flush (agreeing with the census from the trace log too).
- The 25 to 30 per file of `xact_commit` is "the real 11 to 12 x the doubled accounting of the write-side ones
  plus the noise of the Citus maintenance daemon and the like".
- **There was no hidden heavyweight query** (it was confirmed that `GetOccupiedBytes` is cached per inode, and
  so on).
- The lesson: **`xact_commit` cannot be used as a precise per-operation census on Citus** (it is for spotting
  trends). Making it precise needs pg_stat_statements preloaded (a request to the administrator).

### The negative lookup cache (`mount.negative_cache_ttl_ms`) - the implementation and the measurements

It was implemented (the default is **0 = disabled**, opt-in). An ENOENT is recorded in the InodeCache as
"path -> (the parent id, the expiry)", and a re-lookup within the TTL does not go to the database. The
invalidation has three routes: **this client's own create/rename/delete**
(`Put` / `InvalidateChildren` - recording the marker is in the same lock section as the lookup's SELECT, so the
race of "a create slips in after the SELECT and before the recording, leaving a stale ENOENT" cannot happen
structurally) / **readdir** (`PutChildren` = the database's latest listing is authoritative) /
**a remote notification or a live reload** (a TTL of 0 clears everything).

Measured (an `rsync -a` of 4 KB x 300, Citus rf=2, write_back=on):

| | TTL=0 (disabled) | TTL=3000 | The difference |
|---|---|---|---|
| WALL | 15.9 s (53 ms/file) | 14.7 s (49 ms/file) | **1.08x** |
| commits | 30.1/file | 26.9/file | **-3.2 tx/file** |

* What goes is **the second and later negative lookups of the same path, about 3 per file** (the kernel lookup
  -> the existence check in FUSE's Create, rsync's lstat -> the check on the rename destination and so on).
  **The first ENOENT check, about 3.4 per file, cannot be removed in principle** (it has to ask the database
  once whether it really does not exist).
* The gain is small because a lookup is a light read (a router SELECT of about 1.3 ms).
  **The larger the RTT to the workers, the more it helps.**
* The tests are [tests/linux/negcache.sh](../../tests/linux/negcache.sh) 7/7 PASS (the visibility contract plus
  the live reload). The e2e suite (off by default) is 43/44 - the 1 FAIL is the known 40P01 flake
  ([tests.md, the known flakes](../tests.md)), its 1-in-3 reproduction rate agrees with the known 15 to 20% per
  round, and it is unrelated to this change (it is off by default, so the path is identical).

### "What works next" again

1. ~~The negative lookup cache~~ -> ✅ implemented (above; a win of about 3 tx/file, 1.08x)
2. **1e (the metadata write-back)** - of the real 11 to 12 tx/file, **it folds the 4 metadata writes plus the 1
   flush into 1 tx**. That is the majority of the real txs remaining after the negative cache. Thanks to the
   correction of the tx accounting, **1e's relative value has gone up from the estimate at projection time**
   (because the real breakdown turned out to be "6.4 lookups plus 5 writes")
3. The first ENOENT check (about 3.4 per file) is a floor that cannot be cut. Large files are a separate axis
   (about 100 ms/MiB / the back-pressure granularity)

---

## Measured: the effect of 1e stage 2 (2026-08-12, the dev server, Citus rf=2 / 8 shards)

With stage 2 in (close-no-flush plus the three synchronization heuristics plus the floor of the errors),
**whether the win of the metadata write-back actually appears** was measured. The conclusion is
**"it does not help a representative bulk copy"**.

### The measurement premises (get these wrong and the numbers lie)

* **Always wait for the daemon to drain.** `fusermount3 -u` **returns as soon as the kernel-side unmount
  finishes**, but `mount.pgfs` then flushes the remaining pending entries and exits. **Without waiting for the
  process to disappear, the write-back's work is measured out and it looks unfairly fast** (this was hit in the
  first measurement and the numbers were corrected).
  * **Do not wait on the wrong thing.** The `started (pid N)` printed to stderr at startup is
    **the pid of the parent process before the fork**, which exits as soon as the mount is established
    (measured 2026-09-19).
    Waiting on that gives zero wait time and **reads the database mid-drain, making it look as though files had
    disappeared** (it was actually hit in the B-1 measurement and an `md5=NG` plus a short count was nearly
    mistaken for data loss).
    Wait for the real daemon to disappear **by process name**, as in `pgrep -x mount.pgfs`
    (`pgrep -f` also matches the command line of the parent shell that wrote the measurement script).
* **Check the integrity every time.** Remount (= an empty cache, read back from the database) and compare the
  md5 set against the source. Without being able to reject "fast but broken" with numbers, the measurement is
  meaningless (all 300 files matched in every case).
* `xact_commit` is a counter for the whole shared database, so read it only as a relative indication.

### The results (4 KB x 300 files, **the total of the foreground plus the drain**)

| The workload | metadata off | metadata **on** | The ratio |
|---|---|---|---|
| `rsync -a` | 11.8 s (39.2 ms/file) | 11.8 s (39.3 ms/file) | **1.00x** |
| `open(O_CREAT)` plus write plus close (no O_EXCL) | 4.84 s (16.1 ms/file) | 3.72 s (12.4 ms/file) | **1.30x** |

* **rsync does not get faster at all** (the reason is below).
* Even a create that does not use O_EXCL stops at **1.30x overall**. **The foreground gets 13x faster**
  (4.29 s -> 0.32 s), but **the work only moves into the drain** and the total does not change.
* The drain's 3.4 s / 300 = **11.3 ms/file ≈ one Citus tx**. In other words
  **the metadata folding itself works as designed** (several txs -> 1 tx).

### Why it does not help rsync - **both `rsync` and `cp` use `O_CREAT|O_EXCL`**

Measured with `strace`:

```
openat(AT_FDCWD, ".f1.awJpho", O_RDWR|O_CREAT|O_EXCL, 0600)          = 1   # rsync's temp file
rename(".f1.awJpho", "f1")                                           = 0
openat(AT_FDCWD, "/home/user/mnt/pgfs/dst/f1", O_WRONLY|O_CREAT|O_EXCL, 0644) = 4   # cp's destination
```

Stage 2's **synchronization heuristic (c) makes an `O_EXCL` create write-through** (see 1e).
Therefore **not one file rsync or cp creates becomes pending**, and the following `chmod` / `utimens` /
`rename` are operations against a persisted inode so they stay write-through as well - **the folding never
happens once**.

* **The design's aim (folding rsync's create -> write -> close -> chmod -> utimens -> rename into 1 tx) and
  heuristic (c) are incompatible.** The projection benchmark reproduced the tx sequence in raw SQL, so
  **it did not look at the syscall flags and could not detect this contradiction**.
* The A/B check: a create with `O_EXCL` has a row in the database right after the close (write-through), and one
  without `O_EXCL` has no row (pending). `pgfsctl status` likewise shows `metadata flushes = 1` /
  `data flushes = 99` for an rsync of 99 files.

### The measurements of B-1 `defer` (2026-09-19, **after the knob was implemented**)

The `rsync` was re-measured with `mount.write_back_metadata_exclusive_create = defer`.
As in "the measurement premises" above, it is **the total with the wait for the daemon to disappear**, with the
md5s compared every time.

The environment: the dev server (the schema `pgfs_test`, **Citus rf=2 / 8 shards**).
The workload: **an `rsync -a` of 4 KB x 500 files.** Three runs each.

| The setting | The foreground (when the rsync returns) | **The total (including the drain)** | ms/file | The ratio |
|---|---|---|---|---|
| `write_through` (the default) | 18.49 / 18.29 / 18.55 s | **18.62 / 18.42 / 18.57 s** | 37.1 | 1.00x |
| **`defer`** | 1.24 / 1.24 / 1.23 s | **5.66 / 5.79 / 5.67 s** | 11.3 | **3.28x** |

All 6 runs gave 500 files with matching md5s.

* **The projection (39 -> about 12 ms/file = about 3x) was right.** Measured, 37.1 -> 11.3 ms/file = **3.28x**.
  The 11.3 ms/file after the drain is almost exactly "one Citus tx", backing up that **one file is folded into
  one tx**.
* **Looking at the foreground alone gives 14.9x, and that number must not be used.** The work only moves into
  the drain, and looking at anything but the total lies (the same trap was hit in the stage 2 measurement).
* That 3.28x is **the price of losing the cross-client `O_EXCL` exclusion**. For why the default does not
  change, see "where it sits" in
  [metadata-write-back-reviews.md, the settled design of B-1](metadata-write-back-reviews.md).

### What can be said from this measurement

1. **1e takes "a file whose whole lifecycle can be deferred" to about 12 ms/file.** This workload stopped at
   1.3x because it is only a create plus a write and **there is little to fold with**.
2. **The win is greatest on a workload with many write-through metadata operations after the close** = exactly
   rsync (39 ms/file today ≈ 5 synchronous txs). If `O_EXCL` could be made deferrable,
   **39 -> about 12 ms/file = about 3x** could be expected (projected) -> **it was measured on 2026-09-19 and
   3.28x was confirmed** (the measurements of B-1 `defer` above).
3. **The grounds for keeping the default off got stronger**: on a default configuration it is 0% for rsync/cp
   and 1.3% for other creates, which does not pay for the durability contract that is lost
   ([Mount.md, write-back](../Mount.md)).

## Measured: the effect of A-10 (putting an overwrite of a persisted inode back to a synchronous close) (2026-09-19, the dev server)

Round A's **A-10** is the fix that "puts the synchronous-close mark on as soon as a write to a body already in
the database (persisted) starts" = confining close-no-flush to **pending-born**
([metadata-write-back-reviews.md, the round A fixes](metadata-write-back-reviews.md)).
The aim is to close the window where "an overwrite of an existing file is partially committed in a separate tx
every interval, and a crash leaves **a chimera with a new first half and an old second half**".
**Its performance impact had been left unmeasured**, so it was measured.

The environment: the dev server (the schema `pgfs_test`, **Citus rf=2 / 8 shards**).
As in **the measurement premises above (get these wrong and the numbers lie)**, it is **the total with the wait
for the daemon to disappear**, remounting every time (= an empty cache, read back from the database) and
comparing the contents (consistent on every run).

The A/B is against **a build with A-10's single line (the `MarkSyncOnClose` of `WriteDataBuffered`) removed**.
As a comparison, **metadata write-back off (= 1d only, close to the default configuration)** was measured too.

### The range A-10 affects is "an overwrite without a truncate" only

Synchronization heuristic **(b)** **already** upgrades the close of a `truncate` / `O_TRUNC` to synchronous.
Therefore **an overwrite with `O_TRUNC`, such as `cat src > f`, was synchronous before A-10 too**, and what A-10
changes is confined to **a pure overwrite with neither `O_TRUNC` nor `O_CREAT`** (`open(f, "r+b")` /
`dd conv=notrunc` / a database file / an mmap write-back and so on). The workloads below write in that shape.
**A bulk copy (dominated by creates) is pending-born and is unaffected by A-10.**

### Workload A: overwriting 300 separate files once each (4 KB)

| The setting | The foreground | **The total (including the drain)** | ms/file |
|---|---|---|---|
| metadata off (1d only) | 7.26 / 6.87 s | **7.34 / 6.89 s** | 24.5 / 23.0 |
| metadata on, **without A-10** | 0.63 / 0.65 / 0.64 s | **7.02 / 7.24 / 6.96 s** | 23.4 / 24.1 / 23.2 |
| metadata on, **with A-10 (current)** | 7.31 / 6.41 / 5.96 s | **7.32 / 6.42 / 5.97 s** | 24.4 / 21.4 / 19.9 |

**All three totals fall inside the spread of 19.9 to 24.5 ms/file, so A-10's cost is below the measurement
limit.** Looking at the foreground alone gives 0.64 -> 6.6 s = **10x slower**, but that is
**the work merely moving from the drain back into the foreground** (the same trap as the stage 2 and B-1
measurements, so do not use the foreground number on its own).

### Workload B: opening, writing and closing one persisted file 300 times (4 KB at shifting positions)

**A-10's worst case.** A synchronous flush runs on every close, so it is the shape with the most to coalesce.

| The setting | The foreground | **The total (including the drain)** | ms/close |
|---|---|---|---|
| metadata off (1d only) | 33.77 / 33.81 s | **33.85 / 33.88 s** | 112.8 / 112.9 |
| metadata on, **without A-10** | 0.07 s x3 | **0.27 s x3** | 0.9 |
| metadata on, **with A-10 (current)** | 33.83 / 34.48 / 33.55 s | **33.84 / 34.56 / 33.57 s** | 112.8 / 115.2 / 111.9 |

**The ratio against no A-10 is 125x.** But **with A-10 it is the same value as the default with metadata off**
(112.8 against 112.8), and **A-10 merely cancels an unsafe speed-up that stage 2 introduced and goes back to the
1d behaviour; it is not slower than the default**.

The 113 ms per close is not tx overhead but mostly **the full replacement of a growing chunk (the
read-modify-write amplification)** (this workload grows the file to 1.2 MiB, so once chunk 0 is full it rewrites
about 1 MiB every time. Consistent with **the real main cause is not the 2PC but the read-modify-write
amplification of the chunk rows** above).
**It is not that A-10 creates the 113 ms; A-10 makes it be paid on every close.**

### What can be said from this measurement

1. **A-10's price concentrates on a workload that keeps reopening and overwriting one file.**
   With a single overwrite each (workload A) the totals show no difference.
2. **Even paying that price it is not slower than the default (metadata off).** The 0.9 ms/close without A-10 is
   a number that only appears by accepting the chimera, so **it must not be used as the basis of comparison**.
3. If this path is to be made faster, the way is either **to stop repeating closes without an `fsync` in
   between** or to cut it on the back-pressure's flush granularity, not to undo A-10.

---

## Measured: the baseline for handle-context stage B (2026-09-20, the dev server, **a single PG**)

For the gate of
[handle-context.md, stage B's acceptance criteria and API surface, point (4)](handle-context.md). Stage B
**flips the identification of `Read` / `Write` from the path to the `InodeId`**, so a baseline was taken in the
stage A state **to show there is no regression**. **It is re-taken with the same script after
stage B is implemented and compared.**

### Why measure on a single PG (rather than Citus)

What is to be measured is **the client-side resolution cost** (two dictionary lookups, `byPath` then `byId`,
plus the UTF-8 decode of `PathToString`, being replaced by one `byId`). On Citus one chunk write takes
**about 17x that of a single PG** (the breakdown of the latency above), so **the client-side difference is
buried entirely in the database latency**. A non-distributed schema `pgfs_bench` was created for the
measurement and dropped afterwards.

### The baseline (stage A, 5 runs, **the total of the foreground plus the drain**)

| The workload | The median | The min | The max | The spread (max-min)/median |
|---|---|---|---|---|
| W1 `rsync -a` 4 KB x 300 | **2.12 s** (7.1 ms/file) | 1.91 s | 2.68 s | **36.3%** |
| W2 cold read 4 KB x 300 (`md5sum`) | **0.29 s** | 0.28 s | 0.30 s | 6.9% |
| W3 write 64 MiB `dd bs=1M conv=fsync` | **0.88 s** (73 MB/s) | 0.86 s | 0.90 s | 4.5% |
| W4 cold read 64 MiB `dd bs=128k` | **0.23 s** (278 MB/s) | 0.23 s | 0.24 s | 4.3% |

All 20 runs gave **300 files with matching md5s** (for W3/W4, a matching md5 over the 64 MiB). The integrity is
checked every time **after remounting** (= an empty cache, read back from the database).

### The tx baseline (the `xact_commit` delta of W1, 3 runs)

| | The total | /file |
|---|---|---|
| W1 `rsync -a` 4 KB x 300 | 9673 / 9644 / 9645 | **32.2 / 32.1 / 32.1** |

**This is the sharpest indicator for stage B.** Its spread is 0.3%, two orders of magnitude sharper than the
timing, and it directly measures "did the database round trips grow". Even if the time is buried in the error,
**a move in tx/file is certain to be noticed.**
(`xact_commit` is a counter for the whole shared database, so read it only as a relative figure.)

### The discriminating power - what this baseline can and cannot say

- **It can say**: **a regression over 5%** in W3 / W4 / tx/file. W3 and W4 have a spread of 4 to 5%, and
  tx/file 0.3%.
- **It cannot say**: **a change of under 20%** in W1. A 300-file rsync on a shared server has a spread of 36%,
  and in this shape it is a coarse baseline. To claim a difference in W1, the number of runs has to go up.
- **A difference in time is unlikely to appear in the first place.** What is removed is
  **one dictionary lookup plus one UTF-8 decode** per callback, orders of magnitude below chunk I/O (in ms).
  **So it is a measurement to show "it did not get slower", not "it got faster"**, and the main judgement rests
  on the tx/file above.

To reproduce: a disposable script (create the `pgfs_bench` schema and mkfs -> generate 4 KB x 300 and 64 MiB
from `/dev/urandom` -> W1 to W4 -> drop). After the `fusermount3 -u`, **wait for the real daemon to disappear
with `pgrep -x mount.pgfs`** (see the measurement premises).

### The results: stage B (after flipping the FUSE adapter) - **no regression**

Re-taken with the same script and the same procedure.

| The workload | Stage A | **Stage B** | The difference |
|---|---|---|---|
| W1 `rsync -a` 4 KB x 300 | 2.12 s | **1.95 s** | Inside the spread (36%). **No difference is claimed** |
| W2 cold read 4 KB x 300 | 0.29 s | **0.28 s** | As above |
| W3 write 64 MiB `bs=1M` | 0.88 s | **0.87 s** | As above |
| W4 cold read 64 MiB `bs=128k` | 0.23 s | **0.23 s** | Unchanged |
| **W1's `xact_commit`** | 32.2 / 32.1 / 32.1 | **32.1 / 32.2 / 32.1** | **Identical** |

All 20 runs gave matching md5s after remounting.

**The gate was passed.** **The tx/file the judgement rests on has not moved** = **the database round trips have
not grown.** That is consistent with the static check of stage B's performance gate (anything that hits `byPath`
is certain to hit `byId`).

**No improvement in time is claimed.** W1 came down from 2.12 to 1.95 s, but W1's spread is 36% and
**that difference is indistinguishable from noise**. As announced, the conclusion is **"it did not get
slower"**, not "it got faster".
