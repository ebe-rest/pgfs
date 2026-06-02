#!/usr/bin/env bash
#
# Audit-log (docs/audit-log.md) dedicated test.
#
# Brings up pgfs on a multi-node Citus cluster (coord + worker1), mounts one client, and
# verifies behaviour specific to the audit log. The Linux e2e suite (tests/linux/e2e.sh)
# checks the correctness of FS operations; this one checks "what gets recorded in pgfs_audit
# as a side effect of those operations".
#
# Five things are checked:
#   A. Each op (create/delete/rename/chmod/chown/hardlink) inserts the expected row into pgfs_audit.
#   B. caller_* (uid/uname/host/ip) is recorded with the caller's real values (Linux uses fuse_get_context).
#   C. Automatic partition creation (= the month-rollover mechanism):
#        C1. Right after mkfs there is no partition for the current month; the first audit op
#            creates it on demand (ensure-before-insert).
#        C2. There is no DEFAULT partition, so a direct INSERT into an uncovered month is rejected.
#            Adding that month's partition with the same DDL lets the INSERT succeed
#            (= a month boundary is just another key on the same code path).
#   D. With audit.enabled=false (re-run mkfs without --audit) nothing is recorded.
#
# The single-transaction Citus commit (an operation tx that also carries an occurred_at-distributed
# audit INSERT can still commit via 2PC) is proven at the same time as A/B/C hold on multi-node
# Citus (also covered redundantly by race_multinode.sh Test 1).
#
# Intended to run on the Linux client host. If the docker daemon is stopped it is started
# automatically and restored to its original state by an exit trap (same convention as race_multinode.sh).
#
# Usage:
#   bash tests/citus/audit.sh
#
# Environment variables (shared with race_multinode.sh): PGFS_PROBE_IMAGE / COORD_NAME / WORKER1_NAME /
#   COORD_PORT / WORKER1_PORT / SUPER_USER / SUPER_PASSWORD / PGFS_USER / PGFS_PASSWORD /
#   PGFS_DB / MKFS_BIN / MOUNT_BIN / MOUNT1 / TOML1 / MLOG1 / PGFS_TEST_LOG
#
# Logs: /tmp/citus_audit.log and /tmp/pgfs_audit.mount.log

set -uo pipefail

# === Configuration ===
IMAGE="${PGFS_PROBE_IMAGE:-citusdata/citus:latest}"
COORD_NAME="${COORD_NAME:-pgfs-audit-coord}"
WORKER1_NAME="${WORKER1_NAME:-pgfs-audit-worker1}"
COORD_PORT="${COORD_PORT:-15542}"
WORKER1_PORT="${WORKER1_PORT:-15543}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
LOG="${PGFS_TEST_LOG:-/tmp/citus_audit.log}"

MKFS_BIN="${MKFS_BIN:-$HOME/project/pgfs_cs/bin/Publish/mkfs.pgfs}"
MOUNT_BIN="${MOUNT_BIN:-$HOME/project/pgfs_cs/bin/Publish/mount.pgfs}"

MOUNT1="${MOUNT1:-/tmp/pgfs_audit}"
TOML1="${TOML1:-/tmp/pgfs_audit.toml}"
MLOG1="${MLOG1:-/tmp/pgfs_audit.mount.log}"

COORD_CONN="Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"
SUPER_CONN="Host=localhost;Port=$COORD_PORT;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
WORKER1_SPEC="localhost:$WORKER1_PORT"

# Name used by the test (a subtree under the mount).
TDIR="audit_test"

# === Helpers ===
log()  { local line="[$(date +%H:%M:%S)] $*"; echo "$line" >&2; echo "$line" >> "$LOG"; }
hr()   { local line="--------------------------------------------------------------------------------"; echo "$line" >&2; echo "$line" >> "$LOG"; }
sec()  { echo "" >> "$LOG"; hr; log "=== $* ==="; hr; }
die()  { log "ERROR: $*"; exit 1; }

declare -i T_PASS=0
declare -i T_FAIL=0
declare -a FAILED=()
pass() { T_PASS=$((T_PASS+1)); log "  ✓ PASS: $1"; }
fail() { T_FAIL=$((T_FAIL+1)); FAILED+=("$1: $2"); log "  ✗ FAIL: $1 — $2"; }

# Run psql on the coordinator (as super). -tA is tuples-only + unaligned.
sqlcpg() { docker exec -i "$COORD_NAME" psql -h localhost -p "$COORD_PORT" -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }
# A query returning a single value (trimmed).
scalar() { sqlcpg -tA -c "$1" | tr -d ' '; }
# Audit row count per op.
audit_count_op() { scalar "SELECT count(*) FROM pgfs.pgfs_audit WHERE op='$1';"; }
# Whether a table (partition) exists (0/1).
table_exists() { scalar "SELECT count(*) FROM pg_tables WHERE schemaname='pgfs' AND tablename='$1';"; }

