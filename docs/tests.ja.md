# テスト一覧・実行方法・環境要件

pgfs の全テストの **ハブドキュメント**。「どんなテストがあるか / どう実行するか / 何の環境が要るか」をここに集約する。各ランナーの細かいオプション (flow.ps1 のパラメータ等) は各ディレクトリの README を正とし、ここからリンクする。

> **背景**: テスト環境がホスト依存 (Windows ホスト → ssh で linux_client / pgsql_server) でばらついているので、**docker に寄せて再現性を上げられないか** を検討するための棚卸しでもある。docker 化の分析は末尾の [§docker 統合の検討](#docker-統合の検討) を参照。

---

## テスト一覧

| スイート | 件数 | 何を見るか | 場所 | 詳細 README |
|---|---|---|---|---|
| **Linux e2e** | 35 | mount.pgfs (FUSE) の全オペレーションを実 FS 操作で確認 (POSIX ACL setfacl/getfacl 含む) | [tests/linux/e2e.sh](../tests/linux/e2e.sh) | [tests/linux/README.ja.md](../tests/linux/README.ja.md) |
| **Linux e2e (full docker)** | 35 | 上記 Linux e2e を **単一 PG + mount コンテナ**で完結 (ssh linux_client / ホスト dotnet 非依存) | [tests/docker/run.sh](../tests/docker/run.sh) | [tests/docker/README.ja.md](../tests/docker/README.ja.md) |
| **Windows e2e** | 26 | pgfs.assign (Dokan) の全オペレーションを実 FS 操作で確認 (ACL 投影 Get/SetFileSecurity 含む) | [tests/windows/e2e.ps1](../tests/windows/e2e.ps1) | [tests/windows/README.ja.md](../tests/windows/README.ja.md) |
| **Citus mkfs マトリックス** | 18 | `mkfs --citus / --worker / --clean` の組合せ挙動 (新規 / 既存維持 / 再構築) | [tests/citus/test_matrix.sh](../tests/citus/test_matrix.sh) | [tests/citus/README.ja.md](../tests/citus/README.ja.md) |
| **Citus multinode probe** | 13 セクション | Citus 仕様の挙動確認 (auto-sync / DDL 伝搬 / shard 配置 等) の one-off probe | [tests/citus/multinode_probe.sh](../tests/citus/multinode_probe.sh) | [tests/citus/README.ja.md](../tests/citus/README.ja.md) |
| **Citus race multinode** | 4 | 多ノード Citus + 2 mount client でのクロスクライアント排他制御 + 多ノード e2e | [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) | [tests/citus/README.ja.md](../tests/citus/README.ja.md) |
| **監査ログ専用** | 12 | 多ノード Citus + 1 mount client で監査ログ固有の振る舞い (各 op 記録 / caller_* / パーティション自動作成 = 月跨ぎ機構 / `audit.enabled=false` で 0 行) | [tests/citus/audit.sh](../tests/citus/audit.sh) | [tests/citus/README.ja.md](../tests/citus/README.ja.md) |
| **Citus verify** | SQL 診断 | 1 ノード Citus セットアップ後の `citus_tables` / 分散キー / shard 配置 / EXPLAIN | [tests/citus/verify.sql](../tests/citus/verify.sql) | [tests/citus/README.ja.md](../tests/citus/README.ja.md) |

### Linux e2e (35 件) のカテゴリ

ディレクトリ操作 / ファイル基本 / データ I/O (bytea) / 名前変更 / 権限 (chmod/chown) / シンボリックリンク / ハードリンク / xattr / **POSIX ACL (setfacl/getfacl)** / メタデータ (StatFS/utime) / 並行性 / 名前解決 fallback。カバー範囲は [docs/Mount.md](Mount.md) の ✅ オペレーション。

### Windows e2e (26 件) のカテゴリ

ディレクトリ操作 / ファイル基本 / データ I/O (bytea) / truncate / 名前変更 / 属性 (ReadOnly/Hidden/System/Archive) / ボリューム・パターン / 並行性。POSIX 専用 (symlink/hardlink/chmod/chown/xattr API) は DokanNet 非対応で対象外、代わりに Windows 固有を追加。カバー範囲は [docs/Assign.md](Assign.md) の ✅/⚠️ オペレーション。

### 最新の結果

| スイート | 結果 | 検証環境 |
|---|---|---|
| Linux e2e | **35/35 ALL PASSED** | 単 PG / 1 ノード Citus (pgsql_server) / 多ノード Citus (docker) いずれも |
| Linux e2e (full docker) | **35/35 ALL PASSED** | 単一 PG (postgres:17) + mount コンテナ、linux_client 上で実機検証 |
| Windows e2e | **26/26 ALL PASSED** | 単 PG / 1 ノード Citus (pgsql_server) |
| Citus mkfs マトリックス | **18/18 PASS** | Citus 14.0.0 docker on linux_client |
| Citus race multinode | **4/4 PASS** | Citus 14.0.0 docker on linux_client |
| 監査ログ専用 | **12/12 PASS** | Citus docker on linux_client |

(各スイートの最新パス状況の正は各ディレクトリ README。)

---

## 実行方法

### Linux e2e

```cmd
REM 全フロー (rsync → publish → mount → test → unmount)
tests\linux\flow.cmd
REM テスト名フィルタ
tests\linux\flow.cmd xattr
REM テストだけ (マウント済み前提)
tests\linux\run.cmd
```

Linux ホスト上で直接:

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

オプション (`-NoSync` / `-NoBuild` / `-NoMount` / `-KeepMounted`) と環境変数 (`PGFS_TEST_PG_EXEC` 等) は [tests/linux/README.ja.md](../tests/linux/README.ja.md)。

### Windows e2e

```cmd
REM 全フロー (mount → test → unmount、ビルドは既定スキップ)
tests\windows\flow.cmd
REM 先に publish してから
tests\windows\flow.cmd -Build
REM テストだけ (マウント済み前提)
tests\windows\run.cmd
```

詳細は [tests/windows/README.ja.md](../tests/windows/README.ja.md)。

### Citus 系 (すべて linux_client 上の bash で実行)

```bash
bash tests/citus/test_matrix.sh        # mkfs 18 ケースマトリックス
bash tests/citus/multinode_probe.sh    # Citus 仕様 probe
bash tests/citus/race_multinode.sh     # クロスクライアント排他制御 + 多ノード e2e
bash tests/citus/audit.sh              # 監査ログ専用 (各 op 記録 / caller_* / パーティション / enabled=false)
```

```cmd
REM 1 ノード Citus 構成診断 (Windows ホスト → ssh pgsql_server)
tests\citus\verify.cmd
```

詳細は [tests/citus/README.ja.md](../tests/citus/README.ja.md)。

---

## 環境要件

各スイートが **今** 必要としている環境。docker 化の検討材料。

| スイート | 実行ホスト | DB | マウント層 | docker | リモートホスト依存 |
|---|---|---|---|---|---|
| Linux e2e | Windows → ssh Linux | PG (fallback テストは別途 psql 到達が必要) | libfuse3 | なし | **linux_client** (ビルド + mount 実行) |
| Windows e2e | Windows ローカル | PG | Dokan 2.x ドライバ | なし | なし (ローカル完結) |
| Citus mkfs マトリックス | ssh Linux (bash) | **docker Citus** (coord + worker1) | なし (mkfs のみ) | **あり** | linux_client (docker daemon) |
| Citus multinode probe | ssh Linux (bash) | **docker Citus** (coord + worker) | なし | **あり** | linux_client |
| Citus race multinode | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount は **ホスト** で起動) | DB のみ docker | linux_client |
| 監査ログ専用 | ssh Linux (bash) | **docker Citus** (coord + worker1) | libfuse3 (mount は **ホスト** で起動) | DB のみ docker | linux_client |
| Citus verify | Windows → ssh pgsql_server | **1 ノード Citus on pgsql_server** (実機) | なし | なし | **pgsql_server** |

