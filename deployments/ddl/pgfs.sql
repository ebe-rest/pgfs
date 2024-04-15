

/* schema pgfs */


DROP SCHEMA IF EXISTS pgfs CASCADE;
CREATE SCHEMA pgfs AUTHORIZATION CURRENT_USER
;


/* basic function */

/*

select (('0000'||'000'||'111'||'111'||'111')::bit(16)::int);
--        ifmt    ugs    rwx    rwx    rwx
--        |       |||    |      |      |
--        |       |||    |      |      +-- other
--        |       |||    |      +--------- group
--        |       |||    +---------------- owner
--        |       ||+--------------------- stickey
--        |       |+---------------------- set gid
--        |       +----------------------- set uid
--        0001 socket
--        0010 character device
--        0100 directory
--        0110 block device
--        1000 regular file
--        1010 symbolic link
--        1100 socket
--        1111 bit mask

select ('0100'||'000'||'111'||'111'||'111')::bit(16)::int; -- 16895
select ('1000'||'000'||'111'||'111'||'111')::bit(16)::int; -- 33279

*/

DROP FUNCTION IF EXISTS pgfs.rand_int8 CASCADE;
CREATE FUNCTION pgfs.rand_int8() RETURNS
    BIGINT
AS $$
    SELECT cast((cast(random() AS NUMERIC) * 18446744073709551616) - 9223372036854775808 AS BIGINT)
$$ LANGUAGE sql VOLATILE;

DROP FUNCTION IF EXISTS pgfs.new_ino CASCADE;
CREATE FUNCTION pgfs.new_ino() RETURNS
    BIGINT
AS $$ BEGIN
    RETURN pgfs.rand_int8();
END $$ LANGUAGE plpgsql VOLATILE;

DROP FUNCTION IF EXISTS pgfs.default_ino   CASCADE; CREATE FUNCTION pgfs.default_ino()   RETURNS BIGINT    AS $$ SELECT pgfs.new_ino()                $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_mode  CASCADE; CREATE FUNCTION pgfs.default_mode()  RETURNS BIGINT    AS $$ SELECT 0                             $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_nlink CASCADE; CREATE FUNCTION pgfs.default_nlink() RETURNS BIGINT    AS $$ SELECT 0                             $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_uid   CASCADE; CREATE FUNCTION pgfs.default_uid()   RETURNS BIGINT    AS $$ SELECT 0                             $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_gid   CASCADE; CREATE FUNCTION pgfs.default_gid()   RETURNS BIGINT    AS $$ SELECT 0                             $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_size  CASCADE; CREATE FUNCTION pgfs.default_size()  RETURNS BIGINT    AS $$ SELECT 0                             $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_atime CASCADE; CREATE FUNCTION pgfs.default_atime() RETURNS TIMESTAMP AS $$ SELECT NOW()                         $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_ctime CASCADE; CREATE FUNCTION pgfs.default_ctime() RETURNS TIMESTAMP AS $$ SELECT NOW()                         $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_mtime CASCADE; CREATE FUNCTION pgfs.default_mtime() RETURNS TIMESTAMP AS $$ SELECT NOW()                         $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_itime CASCADE; CREATE FUNCTION pgfs.default_itime() RETURNS TIMESTAMP AS $$ SELECT NOW()                         $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_utime CASCADE; CREATE FUNCTION pgfs.default_utime() RETURNS TIMESTAMP AS $$ SELECT NOW()                         $$ LANGUAGE sql VOLATILE;
DROP FUNCTION IF EXISTS pgfs.default_dtime CASCADE; CREATE FUNCTION pgfs.default_dtime() RETURNS TIMESTAMP AS $$ SELECT CAST('infinity' AS TIMESTAMP) $$ LANGUAGE sql IMMUTABLE;

DROP FUNCTION IF EXISTS pgfs.default_bno   CASCADE; CREATE FUNCTION pgfs.default_bno()   RETURNS BIGINT AS $$ SELECT 0                 $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_first CASCADE; CREATE FUNCTION pgfs.default_first() RETURNS BIGINT AS $$ SELECT 0                 $$ LANGUAGE sql IMMUTABLE;
DROP FUNCTION IF EXISTS pgfs.default_data  CASCADE; CREATE FUNCTION pgfs.default_data()  RETURNS BYTEA  AS $$ SELECT CAST('' AS BYTEA) $$ LANGUAGE sql IMMUTABLE;


/* table node is inode */


