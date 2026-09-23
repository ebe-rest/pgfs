# assign.pgfs 仕様

> **道順**: [docs/README.ja.md](README.ja.md) › **本書**
>
> **この doc が正である範囲**: `assign.pgfs` (Windows / Dokan) の **利用者向け仕様** — CLI・前提・
> 対応済みオペレーション・既知の制限。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [design/windows-parity.ja.md](design/windows-parity.ja.md) | **設計と as-built**。なぜそう実装したか |
> | [Mount.ja.md](Mount.ja.md) | Linux 側 (`mount.pgfs` / FUSE) の同じ位置づけの doc |
> | [Mkfs.ja.md](Mkfs.ja.md) | 設定項目の既定値表 (Assign/Mount の抜粋はそちらへ委譲) |
> | [tests.ja.md](tests.ja.md) | Windows スイートの件数・実行方法 |

PGFS ファイルシステムを **DokanNet 経由でマウント** する Windows 用ツール `assign.pgfs` の仕様です。

このドキュメントは現行実装 ([src/assign/](../src/assign/)) で確定した仕様をまとめたもので、Linux/macOS 用の [Pgfs.Mount](../src/mount/) (FUSE 版) と対になる存在です。共通部分はすべて [`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs) に集約されており、Mount/Assign は OS 固有のアダプタに徹しています。

> 現行コードの静的照合 + **Windows 実機検証**: 2026-09-19。既定設定 (write-back off) では **e2e 30/30 + cross-client 6/6** が緑。
> **write-back (data / metadata) を on にした受入は 完了** ([writeback.ps1](../tests/windows/writeback.ps1) 6/6 + [wbmeta.ps1](../tests/windows/wbmeta.ps1) 4 passed + 1 skip)。機能差、実装候補、推奨方針、受入条件は [Windows 展開設計](design/windows-parity.ja.md) を参照する。

## 役割

PGFS が初期化された PostgreSQL データベース ([docs/Mkfs.ja.md](Mkfs.ja.md) で構築) を、Windows のドライブまたはディレクトリにマウントし、エクスプローラやアプリケーションから普通のファイルシステムとしてアクセスできるようにします。

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Core.Api.Api（クロスプラットフォーム）
        ↑↓
    Pgfs.Dokan.FileSystem : DokanNet.IDokanOperations2（Windows 固有）
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
>
> **Ctrl+C は 2 発目から意味が変わります** (mount.pgfs と同契約):
>
> | 回数 | 動作 |
> |---|---|
> | 1 発目 | アンマウント要求。write-back の未 flush は期限 (`--write-back-flush-timeout-ms`・既定 30 秒) まで書き切ろうとする |
> | 2 発目 | **flush の待機を打ち切る**。書けなかったぶんは失われますが、**何が失われたかのログと exit 4 は残ります** |
> | 3 発目以降 | 即時終了 (報告経路も諦める) |
>
> コンソールの × / ログオフ / `Stop-Process` はこの段階分けを通りません (Windows が ~5 秒で打ち切る /
> `Stop-Process` はプロセスを即座に終了させる)。**未 flush を抱えたまま止めるなら Ctrl+C を使ってください**。

### 前提

- **.NET 10 SDK**
- **Dokan 2.x のカーネルドライバ** が事前にインストールされていること
  - [Dokan releases](https://github.com/dokan-dev/dokany/releases) から `DokanSetup_redist.exe` を入れる
  - 起動時に Dokan ドライバが見えない場合は `DokanException` で落ちる
- マウントポイントが既存ドライブと衝突していないこと (** assign が起動前に検査**して、使用中なら空き候補を添えて終了する)
  - ドライブレター指定の場合: `P:` などが空いていること。**メディア無しの CD-ROM や未接続のリムーバブルもレターを占有する**ので注意 (`DriveInfo` の一覧で判定している)
  - ディレクトリ指定の場合: そのパスが NTFS 上の **空ディレクトリ**であること
- [docs/Mkfs.ja.md](Mkfs.ja.md) で DB 側の初期化が済んでいること

### Linux / macOS 上での挙動

ビルドだけは Linux/macOS でも通します（クロスコンパイル目的）。通常起動では設定の構築後に `OperatingSystem.IsWindows()` ガードで `mount.pgfs` (FUSE 版) に誘導するエラーを出して終了します。

## 設定

設定モデルは [`Pgfs.Core.Config.RootConfig`](../src/core/src/Config/RootConfig.cs) を共有しています。Mkfs / Mount と同じ TOML 設定ファイル ([pgfs.toml.example](../pgfs.toml.example)) と同じコマンドラインオプションが使えます。詳細は [docs/Mkfs.ja.md](Mkfs.ja.md) を参照。

assign.pgfs が特に使うのは:

| 設定 | 引数オプション | 既定 |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Windows: `P:` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `information` |
| `database.notify_enabled` | `--notify` | `false` (データ変更通知の opt-in。Core cache を無効化し NotifyUpdate を呼ぶ。**複数マウントを同時に運用するなら事実上必須** — 下の注を参照) |

### Linux 追加機能の適用状態

`mount.cache_data_max_bytes` (64 MiB)、`mount.negative_cache_ttl_ms` (0)、`mount.write_back` (false)、
`mount.write_back_metadata` (false) と各上限・間隔・timeout は共通 Schema / Api を使用する。
既定値・保存先・Live ポリシーは [settings-matrix.ja.md](design/settings-matrix.ja.md) を参照する。
`pgfsctl config` / `status`、mounts 登録・30 秒 heartbeat も共通実装である。

**metadata on 時は Cleanup の CloseInode も原則同期 flush しない**。明示 FlushFileBuffers は
FlushInode / FlushDirectory を呼ぶ。Cleanup は void なので失敗を返せずログに残す。
**修正済**: CreateNew は Core へ `exclusive=true` を渡す (衝突判定は DB の一意制約・
2 マウント同時の実測で成功は常に 1 件)。終了時の書き残しは `api.Dispose()` 後の `UnflushedAtShutdown` を見て
**exit 4** で報告する (mount.pgfs と同契約)。**停止シグナルの段階化も配線済** — 2 発目の Ctrl+C で `Api.AbandonFlush()` を呼び、粘りを打ち切ってから喪失レポート + exit 4 で終わる (§B-12 の Windows 配線)。**`FileOptions.WriteThrough` も配線済** (WriteFile ごとに完全バリア。[writeback.ps1](../tests/windows/writeback.ps1) で検証)。
**裁定済みの仕様**: `mkdir` は `exclusive: false` のまま (**同期化すると pending ディレクトリが生まれず、祖先チェーン INSERT が到達不能になる**ため)。**既定では DB の一意制約が同名 mkdir を弾く**が、**`write_back_metadata` を on にすると pending 採択で衝突が成功に化ける余地が残る** (B-1 のノブは `O_EXCL` の create が対象で、mkdir は対象外)。詳細は [windows-parity.ja.md §`mkdir` を同期にしない](design/windows-parity.ja.md)。~~`write_back_metadata` を on にした Windows 受入は未検証~~ → **2026-09-19 に [wbmeta.ps1](../tests/windows/wbmeta.ps1) で受入済**
(data write-back = `mount.write_back` on は 6/6 で検証済)。

> **⚠ 複数マウントを同時に使うなら `--notify` を付けること**。各マウントの `InodeCache` / read キャッシュは
> 独立で、他マウントの変更は LISTEN/NOTIFY でしか伝わらない。既定 (`database.notify_enabled=false`) では、
> **他マウントが行った create / 上書き / delete / 置換 rename がいつまでも見えない**
> (2026-09-19 に 2 マウントで実測。`--notify` 付きなら数百 ms で追随する)。
> 排他 (`CREATE_NEW` / `mkdir` の衝突) は DB の一意制約で決まるので notify の有無に関係なく正しく働く。
> テストは [tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1)。

## 実装している Dokan 操作

[src/dokan/src/FileSystem.cs](../src/dokan/src/FileSystem.cs) は [`DokanNet.IDokanOperations2`](https://github.com/dokan-dev/dokan-dotnet) を実装し、以下のコールバックに対応します。

| 操作 | 対応 | 備考 |
|---|---|---|
| `Mounted` / `Unmounted` | ✅ | マウント完了／解除をログに残す |
| `GetVolumeInformation` | ✅ | ボリュームラベル、`FileSystemFeatures` |
| `GetDiskFreeSpace` | ✅ | `Api.GetStatFs` 経由。mkfs `--statfs` で `{prefix}statfs()` (plperlu) を作っていればサーバ側の**実ディスク空き**、無ければ公称容量 (`max_file_size` − `pg_database_size`)。詳細 [docs/df-support.ja.md](design/df-support.ja.md) |
| `CreateFile` | ✅ | `FileMode` の `CreateNew` / `Create` / `Open` / `OpenOrCreate` / `Truncate` / `Append` をすべて処理 |
| `Cleanup` | ⚠️ | 非削除時は CloseInode、DeletePending 時は実削除 (+ **`NotifyDelete` で Windows 側キャッシュの無効化を促す**)。flush/delete 失敗はログのみで、呼出し元へ返せない |
| `CloseFile` | ✅ | `info.Context` をクリア |
| `GetFileInformation` | ✅ | inode → `ByHandleFileInformation` |
| `FindFiles` / `FindFilesWithPattern` | ✅ | `Api.ListChildren` + `DokanHelper.DokanIsNameInExpression` でフィルタ。`ShortFileName` は常に `default` で Windows カーネル任せ (8.3 シンボリックアクセスは想定外) |
| `ReadFile` | ✅ | `Api.ReadData`（bytea チャンク経由） |
| `WriteFile` | ✅ | `Api.WriteData`、`info.WriteToEndOfFile` のときは追記モード。**`FILE_FLAG_WRITE_THROUGH` 付きのハンドルは書き込みごとに完全バリア** (`Api.FlushInode`) |
| `FlushFileBuffers` | ✅ | ファイルは FlushInode、ディレクトリは FlushDirectory。失敗は Error。**対象を解決できない場合は FileNotFound** (無条件 Success の fail-open を解消) |
| `SetFileAttributes` | ✅ | `ReadOnly` は st_mode の write ビットに反映、`Hidden` / `System` / `Archive` は xattr (`user.win.attrs`) に JSON `{hidden,system,archive}` で保存。**実質変更が無い要求は DB を触らず成功** (。`Remove-Item` が削除前に呼ぶ ReadOnly 落としで、書き込めない inode の削除が阻まれるのを防ぐ) |
| `SetFileTime` | ✅ | `lastWriteTime` のみ DB 反映。`atime` は要件により無視 |
| `DeleteFile` / `DeleteDirectory` | ✅ | Windows お作法どおり、削除可能性チェックのみ。実削除は `Cleanup` で行う |
| `MoveFile` | ✅ | `replace` フラグ対応、同種類チェック、空ディレクトリチェック。**同一パス / 同一 inode への rename は no-op 成功** (置換削除に入れない) |
| `SetEndOfFile` | ✅ | `Api.TruncateData`（bytea チャンクもまとめて切り詰め） |
| `SetAllocationSize` | ✅ | ** EOF と分離**: 指定が現在の EOF 未満なら truncate、以上なら**論理内容を変えず成功**。ただし実際の容量予約は行わず、`ByHandleFileInformation` に AllocationSize を返す経路も無い |
| `LockFile` / `UnlockFile` | ✅ | ** `UserModeLock` を外した** — byte-range lock は **Dokan ドライバがカーネル側で強制**する。以前は自前コールバックが常に Success で「取れていないロックを取れた」と嘘をついていた。**クロスクライアント (別マウント間) の範囲ロックは別設計** |
| `GetFileSecurity` / `SetFileSecurity` | ✅ | inode (uname/gname/st_mode + 正準 ACL) ⇄ Windows SD を投影/逆投影。owner/group→SID、mode→owner/group/Everyone の allow ACE、named は `user.pgfs_acl`。deny/ACE順/継承は投影で落とす。詳細は [permission-interop.ja.md](design/permission-interop.ja.md) |
| `FindStreams` | ❌ | Alternate Data Streams は未対応、空コレクションを返す |

凡例: ✅ 対応処理あり、⚠️ 制限・不具合あり、❌ 未実装。実機検証済みを意味する分類ではない。

## アーキテクチャ

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Assign.Program          ── 引数解析・OS 判定・マウント │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Dokan.FileSystem       ── Dokan コールバック (Windows) │
│   - SplitParent / NormalizePath   ※OS 非依存             │
│   - DoCreate / Resolve            ※OS 非依存             │
│   - ToAttributes (FileSystemUtils) ※Windows 固有         │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Dokan.WindowsUserResolver ── SID ↔ NTAccount 解決 (Windows 固有) │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.Api             ── DB 操作 (クロスプラットフォーム) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
│   - ReadData / WriteData / TruncateData                 │
│   - CreateSymlink / CreateHardLink                      │
│   - GetXAttr / SetXAttr / ListXAttr / RemoveXAttr       │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.InodeCache      ── inode メモリキャッシュ    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Utility.Pg          ── Npgsql + Dapper ラッパ   │
└─────────────────────────────────────────────────────────┘
```

### Mount との対称性

| 項目 | Mount (Linux) | Assign (Windows) |
|---|---|---|
| マウントライブラリ | libfuse (内製 binding) | DokanNet 2.3 |
| 親コールバック型 | `FuseFileSystemBase` | `IDokanOperations2` |
| ユーザー解決 | `UserResolver` (libc getpwnam) | `WindowsUserResolver` (NTAccount/SID) |
| エラーコード | POSIX errno (`-ENOENT` 等) | `DokanResult.FileNotFound` 等 |
| パス区切り | `/` 固定 | `\` を `/` に正規化してから Api へ |
| 削除のタイミング | `Unlink` 即削除 | `Cleanup` で `DeletePending` を見て削除 |
| 書き込みの追記モード | offset 引数 | `info.WriteToEndOfFile` |
| ACL | st_mode + 正準 ACL (`system.posix_acl_access` 経由) | st_mode + 正準 ACL (`Get/SetFileSecurity` で SD 投影) |

両者とも[`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs)を呼ぶだけで実 DB 操作は共通化されています。

