# The pgfs documentation index

> **Route**: **this document is the entrance**. Everything else is reached from here.
>
> **What this document is the source of truth for**: **the index and the classification of every document**.
> It lists which document is the source of truth for what.
> Individual designs, specifications and measurements do not go here; they always live behind the link
> (writing the content into the index duplicates it, and one copy then rots).
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [next.md](next.md) | **What to do next** (the punch-list). The summary of where things stand is here too |
> | [architecture.md](architecture.md) | The project layout, the dependencies and how to build |
> | [history.md](history.md) | How the completed items without a document of their own came about |
> | [../README.md](../README.md) | **The public** entrance (it lists the released documents only) |

The full list of pgfs documents. **What to do next is in [next.md](next.md)** (where things stand and
what to do next).

The layout: **feature designs and reference material live in [`design/`](design/)**. **The tool
specifications (`Mkfs`/`Mount`/`Assign`), the entry-point documents and `ddl/` stay directly under `docs/`.**

## Reading order

- How to use it today: [Mount](Mount.md) / [Assign](Assign.md) / [Pgfsctl](Pgfsctl.md) -> [the settings list](design/settings-matrix.md).
- The limitations that are still live: [CHANGELOG.md](../CHANGELOG.md), the known limitations section -> [the as-built status of the runtime control plane](design/runtime-control-plane.md) -> [the test record](tests.md).
- Rolling features out to Windows: [the Windows design](design/windows-parity.md) -> [where Assign stands](Assign.md) -> the acceptance criteria in that same design.

## 🚩 The entry points and the overall picture

| Document | Contents |
|---|---|
| [next.md](next.md) | **Where a session starts, and what to do next** - the punch-list / where things stand / the index of completed work |
| [architecture.md](architecture.md) | The project layout (Core/Fuse/Dokan plus thin exes) / the dependencies / the internal structure of Core / how to build and run |
| [history.md](history.md) | An archive of how the completed items without a document of their own came about (new entries use the current paths) |

## The specifications (the tool CLIs)

| Document | Contents |
|---|---|
| [Mkfs.md](Mkfs.md) | The specification of `mkfs.pgfs` |
| [Mount.md](Mount.md) | The specification of `mount.pgfs` (Linux/macOS) |
| [Assign.md](Assign.md) | The specification of `assign.pgfs` (Windows) |
| [Pgfsctl.md](Pgfsctl.md) | The specification of `pgfsctl`, the runtime control-plane CLI (`config` / `status` / `prune`) |
| [tests.md](tests.md) | **The test hub** - the full catalog / how to run them / the environment requirements / the docker integration |

## [design/](design/) - the feature designs and reference material

| Document | Contents |
|---|---|
| [design/v0.3.0-options.md](design/v0.3.0-options.md) | **(candidates gathered, not implemented)** the candidates for turning what were "choices" into options in v0.3.0, and how they are classified |
| [design/v0.2.0-plan.md](design/v0.2.0-plan.md) | The v0.2.0 design - splitting `Lib` into `Core`/`Fuse`/`Dokan` plus bringing FUSE in-house (the decisions, the points to settle, where things moved, the naming convention) |
| [design/metadata-write-back.md](design/metadata-write-back.md) | Metadata write-back (deferring the metadata writes) |
| [design/metadata-write-back-reviews.md](design/metadata-write-back-reviews.md) | The review record of metadata write-back (the as-built of round A and B-1 to B-13) |
| [design/write-back.md](design/write-back.md) | write-back (deferring the writes of the file bodies) |
| [design/cache.md](design/cache.md) | Caching (the inode LRU / the content read cache / read-ahead) |
| [design/control-plane.md](design/control-plane.md) | The control plane (the registry / the control NOTIFY / pgfsctl config and status) |
| [design/gui.md](design/gui.md) | The GUI operations dashboard (Avalonia) |
| [design/runtime-control-plane.md](design/runtime-control-plane.md) | **The implementation, the remaining work and the design history** of the runtime control plane plus the caching - going faster with caches / applying settings live / the config and status subcommands / the GUI. Unified around aggregating in the database (NOTIFY plus a registry) |
| [design/windows-parity.md](design/windows-parity.md) | **(the "Windows basics" stage is implemented; the rest is not)** the design for rolling the Linux features out to Windows - the current gaps / the candidates / why each is recommended / a common base with OS-specific derivations / the stages and their acceptance criteria |
| [design/fuse-binding.md](design/fuse-binding.md) | The design of the in-house FUSE binding - the table of libfuse3's public API against what pgfs uses / the ops / the structs |
| [design/handle-context.md](design/handle-context.md) | **(stages A to C implemented; stage D not started)** unifying the handle context (`OpenFileContext`) - dropping path-first identification / putting the audit subject and the synchronization policy on the handle / stages A to D |
| [design/namespace-policy.md](design/namespace-policy.md) | The namespace policy - the record of deciding **together** the three cases where what is visible and what exists diverge (hiding `.fuse_hidden*` / the Windows reserved names / trailing spaces and dots). The options and their trade-offs / the decisions and the premises that later broke / the lines that must not be crossed |
| [design/support_for_citus.md](design/support_for_citus.md) | Citus (horizontal distribution) support - moving to bytea / the distribution strategy / the `pgfs_lock` exclusion control |
| [design/raid.md](design/raid.md) | **(at the idea stage)** a RAID that gathers several PostgreSQL instances into one FS - per-path placement / the replica count (0/n/-1) / merging the namespaces |
| [design/audit-log.md](design/audit-log.md) | The audit log - the monthly `{prefix}audit` partitions / the caller context / where the hooks sit |
| [design/permission-interop.md](design/permission-interop.md) | The Linux-to-Windows interoperability of permissions, ownership and ACLs (diagram: [design/permission-interop-diagram.html](design/permission-interop-diagram.html)) |
| [design/df-support.md](design/df-support.md) | The real free space behind `df` (statfs) - the plperlu `SECURITY DEFINER` / mkfs `--statfs` / the three-stage fallback / the Citus aggregation |
| [design/fstab-support.md](design/fstab-support.md) | `/etc/fstab` support |
| [design/xattr-bytea.md](design/xattr-bytea.md) | Transparent xattr byte strings (a parallel-array KVS) |
| [design/settings-and-plperlu.md](design/settings-and-plperlu.md) | Reorganizing the settings scopes plus the `app.plperlu` gate plus the tablespace auto-mkdir |
| [design/database.md](design/database.md) | The database schema (the per-table DDL is in [ddl/](ddl/README.md)) |
| [design/settings-matrix.md](design/settings-matrix.md) | The matrix of every settings item against (CLI/TOML/DB/default/when it is read) |
| [design/coding-style.md](design/coding-style.md) | The coding conventions (including the conditional-branch rule) |
| [../CHANGELOG.md](../CHANGELOG.md) | **The change history** (per release tag, including the migration steps and the known limitations) |
| [design/data-id-lifecycle.md](design/data-id-lifecycle.md) | The lifecycle of `data_id` (hardlink sharing / fixing the stability of `st_ino`) |
| [design/performance.md](design/performance.md) | The implementation and measurement record of the performance work, plus the remaining candidates |

The DDL belongs with design by classification, but since it is per-table SQL it stays as
[`ddl/`](ddl/README.md) directly under `docs/`.
