#!/usr/bin/env bash
#
# Full-docker Linux e2e runner (single PG / non-Citus).
#
# Brings up two containers via compose: coord (PostgreSQL) and mount (mount.pgfs + the e2e
# tooling), then inside the mount container runs mkfs -> FUSE mount -> tests/linux/e2e.sh.
# Depends on neither ssh linux_client nor the host's dotnet = reproducible in CI too.
#
# Usage:
#   bash tests/docker/run.sh                  # build -> up -> mkfs -> mount -> e2e -> down
#   TEST_FILTER=xattr bash tests/docker/run.sh
#   KEEP_UP=1 bash tests/docker/run.sh        # do not tear down (for failure investigation)
#   NO_BUILD=1 bash tests/docker/run.sh       # reuse the existing image (do not rebuild)
#
# Main environment variables (excerpt; all are ${VAR:-default}):
#   PGFS_DOCKER_PG_IMAGE   coord's PG image (default postgres:17)
#   PGFS_DOCKER_SDK_IMAGE  the build stage's SDK image (default mcr.../sdk:10.0)
#   SUPER_USER / SUPER_PASSWORD       postgres superuser
#   PGFS_USER / PGFS_PASSWORD / PGFS_DB   pgfs role / DB
#   PGFS_SCHEMA            schema (default pgfs; the fallback test looks up pgfs.pgfs_inode)
#   MOUNT_POINT            in-container mount point (default /mnt/pgfs)
#   TEST_FILTER            e2e test-name filter
#
# Windows e2e is out of scope for docker because Dokan is a kernel driver. See docs/tests.md.

set -uo pipefail

cd "$(dirname "$0")"

PROJECT="${COMPOSE_PROJECT:-pgfs-e2e}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
PGFS_SCHEMA="${PGFS_SCHEMA:-pgfs}"
MOUNT_POINT="${MOUNT_POINT:-/mnt/pgfs}"
TEST_FILTER="${TEST_FILTER:-}"
KEEP_UP="${KEEP_UP:-0}"
NO_BUILD="${NO_BUILD:-0}"

log() { echo "[$(date +%H:%M:%S)] $*" >&2; }
die() { log "ERROR: $*"; exit 1; }

dc()    { docker compose -p "$PROJECT" "$@"; }
dexec() { dc exec -T mount "$@"; }

cleanup() {
    if [ "$KEEP_UP" = "1" ]; then
        log "KEEP_UP=1: leaving containers up (clean up with: docker compose -p $PROJECT down -v)"
        return
    fi
    log "teardown: docker compose down -v"
    dc down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

command -v docker >/dev/null || die "docker missing"
docker compose version >/dev/null 2>&1 || die "docker compose v2 is required"

# === build + up ===
BUILD_FLAG="--build"
if [ "$NO_BUILD" = "1" ]; then
    BUILD_FLAG=""
fi
log "compose up -d $BUILD_FLAG (coord + mount)"
# shellcheck disable=SC2086
dc up -d $BUILD_FLAG coord mount || die "compose up failed"

# === wait for coord ===
log "waiting until coord is reachable from the mount container (max 60s)"
ready=no
for i in $(seq 1 60); do
    if dexec sh -c "PGPASSWORD=$SUPER_PASSWORD psql -h coord -U $SUPER_USER -d postgres -c 'SELECT 1' >/dev/null 2>&1"; then
        ready=yes; break
    fi
    sleep 1
done
[ "$ready" = "yes" ] || { dc logs coord | tail -30; die "cannot reach coord PG"; }
log "  coord ready (${i}s)"

SUPER_CONN="Host=coord;Port=5432;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
PGFS_CONN="Host=coord;Port=5432;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"

# === mkfs ===
log "mkfs --clean (schema=$PGFS_SCHEMA)"
dexec sh -c "mkfs.pgfs --clean -c '$PGFS_CONN' --super '$SUPER_CONN' -s '$PGFS_SCHEMA'" \
    || die "mkfs failed"

# === pgfs.toml + mount point ===
log "writing pgfs.toml + mkdir $MOUNT_POINT"
dexec sh -c "mkdir -p '$MOUNT_POINT'"
printf '%s\n' \
    "[database]" \
    "schema = \"$PGFS_SCHEMA\"" \
    "connection = \"$PGFS_CONN\"" \
    | dexec sh -c "cat > /tmp/pgfs.toml"

# === mount (background in container) ===
# Use --foreground (pgfs's -f is the short form of setting.file, not foreground).
# exec -d detaches, so it stays resident while running in the foreground.
log "mount.pgfs --foreground (in-container background)"
dc exec -d mount sh -c "mount.pgfs --setting-file /tmp/pgfs.toml --mount-point '$MOUNT_POINT' --foreground > /tmp/mount.log 2>&1"

log "waiting for the mountpoint to become ready (max 30s)"
ready=no
for i in $(seq 1 30); do
    if dexec mountpoint -q "$MOUNT_POINT" 2>/dev/null; then ready=yes; break; fi
    sleep 1
done
if [ "$ready" != "yes" ]; then
    log "mount.log tail:"; dexec sh -c "tail -30 /tmp/mount.log" || true
    die "mount did not become ready"
fi
log "  mounted (${i}s)"

# === e2e (in mount container) ===
log "running tests/linux/e2e.sh in the mount container (filter='${TEST_FILTER}')"
PG_EXEC="PGPASSWORD=$PGFS_PASSWORD psql -h coord -U $PGFS_USER -d $PGFS_DB -tA -q"
rc=0
dexec env TEST_FILTER="$TEST_FILTER" PGFS_TEST_PG_EXEC="$PG_EXEC" \
    bash /tests/linux/e2e.sh "$MOUNT_POINT" || rc=$?

# === unmount ===
log "unmount"
dexec sh -c "fusermount3 -u '$MOUNT_POINT' 2>/dev/null || true"

if [ "$rc" -eq 0 ]; then
    log "RESULT: Linux e2e PASSED (full docker, single PG)"
    exit 0
fi
log "RESULT: Linux e2e FAILED (exit $rc)"
exit "$rc"
