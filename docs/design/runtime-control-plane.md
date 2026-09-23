# The runtime control plane and the cache speed-ups - the design plan

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the overall shape of the five operations themes and
> how they relate to each other**.
> **It holds no per-feature design, as-built status or change record** - the six documents below own those,
> and this is the index.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [cache.md](cache.md) | (1) caching (the inode LRU / the content read cache / read-ahead) |
> | [write-back.md](write-back.md) | Deferring the writes of the file bodies |
> | [metadata-write-back.md](metadata-write-back.md) | Deferring the metadata writes (the settled design plus the implementation status) |
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | The review record of metadata write-back (rounds A and B-1 to B-13) |
> | [control-plane.md](control-plane.md) | (2)(3)(4) the registry / the control NOTIFY / `pgfsctl config` and `status` |
> | [gui.md](gui.md) | (5) the GUI operations dashboard |
> | [performance.md](performance.md) | The measurements and improvement candidates the caching work was built on |
> | [settings-matrix.md](settings-matrix.md) | The list of settings items and the values of their reload policies |
> | [database.md](database.md) / [../ddl/](../ddl/README.md) | The DDL of `{prefix}mounts` |
>

> The five operations themes -
> (1) going faster by filling the caches / (2) applying a settings change while still mounted / (3) a
> settings-change subcommand / (4) a state-inspection subcommand / (5) wrapping all of these in a GUI -
> are bound together here as **a single design**.
> This document first pins down **the decisions**, and the detailed design is appended afterwards
> (a design document -> agreement -> implementation -> a full e2e).
> **Where things stand**: the caches, data write-back, metadata write-back through stage 2, the control
> plane, and the GUI's skeleton and read-only dashboard are all implemented. read-ahead and the GUI's
> config-set and finishing steps are not.
> **Every unfixed point raised about metadata write-back has been closed** - nothing is left from round A or
> round B.
> What this document used to say about "15 of the 16 existing tests FAILing" **was resolved in A-7**, and
> `wbmeta.sh` is now **green on all 27**.
> **All nine Linux suites are green**, and the docker and Citus runs have been repeated. The counts are owned
> by [tests.md](../tests.md).
> The current CLI contract is in [Mount.md](../Mount.md), and the additional Windows design in
> [windows-parity.md](windows-parity.md). The "settled design" sections below keep the plan as it was at the
> time, and the differences from the implementation are shown in the as-built sections.

## How the five themes fit together (why they are one document)

They look like five separate things, but the dependencies run like this:

- **(2)(3)(4) share one foundation** - each of them needs a way to reach a running mount/assign process (a
  control channel). It did not exist when the design started; the NOTIFY control channel and the mounts
  registry are implemented now.
- **(1) is semi-independent** - it produces a speed-up on its own. Where it needs a "data-write NOTIFY" to
  invalidate the content cache, it meets (2)'s foundation.
- **(5) sits on top of (3)(4)** - keeping `config`/`status` machine-readable (`--json`) makes the GUI a thin
  front end over them.

```
  (5) GUI ─────────────────────────────┐ (a wrapper over (3)(4))
  (3) config changes ─┐  (4) status ─┐  │
                      ▼              ▼  ▼
  (2) live apply ──── the control plane (the foundation) ──┐
       control NOTIFY messages + the pgfs_mounts registry  │
                      ▲                                    │
  (1) caching ────────┘ (meeting it at the data-write NOTIFY)
                                             ▼
                                      Pgfs.Core / PostgreSQL
```

## A guide to the current implementation

| Item | What is implemented now | What is still constrained |
|---|---|---|
| Settings | ApplySingleLive changes the Live items of RootConfig | - |
| Communication | The control LISTEN is always started; only the data notifications are gated by notify_enabled | No ACK that a notification was applied, and no guaranteed full resync after a reconnect |
| Administration | pgfsctl config/status, and the heartbeat / config / stats of mounts | The heartbeat is the most recent snapshot |
| Caching | The inode LRU, the content read cache, the negative cache | read-ahead is not implemented, and this is a separate layer from the OS cache |
| write-back | DirtySet and DirtyNamespace, explicit synchronization, the background and shutdown flushes | Both data and metadata are off by default |
| GUI | A read-only dashboard calling Core directly | Editing settings and packaging for distribution are not finished |

The implementation lives in [Api.cs](../../src/core/src/Api/Api.cs),
[Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs) and the [GUI](../../src/gui/).

## Where things stood when the design started (history, not the current specification)

