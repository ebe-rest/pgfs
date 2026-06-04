# df support (real free-space reporting via statfs)

Design for making `df /mnt/pgfs` (FUSE statvfs / the Windows volume free space) report the
**real on-disk free space of the backend PostgreSQL tablespace**. On a multi-node Citus
deployment the figures are collected from every node and summed.

For the Japanese version see [df-support.ja.md](df-support.ja.md).

Status: **implemented**. The non-Citus full chain, the Citus multi-worker aggregation, and
`df` over a live mount have all been verified on real hardware. This document is the source of truth.

Implementation: [Schema.Statfs.Mode](../src/lib/src/Config/Schema.cs) (`--statfs`) /
[Initializer.CreateStatfsFunctionsAsync](../src/mkfs/src/Initializer.cs) (function creation) /
[Api.GetStatFs](../src/lib/src/Api/Api.cs) (call site + a few-second cache + nominal fallback) /
the wiring in [mount StatFS](../src/mount/src/FileSystem.cs) + [assign GetDiskFreeSpace](../src/assign/src/FileSystem.cs).

## Motivation / current state

The naive [`FileSystem.StatFS`](../src/mount/src/FileSystem.cs) returns this pseudo disk information:

- capacity = [`Api.GetCapacityBytes`](../src/lib/src/Api/Api.cs) = `file_system.max_file_size` (nominal capacity)
- used = [`Api.GetTotalUsedBytes`](../src/lib/src/Api/Api.cs) = `pg_database_size(...)`
- free = capacity − used

That is "nominal capacity − DB size", **not the real free space of the underlying FS**. We want
`df` to report the truth.

### Why plain SQL cannot do it

PostgreSQL has no SQL that returns "free space of the FS behind a tablespace". `pg_tablespace_size()`
only returns **usage**. To learn the free space you must `statvfs(2)` the underlying directory, which
is not reachable from SQL. Therefore a **server-side function** must perform the `statvfs`.

## Adopted approach: a plain `CREATE FUNCTION` in plperlu (not an extension)

Of the three options considered, we adopted **③ a plain `CREATE FUNCTION` in plperlu, not an extension**.

| Option | Compile | Distribution | Fit with mkfs |
|---|---|---|---|
| ① C extension | required (.so) | **a binary per PG major × OS × arch** dropped into each node's pkglibdir; build/distribution effort comparable to installing Citus (server headers needed) | ✗ heavy |
| ② PL function extension | none | two text files (`.control` + `.sql`) **physically placed in each node's SHAREDIR**. `CREATE EXTENSION` propagates via Citus, but the file placement is separate | △ file placement needs ssh/scp in mkfs = major rework |
| **③ plain CREATE FUNCTION (plperlu)** | none | **SQL text only**. Citus distributes it to every node with `run_command_on_all_nodes($$ CREATE FUNCTION ... $$)` | ✅ **mkfs stays a pure-SQL tool** |

### What decides for ③

