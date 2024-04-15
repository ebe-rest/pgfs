package pgfs

import (
	"crypto/rand"
	"errors"
	"fmt"
	"math"
	"math/big"
	"syscall"
	"time"

	"github.com/hanwen/go-fuse/v2/fuse"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"

	"pgfs/pkg/core"
	"pgfs/pkg/log"
)

func integerToPgInt8[T ~int | ~int8 | ~int16 | ~int32 | ~int64 | ~uint | ~uint8 | ~uint16 | ~uint32 | ~uint64 | ~uintptr](v T) pgtype.Int8 {
	return pgtype.Int8{Int64: int64(v), Valid: true}
}

func integerPtrToPgInt8[T ~int | ~int8 | ~int16 | ~int32 | ~int64 | ~uint | ~uint8 | ~uint16 | ~uint32 | ~uint64](v *T) pgtype.Int8 {
	if v == nil {
		return pgtype.Int8{Valid: false}
	}
	return integerToPgInt8(*v)
}

func pgInt8ToUint32(v pgtype.Int8) uint32 {
	if !v.Valid {
		return 0
	}
	return uint32(v.Int64)
}

func pgInt8ToUint64(v pgtype.Int8) uint64 {
	if !v.Valid {
		return 0
	}
	return uint64(v.Int64)
}

// ---

func timeToPgTimestamp(v time.Time) pgtype.Timestamp {
	return pgtype.Timestamp{Time: v, InfinityModifier: pgtype.Finite, Valid: true}
}

func pgTimestampToTime(value pgtype.Timestamp) time.Time {
	if !value.Valid || value.InfinityModifier != pgtype.Finite {
		return time.Date(1970, 1, 1, 0, 0, 0, 0, time.Local)
	}
	return value.Time.In(time.Local)
}

// ---

func pgTextToString(value pgtype.Text) string {
	if !value.Valid {
		return ""
	}
	return value.String
}

// ---

func toPgValue(value any) (any, error) {
	switch v := value.(type) {
	case nil:
		return nil, nil

	case int:
		return integerToPgInt8(v), nil
	case int8:
		return integerToPgInt8(v), nil
	case int16:
		return integerToPgInt8(v), nil
	case int32:
		return integerToPgInt8(v), nil
	case int64:
		return integerToPgInt8(v), nil
	case uint:
		return integerToPgInt8(v), nil
	case uint8:
		return integerToPgInt8(v), nil
	case uint16:
		return integerToPgInt8(v), nil
	case uint32:
		return integerToPgInt8(v), nil
	case uint64:
		return integerToPgInt8(v), nil

	case string:
		return stringToPgText(v), nil

	case *int:
		return integerPtrToPgInt8(v), nil
	case *int8:
		return integerPtrToPgInt8(v), nil
	case *int16:
		return integerPtrToPgInt8(v), nil
	case *int32:
		return integerPtrToPgInt8(v), nil
	case *int64:
		return integerPtrToPgInt8(v), nil
	case *uint:
		return integerPtrToPgInt8(v), nil
	case *uint8:
		return integerPtrToPgInt8(v), nil
	case *uint16:
		return integerPtrToPgInt8(v), nil
	case *uint32:
		return integerPtrToPgInt8(v), nil
	case *uint64:
		return integerPtrToPgInt8(v), nil

	case *string:
		return stringPtrToPgText(v), nil

		// case [16]byte:
		// 	return pg.UuidToPgUuid(v), nil
		// case uuid.UUID:
		// 	return pg.UuidToPgUuid(v), nil
		// case pgtype.UUID:
		// 	return v, nil
		// case *[16]byte:
		// 	return pg.UuidPtrToPgUuid(v), nil
		// case *uuid.UUID:
		// 	return pg.UuidPtrToPgUuid(v), nil
		// case *pgtype.UUID:
		// 	return pg.PgUuidPtrToUuid(v), nil
		//
		// case []rune:
		// 	return pg.StringToPgText(v), nil
		// case pgtype.Text:
		// 	return v, nil
		// case *[]rune:
		// 	return pg.StringPtrToPgText(v), nil
		// case *pgtype.Text:
		// 	return pg.PgTextPtrToPgText(v), nil
		//
		// case time.Time:
		// 	return pg.TimeToPgTimestamp(v), nil
		// case pgtype.Timestamp:
		// 	return v, nil
		// case *time.Time:
		// 	return pg.TimePtrToPgTimestamp(v), nil
		// case *pgtype.Timestamp:
		// 	return pg.PgTimestampPtrToPgTimestamp(v), nil
		//
		// case big.Int:
		// 	return pg.BigIntValToPg(v), nil
		// case pgtype.Int8:
		// 	return v, nil
		// case pgtype.Numeric:
		// 	return pg.PgNumericToPg(v), nil
		// case *big.Int:
		// 	return pg.BigIntToPg(v), nil
		// case *pgtype.Int8:
		// 	return pg.PgInt8PtrToPgInt8(v), nil
		// case *pgtype.Numeric:
		// 	return pg.PgNumericPtrToPg(v), nil
		// case **big.Int:
		// 	return pg.BigIntPtrToPg(v), nil
	}

	return nil, fmt.Errorf("unknown type: %T", value)
}

func uint64ToPg[T ~uint64](v T) any {
	if v > math.MaxInt64 {
		w := new(big.Int).SetUint64(uint64(v))
		return pgtype.Numeric{Int: w, Exp: 0, NaN: false, InfinityModifier: 0, Valid: true}
	}
	return integerToPgInt8(int64(v))
}

func uint64PtrToPg[T ~uint64](v *T) any {
	if v == nil {
		return pgtype.Int8{Valid: false}
	}
	return uint64ToPg(*v)
}

func stringToPgText[V ~string | []byte | []rune](v V) pgtype.Text {
	return pgtype.Text{String: string(v), Valid: true}
}

func stringPtrToPgText[V ~string | []byte | []rune](v *V) pgtype.Text {
	if v == nil {
		return pgtype.Text{Valid: false}
	}
	return pgtype.Text{String: string(*v), Valid: true}
}

func timePtrToPgTimestamp(v *time.Time) pgtype.Timestamp {
	if v == nil {
		return pgtype.Timestamp{Valid: false}
	}
	return pgtype.Timestamp{Time: *v, InfinityModifier: pgtype.Finite, Valid: true}
}

// ---

func newRandomInt64() int64 {
	n, _ := rand.Int(rand.Reader, new(big.Int).SetUint64(math.MaxUint64))
	return int64(n.Uint64())
}

func newRandomUint64() uint64 {
	n, _ := rand.Int(rand.Reader, new(big.Int).SetUint64(math.MaxUint64))
	return n.Uint64()
}

// ---

func errorToStatus(err error) fuse.Status {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		switch pgErr.Code {
		case "23505": // unique_violation
			return fuse.Status(syscall.EEXIST)
		}
	}

	log.Debug("unknown error:", err)
	return fuse.EIO
}

// ---

func toString(a any) string {
	switch b := a.(type) {
	case *fuse.Context:
		return log.Sprintf("caller: {pid: %d, uid: %d, gid: %d}", b.Pid, b.Uid, b.Gid)
	default:
		return core.ToString(a)
	}
}
