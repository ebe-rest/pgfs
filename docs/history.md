# The project history

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: the archive of how the completed items **that have no
> document of their own** came about. It holds the things where "why it was decided that way" would be missed if
> it were not kept, but that do not warrant a feature document of their own.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [README.md](README.md) | **The index of every document.** The entrance is there |
> | [next.md](next.md) | **What to start next.** Once something is done it moves here or into a feature document |
> | Each `design/*.md` | The design, the as-built and the record of changes of a feature that **has its own document**. That one is authoritative |
>
> **When adding to it**: if the feature has its own document, add to **its record-of-changes section**. Only
> when it has none does it go here.

A document keeping how the large past fixes and the decisions that led to the current design came about. Only
what is likely to need looking back on is kept here. The history of a completed item with no feature document of
its own is gathered here (where a feature document exists, that one is authoritative).

> **About the paths**: each entry is kept as **a fact as of when it was recorded**. The entries from before the
> v0.2.0 reorganization contain paths from the old structure (`src/lib/` / `vendor/Tmds.Fuse`),
> which are kept as they were as correct descriptions of the time (the current code is `src/core/` and so on).
> A new entry is written with the current paths as of when it is recorded.

---

## v0.2.1 - where settings live, permissions on Windows and deleting a filesystem (complete)

v0.2.1 is a release of corrections found while running v0.2.0 across several machines. **Each item's source of
truth is the linked document**; this entry only keeps why things were decided the way they were.

- **Settings fall into three kinds and each kind has one home**: client-specific settings (the connection, `mount.*`,
  notify, logging, retries) live in `pgfs.toml`; filesystem-specific settings (`audit.enabled`, `app.*`,
  `file_system.*`) live in the database and are fixed when the filesystem is created; creation-only instructions
  (`--clean`, `--root-access`, `--citus`, `--worker`, `--shard-count`, `--rf`, `--tablespace`) are stored nowhere.
  v0.2.0 wrote some of each kind to the wrong place, so running mkfs again silently overwrote filesystem settings and
  a `pgfs.toml` could register Citus workers by itself. The consequence that stayed visible: **whether a filesystem
  is Citus is read from the database itself** (the `citus` extension and `pg_dist_node`), never from a setting
  ([Mkfs.md](Mkfs.md)).
- **mkfs needs the settings file's location (`-f`)** and no longer searches the default paths, because a search could
  pick up (and then overwrite) the `pgfs.toml` of a mount that runs at logon.
- **Names that cannot be resolved are shown the way the OS shows them** (the kernel's overflowuid / overflowgid on
  Linux, `ANONYMOUS LOGON` on Windows) instead of configurable fallback names, and the name recorded when the
  creator's name is unknown is the filesystem setting `file_system.unknown_name`. What a client calls itself
  (`mount.self_uname` / `self_gname`) is a separate, client-side declaration
  ([permission-interop.md](design/permission-interop.md)).
- **Windows checks POSIX permissions itself**, because Dokan does not check access against the security descriptor
  the filesystem returns. The check follows the POSIX order rather than Windows's union of ACEs so that both
  operating systems give the same answer; the read-only attribute was changed to "nobody can write" at the same
  time, since judging it by the mounting user made other people's files undeletable from Windows
  ([permission-interop.md](design/permission-interop.md)).
- **Deleting a filesystem finds its nodes in the database** (`pg_dist_node`), refuses to start unless every worker is
  reachable, counts what is still connected and asks before it drops anything; `mkfs --purge` deletes without
  re-creating ([Mkfs.md](Mkfs.md)).
- **The root inode is re-read after a remote change**; it had been read once at mount time, so a `chmod` of the root
  on one mount stayed invisible on the others until they remounted ([cache.md](design/cache.md)).

## Turning the inode UPDATEs into router queries - resolving the Citus distributed deadlock (complete)

`{prefix}inode`'s UPDATEs had only `WHERE id = @id`, which does not include the distribution key (`parent_id`),
so **Citus was broadcasting them to every shard** (the Task Count of an `EXPLAIN` = the shard count). The real
harms were two: (a) one metadata update becomes "the shard count x the placement count" remote statements; (b)
the order in which the shard locks are taken is non-deterministic and a `40P01 distributed deadlock` happens
under concurrency. **Two of them actually failed in an `rsync -a` of 700 files (a chmod plus a utime per file)**
(rsync exited 23). The 38-case Linux e2e suite has low concurrency and did not surface it.

The countermeasure is including `parent_id` in the WHERE on all 9 paths that update an inode. The distribution
key is resolved by `Api.ResolveParentId` through the InodeCache, or one database read if it is not there, so
the signatures on the FUSE and Dokan sides are unchanged. For the details see
[support_for_citus.md, an UPDATE must always include the distribution key in its WHERE](design/support_for_citus.md).

**The effect**: the deadlocks are gone (0 rsync errors) but **the throughput only went up 9%** (2.08 -> 2.27
MB/s on Citus rf=2; a single PG is 13.46 MB/s). The benchmark and the cause analysis (the main cause being 1 FS
operation = 1 distributed transaction x the 2PC, with the 128 KiB `max_write` as the biggest lever) were
recorded in [performance.md](design/performance.md).

## Gathering the exclusion into `{prefix}lock` and making it independent of the replication factor (complete)

The response to the problem that **row locks cannot be used** when Citus's shard replication count
(`citus.shard_replication_factor`) is anything but 1. The source of truth for the design is
[support_for_citus.md, the exclusion and the replication factor](design/support_for_citus.md).

**How it started**: the shared Citus cluster (the dev server) defaults to `shard_replication_factor = 3`, and
distributing pgfs there makes `SELECT … FOR UPDATE` die with
`0A000 could not run distributed query with FOR UPDATE/SHARE commands`. It cannot be avoided with an equality
filter on the distribution key either (with rf > 1 it is statement-based replication and the results can differ
between placements, so Citus refuses it). Rebuilding the same table at rf=1 was confirmed A/B to work.

**The judgement**: switching the locking mechanism by rf is poor design. **Leave only the table being locked
undistributed** and make it independent of the rf.

* `{prefix}lock` went from `create_distributed_table('…','target_id')` to
  **`citus_add_local_table_to_metadata`** (one copy on the coordinator).