DROP TABLE IF EXISTS pgfs.node CASCADE;
CREATE TABLE pgfs.node (
    ino   BIGINT    NOT NULL DEFAULT pgfs.default_ino()   /* inode number          */,
    mode  BIGINT    NOT NULL DEFAULT pgfs.default_mode()  /* mode                  */,
    nlink BIGINT    NOT NULL DEFAULT pgfs.default_nlink() /* number of hard links  */,
    uid   BIGINT    NOT NULL DEFAULT pgfs.default_uid()   /* user of owner         */,
    gid   BIGINT    NOT NULL DEFAULT pgfs.default_gid()   /* group of owner        */,
    size  BIGINT    NOT NULL DEFAULT pgfs.default_size()  /* file size             */,
    atime TIMESTAMP NOT NULL DEFAULT pgfs.default_atime() /* access                */,
    ctime TIMESTAMP NOT NULL DEFAULT pgfs.default_ctime() /* change status         */,
    mtime TIMESTAMP NOT NULL DEFAULT pgfs.default_mtime() /* modify                */,
    itime TIMESTAMP NOT NULL DEFAULT pgfs.default_itime() /* date time of inserted */,
    utime TIMESTAMP NOT NULL DEFAULT pgfs.default_utime() /* date time of updated  */,
    dtime TIMESTAMP NOT NULL DEFAULT pgfs.default_dtime() /* date time of deleted  */
)
;

ALTER TABLE pgfs.node ADD CONSTRAINT node_pk
PRIMARY KEY (ino) INCLUDE (dtime)
;

CREATE OR REPLACE FUNCTION pgfs.new_ino() RETURNS
    BIGINT
AS $$ DECLARE
    _ino BIGINT;
BEGIN
    LOOP
        _ino := pgfs.rand_int8();
        IF EXISTS (SELECT ino FROM pgfs.node WHERE ino = _ino) THEN
            CONTINUE;
        END IF;
        RETURN _ino;
    END LOOP;
END $$ LANGUAGE plpgsql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.get_node CASCADE;
CREATE FUNCTION pgfs.get_node(
    _ino BIGINT = NULL,
    _dtime TIMESTAMP = now()
) RETURNS
    SETOF pgfs.node
AS $$ BEGIN
    IF _ino IS NULL THEN
        IF _dtime IS NULL THEN
            RETURN QUERY SELECT * FROM pgfs.node;
        ELSE
            RETURN QUERY SELECT * FROM pgfs.node WHERE dtime > _dtime;
        END IF;
    ELSE
        IF _dtime IS NULL THEN
            RETURN QUERY SELECT * FROM pgfs.node WHERE ino = _ino;
        ELSE
            RETURN QUERY SELECT * FROM pgfs.node WHERE ino = _ino AND dtime > _dtime;
        END IF;
    END IF;
END $$ LANGUAGE plpgsql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.add_node CASCADE;
CREATE FUNCTION pgfs.add_node(
    _ino   BIGINT = default_ino(),
    _mode  BIGINT = default_mode(),
    _nlink BIGINT = default_nlink(),
    _uid   BIGINT = default_uid(),
    _gid   BIGINT = default_gid(),
    _itime TIMESTAMP = default_itime(),
    _dtime TIMESTAMP = default_dtime()
) RETURNS
    SETOF pgfs.
node AS $$
    INSERT INTO pgfs.node (ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime)
    VALUES (_ino, _mode, _nlink, _uid, _gid, 0, _itime, _itime, _itime, _itime, _itime, _dtime)
    RETURNING *;
$$ LANGUAGE sql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.set_mode CASCADE;
CREATE FUNCTION pgfs.set_mode(
    _ino BIGINT,
    _mode BIGINT,
    _ctime TIMESTAMP = now()
) RETURNS
    SETOF pgfs.node
AS $$
    UPDATE pgfs.node
    SET mode = _mode, ctime = _ctime, utime = _ctime
    WHERE ino = _ino
    RETURNING *;
$$ LANGUAGE sql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.remove_node CASCADE;
CREATE FUNCTION pgfs.remove_node(
    _ino BIGINT,
    _dtime TIMESTAMP = now()
) RETURNS
    SETOF pgfs.node
AS $$
    UPDATE pgfs.node
    SET dtime = _dtime
    WHERE ino = _ino AND dtime > _dtime
    RETURNING *;
$$ LANGUAGE sql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.purge_node CASCADE;
CREATE FUNCTION pgfs.purge_node(
    _ino BIGINT
) RETURNS
    SETOF pgfs.node
AS $$
    DELETE FROM pgfs.node
    WHERE ino = _ino
    RETURNING *
$$ LANGUAGE sql VOLATILE
;


