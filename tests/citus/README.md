# pgfs Citus verification

> For the full list of tests, environment requirements, and the docker-integration analysis, see [docs/tests.md](../../docs/tests.md) (the hub). This README covers the operational details of the Citus verification scripts in this directory.

Verification scripts for the Citus support (distribution and cross-client locking).

## Files

| File | Purpose | Run environment |
|---|---|---|
| [verify.sql](verify.sql) + [verify.cmd](verify.cmd) | Configuration-diagnostic SQL against an empty PGFS that has been `mkfs --clean --citus`'d (= a 1-node Citus on pgsql_server). Checks `citus_tables` / distribution keys / shard placement / EXPLAIN | Windows host -> via ssh pgsql_server |
| [multinode_probe.sh](multinode_probe.sh) | A one-off probe script to confirm Citus behavior (auto-sync / DDL propagation / shard placement / citus_add_local_table_to_metadata etc.). Spins up a 2-node Citus (coord + worker) in docker and runs 13 sections of probe SQL | linux_client (OK even from a stopped docker daemon; cleaned up via trap) |
| [test_matrix.sh](test_matrix.sh) | An **18-case** matrix test for mkfs multi-node Citus (3 initial x 6 target). Spins up a 2-node Citus (coord + worker1) in docker and, per case, runs setup -> mkfs -> state verification -> next | linux_client (same as above) |
| [race_multinode.sh](race_multinode.sh) | Remaining verification for cross-client locking. Spins up a 2-node docker Citus + 2 mount.pgfs and checks, end to end, (i) the Linux e2e 34/34 on multi-node Citus, (ii) cross-client consistency under a concurrent write race (md5/size match), (iii) the EEXIST guarantee under a concurrent mkdir race, (iv) a realistic cumulative size for the pgfs_lock rows | linux_client (same as above) |

## Common prerequisites

- The Linux/Windows e2e ([tests/linux/](../linux/README.md) / [tests/windows/](../windows/README.md)) are **functional tests, not regression tests** for this directory. Their pass history on Citus is recorded separately in [docs/history.md](../../docs/history.md).
- The main purpose of this directory is the **configuration diagnostics** of "was the Citus distribution set up as expected" and the correctness of "mkfs's Citus-flag behavior".

## Environment variables (common to `*.sh`)

The config constants of each script are overridable via environment variables (`${VAR:-default}`). Use them to run docker on a different host/port, or to vary container names for parallel runs.

| Variable | Default (test_matrix / multinode_probe / race_multinode) | Purpose |
|---|---|---|
| `PGFS_PROBE_IMAGE` | `citusdata/citus:latest` | Citus docker image |
| `COORD_NAME` / `WORKER1_NAME` | `pgfs-citus-{matrix,verify,race}-{coord,worker1}` | container names |
| `COORD_PORT` / `WORKER1_PORT` | `15432` / `15433` (race uses `15532` / `15533`) | host published ports |
| `SUPER_USER` / `SUPER_PASSWORD` | `postgres` / `postgres` | super connection (probe uses `PG_USER` / `PG_PASSWORD`) |
| `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` | `pgfs` | PGFS user / DB |
| `MKFS_BIN` / `MOUNT_BIN` | `<repo>/bin/Publish/{mkfs,mount}.pgfs` (host-specific) | binaries under test |
| `PGFS_TEST_LOG` | `/tmp/citus_*.log` | log output path |
| `MOUNT1` / `MOUNT2` / `TOML1` / `TOML2` (race only) | `/tmp/pgfs{1,2}` family | mount point / toml paths |

`verify.cmd` can change its ssh target with `VERIFY_REMOTE` (default `pgsql_server`). `e2e.sh`'s `test_fallback_uname_gname` can swap its psql invocation with `PGFS_TEST_PG_EXEC` (see [docs/tests.md](../../docs/tests.md)).

## verify.sql / verify.cmd (1-node configuration diagnostics)

Checks the configuration of a **live Citus** (pgsql_server, Citus 13.1.1) that has been `mkfs --clean --citus`'d.

```cmd
tests\citus\verify.cmd
```

Or manually:

