# Linux e2e (full docker)

A setup that removes host dependencies (ssh linux_client / the host's dotnet / the symlink-race
workaround) and runs **the Linux e2e entirely in docker**. The goal is to make it reproducible in CI too.

The scope is currently a **single PG (non-Citus)**. Full-docker for multi-node Citus + race + audit is
the next step ([docs/next.md](../../docs/next.md) #9); the template is [tests/citus/race_multinode.sh](../citus/race_multinode.sh).

> Windows e2e is out of scope for docker because Dokan is a Windows kernel driver. See [docs/tests.md](../../docs/tests.md).

---

## Layout

| Component | Role |
|---|---|
| [Dockerfile.mount](Dockerfile.mount) | Multi-stage build. Publish `mount.pgfs`/`mkfs.pgfs` self-contained with the SDK, then COPY into debian-slim + fuse3 + xattr/acl/psql tools |
| [compose.yml](compose.yml) | `coord` (PostgreSQL) + `mount` (the FUSE container). mount gets `SYS_ADMIN` / `/dev/fuse` / `apparmor:unconfined` |
| [run.sh](run.sh) | up -> mkfs -> FUSE mount -> run [tests/linux/e2e.sh](../linux/e2e.sh) in-container -> down |

`tests/linux/` is brought into the container as a **read-only bind mount**, so editing a test needs no image
rebuild. Rebuild only when `mount.pgfs`/`mkfs.pgfs` itself changes (`run.sh` defaults to `--build`).

---

## Running

```bash
# Prerequisites: docker + docker compose v2, submodule fetched (vendor/Tmds.Fuse)
bash tests/docker/run.sh                 # full flow (build -> e2e 35/35 -> teardown)
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

- **e2e runs in-container**: exposing the FUSE mount to the host (mount-namespace propagation) is fragile, so
  `e2e.sh` runs inside the mount container, with the mount point also inside the container.
- **The fallback test**: `test_fallback_uname_gname` INSERTs into the DB directly. `run.sh` passes
  `PGFS_TEST_PG_EXEC="psql -h coord ..."`, so it uses psql over the compose network rather than the
  ssh pgsql_server path.
- **submodule required**: the build stage COPYs the `vendor/Tmds.Fuse` source, so
  `git submodule update --init --recursive` must have run.
