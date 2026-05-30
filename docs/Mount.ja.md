# mount.pgfs 仕様

PGFS ファイルシステムを **FUSE 経由でマウント** する Linux / macOS 用ツール `mount.pgfs` の仕様です。

このドキュメントは現行実装 ([src/mount/](../src/mount/)) の仕様をまとめたもので、Windows 用の [Pgfs.Assign](../src/assign/) (DokanNet 版) とは別の実行ファイルです。共通部分はすべて [`Pgfs.Lib.Api.Api`](../src/lib/src/Api/Api.cs) に集約されています。

英語版は [Mount.md](Mount.md) を参照してください。

## 役割

PGFS が初期化された PostgreSQL データベース ([docs/Mkfs.md](Mkfs.md) で構築) を、Linux/macOS のディレクトリツリーとしてユーザー空間に見せます。

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Lib.Api.Api（クロスプラットフォーム）
        ↑↓
    Pgfs.Mount.FileSystem : Tmds.Fuse.FuseFileSystemBase（Linux/macOS 固有）
        ↑↓ FUSE
    Linux カーネル / macOS macFUSE
        ↑↓
    cd /mnt/pgfs && ls
```

## ビルドと実行

```bash
# ビルド
dotnet build src/mount/Mount.csproj

# 起動（既定: localhost:5432 に接続して /mnt/pgfs にマウント）
sudo dotnet run --project src/mount

# 別のマウントポイント
sudo dotnet run --project src/mount -- -m /mnt/myfs

# 接続文字列を明示
sudo dotnet run --project src/mount -- \
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    -m /mnt/pgfs

# ヘルプ
dotnet run --project src/mount -- --help
```

> **アンマウント**: `fusermount3 -u /mnt/pgfs` (Linux) または Ctrl+C (LazyUnmount)。

### プロセスがクラッシュしたあとのアンマウント

mount.pgfs のプロセスがクラッシュしたり SIGKILL で落ちると、**カーネル側のマウントエントリだけが残った状態**になります。`mount` コマンドで確認すると以下のように見え、そのままでは再マウントできません:

```bash
$ mount | grep pgfs
/dev/fuse on /mnt/pgfs type fuse (rw,nosuid,nodev,relatime,user_id=1000,group_id=1000)

$ ls /mnt/pgfs
ls: cannot access '/mnt/pgfs': Transport endpoint is not connected
```

このときは **手動でアンマウント**してください:

```bash
# 通常はこれで OK（普段のアンマウントと同じ）
fusermount3 -u /mnt/pgfs

