# Caching - the inode LRU / the content read cache / read-ahead

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **this document**
>
> **What this document is the source of truth for**: the design, implementation status and change record of
> the caches on the reading side. The cap and the LRU of `InodeCache`, the content (body) read cache, the
> cross-client invalidation through the data-write NOTIFY, the negative cache and read-ahead (not started)
> belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [write-back.md](write-back.md) | The cache on the **writing** side. Dirty chunks and the flush |
> | [metadata-write-back.md](metadata-write-back.md) | Deferring metadata writes. The ledger of pending inodes |
> | [control-plane.md](control-plane.md) | The path that changes the cache caps at run time (`pgfsctl config`) and how the statistics are exposed (Layer 3) |
> | [performance.md](performance.md) | The measurements and improvement candidates as a whole. This is the design; that is the numbers |
> | [settings-matrix.md](settings-matrix.md) | The defaults and reload policies of `mount.cache_*` |
> | [runtime-control-plane.md](runtime-control-plane.md) | The overall shape of the operations work (the hub) |

## Design

Two caches on the reading side. For where they sit in the whole, see the table in
[runtime-control-plane.md](runtime-control-plane.md).

### The settled design - the cap and the LRU wiring of InodeCache

**The problem**: [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) initializes its three dictionaries
`byId`/`byPath`/`childrenByParent` with `cache_max_entries`, but that is **only a capacity hint for
`RichDictionary` (a `Dictionary` wrapper) and evicts nothing** - so nothing is removed except by an explicit
`Invalidate*` and **it grows without a bound**. `cache_max_entries` effectively does nothing.

**The decisions**:

1. **The eviction policy is LRU.** The existing `Inode.CacheTime` (already updated by `UpdateCacheTime()` on
   every Get/Put) is used as the recency (no new state is introduced).
