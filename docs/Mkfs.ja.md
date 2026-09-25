# mkfs.pgfs 仕様

> **道順**: [docs/README.ja.md](README.ja.md) › **本書**
>
> **この doc が正である範囲**: **`mkfs.pgfs` の仕様** — CLI オプション、**mkfs から指定できる設定項目の既定値表**、
> TOML の書式、DDL の適用フロー。**mkfs から指定できるものの既定値はここが正**で、Mount / Assign 側の抜粋表は本書へ委譲する。
>
> **全項目の網羅表は [design/settings-matrix.ja.md](design/settings-matrix.ja.md) が正**である。本書は
> **mkfs の CLI から意味のあるものだけ**を載せており、**マウント時にしか効かないもの**
> (`mount.max_write` / `mount.foreground` /
> `database.retry_*` / `database.notify_enabled` など) は**意図して載せていない**。
> **「ここに無い = 存在しない」ではない**ので、全項目を確かめるときは matrix を見ること
> (「全設定項目の既定値表」と書いてあったのを、実態に合わせて範囲を狭めた)。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [design/database.ja.md](design/database.ja.md) | DB スキーマ設計 (テーブルの意味) |
> | [ddl/README.ja.md](ddl/README.ja.md) | テーブル単位の DDL 本体 |
> | [design/settings-matrix.ja.md](design/settings-matrix.ja.md) | 全設定項目の**マトリクス** (保存先・reload ポリシー) |
> | [Mount.ja.md](Mount.ja.md) / [Assign.ja.md](Assign.ja.md) | マウント側の仕様。既定値は本書へ委譲 |
> | [design/support_for_citus.ja.md](design/support_for_citus.ja.md) | `--citus` / `--shard-count` / `--rf` の設計 |

PGFS ファイルシステムを PostgreSQL データベース上に初期化するツール `mkfs.pgfs` の仕様です。

このドキュメントは **現行実装 ([src/mkfs/](../src/mkfs/) — `Pgfs.Core.Config` ベース) で確定した仕様**をまとめたものです。実装上の暫定や未対応事項は「暫定実装」セクションに記載します。

## 役割

PostgreSQL データベースに対し、PGFS が必要とする以下を冪等に作成します。

1. PGFS 用ユーザー
2. テーブルスペース（指定された場合のみ）
3. データベース
4. スキーマ
5. テーブル: `{prefix}inode`, `{prefix}data`, `{prefix}data_chunk`, `{prefix}lock`, `{prefix}settings`, `{prefix}audit`, `{prefix}mounts` (**7 つ**) と、`--statfs` 指定時の `{prefix}statfs()` 関数
6. ルートディレクトリ inode（`id = 0`）
7. `SaveTo=DB` を持つ設定値の永続化（`{prefix}settings` への UPSERT）
8. 設定ファイル `pgfs.toml`（`SaveTo=File` を持つ設定の書き出し）

**テーブルなどの作成は冪等**で、既に存在する場合は `CREATE` をスキップします。**ただし設定は冪等では
ありません** — `--clean` を付けなくても、`{prefix}settings` に保存した設定 (`audit.enabled` /
`app.plperlu` / `app.statfs` / `database.citus` / tablespace / `file_system.*` / `fallback_*`) を
**その実行の CLI 指定か既定値で上書きし**、`pgfs.toml` も書き換えます (既存 DB の設定を読まないため)。
既存の FS に対して再実行するときは、**作成時と同じオプションを全部付ける**か、必要な DDL だけを
直接流してください ([CHANGELOG.ja.md](../CHANGELOG.ja.md) §既知の制限)。

## 実行方式

エントリポイントは [src/mkfs/src/Program.cs](../src/mkfs/src/Program.cs)。

```pwsh
# 開発ビルド (bin/Debug/mkfs.pgfs.{dll,exe})
dotnet build src/mkfs/Mkfs.csproj
# 開発時実行
dotnet run --project src/mkfs -- -f pgfs.toml [options]
# あるいはビルド済み実行ファイル
./bin/Debug/mkfs.pgfs -f pgfs.toml [options]

# 自己完結発行 (bin/Publish/mkfs.pgfs[.exe] — single-file, ホスト RID 自動)
dotnet publish src/mkfs/Mkfs.csproj -c Release
```

優先順位（後勝ち）: **既定値 < DB < 設定ファイル (TOML) < コマンドライン引数**。

