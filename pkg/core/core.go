package core

import (
	"fmt"
	"reflect"
	"time"

	"github.com/google/uuid"
)

var ZeroUuid = uuid.Nil
var ZeroTime = time.Date(1970, 1, 1, 0, 0, 0, 0, time.Local)

func ToString(a any) string {
	switch b := a.(type) {
	case nil:
		return "<nil>"
	case string:
		return b
	case []byte:
		return string(b)
	case []rune:
		return string(b)
	case fmt.Stringer:
		return b.String()
	default:
		return reflect.ValueOf(b).String()
	}
}
