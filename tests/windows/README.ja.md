# pgfs Windows e2e テスト

> 全テストの一覧 / 環境要件 / docker 統合の検討は [docs/tests.md](../../docs/tests.md) (ハブ) を参照。本 README はこのディレクトリのランナー (`e2e.ps1` / `flow.ps1` / `run.cmd`) の操作詳細を扱う。

pgfs.assign (Windows / Dokan) でマウント済みの PGFS に対して、実装済み機能 ([docs/Assign.md](../../docs/Assign.md)) を一括で動作確認する e2e テスト。

Linux 版 ([tests/linux/](../linux/README.ja.md)) と対になる Windows 版で、テストの構造はほぼ同じ。Windows 固有の操作 (ReadOnly / Hidden / System / Archive 属性 / ボリューム情報 / ワイルドカード検索) を追加し、Windows 側に存在しない操作 (POSIX symlink / hardlink / chmod / chown / 任意 xattr の API 公開) は対象外。

## ファイル

| ファイル | 内容 |
|---|---|
| [e2e.ps1](e2e.ps1) | テスト本体 (PowerShell) |
| [run.cmd](run.cmd) | テスト**だけ**実行 (マウント済み前提) |
| [flow.ps1](flow.ps1) | **全フロー**: (任意ビルド) → mount → test → unmount |
| [flow.cmd](flow.cmd) | `flow.ps1` の cmd ラッパー |

## シナリオの方針

- テスト開始時に `$MountRoot\test` を再帰削除してから作成
- 全テストは `$MountRoot\test\` 配下でのみ実行する
- 各テストはユニークな接頭辞 (`t01_` / `t02_` ...) を使い相互非干渉
- 最後 (正常終了でも失敗でも) に `$MountRoot\test` を再帰削除

## カバー範囲

[docs/Assign.md](../../docs/Assign.md) で ✅ / ⚠️ になっている Dokan オペレーションを exercise する。

| カテゴリ | テスト |
|---|---|
| **ディレクトリ操作** | mkdir/rmdir、ネストディレクトリ、100 ファイルディレクトリ、非空 rmdir 拒否 |
| **ファイル基本** | new/del、small write/read、append、FileMode.Create で上書き (O_TRUNC 相当) |
| **データ I/O (bytea)** | 2 MiB round-trip (チャンクまたぎ)、truncate 縮小/伸長/ゼロ |
| **名前変更** | Move-Item、サブディレクトリへの Move-Item |
| **属性 / 時刻** | SetFileAttributes (ReadOnly / Hidden / System / Archive)、xattr `user.win_attrs` 往復、ドットファイル fallback、un-hide の永続化、SetFileTime (LastWriteTime) |
| **ボリューム / パターン** | GetVolumeInformation 経由のドライブ情報、FindFilesWithPattern (`-Filter`) |
| **並行性** | 異なるファイルへの並列書き込み、同じファイルからの並列読み出し、並列 mkdir |

## 既知の Assign 側の問題

テスト整備中に判明したもの。e2e は迂回路 (.NET API 直叩き) で通しているが、別途修正対象:

| 問題 | 状態 | メモ |
|---|---|---|
| `Copy-Item` (CopyFileEx) で 2 MiB ファイルが `IOException` | ⚠️ 迂回中 | `[System.IO.File]::WriteAllBytes` 直叩きでは round-trip 成功。CopyFileEx が呼ぶ補助 API (GetFileSecurity / FindStreams / 属性問い合わせ) のいずれかが Dokan 経由で予期しない応答を返している可能性が高い。`test_large_file_round_trip` は WriteAllBytes 経由で通している。CopyFileEx 経路のサポートは別タスク |
| `dokanctl /u` が "Admin rights required" で失敗 | 〇 (回避) | Dokan の仕様で `dokanctl /u` は管理者権限必須。`flow.ps1` は `Process.Kill()` でフォールバック (アンマウント自体は成功し、テスト結果に影響なし) |

## 現状

**26 passed / 0 failed / 0 skipped — ALL PASSED** (単 PG モード / 1 ノード Citus いずれでも 26/26。ACL 投影 `test_getfilesecurity_projection` / 逆投影 `test_setfilesecurity_roundtrip` を追加)。

## カバーしていないもの

[docs/Assign.md](../../docs/Assign.md) の TODO 表の ❌ 項目は未実装のため対象外:

- GetFileSecurity / SetFileSecurity (NotImplemented → カーネル既定 ACL)
- Alternate Data Streams (`file.txt:stream`)
- xattr (DokanNet に xattr API なし)
- symlink / hardlink (DokanNet の `IDokanOperations2` に未対応)
- POSIX 互換 range lock
- Junction (再解析ポイント)

未実装機能の残一覧は [docs/next.md](../../docs/next.md) を参照。

## 使い方

### A. 全フロー (推奨)

`flow.cmd` または `flow.ps1` が `mount → test → unmount` を一括実行します。

```cmd
tests\windows\flow.cmd
```

PowerShell から:

```powershell
.\tests\windows\flow.ps1
```

オプション (どちらの呼び方でも同じ):

| オプション | 動作 |
|---|---|
| `pattern` (位置引数) | テスト名フィルタ。例: `flow.cmd concurrent` で並行テストだけ |
| `-Build` | 事前に `dotnet publish pgfs.sln -c Release` を実行する (既定: スキップ) |
| `-NoMount` | マウント済みとしてテストだけ実行 (mount 操作を一切しない) |
| `-KeepMounted` | テスト後にアンマウントしない (調査用) |

> **Linux 版との違い**: Linux 側の `flow.ps1` は既定でビルドを実行するが、Windows 側は既定でスキップする。Windows は開発機本体で IDE / `dotnet build` を使う前提のため、毎回ビルドし直す必要はない。明示的にビルドしたい場合のみ `-Build` を渡す。

組み合わせ可能:

```cmd
tests\windows\flow.cmd -Build              REM 先に publish してから実行
tests\windows\flow.cmd concurrent          REM "concurrent" を含むテストだけ
tests\windows\flow.cmd -KeepMounted        REM テスト後マウント維持
```

マウントポイントや設定ファイルを変えたい場合は PowerShell 直呼び:

```powershell
.\tests\windows\flow.ps1 -MountPoint R: -SettingFile C:\path\to\pgfs.toml
```

### B. テストだけ (マウント済み前提)

別ターミナルで `assign.pgfs.exe` を稼働させている場合:

```cmd
tests\windows\run.cmd
tests\windows\run.cmd concurrent
```

### C. 手動フロー (各ステップを個別に)

```cmd
REM 1. ビルド (任意 — IDE でビルド済みならスキップ)
dotnet publish pgfs.sln -c Release

