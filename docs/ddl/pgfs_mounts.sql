-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_mounts.sql
-- The registry of the running mounts (volatile)
--
-- Every mount.pgfs / assign.pgfs process INSERTs one row at startup, updates heartbeat_at with a periodic
-- heartbeat, and DELETEs the row on a normal exit. It is the foundation on which the status subcommand
-- sees "what is mounted where" across the cluster. The canonical design is
-- docs/design/control-plane.md.
--
-- config and stats are JSONB (a snapshot of the effective settings / the cache statistics and so on).
-- A stale row (heartbeat_at is old = the process died abnormally) is judged by the reader from how much
-- time has passed.
--

DROP TABLE IF EXISTS pgfs.pgfs_mounts CASCADE
;

CREATE TABLE pgfs.pgfs_mounts
(
	mount_id     TEXT      NOT NULL,
	host         TEXT      NOT NULL,
	pid          BIGINT    NOT NULL,
	mountpoint   TEXT      NOT NULL,
	mode         TEXT      NOT NULL,
	started_at   TIMESTAMP NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC'),
	heartbeat_at TIMESTAMP NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC'),
	config       JSONB     NOT NULL DEFAULT '{}'::JSONB,
	stats        JSONB     NOT NULL DEFAULT '{}'::JSONB
)
;

ALTER TABLE pgfs.pgfs_mounts
	ADD CONSTRAINT pk_pgfs_mounts PRIMARY KEY (mount_id)
;
