package pgfs

import (
	"golang.org/x/exp/maps"
)

type pgXAttr struct {
	xattr   map[string][]byte
	changed bool
}

func newXAttr() *pgXAttr {
	a := &pgXAttr{
		xattr:   map[string][]byte{},
		changed: true,
	}
	return a
}

func (a *pgXAttr) IsValid() bool {
	return a != nil && a.xattr != nil
}

func (a *pgXAttr) IsChanged() bool {
	return a.changed
}
func (a *pgXAttr) SetChanged() {
	a.changed = true
}
func (a *pgXAttr) AcceptChanges() {
	a.changed = false
}

func (a *pgXAttr) Keys() []string {
	return maps.Keys(a.xattr)
}

func (a *pgXAttr) Put(key string, value []byte) {
	a.changed = true
	a.xattr[key] = value
}

func (a *pgXAttr) Remove(key string) {
	n := len(a.xattr)
	delete(a.xattr, key)
	if n != len(a.xattr) {
		a.changed = true
	}
}

func (a *pgXAttr) Get(key string) []byte {
	return a.xattr[key]
}
