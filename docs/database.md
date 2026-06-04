This is the database schema design for the PostgreSQL filesystem driver (PGFS).

> **DDL files**: the CREATE statement for each table is split per table under [docs/ddl/](ddl/README.md). Use them for reference, for a manual setup, or to review the design.
>
> **Citus (horizontal sharding)**: for the distribution strategy, bytea storage, and the `pgfs_lock` table, see [docs/support_for_citus.md](support_for_citus.md) (opt-in via `mkfs --citus`).

-----

## 🏛️ PGFS database schema design

* For auditing, every row carries a creation timestamp, creating user name, update timestamp, and updating user name.
* Foreign keys, foreign constraints, and triggers are not created, because they complicate manual recovery when something goes wrong. Sequences, however, are used.

### 1. Directory entries and inode attributes (`pgfs_inode`)

DDL: [docs/ddl/pgfs_inode.sql](ddl/pgfs_inode.sql)

The core table of the filesystem. It holds the (inode-like) metadata for every file, directory, symlink, and hardlink.

| Column        | Type        | NULL       | DEFAULT             | INDEX   | Description                                                       |
|:--------------|:------------|:-----------|---------------------|---------|:-----------------------------------------------------------------|
| `id`          | `BIGSERIAL` | `NOT NULL` |                     | PK      | Unique node ID; the identifier of every file/directory. The root is 0, INSERTed at build time. |
| `parent_id`   | `BIGINT`    | `NOT NULL` |                     | IX1 UK1 | The parent directory's `id`. The root is 0.                      |
| `name`        | `TEXT`      | `NOT NULL` |                     | IX2 UK1 | The file name within the parent directory. UTF-8 throughout.     |
| `uname`       | `TEXT`      | `NOT NULL` |                     | IX3     | Owner user name.                                                 |
| `gname`       | `TEXT`      | `NOT NULL` |                     | IX4     | Owner group name.                                                |
| `st_mode`     | `INTEGER`   | `NOT NULL` |                     |         | File type and permission (chmod).                                |
| `st_nlink`    | `INTEGER`   | `NOT NULL` | `1`                 |         | Hardlink count.                                                  |
| `st_size`     | `BIGINT`    | `NOT NULL` | `0`                 |         | File size in bytes. **Stays 0** for directories and symlinks (reason in the note below). |
| `st_mtime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | Last modification time (microsecond precision).                  |
| `st_ctime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | Inode last change time (microsecond precision).                  |
| `link_target` | `TEXT`      | `NULL`     |                     |         | The link target path for a symlink or junction.                  |
| `is_junction` | `BOOLEAN`   | `NOT NULL` | `FALSE`             |         | `TRUE` for a Windows junction.                                   |
| `data_id`     | `BIGINT`    | `NULL`     |                     |         | ID referencing the file body (`pgfs_data.id`). `NULL` for directories. |
| `xattr_names`  | `TEXT[]`  | `NOT NULL` | `{}`                |         | The xattr name array. Paired with `xattr_values` at the same index. Reserved keys: `user.pgfs_acl` (canonical ACL document JSON), `user.win.attrs` (Windows attributes JSON `{hidden,system,archive}`). See [permission-interop.md](permission-interop.md). |
| `xattr_values` | `BYTEA[]` | `NOT NULL` | `{}`                |         | The xattr value array (held faithfully as bytea; any byte string including NUL). Paired with `xattr_names` at the same index. See [xattr-bytea.md](xattr-bytea.md) for the design. |
| `created_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | Creation timestamp                                               |
| `created_by`  | `TEXT`      | `NOT NULL` |                     |         | Creating user name                                               |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | Update timestamp                                                 |
| `updated_by`  | `TEXT`      | `NOT NULL` |                     |         | Updating user name                                               |

* Root directory ID: because PostgreSQL `BIGSERIAL` starts auto-numbering at `1`, `id` = 0 cannot normally be inserted; it is `INSERT`ed explicitly as 0 during initial data seeding.
* There is no last-access time `st_atime`; it returns the same value as `st_mtime`.
* **A directory's `st_size` is fixed at 0** (not the entry count nor 4096). Reasons: (1) no standard tool interprets `st_size` as "entry count" (`du` uses `st_blocks`; it would only decorate the number in `ls -l`), so there is no functional need. (2) Counting on read adds a DB round-trip per `getattr`, defeating the InodeCache hit rate. (3) Maintaining the parent's count on write is invasive — it adds an UPDATE to every mutating operation — and not worth it for a decorative value. So the safest, lowest-cost choice of leaving it 0 is adopted.

-----

### 2. Reference management for file bodies (`pgfs_data`)

DDL: [docs/ddl/pgfs_data.sql](ddl/pgfs_data.sql)

A table that manages hardlinks and bytea chunk splitting.

| Column       | Type        | NULL       | DEFAULT             | INDEX | Description                                                  |
|:-------------|:------------|:-----------|---------------------|-------|:-------------------------------------------------------------|
| `id`         | `BIGSERIAL` | `NOT NULL` |                     | PK    | Data reference ID. May be referenced by multiple files (hardlinks). |
| `chunk_size` | `INTEGER`   | `NOT NULL` |                     |       | The size in bytes of the bytea chunks the file is split into. |
| `total_size` | `BIGINT`    | `NOT NULL` |                     |       | The logical total size of the file this body holds.          |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | Creation timestamp                                           |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | Creating user name                                           |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | Update timestamp                                             |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | Updating user name                                           |

#### Chunk management (`pgfs_data_chunk`)

DDL: [docs/ddl/pgfs_data_chunk.sql](ddl/pgfs_data_chunk.sql)

The file body is stored as bytea chunks: one chunk = one bytea (1MB by default).

| Column        | Type        | NULL       | INDEX | Description                                                  |
|:--------------|:------------|:-----------|-------|:-------------------------------------------------------------|
| `data_id`     | `BIGINT`    | `NOT NULL` | PK    | Parent data reference ID.                                    |
| `chunk_index` | `INTEGER`   | `NOT NULL` | PK    | The chunk order, starting at 0.                              |
| `payload`     | `BYTEA`     | `NOT NULL` |       | The chunk's binary data. Its length is "the number of bytes written so far" (the tail may be partial; an interior hole is represented as length(payload) not yet reaching the write offset). A ~1MB value is TOASTed by PG, so partial reads (`substring`) benefit from server-side partial detoast. |
| `created_at`  | `TIMESTAMP` | `NOT NULL` |       | Creation timestamp                                           |
| `created_by`  | `TEXT`      | `NOT NULL` |       | Creating user name                                           |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` |       | Update timestamp                                             |
| `updated_by`  | `TEXT`      | `NOT NULL` |       | Updating user name                                           |

