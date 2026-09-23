# The audit log

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: the design and the implementation status of the audit log
> feature. The schema of `{prefix}audit` and the contents of its `detail`, the procedure of "ensure the current
> month before the op if it is not there" for the monthly RANGE partitions, the 6 operations that are recorded
> and where they are hooked, how the caller's context (uid / uname / domain / host / ip) is obtained, the on/off
> of `audit.enabled`, and the contract that **the audit is not durable once the metadata write-back is
> enabled**, all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../ddl/README.md](../ddl/README.md) | The DDL itself for `{prefix}audit` ([pgfs_audit.sql](../ddl/pgfs_audit.sql)) |
> | [database.md](database.md) | The design of the whole schema and the timestamp convention. This document holds only the audit table's share |
> | [metadata-write-back.md](metadata-write-back.md) | The design of the metadata deferral (1e) itself. The side that folds the audit rows into the flush tx |
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | The review record of 1e (how `writeback_loss` and making the cancellation pair synchronous came about) |
> | [windows-parity.md](windows-parity.md) | The Windows (Dokan) as-built. Where `GetRequestor` is obtained and what remains different |
> | [settings-matrix.md](settings-matrix.md) | The list of `audit.enabled`'s CLI, where it is stored and its reload policy |
> | [../Mkfs.md](../Mkfs.md) | The user-facing `mkfs --audit` specification and the default |
> | [support_for_citus.md](support_for_citus.md) | The policy of the Citus distribution itself, including the audit table |
> | [../tests.md](../tests.md) | The hub for the tests. The counts, how to run them and the environment requirements are there |

The feature that records **the metadata-changing operations** - chmod, chown, deletes, renames, creates and
hardlinks - in a dedicated table `{prefix}audit` as one operation = one row (answering the "audit log"
requirement).

The original idea (the old audit-log implementation plan in [docs/next.md](../next.md)) was "just emit one line
of text through `Logger.Information`", but after a design discussion it was changed to
**keeping it structured in a date-partitioned table in the database**. This document is the current source of
truth.

## The design decisions (settled in discussion)

| # | The question | The decision |
|---|---|---|
| 1 | "Who" | The caller's **IP / host name / UID / user name / domain (Windows only)** are recorded |
| 2 | Where it goes | A **database table** `{prefix}audit`, not a log file |
| 3 | The partitions | **Monthly RANGE partitioning** on `occurred_at`, created automatically by the application, with a `DEFAULT` catch-all |
| 4 | When it is written | **The same transaction as the operation (atomically)** |
| 5 | on/off | The database-stored setting `audit.enabled`. **Initialized by mkfs (`--audit`)** and changed live with `pgfsctl config set audit.enabled true/false` |
| 6 | A prefix | Not needed (it is a structured database row, so it carries no token for grepping) |
| 7 | The Citus placement | **Distributed** on `occurred_at` (on the premise that a search must give a date range) |
| 8 | What the caller IP means | **The own host name** plus **the source IP as PG sees it** (`inet_client_addr()`) |

## The schema `{prefix}audit`

A RANGE partitioned table with `occurred_at` as the partition key.

