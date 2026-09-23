# アーキテクチャ

> **道順**: [docs/README.ja.md](README.ja.md) › **本書**
>
> **この doc が正である範囲**: **プロジェクトの構成** (Core / Fuse / Dokan + 薄い exe の分割、
> Core 内部のファイル構成、アセンブリ間の依存) と **ビルド・実行手順**。
> 「どのコードがどこにあるか」を探すときの最初の 1 枚。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [Mkfs.ja.md](Mkfs.ja.md) / [Mount.ja.md](Mount.ja.md) / [Assign.ja.md](Assign.ja.md) / [Pgfsctl.ja.md](Pgfsctl.ja.md) | 各 CLI の**仕様** (オプション・挙動) |
> | [design/database.ja.md](design/database.ja.md) | DB スキーマ |
> | [design/coding-style.ja.md](design/coding-style.ja.md) | コーディング規約 |
> | [design/performance.ja.md](design/performance.ja.md) | 性能の実測と改善候補 |
> | [design/support_for_citus.ja.md](design/support_for_citus.ja.md) | Citus (水平分散) 対応 |
> | [design/fuse-binding.ja.md](design/fuse-binding.ja.md) | FUSE 内製バインディングの構造 |
> | [next.ja.md](next.ja.md) / [history.ja.md](history.ja.md) | 次にやること / 完了した経緯 |

pgfs のソリューション構成・依存パッケージ・Core 内部のファイル構成・ビルド/実行手順をまとめたドキュメント。

## ソリューション構成

ソリューション [pgfs.sln](../pgfs.sln) に 8 つのプロジェクトがあります (v0.2.0 で旧 `Lib` を OS 機構層で分割)。

| プロジェクト | パス | 役割 | プラットフォーム |
|---|---|---|---|
| **Core** | [src/core/](../src/core/) | OS/機構 非依存のコアライブラリ（Models / Api / Config / Logging / Collections / Utility / Objects）。`#if` なし | クロスプラットフォーム |
| **Fuse** | [src/fuse/](../src/fuse/) | Linux/macOS の FS 機構層: 内製 libfuse バインディング + FUSE FileSystem + PosixAcl 投影 | Linux / macOS |
| **Dokan** | [src/dokan/](../src/dokan/) | Windows の FS 機構層: Dokan FileSystem + Windows SID/ACL 投影 + WindowsUserResolver | Windows |
| **Mkfs** | [src/mkfs/](../src/mkfs/) | PostgreSQL 側のテーブル等を初期化する CLI (薄い exe → Core) | クロスプラットフォーム |
| **Mount** | [src/mount/](../src/mount/) | Linux/macOS 用マウントツール (薄い exe → Fuse + Core) | Linux / macOS |
| **Assign** | [src/assign/](../src/assign/) | Windows 用マウントツール (薄い exe → Dokan + Core) | Windows |
| **Ctl** | [src/ctl/](../src/ctl/) | 実行時コントロールプレーン CLI `pgfsctl` (config / status サブコマンド、薄い exe → Core のみ) | クロスプラットフォーム |
| **Gui** | [src/gui/](../src/gui/) | 運用 GUI `pgfsgui` (Avalonia desktop・status/config の薄いフロント → Core のみ。**Phase 5・実装中**) | クロスプラットフォーム |

依存方向は **tools (mkfs/mount/assign) → 機構層 (Fuse/Dokan) → Core → PostgreSQL** の一本道。`pgfsctl` (CLI) と `pgfsgui` (Avalonia GUI) は機構層を介さず **Core のみ**に依存する管理ツール (FUSE/Dokan 不要なので両 OS で動く)。分割の設計は [v0.2.0-plan.ja.md](design/v0.2.0-plan.ja.md) / [fuse-binding.ja.md](design/fuse-binding.ja.md)、`pgfsctl`/`pgfsgui` は [control-plane.ja.md](design/control-plane.ja.md) / [gui.ja.md](design/gui.ja.md) を参照。

