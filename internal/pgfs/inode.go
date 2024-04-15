package pgfs

import (
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"
)

// nodefs.Inode methods
type nodefsInodeMethods interface {
	String() string
	AnyFile() (file nodefs.File)
	Children() (out map[string]*nodefs.Inode)
	Parent() (parent *nodefs.Inode, name string)
	FsChildren() (out map[string]*nodefs.Inode)
	Node() nodefs.Node
	Files(mask uint32) (files []nodefs.WithFlags)
	IsDir() bool
	NewChild(name string, isDir bool, fsi nodefs.Node) *nodefs.Inode
	GetChild(name string) (child *nodefs.Inode)
	AddChild(name string, child *nodefs.Inode)
	RmChild(name string) (ch *nodefs.Inode)
}

type pgInode struct {
	inode *nodefs.Inode
}

var _ nodefsInodeMethods = (*pgInode)(nil)

func newInode(inode *nodefs.Inode) *pgInode {
	a := &pgInode{inode: inode}
	return a
}

func (a *pgInode) IsValid() bool {
	return a != nil && a.inode != nil
}

func (a *pgInode) String() string {
	return a.inode.String()
}

func (a *pgInode) Inode() *nodefs.Inode {
	return a.inode
}
func (a *pgInode) SetInode(inode *nodefs.Inode) {
	a.inode = inode
}

func (a *pgInode) AnyFile() (file nodefs.File) {
	return a.inode.AnyFile()
}

func (a *pgInode) Children() (out map[string]*nodefs.Inode) {
	return a.inode.Children()
}

func (a *pgInode) Parent() (parent *nodefs.Inode, name string) {
	return a.inode.Parent()
}

func (a *pgInode) FsChildren() (out map[string]*nodefs.Inode) {
	return a.inode.FsChildren()
}

func (a *pgInode) Node() nodefs.Node {
	return a.inode.Node()
}

func (a *pgInode) Files(mask uint32) (files []nodefs.WithFlags) {
	return a.inode.Files(mask)
}

func (a *pgInode) IsDir() bool {
	return a.inode.IsDir()
}

func (a *pgInode) NewChild(name string, isDir bool, fsi nodefs.Node) *nodefs.Inode {
	return a.inode.NewChild(name, isDir, fsi)
}

func (a *pgInode) GetChild(name string) (child *nodefs.Inode) {
	return a.inode.GetChild(name)
}

func (a *pgInode) AddChild(name string, child *nodefs.Inode) {
	a.inode.AddChild(name, child)
}

func (a *pgInode) RmChild(name string) (ch *nodefs.Inode) {
	return a.inode.RmChild(name)
}
