package pg

import (
	"errors"
	"fmt"
	"net/url"
	"os"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"golang.org/x/crypto/ssh/terminal"

	"pgfs/pkg/context"
	"pgfs/pkg/log"
)

type common interface {
	Exec(ctx context.Context, sql string, args ...any) (CommandTag, error)
	Query(ctx context.Context, sql string, args ...any) (Rows, error)
	Exists(ctx context.Context, sql string, args ...any) (bool, error)
	QueryScanSingleRow(ctx context.Context, sql string, args []any, dest []any) (CommandTag, error)
}

type Pg interface {
	common
	BuildConfig(params map[string]string) error
	Config() *pgx.ConnConfig
	Connect(ctx context.Context) (Conn, error)
	Begin(ctx context.Context) (Tx, error)
}

type pg struct {
	config *pgx.ConnConfig
}

type Conn interface {
	common
	Close(ctx context.Context, err error) error
	Begin(ctx context.Context) (Tx, error)
}

type conn struct {
	pg   *pg
	conn *pgx.Conn
}

type Tx interface {
	common
	Close(ctx context.Context, err error) error
	Commit(ctx context.Context, err error) error
	Rollback(ctx context.Context, err error) error
}

type tx struct {
	conn *conn
	tx   pgx.Tx
}

type CommandTag interface {
	RowsAffected() int64
	String() string
}

type commandTag struct {
	tag pgconn.CommandTag
}

type Rows interface {
	Close()
	Err() error
	CommandTag() pgconn.CommandTag
	RowsAffected() int64
	String() string
	FieldDescriptions() []pgconn.FieldDescription
	Next() bool
	Scan(dest ...any) error
	Values() ([]any, error)
	RawValues() [][]byte
}

type rows struct {
	rows pgx.Rows
}

type configBuilder struct {
	params map[string]string
}

func New() Pg {
	a := &pg{}
	return a
}

func (a *pg) BuildConfig(params map[string]string) error {
	var err error
	b := &configBuilder{}
	b.params = params
	passwords := map[string]bool{}

	// [postgresql|postgres]://[user[:password]@][[host][:port][,...]][/dbname][?name=value[&...]]
	if strings.HasPrefix(params["dbname"], "postgresql://") || strings.HasPrefix(params["dbname"], "postgres://") {
		connString := params["dbname"]
		params["dbname"] = ""

		var u *url.URL
		if params["fourcePassword"] == "1" {
			u, err = url.Parse(connString)
			if err != nil {
				return err
			}
			if _, ok := u.User.Password(); ok {
				goto retryWithOtherPassword
			}
		}

		err = buildByString(a, connString)
		if err != nil {
			u, err = url.Parse(connString)
			if err != nil {
				return err
			}
			goto retryWithOtherPassword
		}

		return nil

	retryWithOtherPassword:
		for k, vs := range u.Query() {
			for i := range vs {
				vs[i] = vs[i]
			}
			params[k] = strings.Join(vs, ",")
		}
		if ssl, ok := params["ssl"]; ok {
			if params["sslmode"] == "" && ssl == "true" {
				params["sslmode"] = "require"
			}
			delete(params, "ssl")
		}
		if u.User.Username() != "" {
			params["user"] = u.User.Username()
		}
		if password, ok := u.User.Password(); ok && password != "" {
			passwords[(password)] = true
		}
		if u.Hostname() != "" {
			params["host"] = u.Hostname()
		}
		if u.Port() != "" {
			params["port"] = u.Port()
		}
		if u.Path != "" {
			params["dbname"] = u.Path
		}
	}

	if params["fourcePassword"] == "1" {
		err = buildByInputPassword(a, b)
		if err != nil {
			return err
		}

		return nil
	}

	if params["password"] != "" {
		passwords[params["password"]] = true
	}

	for password := range passwords {
		params["password"] = password
		err = buildByParams(a, b)
		if err == nil {
			return nil
		}
	}

	delete(params, "password")
	err = buildByParams(a, b)
	if err == nil {
		return nil
	}

	if params["noPassword"] != "1" {
		err = buildByInputPassword(a, b)
		if err == nil {
			return nil
		}
	}

	return err
}

func buildByInputPassword(a *pg, b *configBuilder) error {
	var err error
	params := b.params

	for try := 0; try < 3; try++ {
		_, _ = fmt.Printf("password: ")

		var tty *os.File
		tty, err = os.Open("/dev/tty")
		if err != nil {
			return err
		}

		var password []byte
		password, err = terminal.ReadPassword(int(tty.Fd()))
		if err != nil {
			return err
		}

		_, _ = fmt.Printf("\n")

		err = tty.Close()
		if err != nil {
			return err
		}

		params["password"] = string(password)
		err = buildByParams(a, b)
		if err != nil {
			continue
		}

		return nil
	}

	return err
}

func buildByParams(a *pg, b *configBuilder) error {
	connString := paramsToConnString(b)
	return buildByString(a, connString)
}

func buildByString(a *pg, connString string) error {
	config, err := pgx.ParseConfig(connString)
	if err != nil {
		return err
	}

	var con *pgx.Conn
	con, err = connect(context.Background(), config)
	if err != nil {
		return err
	}

	err = con.Close(context.Background())
	if err != nil {
		return err
	}

	a.config = config
	return nil
}

