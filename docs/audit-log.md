# Audit log

Records **metadata-changing operations** — chmod / chown / delete / rename / create / hard link — into a dedicated table `{prefix}audit`, one operation per row.

A Japanese translation is available in [audit-log.ja.md](audit-log.ja.md).

Rather than a single text line via `Logger.Information`, the audit trail is kept **structured in a date-partitioned DB table**. This document is the source of truth.

## Design decisions

| # | Topic | Decision |
|---|---|---|
| 1 | "who" | Record the caller's **IP / hostname / UID / user name / domain (Windows only)** |
| 2 | Output target | A **DB table** `{prefix}audit`, not a log file |
| 3 | Partitioning | **Monthly RANGE** on `occurred_at` + app auto-creation + no `DEFAULT` |
| 4 | Write timing | **Same transaction as the operation (atomic)** |
| 5 | on/off | DB-stored setting `audit.enabled`. **Initialized by mkfs (`--audit`)**; a management tool later |
| 6 | prefix | not needed (structured DB rows, so no grep token) |
| 7 | Citus placement | **distributed** by `occurred_at` (assuming queries always specify a time range) |
| 8 | meaning of caller IP | **own hostname** + **the connection source IP as seen by PG** (`inet_client_addr()`) |

## Schema `{prefix}audit`

A RANGE-partitioned table keyed on `occurred_at`.

