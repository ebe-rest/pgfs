# Citus support design

The design behind running PostgreSQL **horizontally distributed** with [Citus](https://www.citusdata.com/). This is the source of truth for the Citus-related decisions.

A Japanese translation is available in [support_for_citus.ja.md](support_for_citus.ja.md).

> Motivation: build a filesystem larger than a single PostgreSQL instance's disk capacity (realistically a few TB to tens of TB). With a Citus coordinator + N workers, adding workers scales capacity and write throughput in parallel.

Citus support spans three areas, each useful on its own:

| Area | Content | Effect on a non-Citus setup |
|---|---|---|
| **Storage** | Data body stored as `bytea` (not Large Objects) | Self-contained even on single PG; a prerequisite for Citus but valuable alone |
| **Distribution** | Distributed / local table setup + the mkfs `--citus` flag + the `pgfs_lock` table | Citus-only; single-PG operation keeps working |
| **Locking** | `SELECT FOR UPDATE`-based mutual exclusion on `pgfs_lock`, wired into the Api | Works on both single PG and Citus; combined with Notify for cross-client consistency |

---

## Storage: `bytea` instead of Large Objects

The data body lives in [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs)'s `ReadData` / `WriteData` / `TruncateData` / `ReleaseData` (plus helpers `ReadChunkSlice` / `WriteChunkSlice` / `TruncateChunk` / `DropAllChunks`). The DDL is [docs/ddl/pgfs_data_chunk.sql](ddl/pgfs_data_chunk.sql), the init code is `CreateDataChunkTableAsync` in [src/mkfs/src/Initializer.cs](../src/mkfs/src/Initializer.cs), and the model is [src/lib/src/Models/Chunk.cs](../src/lib/src/Models/Chunk.cs).

### Why bytea

A PostgreSQL Large Object is written straight into the `pg_largeobject` system catalog:

```
pgfs_data_chunk(data_id, chunk_index, lo_oid OID)  ->  pg_largeobject(loid, pageno, data BYTEA(2KB))
```

`pg_largeobject` is a system catalog, so **Citus cannot distribute it**. Adding workers would still pin the LO body to the single coordinator forever, never crossing the capacity wall.

So the data body is held in a **user table as `bytea`**. Being a user-table column, it can be the target of `create_distributed_table`.

### Schema

```sql
CREATE TABLE pgfs_data_chunk (
    data_id     BIGINT  NOT NULL,
    chunk_index INTEGER NOT NULL,
    payload     BYTEA   NOT NULL,
    created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    created_by  TEXT      NOT NULL,
    updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    updated_by  TEXT      NOT NULL,
    PRIMARY KEY (data_id, chunk_index)
);
```

- 1 chunk = 1 row = 1 bytea (default `file_system.default_chunk_size = 1MB`).
- A 1MB bytea is always TOASTed (the PG TOAST threshold is ~2KB).
- chunk_size is tunable via `file_system.default_chunk_size`.

### Implementation notes

- **`repeat(bytea, integer)` does not exist in PG** — only `repeat(text, integer)` — so zero padding is built with `decode(repeat('00', N), 'hex')`.
- WriteData uses a one-SQL-per-chunk upsert (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`). The CASE branches over the three cases (middle overlay / tail overwrite / zero-pad + concat). Concurrent WriteFile races (CopyFileEx etc.) are serialized automatically by PG row locks.
- Each chunk's payload length equals "the number of bytes written so far". A hole in the middle is represented by `length(payload)` not yet reaching the write offset (a short `substring` result is zero-filled).
- `substring(payload from N for M)` triggers PG 13+ partial TOAST detoast, so network transfer is only the requested span — server-side partial read equivalent to LO's `lo_seek + lo_read`.

### Api mapping (vs. the LO approach)

| LO | bytea |
|---|---|
| `lo_create()` -> `INSERT pgfs_data_chunk(lo_oid)` | `INSERT pgfs_data_chunk(payload)` |
| `lo_open(oid, INV_READ); lo_seek(off); lo_read(len); lo_close(fd)` | `SELECT substring(payload from @off+1 for @len) FROM pgfs_data_chunk WHERE ...` |
| `lo_open(oid, INV_WRITE); lo_seek(off); lo_write(buf); lo_close(fd)` | full-chunk: `UPDATE ... SET payload = @new WHERE ...` / partial: `SET payload = overlay(payload placing @new from @off+1 for @len)` |
| `lo_truncate64(fd, len); lo_close(fd)` | `UPDATE ... SET payload = substring(payload for @len) WHERE ...` |
| `lo_unlink(oid)` | `DELETE FROM pgfs_data_chunk WHERE ...` |

### Performance characteristics

| Operation | LO | bytea |
|---|---|---|
| 4KB sequential read | `lo_seek+lo_read` 2 RTT | `substring(...)` 1 RTT |
| 4KB random read (no chunk crossing) | as above | as above (partial TOAST detoast) |
| 1MB full chunk write | `lo_open+lo_write+lo_close` 3 RTT | `UPDATE SET payload=...` 1 RTT |
| 4KB partial chunk write | `lo_seek+lo_write` 2 RTT (page-level COW) | `UPDATE SET payload=overlay(...)` 1 RTT (whole-TOAST rewrite) |

For typical use (cp / sequential writes) bytea is slightly favorable; for partial-heavy workloads LO is slightly favorable. FUSE / Dokan reads/writes are mostly 4KB–128KB, so with chunk_size 1MB most are full-chunk and the difference is small.

### Migrating existing data

A fresh environment is rebuilt with `mkfs --clean`. To migrate an environment that already holds real data:

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

Offline migration is sufficient; hot migration while mounted is not supported.

### Rejected alternatives

1. **A custom LO table distributed across workers**: holding small-page bytea in a separate table. That is just "variable-page-size bytea", equivalent to setting `pgfs_data_chunk`'s chunk_size to 2KB. No point.
2. **A PG instance per worker with application-layer sharding + replication**: manual sharding, with replication / failover / consistency checks all hand-written. Effectively "implement a distributed FS yourself", losing the benefits of depending on PostgreSQL (RDBMS operational know-how, SQL auditing, ACID).

Plain bytea is the best choice.

---

## Table distribution

`mkfs --citus [--worker host[:port],...]` supports both single-node and multi-node setups. The implementation is spread across:

- [Schema.Database.Citus](../src/lib/src/Config/Schema.cs) (BoolField) + [Schema.Database.Workers](../src/lib/src/Config/Schema.cs) (StringListField) — the CLI / TOML entry points.
- [DatabaseConfig.Workers](../src/lib/src/Config/DatabaseConfig.cs) — normalized into `List<(string Host, int Port)>`.
- [Initializer.InitializeAsync](../src/mkfs/src/Initializer.cs) facade + [Initializer.EnsureDatabaseAsync](../src/mkfs/src/Initializer.cs) — bundles worker bootstrap + coordinator DB ensure + Citus topology.
- Each [CreateXxxTableAsync](../src/mkfs/src/Initializer.cs) — calls `create_distributed_table` / `citus_add_local_table_to_metadata` only when it freshly created the table (per-table responsibility).
- Citus-compatibility on the Api side ([Api.Rename](../src/lib/src/Api/Api.cs) / [Api.EnsureDataRow](../src/lib/src/Api/Api.cs) / [Api.WriteChunkSlice](../src/lib/src/Api/Api.cs) / [ConfigStore.Save](../src/lib/src/Config/ConfigStore.cs)).
- DDL changes ([pgfs_inode.sql](ddl/pgfs_inode.sql) composite PK + [pgfs_lock.sql](ddl/pgfs_lock.sql)).

Verification lives in [tests/citus/](../tests/citus/README.md): [multinode_probe.sh](../tests/citus/multinode_probe.sh) (a one-off probe of Citus behavior — auto-sync / DDL propagation / shard placement) and [test_matrix.sh](../tests/citus/test_matrix.sh) (the 18-case mkfs matrix: 3 initial x 6 target).

### Per-table distribution strategy

| Table | Distribution | Key | Reason |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | the `(parent_id, name)` UK closes within a shard (Citus does not guarantee cross-shard UKs) / ListChildren of one directory completes in 1 shard / path traversal incurs cross-shard hops but InodeCache absorbs them |
| `pgfs_data` | distributed | `id` | simple lookup. Hard-link siblings share the same data_id, so it co-locates with `pgfs_data_chunk` |
| `pgfs_data_chunk` | distributed | `data_id` | all chunks of one file land on the same shard -> sequential read/write completes in 1 shard. `colocate_with => 'pgfs_data'` puts it on the same shard as `pgfs_data` |
| `pgfs_lock` | distributed | `target_id` | spreads `SELECT FOR UPDATE` across workers instead of concentrating it on the coordinator |
| `pgfs_settings` | **local (not distributed)** | — | no read/write from all workers, so no need to distribute. `citus_add_local_table_to_metadata` registers it in metadata so a future distributed-table JOIN can reference it |

### mkfs Citus flags

| Flag | Meaning | Default |
|---|---|---|
| `--citus` | enable Citus (call the extension + create_distributed_table family) | false |
| `-w` / `--worker` / `--workers` | comma-separated worker spec `host[:port]` | (empty) |

```bash
# Single-node Citus (coordinator only; the coordinator holds shards)
mkfs.pgfs --clean --citus -c "Host=coord;..." --super "..."

# Multi-node Citus (coordinator + worker1 + worker2)
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

`mkfs --citus` performs the Citus setup **only when creating the database**:

1. **worker bootstrap**: on each `--worker`, via a super connection, `EnsureUser` -> (if `--clean`, DropDatabase) -> `CREATE DATABASE pgfs` -> `CREATE EXTENSION IF NOT EXISTS citus`.
2. **coordinator DB ensure**: on the coordinator, via a super connection, `CREATE DATABASE pgfs`.
3. **Citus topology**:
   ```sql
   CREATE EXTENSION IF NOT EXISTS citus;
   SELECT citus_set_coordinator_host(coordHost, coordPort);
   SELECT citus_add_node(workerHost, workerPort);   -- foreach worker
   SELECT citus_set_node_property(coordHost, coordPort, 'shouldhaveshards', true);  -- only when there are 0 workers
   ```
4. The subsequent `EnsureSchemaAsync` / `CreateXxxTableAsync` then auto-propagate CREATE SCHEMA / shard tables to the workers via **Citus DDL propagation**.
5. `create_distributed_table` / `citus_add_local_table_to_metadata` is called right after each `CreateXxxTableAsync` freshly creates a table (decided via its `Task<bool>` return):

```sql
SELECT create_distributed_table('pgfs.pgfs_inode',      'parent_id');
SELECT create_distributed_table('pgfs.pgfs_data',       'id');
SELECT create_distributed_table('pgfs.pgfs_data_chunk', 'data_id', colocate_with => 'pgfs.pgfs_data');
SELECT create_distributed_table('pgfs.pgfs_lock',       'target_id');
SELECT citus_add_local_table_to_metadata('pgfs.pgfs_settings');
```

The point of `citus_add_local_table_to_metadata`: `pgfs_settings` stays coordinator-only, but registering it in Citus metadata means (a) it can be JOINed from distributed tables later, (b) Citus backup/restore tools recognize it, (c) it shows up in inspection views like `citus_tables`. The cost is near-zero.

### EnsureDatabaseAsync structure

```text
EnsureDatabaseAsync:
  [pre] check whether the coordinator pgfs DB exists
    exists -> LogExistingCitusStateAsync (Citus only) -> return
    absent -> continue below

  [Step 1] ensure pgfs DB + the Citus extension on each worker (acts only with --citus + workers specified)
    foreach worker:
      super to worker maintenance:  EnsureDatabaseOnAsync(worker pgfs DB)
      super to worker pgfs DB:      CREATE EXTENSION IF NOT EXISTS citus
      // No CREATE SCHEMA (left to Citus DDL propagation, same reason coordinator-host propagation is unneeded)

  [Step 2] CREATE the coordinator pgfs DB

  [Step 3] Citus topology (coordinator only; auto-synced to workers)
    super to coord pgfs DB:
      CREATE EXTENSION IF NOT EXISTS citus
      if (coord (groupid=0) not in pg_dist_node):
        citus_set_coordinator_host(coordHost, coordPort)
      foreach worker:
        citus_add_node(workerHost, workerPort)   <- idempotent + auto-syncs workers
      if (workers.empty):
        citus_set_node_property(coord, 'shouldhaveshards', true)
```

EnsureSchemaAsync issues CREATE SCHEMA on the coordinator only -> Citus DDL propagation creates it on the workers too. Likewise each `CreateXxxTableAsync`'s CREATE TABLE runs on the coordinator only -> the `create_distributed_table` call creates shards on the workers.

### Core constraints (decisions worth remembering)

1. **`--citus` × a custom `--tablespace` can be combined**. By dropping the per-table `TABLESPACE` clause and instead **inheriting the default tablespace via `CREATE DATABASE WITH TABLESPACE`**, shards also inherit the worker DB's default tablespace. A tablespace is node-local, so [EnsureTablespaceAsync](../src/mkfs/src/Initializer.cs) creates it on **the coordinator + every worker** (Citus does not propagate CREATE TABLESPACE). The LOCATION dir is auto-created by plperlu auto-mkdir (owned by postgres, 0700) when `app.plperlu` is allowed. Multi-node needs the dir on each worker (mkfs is SQL-only and cannot remote-mkdir, but the plperlu mkdir runs on each worker connection). See [settings-and-plperlu.md](settings-and-plperlu.md) for the design.
2. **If the DB already exists (no --clean, or --clean failed to drop, etc.), all Citus mutations are skipped**: `EnsureDatabaseAsync` checks for the coordinator DB at the start; if it exists it only reports the state ([LogExistingCitusStateAsync](../src/mkfs/src/Initializer.cs)) and returns immediately. `citus_add_node` / `shouldhaveshards` / `create_distributed_table` / `citus_add_local_table_to_metadata` are **never called**. This guarantees that re-running mkfs never breaks a running cluster, that recovering a mistaken `mkfs --citus --worker w1` against an empty DB requires `--clean`, and that "running mkfs idempotently leaves the cluster state unchanged".
3. **The full setup runs only when the DB is freshly created** — the contrapositive of (2).
4. **`create_distributed_table` / `citus_add_local_table_to_metadata` are the per-table method's responsibility**: `CreateXxxTableAsync` calls them after a fresh table creation (`CreateTableAsync` returns `Task<bool>` for "created / skipped"). It leaves distribution untouched when skipping an existing table.
5. **Propagating `citus_set_coordinator_host` to workers is unnecessary**: confirmed by probe ([multinode_probe.sh](../tests/citus/multinode_probe.sh)) — calling `citus_add_node('worker', port)` on the coordinator auto-syncs the coordinator (groupid=0) row into the workers' `pg_dist_node`. `citus_add_local_table_to_metadata` also works without an explicit set_coordinator_host on the workers.

### Gotchas confirmed during implementation

- **Citus requires a unique constraint to include the distribution key**. `pgfs_inode`'s PK was originally `(id)`, but distributing by `parent_id` makes Citus reject it with `cannot create constraint on "pgfs_inode"`. The PK is the composite `(parent_id, id)`, with a standalone id INDEX added for `WHERE id = @id` lookups. id is BIGSERIAL and globally unique (sequence on the coordinator), so `(parent_id, id)` is effectively unique on id alone.
- **No standalone `id` UNIQUE constraint, even in single-PG mode**: under Citus no UK / PK / EXCLUSION can express an id-only constraint (above); CHECK cannot use a subquery, so "uniqueness vs. other rows" is inexpressible, and a trigger pseudo-implementation would run a cross-shard lookup on every INSERT and collapse performance. A "single-PG-only id UK" branch is technically possible but would (a) make the schema diverge between single-PG and Citus, (b) snag an in-place single-PG -> Citus migration, (c) a defensive startup `GROUP BY id HAVING COUNT > 1` would only be slow with no perceptible benefit. So **both modes have no UK and rely on the BIGSERIAL sequence + transaction discipline** for uniqueness. The basis: the BIGSERIAL sequence is single (coordinator) and never double-issues; the only id-bypass path is `Api.Rename`'s `OVERRIDING SYSTEM VALUE`, where DELETE+INSERT completes within one transaction so no duplicate id exists in an intermediate state. **Confirm this decision before re-proposing "let's guarantee id uniqueness just in case"** — it is a deliberately discarded option.
- **The root inode's ON CONFLICT targets the `(parent_id, name)` UK**. `ON CONFLICT (id)` needs a standalone (id) UK which Citus cannot create; the `(parent_id, name)` UK includes the distribution key, so it works. The root is unique at (0, '/').
- **The coordinator must always be registered in `pg_dist_node`** (a prerequisite for `citus_add_local_table_to_metadata`). The check must be **"is the coordinator (`groupid = 0`) row present"**, not "is `pg_dist_node` empty" — an "only register when pg_dist_node is empty" check would break in a setup where the user added a worker first via `citus_add_node` (non-empty pg_dist_node but no coordinator). mkfs --citus calls `citus_set_coordinator_host(host, port)` when `SELECT count(*) FROM pg_dist_node WHERE groupid = 0` is 0.
- **Set `shouldhaveshards = true` only with 0 workers**: in a single-node setup (no workers in `pg_dist_node`), `create_distributed_table` dies with "replication_factor (1) exceeds number of worker nodes (0)", so the coordinator itself must be a shard host (`citus_set_node_property(host, port, 'shouldhaveshards', true)`). With workers present, shards belong on the workers, so the coordinator default (`shouldhaveshards = false`) is respected. The check is `SELECT count(*) FROM pg_dist_node WHERE groupid <> 0 AND noderole = 'primary'` = 0, independent of the coordinator-registration check.
- **`FOR UPDATE` needs the distribution key**: `SELECT ... WHERE id = @id FOR UPDATE` dies on Citus with "could not run distributed query with FOR UPDATE/SHARE commands". `Api.Rename` and `Api.EnsureDataRow` change their FOR UPDATE to `WHERE parent_id = @parent_id AND id = @id`, passing parent_id from the caller's `Inode.ParentId` (in Rename, fetched via a broadcast SELECT first if not cached).
- **`ON CONFLICT DO UPDATE` clause expressions must be IMMUTABLE**: `DO UPDATE SET col = current_timestamp` on a distributed/metadata-registered table dies with "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked IMMUTABLE". `WriteChunkSlice` and `ConfigStore.Save` replace `updated_at = current_timestamp` with a client-generated `@now` and reference `EXCLUDED.updated_at` (a constant in VALUES, hence treated as IMMUTABLE). The same SQL works on single PG, so no Citus-flag branch is needed.
- **cross-shard rename is DELETE + INSERT OVERRIDING SYSTEM VALUE**: the path in `Rename` that changes parent_id is rejected by Citus as a distribution-key rewrite. Within one transaction it row-locks the old row (FOR UPDATE) -> DELETE -> INSERT into the new shard, preserving the BIGSERIAL id with `OVERRIDING SYSTEM VALUE`. The parent-unchanged case uses the original UPDATE (lightweight, single-shard) as-is.

### `pgfs_inode` path traversal cost

Resolving `/a/b/c` with `pgfs_inode` distributed by `parent_id`:

1. get root (id=0) -> 1 shard.
2. get root's child `a` (`WHERE parent_id=0 AND name='a'`) -> the shard holding parent_id=0 (= the root's shard).
3. get `a`'s child `b` -> the shard holding parent_id=a.id. **Since this is hashed on a.id, likely a different shard.**
4. get `b`'s child `c` -> likewise a hash-determined shard.

At depth N, up to N cross-shard hops. In practice:

- **When `InodeCache.byPath` hits, the 2nd time on is 0 hops** ([src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs)).
- Only the first path resolution after a cold start (e.g. a process restart) is slow.
- Directory depth is realistically 10–20 -> ~10–20 shard hops per request ≈ tens of ms (acceptable).

For workloads where InodeCache hit rate degrades (e.g. heavy random path access), increase `InodeCache`'s size via `mount.cache_max_entries`, or consider maintaining a path -> inode_id materialized view on the coordinator.

### cross-shard rename

When `Rename(id, newParentId, newName)`'s newParentId is on a different shard, the row moves across shards. Citus does **not support an UPDATE that changes the distribution key**, so `UPDATE pgfs_inode SET parent_id = @new` may not go through directly. The handling is INSERT into the new shard + DELETE of the old row within one transaction, preserving inode_id via `OVERRIDING SYSTEM VALUE`. An alternative — distributing `pgfs_inode` by `id` so rename is a plain UPDATE — breaks the `(parent_id, name)` UK (no cross-shard UK guarantee); the trade-off is in the open questions below.

---

## Cross-client locking (`pgfs_lock`)

The lock helpers live in [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) (`LockTargets` / `LockData` / `LockInode` / `LockInodes`). Each mutating Api method takes the appropriate lock at its start and releases it when the transaction ends (COMMIT/ROLLBACK). Multi-node Citus + 2-client concurrent races are covered by [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh).

### Motivation

When multiple clients (e.g. a Linux mount.pgfs and a Windows pgfs.assign sharing the same pgfs DB) **write the same file at once**, races occur:

- `WriteData`: two clients read-modify-writing the same chunk lose updates.
- `TruncateData`: a truncate during a write causes inconsistency.
- `Rename`: two clients renaming the same file to different paths — one is rejected by ON CONFLICT, but the in-memory InodeCache holds stale state.

[Notify (LISTEN/NOTIFY)](history.md) is **after-the-fact change notification**, not race prevention; mutual exclusion during writes is needed separately.

### Why a custom table + `SELECT FOR UPDATE`

| Aspect | `pg_advisory_xact_lock` | custom TTL table + heartbeat | **`pgfs_lock` + SELECT FOR UPDATE (chosen)** |
|---|---|---|---|
| Acquire cost | μs (in-memory hash) | ms + heartbeat | ms (fast if the lock row is cached) |
| Waiting | server-side block | client polling (pseudo-block via LISTEN/NOTIFY) | **server-side block** |
| Auto-release | session / tx end | TTL wait (up to 30s lag) | **tx end** |
| TTL race | none | yes (heartbeat lag + TTL race -> double acquisition) | **none** |
| Dedicated connection | (not for the xact form) | required (for heartbeat) | **not required** |
| Citus distribution | coordinator-local only | distributable (target_id key) | **distributable** |
| Visibility | `pg_locks` view | `SELECT FROM pgfs_lock` | `pg_locks` + (who is waiting via pg_stat_activity) |
| Holding across txns | possible with the session form | possible | not possible (released at tx end) |

**Conclusion**: the `SELECT FOR UPDATE` approach gets the strengths of an in-memory advisory lock (sub-ms / server-side block / no TTL race / auto-release) and of the TTL-table approach (Citus-distributable). pgfs writes are "1 operation = 1 transaction", so holding across transactions is unneeded — `FOR UPDATE` suffices.

`pg_advisory_xact_lock` was not chosen because it is coordinator-local (Citus cannot spread lock acquisition to workers -> the coordinator session table is a bottleneck) and invisible across coordinators in a multi-coordinator HA Citus. A custom TTL table was not chosen because fully closing the TTL race (the well-known Redis SETNX-with-TTL problem) needs fencing tokens and added complexity, a dedicated heartbeat connection + background task, and acquire-time client polling that worsens latency.

### Why a single BIGINT (`target_id`)

Citus's `create_distributed_table` is **hash-distributed and accepts only a single column's value as the hash input**. A composite key (e.g. `(kind TEXT, id BIGINT)` to separate kind = 'inode' / 'data') cannot be the distribution key directly. To make `pgfs_lock` distributed (to avoid the coordinator-local bottleneck), the distribution key must be one column — necessarily a single `target_id BIGINT`.

The consequence: **inode ids and data ids (both BIGSERIAL) can collide in the same numeric space**. Handling inode_id = 5 and data_id = 5 both through a single `pgfs_lock(target_id)` would let a tx locking one inadvertently block the other.

### The collision avoidance: a sign-based namespace

`data lock = target_id = data_id (positive)`, `inode lock = target_id = -inode_id (negative)` — the sign separates the namespace (see the `LockData` / `LockInode` helpers in [Api.cs](../src/lib/src/Api/Api.cs)). Unlike the 2-key form of `pg_advisory_xact_lock(ns, key)`, only a single-column PK is available, so the namespace must be expressed on the value side.

An alternative is to split `pgfs_inode_lock` into a separate table, which would let inode locks be distributed by `parent_id` (a different colocation group from data) to cut inode-operation cross-shard hops, and keep operational stats separate. The operational pattern is not settled enough to justify an extra table, so we start with a single `pgfs_lock` + sign namespace, leaving room to re-organize into `pgfs_inode_lock` if inode-lock workload turns out to dominate.

### Schema

```sql
CREATE TABLE pgfs_lock (
    target_id BIGINT PRIMARY KEY
);
```

A single-column PK. Acquiring a lock is just "take the row lock", so no extra columns are needed. Under Citus: `SELECT create_distributed_table('pgfs_lock', 'target_id');`

### Usage

```sql
BEGIN;
-- ensure the target_id row (create if absent)
INSERT INTO pgfs_lock(target_id) VALUES (@data_id) ON CONFLICT DO NOTHING;
-- take the row lock; another tx that SELECT FOR UPDATEs the same row blocks
SELECT 1 FROM pgfs_lock WHERE target_id = @data_id FOR UPDATE;
-- the actual write here
UPDATE pgfs_data_chunk SET payload = ... WHERE data_id = @data_id AND chunk_index = @ci;
COMMIT;   -- the row lock is auto-released at tx end
```

### Implementation decisions

- **`LockTargets`'s SQL is two statements: INSERT ON CONFLICT DO NOTHING + SELECT 1 ... FOR UPDATE.** Each has a target_id-only WHERE, so it completes in a single shard (safe under Citus). Folding into one CTE statement was not attempted (simplicity first).
- **Multiple locks are acquired via `LockInodes(params long[])` in fixed ascending target_id order**: negate the input inode_ids -> dedupe -> sort ascending -> SELECT FOR UPDATE in order. This avoids deadlock even when two renames take a common inode in different orders.
- **`WriteData` / `TruncateData` lock after `EnsureDataRow`**: if `inode.DataId` is null, `EnsureDataRow` creates the data row and fills `inode.data_id` (serializing concurrent races internally with an inode-row FOR UPDATE). Then `LockData(dataId)` serializes WriteData/TruncateData against each other. The order is fixed (inode first -> data second), so it does not conflict with other transactions.
- **`Update*` (Mode/Owner/Size/Timestamps) moved from a single `Pg.Execute` to a transaction**: originally it opened a connection for a single SQL and could not hold a lock; now `using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE.
- **`SetXAttr` / `RemoveXAttr` are not locked**: the lost-update risk for concurrent metadata updates is low, and JSON merge / strip is atomic in a single UPDATE. Can be added later if needed.
- **The data-drop path in `DeleteInode` is not given a data lock**: the lock table covers inodes only. A delete-while-open race (a WriteData on data held by an open file handle) theoretically remains, but the requirement is "I/O on a file deleted while open is undefined".

### Lock-acquisition paths

| Api method | Lock target | Reason |
|---|---|---|
| `WriteData(inode, off, src)` | data: `inode.DataId` | serialize concurrent writes to the same file |
| `TruncateData(inode, len)` | data: `inode.DataId` | same |
| `ReleaseData(dataId)` | data: `dataId` | prevent other clients writing during data release |
| `UpdateMode/Owner/Size/Timestamps(id)` | inode: `id` | prevent lost updates on concurrent metadata changes |
| `Rename(id, newParent, newName)` | inode: `id`, `oldParent`, `newParent` | three locks (old parent / new parent / target inode); ascending order avoids deadlock |
| `DeleteInode(inode)` | inode: `inode.Id`, `inode.ParentId` | serialize the parent's children list and the body changing together |
| `CreateHardLink(source, newParent, newName)` | inode: `source.Id`, `newParent` | serialize the hard-link sibling's nlink update and the new inode creation |

Paths taking multiple locks (`Rename` / `DeleteInode` / `CreateHardLink`) avoid deadlock by **fixed ascending target_id order**.

### Lock-row accumulation

Rows are only added via `INSERT ON CONFLICT DO NOTHING`, never `DELETE`d.

- ~50 bytes per row (including the PRIMARY KEY index).
- 1 million files x 2 (data + inode) ≈ 100MB -> negligible.
- Adding deletion would open a race ("the lock row is DELETEd <-> another tx comes to SELECT FOR UPDATE it").
- A separate cron `DELETE` is possible if ever needed (realistically not).

### Cautions

- **Co-location asymmetry**: after `mkfs --citus`, `citus_tables` shows all four distributed tables (`pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` / `pgfs_lock`) in the same colocation_id (default group) (BIGINT distribution key + same shard_count, so Citus auto-co-locates). When it helps and when it does not:
  - **data-lock case** (target_id = data_id): `pgfs_lock`'s distribution-key value equals `pgfs_data`'s (id) -> same hash -> **lands on the same shard, no cross-network hop**.
  - **inode-lock case** (target_id = -inode_id): `pgfs_lock`'s hash(-inode_id) differs from `pgfs_inode`'s hash(parent_id) -> **the lock shard and inode shard diverge** (one network hop).

  Acceptable for now; if an inode-lock-dominant workload appears, splitting `pgfs_inode_lock` into a separate table distributed by `parent_id` + co-located with `pgfs_inode` resolves the asymmetry.
- **Locks spanning multiple shards under Citus**: with `WHERE target_id IN (a, b)` where a and b are on different shards, acquisition order is non-deterministic and raises deadlock risk. Keep **1 SQL = 1 lock unit** (the helpers take them one at a time).
- **Forgetting to lock**: writing `WriteData` etc. without calling `LockData` / `LockInode` causes a race. The discipline of checking every mutation path in review is required; the convention of calling `LockData` at the start of `Api.WriteData(...)` is also stated in [Api.cs](../src/lib/src/Api/Api.cs)'s class doc.
- **Application-side deadlock**: if one thread takes nested locks `LockData(A)` -> `LockData(B)` while another takes them in reverse, it hangs. **Ascending target_id order** is fixed as a project convention.

---

## Verification

The [tests/citus/](../tests/citus/README.md) suite covers:

- **single-node and multi-node setup** via `mkfs --clean --citus` (with and without `--worker`).
- **Linux / Windows e2e pass unchanged** under Citus (distribution does not affect behavior).
- **the mkfs matrix** (3 initial x 6 target = 18 cases of new / keep-existing / rebuild-via-`--clean`) — [test_matrix.sh](../tests/citus/test_matrix.sh).
- **shard targeting** via `EXPLAIN ANALYZE` — [verify.sql](../tests/citus/verify.sql) confirms `WHERE parent_id = N AND id = N` is Task Count 1 while a bare `WHERE id = N` scans all shards.
- **cross-shard rename** (the INSERT + DELETE path) via the rename-into-subdir e2e case.
- **`pgfs_settings` is coordinator-only and metadata-registered** (`citus_table_type='local'` in verify.sql).
- **DDL propagation / `citus_add_node` auto-sync / `citus_add_local_table_to_metadata` without explicit set_coordinator_host** — [multinode_probe.sh](../tests/citus/multinode_probe.sh).
- **concurrent races from 2 clients** (write race md5 match, mkdir race EEXIST, lock-row accumulation stays bounded) — [race_multinode.sh](../tests/citus/race_multinode.sh).

---

## Open questions / future

- The measured cost of `pgfs_inode`'s `parent_id` distribution causing cross-shard path-traversal hops, on a workload with low InodeCache hit rate.
- Preserving inode_id across a cross-shard rename: the INSERT + DELETE method changes the BIGSERIAL id; the SQL design to keep the same id via `OVERRIDING SYSTEM VALUE`.
- Whether to target multi-coordinator HA Citus (Enterprise); if so, the advisory-lock + coordinator-local combination needs separate consideration.
- Whether bytea partial reads (`substring`) really get partial TOAST detoast on PG 13+; if so, a larger chunk_size is fine.
- Distributing `pgfs_inode` by `id` and guaranteeing the `(parent_id, name)` UK in another layer (e.g. application-side retry on `unique_violation`) — worth revisiting if the `parent_id`-distribution rename cost is unacceptable.
- Citus version dependence: record the tested Citus version, since `create_distributed_table`'s signature can change.

## References

- [Citus docs](https://docs.citusdata.com/) — distributed tables in general.
- [docs/database.md](database.md) — the current schema.
- [docs/history.md](history.md) — the Notify (cross-client change notification) design.
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) — the data-access implementation.
- [docs/performance.md](performance.md) — InodeCache performance ideas (which ease the cross-shard hop).