* **The row locks were gathered into `{prefix}lock` alone**: the 2 `FOR UPDATE`s on `{prefix}inode` (fetching
  the old row in `Api.Rename` and serializing the data row creation in `Api.EnsureDataRow`) were removed and
  left to the `LockInodes` / `LockInode` (= `{prefix}lock`) just before them. A `LockInode` was newly added to
  `EnsureDataRow` (its target_id is negative = taken before the positive data_id, so the ascending discipline
  holds too).
* As a result, the rf of `{prefix}inode` / `data` / `data_chunk` / `audit` can be chosen freely (rf=1 is the
  equivalent of RAID0 and rf=N of an N-way mirror: a pure choice about storage redundancy).

**Why a Citus local table rather than a reference table** (compared by measurement):

| | Row locks | Serializing across entry nodes | 100 locks (entering through the coordinator) | The placements |
|---|---|---|---|---|
| Distributed rf=1 | ✅ | - | 32 ms | 1 |
| Distributed rf>=2 | ❌ | - | - | rf |
| **Citus local** (taken) | ✅ | ✅ a 2.0-second wait | **11 ms** | 1 |
| A reference table | ✅ | ✅ a 2.0-second wait | 61 ms | Every node |

An advisory lock is node-local PG state and is invisible from another node, but **a Citus local table is one row
that really exists**, so whichever node is the entry point Citus routes to that one row. Citus 11+ has every
node hold the metadata and be able to be a query entry point (= the equivalent of multi-coordinator), and it was
confirmed by measurement that one test worker waits 2 seconds while another holds it. A reference table works
too but touches every placement on every lock, so it is 5.5 times slower and the lock itself fails if one
placement is missing. The trade-off is that **the locks concentrate on one row on the coordinator** (the old
document's "distribute on target_id to spread them across the workers" was withdrawn).

**Three mkfs options were added at the same time**: `--shard-count` and
`--shard-replication-factor` (`--rf`) issue a `SET` concatenated **into the same command** as the session that
calls `create_distributed_table` (a separate call could pull a different connection from the pool).
`--distribute-existing` is the explicit flag for making the existing tables Citus too (idempotent through the
`pg_dist_partition` check). The existing safety valve of not touching the topology mutations on an existing
database was kept.

**The verification** (the dev server's shared Citus 13.1 cluster plus a real PG 17.5):

* `mkfs --citus --rf 2 --shard-count 8` distributed the test schema -> 4 tables distributed (8 shards x 2
  placements) plus `lock`/`settings`/`mounts` local -> **Linux e2e 37 passed / 0 failed / 1 skipped (38
  cases)**.
* On non-Citus (a single PG) it was **37 passed / 0 failed / 1 skipped** = no regression from gathering the
  locks.
* `--distribute-existing`: a non-Citus FS holding a 3 MiB file was distributed in place -> **the md5 was
  unchanged**. A second run said "already under Citus management - skipped" for all 7 tables.

## The occupied bytes and `st_blocks` - sparse file support (complete)

The fix for `du` **reporting hundreds of times the real thing for a sparse file**. The source of truth for the
convention is [database.md, the occupied bytes and st_blocks](design/database.md).

**The symptom**: a file that was `truncate -s 1G`'d has 0 chunk rows = 0 occupied, yet `du` answers 1.0G,
because [src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) derived `st_blocks` mechanically from the
`st_size`. `df` (statfs) is a plperlu measurement and was correct; what was off was only the `st_blocks` that
`du`, `tar --sparse` and the like look at.

**An inconsistency found at the same time**: `pgfs_data.total_size` was defined as "the logical total size" yet,
after a literal `0` was put in at `INSERT` time, it **was never updated or read** (every row was 0). Layer 2's
`used` was derived from `sum(length(payload))` so there was no real harm, but the column was dead.

**The design taken**: `total_size` was redefined and maintained as **the occupied bytes (the sum of
`length(payload)` over every chunk)**, and `st_blocks = ceil(total_size / 512)`. **No schema change** was chosen
because `mkfs`'s idempotency does not add a column to an existing table (it does nothing if it exists), so
adding a new column would make an existing FS unmountable.

* **Maintaining it**: the 3 paths that change a chunk's payload (the UPSERT, trimming the last one, removing a
  trailing one) return the delta in the payload length, and one operation reflects it with one
  `UPDATE … total_size = GREATEST(0, total_size + delta) … RETURNING total_size`. A re-aggregating approach is
  quadratically slow on a large file and was not taken.
* **Reading it**: it is not on the inode row, so `Api.GetOccupiedBytes` fetches one row and caches it on the
  in-memory `Inode`. A directory enumeration has `ListChildren` prefetch them in one query with `id = ANY(…)` to
  avoid an N+1 on the getattrs. On Citus, the inode and the data are not colocated (the distribution keys are
  `parent_id` and `id`), so **a JOIN is not used**.
* **An existing FS**: `du` gives 0 while `total_size` stays 0, so a backfill SQL statement was put in
  database.md (running it once keeps it maintained from then on).

**Measured (the dev server / a real PG 17.5)**: `truncate -s 1G` gives `du` 0 and apparent 1.0G; writing 4 KiB
at the end gives `du` 1.0M (only one chunk is materialized and the hole is not filled); an ordinary 3 MiB file
gives `du` 3.0M; and `du` deduplicates hardlinks by their `st_ino`. A cold `ls -l` has the prefetch working with
no extra round trips.

**The regression test**: `test_sparse_du_blocks` ([tests/linux/e2e.sh](../tests/linux/e2e.sh)). It was confirmed
to FAIL under the mutation of deriving `st_blocks` from the `st_size` again.

## Unifying the timestamps on UTC (complete)

The fix for the bug where **local times were being stored** in the `TIMESTAMP` columns (`st_mtime` / `st_ctime` /
`created_at` / `updated_at` / `occurred_at` / `started_at` / `heartbeat_at`). The source of truth for the
convention is [database.md, the timestamp convention](design/database.md).

**The symptom**: the mtime and ctime FUSE returns are off by the host's UTC offset (+9h in JST). The reading side
(`DateTime.SpecifyKind(..., DateTimeKind.Utc)` in
[src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs)) assumed from the start that "the database values
are UTC", while the writing side put local times in on both of its paths.

1. **The SQL path**: a bare `current_timestamp` against a `timestamp without time zone` column is
   **the local wall clock of the session's TimeZone**. The column DEFAULTs
   ([src/mkfs/src/Initializer.cs](../src/mkfs/src/Initializer.cs)) and the
   `SET st_mtime = current_timestamp` family ([src/core/src/Api/Api.cs](../src/core/src/Api/Api.cs)) applied ->
   they were unified on `current_timestamp AT TIME ZONE 'UTC'`. The `now()` of `StatusAdmin.ListMounts`, which
   computes an elapsed time on the database side, was aligned to `now() AT TIME ZONE 'UTC'` too (because the
   columns became UTC).
2. **The parameter path**: passing a `DateTime` with `Kind=Utc` to Npgsql **sends it as a `timestamptz`, and PG
   casts it through the session's TimeZone when assigning it to a `timestamp` column**. So the places passing a
   `DateTime.UtcNow` (`ConfigStore.SaveJson` / `WriteFullChunk` / the `@mtime` of `UpdateTimestamps` / the audit
   `occurred_at`) were storing local times too -> they are normalized to **a UTC wall clock plus
   `Kind=Unspecified`** with `Pg.UtcNow` / `Pg.ToDbUtc()`
   ([src/core/src/Utility/Pg.cs](../src/core/src/Utility/Pg.cs)) before being passed.

**Why it went unnoticed for so long**: (a) the docker e2e suite has the container on UTC so the offset is 0, and
(b) within one mount session the value on the `InodeCache` is returned (keeping its Kind), so a `stat` right
after a creation looks correct. It surfaces only **after a remount, after a cache eviction, or when referenced
from another client** - that is, only when it is read back from the database. It first appeared when running
against a real PG on a JST host.

## Finishing off the Config work 

Three finishing touches on `Pgfs.Core.Config` (`Field<T>` / `ConfigLoader` / `Schema`). None of them has a
feature document of its own, so they are recorded here (the code in
[src/core/src/Config/](../src/core/src/Config/) is the source of truth for the implementation).

### Removing the double parse of the CLI arguments

mount and assign's `BuildRootConfig` builds in two stages because the ConfigStore (the database-derived
settings) needs its connection details from the CLI or the TOML, but the old implementation
**stood up a lite Loader and a full Loader separately and ran the CLI parse and the TOML read twice** (the same
result, duplicated work). It was unified into **reusing the same Loader instance and bolting on only phase 3
(the database) with [ConfigLoader.WithStore](../src/core/src/Config/ConfigLoader.cs) (a new fluent API)**:
`loader = new ConfigLoader(args, AllFields, null); var db = loader.BuildDatabaseConfig(); ...;
loader.WithStore(store).BuildRootConfig();`. The ctor's phase 3 block was **extracted verbatim** into
`ApplyStoreFields(store)` and is called from both the ctor (with store != null) and `WithStore` (the
store-taking ctor path is unchanged for backwards compatibility, and mkfs is a single shot with store=null so it
is unaffected). The invariant of the higher source winning - "the database fills in only the keys the CLI and
the TOML did not set" - is maintained. **The verification**: a throwaway probe (referencing Core.csproj, needing
no database) PASSed that (A) a fresh Loader's direct `BuildRootConfig()` and (B) a `BuildDatabaseConfig()`
followed by a `BuildRootConfig()` on the same Loader agree in their `DescribeProvided`, every resolved value and
the warning count (mount and assign's e2e suites go down this path every time, so a regression catches it too).

### The `Field` self-check

A guardrail that detects "declared but forgot to wire" for a settings Field at startup.
[ConfigLoader.Resolve](../src/core/src/Config/ConfigLoader.cs) records every `FullKey` it touched in
`resolvedFullKeys`, and the public
[ConfigLoader.UnresolvedFields](../src/core/src/Config/ConfigLoader.cs) returns the difference against
`Schema.AllFields` (enumerated from the nested static classes by reflection) = the Fields that were never
Resolved. `BuildRootConfig` is the single path common to every tool that builds every sub Config, so at its end,
if `UnresolvedFields()` is non-empty, each Field is pushed onto the Warnings (which come out in the startup log
and on stderr through `ConfigLoader.Warnings`; no tool filter is needed). **The verification**: a throwaway
probe PASSed that (1) after `BuildRootConfig()` it is AllFields=31 / unresolved=0 / warnings=0 (no false
positive with everything wired) and (2) with only `BuildMountConfig()` it is unresolved=26 including
`database.connection` (= a missed wiring is detected correctly). Forgetting the POCO or Resolve wiring when
adding a new Field is noticed automatically at the next startup.

### Generating the help automatically

The 3 hand-written `ShowHelp`s (in each of mkfs, mount and assign's `Program.cs`) were removed, and
[HelpText.Build](../src/core/src/Config/HelpText.cs) assembles the `--help` by walking the `CliOptions`,
`Comment`, type and default of [Schema.AllFields](../src/core/src/Config/Schema.cs). Each Program passes only
the introduction (the Usage) and the footer (the tool-specific prose about going through fstab, the unmount
procedure and so on). The per-tool selection is
[Field.AppliesTo](../src/core/src/Config/Field.cs) (a `[Flags] enum Tool`). Being a help-only filter, it does
not affect the CLI parsing (mount silently accepting `--citus` is unchanged). The value placeholder is derived
from the type (`<n>` / `<connstr>` / `<level>` and so on) and can be overridden explicitly with `Field.ArgName`.
The defaults of the bool flags and of the connection strings (which are secret) are hidden.

---

## Citus Phase 3: pgfs_lock plus a SELECT FOR UPDATE exclusion (complete)

The cross-client exclusion using row locks on the `pgfs_lock(target_id BIGINT PK)` created in advance in
Phase 2 was built into [Api.cs](../src/core/src/Api/Api.cs).
**[support_for_citus.md, Phase 3](design/support_for_citus.md) is the source of truth for the details of the
design, where it is built in and the implementation notes on the lock SQL.** Only how the decisions came about
is here:

- **Why "a custom TTL table plus a heartbeat" and `pg_advisory_xact_lock` were not taken**: the TTL race, the
  coordinator-local restriction and not being distributable by Citus (the details are in the option comparison
  table of support_for_citus.md Phase 3).
- **The namespace separation was implemented by "the sign of the target_id" rather than "a separate table"**:
  Citus's single-column distribution restriction forces the single-column `pgfs_lock(target_id BIGINT PK)`, so
  the namespace is separated by the sign of the value (`+data_id` / `-inode_id`). The idea of "carving
  pgfs_inode_lock out into a separate table distributed on parent_id and colocated" is left as room to
  reorganize if the asymmetric cost shows up in a workload dominated by inode locks.
- **The scope of what is locked is as in the documents' table**: WriteData/TruncateData/ReleaseData (a data
  lock) / Update{Mode,Owner,Size,Timestamps} (an inode lock) / Rename / DeleteInode / CreateHardLink (several
  inode locks). SetXAttr/RemoveXAttr/CreateFile/CreateDirectory/CreateSymlink are deliberately out of scope
  (a single UPDATE is atomic; the reason for not taking the parent's lock is in the documents' section on what
  is not locked).
- **The one-shot writes of `Pg.Execute` were promoted to being tx-based**: the 4 methods
  UpdateMode/Owner/Size/Timestamps could not hold a lock in the old one-statement `Pg.Execute(...)` structure.
  They were converted to `using var conn = NewConnection(); using var tx = conn.BeginTransaction()` plus
  `LockInode` plus the UPDATE plus a Commit.
- **`LockTargets`'s SQL was split into 2 statements**: an `INSERT ... ON CONFLICT DO NOTHING` plus a
  `SELECT ... FOR UPDATE`. Folding them into one with a CTE was not taken, preferring simplicity (each has a
  WHERE on the target_id alone so it completes within a single shard on Citus too).
- **Accepting the stale-hintParentId race of `Rename`**: another client can complete a rename between fetching
  the cached.ParentId and the LockInodes, but the staleness is detected by the following
  `SELECT ... WHERE parent_id = @hint AND id = @id FOR UPDATE` returning nothing and taking the false path
  (a wrongly taken old-parent lock is released automatically at the end of the tx).

**The verification** ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh), 4/4 PASS on
2026-05-26):
- The Linux e2e suite passes 34/34 on multi-node Citus (= an overall confirmation that Phase 2's cross-shard
  hops and Phase 3's locks work)
- A concurrent write race (urandom against zero, 8 MiB x 4 rounds) has the md5 and size seen from both clients
  always agreeing
- Concurrent same-name mkdirs (20 rounds x serial plus parallel) always have exactly 1 client get EEXIST
- The pgfs_lock accumulation: after the 4 tests, rows=178 / size=768 kB (consistent with the design figure of
  "about 50 bytes per row, under 100 MB at a million rows")

**A by-product**: a `PGFS_TEST_PG_EXEC` environment-variable override was added to the `pg_exec` of
[tests/linux/e2e.sh](../tests/linux/e2e.sh) (for test_fallback_uname_gname, which used to hard-code an ssh plus
psql to the PG server). race_multinode.sh sets `docker exec -i $COORD_NAME psql ...` and passes it in.

---

## Citus Phase 2: making the tables distributed plus mkfs --citus plus multi-node (complete)

On top of Phase 1 (the move to bytea) removing the `pg_largeobject` dependence, a `mkfs --citus` was implemented
that distributes 4 tables with `create_distributed_table` and places `pgfs_settings` locally. Both
**a single-node configuration (no workers)** and **a multi-node configuration (a coordinator plus N workers,
`--worker host[:port],...`)** can be brought up with mkfs alone.

**How the design iterated** (3 times):
- The first version carved `SetupCitusDistributionAsync` out at the facade level and branched on it in
  `InitializeAsync`
- The user's feedback led to a redesign for "no tablespace can be given / read-only when the database exists /
  integrating into `EnsureDatabaseAsync` / a per-table create_distributed_table"
- Further feedback led to "a `CREATE SCHEMA` is enough through the propagation / a
  `citus_set_coordinator_host` on each worker might be needed too", and in the end
  [multinode_probe.sh](../tests/citus/multinode_probe.sh) confirmed on real hardware that "the auto-sync of
  `citus_add_node` syncs the coordinator into the worker's pg_dist_node automatically" -> it was settled that
  **Phase 4 (a per-worker citus_set_coordinator_host) is unnecessary** and it was deleted

**[support_for_citus.md, Phase 2](design/support_for_citus.md) is the source of truth for the details of the
distribution strategy, handling the Citus restrictions, the mkfs flags and the idempotency guarantees** (making
the PK composite / the distribution key being mandatory for FOR UPDATE / the IMMUTABLE restriction on a DO
UPDATE clause / the DELETE+INSERT of a cross-shard rename / the independent checks for the coordinator
registration and shouldhaveshards and so on). Only the 2 points at the level of a policy decision are here:

- **The branch of "create the UK on the id alone only in the single-PG mode" is not taken**: the schema would
  differ between single-PG and Citus, an in-place migration would get stuck on the UK, and a validation at
  application startup only makes it slower with no perceptible benefit - so there is no UK in either mode and
  the uniqueness is kept by the BIGSERIAL sequence plus tx discipline. Check this judgement before re-proposing
  "let us guarantee the uniqueness of the id just in case" (the details are in stumbling block 1 of
  support_for_citus.md Phase 2).
- **`SetupCitusDistributionAsync` was dismantled and its responsibilities spread**: the cluster topology
  settings went inside `EnsureDatabaseAsync`, and the per-table `create_distributed_table` /
  `citus_add_local_table_to_metadata` went inside each `CreateXxxTableAsync`. `CreateTableAsync` was changed to
  a `Task<bool>` so that the distribution only runs when a table was newly created (consistent with skipping the
  Citus mutations when the database exists).

**The test results**:
- The mkfs matrix test ([test_matrix.sh](../tests/citus/test_matrix.sh)): 3 initial x 6 target =
  **18/18 PASS** (Citus 14.0.0 on docker, on the Linux client, 2026-05-26)
- The single-PG mode and single-node Citus (on the PG server, Citus 13.1.1): Linux 34/34 and Windows 24/24 ALL
  PASSED
- The Linux e2e suite on multi-node Citus was done together with Phase 3 in
  [race_multinode.sh](../tests/citus/race_multinode.sh) -> see Phase 3 above

**The rejected options** (considered and not taken):
- Distributing `pgfs_inode` on `id` and guaranteeing the UK in a separate layer - the `(parent_id, name)` UK
  breaks and preventing an application-layer race becomes complex
- Making `pgfs_settings` a distributed table - unnecessary, since only the coordinator reads and writes the
  settings

---

## Citus Phase 1: large objects -> bytea (complete)

As a precondition for scaling the capacity (the Citus distribution), the storage of the file bodies was replaced
from PG's large objects with a bytea column. **It is an independent change that stands on its own even in
single-PG operation, not only with Citus** (the large objects' 2 RTTs of `lo_seek` plus `lo_read` become 1 RTT
of `substring(payload from N for M)`, so on a workload without many partial writes bytea is slightly faster).

**Why it is needed**: `pg_largeobject` is one of PG's system catalogs and cannot be a target of
`create_distributed_table`. However many workers are added, the large object bodies stay concentrated on the one
coordinator forever and the capacity wall cannot be crossed. A bytea is a column of a user table so Citus can
distribute it.

**[support_for_citus.md, Phase 1](design/support_for_citus.md) is the source of truth for the core of the
implementation and the semantics** (the DDL, the SQL patterns, the TOAST partial detoast). Only how the design
decisions came about is here:

- **Each chunk's payload length = "the number of bytes written so far"** (following the large object semantics
  as they are). The option of "fixing every chunk at the chunk_size (with zero padding)" was considered, but the
  variable length was chosen for the storage efficiency and the accuracy of du.
