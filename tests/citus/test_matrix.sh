#!/usr/bin/env bash
#
# Test matrix for mkfs multi-node Citus.
# Runs on linux_client (docker daemon assumed not running).
#
# Matrix: 3 initial states x 6 target operations = 18 cases + 9 additional (X1-X9, at the end)
#   initial:
#     I1  new (no DB)
#     I2  existing (after mkfs --clean --citus, coordinator only = 1-node Citus)
#     I3  existing (after mkfs --clean --citus --worker w1, 1 coordinator + 1 worker)
#   target (--clean or no-clean x 3 citus levels):
#     Ta  --clean (no citus)            -> non-Citus single DB
#     Tb  --clean --citus               -> 1-node Citus
#     Tc  --clean --citus --worker w1   -> 1 coord + 1 worker
#     Td  (no --clean, no citus)        -> keep existing (do not touch Citus)
#     Te  (no --clean, --citus)         -> keep existing (DB exists, so do not touch Citus config)
#     Tf  (no --clean, --citus --worker w1) -> keep existing (same as above)
#
# Expected final state:
#   I*+T(a-c): the new configuration per target (--clean drops first, then rebuilds)
#   I*+T(d-f): I*'s configuration unchanged (DB exists -> mkfs does not touch it)
#
# On exit, restore the docker daemon to its original state.
# Log: all output is also written to /tmp/citus_test_matrix.log.

set -uo pipefail

# === Configuration ===
# Everything is overridable via environment variables (${VAR:-default}). Useful when running docker
# on a different host/port etc.
IMAGE="${PGFS_PROBE_IMAGE:-citusdata/citus:latest}"
COORD_NAME="${COORD_NAME:-pgfs-citus-matrix-coord}"
WORKER1_NAME="${WORKER1_NAME:-pgfs-citus-matrix-worker1}"
COORD_PORT="${COORD_PORT:-15432}"
WORKER1_PORT="${WORKER1_PORT:-15433}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
LOG="${PGFS_TEST_LOG:-/tmp/citus_test_matrix.log}"

# mkfs binary on this Linux box (assumes already built via dotnet publish from Windows side)
MKFS_BIN="${MKFS_BIN:-$HOME/project/pgfs_cs/bin/Publish/mkfs.pgfs}"

# Connection strings used for mkfs CLI.
# Note: all containers run with --network host, and each PG listens on a unique host port via -c port=NNN.
# That way mkfs (run on the host) / coord / worker all reference each other via the same "localhost:NNN".
# With a bridge network + port mapping, even if the coordinator registers itself as
# citus_set_coordinator_host('localhost', host_port), the "localhost" seen from the worker points at
# the worker itself, so Citus stops working.
COORD_CONN="Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"
SUPER_CONN="Host=localhost;Port=$COORD_PORT;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
WORKER1_SPEC="localhost:$WORKER1_PORT"

# === Helpers ===
# Note: the visible output of log/hr/sec goes to stderr. When a function like verify_state is captured
#       via $(), this keeps log messages from contaminating the result and breaking the "PASS" check.
#       Appending to the LOG file is kept separate.
log()  { local line="[$(date +%H:%M:%S)] $*"; echo "$line" >&2; echo "$line" >> "$LOG"; }
hr()   { local line="--------------------------------------------------------------------------------"; echo "$line" >&2; echo "$line" >> "$LOG"; }
sec()  { echo "" >> "$LOG"; hr; log "=== $* ==="; hr; }
die()  { log "ERROR: $*"; exit 1; }

# Run psql against a container as super. With --network host, port is the only differentiator.
# Use the psql inside the coord container (the worker container uses the same image, so either works).
sqlc()    { docker exec -i "$COORD_NAME"   psql -h localhost -p "$COORD_PORT"   -U "$SUPER_USER" -d postgres "$@" 2>&1; }
sqlw()    { docker exec -i "$COORD_NAME"   psql -h localhost -p "$WORKER1_PORT" -U "$SUPER_USER" -d postgres "$@" 2>&1; }
sqlcpg()  { docker exec -i "$COORD_NAME"   psql -h localhost -p "$COORD_PORT"   -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }
sqlwpg()  { docker exec -i "$COORD_NAME"   psql -h localhost -p "$WORKER1_PORT" -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }

