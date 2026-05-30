#!/usr/bin/env bash
#
# A one-off script that verifies the behavior of the pgfs multi-node Citus design on real hardware.
# Intended to run on linux_client (docker installed, daemon assumed not running).
#
# Hypotheses to verify:
#   [Q2] When citus_add_node('worker', port) is called on the coordinator, does the coordinator
#        row get auto-synced into the worker's pg_dist_node too?
#        (If it does, the per-worker citus_set_coordinator_host call is unnecessary.)
#   [Q2b] Even without auto-sync, does citus_add_local_table_to_metadata succeed?
#   [Schema] Does a CREATE SCHEMA on the coordinator propagate to the worker? (re-confirming a known result)
#   [DropCascade] Does a DROP SCHEMA CASCADE on the coordinator also clean up the shard tables?
#   [Idempotent] What happens if citus_add_node is called twice?
#   [Shouldhaveshards] In a multi-node setup, check the default shouldhaveshards value of the
#                      worker and of the coordinator.
#
# On exit, **restore the original state** of containers / image / network / docker daemon.
# If the docker daemon was already running, leave it alone; if it was stopped, start -> stop it.
#
# Log: all output is also copied to /tmp/citus_multinode_probe.log (for easy retrieval later).

set -uo pipefail
# Note: do not use -e (we do not want a per-statement psql ERROR to abort the probe).
#       Instead, fatal steps where "if this dies, nothing after matters" (pull / run / wait)
#       exit explicitly.

# === Configuration ===
# Overridable via PGFS_PROBE_IMAGE. For tags see https://hub.docker.com/r/citusdata/citus/tags.
# A patch-level tag like 13.1.1 is often absent on Docker Hub (a matter of how often the official
# images are pushed). As long as the Citus major/minor matches (13.x) the behavior is the same, so
# latest is sufficient.
IMAGE="${PGFS_PROBE_IMAGE:-citusdata/citus:latest}"
NETWORK="${NETWORK:-pgfs-citus-verify-net}"
COORD_NAME="${COORD_NAME:-pgfs-citus-verify-coord}"
WORKER1_NAME="${WORKER1_NAME:-pgfs-citus-verify-worker1}"
COORD_PORT="${COORD_PORT:-15432}"   # host-side published port (maps container 5432)
WORKER1_PORT="${WORKER1_PORT:-15433}"
PG_USER="${PG_USER:-postgres}"
PG_PASSWORD="${PG_PASSWORD:-postgres}"
LOG="${PGFS_TEST_LOG:-/tmp/citus_multinode_probe.log}"

# === Helpers ===
log()  { local line="[$(date +%H:%M:%S)] $*"; echo "$line"; echo "$line" >> "$LOG"; }
hr()   { local line="--------------------------------------------------------------------------------"; echo "$line"; echo "$line" >> "$LOG"; }
sec()  { echo "" >> "$LOG"; hr; log "=== $* ==="; hr; }
die()  { log "ERROR: $*"; exit 1; }
sql_coord()   { docker exec -i "$COORD_NAME"   psql -U "$PG_USER" -d postgres "$@" 2>&1 | tee -a "$LOG"; return 0; }
sql_worker1() { docker exec -i "$WORKER1_NAME" psql -U "$PG_USER" -d postgres "$@" 2>&1 | tee -a "$LOG"; return 0; }

# === Remember initial docker daemon state for trap ===
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
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
    docker rmi   "$IMAGE"        >/dev/null 2>&1 || true
    if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
        log "docker daemon was originally stopped, so systemctl stop docker"
        sudo systemctl stop docker          >/dev/null 2>&1 || true
        sudo systemctl stop docker.socket   >/dev/null 2>&1 || true
    else
        log "docker daemon was already running, so leave it alone"
    fi
    log "the log is kept at $LOG"
}
trap cleanup EXIT

# === Reset log file ===
: > "$LOG"
log "starting the pgfs Citus multi-node probe (image=$IMAGE)"

# === 0. Docker daemon ===
sec "0. Docker daemon (initial state = $DOCKER_WAS_RUNNING)"
if [ "$DOCKER_WAS_RUNNING" = "no" ]; then
    log "start: sudo systemctl start docker"
    sudo systemctl start docker
    for i in $(seq 1 10); do
        if docker info >/dev/null 2>&1; then break; fi
        sleep 1
    done
fi
docker version --format '{{.Server.Version}}' 2>&1 | tee -a "$LOG" || { log "could not start the docker daemon"; exit 1; }

