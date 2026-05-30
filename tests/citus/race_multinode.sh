#!/usr/bin/env bash
#
# Remaining verification script for cross-client locking (concurrency control).
#
# Runs 2 mount clients simultaneously on a multi-node Citus and checks the following 4 things:
#   1. The Linux e2e (34/34) passes on multi-node Citus.
#   2. In a concurrent write race from 2 clients to the same file, the final payload seen from
#      both clients matches (i.e. LockData(dataId) + the PG row lock serialize the writers, so
#      no data corruption occurs).
#   3. In a concurrent mkdir to the same parent from 2 clients, exactly one gets EEXIST
#      (i.e. the (parent_id, name) UK guarantees cross-shard consistency).
#   4. The pgfs_lock table stays a realistic size (count + relation size).
#
# Intended to run on linux_client. If the docker daemon is stopped it is started automatically,
# and restored to its original state on exit (stopped again if it was stopped).
#
# Usage:
#   bash tests/citus/race_multinode.sh
#
# Environment variables:
#   PGFS_PROBE_IMAGE  Citus image (default: citusdata/citus:latest)
#   MKFS_BIN          path to the mkfs executable
#   MOUNT_BIN         path to the mount executable
#
# Logs: /tmp/citus_race_multinode.log and /tmp/pgfs{1,2}.mount.log

set -uo pipefail

# === Configuration ===
# Everything is overridable via environment variables (${VAR:-default}). Useful when running docker
# on a different host/port etc.
IMAGE="${PGFS_PROBE_IMAGE:-citusdata/citus:latest}"
COORD_NAME="${COORD_NAME:-pgfs-race-coord}"
WORKER1_NAME="${WORKER1_NAME:-pgfs-race-worker1}"
COORD_PORT="${COORD_PORT:-15532}"
WORKER1_PORT="${WORKER1_PORT:-15533}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
LOG="${PGFS_TEST_LOG:-/tmp/citus_race_multinode.log}"

MKFS_BIN="${MKFS_BIN:-$HOME/project/pgfs_cs/bin/Publish/mkfs.pgfs}"
MOUNT_BIN="${MOUNT_BIN:-$HOME/project/pgfs_cs/bin/Publish/mount.pgfs}"

MOUNT1="${MOUNT1:-/tmp/pgfs1}"
MOUNT2="${MOUNT2:-/tmp/pgfs2}"
TOML1="${TOML1:-/tmp/pgfs1.toml}"
TOML2="${TOML2:-/tmp/pgfs2.toml}"
MLOG1="${MLOG1:-/tmp/pgfs1.mount.log}"
MLOG2="${MLOG2:-/tmp/pgfs2.mount.log}"

COORD_CONN="Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"
SUPER_CONN="Host=localhost;Port=$COORD_PORT;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
WORKER1_SPEC="localhost:$WORKER1_PORT"

# === Helpers ===
log()  { local line="[$(date +%H:%M:%S)] $*"; echo "$line" >&2; echo "$line" >> "$LOG"; }
hr()   { local line="--------------------------------------------------------------------------------"; echo "$line" >&2; echo "$line" >> "$LOG"; }
sec()  { echo "" >> "$LOG"; hr; log "=== $* ==="; hr; }
die()  { log "ERROR: $*"; exit 1; }

# PASS/FAIL counters
declare -i T_PASS=0
declare -i T_FAIL=0
declare -a FAILED=()
pass() { T_PASS=$((T_PASS+1)); log "  ✓ PASS: $1"; }
fail() { T_FAIL=$((T_FAIL+1)); FAILED+=("$1: $2"); log "  ✗ FAIL: $1 — $2"; }

sqlcpg()  { docker exec -i "$COORD_NAME" psql -h localhost -p "$COORD_PORT" -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }

# === Cleanup ===
MOUNT1_PID=""
MOUNT2_PID=""
DOCKER_WAS_RUNNING=no
if systemctl is-active docker >/dev/null 2>&1; then DOCKER_WAS_RUNNING=yes; fi

