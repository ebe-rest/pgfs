package pgfs

import (
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"
)

type pgColumn struct {
	or    int                   // or is order of columns
	nm    string                // nm is name of the column
	ty    string                // ty is type of column
	value func(st any) (db any) // value get from st
	alloc func() (db any)       // alloc scan target
	scan  func(st any, db any)  // scan set to st
}

type pgColumns map[string]*pgColumn

var pgNodeColumns = pgColumns{
	"ino": &pgColumn{
		or:    1,
		nm:    "ino",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Ino()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetIno(pgInt8ToUint64(*db.(*pgtype.Int8))) },
	},
	"mode": &pgColumn{
		or:    2,
		nm:    "mode",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Mode()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetMode(pgInt8ToUint32(*db.(*pgtype.Int8))) },
	},
	"nlink": &pgColumn{
		or:    3,
		nm:    "nlink",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Nlink()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetNlink(pgInt8ToUint32(*db.(*pgtype.Int8))) },
	},
	"uid": &pgColumn{
		or:    4,
		nm:    "uid",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Uid()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetUid(pgInt8ToUint32(*db.(*pgtype.Int8))) },
	},
	"gid": &pgColumn{
		or:    5,
		nm:    "gid",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Gid()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetGid(pgInt8ToUint32(*db.(*pgtype.Int8))) },
	},
	"size": &pgColumn{
		or:    6,
		nm:    "size",
		ty:    "BIGINT",
		value: func(st any) any { return integerToPgInt8(st.(*pgNode).Size()) },
		alloc: func() any { return &pgtype.Int8{} },
		scan:  func(st any, db any) { st.(*pgNode).SetSize(pgInt8ToUint64(*db.(*pgtype.Int8))) },
	},
	"atime": &pgColumn{
		or:    7,
		nm:    "atime",
		ty:    "TIMESTAMP",
		value: func(st any) any { return timeToPgTimestamp(st.(*pgNode).AccessTime()) },
		alloc: func() any { return &pgtype.Timestamp{} },
		scan:  func(st any, db any) { st.(*pgNode).SetAccessTime(pgTimestampToTime(*db.(*pgtype.Timestamp))) },
	},
	"ctime": &pgColumn{
		or:    8,
		nm:    "ctime",
		ty:    "TIMESTAMP",
		value: func(st any) any { return timeToPgTimestamp(st.(*pgNode).ChangeTime()) },
		alloc: func() any { return &pgtype.Timestamp{} },
		scan:  func(st any, db any) { st.(*pgNode).SetChangeTime(pgTimestampToTime(*db.(*pgtype.Timestamp))) },
	},
	"mtime": &pgColumn{
		or:    9,
		nm:    "mtime",
		ty:    "TIMESTAMP",
		value: func(st any) any { return timeToPgTimestamp(st.(*pgNode).ModTime()) },
		alloc: func() any { return &pgtype.Timestamp{} },
		scan:  func(st any, db any) { st.(*pgNode).SetModTime(pgTimestampToTime(*db.(*pgtype.Timestamp))) },
	},
}

func (a pgColumns) Get(name string) *pgColumn {
	c := a[name]
	if c == nil {
		return a.Default(name)
	}
	return c
}

func (a pgColumns) Default(name string) *pgColumn {
	return &pgColumn{
		or:    0,
		nm:    name,
		ty:    "TIMESTAMP",
		value: func(st any) any { return timeToPgTimestamp(st.(*pgNode).ModTime()) },
		alloc: func() any { return &pgtype.Timestamp{} },
		scan:  func(st any, db any) { st.(*pgNode).SetModTime(pgTimestampToTime(*db.(*pgtype.Timestamp))) },
	}
}

func (a pgNodes) ToRc() map[string][]any {
	rc := map[string][]any{}
	for _, c := range pgNodeColumns {
		rc[c.nm] = make([]any, len(a))
		for i, node := range a {
			rc[c.nm][i] = c.value(node)
		}
	}
	return rc
}

func (a *pgNode) ScanRow(rows pgx.Rows) error {
	desc := rows.FieldDescriptions()
	dest := make([]any, len(desc))
	destMap := make(map[string]any, len(desc))
	for i, d := range desc {
		name := d.Name
		value := pgNodeColumns[name].alloc()
		dest[i] = value
		destMap[name] = value
	}

	err := rows.Scan(dest...)
	if err != nil {
		return err
	}

	for name, value := range destMap {
		pgNodeColumns[name].scan(a, value)
	}

	return nil
}