# === 1. Pull image ===
sec "1. Pull $IMAGE"
if ! docker pull "$IMAGE" 2>&1 | tee -a "$LOG"; then
    log ""
    log "pull failed. The tag '$IMAGE' may not exist on Docker Hub, or the network is down."
    log "alternatives:"
    log "  PGFS_PROBE_IMAGE=citusdata/citus:12.1.5 $0       # a known stable tag"
    log "  PGFS_PROBE_IMAGE=citusdata/citus:latest $0       # try the latest"
    log "  https://hub.docker.com/r/citusdata/citus/tags    # full tag list"
    die "image pull"
fi

# === 2. Network ===
sec "2. Network"
docker network create "$NETWORK" 2>&1 | tee -a "$LOG" || true

# === 3. Start coordinator + worker ===
sec "3. Containers"
# Note: for the probe we use POSTGRES_HOST_AUTH_METHOD=trust + citus.node_conninfo=sslmode=disable
#       to let coordinator->worker authentication pass through. Not OK in production, but fine for a
#       one-shot probe confined to a docker network.
log "coordinator $COORD_NAME (host port $COORD_PORT)"
if ! docker run -d --name "$COORD_NAME" --network "$NETWORK" \
        --hostname "$COORD_NAME" \
        -e POSTGRES_PASSWORD="$PG_PASSWORD" \
        -e POSTGRES_HOST_AUTH_METHOD=trust \
        -p "$COORD_PORT:5432" \
        "$IMAGE" \
        postgres -c citus.node_conninfo=sslmode=disable 2>&1 | tee -a "$LOG"; then
    die "failed to start the coordinator container"
fi

log "worker1 $WORKER1_NAME (host port $WORKER1_PORT)"
if ! docker run -d --name "$WORKER1_NAME" --network "$NETWORK" \
        --hostname "$WORKER1_NAME" \
        -e POSTGRES_PASSWORD="$PG_PASSWORD" \
        -e POSTGRES_HOST_AUTH_METHOD=trust \
        -p "$WORKER1_PORT:5432" \
        "$IMAGE" \
        postgres -c citus.node_conninfo=sslmode=disable 2>&1 | tee -a "$LOG"; then
    die "failed to start the worker1 container"
fi

log "wait for both PGs to become ready (up to 60s)"
ready=no
for i in $(seq 1 60); do
    if docker exec "$COORD_NAME"   pg_isready -U "$PG_USER" >/dev/null 2>&1 && \
       docker exec "$WORKER1_NAME" pg_isready -U "$PG_USER" >/dev/null 2>&1; then
        log "both PGs ready (${i}s)"
        ready=yes
        break
    fi
    sleep 1
done
if [ "$ready" != "yes" ]; then
    log "--- coordinator startup log ---"
    docker logs "$COORD_NAME"   2>&1 | tail -30 | tee -a "$LOG" || true
    log "--- worker1 startup log ---"
    docker logs "$WORKER1_NAME" 2>&1 | tail -30 | tee -a "$LOG" || true
    die "PG did not become ready within 60s"
fi

# === 4. Sanity: Citus extension ===
sec "4. Citus extension version"
sql_coord   -c "SELECT citus_version();"
sql_worker1 -c "SELECT citus_version();"

# === 5. Cluster setup: ONLY coordinator-side ===
# - run citus_set_coordinator_host + citus_add_node on the coordinator
# - **intentionally do NOT** call citus_set_coordinator_host on the worker
#   (so the next section can check whether auto-sync substitutes for it)
sec "5. citus_set_coordinator_host + citus_add_node on the coordinator"
sql_coord -c "SELECT citus_set_coordinator_host('$COORD_NAME', 5432);"
sql_coord -c "SELECT citus_add_node('$WORKER1_NAME', 5432);"

sec "5a. coordinator's pg_dist_node"
sql_coord -c "SELECT nodeid, groupid, nodename, nodeport, noderole, isactive, hasmetadata, metadatasynced, shouldhaveshards FROM pg_dist_node ORDER BY nodeid;"

# === [Q2] Check the worker's pg_dist_node ===
sec "6. [Q2] Does the worker's pg_dist_node contain a coordinator row?"
log "(if citus_add_node auto-syncs the coordinator info to the worker, then the per-worker"
log " 'call citus_set_coordinator_host on the worker' step is unnecessary)"
sql_worker1 -c "SELECT nodeid, groupid, nodename, nodeport, noderole, isactive, hasmetadata, metadatasynced, shouldhaveshards FROM pg_dist_node ORDER BY nodeid;"

# === [Idempotent] Call citus_add_node twice ===
sec "7. [Idempotent] call citus_add_node again with the same arguments"
sql_coord -c "SELECT citus_add_node('$WORKER1_NAME', 5432);"
sql_coord -c "SELECT count(*) AS dist_node_count FROM pg_dist_node;"