すべての TargetFramework は **net10.0**。`PublishAot` / `PublishTrimmed` は CLI の exe で無効 (Gui は同じ発行設定を持たない) (Dapper / Tomlyn / 内製 binding / DokanNet が動的コード生成に依存)。`ImplicitUsings` と `Nullable` も有効。OS 別の `DefineConstants` (`WINDOWS` / `LINUX` / `MACOS`) が定義されます。

### 名前空間

- ルート: `Pgfs.*`
- コアライブラリ: `Pgfs.Core.{Api, Models, Config, Logging, Collections, Objects, Utility}`
- 機構ライブラリ: `Pgfs.Fuse` (Linux/macOS FUSE) / `Pgfs.Dokan` (Windows)
- 実行ファイル: `Pgfs.Mkfs`, `Pgfs.Mount`, `Pgfs.Assign`, `Pgfs.Ctl` (出力 `pgfsctl`), `Pgfs.Gui` (出力 `pgfsgui`・Avalonia) — `pgfsctl`/`pgfsgui` は `{役割}.pgfs` 規約の意図的な例外 (admin ツール名)

### 出力先

すべてのプロジェクトは [bin/](../bin/) 配下の同一ディレクトリにビルド出力されます（`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>`）。

### アセンブリ名

namespace は `Pgfs.Core`, `Pgfs.Mkfs` 等の PascalCase (`RootNamespace`)。出力アセンブリ名は **lowercase + ドット区切り** に統一 (`AssemblyName`): `core.pgfs.dll`, `fuse.pgfs.dll`, `dokan.pgfs.dll`, `mkfs.pgfs.{dll,exe}`, `mount.pgfs.{dll,exe}`, `assign.pgfs.{dll,exe}`。**例外**: コントロールプレーン CLI は `pgfsctl.{dll,exe}` (systemctl 風の 1 語・`{役割}.pgfs` 規約を意図的に破る、[control-plane.ja.md §Phase 3](design/control-plane.ja.md))。命名規約の詳細は [fuse-binding.ja.md §3-8](design/fuse-binding.ja.md) 参照。

---

## 依存パッケージ

| パッケージ | 用途 | 使用先 |
|---|---|---|
| `Npgsql` 9.0.4 | PostgreSQL クライアント | 全プロジェクト |
| `Dapper` 2.1.66 | 軽量 ORM | Core |
| `Tomlyn` 0.20.0 | TOML 設定ファイル | Core |
| `Tmds.LibC` 0.5.0 | libc プリミティブ (stat / statvfs / timespec / dlopen / errno) | Fuse |
| `DokanNet` 2.3.0.3 | Windows DokanNet | Dokan |
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` 12.0.5 | 読み取り運用 GUI | Gui |

libfuse バインディングは v0.2.0 で **`Pgfs.Fuse` に内製化**した (旧 `securefolderfs-community/Tmds.Fuse` フォークを挙動保存で移植・`vendor/Tmds.Fuse` submodule は廃止)。`libfuse3.so.3` は同梱せず実行時に `dlopen` で動的リンクする (LGPL・利用者が用意)。クレジットは [src/fuse/NOTICES.md](../src/fuse/NOTICES.md)、設計は [fuse-binding.ja.md](design/fuse-binding.ja.md) 参照。

---

## ファイル構成（Core）

### Models（[src/core/src/Models/](../src/core/src/Models/)）

DB の 1 行に対応するエンティティ POCO を置く場所。設定モデル系 (旧 `*Settings.cs` 一式) は **削除済み**で、[Config](#configsrccoresrcconfig) に移行済み。

**DB エンティティ系**: `Base.cs`, `Inode.cs`, `Data.cs`, `Chunk.cs`
- 監査用フィールド (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`) を `Base` で共通化 (Dapper 用に小文字プロパティと大文字プロパティの両方を公開)
- `Inode.cs` / `Data.cs` / `Chunk.cs` がそれぞれ `pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` の 1 行を表す POCO

