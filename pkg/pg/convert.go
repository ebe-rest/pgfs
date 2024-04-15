package pg

// func BigIntPtrToPg(v **big.Int) any {
// 	if v == nil {
// 		return pgtype.Int8{Valid: false}
// 	}
// 	return BigIntToPg(*v)
// }
// func BigIntToPg(v *big.Int) any {
// 	if v == nil {
// 		return pgtype.Int8{Valid: false}
// 	}
// 	return BigIntValToPg(*v)
// }
// func BigIntValToPg(v big.Int) any {
// 	if !v.IsInt64() {
// 		return pgtype.Numeric{Int: &v, Exp: 0, NaN: false, InfinityModifier: 0, Valid: true}
// 	}
// 	return pgtype.Int8{Int64: v.Int64(), Valid: true}
// }
//
// func IntToPgPgNumeric[T ~int | ~int8 | ~int16 | ~int32 | ~int64 | ~uint | ~uint8 | ~uint16 | ~uint32](v T) pgtype.Numeric {
// 	return pgtype.Numeric{Int: new(big.Int).SetInt64(int64(v)), Exp: 0, NaN: false, InfinityModifier: pgtype.Finite, Valid: true}
// }
//
// func PgInt8PtrToPgInt8(v *pgtype.Int8) pgtype.Int8 {
// 	if v == nil {
// 		return pgtype.Int8{Valid: false}
// 	}
// 	return *v
// }
//
// func PgNumericPtrToPg(v *pgtype.Numeric) any {
// 	if v == nil {
// 		return pgtype.Numeric{Valid: false}
// 	}
// 	return PgNumericToPg(*v)
// }
// func PgNumericToPg(v pgtype.Numeric) any {
// 	w, err := v.Int64Value()
// 	if err != nil {
// 		return v
// 	}
// 	return w
// }
// func PgNumericToUint64(v pgtype.Numeric) uint64 {
// 	if !v.Valid || v.InfinityModifier != pgtype.Finite || v.NaN || v.Int == nil {
// 		return 0
// 	}
//
// 	if v.Exp == 0 {
// 		if v.Int.IsUint64() {
// 			return v.Int.Uint64()
// 		}
// 		return 0
// 	}
//
// 	big10 := big.NewInt(10)
// 	w := &big.Int{}
// 	w.Set(v.Int)
// 	if v.Exp > 0 {
// 		x := &big.Int{}
// 		x.Exp(big10, big.NewInt(int64(v.Exp)), nil)
// 		w.Mul(w, x)
// 		if w.IsUint64() {
// 			return w.Uint64()
// 		}
// 		return 0
// 	}
//
// 	big0 := big.NewInt(0)
// 	y := &big.Int{}
// 	y.Exp(big10, big.NewInt(int64(-v.Exp)), nil)
// 	z := &big.Int{}
// 	w.DivMod(w, y, z)
// 	if z.Cmp(big0) != 0 {
// 		return 0
// 	}
// 	if w.IsUint64() {
// 		return w.Uint64()
// 	}
// 	return 0
// }
//
// func pgTextPtrToPgText(v *pgtype.Text) pgtype.Text {
// 	if v == nil {
// 		return pgtype.Text{Valid: false}
// 	}
// 	return *v
// }
//
// func PgTimestampPtrToPgTimestamp(v *pgtype.Timestamp) pgtype.Timestamp {
// 	if v == nil {
// 		return pgtype.Timestamp{Valid: false}
// 	}
// 	return *v
// }
//
// func PgUuidPtrToUuid(v *pgtype.UUID) pgtype.UUID {
// 	if v == nil {
// 		return pgtype.UUID{Valid: false}
// 	}
// 	return *v
// }
// func PgUuidToUuid(v pgtype.UUID) uuid.UUID {
// 	if !v.Valid {
// 		return core.ZeroUuid
// 	}
// 	return v.Bytes
// }
// func PgUuidToUuidPtr(v pgtype.UUID) *uuid.UUID {
// 	if !v.Valid {
// 		return nil
// 	}
// 	return (*uuid.UUID)(&v.Bytes)
// }
//
// func UInt64ToPgPgNumeric(v uint64) pgtype.Numeric {
// 	return pgtype.Numeric{Int: new(big.Int).SetUint64(v), Exp: 0, NaN: false, InfinityModifier: pgtype.Finite, Valid: true}
// }
//
// func UuidPtrToPgUuid[V ~[16]byte](v *V) pgtype.UUID {
// 	if v == nil {
// 		return pgtype.UUID{Valid: false}
// 	}
// 	return pgtype.UUID{Bytes: *v, Valid: true}
// }
// func UuidToPgUuid[V ~[16]byte](v V) pgtype.UUID {
// 	return pgtype.UUID{Bytes: v, Valid: true}
// }
// func UuidToUuid[V ~[16]byte](v V) uuid.UUID {
// 	return uuid.UUID(v)
// }