| The column | The type | What it is |
|---|---|---|
| `id` | BIGSERIAL | The row id (part of the PK) |
| `occurred_at` | TIMESTAMP NOT NULL DEFAULT current_timestamp | The time of the operation (no zone, unified with the other tables). **The partition key and the Citus distribution key** |
| `op` | TEXT NOT NULL | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink` |
| `target_id` | BIGINT | The id of the target inode |
| `parent_id` | BIGINT NULL | The parent directory id (for create / delete / rename) |
| `name` | TEXT NULL | The entry name |
| `detail` | JSONB NOT NULL DEFAULT '{}' | The op-specific values (the new mode in octal, the new uname and gname, the old -> new parent and so on) |
| `caller_ip` | INET NULL | The source IP as PG sees it (`inet_client_addr()`; NULL on a local connection) |
| `caller_host` | TEXT NULL | The host name the pgfs process runs on (resolved once at process startup) |
| `caller_uid` | BIGINT NULL | The caller's UID (the FUSE per-call context / Dokan) |
| `caller_uname` | TEXT NULL | The caller's user name |
| `caller_domain` | TEXT NULL | The domain or workgroup (Windows only; NULL on Linux) |

- **The PK is the composite `(occurred_at, id)`.** The partition key `occurred_at` has to be part of the PK
  (a Postgres restriction on partitioned tables), and on top of that the distribution key has to be part of a
  unique constraint when distributed by Citus (the same situation as `pgfs_inode`'s `(parent_id, id)`). A
  separate INDEX on `id` alone is held.
- The fixed columns are the axes for searching "when, who, what and against which". The values that vary per op
  are pushed into the `detail` JSONB (the simple one-row-JSONB design).
- **The audit must be read by `occurred_at` (the `id` order is not the operation order).** `occurred_at` is
  **the time of the operation** but the `id` is **allocated at INSERT time**, so on a path where the write is
  deferred the two orders diverge. Specifically, when the metadata write-back
  ([metadata-write-back.md](metadata-write-back.md)) is enabled, the audit rows are captured at operation time
  and **INSERTed together in the flush tx** (and delayed further if a failed flush latches), so an operation
  that happened later can be allocated an earlier `id`. An ORDER BY or a range search must always use
  `occurred_at`, and the `id` should be confined to breaking ties within the same `occurred_at`.

### What is in the `detail` (per op)

| The op | An example of the detail |
|---|---|
| `create` | `{"mode":"40755","kind":"dir","uname":"alice","gname":"staff"}` |
| `delete` | `{}` (the fixed columns target_id / parent_id / name are enough). **Only a delete that "was replaced by another inode" under the metadata write-back (1e)** carries `{"reason":"write_back_metadata_rename_replace","replaced_by":99}` (a rename-over-existing replacement) or `{"reason":"write_back_metadata_name_conflict","replaced_by":99}` (a cross-client same-name collision resolved by last-flush-wins) |
| `rename` | `{"old_parent":12,"new_parent":34,"new_name":"b.txt"}` |
| `chmod` | `{"mode":"100644"}` (an octal string) |
| `chown` | `{"uname":"bob","gname":"dev"}` |
| `hardlink` | `{"source_id":7,"new_parent":34,"new_name":"link"}` |

## The partitioning strategy (monthly, with the application lazily ensuring the current month, and no DEFAULT)

- The parent table is `PARTITION BY RANGE (occurred_at)`. mkfs creates **only the parent table** and no
  partitions.
- The monthly partitions (`{prefix}audit_YYYY_MM`, covering `[the 1st of the month, the 1st of the next)`) are
  **secured by the application before the INSERT with
  `CREATE TABLE IF NOT EXISTS ... PARTITION OF ...`**. They are cached in an in-process memory set (`yyyy_MM`)
  and the DDL is fired only on the first occasion for that month. "There is only one current time", so
  pre-reading every partition at startup is unnecessary - lazily creating the current month (and any month
  crossed while running) is enough.
- **No DEFAULT partition is created.** The reason: once rows accumulate in the DEFAULT, a later attempt to
  `CREATE` the monthly partition covering that range makes PG error with
  `updated partition constraint for default partition ... would be violated by some row` (= the DEFAULT becomes
  a trap). The current month is always ensured before an INSERT, so a DEFAULT is unnecessary.
- If the ensure fails, the INSERT fails with `no partition found` because the month's partition is not there,
  and **the whole operation tx rolls back** (= the atomic policy of "if the audit cannot be kept, the operation
  does not happen either").

### Coexisting with Citus (a caveat - the out-of-band ensure)

The three of distributing on `occurred_at`, writing in the same tx, and the application creating partitions
automatically collide if they are assembled naively: Citus can **refuse DDL (a partition CREATE) in the middle
of a distributed write transaction**.

The workaround: **securing the partition (`EnsureAuditPartition`) runs on a separate connection (autocommit)
from the operation tx**, narrowed by the memory cache to the first occasion in a month. The INSERT of the audit
row itself happens inside the operation tx. That way no DDL runs inside the same tx.

- On Citus the parent table is distributed with `create_distributed_table('{prefix}audit', 'occurred_at')`.
  The monthly partitions are distributed automatically by Citus once the parent is distributed.
- The remaining risk: mixing the audit INSERT (distributed on `occurred_at`) into the operation tx (where the
  inode is distributed on `parent_id`) makes it **a distributed transaction (a 2PC) spanning several distributed
  tables**. Citus supports that functionally, but that it really commits is verified in the multi-node e2e
  suite (see the tests below).

### ⚠ The ensure must be called while the tx has not INSERTed a single audit row yet (an undetected deadlock)

The out-of-band ensure above has **a restriction that makes recovery impossible if it is broken**. Measured with
`pg_locks` (PG 17.5):

| The statement | The lock it takes on the parent table `{prefix}audit` |
|---|---|
| The `INSERT` of an audit row | `RowExclusiveLock` (the same on the partition) |
| `CREATE TABLE ... PARTITION OF` | A lock that **conflicts with** `RowExclusiveLock` (it blocks while another session's INSERT tx is open) |

In other words, **calling `EnsureAuditPartition` while the same process's operation tx holds a
`RowExclusiveLock` on the parent makes the other connection's DDL wait for that tx while that tx waits for the
DDL to finish**. The wait graph is split across both sides, so **PG's deadlock detector cannot find the cycle
and, with the default `lock_timeout = 0`, it waits forever**.

- The current write-through path is "1 tx = 1 audit row" and `WriteAudit` ensures **before the INSERT**, so it
  does not hold the parent yet and is safe by coincidence (= this ordering must be observed as part of the
  specification).
- **A shape where several months' audit rows are mixed into one tx** (such as when the metadata write-back's
  pending entries straddle a month because of an error latch) **hits it for certain**, because the second ensure
  runs after the parent has been grabbed. The flush tx of
  [metadata-write-back.md](metadata-write-back.md) therefore
  **ensures the whole set of months of the audit rows it will write before opening the tx**.
- As a defence, the DDL of `EnsureAuditPartition` carries **`SET lock_timeout = '5s'`** (so that a broken
  convention throws instead of hanging). Hanging while holding a FUSE lock (the NSGate and so on) cannot be
  recovered from with anything but `kill -9`.

## Wiring the caller's context

The "who" that varies per operation can only be obtained at the OS layer (Mount / Assign), so
`Pgfs.Core.Models.AuditContext` is passed to the Api **ambiently (`AsyncLocal`)**. Each FUSE / Dokan callback
sets `AuditContext.Current` at its head and the Api reads it at the hook.

The `AuditContext` carries only the `Uid` / `Uname` / `Domain` that vary per call. `caller_host` is a process
constant, so the Api resolves it once in its constructor with `Dns.GetHostName()`, and `caller_ip` is evaluated
server-side with `inet_client_addr()` inside the INSERT (neither goes in the `AuditContext`).

| The value | Linux (Mount) | Windows (Assign) |
|---|---|---|
| caller_uid | `fuse_get_context()->uid` (the `Pgfs.Fuse` binding's `Fuse.TryGetCallerContext`) | (NULL - Windows has no concept of a numeric uid) |
| caller_uname | The uid resolved through `UserResolver.UnameOf` | The user part of the `DOMAIN\user` from Dokan's `info.GetRequestor()` |
| caller_domain | (NULL - Linux has no concept of a domain or workgroup) | The DOMAIN part of the same `DOMAIN\user` |
| caller_host | `Dns.GetHostName()` in the Api at process startup | The same |
| caller_ip | `inet_client_addr()` inside the INSERT (evaluated server-side) | The same |

- **The extension to the `Pgfs.Fuse` binding**: the in-house libfuse binding ([src/fuse/](../../src/fuse/),
  ported from the old Tmds.Fuse fork in v0.2.0) carries a P/Invoke to `fuse_get_context()`, readable through the
  public API `Fuse.TryGetCallerContext(out uid, out gid, out pid)`. So that a libfuse that cannot resolve it
  does not break the mount, the symbol resolution is nullable (`TryCreateDelegate`).
- caller_host is "which machine did it" and caller_ip is "the source as PG sees it". In a configuration where
  several clients hit the same DB-FS, "who on which client" can be traced afterwards.
- The OS layer raises `AuditContext.Current` at the head of each mutating callback (when the audit is disabled or
  it cannot be obtained it is null = the caller_* are simply NULL).
- **⚠ Where it is obtained on Windows**: Dokan's `info.GetRequestor()`
  **succeeds only inside the `CreateFile` callback** (it duplicates the requesting thread's impersonation
  token). Calling it from any other callback always fails with
  `Invalid token for impersonation - it cannot be duplicated`.
  Before the fix it was re-fetched at the head of `Cleanup` / `MoveFile` / `SetFileAttributes` /
  `SetFileSecurity` and failed every time (**3400 times** in one pass of the Windows e2e suite), so
  **the audit rows of operations performed from Windows (assign.pgfs) by a build older than v0.2.0 have NULL
  `caller_uname` and `caller_domain`** (the Linux side is unaffected because `fuse_get_context` works in every
  callback).
  Now the subject settled in `CreateFile` is held on the handle (`FileSystem.OpenFile`) and the later operations
  re-raise it from there. The as-built is in [windows-parity.md, the implementation status](windows-parity.md).

## Where it is hooked ([src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs))

After confirming success (`rows > 0` / a non-null return value), `WriteAudit` is called **inside the same tx**.

| The operation | The method | The op | What is recorded |
|---|---|---|---|
| create (dir/file/symlink) | `InsertInode` (the shared path) | `create` | target = the new id, the parent, the name, the detail (mode/kind/uname/gname) |
| delete | `DeleteInode` | `delete` | target = inode.Id, the parent, the name |
| rename | `Rename` | `rename` | target = the id, the detail (old -> new parent, the new_name) |
| chmod | `UpdateMode` | `chmod` | target = the id, the detail (the mode in octal) |
| chown | `UpdateOwner` | `chown` | target = the id, the detail (the uname/gname) |
| hardlink | `CreateHardLink` | `hardlink` | target = the new id, the detail (the source_id, the new_parent, the new_name) |

- **Excluded**: `WriteData` / `UpdateSize` / `UpdateTimestamps` (they are noisy, being data I/O and incidental
  updates).
- `InsertInode` did not open a tx originally (it hit `Pg.Query` directly), so it was **given a `conn` plus a
  `tx`** to put the audit in the same tx (rolling back on a conflict).
- When `audit.enabled` is false, `WriteAudit` returns immediately (the hook itself is always called but is a
  no-op).

## The on/off setting `audit.enabled`

- `Schema.Audit.Enabled` (`scope=audit`, `key=enabled`, a `BoolField`, `SaveTo=Db`, the CLI `--audit`, false by
  default).
- mkfs saves it into `pgfs_settings` in `PopulateSettingsRows`. mount and assign read it at startup through the
  `ConfigLoader`'s database phase, and the Api enables the hooks from `config.Audit.Enabled`.
- `pgfsctl config set audit.enabled true/false` persists it in the database plus applies it live through a
  NOTIFY. There is no ACK for each mount having applied it.
- **`op = writeback_loss`**: the record of unflushed work that **was lost** because it could
  not be written out within the unmount's deadline. One unmount = one row, and the subject is the mount itself
  so `target_id` is null and `name` is the mountpoint.
  The `detail` holds the count, the breakdown (up to 32 entries) and the `timeout_ms`. It is not written when
  `audit.enabled` is off, in which case the only clue is the gravestone in `{prefix}mounts` (for the details see
  [metadata-write-back-reviews.md, the B-2 as-built](metadata-write-back-reviews.md)).
- **The audit is not durable once the metadata write-back is enabled**. With
  `mount.write_back_metadata = true`, the audit rows of the operations against a pending inode
  **only enter the database in that inode's materialization tx**.
  In other words, **an operation that was not `fsync`ed or `fsyncdir`ed loses its audit row together with the
  operation itself in a crash** (it is not "the operation survived but only the audit was lost" but "neither
  happened"). Whatever could not be written out at unmount time leaves an `op = writeback_loss` row, but
  **that is a record that something was lost, not a record of the individual operations** (it holds only a
  summary of the count and the paths).
  **In an operation that relies on the audit as evidence, leave `write_back_metadata` off.**
- **The restriction of the metadata write-back**: the audit rows of a pending entry are captured in memory at
  operation time and saved in the materialization tx. **Only the create/delete pair of a cancellation (a pending
  entry created and removed within the interval) has been written synchronously** (B-3; before
  that it waited on the orphan queue and `create -> read -> unlink` went through with zero trace).
  **The rest of the orphan queue still waits for a flush and can be lost in a crash.** During a live off the
  orphan flush returns early and can be left behind. A successful operation does not necessarily mean the audit
  was persisted at once. **This restriction is also stated in a user-facing way in
  [CHANGELOG.md, the known limitations](../../CHANGELOG.md).**

## The implementation status (implemented, **completely closed on real hardware**)

All three phases are implemented:

1. **The settings and the schema** ✅: [Schema.Audit.Enabled](../../src/core/src/Config/Schema.cs) plus
   [AuditConfig](../../src/core/src/Config/AuditConfig.cs) plus the `ConfigLoader` wiring,
   [docs/ddl/pgfs_audit.sql](../ddl/pgfs_audit.sql), and mkfs's
   [CreateAuditTableAsync](../../src/mkfs/src/Initializer.cs) plus inserting the settings rows.
2. **The Api hooks** ✅: the ambient [AuditContext](../../src/core/src/Models/AuditContext.cs),
   [Api.WriteAudit / EnsureAuditPartition](../../src/core/src/Api/Api.cs), the hooks in the 6 methods, and
   giving `InsertInode` a tx.
3. **The caller plumbing** ✅: `fuse_get_context` in the Pgfs.Fuse binding
   ([LibFuse.cs](../../src/fuse/src/LibFuse.cs) / `Fuse.TryGetCallerContext`), the 9 callbacks of
   [the FUSE FileSystem.cs](../../src/fuse/src/FileSystem.cs) and the
   CreateFile/Cleanup/SetFileAttributes/MoveFile/SetFileSecurity of
   [the Dokan FileSystem.cs](../../src/dokan/src/FileSystem.cs).
   **Where it is obtained was changed on the Windows side**: `CaptureRequestor` in `CreateFile`
   -> held on the handle -> the other callbacks re-raise it with `ApplyAuditContext` (the reason is in the ⚠
   note above).

## The tests

### Completed

- **The existing regressions** ✅: Linux e2e 34/34, Windows e2e 24/24, the Citus multi-node race 4/4 and the
  Citus mkfs matrix 18/18 stay green (verified with the audit enabled).
- **The Citus same-tx commit** ✅: `Initializer.CreateAuditTableAsync` always creates the table regardless of
  `--audit` and distributes it on `occurred_at` with `--citus` (the number of Citus distributed tables = 5:
  inode/data/data_chunk/lock/audit).
  That an operation can commit in the same tx with the audit enabled on multi-node Citus was demonstrated by
  `race_multinode.sh` (4/4). The 2PC risk did not materialize.

### The audit-dedicated tests - [tests/citus/audit.sh](../../tests/citus/audit.sh) ✅ 12/12 PASS (2026-05-31)

The counts of the existing suites (34/24) are unchanged by adding the audit = the audit-specific verification
was split into the dedicated script [tests/citus/audit.sh](../../tests/citus/audit.sh). It stands up a 2-node
Citus on docker plus one mount.pgfs and verifies the following (for the details see the audit.sh section of
[tests/citus/README.md](../../tests/citus/README.md)). **It PASSed 12/12 on the Linux client**
(with the caller_uid, uname, host and ip confirmed, and the create detail.mode=100664 / kind=file confirmed):

- **A**: each op (create/delete/rename/chmod/chown/hardlink) puts the expected row in `pgfs_audit`. The create
  row's name and detail.kind are examined closely.
- **B**: the caller_uid and caller_uname agree with the caller's real values (fuse_get_context), and the
  caller_host and caller_ip are recorded.
- **C1**: right after an mkfs there is no partition for the current month -> the first op creates it
  automatically (ensure-before-insert). There is no DEFAULT partition.
- **C2**: a direct INSERT into an uncovered month is refused -> adding that month's partition with the same DDL
  lets the INSERT through (= a month boundary is the same code path with a different key).
- **D**: with `audit.enabled=false` (re-running mkfs without `--audit`) not a single row is recorded (the table
  itself is always created).

> **A note on crossing a month**: `occurred_at = DateTime.Now` cannot be pushed into a future month in real
> time, so the essential mechanism of "ensure the current month's partition before the op if it is not there" is
> verified deterministically by C1 (the automatic creation of the current month) plus C2 (an INSERT succeeding
> once an arbitrary month is added). A real calendar month boundary simply runs the same
> `EnsureAuditPartition` with the next month's key.

**Completed**: `bash tests/citus/audit.sh` was run on the multi-node Citus on docker on the Linux
client and PASSed **12/12**. The Citus same-tx commit (the 2PC risk) was demonstrated at the same time, once A,
B and C held on multi-node Citus. With that, **the audit log is complete in its feature, its regressions and its
dedicated tests (completely closed)**.
