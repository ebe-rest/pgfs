# mkfs.pgfs 仕様

PGFS ファイルシステムを PostgreSQL データベース上に初期化するツール `mkfs.pgfs` の仕様です。

このドキュメントは [src/mkfs/](../src/mkfs/)（`Pgfs.Lib.Config` ベース）で実装した仕様をまとめたものです。暫定・未対応事項は「暫定実装」セクションに記載します。

英語版は [Mkfs.md](Mkfs.md) を参照してください。

## 役割

PostgreSQL データベースに対し、PGFS が必要とする以下を冪等に作成します。

1. PGFS 用ユーザー
2. テーブルスペース（指定された場合のみ）
3. データベース
4. スキーマ
5. テーブル: `{prefix}inode`, `{prefix}data`, `{prefix}data_chunk`, `{prefix}settings`
6. ルートディレクトリ inode（`id = 0`）
7. `SaveTo=DB` を持つ設定値の永続化（`{prefix}settings` への UPSERT）
8. 設定ファイル `pgfs.toml`（`SaveTo=File` を持つ設定の書き出し）

**すべての工程は冪等**で、既に存在する場合は `CREATE` をスキップします。再実行しても破壊しません。

## 実行方式

エントリポイントは [src/mkfs/src/Program.cs](../src/mkfs/src/Program.cs)。

```pwsh
# 開発ビルド (bin/Debug/mkfs.pgfs.{dll,exe})
dotnet build src/mkfs/Mkfs.csproj
# 開発時実行
dotnet run --project src/mkfs -- [options]
# あるいはビルド済み実行ファイル
./bin/Debug/mkfs.pgfs [options]

# 自己完結発行 (bin/Publish/mkfs.pgfs[.exe] — single-file, ホスト RID 自動)
dotnet publish src/mkfs/Mkfs.csproj -c Release
```

優先順位（後勝ち）: **既定値 < DB < 設定ファイル (TOML) < コマンドライン引数**。

実装上は [`ConfigLoader`](../src/lib/src/Config/ConfigLoader.cs) が CLI → TOML → DB → Default の順にソースを走らせて 1 度に統合します。`--clean` 時は `skipToml = true` で TOML を意図的に無視します。

## コマンドラインオプション

[src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs) の各 `Field<T>` の `CliOptions` で定義されています。下表はその抜粋。

### 接続（PGFS ユーザー）

| オプション | 内容 | 既定 |
|---|---|---|
| `-c`, `--connection`, `--connection-string` | PGFS ユーザーで対象 DB に接続する文字列（Npgsql 形式） | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |

### 接続（スーパーユーザー）

| オプション | 内容 | 既定 |
|---|---|---|
| `--su`, `--super`, `--super-connection`, `--super-connection-string` (および `--super-user-...` 別名) | スーパーユーザーで maintenance DB (template1) に接続する文字列 | `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1;SslMode=Prefer` |

> **`--super` を明示しないときの接続先継承**: super 接続は DROP / CREATE DATABASE 等の破壊的操作に使う。`--super` を省略すると既定の `localhost` に化けるため、`--connection` をリモートに向けた場合に「別サーバ (localhost) の DB を DROP/CREATE してしまう」事故が起きうる。これを防ぐため、**`--super` を明示しないときは `--connection` の Host / Port / SslMode を super 接続へ継承**し、super と user が必ず同じサーバを向くようにしている（資格情報と maintenance DB は super 既定の postgres / template1 のまま）。super 資格情報がサーバごとに違う場合は `--super` を明示すれば、その値が完全に優先される。起動ログ冒頭の `targets: user = ... , super = ...` 行と、続く `user connection:` / `super connection:` 行（どちらも Password はマスク）で実際の宛先・接続文字列を確認できる。

