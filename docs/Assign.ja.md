# pgfs.assign 仕様

PGFS ファイルシステムを **DokanNet 経由でマウント** する Windows 用ツール `pgfs.assign` の仕様です。

このドキュメントは現行実装 ([src/assign/](../src/assign/)) の仕様をまとめたもので、Linux/macOS 用の [Pgfs.Mount](../src/mount/) (Tmds.Fuse 版) と対になる存在です。共通部分はすべて [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs) に集約されており、Mount/Assign は OS 固有のアダプタに徹しています。

英語版は [Assign.md](Assign.md) を参照してください。

## 役割

PGFS が初期化された PostgreSQL データベース ([docs/Mkfs.md](Mkfs.md) で構築) を、Windows のドライブまたはディレクトリにマウントし、エクスプローラやアプリケーションから普通のファイルシステムとしてアクセスできるようにします。

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Lib.Api.Api（クロスプラットフォーム）
        ↑↓
    Pgfs.Assign.FileSystem : DokanNet.IDokanOperations2（Windows 固有）
        ↑↓ Dokan2 ドライバ
    Windows カーネル
        ↑↓
    P:\ または C:\mnt\pgfs などにアクセス
```

## ビルドと実行

```pwsh
# ビルド
dotnet build src\assign\Assign.csproj

# 起動（既定: localhost:5432 に接続して P:\ にマウント）
dotnet run --project src\assign

# 別のマウントポイント（ドライブレター）
dotnet run --project src\assign -- -m R:

# 別のマウントポイント（ディレクトリ）
dotnet run --project src\assign -- -m C:\mnt\pgfs

# 接続文字列を明示
dotnet run --project src\assign -- `
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" `
    -m P:

