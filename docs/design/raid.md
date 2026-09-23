# A RAID over several PostgreSQL instances (multi-database mirroring) - a design note

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the idea of binding several PostgreSQL instances into
> one pgfs** (at the idea stage, not implemented) - per-path placement and the replica count, merging the
> namespaces, the free space reported to the OS, and generalizing it to be independent of the backing store.
> This document is also the source of truth for its own standing: **it is a place for the idea and the open
> questions, not for decisions.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [support_for_citus.md](support_for_citus.md) | Horizontal distribution **within a single database** (a layer orthogonal to this one) |
> | [df-support.md](df-support.md) | Obtaining the free space (`pgfs_statfs`) - the basis of the placement decision |
> | [v0.2.0-plan.md](v0.2.0-plan.md) | Splitting Core from the mechanism layers (where `RaidApi` would sit) |
> | [../Mount.md](../Mount.md) | The current specification of the **upper layer** on Linux (assumed to ride unchanged) |
> | [../Assign.md](../Assign.md) | The current specification of the **upper layer** on Windows (assumed to ride unchanged) |
> | [../next.md](../next.md) | **The priority** of whether to start on it (the punch-list) |

> **Status: at the idea stage.** Not implemented, not agreed. Assessing feasibility and settling the details
> are still ahead. This is a first sketch so the idea is not lost; it is not a set of decisions.
> A note generalizing it into "a union plus replication layer independent of the backing store" was appended
> later (see the generalization section). **That part is speculative as well.**

## The motivation and the idea

Point at several PostgreSQL instances and use **all of them together as one pgfs filesystem**.

- Striping (splitting one file, one entry, across them) is **not** done. Placement is **per file path**, with the whole file in one of the databases.
- Each database is left **completely unchanged** = each is an ordinary pgfs instance (the same schema and prefix, mountable on its own as-is).
- The aim: achieve **horizontal capacity addition (RAID0-like)** and **redundancy (RAID1-like)** purely through **a fan-out in the application layer (mount/assign)**, without touching the backend databases at all.

## The overall picture - how it would work

The current dependency is `tool exe -> the mechanism layer (Fuse/Dokan) -> Api -> one PostgreSQL`. Making it
a RAID only **inserts one aggregation layer** here:

```
            ┌─────────────────────────────────────────────┐
 FUSE/Dokan │  RaidApi  (implements the same surface as Api) │
 (unchanged)│  each FS operation = a fan-out plus a merge    │
            └───┬───────────┬───────────┬─────────────────┘
                │           │           │
            Api(child 1) Api(child 2) Api(child 3)   <- each an ordinary pgfs
                │           │           │
              PG #1       PG #2       PG #3          <- unchanged, mountable on its own
```

- **`RaidApi` (a working name)** implements the same surface as [Api](../../src/core/src/Api/Api.cs) and binds
  an array of child `Api`s. The FUSE/Dokan layer keeps working as though it were looking at a single `Api`
  (the direction of the dependencies does not change).
- Each child Api is an independent pgfs database. **The schema, the prefix and the DDL are unchanged**, so
  dissolving the RAID leaves each database usable as a standalone pgfs as-is.
- The configuration takes **several** `database.connection` values plus the RAID parameters below. It is
  orthogonal to Citus ([support_for_citus.md](support_for_citus.md)), and each child database may be either
  standalone or Citus.

## Placement and the replica count (on creation and on write)

RAID mode adds a **replica count** parameter (`raid.replicas`, tentatively). **The default when it is
omitted is 1.** Placement is decided from each database's real free space through
[`pgfs_statfs()`](df-support.md).

| Value | Equivalent to | Where a new file goes |
|---|---|---|
| **0** | RAID0 | One copy in **the database with the least free space** (= a pack strategy that fills one before spilling into the next) |
| **1 to n** | n-way replication | **n copies in order**, starting from the database with the least free space. When `n` is at or above the number of databases it means **the same as -1** |
| **-1** | RAID1 | A copy in **every database** |

- The default `1` = one copy. Its placement is `n=1` of the "1 to n" mode.
- "Put it where there is least free space" is deliberately **pack rather than spread** (avoiding
  fragmentation and keeping the databases with more free space in reserve for large files later).

## Merging the namespaces (what a read sees)

The trees of the N databases are merged and shown as one. For a given path, the entries at the same path are
collected from every database and then:

- **Deciding they are the same content**: entries whose `modification time` and `file size` agree are taken as
  **the same thing (a replica)** and **folded into one**.
- **Directories are the exception**: even with different `modification times`, the same name is taken as **the
  same directory** (the union of their children is shown).
