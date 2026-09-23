# The database schema design

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the database schema design** - the role of each table and
> the meaning of its columns, how the PKs and the indexes were chosen (including the Citus restrictions),
> **the timestamp convention (always UTC)** and **the occupied bytes and `st_blocks`**.
> "Why it is this shape" is authoritative here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../ddl/README.md](../ddl/README.md) | **The DDL itself** (one table = one file) |
> | [../Mkfs.md](../Mkfs.md) | The CLI that applies the DDL, and the defaults |
> | [support_for_citus.md](support_for_citus.md) | Choosing the distribution keys, the shard count and the exclusion |
> | [data-id-lifecycle.md](data-id-lifecycle.md) | The lifetime of `data_id` (settled at create, immutable until unlink) |
> | [audit-log.md](audit-log.md) | The monthly partitioning of `{prefix}audit` |
> | [xattr-bytea.md](xattr-bytea.md) | The byte-string transparency of the xattr columns |

The database schema design of the PostgreSQL filesystem driver (PGFS).

> **The DDL files**: the CREATE statements for each table are split per table under
> [docs/ddl/](../ddl/README.md). They are for reference when setting things up by hand or checking the design.
>
> **Citus (horizontal distribution) support**: for the distribution strategy, the move to bytea, the
> `pgfs_lock` table and so on see [docs/support_for_citus.md](support_for_citus.md) (Phases 1+2+3 are complete
> and it is opt-in through `mkfs --citus`).

-----

## 🏛️ The outline of the PGFS database schema design

* For auditing, the tables hold the creation time, the creating user name, the update time and the updating user
  name.
* No foreign keys, foreign constraints or triggers are created, because they complicate manual management when
  something goes wrong. Sequences are used, though.

### ⏱️ The timestamp convention: the value of a `TIMESTAMP` column is always UTC

Every timestamp column is a `TIMESTAMP` (= `timestamp without time zone`), and **the value held is always a UTC
wall clock**. They are converted to an epoch as UTC when returned to FUSE or Dokan (through the
`DateTimeKind.Utc` in `FileSystem`), so the writing side has to match. Mixing in a local time makes
**an mtime/ctime that is off by the host's UTC offset** visible (9 hours in JST).

There are only two rules to follow.

