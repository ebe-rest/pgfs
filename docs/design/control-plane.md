# The control plane - the registry / the control NOTIFY / config / status

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **this document**
>
> **What this document is the source of truth for**: the design, the implementation status and the record of
> changes of the foundation and the CLI for reading and writing the settings at runtime and for looking at how
> things are running. The `{prefix}mounts` registry, the control NOTIFY, the reload policy of a `Field` and
> `pgfsctl config` / `status` (Layers 1 to 3) all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../Pgfsctl.md](../Pgfsctl.md) | **The user-facing CLI specification** (the options, sample output). This document is the design side |
> | [gui.md](gui.md) | The GUI side, which reads the same Core API (`StatusAdmin` / `ConfigAdmin`) |
> | [settings-matrix.md](settings-matrix.md) | The list of the settings themselves and their defaults (including the value of the reload policy) |
> | [cache.md](cache.md) / [write-back.md](write-back.md) / [metadata-write-back.md](metadata-write-back.md) | The side of the "targets" being read and written here |
> | [database.md](database.md) / [../ddl/](../ddl/README.md) | The DDL of `{prefix}mounts` |
> | [runtime-control-plane.md](runtime-control-plane.md) | The structure of the whole operations phase and the relations between the phases (the hub) |
>
> **The chapter structure**: this document has three phases side by side, so the fixed three chapters
> (the design -> the implementation status -> the record of changes) are applied **per phase**. Only the
> chronological record is gathered in [the record of changes](#the-record-of-changes) at the end.

## Phase 2: the foundation (the registry plus the control NOTIFY plus the reload policy)

### The settled Phase 2 design plus the implementation plan

The sketches 2-1 to 2-3 below, settled to the point of being implementable after examining the real code.
**The implementation goes 2a -> 2b -> 2c** (each settled by a green Core build plus an e2e regression on real
hardware; the same rhythm as 1a/1b).

**P2-1. A reload policy on `Field` (`enum ReloadPolicy { Live, NextMount, Format }`)**
- `public ReloadPolicy Reload { get; init; } = ReloadPolicy.NextMount;` is added to the base record of
  [Field.cs](../../src/core/src/Config/Field.cs) (the default is the conservative NextMount).
- **Live (re-applied while running)**: `logging.level` -> `Logger.MinLevel` / `logging.output` ->
  `LogSink.Configure` / `database.retry_*` -> `Retry.Configure` (all of them can be called again as statics) /
  `app.statfs` -> simply re-reading `config.Statfs.Mode` (GetStatFs looks at it every time) /
  `mount.cache_max_entries` -> **`SetCapacity` is added to InodeCache** / `mount.cache_data_max_bytes` ->
  **`SetMaxBytes` is added to ContentCache** / `audit.enabled` -> **Api.auditEnabled is made mutable**.
- **NextMount (takes effect on a remount)**: `database.connection` / `super_connection` / `schema` / `prefix` /
  `workers` / `notify_enabled` / `mount.mount_point` / `foreground` / the FuseFlags /
  `mount.self_uname` / `self_gname` (since v0.2.1. The old fallback_* were retired, and tablespace / citus are
  not stored in the database since v0.2.1).
- **Format (mkfs only; immutable afterwards)**: `file_system.version` / `volume_label` / `cluster_size` /
  `default_chunk_size` / `max_file_size`.

**P2-2. The `{prefix}mounts` registry plus the heartbeat**
- Created at mkfs time by [Initializer.CreateTableAsync](../../src/mkfs/src/Initializer.cs) plus the Citus local
  registration (`citus_add_local_table_to_metadata`, the same as `pgfs_settings`).
- mount and assign **INSERT at startup -> UPDATE periodically (the heartbeat) -> DELETE on a clean exit**.
  The heartbeat uses the same `Task.Run` plus `CancellationTokenSource` plus `Pg.Execute` pattern as
  [NotifyChannel](../../src/core/src/Api/NotifyChannel.cs). The interval is a constant 30 s for now (it can
  become a Field later).
- **Compatibility with an existing FS**: when the table is absent (an FS that has not been re-mkfs'd),
  **a warning is logged and the registration and the heartbeat are skipped** while the mount itself succeeds
  (= the policy of not firing DDL at mount time; the DDL is concentrated in mkfs).
- The columns are as in 2-1 (`config` and `stats` are pushed into JSONB).

**P2-3. The NOTIFY control message plus applying a reload**
- `[JsonPropertyName("c")] string? Control` is added to
  [NotifyMessage](../../src/core/src/Api/NotifyChannel.cs) (`"reload"` / `"ping"`), and `PublishControl(op)` is
  added to `Api`.
- `OnRemoteChange`: `Control=="reload"` -> `Api.ReloadLiveConfig()` (new) / `Control=="ping"` -> write the
  heartbeat back at once.
- **`Api.ReloadLiveConfig()`**: `pgfs_settings` is re-read with `new ConfigStore(...).LoadAll(...)` ->
  **only the fields with Reload=Live** are re-parsed and applied to the live targets, and the `config` POCO is
  updated too (so that later reads see the new values).

**The implementation substeps**:
- **2a**: the `ReloadPolicy` enum plus tagging the Fields plus InodeCache.SetCapacity /
  ContentCache.SetMaxBytes / making Api.auditEnabled mutable. **The behaviour does not change** (only tags and
  setters are added), so a green build is the confirmation.
- **2b**: `{prefix}mounts` (created by mkfs plus the Citus local registration) plus the register / heartbeat /
  deregister in Api (an absent table gives a warning and is skipped).
- **2c**: the `Control` on NotifyMessage plus `PublishControl` plus the branch in `OnRemoteChange` plus
  `Api.ReloadLiveConfig()`. The mouth that fires a reload is Phase 3's `config set`, but 2c prepares one
  **firing path for testing** (SIGHUP or a temporary CLI, say) and confirms the live application on real
  hardware.

**The open points (to be confirmed before the implementation)**: (1) is it acceptable to start with the
heartbeat interval as a constant 30 s (making it a Field later)? (2) is "the mount warns and skips / the
registration comes after a re-mkfs" acceptable for an existing FS (or should it self-heal with a mount-time
`CREATE IF NOT EXISTS`)? (3) how should the reload of 2c be fired provisionally without waiting for Phase 3
(receiving a SIGHUP is the natural Linux way)?

**Implementation complete (2026-06-14, 2a/2b/2c, a green Core build)**: the three points were settled as
(1) start with the heartbeat as **a constant 30 s**; (2) an existing FS **gets the registry added idempotently
and non-destructively by a re-mkfs (without --clean)**, and until then the mount warns and skips; (3) the
reload is fired for testing with **psql `pg_notify('<channel>','{"c":"reload"}')`** (the production trigger is
Phase 3's config set). **Important - because `ConfigStore.LoadAll` returns only the SaveTo=Db fields,
`ReloadLiveConfig` can apply immediately only the Live fields stored in the database (`audit.enabled` and
`app.statfs`)**. The runtime-override path for the Live fields stored in a file (logging/cache/retry) is decided
in Phase 3 (the switch already implements every Live key and the setters were prepared in 2a, so it can be
enabled the moment the path is decided).

**Verified on real hardware (the docker e2e suite on the Linux client)**: the standard suite 36/36
plus the caps stress 36/36 (no regression from 2a; a regression for 1a/1b). **2b: register = 1 row in
`pgfs_mounts` / deregister = 0 rows after the unmount.** The deregister did not reach 0 at first, and the e2e
suite on real hardware found the bug that **mount and assign were not Disposing the `Api`** -> fixed with
`using var api` plus making `Api.Dispose` idempotent. Assertions for the register and the deregister were added
to `run.sh`. **2c (the live reload): green on real hardware through `tests/docker/control_plane.sh`** - mount
with notify ON -> `pg_notify('{schema}_{prefix}notify','{"c":"reload"}')` from psql -> `ReloadLiveConfig`
enables `audit.enabled` while running (the audit rows go **0 -> 1** on the next mkdir). That demonstrates the
whole chain of the control NOTIFY -> the reload -> the live application.

### 2-1. The `{prefix}mounts` registry (a proposal)

| The column | The type | What it is |
|---|---|---|
| `mount_id` | TEXT PK | A process-unique id. The sender_id of the current NOTIFY (8 hex digits) is reused |
| `host` | TEXT | The host name |
| `pid` | INT | The process id |
| `mountpoint` | TEXT | Where it is mounted |
| `mode` | TEXT | `'fuse'` / `'dokan'` |
| `started_at` | TIMESTAMPTZ | The time it started |
| `heartbeat_at` | TIMESTAMPTZ | The last heartbeat |
| `config` | JSONB | A snapshot of the effective settings (the values plus their provenance) |
| `stats` | JSONB | The cache statistics and so on (optional) |

- INSERTed at startup, `heartbeat_at` updated periodically and on receiving a `ping`, **DELETEd on a clean
  unmount**. Stale rows are judged and swept by the time since the heartbeat.
- Citus: it is a **local table** (on the coordinator) like `pgfs_settings` and is not distributed (the
  registration and the heartbeat are infrequent).
- Following the preference in persisting the settings (leaning towards a single JSONB row), the mutable state is
  **pushed into the `config` and `stats` JSONB columns**.

### 2-2. The NOTIFY control message
A **control kind** is added to the current payload `{s, i, p, x}` (sender / inode / parent / path-prefix):

```jsonc
{ "s": "<sender>", "t": "ctl", "op": "reload" | "ping", "scope": "<optional>" }
```

The branch is in [Api.cs](../../src/core/src/Api/Api.cs) `OnRemoteChange`:
- `ctl/reload` -> re-read `pgfs_settings` and re-apply **only the fields whose reload policy is `Live`**.
- `ctl/ping` -> write `heartbeat_at` and `stats` back at once (securing the freshness of the status in (4)).

### 2-3. The reload policy of a Field (the key to making (2) and (3) honest)
A [Field](../../src/core/src/Config/Field.cs) has a `SaveTarget` (None/File/Db). **A reload kind** is added
alongside it:

| The kind | What it means | Examples |
|---|---|---|
| `Live` | Can be re-applied while running | `logging.level` / `output`, `mount.cache_max_entries`, `audit.enabled`, `app.statfs` |
| `NextMount` | Takes effect on a remount | `mount.mount_point`, the FuseFlags, `database.connection`, `schema` / `prefix` |
| `Format` | mkfs only; immutable afterwards | `file_system.cluster_size` / `chunk_size` / `max_file_size` |

What a live application really is is replacing the mutable runtime state on the Core side (`Logger.MinLevel` /
`LogSink.Configure` / resetting the InodeCache capacity / the audit flag / the statfs flag / the Windows
permission-check flag). No re-initialization of FUSE or Dokan is needed (= `Live` is exactly the range that needs no change at
the OS layer). The matrix becomes authoritative by adding a reload column to
[settings-matrix.md](settings-matrix.md).

---

## Phase 3: the config subcommand (plus the live application)

### The settled Phase 3 design

The problem statements below are the state before the implementation. The control LISTEN is now always on, and
the shell-out proposal for the GUI has been changed to calling Core directly in Phase 5.

**One discovered precondition plus three design judgements** were settled after comparing against the real code
(closing the open design judgements 1 to 3 below). **The implementation goes 3a -> 3b -> 3c** (each with a
green Core build plus an e2e regression on real hardware; the same rhythm as 1a/1b/2a-c).

**The discovered precondition P3-0 - the control channel is gated on `notify_enabled`**
[Api.cs](../../src/core/src/Api/Api.cs) creates and Starts the `notifyChannel` only when
`config.Database.NotifyEnabled` is true (in the Api ctor). Therefore **a single-client mount (with notify OFF by
default) never receives a reload / set / ping at all** (which is why the reload demo of Phase 2c required notify
ON). `TryRegisterMount` runs independently of notify_enabled, so the status (reading the registry) works even
for a single client, but a live `config set` (the heart of Phase 3) does not get through.

**Decision P3-0 - split the control LISTEN out and keep it always on**: the LISTEN of the NotifyChannel is
**always established regardless of notify_enabled**. `notify_enabled` gates **only the data-change notifications
(publishing and handling the i/p/x/d payloads)**.
- The implementation: the Api ctor always creates and Starts the `notifyChannel`. `Notify(...)`, which sends the
  data changes, returns early on the new flag `this.dataNotifyEnabled = config.Database.NotifyEnabled` (so that
  a single client does not fire pg_notify pointlessly). The control branches of `PublishControl` and
  `OnRemoteChange` are always active.
- The cost: one permanently idle LISTEN connection per mount (the same "resident in the database" trade-off as
  the statfs TTL cache and the heartbeat).
- The control channel and the data notifications keep sharing the same NOTIFY channel
  (`{schema}_{prefix}notify`) (told apart by the `c` of the NotifyMessage, as before).

**Decision P3-1 - the executable structure = a single `pgfsctl`**: `config` and `status` go as subcommands into
**one `pgfsctl`** (referencing Core only; no FUSE or Dokan needed).
- It works on both operating systems (it does not depend on FUSE or Dokan). The Phase 5 GUI shells out to the
  one binary with `--json`.
- The dispatch: a thin new layer that routes to `config` or `status` with `switch(args[0])` (there is no
  subcommand mechanism today, but it is trivial). The argument parsing inside each subcommand reuses the
  existing [ConfigLoader](../../src/core/src/Config/ConfigLoader.cs).
- The naming: `AssemblyName`=`pgfsctl` / `RootNamespace`=`Pgfs.Ctl`. It is **a deliberate exception** to the
  `{role}.pgfs` naming convention (mkfs.pgfs and so on) (a single systemctl-like word is natural for an admin
  tool). The csproj has the same shape as mkfs (OutputType=Exe, referencing only Core.csproj). The new project
  is `src/ctl/` and the output is `bin/Publish/pgfsctl(.exe)`.

**Decision P3-2 - the File+Live application path of `config set` = inlined in the NOTIFY**: the Live fields
stored in a file (logging/cache/retry) cannot touch a remote toml, so they are not persisted in the database.
The value is **inlined in the NOTIFY control message** (`{"c":"set","k":"logging.level","v":"debug"}`) and a
running mount applies it directly to the live target.
- It creates no database row, so **there is no footgun of it coming back on a remount; it is purely ephemeral**
  (the toml is the baseline, and for persistence the user is directed to edit their own toml).
- `[JsonPropertyName("k")] string? Key` and `[JsonPropertyName("v")] string? Value` are added to
  [NotifyMessage](../../src/core/src/Api/NotifyChannel.cs) (omitted from the payload when null).
- On the receiving side, `OnRemoteChange`: `Control=="set"` -> look the Field up in the Schema and, if
  `Reload==Live`, call `ApplySingleLive(field, Value)`. The switch of
  [ReloadLiveConfig](../../src/core/src/Api/Api.cs) is extracted into `ApplySingleLive(Field, string raw)` and
  shared between the reload (a loop over every Live field in the database) and the set (a single Field).

**The behaviour matrix of `config set <scope.key> <value>`** (branching on the Schema's `(SaveTo, Reload)`):

| The field's (SaveTo, Reload) | Persisted | Applied live | What pgfsctl says |
|---|---|---|---|
| **Db, Live** (`audit.enabled` / `app.statfs`) | Written to pgfs_settings | ✅ a `set` NOTIFY -> immediately | persisted + applied live to N mounts |
| **File, Live** (the 5 of logging/cache/retry) | **No (ephemeral)** | ✅ a `set` NOTIFY -> immediately | applied live to N mounts (ephemeral; edit pgfs.toml to persist) |
| Db, NextMount (`plperlu`) | Written to pgfs_settings | On the next mount | persisted; applies on next mount |
| File, NextMount (`mount_point` / `connection` / `schema` / `prefix` / `notify_enabled` / `workers`) | No (a remote toml is not reachable) | On the next mount | cannot reach remote toml; edit pgfs.toml locally; applies next mount |
| Format (`file_system.*`) | Refused | - | mkfs-only, immutable after format |
| None (`--clean` / `foreground` / `super_connection` / `setting.*`) | Refused | - | not a settable runtime field |

- **N (the number of mounts it applied to) is the current number of rows in the `{prefix}mounts` registry.**
  The NOTIFY is fire-and-forget with no ack, so the true "number applied" cannot be obtained (the "most recent"
  trade-off of decision 1). pgfsctl honestly reports "M mounts in the registry -> the control message was
  fired".
- The value is validated by reusing `Field.NormalizeRaw` (Parse -> Format). No super privilege is needed (the
  pgfs user owns pgfs_settings).

**`config get` / `config list`**:
- `config get <scope.key>` ... shows that field's value stored in the database (if any), its default, its SaveTo
  and its Reload.
- `config list` ... every field in scope.key order, with the database value or the default plus the SaveTo plus
  the Reload. If a toml is at hand, the provenance of the value is added. `--json` makes it machine-readable
  (for the GUI in (5)).
- **The view of the effective values of a running mount** is the job of Phase 4's `status`
  (= the `{prefix}mounts.config` snapshot). config get/list stay focused on the database plus the defaults, so
  that the responsibilities are separate.

**The implementation substeps**:
- **3a** (Core only; the behaviour does not change): making the control LISTEN always on (P3-0) plus `k`/`v` on
  NotifyMessage plus the `set` branch in `OnRemoteChange` plus extracting the switch of ReloadLiveConfig into
  `ApplySingleLive`. Even with notify OFF, only one LISTEN is added and the existing e2e suite is unchanged.
- **3b**: the new `pgfsctl` project (Core only) plus `config get/list/set` plus `--json`. The set branches
  between persisting and firing a NOTIFY according to the matrix above. The connection information reuses the
  same `-c/--connection` plus the toml search as mount, through the ConfigLoader.
- **3c**: the e2e suite on real hardware - against **a single-client mount with notify OFF**, apply
  `pgfsctl config set logging.level debug` live, and take the audit from 0 to 1 with
  `pgfsctl config set audit.enabled true` (extending Phase 2c's
  [control_plane.sh](../../tests/docker/control_plane.sh) onto the notify-OFF path).

The live application ((2)) is complete with this command's set -> the NOTIFY -> each mount's `OnRemoteChange`
(no new machinery is needed; it just rides Phase 2's foundation plus P3-0's always-on LISTEN).

### Implementation status (as-built)

- **3a complete**: making the control LISTEN always on (P3-0) plus `k`/`v` on
  NotifyMessage plus the `set` branch in `OnRemoteChange` plus extracting the switch of `ReloadLiveConfig` into
  `ApplySingleLive(field, raw)` (shared between the reload and the set) plus `ApplyLiveSet` plus gating `Notify`
  on `dataNotifyEnabled`. A failure of the control-only LISTEN gives a warning and carries on (only with notify
  ON does it abort the startup as before). **The whole solution builds green and the behaviour is unchanged**
  (the existing e2e suite is unchanged on the notify-ON path; with notify OFF only one idle LISTEN is added).
- **3b complete**: `pgfsctl` (src/ctl, Core only, `Pgfs.Ctl`) plus
  `config get/list/set` plus `--json`. The logic is in Core's public
  [ConfigAdmin](../../src/core/src/Config/ConfigAdmin.cs) (reusable by the GUI). Supporting:
  `Field.FormatDefaultRaw` / `FormatJsonFromRaw`, `ConfigStore.SaveRaw` (a non-generic UPSERT),
  `ConfigLoader.GetMergedRaw`. **The offline smoke test**: verifying the matrix branches (Format /
  File+NextMount / unknown are refused) plus judging the provenance (db/config/default) with a `config
  list/get` against a real database plus confirming that the Password is masked.
- **3c complete (the e2e suite green on real hardware)**: on docker on the Linux
  client (with real FUSE), [control_plane_ctl.sh](../../tests/docker/control_plane_ctl.sh) PASSes - against
  **a single-client mount with notify OFF**, (1) the control LISTEN is always on (P3-0 confirmed directly);
  (2) `pgfsctl config set audit.enabled true` (Db+Live) takes the audit rows **0 -> 1** (persisted plus live);
  (3) the inlined set of `pgfsctl config set logging.level trace` (File+Live) is applied to the mount (P3-2).
  **The regression**: in the same session, run.sh **36/36** plus control_plane.sh (the 2c notify-ON reload)
  PASS. Publishing pgfsctl was added to `Dockerfile.mount`.
- **A known caveat**: the lenient parsers such as `LogLevelField` (`Level.Parse`) do not throw on an invalid
  value but fall back to the default, so `config set logging.level <garbage>` is not refused and the equivalent
  of the default is applied (the same Field validation semantics as the mkfs and mount CLIs). Persisting a
  `config set` in the database needs a `pgfs_settings` connection (on failure it throws -> exit 1).

---

## Phase 4: the status subcommand

It gathers three layers of information (`--json`):
1. **The list of what is running in the cluster** ... the rows of `{prefix}mounts`
   (host/pid/mountpoint/mode/uptime/time since the heartbeat/live?).
2. **The FS statistics (from the database; no mount needed)** ... the schema/prefix/version, the number of
   inodes, the total bytes, the number of chunks, df (reusing statfs), audit on/off, the Citus nodes and shards.
   <- **It is read-only, so this part alone can be released first.**
3. **The details of the running processes** ... the effective settings plus their provenance, read after
   freshening the heartbeat with a `ping` / the cache statistics (entries, hit rate) / the NOTIFY connection /
   the health of the database.

### The settled Phase 4 design

A comparison against the real code plus three agreed judgements. `{prefix}mounts` already has the `config` and
`stats` JSONB columns (the place for the Layer 3 snapshot), but **as things stand a mount writes only
host/pid/mountpoint/mode/started_at/heartbeat_at and leaves config and stats empty**, and the implementation is
staged with that in mind.

**Decision P4-1 - the scope to start with = Layers 1+2 first (read-only; no change to mount)**: the list of what
is running in the cluster (Layer 1) plus the FS statistics (Layer 2) are implemented first. Both come from the
database and are read-only, so they work with no change on the mount side (the "can be released first" subset of
the design). **Layer 3 (the cache statistics and the effective settings of the running processes) is cut into
the next increment.**

**Decision P4-2 - how Layer 3 gets its freshness (in the future) = a heartbeat snapshot**: when Layer 3 is
implemented, the mount writes its own effective settings and cache statistics into the `{prefix}mounts.stats`
(plus `config`) JSONB on every heartbeat (30 s), and the status only reads the registry (no request-reply). It
matches the "most recent value" trade-off of decision 1 and uses the existing `stats` column as it is. The
request-reply `ping` is left as room to bolt on if a low-latency requirement appears (not now).

**Decision P4-3 - the output = a single `pgfsctl status [--json]` with sections**: one command displays "what is
running" and "the FS statistics" as sections. `--json` is machine-readable (for the Phase 5 GUI). There are no
subtargets (`status mounts` / `status fs`) (it is systemctl-status-like).

**The implementation structure**:
- **Core's `StatusAdmin`** (new and public, the counterpart of `ConfigAdmin`) does the database reading:
  - `ListMounts()` -> SELECTs `{prefix}mounts` with `now() - heartbeat_at` and `now() - started_at`, and returns
    each row with the uptime in seconds, the seconds since the heartbeat and live? (the elapsed time being under
    the threshold = 3x the heartbeat = 90 s). An absent table gives an empty result plus a note.
  - `FsStats()` -> aggregates from the database: the number of `{prefix}inode` rows / the number of
    `{prefix}data_chunk` rows / the bytes used (reusing the same aggregation as statfs) /
    `file_system.version`, `volume_label`, `cluster_size` and `max_file_size` (from pgfs_settings) /
    `audit.enabled` / whether Citus is in use plus the number of `pg_dist_node` rows (best-effort).
- **pgfsctl's `status`** is a thin front (text / `--json`). The connection is resolved with the same
  ConfigLoader as config.
- **The implementation substeps**: **4a** Core's `StatusAdmin` (ListMounts plus FsStats) -> **4b** pgfsctl's
  `status` plus `--json` -> **4c** the e2e suite on real hardware (confirming through the `control_plane_ctl.sh`
  family that the status returns the running rows and the statistics while mounted).
- **The document**: on completion, `Pgfsctl.md` (the CLI specification of config plus status) is created and
  registered in the README and the docs/README list.

**The open point (to be settled when Layer 3 is started)**: ~~the items to put on the stats JSONB (entries / the
hit rate / the state of the NOTIFY connection) and the API for getting the statistics out of the InodeCache and
the ContentCache~~ -> ✅ settled in **the settled Phase 4 Layer 3 design below**.

### Implementation status (as-built)

- **4a/4b complete**: Core's
  [StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) (`ListMounts` = adding the uptime, the time since the
  heartbeat and live? from the database's `now()` differences, degrading gracefully when the table is absent /
  `GetFsStats` = the number of inodes, files and chunks, the bytes used as `sum(length(payload))`, the
  version/label/cluster/max plus audit plus citus (plus pg_dist_node)) plus pgfsctl's `status [--json]`
  (sectioned text / JSON). Resolving the connection was factored into
  [CliUtil](../../src/ctl/src/CliUtil.cs) (shared between config and status). **The offline smoke test**:
  confirming the Layer 2 statistics are accurate against a real database, and that an FS with no
  `{prefix}mounts` degrades gracefully.
- **4c complete (the e2e suite green on real hardware)**: on docker on the Linux
  client (with real FUSE), [status.sh](../../tests/docker/status.sh) PASSes - Layer 1 (table_present / one live
  fuse mount row / 0 rows after the unmount deregisters) plus Layer 2 (creating a file takes the inodes 1 -> 3,
  used_bytes > 0 and chunk_count >= 1).
- **The document**: [Pgfsctl.md](../Pgfsctl.md) was created (the CLI specification of config plus status) and
  registered in the README and the docs/README list.
- **Layer 3 complete (4d-1 to 4d-4, the e2e suite green on real hardware)** - see the as-built at
  the end of the settled Phase 4 Layer 3 design below.

### The settled Phase 4 Layer 3 design

A comparison against the real code ([InodeCache](../../src/core/src/Api/InodeCache.cs) /
[ContentCache](../../src/core/src/Api/ContentCache.cs) / [Api](../../src/core/src/Api/Api.cs)'s heartbeat /
[StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) / the `{prefix}mounts` DDL) plus the agreement (the scope =
**both stats and config** / the statistics items = the full set below). **No schema impact** - `{prefix}mounts`
already has the `config` and `stats` JSONB columns created by mkfs (empty `'{}'` today), so Layer 3 only
populates them.

**Decision P4-4 - the freshness = a heartbeat snapshot (making P4-2 concrete)**: **at registration time, on
every 30 s heartbeat and on receiving a ping**, the mount serializes its own `InodeCache` / `ContentCache`
statistics and effective settings to JSON and writes them into `{prefix}mounts.stats` / `.config`. The `status`
only reads the registry (no request-reply). The freshness is "the value from the most recent heartbeat" (the
trade-off of decision 1). It is written at registration time too, so it shows in the status from the moment it
starts.

**Decision P4-5 - the cache statistics API (the behaviour does not change)**: cumulative counters (`long`) are
added to both caches and incremented under the existing `lock(this)` (almost no effect on the hot path). A
read-only `Stats()` is exposed for the snapshot:
- `ContentCache.Stats()` -> `entries` (the number of chunks) / `bytes` (currentBytes) / `maxBytes` / `hits` /
  `misses` / `evictions` / `generation`. The hit/miss comes from the return of `Get` (non-null = a hit / null =
  a miss) = **a single choke point**.
- `InodeCache.Stats()` -> `entries` (byId.Count = the authority) / `pathEntries` / `childrenLists` / `capacity`
  (cacheMaxEntries) / `hits` / `misses` / `evictions`. The hit/miss is at **the Load boundary** = resolved from
  the cache (a hit) / fell through to an inode query against the database (a miss). It goes in the `load()` that
  the several Load overloads converge on.
- The hit rate is not stored but computed as `hits/(hits+misses)` when the status is displayed. `evictions` is
  the cumulative count of what `EvictIfOverCapacity` / `EvictIfOverBudget` dropped.

**Decision P4-6 - what is in the snapshot (values only)**:
- The `stats` JSONB: `{ "inode":{entries,capacity,hits,misses,evictions},
  "content":{entries,bytes,maxBytes,hits,misses,evictions,generation},
  "notify":{control_listen,data_enabled,connected}, "snapshot_at":<ISO8601> }`.
- The `config` JSONB: **the effective values** of the running `RootConfig` (= the values the mount is actually
  running with). Unlike `pgfsctl config list` (the database plus the defaults), the new value is that
  **the result after an ephemeral File+Live set, and the result of the CLI and toml overrides, is visible**.
  **v1 has the values only** - the provenance (CLI/toml/DB/default) is put off because `RootConfig` does not
  retain it after loading (if the provenance is needed, that is a separate increment giving the ConfigLoader a
  provenance).

**The implementation structure**:
- **Api**: `WriteHeartbeat` is widened to `SET heartbeat_at, stats=@stats, config=@config`. The same JSON is
  written by the register INSERT and on receiving a ping (`OnRemoteChange`) too. The JSON is built with
  System.Text.Json (already used by NotifyMessage). `notify.connected` is the connection state of the
  NotifyChannel, and `data_enabled` is `dataNotifyEnabled`.
- **StatusAdmin**: `config` and `stats` are added to the SELECT of `ListMounts` and put on `MountInfo` as raw
  JSON (or typed). An absent column degrades gracefully (the existing policy).
- **pgfsctl status**: a Layer 3 section per live mount (the cache statistics plus the effective settings). A
  non-live one shows its last snapshot plus a note on the elapsed time. `--json` is extended.

**The implementation substeps** (the a/b/c rhythm):
- **4d-1**: `InodeCache.Stats()` / `ContentCache.Stats()` plus the counters (the behaviour does not change; the
  whole solution builds green).
- **4d-2**: the Api writes the stats+config JSON on the register, the heartbeat and a ping.
- **4d-3**: the StatusAdmin reads config and stats, and pgfsctl status displays Layer 3 (plus `--json`).
- **4d-4**: the e2e suite on real hardware (extending [status.sh](../../tests/docker/status.sh)) - mount ->
  warm the content cache by creating and reading a file -> assert that the status returns
  `content.entries>0` / `inode.hits>0` plus the effective config.

**The open points (to be confirmed during the implementation)**: (1) the range of the fields to put in the
`config` snapshot (the whole `RootConfig`, or the operations-related subset of the settings matrix).
(2) how to count a path walk (`load(parser,…)`) when the InodeCache hit/miss is placed at the Load boundary
(whether each hop's database query counts as a miss, or one Load counts as one unit).

### Implementation status (as-built)

Implemented and green in the e2e suite on real hardware. The two open points were settled as (1) **the
operations-related subset** (13 keys in [Api.BuildConfigJson](../../src/core/src/Api/Api.cs) =
logging / retry / notify_enabled / mount_point / cache x2 / statfs / audit / version / label. The password is
not included) and (2) **one Load = one unit** (the hit/miss is at the private `load(long?,string?)` boundary;
a path walk is one miss regardless of how many database queries happen inside).

- **4d-1**: cumulative counters (hits/misses/evictions) plus a read-only `Stats()`
  (`InodeCacheStats` / `ContentCacheStats`) on [InodeCache](../../src/core/src/Api/InodeCache.cs) and
  [ContentCache](../../src/core/src/Api/ContentCache.cs). The Content hit/miss comes from the return of `Get`
  (a single choke point) and the Inode one from the Load boundary. All of it is under the existing `lock(this)`.
  **The behaviour does not change and Core builds green.**
- **4d-2**: [Api.WriteHeartbeat](../../src/core/src/Api/Api.cs) widened to write `heartbeat_at` plus
  the `stats` / `config` JSONB (on all three paths: right after the register, the 30 s heartbeat and receiving a
  ping). `BuildStatsJson` (inode/content/notify/snapshot_at) plus `BuildConfigJson` (the effective values, values
  only). [NotifyChannel.Connected](../../src/core/src/Api/NotifyChannel.cs) was added. **No schema impact**
  (the mounts.config / stats columns are created by mkfs).
- **4d-3**: [StatusAdmin.ListMounts](../../src/core/src/Api/StatusAdmin.cs) reads `config::text` and
  `stats::text` too and puts them on `MountInfo`. [StatusCommand](../../src/ctl/src/StatusCommand.cs) emits the
  Layer 3 section (the inode / content cache statistics plus the computed hit rate plus the NOTIFY connection
  state plus the effective config as key=value) as text and as `--json` (embedded nested with JsonNode).
  A stale mount shows `[stale]`.
- **4d-4 (the e2e suite green on real hardware)**: the extended
  [status.sh](../../tests/docker/status.sh) PASSes on docker on the Linux client (with real FUSE) - warm the
  content cache with a read -> a ping control NOTIFY (the always-on LISTEN, P3-0) updates the snapshot at once
  -> `content chunks=1` / `inode hits=33` / the effective config (`audit.enabled`) / `notify listen=on`.
  **The regression**: run.sh **36/36** PASS plus 0 deregistered rows (no regression from the read hot-path
  counters and writing the heartbeat snapshot).

---


## The record of changes

The chronological record goes here (the chapters of each phase above are the source of truth for the design and
the as-built).

- [runtime-control-plane.md](runtime-control-plane.md) had swollen to 1,802 lines, so it was
  split by feature and this document was carved out of it. The contents are as they were before the split.