実装上は [`ConfigLoader`](../src/core/src/Config/ConfigLoader.cs) が CLI → TOML → DB → Default の順に Phase を走らせて 1 度に統合します。`--clean` 時は `skipToml = true` で TOML を意図的に無視します。

## コマンドラインオプション

[src/core/src/Config/Schema.cs](../src/core/src/Config/Schema.cs) の各 `Field<T>` の `CliOptions` で定義されています。下表はその抜粋。

### 接続（PGFS ユーザー）

| オプション | 内容 | 既定 |
|---|---|---|
| `-c`, `--connection`, `--connection-string` | PGFS ユーザーで対象 DB に接続する文字列（Npgsql 形式） | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |

### 接続（スーパーユーザー）

| オプション | 内容 | 既定 |
|---|---|---|
| `--su`, `--super`, `--super-connection`, `--super-connection-string` (および `--super-user-...` 別名) | スーパーユーザーで maintenance DB (template1) に接続する文字列 | `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1;SslMode=Prefer` |

> **`--super` を明示しないときの接続先継承**: super 接続は DROP / CREATE DATABASE 等の破壊的操作に使う。`--super` を省略すると既定の `localhost` に化けるため、`--connection` をリモートに向けた場合に「別サーバ (localhost) の DB を DROP/CREATE してしまう」事故が起きうる。これを防ぐため、**`--super` を明示しないときは `--connection` の Host / Port / SslMode を super 接続へ継承**し、super と user が必ず同じサーバを向くようにしている (資格情報と maintenance DB は super 既定の postgres / template1 のまま)。super 資格情報がサーバごとに違う場合は `--super` を明示すれば、その値が完全に優先される。起動ログ冒頭の `接続先: user = ... , super = ...` 行と、続く `user connection:` / `super connection:` 行 (どちらも Password はマスク) で実際の宛先・接続文字列を確認できる。

接続文字列形式を 1 つで指定する設計です。個別の `--host`, `--port`, `--username`, `--password` は **当面は実装しません**。

> ⚠ **設定ファイル側にも個別指定はありません。** 以前ここには「設定ファイル側で個別に書ける `database.connection.host` 等を使ってください」と書いてあり、[pgfs.toml.example](../pgfs.toml.example) もその形でしたが、**実装は読めません** — 設定ファイルは **`スコープ.キー = 値` の 2 段しか解釈しない**ので、`database.connection.host = "..."` はテーブルとして扱われ、接続文字列に化けて `Format of the initialization string does not conform to specification` で落ちていました (2026-09-21 に実測・修正)。**接続は 1 行の接続文字列で書いてください** (kv 形式 / URL 形式のどちらでも可)。テーブル形式を書いた場合は**無視して警告**を出します。

### スキーマとテーブル

| オプション | 内容 | 既定 |
|---|---|---|
| `-s`, `--schema`, `--schema-name` | スキーマ名 | `public` |
| `-x`, `--prefix`, `--table-prefix`, `--table-name-prefix` | テーブル名プレフィックス。末尾 `_` がなければ自動付与 | `pgfs_` |
| `--tablespace`, `--tablespace-name` | テーブルスペース名 (Citus でもカスタム可。`CREATE DATABASE WITH TABLESPACE` 継承方式) | `pg_default` |
| `--tablespace-path` | 新規テーブルスペース作成時のディレクトリパス。`--allow-plperlu` (既定) なら mkfs が plperlu で dir を postgres 所有 0700 で自動作成 | （空） |
| `--citus` | テーブルを Citus 分散テーブルとして登録する。詳細は [docs/support_for_citus.ja.md](design/support_for_citus.ja.md) | `false` |
| `-w`, `--worker`, `--workers` | Citus worker ノードのカンマ区切り `host[:port]` (例: `--worker "w1:5432,w2:5432"`)。空なら 1 ノード構成 (coordinator のみ)。`--citus` と併用。 | （空） |
| `--shard-count` | Citus の `citus.shard_count` (分散テーブルの shard 数)。`0` はクラスタ既定に従う | `0` |
| `--shard-replication-factor`, `--rf` | shard 1 つを何ノードに置くか (= ストレージ冗長。`1` なら複製なし)。`0` はクラスタ既定に従う。排他制御は非分散の `{prefix}lock` に集約してあるので、この値はロック機構に影響しない | `0` |
| `--distribute-existing` | `--citus` 併用時、**既存**テーブルも Citus 化する (分散/metadata 登録)。既定は「新規作成したテーブルだけ」 | `false` |

