# pgfs

[English README here](README.md)

> **道順**: **本書が公開リポジトリの入口** › [docs/README.ja.md](docs/README.ja.md) (ドキュメント索引) › 各 doc
>
> **この doc が正である範囲**: pgfs が何であるか、動かすまでの最短手順、リリース対象 doc の一覧。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [docs/README.ja.md](docs/README.ja.md) | **全 doc の索引** |
> | [CHANGELOG.ja.md](CHANGELOG.ja.md) | リリースごとの差分・移行手順・既知の制限 |
> | [docs/Mkfs.ja.md](docs/Mkfs.ja.md) / [docs/Mount.ja.md](docs/Mount.ja.md) / [docs/Assign.ja.md](docs/Assign.ja.md) | 各 CLI の利用者向け仕様 |

**PostgreSQL をバックエンドストレージとして使う FUSE ファイルシステム** を C# (.NET 10) で実装するプロジェクトです。Linux / macOS / Windows で同じ動作を提供することを目標としています。

## 概要

- ファイルシステム全体（ディレクトリエントリ、inode 属性、ファイルデータ、設定値）を **PostgreSQL のテーブル** (`bytea` チャンク) に保存します。
- Linux では **FUSE (内製 libfuse バインディング)** (macOS の実行互換性は未検証) で、Windows では **Dokan (`DokanNet`)** でマウントします。
- 設定値は **TOML** (`pgfs.toml`) と DB の `pgfs_settings` テーブルから読み込み、CLI 引数で上書きできます。
- DB スキーマは [docs/database.ja.md](docs/design/database.ja.md) を参照してください。

> **現状**: Mkfs / Mount / Assign に加え、キャッシュ、write-back、`pgfsctl config` / `status` / `prune`、読み取り GUI を実装している。
> **Linux / 共通 Core のレビュー指摘は全件クローズ済み**で、**write-back (data / metadata) を有効にした Windows 受入も完了**している。
> **いま残っているのは「直っていない不具合」ではなく、意図して選んだ制限**である — 一覧は
> [CHANGELOG.ja.md §既知の制限](CHANGELOG.ja.md) を参照。**テストの件数と検証記録の正は [docs/tests.ja.md](docs/tests.ja.md)**。

> **過去の基盤整備**: 3 本柱 (Mkfs / Mount / Assign) すべて実装完了。Linux / Windows の e2e、多ノード Citus 上の race 検証、監査ログ専用スイートまで通過している (**件数は増えるので本書には書かない。正は [docs/tests.ja.md](docs/tests.ja.md)**)。Citus (水平分散) は Phase 1+2+3 完了、監査ログ完了、ACL/権限の Linux↔Windows 相互運用も実装済 ([docs/permission-interop.ja.md](docs/design/permission-interop.ja.md))。**v0.2.0 で `Lib` を OS 機構層 (`Core` / `Fuse` / `Dokan`) に分割 + libfuse バインディングを内製化** (両 OS e2e 緑で再検証済 — [docs/v0.2.0-plan.ja.md](docs/design/v0.2.0-plan.ja.md))。次にやることの一覧は [docs/next.ja.md](docs/next.ja.md) を参照してください。

## ソリューション構成

| プロジェクト | パス | 役割 | プラットフォーム | 状態 |
|---|---|---|---|---|
| **Core** | [src/core/](src/core/) | OS/機構 非依存のコアライブラリ（Models / Api / Config / Logging / Collections / Utility / Objects） | クロスプラットフォーム | 実装完了 |
| **Fuse** | [src/fuse/](src/fuse/) | Linux/macOS の FS 機構層（内製 libfuse バインディング + FUSE FileSystem + PosixAcl 投影） | Linux / macOS | 実装完了・Linux で動作確認 |
| **Dokan** | [src/dokan/](src/dokan/) | Windows の FS 機構層（Dokan FileSystem + Windows SID/ACL 投影） | Windows | 実装完了・Windows で動作確認 |
| **Mkfs** | [src/mkfs/](src/mkfs/) | PostgreSQL 側のテーブル等を初期化する CLI (`mkfs.pgfs`、薄い exe → Core) | クロスプラットフォーム | 実装完了・Linux で動作確認 |
| **Mount** | [src/mount/](src/mount/) | Linux/macOS 用マウントツール (`mount.pgfs`、薄い exe → Fuse) | Linux / macOS | 主要操作・ACL 投影を実装。既知不具合・未対応操作あり |
| **Assign** | [src/assign/](src/assign/) | Windows 用マウントツール (`assign.pgfs`、薄い exe → Dokan) | Windows | 主要操作・ACL 投影を実装。リンク・ADS 等未対応、最新機能は未検証 |
| **Ctl** | [src/ctl/](src/ctl/) | `pgfsctl config/status` | クロスプラットフォーム | 実装済み |
| **Gui** | [src/gui/](src/gui/) | `pgfsgui` 運用画面 | クロスプラットフォーム | 読み取り MVP、設定編集・配布は未完了 |

