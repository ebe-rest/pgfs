-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_settings.sql
-- Settings value store (flat form)
--
-- A flat (scope, key) PK key-value store.
-- One row = the value of one Pgfs.Lib.Config.Field. e.g. (scope='mount', key='fallback_uname', value='"nobody"').
-- value is JSONB, holding the native JSON form: string -> "..." / number -> 42 / bool -> true / false.
--
-- Reading and writing setting values is handled by Pgfs.Lib.Config.ConfigStore (LoadAll / Save<T>).
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP TABLE IF EXISTS pgfs.pgfs_settings CASCADE
;

CREATE TABLE pgfs.pgfs_settings
(
	scope      TEXT      NOT NULL,
	key        TEXT      NOT NULL,
	value      JSONB     NOT NULL DEFAULT 'null'::JSONB,
	created_at TIMESTAMP NOT NULL DEFAULT current_timestamp,
	created_by TEXT      NOT NULL,
	updated_at TIMESTAMP NOT NULL DEFAULT current_timestamp,
	updated_by TEXT      NOT NULL
)
;

ALTER TABLE pgfs.pgfs_settings
	ADD CONSTRAINT pk_pgfs_settings PRIMARY KEY (scope, key)
;
