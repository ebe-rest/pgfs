# The Citus design memo

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the design decisions of the Citus horizontal
> distribution** - the move to bytea (Phase 1), the distribution strategy per table and the choice of the
> distribution keys, the shard count and the replication factor, the exclusion through `{prefix}lock` plus
> `SELECT FOR UPDATE` (Phase 3), and the shard-touch order of a write tx. "Why it takes this shape on Citus" and
> how it differs from a single PG are authoritative here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [database.md](database.md) | **The schema itself** (each table's role, the meaning of its columns, the timestamp convention) |
> | [../ddl/README.md](../ddl/README.md) | **The DDL itself** (one table = one file) |
> | [../Mkfs.md](../Mkfs.md) | **The CLI specification and the defaults** of `--citus` / `--shard-count` / `--rf` / `--distribute-existing` |
> | [performance.md](performance.md) | **The measurements and the improvement candidates** for the performance (relieving the cross-shard hops, the InodeCache) |
> | [df-support.md](df-support.md) | **Aggregating the free space** across nodes (`pgfs_statfs` / plperlu) |
> | [raid.md](raid.md) | A separate layer that binds **several PostgreSQL instances** into one FS (this document is about distribution within one database) |
> | [../tests.md](../tests.md) | **The list of tests, their counts and how to run them** in a Citus environment |
> | [../history.md](../history.md) | How the completed items that have no document of their own came about (the Notify design and so on) |
>
> **An external reference**: the [Citus docs](https://docs.citusdata.com/) - distributed tables in general
> (the links to the implementation code are in each section).

A document gathering the design decisions for running PostgreSQL **horizontally distributed**
([Citus](https://www.citusdata.com/)). It is referred to as the source of truth before starting the
implementation.

> The motivation: to build a filesystem beyond the disk capacity ceiling of one PostgreSQL instance
> (realistically a few TB to a few tens of TB). With Citus assembled as a coordinator plus N workers, adding a
> worker scales the capacity and the write throughput in parallel.

## The overall approach

It is implemented in 3 stages, in order. **Each stage is completed before moving to the next.** Even if it is
abandoned midway, each stage is useful on its own.

| The phase | What it is | The effect on an existing environment | The status |
|---|---|---|---|
| **Phase 1** | Large objects -> bytea | Complete within a single PG. It is a precondition for the Citus migration but is valuable on its own | **Complete** (Linux 34/34 and Windows 24/24 ALL PASSED) |
| **Phase 2** | Making it Citus (the distribute / reference settings plus adding `--citus` to mkfs plus creating the pgfs_lock table in advance) | Only in a Citus environment. Single-PG operation keeps working | **Complete** (Linux 34/34 and Windows 24/24 ALL PASSED in both the single-PG and the Citus modes) |
| **Phase 3** | Implementing the `SELECT FOR UPDATE`-based exclusion on `pgfs_lock` and building it into the Api | It works on both a single PG and Citus. Combined with Notify it gives cross-client consistency | **Complete** (`LockData` / `LockInode` / `LockInodes` built into the Api, with Linux 34/34 and Windows 24/24 maintained) |

The details of each phase are in the sections below.

---

## Phase 1: Large objects -> bytea (complete)

**Completed.** The implementation is gathered in the `ReadData` / `WriteData` / `TruncateData` /
`ReleaseData` of [src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs) plus the helpers `ReadChunkSlice` /
`WriteChunkSlice` / `TruncateChunk` / `DropAllChunks`. The DDL is
[docs/ddl/pgfs_data_chunk.sql](../ddl/pgfs_data_chunk.sql), the initialization code is
`CreateDataChunkTableAsync` in [src/mkfs/src/Initializer.cs](../../src/mkfs/src/Initializer.cs), and the model is
[src/core/src/Models/Chunk.cs](../../src/core/src/Models/Chunk.cs).

Implementation notes:

- **`repeat(bytea, integer)` does not exist in PG.** There is only `repeat(text, integer)`, so the zero padding
  is assembled with `decode(repeat('00', N), 'hex')`.
- WriteData uses an upsert that completes in one SQL statement per chunk
  (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`). The CASE branches on
  the three cases of "a central overlay / overwriting the tail / zero padding plus concatenation". A concurrent
  WriteFile race (from CopyFileEx and the like) is serialized automatically by PG's row lock.
- Each chunk's payload length is **equal to "the number of bytes written so far"** = following the large object
  semantics as they are. A hole in the middle is expressed by the length(payload) not reaching the write offset
  (if a substring returns a short result, it is zero-filled).
- A 1 MB bytea exceeds PG's TOAST threshold (about 2 KB) and is TOASTed automatically.
  `substring(payload from N for M)` triggers PG 13+'s partial detoast, so only what was asked for goes over the
  network.

### Why bytea

PG's large objects are written straight into the system catalog `pg_largeobject`:

```
pgfs_data_chunk(data_id, chunk_index, lo_oid OID)  →  pg_largeobject(loid, pageno, data BYTEA(2KB))
```

`pg_largeobject` is a system catalog, so **Citus cannot distribute it**. However many workers are added, the
large object bodies stay concentrated on the one coordinator forever and the capacity wall cannot be crossed.

-> The bodies have to be replaced with **a user table holding bytea**. A bytea is a column of a user table, so
it can be a target of Citus's `create_distributed_table`.

### The new schema

```sql
CREATE TABLE pgfs_data_chunk (
    data_id     BIGINT  NOT NULL,
    chunk_index INTEGER NOT NULL,
    payload     BYTEA   NOT NULL,   -- the old lo_oid held directly as a bytea
    created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    created_by  TEXT      NOT NULL,
    updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    updated_by  TEXT      NOT NULL,
    PRIMARY KEY (data_id, chunk_index)
);
```

- One chunk = one row = one bytea (the default `file_system.default_chunk_size = 1MB`)
- A 1 MB bytea is certain to be TOASTed (PG's TOAST threshold is about 2 KB)
- The chunk_size can be adjusted with `file_system.default_chunk_size`. The same setting is reused after the
  move to bytea

### Rewriting the Api

| Old (large objects) | New (bytea) |
|---|---|
| `lo_create()` -> `INSERT pgfs_data_chunk(lo_oid)` | `INSERT pgfs_data_chunk(payload)` in one go |
| `lo_open(oid, INV_READ); lo_seek(off); lo_read(len); lo_close(fd)` | `SELECT substring(payload from @off+1 for @len) FROM pgfs_data_chunk WHERE ...` |
| `lo_open(oid, INV_WRITE); lo_seek(off); lo_write(buf); lo_close(fd)` | A full-chunk write: `UPDATE ... SET payload = @new WHERE ...` / a partial one: `SET payload = overlay(payload placing @new from @off+1 for @len)` |
| `lo_truncate64(fd, len); lo_close(fd)` | `UPDATE ... SET payload = substring(payload for @len) WHERE ...` |
| `lo_unlink(oid)` | `DELETE FROM pgfs_data_chunk WHERE ...` |

A partial read (`substring`) gets **PG 13+'s partial TOAST detoast**, so it is a server-side partial read
equivalent to a large object's `lo_seek` plus `lo_read` (= only what was asked for goes over the network).

### The performance difference (predicted before measuring)

| The operation | Large objects | bytea |
|---|---|---|
| A 4 KB sequential read | `lo_seek`+`lo_read`, 2 RTTs | `substring(...)`, 1 RTT |
| A 4 KB random read (not crossing a chunk) | The same | The same (a partial TOAST detoast) |
| A 1 MB full chunk write | `lo_open`+`lo_write`+`lo_close`, 3 RTTs | `UPDATE SET payload=...`, 1 RTT |
| A 4 KB partial chunk write | `lo_seek`+`lo_write`, 2 RTTs (a per-page COW) | `UPDATE SET payload=overlay(...)`, 1 RTT (rewriting the whole TOAST) |

For the usual uses (cp, sequential writes) bytea is slightly ahead, and for a partial-heavy workload large
objects are slightly ahead. Reads and writes from FUSE and Dokan are mostly in units of 4 KB to 128 KB, so with
a chunk_size of 1 MB most of them are full-chunk and the difference is small.

### The migration procedure

The development machine has no live data, so it is rebuilt with `mkfs --clean`. To migrate an environment that
does hold live data:

```sql
ALTER TABLE pgfs_data_chunk ADD COLUMN payload BYTEA;

DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT data_id, chunk_index, lo_oid FROM pgfs_data_chunk WHERE payload IS NULL LOOP
        UPDATE pgfs_data_chunk SET payload = lo_get(r.lo_oid)
          WHERE data_id = r.data_id AND chunk_index = r.chunk_index;
        PERFORM lo_unlink(r.lo_oid);
    END LOOP;
END $$;

ALTER TABLE pgfs_data_chunk DROP COLUMN lo_oid;
ALTER TABLE pgfs_data_chunk ALTER COLUMN payload SET NOT NULL;
```

-> An offline migration is enough. A hot migration while mounted is not supported for now.

### The rejected option: "keep the large objects and distribute and replicate them to the workers"

It was considered and not taken, for these reasons:

1. **A custom large-object table plus Citus distribution**: the idea of holding a bytea with a smaller page size
   in a separate table. That amounts to **"a bytea with a variable page size"**, which is equivalent to setting
   `pgfs_data_chunk`'s chunk_size to 2 KB. **There is no point.**
2. **Standing up a PG instance on each worker and doing the sharding and replication at the application
   layer**: it becomes manual sharding, and the replication, the failover and the consistency checking all have
   to be written by hand. That is effectively "implementing a distributed FS by hand", and it loses the
   advantages of pgfs depending on PostgreSQL (the operational know-how of an RDBMS, auditing through SQL,
   ACID).

-> **Simply moving to bytea is best.**

---

## Phase 2: Making it Citus (complete, including multi-node)

**Completed.** `mkfs --citus [--worker host[:port]],...]` supports both the single-node and the
multi-node configurations. The implementation is spread across:

- [Schema.Database.Citus](../../src/core/src/Config/Schema.cs) (a BoolField) plus
  [Schema.Database.Workers](../../src/core/src/Config/Schema.cs) (a StringListField) - the CLI and TOML intake
- [DatabaseConfig.Workers](../../src/core/src/Config/DatabaseConfig.cs) - normalized into a
  `List<(string Host, int Port)>`
- The facade [Initializer.InitializeAsync](../../src/mkfs/src/Initializer.cs) plus
  [Initializer.EnsureDatabaseAsync](../../src/mkfs/src/Initializer.cs) - Phase 1 (the worker bootstrap) plus
  Phase 2 (ensuring the coordinator database) plus Phase 3 (the Citus topology), all inside
- Each [CreateXxxTableAsync](../../src/mkfs/src/Initializer.cs) - it calls `create_distributed_table` /
  `citus_add_local_table_to_metadata` right afterwards, only when it created the table (a per-table
  responsibility)
- Making the Api Citus-compatible ([Api.Rename](../../src/core/src/Api/Api.cs) /
  [Api.EnsureDataRow](../../src/core/src/Api/Api.cs) / [Api.WriteChunkSlice](../../src/core/src/Api/Api.cs) /
  [ConfigStore.Save](../../src/core/src/Config/ConfigStore.cs))
- The DDL changes ([pgfs_inode.sql](../ddl/pgfs_inode.sql), making the PK composite, plus the new
  [pgfs_lock.sql](../ddl/pgfs_lock.sql))

The verification is in two kinds under [tests/citus/](../../tests/citus/README.md):
- [multinode_probe.sh](../../tests/citus/multinode_probe.sh) - a one-off probe for confirming the behaviour of
  Citus itself (auto-sync / DDL propagation / shard placement and so on)
- [test_matrix.sh](../../tests/citus/test_matrix.sh) - the **18-case** matrix of mkfs (3 initial x 6 target).
  **18/18 PASS** on Citus 14 (docker) on the Linux client

### The constraints at the core of the design (the judgements to remember)

1. **`--citus` and a custom `--tablespace` can coexist** (the forbidding guard was removed).
   The per-table `TABLESPACE` clause was dropped in favour of
   **inheriting the default tablespace through `CREATE DATABASE WITH TABLESPACE`**, so the shards inherit the
   worker database's default tablespace too. A tablespace is node-local, so
   [EnsureTablespaceAsync](../../src/mkfs/src/Initializer.cs) creates it on **the coordinator plus every
   worker** (Citus does not propagate a CREATE TABLESPACE). The LOCATION directory is created automatically by
   a plperlu auto-mkdir (owned by postgres at 0700) when `app.plperlu` permits it. Multi-node needs the
   directory on each worker (mkfs is SQL-only and cannot mkdir remotely, but the plperlu mkdir runs on each
   worker connection). The design is in [settings-and-plperlu.md](settings-and-plperlu.md).
2. **If the database already exists (no --clean, or a --clean whose drop failed), every Citus-related mutation
   is skipped**: `EnsureDatabaseAsync` checks at its head whether the coordinator database exists and, if it
   does, only takes stock ([LogExistingCitusStateAsync](../../src/mkfs/src/Initializer.cs)) and returns at once.
   `citus_add_node` / `citus_set_coordinator_host` / `shouldhaveshards` and the worker bootstrap are
   **never called** (corrected: what is skipped is **only the topology**, and
   `create_distributed_table` / `citus_add_local_table_to_metadata` do run for "the tables that were newly
   created". To make the existing tables Citus too, use `--distribute-existing`). That gives:
   - Re-running mkfs does not unintentionally break a Citus cluster that is already up
   - Running `mkfs --citus --worker w1` against an empty database by mistake -> redoing it requires `--clean`
   - The guarantee that "however idempotently mkfs is run, the cluster state does not change"
3. **Phases 1+2+3 run only when the database is newly created**: the contrapositive of (2). Only through
   `--clean` or on the first setup does all of Phase 1 (the worker database bootstrap) -> Phase 2 (the
   coordinator database CREATE) -> Phase 3 (the Citus topology) run.
4. **`create_distributed_table` / `citus_add_local_table_to_metadata` are the responsibility of the per-table
   methods**: `CreateXxxTableAsync` calls them right after it creates a table (`CreateTableAsync` returns a
   `Task<bool>` for "created / skipped"). When an existing table is skipped, the distribution is not touched
   either (consistent with the existing-database case).
5. **Phase 4 (propagating `citus_set_coordinator_host` to the workers) is unnecessary**: confirmed on Citus 14
   in the probe ([multinode_probe.sh](../../tests/citus/multinode_probe.sh), sections 6 and 10) - merely calling
   `citus_add_node('worker', port)` on the coordinator auto-syncs a coordinator row (groupid=0) into the
   worker's `pg_dist_node`. `citus_add_local_table_to_metadata` works without an explicit set_coordinator_host
   on the worker either.

### The phase structure of EnsureDatabaseAsync

```text
EnsureDatabaseAsync:
  [pre] check whether the coordinator's pgfs database exists
    it exists -> LogExistingCitusStateAsync (only with Citus) -> return
    it does not -> carry on below

  [Phase 1] ensure the pgfs database plus the Citus extension on each worker (only acts with --citus plus workers given)
    foreach worker:
      super to the worker's maintenance database:
        EnsureDatabaseOnAsync(the worker's pgfs database)
      super to the worker's pgfs database:
        CREATE EXTENSION IF NOT EXISTS citus
      // no CREATE SCHEMA (for the same reason Phase 4 is unnecessary: it is left to Citus's DDL propagation)

  [Phase 2] CREATE the coordinator's pgfs database

  [Phase 3] the Citus topology (on the coordinator only; propagated to the workers by auto-sync)
    super to the coordinator's pgfs database:
      CREATE EXTENSION IF NOT EXISTS citus
      if (the coordinator (groupid=0) is not in pg_dist_node):
        citus_set_coordinator_host(coordHost, coordPort)
      foreach worker:
        citus_add_node(workerHost, workerPort)   <- idempotent plus auto-syncs the workers
      if (workers.empty):
        citus_set_node_property(coord, 'shouldhaveshards', true)
```

EnsureSchemaAsync issues the CREATE SCHEMA on the coordinator only -> Citus's DDL propagation creates it on the
workers automatically. Likewise each `CreateXxxTableAsync`'s CREATE TABLE is issued on the coordinator only ->
the call to `create_distributed_table` creates the shards on the workers.

The stumbling blocks noticed during the implementation:

- **Citus requires the distribution key to be part of a unique constraint.** `pgfs_inode`'s PK was originally
  `(id)`, but distributing on `parent_id` has Citus reject it with
  `cannot create constraint on "pgfs_inode"`. The PK was changed to the composite `(parent_id, id)` and a
  separate INDEX on `id` alone was added for `WHERE id = @id` searches. The id is a BIGSERIAL and globally
  unique (the sequence is on the coordinator), so `(parent_id, id)` is effectively unique by the id alone.
- **A UNIQUE constraint on `id` alone is deliberately not created even in the single-PG mode (the user's
  judgement)**: in a Citus environment neither a UK, a PK nor an EXCLUSION can make a constraint on
  the id alone (as above). A CHECK cannot take a subquery so "uniqueness against the other rows" cannot be
  expressed, and faking it with a trigger would run a cross-shard lookup on every INSERT and destroy the
  performance. A branch of "create the UK on the id alone only in the single-PG mode" is technically possible,
  but (a) the schema would differ between single-PG and Citus, (b) an in-place migration from single-PG to Citus
  would get stuck on the UK, and (c) a defensive check on the application side at startup, such as running
  `SELECT id, COUNT(*) FROM pgfs_inode GROUP BY id HAVING COUNT > 1`, "only makes it slower with no perceptible
  benefit". So the choice was **no UK in either mode, with the uniqueness kept by the BIGSERIAL sequence plus tx
  discipline**. The grounds: the BIGSERIAL sequence is a single one on the coordinator and never hands out a
  duplicate, and the one place that bypasses the id is the `OVERRIDING SYSTEM VALUE` path of `Api.Rename`, which
  is designed so that the DELETE and INSERT complete in the same tx and no intermediate state has the same id
  twice. **Before re-proposing "let us guarantee the uniqueness of the id just in case", check this
  judgement** - it is an option that was already considered and dropped.
- **The root inode's ON CONFLICT was changed to target the `(parent_id, name)` UK.** `ON CONFLICT (id)` needs a
  UK on the id alone, which the Citus restriction makes impossible. The `(parent_id, name)` UK includes the
  distribution key so it is fine. The root is unique at (0, '/').
- **The coordinator always has to be registered in `pg_dist_node`** (a precondition of
  `citus_add_local_table_to_metadata`): `citus_add_local_table_to_metadata` requires the coordinator to be in
  pg_dist_node (a row with groupid=0 to exist). The check has to be
  **"is there a coordinator (`groupid = 0`) row", not "is `pg_dist_node` as a whole empty"** - the old
  implementation's "register only when `pg_dist_node` is empty" had the pitfall that a configuration where the
  user had added workers first with `citus_add_node` (= pg_dist_node is non-empty but the coordinator is not
  registered) killed the later local registration. mkfs --citus calls `citus_set_coordinator_host(host, port)`
  when **`SELECT count(*) FROM pg_dist_node WHERE groupid = 0`** is 0.
- **`shouldhaveshards = true` is set only when there are 0 workers**: in a single-node configuration (no worker
  in `pg_dist_node`), `create_distributed_table` dies with
  "replication_factor (1) exceeds number of worker nodes (0)", so the coordinator itself has to host the shards
  (`citus_set_node_property(host, port, 'shouldhaveshards', true)`). In a configuration with workers, putting
  the shards on the workers is the intended use, so the coordinator's default
  (`shouldhaveshards = false`) is respected and not touched. The check is
  **`SELECT count(*) FROM pg_dist_node WHERE groupid <> 0 AND noderole = 'primary'`** = 0. It is independent of
  the coordinator-registration check.
- **`FOR UPDATE` requires the distribution key**: `SELECT ... WHERE id = @id FOR UPDATE` dies on Citus with
  "could not run distributed query with FOR UPDATE/SHARE commands". The FOR UPDATEs of `Api.Rename` and
  `Api.EnsureDataRow` were changed to `WHERE parent_id = @parent_id AND id = @id`, with the parent_id passed
  from the caller's `Inode.ParentId` (in Rename, fetched beforehand with a broadcast SELECT if it is not
  cached).
- **The expressions in an `ON CONFLICT DO UPDATE` clause are limited to IMMUTABLE ones**: a
  `DO UPDATE SET col = current_timestamp` against a distributed or metadata-registered table dies with
  "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked IMMUTABLE". The
  `updated_at = current_timestamp` of `WriteChunkSlice` and `ConfigStore.Save` was replaced with a client-side
  `@now` and a reference to `EXCLUDED.updated_at` (a constant in the VALUES, so it is treated as IMMUTABLE). The
  same SQL works on a single PG too, so no branch on a Citus flag is needed.
- **A cross-shard rename is a DELETE plus an INSERT OVERRIDING SYSTEM VALUE**: the path in `Rename` that
  changes the parent_id is refused by Citus as an UPDATE (rewriting the distribution key). In the same tx the
  old row is row-locked (FOR UPDATE), DELETEd and INSERTed into the new shard, with `OVERRIDING SYSTEM VALUE`
  preserving the BIGSERIAL id. The unchanged-parent case keeps the existing UPDATE, which is light and within
  one shard.

### The distribution strategy per table

| The table | The distribution | The key | The reason |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | The `(parent_id, name)` UK closes within a shard (Citus does not guarantee a cross-shard UK) / a ListChildren of one directory completes in 1 shard / a path traversal causes cross-shard hops but the InodeCache absorbs them |
| `pgfs_data` | distributed | `id` | A simple lookup. Hardlink siblings share the same data_id, so it is colocated with `pgfs_data_chunk` |
| `pgfs_data_chunk` | distributed | `data_id` | Every chunk of one file is in the same shard -> a sequential read or write completes within 1 shard. `colocate_with => 'pgfs_data'` puts it in the same shard as `pgfs_data` |
| `pgfs_lock` | **local (= not distributed)** | - | Introduced in Phase 3 (originally distributed on `target_id`). **Changed to a Citus local table**: a distributed table refuses a row lock when `shard_replication_factor > 1`, so a locking mechanism independent of the rf needs it undistributed. Registering it in the metadata with `citus_add_local_table_to_metadata` routes a query entering through a worker to that same row too, so the exclusion holds whichever node is the entry point (see "the exclusion and the replication factor" below) |
| `pgfs_settings` | **local (= not distributed)** | - | There is no reading or writing from every worker, so distribution is unnecessary. Registering it in the metadata with `citus_add_local_table_to_metadata` makes it referenceable if a JOIN from a distributed table ever becomes desirable |

### The exclusion and the replication factor

**Row locks are concentrated in `{prefix}lock` alone.** No `FOR UPDATE` is fired at `{prefix}inode` or the other
distributed tables.

The reason: Citus's shard replication (`citus.shard_replication_factor > 1`) is **statement-based replication**,
so it refuses an operation whose result can differ between placements. A row lock is the representative case,
and `SELECT … FOR UPDATE` gives `0A000 could not run distributed query with FOR UPDATE/SHARE commands` even with
an equality filter on the distribution key. Switching the locking mechanism by rf is poor design, so
**only the table being locked was left undistributed** and made independent of the rf.

| The option | Row locks | Serializing across entry nodes | 100 locks (entering through the coordinator) | The placements |
|---|---|---|---|---|
| Distributed (rf=1) | ✅ | - (rf=1 has no replication) | 32 ms | 1 |
| Distributed (rf>=2) | ❌ Refused | - | - | rf |
| **Citus local** (taken) | ✅ | ✅ Measured at a 2.0-second wait | **11 ms** | 1 (the coordinator) |
| A reference table | ✅ | ✅ Measured at a 2.0-second wait | 61 ms (it touches every placement) | Every node |

* **Citus local was taken** because "Citus routes to one row that really exists". Unlike an advisory lock, which
  is node-local state, **entering through a worker grabs the same row** (Citus 11+ has every node hold the
  metadata and be able to be a query entry point = the equivalent of multi-coordinator). It was confirmed by
  measurement that "while one test node holds it, another waits".
* A reference table works too, but it touches every placement on every lock so it is 5.5 times slower, and the
  lock fails if even one placement is missing (the availability of the lock goes down for the sake of adding
  redundancy). A reference table is more resilient to a coordinator node failure, so it is a candidate to switch
  to if that is what matters.
* The trade-off: the locks **concentrate on one row on the coordinator**. The old document's design rationale of
  "distribute on `target_id` to spread them across the workers" was withdrawn. Measured at 0.11 ms per lock, it
  should not jam up to several thousand metadata operations a second.
* Along with it, **two `FOR UPDATE`s on `{prefix}inode` were removed** (fetching the old row in `Api.Rename` and
  serializing the data row creation in `Api.EnsureDataRow`). Both take `{prefix}lock` with `LockInodes` /
  `LockInode` just before, so the exclusion is equivalent.
* As a result, **the rf of `{prefix}inode` / `data` / `data_chunk` / `audit` can be chosen freely** (rf=1 is the
  equivalent of RAID0 and rf=N of an N-way mirror: a pure choice about storage redundancy).

### The shard-touch order of a write tx is chunk/data -> inode (the 40P01 cure)

To maintain the consistency of the statement-based replication, Citus at rf >= 2 **serializes changes to the
same shard per shard**. In other words "different rows do not collide" does not hold for a distributed table,
and **the order in which a tx touches the shards** is the lock order as it is. Every inode row under one
directory falls in the same shard (the distribution key = `parent_id`), so concurrent writes always converge on
the inode shard.

The write-through and flush txs used to grab the inode shard with the leading
`UPDATE inode SET data_id` (EnsureDataRow) and then proceed to write the chunk/data shard =
**a hold-and-wait of "holding the inode and waiting for the data"**. Combined with a tx waiting in the reverse
order, it formed the cycle of a distributed deadlock (40P01), causing a 15 to 20% per-round flake with 5
concurrent create+writes (identified by measurement with `citus_lock_waits`; the story is in
[tests.md, the cured flake](../tests.md)).

**The convention**: a write tx touches the shards in the order
**`{prefix}lock` (coordinator local) -> chunk/data (colocated) -> inode (one statement at the end)**.
The data_id link is not fired at the head of the tx but folded into the size/mtime UPDATE at the end
(the link arguments of `FinishWriteInodeInTx` / `UpdateInodeSizeAndMtimeInTx` / `LinkDataIdInTx`).
Do not break this order when adding a new write path.

**The safety net**: because a cycle can still remain inside Citus, such as at the COMMIT stage of a 2PC, the
self-contained txs of create / write / flush **re-run a 40P01 / 40001 up to 4 times with a linear backoff plus
jitter** (`Api.IsRetryableDeadlock`). The victim has rolled back entirely so re-running is safe.
The measured effect: the deadlocks went from 33 in 60 rounds to **2 in 120 rounds (about 99% fewer), with the
rest absorbed by the retry = 0 fails**.

### Specifying the shard count and the replication count

The session GUC at the time `create_distributed_table` runs is what settles it, so mkfs sends them together in
the same command:

| The option | What it means | The default |
|---|---|---|
| `--shard-count <n>` | `citus.shard_count` | `0` = follow the cluster default (nothing is set) |
| `--shard-replication-factor <n>` / `--rf <n>` | `citus.shard_replication_factor` = how many nodes one shard is placed on | `0` = follow the cluster default |

Making the `SET` a separate call could pull a different physical connection from the pool, so
**`SET …; SELECT create_distributed_table(…)` is issued as one command**. It does not rewrite the cluster-wide
(`postgresql.conf`) or the per-role or per-database settings, so **it does not affect the distributed tables of
other applications sharing the same database**.

### Making an existing table Citus afterwards (`--distribute-existing`)

By default mkfs makes **only the tables it newly creates** Citus. Running `--citus` against an existing schema
therefore does nothing. Distributing an existing table is a heavy operation that redistributes the data into the
shards, so it is done only when `--distribute-existing` is given explicitly.

* The check is the single question of whether there is a row in `pg_dist_partition` (distributed is
  `partmethod='h'`, and reference or local is `'n'`), and it is **idempotent**. From the second time on, the log
  says "already under Citus management - skipped".
* **Not touching the topology mutations (`citus_add_node` / `citus_set_coordinator_host` /
  `shouldhaveshards`) when the coordinator's database exists** is as before. It is the safety valve against
  breaking a shared cluster's configuration, and `--distribute-existing` only unlocks making the tables Citus.
* Measured (on the dev server's shared Citus 13.1 cluster): running
  `--citus --distribute-existing --rf 2 --shard-count 4` against a non-Citus FS holding a 3 MiB file made 4
  tables distributed and 3 local, and **the file's md5 was unchanged** (an in-place conversion that preserves
  the data).

### An UPDATE must always include the distribution key in its WHERE (making it a router query)

`{prefix}inode`'s distribution key is `parent_id`, so an UPDATE with only `WHERE id = @id` is
**broadcast by Citus to every shard**:

```
EXPLAIN UPDATE {prefix}inode SET … WHERE id = 5                     -> Task Count: 8   (the shard count)
EXPLAIN UPDATE {prefix}inode SET … WHERE parent_id = 0 AND id = 5   -> Task Count: 1   (a router query)
```

That was causing two real harms:

1. **One metadata update becomes "the shard count x the placement count" remote statements** (16 statements with
   8 shards at rf=2).
2. **The order in which the shard locks are taken becomes non-deterministic**, and under concurrency
   `40P01 canceling the transaction since it was involved in a distributed deadlock` happens. Measured: an
   `rsync -a` of 700 files (a chmod plus a utime per file) had `ChMod` and `UpdateTimestamps` fail once each
   (rsync exited 23).

As the countermeasure, all 9 paths that update an inode were made to include `parent_id` in the WHERE
(`UpdateMode` / `UpdateOwner` / `UpdateSize` / `UpdateTimestamps` / `SetXAttr` / `RemoveXAttr` /
`ClearInodeDataId` / `UpdateSizeInTx` / `TouchMtimeInTx`). The distribution key is resolved by
`Api.ResolveParentId` **through the InodeCache, or one database read if it is not there**, so the signatures on
the caller side (FUSE / Dokan) were not changed. On a single PG it just adds one condition (the PK is
`(parent_id, id)` so it is the same path).

**The multi-shard UPDATEs that remain** are the 3 places with `WHERE data_id = …` (recomputing the `st_nlink` in
`DeleteInode` and `CreateHardLink`). A hardlink's sibling inode can be in a different directory = a different
shard, so it is a broadcast in principle. The frequency is low so it is accepted for now, but there is room for
a deadlock in concurrent hardlink operations.

### The Citus support in mkfs

mkfs gained two new flags:

| The flag | What it means | The default |
|---|---|---|
| `--citus` | Enable making it Citus (call the extension plus the `create_distributed_table` family) | false |
| `-w` / `--worker` / `--workers` | A comma-separated list of worker specs `host[:port]` | (empty) |

Examples:

```bash
# single-node Citus (the coordinator only; the coordinator holds the shards too)
mkfs.pgfs --clean --citus -c "Host=coord;..." --super "..."

# multi-node Citus (a coordinator plus worker1 plus worker2)
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

mkfs --citus does the Citus setup **only when it creates the database**:

1. **The worker bootstrap** (Phase 1): on each `--worker`, over a super connection, `EnsureUser` ->
   (DropDatabase with `--clean`) -> `CREATE DATABASE pgfs` -> `CREATE EXTENSION IF NOT EXISTS citus`
2. **Ensuring the coordinator database** (Phase 2): `CREATE DATABASE pgfs` on the coordinator over a super
   connection
3. **The Citus topology** (Phase 3):
   ```sql
   CREATE EXTENSION IF NOT EXISTS citus;
   SELECT citus_set_coordinator_host(coordHost, coordPort);
   SELECT citus_add_node(workerHost, workerPort);   -- foreach worker
   SELECT citus_set_node_property(coordHost, coordPort, 'shouldhaveshards', true);  -- only when there are 0 workers
   ```
4. The later `EnsureSchemaAsync` / `CreateXxxTableAsync` have the CREATE SCHEMA and the shard tables deployed to
   the workers automatically through **Citus's DDL propagation**.
5. `create_distributed_table` / `citus_add_local_table_to_metadata` are called by each `CreateXxxTableAsync`
   right after it creates the table (decided by the `Task<bool>`):

```sql
SELECT create_distributed_table('pgfs.pgfs_inode',      'parent_id');
SELECT create_distributed_table('pgfs.pgfs_data',       'id');
SELECT create_distributed_table('pgfs.pgfs_data_chunk', 'data_id', colocate_with => 'pgfs.pgfs_data');
SELECT create_distributed_table('pgfs.pgfs_lock',       'target_id');
SELECT citus_add_local_table_to_metadata('pgfs.pgfs_settings');
```

The point of `citus_add_local_table_to_metadata`: `pgfs_settings` is placed on the coordinator only, but
registering it in Citus's metadata gives (a) usability if a JOIN from a distributed table ever becomes
desirable, (b) recognition by Citus's backup and restore tools, and (c) visibility in the inspection views such
as `citus_tables`. The cost is nearly zero.

**The behaviour when the database exists** (no `--clean`, or a `--clean` that did not drop it):

mkfs checks whether the coordinator database exists at the head of `EnsureDatabaseAsync` and,
**if it already exists, takes stock read-only on the Citus side and returns at once**. Neither the worker
bootstrap nor the Citus topology settings are touched. That is the design for guaranteeing "however idempotently
mkfs is run, the cluster state does not change". For the details see "the constraints at the core of the design"
above.

### The path-traversal cost of `pgfs_inode`

Resolving `/a/b/c` with `pgfs_inode` distributed on `parent_id`:

1. Get the root (id=0) -> 1 shard
2. Get the root's child `a` (`WHERE parent_id=0 AND name='a'`) -> to the shard holding parent_id=0 (= the shard
   holding the root)
3. Get `a`'s child `b` -> to the shard holding parent_id=a.id. **That is the shard from hashing a's id, so it is
   very likely a different shard**
4. Get `b`'s child `c` -> likewise to the shard decided by the hash

At worst, N cross-shard hops for a path of depth N. In practice:

- **With `InodeCache.byPath` hitting, the second and later times are 0 hops**
  ([src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs))
- Only the first path resolution is slow on a cold start (right after a process restart, say)
- Directory depth is practically 10 to 20 -> 10 to 20 shard crossings per request, about tens of ms (acceptable)

It can become a bottleneck in a workload where the InodeCache's hit rate degrades (a lot of random path access,
say). In that case:

- Increase the size of the `InodeCache`'s `byId` / `byPath` with `mount.cache_max_entries` (the default 1024 to
  tens of thousands)
- There is also room to consider maintaining a materialized view of path -> inode_id on the coordinator

### A cross-shard rename

When `Rename(id, newParentId, newName)` has a newParentId in a different shard from the current one, the row
moves across shards. Citus **does not support an UPDATE that changes the distribution key**, so
`UPDATE pgfs_inode SET parent_id = @new` may not go through directly (depending on the Citus version).

The workaround:

```sql
BEGIN;
INSERT INTO pgfs_inode (parent_id, name, ...) SELECT @newParent, @newName, ... FROM pgfs_inode WHERE id = @id;
DELETE FROM pgfs_inode WHERE id = @id;
COMMIT;
```

= realizing the move across shards with "a new INSERT plus a DELETE of the old row". But the inode_id changes,
so it has to be rewritten into SQL that preserves the inode_id (changing the PRIMARY KEY from a BIGSERIAL to an
explicitly specified `id`, and so on). **A main consideration at implementation time.**

As an alternative, making the inode_id itself the distribution key (= distributing `pgfs_inode` on `id`) makes a
rename a simple UPDATE, but it breaks the `(parent_id, name)` UK (there is no UK guarantee across shards). The
trade-off is to be reconsidered before adopting it.

---

## Phase 3: the `pgfs_lock` table plus `SELECT FOR UPDATE` (complete)

**Completed.** The implementation is gathered in the `LockTargets` / `LockData` / `LockInode` /
`LockInodes` helpers of [src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs) (in the middle of the file,
right below `QualifiedTable`). Each mutating Api method takes the appropriate lock at its head, and it is
released automatically at the end of the tx (COMMIT/ROLLBACK). Linux 34/34 and Windows 24/24 were maintained
(no regression, in the single-PG mode). A concurrent race with multi-node Citus plus 2 clients PASSes 4/4 in
[tests/citus/race_multinode.sh](../../tests/citus/race_multinode.sh) (Linux e2e 34/34, the write race md5s
agreeing, the mkdir race giving EEXIST, and the pgfs_lock accumulated size being reasonable).

The implementation judgements:

- **`LockTargets`'s SQL is 2 statements, an INSERT ON CONFLICT DO NOTHING plus a SELECT 1 ... FOR UPDATE**: each
  has a WHERE on the target_id alone, so it completes within a single shard (safe on Citus too). Folding them
  into one statement with a CTE was not tried (simplicity was preferred).
- **Taking several locks is done through `LockInodes(params long[])` with the target_id fixed in ascending
  order**: the input inode_ids are sign-flipped, deduplicated, sorted ascending and SELECT FOR UPDATEd in turn.
  That keeps a race where "Rename(A, parent_of_A, newParent)" and "Rename(B, ...)" take a shared inode in
  different orders from deadlocking.
- **`WriteData` / `TruncateData` take the lock after `EnsureDataRow`**: when `inode.DataId` is null,
  `EnsureDataRow` creates the data row and fills in `inode.data_id` (serializing the concurrent race inside with
  a FOR UPDATE on the inode row). After that, `LockData(dataId)` is taken to serialize the WriteData and
  TruncateData races. It ends up holding both an inode row lock and a data row lock in the same tx, but the
  order is fixed (the inode first, the data second) so it does not collide with another tx.
- **The `Update*` family (Mode/Owner/Size/Timestamps) was changed from a one-shot `Pg.Execute` to being
  tx-based**: it used to open a connection for just one SQL statement and close it, so it could not hold a lock.
  It was changed to `using var conn = NewConnection(); using var tx = conn.BeginTransaction()` plus `LockInode`
  plus the UPDATE.
- **`SetXAttr` / `RemoveXAttr` are not locked**: the judgement was not to include them in the lock table of the
  documents (the requirement and the risk of a lost update from concurrent metadata changes are low, and the
  JSON merge and strip are atomic in a single UPDATE). It can be added later if needed.
- **No data lock was added to `DeleteInode`'s data-drop path**: the documents' lock table covered inodes only,
  so that was followed. A delete-while-open race (a concurrent WriteData against data an open file handle
  holds) remains in theory, but the requirement presumes "I/O against a file deleted while open is undefined".

### The motivation

When several clients (a Linux mount.pgfs and a Windows assign.pgfs sharing the same pgfs database, say)
**write the same file at the same time**, a race happens:

- `WriteData`: two clients doing a read-modify-write of the same chunk gives a lost update
- `TruncateData`: a truncate during a write gives an inconsistency
- `Rename`: two clients renaming the same file to different paths has one of them rejected by the ON CONFLICT,
  but the in-memory InodeCache keeps the stale state

[Notify (LISTEN/NOTIFY)](../history.md) is **an after-the-fact notification of a change**, not a prevention of a
race. Exclusion during a write is needed separately.

### Why a custom table plus `SELECT FOR UPDATE`

A comparison of the 3 options considered:

| The aspect | `pg_advisory_xact_lock` | A custom TTL table plus a heartbeat | **`pgfs_lock` plus SELECT FOR UPDATE (taken)** |
|---|---|---|---|
| The cost of acquiring | μs (an in-memory hash) | ms plus a heartbeat | ms (fast once the lock row is cached) |
| Waiting | A server-side block | Client polling (a pseudo-block with LISTEN/NOTIFY is possible) | **A server-side block** |
| Automatic release | At the end of the session or the tx | Waiting for the TTL (up to a 30 s delay) | **At the end of the tx** |
| A TTL race | None | Yes (a heartbeat delay plus a TTL race gives a double acquisition) | **None** |
| A dedicated connection | (Not needed for the xact version) | Needed (for the heartbeat) | **Not needed** |
| Citus distribution | The coordinator only | Distributable (on the target_id key) | **Distributable** |
| Visibility | The `pg_locks` view | `SELECT FROM pgfs_lock` | `pg_locks` plus pg_stat_activity for who is waiting |
| Holding across txs | Possible with the session version | Possible | Not possible (released at the end of the tx) |

**The conclusion**: the `SELECT FOR UPDATE` option gets both the good of an in-memory advisory lock (sub-ms, a
server-side block, no TTL race, automatic release) and the good of the TTL table option (distributable by
Citus). pgfs's writes are "one operation = one tx" so holding across txs is unnecessary = `FOR UPDATE` is
enough.

Why `pg_advisory_xact_lock` was not chosen:

- It is coordinator-local: the lock acquisition cannot be spread to the workers on Citus -> the coordinator's
  session table is a bottleneck
- On a multi-coordinator HA Citus it is not visible between the coordinators

Why a custom TTL table was not chosen:

- Fully closing the TTL race (the well-known problem of Redis's SETNX with a TTL) needs fencing tokens and the
  like, which raises the complexity
- A dedicated connection plus a background task are needed for the heartbeat
- The client polling (or the pseudo-block through LISTEN/NOTIFY) at acquisition time worsens the latency

### Why a single BIGINT (`target_id`)

Citus's `create_distributed_table` is **hash distribution, and the hash input is the value of a single column
only**. A composite key (splitting kind = 'inode' and 'data' with `(kind TEXT, id BIGINT)`, say) cannot be the
distribution key directly. Since `pgfs_lock` was to be distributed (the Phase 3 motivation: avoiding the
coordinator-local bottleneck), the distribution key had to be one column, which necessarily makes it the single
column `target_id BIGINT`.

The consequence is having to face the problem that **an inode's id and a data's id (both BIGSERIALs) can collide
in the same numeric space**. Handling inode_id = 5 and data_id = 5 in the same single `pgfs_lock(target_id)`
means a tx that locked one unintentionally blocks the other's lock acquisition.

### The collision avoidance taken: separating the namespaces by sign

`a data lock = target_id = data_id (positive)` and `an inode lock = target_id = -inode_id (negative)` separate
the namespaces by the sign of the value (see the `LockData` / `LockInode` helpers of
[Api.cs](../../src/core/src/Api/Api.cs); there is an implementation sketch below). Unlike the 2-key form of
`pg_advisory_xact_lock(ns, key)`, only a single-column PK is possible, so the namespace **has to be expressed on
the value side**.

An alternative is **carving `pgfs_inode_lock` out as a separate table**. That would have the advantages of:
- Distributing the inode locks on `parent_id` (a different colocation group from the data) and cutting the
  cross-shard hops of the inode operations
- Making it easier to separate the operational statistics of the inode lock table and the data lock table

...but at Phase 2 it was judged that "the operational patterns are not settled enough to justify one more
table", so it starts with the single `pgfs_lock` plus the sign namespace. It is left as room to reorganize into
`pgfs_inode_lock` if the inode lock workload turns out to dominate.

### The schema

```sql
CREATE TABLE pgfs_lock (
    target_id BIGINT PRIMARY KEY
);
```

The substance is a single-column PK. Acquiring a lock is just "take a row lock", so no extra column is needed.

In a Citus environment:
```sql
SELECT create_distributed_table('pgfs_lock', 'target_id');
```

### How it is used

```sql
BEGIN;
-- secure the target_id row (create it if it is not there)
INSERT INTO pgfs_lock(target_id) VALUES (@data_id) ON CONFLICT DO NOTHING;
-- take the row lock. Another tx doing a SELECT FOR UPDATE on the same row blocks
SELECT 1 FROM pgfs_lock WHERE target_id = @data_id FOR UPDATE;
-- the real write here
UPDATE pgfs_data_chunk SET payload = ... WHERE data_id = @data_id AND chunk_index = @ci;
COMMIT;   -- the row lock is released automatically at the end of the tx
```

A thin helper goes in [Api.cs](../../src/core/src/Api/Api.cs):

```csharp
private void LockData(NpgsqlConnection conn, NpgsqlTransaction tx, long dataId) {
    conn.Execute(
        $"INSERT INTO {this.QualifiedTable("lock")}(target_id) VALUES (@id) ON CONFLICT DO NOTHING; " +
        $"SELECT 1 FROM {this.QualifiedTable("lock")} WHERE target_id = @id FOR UPDATE",
        new { id = dataId }, tx);
}

private void LockInode(NpgsqlConnection conn, NpgsqlTransaction tx, long inodeId) {
    // to separate the inode and data namespaces, an inode uses the negative range (-inode_id).
    // Or a separate table (pgfs_inode_lock) would do. Unlike the 2-key form of
    // `SELECT pg_advisory_xact_lock(ns, key)`, this is a single-column PK, so the namespace has to be
    // separated by the value.
    conn.Execute(
        $"INSERT INTO {this.QualifiedTable("lock")}(target_id) VALUES (@id) ON CONFLICT DO NOTHING; " +
        $"SELECT 1 FROM {this.QualifiedTable("lock")} WHERE target_id = @id FOR UPDATE",
        new { id = -inodeId }, tx);
}
```

(For the grounds of the namespace separation and the comparison of the alternatives, see "why a single BIGINT
(`target_id`)" above.)

### The paths that need a lock

| The Api method | What is locked | The reason |
|---|---|---|
| `WriteData(inode, off, src)` | data: `inode.DataId` | Serializing concurrent writes to the same file |
| `TruncateData(inode, len)` | data: `inode.DataId` | The same |
| `ReleaseData(dataId)` | data: `dataId` | Preventing another client's write during the release |
| `UpdateMode/Owner/Size/Timestamps(id)` | inode: `id` | Preventing a lost update from concurrent metadata changes |
| `Rename(id, newParent, newName)` | inode: `id`, `oldParent`, `newParent` | Three locks: the old parent, the new parent and the target inode. Deadlocks are avoided by **fixing the ascending order** |
| `DeleteInode(inode)` | inode: `inode.Id`, `inode.ParentId` | Serializing a simultaneous change of the parent's children list and of the body |
| `CreateHardLink(source, newParent, newName)` | inode: `source.Id`, `newParent` | Serializing the nlink update of the hardlink siblings and the creation of the new inode |

On the paths that take several locks (`Rename` / `DeleteInode` / `CreateHardLink`), deadlocks are avoided by
**fixing the ascending target_id order**.

### The accumulation of the lock rows

The policy is that rows only grow through `INSERT ON CONFLICT DO NOTHING` and are never `DELETE`d.

- One row is about 50 bytes (including the PRIMARY KEY index)
- A million files x 2 (data plus inode) = about 100 MB -> negligible
- Putting a delete in creates room for a race of "the lock row is DELETEd while another tx comes to
  SELECT FOR UPDATE it"
- If needed, a separate cron `DELETE` (realistically unnecessary)

### The caveats

- **The asymmetry of the colocation** (confirmed in the Phase 2 verification, 2026-05-26): looking at
  `citus_tables` after `mkfs --citus`, all 4 distributed tables `pgfs_inode` / `pgfs_data` /
  `pgfs_data_chunk` / `pgfs_lock` land in **the same colocation_id (the default group)** (they have a BIGINT
  distribution key and the same shard_count, so Citus colocates them automatically). Where that helps and where
  it does not:
  - **When a data lock is taken** (target_id = data_id): `pgfs_lock`'s distribution key value and `pgfs_data`'s
    (id) are the same number -> the same hash -> **they land in the same shard, with no network crossing**
  - **When an inode lock is taken** (target_id = -inode_id): `pgfs_lock`'s hash(-inode_id) and `pgfs_inode`'s
    hash(parent_id) are different things -> **the lock shard and the inode shard diverge** (one network
    crossing)

  It is accepted for now, but once a workload dominated by inode locks appears, the asymmetry can be resolved by
  making `pgfs_inode_lock` a separate table distributed on `parent_id` and colocated with `pgfs_inode`.
- **Locking across several shards on Citus**: with `WHERE target_id IN (a, b)` where a and b are in different
  shards, the acquisition order becomes non-deterministic and the deadlock risk goes up.
  **Keep to 1 SQL statement = 1 lock unit** (the helpers above take them one at a time).
- **Forgetting to take one**: writing a `WriteData` and so on without calling `LockData` / `LockInode` gives a
  race. The discipline of checking every mutation path in code review is needed. The convention of calling
  `LockData` at the head of `Api.WriteData(...)` is also stated in the class doc of
  [Api.cs](../../src/core/src/Api/Api.cs).
- **A deadlock on the application side**: if the same thread takes several nested, as in
  `LockData(A)` -> `LockData(B)`, and another thread takes them in reverse order, it hangs.
  **The acquisition order of the locks is fixed as ascending target_id** as a project convention.

---

## The verification items at migration time

To be confirmed with the e2e suite per phase.

### The Phase 1 (bytea) verification

- [x] Linux 34/34 and Windows 24/24 maintained (achieved)
- [x] `test_large_file_round_trip` gives a byte-for-byte round trip of a 2 MiB file (included in both the Linux
      and the Windows e2e suites)
- [x] `test_truncate_*` gives matching payload lengths after a truncate (included in both e2e suites)
- [ ] The response time of a partial read (a random offset) has not regressed from the large object version
      (roughly confirmed with `time dd if=... bs=4K count=1 skip=1000` and the like, without BenchmarkDotNet) -
      **not done (the performance evaluation is a separate task)**
- [ ] The database size (`pg_database_size`) roughly matches the large object version (the TOAST compression
      efficiency being similar too) - **not done**

### The Phase 2 (Citus) verification

- [x] Set up Citus with 1 node (no workers, the coordinator only) and initialize with `mkfs --clean --citus`
      (achieved, Citus 13.1.1 on the PG server / Citus 14.0.0 on docker)
- [x] Set up Citus with multiple nodes (a coordinator plus 1 worker) and initialize with
      `mkfs --clean --citus --worker w1` (achieved, Citus 14.0.0 on docker)
- [x] The Linux and Windows e2e suites pass as they are (= making the tables distributed does not affect the
      features) - Linux 34/34 and Windows 24/24 ALL PASSED (single-node Citus on the PG server)
- [x] The mkfs matrix test: the 3 initial x 6 target = 18 combinations do "new / keep existing / rebuild with
      --clean" as intended - 18/18 PASS in
      [tests/citus/test_matrix.sh](../../tests/citus/test_matrix.sh)
- [x] Confirm with `EXPLAIN ANALYZE` that the path-resolution query reaches "the expected shard" - confirmed in
      [tests/citus/verify.sql](../../tests/citus/verify.sql) (`WHERE parent_id = N AND id = N` is Task Count 1,
      while `WHERE id = N` alone scans 32 shards)
- [x] A cross-shard rename works (the new INSERT plus old DELETE path) - `test_rename_into_subdir` (in both e2e
      suites) covers a parent-changing rename and passed in the Citus mode
- [x] Confirm `pgfs_settings` is only on the coordinator and is registered in the `citus_tables` view -
      confirmed in verify.sql (citus_table_type='local')
- [x] The DDL propagation: a CREATE/DROP SCHEMA on the coordinator propagates to the workers automatically -
      confirmed in [multinode_probe.sh](../../tests/citus/multinode_probe.sh) sections 8 and 13
- [x] The auto-sync of `citus_add_node`: a coordinator row (groupid=0) is added automatically to the worker's
      `pg_dist_node` - confirmed in multinode_probe.sh section 6 (demonstrating Phase 4 is unnecessary)
- [x] `citus_add_local_table_to_metadata` goes through without an explicit `citus_set_coordinator_host` on the
      worker - confirmed in multinode_probe.sh section 10
- [ ] Stop a worker and run on one lung -> access to that shard errors out (consistency OK) - **not done**
      (planned along with the Phase 3 exclusion verification)
- [ ] Recover the worker -> access returns - **not done** (the same)
- [ ] The Linux and Windows e2e suites passing on multi-node Citus - **not done** (the e2e suite has passed on
      real hardware only on single-node Citus; multi-node Citus stopped at the mkfs test_matrix)

### The Phase 3 (lock) verification

- [x] The whole e2e suite is maintained with a single client (the performance degradation of putting the lock
      acquisition in the write path is within tolerance) - Linux 34/34 and Windows 24/24 ALL PASSED
      (the single-PG mode)
- [x] The Linux e2e suite passes on multi-node Citus (a coordinator plus worker1 on docker) - **34/34 PASS**
      ([tests/citus/race_multinode.sh](../../tests/citus/race_multinode.sh) Test 1)
- [x] A concurrent write race against the same file from 2 clients - `dd /dev/urandom -> mount1` plus
      `dd /dev/zero -> mount2` at 8 MiB x 4 rounds, with the md5 and the size seen from both clients always
      agreeing (race_multinode.sh Test 2)
- [x] A concurrent mkdir race into the same directory from 2 clients - 20 rounds x (serial plus parallel) always
      have one side give EEXIST (the `(parent_id, name)` UK guarantees the cross-shard consistency)
      (race_multinode.sh Test 3)
- [x] The `pgfs_lock` rows accumulate without being `DELETE`d, but the size stays within a realistic range -
      after the 4 tests above it was **rows=178 / size=768 kB** = consistent with the design figures (about 50
      bytes per row, under 100 MB at a million rows) (race_multinode.sh Test 4)

---

## The unresolved matters and future considerations

- **The measured cost of a path traversal causing cross-shard hops** when `pgfs_inode`'s distribution key is
  `parent_id`. How much slower it gets in a workload where the InodeCache's hit rate is low.
- **Preserving the inode_id on a cross-shard rename**: the INSERT plus DELETE approach changes the BIGSERIAL id.
  Designing SQL that keeps the same id with `OVERRIDING SYSTEM VALUE` and the like.
- Whether **multi-coordinator HA Citus** (Enterprise) is in scope. If it is, the combination of an advisory lock
  plus coordinator-local needs separate consideration.
- **Whether a partial read of a bytea (`substring`) really gets a partial TOAST detoast on PG 13+**, measured.
  If it does, a larger chunk_size would be fine.
- **The option of distributing `pgfs_inode` on `id` and guaranteeing the `(parent_id, name)` UK in a separate
  layer** (retrying a `unique_violation` on the application side, say) is also worth reconsidering. If the
  rename cost of distributing on `parent_id` turns out unacceptable, swing to this.
- **The dependence on the Citus version**: record the Citus version used in the verification (the signature of
  `create_distributed_table` and the like can change).
