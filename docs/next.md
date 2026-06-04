# What's next

A list of what to work on next. Update it and include the change in the commit whenever a piece of work completes.

**Duplication is allowed (this file only)**: it may summarize and restate content from other documents. Keep it short if a link suffices; write it out when judgment material is needed.

For the Japanese version see [next.ja.md](next.ja.md).

---

## 🟢 Feature additions (in the requirements, not yet implemented)

| # | Item | Content | Size |
|---|---|---|---|
| 1 | **ACL / permission interop between Linux↔Windows** (main goal met, items remain) | The source of truth is [docs/permission-interop.md](permission-interop.md) / the diagram is [permission-interop-diagram.html](permission-interop-diagram.html). PGFS is a name-based ACL store with POSIX mode canonical and Windows a projection view. Name handling (normalization [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs) / domain stripping / principal mapping / `user.win.attrs` JSON / ReadOnly via st_mode) and the ACL body (canonical model [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs), Windows `GetFileSecurity`/`SetFileSecurity` projection, Linux POSIX ACL [PosixAcl](../src/lib/src/Models/PosixAcl.cs) `system.posix_acl_access` ⇄ mode + `user.pgfs_acl`) are implemented + regression-tested. **Remaining**: strict enforcement of named ACLs (once required), Windows inheritance translation of `system.posix_acl_default`, automated cross-OS round-trip tests | medium |
| 2 | **Junction** (Assign side) | The `pgfs_inode.is_junction` column already exists, and the Linux side substitutes a symlink. Implement junction create / resolve / delete in IDokanOperations on the Windows side | medium |
| 3 | **ADS (Alternate Data Streams)** (Windows) | NTFS-compatible `:streamname` via Dokan. Needs a data-model extension | large |

## 🟡 Operations & verification

For the full test list / environment requirements / docker-integration analysis, see [docs/tests.md](tests.md).

| # | Item | Content |
|---|---|---|
| 4 | **docker integration of the tests** (single-PG done / multi-node remaining) | The full-docker Linux e2e for a single PG is done ([tests/docker/](../tests/docker/README.md)). Remaining is extending multi-node Citus + race + audit into the same harness (`race_multinode.sh` is the template). The Windows e2e is out of scope because Dokan is a kernel driver. Analysis in [docs/tests.md §docker integration](tests.md#docker-integration). |
| 5 | **Windows e2e on multi-node Citus** | The remaining cross-client locking verification ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) ran the Linux side only. A slot to run the Windows e2e over docker Citus is not built yet |
| 6 | **harness output issue with Linux flow.cmd** | The PowerShell tool's `Write-Host` does not reach stdout when a long run is backgrounded. Rewriting flow.ps1 to favor `Write-Output`, or using the `*-Information` path, makes it visible in the harness |

## 🔵 Code quality & conventions

| # | Item | Content | Size |
|---|---|---|---|
| 7 | **follow coding-style v4** | ~20 `else` clauses + ~20 ternaries, ~40 sites total. For rewrite patterns see `FirstList.cs` (switch expression, tuple decomposition, Try* extraction) and [Api.cs](../src/lib/src/Api/Api.cs) (bracing the Logger guards). Details in [coding-style.md](coding-style.md) | small per file |

## 🟣 Performance — [docs/performance.md](performance.md)

| # | Item | Content |
|---|---|---|
| 8 | **measure path-traversal cross-shard hops** | The traversal cost of `parent_id` distribution on multi-node Citus. Measure including the InodeCache hit rate |
| 9 | **measure bytea partial read** | Whether PG 13+ partial TOAST detoast works as expected. Room to raise chunk_size |
| 10 | **enable FUSE `writeback_cache`** | write-through → write-back. Reduces PG round-trips for small sequential writes. Verify `attr_timeout=0` / notify consistency / the crash-loss window. [performance.md](performance.md) #8 |

## ⚪ Future considerations (design-record level, low priority)

| # | Item | Content |
|---|---|---|
| 11 | **split out `pgfs_inode_lock`** | Locking starts with a single `pgfs_lock` + a signed namespace. Split into a separate table once an inode-lock-dominant workload shows asymmetric cost ([support_for_citus.md §locking](support_for_citus.md)) |
| 12 | **consider multi-coordinator HA Citus** | Whether to bring Enterprise Citus into view |
| 13 | **distribute `pgfs_inode` by id** | An alternative that guarantees the `(parent_id, name)` UK in the application layer. Reconsider if cross-shard rename cost becomes a problem |

---

## Already in (only items related to this list)

Keep only the completed items that are the premise / surroundings of this list. For the background design decisions, see [history.md](history.md).