# Run mkfs against the containerized coordinator with a coordinator/worker config
# Args: $1=clean(yes|no) $2=citus_level(none|coord|coord_worker) [$3=schema (default pgfs)]
run_mkfs() {
    local clean=$1
    local citus=$2
    local schema=${3:-pgfs}

    local args=()
    if [ "$clean" = "yes" ]; then
        args+=(--clean --yes)  # non-interactive, so skip the confirmation (since v0.2.1; without it nothing is dropped and it exits 3)
    fi
    args+=(-c "$COORD_CONN")
    args+=(-f pgfs.toml)  # mkfs requires the settings file location (since v0.2.1; created if missing)
    args+=(-s "$schema")
    args+=(--super "$SUPER_CONN")
    case "$citus" in
        none)         ;;
        coord)        args+=(--citus) ;;
        coord_worker) args+=(--citus --worker "$WORKER1_SPEC") ;;
        *) die "unknown citus level: $citus" ;;
    esac

    log ">>> mkfs.pgfs ${args[*]}"
    "$MKFS_BIN" "${args[@]}" 2>&1 | tee -a "$LOG" >/dev/null
    return ${PIPESTATUS[0]}
}

# Verify post-mkfs state
# Args: $1=expected_citus_level (none|coord|coord_worker)
verify_state() {
    local expected=$1
    local result=PASS
    local details=""

    # 1. coordinator pgfs DB exists & has the 5 expected tables in pgfs schema
    local table_count=$(sqlc -tA -c "SELECT count(*) FROM pg_database WHERE datname='$PGFS_DB';" 2>/dev/null | tr -d ' ')
    if [ "$table_count" != "1" ]; then
        result=FAIL; details="$details; coordinator DB '$PGFS_DB' missing"
    fi
    # **Look at `pg_class` rather than `pg_tables`** - `pgfs_audit` is **a partition parent (relkind='p')** and
    # so does not appear in `pg_tables`. Missing it here produces the false report "a table is missing" (this was hit).
    table_count=$(sqlcpg -tA -c "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname='pgfs' AND c.relkind IN ('r','p') AND c.relname IN ('pgfs_inode','pgfs_data','pgfs_data_chunk','pgfs_lock','pgfs_settings','pgfs_audit','pgfs_mounts');" 2>/dev/null | tr -d ' ')
    if [ "$table_count" != "7" ]; then
        result=FAIL; details="$details; expected 7 tables in pgfs schema, got '$table_count'"
    fi

    # 2. Citus state based on expected level
    case "$expected" in
        none)
            local citus_ext=$(sqlcpg -tA -c "SELECT count(*) FROM pg_extension WHERE extname='citus';" 2>/dev/null | tr -d ' ')
            if [ "$citus_ext" != "0" ]; then
                result=FAIL; details="$details; expected citus extension absent, got count=$citus_ext"
            fi
            ;;
        coord)
            local node_count=$(sqlcpg -tA -c "SELECT count(*) FROM pg_dist_node WHERE noderole='primary';" 2>/dev/null | tr -d ' ')
            if [ "$node_count" != "1" ]; then
                result=FAIL; details="$details; expected 1 node in pg_dist_node, got '$node_count'"
            fi
            local should=$(sqlcpg -tA -c "SELECT shouldhaveshards FROM pg_dist_node WHERE groupid=0;" 2>/dev/null | tr -d ' ')
            if [ "$should" != "t" ]; then
                result=FAIL; details="$details; expected coordinator shouldhaveshards=t (1-node), got '$should'"
            fi
            local dist_count=$(sqlcpg -tA -c "SELECT count(*) FROM citus_tables WHERE citus_table_type='distributed';" 2>/dev/null | tr -d ' ')
            if [ "$dist_count" != "4" ]; then
                result=FAIL; details="$details; expected 4 distributed tables (inode/data/data_chunk/audit), got '$dist_count'"
            fi
            local local_count=$(sqlcpg -tA -c "SELECT count(*) FROM citus_tables WHERE citus_table_type='local';" 2>/dev/null | tr -d ' ')
            if [ "$local_count" != "3" ]; then
                result=FAIL; details="$details; expected 3 local (metadata) tables (lock/settings/mounts), got '$local_count'"
            fi
            ;;
        coord_worker)
            local node_count=$(sqlcpg -tA -c "SELECT count(*) FROM pg_dist_node WHERE noderole='primary';" 2>/dev/null | tr -d ' ')
            if [ "$node_count" != "2" ]; then
                result=FAIL; details="$details; expected 2 nodes (coord+worker), got '$node_count'"
            fi
            local should=$(sqlcpg -tA -c "SELECT shouldhaveshards FROM pg_dist_node WHERE groupid=0;" 2>/dev/null | tr -d ' ')
            if [ "$should" != "f" ]; then
                result=FAIL; details="$details; expected coordinator shouldhaveshards=f (multi-node), got '$should'"
            fi
            local worker_should=$(sqlcpg -tA -c "SELECT shouldhaveshards FROM pg_dist_node WHERE groupid<>0;" 2>/dev/null | tr -d ' ')
            if [ "$worker_should" != "t" ]; then
                result=FAIL; details="$details; expected worker shouldhaveshards=t, got '$worker_should'"
            fi
            local dist_count=$(sqlcpg -tA -c "SELECT count(*) FROM citus_tables WHERE citus_table_type='distributed';" 2>/dev/null | tr -d ' ')
            if [ "$dist_count" != "4" ]; then
                result=FAIL; details="$details; expected 4 distributed tables (inode/data/data_chunk/audit), got '$dist_count'"
            fi
            # worker should have the same 5 distributed table metadata
            local worker_citus_ext=$(sqlwpg -tA -c "SELECT count(*) FROM pg_extension WHERE extname='citus';" 2>/dev/null | tr -d ' ')
            if [ "$worker_citus_ext" != "1" ]; then
                result=FAIL; details="$details; worker missing citus extension"
            fi
            ;;
    esac

    if [ "$result" = "PASS" ]; then
        log "  ✓ verify ($expected) PASS"
    else
        log "  ✗ verify ($expected) FAIL: ${details#; }"
    fi
    echo "$result"
}