- **WriteData completes in one upsert per chunk**: the 3-case branch of
  `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`. A concurrent WriteFile
  race is serialized automatically by PG's row lock (the old large object version's rescue of
  `lo_create` plus `ON CONFLICT DO NOTHING` plus an orphan `lo_unlink` is unnecessary).
- **`repeat(bytea, integer)` does not exist in PG** (there is only `repeat(text, integer)`) - the zero padding
  is assembled with `decode(repeat('00', N), 'hex')`. It was first written as
  `repeat('\x00'::bytea, N)` and surfaced in the first e2e run.

**The test results**: Linux 34/34 and Windows 24/24 ALL PASSED.

**The rejected options**:
- **"Keep the large objects and distribute and replicate them to the workers"**: distributing a custom
  large-object table with Citus is equivalent to "a bytea with a variable page size", so there is no point.
- **Fixing the chunk payload at the chunk_size (with zero padding)**: the variable length was chosen because
  "the du figure being what it looks like" was wanted.

---

## The model class tidy-up refactor

The old `Pgfs.Lib.Models.*Settings` tree (`Setting` / `Settings` / `RootSettings` / `BaseProvider<A>` /
`ChangingEventArgs` / the `JSON_INVALID` sentinel / the negative ids of `Statics.GetNextId()` and the other
"clever" machinery) was replaced with **static `Field<T>` descriptors plus mutable POCOs plus `ConfigLoader`
(merging the CLI/TOML/database/defaults) plus `ConfigStore` (the database I/O)**. The final form is under
[src/core/src/Config/](../src/core/src/Config/). The Schema is
[Schema.cs](../src/core/src/Config/Schema.cs) (enumerating every Field automatically by reflection).