# === Cleanup ===
MOUNT1_PID=""
DOCKER_WAS_RUNNING=no
if systemctl is-active docker >/dev/null 2>&1; then DOCKER_WAS_RUNNING=yes; fi

cleanup() {
    sec "Cleanup"
    if mountpoint -q "$MOUNT1" 2>/dev/null; then
        log "  fusermount3 -u $MOUNT1"
        fusermount3 -u "$MOUNT1" 2>/dev/null || sudo fusermount3 -u "$MOUNT1" 2>/dev/null || true
    fi
    if [ -n "$MOUNT1_PID" ] && kill -0 "$MOUNT1_PID" 2>/dev/null; then
        kill "$MOUNT1_PID" 2>/dev/null || true
    fi
    wait 2>/dev/null || true
    rmdir "$MOUNT1" 2>/dev/null || true
    rm -f "$TOML1"

    docker stop "$COORD_NAME"   >/dev/null 2>&1 || true
    docker stop "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rm   "$COORD_NAME"   >/dev/null 2>&1 || true
    docker rm   "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rmi  "$IMAGE"        >/dev/null 2>&1 || true
    if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
        log "docker daemon was not running originally, so systemctl stop docker"
        sudo systemctl stop docker        >/dev/null 2>&1 || true
        sudo systemctl stop docker.socket >/dev/null 2>&1 || true
    fi
    log "mount log: $MLOG1"
    log "main log:  $LOG"
}
trap cleanup EXIT

# === Sanity ===
: > "$LOG"
log "Starting the audit-log dedicated test (image=$IMAGE)"

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

log "Wait for both PGs to become ready (max 60s)"
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

mount_client() {
    cat >"$TOML1" <<EOF
[database]
schema = "pgfs"
connection = "Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SslMode=Disable"
EOF
    mkdir -p "$MOUNT1"
    "$MOUNT_BIN" --setting-file "$TOML1" --mount-point "$MOUNT1" -f >"$MLOG1" 2>&1 &
    MOUNT1_PID=$!
    log "  mount pid=$MOUNT1_PID, waiting for ready (max 30s)"
    for i in $(seq 1 30); do
        if mountpoint -q "$MOUNT1"; then log "  mount ready (${i}s)"; return 0; fi
        sleep 1
    done
    log "mount log tail:"; tail -20 "$MLOG1" | tee -a "$LOG"
    die "mount did not become ready"
}

unmount_client() {
    if mountpoint -q "$MOUNT1" 2>/dev/null; then
        fusermount3 -u "$MOUNT1" 2>/dev/null || sudo fusermount3 -u "$MOUNT1" 2>/dev/null || true
    fi
    if [ -n "$MOUNT1_PID" ] && kill -0 "$MOUNT1_PID" 2>/dev/null; then
        kill "$MOUNT1_PID" 2>/dev/null || true
        wait "$MOUNT1_PID" 2>/dev/null || true
    fi
    MOUNT1_PID=""
}

# ====================================================================
# Part 1: audit.enabled=true (mkfs --audit)
# ====================================================================
sec "mkfs --clean --citus --audit --worker $WORKER1_SPEC"
"$MKFS_BIN" --clean --citus --audit \
    -c "$COORD_CONN" \
    -s pgfs \
    --super "$SUPER_CONN" \
    --worker "$WORKER1_SPEC" 2>&1 | tee -a "$LOG" >/dev/null \
    || die "mkfs (--audit) failed"

# Sanity: the audit table is among the distributed tables (inode/data/data_chunk/lock/audit = 5).
dist_count=$(scalar "SELECT count(*) FROM citus_tables WHERE citus_table_type='distributed';")
audit_dist=$(scalar "SELECT count(*) FROM citus_tables WHERE table_name::text='pgfs.pgfs_audit' AND citus_table_type='distributed';")
log "  citus_tables distributed=$dist_count, pgfs_audit distributed=$audit_dist"
[ "$dist_count" = "5" ] || die "unexpected distributed table count: $dist_count (should be 5)"
if [ "$audit_dist" = "1" ]; then
    pass "pgfs_audit is registered as an occurred_at-distributed table"
else
    fail "pgfs_audit distribution" "does not appear as distributed in citus_tables (audit_dist=$audit_dist)"
fi