## 設計判断・暫定実装

### 名前正規化 + well-known principal マッピング

owner / group / principal 名は **保存時・照合時・呼び出し元名のすべて**で正規化する ([NameNormalizer](../src/core/src/Utility/NameNormalizer.cs): 全角ASCII→半角 + ドメイン除去 `\`・`@` + 小文字化)。DB は **Linux 名で保存**し、Windows ⇄ Linux の well-known 名は [WindowsUserResolver](../src/dokan/src/WindowsUserResolver.cs) のマッピングで双方向変換する: `root`↔`Administrator(s)` / `nobody`・`nogroup`↔`NT AUTHORITY\ANONYMOUS LOGON` / `other`↔`Everyone`。これにより Windows で作ったファイルも Linux で同名解決でき、大小・全半角も同一視される (設計の正は [permission-interop.ja.md](design/permission-interop.ja.md))。

### `Hidden` / `System` / `Archive` 属性

Linux 側に対応する概念が無いため、xattr `user.win.attrs` に JSON `{hidden,system,archive}` (bool) のバイト列を保存する (`pgfs_inode` の `xattr_names`/`xattr_values` 並行配列。値は bytea。[xattr-bytea.ja.md](design/xattr-bytea.ja.md))。`user.` 名前空間なので Linux の `getfattr` からも見える。`compressed` は将来対応。空 (全 OFF) でも **xattr は削除せず JSON を書き込む**。

`GetFileInformation` / `FindFiles` 系での判定は二値:
- **xattr 有り** (値が 0 でも) = Windows 側で `SetFileAttributes` を一度でも触ったことがある → xattr 値をそのまま信頼
- **xattr 無し** = Linux で作られたまま Windows が一度も触っていない → 「先頭ドット = Hidden」のヒューリスティクスを fallback として適用

「マスク 0 のとき xattr を消す」設計だと、ドットファイルを Windows Explorer で un-hide した直後に xattr が消え、次回読み出しで heuristic が復活して **また Hidden に戻ってしまう UX バグ** を踏むため、xattr 有無を「触ったかどうか」のフラグとして残す設計にしている。

`ReadOnly` は引き続き st_mode の write ビットで管理 (Linux 側と双方向で見える形)。実装は [src/dokan/src/FileSystemUtils.cs](../src/dokan/src/FileSystemUtils.cs) の `WinAttrsXattrKey` (= `user.win.attrs`) / `LoadWinAttrs` / `SaveWinAttrs`。

### Alternate Data Streams (ADS)

NTFS の `file.txt:stream` 形式の代替ストリームは未対応。`FindStreams` は空で `NotImplemented` を返します。Linux 側の xattr 相当の機能は xattr API (DokanNet には無い) ではなく ADS にマッピングする選択もありますが、現状は xattr 経由を未公開のまま据え置いています。

### ACL (GetFileSecurity / SetFileSecurity)

POSIX を正準・Windows ACL を **投影ビュー**として実装済み (設計の正は [permission-interop.ja.md](design/permission-interop.ja.md))。

- **GetFileSecurity (読み)**: inode を Windows セキュリティ記述子に投影する ([FileSystemUtils.BuildSecurity](../src/dokan/src/FileSystemUtils.cs))。owner/group を uname/gname→SID、DACL を `st_mode` の owner/group/Everyone allow ACE + 正準 ACL (`user.pgfs_acl`) の named エントリに合成。Explorer の「セキュリティ」タブと `icacls` が POSIX 権限を反映する。
- **SetFileSecurity (書き)**: 受領した SD を逆投影する ([FileSystemUtils.ApplySecurity](../src/dokan/src/FileSystemUtils.cs))。owner/group SID→名前 (owner にグループ SID が来たら `owner=nobody` / `group=該当` に振り分け)、DACL の基本 3 クラス→`st_mode`、named→`user.pgfs_acl`。
- **投影で落とすもの**: deny ACE / ACE 順序 / 継承フラグ は POSIX に等価が無いため採用しない (Windows⇄Windows の ACL 完全一致は保証しない)。
- Linux 側は `system.posix_acl_access` (setfacl/getfacl) が同じ正準ストアと往復する ([Mount.ja.md](Mount.ja.md))。

### LockFile / UnlockFile

`DokanOptions.UserModeLock` は LockFile / UnlockFile を **userspace で処理する**指定です。pgfs は範囲ロックの台帳を持たないため、付けたままだとコールバックが常に Success を返し、**取れていないロックを「取れた」と返す**状態でした。**オプションを外し、Dokan ドライバに任せる形へ変更**しています (DokanNet の定義: "Enable Lockfile/Unlockfile operations. Otherwise Dokan will take care of it.")。実機で**同一マウント内の byte-range lock が強制されること**を確認済み (`test_byte_range_lock_enforced`: 2 本目のハンドルの `Lock` が `IOException`、`Unlock` 後は取得できる)。万一コールバックが呼ばれたときは `NotImplemented` を返します (嘘の成功を返さない)。

**別マウント間 (クロスクライアント) の範囲ロックは未対応**です。ドライバが見ているのは自分のマウント内だけで、期限・プロセス死亡・lease 回復を含む分散ロックは別設計になります ([Windows 展開設計](design/windows-parity.ja.md))。DB の `pgfs_lock` は tx 更新用であり、アプリケーションの範囲ロックとは別です。

### マウントポイント

- **ドライブレター** (`P:`, `R:` など): 空いている文字を指定。MountManager は競合時に別の文字を割り当て得る。**通知先は修正済** (Mounted が申告する実マウント先を保持し、NotifyUpdate に絶対パスを渡す)。**設定値・`{prefix}mounts` の登録行は要求値のままで更新しない** (未修正)。既に使われている文字を指定すると Dokan は `Something's wrong with the Dokan driver` で落ちる (事前チェック無し)
- **ディレクトリパス** (`C:\mnt\pgfs`): NTFS の既存の **空ディレクトリ** を指定。マウント中はそのディレクトリの本来の中身は隠れる

### 作れない名前

**Windows から新規作成するときだけ、次の名前を `STATUS_OBJECT_NAME_INVALID` で拒否する。**
**既存のものは開ける** — Linux から作られた名前を Windows で読めなくしないため。

| 弾く名前 | 弾く理由 (実測) |
|---|---|
| **MS-DOS デバイス名** `CON` / `PRN` / `AUX` / `NUL` / `COM1`〜`COM9` / `LPT1`〜`LPT9` (拡張子付きも同じ。`CON.txt` も弾く) | **`NUL` を 1 つ作るとそのディレクトリが `Remove-Item -Recurse` で消せなくなる** (`ERROR_INVALID_FUNCTION`)。回収には `\\?\` 経由の個別削除が要る |
| **末尾が空白またはドット** (`trail ` / `trail.`) | **Win32 の正規化で末尾が落ちるので、`dot.` を指定すると `dot` の中身が黙って返る**。**エラーにならない**ので、利用者は誤ったデータを読んだことに気づけない |

**素の Win32 パスからは、そもそも FS に届かないものもある** — `NUL` は Win32 層が NUL デバイスに
解決するので pgfs は関与しない (書き込みは成功するがファイルは作られない)。**実際に作られるのは
`\\?\` 経由**なので、そこで弾いている。

**`.fuse_hidden<16 桁 16 進>` は Windows では隠さない** (Linux とここだけ挙動が違う)。
libfuse が作る残骸を隠す仕組みだが、**Windows では libfuse が動かないので残骸が生まれず**、
隠すと「**列挙に出ないのに消せない**」だけが残るため。**方針の正は
[namespace-policy.ja.md](design/namespace-policy.ja.md)。**

### CreateFile の戻り値

- `Open`: 存在しない → `FileNotFound` または `PathNotFound`（ディレクトリ要求時）
- `OpenOrCreate`: 既存あり → `AlreadyExists` (info.Context にセット)、無し → 新規作成して `Success`
- `Create`: 既存あり → 中身を捨てて `AlreadyExists`、無し → 新規作成して `Success`
- `CreateNew`: 既存あり → `FileExists`、無し → 新規作成して `Success`。**作成は `Api.CreateFile(exclusive: true)`** なので、別マウントとの同時 create でも DB の一意制約で片方だけが勝つ (敗者は `AlreadyExists`)
- `Truncate`: 既存無し → `FileNotFound`、ディレクトリ → `AccessDenied`、ファイル → 中身を捨てて `Success`
- `Append`: 既存無し → 新規作成、有り → そのまま開く（Dokan が次の WriteFile で `WriteToEndOfFile` を立ててくる）

## 既知の制限・TODO

| 項目 | 状態 | メモ |
|---|---|---|
| データ I/O (Read/Write) | ✅ | `Api.ReadData` / `WriteData` を共通基盤として使用 |
| append (`FILE_APPEND_DATA`) | ✅ | **末尾を決めるのは Core** (`Api.AppendData`)。Dokan は `WriteToEndOfFile` で「末尾へ書け」と言ってくるだけなので、FS 側が**解決し直した末尾**へ書く (段階 A まではハンドルが握った古い `Inode.Size` を使い、**他マウントが伸ばしたぶんを上書きして消していた** — [crossclient.ps1](../tests/windows/crossclient.ps1) の `test_x_append_handle_sees_peer_growth` が 11 → 6 バイトの損失で再現)。**不可分性の上限は [Mount.ja.md §append の契約](Mount.ja.md) が正** (write-through のときだけ / 1 回のコールバックに収まるときだけ)。**Windows では 1 回の `WriteFile` は分割されずに 1 回のコールバックとして届く** (2026-09-21 実測) — 64KB / 1MB / 16MB / **64MB** のすべてで `NumberOfBytesToWrite` がそのまま 1 行で来た。生の Win32 `WriteFile` / `FILE_FLAG_WRITE_THROUGH` / `FILE_APPEND_DATA` / .NET `FileStream` の 4 経路とも同じで、`NoCache=False, PagingIo=False` (キャッシュ経由でもページング I/O でもない同期の降り方)。**Linux は `max_write` 超えで切れるので、append の不可分性の上限は OS で違う** — Windows の上限を Linux に持ち込まないこと |
| SID ↔ uname/gname 解決 | ✅ | [WindowsUserResolver](../src/dokan/src/WindowsUserResolver.cs) で NTAccount.Translate |
| Truncate のチャンク削減 | ✅ | `Api.TruncateData`（Mount と共通） |
| ACL (Get/Set FileSecurity) | ✅ | POSIX 正準・Windows 投影ビュー。SD ⇄ st_mode + 正準 ACL (`user.pgfs_acl`)。owner=group は nobody/該当へ。deny/継承は投影で落とす。詳細は [permission-interop.ja.md](design/permission-interop.ja.md)。残: named ACL の厳密 enforce (要件待ち) |
| Hidden / System / Archive 属性 | ✅ | xattr `user.win.attrs` に JSON `{hidden,system,archive}` で保存。`ReadOnly` は st_mode の write ビット (従来通り)。Linux で作った dotfile は heuristic fallback で Hidden 表示 |
| Alternate Data Streams | ❌ | NTFS の ADS は未対応 |
| アプリケーションの range lock | ⚠️ | **同一マウント内はドライバが強制** ( `UserModeLock` を外した)。**別マウント間は未対応**。Core の tx 排他 (`pgfs_lock`) とは別物 |
| symlink / hardlink の native 作成・junction | ❌ | **2026-09-19 に PoC 済: 現バインディングでは作成も読みも成立しない**。`IDokanOperations2` に link 系コールバックが無く、reparse データの get/set 入口も無い。実測で `mklink /H` `/J` `/D` と `File.CreateSymbolicLink` が全滅し、**Linux 由来の symlink は列挙から消え、名指しすると空ファイルとして開ける**。詳細は [windows-parity.ja.md §native link の到達性 PoC](design/windows-parity.ja.md) |
| Notify (他クライアント変更通知) | ⚠️ | **修正**: `Mounted` が申告する実マウント先を前置した絶対パス (`P:\dir\file`) を渡し、bool 戻り値も失敗ログに使う。**消えた対象は `NotifyDelete`** を撃つ (`NotifyUpdate` では属性変更扱いになり、相手側のキャッシュにエントリが残って `Test-Path` が true を返し続ける)。種別はファイル → ディレクトリの順に試す (payload に op / 種別が無いため)。2 マウントでの**可視性は実測済** (create / 上書き / delete / 置換 rename が数百 ms で追随・[crossclient.ps1](../tests/windows/crossclient.ps1))。**他マウントの変更は FileSystemWatcher / Explorer のイベントにならない** (2026-09-21 実測)。**ローカル操作では豊富に出る** (`Created` / `Changed` / `Renamed` / `Deleted` — ドライバが生成する) のに、**他マウント由来は 1 件も出ない**。つまり `NotifyUpdate` / `NotifyDelete` は**キャッシュ無効化としては効く**が (可視性は上記のとおり実測済)、**`ReadDirectoryChangesW` の通知は生まない**。**読み直せば新しい値が見えるが、開いたままのウィンドウは更新されない** (F5 が要る)。**Windows には「通知で画面が直る」経路がそもそも無い**ということであり、[Mount.ja.md](Mount.ja.md) が書く「全破棄のときは OS へ渡せるものが無い」とは層が違う (**渡せたとしても画面は直らない**)。[公式 DokanInstance API](https://dokan-dev.github.io/dokan-dotnet-doc/html/class_dokan_instance.html) / [展開設計](design/windows-parity.ja.md) |
| 接続失敗時の再接続 | ✅ | [Retry](../src/core/src/Utility/Retry.cs) で `Pg.OpenConnection` 系を包む。指数バックオフ、`database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` で調整。任意のクエリの再試行はしない。別途 Core の create / write / flush は 40P01・40001 の bounded tx retry を持つ |
| OS に存在しない uname / gname のフォールバック | ✅ | `NTAccount.Translate` 失敗時、`mount.fallback_uname` / `mount.fallback_gname` (DB 保存、既定 `nobody` / `nogroup`) を SID 解決して返す。`nobody`/`nogroup` は well-known マッピングで `NT AUTHORITY\ANONYMOUS LOGON` に解決される。それも SID 解決できなければ `WellKnownSidType.AnonymousSid` を hardcode し warning ログ。実装は [src/dokan/src/WindowsUserResolver.cs](../src/dokan/src/WindowsUserResolver.cs) |

### 追加の未修正事項（**棚卸し**。取り消し線は修正済み）

- ~~DoCreate は要求元でなくプロセス既定の owner/group を使う~~ → **修正済**。uname = 要求元の User SID を解決 / gname = **親ディレクトリから継承** / 取得失敗は `fallback_*` + Warning。監査の主体も同日に CreateFile での確定 + ハンドル保持へ修正済 (それ以前の Windows 由来の監査行は `caller_uname` / `caller_domain` が NULL)。**残**: ドメイン環境の同名ユーザー (`CORP\alice` と `LOCAL\alice`) は**所有者としては区別できない** — `NameNormalizer` が domain 部を落とす**仕様**のため (**監査の `caller_domain` には残る**)。整理は [permission-interop.ja.md §ドメイン名は所有者からは落ち、監査には残る](design/permission-interop.ja.md)。**実機のドメイン環境では未検証**。
- ~~Context に保持した Inode はリモート cache invalidation 後も再取得しない~~ → **修正済** (handle-context 段階 B。`Resolve` が `InodeId` で毎回引き直す)。
- ~~FileIndex は inode.Id のため hardlink 兄弟で一致しない~~ → **修正済**。`data_id | 0x8000_0000_0000_0000` = **FUSE の `st_ino` と同じ式**にした ([windows-parity.ja.md](design/windows-parity.ja.md))。
- ~~Assign は Dispose 後の UnflushedAtShutdown を見ず正常経路で 0 を返す~~ → **修正 (exit 4)**。ただし Cleanup の成功をアプリへの耐久性保証にしない点は変わらない。
- ~~SetFileAttributes は ReadOnly の変更時に permission 全体を 0444 / 0644 / 0755 に作り直す~~ →
  **修正済**。**write ビットだけを触る** (付ける = 0222 を落とす / 外す = owner の 0200 を戻す)。
  以前は**ディレクトリに ReadOnly を付けると 0444 になり、x ビットが落ちていた** (Windows 側は mode を
  持たないので気づけない)。**`-o default_permissions` を付けた Linux マウントからは traverse できなくなる**
  ことを実測済み (既定のマウントでは mode が強制されないので、壊れた mode が静かに残るだけ)。**0755 / 0644 / 0750 は往復で完全に戻る**。
  **残る割り切り**: group / other に write があった mode は、往復でその 2 つを失う (POSIX 側に元の
  write ビットを覚える場所が無いため。Windows から作られる mode では起こらない)。

詳細な根拠と実装案は [Windows 展開設計](design/windows-parity.ja.md) に集約する。

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
# assign.pgfs のターミナルで Ctrl+C
# または別ターミナルで:
dokanctl /u P:
```

## 外部・ソースへの参照

> doc への行き先は **H1 直下の行き先表**が正。ここには**ドキュメント以外**の参照だけを置く。

- [src/core/src/Api/Api.cs](../src/core/src/Api/Api.cs) 共通 API
- [Dokan](https://dokan-dev.github.io/) Dokan ドライバ
- [DokanNet](https://github.com/dokan-dev/dokan-dotnet) C# バインディング
