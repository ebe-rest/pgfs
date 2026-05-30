# pgfs

**PostgreSQL をバックエンドストレージとして使う FUSE ファイルシステム** を C# (.NET 10) で実装するプロジェクトです。Linux / macOS / Windows で同じ動作を提供することを目標としています。

[English README here](README.md)

## 概要

- ファイルシステム全体（ディレクトリエントリ、inode 属性、ファイルデータ、設定値）を **PostgreSQL のテーブル** (`bytea` チャンク) に保存します。
- Linux / macOS では **FUSE (`Tmds.Fuse`)** で、Windows では **Dokan (`DokanNet`)** でマウントします。
- 設定値は **TOML** (`pgfs.toml`) と DB の `pgfs_settings` テーブルから読み込み、CLI 引数で上書きできます。
- DB スキーマは [docs/database.ja.md](docs/database.ja.md) を参照してください。

> **現状**: 3 本柱 (Mkfs / Mount / Assign) すべて実装が完了し、Linux e2e 34/34 + Windows e2e 24/24 + 多ノード Citus 上の race 検証 4/4 まで通過。Citus (水平分散) は完了。次にやることの一覧は [docs/next.ja.md](docs/next.ja.md) を参照してください。

## ソリューション構成

| プロジェクト | パス | 役割 | プラットフォーム | 状態 |
|---|---|---|---|---|
| **Lib** | [src/lib/](src/lib/) | コアライブラリ（Models / Api / Logging / Collections / Utility / Objects） | クロスプラットフォーム | 実装完了 |
| **Mkfs** | [src/mkfs/](src/mkfs/) | PostgreSQL 側のテーブル等を初期化する CLI (`mkfs.pgfs`) | クロスプラットフォーム | 実装完了・Linux で動作確認 |
| **Mount** | [src/mount/](src/mount/) | Linux/macOS 用マウントツール (`mount.pgfs`、Tmds.Fuse) | Linux / macOS | 全 FUSE 操作実装済み（データ I/O・xattr・symlink・hard link 含む）・Linux で動作確認 |
| **Assign** | [src/assign/](src/assign/) | Windows 用マウントツール (`pgfs.assign`、DokanNet) | Windows | 全 Dokan 操作実装済み（ACL と ADS は未対応）・Windows で動作確認 |

## 必要なもの