cleanup() {
    sec "Cleanup"
    # Unmount FUSE first
    for m in "$MOUNT1" "$MOUNT2"; do
        if mountpoint -q "$m" 2>/dev/null; then
            log "  fusermount3 -u $m"
            fusermount3 -u "$m" 2>/dev/null || sudo fusermount3 -u "$m" 2>/dev/null || true
        fi
    done
    for p in "$MOUNT1_PID" "$MOUNT2_PID"; do
        if [ -n "$p" ] && kill -0 "$p" 2>/dev/null; then
            kill "$p" 2>/dev/null || true
        fi
    done
    wait 2>/dev/null || true
    rmdir "$MOUNT1" "$MOUNT2" 2>/dev/null || true
    rm -f "$TOML1" "$TOML2"

    docker stop "$COORD_NAME"   >/dev/null 2>&1 || true
    docker stop "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rm   "$COORD_NAME"   >/dev/null 2>&1 || true
    docker rm   "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rmi  "$IMAGE"        >/dev/null 2>&1 || true
    if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
        log "docker daemon was originally stopped, so systemctl stop docker"
        sudo systemctl stop docker        >/dev/null 2>&1 || true
        sudo systemctl stop docker.socket >/dev/null 2>&1 || true
    fi
    log "mount logs: $MLOG1 $MLOG2"
    log "main log:   $LOG"
}
trap cleanup EXIT

# === Sanity ===
: > "$LOG"
log "starting the cross-client race multinode verification (image=$IMAGE)"

[ -x "$MKFS_BIN" ]  || die "mkfs binary not found: $MKFS_BIN"
[ -x "$MOUNT_BIN" ] || die "mount binary not found: $MOUNT_BIN"
command -v fusermount3 >/dev/null || die "fusermount3 missing (install fuse3)"

# === Docker daemon ===
sec "Docker daemon (initial = $DOCKER_WAS_RUNNING)"
if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
    log "start: sudo systemctl start docker"
    sudo systemctl start docker
    for i in $(seq 1 10); do
        if docker info >/dev/null 2>&1; then break; fi
        sleep 1
    done
fi

# === Pull + start containers ===
sec "Pull $IMAGE"
docker pull "$IMAGE" 2>&1 | tee -a "$LOG" >/dev/null || die "pull failed"

sec "Containers (--network host)"
docker run -d --name "$COORD_NAME" --network host \
    -e POSTGRES_PASSWORD="$SUPER_PASSWORD" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    -e PGPORT="$COORD_PORT" \
    "$IMAGE" \
    postgres -c port="$COORD_PORT" -c citus.node_conninfo=sslmode=disable >>"$LOG" 2>&1 || die "coord failed"

docker run -d --name "$WORKER1_NAME" --network host \
    -e POSTGRES_PASSWORD="$SUPER_PASSWORD" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    -e PGPORT="$WORKER1_PORT" \
    "$IMAGE" \
    postgres -c port="$WORKER1_PORT" -c citus.node_conninfo=sslmode=disable >>"$LOG" 2>&1 || die "worker1 failed"

log "wait for both PGs to become ready (max 60s)"
ready=no
for i in $(seq 1 60); do
    if docker exec "$COORD_NAME"   pg_isready -h localhost -p "$COORD_PORT"   -U "$SUPER_USER" >/dev/null 2>&1 && \
       docker exec "$WORKER1_NAME" pg_isready -h localhost -p "$WORKER1_PORT" -U "$SUPER_USER" >/dev/null 2>&1; then
        log "  both PGs ready (${i}s)"
        ready=yes; break
    fi
    sleep 1
done
[ "$ready" = "yes" ] || die "PG did not become ready"

# === mkfs --clean --citus --worker ===
sec "mkfs --clean --citus --worker $WORKER1_SPEC"
"$MKFS_BIN" --clean --citus \
    -c "$COORD_CONN" \
    -s pgfs \
    --super "$SUPER_CONN" \
    --worker "$WORKER1_SPEC" 2>&1 | tee -a "$LOG" >/dev/null \
    || die "mkfs failed"

# Sanity: 4 distributed + 1 local
dist_count=$(sqlcpg -tA -c "SELECT count(*) FROM citus_tables WHERE citus_table_type='distributed';" | tr -d ' ')
local_count=$(sqlcpg -tA -c "SELECT count(*) FROM citus_tables WHERE citus_table_type='local';" | tr -d ' ')
log "  citus_tables: distributed=$dist_count local=$local_count"
[ "$dist_count" = "4" ] && [ "$local_count" = "1" ] || die "unexpected citus_tables: dist=$dist_count local=$local_count"