| Column | Type | Content |
|---|---|---|
| `id` | BIGSERIAL | row id (part of the PK) |
| `occurred_at` | TIMESTAMP NOT NULL DEFAULT current_timestamp | operation time (no zone, consistent with other tables). **Partition key / Citus distribution key** |
| `op` | TEXT NOT NULL | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink` |
| `target_id` | BIGINT | the target inode's id |
| `parent_id` | BIGINT NULL | parent directory id (create / delete / rename) |
| `name` | TEXT NULL | entry name |
| `detail` | JSONB NOT NULL DEFAULT '{}' | op-specific values (new mode in octal, new uname/gname, old->new parent, etc.) |
| `caller_ip` | INET NULL | connection source IP as seen by PG (`inet_client_addr()`; NULL for a local connection) |
| `caller_host` | TEXT NULL | hostname where the pgfs process runs (resolved once at process start) |
| `caller_uid` | BIGINT NULL | caller UID (FUSE per-call context / Dokan) |
| `caller_uname` | TEXT NULL | caller user name |
| `caller_domain` | TEXT NULL | domain / workgroup (Windows only; NULL on Linux) |

- **The PK is the composite `(occurred_at, id)`**. The partition key `occurred_at` must be in the PK (the Postgres partitioned-table constraint), and under Citus the distribution key must be in the unique constraint too (the same situation as `pgfs_inode`'s `(parent_id, id)`). A standalone `id` INDEX is kept.
- The fixed columns are the search axes ("when / who / what / which"). Op-specific values go into the `detail` JSONB (a simple one-row-JSONB design).

### `detail` contents (per op)

| op | example detail |
|---|---|
| `create` | `{"mode":"40755","kind":"dir","uname":"alice","gname":"staff"}` |
| `delete` | `{}` (the fixed columns target_id / parent_id / name are enough) |
| `rename` | `{"old_parent":12,"new_parent":34,"new_name":"b.txt"}` |
| `chmod` | `{"mode":"100644"}` (octal string) |
| `chown` | `{"uname":"bob","gname":"dev"}` |
| `hardlink` | `{"source_id":7,"new_parent":34,"new_name":"link"}` |

## Partition strategy (monthly + app lazily ensures the current month / no DEFAULT)

- The parent table is `PARTITION BY RANGE (occurred_at)`. mkfs creates **only the parent table**, not the partitions.
- Month partitions (`{prefix}audit_YYYY_MM`, range `[1st of month, 1st of next month)`) are **ensured by the application before INSERT** with `CREATE TABLE IF NOT EXISTS ... PARTITION OF ...`. It caches the month set (`yyyy_MM`) in process memory and issues the DDL only on the first row of that month. Since "there is only one current time", reading all partitions ahead at startup is unnecessary — lazily creating only the current month (and any month crossed while running) suffices.
- **No DEFAULT partition is created.** Reason: once rows accumulate in DEFAULT, a later `CREATE` of a month partition covering that range errors with `updated partition constraint for default partition ... would be violated by some row` (DEFAULT becomes a trap). Since the current month is always ensured before INSERT, DEFAULT is unnecessary.
- If ensure fails, the INSERT fails with `no partition found` for the missing month partition and **the whole operation transaction rolls back** (the atomic policy: if the audit cannot be recorded, the operation does not stand either).

### Coexisting with Citus (note — out-of-band ensure)

`occurred_at` distribution + same-tx write + app auto-partition-creation collide if combined naively: Citus can **reject DDL (the partition CREATE) in the middle of a distributed write transaction**.

The workaround: **ensure the partition (`EnsureAuditPartition`) on a separate connection (autocommit), not the operation tx**, narrowed by the memory cache to the first row of a month. The audit-row INSERT itself runs inside the operation tx, so no DDL runs within that tx.

- Under Citus the parent table is distributed with `create_distributed_table('{prefix}audit', 'occurred_at')`. Once the parent is distributed, Citus auto-distributes the month partitions.
- Residual risk: mixing the audit (`occurred_at`-distributed) INSERT into the operation tx (where inode is `parent_id`-distributed) makes a **distributed transaction spanning multiple distributed tables (2PC)**. Citus supports this functionally; verify it actually commits in multi-node e2e (see tests below).

## Wiring the caller context

The per-operation "who" can only be obtained at the OS layer (Mount / Assign), so `Pgfs.Lib.Models.AuditContext` is passed to the Api as an **ambient (`AsyncLocal`)** value. Each FUSE / Dokan callback sets `AuditContext.Current` at its start, and the Api reads it at hook time.

`AuditContext` carries only the per-call `Uid` / `Uname` / `Domain`. `caller_host` is a process constant, so the Api resolves `Dns.GetHostName()` once in its constructor, and `caller_ip` is evaluated server-side in the INSERT with `inet_client_addr()` (neither goes into `AuditContext`).

| Value | Linux (Mount) | Windows (Assign) |
|---|---|---|
| caller_uid | `fuse_get_context()->uid` (the fork's added `Fuse.TryGetCallerContext`) | (NULL — Windows has no numeric uid concept) |
| caller_uname | uid resolved via `UserResolver.UnameOf` | the user part of Dokan `info.GetRequestor()`'s `DOMAIN\user` |
| caller_domain | (NULL — Linux has no domain/workgroup concept) | the DOMAIN part of the same `DOMAIN\user` |
| caller_host | `Dns.GetHostName()` at process start | same |
| caller_ip | `inet_client_addr()` in the INSERT (server-side) | same |

- **Tmds.Fuse fork extension**: the fork ([vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/)) did not expose `fuse_get_context()`, so a P/Invoke was added and exposed as the public API `Fuse.TryGetCallerContext(out uid, out gid, out pid)`. So a libfuse that cannot resolve the symbol does not break the mount, symbol resolution is nullable (`TryCreateDelegate`).
- caller_host is "which machine performed the operation", caller_ip is "the connection source as seen by PG". In a setup where multiple clients hit the same DB-FS, you can later trace "which client's who".
- The OS layer calls `SetAuditContext()` at the start of each mutating callback to set `AuditContext.Current` (when auditing is disabled or retrieval fails, it sets null = caller_* just become NULL).

## Hook points ([src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs))

After confirming success (`rows > 0` / non-null return), `WriteAudit` is called **within the same tx**.

| Operation | Method | op | Recorded |
|---|---|---|---|
| create (dir/file/symlink) | `InsertInode` (shared path) | `create` | target=new id, parent, name, detail(mode/kind/uname/gname) |
| delete | `DeleteInode` | `delete` | target=inode.Id, parent, name |
| rename | `Rename` | `rename` | target=id, detail(old->new parent, new_name) |
| chmod | `UpdateMode` | `chmod` | target=id, detail(mode in octal) |
| chown | `UpdateOwner` | `chown` | target=id, detail(uname/gname) |
| hardlink | `CreateHardLink` | `hardlink` | target=new id, detail(source_id, new_parent, new_name) |

- **Excluded**: `WriteData` / `UpdateSize` / `UpdateTimestamps` (data I/O and incidental updates are too noisy).
- `InsertInode` originally had no transaction (a direct `Pg.Query`); to put the audit in the same tx it was changed to **use `conn` + `tx`** (rollback on conflict).
- When `audit.enabled` is false, `WriteAudit` returns immediately (the hook is always called but is a no-op).

## The on/off setting `audit.enabled`

- `Schema.Audit.Enabled` (`scope=audit`, `key=enabled`, `BoolField`, `SaveTo=Db`, CLI `--audit`, default false).
- mkfs saves it into `pgfs_settings` via `PopulateSettingsRows`. mount / assign read it through `ConfigLoader`'s DB phase at startup, and the Api enables the hooks via `config.Audit.Enabled`.
- A management tool could flip the `pgfs_settings` `(audit, enabled)` row later (this implementation only initializes it in mkfs).

## Implementation

Three layers:

1. **Settings / schema**: [Schema.Audit.Enabled](../src/lib/src/Config/Schema.cs) + [AuditConfig](../src/lib/src/Config/AuditConfig.cs) + `ConfigLoader` wiring, [docs/ddl/pgfs_audit.sql](ddl/pgfs_audit.sql), mkfs [CreateAuditTableAsync](../src/mkfs/src/Initializer.cs) + the settings-row insertion.
2. **Api hooks**: the [AuditContext](../src/lib/src/Models/AuditContext.cs) ambient, [Api.WriteAudit / EnsureAuditPartition](../src/lib/src/Api/Api.cs), hooks on the 6 methods, the transactionalization of `InsertInode`.
3. **Caller plumbing**: `fuse_get_context` in the Tmds.Fuse fork ([LibFuse.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/LibFuse.cs) / `Fuse.TryGetCallerContext`), `SetAuditContext` in the 9 callbacks of [mount/FileSystem.cs](../src/mount/src/FileSystem.cs) and in CreateFile/Cleanup/SetFileAttributes/MoveFile of [assign/FileSystem.cs](../src/assign/src/FileSystem.cs).

## Tests

Run in the test environment ([docs/tests.md](tests.md)):

- Existing regression: the Linux / Windows e2e stay green.
- Added: each op inserts the expected row into `pgfs_audit`; `audit.enabled=false` yields 0 rows; partitions are auto-created across a month boundary; caller_* (uid/uname/domain/host/ip) are populated as expected.
- Citus: with auditing enabled on multi-node Citus, an operation commits in a single tx (verifying the 2PC risk), reusing the [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) framework.