接続文字列形式を 1 つで指定する設計です。個別の `--host`, `--port`, `--username`, `--password` は **当面は実装しません**（設定ファイル側で個別に書ける `database.connection.host` 等を使ってください — [§暫定実装](#暫定実装) 参照）。

### スキーマとテーブル

| オプション | 内容 | 既定 |
|---|---|---|
| `-s`, `--schema`, `--schema-name` | スキーマ名 | `public` |
| `-x`, `--prefix`, `--table-prefix`, `--table-name-prefix` | テーブル名プレフィックス。末尾 `_` がなければ自動付与 | `pgfs_` |
| `--tablespace`, `--tablespace-name` | テーブルスペース名 | `pg_default` |
| `--tablespace-path` | 新規テーブルスペース作成時のディレクトリパス | （空） |
| `--citus` | テーブルを Citus 分散テーブルとして登録する。詳細は [docs/support_for_citus.md](support_for_citus.md) | `false` |
| `-w`, `--worker`, `--workers` | Citus worker ノードのカンマ区切り `host[:port]`（例: `--worker "w1:5432,w2:5432"`）。空なら 1 ノード構成（coordinator のみ）。`--citus` と併用。 | （空） |

### ファイルシステム設定

これらは `pgfs_settings` テーブルにも保存されます（`SaveTo=File` も持つので `pgfs.toml` にも書き出されます）。

| オプション | 内容 | 既定 |
|---|---|---|
| `--volume-label` | ボリュームラベル | `pgfs` |
| `--cluster-size` | クラスタサイズ（バイト） | `4096` |
| `--default-chunk-size` | bytea チャンクサイズ（バイト） | `1048576` (1 MiB) |
| `--max-file-size` | 最大ファイルサイズ（バイト、`-1` で無制限） | `1099511627776` (1 TiB) |
| `--version` | ファイルシステムバージョン | `1.0.0` |

### マウント設定

| オプション | 内容 | 既定 |
|---|---|---|
| `-m`, `--mount-point` | マウントポイント | Linux/macOS: `/mnt/pgfs` ／ Windows: `P:` |
| `--cache-max-entries` | inode キャッシュエントリ上限 | `1024` |

### ロギング

| オプション | 内容 | 既定 |
|---|---|---|
| `--log-level`, `--log-min-level`, `--min-log-level` | 最低ログレベル | `Warning` |
| `--log-output` | ログ出力先（`stdout`/`stderr`/`file`） | `stderr` |

### 設定ファイル

| オプション | 内容 | 既定 |
|---|---|---|
| `-f`, `--setting`, `--setting-file` | 設定ファイルパス | `pgfs.toml` |
| `--setting-path`, `--setting-search-path`, `--setting-file-path`, `--setting-file-search-path` | 設定ファイル探索パス（複数指定可） | カレント → `~/.config/pgfs` → `~/.config` → `~` → `LocalAppData/pgfs` → `AppData/pgfs` |

### その他

| オプション | 内容 |
|---|---|
| `-?`, `-h`, `--help` | ヘルプを表示して終了 |
| `--clean` | 既存の `pgfs.toml` を無視し、`DROP DATABASE` してから再作成する。短縮形なし。テーブルスペースとロール（PGFS ユーザー）は破棄しないので、再作成しても所有者・テーブルスペースは維持される。他のクライアントが接続中の場合は `pg_terminate_backend` で強制切断する。 |

## 設定ファイル (pgfs.toml)

TOML 形式。ドット記法で階層を表現します。

```toml
# database セクション
database.tablespace = "pg_default"
database.schema = "public"
database.prefix = "pgfs_"

# 接続文字列は JSON 文字列として直列化されます
database.connection = "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer"
database.super_connection = "Host=localhost;Port=5432;Username=postgres;Database=template1;SslMode=Prefer"

# file_system セクション
file_system.cluster_size = 4096
file_system.default_chunk_size = 1048576
file_system.max_file_size = 1099511627776

# mount セクション
mount.mount_point = "/mnt/pgfs"
mount.cache_max_entries = 1024

# logging セクション
logging.level = "warning"
```

## DB スキーマ詳細

[docs/database.md](database.md) を正として、mkfs が実際に作成するテーブルは以下のとおりです。

### `{prefix}inode`

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | PK |
| `parent_id` | `BIGINT` | NOT NULL | UK1, IX1 |
| `name` | `TEXT` | NOT NULL | UK1 |
| `uname` | `TEXT` | NOT NULL | IX2 |
| `gname` | `TEXT` | NOT NULL | IX3 |
| `st_mode` | `INTEGER` | NOT NULL | |
| `st_nlink` | `INTEGER` | NOT NULL | `1` |
| `st_size` | `BIGINT` | NOT NULL | `0` |
| `st_mtime` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `st_ctime` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `link_target` | `TEXT` | NULL | |
| `is_junction` | `BOOLEAN` | NOT NULL | `FALSE` |
| `data_id` | `BIGINT` | NULL | |
| `xattrs` | `JSONB` | NOT NULL | `'{}'::JSONB` |
| `created_at` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `created_by` | `TEXT` | NOT NULL | |
| `updated_at` | `TIMESTAMP` | NOT NULL | `current_timestamp` |
| `updated_by` | `TEXT` | NOT NULL | |

PK = `(parent_id, id)`、UK1 = `(parent_id, name)`、IX = `id` / `uname` / `gname`。

PK を `(parent_id, id)` の複合にしているのは Citus 制約への対応（unique constraint には分散キー `parent_id` を含む必要がある）。id は BIGSERIAL でグローバル一意（sequence は coordinator）なので、`(parent_id, id)` も実質 id だけで一意になる。`WHERE id = @id` 検索用に `id` 単独 INDEX を別途持つ。`parent_id` 単独 INDEX は PK の leftmost プレフィックスで代用できるため作成しない。

ルート inode は `id = 0, parent_id = 0, name = '/', st_mode = 16877 (=0o40755)` を `ON CONFLICT (parent_id, name) DO NOTHING` で挿入します（`(id)` 単独 UK は Citus 制約上作れないため `(parent_id, name)` UK ターゲットを使う）。

### `{prefix}data`

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | PK |
| `chunk_size` | `INTEGER` | NOT NULL | |
| `total_size` | `BIGINT` | NOT NULL | |
| `created_at` / `created_by` / `updated_at` / `updated_by` | 監査用 | NOT NULL | |

### `{prefix}data_chunk`

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `data_id` | `BIGINT` | NOT NULL | PK |
| `chunk_index` | `INTEGER` | NOT NULL | PK |
| `payload` | `BYTEA` | NOT NULL | |
| 監査用 4 列 | | NOT NULL | |

PK = `(data_id, chunk_index)`。ファイル内容は `bytea` チャンクで保持し、1 ファイルの全チャンクが同一 Citus shard に載るようにしている（詳細は [docs/support_for_citus.md](support_for_citus.md)）。

### `{prefix}lock`

cross-client 排他制御の lock token テーブル。

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `target_id` | `BIGINT` | NOT NULL | PK |

PK = `target_id` のみ。監査列なし（データではなく lock token なので）。

### `{prefix}settings`

フラット `(scope, key)` PK。[`ConfigStore.Save<T>`](../src/lib/src/Config/ConfigStore.cs) がここに UPSERT し、[`ConfigStore.LoadAll`](../src/lib/src/Config/ConfigStore.cs) がここから読み出します。

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `scope` | `TEXT` | NOT NULL | PK |
| `key` | `TEXT` | NOT NULL | PK |
| `value` | `JSONB` | NOT NULL | `'null'::JSONB` |
| 監査用 4 列 | | NOT NULL | |

PK = `(scope, key)`。1 行 = 1 つの `Field<T>` の値で、`value` は JSONB ネイティブ表現（string → `"..."` / 数値 → `42` / bool → `true`/`false`）。

## Citus 分散化 (`--citus` / `--worker`)

`--citus` を付けて mkfs すると Citus 拡張を有効化し、各テーブルを分散テーブルとして登録します:

| テーブル | 分散方式 | 分散キー | colocation |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | default group |
| `pgfs_data` | distributed | `id` | default group |
| `pgfs_data_chunk` | distributed | `data_id` | `pgfs_data` と co-located（1 ファイル = 1 shard） |
| `pgfs_lock` | distributed | `target_id` | default group |
| `pgfs_settings` | local + metadata | — | `citus_add_local_table_to_metadata` で metadata 登録だけ |

`--worker host[:port],...` を併用すると、その worker 上にも pgfs DB + Citus 拡張を bootstrap し、coordinator から `citus_add_node` で登録します。

### 使用例

```bash
# 1 ノード構成（worker なし、coordinator が自分で shard を持つ）
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres"

# 多ノード構成（coordinator + worker1 + worker2）
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

### 制約

- `--citus` × `--tablespace ≠ pg_default` は両立不可（pre-flight で拒否）。
- `--clean --citus` で worker DB を DROP するのは **`--worker` で指定された worker のみ**。worker を構成から外す場合はその worker 上の pgfs DB を手動で DROP する必要あり。
- 多ノード構成では worker 側の PG にも `shared_preload_libraries = 'citus'` が設定済みである必要あり。

### 動作詳細 / 検証

EnsureDatabaseAsync のステップ構造（worker bootstrap → coordinator DB → Citus topology）や「DB 既存時はすべての Citus 関連 mutate をスキップ」という冪等性ルール、各種つまずきポイントは **[docs/support_for_citus.md](support_for_citus.md) を正とする**。検証スクリプトは [tests/citus/README.md](../tests/citus/README.md)（`multinode_probe.sh` / `test_matrix.sh` / `race_multinode.sh` / `verify.sql`）。

## 冪等性

すべての `CREATE` は事前に存在確認 SQL を発行してから実行します。

| 対象 | 存在確認 | 既存時の動作 |
|---|---|---|
| ユーザー | `pg_user.usename` | スキップ（パスワード変更はしない） |
| テーブルスペース | `pg_tablespace.spcname` | スキップ |
| データベース | `pg_database.datname` | スキップ |
| スキーマ | `pg_namespace.nspname` | スキップ |
| テーブル | `pg_class + pg_namespace` | スキップ |
| ルート inode | `INSERT ... ON CONFLICT (parent_id, name) DO NOTHING` | 挿入されない |
| 設定行 | `INSERT ... ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value` | 値が上書きされる |

### `--clean` での再作成

`--clean` を指定すると、データベース作成の前に **`DROP DATABASE`** を実行してから一連の `CREATE` を流します。

- **削除する**: 対象データベース（その中の `pgfs_*` テーブル / ルート inode / 設定行 / bytea チャンクもろとも）
- **削除しない**: テーブルスペース、PGFS ユーザー（ロール）、スーパーユーザー接続情報
- **設定ファイル**: 既存の `pgfs.toml` を **読み込まない**（「ファイルが無いもの」として起動）。CLI 引数だけが適用され、終了時に新しい `pgfs.toml` が書き出される。
- **他クライアント接続**: `pg_terminate_backend` で強制切断してから DROP する。マウントしている mount.pgfs / pgfs.assign は事前に止めておくこと。

## Linux 固有事項

mkfs は基本的にクロスプラットフォームですが、以下の点は **Linux 上での実行を前提**にしています。

- **テーブルスペースのディレクトリ準備**: `CREATE TABLESPACE ... LOCATION '<path>'` は PostgreSQL サーバープロセス（通常 `postgres` ユーザー）が読み書きできるディレクトリでなければなりません。Linux では事前に以下を `sudo` で実施する必要があります。
  ```bash
  sudo mkdir -p /var/lib/pgfs
  sudo chown postgres:postgres /var/lib/pgfs
  sudo chmod 0700 /var/lib/pgfs
  ```
  この処理はマシン側の OS / 配置によって異なるため mkfs では自動化していません。macOS では `postgres` ユーザーの uid/gid が異なります。Windows では NTFS ACL を設定する必要があり、現在の mkfs は未対応です。
- **既定マウントポイント** は `/mnt/pgfs`（[`Schema.Mount.MountPoint`](../src/lib/src/Config/Schema.cs) の Linux/macOS 既定）。Windows では `P:`。
- **ルート inode の `st_mode = 16877 (0o40755)`** は POSIX のディレクトリ + `rwxr-xr-x` を表しています。Windows のジャンクションは `is_junction` 列で区別する設計（[docs/database.md](database.md)）。

これら以外（接続文字列、SQL クエリ、テーブル定義など）はクロスプラットフォームです。

## 暫定実装

| 項目 | 現行実装 | 理由 / 暫定 |
|---|---|---|
| 設定ファイル形式 | TOML (`pgfs.toml`) | 現行 Lib が Tomlyn を使用。INI 等は対応しない。 |
| 接続文字列の指定方法 | `-c <Npgsql connection string>` に統合 | 個別の `-h`/`-p`/`-U`/`-w` は持たない（`Schema.Database.*` に Field 追加で対応可）。 |
| パスワードプロンプト | 未実装 | 当面は接続文字列または `PGPASSWORD` 環境変数で渡す想定。 |
| `-o`（出力先設定ファイル） | 未実装。`-f` と兼用で `Setting.Path` に書き戻す | 暫定。専用オプションは後日。 |
| `-v`（詳細ログ） | 未実装（`--log-level debug` で代用） | 暫定。エイリアスは後日。 |
| AOT 発行 | `PublishAot=false` に設定 | Dapper / Tomlyn のリフレクション依存により AOT 発行は不可。全プロジェクトで明示無効化済み。 |

## 既知の制限・TODO

- **接続失敗時のリトライ機構**: 未実装。一発失敗で終了。要件には Polly での指数バックオフが書かれているが、mkfs では不要と判断し当面入れない。
- **`PGPASSWORD` 環境変数の利用**: 未実装。Npgsql の接続文字列に直接書く必要あり。
- **設定値のバリデーション**: 未実装。負の `cluster_size` や不正な `mount_point` も通過する。
- **権限不足のときのエラーメッセージ**: 単に Npgsql の例外がそのまま流れる。
- **Linux 以外の動作確認**: macOS / Windows での実機テストはしていない。Linux で動かしてから順次対応。

## 内部構造

[src/mkfs/src/](../src/mkfs/src/) は以下の 2 ファイル構成です。

- **[Program.cs](../src/mkfs/src/Program.cs)**: `ConfigLoader` で CLI / TOML / Default から `RootConfig` を組み立て（`--clean` 時は `skipToml: true`）→ `Initializer.InitializeAsync` → SaveTo=File の値を TOML に書き出し。
- **[Initializer.cs](../src/mkfs/src/Initializer.cs)**: 上記「役割」の各ステップを実装。すべて `Pg.ExecuteAsync` / `Pg.QueryAsync` 経由で SQL を発行。テーブル作成は `CreateTableAsync` ヘルパに集約。SaveTo=Db の値は `ConfigStore.Save<T>` で `pgfs_settings` に UPSERT。

設定モデルは [src/lib/src/Config/](../src/lib/src/Config/) の `RootConfig` / `Schema` をそのまま利用しています。mkfs 用の独自設定クラスは作りません。

## 参照

- [docs/database.md](database.md) DB スキーマ設計