- **Mount option `-o`**: classifies `-o key=val,flag,...` via [ConfigLoader.ParseDashOOptions](../src/lib/src/Config/ConfigLoader.cs) (FUSE passthrough / accept-and-ignore + `x-` prefix / pgfs setting / unknown=Warning / unsupported mount operation=explicit Warning). Details in [Mount.md §Mount options](Mount.md). Assign-side `-o` is future work.
- **transparent bytea xattr**: replaced `pgfs_inode.xattrs JSONB`+Base64 with the `xattr_names TEXT[]` + `xattr_values BYTEA[]` parallel arrays, holding values faithfully as bytea (NUL/high bytes round-trip unmodified; the encode heuristic is gone). Details in [xattr-bytea.md](xattr-bytea.md). **→ premise of feature addition #1 (ACL)**
- **df support (statfs real free space)**: `df` reports the real disk free space of the tablespace. A plain plperlu `CREATE FUNCTION` + `SECURITY DEFINER` (mkfs stays pure-SQL; Citus uses `run_command_on_all_nodes`/`_workers`), a three-tier fallback, mkfs `--statfs <auto\|require\|nominal>` (`app.statfs`, `SaveTo=Db`). Citus multi-worker aggregation + a dedicated test ([statfs.sh](../tests/citus/statfs.sh)) verified. Source of truth: [df-support.md](df-support.md).
- **settings-scope rework + plperlu gate + Citus custom tablespace**: new scope `app` + `app.plperlu` (the plperlu upper gate), the statfs mode under `app.statfs`, `database.citus`/the `file_system` size keys made DB-authoritative (not emitted to the generated toml), the Citus×custom-tablespace prohibition lifted + plperlu auto-mkdir. Source of truth: [settings-and-plperlu.md](settings-and-plperlu.md); cross-cut: [settings-matrix.md](settings-matrix.md).
- **auto-generated help**: the hand-written `ShowHelp` is retired; [HelpText.Build](../src/lib/src/Config/HelpText.cs) walks [Schema.AllFields](../src/lib/src/Config/Schema.cs) to assemble `--help`. Per-tool selection is via [Field.AppliesTo](../src/lib/src/Config/Field.cs).
- **`Field` self-check**: [ConfigLoader.Resolve](../src/lib/src/Config/ConfigLoader.cs) records the Fields it touches, and [ConfigLoader.UnresolvedFields](../src/lib/src/Config/ConfigLoader.cs) puts the diff against `Schema.AllFields` (= forgotten wiring) into a startup Warning.
- **single-parse of CLI args**: unified mount/assign's `BuildRootConfig`, which parsed CLI/TOML twice with two Loaders (lite+full), into reusing one Loader + a Phase-3 retrofit via [ConfigLoader.WithStore](../src/lib/src/Config/ConfigLoader.cs) (CLI/TOML parsed once).
- **docker integration of the tests (single PG)**: [tests/docker/](../tests/docker/README.md) runs the Linux e2e inside two containers — `coord` (postgres) + `mount` (FUSE mount). No ssh / host dotnet dependency. **→ premise of operations #4**
- **UserResolver cache**: both OS resolvers are already cached with bidirectional `ConcurrentDictionary` + `GetOrAdd`. `getpwnam`/`getgrnam`/`getpwuid`/`getgrgid` are called once per (key, result). Details in [performance.md](performance.md) #1.
- **Linux↔Windows permission/ACL interop**: PGFS is a name-based ACL store with POSIX mode canonical and Windows a projection view. Name handling and the ACL body path are implemented + regression-tested. Details in [permission-interop.md](permission-interop.md). **→ premise of feature addition #1**
- **Citus support** (distribution + cross-client locking): Large Object → bytea, `mkfs --citus [--worker ...]`, `pgfs_lock` + `SELECT FOR UPDATE` locking, Linux e2e + concurrent write/mkdir race + lock accumulation validity on multi-node Citus. Details in [support_for_citus.md](support_for_citus.md), verification in [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh). **→ premise of performance #8/#9**
- **configuration model**: removed the old `*Settings.cs` tree, unified on `Pgfs.Lib.Config` (`Field<T>` + `ConfigLoader` + `ConfigStore`). Details in [architecture.md §Config](architecture.md).
- **audit log**: records chmod / chown / delete / rename / create / hardlink into the dedicated table `{prefix}audit` (`occurred_at` monthly RANGE partitions), one operation = one row, in the same tx as the operation. on/off is `audit.enabled` (initialized by mkfs `--audit`). Details in [audit-log.md](audit-log.md).
- **Notify (LISTEN/NOTIFY)**: cross-client change notification ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs)), `database.notify_enabled` opt-in. **→ premise of multi-client operation**
- **connection retry**: [Retry.cs](../src/lib/src/Utility/Retry.cs) retries transient failures (PG restart / network blip) with exponential backoff. **→ operational premise**
- **uname/gname fallback**: mount.fallback_uname / fallback_gname, defaults nobody/nogroup.
- **/etc/fstab support**: positional args / `-o` parser / automatic daemonization / verified via `sudo mount -t pgfs` + **boot-time mount (fstab line + reboot) also verified on real hardware**. Details in [fstab-support.md](fstab-support.md).
- **log output destination**: `logging.output = "daily:~/pgfs/log/pgfs-*.log"` and rotation wired into `Logger.Output` via [LogSink](../src/lib/src/Logging/LogSink.cs) + [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs).
- **`ConfigLoader.Warnings`**: pushes unknown options (typos such as `--cutus`) into `Warnings` and logs them at startup. Also a `param: scope.key=value` dump of the interpreted parameters (Password masked) and a terse stderr output at Warning level and above.
- **Tmds.Fuse fork**: `attr_timeout=0` / `use_ino` / `allow_other` via the `securefolderfs-community/Tmds.Fuse` fork. Details in [fstab-support.md](fstab-support.md).