# Bring the cluster back to a clean slate (drop pgfs DB on both nodes, clear pg_dist_node on coord)
reset_cluster() {
    log "  reset: drop pgfs DB on coord + worker, clear pg_dist_node"
    # Coordinator: try to drop any local cluster metadata first (otherwise DROP DATABASE may complain)
    sqlc -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$PGFS_DB' AND pid<>pg_backend_pid();" >/dev/null 2>&1 || true
    sqlc -c "DROP DATABASE IF EXISTS $PGFS_DB;" >/dev/null 2>&1 || true
    sqlw -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$PGFS_DB' AND pid<>pg_backend_pid();" >/dev/null 2>&1 || true
    sqlw -c "DROP DATABASE IF EXISTS $PGFS_DB;" >/dev/null 2>&1 || true
    # Also drop pgfs role to avoid OWNER conflicts (mkfs will recreate it)
    sqlc -c "DROP ROLE IF EXISTS $PGFS_USER;" >/dev/null 2>&1 || true
    sqlw -c "DROP ROLE IF EXISTS $PGFS_USER;" >/dev/null 2>&1 || true
}

# === Remember initial docker daemon state ===
DOCKER_WAS_RUNNING=no
if systemctl is-active docker >/dev/null 2>&1; then
    DOCKER_WAS_RUNNING=yes
fi

cleanup() {
    sec "Cleanup"
    docker stop  "$COORD_NAME"   >/dev/null 2>&1 || true
    docker stop  "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rm    "$COORD_NAME"   >/dev/null 2>&1 || true
    docker rm    "$WORKER1_NAME" >/dev/null 2>&1 || true
    docker rmi   "$IMAGE"        >/dev/null 2>&1 || true
    if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
        log "docker daemon was originally stopped, so systemctl stop docker"
        sudo systemctl stop docker        >/dev/null 2>&1 || true
        sudo systemctl stop docker.socket >/dev/null 2>&1 || true
    fi
    log "the log is at $LOG"
}
trap cleanup EXIT

# === Reset log ===
: > "$LOG"
log "starting the mkfs multi-node Citus test matrix (image=$IMAGE, mkfs=$MKFS_BIN)"

