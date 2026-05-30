# Tests: catalog, how to run, environment requirements

The **hub document** for all pgfs tests. It collects "what tests exist / how to run them / what environment they need" in one place. The fine-grained options of each runner (flow.ps1 parameters etc.) live in each directory's README, linked from here.

> **Background**: the test environment varies by host (a Windows host driving linux_client / pgsql_server over ssh), so this is also a stock-take for considering whether it can be consolidated onto docker for reproducibility. The docker analysis is in [§docker integration](#docker-integration) at the end.

---

## Test catalog

| Suite | Count | What it checks | Location | Detailed README |
|---|---|---|---|---|
| **Linux e2e** | 34 | every mount.pgfs (FUSE) operation, via real FS operations | [tests/linux/e2e.sh](../tests/linux/e2e.sh) | [tests/linux/README.md](../tests/linux/README.md) |
| **Windows e2e** | 24 | every pgfs.assign (Dokan) operation, via real FS operations | [tests/windows/e2e.ps1](../tests/windows/e2e.ps1) | [tests/windows/README.md](../tests/windows/README.md) |
| **Citus mkfs matrix** | 18 | the combined behavior of `mkfs --citus / --worker / --clean` (new / keep-existing / rebuild) | [tests/citus/test_matrix.sh](../tests/citus/test_matrix.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus multinode probe** | 13 sections | a one-off probe of Citus behavior (auto-sync / DDL propagation / shard placement etc.) | [tests/citus/multinode_probe.sh](../tests/citus/multinode_probe.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus race multinode** | 4 | cross-client locking + multi-node e2e on multi-node Citus with 2 mount clients | [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) | [tests/citus/README.md](../tests/citus/README.md) |
| **Citus verify** | SQL diagnostics | `citus_tables` / distribution key / shard placement / EXPLAIN after a 1-node Citus setup | [tests/citus/verify.sql](../tests/citus/verify.sql) | [tests/citus/README.md](../tests/citus/README.md) |

### Linux e2e (34) categories

Directory operations / basic file operations / data I/O (bytea) / rename / permissions (chmod/chown) / symbolic links / hard links / xattr / metadata (StatFS/utime) / concurrency / name resolution fallback. The coverage is the ✅ operations of [docs/Mount.md](Mount.md).

### Windows e2e (24) categories

Directory operations / basic file operations / data I/O (bytea) / truncate / rename / attributes (ReadOnly/Hidden/System/Archive) / volume / pattern / concurrency. POSIX-only features (symlink/hardlink/chmod/chown/xattr APIs) are out of scope as DokanNet does not support them; Windows-specific tests are added instead. The coverage is the ✅/⚠️ operations of [docs/Assign.md](Assign.md).

### Latest results

| Suite | Result | Verified on |
|---|---|---|
| Linux e2e | **34/34 ALL PASSED** | single PG / 1-node Citus (pgsql_server) / multi-node Citus (docker), all of them |
| Windows e2e | **24/24 ALL PASSED** | single PG / 1-node Citus (pgsql_server) |
| Citus mkfs matrix | **18/18 PASS** | Citus 14.0.0 docker on linux_client |
| Citus race multinode | **4/4 PASS** | Citus 14.0.0 docker on linux_client |

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

> Current analysis of the motivation "wouldn't it be easier if everything were on docker". **Not yet started** (tied to the operational items in [docs/next.md](next.md)).

### Already on docker

The DB for the Citus suites is already docker (`citusdata/citus:latest` started as coord/worker with `--network host`). `race_multinode.sh` is a hybrid of **DB on docker + mount on the host**.

### The Linux side can be fully dockerized

If the mount in `race_multinode.sh` is moved from the host into a container, the Linux e2e becomes self-contained as a **PG/Citus container + mount.pgfs container**. What is needed:

- mount.pgfs container: mount FUSE inside the container with `--cap-add SYS_ADMIN --device /dev/fuse --security-opt apparmor:unconfined`. Build an image with the dotnet runtime + fuse3 (or COPY a self-contained `dotnet publish` binary).
- network: the same docker network as the DB container, or `--network host`.
- bind-mount the verification mount point onto the host so e2e.sh can run against it from outside the container.

This removes the **ssh linux_client dependency and the symlink-race workaround**, and makes it reproducible in CI.

### The Windows side cannot be dockerized

Dokan is a **Windows kernel driver** and cannot provide a FUSE-equivalent mount even in a Windows container (Dokan's user-mode API assumes the kernel driver). The Windows e2e must stay **on a Windows host directly**. It is out of scope for docker.

### Recommendation for now

1. Consolidate the Linux e2e + Citus suites into one docker-compose (or script) under `tests/docker/` etc. `race_multinode.sh` is nearly a template.
2. Make `verify`'s pgsql_server hardcoding overridable too, so it can point at docker Citus (the same trick as Linux e2e's `PGFS_TEST_PG_EXEC`).
3. Keep the Windows e2e host-based, and note it is out of scope for docker.

---

## Related

- [docs/Mount.md](Mount.md) — the FUSE operations the Linux e2e covers
- [docs/Assign.md](Assign.md) — the Dokan operations the Windows e2e covers
- [docs/support_for_citus.md](support_for_citus.md) — the design the Citus tests verify
- [docs/next.md](next.md) — not-yet-started test items (multi-node Windows e2e / the flow.cmd harness output issue / docker integration)