func paramsToConnString(b *configBuilder) string {
	params := b.params

	var s []string
	for k, v := range params {
		s = append(s, fmt.Sprintf("%s=%s ", k, quoteForConnString(v)))
	}

	return strings.Join(s, " ")
}

func quoteForConnString(value string) string {
	if value == "" {
		return "''"
	}
	if !strings.ContainsAny(value, " '\\") {
		return value
	}
	return "'" + strings.NewReplacer("'", "\\'", "\\", "\\\\").Replace(value) + "'"
}

func connect(ctx context.Context, config *pgx.ConnConfig) (*pgx.Conn, error) {
	c := context.UseContext(ctx)
	var err error
	goto start

onerror:
	log.Debug(err)
	return nil, c.Cancel(err)

start:
	if config.Tracer == nil {
		config.Tracer = tracer{}
	}
	// https://github.com/jackc/pgx/issues/747
	config.StatementCacheCapacity = 1

	con, err := pgx.ConnectConfig(c, config)
	if err != nil {
		goto onerror
	}

	err = con.Ping(c)
	if err != nil {
		goto onerror
	}

	return con, nil
}

type tracer struct {
}

type tracerContext struct {
	context.Context
	startTime time.Time
	startData *pgx.TraceQueryStartData
	*time.Timer
}

func (a tracer) TraceQueryStart(ctx context.Context, _ *pgx.Conn, start pgx.TraceQueryStartData) context.Context {
	now := time.Now()
	c := &tracerContext{
		Context:   ctx,
		startTime: now,
		startData: &start,
		Timer:     time.NewTimer(time.Minute),
	}
	go func() {
		select {
		case <-c.C:
			log.Debug("query running:", now, start.SQL, start.Args)
		}
	}()
	return c
}

func (a tracer) TraceQueryEnd(ctx context.Context, _ *pgx.Conn, end pgx.TraceQueryEndData) {
	c, ok := ctx.(*tracerContext)
	if !ok {
		return
	}
	if c.Stop() {
		log.Debug("query:", c.startTime, c.startData.SQL, c.startData.Args, end.CommandTag, time.Now().Sub(c.startTime), end.Err)
	} else {
		<-c.C
		log.Debug("query:", c.startTime, end.CommandTag, time.Now().Sub(c.startTime), end.Err)
	}
}

func (a *pg) Config() *pgx.ConnConfig {
	return a.config
}

func (a *pg) Connect(ctx context.Context) (Conn, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return nil, err
	}

	c := &conn{}
	c.pg = a

	c.conn, err = connect(x, a.config)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return c, nil
}

func (a *conn) Close(ctx context.Context, err error) error {
	var x context.Context2

	if err != nil {
		x = context.UseContext(ctx)
		_ = x.Cancel(err)
		goto hasError

	}

	x, err = context.UseContextCause(ctx)
	if err != nil {
		goto hasError
	}

	err = a.conn.Close(x)
	if err != nil {
		return x.Cancel(err)
	}

	return nil

hasError:
	e := a.conn.Close(x)
	if e != nil {
		err = errors.Join(err, e)
	}

	return err
}

func (a *tx) Close(ctx context.Context, err error) error {
	err = a.Commit(ctx, err)
	err = a.conn.Close(ctx, err)
	return err
}

func (a *pg) Begin(ctx context.Context) (Tx, error) {
	c, err := a.Connect(ctx)
	if err != nil {
		return nil, err
	}

	t, err := c.Begin(ctx)
	if err != nil {
		return nil, err
	}

	return t, nil
}

func (a *conn) Begin(ctx context.Context) (Tx, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return nil, err
	}

	t := &tx{}
	t.conn = a

	t.tx, err = a.conn.Begin(x)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return t, nil
}

func (a *tx) Commit(ctx context.Context, err error) error {
	var x context.Context2

	if err != nil {
		x = context.UseContext(ctx)
		_ = x.Cancel(err)
		goto hasError

	}

	x, err = context.UseContextCause(ctx)
	if err != nil {
		goto hasError
	}

	err = a.tx.Commit(x)
	if err != nil {
		return x.Cancel(err)
	}

	return nil

hasError:
	e := a.tx.Rollback(x)
	if e != nil {
		err = errors.Join(err, e)
	}

	return err
}

func (a *tx) Rollback(ctx context.Context, err error) error {
	var x context.Context2

	if err != nil {
		x = context.UseContext(ctx)
		_ = x.Cancel(err)
		goto hasError
	}

	x, err = context.UseContextCause(ctx)
	if err != nil {
		goto hasError
	}

	err = a.tx.Rollback(x)
	if err != nil {
		return x.Cancel(err)
	}

	return nil

hasError:
	e := a.tx.Rollback(x)
	if e != nil {
		err = errors.Join(err, e)
	}

	return err
}

func (a *pg) Exec(ctx context.Context, sql string, args ...any) (CommandTag, error) {
	c, err := a.Connect(ctx)
	if err != nil {
		return nil, err
	}
	defer func() {
		err = c.Close(ctx, err)
	}()

	return c.Exec(ctx, sql, args...)
}