### ファイルシステム設定 (DB 保管、全クライアント共通)

これらは fs 識別情報なので **`pgfs_settings` に保存** (SaveTo=Db) され、**生成 `pgfs.toml` には書き出しません**。
特に `cluster_size` / `default_chunk_size` はクライアント間で値がズレるとデータ破損しうるため DB 一元が正です
(File→Db 化。詳細 [settings-and-plperlu.ja.md](design/settings-and-plperlu.ja.md))。

| オプション | 内容 | 既定 |
|---|---|---|
| `--volume-label` | ボリュームラベル | `pgfs` |
| `--cluster-size` | クラスタサイズ（バイト） | `4096` |
| `--default-chunk-size` | bytea チャンクサイズ（バイト） | `1048576` (1 MiB) |
| `--max-file-size` | 最大ファイルサイズ（バイト、`-1` で無制限） | `1099511627776` (1 TiB) |
| `--fs-version` | ファイルシステムバージョン (v0.2.1 で `--version` から改名) | `1.0.0` |

**root ディレクトリの権限** (v0.2.1〜) は DB にも toml にも残さない、mkfs だけのアクション:

| オプション | 内容 | 既定 |
|---|---|---|
| `--root-access owner\|everyone` | root ディレクトリ (inode 0) を**新しく作るとき**の権限。`owner` = `root:root 0755` (Windows からは Administrators が書ける) / `everyone` = `root:root 1777` (誰でも書けて、消せるのは作った本人だけ = `/tmp` と同じ sticky。Windows からは Everyone が書ける)。**既に root があるときは変えず Warning を出す** (変えるならマウントして `sudo chmod`)。Windows は mode を強制しないので、sticky の効き目は Linux だけ | `owner` |

### 機能フラグ (DB 保管、全クライアント共通・作った後に変えるのは `pgfsctl config set`)

これらは `pgfs_settings` に保存され、mount / assign が起動時に DB から読みます (TOML には書きません)。

| オプション | 内容 | 既定 |
|---|---|---|
| `--audit` | メタデータ変更の監査ログを `{prefix}audit` に記録する。詳細は [docs/audit-log.ja.md](design/audit-log.ja.md) | `false` |
| `--statfs`, `--statfs-mode` | `df` (statfs) の実空き容量レポートのモード。`auto` (plperlu あれば実測 / 無ければ公称) / `require` (plperlu 必須、無ければ mkfs 失敗) / `nominal` (常に公称容量、関数を作らない)。保存キーは `app.statfs`。詳細は [docs/df-support.ja.md](design/df-support.ja.md) | `auto` |
| `--plperlu` `[true\|false]` / `--allow-plperlu` / `--deny-plperlu` | untrusted plperlu の使用許可 (上位ゲート、`app.plperlu`)。`require`+deny は矛盾でエラー、auto+deny は nominal 相当。tablespace auto-mkdir もこのゲート下。詳細 [settings-and-plperlu.ja.md](design/settings-and-plperlu.ja.md) | `true` (allow) |

### マウント設定

| オプション | 内容 | 既定 |
|---|---|---|
| `-m`, `--mount-point` | マウントポイント | Linux/macOS: `/mnt/pgfs` ／ Windows: `P:` |
| `--cache-max-entries` | inode メタキャッシュの件数上限 | `1024` |
| `--cache-data-max-bytes` | ファイル本体 (data_chunk) の read キャッシュのバイト予算（`0` で無効） | `67108864`（64MiB） |

### ロギング

| オプション | 内容 | 既定 |
|---|---|---|
| `--log-level`, `--log-min-level`, `--min-log-level` | 最低ログレベル | `Information` |
| `--log-output` | ログ出力先（`stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`。**`file` というリテラルは無い** — ファイル出力は `daily:~/pgfs/log/pgfs-*.log` のように cycle 付きで書く。cycle は `none`/`hourly`/`daily`/`monthly`） | `stderr` |

### 設定ファイル

| オプション | 内容 | 既定 |
|---|---|---|
| `-f`, `--setting`, `--setting-file` | **設定ファイルの場所 (mkfs では必須・v0.2.1〜)**。あれば読み込んで終了時にそこへ書き戻し、無ければ新規作成。`--clean` なら読まずに上書き | (必須) |
| `--setting-path`, `--setting-search-path`, `--setting-file-path`, `--setting-file-search-path` | 設定ファイル探索パス。**mkfs は使わない** (指定すると Warning。mount / assign / pgfsctl 用) | — |

