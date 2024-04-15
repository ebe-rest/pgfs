package pgfs

import (
	"fmt"

	"pgfs/pkg/context"
	"pgfs/pkg/pg"
)

type updateNodesExecutor struct {
	tx    pg.Tx
	fs    *pgFs
	nodes pgNodes
}

func updateNodes(
	ctx context.Context,
	tx pg.Tx,
	fs *pgFs,
	nodes ...*pgNode,
) (
	updatedNodes []*pgNode,
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

	a := &updateNodesExecutor{
		tx:    tx,
		fs:    fs,
		nodes: nodes,
	}

	err = a.execute(x)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return a.nodes, nil
}

func (a *updateNodesExecutor) execute(x context.Context2) error {
	rc := a.nodes.ToRc()
	q := `
		UPDATE pgfs.node AS n
		SET
			mode = o.mode,
			nlink = o.nlink,
			uid = o.uid,
			gid = o.gid,
			size = o.size,
			atime = o.atime,
			ctime = o.ctime,
			mtime = o.mtime
		FROM (
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
		) AS o
		WHERE
			o.ino = n.ino
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

	return err
}
