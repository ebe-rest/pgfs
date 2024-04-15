package main

import (
	"flag"
	"os"

	"pgfs/internal/pgfs"
	"pgfs/pkg/context"
	"pgfs/pkg/envs"
	"pgfs/pkg/log"
	"pgfs/pkg/pg"
)

type args struct {
	isDebug      bool
	pgConnParams map[string]string
	mountPoint   string
	args         []string
}

func main() {
	x := context.NewContext()
	e := envs.Parse()

	a := parseArgs(e)
	log.SetDebug(a.isDebug)
	log.Debug("pgConnParams:", a.pgConnParams)
	log.Debug("mountPoint:", a.mountPoint)

	var err error
	goto start

onerror:
	log.Debug(err)
	panic(x.Cancel(err))

start:
	p := pg.New()
	err = p.BuildConfig(a.pgConnParams)
	if err != nil {
		goto onerror
	}

	err = pgfs.Serv(x, p, a.mountPoint, a.isDebug)
	if err != nil {
		goto onerror
	}

	os.Exit(0)
}

func parseArgs(e envs.Envs) *args {
	a := &args{}

	a.isDebug = false
	isDebug := flag.Bool("debug", false, "デバッグモードを有効にします。")

	a.pgConnParams = map[string]string{}

	a.pgConnParams["dbname"] = e["PGDATABASE"]
	dbname1 := flag.String("d", a.pgConnParams["dbname"], "--dbname")
	dbname2 := flag.String("dbname", a.pgConnParams["dbname"], "接続するデータベースの名前を指定します。\n"+
		"コマンドラインでオプション以外の最初の引数としてdbnameを指定するのと同じ効力を持ちます。\n"+
		"dbnameは接続文字列でも構いません。\n"+
		"その場合、接続文字列パラメータは衝突するコマンドラインオプションに優先します。")

	a.pgConnParams["host"] = e["PGHOST"]
	host1 := flag.String("h", a.pgConnParams["host"], "--host")
	host2 := flag.String("host", a.pgConnParams["host"], "サーバを実行しているマシンのホスト名を指定します。\n"+
		"この値がスラッシュから始まる場合、Unixドメインソケット用のディレクトリとして使用されます。")

	a.pgConnParams["port"] = e["PGPORT"]
	port1 := flag.String("p", a.pgConnParams["port"], "--port")
	port2 := flag.String("port", a.pgConnParams["port"], "サーバが接続監視を行っているTCPポートもしくはローカルUnixドメインソケットファイルの拡張子を指定します。\n"+
		"環境変数PGPORTの値、環境変数が設定されていない場合はコンパイル時に指定した値（通常は5432）がデフォルト値となります。")

	a.pgConnParams["user"] = e["PGUSER"]
	user1 := flag.String("U", a.pgConnParams["user"], "--username")
	user2 := flag.String("username", a.pgConnParams["user"], "デフォルトのユーザではなくusernameユーザとしてデータベースに接続します\n"+
		"（当然、そうする権限を持っていなければなりません）。")

	fourcePassword1 := flag.Bool("W", false, "--password")
	fourcePassword2 := flag.Bool("password", false, "パスワードが使われない場合であっても、データベースに接続する前にpsqlは強制的にパスワード入力を促します。\n"+
		"サーバがパスワード認証を必要とし、かつ、.pgpassファイルなどの他の情報源からパスワードが入手可能でない場合、psqlは常にパスワード入力を促します。\n"+
		"しかし、psqlは、サーバにパスワードが必要かどうかを判断するための接続試行を無駄に行います。 こうした余計な接続試行を防ぐために-Wの入力が有意となる場合もあります。")

	noPassword1 := flag.Bool("w", false, "--no-password")
	noPassword2 := flag.Bool("no-password", false, "パスワードの入力を促しません。\n"+
		"サーバがパスワード認証を必要とし、かつ、.pgpassファイルなどの他の情報源からパスワードが入手可能でない場合、接続試行は失敗します。\n"+
		"バッチジョブやスクリプトなどパスワードを入力するユーザが存在しない場合にこのオプションは有用かもしれません。")

	a.mountPoint = "/pgfs"
	mountPoint1 := flag.String("m", a.mountPoint, "--mount-point")
	mountPoint2 := flag.String("mount-point", a.mountPoint, "マウントポイントを指定します")

	flag.Parse()
	a.args = flag.Args()

	if isDebug != nil && *isDebug {
		a.isDebug = true
	}

	switch {
	case dbname2 != nil && *dbname2 != a.pgConnParams["dbname"]:
		a.pgConnParams["dbname"] = *dbname2
	case dbname1 != nil && *dbname1 != a.pgConnParams["dbname"]:
		a.pgConnParams["dbname"] = *dbname1
	case len(a.args) != 0 && a.args[0] != a.pgConnParams["dbname"]:
		a.pgConnParams["dbname"] = a.args[0]
		a.args = a.args[1:]
	case a.pgConnParams["dbname"] != "":
		a.pgConnParams["dbname"] = a.pgConnParams["dbname"]
	default:
		delete(a.pgConnParams, "dbname")
	}

	switch {
	case host2 != nil && *host2 != a.pgConnParams["host"]:
		a.pgConnParams["host"] = *host2
	case host1 != nil && *host1 != a.pgConnParams["host"]:
		a.pgConnParams["host"] = *host1
	case a.pgConnParams["host"] != "":
		a.pgConnParams["host"] = a.pgConnParams["host"]
	default:
		delete(a.pgConnParams, "host")
	}

	switch {
	case port2 != nil && *port2 != a.pgConnParams["port"]:
		a.pgConnParams["port"] = *port2
	case port1 != nil && *port1 != a.pgConnParams["port"]:
		a.pgConnParams["port"] = *port1
	case a.pgConnParams["port"] != "":
		a.pgConnParams["port"] = a.pgConnParams["port"]
	default:
		delete(a.pgConnParams, "port")
	}

	switch {
	case user2 != nil && *user2 != a.pgConnParams["user"]:
		a.pgConnParams["user"] = *user2
	case user1 != nil && *user1 != a.pgConnParams["user"]:
		a.pgConnParams["user"] = *user1
	case a.pgConnParams["user"] != "":
		a.pgConnParams["user"] = a.pgConnParams["user"]
	default:
		delete(a.pgConnParams, "user")
	}

	switch {
	case fourcePassword2 != nil && *fourcePassword2:
		a.pgConnParams["fourcePassword"] = "1"
	case fourcePassword1 != nil && *fourcePassword1:
		a.pgConnParams["fourcePassword"] = "1"
	}

	if a.pgConnParams["fourcePassword"] != "1" {
		a.pgConnParams["password"] = e["PGPASSWORD"]
	}

	switch {
	case noPassword2 != nil && *noPassword2:
		a.pgConnParams["noPassword"] = "1"
	case noPassword1 != nil && *noPassword1:
		a.pgConnParams["noPassword"] = "1"
	}

	switch {
	case mountPoint2 != nil && *mountPoint2 != a.mountPoint:
		a.mountPoint = *mountPoint2
	case mountPoint1 != nil && *mountPoint1 != a.mountPoint:
		a.mountPoint = *mountPoint1
	case len(a.args) != 0 && a.args[0] != a.mountPoint:
		a.mountPoint = a.args[0]
		a.args = a.args[1:]
	}

	return a
}
