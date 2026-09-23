-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_data.sql
-- Reference management for file bodies
--
-- Manages hardlinks and bytea chunk splitting (pgfs_data_chunk).
-- One row corresponds to "one file's byte stream" and may be referenced by multiple inodes (hardlinks).
--
-- total_size is "the bytes actually occupied in PG" = the sum of length(payload) over every chunk.
-- It is not the logical size (pgfs_inode.st_size). FUSE's st_blocks (what du sees) is derived from it,
-- so for a sparse file it is smaller than st_size. For the details see docs/design/database.md,
-- the occupied bytes and st_blocks.
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_data CASCADE
;

CREATE TABLE pgfs.pgfs_data
(
	id         BIGSERIAL NOT NULL,
	chunk_size INTEGER   NOT NULL,
	total_size BIGINT    NOT NULL,
	created_at TIMESTAMP NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC'),
	created_by TEXT      NOT NULL,
	updated_at TIMESTAMP NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC'),
	updated_by TEXT      NOT NULL
)
;

ALTER TABLE pgfs.pgfs_data
	ADD CONSTRAINT pk_pgfs_data PRIMARY KEY (id)
;