# === [Schema] DDL propagation (re-confirming a known result) ===
sec "8. [Schema] CREATE SCHEMA on the coordinator -> does it propagate to the worker?"
sql_coord -c "CREATE SCHEMA pgfs_probe AUTHORIZATION postgres;"
log "Look for pgfs_probe in the worker's pg_namespace:"
sql_worker1 -c "SELECT nspname FROM pg_namespace WHERE nspname = 'pgfs_probe';"

# === [Table + Distribute] ===
sec "9. [Distribute] CREATE TABLE + create_distributed_table"
sql_coord <<'SQL'
CREATE TABLE pgfs_probe.inode (
    id BIGINT NOT NULL,
    parent_id BIGINT NOT NULL,
    name TEXT NOT NULL,
    PRIMARY KEY (parent_id, id)
);
SELECT create_distributed_table('pgfs_probe.inode', 'parent_id');
SQL

sec "9a. Check the coordinator's citus_tables / pg_dist_shard"
sql_coord -c "SELECT table_name, citus_table_type, distribution_column, colocation_id FROM citus_tables WHERE table_name::text LIKE 'pgfs_probe%' ORDER BY table_name;"
sql_coord -c "SELECT n.nodename, count(*) AS shards_on_node FROM pg_dist_shard s JOIN pg_dist_placement p USING (shardid) JOIN pg_dist_node n ON p.groupid = n.groupid WHERE s.logicalrelid = 'pgfs_probe.inode'::regclass GROUP BY n.nodename ORDER BY n.nodename;"

sec "9b. Are the real shard tables visible on the worker?"
sql_worker1 -c "SELECT count(*) AS shard_table_count FROM pg_tables WHERE schemaname = 'pgfs_probe' AND tablename LIKE 'inode%';"

# === [Q2b] Does citus_add_local_table_to_metadata succeed without set_coordinator_host on the worker? ===
sec "10. [Q2b] citus_add_local_table_to_metadata (without having called set_coordinator_host on the worker)"
sql_coord <<'SQL'
CREATE TABLE pgfs_probe.settings (
    scope TEXT NOT NULL,
    key TEXT NOT NULL,
    value JSONB NOT NULL DEFAULT 'null'::JSONB,
    PRIMARY KEY (scope, key)
);
SELECT citus_add_local_table_to_metadata('pgfs_probe.settings');
SQL
log "Coordinator's citus_tables state:"
sql_coord -c "SELECT table_name, citus_table_type, distribution_column, colocation_id FROM citus_tables WHERE table_name::text LIKE 'pgfs_probe%' ORDER BY table_name;"
log "Is settings visible on the worker (is the metadata synced)?:"
sql_worker1 -c "SELECT count(*) AS settings_in_worker_pg_tables FROM pg_tables WHERE schemaname = 'pgfs_probe' AND tablename = 'settings';"

# === [Smoke] insert/select ===
sec "11. [Smoke] insert/select"
sql_coord <<'SQL'
INSERT INTO pgfs_probe.inode VALUES (1, 0, 'a'), (2, 0, 'b'), (3, 1, 'c'), (4, 2, 'd'), (5, 3, 'e');
INSERT INTO pgfs_probe.settings VALUES ('test', 'hello', '"world"'::jsonb);
SELECT count(*) AS inode_rows FROM pgfs_probe.inode;
SELECT * FROM pgfs_probe.settings;
SQL

# === [Shouldhaveshards] default per node ===
sec "12. [Shouldhaveshards] default shouldhaveshards of coordinator / worker"
log "pg_dist_node as seen from the coordinator:"
sql_coord -c "SELECT nodename, groupid, noderole, shouldhaveshards FROM pg_dist_node ORDER BY nodeid;"

# === [DropCascade] propagation to the worker ===
sec "13. [DropCascade] DROP SCHEMA CASCADE on the coordinator -> is the worker also cleaned up?"
sql_coord -c "DROP SCHEMA pgfs_probe CASCADE;"
log "Check pgfs_probe in the worker's pg_namespace (0 rows means it propagated):"
sql_worker1 -c "SELECT count(*) AS still_there FROM pg_namespace WHERE nspname = 'pgfs_probe';"
log "Worker-side shard tables (0 rows means cleaned up):"
sql_worker1 -c "SELECT count(*) AS shard_table_count FROM pg_tables WHERE schemaname = 'pgfs_probe';"

sec "Done"
log "all probes complete. output log: $LOG"
log "what to look at:"
log "  Q2  -> in section 6, if the worker's pg_dist_node has a coordinator row with groupid=0, auto-sync succeeded"
log "  Q2b -> in section 10, if citus_add_local_table_to_metadata did not ERROR, it works"
log "  Schema propagation -> in sections 8 / 13, if the nspname is visible on the worker, OK"