# パーミッションエラーが出る場合は強制
fusermount3 -uz /mnt/pgfs    # -z = lazy (busy でも次の利用で外す)
# または
sudo umount /mnt/pgfs
sudo umount -l /mnt/pgfs     # lazy
```

`mount` コマンドで `/dev/fuse on .../mnt/pgfs` が消えたら復旧完了です。その後通常通り `mount.pgfs -m ...` で再マウントできます。

### 前提

- **.NET 10 SDK**
- **libfuse3** / **fusermount3** がインストールされていること
  - Ubuntu/Debian: `sudo apt install fuse3 libfuse3-3`
  - Arch: `sudo pacman -S fuse3`
  - 起動時に `Fuse.CheckDependencies()` で確認し、不足していれば終了する
- マウントポイントが事前に作成されていること
  - `sudo mkdir -p /mnt/pgfs && sudo chown $USER /mnt/pgfs`
- [docs/Mkfs.md](Mkfs.md) で DB 側の初期化が済んでいること

### Windows 上での挙動

ビルドだけは Windows でも通します（クロスコンパイル目的）。実行すると先頭で `OperatingSystem.IsWindows()` に引っかかって `pgfs.assign` (DokanNet 版) に誘導するエラーを出して終了します。Windows でマウントする場合は [src/assign/](../src/assign/) を使ってください。

## 設定

設定モデルは [`Pgfs.Lib.Config.RootConfig`](../src/lib/src/Config/RootConfig.cs) を共有しています。Mkfs と同じ TOML 設定ファイルと同じコマンドラインオプションが使えます。詳細は [docs/Mkfs.md](Mkfs.md) を参照。

mount.pgfs が特に使うのは:

| 設定 | 引数オプション | 既定 |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Linux/macOS: `/mnt/pgfs` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `logging.level` | `--log-level` | `warning` |
| `database.notify_enabled` | `--notify` | `false`。複数クライアントから同じ PG/pgfs を mount するときに有効化推奨。詳細は下記「他クライアント変更通知」 |

`pgfs.toml` は Mkfs と同じ TOML を使えます。

## 実装している FUSE 操作

[src/mount/src/FileSystem.cs](../src/mount/src/FileSystem.cs) は [`Tmds.Fuse.FuseFileSystemBase`](https://github.com/tmds/Tmds.Fuse) を継承し、以下を override しています。

| 操作 | 対応 | 備考 |
|---|---|---|
| `GetAttr` | ✅ | inode → `stat` 構造体マッピング |
| `OpenDir` | ✅ | 存在＆ディレクトリ確認のみ |
| `ReadDir` | ✅ | `.` / `..` + 子 inode 列挙 |
| `ReleaseDir` | ✅ | no-op |
| `MkDir` | ✅ | `Api.CreateDirectory` |
| `RmDir` | ✅ | 空チェック後 `Api.DeleteInode` |
| `Create` | ✅ | `Api.CreateFile` （空ファイル） |
| `Unlink` | ✅ | `Api.DeleteInode`（参照のなくなったデータも解放） |
| `Rename` | ✅ | `Api.Rename`（RENAME_NOREPLACE 対応） |
| `ChMod` | ✅ | 種別ビットを保持して `Api.UpdateMode` |
| `Chown` | ✅ | `UserResolver` で uid/gid → uname/gname 解決後 `Api.UpdateOwner` |
| `Truncate` | ✅ | `Api.TruncateData`（bytea チャンクもまとめて切り詰め） |
| `UpdateTimestamps` | ✅ | `mtime` のみ DB 反映、`atime` は無視（要件） |
| `Open` | ✅ | 存在確認のみ |
| `Release` | ✅ | no-op |
| `Read` | ✅ | `Api.ReadData`（bytea チャンク経由、穴は 0 埋め） |
| `Write` | ✅ | `Api.WriteData`（bytea チャンク経由、初回時は data 行を自動作成） |
| `StatFS` | ✅ | `pg_database_size` を実使用量に |
| `GetXAttr` | ✅ | `pgfs_inode.xattrs` JSONB から `->>` で取得、値は Base64 で復号 |
| `SetXAttr` | ✅ | JSONB を `||` でマージ、`XATTR_CREATE` / `XATTR_REPLACE` フラグ尊重 |
| `ListXAttr` | ✅ | `jsonb_object_keys` で列挙、NUL 終端形式で返す |
| `RemoveXAttr` | ✅ | JSONB から `-` 演算子で削除 |
| `SymLink` | ✅ | `Api.CreateSymlink`（`S_IFLNK | 0777`, `link_target` 列に格納） |
| `ReadLink` | ✅ | `inode.LinkTarget` を NUL 終端で返す |
| `Link` | ✅ | `Api.CreateHardLink`（同じ `data_id` の inode を追加、`st_nlink` を全リンクで更新） |

凡例: ✅ 完了、⚠️ 部分実装、❌ 未実装 (`-ENOSYS`)。

未実装の操作はありません（どれも `-ENOSYS` を返しません）。

## 他クライアント変更通知 (Notify)

`database.notify_enabled=true` で有効化すると、PostgreSQL `LISTEN` / `NOTIFY` 経由で他クライアントの書き込みを受信し、ローカル `InodeCache` を invalidate する ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs))。複数の `mount.pgfs` / `pgfs.assign` から同じ PG/pgfs を共有マウントしているときに、書き込みクライアントの変更が他クライアントの `stat` / `ls` に反映される。

**Linux 側の制約**: Tmds.Fuse の高レベル API には libfuse の low-level `fuse_lowlevel_notify_inval_*` に相当するエクスポートが無いため、**kernel inode/dentry キャッシュへのアクティブな invalidate ができない**。実用上の影響:

- `attr_timeout=0` (既定) のおかげで `stat` 系のアクティブな問い合わせは毎回 FUSE → 当方の `InodeCache` → DB ヒットになり、最新値が見える。
- ただし `inotify` などの **passive subscriber** には変更が伝わらない (FUSE 経由で起きた更新は、別マシンで起きたものに限らず kernel `fsnotify` を発火しないため)。`watch -n 1 ls` のような能動 polling で代替する想定。
- 将来 vendored Tmds.Fuse に low-level notify を patch すれば inotify も繋がる (今は未対応)。

詳細仕様 / ペイロード / 受信処理は [history.md](history.md) 「他クライアント変更通知 (Notify)」を参照。

## アーキテクチャ

### レイヤ

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Mount.Program           ── 引数解析・OS 判定・マウント │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Mount.FileSystem        ── FUSE コールバック (Linux) │
│   - FillStat / ResolveOwner    ※Linux 固有              │
│   - CurrentUserNames           ※Windows でも応用可能     │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Api.Api             ── DB 操作 (クロスプラットフォーム) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Api.InodeCache      ── inode メモリキャッシュ    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Lib.Utility.Pg          ── Npgsql + Dapper ラッパ   │
└─────────────────────────────────────────────────────────┘
```

