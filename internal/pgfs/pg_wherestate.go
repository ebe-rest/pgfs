package pgfs

import (
	"fmt"
	"strconv"
)

func buildWhereStates(
	whereStates []any,
	placeHolderStart int,
	argsAppendTo []any,
) (
	conditions []string,
	args []any,
	placeHolderNext int,
	err error,
) {
	next := func(i *int) (any, bool) {
		if *i >= len(whereStates) {
			return nil, false
		}
		s := whereStates[*i]
		*i++
		return s, true
	}

	args = argsAppendTo
	placeHolderNext = placeHolderStart
	for i := 0; ; {
		a, ok := next(&i)
		if !ok {
			return conditions, args, placeHolderNext, nil
		}

		state, ok := a.(string)
		if !ok {
			return nil, nil, 0, fmt.Errorf("argument args[%d] expected string bat %T", i, whereStates[i])
		}

		for o := 0; o < len(state); o++ {
			if state[o] != '$' {
				continue
			}

			num := strconv.Itoa(placeHolderNext)
			state = state[:o+1] + num + state[o+1:]
			placeHolderNext++
			o += len(num)

			a, ok = next(&i)
			if !ok {
				return nil, nil, 0, fmt.Errorf("argument args[%d] is nothing", i)
			}

			a, err = toPgValue(a)
			if err == nil {
				return nil, nil, 0, fmt.Errorf("argument args[%d] %w", i, err)
			}
			args = append(args, a)
		}
	}
}
