# Linux e2e (full docker)

ホスト依存 (ssh linux_client / ホストの dotnet / symlink-race 回避策) を排除し、**Linux e2e を docker だけで完結**させる構成。CI でも再現可能にするのが目的。

スコープは現状 **単一 PG (非 Citus)**。多ノード Citus + race + audit のフル docker 化は次段階 ([docs/next.md](../../docs/next.md) #9)、雛形は [tests/citus/race_multinode.sh](../citus/race_multinode.sh)。

> Windows e2e は Dokan (Windows カーネルドライバ) のため docker 化対象外。[docs/tests.md](../../docs/tests.md) 参照。

---

## 構成

| 要素 | 役割 |
|---|---|
| [Dockerfile.mount](Dockerfile.mount) | 多段ビルド。SDK で `mount.pgfs`/`mkfs.pgfs` を self-contained publish → debian-slim + fuse3 + xattr/acl/psql ツールに COPY |
| [compose.yml](compose.yml) | `coord` (PostgreSQL) + `mount` (FUSE コンテナ)。mount は `SYS_ADMIN` / `/dev/fuse` / `apparmor:unconfined` 付き |
| [run.sh](run.sh) | up → mkfs → FUSE マウント → [tests/linux/e2e.sh](../linux/e2e.sh) をコンテナ内実行 → down |

`tests/linux/` は **read-only bind mount** でコンテナに持ち込むので、テスト編集時にイメージ再ビルドは不要。`mount.pgfs`/`mkfs.pgfs` 本体を変えたときだけ再ビルド (`run.sh` は既定で `--build`)。

---

## 実行

```bash
# 前提: docker + docker compose v2、submodule 取得済み (vendor/Tmds.Fuse)
bash tests/docker/run.sh                 # 全フロー (build → e2e 35/35 → teardown)
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
- **submodule 必須**: build stage が `vendor/Tmds.Fuse` のソースを COPY するため、`git submodule update --init --recursive` 済みであること。
