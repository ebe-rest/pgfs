-- Diagnostics for the pgfs Citus distributed setup
--
-- Assumes the state right after `mkfs --citus` (an empty FS). See ./README.md for details.
-- Example expected output is in the trailing comment of each block.

\echo '=== citus_version ==='
SELECT citus_version();

\echo ''
\echo '=== pg_dist_node (coordinator + workers) ==='
-- In a single-node setup there is only the coordinator (groupid=0, noderole=primary, shouldhaveshards=t).
SELECT nodeid, groupid, nodename, nodeport, noderole, isactive, shouldhaveshards
  FROM pg_dist_node ORDER BY nodeid;

\echo ''
\echo '=== citus_tables (distributed / local placement) ==='
-- Expected:
--   pgfs.pgfs_inode      distributed parent_id  colocation 1
--   pgfs.pgfs_data       distributed id         colocation 1
--   pgfs.pgfs_data_chunk distributed data_id    colocation 1
--   pgfs.pgfs_lock       distributed target_id  colocation 1
--   pgfs.pgfs_settings   local       <none>     colocation 0
SELECT table_name, citus_table_type, distribution_column, colocation_id
  FROM citus_tables ORDER BY table_name;

\echo ''
\echo '=== shard count / table ==='
SELECT logicalrelid::text AS table_name, COUNT(*) AS shards
  FROM pg_dist_shard
  GROUP BY logicalrelid
  ORDER BY table_name;

\echo ''
\echo '=== PK structure of pgfs_inode (Citus constraint: must include the distribution key parent_id) ==='
SELECT conname, pg_get_constraintdef(oid)
  FROM pg_constraint
  WHERE conrelid = 'pgfs.pgfs_inode'::regclass
  ORDER BY conname;

\echo ''
\echo '=== EXPLAIN: WHERE id = N only (parent_id unknown) is a cross-shard hop ==='
-- Target the root inode (id=0, parent_id=0). A lookup without parent_id scans all shards.
EXPLAIN (COSTS off, VERBOSE off)
  SELECT id FROM pgfs.pgfs_inode WHERE id = 0;

\echo ''
\echo '=== EXPLAIN: WHERE parent_id = N AND id = N is single-shard ==='
EXPLAIN (COSTS off, VERBOSE off)
  SELECT id FROM pgfs.pgfs_inode WHERE parent_id = 0 AND id = 0;

\echo ''
\echo '=== EXPLAIN: a data_chunk read by data_id is single-shard ==='
EXPLAIN (COSTS off, VERBOSE off)
  SELECT substring(payload FROM 1 FOR 4096) FROM pgfs.pgfs_data_chunk WHERE data_id = 1 AND chunk_index = 0;

\echo ''
\echo '=== contents of pgfs_settings (the 4 rows mkfs inserts) ==='
SELECT scope, key, value FROM pgfs.pgfs_settings ORDER BY scope, key;