### その他

| オプション | 内容 |
|---|---|
| `-?`, `-h`, `--help` | ヘルプを表示して終了 |
| `--version` | プログラムの版を表示して終了 (v0.2.1〜。mount / assign / pgfsctl も同じ) |
| `--clean` | 既存の `pgfs.toml` を無視し、`DROP DATABASE` してから再作成する。短縮形なし。テーブルスペースとロール（PGFS ユーザー）は破棄しないので、再作成しても所有者・テーブルスペースは維持される。**消す前に接続中のものと確認を取る** (下の §消す前の確認・v0.2.1〜) |
| `--purge` | **消して終わる** (v0.2.1〜)。`-f` の設定ファイルの接続先の DB を、Citus なら全 worker の同名 DB も `DROP DATABASE` する。**作り直さない / 設定ファイルも書かない**。`--clean` と同時には指定できない (exit 2)。下の §`--purge` |
| `--yes`, `-y` | `--clean` / `--purge` の**確認を省く** (スクリプト用)。**非対話 (stdin が端末でない) で無ければ何も消さずに exit 3** |
| `--now` | `--clean` / `--purge` のとき、**接続中のもの (生きているマウント / 他の接続) を待たずに切って進む**。無ければ、対話は「再試行しますか」を繰り返し、非対話は exit 3 |

## 設定ファイル (pgfs.toml)

### mkfs が書き出す配布用 toml に何が入るか (v0.2.1〜)

mkfs は終了時に、他のクライアントへ配るための `pgfs.toml` を書き出す。**入るのは次の 2 種類だけ**:

1. **接続の核** — `database.connection` (Password はそのまま・先頭コメントの mkfs コマンド行ではマスク) / `database.schema` / `database.prefix`。常に書く。
2. **CLI か読み込んだ TOML で明示された `SaveTo=File` の項目** — `--notify` / `--no-notify`、`-m` (mount_point)、`--write-back` などの mount / assign 向けの項目も含む。
   **mkfs 自身には効かない項目でも、明示すれば toml に残る** (`mkfs --help` の `[written to pgfs.toml for mount/assign]` が付いた行)。
   **`database.workers` だけは例外で書かない** (mkfs 専用で配布先には意味が無く、書くと後の `--clean` なしの mkfs が worker を勝手に登録するため)。

**既定値のままの項目は書かない**。書くと既定値が配布先に固定され、後の版で既定を変えても追従しないため
(例: v0.2.1 で `database.notify_enabled` の既定を true にしたが、v0.2.0 の mkfs が吐いた toml に `false` が入っていたら追従しない)。
**`mount.mount_point` も明示したときだけ書く** — Linux で吐いた toml を Windows に配っても書き換えずに済む (マウント先は各 OS の既定 `/mnt/pgfs` / `P:`)。

`SaveTo=Db` の項目 (tablespace / file_system のサイズ系 / audit / statfs / plperlu ほか) は DB が正なので toml には出ない。

**書き出し先は `-f` の場所だけ** (v0.2.1〜・必須)。v0.2.0 までは既定の探索パス (カレント → `~/.config/pgfs` → … → `LocalAppData/pgfs`) から toml を探し、
**見つかった場所に書き戻していた**ため、常駐マウント用の toml (例: `%LOCALAPPDATA%\pgfs\pgfs.toml`) を読んで上書きすることがあった。

### 書式

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
mount.cache_data_max_bytes = 67108864
mount.negative_cache_ttl_ms = 0
mount.write_back = false
mount.write_back_max_bytes = 67108864
mount.write_back_interval_ms = 1000
mount.write_back_metadata = false
mount.write_back_metadata_exclusive_create = "write_through"
mount.write_back_max_inodes = 4096
mount.write_back_flush_timeout_ms = 30000