2. **One cap, with `byId` as the authority.** When `byId.Count > cache_max_entries` it evicts by LRU, and
   afterwards it **sweeps `byPath`/`childrenByParent` of entries pointing at ids that are no longer in byId**
   to keep them consistent (three dictionaries bounded indirectly by byId's single cap). The root has
   `CacheTime = DateTime.MaxValue` fixed, so it is **never evicted**.
3. **Thread safety stays as it is** (the eviction happens under `lock(this)`). Resolving `lock(this)` plus
   database queries inside the lock ([performance.md](performance.md)) is **a separate step** (it is not
   mixed in here).

**The eviction mechanism is a batch on overflow**:
- The trigger: immediately after the two places that write into byId (`put` / `PutChildren`).
- Only when `byId.Count > cap` does it **evict in one batch down to the low-water mark (`cap - cap/8`)** in
  ascending `CacheTime` order. Normally (at or below the cap) it returns at once = almost zero cost per
  operation, and the O(n log n) sort happens only on overflow.
- Whatever was just added has already been through `UpdateCacheTime()` in `put`, so it is the newest and is
  not dropped. `cache_max_entries <= 0` is treated as eviction disabled (= unbounded, as before).

**Unchanged**: the semantics of the existing `Invalidate*` and of the NOTIFY invalidation. The content (body)
cache is a separate layer.

**Verified on real hardware (docker e2e)**: **36/36** at the default cap (a regression run), plus **36/36**
with `CACHE_MAX_ENTRIES=8`, which makes the eviction fire throughout every test (confirming that the eviction
and the cascading sweep break nothing). A `CACHE_MAX_ENTRIES` injection point was added to
`tests/docker/run.sh`.

**Where the two meet**: the "data-write NOTIFY" added by the content cache rides the same NOTIFY path as the
control messages. With correct invalidation in place there is also room, later, to relax `attr_timeout=0`
(set in [Mount Program.cs](../../src/mount/src/Program.cs) for the sake of hardlink nlink) a little and go
faster still.

### The settled design - the content (body) read cache plus the data-write NOTIFY

**The aim**: stop every `data_chunk` read from making a database round trip by keeping hot chunks in memory,
which speeds reads up (a different layer from the metadata cache above).

**The structure - `ContentCache` (new, Core.Api)**:
- The key `(data_id, chunk_index)` -> **the full chunk payload** (`byte[]`). Hardlinks share a data_id, so
  keying on data_id is the correct granularity.
- **A byte-budget LRU**: when the total bytes exceed `mount.cache_data_max_bytes` (a new Field, 64MiB by
  default, `0` disables it) it evicts in one batch down to the low-water mark (7/8 of the budget) in
  ascending `CacheTime` order (the same batch approach as the inode cache).

**The read path (`ReadData`)**: for each chunk, `Get(data_id, idx)` - on a hit it copies a slice out of the
full payload; on a miss it **reads the full payload** with `ReadFullChunk`, calls `PutIfGeneration` and then
copies (so later partial reads hit as well). Holes (no row, or a short payload) are zero-filled as before.
With `cache_data_max_bytes=0` the previous `substring` partial-detoast path (no cache) is kept.

**write/truncate/release = write-invalidate**: after the operation, `InvalidateData(data_id)` discards every
chunk of that data_id, so the next read fetches it from the database again. The aim here is faster reads, so
there is no write-through merge (that comes with write-back).

**Cross-client invalidation (the data-write NOTIFY)**: the existing `Notify(...)` in `WriteData`/`TruncateData`
**carries inode ids only**. NotifyMessage gains **`"d"` = data_ids** and write/truncate put the data_id on it
too. On the receiving side `OnRemoteChange` passes `msg.DataIds` to `ContentCache.InvalidateData`. With
`notify_enabled=false` (the single-client default) this client's own write-invalidate is enough for
consistency (nobody else is writing).

**Closing the stale-read race (important - a global generation counter)**: if another thread or another
client writes and invalidates while a read is fetching an old chunk, the old payload could be burnt into the
cache. `ContentCache.Generation` is captured when the read starts, `PutIfGeneration(..., gen)` **only accepts
it when the generation has not changed**, and `InvalidateData` does `++` on the generation. That makes "a
payload older than the last write remains in the cache" impossible (= no permanent staleness). A single read
seeing a mix of old and new during a concurrent write is accepted **as equivalent to the page cache** (a real
filesystem does not guarantee a read() snapshot either). It is **O(1) state** (no per-data_id dictionary is needed).

**The new Field**: `mount.cache_data_max_bytes` (a LongField, SaveTo=File, AppliesTo=Mount|Assign, 64MiB by
default). Added to [settings-matrix.md](settings-matrix.md) / [Mkfs.md](../Mkfs.md).

**Unchanged**: `pgfs_lock` and the tx exclusion. The content cache is a pure cache above the database and does
not touch write consistency.

**Verified on real hardware (docker e2e)**: **36/36** at the default (64MiB), plus **36/36** with
`CACHE_DATA_MAX_BYTES=4096` (a budget below one chunk, so eviction, generation invalidation and
write-invalidate all fire constantly), plus **36/36** with both the metadata and the body caches tiny
(`CACHE_MAX_ENTRIES=8` + `CACHE_DATA_MAX_BYTES=4096`). A `CACHE_DATA_MAX_BYTES` injection point was added to
`tests/docker/run.sh`.

---


## Implementation status (as-built)

**Both are implemented and green in the e2e on real hardware**: the cap and LRU wiring of `InodeCache`, and
the content read cache plus the cross-client invalidation through the data-write NOTIFY (with the global
generation guard).
The negative cache was added afterwards, and its regression is held by
[tests/linux/negcache.sh](../../tests/linux/negcache.sh).

**read-ahead has not been started.**

> The individual as-built notes stay at this granularity because, even before the split, the pre-split
> [runtime-control-plane.md](runtime-control-plane.md) carried only the settled design for these two (the
> implementation details lived on the punch-list at the time). **Anything added should go in this chapter.**

## Change record

The chronological record is appended here (the design and the as-built status are owned by the two chapters above).

- [runtime-control-plane.md](runtime-control-plane.md) had grown to 1,802 lines, so it was split by feature
  and this document was carved out of it. The design content is as it was before the split.
  The implementation-status chapter was created at the split (there was no separate section before).
