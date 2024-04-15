package pgfs

import (
	"path"
	"sync"

	"pgfs/pkg/log"
)

// pgFile
// implements nodefs.File
// reference to nodefs.defaultFile
// delegate to pgNode
type pgFile struct {
	node    *pgNode
	fno     uint64
	path    string
	changed bool
	created bool
	mutex   sync.Mutex
}

type pgFiles []*pgFile

func newFile(node *pgNode) *pgFile {
	a := &pgFile{
		node:    node,
		changed: true,
		created: true,
	}
	return a
}

func (a *pgFile) IsValid() bool {
	return a != nil && a.node.IsValid()
}

func (a *pgFile) String() string {
	return log.Sprintf("pgfile{%s}", a.Name())
}

func (a *pgFile) Path() string {
	return a.path
}
func (a *pgFile) SetPath(path string) {
	a.changed = true
	a.path = path
}
func (a *pgFile) Name() string {
	return path.Base(a.path)
}
func (a *pgFile) SetName(name string) {
	a.changed = true
	a.path = path.Join(path.Dir(a.path), name)
}