# === Create 2 pgfs.toml + mount points ===
sec "create 2 pgfs.toml + mount points"
for t in "$TOML1" "$TOML2"; do
    cat >"$t" <<EOF
[database]
schema = "pgfs"
connection = "Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SslMode=Disable"
notify_enabled = true
EOF
done
mkdir -p "$MOUNT1" "$MOUNT2"

# === Mount 2 clients ===
sec "mount.pgfs x 2 (foreground in &)"
"$MOUNT_BIN" --setting-file "$TOML1" --mount-point "$MOUNT1" -f >"$MLOG1" 2>&1 &
MOUNT1_PID=$!
"$MOUNT_BIN" --setting-file "$TOML2" --mount-point "$MOUNT2" -f >"$MLOG2" 2>&1 &
MOUNT2_PID=$!
log "  mount1 pid=$MOUNT1_PID  mount2 pid=$MOUNT2_PID"

log "wait for both mountpoints to become ready (max 30s)"
ready=no
for i in $(seq 1 30); do
    if mountpoint -q "$MOUNT1" && mountpoint -q "$MOUNT2"; then
        log "  both mounts ready (${i}s)"
        ready=yes; break
    fi
    sleep 1
done
if [ "$ready" != "yes" ]; then
    log "mount1 log tail:"; tail -20 "$MLOG1" | tee -a "$LOG"
    log "mount2 log tail:"; tail -20 "$MLOG2" | tee -a "$LOG"
    die "mount did not become ready"
fi

# === Test 1: Linux e2e 34/34 on multi-node Citus ===
sec "Test 1: Linux e2e 34/34 against $MOUNT1 (multinode Citus)"
TESTS_E2E_RC=0
# test_fallback_uname_gname uses the pgsql_server-hardcoded ssh+psql path.
# To point it at docker Citus, it must be overridden with PGFS_TEST_PG_EXEC.
export PGFS_TEST_PG_EXEC="docker exec -i $COORD_NAME psql -h localhost -p $COORD_PORT -U $SUPER_USER -d $PGFS_DB -tA -q"
bash "$(dirname "$0")/../linux/e2e.sh" "$MOUNT1" 2>&1 | tee -a "$LOG" || TESTS_E2E_RC=$?
unset PGFS_TEST_PG_EXEC
if [ "$TESTS_E2E_RC" -eq 0 ]; then
    pass "Linux e2e on multinode Citus"
else
    fail "Linux e2e on multinode Citus" "exit $TESTS_E2E_RC"
fi

# === Test 2: final payload match under a concurrent write race ===
sec "Test 2: concurrent write race"
mkdir -p "$MOUNT1/race"
RACE_FILE_REL="race/A"
ROUNDS=4
SIZE_MB=8
race_ok=1
for r in $(seq 1 $ROUNDS); do
    log "  round $r/$ROUNDS: dd /dev/urandom (mount1) + dd /dev/zero (mount2), $SIZE_MB MiB each"
    (dd if=/dev/urandom of="$MOUNT1/$RACE_FILE_REL" bs=1M count=$SIZE_MB conv=notrunc status=none) &
    P1=$!
    (dd if=/dev/zero    of="$MOUNT2/$RACE_FILE_REL" bs=1M count=$SIZE_MB conv=notrunc status=none) &
    P2=$!
    wait $P1 $P2
    sync
    sleep 1  # wait for the notify channel + InodeCache to settle
    H1=$(md5sum "$MOUNT1/$RACE_FILE_REL" | awk '{print $1}')
    H2=$(md5sum "$MOUNT2/$RACE_FILE_REL" | awk '{print $1}')
    S1=$(stat -c %s "$MOUNT1/$RACE_FILE_REL")
    S2=$(stat -c %s "$MOUNT2/$RACE_FILE_REL")
    log "    mount1: md5=$H1 size=$S1 / mount2: md5=$H2 size=$S2"
    if [ "$H1" = "$H2" ] && [ "$S1" = "$S2" ]; then
        continue
    fi
    fail "race write round $r" "md5/size mismatch: m1=$H1/$S1 m2=$H2/$S2"
    race_ok=0
    break