| Item | At the time | Reference |
|---|---|---|
| Applying settings | Resolved **exactly once at start-up** (CLI -> toml -> the DB `pgfs_settings` -> the default); `RootConfig` was effectively immutable afterwards. **No live reload** | [ConfigLoader.cs](../../src/core/src/Config/ConfigLoader.cs) `BuildRootConfig` and each `Program.cs` |
| A channel to a running mount | **No IPC.** `LISTEN/NOTIFY` only, and **one way** (another client -> the mount listens, invalidating an inode/path; no reply) | [NotifyChannel.cs](../../src/core/src/Api/NotifyChannel.cs) / [Api.cs](../../src/core/src/Api/Api.cs) `OnRemoteChange` |
| Subcommands | mkfs/mount/assign were **one thin exe each**. No dispatch table. The default for a new command was "add another thin exe" | [src/mkfs](../../src/mkfs/src/Program.cs) / [mount](../../src/mount/src/Program.cs) / [assign](../../src/assign/src/Program.cs) |
| InodeCache | Three `RichDictionary` instances (id/path/children). `mount.cache_max_entries` defaulted to 1024 **but the capacity was only an initial hint and there was no LRU eviction**. No TTL; invalidation by hand through NOTIFY | [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) |
| A cache of the file bodies | **None.** Every read/write made a database round trip in a fresh tx (the partial TOAST detoast did work) | [Api.cs](../../src/core/src/Api/Api.cs) `ReadData`/`WriteData` |
| statfs | **A 5-second TTL cache existed** (the precedent for keeping state in the database) | [Api.cs](../../src/core/src/Api/Api.cs) `StatFsCacheTtl` |
| Where NOTIFY fired | The **metadata operations** - create/delete/chmod/chown/hardlink/rename and so on. A data write **did not fire one** (the content cache needed that added) | [Api.cs](../../src/core/src/Api/Api.cs) |

## The decisions

### Decision 1: the control channel is **aggregated in the database** (NOTIFY plus a registry)

The channel to a running process is built by extending the existing `LISTEN/NOTIFY` backbone.
- The existing NOTIFY is extended so it can **carry control messages (reload / ping)** as well.
- Each mount/assign registers itself in the **`{prefix}mounts` registry** at start-up and heartbeats.
- **Why**: it rides the current database-centred design as-is / it shows every mount across the cluster at
  once / it adds no new IPC surface (a socket or a named pipe) / there is a precedent in `statfs`'s TTL cache.
- **The trade-off (accepted)**: state is obtained through the heartbeat, so it is "the most recent" value
  rather than strictly synchronous. If a workload turns up that needs strict low-latency request-reply, there
  is room to add a local socket **as a later hybrid** (not now).
- **Rejected**: a local socket on its own (it cannot see across the cluster from a single host, and it adds an IPC surface).

### Decision 2: **the caching comes first**

Caching -> the foundation -> config (and live apply) -> status -> the GUI, in that order. The caching is
semi-independent and produces a speed-up early, and adding the `data-write NOTIFY` makes it flow naturally
into the foundation. The details are in the build order below.

## The build order