> Data flow: `pgfs_inode.data_id` $\rightarrow$ `pgfs_data.id` $\rightarrow$ `pgfs_data_chunk.payload` (the bytea data body)

-----

### 3. Settings store (`pgfs_settings`)

DDL: [docs/ddl/pgfs_settings.sql](ddl/pgfs_settings.sql)

A simple key-value store with a flat `(scope, key)` PK. One row = the persisted value of one [`Pgfs.Lib.Config.Field`](../src/lib/src/Config/Field.cs). Reading and writing are handled by `LoadAll` / `Save<T>` in [`Pgfs.Lib.Config.ConfigStore`](../src/lib/src/Config/ConfigStore.cs).

| Column       | Type        | NULL       | DEFAULT             | INDEX | Description                                                       |
|:-------------|:------------|:-----------|---------------------|-------|:-----------------------------------------------------------------|
| `scope`      | `TEXT`      | `NOT NULL` |                     | PK    | Scope name. e.g. `"mount"`, `"file_system"`.                     |
| `key`        | `TEXT`      | `NOT NULL` |                     | PK    | Key name within the scope. e.g. `"fallback_uname"`, `"volume_label"`. |
| `value`      | `JSONB`     | `NOT NULL` | `'null'::JSONB`     |       | The value in native JSONB form. string → `"..."` / number → `42` / bool → `true`/`false` / null = unset. |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | Creation timestamp                                               |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | Creating user name                                               |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | Update timestamp                                                 |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | Updating user name                                               |