# === 0. check mkfs binary ===
if [ ! -x "$MKFS_BIN" ]; then
    die "mkfs binary not found or not executable: $MKFS_BIN"
fi

# === Start docker daemon ===
sec "Docker daemon (initial state = $DOCKER_WAS_RUNNING)"
if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
    log "start: sudo systemctl start docker"
    sudo systemctl start docker
    for i in $(seq 1 10); do
        if docker info >/dev/null 2>&1; then break; fi
        sleep 1
    done
fi

# === Pull image ===
sec "Pull $IMAGE"
if ! docker pull "$IMAGE" 2>&1 | tee -a "$LOG"; then
    die "image pull failed (tag invalid?)"
fi

# === Containers (--network host, so no network creation needed) ===
sec "Containers"
log "coordinator $COORD_NAME (host port $COORD_PORT, --network host)"
if ! docker run -d --name "$COORD_NAME" --network host \
        -e POSTGRES_PASSWORD="$SUPER_PASSWORD" \
        -e POSTGRES_HOST_AUTH_METHOD=trust \
        -e PGPORT="$COORD_PORT" \
        "$IMAGE" \
        postgres -c port="$COORD_PORT" -c citus.node_conninfo=sslmode=disable 2>&1 | tee -a "$LOG"; then
    die "coordinator failed to start"
fi

log "worker1 $WORKER1_NAME (host port $WORKER1_PORT, --network host)"
if ! docker run -d --name "$WORKER1_NAME" --network host \
        -e POSTGRES_PASSWORD="$SUPER_PASSWORD" \
        -e POSTGRES_HOST_AUTH_METHOD=trust \
        -e PGPORT="$WORKER1_PORT" \
        "$IMAGE" \
        postgres -c port="$WORKER1_PORT" -c citus.node_conninfo=sslmode=disable 2>&1 | tee -a "$LOG"; then
    die "worker1 failed to start"
fi

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

# Sanity: confirm we can psql directly into both nodes.
sqlc -c "SELECT 1;" >/dev/null 2>&1 || die "coordinator psql connection failed"
sqlw -c "SELECT 1;" >/dev/null 2>&1 || die "worker psql connection failed"

# === Run matrix ===
sec "Matrix execution"

# Initial state setters: produce I1 / I2 / I3 on the cluster
setup_I1() {
    log "  setup I1 (no DB)"
    reset_cluster
}
setup_I2() {
    log "  setup I2 (1-node Citus, after --clean --citus)"
    reset_cluster
    run_mkfs yes coord >/dev/null || die "I2 setup mkfs failed"
}
setup_I3() {
    log "  setup I3 (coord + worker, after --clean --citus --worker)"
    reset_cluster
    run_mkfs yes coord_worker >/dev/null || die "I3 setup mkfs failed"
}

# Target operations + expected results
# Format: initial|target|expect_post_state|description
# Ta-Tc: include --clean, so the result changes per target
# Td-Tf: no --clean -> keep existing (= the initial state unchanged)
declare -a CASES=(
    "I1|Ta|none|new, no DB -> --clean (no citus) builds non-Citus"
    "I1|Tb|coord|new -> --clean --citus builds 1-node Citus"
    "I1|Tc|coord_worker|new -> --clean --citus --worker builds coord+worker"
    "I2|Ta|none|1-node Citus -> --clean (no citus) resets to non-Citus"
    "I2|Tb|coord|1-node Citus -> --clean --citus rebuilds the same configuration"
    "I2|Tc|coord_worker|1-node Citus -> --clean --citus --worker expands to coord+worker"
    "I3|Ta|none|coord+worker -> --clean (no citus) resets to non-Citus"
    "I3|Tb|coord|coord+worker -> --clean --citus shrinks to 1 node"
    "I3|Tc|coord_worker|coord+worker -> --clean --citus --worker rebuilds the same configuration"
    "I1|Td|none|new, no DB -> no-clean (no citus) builds non-Citus (initial full setup)"
    "I1|Te|coord|new, no DB -> no-clean --citus builds 1-node Citus (citus flag takes effect)"
    "I1|Tf|coord_worker|new, no DB -> no-clean --citus --worker builds coord+worker (citus flag takes effect)"
    "I2|Td|coord|1-node Citus -> no-clean (no citus) keeps existing"
    "I2|Te|coord|1-node Citus -> no-clean --citus keeps existing (DB-exists guard)"
    "I2|Tf|coord|1-node Citus -> no-clean --citus --worker keeps existing (DB-exists guard)"
    "I3|Td|coord_worker|coord+worker -> no-clean (no citus) keeps existing"
    "I3|Te|coord_worker|coord+worker -> no-clean --citus keeps existing"
    "I3|Tf|coord_worker|coord+worker -> no-clean --citus --worker keeps existing"
)

