# pgfs Linux e2e テスト

> 全テストの一覧 / 環境要件 / docker 統合の検討は [docs/tests.md](../../docs/tests.md) (ハブ) を参照。本 README はこのディレクトリのランナー (`e2e.sh` / `flow.ps1` / `run.cmd`) の操作詳細を扱う。

mount.pgfs (Linux) でマウント済みの PGFS に対して、実装済み機能 ([docs/Mount.md](../../docs/Mount.md)) を一括で動作確認する e2e テスト。

## ファイル

| ファイル | 内容 |
|---|---|
| [e2e.sh](e2e.sh) | bash テスト本体 (Linux 上で実行) |
| [run.cmd](run.cmd) | テスト**だけ**実行 (マウント済み前提) |
| [flow.ps1](flow.ps1) | **全フロー**: rsync → publish → mount → test → unmount (PowerShell) |
| [flow.cmd](flow.cmd) | `flow.ps1` の cmd ラッパー |

## シナリオの方針

- テスト開始時に `$MOUNT_ROOT/test` を `rm -rf` してから `mkdir`
- 全テストは `$MOUNT_ROOT/test/` 配下でのみ実行する
- 各テストはユニークな接頭辞 (`t01_` / `t02_` ...) を使い相互非干渉
- 最後 (正常終了でも失敗でも) に `$MOUNT_ROOT/test` を `rm -rf`

## カバー範囲

[docs/Mount.md](../../docs/Mount.md) で ✅ になっている全 FUSE オペレーションを exercise する。

| カテゴリ | テスト |
|---|---|
| **ディレクトリ操作** | mkdir/rmdir、ネストディレクトリ、100 ファイルディレクトリ、非空 rmdir 拒否 |
| **ファイル基本** | touch/unlink、small write/read、append (O_APPEND)、O_TRUNC で上書き |
| **データ I/O (bytea)** | 2 MiB round-trip (チャンクまたぎ)、truncate 縮小/伸長/ゼロ |
| **名前変更** | 通常 mv、サブディレクトリへの mv |
| **権限** | chmod (ファイル/ディレクトリ)、chown 自分自身 |
| **シンボリックリンク** | 相対パス、dangling、絶対パス |
| **ハードリンク** | 基本、片方削除後の生存、サブディレクトリまたぎ |
| **xattr** | set/get、list、remove、上書き |
| **メタデータ** | StatFS (df)、utime (touch -d / UTIME_NOW) |
| **並行性** | 異なるファイルへの並列書き込み、同じファイルからの並列読み出し、並列 mkdir |
| **名前解決 fallback** | DB 直接 INSERT で存在しない uname/gname の inode を作って `stat` が `nobody`/`nogroup` を返すこと (`ssh pgsql_server` で psql 経由、未到達なら SKIP) |

## 使い方

### A. 全フロー (推奨)

`flow.cmd` または `flow.ps1` が `rsync → publish → mount → test → unmount` を一括実行します。

```cmd
tests\linux\flow.cmd
```

PowerShell から:

```powershell
.\tests\linux\flow.ps1
```

オプション (どちらの呼び方でも同じ):

| オプション | 動作 |
|---|---|
| `xattr` (位置引数) | テスト名フィルタ。例: `flow.cmd xattr` で xattr 系 4 件だけ |
| `-NoSync` | rsync をスキップ (既に同期済み) |
| `-NoBuild` | `dotnet publish` をスキップ |
| `-NoMount` | マウント済みとしてテストだけ |
| `-KeepMounted` | テスト後にアンマウントしない (調査用) |

組み合わせ可能:

```cmd
tests\linux\flow.cmd -NoBuild              REM rsync + mount + test + unmount
tests\linux\flow.cmd -NoSync -NoBuild      REM mount + test + unmount
tests\linux\flow.cmd hardlink -KeepMounted REM hardlink テスト後、マウント維持
```

接続先や パスを変えたい場合は環境変数または PowerShell パラメータ:

```powershell
.\tests\linux\flow.ps1 -Remote other-host -MountPoint /tmp/m
```

### B. テストだけ (マウント済み前提)

既に別ターミナルで mount.pgfs を稼働させている場合:

```cmd
tests\linux\run.cmd
tests\linux\run.cmd xattr
```

### C. 手動フロー (各ステップを個別に)

下記は環境変数の意味例。実際のパスは `$REMOTE` (ホスト) / `$REMOTE_REPO` (リモート checkout dir) / `$REMOTE_DOTNET` / `$REMOTE_SETTING_FILE` / `$MOUNT_POINT` を各環境に合わせて設定する。

```cmd
REM 1. 同期
bash -c "rsync -avz --delete ./ $REMOTE:$REMOTE_REPO/"

REM 2. ビルド
bash -c "ssh $REMOTE $REMOTE_DOTNET publish $REMOTE_REPO/pgfs.sln"

REM 3. (別ターミナル) マウント
bash -c "ssh $REMOTE $REMOTE_REPO/bin/Publish/mount.pgfs --setting-file $REMOTE_SETTING_FILE --mount-point $MOUNT_POINT"

REM 4. テスト
tests\linux\run.cmd

REM 5. アンマウント
bash -c "ssh $REMOTE fusermount3 -u $MOUNT_POINT"
```