**監査・ACL 系**: `AuditOp.cs`, `AuditContext.cs`, `PgfsAcl.cs` (正準 ACL)
- `AuditOp.cs` (op 文字列定数) / `AuditContext.cs` (呼び出し元 ambient コンテキスト) — 監査ログ ([audit-log.ja.md](design/audit-log.ja.md))
- `PgfsAcl.cs` 正準 ACL ドキュメント (`user.pgfs_acl` xattr の JSON: named `entries[]` / `default[]`)。Windows DACL ⇔ Linux POSIX ACL の共通正準モデル
- ※ POSIX ACL バイナリ codec `PosixAcl.cs` は **v0.2.0 で `Pgfs.Fuse` ([src/fuse/src/PosixAcl.cs](../src/fuse/src/PosixAcl.cs)) へ移動**した (正準形=Core / OS 投影=機構層 Fuse の分離)。設計は [permission-interop.ja.md](design/permission-interop.ja.md)

**LoggingOutput 関連の Enum 群**: `SettingLoggingKind.cs` (Flags: None/Stderr/Stdout/File), `SettingLoggingCycle.cs` (None/Hourly/Daily/Monthly), `SettingLoggingOutput.cs` (上 2 つを束ねた POCO)
- [`LoggingOutputField`](../src/core/src/Config/Field.cs) が型として参照するため Models に残置 (移動先候補としては `Pgfs.Core.Logging` だが、本ファイルは「log のフォーマット記述」であって「log 出力本体」ではないので現状の置き場が無難)

> **🗒 旧 `*Settings.cs` は全削除**: `RootSettings.cs` / `MountSettings.cs` / `DatabaseSettings.cs` / `FileSystemSettings.cs` / `LoggingSettings.cs` / `SettingSettings.cs` / `Setting.cs` / `Settings.cs` / `BoolSetting.cs` / `SettingStorage.cs` / `BeforeChangeEventArgs.cs` / `DatabaseConnectionSetting.cs` / `Primitive.cs` の 13 ファイルを削除。`Base.cs` も `BaseProvider<A>` / `Statics` / `ChangingEventArgs` / `Created`/`Updated` 機構を撤去した最小形に書き直した。

### Config（[src/core/src/Config/](../src/core/src/Config/)）

旧 `Settings` ツリーの後継。**静的 `Field<T>` 記述子 + mutable POCO + `ConfigLoader` (CLI/TOML/DB/Default 統合) + `ConfigStore` (DB I/O)** で構成。

- **`Field<T>` (抽象基底 + 派生型)**: 設定 1 つあたりの記述子。Scope / Key / CliOptions / DashOName / SaveTo (`None`/`File`/`Db`) / DefaultFn / Comment を持ち、型ごとに `Parse(string) → T` / `Format(T) → string` を持つ。派生型: `StringField` / `IntField` / `LongField` / `BoolField` / `StringListField` / `LogLevelField` / `LoggingOutputField` / `ConnectionField`。ヘルプ生成用に `AppliesTo` (`[Flags] enum Tool`、どのツールの `--help` に載せるか / 既定 `All`) と `ArgName` (値プレースホルダ上書き) も持つ。
- **`Schema` (静的クラス)**: `Schema.Mount.MountPoint` のように nested static class で全 `Field<T>` を宣言。`Schema.AllFields` は reflection で全フィールドを自動列挙 (新規 Field を足したら自動で含まれる)。
- **`HelpText` (静的クラス)**: `--help` テキストを `Schema.AllFields` から生成 (`CliOptions` / `Comment` / 型 / 既定値を walk)。各 Program (mkfs/mount/assign) はイントロ文 + ツール固有のフッター散文だけ渡し、オプション一覧は自動生成。`Field.AppliesTo` でツール別に出し分ける (help 専用フィルタ、CLI パースには非干渉)。手書き `ShowHelp` は廃止済み。
- **POCO**: `MountConfig` / `DatabaseConfig` / `FileSystemConfig` / `LoggingConfig` / `SettingFileConfig`。プロパティは `{ get; set; }`。Live 設定の変更に使用するが、volume_label は Format で変更不可。集約 `RootConfig` が全部まとめる + `Help` / `Clean` を直接持つ。
- **`ConfigLoader`**: 1 度に CLI / TOML / DB / Default を統合して `RootConfig` を組み立てる。Phase は **CLI → TOML → DB** の順で「上位が既に入れていれば skip」(priority `CLI > TOML > DB > Default`)。TOML パスは CLI 解決済みの `setting.file` から内部で探索。`mount(8)` helper context (親 comm = `mount` AND positional あり) のときだけ `-i -f -n -s -v -N -t` を silent 飲み込み。
- **`ConfigStore`**: フラット `pgfs_settings(scope, key, value)` に対する `(scope, key) → 値` の DB 読み書き。`LoadAll` は単純な `SELECT scope, key, value FROM table` の全件読み + 許可リストフィルタ。`Save<T>` は `INSERT ... ON CONFLICT (scope, key) DO UPDATE` で UPSERT。JSON 表現は `Field<T>.FormatJson(value)` 経由 (Int/Long/Bool はネイティブ JSON、それ以外は `Format(value)` を JSON 文字列としてエンコード)。

