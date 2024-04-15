package pgfs

import (
	"time"

	"github.com/hanwen/go-fuse/v2/fuse"
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"
)

// implements nodefs.File
// reference to nodefs.defaultFile
var _ nodefs.File = (*pgFile)(nil)

func (a *pgFile) SetInode(inode *nodefs.Inode) {
	a.node.SetInode(inode)
}

func (a *pgFile) InnerFile() nodefs.File {
	return nil
}

// Read implements nodefs.File
// call from pgNode.Read
func (a *pgFile) Read(dest []byte, off int64) (fuse.ReadResult, fuse.Status) {

	// end := int(off) + len(dest)
	// if end > len(f.data) {
	// 	end = len(f.data)
	// }

	// selectFileBlocks()

	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Write(data []byte, off int64) (written uint32, code fuse.Status) {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) GetLk(owner uint64, lk *fuse.FileLock, flags uint32, out *fuse.FileLock) (code fuse.Status) {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) SetLk(owner uint64, lk *fuse.FileLock, flags uint32) (code fuse.Status) {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) SetLkw(owner uint64, lk *fuse.FileLock, flags uint32) (code fuse.Status) {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Flush() fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Release() {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Fsync(flags int) (code fuse.Status) {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Truncate(size uint64) fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) GetAttr(out *fuse.Attr) fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Chown(uid uint32, gid uint32) fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Chmod(perms uint32) fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Utimens(atime *time.Time, mtime *time.Time) fuse.Status {
	// TODO implement me
	panic("implement me")
}

func (a *pgFile) Allocate(off uint64, size uint64, mode uint32) (code fuse.Status) {
	// TODO implement me
	panic("implement me")
}
