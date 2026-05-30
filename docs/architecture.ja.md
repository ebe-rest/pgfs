# アーキテクチャ

pgfs のソリューション構成・依存パッケージ・Lib 内部のファイル構成・ビルド/実行手順をまとめたドキュメント。

## ソリューション構成

ソリューション [pgfs.sln](../pgfs.sln) に 4 つのプロジェクトがあります。

| プロジェクト | パス | 役割 | プラットフォーム |
|---|---|---|---|
| **Lib** | [src/lib/](../src/lib/) | コアライブラリ（Models / Api / Logging / Collections / Utility / Objects） | クロスプラットフォーム |
| **Mkfs** | [src/mkfs/](../src/mkfs/) | PostgreSQL 側のテーブル等を初期化する CLI | クロスプラットフォーム |
| **Mount** | [src/mount/](../src/mount/) | Linux / macOS 用マウントツール（Tmds.Fuse 使用） | Linux / macOS |
| **Assign** | [src/assign/](../src/assign/) | Windows 用マウントツール（DokanNet 使用） | Windows |

すべての TargetFramework は **net10.0**。`PublishAot` / `PublishTrimmed` が有効（Lib を除く）。`ImplicitUsings` と `Nullable` も有効。OS 別の `DefineConstants` (`WINDOWS` / `LINUX` / `MACOS`) が定義されます。

### 名前空間

- ルート: `Pgfs.*`
- ライブラリ: `Pgfs.Lib.{Api, Models, Logging, Collections, Objects, Utility}`
- 実行ファイル: `Pgfs.Mkfs`, `Pgfs.Mount`, `Pgfs.Assign`

### 出力先

すべてのプロジェクトは [bin/](../bin/) 配下の同一ディレクトリにビルド出力されます（`<BaseOutputPath>$(SolutionDir)bin\</BaseOutputPath>`）。

### アセンブリ名

namespace は `Pgfs.Lib`, `Pgfs.Mkfs` 等の PascalCase。出力アセンブリ名は **lowercase + ドット区切り** に統一: `lib.pgfs.dll`, `mkfs.pgfs.{dll,exe}`, `mount.pgfs.{dll,exe}`, `assign.pgfs.{dll,exe}`。これは `<AssemblyName>` で csproj に設定。

---

## 依存パッケージ

| パッケージ | 用途 | 使用先 |
|---|---|---|
| `Npgsql` 9.0.4 | PostgreSQL クライアント | 全プロジェクト |
| `Dapper` 2.1.66 | 軽量 ORM | Lib |
| `Tomlyn` 0.20.0 | TOML 設定ファイル | Lib |
| `Tmds.Fuse` (fork) | Linux/macOS FUSE | Mount |
| `DokanNet` 2.3.0.3 | Windows DokanNet | Assign |

`Tmds.Fuse` は本家 `tmds/Tmds.Fuse 0.1.0-190711-50` ではなく **`securefolderfs-community/Tmds.Fuse` フォーク** を [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) に submodule で持って `ProjectReference` 参照する形にしている (`MountOptions.Options` 追加と `use_ino` downstream パッチのため)。経緯は [fstab-support.ja.md §Tmds.Fuse のフォーク採用](fstab-support.ja.md) 参照。

---

## ファイル構成（Lib）

### Models（[src/lib/src/Models/](../src/lib/src/Models/)）

