# What to do next

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **what to start next** (the punch list) and a one-line
> statement of where things stand.
> **It does not hold the details of what is finished** - only an index of where to go, in
> [the completed items](#the-completed-items-links-only).
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [README.md](README.md) | **The index of every document.** The entrance is here |
> | [history.md](history.md) | The archive of **how** the completed items with no document of their own came about |
> | [tests.md](tests.md) | The test counts, how to run them and the suites' side effects |
> | Each `design/*.md` | The design and the as-built per feature. **What comes off the punch list moves there** |

## Where things stand (in a line)

**v0.2.0 (the Core/Fuse/Dokan split plus the in-house FUSE binding) is implemented**, and
**the operations phase is implemented apart from the Phase 5 GUI and the 1c read-ahead**. The caches (1a/1b),
the data write-back (1d), the metadata write-back (1e, stages 1 and 2 plus every finding of review rounds A and
B), the foundation (2a-2c), config (Phase 3) and status Layers 1-3 (Phase 4) are all green on real hardware, and
**Phase 5's GUI is done as far as 5a/5b (5c/5d remain)**.

[handle-context.md](design/handle-context.md) is **complete through stages A to C**, and the 3 items around
`O_APPEND` plus the rewinding of `st_size` are closed too. **No test is left un-re-run** - the 9 Linux suites,
the 7 Windows suites, the docker runner and the 2 Citus suites have all been run against the current HEAD
([tests.md](tests.md) is the source of truth for the counts).

---

## v0.2.0 - the in-house FUSE binding plus splitting the library by OS layer

[v0.2.0-plan.md](design/v0.2.0-plan.md) is the source of truth. It is the version after v0.1.0.
**Implemented, with the e2e suites green on both operating systems.**

| The phase | The status | What it is |
|---|---|---|
| Decision 1, the in-house FUSE binding | ✅ Agreed | Hold a minimal P/Invoke to libfuse of our own and retire the `vendor/Tmds.Fuse` submodule and fork (Tmds.Fuse is credited) |
| Decision 2, splitting the library | ✅ Agreed | `Pgfs.Core` (the shared SQL, with zero OS `#if`s) plus `Pgfs.Fuse` (Linux/libfuse) plus `Pgfs.Dokan` (Windows/Dokan), named after the mechanisms |
| **A, the design** (the open points, the naming, where things move) | ✅ **Closed** | The 7 open points plus the naming convention plus where each `.cs` moves, settled against the real thing |
| **B, the design of the in-house FUSE binding** | ✅ **Closed** | [fuse-binding.md](design/fuse-binding.md) is the source of truth. The whole public libfuse3 API against pgfs's usage (by verb) plus the struct surface plus the 6 settled design judgements |
| **The implementation plus the e2e suites on both operating systems** | ✅ **Green (2026-06-07)** | The 4-way Core/Fuse/Dokan split plus the in-house libfuse binding plus retiring the vendor submodule. **The full Linux e2e suite 36/36 plus the Windows Dokan suite 27/27 PASS**, with the whole solution green on Windows |
| **The post-green cleanup** | ✅ Mostly applied | The typed fuse_config (retiring the offset-64 poke) / KEEPCACHE (3->4) / moving PosixAcl into Fuse, **re-verified green on both operating systems (Linux 36/36)**. The symlink is correct by design and unchanged. Remaining: splitting `ServiceResolver` with `#if` (optional) plus macOS |

The main decisions of A (the details and the grounds are in [v0.2.0-plan.md](design/v0.2.0-plan.md)):
- **The dependencies run one way**: `the tool exe -> the mechanism library (Fuse/Dokan) -> Core -> PG`. The tool
  executables (`mkfs.pgfs` / `mount.pgfs` / `assign.pgfs`) are "the adapters to the OS's calling conventions"
  (the argv convention, the daemonization, the install name).
- **The Linux layer is named `Pgfs.Fuse`** (after the mechanism, symmetrically with Dokan; `Pgfs.Posix` was not
  taken).
- **The Windows-OS-common parts** (`WindowsUserResolver` / `FileSystemUtils` / the win attrs) live with
  `Pgfs.Dokan` for now, and everything but `FileSystem.cs` would be extracted into `Pgfs.Windows` if WinFsp were
  added (YAGNI).
- **The naming convention**: the `AssemblyName` is the lowercase `{role}.pgfs` and the `RootNamespace` is the
  Pascal `Pgfs.{Role}`.
- **The heart of the OS branching**: the only compile-time `#if WINDOWS` is `ServiceResolver.cs` (the native
  getservbyname = ws2_32 against libc). The Schema's default mount point and the ConfigLoader's helper context
  are left as runtime `OperatingSystem.Is*` (benign).

### What remains of v0.2.0 (all optional, none urgent)

1. **Splitting `ServiceResolver` with `#if WINDOWS`** - turning the native `getservbyname` (ws2_32 / libc) into
   an interface injected by each mechanism layer. A purity improvement to make Core truly "zero OS `#if`s" (it
   is not mandatory, since the feature is complete through the per-OS compilation). For the details see
   [fuse-binding.md, the implementation status](design/fuse-binding.md) /
   [v0.2.0-plan.md](design/v0.2.0-plan.md).
2. **macOS support** (the libfuse family = macFUSE / fuse-t). It can live in `Pgfs.Fuse`.

## Feature additions (in the requirements but unimplemented)

| # | The item | What it is | The scale |
|---|---|---|---|
| 4 | **The Linux-Windows interoperability of the ACLs and the permissions** - the main purpose is achieved; only what remains | [permission-interop.md](design/permission-interop.md) is the source of truth for the design and [permission-interop-diagram.html](design/permission-interop-diagram.html) for the diagram. The 5 immediate items plus the ACL proper 3-0 to 3-3 (the canonical model [PgfsAcl](../src/core/src/Models/PgfsAcl.cs) / the Windows read and write projection / the Linux POSIX ACL [PosixAcl](../src/fuse/src/PosixAcl.cs)) are complete and regressed. **Remaining**: converting the Windows inheritance of `system.posix_acl_default`, and an automated test of the cross-OS round trip. 3-4 (the strict enforcement of a named ACL) is held back awaiting a requirement ([permission-interop.md, re-evaluating Phase 3-4](design/permission-interop.md)) | Only what remains |
| 5 | **Junctions and native links** (the Assign side) | The `pgfs_inode.is_junction` column already exists and the Linux side substitutes a symlink. **A reachability PoC was done on 2026-09-19 = with the current binding neither creating nor reading works**: `IDokanOperations2` has no link callbacks and there is no entry point for getting or setting the reparse data. Measured, `mklink /H`, `/J`, `/D` and `File.CreateSymbolicLink` all fail, and **a symlink of Linux origin disappears from the enumeration and opens as an empty file when named directly**. -> **An addition to DokanNet / Dokany or a switch to WinFsp is a precondition.** [windows-parity.md, the reachability PoC for native links](design/windows-parity.md) is the source of truth | Large (it involves changing the binding or the backend) |
| 6 | **ADS (Alternate Data Streams)** (Windows) | The NTFS-compatible `:streamname` through Dokan. It needs the data model extended | Large |

## The runtime control plane plus the caches

The design binding the 5 themes of "the operations phase" into one. The hub is
[runtime-control-plane.md](design/runtime-control-plane.md). The decisions: (1) the control channel is
**gathered in the database (NOTIFY plus the `{prefix}mounts` registry)`**, and (2) the order of work puts
**the caches first**.

| The phase | The theme | The status |
|---|---|---|
| 1 | (1) The caches (1a the capacity/LRU wiring -> 1b the read cache plus the data-write NOTIFY -> 1c read-ahead -> 1d write-back -> 1e the metadata write-back) | **1a plus 1b plus 1d complete (all green in the e2e suite on real hardware)**. 1d = `mount.write_back` (off by default), measured at **6.1x with `dd bs=128k` and 1.4x with rsync**. **1e stages 1 plus 1.5 plus 2 complete and green on real hardware in both modes** (2026-08-12; `mount.write_back_metadata` is off by default; the as-built is in [metadata-write-back.md](design/metadata-write-back.md)). Stage 2 = close-no-flush plus the 3 synchronization heuristics plus the 4-level floor of the errors plus the two-phase flip. **The automated tests are wbmeta.sh, all green.** 1c (read-ahead) has not been started |
| 2 | The foundation (the NOTIFY control messages plus the `{prefix}mounts` registry plus the Field reload policy Live/NextMount/Format) | **2a/2b/2c green in the e2e suite on real hardware.** An immediate reload applies to the database-stored Live fields (audit/statfs); the file-stored Live ones are Phase 3 |
| 3 | (3) The `config` subcommand (plus (2) the live application) | **✅ Complete and green in the e2e suite on real hardware.** 3a the always-on control LISTEN / 3b a single `pgfsctl` plus Core's `ConfigAdmin` with config get/list/set --json / 3c the live application verified on real hardware against a notify-OFF mount |
| 4 | (4) The `status` subcommand (the read-only database-derived subset can go first) | **Layers 1+2+3 all complete and green in the e2e suite on real hardware.** `pgfsctl status [--json]` = the list of what is running in the cluster plus the FS statistics (Layers 1+2) plus the running processes' cache statistics plus their effective settings (Layer 3, populating `{prefix}mounts.stats` / `config` from the heartbeat snapshot). [control-plane.md](design/control-plane.md) is the source of truth |
| 5 | (5) The GUI (a wrapper around (3) and (4)) | **Avalonia**, calling Core (StatusAdmin/ConfigAdmin) in-process directly, with minimal dependencies (plain MVVM). **5a the skeleton plus 5b the read-only dashboard MVP are implemented (the build is green; the visual check is manual)** - the connection bar / Mounts (L1) / Filesystem (L2) / Process detail (L3) / the Config list, with a 3 s poll plus a Refresh that pings. **Remaining: 5c config set / 5d the finishing touches.** [gui.md](design/gui.md) is the source of truth |

**The next move**: **implementing the Phase 5 GUI** (Avalonia; the MVP is the read-only dashboard). The GUI does
not ride on the docker e2e suite so **the visual check is manual** - start it with
`dotnet run --project src/gui/Gui.csproj`, Connect to PG and see whether Mounts / Filesystem / Process detail /
Config appear (seeing Layers 1 and 3 needs one mount running; Layer 2 and Config appear with no mount).
**Remaining: 5c** (a live config set from the Config screen) -> **5d** (the connection dialog, the error display,
the publish settings). [gui.md](design/gui.md) is the source of truth.

**Candidates after or alongside Phase 5**:
1. **Measuring the path traversal and the bytea partial read** ([performance.md](design/performance.md)) - Layer
   3's cache hit rate is the footing for the measurement.

## Operations and verification

For the whole list of the tests, the environment requirements and the current analysis of the docker
consolidation see [docs/tests.md](tests.md).

| # | The item | What it is |
|---|---|---|
| 8 | **Consolidating the tests on docker** (a single PG ✅ / multi-node is merged with #9) | Fully dockerizing the Linux e2e suite on a single PG is complete ([tests/docker/](../tests/docker/README.md)). **Remaining**: extending multi-node Citus plus the race plus the audit into the same frame (`race_multinode.sh` is the template). The Windows e2e suite is out of scope because Dokan is a kernel driver. The analysis is in [docs/tests.md, the consideration of consolidating on docker](tests.md#the-consideration-of-consolidating-on-docker) |
| 9 | **The Windows e2e suite on multi-node Citus** | The remaining Phase 3 verification ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) ran only the Linux suite. A frame for running the Windows suite through a docker Citus has not been made |
| 10 | **The harness output problem of flow.cmd** (partly relieved) | The issue of a PowerShell tool's `Write-Host` not reaching stdout when a long run is backgrounded. **`flow.cmd` / `run.cmd` were changed to prefer pwsh on 2026-09-19**, and it was confirmed that all the output can be captured through a redirect (`> log 2>&1`) (with the side benefit of resolving the 3 false FAILs from PS 5.1 failing to load `Get-Acl` / `Get-FileHash`). **Remaining**: rewriting the `Write-Host`s into `Write-Output` / `*-Information` has not been started |
| 22 | **What remains of unifying the timestamps on UTC** (the substance is ✅) | The `TIMESTAMP` columns are unified on always storing UTC ([database.md, the timestamp convention](design/database.md) / the regression test `test_timestamp_utc_roundtrip`). **Remaining**: (1) **migrating an existing FS created on a non-UTC host** (replacing the column DEFAULTs plus shifting the existing rows; the procedure is in database.md. An FS created on docker has an offset of 0 so only the DEFAULTs are needed); (2) the Linux equivalent of `test_timestamp_utc_roundtrip` (a round trip on a non-UTC host) does not exist on the Windows side, where only the `SetFileTime` round trip was watched; (3) running the new tests on the docker runner (the `CACHE_MAX_ENTRIES=8` variant) to make the database read-back path green too |
| 24 | **What remains of the Citus exclusion and the rf** (the substance is ✅) | `{prefix}lock` was made a Citus local table to gather the row locks in one place and make it rf-independent ([support_for_citus.md, the exclusion and the replication factor](design/support_for_citus.md)). `--shard-count` / `--rf` / `--distribute-existing` were added to mkfs. **Turning the inode UPDATEs into router queries is complete too** (including the distribution key in the WHERE took the Task Count from 8 to 1 and resolved the distributed deadlock). **Remaining**: (1) ~~the FUSE `max_write`~~ -> **it came to nothing** (libfuse3 already negotiates up to the kernel limit of 1 MiB by default; it is implemented as a knob with a default of 0). **The root of the slowness was corrected**: it is not "the workers' commit latency x the number of 2PC participants" but **the read-modify-write amplification of the chunk rows** (a contribution of 7.5x, with the number of commits contributing 17%). The same amplification happens on a single PG ([performance.md](design/performance.md)). What works, in order, is **Phase 1d's write-back (a projected 6.5 to 8.1x)** -> `writeback_cache` (on the kernel side) -> the workers' `synchronous_commit`; (2) **running `tests/citus/race_multinode.sh` and `audit.sh` at rf>=2**; (3) the 3 places with `WHERE data_id` (recomputing a hardlink's nlink) are multi-shard in principle = there is room for a deadlock in concurrent hardlink operations; (4) measuring the ceiling at which the locks concentrating on one row on the coordinator becomes a problem (0.11 ms per lock today); (5) the room to switch to the reference-table version |
| 25 | ~~**The 40P01 flake of `test_concurrent_writes_diff_files`**~~ -> ✅ **cured at the root** | The cause = under the per-shard serialization at rf>=2, a write tx **grabbing the inode shard with the leading `UPDATE inode SET data_id` and then waiting for the chunk/data** (a hold-and-wait, identified by measurement with `citus_lock_waits`). The fix = (1) **normalizing the shard-touch order** (folding the data_id link to the end of the tx and unifying on chunk/data -> inode; the deadlocks went down about 99%) plus (2) **a bounded retry of 40P01/40001 for create/write/flush** (absorbing the rest). The verification = 180 rounds of the reproduction loop with 0 fails plus the e2e suite green in both modes. The convention and the details are in [support_for_citus.md, the shard-touch order of a write tx](design/support_for_citus.md) / [tests.md, the cured flake](tests.md). **The theoretical cycles that remain** (the reverse order of DeleteInode, the multi-shard UPDATE of a hardlink) are unobserved and the retry catches them |
| 26 | **What remains of the write-back (Phase 1d)** (the substance is ✅) | [write-back.md](design/write-back.md) is the source of truth. **Remaining**: (1) a `WRITE_BACK` injection point in the docker runner (`tests/docker/run.sh`); (2) an automated test that `-EIO` is returned when a flush fails (the path is implemented; it needs a way to bring the database down); (3) a cross-client last-flush-wins test; (4) batching across files (`write_back_batch_files`) is **deliberately unimplemented** (the extra gain of +8.8% does not pay for the wider loss window and the longer lock hold) |
| 23 | **What remains of the occupied bytes and `st_blocks`** (the substance is ✅) | `pgfs_data.total_size` is maintained as the occupied bytes and reflected in the `st_blocks` (= `du`) ([database.md, the occupied bytes and st_blocks](design/database.md) / the regression test `test_sparse_du_blocks`). **Remaining**: (1) **backfilling an existing FS** (`du` gives 0 while the `total_size` stays 0; the SQL is in database.md); (2) **the Windows (Dokan) side is unsupported in principle** - **a PoC was done on 2026-09-19: with the current binding there is no path to return it**. The output structs (`ByHandleFileInformation` / `FindFileInformation`) have no slot for an AllocationSize and the driver synthesizes it from the EOF (measured: a sparse file occupying 1 byte was declared as 8,389,120). Resolving it needs an addition to DokanNet / Dokany or a switch to WinFsp. [windows-parity.md, the reachability PoC for AllocationSize](design/windows-parity.md) is the source of truth |

## Code quality and the conventions

| # | The item | What it is | The scale |
|---|---|---|---|
| 11 | **Following coding conventions v4** | About 40 places in total: roughly 20 `else` clauses and 20 ternary operators. The rewriting patterns are in `FirstList.cs` (switch expressions, tuple deconstruction, extracting Try*) and [Api.cs](../src/core/src/Api/Api.cs) (giving the logger guards braces). For the details see [coding-style.md](design/coding-style.md) | Small, per file |

## Performance improvements

[performance.md](design/performance.md) is the source of truth.

| # | The item | What it is |
|---|---|---|
| 17 | **Measuring the cross-shard hops of a path traversal** | The traversal cost of distributing on `parent_id` on multi-node Citus. Measured including the InodeCache hit rate |
| 18 | **Measuring the bytea partial read** | Whether PG 13+'s partial TOAST detoast works as expected. The room to raise the chunk_size |
| 19 | **The set of 3 for reducing the txs on the write path** ([performance.md](design/performance.md) is the source of truth) | (1) ~~a negative lookup cache~~ -> ✅ **implemented**: `mount.negative_cache_ttl_ms` (0 = disabled by default, live reloadable). Measured at **-3.2 tx/file and 1.08x** (an rsync of 4 KB x 300; what it can remove is only the second and later lookups of the same name, and the first ENOENT of about 3.4 per file remains in principle). The test is [tests/linux/negcache.sh](../tests/linux/negcache.sh) 7/7; (2) ~~identifying the about 14 unidentified tx/file~~ -> ✅ **explained**: most of it was not hidden queries but **Citus's 2PC being double-counted in the xact_commit** (confirmed against raw SQL). The real txs are about 11 to 12 per file; (3) **the flush granularity of the back-pressure** (a 64 MB file breaks into about 1.2 tx/MiB; revisiting the default of `write_back_max_bytes` / batching the flush) - not started |

## Future considerations (at the level of a design record, low priority)

| # | The item | What it is |
|---|---|---|
| 19 | **Separating out `pgfs_inode_lock`** | Phase 3 started with a single `pgfs_lock` plus the sign namespace. If the asymmetric cost ([support_for_citus.md, the Phase 3 caveats](design/support_for_citus.md)) shows up in a workload dominated by inode locks, it becomes a separate table |
| 20 | **Considering multi-coordinator HA Citus** | Whether Enterprise Citus is in scope |
| 21 | **Distributing `pgfs_inode` on the id** | The alternative of guaranteeing the `(parent_id, name)` UK at the application layer. To be reconsidered if the cost of a cross-shard rename becomes a problem |

---

## The completed items (links only)

**An index of where to go** for the completed items that have recently been the premises or the surroundings of
this list. The linked documents are authoritative for the details and the verification results. For the older
history see [history.md](history.md).

| The completed item | The authoritative document |
|---|---|
| **v0.2.0** (the Core/Fuse/Dokan split plus the in-house libfuse binding) | Above / [v0.2.0-plan.md](design/v0.2.0-plan.md) / [fuse-binding.md](design/fuse-binding.md) |
| The Mount `-o` support | [Mount.md, the mount options](Mount.md) |
| The byte-string transparency of the xattr | [xattr-bytea.md](design/xattr-bytea.md) |
| The ACL and permission interoperability 3-0 to 3-3 | [permission-interop.md](design/permission-interop.md) (what remains is #4 above) |
| The df support plus the Citus multi-worker aggregation | [df-support.md](design/df-support.md) |
| Reorganizing the settings scopes plus the plperlu gate plus the tablespace | [settings-and-plperlu.md](design/settings-and-plperlu.md) / [settings-matrix.md](design/settings-matrix.md) |
| Consolidating the single-PG configuration on docker | [tests/docker/](../tests/docker/README.md) (what remains is #8 above) |
| Mounting at boot through /etc/fstab | [fstab-support.md](design/fstab-support.md) |
| Finishing off the Config work (the automatic help, the Field self-check, removing the double parse) | [history.md, finishing off the Config work](history.md) |
| The UserResolver cache | [performance.md](design/performance.md) |
| The audit log / Citus Phases 1+2+3 / the model tidy-up / Notify, retry, the fallback and the rest | [history.md](history.md) / [audit-log.md](design/audit-log.md) / [support_for_citus.md](design/support_for_citus.md) |
| The settled design of the metadata write-back (1e) and stages 1, 1.5 and 2 | [metadata-write-back.md](design/metadata-write-back.md) |
| The 1e reviews, round A (A-1 to A-10) and round B (B-1 to B-13) | [metadata-write-back-reviews.md](design/metadata-write-back-reviews.md) |
| The Windows wiring of B-12 (the second stop signal abandoning the flush) | [metadata-write-back-reviews.md](design/metadata-write-back-reviews.md) |
| The design and the measurements of the data write-back (1d) (`dd bs=128k` 6.1x) | [write-back.md](design/write-back.md) |
| Fixing the lifecycle of `data_id` (the hardlink sharing, the stability of `st_ino`) | [data-id-lifecycle.md](design/data-id-lifecycle.md) |
| Unifying the handle context (stages A to C) plus the append contract | [handle-context.md](design/handle-context.md) / [Mount.md](Mount.md) |
| Raising the Windows implementation (the exclusive create, deriving the owner, the byte-range locks and the rest) | [windows-parity.md](design/windows-parity.md) |
| The visibility of a delete - settling the 2 known flakes (they were a problem with how it was tested, not with the FS) | [windows-parity.md](design/windows-parity.md) |
| The namespace policy (the reserved names, the trailing spaces and dots, `.fuse_hidden*`) | [namespace-policy.md](design/namespace-policy.md) |
| The test counts, how to run them and the suites' side effects (the rows left in `{prefix}mounts`) | [tests.md](tests.md) |