| The step | An outline |
|---|---|
| **1** `MountConfig` | The `Pgfs.Lib.Config` namespace was created. The `mount.*` scope (mount_point / cache_max_entries / fallback_uname / fallback_gname / foreground) was turned into a Config with the pattern of static `Field<T>` descriptors plus a POCO plus `ConfigLoader`. `Api(RootSettings)` became `Api(RootSettings, MountConfig)`, and the old `Api.LoadMountFallbackSettingsFromDb` was replaced with going through `ConfigStore.LoadAll`. The old `RootSettings` remained for the other scopes |
| **2** `FileSystemConfig` plus the `RootConfig` aggregate | `Schema.FileSystem.*` (version / volume_label / cluster_size / default_chunk_size / max_file_size) and `LongField` were added. To avoid `Api`'s ctor arguments swelling with every new sub Config, the aggregate `RootConfig` was introduced and it became `Api(RootSettings, RootConfig)`. `Schema.AllFields` enumerates automatically by reflection (a new Field is included automatically) |
| **3** `SettingFileConfig` plus reordering the phases to CLI->TOML->DB | setting.* (file / search_path) was turned into a Config. The chicken-and-egg (`setting.file` deciding the TOML path) is resolved inside the ConfigLoader. The phases were reordered to **CLI -> TOML -> the database** plus "skip if something higher has already put it in", securing the priority (CLI > TOML > the database > the default). `StringListField` was added (comma-separated), with a recursive comma-join for a `TomlArray` |
| **4** `LoggingConfig` | logging.* (level / output) was turned into a Config. `LogLevelField` (delegating to `Level.Parse`) and `LoggingOutputField` (`stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`) were created. The default of `logging.level` went from the old Warning to **Information**. **`Logger.MinLevel = config.Logging.MinLevel` was applied for real in mount and assign's Main**, so the setting took effect for the first time (in the old LoggingSettings era it was declared but never reached the real thing). `--log-level trace` was added to `tests/linux/flow.ps1` to capture the SQL trace log in the tests |
| **5** `DatabaseConfig` plus removing the old `RootSettings` from mount and assign entirely | database.* was turned into a Config, and `ConnectionField` (an `NpgsqlConnectionStringBuilder`, passing both the kv form and the URL form through the ctor) was created. `Schema.Root.Help` / `Schema.Root.Clean` (scope=`root`) were added and received through `RootConfig.Help` / `RootConfig.Clean`. `MountConfig.FuseFlags` was added (the buffer for `-o allow_other` and so on). It became **Api(RootConfig)** and `InodeCache(RootConfig)`. The `new RootSettings()` / `ParseArguments` / `LoadFromFile` were removed entirely from mount and assign's `Program.cs`. The Help check is decided in advance with a lite Loader at store=null (so that `--help` does not attempt a database connection). Building the Config itself stays two-stage (lite -> build the store -> full) to resolve the chicken-and-egg |
| **6** Flattening `pgfs_settings` plus moving mkfs onto `RootConfig` plus implementing `ConfigStore.Save<T>` properly | `pgfs_settings` went from the old hierarchical `(id, parent_id, key, value)` to a flat `(scope, key, value)` PK (requiring a database rebuild, handled with `mkfs --clean`). `ConfigStore` was rewritten: `LoadAll` is a simple read of everything plus an allow-list filter, and `Save<T>` UPSERTs with `INSERT ... ON CONFLICT (scope, key) DO UPDATE`. `Field<T>.FormatJson(value)` was created and overridden in IntField/LongField/BoolField (the native JSON representation), with everything else being `JsonSerializer.Serialize(Format(value))`. `Schema.Database.SuperConnection` was added with `SaveTarget.None` and reflected in the `DatabaseConfig.SuperConnection` property (only mkfs reads it). A `ConfigLoader(..., skipToml)` argument was added and used to skip the TOML under `--clean`. **mkfs was rewritten wholesale**: `Initializer(RootSettings)` became `Initializer(RootConfig)`, the TOML write-out assembles a `Tomlyn.Toml.FromModel(TomlTable)` inside mkfs, and `PopulateSettingsRows` became an explicit enumeration of `store.Save(Schema.X.Y, config.X.Y)` (the 4 of mount.fallback_uname / fallback_gname / file_system.version / file_system.volume_label) |
| **7** Removing the old `Models/*Settings.cs` entirely | The 13 files `RootSettings.cs` / `MountSettings.cs` / `DatabaseSettings.cs` / `FileSystemSettings.cs` / `LoggingSettings.cs` / `SettingSettings.cs` / `Setting.cs` / `Settings.cs` / `BoolSetting.cs` / `SettingStorage.cs` / `BeforeChangeEventArgs.cs` / `DatabaseConnectionSetting.cs` / `Primitive.cs` were removed. `Base.cs` was simplified into the minimal form holding only the 5 columns `id` / `created_at` / `created_by` / `updated_at` / `updated_by` that `Inode`/`Data`/`Chunk` need (the `BaseProvider<A>` / `ChangingEventArgs` / `Created`/`Updated` / `OnIdChanging` / `Statics` were removed). The dead properties `Created = true` / `Updated = true` written when inserting the root inode in `InodeCache.cs` were removed too. What remained in `Models/` is only the 7 files of the database row models (`Inode.cs` / `Data.cs` / `Chunk.cs` / `Base.cs`) plus the enums `LoggingOutputField` references as types (`SettingLoggingOutput.cs` / `SettingLoggingCycle.cs` / `SettingLoggingKind.cs`) |