* The PK is `(scope, key)`. UPSERT is done with `ON CONFLICT (scope, key) DO UPDATE`.
* Only fields whose `Schema.Field<T>` `SaveTo` flag includes `Db` are stored as rows. Fields that are `SaveTo=File` only are written to `pgfs.toml`.
* On Citus, this is a local table registered into metadata with `citus_add_local_table_to_metadata`. It is visible from JOINs against distributed tables and in Citus inspection views, but holds no shards (coordinator only).

-----

### 4. Lock token (`pgfs_lock`)

DDL: [docs/ddl/pgfs_lock.sql](ddl/pgfs_lock.sql)

A lock-token table for cross-client mutual exclusion.

| Column      | Type     | NULL       | INDEX | Description                                                  |
|:------------|:---------|:-----------|-------|:-------------------------------------------------------------|
| `target_id` | `BIGINT` | `NOT NULL` | PK    | Lock target ID. A data lock uses `data_id` (positive); an inode lock uses `-inode_id` (negative), namespacing the two. |

No audit columns (it is a lock token, not data). A row is created with `INSERT ... ON CONFLICT DO NOTHING`, and locked with `SELECT 1 FROM pgfs_lock WHERE target_id = @id FOR UPDATE` → released automatically when the transaction ends. Rows accumulate (they are never DELETEd), but at 1,000,000 rows that is ~100MB, so it is not a concern. See [docs/support_for_citus.md](support_for_citus.md).

### 5. Audit log (`pgfs_audit`)

DDL: [docs/ddl/pgfs_audit.sql](ddl/pgfs_audit.sql) / design of record: [docs/audit-log.md](audit-log.md)

Records metadata-changing operations (create / delete / rename / chmod / chown / hardlink), one operation = one row. A table **RANGE-partitioned monthly** on `occurred_at` (no DEFAULT partition; before INSERT the app ensures the current month's partition with `CREATE ... IF NOT EXISTS`). Opt-in via `audit.enabled` (mkfs `--audit`). The record is written in the same transaction as the operation (atomic).

| Column          | Type        | NULL       | Description                                                       |
|:----------------|:------------|:-----------|:-----------------------------------------------------------------|
| `id`            | `BIGSERIAL` | `NOT NULL` | Row ID (part of the PK)                                          |
| `occurred_at`   | `TIMESTAMP` | `NOT NULL` | Operation time (no zone). **Partition key / Citus distribution key.** The PK is the composite `(occurred_at, id)`. |
| `op`            | `TEXT`      | `NOT NULL` | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink`  |
| `target_id`     | `BIGINT`    | `NULL`     | The target inode's id                                            |
| `parent_id`     | `BIGINT`    | `NULL`     | Parent directory id (create / delete / rename)                   |
| `name`          | `TEXT`      | `NULL`     | Entry name                                                       |
| `detail`        | `JSONB`     | `NOT NULL` | op-specific values (new mode in octal / new uname/gname / old→new parent, etc.) |
| `caller_ip`     | `INET`      | `NULL`     | The connecting IP as seen by PG (`inet_client_addr()`)           |
| `caller_host`   | `TEXT`      | `NULL`     | The pgfs process's host name                                     |
| `caller_uid`    | `BIGINT`    | `NULL`     | The caller's UID (Linux: `fuse_get_context`; NULL on Windows, which has no numeric uid) |
| `caller_uname`  | `TEXT`      | `NULL`     | The caller's user name                                           |
| `caller_domain` | `TEXT`      | `NULL`     | Domain / workgroup (Windows only)                                |

INDEXes are on `id` alone / `op` / `target_id`. No foreign keys or triggers (same as the other tables).