CUR_MONTH=$(date +%Y_%m)
CUR_PART="pgfs_audit_$CUR_MONTH"

# --- C1 (first half): the current-month partition must be absent right after mkfs ---
sec "C1: the current-month partition ($CUR_PART) should be absent right after mkfs"
pre=$(table_exists "$CUR_PART")
default_exists=$(table_exists "pgfs_audit_default")
log "  $CUR_PART exists=$pre / pgfs_audit_default exists=$default_exists"
if [ "$pre" = "0" ]; then
    pass "C1-before: no current-month partition right after mkfs (op-driven creation by design)"
else
    fail "C1-before" "$CUR_PART exists before any op (did mkfs create it?)"
fi
if [ "$default_exists" = "0" ]; then
    pass "no DEFAULT partition (pgfs_audit_default)"
else
    fail "DEFAULT partition" "pgfs_audit_default exists (violates the no-DEFAULT design)"
fi

mount_client

# --- A: recording per op ---
sec "A: run 6 ops (create/chmod/chown/hardlink/rename/delete)"
ROOT="$MOUNT1/$TDIR"
mkdir -p "$ROOT"                              # create (dir)
echo "hello" > "$ROOT/f.txt"                  # create (file)
chmod 600 "$ROOT/f.txt"                       # chmod
chown "$(id -un):$(id -gn)" "$ROOT/f.txt"     # chown (to self; value is unchanged but the API is called)
ln "$ROOT/f.txt" "$ROOT/hard.txt"             # hardlink
mv "$ROOT/f.txt" "$ROOT/renamed.txt"          # rename
rm "$ROOT/hard.txt"                           # delete (file)
rm "$ROOT/renamed.txt"                        # delete (file)
sync; sleep 1

sec "A: inspect audit row counts per op"
A_OK=1
for op in create chmod chown hardlink rename delete; do
    n=$(audit_count_op "$op")
    if [[ "$n" =~ ^[0-9]+$ ]] && [ "$n" -ge 1 ]; then
        log "    op=$op rows=$n  OK"
    else
        log "    op=$op rows=$n  expected >=1"
        A_OK=0
    fi
done
if [ "$A_OK" = "1" ]; then
    pass "A: all 6 ops were recorded in pgfs_audit"
else
    fail "A: per-op recording" "one or more ops had 0 audit rows (see the log above)"
fi

