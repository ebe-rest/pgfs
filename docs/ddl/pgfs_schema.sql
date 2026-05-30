-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_schema.sql
-- PGFS schema definition
--
--
-- Run it with a command like:
--
-- psql -U pgfs -h 127.0.0.1 -p 5432 pgfs
--

DROP SCHEMA IF EXISTS pgfs CASCADE
;

CREATE SCHEMA pgfs
    AUTHORIZATION pgfs
;