| Theme | Contents | Schema impact |
|---|---|---|
| **Caching** | The InodeCache capacity/LRU wiring ✅ -> the content read cache plus the data-write NOTIFY ✅ -> read-ahead (not started) -> data write-back ✅ (implemented and verified on real hardware) -> **metadata write-back (implemented through stage 2)** | None (only the NOTIFY payload was extended) |
| **The foundation** | The NOTIFY control messages (reload/ping) plus the `{prefix}mounts` registry plus the reload policy on Field | **Yes** (`{prefix}mounts` was added) |
| **config (and live apply)** ✅ | The `config` subcommand of the single `pgfsctl` (get/set/list/`--json`). A NOTIFY on set (`reload` or an inline `set`) makes it apply live. The control LISTEN was separated from notify_enabled (always ON). **Implemented, e2e green on real hardware** | None |
| **status** ✅ | The single `pgfsctl status [--json]`. **Layer 1 (the activity listing) and Layer 2 (the FS statistics) implemented, e2e green on real hardware** (from the database, read-only, no change to mount). **Layer 3 (a running process's cache statistics plus its effective settings) also implemented and green** (through the heartbeat snapshot) | None (the mounts.config/stats columns already existed) |
| **The GUI** | The GUI (a wrapper over config and status). **Avalonia (cross-platform desktop), calling Core in process. The skeleton and the read-only dashboard MVP are implemented (builds green; the visual check is manual). Config set and the finishing touches remain** | None |

---

## Caching in detail

[performance.md](performance.md) is the foundation (the InodeCache lock contention, and writeback_cache).
This section reorganizes it from the "go faster" angle.

| Item | Contents | ROI / risk |
|---|---|---|
| **The InodeCache capacity wiring** | `cache_max_entries` was only an initial hint with no eviction -> **make the cap real and evict by LRU**. At the same time, moving `lock(this)` to a `ConcurrentDictionary` (with the database fetch outside the lock) was considered | High / low (close to a bug fix) |
| **The content (read) cache** | Page-cache the `data_chunk` reads in memory. **Invalidation is essential for multi-client consistency** -> a "data-write NOTIFY" was added (a data write did not fire one). The alternative was a version/mtime column on the inode for a generation check | High / medium |
| **read-ahead** | Detect a sequential read and read ahead | Medium / medium |
| **Data write-back** | Have the kernel coalesce dirty data through FUSE's `writeback_cache` and cut the number of PG WRITEs. **It presupposed verifying the `pgfs_lock` exclusion, cross-client consistency and the extent of loss on a crash** -> **✅ implemented and verified on real hardware** (implemented in the application layer) | Medium-high / high |
| **Metadata write-back** | Accumulate creates, attribute changes and renames as pending and flush one file in one tx. **Implemented through stage 2, with the review findings (A-1 to A-10 and B-1 to B-13) all addressed** | High / high |

The implementation order was by ascending risk: **the wiring -> the read side -> the write side -> the namespace side**.

### The read-side caches

**Split out into [cache.md](cache.md)**. The cap and LRU of `InodeCache`, the content read cache, the
cross-client invalidation through the data-write NOTIFY, the negative cache and read-ahead (not started) are
owned there.

### Data write-back

**Split out into [write-back.md](write-back.md)**. How dirty chunks are represented, the granularity of the
flush, reserving a `data_id` block up front, the `mount.write_back` knobs and the measurements
(6.1x with `dd bs=128k`) are owned there.

### Metadata write-back

**Split out into [metadata-write-back.md](metadata-write-back.md)**. The pending-born principle, the ledger of
pending inodes, the three synchronization heuristics, the invariants of the flush tx and the as-built status
of stages 1, 1.5 and 2 are owned there.

### The review record of metadata write-back

**Split out into [metadata-write-back-reviews.md](metadata-write-back-reviews.md)**.
The findings and the as-built fixes of round A (A-1 to A-10) and round B (B-1 to B-13), and **making the live
disabling of data write-back two-phase**, are owned there. It was 711 lines at the time of the split, the
second largest document in the repository.

## The foundation, config and status in detail

**Split out into [control-plane.md](control-plane.md)**. The `{prefix}mounts` registry, the control NOTIFY,
the reload policy on Field, and the design and as-built status of `pgfsctl config` / `status` (Layers 1 to 3)
are owned there.

## The GUI in detail

**Split out into [gui.md](gui.md)**. A thin operations front end (Avalonia) that reads `config` / `status`.
The technology choice, the screen layout and the as-built status of the skeleton and the dashboard are owned there.

## The design questions that are still open

1. ~~**The control channel is gated by `notify_enabled`**~~ -> ✅ **decided**: the control LISTEN is always
   ON, and `notify_enabled` gates only the sending and receiving of data-change notifications. `config set`
   reaches a single-client mount too. See the control plane in detail.
2. ~~**One `pgfsctl` or two exes for `config`/`status`**~~ -> ✅ **decided**: a single `pgfsctl` (Core-only,
   with subcommands). A deliberate exception to the `{role}.pgfs` naming convention.
3. ~~**How a File-target (toml) setting is treated by `config set`**~~ -> ✅ **decided**: File+Live applies
   ephemerally live, carried inline on the NOTIFY (no database row is created, so there is no footgun of it
   coming back on remount). File+NextMount prints guidance for editing the local toml. See the matrix in the
   control plane detail.
4. ~~**Who creates `{prefix}mounts`**~~ -> ✅ **decided**: mkfs creates it. An existing FS gets it added
   idempotently and non-destructively by re-running mkfs (without --clean), and until then the mount skips
   with a warning.
5. ~~**The invalidation of the content cache**~~ -> ✅ **decided**: cross-client invalidation through the
   data-write NOTIFY (`"d"` = data_id) plus **a global generation guard** to close the stale-read race. The
   inode version/mtime column was rejected. Green in the e2e on real hardware.
6. ~~**Whether to bring in data write-back**~~ -> ✅ **decided (adopted)**: a raw-SQL benchmark projected
   **6.5x on a single PG / 8.1x on Citus rf=2** (mostly from the read-modify-write amplification of the chunk
   rows; [performance.md](performance.md)). The dirty representation, the flush granularity and the `data_id`
   reservation were settled = the write-back design. **Implemented and verified on real hardware.**
7. **The heartbeat interval and the stale threshold.**
8. ~~**The GUI technology choice**~~ -> ✅ **decided**: Avalonia (cross-platform desktop). The MVP is a
   read-only dashboard first. See the GUI in detail.
9. ~~**How metadata write-back handles visibility and audit consistency**~~ -> ✅ **decided**: the
   pending-born principle plus a relaxed close contract plus the three synchronization heuristics plus one
   file in one tx. It was settled with the findings of a two-lens adversarial review (concurrency /
   data safety, the POSIX contract and auditing) folded in. **Implemented through stage 2.** For where things
   stand, see the as-built status and the review results of metadata write-back.
