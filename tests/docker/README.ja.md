# Linux e2e (full docker)

> **道順**: [docs/README.ja.md](../../docs/README.ja.md) › [docs/tests.ja.md](../../docs/tests.ja.md) (テストのハブ) › **本書**

ホスト依存 (ssh linux_client / ホストの dotnet / symlink-race 回避策) を排除し、**Linux e2e を docker だけで完結**させる構成。CI でも再現可能にするのが目的。

スコープは現状 **単一 PG (非 Citus)**。多ノード Citus + race + audit のフル docker 化は次段階 ([docs/next.ja.md](../../docs/next.ja.md) #9)、雛形は [tests/citus/race_multinode.sh](../citus/race_multinode.sh)。

> Windows e2e は Dokan (Windows カーネルドライバ) のため docker 化対象外。[docs/tests.ja.md](../../docs/tests.ja.md) 参照。

---

## 構成

| 要素 | 役割 |
|---|---|
| [Dockerfile.mount](Dockerfile.mount) | 多段ビルド。SDK で `mount.pgfs`/`mkfs.pgfs`/`pgfsctl` を self-contained publish → debian-slim + fuse3 + xattr/acl/psql ツールに COPY |
| [compose.yml](compose.yml) | `coord` (PostgreSQL) + `mount` (FUSE コンテナ)。mount は `SYS_ADMIN` / `/dev/fuse` / `apparmor:unconfined` 付き |
| [run.sh](run.sh) | up → mkfs → FUSE マウント → [tests/linux/e2e.sh](../linux/e2e.sh) をコンテナ内実行 → down。mount 後に `{prefix}mounts` 登録/解除もアサート (Phase 2 / 2b) |
| [control_plane.sh](control_plane.sh) | Phase 2 コントロールプレーン (live reload) の機能テスト。notify ON で mount → psql から reload NOTIFY → `audit.enabled` が走行中に切り替わるのを観測 ([docs/design/runtime-control-plane.ja.md](../../docs/design/runtime-control-plane.ja.md))。 |
| [control_plane_ctl.sh](control_plane_ctl.sh) | Phase 3 / 3c の機能テスト。**notify OFF** で mount → `pgfsctl config set audit.enabled true` (Db+Live) で監査 0→1、`pgfsctl config set logging.level trace` (File+Live) のインライン set 適用を観測。制御 LISTEN が notify OFF でも常時 ON (P3-0) であることを直接確認 ([docs/design/runtime-control-plane.ja.md §Phase 3](../../docs/design/runtime-control-plane.ja.md))。 |
| [status.sh](status.sh) | Phase 4 / 4c+4d の機能テスト。mount 中に `pgfsctl status` が Layer 1 (稼働行 live/fuse・unmount で deregister) + Layer 2 (inode/used_bytes/chunk 集計) + Layer 3 (read で content キャッシュ温め → ping NOTIFY で snapshot 即更新 → content chunks / inode hits / 実効 config / notify) を返すのを観測 ([docs/design/runtime-control-plane.ja.md §Phase 4](../../docs/design/runtime-control-plane.ja.md))。 |

`tests/linux/` は **read-only bind mount** でコンテナに持ち込むので、テスト編集時にイメージ再ビルドは不要。`mount.pgfs`/`mkfs.pgfs` 本体を変えたときだけ再ビルド (`run.sh` は既定で `--build`)。

---

## 実行

```bash
# 前提: docker + docker compose v2 (libfuse バインディングは Pgfs.Fuse に内製化済みのため submodule 不要)
bash tests/docker/run.sh                 # 全フロー (build → 現行 Linux e2e → teardown)
TEST_FILTER=xattr bash tests/docker/run.sh
KEEP_UP=1 bash tests/docker/run.sh       # 失敗調査用にコンテナを残す
NO_BUILD=1 bash tests/docker/run.sh      # 既存 image を使い回す
```

失敗調査 (`KEEP_UP=1` で残したあと):

```bash
docker compose -p pgfs-e2e exec mount sh -c "tail -50 /tmp/mount.log"
docker compose -p pgfs-e2e exec mount bash /tests/linux/e2e.sh /mnt/pgfs
docker compose -p pgfs-e2e down -v       # 掃除
```

---

## 環境変数

すべて `${VAR:-default}` で上書き可能。

| 変数 | 既定 | 用途 |
|---|---|---|
| `PGFS_DOCKER_PG_IMAGE` | `postgres:17` | coord の PG イメージ |
| `PGFS_DOCKER_SDK_IMAGE` | `mcr.microsoft.com/dotnet/sdk:10.0` | build stage の SDK イメージ |
| `SUPER_USER` / `SUPER_PASSWORD` | `postgres` / `postgres` | スーパーユーザー (mkfs の DB/ロール作成) |
| `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` | `pgfs` | pgfs ロール / DB |
| `PGFS_SCHEMA` | `pgfs` | スキーマ (fallback テストが `pgfs.pgfs_inode` を引く) |
| `MOUNT_POINT` | `/mnt/pgfs` | コンテナ内マウント先 |
| `TEST_FILTER` | (空) | e2e のテスト名フィルタ |
| `KEEP_UP` / `NO_BUILD` | `0` / `0` | teardown 抑止 / 再ビルド抑止 |

---

## 仕組みの要点

- **e2e はコンテナ内で実行**: FUSE マウントをホストに見せる方式 (mount namespace 伝播) は脆いので、`e2e.sh` を mount コンテナ内で走らせ、マウントポイントもコンテナ内に置く。
- **fallback テスト**: `test_fallback_uname_gname` は DB に直接 INSERT する。`run.sh` が `PGFS_TEST_PG_EXEC="psql -h coord ..."` を渡すことで、ssh pgsql_server 経路ではなく compose network 越しの psql を使う。
- **submodule 不要** (v0.2.0〜): libfuse バインディングは `Pgfs.Fuse` (`src/fuse/`) に内製化済み。build stage は `src/` 一式を COPY して `src/mount`/`src/mkfs` を publish するだけで、外部 submodule は要らない。
