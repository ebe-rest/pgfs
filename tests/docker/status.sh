#!/usr/bin/env bash
#
# A functional test of pgfsctl status (Layer 1 the activity listing + Layer 2 the FS statistics + Layer 3 the process details).
# Single PG / non-Citus, full docker. On an FS where mkfs created {prefix}mounts, it checks that while
# mounted status returns the activity row plus the FS statistics plus the cache statistics, and that the
# activity row disappears on unmount (deregistration).
#
# The flow:
#   1) mkfs (--clean) -> mount (notify OFF). The {prefix}mounts table exists.
#   2) status --json: table_present=true / one live fuse mount row / inodes=1, used=0 (the root only).
#   3) create a dir and a file with contents on the mount.
#   4) status: inode_count grew, used_bytes>0, chunk_count>=1 (Layer 2 aggregates the real data).
#   4.5) Layer 3: read the file to warm the content cache -> a ping NOTIFY to refresh the snapshot at once ->
#        the status text shows content chunks>=1 / inode hits>0 / the effective config (audit.enabled) / notify listen=on.
#   5) unmount -> status: 0 activity rows (deregistered), table_present still true.
#
# Usage:
#   bash tests/docker/status.sh
#   NO_BUILD=1 bash tests/docker/status.sh
#   KEEP_UP=1  bash tests/docker/status.sh
#
# The design of record is docs/design/runtime-control-plane.md. It uses the same compose as run.sh under a different project name.

set -uo pipefail

cd "$(dirname "$0")"

PROJECT="${COMPOSE_PROJECT:-pgfs-status}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
PGFS_SCHEMA="${PGFS_SCHEMA:-pgfs}"
MOUNT_POINT="${MOUNT_POINT:-/mnt/pgfs}"
KEEP_UP="${KEEP_UP:-0}"
NO_BUILD="${NO_BUILD:-0}"

log() { echo "[$(date +%H:%M:%S)] $*" >&2; }
die() { log "ERROR: $*"; exit 1; }

dc()    { docker compose -p "$PROJECT" "$@"; }
dexec() { dc exec -T mount "$@"; }

