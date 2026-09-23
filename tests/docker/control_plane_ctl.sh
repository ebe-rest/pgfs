#!/usr/bin/env bash
#
# Verifies the live application of `pgfsctl config set` on **a single-client mount with notify OFF**.
# Single PG / non-Citus, full docker. Where control_plane.sh verifies "notify ON + a reload NOTIFY from
# psql", this one verifies the heart of the pgfsctl design:
#   - the control LISTEN is always ON regardless of notify_enabled -> control reaches a mount with notify OFF.
#   - pgfsctl (the config subcommand) is what fires it (rather than hitting psql directly).
#   - Db+Live is persisted and live; File+Live is an ephemeral live set carried inline on the NOTIFY.
#
# The flow:
#   1) mkfs (without --audit) -> mount with audit.enabled=false. pgfs.toml **does not write** notify_enabled (= OFF by default).
#   2) mkdir -> no audit row is added (audit is disabled). The baseline is A0.
#   3) pgfsctl config set audit.enabled true  (Db+Live) -> persisted + a set NOTIFY.
#   4) mkdir -> A1 > A0 is the evidence that the notify-OFF mount received the control set and enabled audit live.
#   5) pgfsctl config set logging.level trace (File+Live) -> an ephemeral set NOTIFY.
#      "control set applied: logging.level" appearing in mount.log means File+Live applies live as well.
#
# Usage:
#
#   NO_BUILD=1 bash tests/docker/control_plane_ctl.sh   # reuse an existing image
#   KEEP_UP=1  bash tests/docker/control_plane_ctl.sh   # do not tear down, for investigating a failure
#
# The design of record is docs/design/runtime-control-plane.md. It uses the same compose as run.sh / control_plane.sh under a different project name.

set -uo pipefail

cd "$(dirname "$0")"

PROJECT="${COMPOSE_PROJECT:-pgfs-cpctl}"
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

# === mkfs (without --audit = starting with audit.enabled false) ===
log "mkfs --clean (schema=$PGFS_SCHEMA, starting with audit disabled)"
dexec sh -c "mkfs.pgfs --clean -c '$PGFS_CONN' --super '$SUPER_CONN' -s '$PGFS_SCHEMA'" || die "mkfs failed"

# === pgfs.toml (without notify_enabled = OFF by default) + the mount point ===
log "writing out pgfs.toml (no notify_enabled = OFF) + mkdir $MOUNT_POINT"
dexec sh -c "mkdir -p '$MOUNT_POINT'"
printf '%s\n' \
    "[database]" \
    "schema = \"$PGFS_SCHEMA\"" \
    "connection = \"$PGFS_CONN\"" \
    | dexec sh -c "cat > /tmp/pgfs.toml"

# === mount (foreground, in the background inside the container) ===
log "mount.pgfs --foreground (notify OFF - the control LISTEN should still be permanently ON)"
dc exec -d mount sh -c "mount.pgfs --setting-file /tmp/pgfs.toml --mount-point '$MOUNT_POINT' --foreground > /tmp/mount.log 2>&1"

log "waiting for the mountpoint to become ready (max 30s)"
ready=no
for i in $(seq 1 30); do
    if dexec mountpoint -q "$MOUNT_POINT" 2>/dev/null; then ready=yes; break; fi
    sleep 1
done
if [ "$ready" != "yes" ]; then
    dexec sh -c "tail -30 /tmp/mount.log" || true
    die "the mount never became ready"
fi
log "  mounted (${i}s)"

# Wait a little for the control channel to start LISTENing (it should always LISTEN even with notify OFF; the Api ctor LISTENs synchronously)
sleep 1

PSQL="PGPASSWORD=$PGFS_PASSWORD psql -h coord -U $PGFS_USER -d $PGFS_DB -tA"
audit_count() { dexec sh -c "$PSQL -c 'SELECT count(*) FROM $PGFS_SCHEMA.pgfs_audit'" 2>/dev/null | tr -d '[:space:]'; }

# Confirm from mount.log that the control LISTEN is established even with notify OFF (direct evidence).
if dexec sh -c "grep -q 'NotifyChannel: LISTEN' /tmp/mount.log"; then
    log "  control LISTEN confirmed (LISTENing even with notify OFF)"
else
    log "mount.log tail:"; dexec sh -c "tail -30 /tmp/mount.log" || true
    die "the control LISTEN is not established with notify OFF"
fi

# === Test A (Db+Live): pgfsctl config set audit.enabled true ===
dexec sh -c "mkdir -p '$MOUNT_POINT/ctl_before'" || die "mkdir ctl_before failed"
A0=$(audit_count)
log "baseline audit rows (audit disabled) = '$A0'"

log "pgfsctl config set audit.enabled true (Db+Live -> persisted + a set NOTIFY)"
dexec sh -c "pgfsctl config set audit.enabled true -c '$PGFS_CONN' -s '$PGFS_SCHEMA'" || die "pgfsctl set audit.enabled failed"
sleep 2

dexec sh -c "mkdir -p '$MOUNT_POINT/ctl_after'" || die "mkdir ctl_after failed"
A1=$(audit_count)
log "after pgfsctl set audit rows = '$A1' (growing from the baseline $A0 means it applied live to the notify-OFF mount)"

# === Test B (File+Live): pgfsctl config set logging.level trace ===
log "pgfsctl config set logging.level trace (File+Live → ephemeral set NOTIFY)"
dexec sh -c "pgfsctl config set logging.level trace -c '$PGFS_CONN' -s '$PGFS_SCHEMA'" || die "pgfsctl set logging.level failed"
sleep 2

LOGGING_APPLIED=no
if dexec sh -c "grep -q 'control set applied: logging.level' /tmp/mount.log"; then
    LOGGING_APPLIED=yes
fi
log "File+Live logging.level applied in mount = $LOGGING_APPLIED"

# === unmount ===
log "unmount"
dexec sh -c "fusermount3 -u '$MOUNT_POINT' 2>/dev/null || true"

PASS=yes
if [ ! "${A1:-0}" -gt "${A0:-0}" ]; then
    PASS=no
    log "FAIL: Db+Live (audit.enabled) did not apply live to the notify-OFF mount ($A0 -> $A1)"
fi
if [ "$LOGGING_APPLIED" != "yes" ]; then
    PASS=no
    log "FAIL: the control set of File+Live (logging.level) was not applied to the mount"
fi

if [ "$PASS" = "yes" ]; then
    log "RESULT: 3c pgfsctl live config set PASSED (notify OFF: Db+Live audit $A0→$A1 + File+Live logging.level applied)"
    exit 0
fi
log "mount.log tail:"; dexec sh -c "tail -40 /tmp/mount.log" || true
log "RESULT: 3c pgfsctl live config set FAILED"
exit 1