# ヘルプ
dotnet run --project src\assign -- --help
```

> **アンマウント**: Ctrl+C でアンマウント要求。または別ターミナルで `dokanctl /u <mountpoint>` を使うこともできます。

### 前提

- **.NET 10 SDK**
- **Dokan 2.x のカーネルドライバ** が事前にインストールされていること
  - [Dokan releases](https://github.com/dokan-dev/dokany/releases) から `DokanSetup_redist.exe` を入れる
  - 起動時に Dokan ドライバが見えない場合は `DokanException` で落ちる
- マウントポイントが既存ドライブと衝突していないこと
  - ドライブレター指定の場合: `P:` などが空いていること
  - ディレクトリ指定の場合: そのパスが NTFS 上の **空ディレクトリ**であること
- [docs/Mkfs.md](Mkfs.md) で DB 側の初期化が済んでいること

### Linux / macOS 上での挙動

ビルドだけは Linux/macOS でも通します（クロスコンパイル目的）。実行すると先頭で `OperatingSystem.IsWindows()` ガードに引っかかって `mount.pgfs` (Tmds.Fuse 版) に誘導するエラーを出して終了します。

## 設定

設定モデルは [`Pgfs.Lib.Config.RootConfig`](../src/lib/src/Config/RootConfig.cs) を共有しています。Mkfs / Mount と同じ TOML 設定ファイルと同じコマンドラインオプションが使えます。詳細は [docs/Mkfs.md](Mkfs.md) を参照。

pgfs.assign が特に使うのは:

| 設定 | 引数オプション | 既定 |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Windows: `P:` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `warning` |

## 実装している Dokan 操作

[src/assign/src/FileSystem.cs](../src/assign/src/FileSystem.cs) は [`DokanNet.IDokanOperations2`](https://github.com/dokan-dev/dokan-dotnet) を実装し、以下のコールバックに対応します。

| 操作 | 対応 | 備考 |
|---|---|---|
| `Mounted` / `Unmounted` | ✅ | マウント完了／解除をログに残す |
| `GetVolumeInformation` | ✅ | ボリュームラベル、`FileSystemFeatures` |
| `GetDiskFreeSpace` | ✅ | `pg_database_size` を実使用量に |
| `CreateFile` | ✅ | `FileMode` の `CreateNew` / `Create` / `Open` / `OpenOrCreate` / `Truncate` / `Append` をすべて処理 |
| `Cleanup` | ✅ | `info.DeletePending` のとき実削除する Windows のお作法に準拠 |
| `CloseFile` | ✅ | `info.Context` をクリア |
| `GetFileInformation` | ✅ | inode → `ByHandleFileInformation` |
| `FindFiles` / `FindFilesWithPattern` | ✅ | `Api.ListChildren` + `DokanHelper.DokanIsNameInExpression` でフィルタ。`ShortFileName` は常に `default` で Windows カーネル任せ (8.3 シンボリックアクセスは想定外) |
| `ReadFile` | ✅ | `Api.ReadData`（bytea チャンク経由） |
| `WriteFile` | ✅ | `Api.WriteData`、`info.WriteToEndOfFile` のときは追記モード |
| `FlushFileBuffers` | ✅ | PostgreSQL 側のコミットで永続化済みなので no-op |
| `SetFileAttributes` | ✅ | `ReadOnly` は st_mode の write ビットに反映、`Hidden` / `System` / `Archive` は xattr (`user.win.attrs`) に JSON `{hidden,system,archive}` で保存 |
| `SetFileTime` | ✅ | `lastWriteTime` のみ DB 反映。`atime` は要件により無視 |
| `DeleteFile` / `DeleteDirectory` | ✅ | Windows お作法どおり、削除可能性チェックのみ。実削除は `Cleanup` で行う |
| `MoveFile` | ✅ | `replace` フラグ対応、同種類チェック、空ディレクトリチェック |
| `SetEndOfFile` | ✅ | `Api.TruncateData`（bytea チャンクもまとめて切り詰め） |
| `SetAllocationSize` | ✅ | 暫定で `SetEndOfFile` と同じ挙動 |
| `LockFile` / `UnlockFile` | ✅ (簡易) | `DokanOptions.UserModeLock` でカーネル側に任せ、ここは常に Success |
| `GetFileSecurity` / `SetFileSecurity` | ✅ | inode (uname/gname/st_mode + 正準 ACL) ⇄ Windows SD を投影/逆投影。owner/group→SID、mode→owner/group/Everyone の allow ACE、named は `user.pgfs_acl`。deny/ACE順/継承は投影で落とす。詳細は [permission-interop.md](permission-interop.md) |
| `FindStreams` | ❌ | Alternate Data Streams は未対応、`NotImplemented` を返す |

凡例: ✅ 完了、⚠️ 部分実装、❌ 未実装。

## アーキテクチャ

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Assign.Program          ── 引数解析・OS 判定・マウント │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Assign.FileSystem       ── Dokan コールバック (Windows) │
│   - SplitParent / NormalizePath   ※OS 非依存             │
│   - DoCreate / Resolve            ※OS 非依存             │
│   - ToAttributes (FileSystemUtils) ※Windows 固有         │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Assign.WindowsUserResolver ── SID ↔ NTAccount 解決 (Windows 固有) │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Api.Api             ── DB 操作 (クロスプラットフォーム) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
│   - ReadData / WriteData / TruncateData                 │
│   - CreateSymlink / CreateHardLink                      │
│   - GetXAttr / SetXAttr / ListXAttr / RemoveXAttr       │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Api.InodeCache      ── inode メモリキャッシュ    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Utility.Pg          ── Npgsql + Dapper ラッパ   │
└─────────────────────────────────────────────────────────┘
```

### Mount との対称性

