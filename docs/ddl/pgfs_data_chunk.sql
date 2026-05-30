-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_data_chunk.sql
-- File-body chunks (bytea)
--
-- One file body (pgfs_data) split into chunk_size units, one chunk = one bytea.
-- Data flow: pgfs_inode.data_id -> pgfs_data.id -> pgfs_data_chunk.payload
--
-- A 1MB bytea is reliably TOASTed, and PG 13+ partial detoast makes server-side
-- partial reads work via `substring(payload from off for len)`.
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_data_chunk CASCADE
;

CREATE TABLE pgfs.pgfs_data_chunk
(
	data_id     BIGINT    NOT NULL,
	chunk_index INTEGER   NOT NULL,
	payload     BYTEA     NOT NULL,
	created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
	created_by  TEXT      NOT NULL,
	updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
	updated_by  TEXT      NOT NULL
)
;

ALTER TABLE pgfs.pgfs_data_chunk
	ADD CONSTRAINT pk_pgfs_data_chunk PRIMARY KEY (data_id, chunk_index)
;