## 必要なもの

- **.NET 10 SDK** 以降（[ダウンロード](https://dotnet.microsoft.com/download)）
- **PostgreSQL 17** （データベース側）
- **Linux**: libfuse3 (`fuse3` 等) ※Mount を使う場合のみ。`mount.pgfs` が実行時に `libfuse3.so.3` を dlopen する (同梱しない)
- **Windows**: [Dokan 2.x](https://github.com/dokan-dev/dokany/releases) ※Assign プロジェクトを使う場合のみ
- **macOS**: 実行互換性は未検証。現行 binding は `libfuse3.so.3` を前提とするため、macFUSE を導入するだけで動くとは保証しない

## ビルド

以下はビルド手順です。過去の成功記録と現行ソースの検証は別であり、今回ビルドは実行していません。

```pwsh
# 全プロジェクト一括
dotnet build pgfs.sln

# 個別ビルド
dotnet build src/core/Core.csproj
dotnet build src/mkfs/Mkfs.csproj
dotnet build src/mount/Mount.csproj   # Linux/macOS 実行向け (Windows でもクロスビルドのみ通る)
dotnet build src/assign/Assign.csproj # Windows 実行向け (Linux/macOS でもクロスビルドのみ通る)
```

ビルド成果物は [bin/](bin/) 配下に出力されます (`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` 設定):

| 構成 | 出力先 | 内容 |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `core.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + 依存 dll (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | 同上の Release 版 (framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | CLI は single-file self-contained な `{mkfs,mount,assign}.pgfs[.exe]` と `pgfsctl[.exe]` (ホスト RID 自動)。GUI の同形態の発行設定は未整備 |

アセンブリ名はすべて小文字ドット区切り (`core.pgfs`, `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs`) で、Debug ビルドなら `./bin/Debug/mkfs.pgfs.exe` のように直接起動できます。

### Mount（Linux/macOS）

```pwsh
dotnet build src/mount/Mount.csproj
```

ビルドは Windows でも通ります（クロスコンパイル目的）。実行は Linux/macOS のみ。詳細は [docs/Mount.ja.md](docs/Mount.ja.md) を参照。データ I/O・xattr・リンク等の入口は実装されています。`Access` は**意図して実装していません** — アクセス可否の判定はマウント時の `default_permissions` でカーネルに委ねる設計です。`FAllocate` は未実装です (`-ENOSYS` を返します)。**意図して選んだ制限の一覧は [CHANGELOG.ja.md §既知の制限](CHANGELOG.ja.md)** を参照してください。

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

# ソリューション一括 (CLI と GUI の発行設定は異なる)
dotnet publish pgfs.sln -c Release
```

RID は `UseCurrentRuntimeIdentifier=true` で**ホスト OS から自動判定**。別 OS 向けにビルドしたい場合は `-r <RID>` で上書きできます (例: `dotnet publish ... -c Release -r linux-x64`)。

> AOT 発行 (`PublishAot=true`) は Dapper / Tomlyn / 内製 libfuse binding / DokanNet のリフレクション・P/Invoke 依存により当面動きません。CLI プロジェクトでは `PublishAot=false` / `PublishTrimmed=false` に設定済みです。GUI の配布は別途整備対象です。

## 使い方

### 1. PostgreSQL を準備

PostgreSQL 17 を起動し、スーパーユーザー（通常 `postgres`）で接続できる状態にします。

### 2. PGFS の初期化

`mkfs.pgfs` で PGFS が使うユーザー / データベース / スキーマ / テーブル / 初期データを作成します。

```bash
# 既定値で初期化（localhost:5432 / postgres スーパーユーザー / pgfs ユーザー＋DB を新規作成）
dotnet run --project src/mkfs -- -f pgfs.toml

# スーパーユーザー接続を明示
dotnet run --project src/mkfs -- -f pgfs.toml \
    --super-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1"

# PGFS ユーザー接続を明示
dotnet run --project src/mkfs -- -f pgfs.toml \
    --connection "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs"

# スキーマ・プレフィックスを変える
dotnet run --project src/mkfs -- -f pgfs.toml -s myschema -x myfs_

# ヘルプ
dotnet run --project src/mkfs -- --help
```

完了すると `-f` で指定した場所に `pgfs.toml`（配布用の設定ファイル）が書き出されます。**`-f` は必須**で、ファイルがあれば読み込んでそこへ書き戻し、無ければ新規作成します。詳細は [docs/Mkfs.ja.md](docs/Mkfs.ja.md) を参照してください。

### 3. マウント

Linux なら `mount.pgfs` でマウントできます。macOS は未検証です。

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

Windows なら `assign.pgfs` でドライブにマウントできます (Dokan2 ドライバが必要):

```pwsh
dotnet run --project src\assign -- -m P:
# 別ターミナルで:
Get-ChildItem P:\
```

詳細は [docs/Assign.ja.md](docs/Assign.ja.md) を参照。ログオン時に常駐させる手順とスクリプトは [docs/Assign.ja.md §ログオン時に常駐させる](docs/Assign.ja.md) / [scripts/windows/](scripts/windows/pgfs-mount.ps1)。

## ドキュメント

- [docs/README.ja.md](docs/README.ja.md) - ドキュメント索引と読み順
- [docs/design/windows-parity.ja.md](docs/design/windows-parity.ja.md) - Linux 機能の Windows 展開設計（候補・推奨・受入条件）
- [docs/next.ja.md](docs/next.ja.md) - **次にやること** (優先度順、作業ごとに更新)
- [docs/architecture.ja.md](docs/architecture.ja.md) - プロジェクト構成 / 依存パッケージ / Lib 内部 / ビルド・実行
- [docs/Mkfs.ja.md](docs/Mkfs.ja.md) - `mkfs.pgfs` の仕様
- [docs/Mount.ja.md](docs/Mount.ja.md) - `mount.pgfs` の仕様 (Linux/macOS)
- [docs/Assign.ja.md](docs/Assign.ja.md) - `assign.pgfs` の仕様 (Windows)
- [docs/Pgfsctl.ja.md](docs/Pgfsctl.ja.md) - `pgfsctl` の仕様 (実行時コントロールプレーン CLI — `config` / `status` / `prune`)
- [docs/database.ja.md](docs/design/database.ja.md) - PostgreSQL スキーマ設計
- [docs/ddl/](docs/ddl/README.ja.md) - テーブル単位の DDL
- [docs/coding-style.ja.md](docs/design/coding-style.ja.md) - C# コーディング規約
- [docs/performance.ja.md](docs/design/performance.ja.md) - 性能改善の実装・測定記録と残候補
- [docs/support_for_citus.ja.md](docs/design/support_for_citus.ja.md) - Citus (水平分散) 対応の設計メモ (Phase 1+2+3 完了)
- [docs/history.ja.md](docs/history.ja.md) - 現行設計に至る経緯 (モデル整理 / Tmds.Fuse fork / fstab / Retry / race 修正 / Citus 等)
- [docs/fstab-support.ja.md](docs/design/fstab-support.ja.md) - `/etc/fstab` 対応 (過去の起動時マウント確認記録と現行の制約)
- [docs/tests.ja.md](docs/tests.ja.md) - **テストのハブ** (全テスト一覧 / 実行方法 / 環境要件 / docker 統合の検討)。各ランナー詳細は [tests/linux/](tests/linux/README.ja.md) / [tests/windows/](tests/windows/README.ja.md) / [tests/citus/](tests/citus/README.ja.md)

## ライセンス

[LICENSE](LICENSE) を参照してください（MIT ライセンス）。
