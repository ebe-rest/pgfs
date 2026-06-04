# 設定項目マトリックス

> 各設定項目の真の宣言は [src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs) に集約 (`Schema.<Scope>.<Key>` の static `Field<T>` 記述子)。本ドキュメントは「CLI フラグ / TOML キー / DB 保存先 / 既定値 / 参照タイミング」を一覧で横串に見るためのもの。**コードと食い違ったら Schema が正**。将来は Schema 由来の自動生成に置き換える案あり。

関連: [docs/Mkfs.ja.md](Mkfs.ja.md) (mkfs.pgfs の CLI 一覧) / [docs/Mount.ja.md](Mount.ja.md) / [docs/Assign.ja.md](Assign.ja.md) / [docs/fstab-support.ja.md](fstab-support.ja.md) (`-o key=val,...` 経由のオプション)

---

## 凡例

- **キー**: `pgfs_settings` テーブル / TOML での階層キー (`scope.key`)。コードの `Field.Scope` / `Field.Key` と一致
- **CLI**: 受け付ける引数。複数あれば最も一般的なものを太字
- **TOML**: `pgfs.toml` から読む (= `SaveTo = File`)
- **DB**: `pgfs_settings(scope, key, value)` 行として永続化される (= `SaveTo = Db`)
- **既定**: `Field.DefaultFn()` の返り値
- **型**: 値の C# 型 (`Field<T>` の T)
- **参照タイミング**: いつコード側が `RootConfig` から読むか (起動時 / mkfs 時 / 動的)
- **備考**: 制約、依存関係、既知の罠

## 優先順位

[`ConfigLoader`](../src/lib/src/Config/ConfigLoader.cs) は 1 度に CLI / TOML / DB / Default を統合する。優先順位は CLI > TOML > DB > Default。読み込みは `CLI → TOML → DB` の順に走り、「上位ソースが既に値を入れていれば skip」方式で実現する。`AssignPositional` / `ParseDashOOptions` も「未読込時のみ反映」なので、明示 `-c` / `-m` / `-f` が positional より優先される。

---

## 1. ルート (`-` / `--` 直書き、親なし)

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `help` | `-?` `-h` **`--help`** | ❌ | ❌ | false | bool | CLI parse 直後 | 使い方表示 → 即終了 |
| `clean` | **`--clean`** | ❌ | ❌ | false | bool | mkfs `Initializer.cs` の冒頭 | DROP DATABASE → 再作成。短縮形を意図的に持たせていない (誤実行防止) |

