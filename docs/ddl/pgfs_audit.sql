-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_audit.sql
-- Audit log (records metadata-changing operations)
--
-- Records metadata changes such as chmod / chown / delete / rename / create / hard link, one operation
-- per row. A monthly RANGE-partitioned table keyed on occurred_at. The canonical design is ../audit-log.md.
--
-- On/off is controlled by the pgfs_settings (scope='audit', key='enabled') row (initialized by mkfs --audit).
--
-- The monthly partitions (pgfs_audit_YYYY_MM) are always created by the application before INSERT
-- (ensure-before-insert), so an audit row always lands in its own month partition. No DEFAULT partition is
-- created, because it is a trap: once rows accumulate in DEFAULT, you can no longer CREATE a month
-- partition covering that range (PG errors out).
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_audit CASCADE
;

CREATE TABLE pgfs.pgfs_audit
(
	id            BIGSERIAL NOT NULL,
	occurred_at   TIMESTAMP NOT NULL DEFAULT current_timestamp,
	op            TEXT      NOT NULL,
	target_id     BIGINT    NULL,
	parent_id     BIGINT    NULL,
	name          TEXT      NULL,
	detail        JSONB     NOT NULL DEFAULT '{}'::JSONB,
	caller_ip     INET      NULL,
	caller_host   TEXT      NULL,
	caller_uid    BIGINT    NULL,
	caller_uname  TEXT      NULL,
	caller_domain TEXT      NULL
)
PARTITION BY RANGE (occurred_at)
;

-- The PK is a composite including the partition key occurred_at (the Postgres partitioned-table constraint
-- + the need to include the distribution key in a unique constraint under Citus; the same situation as
-- pgfs_inode's (parent_id, id)).
ALTER TABLE pgfs.pgfs_audit
	ADD CONSTRAINT pk_pgfs_audit PRIMARY KEY (occurred_at, id)
;

CREATE INDEX ix_pgfs_audit_id ON pgfs.pgfs_audit (id)
;

CREATE INDEX ix_pgfs_audit_op ON pgfs.pgfs_audit (op)
;

CREATE INDEX ix_pgfs_audit_target ON pgfs.pgfs_audit (target_id)
;

-- Example month partition (normally the application creates these automatically before INSERT; no DEFAULT
-- is created on purpose).
-- CREATE TABLE pgfs.pgfs_audit_YYYY_MM PARTITION OF pgfs.pgfs_audit
-- 	FOR VALUES FROM ('YYYY-MM-01') TO ('<next month>-01')
-- ;
