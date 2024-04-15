package pgfs

import (
	"path"
	"runtime"
	"time"

	"github.com/hanwen/go-fuse/v2/fuse"
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"
	"github.com/jackc/pgx/v5/pgtype"

	"pgfs/pkg/context"
	"pgfs/pkg/log"
	"pgfs/pkg/pg"
)

// implements nodefs.Node.
// references nodefs.defaultNode
var _ nodefs.Node = (*pgNode)(nil)

// OnMount
// implements nodefs.Node.
// do nothing.
func (a *pgNode) OnMount(conn *nodefs.FileSystemConnector) {
	log.Debug(
		"■ OnMount",
		toString(a),
		toString(conn.RawFS().String()),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()
}

// OnUnmount
// implements nodefs.Node.
// do nothing.
func (a *pgNode) OnUnmount() {
	log.Debug(
		"■ OnUnmount",
		toString(a),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()
}

// ---

// GetAttr
// implements nodefs.Node.GetAttr
// references nodefs.defaultNode.GetAttr
func (a *pgNode) GetAttr(attr *fuse.Attr, file nodefs.File, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ GetAttr",
		"a", toString(a),
		"file", toString(file),
		"ctx", toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	if file != nil {
		// TODO
		runtime.Breakpoint()
		return file.GetAttr(attr)
	}

	*attr = *a.Attr()

	return fuse.OK
}

// GetXAttr
// implements nodefs.Node.GetXAttr
// references nodefs.defaultNode.GetXAttr
func (a *pgNode) GetXAttr(attr string, ctx *fuse.Context) (data []byte, code fuse.Status) {
	log.Debug(
		"■ GetXAttr",
		toString(a),
		toString(attr),
		toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	data = a.xattr.Get(attr)
	if len(data) == 0 {
		return nil, fuse.ENOATTR
	}

	return data, fuse.OK
}

// OpenDir
// implements nodefs.Node.OpenDir
// references nodefs.defaultNode.OpenDir
func (a *pgNode) OpenDir(ctx *fuse.Context) (data []fuse.DirEntry, code fuse.Status) {
	log.Debug(
		"■ OpenDir",
		toString(a),
		toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	x := context.UseContext(ctx)

	tx, err := a.Begin(x)
	if err != nil {
		return nil, errorToStatus(x.Cancel(err))
	}
	defer func() {
		err = tx.Close(x, err)
		if err != nil {
			data = nil
			code = errorToStatus(x.Cancel(err))
		}
	}()

	q := `
		SELECT
			n.mode,
			p.path,
			n.ino
		FROM
			pgfs.file AS p
		INNER JOIN
			pgfs.file AS c
		ON
			c.pno = p.fno
		INNER JOIN
			pgfs.node AS n
		ON
			n.ino = c.ino
		WHERE
			n.ino <> 0
			AND p.ino = $1
	`
	var rows pg.Rows
	rows, err = tx.Query(x, q, integerToPgInt8(a.Ino()))
	if err != nil {
		return nil, errorToStatus(x.Cancel(err))
	}
	defer rows.Close()

	children := a.inode.Children()
	children := a.inode.Children()

	data = nil
	for rows.Next() {
		var m pgtype.Int8
		var p pgtype.Text
		var i pgtype.Int8
		err = rows.Scan(&m, &p, &i)
		if err != nil {
			return nil, errorToStatus(x.Cancel(err))
		}
		data = append(data, fuse.DirEntry{
			Mode: pgInt8ToUint32(m),
			Name: path.Base(pgTextToString(p)),
			Ino:  pgInt8ToUint64(i),
		})
		a.inode.NewChild()
	}

	return data, fuse.OK
}

// ---

// Lookup TODO
// implements nodefs.Node
func (a *pgNode) Lookup(attr *fuse.Attr, name string, ctx *fuse.Context) (*nodefs.Inode, fuse.Status) {
	log.Debug(
		"■ Lookup",
		toString(a),
		toString(name),
		toString(ctx),
	)
	return nil, fuse.ENOENT

	// inode := a.GetChild(name)
	// if inode != nil {
	// 	return inode, fuse.OK
	// }
	//
	// a.mutex.Lock()
	// defer a.mutex.Unlock()
	//
	// x := context.UseContext(ctx)
	//
	// tx, err := a.fs.pg.Begin(x)
	// if err != nil {
	// 	return nil, errorToStatus(x.Cancel(err))
	// }
	// defer func() {
	// 	err = tx.Close(x, err)
	// }()
	//
	// _, node, err := selectNodes(x, tx, a, nil, "name = $", name)
	// if err != nil {
	// 	return nil, errorToStatus(x.Cancel(err))
	// }
	// if node == nil {
	// 	return nil, fuse.ENOENT
	// }
	//
	// inode = a.inode.NewChild(node.name, node.attr.IsDir(), node)
	// *attr = *node.attr.attr
	// return inode, fuse.OK
}

// Deletable always returns true
func (a *pgNode) Deletable() bool {
	log.Debug(
		"■ OnUnmount",
		toString(a),
	)

	return true
}

// OnForget implement nodefs.Node
func (a *pgNode) OnForget() {
	log.Debug(
		"■ OnForget",
		toString(a),
	)

	for {
		parent, name := a.inode.Parent()
		if parent == nil {
			break
		}
		parent.RmChild(name)
	}
}

// Access TODO implement me
func (a *pgNode) Access(mode uint32, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Access",
		toString(a),
		toString(mode),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Readlink TODO implement me
func (a *pgNode) Readlink(ctx *fuse.Context) ([]byte, fuse.Status) {
	log.Debug(
		"■ Readlink",
		toString(a),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Mknod TODO implement me
func (a *pgNode) Mknod(name string, mode uint32, dev uint32, ctx *fuse.Context) (newNode *nodefs.Inode, code fuse.Status) {
	log.Debug(
		"■ Mknod",
		toString(a),
		toString(name),
		toString(mode),
		toString(dev),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Mkdir TODO
// implements nodefs.Node
func (a *pgNode) Mkdir(name string, mode uint32, ctx *fuse.Context) (*nodefs.Inode, fuse.Status) {
	log.Debug(
		"■ Mkdir",
		toString(a),
		toString(name),
		toString(mode),
		toString(ctx),
	)
	return nil, fuse.ENOENT

	// a.mutex.Lock()
	// defer a.mutex.Unlock()
	//
	// x := context.UseContext(ctx)
	//
	// tx, err := a.fs.pg.Begin(x)
	// if err != nil {
	// 	return nil, errorToStatus(x.Cancel(err))
	// }
	// defer func() {
	// 	err = tx.Close(x, err)
	// }()
	//
	// node := newNode(a.fs)
	// node.name = name
	// node.attr.SetChangeTime(time.Now())
	// node.attr.SetModTime(time.Now())
	// node.attr.SetMode(mode | fuse.S_IFDIR)
	// node.attr.SetUid(ctx.Uid)
	// node.attr.SetGid(ctx.Gid)
	// _, err = insertNodes(x, tx, a.fs, node)
	// if err != nil {
	// 	return nil, errorToStatus(x.Cancel(err))
	// }
	//
	// a.Inode().NewChild(name, true, node)
	// return node.Inode(), fuse.OK
}

// Unlink TODO implement me
func (a *pgNode) Unlink(name string, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Unlink",
		toString(a),
		toString(name),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Rmdir TODO implement me
func (a *pgNode) Rmdir(name string, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Rmdir",
		toString(a),
		toString(name),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Symlink TODO implement me
func (a *pgNode) Symlink(name string, content string, ctx *fuse.Context) (*nodefs.Inode, fuse.Status) {
	log.Debug(
		"■ Symlink",
		toString(a),
		toString(name),
		toString(content),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Rename TODO implement me
func (a *pgNode) Rename(oldName string, newParent nodefs.Node, newName string, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Rename",
		toString(a),
		toString(oldName),
		toString(newParent),
		toString(newName),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Link TODO implement me
func (a *pgNode) Link(name string, existing nodefs.Node, ctx *fuse.Context) (newNode *nodefs.Inode, code fuse.Status) {
	log.Debug(
		"■ Link",
		toString(a),
		toString(name),
		toString(existing),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Create TODO implement me
func (a *pgNode) Create(name string, flags uint32, mode uint32, ctx *fuse.Context) (file nodefs.File, child *nodefs.Inode, code fuse.Status) {
	log.Debug(
		"■ Create",
		toString(a),
		toString(name),
		toString(flags),
		toString(mode),
		toString(ctx),
	)
	return nil, nil, fuse.ENOENT

	// a.mutex.Lock()
	// defer a.mutex.Unlock()
	//
	// x := context.UseContext(ctx)
	//
	// tx, err := a.Pg().Begin(x)
	// if err != nil {
	// 	return nil, nil, errorToStatus(x.Cancel(err))
	// }
	// defer func() {
	// 	err = tx.Close(x, err)
	// }()
	//
	// f := InitFile()
	//
	// n := InitNode(a, nil)
	// n.SetName(name)
	// n.SetChangeTime(time.Now())
	// n.SetModTime(time.Now())
	// n.SetMode(mode | fuse.S_IFDIR)
	// n.SetUid(ctx.Uid)
	// n.SetGid(ctx.Gid)
	//
	// _, err = insertNodes(x, tx, n)
	// if err != nil {
	// 	return nil, nil, errorToStatus(x.Cancel(err))
	// }
	//
	// a.Inode().NewChild(name, true, n)
	// return nil, n.Inode(), fuse.OK
	//
	// // return a.node.Create(name, flags, mode, ctx)
	//
	// // runtime.Breakpoint()
	// // panic("implement me")
}

// Open TODO implement me
func (a *pgNode) Open(flags uint32, ctx *fuse.Context) (file nodefs.File, code fuse.Status) {
	log.Debug(
		"■ Open",
		toString(a),
		toString(flags),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Read
// implements nodefs.Node.Read
// reference to nodefs.defaultNode.Read
func (a *pgNode) Read(file nodefs.File, dest []byte, off int64, ctx *fuse.Context) (fuse.ReadResult, fuse.Status) {
	log.Debug(
		"■ Read",
		toString(a),
		toString(file),
		toString(ctx),
	)

	if file != nil {
		return file.Read(dest, off)
	}

	return nil, fuse.ENOSYS
}

// Write same as nodefs.*defaultNode.Write
func (a *pgNode) Write(file nodefs.File, data []byte, off int64, ctx *fuse.Context) (written uint32, code fuse.Status) {
	log.Debug(
		"■ Open",
		toString(a),
		toString(file),
		toString(ctx),
	)

	if file != nil {
		return file.Write(data, off)
	}
	return 0, fuse.ENOSYS
}

// RemoveXAttr implement nodefs.Node.RemoveXAttr
func (a *pgNode) RemoveXAttr(attr string, ctx *fuse.Context) fuse.Status {
	log.Debug(
		"■ RemoveXAttr",
		toString(a),
		toString(attr),
		toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	a.xattr.Remove(attr)

	return fuse.OK
}

// SetXAttr implement nodefs.Node.SetXAttr
func (a *pgNode) SetXAttr(attr string, data []byte, flags int, ctx *fuse.Context) fuse.Status {
	log.Debug(
		"■ SetXAttr",
		toString(a),
		toString(attr),
		toString(data),
		toString(flags),
		toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	a.xattr.Put(attr, data)

	return fuse.OK
}

// ListXAttr implement nodefs.Node.ListXAttr
func (a *pgNode) ListXAttr(ctx *fuse.Context) (attrs []string, code fuse.Status) {
	log.Debug(
		"■ ListXAttr",
		toString(a),
		toString(ctx),
	)

	a.mutex.Lock()
	defer a.mutex.Unlock()

	return a.xattr.Keys(), fuse.OK
}

// GetLk TODO implement me
func (a *pgNode) GetLk(file nodefs.File, owner uint64, lk *fuse.FileLock, flags uint32, out *fuse.FileLock, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ GetLk",
		toString(a),
		toString(file),
		toString(owner),
		toString(lk),
		toString(flags),
		toString(out),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// SetLk TODO implement me
func (a *pgNode) SetLk(file nodefs.File, owner uint64, lk *fuse.FileLock, flags uint32, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ SetLk",
		toString(a),
		toString(file),
		toString(owner),
		toString(lk),
		toString(flags),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// SetLkw TODO implement me
func (a *pgNode) SetLkw(file nodefs.File, owner uint64, lk *fuse.FileLock, flags uint32, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ SetLkw",
		toString(a),
		toString(file),
		toString(owner),
		toString(lk),
		toString(flags),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Chmod TODO implement me
func (a *pgNode) Chmod(file nodefs.File, perms uint32, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Chmod",
		toString(a),
		toString(file),
		toString(perms),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Chown TODO implement me
func (a *pgNode) Chown(file nodefs.File, uid uint32, gid uint32, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Chown",
		toString(a),
		toString(file),
		toString(uid),
		toString(gid),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Truncate TODO implement me
func (a *pgNode) Truncate(file nodefs.File, size uint64, ctx *fuse.Context) (code fuse.Status) {
	log.Debug(
		"■ Truncate",
		toString(a),
		toString(file),
		toString(size),
		toString(ctx),
	)

	runtime.Breakpoint()
	panic("implement me")
}

// Utimens TODO implement me
func (a *pgNode) Utimens(file nodefs.File, atime *time.Time, mtime *time.Time, ctx *fuse.Context) (code fuse.Status) {
	runtime.Breakpoint()
	panic("implement me")
}

// Fallocate TODO implement me
func (a *pgNode) Fallocate(file nodefs.File, off uint64, size uint64, mode uint32, ctx *fuse.Context) (code fuse.Status) {
	runtime.Breakpoint()
	panic("implement me")
}

// StatFs TODO implement me
func (a *pgNode) StatFs() *fuse.StatfsOut {
	runtime.Breakpoint()
	panic("implement me")
}