### 共通の前提

- **.NET 10 SDK** (ビルド時)。リモートは `REMOTE_DOTNET` 環境変数で指定 (デフォルトはホスト個別)。
- **PostgreSQL 17+** (Citus テストは Citus 拡張入りイメージ `citusdata/citus:latest`)。
- **Linux**: libfuse3 + `attr` パッケージ (xattr テスト用 `getfattr`/`setfattr`、未インストールなら xattr テストは SKIP)。
- **Windows**: Dokan 2.x ドライバ (`DokanSetup_redist.exe`)。
- **Citus 系**: docker daemon (停止状態からでも各スクリプトが `sudo systemctl start docker` で起動し、trap で元に戻す)。
- リモート実行は **SSH 鍵でパスワード無しログイン可能** であること (linux_client / pgsql_server)。

### 環境変数によるオーバライド

ホスト名・パス・ポート・認証情報などの **ハードコードはすべて環境変数で上書き可能**。docker 化やホスト変更時はスクリプトを編集せず環境変数で向け先を変える。優先順位は **CLI 引数 > 環境変数 > 既定**。

| 対象 | 主な環境変数 |
|---|---|
| Linux flow.ps1 / run.cmd | `REMOTE` / `REMOTE_REPO` / `MOUNT_POINT` / `REMOTE_SETTING_FILE` / `REMOTE_DOTNET` / `REMOTE_MOUNT_BINARY` |
| Windows flow.ps1 / run.cmd | `MOUNT_ROOT` / `ASSIGN_BINARY` / `ASSIGN_SETTING_FILE` |
| Linux e2e.sh (fallback テスト) | `PGFS_TEST_PG_EXEC` (psql 呼び出し全文を差し替え) / `TEST_FILTER` |
| Citus `*.sh` | `PGFS_PROBE_IMAGE` / `COORD_NAME` / `WORKER1_NAME` / `COORD_PORT` / `WORKER1_PORT` / `SUPER_USER` / `SUPER_PASSWORD` / `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` / `MKFS_BIN` / `MOUNT_BIN` / `PGFS_TEST_LOG` (詳細は [tests/citus/README.ja.md](../tests/citus/README.ja.md)) |
| Citus verify.cmd | `VERIFY_REMOTE` (既定 pgsql_server) |

