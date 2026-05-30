-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_data.sql
-- Reference management for file bodies
--
-- Manages hardlinks and bytea chunk splitting (pgfs_data_chunk).
-- One row corresponds to "one file's byte stream" and may be referenced by multiple inodes (hardlinks).
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
	created_at TIMESTAMP NOT NULL DEFAULT current_timestamp,
	created_by TEXT      NOT NULL,
	updated_at TIMESTAMP NOT NULL DEFAULT current_timestamp,
	updated_by TEXT      NOT NULL
)
;

ALTER TABLE pgfs.pgfs_data
	ADD CONSTRAINT pk_pgfs_data PRIMARY KEY (id)
;