```bash
ssh pgsql_server 'sudo -u postgres -i psql pgfs' < tests/citus/verify.sql
```

Expected output (details in the [verify.sql](verify.sql) comments):

- `citus_tables`: `pgfs.pgfs_inode` / `pgfs.pgfs_data` / `pgfs.pgfs_data_chunk` / `pgfs.pgfs_lock` are `distributed`, `pgfs.pgfs_settings` is `local`
- distribution keys: `parent_id` / `id` / `data_id` / `target_id` in that order
- colocation: the 4 distributed tables are in the default colocation group (auto-colocated by a BIGINT distribution key + the same shard_count)
- shard count: `citus.shard_count` (default 32)
- `pg_dist_node`: only the coordinator (this machine), `shouldhaveshards = true`

## multinode_probe.sh (confirming Citus behavior)

A script that confirms, on a 2-node docker Citus, the behaviors the design assumed "Citus would do for us". It was used to decide "is a per-worker citus_set_coordinator_host call necessary?" while designing distribution.

```bash
# on linux_client
bash tests/citus/multinode_probe.sh
```

Hypotheses verified (details in the [multinode_probe.sh](multinode_probe.sh) header + per-section comments):

- **[Q2]** Does citus_add_node's auto-sync synchronize the coordinator (groupid=0) row into the worker's `pg_dist_node`? -> **✅ Yes** (the per-worker call is unnecessary)
- **[Q2b]** Does `citus_add_local_table_to_metadata` succeed without `citus_set_coordinator_host` on the worker? -> **✅ Yes**
- **[Schema]** Does the coordinator's `CREATE SCHEMA` propagate to the worker as DDL? -> **✅ propagates**
- **[DropCascade]** Does `DROP SCHEMA CASCADE` clean up the worker shards too? -> **✅ cleaned up**
- **[Idempotent]** Behavior of calling `citus_add_node` again -> ✅ idempotent (no duplicate)
- **[Distribute]** Does `create_distributed_table` create shards on the worker? -> ✅ confirmed via Task Count + shards_on_node
- **[Shouldhaveshards]** Defaults for coordinator / worker in a multi-node setup -> ✅ coordinator=false / worker=true

### How it works

- docker pull `citusdata/citus:latest` (resolved at run time, overridable with `PGFS_PROBE_IMAGE`)
- start 2 containers on a bridge network + port map (coord:15432, worker1:15433)
- configure Citus over a super connection from the coordinator -> run the 13 sections of probe SQL in order
- on exit, a trap restores containers / image / docker daemon to their **original state** (if the daemon was already running, leave it alone)
- log at `/tmp/citus_multinode_probe.log`

## test_matrix.sh (mkfs multi-node Citus 18-case matrix)

Confirms that the combinations of mkfs --citus / --worker / --clean drive the 3 branches of `EnsureDatabaseAsync` (fresh setup / keep-existing guard / drop->rebuild on --clean) as intended.

```bash
# on linux_client (the mkfs.pgfs binary is needed at <repo>/bin/Publish/mkfs.pgfs)
bash tests/citus/test_matrix.sh
```

### Matrix (3 x 6 = 18 cases)