Linux 34/34 and Windows 24/24 ALL PASSED were maintained at the end of each step.

---

## The active design decisions and the current infrastructure

### Replacing Tmds.Fuse

The upstream `tmds/Tmds.Fuse 0.1.0-190711-50` (not updated since 2019) was switched to the active fork
`securefolderfs-community/Tmds.Fuse` (last updated 2026-03, .NET 10). It was taken in as a submodule at
`vendor/Tmds.Fuse/` (the credits are in [NOTICES.md](../src/fuse/NOTICES.md)),
[Mount.csproj](../src/mount/Mount.csproj) referenced it as a ProjectReference, the `NuGet.Config` (the MyGet
feed) was removed, and the vendor csproj was added to `pgfs.sln` (for the Release/Debug config propagation).

That resolved the 3 constraints being hit with the upstream version:
- **There is no `MountOptions.Options`** -> the fork added it. `-o allow_other,attr_timeout=0,...` can be told
  to libfuse.
- **`attr_timeout=0` cannot be passed** -> it is passed by default. Disabling the kernel attribute cache makes a
  `stat a` right after `ln a b` return the new `st_nlink` at once (the `sleep 1.1` workaround was removed).
- **`-o use_ino` is refused as an `unknown option` on libfuse 3** -> a downstream patch was applied in the
  fork's `Init` callback writing 1 into `fuse_config.use_ino` (at offset 64). That makes a hardlink's
  `stat -c '%i'` agree (the inode equality assertion of `test_hardlink_basic` came back).

