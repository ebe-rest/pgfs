package pgfs

import (
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"
)

// delegate to nodefs.Inode
// references nodefs.defaultFile
//
// The inode field is nil until SetInode is called from nodefs.FileSystemConnector,
// so check if it is nil before delegating.

func (a *pgNode) AnyFile() (file nodefs.File) {
	if a.inode == nil {
		return nil
	}
	return a.inode.AnyFile()
}

func (a *pgNode) Children() (out map[string]*nodefs.Inode) {
	if a.inode == nil {
		return nil
	}
	return a.inode.Children()
}

func (a *pgNode) Parent() (parent *nodefs.Inode, name string) {
	if a.inode == nil {
		return nil, ""
	}
	return a.inode.Parent()
}

func (a *pgNode) FsChildren() (out map[string]*nodefs.Inode) {
	if a.inode == nil {
		return nil
	}
	return a.inode.FsChildren()
}

func (a *pgNode) Files(mask uint32) (files []nodefs.WithFlags) {
	if a.inode == nil {
		return nil
	}
	return a.inode.Files(mask)
}

func (a *pgNode) NewChild(name string, isDir bool, fsi nodefs.Node) *nodefs.Inode {
	if a.inode == nil {
		return nil
	}
	return a.inode.NewChild(name, isDir, fsi)
}

func (a *pgNode) GetChild(name string) (child *nodefs.Inode) {
	if a.inode == nil {
		return nil
	}
	return a.inode.GetChild(name)
}

func (a *pgNode) AddChild(name string, child *nodefs.Inode) {
	if a.inode == nil {
		return
	}
	a.inode.AddChild(name, child)
}

func (a *pgNode) RmChild(name string) (ch *nodefs.Inode) {
	if a.inode == nil {
		return nil
	}
	return a.inode.RmChild(name)
}
