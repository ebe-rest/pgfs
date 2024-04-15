package pgfs

import (
	"fmt"

	"golang.org/x/exp/maps"

	"pgfs/pkg/context"
	"pgfs/pkg/pg"
)

type insertNodesExecutor struct {
	tx    pg.Tx
	fs    *pgFs
	nodes pgNodes
}

func insertNodes(
	ctx context.Context,
	tx pg.Tx,
	fs *pgFs,
	nodes ...*pgNode,
) (
	insertedNodes []*pgNode,
	err error,
) {
	if len(nodes) == 0 {
		return nodes, nil
	}

	x := context.UseContext(ctx)

	for i, node := range nodes {
		if !node.IsValid() {
			return nil, x.Cancel(fmt.Errorf("nodes[%d] is invalid", i))
		}
	}

	if !fs.IsValid() {
		fs = nodes[0].Fs()
	}

	if tx == nil {
		tx, err = fs.pg.Begin(ctx)
		if err != nil {
			return nil, x.Cancel(err)
		}
		defer func() {
			err = tx.Close(x, err)
		}()
	}

	a := &insertNodesExecutor{
		tx:    tx,
		fs:    fs,
		nodes: nodes,
	}

	err = a.generateInoAll(x)
	if err != nil {
		return nil, x.Cancel(err)
	}

	err = a.execute(x)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return a.nodes, nil
}

func (a *insertNodesExecutor) generateInoAll(x context.Context2) error {
	for {
		ok, err := a.generateIno(x)
		if err != nil {
			return x.Cancel(err)
		}
		if !ok {
			continue
		}
		return nil
	}
}

func (a *insertNodesExecutor) generateIno(x context.Context2) (ok bool, err error) {
	nm := map[int64]*pgNode{}
	for _, node := range a.nodes {
		if node.Ino() == 0 {
			for {
				ino := newRandomInt64()
				_, ok = nm[ino]
				if ok {
					continue
				}
				node.SetIno(uint64(ino))
				break
			}
		}
		nm[int64(node.Ino())] = node
	}

	q := `
		SELECT ino
		FROM pgfs.node
		WHERE ino = ANY($1)
	`
	args := []any{maps.Keys(nm)}
	rows, err := a.tx.Query(x, q, args...)
	if err != nil {
		return false, x.Cancel(err)
	}
	defer rows.Close()

	ok = true
	for rows.Next() {
		ok = false

		var ino int64
		err = rows.Scan(&ino)
		if err != nil {
			return false, x.Cancel(err)
		}

		node := nm[ino]
		node.SetIno(0)
	}
	err = rows.Err()
	if err != nil {
		return false, x.Cancel(err)
	}

	return ok, nil
}

func (a *insertNodesExecutor) execute(x context.Context2) error {
	rc := a.nodes.ToRc()
	q := `
		INSERT INTO pgfs.node AS n (
			ino,
			mode,
			nlink,
			uid,
			gid,
			size,
			atime,
			ctime,
			mtime
		)
		SELECT
			UNNEST(CAST($1  AS BIGINT[]))    AS ino,
			UNNEST(CAST($2  AS BIGINT[]))    AS mode,
			UNNEST(CAST($3  AS BIGINT[]))    AS nlink,
			UNNEST(CAST($4  AS BIGINT[]))    AS uid,
			UNNEST(CAST($5  AS BIGINT[]))    AS gid,
			UNNEST(CAST($6  AS BIGINT[]))    AS size,
			UNNEST(CAST($7  AS TIMESTAMP[])) AS atime,
			UNNEST(CAST($8  AS TIMESTAMP[])) AS ctime,
			UNNEST(CAST($9  AS TIMESTAMP[])) AS mtime
	`
	_, err := a.tx.Exec(
		x,
		q,
		rc["ino"],
		rc["mode"],
		rc["nlink"],
		rc["uid"],
		rc["gid"],
		rc["size"],
		rc["atime"],
		rc["ctime"],
		rc["mtime"],
	)
	if err != nil {
		return x.Cancel(err)
	}

	return nil
}
