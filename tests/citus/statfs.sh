#!/usr/bin/env bash
#
# df (statfs) dedicated test. The source of truth for the design is docs/df-support.md.
#
# Verifies the behavior of pgfs_statfs() on a multi-node Citus (coord + worker1). Where e2e.sh
# checks the correctness of FS operations, this one checks "what the server-side statfs function
# returns".
#
# Items verified:
#   require mode (worker aggregation — the main goal and the only previously unverified path):
#     R1. mkfs --statfs require creates pgfs_statfs() on the coordinator
#     R2. fs_free / statvfs are distributed to worker1 too (run_command_on_all_nodes)
#     R3. SELECT * FROM pgfs_statfs() on the coordinator returns total>0 / 0<avail<=total
#     R4. that return value matches worker1's own pgfs_fs_free()
#         (one worker, so "aggregate = the worker's value"; also evidence the coord is not double-counted)
#     R5. * proof of mechanism: pgfs_statfs() still returns a value even after dropping only the
#         coordinator's fs_free / statvfs. The ELSE branch of statfs() calls the worker's fs_free via
#         run_command_on_workers, so it holds even without the coord-local functions (= distinguishable
#         from coord-local execution).
#   auto mode:
#     A1. plperl is enabled, so pgfs_statfs() returns a measured value just like require
#   nominal mode:
#     N1. pgfs_statfs() / fs_free / statvfs are dropped and do not exist
#         (clients fall back to the nominal capacity)
#
# A Citus image with plperl is required. There is no off-the-shelf one, so build and use
# tests/docker/Dockerfile.citus-plperl (auto-built if PGFS_PROBE_IMAGE is unset).
#
# Intended to run on linux_client. If the docker daemon is stopped it is auto-started and restored
# to the original state via a trap at exit (the same idiom as race_multinode.sh / audit.sh).
#
# Usage:
#   bash tests/citus/statfs.sh
#
# Env vars (shared with race_multinode.sh / audit.sh): PGFS_PROBE_IMAGE / COORD_NAME /
#   WORKER1_NAME / COORD_PORT / WORKER1_PORT / SUPER_USER / SUPER_PASSWORD / PGFS_USER /
#   PGFS_PASSWORD / PGFS_DB / MKFS_BIN / PGFS_TEST_LOG
#
# Log: /tmp/citus_statfs.log

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

# === Configuration ===
# The image with plperl. If unset, build Dockerfile.citus-plperl as pgfs-citus-plperl.
IMAGE="${PGFS_PROBE_IMAGE:-pgfs-citus-plperl}"
COORD_NAME="${COORD_NAME:-pgfs-statfs-coord}"
WORKER1_NAME="${WORKER1_NAME:-pgfs-statfs-worker1}"
COORD_PORT="${COORD_PORT:-15552}"
WORKER1_PORT="${WORKER1_PORT:-15553}"
SUPER_USER="${SUPER_USER:-postgres}"
SUPER_PASSWORD="${SUPER_PASSWORD:-postgres}"
PGFS_USER="${PGFS_USER:-pgfs}"
PGFS_PASSWORD="${PGFS_PASSWORD:-pgfs}"
PGFS_DB="${PGFS_DB:-pgfs}"
LOG="${PGFS_TEST_LOG:-/tmp/citus_statfs.log}"

MKFS_BIN="${MKFS_BIN:-$HOME/project/pgfs_cs/bin/Publish/mkfs.pgfs}"

COORD_CONN="Host=localhost;Port=$COORD_PORT;Username=$PGFS_USER;Password=$PGFS_PASSWORD;Database=$PGFS_DB;SSL Mode=Disable"
SUPER_CONN="Host=localhost;Port=$COORD_PORT;Username=$SUPER_USER;Password=$SUPER_PASSWORD;Database=postgres;SSL Mode=Disable"
WORKER1_SPEC="localhost:$WORKER1_PORT"

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

# Run psql as super on coord / worker1 (-tA = tuples-only + unaligned).
sqlcpg() { docker exec -i "$COORD_NAME"   psql -h localhost -p "$COORD_PORT"   -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }
sqlcw1() { docker exec -i "$WORKER1_NAME" psql -h localhost -p "$WORKER1_PORT" -U "$SUPER_USER" -d "$PGFS_DB" "$@" 2>&1; }
scalarc()  { sqlcpg -tA -c "$1" | tr -d ' '; }
scalarw1() { sqlcw1 -tA -c "$1" | tr -d ' '; }