変更時の永続化規約: 呼び側が `config.X.Y = newValue;` した直後に `store.Save(Schema.X.Y, newValue)` を明示的に呼ぶ (setter フックは入れていない、Loader/Store 分離のため)。具体例は将来 `Api.SetVolumeLabel(string)` 等の薄いラッパに集約予定。

### Api（[src/core/src/Api/](../src/core/src/Api/)）

- `Api.cs` ファイルシステム操作の公開 API（inode CRUD、データ I/O via bytea チャンク、xattr、symlink、hard link、ボリューム情報）。`IDisposable`、`OsBridge` プロパティで OS 通知ブリッジを受ける。
- **cross-client 排他制御**: `LockTargets` / `LockData(dataId)` / `LockInode(inodeId)` / `LockInodes(params long[])` のプライベートヘルパで `pgfs_lock` 上に `SELECT ... FOR UPDATE` 行ロックを取る。各 mutating メソッド (`WriteData` / `TruncateData` / `ReleaseData` / `Update{Mode,Owner,Size,Timestamps}` / `Rename` / `DeleteInode` / `CreateHardLink`) の冒頭で適切なロックを取り、tx 終了で自動解放。複数 lock 取得は target_id 昇順固定でデッドロック回避。詳細は [support_for_citus.ja.md §Phase 3](design/support_for_citus.ja.md)。
- `InodeCache.cs` inode のメモリキャッシュ + Dapper 経由の SELECT/INSERT。`byId` / `byPath` の 2 経路で検索可能。ルートは `id = 0` で固定。`TryGetPath(id, out path)` で parent チェーン walk による full path 解決を提供 (Notify 経路で使用)。
- `Mode.cs` POSIX `st_mode` 定数（S_IFDIR, S_IRWXU 等）
- `ContentCache.cs` / `DirtySet.cs`: 本体 read LRU と dirty チャンク。`DirtyNamespace.cs` / `IdReservation.cs` / `Api.WriteBackMetadata.cs`: pending inode、ID 予約、メタデータ実体化・監査・終了時 drain。実装契約と未修正事項は [metadata-write-back.ja.md](design/metadata-write-back.ja.md) / [metadata-write-back-reviews.ja.md](design/metadata-write-back-reviews.ja.md) を参照。
- `NotifyChannel.cs` / `RemoteChangeInfo.cs`: 制御 LISTEN は常時起動し、データ変更通知だけを `database.notify_enabled` で制御する。受信時は inode/path/parent と data_id に応じて InodeCache / ContentCache を無効化する。Linux からカーネルへの能動 invalidation は未実装。Assign の NotifyUpdate にはパスの不備がある ([Windows 設計](design/windows-parity.ja.md))。`attr_timeout=0` だけで Core・本体・他 mount の鮮度を保証しない。
- `HandleTable.cs` / `OpenFileContext.cs` / `OpenInodes.cs` / `Api.Handle.cs`: **ハンドル文脈** (handle-context 段階 A〜C)。`HandleTable` が open ごとに一意な `fh` を払い出し (1 始まり・単調増加・再利用なし)、`OpenFileContext` が「その open で確定した inode id」を持つ。`OpenInodes` が**実体の参照カウント**を持ち、`Api.Handle.cs` の最終解放が名前の消えた実体を落とす (`DropOrphanData`)。設計と as-built は [handle-context.ja.md](design/handle-context.ja.md)。
- `PruneAdmin.cs`: **異常終了が残したものの掃除** (`pgfsctl prune` の実体)。`{prefix}mounts` の古い行 / 孤児 data / libfuse の `.fuse_hidden*` を、**種類ごとに違う live 判定**で消す。仕様は [Pgfsctl.ja.md §prune](Pgfsctl.ja.md)。
- `ConfigAdmin` (Config) / `StatusAdmin` (Api): pgfsctl と GUI が共用する管理ロジック。mount 登録、30 秒 heartbeat、実効設定・統計の snapshot は Api が担当する。

