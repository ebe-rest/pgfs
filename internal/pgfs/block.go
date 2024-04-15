package pgfs

import (
	"pgfs/pkg/log"
)

type pgBlock struct {
	data    []byte
	changed bool
	created bool
}

type pgBlocks []*pgBlock

func newBlock() *pgBlock {
	a := &pgBlock{
		changed: true,
		created: true,
	}
	return a
}

func (a *pgBlock) IsValid() bool {
	return a != nil
}

func (a *pgBlock) IsChanged() bool {
	return a.changed
}
func (a *pgBlock) SetChanged() {
	a.changed = true
}
func (a *pgBlock) IsCreated() bool {
	return a.created
}
func (a *pgBlock) SetCreated() {
	a.created = true
}
func (a *pgBlock) AcceptChanges() {
	a.changed = false
	a.created = false
}

func (a *pgBlock) String() string {
	return log.Sprintf("block{%dbytes}", len(a.data))
}

// ---