-- table block is file content


DROP TABLE IF EXISTS pgfs.block CASCADE;
CREATE TABLE pgfs.block
(
    ino   BIGINT NOT NULL                              /* reference of inode number     */,
    first BIGINT NOT NULL DEFAULT pgfs.default_first() /* starting position byte from 0 */,
    last  BIGINT NOT NULL DEFAULT pgfs.default_size()  /* = first + octet_length(data)  */,
    data  BYTEA  NOT NULL DEFAULT pgfs.default_data()  /* block data                    */
)
;

ALTER TABLE pgfs.block ADD CONSTRAINT block_pk
    PRIMARY KEY (ino, first) INCLUDE (last)
;

ALTER TABLE pgfs.block ADD CONSTRAINT block_fk1
    FOREIGN KEY (ino) REFERENCES pgfs.node (ino) ON DELETE CASCADE
;

DROP FUNCTION IF EXISTS pgfs.write_block CASCADE;
CREATE FUNCTION pgfs.write_block(
    _ino BIGINT,
    _first BIGINT,
    _data bytea,
    _utime TIMESTAMP = now(),
    _dtime TIMESTAMP = now()
) RETURNS SETOF pgfs.block AS $$
DECLARE
    _last BIGINT;

    _pre_first BIGINT;
    _pre_last BIGINT;
    _pre_data BYTEA;

    _suf_first BIGINT;
    _suf_last BIGINT;
    _suf_data BYTEA;

    _row_count BIGINT;
BEGIN
    IF NOT EXISTS (
        SELECT a.ino
        FROM pgfs.node AS a
        WHERE a.ino = _ino
            AND (_dtime IS NULL OR a.dtime >= _dtime)
    ) THEN
        RAISE INFO 'write_block: ino % is not found.', _ino;
        RETURN;
    END IF;

    _last := _first + OCTET_LENGTH(_data);
    IF _last IS NULL OR _first >= _last OR _first < 0 THEN
        RAISE INFO 'write_block: data range is illegal.';
        RETURN;
    END IF;

    SELECT a.first AS pre_first,
        _first AS pre_last,
        SUBSTRING(a.data, 1, CAST(_first - a.first AS INT)) AS pre_data
    INTO _pre_first,
        _pre_last,
        _pre_data
    FROM pgfs.block AS a
    WHERE a.ino = _ino
        AND a.first <= _first
        AND a.last > _first;

    SELECT _last AS suf_first,
        a.last AS suf_last,
        SUBSTRING(a.data, CAST(_last - a.first + 1 AS INT)) AS suf_data
    INTO _suf_first,
        _suf_last,
        _suf_data
    FROM pgfs.block AS a
    WHERE a.ino = _ino
        AND a.first < _last
        AND a.last >= _last;

    IF _pre_first IS NOT NULL OR _suf_last IS NOT NULL THEN
        DELETE FROM pgfs.block AS a
        WHERE a.ino = _ino
            AND (_pre_first IS NULL OR a.first >= _pre_first)
            AND (_suf_last IS NULL OR a.last <= _suf_last);

        GET DIAGNOSTICS _row_count = ROW_COUNT;
        RAISE INFO $q$
            DELETE FROM pgfs.block AS a
            WHERE a.ino = %
                AND (% IS NULL OR a.first >= %)
                AND (% IS NULL OR a.last <= %)
            % rows
        $q$, _ino, _pre_first, _pre_first, _suf_last, _suf_last, _row_count;
    END IF;

    RETURN QUERY
    INSERT INTO pgfs.block AS a (
        ino,
        first,
        last,
        data
    )
    SELECT _ino AS ino,
        _pre_first AS first,
        _pre_last AS last,
        _pre_data AS data
    WHERE octet_length(_pre_data) > 0
    UNION ALL
    SELECT _ino AS ino,
        _first AS first,
        _last AS last,
        _data AS data
    UNION ALL
    SELECT _ino AS ino,
        _suf_first AS first,
        _suf_last AS last,
        _suf_data AS data
    WHERE octet_length(_suf_data) > 0
    RETURNING a.*;

    UPDATE pgfs.node AS a
    SET size = (
            SELECT MAX(h.last)
            FROM pgfs.block AS h
        ),
        ctime = _utime,
        mtime = _utime,
        utime = _utime
    WHERE ino = _ino;