# logging セクション
logging.level = "information"
```

## DB スキーマ詳細

[docs/database.ja.md](design/database.ja.md) を正として、mkfs が実際に作成するテーブルは以下のとおりです。

### `{prefix}inode`

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `id` | `BIGSERIAL` | NOT NULL | 複合 PK の一部 |
| `parent_id` | `BIGINT` | NOT NULL | UK1, IX1 |
| `name` | `TEXT` | NOT NULL | UK1 |
| `uname` | `TEXT` | NOT NULL | IX2 |
| `gname` | `TEXT` | NOT NULL | IX3 |
| `st_mode` | `INTEGER` | NOT NULL | |
| `st_nlink` | `INTEGER` | NOT NULL | `1` |
| `st_size` | `BIGINT` | NOT NULL | `0` |
| `st_mtime` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `st_ctime` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `link_target` | `TEXT` | NULL | |
| `is_junction` | `BOOLEAN` | NOT NULL | `FALSE` |
| `data_id` | `BIGINT` | NULL | |
| `xattr_names` | `TEXT[]` | NOT NULL | `'{}'::TEXT[]` |
| `xattr_values` | `BYTEA[]` | NOT NULL | `'{}'::BYTEA[]` |
| `created_at` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `created_by` | `TEXT` | NOT NULL | |
| `updated_at` | `TIMESTAMP` | NOT NULL | `(current_timestamp AT TIME ZONE 'UTC')` |
| `updated_by` | `TEXT` | NOT NULL | |

PK = `(parent_id, id)`、UK1 = `(parent_id, name)`、IX = `id` / `uname` / `gname`。

PK を `(parent_id, id)` の複合にしているのは Citus 制約への対応 (unique constraint には分散キー `parent_id` を含む必要がある)。id は BIGSERIAL でグローバル一意 (sequence は coordinator) なので、`(parent_id, id)` も実質 id だけで一意になる。`WHERE id = @id` 検索用に `id` 単独 INDEX を別途持つ。`parent_id` 単独 INDEX は PK の leftmost プレフィックスで代用できるため作成しない。

ルート inode は `id = 0, parent_id = 0, name = '/', st_mode = 16877 (=0o40755)` を `ON CONFLICT (parent_id, name) DO NOTHING` で挿入します (`(id)` 単独 UK は Citus 制約上作れないため `(parent_id, name)` UK ターゲットを使う)。

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

PK = `(data_id, chunk_index)`。旧設計では Large Object (`lo_oid OID` + `pg_largeobject`) を使っていたが、Citus 分散の前提として **Phase 1 で bytea に置換** (詳細は [docs/support_for_citus.ja.md](design/support_for_citus.ja.md))。

### `{prefix}lock`

Citus Phase 2 で先行作成。cross-client 排他制御の lock token テーブル。Phase 3 で Api の行ロックに接続済みである。

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `target_id` | `BIGINT` | NOT NULL | PK |

PK = `target_id` のみ。監査列なし (データではなく lock token なので)。

### `{prefix}settings`

フラットな `(scope, key)` PK に変更。[`ConfigStore.Save<T>`](../src/core/src/Config/ConfigStore.cs) がここに UPSERT し、[`ConfigStore.LoadAll`](../src/core/src/Config/ConfigStore.cs) がここから読み出します。

| カラム | 型 | NULL | DEFAULT |
|---|---|---|---|
| `scope` | `TEXT` | NOT NULL | PK |
| `key` | `TEXT` | NOT NULL | PK |
| `value` | `JSONB` | NOT NULL | `'null'::JSONB` |
| 監査用 4 列 | | NOT NULL | |

PK = `(scope, key)`。1 行 = 1 つの `Field<T>` の値で、`value` は JSONB ネイティブ表現 (string → `"..."` / 数値 → `42` / bool → `true`/`false`)。

## Citus 分散化 (`--citus` / `--worker`)

`--citus` を付けて mkfs すると Citus 拡張を有効化し、対象テーブルを次の方式で登録します (lock / settings / mounts は非分散):

| テーブル | 分散方式 | 分散キー | colocation |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | default group |
| `pgfs_data` | distributed | `id` | default group |
| `pgfs_data_chunk` | distributed | `data_id` | `pgfs_data` と co-located (1 ファイル = 1 shard) |
| `pgfs_lock` | local + metadata | — | 排他用の 1 コピーを coordinator に置く |
| `pgfs_audit` | distributed | `occurred_at` | 月次パーティション |
| `pgfs_mounts` | local + metadata | — | 実行中 mount/assign の登録表 |
| `pgfs_settings` | local + metadata | — | `citus_add_local_table_to_metadata` で metadata 登録だけ |

`--worker host[:port],...` を併用すると、その worker 上にも pgfs DB + Citus 拡張を bootstrap し、coordinator から `citus_add_node` で登録します。

shard 数と複製数は `--shard-count` / `--shard-replication-factor` (`--rf`) で指定できます (どちらも `0` = クラスタ既定に従う)。値は `create_distributed_table` を呼ぶセッションにだけ効くので、同じ DB を共有する他アプリの分散テーブルには影響しません。**複製数はストレージ冗長の選択**で、排他制御 (`{prefix}lock` = 非分散の citus local table) には影響しません。

既存スキーマに後から `--citus` を流しても、既定では**新規作成したテーブルだけ**が Citus 化されます。既存テーブルまで分散したいときは `--distribute-existing` を明示します (データを shard へ再配置する重い操作なので明示フラグのみ・冪等)。

### 使用例

```bash
# 1 ノード構成 (worker なし、coordinator が自分で shard を持つ)
mkfs.pgfs -f pgfs.toml --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres"