func (a *conn) Exec(ctx context.Context, sql string, args ...any) (CommandTag, error) {
	return exec(ctx, a.conn.Exec, sql, args)
}

func (a *tx) Exec(ctx context.Context, sql string, args ...any) (CommandTag, error) {
	return exec(ctx, a.tx.Exec, sql, args)
}

func exec(
	ctx context.Context,
	exec func(ctx context.Context, sql string, args ...any) (commandTag pgconn.CommandTag, err error),
	sql string,
	args []any,
) (CommandTag, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return nil, err
	}

	t := new(commandTag)
	t.tag, err = exec(x, sql, args...)
	if err != nil {
		return nil, x.Cancel(err)
	}

	return t, nil
}

func (a *pg) Query(ctx context.Context, sql string, args ...any) (Rows, error) {
	c, err := a.Connect(ctx)
	if err != nil {
		return nil, err
	}
	defer func() {
		err = c.Close(ctx, err)
	}()

	return c.Query(ctx, sql, args...)
}

func (a *conn) Query(ctx context.Context, sql string, args ...any) (Rows, error) {
	return query(ctx, a.conn.Query, sql, args)
}

func (a *tx) Query(ctx context.Context, sql string, args ...any) (Rows, error) {
	return query(ctx, a.tx.Query, sql, args)
}

func query(
	ctx context.Context,
	query func(ctx context.Context, sql string, args ...any) (pgx.Rows, error),
	sql string,
	args []any,
) (Rows, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return nil, err
	}

	r := new(rows)
	r.rows, err = query(x, sql, args...)
	if err != nil {
		return nil, x.Cancel(err)
	}

	err = r.rows.Err()
	if err != nil {
		return nil, x.Cancel(err)
	}

	return r, nil
}

func (a *pg) Exists(ctx context.Context, sql string, args ...any) (bool, error) {
	c, err := a.Connect(ctx)
	if err != nil {
		return false, err
	}
	defer func() {
		err = c.Close(ctx, err)
	}()

	return c.Exists(ctx, sql, args...)
}

func (a *conn) Exists(ctx context.Context, sql string, args ...any) (bool, error) {
	return exists(ctx, a.conn.Query, sql, args)
}

func (a *tx) Exists(ctx context.Context, sql string, args ...any) (bool, error) {
	return exists(ctx, a.tx.Query, sql, args)
}

func exists(
	ctx context.Context,
	query func(ctx context.Context, sql string, args ...any) (pgx.Rows, error),
	sql string,
	args []any,
) (bool, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return false, err
	}

	r, err := query(x, sql, args...)
	if err != nil {
		return false, x.Cancel(err)
	}
	defer func() {
		r.Close()
	}()

	ok := r.Next()
	err = r.Err()
	if err != nil {
		return false, x.Cancel(err)
	}

	return ok, nil
}

func (a *pg) QueryScanSingleRow(ctx context.Context, sql string, args []any, dest []any) (CommandTag, error) {
	return queryScanSingleRow(ctx, a.Query, sql, args, dest)
}

func (a *conn) QueryScanSingleRow(ctx context.Context, sql string, args []any, dest []any) (CommandTag, error) {
	return queryScanSingleRow(ctx, a.Query, sql, args, dest)
}

func (a *tx) QueryScanSingleRow(ctx context.Context, sql string, args []any, dest []any) (CommandTag, error) {
	return queryScanSingleRow(ctx, a.Query, sql, args, dest)
}

func queryScanSingleRow(
	ctx context.Context,
	query func(ctx context.Context, sql string, args ...any) (Rows, error),
	sql string,
	args []any,
	dest []any,
) (CommandTag, error) {
	x, err := context.UseContextCause(ctx)
	if err != nil {
		return nil, err
	}

	r, err := query(x, sql, args...)
	if err != nil {
		return nil, x.Cancel(err)
	}

	if r.Next() {
		err = r.Scan(dest...)
		if err != nil {
			return nil, x.Cancel(err)
		}
	}

	r.Close()
	return r.CommandTag(), nil
}

func (a *commandTag) RowsAffected() int64 {
	return a.tag.RowsAffected()
}

func (a *commandTag) String() string {
	return a.tag.String()
}

func (a *rows) Close() {
	a.rows.Close()
}

func (a *rows) Err() error {
	return a.rows.Err()
}

func (a *rows) CommandTag() pgconn.CommandTag {
	return a.rows.CommandTag()
}

func (a *rows) RowsAffected() int64 {
	return a.CommandTag().RowsAffected()
}

func (a *rows) String() string {
	return a.CommandTag().String()
}

func (a *rows) FieldDescriptions() []pgconn.FieldDescription {
	return a.rows.FieldDescriptions()
}

func (a *rows) Next() bool {
	return a.rows.Next()
}

func (a *rows) Scan(dest ...any) error {
	return a.rows.Scan(dest...)
}

func (a *rows) Values() ([]any, error) {
	return a.rows.Values()
}

func (a *rows) RawValues() [][]byte {
	return a.rows.RawValues()
}
