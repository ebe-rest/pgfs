# The pgfsctl specification

> **Route**: [docs/README.md](README.md) › **this document**
>
> **What this document is the source of truth for**: **the specification of `pgfsctl`** - the CLI and the output
> of `config` (get / list / set) and `status` (Layer 1 the cluster / Layer 2 the FS statistics / Layer 3 the
> running processes), and of `prune` (cleaning up what an abnormal exit left behind).
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [design/control-plane.md](design/control-plane.md) | **The design** of the control plane (the control messages / `{prefix}mounts` / the live application) |
> | [design/settings-matrix.md](design/settings-matrix.md) | Every setting and its reload policy (what can be changed live) |
> | [Mkfs.md](Mkfs.md) / [Mount.md](Mount.md) / [Assign.md](Assign.md) | The specifications of the other tools |
> | [../tests/docker/README.md](../tests/docker/README.md) | The docker runner (`control_plane_ctl.sh` / `status.sh`) |

This is the specification of `pgfsctl`, PGFS's **runtime control-plane CLI**. It has the two subcommands
`config` (looking at and changing the settings) and `status` (how the cluster is running / the FS statistics).

[docs/design/control-plane.md](design/control-plane.md) is the source of truth for the design. The
implementation is in [src/ctl/](../src/ctl/) (`Pgfs.Ctl`, producing `pgfsctl`), and the logic is in Core's
[ConfigAdmin](../src/core/src/Config/ConfigAdmin.cs) / [StatusAdmin](../src/core/src/Api/StatusAdmin.cs)
(reusable by the GUI too).

## Its role and where it sits

- Where `mkfs.pgfs` / `mount.pgfs` / `assign.pgfs` are the tools that "create and mount an FS", `pgfsctl` is the
  tool for **operating an FS that is running** (a systemctl-like admin command).
- It **depends on neither FUSE nor Dokan and references only Core**, so it runs on both Linux and Windows.
- The name is **a deliberate exception** to the `{role}.pgfs` convention (`mkfs.pgfs` and so on): a single
  admin-tool-like word, `pgfsctl` ([control-plane.md, Phase 3 P3-1](design/control-plane.md)).

## The connection options (common to every subcommand)

The target is resolved by the same [ConfigLoader](../src/core/src/Config/ConfigLoader.cs) as `mount.pgfs`
(the CLI plus `pgfs.toml`).

| The option | What it means |
|---|---|
| `-c` / `--connection <connstr>` | The PGFS connection string (the kv form or the `postgresql://` URL form) |
| `-s` / `--schema <name>` | The schema name (`public` by default) |
| `-x` / `--prefix <prefix>` | The table prefix (`pgfs_` by default) |
| `-f` / `--setting-file <toml>` | The name of the settings file |
| `--setting-path <dirs>` | The search path for the settings file (comma-separated) |

If a `pgfs.toml` is on the search path, whatever is not given on the CLI is filled in from it (the precedence is
CLI > TOML > the database > the default). For the details see [settings-matrix.md](design/settings-matrix.md).

---

## `config` - looking at and changing the settings

```
pgfsctl --version                     # show the version and exit (since v0.2.1)
pgfsctl config list [--json] [the connection options]
pgfsctl config get <scope.key> [--json] [the connection options]
pgfsctl config set <scope.key> <value> [the connection options]
```

- **`config list`** ... lists every setting in `scope.key` order as "the effective value plus the provenance
  plus where it is persisted plus the reload policy". The Password of a connection string is masked.
