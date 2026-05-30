-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_lock.sql
-- Cross-client mutual-exclusion lock token table
--
-- One row = one lock target (inode_id / data_id etc.). A row lock is taken with
-- `SELECT 1 FROM pgfs_lock WHERE target_id = @id FOR UPDATE` and released automatically when the
-- transaction ends. Under Citus the table is distributed by `target_id` to avoid a coordinator
-- bottleneck (see [docs/support_for_citus.md](../support_for_citus.md)).
--
-- Why a single BIGINT (target_id):
--   Citus's create_distributed_table is hash-distributed, and the hash input is a single column.
--   To make pgfs_lock distributed (avoiding a coordinator-local bottleneck) the distribution key must
--   be one column, so a (kind, id) composite is not possible. Hence the single target_id BIGINT column.
--   The consequence is that inode_id and data_id (both BIGSERIAL) can collide in the same numeric
--   space, which must be handled.
--
-- The collision avoidance used (target_id namespace):
--   - data lock:  target_id = data_id   (positive integer)
--   - inode lock: target_id = -inode_id (negative integer)
--   Unlike the 2-key form of pg_advisory_xact_lock(ns, key), this is a single column, so the namespace
--   is expressed on the value side (the sign).
--
-- An alternative would be to split pgfs_inode_lock into a separate table distributed by parent_id, so it
-- is co-located with inode operations and saves a cross-shard hop. The operational pattern is not settled,
-- so we start with a single pgfs_lock + sign namespace.
--
-- It carries no audit columns because it holds lock tokens, not data (it accumulates, but at ~50 bytes/row
-- that is under 100MB for a million files, and the policy is to grow via `INSERT ON CONFLICT DO NOTHING`
-- without ever `DELETE`-ing).
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_lock CASCADE
;

CREATE TABLE pgfs.pgfs_lock
(
	target_id BIGINT NOT NULL
)
;

ALTER TABLE pgfs.pgfs_lock
	ADD CONSTRAINT pk_pgfs_lock PRIMARY KEY (target_id)
;
