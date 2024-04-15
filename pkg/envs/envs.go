package envs

import (
	"os"
	"strings"
)

type Envs map[string]string

func Parse() Envs {
	envs := Envs{}
	for _, e := range os.Environ() {
		kv := strings.SplitN(e, "=", 2)
		if len(kv) != 2 {
			continue
		}
		envs[kv[0]] = kv[1]
	}
	return envs
}