Logic for assembling the FUSE options was added to `Mount/Program.cs`: the default `attr_timeout=0` plus the
received `FuseFlags` are joined with `,` and set on `Tmds.Fuse.MountOptions.Options`.

### The `/etc/fstab` support

The positional arguments (source = the connection or the setting.file, target = the mount point) and an
`-o key=val,flag,...` parser were built into
[`ConfigLoader.ParseCli`](../src/core/src/Config/ConfigLoader.cs). The first positional branches on a heuristic:
starting with `postgresql:` makes it `database.connection` (the URL form), and otherwise `setting.file` (a TOML
path) (letting `mount.pgfs postgresql://... /mnt/pgfs` and `mount.pgfs /etc/pgfs.toml /mnt/pgfs` coexist; the kv
form cannot be told apart so `-c` is mandatory for it).

The irrelevant flags from fstab and the `mount(8)` helper (`-i`, `-f`, `-n`, `-s`, `-v`, `-N`, `-t`, `_netdev`,
`noauto`, `noatime`, ...) are silently skipped **only in a helper context**. The helper-context decision is the
AND of (a) there being a positional in `args` and (b) the parent process's comm (`/proc/<ppid>/comm`) being
`"mount"`. On anything other than Linux it is always treated as a direct invocation (the same code runs safely
in `assign.pgfs` too). That makes `-f` (= the short form of setting.file) and `-s` (= the short form of
database.schema) effective **only on a direct invocation**.

Automatic daemonization through child-process separation (the MOUNTED signal scheme: the child emits
`PGFS_MOUNTED_OK` as the first line of its stdout, and the parent exits once it reads that and releases
mount(8); `--foreground` pins it to the foreground).

