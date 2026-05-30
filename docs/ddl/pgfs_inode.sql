-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_inode.sql
-- Directory entries and inode attributes
--
-- The core table that holds information for files / directories / symlinks / hardlinks.
-- The root directory is seeded with id = 0.
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_inode CASCADE
;

CREATE TABLE pgfs.pgfs_inode
(
	id          BIGSERIAL NOT NULL,
	parent_id   BIGINT    NOT NULL,
	name        TEXT      NOT NULL,
	uname       TEXT      NOT NULL,
	gname       TEXT      NOT NULL,
	st_mode     INTEGER   NOT NULL,
	st_nlink    INTEGER   NOT NULL DEFAULT 1,
	st_size     BIGINT    NOT NULL DEFAULT 0,
	st_mtime    TIMESTAMP NOT NULL DEFAULT current_timestamp,
	st_ctime    TIMESTAMP NOT NULL DEFAULT current_timestamp,
	link_target TEXT      NULL,
	is_junction BOOLEAN   NOT NULL DEFAULT FALSE,
	data_id     BIGINT    NULL,
	xattrs      JSONB     NOT NULL DEFAULT '{}'::JSONB,
	created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
	created_by  TEXT      NOT NULL,
	updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
	updated_by  TEXT      NOT NULL
)
;

-- The PK is the composite (parent_id, id). When the table is distributed with Citus, a unique
-- constraint must include the distribution key (parent_id); see ../support_for_citus.md. Since id is
-- globally unique via BIGSERIAL, (parent_id, id) is effectively unique by id alone. A separate
-- standalone INDEX on id supports WHERE id = @id lookups.
-- Note: a standalone UNIQUE constraint on id is intentionally not created in either mode (uniqueness
-- is guaranteed by the BIGSERIAL sequence plus transaction discipline).
ALTER TABLE pgfs.pgfs_inode
	ADD CONSTRAINT pk_pgfs_inode
		PRIMARY KEY (parent_id, id)
;

INSERT INTO pgfs.pgfs_inode (
	id,
	parent_id,
	name,
	uname,
	gname,
	st_mode,
	st_nlink,
	st_size,
	is_junction,
	xattrs,
	created_by,
	updated_by
)
VALUES (
	0, -- id
	0, -- parent_id (self)
	'/', -- name
	'root', -- uname
	'root', -- gname
	16877, -- st_mode (directory, permission 0755)
	1, -- st_nlink
	0, -- st_size
	FALSE, -- is_junction
	'{}'::JSONB, -- xattrs
	'pgfs', -- created_by
	'pgfs' -- updated_by
)
ON CONFLICT (parent_id, name) DO NOTHING
;

CREATE UNIQUE INDEX uk_pgfs_inode_parent_name
	ON pgfs.pgfs_inode (parent_id, name)
;

CREATE INDEX ix_pgfs_inode_id ON pgfs.pgfs_inode (id)
;

CREATE INDEX ix_pgfs_inode_uname ON pgfs.pgfs_inode (uname)
;

CREATE INDEX ix_pgfs_inode_gname ON pgfs.pgfs_inode (gname)
;