| Where it is generated | How to write it |
|---|---|
| Putting "now" in inside SQL | `current_timestamp AT TIME ZONE 'UTC'` (a bare `current_timestamp` is the local wall clock of the session's TimeZone) |
| Passing a `DateTime` from C# | Normalize it with `Pg.UtcNow` / `Pg.ToDbUtc(value)` to **a UTC wall clock plus `DateTimeKind.Unspecified`** |

A `DateTime` with `Kind=Utc` must not be passed as a parameter as it is. Npgsql sends it as a `timestamptz`, so
when PG assigns it to a `timestamp` column it **casts through the session's TimeZone** and a local wall clock is
stored. For the same reason, when an elapsed time is computed in SQL it is compared against
`now() AT TIME ZONE 'UTC'` (the uptime and the heartbeat elapsed time of `StatusAdmin.ListMounts`).

* The column DEFAULTs are `DEFAULT (current_timestamp AT TIME ZONE 'UTC')` too ([docs/ddl/](../ddl/README.md)).
* The monthly partition boundaries of the audit log are decided on UTC too (the `occurred_at` is UTC, so they
  agree).
* **Migrating an existing FS**: `mkfs` does not rewrite the DEFAULTs of an existing table, so an FS created
  before this convention has rows in local time and the old DEFAULTs. Migrating an FS created on a host in a
  non-UTC timezone needs the DEFAULTs replaced
  (`ALTER TABLE … ALTER COLUMN … SET DEFAULT (current_timestamp AT TIME ZONE 'UTC')`) and the existing rows
  shifted (`UPDATE … SET st_mtime = st_mtime - interval 'N hours'`, where N is the host's UTC offset at
  creation time). An FS created on a UTC host (a docker container, say) has an offset of 0, so only replacing
  the DEFAULTs is needed.

### 1. The directory entries and the inode attributes (`pgfs_inode`)

The DDL: [docs/ddl/pgfs_inode.sql](../ddl/pgfs_inode.sql)

The core table of the filesystem, holding the information (the inode-like metadata) of every file, directory,
symlink and hardlink.

| The column | The type | NULL | DEFAULT | INDEX | What it is |
|:--|:--|:--|---|---|:--|
| `id` | `BIGSERIAL` | `NOT NULL` | | Part of the composite PK | The unique node id. The identifier of every file and directory. The root is 0 and is INSERTed when the database is built |
| `parent_id` | `BIGINT` | `NOT NULL` | | IX1 UK1 | The `inode_id` of the parent directory. The root is 0 |
| `name` | `TEXT` | `NOT NULL` | | IX2 UK1 | The file name within the parent directory. Unified as UTF-8 |
| `uname` | `TEXT` | `NOT NULL` | | IX3 | The owning user name |
| `gname` | `TEXT` | `NOT NULL` | | IX4 | The owning group name |
| `st_mode` | `INTEGER` | `NOT NULL` | | | The file kind and the permissions (chmod) |
| `st_nlink` | `INTEGER` | `NOT NULL` | `1` | | The hardlink count |
| `st_size` | `BIGINT` | `NOT NULL` | `0` | | The file size in bytes. A directory or a symlink **stays 0** (the reason is in the note below) |
| `st_mtime` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The last modification time (microsecond precision) |
| `st_ctime` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The last inode change time (microsecond precision) |
| `link_target` | `TEXT` | `NULL` | | | The target path of a symlink or a junction |
| `is_junction` | `BOOLEAN` | `NOT NULL` | `FALSE` | | `TRUE` for a Windows junction |
| `data_id` | `BIGINT` | `NULL` | | | The id referring to the file's body (the id in `pgfs_data`). `NULL` for a directory |
| `xattr_names` | `TEXT[]` | `NOT NULL` | `{}` | | The array of extended attribute (xattr) names. Paired with `xattr_values` at the same index. The reserved keys are `user.pgfs_acl` (the canonical ACL document as JSON) and `user.win.attrs` (the Windows attributes as JSON, `{hidden,system,archive}`). For the details see [permission-interop.md](permission-interop.md) |
| `xattr_values` | `BYTEA[]` | `NOT NULL` | `{}` | | The array of xattr values (held faithfully as bytea, so any byte string including NULs is allowed). Paired with `xattr_names` at the same index. Migrated from the old `xattrs JSONB` plus Base64. The design is in [xattr-bytea.md](xattr-bytea.md) |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The creation time |
| `created_by` | `TEXT` | `NOT NULL` | | | The creating user name |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The update time |
| `updated_by` | `TEXT` | `NOT NULL` | | | The updating user name |

* The inode's PK is `(parent_id, id)`, with a separate search INDEX on `id` alone. `(parent_id, name)` is
  UNIQUE.
* The root directory id: `BIGSERIAL`'s automatic allocation starts at 1 by default, but an explicit INSERT of 0
  is possible. The initial data specifies `id=0`
  ([the PostgreSQL serial types](https://www.postgresql.org/docs/17/datatype-numeric.html#DATATYPE-SERIAL)).
* There is no last access time `st_atime`. The same value as `st_mtime` is returned.
* **A directory's `st_size` is fixed at 0** (it is not the number of entries or 4096). The reasons: (1) no
  standard tool interprets `st_size` as "the number of entries" (`du` looks at `st_blocks`, and it only changes
  the decorative number in `ls -l`), so there is no functional need; (2) a COUNT at read time adds a database
  round trip to every `getattr` and destroys the benefit of the InodeCache's hits; (3) maintaining the parent's
  count at write time is an invasive change that adds an UPDATE to every mutating operation, which does not pay
  for a decoration. Leaving it at 0, the safest and cheapest option, was therefore taken.

-----

### 2. Managing the reference to the body (`pgfs_data`)

The DDL: [docs/ddl/pgfs_data.sql](../ddl/pgfs_data.sql)

The table for managing the hardlinks and the bytea chunk splitting.

| The column | The type | NULL | DEFAULT | INDEX | What it is |
|:--|:--|:--|---|---|:--|
| `id` | `BIGSERIAL` | `NOT NULL` | | PK | The data reference id. It may be referred to by several files (hardlinks) |
| `chunk_size` | `INTEGER` | `NOT NULL` | | | The size in bytes of the bytea chunks the file is split into |
| `total_size` | `BIGINT` | `NOT NULL` | | | **The number of bytes of payload stored** (= the sum of `length(payload)` over every chunk). It is not the logical size (that is `pgfs_inode.st_size`). For a sparse file it is smaller than the `st_size` because there are no chunk rows for the holes. For the details see "the occupied bytes and `st_blocks`" below |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The creation time |
| `created_by` | `TEXT` | `NOT NULL` | | | The creating user name |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The update time |
| `updated_by` | `TEXT` | `NOT NULL` | | | The updating user name |

#### The occupied bytes and `st_blocks` (sparse files)

"Occupied" in this section means **the sum of the stored payload lengths**, not PG's physical size after TOAST
compression, nor the disk used by the rows, the indexes or the WAL. `pgfs_data.total_size` holds that value,
and FUSE's **`st_blocks`** (= what `du` looks at) is returned as `ceil(total_size / 512)`.
Deriving `st_blocks` from the logical size (`st_size`) would **report hundreds of times the real thing for a
sparse file** (a file that was `truncate -s 1G`'d has 0 chunk rows = 0 occupied, yet would be reported as
1 GiB).

* **How it is maintained**: the operations that change a chunk's payload (a chunk UPSERT / trimming the last
  chunk / removing a trailing chunk) return the **delta** in the payload length, and one operation reflects it
  with one `UPDATE … SET total_size = GREATEST(0, total_size + delta) … RETURNING total_size`.
  Re-aggregating with `sum(length(payload))` is proportional to the number of chunks, so running it on every
  write becomes quadratically slow on a large file. `length(bytea)` itself only reads the raw size from the
  TOAST pointer, so no detoast happens.
* **Reading it**: the occupied bytes are not on the inode row, so `Api.GetOccupiedBytes` fetches one row from
  `{prefix}data` and caches it on the in-memory `Inode`. A directory enumeration has `ListChildren`
  **prefetch them all in one query** with `id = ANY(…)` to avoid an N+1 on the getattrs. On Citus too, the
  distribution key of `{prefix}data` is `id`, so it is a router query (a JOIN between inode and data is not used
  because their distribution keys differ).
* **How holes are handled**: reading a hole returns zeros and **creates no chunk row** (reading does not
  materialize it). Writing into a hole materializes **only the one chunk containing that position**, with zero
  padding from the start of that chunk (`decode(repeat('00', …))`).
* **Where `du` cannot see it**: the Windows (Dokan) side does not return the equivalent of `st_blocks`, so this
  value only takes effect on Linux/FUSE.
* **Migrating an existing FS**: an FS created before this convention has a `total_size` of 0, so `du` returns 0.
  Running the following once puts the occupied bytes in (after that it is maintained automatically).

```sql
UPDATE {prefix}data d
   SET total_size = s.sum_len,
       updated_at = (current_timestamp AT TIME ZONE 'UTC')
  FROM (SELECT data_id, sum(length(payload)) AS sum_len FROM {prefix}data_chunk GROUP BY data_id) s
 WHERE d.id = s.data_id AND d.total_size <> s.sum_len;
```

#### Managing the chunks (`pgfs_data_chunk`)

The DDL: [docs/ddl/pgfs_data_chunk.sql](../ddl/pgfs_data_chunk.sql)

The body is stored as bytea chunks. One chunk = one bytea (1 MB by default). The old design used large objects
(`pg_largeobject`), but they cannot be distributed by Citus so they were replaced with bytea in Phase 1 (for the
details see [docs/support_for_citus.md](support_for_citus.md)).

| The column | The type | NULL | INDEX | What it is |
|:--|:--|:--|---|:--|
| `data_id` | `BIGINT` | `NOT NULL` | PK | The parent data reference id |
| `chunk_index` | `INTEGER` | `NOT NULL` | PK | The chunk's position, starting at 0 |
| `payload` | `BYTEA` | `NOT NULL` | | The chunk's binary data. Its length is "the number of bytes written so far" (the last one is partial, and a hole in the middle is expressed as `length(payload)` not reaching the write offset). A 1 MB-class value is TOASTed by PG, so a partial read (`substring`) gets a server-side partial detoast |
| `created_at` | `TIMESTAMP` | `NOT NULL` | | The creation time |
| `created_by` | `TEXT` | `NOT NULL` | | The creating user name |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | | The update time |
| `updated_by` | `TEXT` | `NOT NULL` | | The updating user name |

> The data flow: `pgfs_inode.data_id` $\rightarrow$ `pgfs_data.id` $\rightarrow$ `pgfs_data_chunk.payload`
> (the bytea body)

-----

### 3. The settings store (`pgfs_settings`)

The DDL: [docs/ddl/pgfs_settings.sql](../ddl/pgfs_settings.sql)

Slice 6 of the settings model tidy-up changed it into a simple key-value store with a flat
(scope, key) PK. One row is the persisted value of one
[`Pgfs.Core.Config.Field`](../../src/core/src/Config/Field.cs). Reading and writing are the job of
[`Pgfs.Core.Config.ConfigStore`](../../src/core/src/Config/ConfigStore.cs)'s `LoadAll` / `Save<T>`.

| The column | The type | NULL | DEFAULT | INDEX | What it is |
|:--|:--|:--|---|---|:--|
| `scope` | `TEXT` | `NOT NULL` | | PK | The scope name. For example `"mount"` or `"file_system"` |
| `key` | `TEXT` | `NOT NULL` | | PK | The key name within the scope. For example `"fallback_uname"` or `"volume_label"` |
| `value` | `JSONB` | `NOT NULL` | `'null'::JSONB` | | The value in its native JSONB form. A string -> `"..."` / a number -> `42` / a bool -> `true`/`false` / null = not set |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The creation time |
| `created_by` | `TEXT` | `NOT NULL` | | | The creating user name |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` | | The update time |
| `updated_by` | `TEXT` | `NOT NULL` | | | The updating user name |

* The PK is `(scope, key)`. An UPSERT is done with `ON CONFLICT (scope, key) DO UPDATE`.
* Only a `Schema.Field<T>` whose `SaveTo` flags include `Db` is saved as a row. One that is `SaveTo=File` only
  is written out to the `pgfs.toml` side.
* The change from the old form (a hierarchy of `id` plus `parent_id`) is destructive, so migrating requires
  rebuilding with `mkfs --clean`.
* On Citus it is a local table registered in the metadata with `citus_add_local_table_to_metadata`. It is
  visible to a JOIN from a distributed table and in the Citus inspection views, but it has no shards
  (the coordinator only).

-----

### 4. The lock tokens (`pgfs_lock`)

The DDL: [docs/ddl/pgfs_lock.sql](../ddl/pgfs_lock.sql)


> **It is not distributed on Citus**: it is made a Citus local table with
> `citus_add_local_table_to_metadata`, holding a single copy on the coordinator. A distributed table refuses a
> row lock (`SELECT … FOR UPDATE`) when `citus.shard_replication_factor > 1`, so it is left undistributed to
> keep the locking mechanism independent of the replication count. Registering it in the metadata routes a query
> entering through a worker to that same row too, so the exclusion holds whichever node is the entry point.
> **Row locks are concentrated in this table alone** and no `FOR UPDATE` is fired at `pgfs_inode` or the rest.
> For the details see [support_for_citus.md, the exclusion and the replication factor](support_for_citus.md).

The lock token table for the cross-client exclusion. It was created in advance in Citus Phase 2 and
built into Api's `LockData` / `LockInode` / `LockInodes` in Phase 3. It does not mean every contention is
resolved, though. **The contention between a parent deletion and a write-through create was fixed**, and all 15 findings raised in the review are closed.
**[CHANGELOG.md, the known limitations](../../CHANGELOG.md) is the source of truth for the limitations that
remain.**

| The column | The type | NULL | INDEX | What it is |
|:--|:--|:--|---|:--|
| `target_id` | `BIGINT` | `NOT NULL` | PK | The id of the lock target. A data lock uses the `data_id` (positive) and an inode lock uses `-inode_id` (negative), separating the namespaces |

There are no audit columns (it is a lock token, not data). The design is: create the row with
`INSERT ... ON CONFLICT DO NOTHING`, take a row lock with
`SELECT 1 FROM pgfs_lock WHERE target_id = @id FOR UPDATE`, and let it be released automatically at the end of
the tx. The rows accumulate (they are never DELETEd), but a million of them is about 100 MB so it is not a
problem. For the details see Phase 3 of [docs/support_for_citus.md](support_for_citus.md).

### 5. The audit log (`pgfs_audit`)

The DDL: [docs/ddl/pgfs_audit.sql](../ddl/pgfs_audit.sql) / the source of truth for the design:
[docs/audit-log.md](audit-log.md)

It records the metadata-changing operations (create / delete / rename / chmod / chown / hardlink) as one
operation = one row. It is a **monthly RANGE partitioned** table keyed on `occurred_at` (no DEFAULT partition is
made; the application ensures the current month's partition with a `CREATE ... IF NOT EXISTS` before the
INSERT). It is opt-in through `audit.enabled` (mkfs `--audit`). The record goes in the same transaction as the
operation (atomically).

| The column | The type | NULL | What it is |
|:--|:--|:--|:--|
| `id` | `BIGSERIAL` | `NOT NULL` | The row id (part of the PK) |
| `occurred_at` | `TIMESTAMP` | `NOT NULL` | The time of the operation (no zone). **The partition key and the Citus distribution key.** The PK is the composite `(occurred_at, id)` |
| `op` | `TEXT` | `NOT NULL` | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink` |
| `target_id` | `BIGINT` | `NULL` | The id of the target inode |
| `parent_id` | `BIGINT` | `NULL` | The parent directory id (for create / delete / rename) |
| `name` | `TEXT` | `NULL` | The entry name |
| `detail` | `JSONB` | `NOT NULL` | The op-specific values (the new mode in octal / the new uname and gname / the old -> new parent and so on) |
| `caller_ip` | `INET` | `NULL` | The source IP as PG sees it (`inet_client_addr()`) |
| `caller_host` | `TEXT` | `NULL` | The host name of the pgfs process |
| `caller_uid` | `BIGINT` | `NULL` | The caller's UID (on Linux from `fuse_get_context`; on Windows there is no numeric uid so it is NULL) |
| `caller_uname` | `TEXT` | `NULL` | The caller's user name |
| `caller_domain` | `TEXT` | `NULL` | The domain or workgroup (Windows only) |

The indexes are `id` alone, `op` and `target_id`. No foreign keys or triggers are created (as with the other
tables).

### 6. The registry of running mounts (`pgfs_mounts`)

The DDL: [docs/ddl/pgfs_mounts.sql](../ddl/pgfs_mounts.sql) / the source of truth for the design: Phase 2 of
[docs/design/control-plane.md](control-plane.md)

A **volatile registry** where each mount.pgfs / assign.pgfs process INSERTs one row at startup, updates
`heartbeat_at` on a periodic heartbeat and DELETEs it on a clean exit. **The exception: when it could not write
everything out within the unmount's deadline and lost unflushed work, it is not DELETEd but left**
(B-2). That is a **gravestone** carrying `ended` / `endedAt` / `unflushedLoss` / `lost` on the
`stats`, which `pgfsctl status` and the warning at the next mount look at. It does not go away automatically, so
operations removes it. **No column was added** (the DDL is concentrated in mkfs, so adding a column would
require a re-mkfs on an existing FS; it went on the `stats` JSONB instead). It is the groundwork for the status
subcommand (operations) to list the running mounts across the cluster. On an existing FS with no such table the
mount merely warns and skips, with no effect on the behaviour (the DDL is concentrated in mkfs). On Citus it is
coordinator-local plus registered in the metadata, like `pgfs_settings` (it is not distributed).

| The column | The type | NULL | What it is |
|:--|:--|:--|:--|
| `mount_id` | `TEXT` | `NOT NULL` | The process-unique id (the PK). Random hex |
| `host` | `TEXT` | `NOT NULL` | The host name of the pgfs process |
| `pid` | `BIGINT` | `NOT NULL` | The process id |
| `mountpoint` | `TEXT` | `NOT NULL` | Where it is mounted |
| `mode` | `TEXT` | `NOT NULL` | `fuse` / `dokan` |
| `started_at` | `TIMESTAMP` | `NOT NULL` | The time it started |
| `heartbeat_at` | `TIMESTAMP` | `NOT NULL` | The last heartbeat (every 30 s by default). Used to judge staleness |
| `config` | `JSONB` | `NOT NULL` | The snapshot of the effective settings |
| `stats` | `JSONB` | `NOT NULL` | The cache statistics and so on |

No foreign keys or triggers are created.