# Run mkfs --clean --citus --worker --statfs <mode> for the given mode
do_mkfs() {
	local mode="$1"
	sec "mkfs --clean --citus --worker $WORKER1_SPEC --statfs $mode"
	"$MKFS_BIN" --clean --citus \
		-c "$COORD_CONN" \
		-s pgfs \
		--super "$SUPER_CONN" \
		--worker "$WORKER1_SPEC" \
		--statfs "$mode" 2>&1 | tee -a "$LOG" >/dev/null
	return "${PIPESTATUS[0]}"
}

# === Cleanup ===
DOCKER_WAS_RUNNING=no
if systemctl is-active docker >/dev/null 2>&1; then DOCKER_WAS_RUNNING=yes; fi

cleanup() {
	sec "Cleanup"
	docker stop "$COORD_NAME"   >/dev/null 2>&1 || true
	docker stop "$WORKER1_NAME" >/dev/null 2>&1 || true
	docker rm   "$COORD_NAME"   >/dev/null 2>&1 || true
	docker rm   "$WORKER1_NAME" >/dev/null 2>&1 || true
	# Keep the self-built local image ($IMAGE) (rebuilds are fast thanks to the layer cache).
	if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
		log "docker daemon was originally stopped, so systemctl stop docker"
		sudo systemctl stop docker        >/dev/null 2>&1 || true
		sudo systemctl stop docker.socket >/dev/null 2>&1 || true
	fi
	log "main log: $LOG"
}
trap cleanup EXIT

# === Sanity ===
: > "$LOG"
log "Starting df (statfs) verification (image=$IMAGE)"
[ -x "$MKFS_BIN" ] || die "mkfs binary not found: $MKFS_BIN"

# === Docker daemon ===
sec "Docker daemon (initial = $DOCKER_WAS_RUNNING)"
if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
	log "starting: sudo systemctl start docker"
	sudo systemctl start docker
	for i in $(seq 1 10); do
		if docker info >/dev/null 2>&1; then break; fi
		sleep 1
	done
fi

# === Prepare the image with plperl ===
# If PGFS_PROBE_IMAGE is explicitly set, use it (assumed to already include plperl).
# The default (pgfs-citus-plperl) is built from Dockerfile.citus-plperl.
sec "Image: $IMAGE"
if [ -n "${PGFS_PROBE_IMAGE:-}" ]; then
	log "Using PGFS_PROBE_IMAGE=$IMAGE (assumed to include plperl)"
else
	log "docker build -t $IMAGE -f tests/docker/Dockerfile.citus-plperl tests/docker/"
	docker build -t "$IMAGE" -f "$REPO/tests/docker/Dockerfile.citus-plperl" "$REPO/tests/docker/" 2>&1 \
		| tee -a "$LOG" >/dev/null || die "image build failed"
fi

# === Containers (--network host) ===
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

log "waiting until both PGs are ready (max 60s)"
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

# Lightly confirm plperl is really installed (reject a forgotten-plperl image early).
# The pgfs DB does not exist yet (pre-mkfs), so smoke-test on the always-present postgres DB.
if ! docker exec -i "$WORKER1_NAME" psql -h localhost -p "$WORKER1_PORT" -U "$SUPER_USER" -d postgres \
		-c "CREATE EXTENSION IF NOT EXISTS plperlu;" >/dev/null 2>&1; then
	die "CREATE EXTENSION plperlu failed on worker1 (does $IMAGE lack plperl?)"
fi

# ======================================================================
# require mode (worker aggregation — the main goal)
# ======================================================================
do_mkfs require || die "mkfs --statfs require failed"

sec "require: R1 pgfs_statfs() exists on the coordinator"
HAS_STATFS=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_statfs()') IS NOT NULL;")
if [ "$HAS_STATFS" = "t" ]; then
	pass "R1 pgfs_statfs() exists on the coordinator"
else
	fail "R1 pgfs_statfs() missing" "to_regprocedure=NULL (got '$HAS_STATFS')"
fi

sec "require: R2 fs_free / statvfs are distributed to worker1 too"
W1_FSFREE=$(scalarw1 "SELECT to_regprocedure('pgfs.pgfs_fs_free(text)') IS NOT NULL;")
W1_STATVFS=$(scalarw1 "SELECT to_regprocedure('pgfs.pgfs_statvfs(text)') IS NOT NULL;")
if [ "$W1_FSFREE" = "t" ] && [ "$W1_STATVFS" = "t" ]; then
	pass "R2 fs_free / statvfs distributed to worker1 (run_command_on_all_nodes)"
