-- PostgreSQL FileSystem Driver (PGFS)
--
-- pgfs_database.sql
-- PGFS database definition
--
-- Change the TABLESPACE LOCATION ( '/var/lib/pgfs' ) as needed.
--
-- Run it with a command like:
--
-- sudo -u postgres mkdir -p '/var/lib/pgfs'
-- psql -U postgres -h 127.0.0.1 -p 5432 postgres
--

DROP DATABASE IF EXISTS pgfs
;

DROP TABLESPACE IF EXISTS pgfs
;

DROP USER IF EXISTS pgfs
;

CREATE USER pgfs
    WITH SUPERUSER
    ENCRYPTED PASSWORD 'pgfs'
;

CREATE TABLESPACE pgfs
    OWNER pgfs
    LOCATION '/var/lib/pgfs'
;

CREATE DATABASE pgfs
    WITH
    OWNER = pgfs
    ENCODING = 'UTF-8'
    TABLESPACE = pgfs
    TEMPLATE = template0
;
