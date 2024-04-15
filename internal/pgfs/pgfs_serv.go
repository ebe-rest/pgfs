package pgfs

import (
	"github.com/hanwen/go-fuse/v2/fuse"
	"github.com/hanwen/go-fuse/v2/fuse/nodefs"

	"pgfs/pkg/context"
	"pgfs/pkg/pg"
)

func Serv(ctx context.Context, pg pg.Pg, mountPoint string, isDebug bool) error {
	x := context.UseContext(ctx)

	err := checkMountPoint(mountPoint)
	if err != nil {
		return x.Cancel(err)
	}

	fs, err := newFs(x, pg, mountPoint)
	if err != nil {
		return x.Cancel(err)
	}

	conn := nodefs.NewFileSystemConnector(fs.root, &nodefs.Options{
		AttrTimeout:         0,
		Debug:               isDebug,
		EntryTimeout:        0,
		LookupKnownChildren: false,
		NegativeTimeout:     0,
		Owner:               nil,
		PortableInodes:      false,
	})

	server, err := fuse.NewServer(conn.RawFS(), mountPoint, &fuse.MountOptions{
		AllowOther:               true,
		Debug:                    isDebug,
		DirectMount:              false,
		DirectMountFlags:         0,
		DirectMountStrict:        false,
		DisableReadDirPlus:       false,
		DisableXAttrs:            false,
		EnableAcl:                true,
		EnableLocks:              true,
		EnableSymlinkCaching:     false,
		ExplicitDataCacheControl: false,
		FsName:                   "pgfs",
		IgnoreSecurityLabels:     false,
		Logger:                   nil,
		MaxBackground:            0,
		MaxReadAhead:             0,
		MaxWrite:                 0,
		Name:                     pg.Config().Database,
		Options:                  nil,
		RememberInodes:           false,
		SingleThreaded:           false,
		SyncRead:                 false,
	})
	if err != nil {
		return x.Cancel(err)
	}

	go server.Serve()
	err = server.WaitMount()
	if err != nil {
		return x.Cancel(err)
	}

	server.Wait()

	return nil
}