| 項目 | Mount (Linux) | Assign (Windows) |
|---|---|---|
| マウントライブラリ | Tmds.Fuse | DokanNet 2.3 |
| 親コールバック型 | `FuseFileSystemBase` | `IDokanOperations2` |
| ユーザー解決 | `UserResolver` (libc getpwnam) | `WindowsUserResolver` (NTAccount/SID) |
| エラーコード | POSIX errno (`-ENOENT` 等) | `DokanResult.FileNotFound` 等 |
| パス区切り | `/` 固定 | `\` を `/` に正規化してから Api へ |
| 削除のタイミング | `Unlink` 即削除 | `Cleanup` で `DeletePending` を見て削除 |
| 書き込みの追記モード | offset 引数 | `info.WriteToEndOfFile` |
| ACL | st_mode + 正準 ACL (`system.posix_acl_access` 経由) | st_mode + 正準 ACL (`Get/SetFileSecurity` で SD 投影) |

両者とも [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs) を呼ぶだけで実 DB 操作は共通化されています。

## 設計判断・暫定実装

### 名前正規化 + well-known principal マッピング

owner / group / principal 名は **保存時・照合時・呼び出し元名のすべて**で正規化する ([NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs): 全角ASCII→半角 + ドメイン除去 `\`・`@` + 小文字化)。DB は **Linux 名で保存**し、Windows ⇄ Linux の well-known 名は [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) のマッピングで双方向変換する: `root`↔`Administrator(s)` / `nobody`・`nogroup`↔`NT AUTHORITY\ANONYMOUS LOGON` / `other`↔`Everyone`。これにより Windows で作ったファイルも Linux で同名解決でき、大小・全半角も同一視される (設計の正は [permission-interop.md](permission-interop.md))。

### `Hidden` / `System` / `Archive` 属性

Linux 側に対応する概念が無いため、`pgfs_inode.xattrs` JSONB の `user.win.attrs` キーに JSON `{hidden,system,archive}` (bool) で保存する。`user.` 名前空間なので Linux の `getfattr` からも見える。`compressed` は将来対応。空 (全 OFF) でも **xattr は削除せず JSON を書き込む**。

`GetFileInformation` / `FindFiles` 系での判定は二値:
- **xattr 有り** (値が 0 でも) = Windows 側で `SetFileAttributes` を一度でも触ったことがある → xattr 値をそのまま信頼
- **xattr 無し** = Linux で作られたまま Windows が一度も触っていない → 「先頭ドット = Hidden」のヒューリスティクスを fallback として適用

「マスク 0 のとき xattr を消す」設計だと、ドットファイルを Windows Explorer で un-hide した直後に xattr が消え、次回読み出しで heuristic が復活して **また Hidden に戻ってしまう UX バグ** を踏むため、xattr 有無を「触ったかどうか」のフラグとして残す設計にしている。

`ReadOnly` は引き続き st_mode の write ビットで管理 (Linux 側と双方向で見える形)。実装は [src/assign/src/FileSystemUtils.cs](../src/assign/src/FileSystemUtils.cs) の `WinAttrsXattrKey` (= `user.win.attrs`) / `LoadWinAttrs` / `SaveWinAttrs`。

### Alternate Data Streams (ADS)

NTFS の `file.txt:stream` 形式の代替ストリームは未対応。`FindStreams` は空で `NotImplemented` を返します。Linux 側の xattr 相当の機能は xattr API (DokanNet には無い) ではなく ADS にマッピングする選択もありますが、現状は xattr 経由を未公開のまま据え置いています。

### ACL (GetFileSecurity / SetFileSecurity)

POSIX を正準・Windows ACL を **投影ビュー**として実装済み (設計の正は [permission-interop.md](permission-interop.md))。

- **GetFileSecurity (読み)**: inode を Windows セキュリティ記述子に投影する ([FileSystemUtils.BuildSecurity](../src/assign/src/FileSystemUtils.cs))。owner/group を uname/gname→SID、DACL を `st_mode` の owner/group/Everyone allow ACE + 正準 ACL (`user.pgfs_acl`) の named エントリに合成。Explorer の「セキュリティ」タブと `icacls` が POSIX 権限を反映する。
- **SetFileSecurity (書き)**: 受領した SD を逆投影する ([FileSystemUtils.ApplySecurity](../src/assign/src/FileSystemUtils.cs))。owner/group SID→名前 (owner にグループ SID が来たら `owner=nobody` / `group=該当` に振り分け)、DACL の基本 3 クラス→`st_mode`、named→`user.pgfs_acl`。
- **投影で落とすもの**: deny ACE / ACE 順序 / 継承フラグ は POSIX に等価が無いため採用しない (Windows⇄Windows の ACL 完全一致は保証しない)。
- Linux 側は `system.posix_acl_access` (setfacl/getfacl) が同じ正準ストアと往復する ([Mount.md](Mount.md))。

### LockFile / UnlockFile

`DokanOptions.UserModeLock` を立てたうえで `Success` を返しています。これにより Dokan カーネルが range lock を自前で持ち、PostgreSQL のアドバイザリロックには到達しません。POSIX 互換ロックは将来対応予定。

### マウントポイント

- **ドライブレター** (`P:`, `R:` など): 空いている文字を指定。`MountManager` オプションで Dokan に管理させる
- **ディレクトリパス** (`C:\mnt\pgfs`): NTFS の既存の **空ディレクトリ** を指定。マウント中はそのディレクトリの本来の中身は隠れる

### CreateFile の戻り値

- `Open`: 存在しない → `FileNotFound` または `PathNotFound`（ディレクトリ要求時）
- `OpenOrCreate`: 既存あり → `AlreadyExists` (info.Context にセット)、無し → 新規作成して `Success`
- `Create`: 既存あり → 中身を捨てて `AlreadyExists`、無し → 新規作成して `Success`
- `CreateNew`: 既存あり → `FileExists`、無し → 新規作成して `Success`
- `Truncate`: 既存無し → `FileNotFound`、ディレクトリ → `AccessDenied`、ファイル → 中身を捨てて `Success`
- `Append`: 既存無し → 新規作成、有り → そのまま開く（Dokan が次の WriteFile で `WriteToEndOfFile` を立ててくる）

## 既知の制限・TODO

| 項目 | 状態 | メモ |
|---|---|---|
| データ I/O (Read/Write) | ✅ | `Api.ReadData` / `WriteData` を共通基盤として使用 |
| SID ↔ uname/gname 解決 | ✅ | [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) で NTAccount.Translate |
| Truncate のチャンク削減 | ✅ | `Api.TruncateData`（Mount と共通） |
| ACL (Get/Set FileSecurity) | ✅ | POSIX 正準・Windows 投影ビュー。SD ⇄ st_mode + 正準 ACL (`user.pgfs_acl`)。owner=group は nobody/該当へ。deny/継承は投影で落とす。詳細は [permission-interop.md](permission-interop.md)。残: named ACL の厳密 enforce (要件待ち) |
| Hidden / System / Archive 属性 | ✅ | xattr `user.win.attrs` に JSON `{hidden,system,archive}` で保存。`ReadOnly` は st_mode の write ビット (従来通り)。Linux で作った dotfile は heuristic fallback で Hidden 表示 |
| Alternate Data Streams | ❌ | NTFS の ADS は未対応 |
| POSIX 互換 range lock | ❌ | `DokanOptions.UserModeLock` でカーネル任せ |
| Junction（再解析ポイント） | ❌ | DB スキーマには `is_junction` 列あり。Mount からのみ参照可能。Assign 側は未対応 |
| Notify (他クライアント変更通知) | ✅ | `database.notify_enabled=true` (CLI `--notify`) で有効化。PostgreSQL LISTEN/NOTIFY 経由で他クライアントの書き込みを受信し、ローカル `InodeCache` を invalidate した後、[FileSystem.PropagateRemoteChange](../src/assign/src/FileSystem.cs) が `DokanInstance.NotifyUpdate(WindowsPath)` で Explorer に再描画を依頼する。ペイロード仕様等の詳細は [history.md](history.md) 「他クライアント変更通知 (Notify)」参照 |
| 接続失敗時の再接続 | ✅ | [Retry](../src/lib/src/Utility/Retry.cs) で `Pg.OpenConnection` 系を包む。指数バックオフ、`database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` で調整。クエリ実行中の例外は idempotency 問題があるため再試行しない |
| OS に存在しない uname / gname のフォールバック | ✅ | `NTAccount.Translate` 失敗時、`mount.fallback_uname` / `mount.fallback_gname` (DB 保存、既定 `nobody` / `nogroup`) を SID 解決して返す。`nobody`/`nogroup` は well-known マッピングで `NT AUTHORITY\ANONYMOUS LOGON` に解決される。それも SID 解決できなければ `WellKnownSidType.AnonymousSid` を hardcode し warning ログ。実装は [src/assign/src/WindowsUserResolver.cs](../src/assign/src/WindowsUserResolver.cs) |

## 動作確認シナリオ（Windows 想定）

```pwsh
# 1. Dokan2 のドライバが入っていることを確認
sc query dokan2
# STATE が RUNNING ならよし