- **When there are several divergent copies (different content)**:
  - `modification time desc, file size desc` makes **the newest one authoritative** (shown at the plain path).
  - The rest are shown with a **suffix** `~1`, `~2`, ... appended (newest first).
  - When numbering the suffixes, **a number that collides with an existing entry is skipped** (an unused
    number is chosen so that a real `name~1` is not trampled).

-> The policy is to show every conflicting copy rather than hide them (like Dropbox's "conflicted copy").
Replicas of each other appear as one.

## On write

- If a suffix had been applied on read (= there was a conflict), **every body is renamed in that state**
  (persisting the ambiguity of the conflict as a name, so what is shown is fixed).
- When the **number of entries for a logical file is below the replica count**, additional replica targets are
  chosen by the placement rule for new files and the content is replicated (**self-healing towards the target
  redundancy** triggered by a write).
- **The same write operation is applied to every (replica) entry** (a mirrored write).

## The free space reported to the OS (statfs / df)

- Report **the largest free space among all the databases** (optimistically: "at least this much can still be
  written"). Each database's free space reuses `pgfs_statfs()` from [df-support.md](df-support.md) directly.

## Things to watch while taking it further (to settle / open questions)

The route is as above. Roughly nine points remain to be settled. None of them is a reason not to do it; they
are things to decide.

1. **The difference between `0` and `1`**: as written, both "0 (RAID0)" and "the default 1" place *one copy in
   the one database with the least free space*, so their placement is identical. Either give `0` a distinct
   meaning (say, a pure label for "zero redundancy", or room for striping later) or fold it into an alias of `1`.
2. **The atomicity of replication and partial failures**: when only k of N succeed before it dies. The mtime
   and size drift between replicas, so the next read turns it into "a conflict = `~N`". Either **accept that**
   (surfacing it as a conflict) or add a best-effort resync.
3. **The assumption "mtime + size agree = the content is the same"**: the risk of a wrong merge from a
   coincidental match. Either use a fingerprint (a hash column) on `pgfs_data` alongside it, or accept
   mtime+size as specified.
4. **Cross-database consistency and locking**: the existing [`pgfs_lock`](support_for_citus.md) is within one
   database. Serializing a rename or a write that spans N databases needs something of its own (or has to be
   absorbed by surfacing conflicts as in point 2).
5. **Read cost**: a fan-out to N databases plus a merge for every `lookup` / `readdir`. The InodeCache in the
   aggregation layer, the cost of an N-way merge for readdir, and the behaviour when one lung is down (some
   database unreachable).
6. **The stability of the conflict suffixes**: `~N` can shift on every read depending on which databases are
   alive. The design fixes it by renaming the bodies on a write, but **how the view shifts during a read-only
   access** still has to be handled.
7. **`st_ino` and hardlinks**: with per-path placement, sharing an inode across databases is impossible. The
   merged `st_ino` needs numbering that is **unique across databases** (mixing a database index into the `id`,
   for example).
8. **Fanning out deletes and renames**: propagating to every replica. The semantics of deleting or moving a
   path that carries conflicts (`~N`).
9. **Expressing the configuration**: how several connections plus the replica count are written in the CLI and
   the TOML ([settings-matrix.md](settings-matrix.md)). Whether the child databases may differ in schema or prefix.

## What the current design already gives it (what can be reused)

- **Free space**: `pgfs_statfs()` from [df-support.md](df-support.md) is reused both for the placement decision and for the statfs report.
- **The layers that need no change**: as long as the aggregation layer preserves the `Api` surface,
  [Mount.md](../Mount.md) / [Assign.md](../Assign.md) (FUSE/Dokan) are untouched. The v0.2.0 split of the
  mechanism layers ([v0.2.0-plan.md](v0.2.0-plan.md)) already made Core OS-independent, so `RaidApi` sits in
  Core naturally.
- **Each database unchanged**: orthogonal to Citus. A child database can be either standalone or Citus.

## The generalization: a union plus replication layer independent of the backing store

> **Still speculative. Not a decision.** A thinking note: generalizing the "RAID over several PostgreSQL
> instances" above led to noticing that *it need not even be PGFS*.

### The realization

The aggregation layer (`RaidApi`) **does not care what backs its children**. Today the children are tied to
`Api` (PostgreSQL), but lifting the children to **a "backing store" abstraction** keeps the fan-out, merge and
replication logic exactly as it is while the backing need not be PG.

```
RaidApi -> IBackingStore[]
            ├─ PgfsBackingStore   (= the current Api / PostgreSQL)
            ├─ PassthroughStore   (= just a directory or a mount point. System.IO directly)
            └─ ... (later: an S3-style object store, and so on)
```

- **All PG** -> the "RAID over several PostgreSQL instances" above (the current sketch).
- **All plain directories** -> **a general union plus mirror FUSE with no PG at all** (= "it need not even be PGFS").
- **Mixed** -> the "separating the metadata plane from the data plane" idea below.

### Why it would ride so easily

The decisions in the current sketch already assume **only the lowest common denominator of metadata**:

- Deciding replicas are the same = **mtime + size** (the namespace merge) -> obtainable even on a dumb filesystem such as exFAT.
- The placement basis = **free space** (`pgfs_statfs`) -> obtainable from `statvfs` / `DriveInfo` for a plain directory.

It is built without depending on anything pgfs-specific (xattrs / ACLs / shared st_ino / hardlinks), so it
ports easily to a dumb backing (the design happened to reach for the lowest common denominator in advance).

### What it could be used for (speculatively)

- **RAID1 (a replica count of -1 or 2) = a media replication tool**: a live mirror into two directories. The
  write-triggered self-healing becomes the rebuild.
- **Ten USB sticks, each exFAT, with a replica count of 2 = an instant backup plus a distributed FS**: a
  mergerfs+SnapRAID-style heap-of-disks NAS, but with real-time replication.
- **The metadata plane = PG (pgfs) / the data plane = dumb disks**: exFAT cannot hold owner/mode/xattr/symlink/
  hardlink (it can hold mtime and size). The POSIX metadata, the replica catalogue and the st_ino numbering
  are made authoritative by **mixing in pgfs (PG) as a single catalogue**, while the bodies are spread and
  replicated across the USB sticks. The same shape as Ceph (MDS+OSD) and HDFS (NameNode+DataNode) -
  **pgfs takes only the part it is strongest at, the metadata database, rather than the whole filesystem**.

### The "Citus is not needed" argument (on capacity alone)

Putting **PostgreSQL's data directory** on top of this filesystem lets even a single PG **add the capacity of
many disks horizontally** -> *for capacity alone*, Citus is not needed.

- A caveat: PG can already spread across several disks with **tablespaces**. What the union FS adds is "made
  transparent behind one mount, replication included".
- What it does **not** replace: computation and throughput (a single PG is one process, one WAL, one node's
  CPU/RAM/IO), HA across machines, and distributed parallel queries. = **capacity is not performance.**
- **Beware the cycle**: making the RAID layer itself pgfs (PG) gives PG-on-FUSE-on-PG and destroys itself. For
  the data-directory use, the RAID layer must be **a dumb FS over real disks** (acyclic).

### Nearby existing systems (= evidence that the design space is real)

mergerfs (pooling) / SnapRAID (parity across heterogeneous disks) / git-annex `numcopies` (a replica count) /
Ceph and HDFS (separating the metadata plane from the data plane). This proposal sits as "pooling + real-time
n-way replication + conflict-copy (`~N`) reconciliation + optionally PG as the metadata authority" in a single
user-space filesystem.

### Further points to settle (the open questions the generalization adds)

On top of the nine above (all of them candidate decisions at the speculative stage):

10. **The backing-store abstraction**: how to cut the surface of `IBackingStore` (CRUD + statfs +
    capabilities). The minimal common ground between `PgfsBackingStore` and `PassthroughStore`.
11. **Feature capabilities**: xattr/ACL/symlink/hardlink support differs per backing -> express it as
    `Capabilities` and have `RaidApi` degrade (a backing that cannot hold something is either supplemented
    from the metadata plane or loses the feature).
12. **Where the metadata plane lives**: whether the POSIX metadata, the replica catalogue and the st_ino
    numbering for a dumb backing are held in a PG catalogue, in a sidecar, or not at all (the two-plane idea).
13. **fsync and ordering for a DB-on-FUSE**: for the data-directory use, PostgreSQL's fsync honesty, write
    ordering and tolerance of a partial replica on a crash are the one thing that decides success or failure
    (exFAT is weak on durability).
14. **RAID1 is not a backup**: a live mirror propagates deletes too -> it is useless against an accidental
    delete or ransomware (it is strong against a disk failure). Whether a trash or versioning belongs in a
    separate layer. The conflict copies `~N` are a partial safety net.
15. **Hot plug, degraded operation and rebuild**: degraded reads when a USB stick is pulled, and re-replication
    on its return or on an addition. Placement that is aware of failure domains.
16. **Extending how the configuration is expressed**: a notation that mixes children that are "a connection
    string (PG)" with ones that are "a directory path" (an extension of point 9).