### Windows でも共有可能な箇所

FileSystem.cs 内のロジックには `// Windows-shareable:` というコメントで Windows (DokanNet) 側 [`Pgfs.Assign`](../src/assign/) でも転用可能な部分を明示しています。

- **パス分解** (`SplitParent`): 区切り文字を引数化すれば OS 非依存。Mount は `/` 固定。
- **新規 inode の uname/gname 決定** (`CurrentUserNames`): `Environment.UserName` の取得は両 OS で同じ。Linux 固有なのは getuid()/getgid() 部分だけ。
- **バイト列パス → string 変換** (`PathToString`): UTF-8 デコード自体は OS 非依存。

逆に **Linux 固有** なのは:

- `Tmds.Linux.stat` / `statvfs` / `mode_t` / `uid_t` / `gid_t` / `timespec` を扱う部分（`FillStat`, `StatFS`）
- `getuid()` / `getgid()` の呼び出し（`Tmds.Linux.LibC` 経由）
- `RENAME_NOREPLACE` などの Linux 固有フラグ

これらは Windows 側では NTSTATUS / FileAttributes / DOKAN_FILE_INFO 等に置き換えになります。

## 設計判断・差分・暫定実装

### スレッディング

`SupportsMultiThreading => true` を返し、Tmds.Fuse はマルチスレッドで FUSE コールバックを呼び出します。`InodeCache` は `lock(this)` で同期、`Pg.cs` は `NpgsqlDataSource` の接続プールに任せます。`Api` の各メソッドは単発の SQL 操作なので原子的です。

ただし「複数操作の組み合わせ」(`MkDir` の親存在チェック → CreateDirectory) は **非原子**です。同時に同じパスへ `MkDir` が来ると DB の `UNIQUE(parent_id, name)` で 1 つだけ成功し、もう一方は `Api.CreateDirectory` が null を返すので `-EEXIST` が返ります。これは妥当な挙動。

### 認証情報の扱い

- **新規 inode の uname/gname**: `Environment.UserName` を採用。要件では `getuid()` で取得した実効ユーザー名となっていますが、`Environment.UserName` で実質同等なため。
- **uid/gid 解決**: [`UserResolver`](../src/mount/src/UserResolver.cs) で `getpwnam` / `getpwuid` / `getgrnam` / `getgrgid` を libc P/Invoke。結果はメモリにキャッシュするので、`ls -al` を繰り返しても libc を再度叩かない。
- **`Chown`**: 渡された uid/gid を `UnameOf`/`GnameOf` で名前に解決し、`Api.UpdateOwner` で DB を書き換える。`uid == 0xFFFFFFFF (-1)` の慣習 (=変更しない) を尊重。