else
	fail "R2 distribution to worker1 missing" "fs_free=$W1_FSFREE statvfs=$W1_STATVFS"
fi

sec "require: R3 pgfs_statfs() returns a measured value on the coordinator"
ST=$(scalarc "SELECT total||','||avail FROM pgfs.pgfs_statfs();")
ST_TOTAL="${ST%%,*}"; ST_AVAIL="${ST##*,}"
log "  pgfs_statfs(): total=$ST_TOTAL avail=$ST_AVAIL"
if [[ "$ST_TOTAL" =~ ^[0-9]+$ ]] && [[ "$ST_AVAIL" =~ ^[0-9]+$ ]] \
	&& [ "$ST_TOTAL" -gt 0 ] && [ "$ST_AVAIL" -gt 0 ] && [ "$ST_AVAIL" -le "$ST_TOTAL" ]; then
	pass "R3 total>0 / 0<avail<=total (total=$ST_TOTAL avail=$ST_AVAIL)"
else
	fail "R3 invalid statfs return value" "total=$ST_TOTAL avail=$ST_AVAIL"
fi

sec "require: R4 the return value matches worker1's fs_free() (aggregate = one worker)"
WF=$(scalarw1 "SELECT total||','||avail FROM pgfs.pgfs_fs_free();")
WF_TOTAL="${WF%%,*}"; WF_AVAIL="${WF##*,}"
log "  worker1 fs_free(): total=$WF_TOTAL avail=$WF_AVAIL"
# For reference, also emit worker1's container df (a human cross-check)
W1_DATADIR=$(scalarw1 "SHOW data_directory;")
DF_LINE=$(docker exec "$WORKER1_NAME" sh -c "df -B1 --output=size,avail '$W1_DATADIR' 2>/dev/null | tail -1" 2>/dev/null | tr -s ' ')
log "  worker1 df $W1_DATADIR: $DF_LINE"
r4_ok=1
# total does not change over time, so require an exact match
if [ "$ST_TOTAL" != "$WF_TOTAL" ]; then
	r4_ok=0
	log "    total mismatch: statfs=$ST_TOTAL worker_fs_free=$WF_TOTAL"
fi
# avail drifts slightly with disk activity between the two calls. Treat as equal within 10%.
if [[ "$ST_AVAIL" =~ ^[0-9]+$ ]] && [[ "$WF_AVAIL" =~ ^[0-9]+$ ]] && [ "$WF_AVAIL" -gt 0 ]; then
	DIFF=$(( ST_AVAIL > WF_AVAIL ? ST_AVAIL - WF_AVAIL : WF_AVAIL - ST_AVAIL ))
	TOL=$(( WF_AVAIL / 10 ))
	if [ "$DIFF" -gt "$TOL" ]; then
		r4_ok=0
		log "    avail diverges: statfs=$ST_AVAIL worker=$WF_AVAIL diff=$DIFF tol=$TOL"
	fi
else
	r4_ok=0
	log "    worker fs_free avail invalid: $WF_AVAIL"
fi
if [ "$r4_ok" = "1" ]; then
	pass "R4 statfs == worker1 fs_free (total exact + avail within 10% → aggregate = one worker, no coord double-count)"
else
	fail "R4 statfs diverges from worker1 fs_free" "statfs=$ST_TOTAL/$ST_AVAIL worker=$WF_TOTAL/$WF_AVAIL"
fi