## テストの絞り込み

第 1 引数にフィルタ文字列を渡すと、テスト名に含まれるものだけ実行する:

```cmd
tests\linux\run.cmd xattr          # xattr 系 4 テスト
tests\linux\run.cmd hardlink       # ハードリンク 3 テスト
tests\linux\run.cmd concurrent     # 並行アクセス 3 テスト
```

## 環境変数 / パラメータ

### run.cmd (テストだけ)

| 変数 | 既定 |
|---|---|
| `REMOTE` | `linux_client` |
| `REMOTE_REPO` | `~/project/pgfs_cs` (実値はホスト個別) |
| `MOUNT_POINT` | `~/mnt/pgfs` (実値はホスト個別) |

例:

```cmd
set REMOTE=other-host
set MOUNT_POINT=/tmp/pgfs_mount
tests\linux\run.cmd
```

### flow.ps1 (全フロー)

CLI パラメータと環境変数の両方で設定可能 (優先順位: **CLI 引数 > 環境変数 > 既定**)。環境変数は `run.cmd` と共通のものは同名。

| パラメータ | 環境変数 | 既定 | 用途 |
|---|---|---|---|
| `-Remote` | `REMOTE` | `linux_client` | SSH 接続先 |
| `-RemoteRepo` | `REMOTE_REPO` | `~/project/pgfs_cs` | リモート側リポジトリパス |
| `-MountBinary` | `REMOTE_MOUNT_BINARY` | `${RemoteRepo}/bin/Publish/mount.pgfs` | mount.pgfs バイナリパス |
| `-MountPoint` | `MOUNT_POINT` | `~/mnt/pgfs` | マウントポイント |
| `-SettingFile` | `REMOTE_SETTING_FILE` | `~/pgfs.toml` | 設定ファイルパス |
| `-DotnetPath` | `REMOTE_DOTNET` | `~/dotnet/10.0.300/dotnet` | dotnet バイナリ |
| `-Filter` (位置 0) | - | (なし) | テスト名フィルタ |
| `-NoSync` | - | - | rsync スキップ |
| `-NoBuild` | - | - | publish スキップ |
| `-NoMount` | - | - | マウント済み前提でテストだけ |
| `-KeepMounted` | - | - | テスト後にアンマウントしない |

例 (環境変数で別ホストに向ける):

```powershell
$env:REMOTE = "other-host"; $env:MOUNT_POINT = "/tmp/pgfs_mount"
.\tests\linux\flow.ps1 -NoBuild
```

## Linux 上で直接実行する場合

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

### 環境変数

| 変数 | 用途 |
|---|---|
| `TEST_FILTER` | テスト名に部分一致するものだけ実行 |
| `PGFS_TEST_PG_EXEC` | `test_fallback_uname_gname` が使う psql 呼び出し全文をオーバライド (既定: pgsql_server 上のソースビルド psql を ssh 経由で叩く形)。docker Citus に向けるときは `docker exec -i <container> psql ...` を設定する。[tests/citus/race_multinode.sh](../citus/race_multinode.sh) が使用 |

## 出力例

```
=== mount.pgfs ===
  mounted at /mnt/pgfs

=== e2e tests ===
=== pgfs Linux e2e tests ===
Mount root: /mnt/pgfs
Test root:  /mnt/pgfs/test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...
PASS: test_concurrent_mkdir_diff_dirs
PASS: test_fallback_uname_gname

===========================================
Results: 35 passed, 0 failed, 0 skipped (out of 35)

=== unmount ===
  unmounted
  mount log: tests\linux\mount.log (6 lines)

  ALL PASSED
```

## 現状

**35 passed / 0 failed / 0 skipped — ALL PASSED** (単 PG モード + 1 ノード Citus + 多ノード Citus on docker、いずれも 35/35。POSIX ACL `test_posix_acl_named_user` を追加)。多ノード Citus 検証は [tests/citus/race_multinode.sh](../citus/README.md) 経由。

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 全テスト成功 |
| 1 | 一つ以上失敗 |
| 2 | `MOUNT_ROOT` が存在しない (マウントされていない) |
| 3 | `TEST_ROOT` を作成できない (mount が書き込み不可) |

## 前提パッケージ

xattr 系テストで `attr` パッケージ (`getfattr` / `setfattr`) を使用。未インストールの場合、xattr テストは `SKIP` 表示で飛ばされる:

```bash
sudo apt install attr   # Debian/Ubuntu
sudo dnf install attr   # Fedora/RHEL
```

## カバーしていないもの

[docs/Mount.md](../../docs/Mount.md) の TODO 表の ❌ 項目は未実装のため対象外:

- macOS 動作確認 (Tmds.Fuse の macOS 対応次第)
- Access チェック (`Access` 操作)
- Mount オプション `-o` (フル対応)

未実装機能の残一覧は [docs/next.md](../../docs/next.md) を参照。