### データ I/O (Read / Write)

PGFS のデータ本体は `pgfs_data` + `pgfs_data_chunk` (1 行 = 1 bytea) に分割される設計で、Mount 側からの読み書きは **実装済み**:

- `Read` は `Api.ReadData` が `substring(payload from N for M)` で bytea チャンクから部分読みし、穴は 0 埋め (PG 13+ の partial TOAST detoast 効果)。
- `Write` は `Api.WriteData` がチャンク行を必要に応じて自動作成、書き込み後に `st_size` を伸ばす。1 SQL/チャンクの upsert (`INSERT ... ON CONFLICT DO UPDATE SET payload = CASE ... END`) で完結、並行 write race は PG の行ロックで自動直列化。
- `Truncate` (および `Open(O_TRUNC)`) は `Api.TruncateData` が末尾チャンクの payload を `substring` で切り詰め (or 0 パディングで拡張)、不要チャンクは DELETE。
- `Create` は空ファイルの inode を作成。最初の `Write` 時にチャンク行が作られる。

> データ本体は Citus で分散できるよう bytea チャンクで保持している (1 ファイル = 1 shard)。詳細は [docs/support_for_citus.md](support_for_citus.md)。

### 拡張属性・シンボリックリンク・ハードリンク

すべて実装済み:

- 拡張属性は `pgfs_inode.xattrs` JSONB に Base64 で格納 (`Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr`)。`GetXAttr` / `ListXAttr` はキャッシュにある `Inode.Xattrs` JSON をローカル解析する (SELinux の `security.selinux` 頻繁プローブ対策)。
- シンボリックリンクは `link_target` 列に格納 (`Api.CreateSymlink`)。`ReadLink` は NUL 終端で返す。
- ハードリンクは同じ `data_id` を共有する複数 inode を作成 (`Api.CreateHardLink`)。`st_nlink` は全リンクで同期更新。

### `st_atime`

要件: 最終アクセス時刻は保持せず、`st_mtime` と同じ値を返す。実装もそのとおりです (`FillStat` で `s.st_atim = mtime.ToTimespec()`)。

### マウントオプション

既定の FUSE オプションは `attr_timeout=0` (kernel attr キャッシュ無効化。これにより `ln` 直後の `stat` が最新の `st_nlink` を見る) に加え、fstab `-o ...` で受け取ったフラグ。後勝ちなので、ユーザーが `-o` で同名キーを上書きすれば効く。

### シャットダウン

`Ctrl+C` 受信時に `LazyUnmount` を試みます (`fusermount3 -uz` 相当)。プロセスが kill された場合は `fusermount3 -u <mountpoint>` で手動 unmount してください。

## 既知の制限・TODO