cleanup() {
    if [ "$KEEP_UP" = "1" ]; then
        log "KEEP_UP=1: leaving the containers up (to clean up: docker compose -p $PROJECT down -v)"
        return
    fi
    log "teardown: docker compose down -v"
    dc down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

command -v docker >/dev/null || die "docker missing"
docker compose version >/dev/null 2>&1 || die "docker compose v2 is required"

BUILD_FLAG="--build"
if [ "$NO_BUILD" = "1" ]; then
    BUILD_FLAG=""
fi
log "compose up -d $BUILD_FLAG (coord + mount)"
# shellcheck disable=SC2086
dc up -d $BUILD_FLAG coord mount || die "compose up failed"

log "waiting for coord to become reachable (max 60s)"
ready=no
for i in $(seq 1 60); do
    if dexec sh -c "PGPASSWORD=$SUPER_PASSWORD psql -h coord -U $SUPER_USER -d postgres -c 'SELECT 1' >/dev/null 2>&1"; then
        ready=yes; break
    fi
    sleep 1
done
[ "$ready" = "yes" ] || { dc logs coord | tail -30; die "cannot reach the coord PG"; }
log "  coord ready (${i}s)"

SUPER_CONN="Host=coord;Port=5432;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
PGFS_CONN="Host=coord;Port=5432;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"

log "mkfs --clean (schema=$PGFS_SCHEMA)"
dexec sh -c "mkfs.pgfs -f pgfs.toml --clean --yes -c '$PGFS_CONN' --super '$SUPER_CONN' -s '$PGFS_SCHEMA'" || die "mkfs failed"

dexec sh -c "mkdir -p '$MOUNT_POINT'"
printf '%s\n' "[database]" "schema = \"$PGFS_SCHEMA\"" "connection = \"$PGFS_CONN\"" \
    | dexec sh -c "cat > /tmp/pgfs.toml"

log "mount.pgfs --foreground"
dc exec -d mount sh -c "mount.pgfs --setting-file /tmp/pgfs.toml --mount-point '$MOUNT_POINT' --foreground > /tmp/mount.log 2>&1"

log "waiting for the mountpoint to become ready (max 30s)"
ready=no
for i in $(seq 1 30); do
    if dexec mountpoint -q "$MOUNT_POINT" 2>/dev/null; then ready=yes; break; fi
    sleep 1
done
[ "$ready" = "yes" ] || { dexec sh -c "tail -30 /tmp/mount.log" || true; die "the mount never became ready"; }
log "  mounted (${i}s)"
sleep 1

STATUS="pgfsctl status --json -c '$PGFS_CONN' -s '$PGFS_SCHEMA'"
status_json() { dexec sh -c "$STATUS" 2>/dev/null; }
jnum() { echo "$1" | grep -o "\"$2\": [0-9]*" | grep -o '[0-9]*' | head -1; }

PASS=yes
fail() { PASS=no; log "FAIL: $*"; }

# === 2) status right after the mount ===
J=$(status_json)
echo "$J" | grep -q '"table_present": true' || fail "Layer1: table_present is not true"
[ "$(echo "$J" | grep -c '"mount_id"')" -eq 1 ] || fail "Layer1: the activity rows are not 1 (got $(echo "$J" | grep -c '"mount_id"'))"
echo "$J" | grep -q '"live": true' || fail "Layer1: there is no live mount"
echo "$J" | grep -q '"mode": "fuse"' || fail "Layer1: there is no mode=fuse"
I0=$(jnum "$J" inode_count)
log "right after the mount: inodes=$I0 (the root only = 1 expected) / table_present, live and fuse confirmed"

# === 3) create a dir and a file with contents ===
dexec sh -c "mkdir -p '$MOUNT_POINT/d1' && echo 'hello pgfs status' > '$MOUNT_POINT/d1/f.txt' && sync" || die "creating the file failed"
sleep 1

# === 4) does status aggregate the real data ===
J=$(status_json)
I1=$(jnum "$J" inode_count)
USED=$(jnum "$J" used_bytes)
CHUNKS=$(jnum "$J" chunk_count)
log "after creating the file: inodes=$I1 used_bytes=$USED chunk_count=$CHUNKS"
[ "${I1:-0}" -gt "${I0:-0}" ] || fail "Layer2: inode_count did not grow ($I0 -> $I1)"
[ "${USED:-0}" -gt 0 ]        || fail "Layer2: used_bytes is still 0"
[ "${CHUNKS:-0}" -ge 1 ]      || fail "Layer2: chunk_count is below 1"

# Smoke-test the text output too (that it comes out without an exception)
dexec sh -c "pgfsctl status -c '$PGFS_CONN' -s '$PGFS_SCHEMA'" >/dev/null 2>&1 || fail "status (text) exited abnormally"

# === 4.5) Layer 3 (the running process's cache statistics + effective settings) ===
# Warm the content cache with a read (cat) and pile up inode hits (ls/stat). A read-only release does not
# invalidate the content, so the chunk stays. The snapshot is only refreshed on the heartbeat (30s), so a
# ping control NOTIFY (always LISTENing) is fired to refresh it at once before reading the status text.
log "Layer 3: warming the content cache with a read + refreshing the snapshot at once with a ping"
dexec sh -c "cat '$MOUNT_POINT/d1/f.txt' >/dev/null && ls -la '$MOUNT_POINT/d1' >/dev/null && stat '$MOUNT_POINT/d1/f.txt' >/dev/null" || fail "Layer3: the read operations failed"
CHANNEL="${PGFS_SCHEMA}_pgfs_notify"   # the mkfs default prefix is pgfs_
printf '%s\n' "SELECT pg_notify('$CHANNEL', '{\"c\":\"ping\"}');" \
    | dexec sh -c "PGPASSWORD=$PGFS_PASSWORD psql -h coord -U $PGFS_USER -d $PGFS_DB -f -" >/dev/null 2>&1 \
    || fail "Layer3: sending the ping NOTIFY failed"
sleep 1
T=$(dexec sh -c "pgfsctl status -c '$PGFS_CONN' -s '$PGFS_SCHEMA'" 2>/dev/null)
echo "$T" | grep -q "Process detail (Layer 3" || fail "Layer3: there is no Process detail section"
CHUNKS_L3=$(echo "$T" | grep 'content cache' | grep -o '[0-9]* chunks' | grep -o '[0-9]*' | head -1)
IHITS=$(echo "$T"     | grep 'inode cache'   | grep -o '([0-9]* hit'  | grep -o '[0-9]*' | head -1)
log "Layer 3: content chunks=$CHUNKS_L3 / inode hits=$IHITS"
[ "${CHUNKS_L3:-0}" -ge 1 ] || fail "Layer3: there is no chunk in the content cache (did the read not warm it / was the snapshot not refreshed?)"
[ "${IHITS:-0}" -gt 0 ]     || fail "Layer3: inode cache hits is 0 (is the Load-boundary counter not working?)"
echo "$T" | grep -q "audit.enabled =" || fail "Layer3: the effective config (audit.enabled) is not printed"
echo "$T" | grep -q "listen=on"       || fail "Layer3: notify listen=on is not printed"

# === 5) status after the unmount and the deregistration ===
log "unmount"
dexec sh -c "fusermount3 -u '$MOUNT_POINT' 2>/dev/null || true"
sleep 2
J=$(status_json)
ROWS=$(echo "$J" | grep -c '"mount_id"')
echo "$J" | grep -q '"table_present": true' || fail "table_present should still be true after the unmount"
[ "$ROWS" -eq 0 ] || fail "an activity row survived the unmount (the deregistration failed, rows=$ROWS)"
log "after the unmount: activity rows=$ROWS (expected 0 = deregistered)"

if [ "$PASS" = "yes" ]; then
    log "RESULT: 4c+4d pgfsctl status PASSED (Layer1 live/fuse/deregister + Layer2 inodes $I0→$I1 used=$USED chunks=$CHUNKS + Layer3 content_chunks=$CHUNKS_L3 inode_hits=$IHITS + config/notify)"
    exit 0
fi
log "mount.log tail:"; dexec sh -c "tail -30 /tmp/mount.log" || true
log "RESULT: 4c+4d pgfsctl status FAILED"
exit 1