REM 2. (別ターミナル) マウント
bin\Publish\assign.pgfs.exe -f pgfs.toml -m P:

REM 3. テスト
tests\windows\run.cmd

REM 4. アンマウント
"C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe" /u P:
```

## テストの絞り込み

第 1 引数にフィルタ文字列を渡すと、テスト名に含まれるものだけ実行する:

```cmd
tests\windows\run.cmd concurrent     # 並列アクセス 3 テスト
tests\windows\run.cmd truncate       # truncate 系 3 テスト
tests\windows\run.cmd rename         # rename 系 2 テスト
```

## 環境変数 / パラメータ

### run.cmd (テストだけ)

| 変数 | 既定 |
|---|---|
| `MOUNT_ROOT` | `P:\` |

例:

```cmd
set MOUNT_ROOT=R:\
tests\windows\run.cmd
```

### flow.ps1 (全フロー)

CLI パラメータと環境変数の両方で設定可能 (優先順位: **CLI 引数 > 環境変数 > 既定**)。`MOUNT_ROOT` は `run.cmd` と共通。

| パラメータ | 環境変数 | 既定 | 用途 |
|---|---|---|---|
| `-MountPoint` | `MOUNT_ROOT` | `P:` | assign.pgfs に渡すマウントポイント (`P:` / `P:\` どちらでも可) |
| `-AssignBinary` | `ASSIGN_BINARY` | `bin\Publish\assign.pgfs.exe` | assign.pgfs.exe のフルパス |
| `-SettingFile` | `ASSIGN_SETTING_FILE` | `pgfs.toml` | 設定ファイルのフルパス |
| `-Filter` (位置 0) | - | (なし) | テスト名フィルタ |
| `-Build` | - | (off) | `dotnet publish` を先に走らせる |
| `-NoMount` | - | (off) | マウント済み前提でテストだけ |
| `-KeepMounted` | - | (off) | テスト後にアンマウントしない |

## PowerShell から直接実行する場合

```powershell
.\tests\windows\e2e.ps1 -MountRoot P:\
.\tests\windows\e2e.ps1 -MountRoot P:\ -Filter concurrent
```

## 出力例

```
=== pgfs.assign ===
  starting: ...\bin\Publish\assign.pgfs.exe -f ...\pgfs.toml -m P:
  mount pid=12345
  mounted at P:\

=== e2e tests ===
=== pgfs Windows e2e tests ===
Mount root: P:\
Test root:  P:\test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...

===========================================
Results: 26 passed, 0 failed, 0 skipped (out of 26)

=== unmount ===
  ...\dokanctl.exe /u P:
  unmounted
  mount log: tests\windows\mount.log (123 lines)

  ALL PASSED
```

## ログ

`flow.ps1` 実行中、`assign.pgfs.exe` の stdout / stderr は以下に保存される (gitignore 推奨):

- `tests\windows\mount.log` — stdout (Trace SQL ログ等)
- `tests\windows\mount.err.log` — stderr

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 全テスト成功 |
| 1 | 一つ以上失敗 |
| 2 | `MountRoot` が存在しない (マウントされていない) |
| 3 | `TestRoot` を作成できない (mount が書き込み不可) |
| 11 | `assign.pgfs.exe` が見つからない (flow のみ) |
| 99 | テスト実行前に例外 (flow のみ) |

## 前提

- **.NET 10 SDK** (ビルド時のみ)
- **Dokan 2.x** ドライバ ([Dokan releases](https://github.com/dokan-dev/dokany/releases) の `DokanSetup_redist.exe`)
  - flow.ps1 は `dokanctl.exe` を `C:\Program Files\Dokan\Dokan Library-*\` 配下から自動検出する
  - 見つからない場合は `Stop-Process` でフォールバック (アンマウントが汚くなる可能性あり)
- マウントポイント (`P:` 等) が既存ドライブと衝突していないこと
- PostgreSQL に PGFS が初期化済み ([docs/Mkfs.md](../../docs/Mkfs.md))
- `pgfs.toml` に DB 接続情報があること