| 項目 | 状態 | メモ |
|---|---|---|
| データ I/O (Read/Write) | ✅ | `Api.ReadData` / `Api.WriteData` を bytea チャンクで実装 |
| `uid/gid` ↔ `uname/gname` の双方向解決 | ✅ | [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs) で libc P/Invoke |
| `Chown` の uname/gname 反映 | ✅ | `Api.UpdateOwner` を呼ぶ。`uid == -1` は変更しない慣習も尊重 |
| 拡張属性 (xattr) | ✅ | `Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr` 実装。値は Base64 で JSONB に格納 |
| シンボリックリンク | ✅ | `Api.CreateSymlink` / `ReadLink` 実装 |
| ハードリンク | ✅ | `Api.CreateHardLink` 実装。`Unlink` で残り inode の `st_nlink` を更新 |
| Truncate のチャンク削減 | ✅ | `Api.TruncateData` が新サイズを超えるチャンクを DELETE し、末端は `substring` で切り詰め |
| macOS 動作確認 | ❌ | Tmds.Fuse の macOS 対応次第。macFUSE が必要 |
| アクセスチェック (`Access`) | ❌ | 当面マウント時に `default_permissions` を渡せばカーネル側で判断される想定 |
| Mount オプション `-o` | ⚠️ | fstab `-o ...` で受け取ったフラグは転送する。整理した既定セットは順次拡充 |
| 接続失敗時の再接続 | ✅ | [Retry](../src/lib/src/Utility/Retry.cs) で `Pg.OpenConnection` 系を包む。指数バックオフ、`database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` で調整。クエリ実行中の例外は idempotency 問題があるため再試行しない |
| OS に存在しない uname / gname のフォールバック | ✅ | `getpwnam` / `getgrnam` 失敗時、`mount.fallback_uname` / `mount.fallback_gname` (DB 保存、既定 `nobody` / `nogroup`) に解決した uid/gid を返す。fallback 名自体が解決できなければ uid=65534 (NFS の nobody 慣習値) を hardcode し warning ログ。実装は [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs)、Linux e2e の `test_fallback_uname_gname` で検証 |
| Read/Write のストリーミング | ⚠️ | 現状各チャンクで個別に upsert/read している。大量 I/O では複数チャンク分を 1 往復にまとめる最適化余地 |
| xattr 値のバイナリ表現 | ⚠️ | Base64 で JSONB に格納している（JSON string にバイト列が乗らないため）。OS の getxattr/setxattr とは透過だが、SQL で直接見ると Base64 のままで読みづらい |

## 動作確認シナリオ（Linux 想定）

```bash
# 1. PostgreSQL に PGFS を初期化（[docs/Mkfs.md](Mkfs.md) 参照）
dotnet run --project src/mkfs

# 2. マウントポイントを準備
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# 3. マウント
dotnet run --project src/mount -- -m /mnt/pgfs

# 4. 別ターミナルでアクセス: メタデータ系
ls -la /mnt/pgfs                # ルートディレクトリ
mkdir /mnt/pgfs/hello           # ディレクトリ作成
ls -la /mnt/pgfs                # hello が見える
rmdir /mnt/pgfs/hello           # 削除
touch /mnt/pgfs/empty.txt       # 空ファイル作成
chmod 600 /mnt/pgfs/empty.txt   # パーミッション変更
chown $USER:$USER /mnt/pgfs/empty.txt  # オーナー変更
rm /mnt/pgfs/empty.txt          # ファイル削除

# 5. データ I/O
echo "hello world" > /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # → hello world
truncate -s 5 /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # → hello

# 6. シンボリックリンク
ln -s /etc/hostname /mnt/pgfs/host
readlink /mnt/pgfs/host         # → /etc/hostname

# 7. ハードリンク
echo data > /mnt/pgfs/orig.txt
ln /mnt/pgfs/orig.txt /mnt/pgfs/copy.txt
ls -l /mnt/pgfs                 # 両方とも st_nlink=2 になっている
rm /mnt/pgfs/orig.txt
cat /mnt/pgfs/copy.txt          # → data （データはまだ生きている）

# 8. 拡張属性
setfattr -n user.tag -v hello /mnt/pgfs/copy.txt
getfattr -d /mnt/pgfs/copy.txt  # → user.tag="hello"

# 9. アンマウント
fusermount3 -u /mnt/pgfs
# または mount.pgfs を起動しているターミナルで Ctrl+C

# プロセスがクラッシュした場合は手動でアンマウントが必要:
mount | grep pgfs                # /dev/fuse on /mnt/pgfs が残っていたら
fusermount3 -u /mnt/pgfs
# または sudo umount /mnt/pgfs
```

## 参照

- [docs/database.md](database.md) DB スキーマ設計
- [docs/Mkfs.md](Mkfs.md) 初期化ツールの仕様
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) 共通 API
- [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) inode キャッシュ
- [Tmds.Fuse](https://github.com/tmds/Tmds.Fuse) FUSE ライブラリ
