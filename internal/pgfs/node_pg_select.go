package pgfs

import (
	"errors"
	"strings"

	"pgfs/pkg/context"
	"pgfs/pkg/pg"
)

type selectNodesExecutor struct {
	tx     pg.Tx
	parent *pgNode
	fs     *pgFs
	states []string
	args   []any
	nodes  []*pgNode
}

func selectNodes(
	ctx context.Context,
	tx pg.Tx,
	parent *pgNode,
	fs *pgFs,
	whereStates ...any,
) (
	nodes []*pgNode,
	node *pgNode,
	err error,
) {
	x := context.UseContext(ctx)

	if fs == nil {
		if parent == nil || parent.Fs() == nil {
			return nil, nil, x.Cancel(errors.New("fs is nil"))
		}

		fs = parent.Fs()
	}

	if tx == nil {
		if fs.pg == nil {
			return nil, nil, x.Cancel(errors.New("pg is nil"))
		}

		tx, err = fs.pg.Begin(ctx)
		if err != nil {
			return nil, nil, x.Cancel(err)
		}

		defer func() {
			err = tx.Close(x, err)
		}()
	}

	if parent != nil {
		whereStates = append(whereStates, `parent_node_id = $`, parent.Ino())
	}
	states, args, _, err := buildWhereStates(whereStates, 1, nil)
	if err != nil {
		return nil, nil, x.Cancel(err)
	}

	a := selectNodesExecutor{
		tx:     tx,
		parent: parent,
		fs:     fs,
		states: states,
		args:   args,
	}

	err = a.execute(x)
	if err != nil {
		return nil, nil, x.Cancel(err)
	}

	if len(a.nodes) == 1 {
		node = a.nodes[0]
	}
	return a.nodes, node, nil
}

func (a *selectNodesExecutor) execute(x context.Context2) (err error) {
	q := `
		SELECT
			ino,
			mode,
			nlink,
			uid,
			gid,
			size,
			atime,
			ctime,
			mtime
		FROM
			pgfs.node`
	if len(a.states) != 0 {
		q += `
		WHERE
			` + strings.Join(a.states, `
			AND`)
	}
	q += `
	`

	var rows pg.Rows
	rows, err = a.tx.Query(x, q, a.args...)
	if err != nil {
		return x.Cancel(err)
	}
	defer rows.Close()

	for rows.Next() {
		node := newNode(a.fs)
		err = rows.Scan(node)
		if err != nil {
			return x.Cancel(err)
		}
		a.nodes = append(a.nodes, node)
	}
	err = rows.Err()
	if err != nil {
		return x.Cancel(err)
	}

	return nil
}