DB の 1 行に対応するエンティティ POCO を置く場所。設定モデル系は [Config](#config-srclibsrcconfig) にある。

**DB エンティティ系**: `Base.cs`, `Inode.cs`, `Data.cs`, `Chunk.cs`
- 監査用フィールド (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`) を `Base` で共通化 (Dapper 用に小文字プロパティと大文字プロパティの両方を公開)
- `Inode.cs` / `Data.cs` / `Chunk.cs` がそれぞれ `pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` の 1 行を表す POCO

**LoggingOutput 関連の Enum 群**: `SettingLoggingKind.cs` (Flags: None/Stderr/Stdout/File), `SettingLoggingCycle.cs` (None/Hourly/Daily/Monthly), `SettingLoggingOutput.cs` (上 2 つを束ねた POCO)
- [`LoggingOutputField`](../src/lib/src/Config/Field.cs) が型として参照するため Models に残置 (移動先候補としては `Pgfs.Lib.Logging` だが、本ファイルは「log のフォーマット記述」であって「log 出力本体」ではないので現状の置き場が無難)

### Config（[src/lib/src/Config/](../src/lib/src/Config/)）

旧 `Settings` ツリーの後継。**静的 `Field<T>` 記述子 + mutable POCO + `ConfigLoader` (CLI/TOML/DB/Default 統合) + `ConfigStore` (DB I/O)** で構成。

- **`Field<T>` (抽象基底 + 派生型)**: 設定 1 つあたりの記述子。Scope / Key / CliOptions / DashOName / SaveTo (`None`/`File`/`Db`) / DefaultFn / Comment を持ち、型ごとに `Parse(string) → T` / `Format(T) → string` を持つ。派生型: `StringField` / `IntField` / `LongField` / `BoolField` / `StringListField` / `LogLevelField` / `LoggingOutputField` / `ConnectionField`。
- **`Schema` (静的クラス)**: `Schema.Mount.MountPoint` のように nested static class で全 `Field<T>` を宣言。`Schema.AllFields` は reflection で全フィールドを自動列挙 (新規 Field を足したら自動で含まれる)。
- **POCO**: `MountConfig` / `DatabaseConfig` / `FileSystemConfig` / `LoggingConfig` / `SettingFileConfig`。プロパティは `{ get; set; }` (将来 volume_label の動的書き換え対応に備え mutable)。集約 `RootConfig` が全部まとめる + `Help` / `Clean` を直接持つ。
- **`ConfigLoader`**: 1 度に CLI / TOML / DB / Default を統合して `RootConfig` を組み立てる。順序は **CLI → TOML → DB** で「上位が既に入れていれば skip」(priority `CLI > TOML > DB > Default`)。TOML パスは CLI 解決済みの `setting.file` から内部で探索。`mount(8)` helper context (親 comm = `mount` AND positional あり) のときだけ `-i -f -n -s -v -N -t` を silent 飲み込み。
- **`ConfigStore`**: フラット `pgfs_settings(scope, key, value)` に対する `(scope, key) → 値` の DB 読み書き。`LoadAll` は単純な `SELECT scope, key, value FROM table` の全件読み + 許可リストフィルタ。`Save<T>` は `INSERT ... ON CONFLICT (scope, key) DO UPDATE` で UPSERT。JSON 表現は `Field<T>.FormatJson(value)` 経由 (Int/Long/Bool はネイティブ JSON、それ以外は `Format(value)` を JSON 文字列としてエンコード)。

変更時の永続化規約: 呼び側が `config.X.Y = newValue;` した直後に `store.Save(Schema.X.Y, newValue)` を明示的に呼ぶ (setter フックは入れていない、Loader/Store 分離のため)。具体例は将来 `Api.SetVolumeLabel(string)` 等の薄いラッパに集約予定。

### Api（[src/lib/src/Api/](../src/lib/src/Api/)）

- `Api.cs` ファイルシステム操作の公開 API（inode CRUD、データ I/O via bytea チャンク、xattr、symlink、hard link、ボリューム情報すべて実装済み）。`IDisposable`、`OsBridge` プロパティで OS 通知ブリッジを受ける。
- **cross-client 排他制御**: `LockTargets` / `LockData(dataId)` / `LockInode(inodeId)` / `LockInodes(params long[])` のプライベートヘルパで `pgfs_lock` 上に `SELECT ... FOR UPDATE` 行ロックを取る。各 mutating メソッド (`WriteData` / `TruncateData` / `ReleaseData` / `Update{Mode,Owner,Size,Timestamps}` / `Rename` / `DeleteInode` / `CreateHardLink`) の冒頭で適切なロックを取り、tx 終了で自動解放。複数 lock 取得は target_id 昇順固定でデッドロック回避。詳細は [support_for_citus.ja.md §排他制御](support_for_citus.ja.md)。
- `InodeCache.cs` inode のメモリキャッシュ + Dapper 経由の SELECT/INSERT。`byId` / `byPath` の 2 経路で検索可能。ルートは `id = 0` で固定。`TryGetPath(id, out path)` で parent チェーン walk による full path 解決を提供 (Notify 経路で使用)。
- `Mode.cs` POSIX `st_mode` 定数（S_IFDIR, S_IRWXU 等）
- `NotifyChannel.cs` / `RemoteChangeInfo.cs` 他クライアント変更通知 (`database.notify_enabled=true` 時のみ作動)。PostgreSQL `LISTEN` / `NOTIFY` 経由で書き込みクライアントから受信側へ `{inode_ids, parent_ids, path_prefixes}` を流す。受信側は `InodeCache` を invalidate し、Assign では `DokanInstance.NotifyUpdate` でさらに Explorer に再描画依頼。Mount (Linux) は Tmds.Fuse 高レベル API の制約で OS ブリッジ未実装 (`attr_timeout=0` で kernel attr cache 無効化 + InodeCache invalidate のみで stat/ls は最新を返す)。

**データ I/O (bytea チャンク) の実装メモ**: 各 inode のデータ本体は `pgfs_data` 1 行 + `pgfs_data_chunk` 複数行 (1 行 = 1 bytea = 1 チャンク、デフォルト chunk_size = 1MB)。WriteData は 1 SQL/チャンクの upsert (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`、CASE で「中央 overlay / 末尾上書き / 0 パディング + 連結」の 3 ケース) で完結、並行 WriteFile race は PG の行ロックで自動直列化。ReadData は `substring(payload from N for M)` で PG 13+ の partial TOAST detoast を活用。各チャンクの payload 長は「これまで書き込まれたバイト数」と等しい。0 パディングは `decode(repeat('00', N), 'hex')` (`repeat(bytea, integer)` は PG に存在しないため)。

### Logging（[src/lib/src/Logging/](../src/lib/src/Logging/)）

- 自前のロガー実装（`Microsoft.Extensions.Logging` ではなく独自）。
- `Logger.Default` 経由の静的 API。`Level.Enum` は `All/Trace/Debug/Information/Warning/Error/Critical/None`。出力先は `Logger.Output` (`Action<string>`)、最低レベルは `Logger.MinLevel`。
- **出力先の設定反映**: 各 Program.cs が起動時に `Logger.MinLevel = config.Logging.MinLevel` と `Logger.Output = LogSink.Create(config.Logging.Output)` を設定する。`logging.output` が `stdout`/`stderr`/`none` ならそれぞれの sink、`<cycle>:<dir>/<pattern>` 形なら [RotatingFileSink](../src/lib/src/Logging/RotatingFileSink.cs) (日付ローテーション + `~` ホーム展開 + `*`→日付スタンプ + ディレクトリ自動作成 + AutoFlush)。設定なしの既定は `stderr`。変換は [LogSink.Create](../src/lib/src/Logging/LogSink.cs)。
- **ホットパスではガード句必須**: `Logger.Trace(...)` は `params object?[]` で配列確保とボクシングが発生する。秒間 1000+ 呼ばれる箇所では必ず `if (Logger.IsTraceEnabled) { Logger.Trace(...); }` のように先に判定する。`IsTraceEnabled` / `IsDebugEnabled` / `IsEnabled(level)` を用意済み。

### Utility（[src/lib/src/Utility/](../src/lib/src/Utility/)）

- **`PathParser.cs`** パスをドライブ / ルート / 名前要素に分解し、別の区切り文字での再構築・ワイルドカード位置検出・前後への挿入が可能。`Lazy<>` で各部品を遅延評価。比較的しっかり書けているコンポーネント。**注意**: `PathParser.FromPath(path)` は OS デフォルトのセパレータ (`Path.DirectorySeparatorChar`) を使う。`Api` / `InodeCache` に流れるパスは常に `/` 区切りに正規化されているので、Lib 内部で呼び出すときは必ず `PathParser.FromPath(path, "/")` と明示すること。
- **`Pg.cs`** Dapper + Npgsql のラッパ（`Query`, `QueryAsync`, `Execute`, `ExecuteAsync`）。`NpgsqlDataSource` を接続文字列ごとにキャッシュ (同じ接続文字列なら同じデータソースが返る。プールはデータソース内部で管理される)。`QuoteIdentifier` / `QuoteLiteral` でエスケープ。SQL は `Logger.Trace` に出る（`TraceQuery` 内で `Logger.IsTraceEnabled` の早期 return ガード済み、Trace 無効時は `Regex.Replace` / `JsonSerializer.Serialize` をスキップ）。`Pg.OpenConnection` + `using var tx = conn.BeginTransaction()` パターンと、`Pg.WithTransaction<T>` ヘルパも提供 (`Span<byte>` を扱うときは ref struct なので lambda にできず、直接 `OpenConnection` を使う)。
- **`ServiceResolver.cs`** `/etc/services` または Win32 `getservbyname` でサービス名 ↔ ポート変換。
- `Indexer.cs` (`ReadOnlyIndexer<,>` / `ReadOnlyIndexer<,,>` を `PathParser`, `Pg` で使用), `Fn.cs`, `String.cs`, `Json.cs` 各種ヘルパ。

### Collections（[src/lib/src/Collections/](../src/lib/src/Collections/)）

- `RichDictionary<K,V>` `Added` イベント・`GetOrAdd<W>` 派生型サポート付きの `Dictionary` 拡張
- `FirstList<T>` `Memory<T>` セグメントの連結リストで構成される高度なリスト。`Inode.Children` で使用
- `ComparerToEqualityComparer<T>` `FirstList` の内部で使用

### Objects（[src/lib/src/Objects/](../src/lib/src/Objects/)）

- `Extensions.cs` `object.To<T>()`, `As<T>()`, `Is<T>()` 等の拡張。**C# 14 (net10.0) の `extension` 構文**を使用。

---

## 設定ファイル

[pgfs.toml.example](../pgfs.toml.example) がサンプル。TOML 形式で、セクション形式 (`[database]` / `[mount]` / `[logging]` / ...)。実行時の探索パスは [`Schema.Setting.SearchPath`](../src/lib/src/Config/Schema.cs) で定義 (上記 [§Config](#config-srclibsrcconfig) 参照)。

---

## ビルドと実行

### 出力パス

すべてのプロジェクトは `<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` 設定で [bin/](../bin/) 配下に出力されます。Debug / Release / Publish で配置が分かれます:

| コマンド | 出力先 | 内容 |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `lib.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + 依存 dll (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | 同上の Release 版 (`DebugType=embedded`, framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | **single-file self-contained** な `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs` の 3 つの実行ファイル (ホスト OS の RID 自動、各 ~38 MB) |

`PublishAot=false` / `PublishTrimmed=false` を全プロジェクトで設定済み (Dapper / Tomlyn / Tmds.Fuse / DokanNet が動的コード生成に依存するため AOT 不可)。`UseCurrentRuntimeIdentifier` と `SelfContained` は `_IsPublishing=true` の Target でのみ有効化し、`dotnet build -c Release` 時には適用されないようにしてある (build 時に framework dll 200 個並ぶのを防ぐため)。

```pwsh
# ソリューションごとビルド
dotnet build pgfs.sln

# 個別ビルド
dotnet build src/lib/Lib.csproj
dotnet build src/mkfs/Mkfs.csproj

# Release publish (ホスト OS 向け single-file)
dotnet publish src/mkfs/Mkfs.csproj -c Release
# あるいはソリューション一括
dotnet publish pgfs.sln -c Release
```

**.NET 10 SDK 必須**（`extension` 構文等を使用）。

---

## 関連

- [Mkfs.ja.md](Mkfs.ja.md) — `mkfs.pgfs` の仕様
- [Mount.ja.md](Mount.ja.md) — `mount.pgfs` (Linux/macOS) の仕様
- [Assign.ja.md](Assign.ja.md) — `pgfs.assign` (Windows) の仕様
- [database.ja.md](database.ja.md) — DB スキーマ
- [coding-style.ja.md](coding-style.ja.md) — コーディング規約
- [performance.ja.md](performance.ja.md) — 性能改善候補
- [support_for_citus.ja.md](support_for_citus.ja.md) — Citus (水平分散) 対応の設計メモ
- [next.ja.md](next.ja.md) — 次にやることの一覧
- [history.ja.md](history.ja.md) — 現行設計に至る decisions