## 2. `setting.*` — 設定ファイル自身の置き場所

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `setting.file` | **`-f`** `--setting` `--setting-file` + **positional[0] (postgresql: 以外)** | ❌ | ❌ | `pgfs.toml` | string | `LoadFromFile` 開始時 | positional[0] が `postgresql:` で始まらないときも流れ込む。短縮形 `-f` は **直接実行時のみ有効** (helper context では `mount(8)` 由来の `--fake` として silent 飲み込み — [docs/fstab-support.ja.md §短縮形の衝突](fstab-support.ja.md#短縮形の衝突)) |
| `setting.search_path` | `--setting-path` `--setting-search-path` `--setting-file-path` `--setting-file-search-path` | ❌ | ❌ | `.` / `$HOME/.config/pgfs` / `$HOME/.config` / `$HOME` / `$LOCALAPPDATA/pgfs` / `$APPDATA/pgfs` | List&lt;string&gt; | `LoadFromFile` で `setting.file` が絶対パスでない場合の探索 | OS 標準ディレクトリ群 |

## 3. `logging.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `logging.level` | `--log-level` `--log-min-level` `--min-log-level` | ✅ | ❌ | `Information` | `Level.Enum` | 起動時 `Logger.MinLevel` に反映 | `all` `trace` `debug` `information` `warning` `error` `critical` `none` |
| `logging.output` | `--log-output` | ✅ | ❌ | `stderr` | `SettingLoggingOutput` | 起動時 [LogSink.Configure](../src/lib/src/Logging/LogSink.cs) で `Logger.Output` に反映 | `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`。cycle = `none\|hourly\|daily\|monthly`、pattern 中の `*` が日付に展開 (hourly=`yyyyMMddHH` / daily=`yyyyMMdd` / monthly=`yyyyMM` / none=空)、`~` はホーム展開。例 `daily:~/pgfs/log/pgfs-*.log`。**Warning 以上は実効シンクが stderr でない限り stderr にも terse 形式 (`pgfs: [Warning] ...`、日時なし) で出る** (file/stdout sink でも警告を見落とさないため)。同様に **起動バナー (プログラム名 + バージョン + Copyright) とプロセス生死マーカー (`started`/`exited`) は常に stderr へ** (`Logger.Lifecycle`、ログがファイルでも起動/終了を追える)。起動時に解釈済みパラメータ (`param: scope.key = value`、接続文字列の Password はマスク) と未知オプション警告も出力 |

## 4. `mount.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `mount.mount_point` | **`-m`** `--mount-point` + **positional[1]** | ✅ | ❌ | `/mnt/pgfs` (Linux/macOS) / `P:` (Windows) | string | `mount.pgfs Program.Main` の FUSE ループ突入直前、`assign.pgfs` の DokanNet 起動時 | |
| `mount.cache_max_entries` | `--cache-max-entries` | ✅ | ❌ | 1024 | int | `InodeCache` 初期化 (3 つの LRU の `capacity`) | パスキャッシュ / id キャッシュ / 子供キャッシュで共有 |
| `mount.fallback_uname` | `--fallback-uname` | ❌ | ✅ | `nobody` | string | `UserResolver` / `WindowsUserResolver` 構築 | OS で uname が解決できないときの逃げ先。FS 全体で 1 値 (= DB 保管) |
| `mount.fallback_gname` | `--fallback-gname` | ❌ | ✅ | `nogroup` | string | 同上 | |
| `mount.foreground` | **`--foreground`** | ❌ | ❌ | false | bool | `mount.pgfs Program.Main` の子プロセス分離判定 | true で前景固定、false でデーモン化。短縮形 `-f` は [setting.file と mount helper の no-value フラグとの衝突](#既知の問題) のため意図的に持たない |

## 5. `file_system.*` — mkfs 時に凍結、以後 readonly

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `file_system.version` | `--version` | ❌ | ✅ | `1.0.0` | string | (現状コード上の参照なし) | mkfs 時凍結用。将来の互換性チェック余地 |
| `file_system.volume_label` | `--volume-label` | ❌ | ✅ | `pgfs` | string | `assign.pgfs` の `GetVolumeInformation` | Windows のドライブ名 |
| `file_system.cluster_size` | `--cluster-size` | ❌ | ✅ | 4096 | long | `Api.StatFs` (`f_bsize`) | バイト単位。**DB 権威** (SaveTo=Db)。クライアント間で値がズレると事故るため。生成 toml には書かない |
| `file_system.default_chunk_size` | `--default-chunk-size` | ❌ | ✅ | 1048576 (1 MiB) | long | (現状 chunk_size は pgfs_data の `chunk_size` 列を真とするため、設定値は新規 data 行作成時のみ初期値として使用) | **DB 権威**。値ズレは chunk 境界解釈不一致でデータ破損しうる |
| `file_system.max_file_size` | `--max-file-size` | ❌ | ✅ | 1099511627776 (1 TiB) | long | `Api.StatFs` (`f_blocks`) ほか | **DB 権威** |

## 6. `database.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `database.connection` | **`-c`** `--connection` `--connection-string` + **positional[0] (postgresql: で始まる)** | ✅ | ❌ | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` | `DatabaseConnectionSetting` (`NpgsqlConnectionStringBuilder` ラッパ) | 全プロセスの DB アクセス | mkfs / mount / assign すべてで必須 |
| `database.super_connection` | `-su` `--su` `--super` `--super-connection` `--super-connection-string` `--super-user` `--super-user-connection` `--super-user-connection-string` | ❌ | ❌ | `Host=localhost;...Username=postgres;Password=postgres;Database=template1;...` | 同上 | mkfs `Initializer` の DB / ROLE / EXTENSION 作成 | `SaveTo=None` でファイルにも DB にも書かない (資格情報の安全)。**`--super` を明示しないときは `database.connection` の Host/Port/SslMode を継承**し、super と user が同じサーバを向くようにする (user をリモートに向けたのに super が localhost の別 DB を DROP/CREATE する事故を防ぐ)。super 資格情報・maintenance DB は既定のまま (postgres / template1)。明示時はその値を完全に尊重 |
| `database.schema` | **`-s`** `--schema` `--schema-name` | ✅ | ❌ | `public` | string | `Api` の全 SQL 修飾 | 短縮形 `-s` は **直接実行時のみ有効** (helper context では `mount(8)` 由来の `--sloppy` として silent 飲み込み — [docs/fstab-support.ja.md §短縮形の衝突](fstab-support.ja.md#短縮形の衝突)) |
| `database.prefix` | **`-x`** `--prefix` `--table-prefix` `--table-name-prefix` | ✅ | ❌ | `pgfs_` | string | テーブル名生成 (`pgfs_inode` 等) | `Database.GetPrefix()` 経由で末尾 `_` を正規化 |
| `database.tablespace` | `--tablespace` `--tablespace-name` | ❌ | ✅ | `pg_default` | string | mkfs `Initializer.EnsureTablespaceAsync` (coordinator + 全 worker) | **Citus でもカスタム可**。per-table 句を廃し `CREATE DATABASE WITH TABLESPACE` 継承。**DB 権威** (設定ファイルで書き換えさせない fs 識別情報。生成 toml 非出力) |
| `database.tablespace_path` | `--tablespace-path` | ❌ | ✅ | `""` | string | mkfs `Initializer.EnsureTablespaceAsync` | 空文字なら新規作成しない。指定時、`app.plperlu` 許可なら plperlu auto-mkdir (postgres 所有 0700)。**DB 権威** |
| `database.retry_max_attempts` | `--retry-max-attempts` | ✅ | ❌ | 5 | int | `Api` ctor の `Retry.Configure` | 接続オープン時のみ再試行 (クエリ実行中は対象外) |
| `database.retry_initial_delay_ms` | `--retry-initial-delay-ms` | ✅ | ❌ | 200 | int | 同上 | 指数バックオフの初期値 |
| `database.retry_max_delay_ms` | `--retry-max-delay-ms` | ✅ | ❌ | 2000 | int | 同上 | バックオフ上限 |
| `database.notify_enabled` | `--notify` `--notify-enabled` | ✅ | ❌ | `false` | bool | `Api` ctor で `NotifyChannel` を起動 | 他クライアント変更通知 (LISTEN/NOTIFY)。1 クライアント運用では OFF 推奨。詳細 [docs/Mount.ja.md](Mount.ja.md) / [docs/Assign.ja.md](Assign.ja.md) |
| `database.citus` | **`--citus`** | ❌ | ✅ | `false` | bool | mkfs `Initializer` が PGFS テーブルを Citus 分散登録するか | **DB 権威** (SaveTo=Db)。「この FS は Citus 化済み」を後追い確認する bool。mount/assign は条件分岐に使わない。詳細 [docs/support_for_citus.ja.md](support_for_citus.ja.md) |

## 8. `audit.*` — 監査ログ

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `audit.enabled` | **`--audit`** | ❌ | ✅ | `false` | bool | mkfs が `pgfs_settings` に保存 → `Api` ctor で読み、各 mutating 操作のフックを有効化 | メタデータ変更を `{prefix}audit` に記録。`mount.fallback_*` と同じ「mkfs で凍結、DB 保管」の項目。詳細 [docs/audit-log.ja.md](audit-log.ja.md) |

## 9. `app.*` — アプリ挙動 (df モード / plperlu ゲート)

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `app.statfs` | **`--statfs`** `--statfs-mode` | ❌ | ✅ | `auto` | string (`auto`/`require`/`nominal`) | mkfs が `{prefix}statfs()` (plperlu) を作る/消す分岐に使用 + `pgfs_settings` に保存。`Api.GetStatFs` は `nominal` のときサーバ問い合わせをスキップ | C# 参照は `Schema.Statfs.Mode`。`df` が実ディスク空きを返すモード。`auto`=plperlu あれば実測/無ければ公称、`require`=plperlu 必須(無ければ mkfs 失敗)、`nominal`=常に公称容量。plperlu 使用可否は `app.plperlu` が上位ゲート。詳細 [docs/df-support.ja.md](df-support.ja.md) / [docs/settings-and-plperlu.ja.md](settings-and-plperlu.ja.md) |
| `app.plperlu` | **`--plperlu [true\|false]`** `--allow-plperlu` (bare) / `--deny-plperlu` (bare, 否定) | ❌ | ✅ | `true` | bool | mkfs が statfs 実測関数 / tablespace auto-mkdir に plperlu を使ってよいかの上位ゲート | untrusted plperlu の許可。`require`+`deny` は矛盾で mkfs エラー。auto+deny は nominal 相当。matrix は [docs/settings-and-plperlu.ja.md](settings-and-plperlu.ja.md) |

---

## ライフサイクル別の集約

### 「mkfs 時に凍結される設定」(= ファイルシステム作成時点で固定)

- `file_system.version`
- `file_system.volume_label`
- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`
- `database.tablespace` / `database.tablespace_path` / `database.prefix` / `database.schema`
- `audit.enabled` (DB 保管、`--audit`。将来は管理ツールで切り替え予定)
- `app.statfs` (DB 保管、`--statfs`。`{prefix}statfs()` 関数の有無を mkfs で凍結)
- `database.citus` (DB 保管、`--citus`)
- `app.plperlu` (DB 保管、`--plperlu`/`--allow-plperlu`/`--deny-plperlu`。plperlu 許可ゲート)
- **DB 権威の FS サイズ系**: `file_system.cluster_size` / `default_chunk_size` / `max_file_size` も DB 保管 (上記 §5 参照、生成 toml には書かない)

これらは mkfs 後に書き換えても、後続のマウントが従う保証はない (`pgfs_data.chunk_size` のように DB 側の列が真になっている場合は特に)。

### 「マウント起動時に読み、以降は不変」

- `mount.mount_point`
- `mount.cache_max_entries`
- `mount.fallback_uname` / `mount.fallback_gname`
- `mount.foreground`
- `logging.level` / `logging.output`
- `database.connection`
- `database.retry_*`

→ **現状のコード上、マウント中に書き換える設定は存在しない**。これが設定モデルを immutable POCO で済むと判断した論拠。

### 「mkfs 時のみ参照、永続化しない」

- `clean` (CLI)
- `database.super_connection` (mkfs 時のみ。資格情報を保存しない)
- `help`

---

## 既知の問題

### `-f` / `-s` の helper context での silent 飲み込み

`mount(8)` helper の内部フラグ (`-i` `-f` `-n` `-s` `-v` `-N <ns>` `-t <type>`) は **helper context で起動された場合のみ** `ParseArguments` の最初で silent に読み飛ばされる (context 依存)。判定は「親プロセス comm が `mount` かつ positional 引数あり」の AND 条件。直接実行時はこの飲み込みが効かず、`-f` は `setting.file` の、`-s` は `database.schema` の短縮形として有効。

詳細 (全短縮形の一覧・判定ロジック・処理順) は [docs/fstab-support.ja.md §短縮形の衝突](fstab-support.ja.md#短縮形の衝突) と [§ConfigLoader.ParseCli の処理順](fstab-support.ja.md#configloaderparsecli-の処理順) を参照。

`mount.foreground` は `-f` を Options から外したので衝突対象外 (`--foreground` のみ)。`-f` は **setting.file 専用**の短縮形。

### `database.super_connection` 値の永続化

`SaveTo=None` のため mkfs 後は破棄され、再 mkfs 時に再度指定が要る。これは意図的 (資格情報の安全)。

### `file_system.default_chunk_size` の使われ方

`Api` の chunk 操作は `pgfs_data.chunk_size` 列 (= 各データ行に保存された値) を真とする。設定値は新規 `data` 行を INSERT する際の初期値として使うのみで、既存ファイルには影響しない。