各スイートの完全な変数一覧は各ディレクトリ README 参照。

### 残るハードコード前提 (env で吸収しきれないもの)

- **リモートビルド先が symlink 越し**になる linux_client 固有の構成によって `dotnet publish` の並列 restore で race が出る件。`-p:RestoreDisableParallel=true` を flow.ps1 に組み込んで回避済み (docker 化で解消する想定)。
- **Citus verify** の前提となる実機 1 ノード Citus セットアップ自体 (`VERIFY_REMOTE` で接続先は変えられるが、そのホストに Citus がセットアップ済みである必要)。

---

## docker 統合の検討

> 「すべて docker にした方がやりやすいのでは」という動機に対する現状分析。**単一 PG 構成は実装済み** ([tests/docker/](../tests/docker/README.ja.md))。残りは [docs/next.md](next.md) の運用項目に紐づく。

### 既に docker 化されている部分

Citus 系の DB は既に docker (`citusdata/citus:latest` を `--network host` で coord/worker 起動)。`race_multinode.sh` は **DB を docker + mount をホスト** のハイブリッド。

### Linux 側はフル docker 化できる → **単一 PG 構成は実装済み** ([tests/docker/](../tests/docker/README.ja.md))

`race_multinode.sh` の mount をホストではなくコンテナに移せば、Linux e2e は **PG コンテナ + mount.pgfs コンテナ** で完結する。**単一 PG (非 Citus) 構成を [tests/docker/](../tests/docker/README.ja.md) として実装** (multi-stage SDK ビルド + docker-compose):

- **mount.pgfs コンテナ** ([Dockerfile.mount](../tests/docker/Dockerfile.mount)): `cap_add SYS_ADMIN` / `devices /dev/fuse` / `security_opt apparmor:unconfined` で FUSE をコンテナ内マウント。多段ビルドで `mount.pgfs`/`mkfs.pgfs` を self-contained publish → fuse3 + attr/acl/psql 入り debian-slim に COPY。
- **e2e はコンテナ内で実行**: FUSE マウントをホストに見せる方式 (mount namespace 伝播) は脆いので、`e2e.sh` を mount コンテナ内で走らせる (マウントポイントもコンテナ内)。`tests/linux/` は read-only bind mount で持ち込み (テスト編集時の再ビルド不要)。
- **fallback テスト**: `PGFS_TEST_PG_EXEC="psql -h coord ..."` を [run.sh](../tests/docker/run.sh) が渡し、compose network 越しの psql で DB 直接操作。

単一 PG 構成では **ssh linux_client 依存と symlink race 回避策が不要** になった。残るは多ノード Citus + race + audit のフル docker 化 ([docs/next.md](next.md) #9、雛形は `race_multinode.sh`)。

> **gotcha (runtime base の固定)**: `debian:stable-slim` は現在 Debian 13 (trixie) を指し、libfuse 3.17 が SONAME を `libfuse3.so.4` に bump している。Tmds.Fuse は `libfuse3.so.3` を dlopen するため trixie ベースだと `CheckDependencies` が「libfuse 未検出」で落ちる。[Dockerfile.mount](../tests/docker/Dockerfile.mount) は **`debian:bookworm-slim` (Debian 12, libfuse 3.14 = `libfuse3.so.3`) に固定**して回避している。

### Windows 側は docker 化できない

Dokan は **Windows カーネルドライバ** で、Windows コンテナでも FUSE 相当のマウントを提供できない (Dokan のユーザモード API はカーネルドライバ前提)。Windows e2e は **Windows ホスト直** が必須のまま。docker 化の対象外。

### 当面のおすすめ

1. ~~Linux e2e を `tests/docker/` に置く~~ → **単一 PG 構成は実装済み** ([tests/docker/](../tests/docker/README.ja.md))。次は Citus 多ノード + race + audit を同じ枠に拡張 (`race_multinode.sh` が雛形)。
2. `verify` の pgsql_server ハードコードも docker Citus に向けられるよう env オーバライド化 (Linux e2e の `PGFS_TEST_PG_EXEC` と同じ手口)。
3. Windows e2e はホスト前提のまま、docker 化のスコープ外と明記。

---

## 関連

- [docs/Mount.md](Mount.md) — Linux e2e がカバーする FUSE オペレーション一覧
- [docs/Assign.md](Assign.md) — Windows e2e がカバーする Dokan オペレーション一覧
- [docs/support_for_citus.md](support_for_citus.md) — Citus テストが検証する設計
- [docs/next.md](next.md) — 未着手のテスト関連項目 (多ノード Windows e2e / flow.cmd の harness 出力問題 / docker 統合)
