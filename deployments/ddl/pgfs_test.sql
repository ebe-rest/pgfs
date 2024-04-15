/*

test for pgfs

*/

DO LANGUAGE plpgsql $$
DECLARE
    _int8 BIGINT;
    _timestamp TIMESTAMP;
    _bytea BYTEA;
    _ino BIGINT;
    _mode BIGINT;
    _nlink BIGINT;
    _uid BIGINT;
    _gid BIGINT;
    _size BIGINT;
    _atime TIMESTAMP;
    _ctime TIMESTAMP;
    _mtime TIMESTAMP;
    _itime TIMESTAMP;
    _utime TIMESTAMP;
    _dtime TIMESTAMP;
    _first BIGINT;
    _last BIGINT;
    _data BYTEA;
BEGIN

    /* test basic functions */

    _int8 := pgfs.rand_int8(); IF _int8 IS NULL THEN RAISE 'rand_int8() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_ino(); IF _int8 IS NULL THEN RAISE 'default_ino() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_mode(); IF _int8 IS NULL THEN RAISE 'default_mode() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_nlink(); IF _int8 IS NULL THEN RAISE 'default_nlink() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_uid(); IF _int8 IS NULL THEN RAISE 'default_uid() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_gid(); IF _int8 IS NULL THEN RAISE 'default_gid() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_size(); IF _int8 IS NULL THEN RAISE 'default_size() is illegal - %', _int8; END IF;
    _timestamp := pgfs.default_atime(); IF _timestamp IS NULL THEN RAISE 'default_atime() is illegal - %', _timestamp; END IF;
    _timestamp := pgfs.default_ctime(); IF _timestamp IS NULL THEN RAISE 'default_ctime() is illegal - %', _timestamp; END IF;
    _timestamp := pgfs.default_mtime(); IF _timestamp IS NULL THEN RAISE 'default_mtime() is illegal - %', _timestamp; END IF;
    _timestamp := pgfs.default_itime(); IF _timestamp IS NULL THEN RAISE 'default_itime() is illegal - %', _timestamp; END IF;
    _timestamp := pgfs.default_utime(); IF _timestamp IS NULL THEN RAISE 'default_utime() is illegal - %', _timestamp; END IF;
    _timestamp := pgfs.default_dtime(); IF _timestamp IS NULL THEN RAISE 'default_dtime() is illegal - %', _timestamp; END IF;
    _int8 := pgfs.default_bno(); IF _int8 IS NULL THEN RAISE 'default_bno() is illegal - %', _int8; END IF;
    _int8 := pgfs.default_first(); IF _int8 IS NULL THEN RAISE 'default_first() is illegal - %', _int8; END IF;
    _bytea := pgfs.default_data(); IF _bytea IS NULL THEN RAISE 'default_data() is illegal - %', _bytea; END IF;

    /* test node functions */

    _int8 := pgfs.new_ino(); IF _int8 IS NULL OR _int8 = 0 THEN RAISE 'new_ino() must be returns not null and not 0.'; END IF;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.get_node(_ino := 0);
    IF _ino IS NULL OR _ino <> 0 THEN RAISE 'get_node().ino is illegal - %', _ino; END IF;
    IF _mode IS NULL THEN RAISE 'get_node().mode is illegal - %', _mode; END IF;
    IF _nlink IS NULL THEN RAISE 'get_node().nlink is illegal - %', _nlink; END IF;
    IF _uid IS NULL THEN RAISE 'get_node().uid is illegal - %', _uid; END IF;
    IF _gid IS NULL THEN RAISE 'get_node().gid is illegal - %', _gid; END IF;
    IF _size IS NULL THEN RAISE 'get_node().size is illegal - %', _size; END IF;
    IF _atime IS NULL THEN RAISE 'get_node().size is illegal - %', _atime; END IF;
    IF _ctime IS NULL THEN RAISE 'get_node().size is illegal - %', _ctime; END IF;
    IF _mtime IS NULL THEN RAISE 'get_node().size is illegal - %', _mtime; END IF;
    IF _itime IS NULL THEN RAISE 'get_node().size is illegal - %', _itime; END IF;
    IF _utime IS NULL THEN RAISE 'get_node().size is illegal - %', _utime; END IF;
    IF _dtime IS NULL OR _dtime <> 'infinity' THEN RAISE 'get_node().dtime is illegal - %', _dtime; END IF;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.add_node();
    IF _ino IS NULL OR _ino = 0 THEN RAISE 'add_node().ino is illegal - %', _ino; END IF;
    IF _mode IS NULL THEN RAISE 'add_node().mode is illegal - %', _mode; END IF;
    IF _nlink IS NULL THEN RAISE 'add_node().nlink is illegal - %', _nlink; END IF;
    IF _uid IS NULL THEN RAISE 'add_node().uid is illegal - %', _uid; END IF;
    IF _gid IS NULL THEN RAISE 'add_node().gid is illegal - %', _gid; END IF;
    IF _size IS NULL THEN RAISE 'add_node().size is illegal - %', _size; END IF;
    IF _atime IS NULL THEN RAISE 'add_node().size is illegal - %', _atime; END IF;
    IF _ctime IS NULL THEN RAISE 'add_node().size is illegal - %', _ctime; END IF;
    IF _mtime IS NULL THEN RAISE 'add_node().size is illegal - %', _mtime; END IF;
    IF _itime IS NULL THEN RAISE 'add_node().size is illegal - %', _itime; END IF;
    IF _utime IS NULL THEN RAISE 'add_node().size is illegal - %', _utime; END IF;
    IF _dtime IS NULL OR _dtime <> 'infinity' THEN RAISE 'add_node().dtime is illegal - %', _dtime; END IF;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.set_mode(_ino, 123);
    IF _ino IS NULL OR _ino = 0 THEN RAISE 'set_mode().ino is illegal - %', _ino; END IF;
    IF _mode IS NULL OR _mode <> 123 THEN RAISE 'set_mode().mode is illegal - %', _mode; END IF;
    IF _nlink IS NULL THEN RAISE 'set_mode().nlink is illegal - %', _nlink; END IF;
    IF _uid IS NULL THEN RAISE 'set_mode().uid is illegal - %', _uid; END IF;
    IF _gid IS NULL THEN RAISE 'set_mode().gid is illegal - %', _gid; END IF;
    IF _size IS NULL THEN RAISE 'set_mode().size is illegal - %', _size; END IF;
    IF _atime IS NULL THEN RAISE 'set_mode().size is illegal - %', _atime; END IF;
    IF _ctime IS NULL THEN RAISE 'set_mode().size is illegal - %', _ctime; END IF;
    IF _mtime IS NULL THEN RAISE 'set_mode().size is illegal - %', _mtime; END IF;
    IF _itime IS NULL THEN RAISE 'set_mode().size is illegal - %', _itime; END IF;
    IF _utime IS NULL THEN RAISE 'set_mode().size is illegal - %', _utime; END IF;
    IF _dtime IS NULL OR _dtime <> 'infinity' THEN RAISE 'set_mode().dtime is illegal - %', _dtime; END IF;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.remove_node(_ino);
    IF _ino IS NULL OR _ino = 0 THEN RAISE 'remove_node().ino is illegal - %', _ino; END IF;
    IF _mode IS NULL THEN RAISE 'remove_node().mode is illegal - %', _mode; END IF;
    IF _nlink IS NULL THEN RAISE 'remove_node().nlink is illegal - %', _nlink; END IF;
    IF _uid IS NULL THEN RAISE 'remove_node().uid is illegal - %', _uid; END IF;
    IF _gid IS NULL THEN RAISE 'remove_node().gid is illegal - %', _gid; END IF;
    IF _size IS NULL THEN RAISE 'remove_node().size is illegal - %', _size; END IF;
    IF _atime IS NULL THEN RAISE 'remove_node().size is illegal - %', _atime; END IF;
    IF _ctime IS NULL THEN RAISE 'remove_node().size is illegal - %', _ctime; END IF;
    IF _mtime IS NULL THEN RAISE 'remove_node().size is illegal - %', _mtime; END IF;
    IF _itime IS NULL THEN RAISE 'remove_node().size is illegal - %', _itime; END IF;
    IF _utime IS NULL THEN RAISE 'remove_node().size is illegal - %', _utime; END IF;
    IF _dtime IS NULL OR _dtime = 'infinity' THEN RAISE 'remove_node().dtime is illegal - %', _dtime; END IF;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.purge_node(_ino);
    IF _ino IS NULL OR _ino = 0 THEN RAISE 'purge_node().ino is illegal - %', _ino; END IF;
    IF _mode IS NULL THEN RAISE 'purge_node().mode is illegal - %', _mode; END IF;
    IF _nlink IS NULL THEN RAISE 'purge_node().nlink is illegal - %', _nlink; END IF;
    IF _uid IS NULL THEN RAISE 'purge_node().uid is illegal - %', _uid; END IF;
    IF _gid IS NULL THEN RAISE 'purge_node().gid is illegal - %', _gid; END IF;
    IF _size IS NULL THEN RAISE 'purge_node().size is illegal - %', _size; END IF;
    IF _atime IS NULL THEN RAISE 'purge_node().size is illegal - %', _atime; END IF;
    IF _ctime IS NULL THEN RAISE 'purge_node().size is illegal - %', _ctime; END IF;
    IF _mtime IS NULL THEN RAISE 'purge_node().size is illegal - %', _mtime; END IF;
    IF _itime IS NULL THEN RAISE 'purge_node().size is illegal - %', _itime; END IF;
    IF _utime IS NULL THEN RAISE 'purge_node().size is illegal - %', _utime; END IF;
    IF _dtime IS NULL OR _dtime = 'infinity' THEN RAISE 'purge_node().dtime is illegal - %', _dtime; END IF;

    /* test block functions */

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.add_node();

    SELECT ino, first, last, data
    INTO _ino, _first, _last, _data
    FROM pgfs.write_block(_ino, 0, CAST(('\x' || REPEAT('00', 1000)) AS BYTEA));

    SELECT ino, first, last, data
    INTO _ino, _first, _last, _data
    FROM pgfs.write_block(_ino, 1000, CAST(('\x' || REPEAT('01', 1000)) AS BYTEA)) ;

    SELECT ino, first, last, data
    INTO _ino, _first, _last, _data
    FROM pgfs.write_block(_ino, 2000, CAST(('\x' || REPEAT('02', 1000)) AS BYTEA)) ;

    SELECT ino, mode, nlink, uid, gid, size, atime, ctime, mtime, itime, utime, dtime
    INTO _ino, _mode, _nlink, _uid, _gid, _size, _atime, _ctime, _mtime, _itime, _utime, _dtime
    FROM pgfs.purge_node(_ino);
