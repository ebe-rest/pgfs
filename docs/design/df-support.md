# df support (statfs reporting the real free space)

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: the mechanism by which `df` / `statfs` return
> **the backend's real free disk space**. The `pgfs_statfs()` / `fs_free()` built with plperlu, the three-stage
> fallback, the 3 modes of `app.statfs` and the nominal fallback, the aggregation across Citus nodes and the
> results of the verification on real hardware all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../Mkfs.md](../Mkfs.md) | **How `--statfs` / `--statfs-mode` are given on the CLI, and the default** |
> | [../Mount.md](../Mount.md) | **The user-facing behaviour** of the FUSE operation StatFS belongs to |
> | [settings-and-plperlu.md](settings-and-plperlu.md) | The `app.plperlu` higher gate and the plperlu-against-statfs behaviour matrix (the preconditions this document's functions have to satisfy) |
> | [settings-matrix.md](settings-matrix.md) | The default and the reload policy of `app.statfs` (the app.* section) |
> | [database.md](database.md) | `{prefix}settings` (where `app.statfs` is stored) and the current "used" derived from `pg_database_size` |
> | [support_for_citus.md](support_for_citus.md) | Distributed setup in general, using `run_command_on_all_nodes` |
> | [../tests.md](../tests.md) | The list of the tests and the environment requirements (including this document's `statfs.sh`) |

The design memo for making `df /mnt/pgfs` (the FUSE statvfs / the Windows volume free space) return
**the real free disk space of the backend PostgreSQL's tablespace**. With multi-node Citus it gathers from every
node and sums them.

The status: **implemented, with every path verified on real hardware (the non-Citus full chain plus the Citus
multi-worker aggregation)**. This document is the source of truth for the implementation.
The verification of the Citus multi-worker aggregation **caught and fixed a type bug in the worker aggregation
branch** (`sum(bigint)` returns numeric and was being passed to a bigint declaration, so it failed) (below).

The implementation: [Schema.Statfs.Mode](../../src/core/src/Config/Schema.cs) (`--statfs`) /
[Initializer.CreateStatfsFunctionsAsync](../../src/mkfs/src/Initializer.cs) (creating the functions) /
[Api.GetStatFs](../../src/core/src/Api/Api.cs) (the call plus a few seconds of caching plus the nominal
fallback) / the wiring of [the mount StatFS](../../src/fuse/src/FileSystem.cs) plus
[the assign GetDiskFreeSpace](../../src/dokan/src/FileSystem.cs).

### The prototype verification on real hardware (the PG server)

In a scratch database, `CREATE EXTENSION plperlu` was run, a plperlu function that shells out to `df` was
created, and it was run against the `data_directory` and confirmed (the scratch database was DROPped afterwards
and the production database was untouched):

- ✅ `CREATE EXTENSION plperlu` / `CREATE FUNCTION ... LANGUAGE plperlu` succeeded
- ✅ The function runs **with the postgres OS user's privileges** and could read the `0700` `data_directory`
  that the ssh user is denied
- ✅ The return values `total=2029890568192 / avail=1683165110272` **agreed exactly** with
  `sudo -u postgres df` and `df /database`
- ⚠️ **`Filesys::Df` is not installed** -> in the three-stage fallback, **tier 1 is absent and tier 2 (the `df`
  command) is what works**. A correct value comes back through the `df` path, so no CPAN addition is needed. The
  implementation may be written with tier 2 as the main path.

### The verification on real hardware after implementing it (the non-Citus full chain)

From Windows, `mkfs --statfs require` (non-Citus, schema=pgfs) was run against a scratch database on the PG
server, then `SELECT total, avail FROM pgfs.pgfs_statfs()` was called **as the pgfs (non-superuser) role** and
compared against a real `df` (the database was DROPped afterwards):

- ✅ `pgfs_statfs() = (2029890568192, 1683160715264)` **agreed exactly** with `df -B1 /database`
- 🐛 **Caught and fixed by the verification**: without making the functions `SECURITY DEFINER`, the
  `current_setting('data_directory')` and `pg_tablespace_location` inside `fs_free` **run with the calling
  role's (pgfs's) privileges and give `permission denied`** (they are limited to a superuser or
  `pg_read_all_settings`). It was resolved by making `{prefix}fs_free` and `{prefix}statfs`
  `SECURITY DEFINER` (owner=postgres). Citus's `run_command_on_workers` needs a superuser too, so making
  `statfs` a DEFINER is mandatory.

### The verification through a real mount on docker (the tests/docker harness)

`df` **through a real FUSE mount** was confirmed on `tests/docker` (a single PG = postgres:17 plus a mount
container):

- ✅ **The auto-degrade**: postgres:17 does not carry plperlu -> `--statfs auto` caught
  `plperlu is not available`, fell back to nominal and the mkfs succeeded. There are 0 statfs functions on the
  coordinator. `df /mnt/pgfs` = `1.0T` (= the nominal `max_file_size`).
- ✅ **The real path (live)**: `postgresql-plperl-17` was installed on the coordinator, mkfs was re-run with
  `--statfs require` and it was remounted -> `df /mnt/pgfs` changed to
  **`393G` (= the docker host's real disk)**. The return value of `pgfs_statfs()` agreed exactly with the
  container's `df $PGDATA`.
  -> That confirms **the full chain FUSE -> `StatFS` -> `Api.GetStatFs` -> `pgfs_statfs()` returns the real free
  space through a real mount**.

### What remains (unverified / missing)

- **The `run_command_on_workers` aggregation across Citus workers**: unverified. `citusdata/citus:latest`
  **does not carry plperl**, so verifying it needs a custom citus image with plperl in it (citus plus
  `postgresql-plperl-N`). A single-node Citus takes the `nworkers=0` branch and uses the local `fs_free` = the
  same path that has been verified.
- **The automated tests**: `test_statfs` in `tests/linux/e2e.sh` was strengthened as far as checking the values
  (total > 0 and avail <= total), but **a dedicated test (the equivalent of tests/citus/statfs.sh)** that tells
  the measured value from the nominal one and exercises the Citus aggregation is not in place.
- **The Windows e2e suite**: there is no dedicated test for `GetDiskFreeSpace` (there is room to add an
  assertion of roughly "the free space > 0").
- The degrade of `auto` with plperlu absent has been confirmed on docker.

---

## The motivation and where things stand

The current [`FileSystem.StatFS`](../../src/fuse/src/FileSystem.cs) returns the following pseudo disk
information:

- The capacity = [`Api.GetCapacityBytes`](../../src/core/src/Api/Api.cs) = `file_system.max_file_size`
  (the nominal capacity)
- The used = [`Api.GetTotalUsedBytes`](../../src/core/src/Api/Api.cs) = `pg_database_size(...)`
- The free = the capacity - the used

In other words it is "the nominal capacity minus the database size", **not the real free space of the underlying
FS**. The aim is to make `df` real.

### Why it cannot be obtained with plain SQL

PostgreSQL has no SQL that returns "the free space of the FS under a tablespace". `pg_tablespace_size()` returns
only **the used**. Knowing the free space requires a `statvfs(2)` on the underlying directory, which SQL cannot
reach. **A server-side function** therefore has to do the `statvfs`.

---

## The approach taken: a "plain CREATE FUNCTION" in plperlu (not made an extension)

Of the three options considered, **(3), creating a plain `CREATE FUNCTION` in plperlu rather than an
extension**, was taken.

| The option | Compilation | Distribution | How it fits mkfs |
|---|---|---|---|
| (1) A C extension | Needed (a .so) | **A binary per PG major version x OS x architecture** into each node's pkglibdir. The same build and distribution operation as introducing Citus (the server headers are needed) | ✗ Heavy |
| (2) An extension of PL functions | Not needed | Two text files, a `.control` plus a `.sql`, **physically placed in each node's SHAREDIR**. `CREATE EXTENSION` is propagated automatically by Citus but placing the files is separate | △ Placing the files needs ssh/scp in mkfs = a big rework |
| **(3) A plain CREATE FUNCTION (plperlu)** | Not needed | **SQL text only.** Citus distributes it to every node with `run_command_on_all_nodes($$ CREATE FUNCTION ... $$)` | ✅ **mkfs can support it while staying pure SQL** |

### What decided (3)

- **mkfs is a "pure SQL tool" today** (only an Npgsql connection; it has no means of touching a node's
  filesystem). An extension ((1) or (2)) needs files placed in each node's `SHAREDIR/extension/`, which would be
  **a big rework** giving mkfs ssh connection details as arguments.
- With (3) the function body is SQL text, so **it can be distributed to every node with SQL alone through
  `run_command_on_all_nodes`**. No file copying and no ssh. mkfs only adds a few statements to the existing
  `--super` path (the Citus setup).
- No compilation and no architecture dependence.
- The one concession: it is not an extension, so **it is not propagated automatically to a worker added
  later** -> mkfs (or a future ensure at mount time) re-runs `CREATE OR REPLACE` on every node. It is absorbed
  the same way as the Citus extension setup.

### The server preconditions (confirmed)

The PL situation on the PG server (PG 17.5 built from source):

- `plpgsql` installed / `plperl` and `plperlu` **available** (`plperl.so` is present, not installed) /
  `plpython3u` **absent** (built without `--with-python`)
- -> **Perl is the only choice.** It is enabled with `CREATE EXTENSION IF NOT EXISTS plperlu` (as a superuser;
  `plperl.so` has been on every node since the PG build, so no file copying is needed).

---

## The function design (the signature is fixed)

They are placed according to the schema plus the table prefix (for example `pgfs.pgfs_statfs`). The entry point
clients call, `{prefix}statfs()`, **keeps its signature unchanged** and only its body is swapped by mode and
environment (to leave room for Windows support and for a move to C).

| The function | The language | Its role |
|---|---|---|
| `{prefix}statvfs(dir text) → (total bigint, avail bigint)` | **plperlu** | Purely statvfs's `dir` and returns the total capacity and the available bytes (the untrusted part is only here) |
| `{prefix}fs_free(tablespace text) → (total, avail)` | plpgsql | Resolves **that node's** tablespace directory and calls `{prefix}statvfs` |
| `{prefix}statfs() → (total, avail)` | plpgsql | **The entry point C# calls.** Local when not Citus, and aggregated across every node with Citus |

### Resolving the directory (inside `fs_free`, node-local)

- `pg_default` / empty / `pg_global` -> `current_setting('data_directory')` (the substance is `base/`)
- A named tablespace -> `pg_tablespace_location(oid)`
- **Each node resolves its own `data_directory`**, so the tablespace name is passed and resolved on the node
  side (the coordinator's path must not be distributed).

### The three-stage fallback of the statvfs body (inside plperlu, at runtime)

```perl
# a sketch of the body of {prefix}statvfs(dir)
# 1) use Filesys::Df if it is there
my $r = eval { require Filesys::Df;
               my $d = Filesys::Df::df($_[0], 1);  # a block size of 1 = in bytes
               return [$d->{blocks}, $d->{bavail}]; };
return $r if $r && @$r;
# 2) if not, shell out to the df command (the strength of being untrusted)
my @o = `df -B1 --output=size,avail "$_[0]" 2>/dev/null`;
# ... parse the second line into [size, avail] ...
# 3) if there is no df either, undef -> the caller falls back to nominal
return undef;
```

### The Citus aggregation (inside `statfs`)

- **Not Citus**: `SELECT * FROM {prefix}fs_free('<the configured tablespace>')`
- **Citus**: the results of
  `run_command_on_all_nodes($$ SELECT total||','||avail FROM {prefix}fs_free('<ts>') $$)` summed with
  **`SUM(total)` / `SUM(avail)`**.

Caveats on the aggregation semantics (accepted on the premise that it goes into df):
- **SUM**ming the free space across nodes gives a total. A single large file cannot exceed one node's free space
  (the chunks are colocated by `data_id`), but expressing df as a total follows the convention of distributed
  filesystems (Ceph, Gluster) -> **it is noted**.
- The tablespace's FS is shared with other databases and tables too -> what comes out is not "pgfs's own free
  space" but "the disk's free space". As the meaning of `df` that is arguably more apt.
- With `replication_factor > 1` the free space is double-counted -> dividing by the effective capacity needs
  consideration (for the future).

---

## The wiring on the C# side

[`FileSystem.StatFS`](../../src/fuse/src/FileSystem.cs) (and the Windows volume free space on the assign side)
call `SELECT total, avail FROM {prefix}statfs()` through a new `Api` method.

**One more fallback on the C# side**: when the function does not exist, errors, or `avail IS NULL`, it falls
back to the current [`GetCapacityBytes`](../../src/core/src/Api/Api.cs) -
[`GetTotalUsedBytes`](../../src/core/src/Api/Api.cs) (= the nominal capacity minus the database size). That
keeps it from breaking on "a plain PG, an old target, or no df".

The whole fallback chain:

```
Filesys::Df  ->  the df command  ->  (no function / a failure)  ->  the nominal capacity on the C# side
```

**The caching**: `statfs` is hammered by the tools. Hitting `run_command_on_all_nodes` every time is heavy, so
the result is **cached for a few seconds** on the C# side.

**The privileges**: creating a function in an untrusted PL needs a superuser (mkfs's `--super`). It is executed
as the pgfs user, so `SECURITY DEFINER` plus a `GRANT EXECUTE` are given.

---

## The mkfs option `--statfs` (3 modes)

It is a property of the whole filesystem, so it is **stored in the database and shared by every client**
(the same `SaveTo.Db` as `audit.enabled`).

| The item | The value |
|---|---|
| The CLI | `--statfs <auto\|require\|nominal>` (the alias `--statfs-mode`) |
| The scope.key | **`app.statfs`** (formerly `statfs.mode`, then `pgfs.statfs`, then `app.statfs`; the C# is `Schema.Statfs.Mode`) |
| The Field type | `StringField` (there is no enum type, so mkfs validates the value; anything other than `auto/require/nominal` is an error) |
| The default | `auto` |
| SaveTo | **`Db`** (an FS property common to every client. mkfs saves it into `pgfs_settings` and mount/assign read it at startup) |
| AppliesTo | `Tool.Mkfs` (the CLI is mkfs only; mount and assign only read it from the database) |

### The behaviour of each mode

| The mode | plperlu | The functions created | When plperlu is absent |
|---|---|---|---|
| **`auto`** (the default) | Used if it is there | The measuring ones (`statvfs` plus the aggregation) if it is there, otherwise a nominal stub | Falls back to the nominal stub |
| **`require`** | Mandatory | The measuring ones | **mkfs fails** (it errors out because `CREATE EXTENSION plperlu` is not possible) |
| **`nominal`** | Not installed | The nominal stub only | - (plperlu is never touched to begin with) |

(The user's must/never/default were renamed the pgfs way: must -> `require`, never -> `nominal`, default ->
`auto`. The value `nominal` was aligned with the existing code's word for "the nominal capacity".)

### The nominal stub

With `auto` and no plperlu, and with `nominal`, the entry point `{prefix}statfs()` is created as
**a plain SQL function**:

```sql
CREATE OR REPLACE FUNCTION {schema}.{prefix}statfs()
RETURNS TABLE(total bigint, avail bigint) LANGUAGE sql AS $$
  SELECT
    (SELECT (value::text)::bigint FROM {schema}.{prefix}settings
       WHERE scope='file_system' AND key='max_file_size') AS total,
    -- avail = the nominal capacity - the database size (the same calculation as the current C#, server-side)
    GREATEST(0,
      (SELECT (value::text)::bigint FROM {schema}.{prefix}settings
         WHERE scope='file_system' AND key='max_file_size')
      - pg_database_size(current_database())) AS avail;
$$;
```

(How an unlimited `-1` max_file_size is treated is aligned with C#'s `GetCapacityBytes`. The signature of
`{prefix}statfs()` is identical between the stub and the measuring version, so C# always calls it the same way.)

---

## Support for PostgreSQL on Windows (can be added later)

This feature runs **on the PG server side**, so what matters is *the PG host's OS*. The pgfs clients
(Linux/Windows) are irrelevant.

- **PG on a Windows host**: there is neither `os.statvfs` nor `df` -> the three-stage fallback above applies as
  it is, and with `auto` it **degrades to nominal automatically**. In other words it works without breaking on a
  Windows PG right now (showing the nominal capacity). A real measurement on Windows can be added later by
  swapping **only the body** of `{prefix}statvfs` (calling the equivalent of the Win32 `GetDiskFreeSpaceEx` from
  plperlu, say). **The signature `{prefix}statfs()` is unchanged, so neither the clients nor the schema are
  touched.**
- **Citus on Windows**: Citus is effectively **Linux-only** (it is distributed only as Linux packages and Linux
  docker images, and there is no Windows build of the Citus extension). "Multi-node aggregation on Windows" is
  therefore not a consideration. The real PG server is Linux, so the distributed case is always Linux. In the
  arrangement of a Windows client (Dokan) against a Linux Citus server, df **receives the server-side measured
  value as it is** too.
  - Note: "Citus = Linux-only" needs confirming against a primary source (only if distribution on a Windows
    server is ever seriously considered). It is unnecessary for the current use.

---

## The implementation TODO (a checklist)

1. ✅ `statfs.mode` in the `Schema` (a `StringField`, `SaveTo=Db`, `AppliesTo=Mkfs`, `auto` by default) plus the
   value validation in mkfs.
2. ✅ Wiring the mkfs CLI `--statfs` / `--statfs-mode`
   ([Schema.Statfs.Mode](../../src/core/src/Config/Schema.cs)).
3. ✅ `Initializer.CreateStatfsFunctionsAsync`:
   - `auto` / `require`: `CREATE EXTENSION IF NOT EXISTS plperlu` (a failure under require fails; auto falls
     back to nominal) -> create `{prefix}statvfs` (plperlu), `{prefix}fs_free` and `{prefix}statfs`.
     **`fs_free` and `statfs` are `SECURITY DEFINER`.**
   - `nominal`: **DROP** the existing `{prefix}statfs` / `fs_free` / `statvfs` to go back to the nominal
     capacity for certain (making a re-mkfs from require to nominal authoritative). With `auto` and no plperlu,
     no function is created and it falls back to nominal. For simplicity of implementation no SQL stub is
     placed. `Api.GetStatFs` skips the server query entirely when `app.statfs=nominal`.
   - With Citus, statvfs and fs_free are placed on every node with `run_command_on_all_nodes` and the `statfs`
     entry point is on the coordinator only (the worker aggregation is `run_command_on_workers`, and a single
     node is local).
4. ✅ `Api.GetStatFs()` (-> `SELECT total, avail FROM {prefix}statfs()`) plus a few seconds of caching plus the
   nominal fallback when the function is absent or fails.
5. ✅ Replacing `FileSystem.StatFS` (mount) and assign's `GetDiskFreeSpace` with calls through `GetStatFs()`.
6. **Verified**: the non-Citus full chain agreeing on real hardware / the functions being DROPped going from
   `require` to `nominal` / the degrade of `auto` with no plperlu / `df` through a real mount on docker
   (the nominal 1T becoming a real 393G under require). Remaining: the Citus multi-worker aggregation (it needs
   a citus image with plperl) / a dedicated automated test (tests/citus/statfs.sh) / a Windows
   `GetDiskFreeSpace` test.
7. (Future) Correcting the double counting with replication_factor > 1 / a real measurement on Windows / an
   ensure across every node at mount time / a move to C.

---

## Verifying the Citus multi-worker aggregation (completed)

The Citus branch of `{prefix}statfs()` (summing the `fs_free` of the shard-holding workers through
`run_command_on_workers`) was the only unverified path. The obstacle was that `citusdata/citus:latest` has no
plperl (and there is no ready-made citus-plus-plperl image), so it was built by hand with
[tests/docker/Dockerfile.citus-plperl](../../tests/docker/Dockerfile.citus-plperl) and verified with
[tests/citus/statfs.sh](../../tests/citus/statfs.sh) (coord + worker1). **7/7 PASS on real hardware on the
Linux client.**

### 🐛 A product bug caught by the verification - a type mismatch in the worker aggregation branch

The Citus worker aggregation branch of `{prefix}statfs()`
([StatfsEntrySql](../../src/mkfs/src/Initializer.cs)) was

```sql
SELECT sum(split_part(r.result, ',', 1)::bigint), sum(...)::bigint FROM run_command_on_workers(...)
```

but PostgreSQL's **`sum(bigint)` returns `numeric`**. The function is declared
`RETURNS TABLE(total bigint, avail bigint)`, so at runtime it fails with
`structure of query does not match function result type` (`numeric` against `bigint`).
**Neither the non-Citus case nor single-node Citus (nworkers=0) goes through that branch**, returning the local
`fs_free` directly, so it only surfaced once a multi-worker Citus was stood up. The fix is simply an explicit
cast `sum(...)::bigint` to the declared type.

### The verification procedure (to reproduce)

```bash
docker build -t pgfs-citus-plperl -f tests/docker/Dockerfile.citus-plperl tests/docker/   # statfs.sh builds it automatically if it is not given
MKFS_BIN=<publish>/mkfs.pgfs bash tests/citus/statfs.sh                                    # coord:15552 + worker1:15553
```

### The assertions (statfs.sh, all 7 PASS)

| # | What it is | The result |
|---|---|---|
| R1 | `pgfs_statfs()` exists on the coordinator with `--statfs require` | ✅ |
| R2 | `fs_free` / `statvfs` are distributed to worker1 too (`run_command_on_all_nodes`) | ✅ |
| R3 | The coordinator's `pgfs_statfs()` returns `total>0 / 0<avail<=total` | ✅ (393 GiB / 216 GiB) |
| R4 | The return value agrees with worker1's `fs_free()` (the aggregation = one worker's worth, with no double counting of the coordinator) | ✅ (the total agrees exactly) |
| R5 | **The proof of the mechanism**: DROPping only the coordinator-local `fs_free` with `citus.enable_ddl_propagation=off` (leaving it on the worker) still has `pgfs_statfs()` return a value = it goes through `run_command_on_workers` | ✅ |
| A1 | `--statfs auto` (with plperl enabled) returns a measured value too | ✅ |
| N1 | With `--statfs nominal`, `statfs` / `fs_free` / `statvfs` are DROPped from both the coordinator and the worker (the nominal fallback) | ✅ |

A note: worker1 runs with `--network host` and shares the same physical disk as the coordinator, so the return
value is about the host's `df`. With 2 workers the same disk would be double-counted (a limitation of the test
setup; one worker is enough to verify the aggregation *mechanism*). Note that R5's DROP has to be wrapped in
`citus.enable_ddl_propagation=off` or Citus propagates the DROP to the worker too (= the worker's `fs_free` goes
as well and the `sum()` becomes NULL and then empty), which would not prove the mechanism.

### The Windows df test

`test_disk_free_space` in [tests/windows/e2e.ps1](../../tests/windows/e2e.ps1): it asserts that
`GetDiskFreeSpace` (through DriveInfo) returns `total>0` and `0<=avail<=total`.
**Verified through a real Dokan mount on real hardware (2026-06-03, Windows e2e 27/27 PASS).**
The `pgfs` database on the PG server has no statfs functions installed, so it is the nominal fallback path
(`P: total=1TiB`). The real statfs path is demonstrated by the docker/Citus work above.