END $$ LANGUAGE plpgsql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.get_block CASCADE;
CREATE FUNCTION pgfs.get_block(
    _ino BIGINT,
    _first BIGINT = 0,
    _size BIGINT = 16384,
    _dtime TIMESTAMP = now()
) RETURNS SETOF pgfs.block AS $$
    SELECT
        _ino as ino,
        CASE
            WHEN a.first <= _first THEN _first
            ELSE a.first
        END AS first,
        CASE
            WHEN a.last >= (_first + _size) THEN (_first + _size)
            ELSE a.last
        END AS last,
        CASE
            WHEN a.first = _first THEN a.data
            WHEN a.first < _first THEN SUBSTRING(a.data, CAST(_first - a.first + 1 AS INT))
            WHEN a.last = (_first + _size) THEN a.data
            WHEN a.last > (_first + _size) THEN SUBSTRING(a.data, 1, CAST((_first + _size) - a.first AS INT))
            ELSE a.data
        END AS data
    FROM pgfs.block AS a
    INNER JOIN pgfs.node b
    ON
        b.ino = a.ino
        AND b.dtime >= _dtime
    WHERE
        a.ino = _ino
        AND a.last > _first
        AND a.first <= (_first + _size)
    ORDER BY a.first
$$ LANGUAGE sql VOLATILE
;

DROP FUNCTION IF EXISTS pgfs.read_block CASCADE;
CREATE FUNCTION pgfs.read_block(
    _ino BIGINT,
    _first BIGINT = 0,
    _size BIGINT = 16384,
    _dtime TIMESTAMP = NOW()
) RETURNS BYTEA AS $$
DECLARE
    _row pgfs.BLOCK;
    _data BYTEA;
    _next BIGINT;
    _len0 INT;
    _len1 INT;
    _len2 INT;
    _len3 INT;
BEGIN
    _data := '';
    _next := _first;
    FOR _row IN SELECT * FROM pgfs.get_block(_ino, _first, _size, _dtime) LOOP
        RAISE INFO '% % %', _data, _next, _row;

        _len0 := _row.first - _next;
        IF _len0 > 0 THEN
            _data := _data || CAST(('\x' || REPEAT('00', _len0)) AS BYTEA);
            _next := _row.first;
        END IF;

        _len1 := COALESCE(OCTET_LENGTH(_row.data), 0);
        _len2 := CAST(_row.last - _row.first AS INT);
        _len3 := _len2 - _len1;
        _next := _row.last;
        IF _len3 > 0 THEN
            _data := _data || _row.data || CAST(('\x' || REPEAT('00', _len3)) AS BYTEA);
            CONTINUE;
        END IF;

        _data := _data || _row.data;
    END LOOP;
    RETURN _data;
END
$$ LANGUAGE plpgsql VOLATILE
;

-- table file is directory entry


CREATE TABLE pgfs.file
(
    fno  BIGINT NOT NULL,
    pno  BIGINT NOT NULL,
    ino  BIGINT NOT NULL,
    path TEXT   NOT NULL
)
;

ALTER TABLE pgfs.file
ADD CONSTRAINT file_pk
PRIMARY KEY (fno)
;

ALTER TABLE pgfs.file
    ADD CONSTRAINT file_fk1
        FOREIGN KEY (pno)
            REFERENCES pgfs.file (fno)
;

ALTER TABLE pgfs.file
    ADD CONSTRAINT file_fk2
        FOREIGN KEY (ino)
            REFERENCES pgfs.node (ino)
;

CREATE UNIQUE INDEX file_uk1
    ON pgfs.file (path)
;

-- -- dummy node
-- INSERT INTO pgfs.node (
--     ino,
--     mode,
--     nlink,
--     uid,
--     gid,
--     size,
--     atime,
--     ctime,
--     mtime
-- )
-- VALUES (
--     0,
--     0,
--     0,
--     0,
--     0,
--     0,
--     NOW(),
--     NOW(),
--     NOW()
-- )
-- ;

-- -- dummy block
-- INSERT INTO pgfs.block (
--     ino,
--     bno,
--     size,
--     data
-- )
-- VALUES (
--     0,
--     0,
--     0,
--     ''
-- )
-- ;

-- -- dummy file
-- INSERT INTO pgfs.file (
--     fno,
--     pno,
--     ino,
--     path
-- )
-- VALUES (
--     0,
--     0,
--     0,
--     E'/1'
-- )
-- ;

/*

root node

The root node's ino is 0.
The initial setting mode is registered as 0,
and the application updates it to the correct value `S_IFDIR | 0o777`
when the application starts for the first time.

*/

SELECT pgfs.add_node(_ino := 0);

-- root file

INSERT INTO pgfs.file (
    fno,
    pno,
    ino,
    path
)
VALUES (
    0,
    0,
    0,
    ''
)
;