END
$$
;
SELECT * FROM pgfs.node ORDER BY ino
;
SELECT * FROM pgfs.block ORDER BY ino, first
;

/*
INSERT INTO pgfs.block (ino, first, last, data) VALUES (100, 10, 15, '|aaa|') ;
INSERT INTO pgfs.block (ino, first, last, data) VALUES (100, 15, 20, '|bbb|') ;
--insert into pgfs.block (ino, first, last, data) values (100, 20, 25, '|ccc|');
INSERT INTO pgfs.block (ino, first, last, data) VALUES (100, 25, 30, '|ddd|') ;
INSERT INTO pgfs.block (ino, first, last, data) VALUES (100, 30, 35, '|eee|') ;
SELECT * FROM pgfs.block WHERE ino = 100 ORDER BY first ;

with a as (
    select a.first as pre_first,
        0 as pre_last,
        substring(a.data, 1, cast(0 - a.first as int)) as pre_data
    from pgfs.block as a
    where a.ino = 100
        and a.first <= 0
        and a.last > 0
), b as (
    select b.first as suf_first,
        b.last as suf_last,
        substring(b.data, cast(27 - b.first + 1 as int)) as suf_data
    from pgfs.block as b
    where b.ino = 100
        and b.first < 27
        and b.last >= 27
), c as (
    delete from pgfs.block AS c
    using a
    cross join b
    where c.ino = 100
        and (a.pre_first is null or c.first >= a.pre_first)
        and (b.suf_last is null or c.last <= b.suf_last)
    RETURNING
        c.*
), d as (
    insert into pgfs.block AS d
    (ino, first, last, data)
    select
        100 as ino,
        a.pre_first as first,
        a.pre_last as last,
        a.pre_data as data
    from a
    where a.pre_first is not null
    union all
    select
        100 as ino,
        0 as first,
        27 as last,
        cast('|zzzzzzzz|' as bytea) as data
    union all
    select
        100 as ino,
        27 as first,
        b.suf_last as last,
        b.suf_data as data
    from b
    where b.suf_last is not null
    RETURNING
        d.*
), e as (
    UPDATE pgfs.node as e
    set size = (select max(h.last) from pgfs.block as h),
        ctime = now(),
        mtime = now(),
        utime = now()
    where ino = 100
)
select * from d
order by first
;

SELECT * FROM pgfs.write_block(100, 0, '|zzzzzzzz|') ;
SELECT * FROM pgfs.write_block(100, 0, '|zzzzzzzzzzz|') ;
SELECT * FROM pgfs.write_block(100, 0, '|zzzzzzzzzzzzz|') ;
SELECT * FROM pgfs.write_block(100, 16, '|zzzzzzzz|') ;
SELECT * FROM pgfs.write_block(100, 1000, '|zzzzzzzz|') ;
SELECT * FROM pgfs.write_block(100, 16, '|ee|') ;
SELECT * FROM pgfs.write_block(100, 10, '|aaaaaaaa|') ;
SELECT * FROM pgfs.write_block(100, 20, '|bbbbbbbb|') ;
SELECT * FROM pgfs.write_block(100, 5, '|cccccccc|') ;
SELECT * FROM pgfs.write_block(100, 15, '|eeeeeeee|') ;
SELECT * FROM pgfs.write_block(100, 4, 'zzzzzzzzzzzzzzzzzzzzzzzzzzzz') ;
SELECT * FROM pgfs.write_block(100, 1000, 'zzzzzzzzzzzzzzzzzzzzzzzzzzzz') ;
SELECT * FROM pgfs.write_block(100, 1000, 'zzzz') ;
SELECT * FROM pgfs.write_block(100, 5, 'zzzzzzzzzz') ;

SELECT * FROM pgfs.block WHERE ino = 100 ORDER BY first;
*/


/*
select string_agg(a.data, '') as data
from (
    select
        case
            when a.first = 12 then a.data
            when a.first < 12 then substring(a.data, cast(12 - a.first + 1 as int))
            when a.last = 27 then a.data
            when a.last > 27 then substring(a.data, 1, cast(27 - a.first as int))
            else a.data
        end as data
    from pgfs.block as a
    where a.ino = 100 and a.last > 12 and a.first <= 27
    order by a.first
) as a
;
*/


SELECT
    a.first,
    (a.last - a.first),
    SUBSTRING(a.data, 1, CAST(25 - a.first AS INT))
FROM
    pgfs.block AS a
WHERE
    a.ino = 100
    AND a.first <= 25
    AND a.last > 25
;


SELECT * FROM pgfs.get_block(100, 12, 28)
;

SELECT * FROM pgfs.read_block(100, 12, 28)
;

SELECT OCTET_LENGTH(('\x' || REPEAT('00', 10))::BYTEA)
;

SELECT OCTET_LENGTH('\x00'::BYTEA)
;