- **`config get <scope.key>`** ... shows a single setting the same way.
- **`config set <scope.key> <value>`** ... validates the value (the `Field`'s Parse) and then branches between
  **persisting it, applying it live and refusing it** according to the matrix below.

The provenance (source) is one of three values: `config` (the CLI or the TOML), `db` (`{prefix}settings`) or
`default`. `--json` is the machine-readable output (for the Phase 5 GUI).

### What `config set` does (branching on `(SaveTo, Reload)`)

| (SaveTo, Reload) | Examples | Persisted | Applied live |
|---|---|---|---|
| Db, Live | `audit.enabled` / `app.statfs` | Written to `{prefix}settings` | ✅ immediately (a `set` NOTIFY) |
| File, Live | `logging.level` / `output` / `cache_*` / `retry_*` | **No (ephemeral)** | ✅ immediately (a `set` NOTIFY) |
| Db, NextMount | `plperlu` | Written to `{prefix}settings` | On the next mount |
| File, NextMount | `mount_point` / `connection` / `schema` / `prefix` / `notify_enabled` | No (a remote toml is not reachable) | On the next mount (it directs you to edit your own toml) |
| Format | `file_system.*` | Refused | - |
| None | `--clean` / `foreground` / `setting.*` | Refused | - |

- The live application is a running mount receiving the NOTIFY and applying it at once. **The control channel is
  always on regardless of `notify_enabled`** ([P3-0](design/control-plane.md)), so it reaches a single-client
  mount (with notify OFF by default) too.
- A Live field stored in a file is **an ephemeral application that creates no database row** (a remount goes back
  to the baseline = the toml; to persist it, edit each client's `pgfs.toml`).
- The number of mounts in the output is the current number of rows in the `{prefix}mounts` registry, not the
  number of successful applications (the NOTIFY takes no ack).
- **The live off of write-back waits for the drain through the two-phase flip** (fixed: the first
  phase stops accepting, and the mode is dropped once everything has been written out). But **do not read the
  success of the CLI as every mount having saved successfully** - the NOTIFY takes no ack, so whether it arrived
  is confirmed through the effective settings in `status`.
- Layer 3 of `status` (the effective settings) shows **`mount.write_back_metadata_exclusive_create`** too.
  This is where to find a mount that is set to `defer`.
- **The effective mode of the metadata write-back**: it shows as
  `write-back(m): on (intake closed = effectively off)`. That is because in the first phase of the two-phase
  live-off flip, **the setting is still on but no new pending entries are being accepted**. It is written
  immediately on the transition, so it looks right even if the flip takes a while.
- **`handles.inodes` works on both operating systems** (added, handle-context C-1): **the number of
  bodies (inodes) that are open**. It is **the number of distinct bodies, not the number of handles**, so three
  handles on the same file count as 1. Both adapters call `Api.OpenHandle` / `CloseHandle` to count it.
- **The `open` and `peak` of the `handles` line are only filled in on a FUSE mount** (
  handle-context (6)): of `handles      : N open / peak M / K inodes`, **`open` and `peak` are the numbers in
  Core's handle table (`HandleTable`)**. **Dokan does not go through that table and puts the object directly on
  `DokanFileInfo.Context`**, so **a Windows mount is always `0 open / peak 0`** ("not counted", not "nothing
  open").
  **They are not folded into `inodes`** - **because the two measure different things**, and seeing `open`
  (the number of handles) and `inodes` (the number of bodies) separately makes it possible to
  **tell a leak of the handles apart from a leak of the counting** (agreed with the Linux side).
- **The freshness of the error state**: the red line also says
  `[from a heartbeat 42s ago]`. The error state only arrives through the heartbeat, so **a failure that cannot
  write to the database leaves no red line and looks green**. The `[stale: heartbeat 5m ago]` (in red) on the
  host line of Layer 3 is the only clue, so look there first. The transition itself is written immediately
  without waiting for the heartbeat period (30 seconds).
- **The gravestone of a lost unmount**: a row left in `{prefix}mounts` is shown as `ENDED` in
  the `LIVE` column of Layer 1, followed in red by `!! write-back UNFLUSHED LOSS (N mount(s))` and the
  breakdown. **It is not removed automatically**, so once it has been seen, remove it with
  `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0`.

The assignment of the reload policy (Live / NextMount / Format) to every setting is in
[settings-matrix.md, the reload policy](design/settings-matrix.md).

### Examples

```bash
# Take a running mount's log level to debug (File+Live, ephemeral)
pgfsctl config set logging.level debug -c "$CONN" -s pgfs

# Enable the audit log while running (Db+Live, persisted plus live)
pgfsctl config set audit.enabled true -c "$CONN" -s pgfs

# Every setting as JSON
pgfsctl config list --json -c "$CONN" -s pgfs

# Turn off the Windows (assign) permission evaluation while running (since v0.2.1; true by default)
pgfsctl config set app.enforce_permissions false -c "$CONN" -s pgfs
```

---

## `status` - how things are running / the FS statistics

```
pgfsctl status [--json] [the connection options]
```

It displays the read-only information from the database as sections in one command (no mount needed). All three
layers are implemented.

1. **Mounts (the list of what is running in the cluster, Layer 1)** ... from the `{prefix}mounts` registry: the
   host / pid / mode (`fuse` or `dokan`) / mountpoint / uptime / time since the heartbeat / live? (under 90 s).
   An absent table (an FS mkfs'd before Phase 2) shows as "table not present".
2. **Filesystem (the FS statistics, Layer 2)** ... **the target (the connection target `host:port/db`; since
   v0.2.1)**, the schema/prefix/version/volume_label, the number of inodes,
   of files and of chunks, the bytes used (`sum(length(payload))`), the cluster_size, the max_file_size, audit
   on/off and whether Citus is in use (plus the number of `pg_dist_node` rows and **the list of the nodes**,
   `coordinator host:port` / `worker host:port`, since v0.2.1; all of it from what is actually in the
   database). A value whose aggregation failed shows as `?`.
   **Use it to check "what will be removed" before `mkfs --clean` / `--purge`** (`pgfsctl status -f <toml>` ->
   `mkfs --purge -f <the same toml>`; [Mkfs.md, --purge](Mkfs.md)).
3. **Process detail (the details of the running processes, Layer 3)** ... the snapshot each mount wrote at its
   most recent heartbeat ([P4-2/P4-4](design/control-plane.md)). **The inode cache** (entries / capacity / the
   hit rate / hits, misses and evictions), **the content cache** (the number of chunks / bytes / max / the hit
   rate / hits, misses and evictions), **write-back** (the dirty bytes and files, the flushes and failures, the
   pending inodes and audit rows, the error state and so on), **handles** (the number of open handles / the
   highest number so far / **the number of bodies that are open**), **notify** (listen / data / connected) and
   **the effective config** (the operations-related subset of the running `RootConfig` = logging/retry/cache/
   statfs/audit/version and so on; values only; the password is not included). A row that is not live is marked
   `[stale]`.

> The freshness is "the value from the most recent heartbeat". The snapshot is updated right after the register,
> on the 30 s heartbeat and on receiving a `ping` control NOTIFY (there is no request-reply). When the CLI needs
> it immediately, an update can be requested by sending
> `pg_notify('{schema}_{prefix}notify','{"c":"ping"}')`, but since it does not wait for a response, the status
> run right after it is not guaranteed to have it.

The `--json` output is
`{ "mounts": { "table_present", "rows": [ { …, "stats": {…}, "config": {…} } ] }, "fs": {…} }`
(each mount row carries `stats` and `config` as nested objects; `fs` also carries `target` / `citus_nodes`).

### Examples

```bash
pgfsctl status -c "$CONN" -s pgfs            # the sectioned display
pgfsctl status --json -c "$CONN" -s pgfs     # for the GUI or a script
```

---

## `prune` - cleaning up what an abnormal exit left behind

```
pgfsctl prune [--apply] [--force] [--mounts-older-than <sec>] [--json] [the connection options]
```

**The default is a dry run.** It only counts and shows; it removes nothing. `--apply` carries it out.
**A cleanup command that does not let you look at what it is about to remove first is dangerous**, so this
default does not change.

There are three targets, and all of them have the same shape of **"an abnormally terminated mount left something
behind and nobody cleans it up"** (the design is in
[handle-context.md, the Core design of stage C](design/handle-context.md)):

| The remnant | What is left | The harm |
|---|---|---|
| **Old rows in `{prefix}mounts`** | They are removed only on a clean exit, so they pile up with every `kill -9` | **Only the look of `status`** |
| **Orphan data rows** | Stage C-2 keeps the body while it is open, so one is left if the daemon dies in the middle of that | It does not show in the namespace but **it counts towards `df`** |
| **`.fuse_hidden*`** | The trace of a libfuse with `hard_remove = 0` letting an `unlink` of an open file escape | **The contents stay forever, and they show in other mounts' `ls` too** |

> **The name of a `.fuse_hidden*` is matched strictly** - what libfuse creates is **`.fuse_hidden` plus 16 hex
> digits**, and **only what matches that format** is a target. A name the user gave a file themselves, such as
> `.fuse_hidden_notes.txt`, is **not treated as a remnant** (such a file is plainly visible in `ls` too).
> The decision is concentrated in the same function as the enumeration side (`Api.IsLibfuseHidden`).

### The safety valves (**the liveness decision differs per kind**)

- **A row in `{prefix}mounts`** is removed if its heartbeat is older than `--mounts-older-than`
  (**3600 seconds** by default). **This is also the ceiling on treating a row from another host as alive**
  (below). **The lower bound is 600 seconds**, and a smaller value (0 and negative values included) is
  refused - a small value **treats mounts alive on other hosts as dead and removes the bodies they are using**.
- **The side that removes data (the orphan data rows and the `.fuse_hidden*` files) is not touched at all if
  even one mount is alive.** `--force` overrides that, but it is for use **after stopping every mount**.
- **When `{prefix}mounts` cannot be read (an existing filesystem that has not been migrated, say), the side
  that removes data is not touched even with `--force`.** Who is alive is unknown = "cannot be seen", not
  "there are none". Run the migration ([CHANGELOG.md](../CHANGELOG.md), the changes that require a migration)
  first. In JSON this is `applied.skipped_because_unknown = true`.
- **A mount registers itself again at the heartbeat interval (30 seconds) when its registry row has
  disappeared.** The same goes for a mount whose registration failed at startup because of a database blip.
  Before, a row once gone stayed invisible for good and did not count towards "leave it alone while any mount
  is alive" above.
- **A row that recorded a loss (a gravestone) is not removed.** It is kept in order to tell the operators how
  much could not be written back (B-2). Remove it by hand once it has been seen.

> **Why "the heartbeat is old" alone is not enough to remove something**: removing the row of
> **a living mount whose heartbeat went down because of a database failure** makes that mount invisible from the
> next run onwards, which **cascades into removing a body that is in use**. The separation is: **do not damage
> the evidence the data-removing side relies on, for the sake of cleaning up `mounts`, whose harm is only
> cosmetic.**

> **It is also the condition that keeps pending inodes out of it.** The pending inodes of the metadata write-back
> **exist only in the memory of a running mount**, so their bodies look "unreferenced". Making it conditional on
> not one mount being alive avoids that structurally.


#### Right after a `kill -9` it could not be cleaned up for 90 seconds (fixed)

**A row that was `kill -9`'d keeps a fresh heartbeat**, so **it looks live for the 90 seconds right after it
died** and **the data-removing side was stopped** (= it could not be cleaned up right after a crash). It was
closed by implementing review finding (4), "look at the liveness of the pid too when it is the same host",
as it stood:

- **A row on the same host is judged by the liveness of the pid.** No process = the row is taken as dead.
- Because **the pid may have been reused**, **whether the process name is `assign.pgfs` or `mount.pgfs`** is
  checked too.
- **A row on another host has no way to have its pid checked**, so **it is treated as alive until it goes past
  the grace period** (`--mounts-older-than`, 3600 seconds by default).

#### A living mount that had merely dropped its heartbeat was treated as dead (fixed)

The fix above went only in the direction of **demoting** a dead row with a fresh heartbeat, and
**the reverse direction (promoting a living row with an old heartbeat) did not work**, because the two were
joined with an AND.

As a result, **the space between the 90 seconds of the heartbeat and the 3600 seconds of the grace period was a
blank band that was neither live nor stale**, and **a mount that had merely been unable to write its heartbeat
for a few minutes because the database was jammed** was treated as dead. Firing `--apply` then
**removes the bodies (`{prefix}data` plus the chunks) that mount has open**.

The decision was changed to **split on the host**.

| The row | The decision now |
|---|---|
| **The same host** | **The liveness of the pid is the answer; the heartbeat is not looked at** (whether the process name is `assign.pgfs` or `mount.pgfs` is checked too) |
| **Another host** | There is no way to check the pid, so **it is treated as alive until it goes past the grace period**. A row that has gone past it is also a target for removal from `{prefix}mounts` in the same run, so by then the evidence itself is gone |

> **The live of the display (`status`) and the live of "is it safe to remove data" (`prune`) are different
> questions.** For `status` it is enough to show "does it look like it is running now" at 90 seconds, but
> `prune` **removes data when it gets it wrong**, so it does not reuse the same threshold.

### Examples

```bash
pgfsctl prune                      # a dry run: see what is left behind
pgfsctl prune --apply              # clean up (the data side is skipped if anything is live)
pgfsctl prune --apply --force      # after stopping every mount, force the data side to be cleaned too
pgfsctl prune --mounts-older-than 86400 --apply   # only rows in mounts older than a day
pgfsctl prune --json               # machine-readable (dry_run / mounts / orphan_data / fuse_hidden / applied)
```

## The exit codes

| The code | What it means |
|---|---|
| 0 | Success |
| 1 | An invalid argument / an unknown subcommand / a refused `config set` (Format / File+NextMount / an unknown key / failed validation) / a failed connection and so on |
