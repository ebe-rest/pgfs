package pgfs

import (
	"sync"

	"github.com/hanwen/go-fuse/v2/fuse"
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"

	"pgfs/pkg/context"
	"pgfs/pkg/log"
	"pgfs/pkg/pg"
)

// pgNode
//
//   - implements nodefs.Node
//   - references nodefs.defaultNode
//   - delegate to pgAttr
//   - delegate to nodefs.Inode
//   - references nodefs.defaultFile
type pgNode struct {
	fs      *pgFs
	attr    *pgAttr
	xattr   *pgXAttr
	inode   *pgInode
	blocks  pgBlocks
	changed bool
	created bool
	mutex   sync.Mutex
}

type pgNodes []*pgNode

func newNode(fs *pgFs) *pgNode {
	a := &pgNode{
		fs:      fs,
		attr:    newAttr(),
		xattr:   newXAttr(),
		inode:   newInode(nil),
		changed: true,
		created: true,
	}
	return a
}

func (a *pgNode) IsValid() bool {
	return a != nil && a.fs.IsValid() && a.attr.IsValid() && a.xattr.IsValid() && a.inode != nil
}

func (a *pgNode) IsChanged() bool {
	return a.changed || a.attr.IsChanged() || a.xattr.IsChanged()
}
func (a *pgNode) SetChanged() {
	a.changed = true
}
func (a *pgNode) IsCreated() bool {
	return a.created
}
func (a *pgNode) SetCreated() {
	a.created = true
}
func (a *pgNode) AcceptChanges() {
	a.changed = false
	a.created = false
	a.attr.AcceptChanges()
	a.xattr.AcceptChanges()
}

func (a *pgNode) String() string {
	return log.Sprintf("node{%d}", a.Ino())
}

// ---

func (a *pgNode) Fs() *pgFs {
	return a.fs
}

func (a *pgNode) Pg() pg.Pg {
	return a.fs.Pg()
}

func (a *pgNode) Begin(ctx context.Context) (pg.Tx, error) {
	return a.fs.Begin(ctx)
}

func (a *pgNode) Attr() *fuse.Attr {
	return a.attr.Attr()
}

// func (a *pgNode) SetAttr(attr *fuse.Attr) {
// 	a.attr.SetAttr(attr)
// }

// Inode implements nodefs.Node.
// called by the nodefs.FileSystemConnector.
func (a *pgNode) Inode() *nodefs.Inode {
	return a.inode.Inode()
}

// SetInode implements nodefs.Node
// called by the nodefs.FileSystemConnector.
func (a *pgNode) SetInode(inode *nodefs.Inode) {
	a.inode.SetInode(inode)
}