- **.NET 10 SDK** 以降（[ダウンロード](https://dotnet.microsoft.com/download)）
- **PostgreSQL 17** （データベース側）
- **Linux**: libfuse3 (Tmds.Fuse 用) ※Mount プロジェクトを使う場合のみ
- **Windows**: [Dokan 2.x](https://github.com/dokan-dev/dokany/releases) ※Assign プロジェクトを使う場合のみ
- **macOS**: macFUSE （限定的対応）

## ビルド

ソリューション一括ビルドが通ります（0 警告 0 エラー）。

```pwsh
# 全プロジェクト一括
dotnet build pgfs.sln

# 個別ビルド
dotnet build src/lib/Lib.csproj
dotnet build src/mkfs/Mkfs.csproj
dotnet build src/mount/Mount.csproj   # Linux/macOS 実行向け (Windows でもクロスビルドのみ通る)
dotnet build src/assign/Assign.csproj # Windows 実行向け (Linux/macOS でもクロスビルドのみ通る)
```

ビルド成果物は [bin/](bin/) 配下に出力されます (`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` 設定):

| 構成 | 出力先 | 内容 |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `lib.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + 依存 dll (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | 同上の Release 版 (framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | single-file self-contained な `{mkfs,mount,assign}.pgfs[.exe]` の 3 ファイル (ホスト RID 自動、各 ~38 MB) |

アセンブリ名はすべて小文字ドット区切り (`lib.pgfs`, `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs`) で、Debug ビルドなら `./bin/Debug/mkfs.pgfs.exe` のように直接起動できます。

### Mount（Linux/macOS）

```pwsh
dotnet build src/mount/Mount.csproj
```

ビルドは Windows でも通ります（クロスコンパイル目的）。実行は Linux/macOS のみ。詳細は [docs/Mount.ja.md](docs/Mount.ja.md) を参照。データの読み書き・拡張属性・シンボリックリンク・ハードリンクを含むすべての FUSE 操作が実装されています。

### Assign（Windows）

```pwsh
dotnet build src/assign/Assign.csproj
```

ビルドは Linux/macOS でも通ります（クロスコンパイル目的）。実行は Windows のみ。Dokan2 のカーネルドライバが事前にインストールされている必要があります。詳細は [docs/Assign.ja.md](docs/Assign.ja.md) を参照。

### 自己完結型実行ファイルの発行

`dotnet publish` でホスト OS 向けの **single-file self-contained** な実行ファイルが [bin/Publish/](bin/Publish/) に出力されます (各 ~38 MB)。

```pwsh
# 個別 publish
dotnet publish src/mkfs/Mkfs.csproj -c Release
dotnet publish src/mount/Mount.csproj -c Release
dotnet publish src/assign/Assign.csproj -c Release

# ソリューション一括 (Mkfs/Mount/Assign の 3 つの exe が bin/Publish/ に並ぶ)
dotnet publish pgfs.sln -c Release
```

RID は `UseCurrentRuntimeIdentifier=true` で**ホスト OS から自動判定**。別 OS 向けにビルドしたい場合は `-r <RID>` で上書きできます (例: `dotnet publish ... -c Release -r linux-x64`)。

> AOT 発行 (`PublishAot=true`) は Dapper / Tomlyn / Tmds.Fuse / DokanNet のリフレクション依存により当面動きません。全プロジェクトで `PublishAot=false` / `PublishTrimmed=false` に設定済みです。

## 使い方

### 1. PostgreSQL を準備

PostgreSQL 17 を起動し、スーパーユーザー（通常 `postgres`）で接続できる状態にします。

### 2. PGFS の初期化

`mkfs.pgfs` で PGFS が使うユーザー / データベース / スキーマ / テーブル / 初期データを作成します。

```bash
# 既定値で初期化（localhost:5432 / postgres スーパーユーザー / pgfs ユーザー＋DB を新規作成）
dotnet run --project src/mkfs

# スーパーユーザー接続を明示
dotnet run --project src/mkfs -- \
    --super-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1"

# PGFS ユーザー接続を明示
dotnet run --project src/mkfs -- \
    --connection "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs"

# スキーマ・プレフィックスを変える
dotnet run --project src/mkfs -- -s myschema -x myfs_

# ヘルプ
dotnet run --project src/mkfs -- --help
```

完了すると `pgfs.toml`（設定ファイル）がカレントディレクトリに書き出されます。詳細は [docs/Mkfs.ja.md](docs/Mkfs.ja.md) を参照してください。

### 3. マウント

Linux/macOS なら `mount.pgfs` でマウントできます。

```bash
# マウントポイントを準備
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# マウント
dotnet run --project src/mount -- -m /mnt/pgfs

# 別ターミナルで
ls -la /mnt/pgfs

# アンマウント
fusermount3 -u /mnt/pgfs
```

データ I/O・拡張属性・シンボリックリンク・ハードリンクすべてに対応しています。詳細は [docs/Mount.ja.md](docs/Mount.ja.md) を参照。

Windows なら `pgfs.assign` でドライブにマウントできます (Dokan2 ドライバが必要):

```pwsh
dotnet run --project src\assign -- -m P:
# 別ターミナルで:
Get-ChildItem P:\
```

詳細は [docs/Assign.ja.md](docs/Assign.ja.md) を参照。

## ドキュメント

各ドキュメントは英語版 (`.md`) と日本語版 (`.ja.md`) があります。

- [docs/next.ja.md](docs/next.ja.md) - **次にやること** (優先度順、作業ごとに更新)
- [docs/architecture.ja.md](docs/architecture.ja.md) - プロジェクト構成 / 依存パッケージ / Lib 内部 / ビルド・実行
- [docs/Mkfs.ja.md](docs/Mkfs.ja.md) - `mkfs.pgfs` の仕様
- [docs/Mount.ja.md](docs/Mount.ja.md) - `mount.pgfs` の仕様 (Linux/macOS)
- [docs/Assign.ja.md](docs/Assign.ja.md) - `pgfs.assign` の仕様 (Windows)
- [docs/database.ja.md](docs/database.ja.md) - PostgreSQL スキーマ設計
- [docs/ddl/](docs/ddl/README.ja.md) - テーブル単位の DDL
- [docs/coding-style.ja.md](docs/coding-style.ja.md) - C# コーディング規約
- [docs/settings-matrix.ja.md](docs/settings-matrix.ja.md) - 設定項目マトリックス (CLI / TOML / DB / 既定 / 参照タイミング)
- [docs/performance.ja.md](docs/performance.ja.md) - 性能改善候補
- [docs/support_for_citus.ja.md](docs/support_for_citus.ja.md) - Citus (水平分散) 対応の設計メモ
- [docs/history.ja.md](docs/history.ja.md) - 現行設計に至る設計判断
- [docs/fstab-support.ja.md](docs/fstab-support.ja.md) - `/etc/fstab` 対応
- [docs/audit-log.ja.md](docs/audit-log.ja.md) - 監査ログ
- [docs/tests.ja.md](docs/tests.ja.md) - **テストのハブ** (全テスト一覧 / 実行方法 / 環境要件 / docker 統合の検討)。各ランナー詳細は [tests/linux/](tests/linux/README.ja.md) / [tests/windows/](tests/windows/README.ja.md) / [tests/citus/](tests/citus/README.ja.md)

## ライセンス

[LICENSE](LICENSE) を参照してください（MIT ライセンス）。