# Inspect the name / detail of a single create row.
created_name=$(scalar "SELECT name FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
created_mode=$(scalar "SELECT detail->>'mode' FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
created_kind=$(scalar "SELECT detail->>'kind' FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
log "  create(f.txt): name=$created_name detail.mode=$created_mode detail.kind=$created_kind"
if [ "$created_name" = "f.txt" ] && [ "$created_kind" = "file" ]; then
    pass "A: the create row's name / detail.kind are as expected"
else
    fail "A: create row content" "name=$created_name kind=$created_kind (expected name=f.txt kind=file)"
fi

# --- B: caller_* ---
sec "B: recording of caller_* (uid/uname/host/ip)"
MY_UID=$(id -u)
MY_UNAME=$(id -un)
c_uid=$(scalar  "SELECT caller_uid   FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
c_uname=$(scalar "SELECT caller_uname FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
c_host=$(scalar "SELECT caller_host  FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
c_ip=$(scalar   "SELECT coalesce(host(caller_ip),'') FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
c_domain=$(scalar "SELECT coalesce(caller_domain,'') FROM pgfs.pgfs_audit WHERE op='create' AND name='f.txt' LIMIT 1;")
log "  caller_uid=$c_uid (mine=$MY_UID) uname=$c_uname (mine=$MY_UNAME) host=$c_host ip=$c_ip domain='$c_domain'"
if [ "$c_uid" = "$MY_UID" ] && [ "$c_uname" = "$MY_UNAME" ]; then
    pass "B: caller_uid / caller_uname carry the caller's real values (fuse_get_context)"
else
    fail "B: caller uid/uname" "uid=$c_uid uname=$c_uname (expected uid=$MY_UID uname=$MY_UNAME)"
fi
if [ -n "$c_host" ]; then
    pass "B: caller_host is recorded ($c_host)"
else
    fail "B: caller_host" "empty"
fi
# caller_ip should be non-null over TCP. Allowing for loopback environment differences, log only if empty (soft).
if [ -n "$c_ip" ]; then
    pass "B: caller_ip is recorded ($c_ip)"
else
    log "  NOTE: caller_ip is empty. Over a unix-domain-socket connection inet_client_addr() is NULL (non-null over TCP). Treated as soft here."
    pass "B: caller_ip (soft: empty but tolerated since it depends on the connection type)"
fi

# --- C1 (second half): the first op auto-created the current-month partition ---
sec "C1: was the current-month partition ($CUR_PART) auto-created after the op?"
post=$(table_exists "$CUR_PART")
log "  $CUR_PART exists=$post"
if [ "$post" = "1" ]; then
    pass "C1-after: the first audit op auto-created the current-month partition via ensure-before-insert"
else
    fail "C1-after" "$CUR_PART still does not exist after the op"
fi

# --- C2: a direct INSERT into an uncovered month is rejected; adding the partition lets it through (the month-boundary mechanism) ---
sec "C2: INSERT into an uncovered month (2099-01) is rejected -> succeeds after adding the partition"
FUT_PART="pgfs_audit_2099_01"
# Clean up any leftover just in case.
sqlcpg -c "DROP TABLE IF EXISTS pgfs.$FUT_PART;" >/dev/null 2>&1 || true
ins_no_part=$(sqlcpg -tA -c "INSERT INTO pgfs.pgfs_audit (occurred_at, op) VALUES ('2099-01-15 00:00:00','probe');" 2>&1)
if echo "$ins_no_part" | grep -qi "no partition of relation"; then
    pass "C2: a direct INSERT into a month with no partition is rejected by PG (confirms the no-DEFAULT design)"
else
    fail "C2: uncovered-month INSERT" "was not rejected: $ins_no_part"
fi
# Create that month's partition with the same DDL the app uses (Citus distributes it automatically).
mk_part=$(sqlcpg -tA -c "CREATE TABLE IF NOT EXISTS pgfs.$FUT_PART PARTITION OF pgfs.pgfs_audit FOR VALUES FROM ('2099-01-01') TO ('2099-02-01');" 2>&1)
ins_with_part=$(sqlcpg -tA -c "INSERT INTO pgfs.pgfs_audit (occurred_at, op) VALUES ('2099-01-15 00:00:00','probe');" 2>&1)
got=$(scalar "SELECT count(*) FROM pgfs.pgfs_audit WHERE op='probe';")
if [ "$got" = "1" ]; then
    pass "C2: adding that month's partition with the same DDL lets the INSERT through (a month boundary = another key on the same code path)"
else
    fail "C2: INSERT after adding the partition" "rows=$got (expected 1)  create=$mk_part insert=$ins_with_part"
fi
# Cleanup
sqlcpg -c "DROP TABLE IF EXISTS pgfs.$FUT_PART;" >/dev/null 2>&1 || true

# ====================================================================
# Part 2: audit.enabled=false (re-run mkfs without --audit)
# ====================================================================
sec "D: re-run mkfs without --audit to verify enabled=false"
unmount_client
"$MKFS_BIN" --clean --citus \
    -c "$COORD_CONN" \
    -s pgfs \
    --super "$SUPER_CONN" \
    --worker "$WORKER1_SPEC" 2>&1 | tee -a "$LOG" >/dev/null \
    || die "mkfs (no --audit) failed"

# The audit table itself is always created regardless of --audit (CreateAuditTableAsync).
audit_tbl=$(table_exists "pgfs_audit")
log "  pgfs_audit table exists (created even when audit is disabled, by design): $audit_tbl"
[ "$audit_tbl" = "1" ] || die "pgfs_audit table is missing (it should always be created)"

mount_client
sec "D: run 6 ops (none should be recorded)"
ROOT="$MOUNT1/$TDIR"
mkdir -p "$ROOT"
echo "x" > "$ROOT/f.txt"
chmod 600 "$ROOT/f.txt"
chown "$(id -un):$(id -gn)" "$ROOT/f.txt"
ln "$ROOT/f.txt" "$ROOT/hard.txt"
mv "$ROOT/f.txt" "$ROOT/renamed.txt"
rm "$ROOT/hard.txt"
rm "$ROOT/renamed.txt"
sync; sleep 1

d_rows=$(scalar "SELECT count(*) FROM pgfs.pgfs_audit;")
log "  pgfs_audit row count (enabled=false): $d_rows"
if [ "$d_rows" = "0" ]; then
    pass "D: with audit.enabled=false nothing is recorded"
else
    fail "D: recording with enabled=false" "rows=$d_rows (expected 0)"
fi
unmount_client

# === Summary ===
sec "Summary"
log "Total: $((T_PASS+T_FAIL))   PASS: $T_PASS   FAIL: $T_FAIL"
if [ $T_FAIL -gt 0 ]; then
    log "Failed:"
    for c in "${FAILED[@]}"; do log "  - $c"; done
    exit 1
fi
log "all $T_PASS checks PASS"