**The PATH-stripping problem when mounting through fstab**: `mount(8)` strips PATH from the environment
entirely when calling the helper (measured, only 8 to 11 environment variables remain: LANG, LOGNAME, PWD,
SHLVL, SUDO_*, TERM, USER, _). Tmds.Fuse's `HasFusermount` looks for `fusermount3` on `$PATH`, so
`CheckDependencies` returned false and it exited immediately. It was resolved by having the head of `Main` in
`Mount/Program.cs` fill in `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin` when PATH is empty
(inherited by the child too through `Process.Start(UseShellExecute=false)`).

For the detailed specification see [fstab-support.md](design/fstab-support.md). A real
`sudo mount -t pgfs -o allow_other ...` was confirmed through a root mount plus non-root user access plus the
umount.

### Retrying a failed connection

Rather than bringing in Polly, a lightweight in-house one
([src/core/src/Utility/Retry.cs](../src/core/src/Utility/Retry.cs)) wraps `Pg.OpenConnection` /
`OpenConnectionAsync`. The transient decision is a `SocketException` / a `TimeoutException` / a
`PostgresException` with one of the SqlStates (`57P03` / `57P01` / `57P02` / `08000` / `08003` / `08006` /
`08001` / `08004` / `53300`) / any other `NpgsqlException`.

**An exception during a query is not retried** (it would break the write idempotency, so the retry is confined
to the connection open). The exponential backoff is controlled by `database.retry_max_attempts` /
`_initial_delay_ms` / `_max_delay_ms` (5 / 200 ms / 2000 ms by default), read by the `Api` ctor and applied
globally with `Retry.Configure`.

### The notification of another client's changes (Notify)

Cross-client change propagation using PostgreSQL's `LISTEN` / `NOTIFY`. The new files
[NotifyChannel.cs](../src/core/src/Api/NotifyChannel.cs) plus
[RemoteChangeInfo.cs](../src/core/src/Api/RemoteChangeInfo.cs).

- **Opt-in**: `database.notify_enabled` (false by default). It can be enabled through the CLI `--notify`, the
  TOML `[database] notify_enabled = true` or `-o notify-enabled`. There is the overhead of one dedicated
  connection plus a `SELECT pg_notify(...)` riding on every write, so OFF is preferable for single-client
  operation.
- **The channel name**: `{schema}_{prefix}notify` (for example `pgfs_pgfs_notify`). Several pgfs instances in
  the same database do not collide.
- **The payload**: the JSON `{"s":<sender>,"i":[ids],"p":[parent_ids],"x":[path_prefixes]}` (within the
  8000-byte limit). The sender id is 8 hex characters and a self-message is filtered on the receiving side.
- **Tied to the writes**: `this.Notify(...)` is embedded at the end of `InsertInode` / `DeleteInode` /
  `UpdateMode|Owner|Size|Timestamps` / `Rename` / `WriteData` / `TruncateData` / `SetXAttr|RemoveXAttr` /
  `CreateHardLink`. It publishes outside the transaction rather than inside (after the commit), through
  `Pg.Execute`'s autocommit.
- **The receiving side's handling**: `OnRemoteChange` (inside the Api) (1) resolves the path with
  `InodeCache.TryGetPath`, (2) applies `InodeCache.Invalidate` / `InvalidateChildren` / `InvalidatePrefix` and
  (3) calls `Api.OsBridge` with the pre-resolved path if one is registered.