**Initial state**:
- I1: no DB
- I2: 1-node Citus configuration (= already `mkfs --clean --citus`'d)
- I3: coord + worker configuration (= already `mkfs --clean --citus --worker w1`'d)

**Target operation**:
- Ta: `mkfs --clean` (no citus) — reset / build to non-Citus
- Tb: `mkfs --clean --citus` — reset / build to 1-node Citus
- Tc: `mkfs --clean --citus --worker w1` — reset / build to coord+worker
- Td: `mkfs` (no --clean, no citus) — keep existing (full build when new)
- Te: `mkfs --citus` (no --clean) — keep existing (1-node Citus build when new)
- Tf: `mkfs --citus --worker w1` (no --clean) — keep existing (coord+worker build when new)

Each case runs setup_I* -> run_mkfs -> verify_state (coord pgfs DB exists / 5 tables / pg_dist_node count / shouldhaveshards / distributed-local counts in citus_tables).

### How it works

- run 2 PG containers in `--network host` mode, each listening on a unique host port (15432 / 15433)
  - with a bridge + port-map approach, `citus_set_coordinator_host('localhost', host_port)` makes the workers see the wrong "localhost", so host networking lets all 3 reference each other at the same address
- POSTGRES_HOST_AUTH_METHOD=trust + citus.node_conninfo=sslmode=disable pass through the SSL/auth config
- mkfs runs on the host (linux_client), connects to coord at localhost:15432, and specifies the worker with `--worker localhost:15433`
- log at `/tmp/citus_test_matrix.log`
- on exit, a trap restores containers / image / docker daemon to their original state

### Result

**18/18 PASS** (Citus 14.0.0 + docker on linux_client).

## race_multinode.sh (remaining cross-client locking verification)

A script that verifies the cross-client locking integrated into the Api, on a multi-node Citus environment. It spins up coord + worker1 in docker, runs 2 mount.pgfs processes in parallel, and checks the 4 items of the locking verification checklist end to end.

```bash
# on linux_client (the mkfs.pgfs / mount.pgfs binaries are needed under bin/Publish/)
bash tests/citus/race_multinode.sh
```

### Checks (4 items)

| Test | Content | Guarantee verified |
|---|---|---|
| Test 1 | The 34 cases of [tests/linux/e2e.sh](../linux/e2e.sh) on multi-node Citus | distribution + locking work on multi-node, with no regression including cross-shard hops |
| Test 2 | Concurrent dd from 2 clients to the same file (8MiB x 4 rounds, urandom vs zero) -> md5/size match on both clients | `LockData(dataId)` serializes the writers; combined with the PG row lock there is no chunk-level torn write / cross-client consistency holds |
| Test 3 | Concurrent mkdir of the same name into the same parent from 2 clients (20 rounds x serial + parallel) | the `(parent_id, name)` UK guarantees cross-shard consistency (even across shards, "two of the same name under the same parent" is impossible from any client) |
| Test 4 | The `pgfs_lock` row count + relation size after runs 1-3 complete | rows accumulate because we do not DELETE, but ~50 bytes/row x thousands = under 1MB (consistent with the design figure of under 100MB for 1M rows) |

### How it works

- run 2 PG containers in `--network host` mode listening on 15532/15533 (same reason as test_matrix.sh)
- initialize with `mkfs --clean --citus --worker localhost:15533`
- generate 2 pgfs.toml at `/tmp/pgfs{1,2}.toml` (`database.notify_enabled = true` enables cross-client notification)
- start 2 mount.pgfs in the background with `-m /tmp/pgfs{1,2} -f`
- [tests/linux/e2e.sh](../linux/e2e.sh)'s `pg_exec` is hardcoded to go through ssh pgsql_server by default, but it is overridable with the `PGFS_TEST_PG_EXEC` environment variable, so it is swapped for psql through the docker container ([test_fallback_uname_gname](../linux/e2e.sh) support)
- on exit, a trap runs fusermount3 -> stop docker -> restore the daemon to its original state
- logs at `/tmp/citus_race_multinode.log` (main) + `/tmp/pgfs{1,2}.mount.log` (mount.pgfs)

### Result

**4/4 PASS** (Citus 14.0.0 + docker on linux_client). pgfs_lock rows=178 / size=768kB.

## Verifying cross-shard rename

`test_rename_into_subdir` (in both the Linux and Windows e2e) covers a **rename into a different parent**, so if it passes on a Citus environment, cross-shard rename is also demonstrated. The implementation is the DELETE+INSERT (OVERRIDING SYSTEM VALUE) path of `Rename` in [Api.cs](../../src/lib/src/Api/Api.cs).

To confirm cross-shard rename explicitly at the SQL level, see the EXPLAIN block in [verify.sql](verify.sql).

## Related documents

- Design: [docs/support_for_citus.md](../../docs/support_for_citus.md)
- Design decisions: [docs/history.md](../../docs/history.md)
- DDL: [docs/ddl/](../../docs/ddl/README.md)
- mkfs CLI: [docs/Mkfs.md](../../docs/Mkfs.md)
