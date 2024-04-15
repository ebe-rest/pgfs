package pgfs

import (
	"fmt"

	"pgfs/pkg/context"
	"pgfs/pkg/pg"
)

type deleteNodesExecutor struct {
	tx   pg.Tx
	fs   *pgFs
	inos []uint64
}

func deleteNodes(
	ctx context.Context,
	tx pg.Tx,
	fs *pgFs,
	nodes ...*pgNode,
) (
	deletedNodes []*pgNode,
	err error,
) {
	x := context.UseContext(ctx)

	if len(nodes) == 0 {
		return nodes, nil
	}

	inos := make([]uint64, len(nodes))
	for i, node := range nodes {
		if !node.IsValid() {
			return nil, x.Cancel(fmt.Errorf("nodes[%d] is invalid", i))
		}
		inos[i] = node.Ino()
	}

	if !fs.IsValid() {
		fs = nodes[0].Fs()
	}

	deletedIds, err := deleteNodesById(x, tx, fs, inos...)
	if err != nil {
		return nil, x.Cancel(err)
	}

	for _, node := range nodes {
		for i, id := range deletedIds {
			if node.Ino() == id {
				deletedNodes = append(deletedNodes, node)
				inos = append(inos[:i], inos[i+1:]...)
				break
			}
		}
	}

	return deletedNodes, nil
}

func deleteNodesById(
	ctx context.Context,
	tx pg.Tx,
	fs *pgFs,
	inos ...uint64,
) (
	_ []uint64,
	err error,
) {
	x := context.UseContext(ctx)

	if len(inos) == 0 {
		return inos, nil
	}

	if !fs.IsValid() {
		return nil, x.Cancel(fmt.Errorf("fs is invalid"))
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

	a := deleteNodesExecutor{
		tx:   tx,
		fs:   fs,
		inos: inos,
	}

	inos, err = a.execute(x)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return inos, nil
}

func (a *deleteNodesExecutor) execute(
	x context.Context2,
) (
	deleted []uint64,
	err error,
) {
	q := `
		DELETE FROM pgfs.node AS n
		WHERE n.id = ANY($1)
		RETURNING
			n.id
	`
	inos := make([]int64, len(a.inos))
	for i, ino := range a.inos {
		inos[i] = int64(ino)
	}
	rows, err := a.tx.Query(
		x,
		q,
		inos,
	)
	if err != nil {
		return nil, x.Cancel(err)
	}

	for rows.Next() {
		var ino int64
		err = rows.Scan(&ino)
		if err != nil {
			return nil, x.Cancel(err)
		}

		deleted = append(deleted, uint64(ino))
	}
	err = rows.Err()
	if err != nil {
		return nil, x.Cancel(err)
	}

	return deleted, nil
}