sec "require: R5 * proof of mechanism — statfs() still returns a value after dropping coord fs_free/statvfs"
# Drop only on the coordinator. Without citus.enable_ddl_propagation=off, Citus would propagate the
# DROP to the worker too (= the worker's fs_free would also vanish), which would not prove the
# mechanism. Dropping with off + in the same session makes it apply to the coord only.
# fs_free/statvfs are not registered as Citus distributed objects (they were just sprayed onto each
# node as plain CREATE FUNCTION via run_command_on_all_nodes), so a DROP under off affects coord only.
sqlcpg -c "SET citus.enable_ddl_propagation=off; DROP FUNCTION IF EXISTS pgfs.pgfs_fs_free(text); DROP FUNCTION IF EXISTS pgfs.pgfs_statvfs(text);" >/dev/null 2>&1
COORD_FSFREE_AFTER=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_fs_free(text)') IS NOT NULL;")
W1_FSFREE_AFTER=$(scalarw1 "SELECT to_regprocedure('pgfs.pgfs_fs_free(text)') IS NOT NULL;")
ST2=$(scalarc "SELECT total||','||avail FROM pgfs.pgfs_statfs();")
ST2_TOTAL="${ST2%%,*}"; ST2_AVAIL="${ST2##*,}"
log "  coord fs_free exists(after drop)=$COORD_FSFREE_AFTER / worker1 fs_free=$W1_FSFREE_AFTER / pgfs_statfs()=$ST2"
if [ "$COORD_FSFREE_AFTER" = "f" ] && [ "$W1_FSFREE_AFTER" = "t" ] \
	&& [[ "$ST2_TOTAL" =~ ^[0-9]+$ ]] && [[ "$ST2_AVAIL" =~ ^[0-9]+$ ]] \
	&& [ "$ST2_TOTAL" -gt 0 ] && [ "$ST2_AVAIL" -gt 0 ]; then
	pass "R5 statfs() returns a value even without the coord-local fs_free (it survives on the worker) (= worker aggregation via run_command_on_workers)"
else
	fail "R5 failed to prove the worker-aggregation path" "coord_fsfree=$COORD_FSFREE_AFTER worker_fsfree=$W1_FSFREE_AFTER statfs=$ST2"
fi

# ======================================================================
# auto mode
# ======================================================================
do_mkfs auto || die "mkfs --statfs auto failed"

sec "auto: A1 plperl is enabled, so pgfs_statfs() returns a measured value"
A_HAS=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_statfs()') IS NOT NULL;")
A_ST=$(scalarc "SELECT total||','||avail FROM pgfs.pgfs_statfs();")
A_TOTAL="${A_ST%%,*}"; A_AVAIL="${A_ST##*,}"
log "  pgfs_statfs(): total=$A_TOTAL avail=$A_AVAIL"
if [ "$A_HAS" = "t" ] \
	&& [[ "$A_TOTAL" =~ ^[0-9]+$ ]] && [[ "$A_AVAIL" =~ ^[0-9]+$ ]] \
	&& [ "$A_TOTAL" -gt 0 ] && [ "$A_AVAIL" -gt 0 ] && [ "$A_AVAIL" -le "$A_TOTAL" ]; then
	pass "A1 auto: pgfs_statfs() measured value (total=$A_TOTAL avail=$A_AVAIL)"
else
	fail "A1 auto mode does not return a measured statfs value" "has=$A_HAS total=$A_TOTAL avail=$A_AVAIL"
fi

# ======================================================================
# nominal mode
# ======================================================================
do_mkfs nominal || die "mkfs --statfs nominal failed"

sec "nominal: N1 statfs / fs_free / statvfs do not exist (nominal-capacity fallback)"
N_STATFS=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_statfs()') IS NOT NULL;")
N_FSFREE=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_fs_free(text)') IS NOT NULL;")
N_STATVFS=$(scalarc "SELECT to_regprocedure('pgfs.pgfs_statvfs(text)') IS NOT NULL;")
# Confirm they are gone on the worker too (DropStatfsFunctionsAsync drops on all nodes)
NW_FSFREE=$(scalarw1 "SELECT to_regprocedure('pgfs.pgfs_fs_free(text)') IS NOT NULL;")
log "  coord: statfs=$N_STATFS fs_free=$N_FSFREE statvfs=$N_STATVFS / worker1: fs_free=$NW_FSFREE"
if [ "$N_STATFS" = "f" ] && [ "$N_FSFREE" = "f" ] && [ "$N_STATVFS" = "f" ] && [ "$NW_FSFREE" = "f" ]; then
	pass "N1 nominal: the functions are dropped on both coord and worker (clients fall back to nominal capacity)"
else
	fail "N1 functions remain under nominal" "coord statfs=$N_STATFS fs_free=$N_FSFREE statvfs=$N_STATVFS / worker fs_free=$NW_FSFREE"
fi

# === Summary ===
sec "Summary"
log "Total: $((T_PASS+T_FAIL))   PASS: $T_PASS   FAIL: $T_FAIL"
if [ $T_FAIL -gt 0 ]; then
	log "Failed:"
	for c in "${FAILED[@]}"; do log "  - $c"; done
	exit 1
fi
log "all $T_PASS items PASS"