# I1+Td, I1+Te, I1+Tf are the "new, no DB -> no-clean (no --clean)" cases.
# mkfs detects that the coord DB is absent and performs a fresh setup (= the same result as --clean).
# The "keep-existing guard" only applies when the DB already exists by design, so I1 (no DB) + Td-Tf
# end up in the same final state as Ta-Tc.

# Run all cases
PASS_COUNT=0
FAIL_COUNT=0
FAILED_CASES=()
for entry in "${CASES[@]}"; do
    IFS='|' read -r I T expected desc <<<"$entry"
    sec "Case $I+$T: $desc"

    # Setup initial state
    case "$I" in
        I1) setup_I1 ;;
        I2) setup_I2 ;;
        I3) setup_I3 ;;
        *) die "unknown initial $I" ;;
    esac

    # Apply target operation (case expr value becomes the function return code)
    case "$T" in
        Ta) run_mkfs yes none ;;
        Tb) run_mkfs yes coord ;;
        Tc) run_mkfs yes coord_worker ;;
        Td) run_mkfs no  none ;;
        Te) run_mkfs no  coord ;;
        Tf) run_mkfs no  coord_worker ;;
        *) die "unknown target $T" ;;
    esac
    mkfs_rc=$?
    if [ "$mkfs_rc" -ne 0 ]; then
        log "  mkfs exit=$mkfs_rc (FAIL)"
        FAIL_COUNT=$((FAIL_COUNT+1))
        FAILED_CASES+=("$I+$T: mkfs exit $mkfs_rc")
        continue
    fi

    # Verify
    result=$(verify_state "$expected")
    if [ "$result" = "PASS" ]; then
        PASS_COUNT=$((PASS_COUNT+1))
    else
        FAIL_COUNT=$((FAIL_COUNT+1))
        FAILED_CASES+=("$I+$T (expected $expected)")
    fi
done

