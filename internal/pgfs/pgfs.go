package pgfs

import (
	"fmt"
	"sync"

	"pgfs/pkg/context"
	"pgfs/pkg/log"
	"pgfs/pkg/pg"
)

const rootNodeId = 0

type pgFs struct {
	pg         pg.Pg
	mountPoint string
	root       *pgNode
	mutex      sync.Mutex
}

func newFs(ctx context.Context, p pg.Pg, mountPoint string) (*pgFs, error) {
	x := context.UseContext(ctx)

	tx, err := p.Begin(x)
	if err != nil {
		return nil, x.Cancel(err)
	}
	defer func() {
		err = tx.Close(x, err)
	}()

	fs := &pgFs{
		pg:         p,
		mountPoint: mountPoint,
	}
	fs.mutex.Lock()
	defer fs.mutex.Unlock()

	q := `
		SELECT
			n.ino,
			n.mode,
			n.nlink,
			n.uid,
			n.gid,
			n.size,
			n.atime,
			n.ctime,
			n.mtime
		FROM
			pgfs.node AS n
		WHERE
			ino = $1
	`
	var rows pg.Rows
	rows, err = tx.Query(x, q, rootNodeId)
	if err != nil {
		return nil, x.Cancel(err)
	}
	defer rows.Close()

	if !rows.Next() {
		return nil, x.Cancel(fmt.Errorf("%w: database is not initialized?", err))
	}

	fs.root = newNode(fs)

	err = rows.Scan(fs.root)
	if err != nil {
		return nil, x.Cancel(err)
	}
	err = rows.Err()
	if err != nil {
		return nil, x.Cancel(err)
	}

	fs.root.AcceptChanges()

	return fs, nil
}

func (a *pgFs) IsValid() bool {
	return a != nil && a.pg != nil && a.root.IsValid()
}

func (a *pgFs) String() string {
	return log.Sprintf("pgfs")
}

func (a *pgFs) Pg() pg.Pg {
	return a.pg
}

func (a *pgFs) Begin(ctx context.Context) (pg.Tx, error) {
	return a.pg.Begin(ctx)
}