// // fromPg convert from pgtype to any pointer type or nil interface
// func fromPg(value any) any {
// 	switch v := value.(type) {
// 	case nil:
// 		return nil
//
// 	case [16]byte:
// 		return (*uuid.UUID)(&v)
// 	case uuid.UUID:
// 		return &v
// 	case pgtype.UUID:
// 		if !v.Valid {
// 			return nil
// 		} else {
// 			return (*uuid.UUID)(&v.Bytes)
// 		}
// 	case *[16]byte:
// 		if v == nil {
// 			return nil
// 		} else {
// 			return (*uuid.UUID)(v)
// 		}
// 	case *uuid.UUID:
// 		if v == nil {
// 			return nil
// 		} else {
// 			return v
// 		}
// 	case *pgtype.UUID:
// 		if v == nil || !v.Valid {
// 			return nil
// 		} else {
// 			return *v
// 		}
//
// 	case string:
// 		return &v
// 	case pgtype.Text:
// 		if !v.Valid {
// 			return nil
// 		} else {
// 			return &v.String
// 		}
// 	case *string:
// 		if v == nil {
// 			return nil
// 		} else {
// 			return v
// 		}
// 	case *pgtype.Text:
// 		if v == nil || !v.Valid {
// 			return nil
// 		} else {
// 			return &v.String
// 		}
//
// 	case time.Time:
// 		return &v
// 	case pgtype.Timestamp:
// 		if !v.Valid || v.InfinityModifier != pgtype.Finite {
// 			return nil
// 		} else {
// 			return &v.Time
// 		}
// 	case *time.Time:
// 		if v == nil {
// 			return nil
// 		} else {
// 			return v
// 		}
// 	case *pgtype.Timestamp:
// 		if v == nil || !v.Valid || v.InfinityModifier != pgtype.Finite {
// 			return nil
// 		} else {
// 			return &v.Time
// 		}
//
// 	case big.Int:
// 		return &v
// 	case pgtype.Numeric:
// 		w, err := numericToBigInt(&v)
// 		if err != nil {
// 			return nil
// 		}
// 		return w
// 	case *big.Int:
// 		if v == nil {
// 			return nil
// 		} else {
// 			return v
// 		}
// 	case *pgtype.Numeric:
// 		w, err := numericToBigInt(v)
// 		if err != nil {
// 			return nil
// 		}
// 		return w
// 	case **big.Int:
// 		if v == nil || *v == nil {
// 			return nil
// 		} else {
// 			return *v
// 		}
//
// 	case int:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case int8:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case int16:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case int32:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case int64:
// 		return pgtype.Int8{Int64: v, Valid: true}
// 	case uint:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case uint8:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case uint16:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case uint32:
// 		return pgtype.Int8{Int64: int64(v), Valid: true}
// 	case pgtype.Int8:
// 		return v
// 	case *int:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *int8:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *int16:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *int32:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *int64:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: *v, Valid: true}
// 		}
// 	case *uint:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *uint8:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *uint16:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *uint32:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return pgtype.Int8{Int64: int64(*v), Valid: true}
// 		}
// 	case *pgtype.Int8:
// 		if v == nil {
// 			return pgtype.Int8{Valid: false}
// 		} else {
// 			return *v
// 		}
// 	}
//
// 	return fmt.Errorf("unknown type: %T", value)
// }
// func numericToBigInt(v *pgtype.Numeric) (*big.Int, error) {
// 	if v == nil || !v.Valid || v.NaN || v.InfinityModifier != pgtype.Finite {
// 		return nil, fmt.Errorf("cannot convert %v to integer", v)
// 	}
//
// 	if v.Exp == 0 {
// 		return v.Int, nil
// 	}
//
// 	var big10 = big.NewInt(10)
// 	num := &big.Int{}
// 	num.Set(v.Int)
// 	if v.Exp > 0 {
// 		mul := &big.Int{}
// 		mul.Exp(big10, big.NewInt(int64(v.Exp)), nil)
// 		num.Mul(num, mul)
// 		return num, nil
// 	}
//
// 	var big0 = big.NewInt(0)
// 	div := &big.Int{}
// 	div.Exp(big10, big.NewInt(int64(-v.Exp)), nil)
// 	remainder := &big.Int{}
// 	num.DivMod(num, div, remainder)
// 	if remainder.Cmp(big0) != 0 {
// 		return nil, fmt.Errorf("cannot convert %v to integer", v)
// 	}
//
// 	return num, nil
// }
// func toInt(value pgtype.Int8) int             { return int(value.Int64) }
// func toInt8(value pgtype.Int8) int8           { return int8(value.Int64) }
// func toInt16(value pgtype.Int8) int16         { return int16(value.Int64) }
// func toInt32(value pgtype.Int8) int32         { return int32(value.Int64) }
// func toInt64(value pgtype.Int8) int64         { return value.Int64 }
// func toUint(value pgtype.Int8) uint           { return uint(value.Int64) }
// func toUint8(value pgtype.Int8) uint8         { return uint8(value.Int64) }
// func toUint16(value pgtype.Int8) uint16       { return uint16(value.Int64) }
// func reinterpretCast[T, U any](u *U) *T {
// 	return (*T)(unsafe.Pointer(u))
// }