# === Additional cases: passing create-only instructions to an existing DB ===
# --citus / --worker are "create it like this" instructions, so they have no effect on a DB that already exists (whether it is Citus is decided by the DB itself).
#   X1: create a **new schema** in an existing non-Citus DB with --citus → it does not become Citus, and the tables are created normally
#       (previously --citus stayed true, and it failed trying to create_distributed_table the new tables)
#   X2: pass --worker to an existing 1-node Citus DB → nothing is created on the worker (not even the pgfs role)
#       (previously "ensure the user", which runs before the DB existence check, also ran on the worker)
sec "Case X1: existing non-Citus DB → no-clean --citus with a new schema"
reset_cluster
run_mkfs yes none >/dev/null || die "X1 setup mkfs failed"
run_mkfs no coord pgfs_x1
x1_rc=$?
x1_ext=$(sqlcpg -tA -c "SELECT count(*) FROM pg_extension WHERE extname='citus'" | tr -d '[:space:]')
x1_tables=$(sqlcpg -tA -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='pgfs_x1'" | tr -d '[:space:]')
if [ "$x1_rc" -eq 0 ] && [ "$x1_ext" = "0" ] && [ "$x1_tables" = "7" ]; then
    log "  ✓ X1 PASS"
    PASS_COUNT=$((PASS_COUNT+1))
else
    log "  ✗ X1 FAIL: mkfs exit=$x1_rc citus_ext=$x1_ext tables(pgfs_x1)=$x1_tables (expected 0 / 0 / 7)"
    FAIL_COUNT=$((FAIL_COUNT+1))
    FAILED_CASES+=("X1: existing non-Citus DB + --citus")
fi

sec "Case X2: existing 1-node Citus DB → no-clean --citus --worker"
reset_cluster
run_mkfs yes coord >/dev/null || die "X2 setup mkfs failed"
run_mkfs no coord_worker
x2_rc=$?
x2_role=$(sqlw -tA -c "SELECT count(*) FROM pg_roles WHERE rolname='$PGFS_USER'" | tr -d '[:space:]')
x2_nodes=$(sqlcpg -tA -c "SELECT count(*) FROM pg_dist_node" | tr -d '[:space:]')
if [ "$x2_rc" -eq 0 ] && [ "$x2_role" = "0" ] && [ "$x2_nodes" = "1" ]; then
    log "  ✓ X2 PASS"
    PASS_COUNT=$((PASS_COUNT+1))
else
    log "  ✗ X2 FAIL: mkfs exit=$x2_rc pgfs role on worker=$x2_role pg_dist_node=$x2_nodes (expected 0 / 0 / 1)"
    FAIL_COUNT=$((FAIL_COUNT+1))
    FAILED_CASES+=("X2: existing DB + --worker")
fi

sec "Case X3: coord + worker → --clean only (no --citus / --worker)"
# The nodes to drop are decided by the DB itself (pg_dist_node). Previously, forgetting --worker left the worker's DB behind.
reset_cluster
run_mkfs yes coord_worker >/dev/null || die "X3 setup mkfs failed"
run_mkfs yes none
x3_rc=$?
x3_wdb=$(sqlw -tA -c "SELECT count(*) FROM pg_database WHERE datname='$PGFS_DB'" | tr -d '[:space:]')
x3_ext=$(sqlcpg -tA -c "SELECT count(*) FROM pg_extension WHERE extname='citus'" | tr -d '[:space:]')
if [ "$x3_rc" -eq 0 ] && [ "$x3_wdb" = "0" ] && [ "$x3_ext" = "0" ]; then
    log "  ✓ X3 PASS"
    PASS_COUNT=$((PASS_COUNT+1))
else
    log "  ✗ X3 FAIL: mkfs exit=$x3_rc $PGFS_DB on worker=$x3_wdb citus_ext=$x3_ext (expected 0 / 0 / 0)"
    FAIL_COUNT=$((FAIL_COUNT+1))
    FAILED_CASES+=("X3: --clean alone also drops the worker's DB")
fi

# === Additional cases: --purge / confirmation prompt (--yes) / live connections (--now) ===
# Call mkfs directly (run_mkfs is for --clean). -f pgfs.toml is the one written by the previous mkfs (--purge takes the connection from it).
mkfs_raw() {
    log ">>> mkfs.pgfs $*"
    "$MKFS_BIN" -f pgfs.toml -c "$COORD_CONN" -s pgfs --super "$SUPER_CONN" "$@" 2>&1 | tee -a "$LOG" >/dev/null
    return ${PIPESTATUS[0]}
}
coord_db() { sqlc -tA -c "SELECT count(*) FROM pg_database WHERE datname='$PGFS_DB'" | tr -d '[:space:]'; }
worker_db() { sqlw -tA -c "SELECT count(*) FROM pg_database WHERE datname='$PGFS_DB'" | tr -d '[:space:]'; }
# Judge one case. $1=name $2=description of the condition (expected vs actual) $3..=test expression to evaluate (PASS if all true)
check() {
    local name=$1 desc=$2; shift 2
    if eval "$*"; then
        log "  ✓ $name PASS"; PASS_COUNT=$((PASS_COUNT+1)); return
    fi
    log "  ✗ $name FAIL: $desc"; FAIL_COUNT=$((FAIL_COUNT+1)); FAILED_CASES+=("$name")
}

sec "Case X4: --clean together with --purge does nothing"
reset_cluster
run_mkfs yes coord >/dev/null || die "X4 setup mkfs failed"
mkfs_raw --clean --purge --yes; rc=$?
check X4 "exit=$rc db=$(coord_db) (expected 2 / 1)" '[ "$rc" -eq 2 ] && [ "$(coord_db)" = "1" ]'

sec "Case X5: --purge without --yes in non-interactive mode does nothing"
mkfs_raw --purge; rc=$?
check X5 "exit=$rc db=$(coord_db) (expected 3 / 1)" '[ "$rc" -eq 3 ] && [ "$(coord_db)" = "1" ]'

sec "Case X6: --purge --yes drops the coord + worker DBs and stops (keeps the toml and the role)"
reset_cluster
run_mkfs yes coord_worker >/dev/null || die "X6 setup mkfs failed"
mkfs_raw --purge --yes; rc=$?
role=$(sqlc -tA -c "SELECT count(*) FROM pg_roles WHERE rolname='$PGFS_USER'" | tr -d '[:space:]')
check X6 "exit=$rc coord=$(coord_db) worker=$(worker_db) toml=$([ -f pgfs.toml ] && echo 1 || echo 0) role=$role (expected 0 / 0 / 0 / 1 / 1)" \
    '[ "$rc" -eq 0 ] && [ "$(coord_db)" = "0" ] && [ "$(worker_db)" = "0" ] && [ -f pgfs.toml ] && [ "$role" = "1" ]'

sec "Case X7: a live mount blocks the drop without --now / --now drops it"
reset_cluster
run_mkfs yes coord >/dev/null || die "X7 setup mkfs failed"
sqlcpg -c "INSERT INTO pgfs.pgfs_mounts (mount_id, host, pid, mountpoint, mode) VALUES ('x7', 'fakehost', 1, '/fake/x7', 'fuse')" >/dev/null
mkfs_raw --purge --yes; rc1=$?; db1=$(coord_db)
grep -q 'fakehost /fake/x7' "$LOG"; seen=$?
mkfs_raw --purge --yes --now; rc2=$?
check X7 "exit=$rc1 db=$db1 listed=$([ $seen -eq 0 ] && echo yes || echo no) → --now: exit=$rc2 db=$(coord_db) (expected 3 / 1 / yes → 0 / 0)" \
    '[ "$rc1" -eq 3 ] && [ "$db1" = "1" ] && [ $seen -eq 0 ] && [ "$rc2" -eq 0 ] && [ "$(coord_db)" = "0" ]'

sec "Case X8: another connection (psql) blocks the drop without --now / --now cuts it and recreates"
reset_cluster
run_mkfs yes coord >/dev/null || die "X8 setup mkfs failed"
docker exec -d "$COORD_NAME" psql -h localhost -p "$COORD_PORT" -U "$SUPER_USER" -d "$PGFS_DB" -c "SELECT pg_sleep(120)"
sleep 1
mkfs_raw --clean --yes --citus; rc1=$?
mkfs_raw --clean --yes --now --citus; rc2=$?
check X8 "exit=$rc1 → --now: exit=$rc2 db=$(coord_db) (expected 3 → 0 / 1)" '[ "$rc1" -eq 3 ] && [ "$rc2" -eq 0 ] && [ "$(coord_db)" = "1" ]'

sec "Case X9: interactive confirmation (n aborts / y drops)"
# Create a terminal with script(1) and feed the answers (with a piped stdin it is judged non-interactive).
reset_cluster
run_mkfs yes coord >/dev/null || die "X9 setup mkfs failed"
cmd="'$MKFS_BIN' -f pgfs.toml -c '$COORD_CONN' -s pgfs --super '$SUPER_CONN' --purge"
printf 'n\n' | script -qec "$cmd" /dev/null >>"$LOG" 2>&1; rc1=$?; db1=$(coord_db)
printf 'y\n' | script -qec "$cmd" /dev/null >>"$LOG" 2>&1; rc2=$?
check X9 "n: exit=$rc1 db=$db1 → y: exit=$rc2 db=$(coord_db) (expected 3 / 1 → 0 / 0)" '[ "$rc1" -eq 3 ] && [ "$db1" = "1" ] && [ "$rc2" -eq 0 ] && [ "$(coord_db)" = "0" ]'

# === Summary ===
sec "Summary"
log "Total: $((PASS_COUNT+FAIL_COUNT))   PASS: $PASS_COUNT   FAIL: $FAIL_COUNT"
if [ $FAIL_COUNT -gt 0 ]; then
    log "Failed cases:"
    for c in "${FAILED_CASES[@]}"; do
        log "  - $c"
    done
    exit 1
fi
log "all 27 cases PASS (matrix 18 + additional 9)"