done
if [ "$race_ok" = "1" ]; then
    pass "concurrent write race ($ROUNDS rounds): the final payload seen from both clients always matches"
fi

# === Test 3: concurrent mkdir with the same name ===
sec "Test 3: concurrent mkdir, same name"
mkdir -p "$MOUNT1/race-mkdir"
sleep 1  # wait for notify to settle (guarantee the state is visible from mount2)
COLLIDE_FAIL=0
COLLIDE_TOTAL=20
for i in $(seq 1 $COLLIDE_TOTAL); do
    # mkdir the same name into the same parent from 2 clients at the same time.
    out1=$(mkdir "$MOUNT1/race-mkdir/d$i" 2>&1); R1=$?
    out2=$(mkdir "$MOUNT2/race-mkdir/d$i" 2>&1); R2=$?
    # The two above run sequentially; to create a real race we want to parallelize with (...)&,
    # but since one always commits first, EEXIST reliably appears (= cross-client UK verification).
    # Try the parallel version too:
    rm -rf "$MOUNT1/race-mkdir/p$i" 2>/dev/null
    sleep 0.05
    (mkdir "$MOUNT1/race-mkdir/p$i" 2>/dev/null) & P1=$!
    (mkdir "$MOUNT2/race-mkdir/p$i" 2>/dev/null) & P2=$!
    wait $P1; PR1=$?
    wait $P2; PR2=$?
    # Serial version: expect R1=0 + R2!=0 (mount1 first + mount2 gets EEXIST).
    if [ "$R1" != "0" ] || [ "$R2" = "0" ]; then
        COLLIDE_FAIL=$((COLLIDE_FAIL+1))
        log "    round $i serial: R1=$R1 R2=$R2 (expected R1=0 R2!=0)"
    fi
    # Parallel version: expect one 0 / one != 0 (which one wins does not matter).
    if { [ "$PR1" = "0" ] && [ "$PR2" = "0" ]; } || { [ "$PR1" != "0" ] && [ "$PR2" != "0" ]; }; then
        COLLIDE_FAIL=$((COLLIDE_FAIL+1))
        log "    round $i parallel: PR1=$PR1 PR2=$PR2 (expected one 0 / one !=0)"
    fi
done
if [ "$COLLIDE_FAIL" -eq 0 ]; then
    pass "concurrent mkdir same name ($COLLIDE_TOTAL rounds x 2): the cross-client UK always gives one EEXIST"
else
    fail "concurrent mkdir same name" "collision control failed in $COLLIDE_FAIL rounds"
fi

# === Test 4: pgfs_lock cumulative size ===
sec "Test 4: pgfs_lock row count / relation size"
LOCK_COUNT=$(sqlcpg -tA -c "SELECT count(*) FROM pgfs.pgfs_lock;" | tr -d ' ')
# citus_total_relation_size sums across all shards for a distributed table.
LOCK_SIZE=$(sqlcpg -tA -c "SELECT pg_size_pretty(citus_total_relation_size('pgfs.pgfs_lock'));" | tr -d ' ')
log "  pgfs_lock: rows=$LOCK_COUNT  size=$LOCK_SIZE"
# Design figure: ~50 bytes per row, under 100MB for 1M operations. Here it should be hundreds to thousands.
if [ "$LOCK_COUNT" -ge 1 ] && [ "$LOCK_COUNT" -le 100000 ]; then
    pass "pgfs_lock row count is in a realistic range (rows=$LOCK_COUNT, size=$LOCK_SIZE)"
else
    fail "pgfs_lock row count" "unexpected rows=$LOCK_COUNT (size=$LOCK_SIZE)"
fi

# === Summary ===
sec "Summary"
log "Total: $((T_PASS+T_FAIL))   PASS: $T_PASS   FAIL: $T_FAIL"
if [ $T_FAIL -gt 0 ]; then
    log "Failed:"
    for c in "${FAILED[@]}"; do log "  - $c"; done
    exit 1
fi
log "all $T_PASS checks PASS"