- **The OS notification bridge**:
  - **Assign (Windows)**: [FileSystem.PropagateRemoteChange](../src/dokan/src/FileSystem.cs) calls
    `DokanInstance.NotifyUpdate(WindowsPath)` to ask Explorer to redraw. The `/` -> `\` conversion is the
    `ToWindowsPath` helper.
  - **Mount (Linux)**: Tmds.Fuse's high-level API has no equivalent of `fuse_lowlevel_notify_inval_*`, so the
    OS bridge is unimplemented. Thanks to `attr_timeout=0`, **an active query** such as a `stat` or an `ls`
    returns the latest value (a database hit through the InodeCache). **It does not reach a passive subscriber
    such as `inotify`**, which remains a constraint. Moving to the low-level API (or adding a notify patch to
    the vendored Tmds.Fuse) is a future matter.
- **Establishing the LISTEN is synchronous**: opening the connection, issuing the `LISTEN` and the Information
  log all complete on the calling thread inside `NotifyChannel.Start()`. Only the wait loop after that is in the
  background (`Task.Run`). The log appears before the MOUNTED signal at `mount.pgfs` startup, so the startup can
  be confirmed in `tests/linux/mount.log` whether it came through `-f` or through fstab.
- **Reconnecting after a disconnection**: the wait loop catches the exception, waits 2 seconds, opens a new
  connection and re-`LISTEN`s. It is a separate system from the exponential backoff retry of the
  `Pg.OpenConnection` family (the LISTEN-dedicated connection is long-lived so it manages itself).

The verification: Linux 34/34 and Windows 24/24 ALL PASSED (no regression with `--notify` enabled either). The
Information log `NotifyChannel: LISTEN pgfs_pgfs_notify (sender=...)` was confirmed in
`tests/linux/mount.log`. **An automated test of the real cross-client behaviour (two mount.pgfs instances with
one's write reaching the other) is not in place** (the e2e harness assumes one mount), and the policy is to
supplement it with manual verification.

### The fallback for a uname or gname that does not exist on the OS

`mount.fallback_uname` (`nobody` by default) and `mount.fallback_gname` (`nogroup` by default) were implemented
with `SaveTarget.Db` (the intention being one value for the whole FS, as it is a security setting). The
constructors of Linux's `UserResolver` and Windows's `WindowsUserResolver` were rewritten to be
`(string fallbackUname, string fallbackGname)`-based, and the fallback names are resolved through the OS APIs
(`getpwnam` / `NTAccount.Translate`) and cached internally at startup. The final hardcode on a failure is
uid=65534 on Linux (NFS's conventional `nobody`) and `WellKnownSidType.AnonymousSid` on Windows, with a warning
logged.

That resolved the behaviour that had been a security accident of "masquerading as the running process's uid" and
"someone else's file looking like one's own". `test_fallback_uname_gname` was added to the Linux e2e suite
(an inode with a bogus uname is created by a direct database INSERT, `nobody`/`nogroup` are confirmed by a stat,
and it is cleaned up with a DELETE).

### The Windows e2e test foundation

`e2e.ps1` (the tests themselves), `flow.ps1` (the whole flow of mount -> test -> unmount), `flow.cmd` (a cmd
wrapper) and `run.cmd` (the tests only) were created under [tests/windows/](../tests/windows/README.md).
Symmetrically with the Linux version, there were 24 cases: mkdir / the file basics / the data I/O (crossing the
chunks) / truncate / rename / ReadOnly / the Hidden+System+Archive xattr round trip / the dotfile heuristic /
persisting a dotfile un-hide / LastWriteTime / the volume information / FindFilesWithPattern / concurrent
access.

The POSIX-only ones (symlink / hardlink / chmod / chown / a public xattr API) are out of scope because DokanNet
does not support them, and Windows-specific ones (SetFileAttributes / SetFileTime / FindFilesWithPattern) were
added instead.

**Persisting the Hidden / System / Archive attributes in an xattr**: the mask value of the
`FileAttributes` (only Hidden, System and Archive) was saved as a 4-byte little-endian int under the
`user.win_attrs` key of the `pgfs_inode.xattrs` JSONB. **Even when the mask is empty (everything off) the xattr
is not removed and 4 zero bytes are written**: it is treated as the binary "the xattr is there = SetFileAttributes
was touched from Windows" / "the xattr is not there = it has not been touched yet", and only when the xattr is
absent is the "a leading dot = Hidden" heuristic applied as a fallback. A design of removing it at a mask of 0
hits the UX bug of "un-hiding a dotfile in Explorer -> the heuristic comes back on the next read and it goes back
to Hidden". `ReadOnly` continues to be the write bits of the st_mode. The implementation is `WinAttrsXattrKey` /
`LoadWinAttrs` / `SaveWinAttrs` in [FileSystemUtils.cs](../src/dokan/src/FileSystemUtils.cs).

### Fixing the races on the write path

- **The SELECT-then-INSERT race of `Api.EnsureChunk`**: when Dokan issues concurrent WriteFiles, the same
  `(data_id, chunk_index)` is INSERTed twice and it dies on the PK constraint `pk_pgfs_data_chunk` (the symptom
  being a `Copy-Item` of a 2 MiB file failing with an `IOException` on Windows, with
  `HResult=0x8007013D` = `ERROR_MR_MID_NOT_FOUND` and the error text not expanded). It was changed to
  `INSERT ... ON CONFLICT (data_id, chunk_index) DO NOTHING`, with the losing tx's orphan large object reclaimed
  with `lo_unlink` and the winner's OID re-fetched with `FindChunkOid`.
  **Not hitting it on Linux was only because FUSE's caching behaviour makes concurrent WriteData unlikely; it
  was very likely a latent race on the Linux side too.**
- **The same pattern remained in `Api.EnsureDataRow`** and was fixed preventively: with two or more concurrent
  txs seeing the in-memory `inode.DataId` as null, both `INSERT pgfs_data` and the last-wins
  `UPDATE inode SET data_id` leaves the loser's data row floating, with the chunks written there becoming
  unreachable - a silent leak plus data-loss bug. `pgfs_data.id` is a serial and does not collide, so it does
  not die loudly, and it may have been broken even while the tests passed. The fix serializes the txs by locking
  the inode row with `SELECT data_id FROM inode WHERE id = @id FOR UPDATE` and re-reading the true `data_id`
  from the database after taking the lock (the database, not the cache, is the truth). If the `UPDATE` hits 0
  rows, the inode is judged to have been removed concurrently and it throws -> the tx rolls back, preventing an
  orphan data row.

### The Linux e2e suite: the symlink and hardlink fixes

- **The argument interpretation of `Mount.FileSystem.SymLink(path, target)` was swapped**: the pre-fork
  Tmds.Fuse 0.1's `FuseMount.Symlink(path*, path*)` repacks libfuse's `(target, linkname)` in reverse and passes
  it to `IFuseFileSystem.SymLink` (confirmed by verifying the DLL's IL:
  `ldarg.2 -> ToSpan -> ldarg.1 -> ToSpan -> callvirt SymLink`). Our override misread arg1 as the link content
  and arg2 as where the link was created, so the INSERT never ran once.
- **Invalidating the caches of the hardlink sibling inodes in `Api.DeleteInode` and `Api.CreateHardLink`**:
  previously only the source or target inode was `inodeCache.Invalidate`d, so 3 or more hardlinks, or a
  `stat b` after an `rm a`, returned a stale `st_nlink`. The sibling ids are fetched inside the tx with
  `SELECT id FROM inode WHERE data_id = @did` and all of them are `Invalidate`d after the commit.

### The infinite-loop bug of `LoadFromDatabase`'s recursive CTE -> resolved

When the old `RootSettings.LoadFromDatabase` had the root's `Id` and `ParentId` at `1`, the BIGSERIAL's first
element id=1 became the self-referencing row `(id=1, parent_id=1)`, and the recursive part
`ON s.parent_id = st.id` joined the same row forever. It was resolved by making the root id=0/parent_id=0
(a BIGSERIAL never returns 0, so a self-loop row cannot exist in principle and a safe CTE can be written with no
cycle detection). It also aligned the convention with `pgfs_inode`'s root (`id = 0`).

**This problem disappeared at the root when `pgfs_settings` was flattened** (a `(scope, key)` PK has
no parent_id, so a recursive CTE is unnecessary in the first place). This entry is a memo of how it came about.

---

## Establishing coding conventions v4

[docs/coding-style.md](design/coding-style.md) was created, and the mandatory braces plus the forced `this.`
qualifier were added to the `.editorconfig`. The conditional rules (the body is "a trailing flow exit plus N
arbitrary statements" or "a single statement only", no `else`, no ternary, a multi-way branch is a `switch`, and
log output is not counted as a side effect) were written down as v4. `FirstList.cs` was brought into line with
v4 (rewriting FindSegmentNode, extracting Try* from InsertNodeFirst/Last). The 26 logger guards in `Api.cs` were
given braces.

The tidy-up removed the old `Models/*Settings.cs`, which took most of the `else`s and ternaries there with it.