# 2. PostgreSQL に PGFS を初期化（[docs/Mkfs.md](Mkfs.md) 参照）
dotnet run --project src\mkfs

# 3. マウント
dotnet run --project src\assign -- -m P:

# 4. 別ターミナル（PowerShell）でアクセス
Get-ChildItem P:\                           # ルートディレクトリ
New-Item -ItemType Directory P:\hello       # ディレクトリ作成
Get-ChildItem P:\                           # hello が見える
Remove-Item P:\hello                        # 削除
New-Item -ItemType File P:\empty.txt        # 空ファイル
"hello world" | Out-File -FilePath P:\test.txt -Encoding utf8
Get-Content P:\test.txt                     # → hello world
Move-Item P:\test.txt P:\renamed.txt        # リネーム
Remove-Item P:\renamed.txt                  # 削除

# 5. アンマウント
# pgfs.assign のターミナルで Ctrl+C
# または別ターミナルで:
dokanctl /u P:
```

## 参照

- [docs/database.md](database.md) DB スキーマ設計
- [docs/Mkfs.md](Mkfs.md) 初期化ツールの仕様
- [docs/Mount.md](Mount.md) Linux/macOS 版マウントツール
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) 共通 API
- [Dokan](https://dokan-dev.github.io/) Dokan ドライバ
- [DokanNet](https://github.com/dokan-dev/dokan-dotnet) C# バインディング