- **mkfs is a "pure-SQL tool"** (Npgsql connection only; it has no way to touch a node's FS). Extensions
  (①②) need files placed in each node's `SHAREDIR/extension/`, which would force a **major rework** to
  pass ssh connection info into mkfs.
- In ③ the function body is SQL text, so it can be **distributed to every node with `run_command_on_all_nodes`
  using SQL alone** — no file copy, no ssh. mkfs only adds a few statements to the existing `--super` path
  (Citus setup).
- No compilation; arch-independent.
- The one concession: because it is not an extension it **does not auto-propagate to workers added later** →
  mkfs (or a future mount-time ensure) re-runs `CREATE OR REPLACE` on every node. This absorbs the same way
  the Citus extension setup does.

### Server prerequisite

We use **plperlu** as the untrusted PL. If `plperl.so` is present on the target PostgreSQL server, it can be
enabled with `CREATE EXTENSION IF NOT EXISTS plperlu` (superuser) with no file copy (`plperl.so` ships with
the PG build on each node). On a server without `plperlu`, `auto` mode degrades to the nominal fallback
(see below).

## Function design (fixed signature)

Place the functions under the schema + table prefix (e.g. `pgfs.pgfs_statfs`). Keep the **signature** of the
entry point `{prefix}statfs()` that clients call **unchanged**, swapping only the body by mode/environment
(leaving room to swap in Windows support or a C implementation later).

| Function | Language | Role |
|---|---|---|
| `{prefix}statvfs(dir text) → (total bigint, avail bigint)` | **plperlu** | purely statvfs `dir` and return total / available bytes (the only untrusted part) |
| `{prefix}fs_free(tablespace text) → (total, avail)` | plpgsql | resolve **this node's** tablespace directory and call `{prefix}statvfs` |
| `{prefix}statfs() → (total, avail)` | plpgsql | **the entry point C# calls**. Local on non-Citus, all-node aggregation on Citus |

### Directory resolution (inside `fs_free`, node-local)

- `pg_default` / empty / `pg_global` → `current_setting('data_directory')` (actually `base/`)
- named tablespace → `pg_tablespace_location(oid)`
- because **each node resolves its own `data_directory`**, pass the tablespace name and resolve it on the
  node (never distribute the coordinator's path).

### The three-tier fallback in statvfs (inside plperlu, at runtime)

```perl
# sketch of {prefix}statvfs(dir)
# 1) use Filesys::Df if available
my $r = eval { require Filesys::Df;
               my $d = Filesys::Df::df($_[0], 1);  # block size 1 = bytes
               return [$d->{blocks}, $d->{bavail}]; };
return $r if $r && @$r;
# 2) otherwise shell out to df (the strength of untrusted PL)
my @o = `df -B1 --output=size,avail "$_[0]" 2>/dev/null`;
# ... parse line 2 into [size, avail] ...
# 3) if df is also missing, undef → caller falls back to nominal
return undef;
```

Even without `Filesys::Df` from CPAN, the `df`-command path (tier 2) works, so a CPAN install is not required.

### Citus aggregation (inside `statfs`)

- **non-Citus**: `SELECT * FROM {prefix}fs_free('<configured tablespace>')`
- **Citus**: sum the results of `run_command_on_workers($$ SELECT total||','||avail FROM {prefix}fs_free('<ts>') $$)`
  with **`SUM(total)` / `SUM(avail)`**.

Notes on the aggregation semantics (accepted as a `df` display convention):
- **Summing** free space across nodes gives a total. A single large file cannot exceed one node's free space
  (chunks are co-located by `data_id`), but the summed `df` figure follows the convention of distributed FSes
  (Ceph/Gluster) → **note it**.
- The tablespace FS is shared with other DBs / other tables → what you see is "disk free space", not
  "pgfs-only free space". As a `df` meaning that is in fact appropriate.
- With `replication_factor > 1`, free space is double-counted → dividing by the effective capacity is needed
  (future work).

## C# wiring

[`FileSystem.StatFS`](../src/mount/src/FileSystem.cs) (and the Windows volume free space on the assign side)
calls `SELECT total, avail FROM {prefix}statfs()` through an `Api` method.

**One more fallback on the C# side**: if the function does not exist / errors / `avail IS NULL`, fall back to
[`GetCapacityBytes`](../src/lib/src/Api/Api.cs) − [`GetTotalUsedBytes`](../src/lib/src/Api/Api.cs)
(= nominal capacity − DB size). This keeps "plain PG, an old endpoint, no df" from breaking.

The full fallback chain:

```
Filesys::Df  →  df command  →  (function missing/failed)  →  nominal capacity on the C# side
```

**Cache**: tools hammer `statfs`. Calling `run_command_on_workers` every time is expensive, so the C# side
**caches the result for a few seconds**.

**Privileges**: creating an untrusted-PL function requires superuser (mkfs `--super`). Execution runs as the
pgfs user, so grant `SECURITY DEFINER` + `GRANT EXECUTE`. The `current_setting('data_directory')` /
`pg_tablespace_location` inside `fs_free` are limited to superuser / `pg_read_all_settings`, so unless
`{prefix}fs_free` and `{prefix}statfs` are **`SECURITY DEFINER` (owner=superuser)** the calling role hits
`permission denied`. Citus's `run_command_on_workers` also requires superuser.

## mkfs option `--statfs` (3 modes)

This is a property of the whole filesystem, so it is **stored in the DB and shared across all clients**
(the same `SaveTo.Db` as `audit.enabled`).

| Item | Value |
|---|---|
| CLI | `--statfs <auto\|require\|nominal>` (alias `--statfs-mode`) |
| scope.key | **`app.statfs`** (C# side `Schema.Statfs.Mode`) |
| Field type | `StringField` (no enum type, so mkfs validates the value; anything other than `auto/require/nominal` is an error) |
| Default | `auto` |
| SaveTo | **`Db`** (an FS property shared by all clients; mkfs stores it in `pgfs_settings`, mount/assign read it at startup) |
| AppliesTo | `Tool.Mkfs` (CLI only on mkfs; mount/assign only read from the DB) |

### Mode behavior

| Mode | plperlu | Functions created | When plperlu is absent |
|---|---|---|---|
| **`auto`** (default) | use if present | real measurement (`statvfs`+aggregation) if present; otherwise none, nominal fallback | falls back to nominal |
| **`require`** | required | the measuring functions | **mkfs fails** (errors out if `CREATE EXTENSION plperlu` is not possible) |
| **`nominal`** | not installed | (functions dropped) | — (plperlu is never touched) |

(The mode names rename the user-specified must/never/default to the pgfs idiom: must→`require` /
never→`nominal` / default→`auto`. The value `nominal` matches the existing code's wording "nominal capacity".)

### Handling of nominal

In `nominal`, the entry point `{prefix}statfs()` / `fs_free` / `statvfs` are **dropped**, and under
`auto`+plperlu-absent no functions are created. In both cases `Api.GetStatFs` skips the server query entirely
when `app.statfs=nominal` and falls back to nominal capacity (`GetCapacityBytes` − `GetTotalUsedBytes`).
A re-`mkfs` from `require`→`nominal` is the authority, so the measuring functions are removed reliably.

## Windows PostgreSQL support (can be added later)

This feature runs **on the PG server side**, so what matters is the *PG host's OS*. The pgfs clients
(Linux/Windows) are irrelevant.

- **PG on a Windows host**: neither `os.statvfs` nor `df` exists → the three-tier fallback above kicks in, and
  `auto` automatically **degrades to nominal**. So PG on Windows works without breaking (nominal display).
  Real Windows measurement can be added later by swapping only the **body** of `{prefix}statvfs` (e.g. calling
  the Win32 `GetDiskFreeSpaceEx` equivalent from plperlu). **Because the `{prefix}statfs()` signature is
  unchanged, neither clients nor the schema need touching.**
- **Citus on Windows**: Citus is effectively **Linux-only** (distributed only as Linux packages + Linux
  docker). So "Windows multi-node aggregation" need not be considered. In the Windows-client (Dokan) →
  Linux-Citus-server topology, `df` simply **receives the server-side measured value**.

## Implementation summary

1. `app.statfs` in `Schema` (`StringField`, `SaveTo=Db`, `AppliesTo=Mkfs`, default `auto`) + value validation in mkfs.
2. mkfs CLI `--statfs` / `--statfs-mode` wiring ([Schema.Statfs.Mode](../src/lib/src/Config/Schema.cs)).
3. [`Initializer.CreateStatfsFunctionsAsync`](../src/mkfs/src/Initializer.cs):
   - `auto`/`require`: `CREATE EXTENSION IF NOT EXISTS plperlu` (fail under require if impossible, nominal fallback under auto)
     → create `{prefix}statvfs` (plperlu) / `{prefix}fs_free` / `{prefix}statfs`. **`fs_free`/`statfs` are `SECURITY DEFINER`**.
   - `nominal`: **DROP** the existing `{prefix}statfs`/`fs_free`/`statvfs`. `auto`+plperlu-absent: create no functions.
   - On Citus, place statvfs/fs_free on every node with `run_command_on_all_nodes`; the `statfs` entry point is
     coordinator-only (worker aggregation via `run_command_on_workers`, local on a single node).
4. [`Api.GetStatFs()`](../src/lib/src/Api/Api.cs) (→ `SELECT total, avail FROM {prefix}statfs()`) + a few-second cache + nominal fallback when the function is missing/fails.
5. Switch [`FileSystem.StatFS`](../src/mount/src/FileSystem.cs) (mount) and assign [`GetDiskFreeSpace`](../src/assign/src/FileSystem.cs) to go through `GetStatFs()`.
6. (future) double-count correction for replication_factor>1 / real Windows measurement / all-node ensure at mount time / a C implementation.

## Design note on Citus multi-worker aggregation

The Citus branch of `{prefix}statfs()` (summing the `fs_free` of shard-holding workers via
`run_command_on_workers`) is verified by building a custom citus image with plperl in
[tests/docker/Dockerfile.citus-plperl](../tests/docker/Dockerfile.citus-plperl) — the `citusdata/citus` image
has no plperl — and running [tests/citus/statfs.sh](../tests/citus/statfs.sh) (coord + worker1).

### Type consistency of the aggregation branch (a gotcha)

In the Citus worker-aggregation branch of `{prefix}statfs()` ([StatfsEntrySql](../src/mkfs/src/Initializer.cs)),
writing

```sql
SELECT sum(split_part(r.result, ',', 1)::bigint), sum(...) FROM run_command_on_workers(...)
```

fails because PostgreSQL's **`sum(bigint)` returns `numeric`**. The function declares
`RETURNS TABLE(total bigint, avail bigint)`, so at runtime it errors with
`structure of query does not match function result type` (`numeric` vs `bigint`). **Non-Citus and
single-node Citus (nworkers=0) return the local `fs_free` directly and never take this branch**, so it only
surfaces once a multi-worker Citus cluster is up. Cast explicitly to the declared type with `sum(...)::bigint`.

### Verification assertions (statfs.sh, 7 items)

| # | Content |
|---|---|
| R1 | with `--statfs require`, `pgfs_statfs()` exists on the coordinator |
| R2 | `fs_free`/`statvfs` are distributed to worker1 too (`run_command_on_all_nodes`) |
| R3 | the coordinator's `pgfs_statfs()` returns `total>0 / 0<avail<=total` |
| R4 | the return value matches worker1's `fs_free()` (aggregation = one worker; no coord double-count) |
| R5 | **proof of mechanism**: even after dropping only the coord-local `fs_free` under `citus.enable_ddl_propagation=off` (it survives on the worker), `pgfs_statfs()` still returns a value = it goes through `run_command_on_workers` |
| A1 | `--statfs auto` (plperl enabled) also returns a measured value |
| N1 | with `--statfs nominal`, `statfs`/`fs_free`/`statvfs` are dropped on both coord and worker (nominal fallback) |

Note: worker1 shares the same physical disk as coord, so its return value ≈ the host `df`. With two workers
the same disk would be double-counted (a limit of the test topology; one worker is enough to verify the
aggregation *mechanism*). The R5 drop must be wrapped in `citus.enable_ddl_propagation=off` or Citus would
propagate the drop to the worker too (the worker's `fs_free` would also vanish and `sum()` would become
NULL→empty), which would not prove the mechanism.

### Windows df test

[tests/windows/e2e.ps1](../tests/windows/e2e.ps1) `test_disk_free_space`: asserts that `GetDiskFreeSpace`
(via DriveInfo) returns `total>0` / `0<=avail<=total`. On a DB without the statfs functions installed it takes
the nominal fallback path (`total=1TiB`). The real statfs path is proven by the docker/Citus tests above.

## Related

- [Mount.md](Mount.md) — the FUSE operation StatFS belongs to
- [database.md](database.md) — `pgfs_settings` (where `app.statfs` is stored) / the current `used` from `pg_database_size`
- [support_for_citus.md](support_for_citus.md) — the distributed setup that uses `run_command_on_all_nodes`
- [settings-matrix.md](settings-matrix.md) — `app.statfs`
- [settings-and-plperlu.md](settings-and-plperlu.md) — the `app.plperlu` upper gate and the plperlu×statfs behavior matrix (the precondition for this document's statfs functions using plperlu)
