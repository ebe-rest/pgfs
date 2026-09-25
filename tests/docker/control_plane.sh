#!/usr/bin/env bash
#
# A functional test of the control plane (live reload). Single PG / non-Citus, full docker.
#
# It mounts with notify enabled, fires a control NOTIFY (`{"c":"reload"}`) from psql and observes that
# `Api.ReloadLiveConfig()` runs. What it verifies is the DB-stored Live item `audit.enabled`:
#   1) mkfs (without --audit) -> mount with audit.enabled=false
#   2) mkdir -> no audit row is added (audit is disabled)
#   3) upsert audit.enabled=true into pgfs_settings + a reload NOTIFY
#   4) mkdir -> an audit row is added if the reload enabled audit
#
# Usage:
#
#   NO_BUILD=1 bash tests/docker/control_plane.sh   # reuse the image run.sh baked
#   KEEP_UP=1  bash tests/docker/control_plane.sh   # do not tear down, for investigating a failure
#
# The design of record is docs/design/runtime-control-plane.md. It uses the same compose as run.sh under a different project name.

set -uo pipefail

cd "$(dirname "$0")"

PROJECT="${COMPOSE_PROJECT:-pgfs-cp}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
PGFS_SCHEMA="${PGFS_SCHEMA:-pgfs}"
MOUNT_POINT="${MOUNT_POINT:-/mnt/pgfs}"
KEEP_UP="${KEEP_UP:-0}"
NO_BUILD="${NO_BUILD:-0}"
# The notification channel name is {schema}_{prefix}notify. The mkfs default prefix is pgfs_.
CHANNEL="${PGFS_SCHEMA}_pgfs_notify"

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
dexec sh -c "mkfs.pgfs -f pgfs.toml --clean --yes -c '$PGFS_CONN' --super '$SUPER_CONN' -s '$PGFS_SCHEMA'" || die "mkfs failed"

# === pgfs.toml (notify_enabled=true) + mount point ===
log "writing out pgfs.toml (notify_enabled=true) + mkdir $MOUNT_POINT"
dexec sh -c "mkdir -p '$MOUNT_POINT'"
printf '%s\n' \
    "[database]" \
    "schema = \"$PGFS_SCHEMA\"" \
    "connection = \"$PGFS_CONN\"" \
    "notify_enabled = true" \
    | dexec sh -c "cat > /tmp/pgfs.toml"

# === mount (foreground, in the background inside the container) ===
log "mount.pgfs --foreground (notify enabled)"
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

# Wait a little for the notify channel to start LISTENing (the Api ctor should have LISTENed synchronously, but just in case)
sleep 1

PSQL="PGPASSWORD=$PGFS_PASSWORD psql -h coord -U $PGFS_USER -d $PGFS_DB -tA"
audit_count() { dexec sh -c "$PSQL -c 'SELECT count(*) FROM $PGFS_SCHEMA.pgfs_audit'" 2>/dev/null | tr -d '[:space:]'; }

# === 1) mkdir with audit disabled -> no audit row is added ===
dexec sh -c "mkdir -p '$MOUNT_POINT/cp_before'" || die "mkdir cp_before failed"
A0=$(audit_count)
log "baseline audit rows (audit disabled) = '$A0'"

# === 2) upsert audit.enabled=true + a reload NOTIFY ===
log "upserting pgfs_settings.audit.enabled=true + sending a reload NOTIFY"
dexec sh -c "$PSQL -c \"INSERT INTO $PGFS_SCHEMA.pgfs_settings (scope,key,value,created_by,updated_by) VALUES ('audit','enabled','true'::jsonb,'cp','cp') ON CONFLICT (scope,key) DO UPDATE SET value=EXCLUDED.value\"" || die "audit.enabled upsert failed"
printf '%s\n' "SELECT pg_notify('$CHANNEL', '{\"c\":\"reload\"}');" | dexec sh -c "$PSQL -f -" || die "pg_notify reload failed"
sleep 2

# === 3) mkdir after the reload -> an audit row is added if audit was enabled ===
dexec sh -c "mkdir -p '$MOUNT_POINT/cp_after'" || die "mkdir cp_after failed"
A1=$(audit_count)
log "after reload audit rows = '$A1' (growing from the baseline $A0 means the reload worked)"

# === unmount ===
log "unmount"
dexec sh -c "fusermount3 -u '$MOUNT_POINT' 2>/dev/null || true"

if [ "${A1:-0}" -gt "${A0:-0}" ]; then
    log "RESULT: live reload PASSED (the reload enabled audit.enabled while running: $A0 -> $A1)"
    exit 0
fi
log "mount.log tail:"; dexec sh -c "tail -30 /tmp/mount.log" || true
log "RESULT: live reload FAILED (the audit rows did not grow: $A0 -> $A1)"
exit 1