# 多ノード構成 (coordinator + worker1 + worker2)
mkfs.pgfs -f pgfs.toml --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

### 制約

- `--citus` × カスタム `--tablespace` は **両立可能**。`CREATE DATABASE WITH TABLESPACE` 継承方式で、tablespace は coordinator + 全 worker に作成される。LOCATION dir は `--allow-plperlu` (既定 allow) なら plperlu が postgres 所有 0700 で自動作成 (`--tablespace-path` 指定時)。詳細 [settings-and-plperlu.ja.md](design/settings-and-plperlu.ja.md)。
- **`--clean` で消す worker は DB の実体 (`pg_dist_node`) で決まる** (v0.2.1〜)。`--worker` を付けなくても、今の構成の worker 全部の同名 DB を消す (v0.2.0 までは `--worker` で指定した worker だけで、付け忘れると worker 側に DB が残った)。
  **worker に 1 つでも繋がらなければ何も消さずに止まる** (`pg_dist_node` のノード名は coordinator から見た名前なので、mkfs を動かすホストから届く名前である必要がある)。使っていない worker は先に coordinator で `citus_remove_node`。
  coordinator の DB が既に無いときだけ `--worker` を当てにする。
- 多ノード構成では worker 側の PG にも `shared_preload_libraries = 'citus'` が設定済みである必要あり。

### 動作詳細 / 検証

EnsureDatabaseAsync の Phase 構造 (worker bootstrap → coordinator DB → Citus topology) や「DB 既存時はすべての Citus 関連 mutate をスキップ」等の冪等性ルール、各種つまずきポイントは **[docs/support_for_citus.ja.md](design/support_for_citus.ja.md) を正とする**。検証スクリプトは [tests/citus/README.ja.md](../tests/citus/README.ja.md) (`multinode_probe.sh` / `test_matrix.sh` / `race_multinode.sh` / `verify.sql`)。

## 冪等性

すべての `CREATE` は事前に存在確認 SQL を発行してから実行します。

| 対象 | 存在確認 | 既存時の動作 |
|---|---|---|
| ユーザー | `pg_user.usename` | スキップ（パスワード変更はしない）。worker 側は DB を作るとき (`--clean` か DB が無い) だけ確保 |
| テーブルスペース | `pg_tablespace.spcname` | スキップ。**DB を作るときだけ**確保する (既存の DB には使われないため) |
| データベース | `pg_database.datname` | スキップ |
| スキーマ | `pg_namespace.nspname` | スキップ |
| テーブル | `pg_class + pg_namespace` | スキップ |
| ルート inode | `INSERT ... ON CONFLICT (parent_id, name) DO NOTHING` | 挿入されない |
| 設定行 | `pgfs_settings` に行があれば既存の FS | **FS 固有の設定は DB の値を採る** (v0.2.1〜・下の §既存の FS に打ち直したとき)。後の版で足した項目だけ増える |

### 既存の FS に打ち直したとき (v0.2.1〜)

設定は 3 つに分かれる: **クライアント固有** (toml — 接続先・`mount.*`・notify・ログ・リトライ) / **FS 固有** (DB — `audit.enabled`・`app.statfs`・`app.plperlu`・`file_system.*`) /
**作るときだけの指示** (どこにも残さない — `--clean`・`--root-access`・`--worker`・`--citus`・`--shard-count`・`--rf`・`--tablespace`・`--tablespace-path`)。

- **FS 固有の設定は FS を作るときに 1 回だけ決める**。`--clean` なしで既存の FS に打ち直すと DB の値を使い、CLI で違う値を渡しても変えずに Warning を出す
  (「既存の FS には効きません。変えるなら `pgfsctl config set`」)。v0.2.0 までは CLI / 既定値で黙って上書きしていた。
