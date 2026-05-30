# What's next

A list of what to work on next. Update it and include the change in the commit whenever a piece of work completes.

**Duplication is allowed (this file only)**: it may summarize and restate content from other documents. Keep it short if a link suffices; write it out when judgment material is needed.

---

## 🟢 Feature additions (in scope but not yet implemented)

| # | Item | Description | Size |
|---|---|---|---|
| 1 | **Mount option `-o`** | full support for mount.pgfs's `-o key=val,flag,...` on the Linux side (room to consider on the Assign side too). The core parser is already in [ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs), so what remains is "tidying unknown-key warnings" and "a compatibility map for typical mount options like `-o ro`" | small–medium |
| 2 | **xattr byte transparency** | currently via Base64 inside JSONB ([Api.cs](../src/lib/src/Api/Api.cs) `EncodeXattrValue`/`DecodeXattrValue`). A branch for arbitrary binary xattrs (e.g. SELinux) + added test cases | medium (Schema change) |
| 3 | **ACL persistence** (Windows) | put DokanNet's `GetFileSecurity` / `SetFileSecurity` onto `pgfs_inode.xattrs` (or a separate column). Currently `NotImplemented` (see the [docs/Assign.md](Assign.md) TODO table) | medium |
| 4 | **Junction** (Assign side) | the `pgfs_inode.is_junction` column already exists, and the Linux side substitutes a symlink. Implement junction create / resolve / delete in IDokanOperations on the Windows side | medium |
| 5 | **ADS (Alternate Data Streams)** (Windows) | NTFS-compatible `:streamname` via Dokan. Needs a data-model extension | large |

## 🟡 Operations / verification

For the full test catalog / environment requirements / docker-integration analysis, see [docs/tests.md](tests.md).

| # | Item | Description |
|---|---|---|
| 6 | **`/etc/fstab` boot-time mount** manual verification | confirm on real hardware that an `/etc/fstab` line + reboot / `sudo mount -a` mounts automatically (the core implementation is done; details in [fstab-support.md](fstab-support.md)) |
| 7 | **docker test integration** | move host dependencies (ssh linux_client / pgsql_server) onto docker for reproducibility. The Linux e2e + Citus suites can be fully dockerized (`race_multinode.sh` is a template); the Windows e2e is out of scope since Dokan is a kernel driver. Analysis in [docs/tests.md §docker integration](tests.md#docker-integration) |
| 8 | **Windows e2e on multi-node Citus** | the cross-client locking verification ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) ran only the Linux 34/34. A slot to run the Windows 24/24 via docker Citus is not yet created |
| 9 | **Linux flow.cmd harness output issue** | a PowerShell tool's `Write-Host` does not reach stdout when a long run is backgrounded. Rewriting flow.ps1 around `Write-Output`, or using the `*-Information` channel, makes it visible in the harness |

## 🔵 Code quality / conventions

| # | Item | Description | Size |
|---|---|---|---|
| 10 | **Help auto-generation** | walk `Schema.AllFields`'s `CliOptions` / `Comment` to assemble the `--help` text. Retire the hand-written help ([src/mount/src/Program.cs](../src/mount/src/Program.cs) `ShowHelp`) | small |
| 11 | **`Field` self-check** | at startup, log a warning for the diff between `Schema.*` declarations and `ConfigLoader.Resolve` references (scan `Schema` by reflection and compare) | small |
| 12 | **CLI argument double-parse** | BuildRootConfig parses the CLI twice (lite + full). There is room to unify it in one pass with a fluent API like `ConfigLoader.WithStore(store)` | medium |

## 🟣 Performance — [docs/performance.md](performance.md)

| # | Item | Description |
|---|---|---|
| 13 | **UserResolver cache** | cache the uid/gid ↔ uname/gname resolution at the sites calling `getpwnam` each time |
| 14 | **path traversal cross-shard hop measurement** | the traversal cost of `parent_id` distribution on multi-node Citus. Measure including the InodeCache hit rate |
| 15 | **bytea partial read measurement** | whether PG 13+'s partial TOAST detoast works as expected. Room to raise chunk_size |

## ⚪ Future consideration (design-record level, low priority)

| # | Item | Description |
|---|---|---|
| 16 | **`pgfs_inode_lock` separation** | locking starts with a single `pgfs_lock` + sign namespace. Split into a separate table if inode locks become the dominant workload with asymmetric cost ([support_for_citus.md §locking](support_for_citus.md)) |
| 17 | **multi-coordinator HA Citus** | consider whether to bring Enterprise Citus into view |
| 18 | **distribute `pgfs_inode` by id** | an alternative guaranteeing the `(parent_id, name)` UK in the application layer. Reconsider if cross-shard rename cost becomes a problem |

---

## Already in place (those relevant to this list)

Only the recently completed items that this list's premises or surroundings depend on. For the design decisions behind them, see [history.md](history.md).

- **Citus support** (distribution + cross-client locking): Large Object → bytea, `mkfs --citus [--worker ...]`, `pgfs_lock` + `SELECT FOR UPDATE` locking, Linux e2e 34/34 + concurrent write/mkdir race + reasonable lock accumulation on multi-node Citus. Details in [support_for_citus.md](support_for_citus.md), verification in [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh). **→ premise for performance #14 / #15**
- **Config model**: the old `*Settings.cs` tree was removed and unified into `Pgfs.Lib.Config` (`Field<T>` + `ConfigLoader` + `ConfigStore`). Details in [architecture.md §Config](architecture.md). **→ premise for code-quality #10/#11/#12**
- **Audit log**: chmod / chown / delete / rename / create / hard link recorded into a dedicated table `{prefix}audit` (monthly RANGE partition by `occurred_at`), 1 operation = 1 row, in the same transaction as the operation. On/off via `audit.enabled` (initialized with mkfs `--audit`). Details in [audit-log.md](audit-log.md).
- **Notify (LISTEN/NOTIFY)**: cross-client change notification ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs)), `database.notify_enabled` opt-in. **→ premise for multi-client operation**
- **Connection retry**: [Retry.cs](../src/lib/src/Utility/Retry.cs) retries transient failures (PG restart / network blip) with exponential backoff. **→ operational premise**
- **uname/gname fallback**: mount.fallback_uname / fallback_gname, default nobody/nogroup.
- **`/etc/fstab` core support**: positional arguments / `-o` parser / automatic daemonization / verified via `sudo mount -t pgfs`. **→ premise for operations #6** (only the boot-time mount real-hardware check remains).
- **Logging output**: `logging.output = "daily:~/pgfs/log/pgfs-*.log"` and rotation wired into `Logger.Output` via [LogSink](../src/lib/src/Logging/LogSink.cs) + [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs).
- **`ConfigLoader.Warnings`**: unknown options (typos like `--cutus`) are collected into `Warnings` and logged at startup, plus a `param: scope.key=value` dump (Password masked) and terse stderr output for Warning and above.
- **Tmds.Fuse fork**: `attr_timeout=0` / `use_ino` / `allow_other` via the `securefolderfs-community/Tmds.Fuse` fork. Details in [fstab-support.md](fstab-support.md).