**データ I/O (bytea チャンク) の実装メモ**: 各 inode のデータ本体は `pgfs_data` 1 行 + `pgfs_data_chunk` 複数行 (1 行 = 1 bytea = 1 チャンク、デフォルト chunk_size = 1MB)。LO 版から **Citus Phase 1** で bytea に置き換えた (詳細は [docs/support_for_citus.ja.md](design/support_for_citus.ja.md))。write-through の WriteData は 1 SQL/チャンクの upsert (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`、CASE で「中央 overlay / 末尾上書き / 0 パディング + 連結」の 3 ケース) で完結、並行 WriteFile race は PG の行ロックで自動直列化。ReadData は content cache または write-back が有効ならフルチャンクを取得し、両方 off のとき `substring(payload from N for M)` で必要範囲を取得する。write-back は dirty チャンクをメモリにまとめ、flush 時にファイル単位の tx で保存する。各チャンクの payload 長は「これまで書き込まれたバイト数」と等しい (LO セマンティクス踏襲)。0 パディングは `decode(repeat('00', N), 'hex')` (`repeat(bytea, integer)` は PG に存在しないため)。

### Logging（[src/core/src/Logging/](../src/core/src/Logging/)）

- 自前のロガー実装（`Microsoft.Extensions.Logging` ではなく独自）。
- `Logger.Default` 経由の静的 API。`Level.Enum` は `All/Trace/Debug/Information/Warning/Error/Critical/None`。出力先は `Logger.Output` (`Action<string>`)、最低レベルは `Logger.MinLevel`。
- **出力先の設定反映**: 各 Program.cs が起動時に `Logger.MinLevel = config.Logging.MinLevel` と `Logger.Output = LogSink.Create(config.Logging.Output)` を設定する。`logging.output` が `stdout`/`stderr`/`none` ならそれぞれの sink、`<cycle>:<dir>/<pattern>` 形なら [RotatingFileSink](../src/core/src/Logging/RotatingFileSink.cs) (日付ローテーション + `~` ホーム展開 + `*`→日付スタンプ + ディレクトリ自動作成 + AutoFlush)。設定なしの既定は `stderr`。変換は [LogSink.Create](../src/core/src/Logging/LogSink.cs)。
- **ホットパスではガード句必須**: `Logger.Trace(...)` は `params object?[]` で配列確保とボクシングが発生する。秒間 1000+ 呼ばれる箇所では必ず `if (Logger.IsTraceEnabled) { Logger.Trace(...); }` のように先に判定する。`IsTraceEnabled` / `IsDebugEnabled` / `IsEnabled(level)` を用意済み。

### Utility（[src/core/src/Utility/](../src/core/src/Utility/)）

- **`PathParser.cs`** パスをドライブ / ルート / 名前要素に分解し、別の区切り文字での再構築・ワイルドカード位置検出・前後への挿入が可能。`Lazy<>` で各部品を遅延評価。比較的しっかり書けているコンポーネント。**注意**: `PathParser.FromPath(path)` は OS デフォルトのセパレータ (`Path.DirectorySeparatorChar`) を使う。`Api` / `InodeCache` に流れるパスは常に `/` 区切りに正規化されているので、Lib 内部で呼び出すときは必ず `PathParser.FromPath(path, "/")` と明示すること。
- **`Pg.cs`** Dapper + Npgsql のラッパ（`Query`, `QueryAsync`, `Execute`, `ExecuteAsync`）。`NpgsqlDataSource` を接続文字列ごとにキャッシュ (同じ接続文字列なら同じデータソースが返る。プールはデータソース内部で管理される)。`QuoteIdentifier` / `QuoteLiteral` でエスケープ。SQL は `Logger.Trace` に出る（`TraceQuery` 内で `Logger.IsTraceEnabled` の早期 return ガード済み、Trace 無効時は `Regex.Replace` / `JsonSerializer.Serialize` をスキップ）。`Pg.OpenConnection` + `using var tx = conn.BeginTransaction()` パターンと、`Pg.WithTransaction<T>` ヘルパも提供 (`Span<byte>` を扱うときは ref struct なので lambda にできず、直接 `OpenConnection` を使う)。
- **`ServiceResolver.cs`** `/etc/services` または Win32 `getservbyname` でサービス名 ↔ ポート変換。
- **`NameNormalizer.cs`** owner/group/principal 名の正規化 (全角ASCII→半角 + ドメイン除去 `\`・`@` + 小文字化)。保存名・照合・呼び出し元名すべてに適用し、両 OS で大小・全半角を同一視する ([permission-interop.ja.md](design/permission-interop.ja.md) 決定 [1][2])。
- `Indexer.cs` (`ReadOnlyIndexer<,>` / `ReadOnlyIndexer<,,>` を `PathParser`, `Pg` で使用), `Fn.cs`, `String.cs`, `Json.cs`, `Retry.cs` 各種ヘルパ。

### Collections（[src/core/src/Collections/](../src/core/src/Collections/)）

- `RichDictionary<K,V>` `Added` イベント・`GetOrAdd<W>` 派生型サポート付きの `Dictionary` 拡張
- `FirstList<T>` `Memory<T>` セグメントの連結リストで構成される高度なリスト。`Inode.Children` で使用
- `ComparerToEqualityComparer<T>` `FirstList` の内部で使用

### Objects（[src/core/src/Objects/](../src/core/src/Objects/)）

- `Extensions.cs` `object.To<T>()`, `As<T>()`, `Is<T>()` 等の拡張。**C# 14 (net10.0) の `extension` 構文**を使用。

---

## 設定ファイル

[pgfs.toml.example](../pgfs.toml.example) がサンプル。TOML 形式で、セクション形式 (`[database]` / `[mount]` / `[logging]` / ...)。実行時の探索パスは [`Schema.Setting.SearchPath`](../src/core/src/Config/Schema.cs) で定義 (上記 [§Config](#configsrccoresrcconfig) 参照)。

---

## ビルドと実行

### 出力パス

下表の single-file 発行は Mkfs / Mount / Assign / Ctl が対象である。Gui の配布設定は未整備であり、ソリューション一括 publish で同じ配置・形態になるとは限らない。今回ビルド・発行は未検証である。

すべてのプロジェクトは `<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` 設定で [bin/](../bin/) 配下に出力されます。Debug / Release / Publish で配置が分かれます:

| コマンド | 出力先 | 内容 |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `core.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + 依存 dll (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | 同上の Release 版 (`DebugType=embedded`, framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | **single-file self-contained** な `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs`, `pgfsctl` の実行ファイル (ホスト OS の RID 自動、各 ~38 MB) |

CLI 発行では `PublishAot=false` / `PublishTrimmed=false` を設定済み (Dapper / Tomlyn / 内製 libfuse binding / DokanNet が動的コード生成・P/Invoke に依存するため AOT 不可)。`UseCurrentRuntimeIdentifier` と `SelfContained` は `_IsPublishing=true` の Target でのみ有効化し、`dotnet build -c Release` 時には適用されないようにしてある (build 時に framework dll 200 個並ぶのを防ぐため)。

```pwsh
# ソリューションごとビルド
dotnet build pgfs.sln

# 個別ビルド
dotnet build src/core/Core.csproj
dotnet build src/mkfs/Mkfs.csproj

# Release publish (ホスト OS 向け single-file)
dotnet publish src/mkfs/Mkfs.csproj -c Release
# あるいはソリューション一括
dotnet publish pgfs.sln -c Release
```

**.NET 10 SDK 必須**（`extension` 構文等を使用）。

---
