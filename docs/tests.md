# Tests: catalog, how to run, environment requirements

The **hub document** for all pgfs tests. It collects "what tests exist / how to run them / what environment they need" in one place. The fine-grained options of each runner (flow.ps1 parameters etc.) live in each directory's README, linked from here.

> **Background**: the test environment varies by host (a Windows host driving linux_client / pgsql_server over ssh), so this is also a stock-take for considering whether it can be consolidated onto docker for reproducibility. The docker analysis is in [§docker integration](#docker-integration) at the end.

---

## Test catalog

| Suite | Count | What it checks | Location | Detailed README |
|---|---|---|---|---|
| **Linux e2e** | 36 | every mount.pgfs (FUSE) operation, via real FS operations (incl. POSIX ACL setfacl/getfacl / binary xattr round-trip) | [tests/linux/e2e.sh](../tests/linux/e2e.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **Linux e2e (full docker)** | 36 | the Linux e2e above, run end-to-end in a **single PG + mount container** (no ssh linux_client / host dotnet dependency) | [tests/docker/run.sh](../tests/docker/run.sh) | [tests/docker/README.md](../tests/docker/README.md) |
| **Windows e2e** | 27 | every pgfs.assign (Dokan) operation, via real FS operations (incl. ACL projection Get/SetFileSecurity / df GetDiskFreeSpace) | [tests/windows/e2e.ps1](../tests/windows/e2e.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Citus mkfs matrix** | 18 | the combined behavior of `mkfs --citus / --worker / --clean` (new / keep-existing / rebuild) | [tests/citus/test_matrix.sh](../tests/citus/test_matrix.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus multinode probe** | 13 sections | a one-off probe of Citus behavior (auto-sync / DDL propagation / shard placement etc.) | [tests/citus/multinode_probe.sh](../tests/citus/multinode_probe.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus race multinode** | 4 | cross-client locking + multi-node e2e on multi-node Citus with 2 mount clients | [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Audit-log dedicated** | 12 | multi-node Citus + 1 mount client; per-op recording / caller_* / automatic partition creation (month-rollover mechanism) / 0 rows when audit.enabled=false | [tests/citus/audit.sh](../tests/citus/audit.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **df (statfs) dedicated** | 7 | multi-node Citus (coord+worker1, a self-built plperl image); worker aggregation of `pgfs_statfs()` + the 3 modes `require`/`auto`/`nominal`. R5 proves the aggregation mechanism | [tests/citus/statfs.sh](../tests/citus/statfs.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus verify** | SQL diagnostics | `citus_tables` / distribution key / shard placement / EXPLAIN after a 1-node Citus setup | [tests/citus/verify.sql](../tests/citus/verify.sql) | [tests/citus/README.md](../tests/citus/README.md) |

### Linux e2e (36) categories

Directory operations / basic file operations / data I/O (bytea) / rename / permissions (chmod/chown) / symbolic links / hard links / xattr (**incl. binary NUL/high-byte round-trip**) / POSIX ACL (setfacl/getfacl) / metadata (StatFS/utime) / concurrency / name resolution fallback. The coverage is the ✅ operations of [docs/Mount.md](Mount.md).

### Windows e2e (27) categories

Directory operations / basic file operations / data I/O (bytea) / truncate / rename / attributes (ReadOnly/Hidden/System/Archive) / volume / pattern / concurrency. POSIX-only features (symlink/hardlink/chmod/chown/xattr APIs) are out of scope as DokanNet does not support them; Windows-specific tests are added instead. The coverage is the ✅/⚠️ operations of [docs/Assign.md](Assign.md).

### Latest results

| Suite | Result | Verified on |
|---|---|---|
| Linux e2e | **36/36 ALL PASSED** | single PG (postgres:17) + mount, verified after the xattr-bytea migration. 1-node/multi-node Citus and pgsql_server need a re-mkfs + re-run because of the schema change |
| Linux e2e (full docker) | **36/36 ALL PASSED** | single PG (postgres:17) + mount container, verified on linux_client. Includes `test_fallback_uname_gname` / `test_xattr_binary` |
| Windows e2e | **27/27 ALL PASSED** | verified with a Dokan mount against pgsql_server (incl. df `test_disk_free_space`) |
| Citus mkfs matrix | **18/18 PASS** | Citus docker on linux_client |
| Citus race multinode | **4/4 PASS** | Citus docker on linux_client |
| Audit-log dedicated | **12/12 PASS** | Citus docker on linux_client |
| df (statfs) dedicated | **7/7 PASS** | a self-built Citus image with plperl (coord+worker1) on linux_client |

(The authoritative latest pass status for each suite is its directory README.)

---

## How to run

### Linux e2e

```cmd
REM full flow (rsync -> publish -> mount -> test -> unmount)
tests\linux\flow.cmd
REM test-name filter
tests\linux\flow.cmd xattr
REM tests only (assumes already mounted)
tests\linux\run.cmd
```

Directly on a Linux host:

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

Options (`-NoSync` / `-NoBuild` / `-NoMount` / `-KeepMounted`) and environment variables (`PGFS_TEST_PG_EXEC` etc.) are in [tests/linux/README.md](../tests/linux/README.md).

### Windows e2e

```cmd
REM full flow (mount -> test -> unmount, build skipped by default)
tests\windows\flow.cmd
REM publish first
tests\windows\flow.cmd -Build
REM tests only (assumes already mounted)
tests\windows\run.cmd
```

Details in [tests/windows/README.md](../tests/windows/README.md).

### Citus (all run in bash on linux_client)

```bash
bash tests/citus/test_matrix.sh        # mkfs 18-case matrix
bash tests/citus/multinode_probe.sh    # Citus behavior probe
bash tests/citus/race_multinode.sh     # cross-client locking + multi-node e2e
bash tests/citus/audit.sh              # audit-log dedicated (per-op recording / caller_* / partition / enabled=false)
```

```cmd
REM 1-node Citus configuration diagnostics (Windows host -> ssh pgsql_server)
tests\citus\verify.cmd
```

Details in [tests/citus/README.md](../tests/citus/README.md).

---

## Environment requirements

The environment each suite **currently** needs. Material for the docker consideration.

| Suite | Run host | DB | Mount layer | docker | Remote-host dependency |
|---|---|---|---|---|---|
| Linux e2e | Windows -> ssh Linux | PG (the fallback test additionally needs psql reachability) | libfuse3 | none | **linux_client** (build + mount) |
| Windows e2e | Windows local | PG | Dokan 2.x driver | none | none (local-only) |
| Citus mkfs matrix | ssh Linux (bash) | **docker Citus** (coord + worker1) | none (mkfs only) | **yes** | linux_client (docker daemon) |
| Citus multinode probe | ssh Linux (bash) | **docker Citus** (coord + worker) | none | **yes** | linux_client |
| Citus race multinode | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount runs on the **host**) | DB only on docker | linux_client |
| Audit-log dedicated | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount runs on the **host**) | DB only on docker | linux_client |
| Citus verify | Windows -> ssh pgsql_server | **1-node Citus on pgsql_server** (live) | none | none | **pgsql_server** |

### Common prerequisites

- **.NET 10 SDK** (build time). The remote is specified by the `REMOTE_DOTNET` environment variable (the default is host-specific).
- **PostgreSQL 17+** (the Citus tests use a Citus-enabled image, `citusdata/citus:latest`).
- **Linux**: libfuse3 + the `attr` package (`getfattr`/`setfattr` for the xattr tests; if not installed the xattr tests SKIP).
- **Windows**: the Dokan 2.x driver (`DokanSetup_redist.exe`).
- **Citus**: a docker daemon (each script starts it with `sudo systemctl start docker` even from stopped, and restores it via trap).
- Remote execution requires **passwordless SSH-key login** (linux_client / pgsql_server).

### Override via environment variables

Hardcoded host names, paths, ports, and credentials are **all overridable via environment variables** (the test composition itself is unchanged). When moving to docker or changing hosts, point the scripts elsewhere via environment variables rather than editing them. Precedence is **CLI arg > env var > default**.

| Target | Main environment variables |
|---|---|
| Linux flow.ps1 / run.cmd | `REMOTE` / `REMOTE_REPO` / `MOUNT_POINT` / `REMOTE_SETTING_FILE` / `REMOTE_DOTNET` / `REMOTE_MOUNT_BINARY` |
| Windows flow.ps1 / run.cmd | `MOUNT_ROOT` / `ASSIGN_BINARY` / `ASSIGN_SETTING_FILE` |
| Linux e2e.sh (fallback test) | `PGFS_TEST_PG_EXEC` (replace the full psql invocation) / `TEST_FILTER` |
| Citus `*.sh` | `PGFS_PROBE_IMAGE` / `COORD_NAME` / `WORKER1_NAME` / `COORD_PORT` / `WORKER1_PORT` / `SUPER_USER` / `SUPER_PASSWORD` / `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` / `MKFS_BIN` / `MOUNT_BIN` / `PGFS_TEST_LOG` (details in [tests/citus/README.md](../tests/citus/README.md)) |
| Citus verify.cmd | `VERIFY_REMOTE` (default pgsql_server) |

The full variable list for each suite is in its directory README.

### Remaining hardcoded assumptions (not absorbed by env)

- The symlink race in `dotnet publish`'s parallel restore, caused by linux_client's specific setup where the **remote build target is reached through a symlink**. Worked around by building `-p:RestoreDisableParallel=true` into flow.ps1 (expected to disappear under docker).
- The live 1-node Citus setup that **Citus verify** assumes (`VERIFY_REMOTE` can change the target, but that host must have Citus set up).

---

## docker integration

> Current analysis of the motivation "wouldn't it be easier if everything were on docker". The **single-PG configuration is implemented** ([tests/docker/](../tests/docker/README.md)); the rest is tied to the operational items in [docs/next.md](next.md).

### Already on docker

The DB for the Citus suites is already docker (`citusdata/citus:latest` started as coord/worker with `--network host`). `race_multinode.sh` is a hybrid of **DB on docker + mount on the host**.

### The Linux side can be fully dockerized → **single-PG configuration is implemented** ([tests/docker/](../tests/docker/README.md))

If the mount in `race_multinode.sh` is moved from the host into a container, the Linux e2e becomes self-contained as a **PG container + mount.pgfs container**. The **single-PG (non-Citus) configuration is implemented under [tests/docker/](../tests/docker/README.md)** (multi-stage SDK build + docker-compose):

- **mount.pgfs container** ([Dockerfile.mount](../tests/docker/Dockerfile.mount)): mounts FUSE inside the container with `cap_add SYS_ADMIN` / `devices /dev/fuse` / `security_opt apparmor:unconfined`. The multi-stage build publishes `mount.pgfs`/`mkfs.pgfs` self-contained, then COPYs them into a debian-slim with fuse3 + attr/acl/psql.
- **e2e runs in-container**: exposing the FUSE mount to the host (mount-namespace propagation) is fragile, so `e2e.sh` runs inside the mount container (mount point also in-container). `tests/linux/` is brought in as a read-only bind mount (no rebuild when editing tests).
- **the fallback test**: [run.sh](../tests/docker/run.sh) passes `PGFS_TEST_PG_EXEC="psql -h coord ..."`, poking the DB directly with psql over the compose network.

For the single-PG configuration this removes the **ssh linux_client dependency and the symlink-race workaround**. What remains is full-docker for multi-node Citus + race + audit ([docs/next.md](next.md) Operations #4; the template is `race_multinode.sh`).

> **gotcha (pinning the runtime base)**: `debian:stable-slim` currently points at Debian 13 (trixie), whose libfuse 3.17 bumped its SONAME to `libfuse3.so.4`. Tmds.Fuse dlopens `libfuse3.so.3`, so a trixie base makes `CheckDependencies` fail with "libfuse not found". [Dockerfile.mount](../tests/docker/Dockerfile.mount) pins **`debian:bookworm-slim` (Debian 12, libfuse 3.14 = `libfuse3.so.3`)** to avoid it.

### The Windows side cannot be dockerized

Dokan is a **Windows kernel driver** and cannot provide a FUSE-equivalent mount even in a Windows container (Dokan's user-mode API assumes the kernel driver). The Windows e2e must stay **on a Windows host directly**. It is out of scope for docker.

### Recommendation for now

1. ~~Put the Linux e2e under `tests/docker/`~~ → **the single-PG configuration is implemented** ([tests/docker/](../tests/docker/README.md)). Next, extend multi-node Citus + race + audit into the same frame (`race_multinode.sh` is the template).
2. Make `verify`'s pgsql_server hardcoding overridable too, so it can point at docker Citus (the same trick as Linux e2e's `PGFS_TEST_PG_EXEC`).
3. Keep the Windows e2e host-based, and note it is out of scope for docker.

---

## Related

- [docs/Mount.md](Mount.md) — the FUSE operations the Linux e2e covers
- [docs/Assign.md](Assign.md) — the Dokan operations the Windows e2e covers
- [docs/support_for_citus.md](support_for_citus.md) — the design the Citus tests verify
- [docs/next.md](next.md) — not-yet-started test items (multi-node Windows e2e / the flow.cmd harness output issue / docker integration)