- **作るときだけの指示**: `--tablespace` / `--tablespace-path` / `--worker` は既存の DB には効かず Warning。**既存の DB のときは worker 側にも tablespace にも何も作らない**
  (v0.2.1。以前はユーザーと tablespace の確保が DB の存在チェックより前にあり、既存の DB でも `--worker` の各ノードにロールを作っていた)。
  **Citus かどうかは DB の実体** (`citus` 拡張の有無) で**どちら向きも**判定する — 既に Citus の DB なら `--citus` を付けなくても後から足すテーブルを分散し、
  **Citus でない DB に `--citus` を付けても Citus にはならない** (Warning。Citus にするなら `--clean` で作り直す。以前は新しい schema のテーブルを分散しようとして落ちた)。
  worker を足すのは coordinator で `citus_add_node`。statfs 関数が測る tablespace も DB の実体 (`pg_database`) から取る。
- `--distribute-existing` は既存 FS 向けの指示なので従来どおり効く。

### `--clean` での再作成

`--clean` を指定すると、データベース作成の前に **`DROP DATABASE`** を実行してから一連の `CREATE` を流します。

- **削除する**: 対象データベース（その中の `pgfs_*` テーブル / ルート inode / 設定行 / bytea チャンクもろとも）。Citus なら **`pg_dist_node` の全 worker の同名 DB も** (上の §Citus 分散化 の §制約)
- **削除しない**: テーブルスペース、PGFS ユーザー（ロール）、スーパーユーザー接続情報
- **設定ファイル**: 既存の `pgfs.toml` を **読み込まない**（「ファイルが無いもの」として起動）。CLI 引数だけが適用され、終了時に新しい `pgfs.toml` が書き出される。
- **他クライアント接続**: **数えて見せ、無くなるまで消さない** (v0.2.1〜・下の §消す前の確認)。v0.2.0 までは黙って `pg_terminate_backend` で切っていた。待たずに切るなら `--now`。

### `--purge` (消して終わる・v0.2.1〜)

`--clean` から「作り直す」を抜いたもの。**消す相手は `-f` の設定ファイルの接続先** (設定ファイルが無ければ exit 2。打ち間違えた接続文字列で別の FS を消さないように、実際に使っている toml を渡させる)。

- **削除する**: 対象データベース。Citus なら `pg_dist_node` の全 worker の同名 DB も (§Citus 分散化 の §制約)。
- **削除しない**: ロール、テーブルスペース、設定ファイル (`-f` の toml)。
- `--clean` と同時に指定すると、どちらの意図か分からないので何もせず exit 2。
- 消す前に、どこを消すかを `pgfsctl status -f <同じ toml>` の `target` / `citus` で確かめられる ([Pgfsctl.ja.md](Pgfsctl.ja.md))。

```bash
pgfsctl status -f /etc/pgfs.toml          # target : host:port/db と citus のノードを確かめる
mkfs.pgfs --purge -f /etc/pgfs.toml       # 接続中のもの → 確認 (y/N) → 消す
```

### 消す前の確認 (`--clean` / `--purge`・v0.2.1〜)

順番は **構成を DB から調べる → worker に届くか → 接続中のもの → 確認 → 消す**。**`--now` は「接続中のもの」だけ、`--yes` は「確認」だけを外す** (両方外すなら `--yes --now`)。

1. **接続中のもの**を数えて一覧で見せる:
   - **生きているマウント** — 対象 DB の全 schema の `*mounts` 表で heartbeat が 90 秒以内の行 (`pgfsctl status` の live と同じ)。host と mountpoint を出す。
   - **それ以外の接続** — `pg_stat_activity` の client backend (自分 = application_name `mkfs.pgfs` と、Citus の内部接続を除く)。Citus なら各 worker の同名 DB も。application_name ごとの件数を出す。

   | 指定 | 対話 (端末あり) | 非対話 |
   |---|---|---|
   | `--now` あり | 切って進む (消すときに `pg_terminate_backend`) | 同左 |
   | `--now` なし | **「再試行しますか (y/n)」を繰り返す**。y で数え直し、0 件で次へ。n で何もせず exit 3 | 何もせず exit 3 |

   再試行の間に、マウントを外したり (`umount` / `dokanctl /u`)、`pgfsctl status` で相手を確かめたりできる。
2. **確認**: 接続先 (`host:port/db`)・**DB の中の schema 全部** (`--clean` / `--purge` は DB ごと消すので、渡した schema 以外も消える)・Citus の worker を見せて「消しますか (y/N)」。
   `--yes` なら見せるだけ。**非対話で `--yes` が無ければ何も消さずに exit 3**。
