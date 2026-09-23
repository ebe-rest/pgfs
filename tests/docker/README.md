# Linux e2e (full docker)

> **Route**: [docs/README.md](../../docs/README.md) › [docs/tests.md](../../docs/tests.md) (the test hub) › **this document**

A setup that removes host dependencies (ssh linux_client / the host's dotnet / the symlink-race
workaround) and runs **the Linux e2e entirely in docker**. The goal is to make it reproducible in CI too.

The scope is currently a **single PG (non-Citus)**. Full-docker for multi-node Citus + race + audit is
the next step ([docs/next.md](../../docs/next.md)); the template is [tests/citus/race_multinode.sh](../citus/race_multinode.sh).

> Windows e2e is out of scope for docker because Dokan is a Windows kernel driver. See [docs/tests.md](../../docs/tests.md).

---

## Layout

| Component | Role |
|---|---|
| [Dockerfile.mount](Dockerfile.mount) | A multi-stage build. Publishes `mount.pgfs`/`mkfs.pgfs`/`pgfsctl` self-contained with the SDK, then COPYs them into debian-slim + fuse3 + the xattr/acl/psql tools |
| [compose.yml](compose.yml) | `coord` (PostgreSQL) + `mount` (the FUSE container). mount carries `SYS_ADMIN` / `/dev/fuse` / `apparmor:unconfined` |
| [run.sh](run.sh) | up -> mkfs -> a FUSE mount -> running [tests/linux/e2e.sh](../linux/e2e.sh) inside the container -> down. It also asserts the `{prefix}mounts` registration and deregistration after the mount |
| [control_plane.sh](control_plane.sh) | A functional test of the control plane (live reload). Mounts with notify ON -> a reload NOTIFY from psql -> observes `audit.enabled` switching over while running ([docs/design/runtime-control-plane.md](../../docs/design/runtime-control-plane.md)). |
| [control_plane_ctl.sh](control_plane_ctl.sh) | A functional test of pgfsctl. Mounts with **notify OFF** -> `pgfsctl config set audit.enabled true` (Db+Live) takes the audit rows from 0 to 1, and the inline set of `pgfsctl config set logging.level trace` (File+Live) is observed applying. It directly confirms that the control LISTEN is always ON even with notify OFF ([docs/design/runtime-control-plane.md](../../docs/design/runtime-control-plane.md)). |
| [status.sh](status.sh) | A functional test of status. While mounted, it observes `pgfsctl status` returning Layer 1 (the activity row live/fuse, deregistered on unmount) + Layer 2 (the inode/used_bytes/chunk aggregation) + Layer 3 (warm the content cache with a read -> refresh the snapshot at once with a ping NOTIFY -> content chunks / inode hits / the effective config / notify) ([docs/design/runtime-control-plane.md](../../docs/design/runtime-control-plane.md)). |

`tests/linux/` is brought into the container as a **read-only bind mount**, so editing a test needs no image
rebuild. Rebuild only when `mount.pgfs`/`mkfs.pgfs` itself changes (`run.sh` defaults to `--build`).

---

## Running

```bash
# Prerequisites: docker + docker compose v2 (the libfuse binding lives in Pgfs.Fuse, so no submodule is needed)
bash tests/docker/run.sh                 # the whole flow (build -> the current Linux e2e -> teardown)
TEST_FILTER=xattr bash tests/docker/run.sh
KEEP_UP=1 bash tests/docker/run.sh       # keep the containers for failure investigation
NO_BUILD=1 bash tests/docker/run.sh      # reuse the existing image
```

Failure investigation (after keeping them with `KEEP_UP=1`):

```bash
docker compose -p pgfs-e2e exec mount sh -c "tail -50 /tmp/mount.log"
docker compose -p pgfs-e2e exec mount bash /tests/linux/e2e.sh /mnt/pgfs
docker compose -p pgfs-e2e down -v       # clean up
```

---

## Environment variables

All are overridable via `${VAR:-default}`.

| Variable | Default | Purpose |
|---|---|---|
| `PGFS_DOCKER_PG_IMAGE` | `postgres:17` | coord's PG image |
| `PGFS_DOCKER_SDK_IMAGE` | `mcr.microsoft.com/dotnet/sdk:10.0` | the build stage's SDK image |
| `SUPER_USER` / `SUPER_PASSWORD` | `postgres` / `postgres` | superuser (mkfs creates the DB/role) |
| `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` | `pgfs` | pgfs role / DB |
| `PGFS_SCHEMA` | `pgfs` | schema (the fallback test looks up `pgfs.pgfs_inode`) |
| `MOUNT_POINT` | `/mnt/pgfs` | in-container mount point |
| `TEST_FILTER` | (empty) | e2e test-name filter |
| `KEEP_UP` / `NO_BUILD` | `0` / `0` | suppress teardown / suppress rebuild |

---

## How it works (key points)

- **The e2e runs inside the container**: showing the FUSE mount to the host (mount namespace propagation) is fragile, so `e2e.sh` runs inside the mount container and the mount point lives in the container too.
- **The fallback test**: `test_fallback_uname_gname` INSERTs into the database directly. `run.sh` passes `PGFS_TEST_PG_EXEC="psql -h coord ..."` so that it uses psql across the compose network rather than the ssh pgsql_server route.
- **No submodule is needed** (from v0.2.0): the libfuse binding lives in `Pgfs.Fuse` (`src/fuse/`). The build stage only COPYs the whole of `src/` and publishes `src/mount`/`src/mkfs`; no external submodule is involved.