3. 消す。

**終了コード**: 0 = 成功 / 1 = 失敗 / 2 = 引数の誤り (`--clean --purge` / `--purge` で設定ファイルが無い) / **3 = 中止 (何も消していない)**。

## Linux 固有事項

mkfs は基本的にクロスプラットフォームですが、以下の点は **Linux 上での実行を前提**にしています。

- **テーブルスペースのディレクトリ準備**: `CREATE TABLESPACE ... LOCATION '<path>'` は PostgreSQL サーバープロセス（通常 `postgres` ユーザー）が読み書きできるディレクトリでなければなりません。Linux では事前に以下を `sudo` で実施する必要があります。
  ```bash
  sudo mkdir -p /var/lib/pgfs
  sudo chown postgres:postgres /var/lib/pgfs
  sudo chmod 0700 /var/lib/pgfs
  ```
  この処理はマシン側の OS / 配置によって異なるため mkfs では自動化していません。macOS では `postgres` ユーザーの uid/gid が異なります。Windows では NTFS ACL を設定する必要があり、現在の mkfs は未対応です。
- **既定マウントポイント** は `/mnt/pgfs`（[`Schema.Mount.MountPoint`](../src/core/src/Config/Schema.cs) の Linux/macOS 既定）。Windows では `P:`。
- **ルート inode の `st_mode = 16877 (0o40755)`** は POSIX のディレクトリ + `rwxr-xr-x` を表しています。Windows のジャンクションは `is_junction` 列で区別する設計 ([docs/database.ja.md](design/database.ja.md))。

これら以外（接続文字列、SQL クエリ、テーブル定義など）はクロスプラットフォームです。

## 暫定実装

| 項目 | 現行実装 | 理由 / 暫定 |
|---|---|---|
| 設定ファイル形式 | TOML (`pgfs.toml`) | 現行 Lib が Tomlyn を使用。INI 等は対応しない。 |
| 接続文字列の指定方法 | `-c <Npgsql connection string>` に統合 | 個別の `-h`/`-p`/`-U`/`-w` は持たない (`Schema.Database.*` に Field 追加で対応可)。 |
| パスワードプロンプト | 未実装 | 当面は接続文字列または `PGPASSWORD` 環境変数で渡す想定。 |
| `-o` (出力先設定ファイル) | 未実装。`-f` と兼用で `Setting.Path` に書き戻す | 暫定。専用オプションは後日。 |
| `-v` (詳細ログ) | 未実装 (`--log-level debug` で代用) | 暫定。エイリアスは後日。 |
| AOT 発行 | `PublishAot=false` に設定 | Dapper / Tomlyn のリフレクション依存により AOT 発行は不可。全プロジェクトで明示無効化済み。 |

## 既知の制限・TODO

- **接続失敗時のリトライ機構**: 未実装。一発失敗で終了。要件には Polly での指数バックオフが書かれているが、mkfs では不要と判断し当面入れない。
- **`PGPASSWORD` 環境変数の利用**: 未実装。Npgsql の接続文字列に直接書く必要あり。
- **設定値のバリデーション**: 未実装。負の `cluster_size` や不正な `mount_point` も通過する。
- **権限不足のときのエラーメッセージ**: 単に Npgsql の例外がそのまま流れる。プレーンな日本語メッセージへの翻訳は未実装。
- **Linux 以外の動作確認**: macOS / Windows での実機テストはしていない。Linux で動かしてから順次対応。

## 内部構造

[src/mkfs/src/](../src/mkfs/src/) は以下の 2 ファイル構成です。

- **[Program.cs](../src/mkfs/src/Program.cs)**: `ConfigLoader` で CLI / TOML / Default から `RootConfig` を組み立て (`--clean` 時は `skipToml: true`) → `Initializer.InitializeAsync` → SaveTo=File の値を TOML に書き出し。
- **[Initializer.cs](../src/mkfs/src/Initializer.cs)**: 上記「役割」の各ステップを実装。すべて `Pg.ExecuteAsync` / `Pg.QueryAsync` 経由で SQL を発行。テーブル作成は `CreateTableAsync` ヘルパに集約。SaveTo=Db の値は `ConfigStore.Save<T>` で `pgfs_settings` に UPSERT。

設定モデルは [src/core/src/Config/](../src/core/src/Config/) の `RootConfig` / `Schema` をそのまま利用しています。mkfs 用の独自設定クラスは作りません。
