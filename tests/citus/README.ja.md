# pgfs Citus 検証

> 全テストの一覧 / 環境要件 / docker 統合の検討は [docs/tests.md](../../docs/tests.md) (ハブ) を参照。本 README はこのディレクトリの Citus 検証スクリプトの操作詳細を扱う。

Citus 対応 (分散化 / 排他制御) に関する検証スクリプト。

## ファイル

| ファイル | 目的 | 実行環境 |
|---|---|---|
| [verify.sql](verify.sql) + [verify.cmd](verify.cmd) | mkfs --clean --citus 済みの空 PGFS (= 1 ノード Citus on pgsql_server) に対する構成診断 SQL。`citus_tables` / 分散キー / shard 配置 / EXPLAIN を確認 | Windows ホスト → ssh pgsql_server 経由 |
| [multinode_probe.sh](multinode_probe.sh) | Citus の仕様確認 (auto-sync / DDL 伝搬 / shard 配置 / citus_add_local_table_to_metadata 等) 用 one-off probe スクリプト。docker で 2 ノード Citus (coord + worker) を立てて 13 セクションの probe SQL を流す | linux_client (docker daemon 未起動からでも OK、trap で後始末) |
| [test_matrix.sh](test_matrix.sh) | mkfs 多ノード Citus の **18 ケース** マトリックステスト (3 initial × 6 target)。docker で 2 ノード Citus (coord + worker1) を立てて、各 case で setup → mkfs → state 検証 → 次へ | linux_client (同上) |
| [race_multinode.sh](race_multinode.sh) | クロスクライアント排他制御の残検証。docker 2 ノード Citus + mount.pgfs × 2 を立てて (i) 多ノード Citus 上の Linux e2e 34/34、(ii) 並行 write race の cross-client 整合性 (md5/size 一致)、(iii) 並行 mkdir race の EEXIST 保証、(iv) pgfs_lock 行累積の現実的サイズ、を一気通貫で確認 | linux_client (同上) |

## 共通の前提

- Linux/Windows e2e ([tests/linux/](../linux/README.ja.md) / [tests/windows/](../windows/README.ja.md)) はこのディレクトリの **回帰テストではなく機能テスト**。Citus 環境での通過実績は [docs/history.md](../../docs/history.md) に記録。
- このディレクトリは「Citus 分散が期待通り設定されたか」の **構成診断** と「mkfs の Citus フラグ系挙動の正しさ」が主目的。

## 環境変数 (`*.sh` 共通)

各スクリプトの config 定数は環境変数で上書き可能 (`${VAR:-default}`)。docker を別ホスト/別ポートで動かす・並行実行でコンテナ名を分ける等に使う。

| 変数 | 既定 (test_matrix / multinode_probe / race_multinode) | 用途 |
|---|---|---|
| `PGFS_PROBE_IMAGE` | `citusdata/citus:latest` | Citus docker イメージ |
| `COORD_NAME` / `WORKER1_NAME` | `pgfs-citus-{matrix,verify,race}-{coord,worker1}` | コンテナ名 |
| `COORD_PORT` / `WORKER1_PORT` | `15432` / `15433` (race は `15532` / `15533`) | ホスト公開ポート |
| `SUPER_USER` / `SUPER_PASSWORD` | `postgres` / `postgres` | super 接続 (probe は `PG_USER` / `PG_PASSWORD`) |
| `PGFS_USER` / `PGFS_PASSWORD` / `PGFS_DB` | `pgfs` | PGFS ユーザ / DB |
| `MKFS_BIN` / `MOUNT_BIN` | `<repo>/bin/Publish/{mkfs,mount}.pgfs` (実値はホスト個別) | テスト対象バイナリ |
| `PGFS_TEST_LOG` | `/tmp/citus_*.log` | ログ出力先 |
| `MOUNT1` / `MOUNT2` / `TOML1` / `TOML2` (race のみ) | `/tmp/pgfs{1,2}` 系 | mount point / toml パス |

`verify.cmd` は `VERIFY_REMOTE` (既定 `pgsql_server`) で ssh 接続先を変更可能。`e2e.sh` の `test_fallback_uname_gname` は `PGFS_TEST_PG_EXEC` で psql 呼び出しを差し替え可能 ([docs/tests.md](../../docs/tests.md) 参照)。

## verify.sql / verify.cmd (1 ノード構成診断)

mkfs --clean --citus 済みの **実機 Citus** (pgsql_server, Citus 13.1.1) に対して構成を確認します。

```cmd
tests\citus\verify.cmd
```

または手動で:

```bash
ssh pgsql_server 'sudo -u postgres -i psql pgfs' < tests/citus/verify.sql
```

期待される出力 (詳細は [verify.sql](verify.sql) コメント):

- `citus_tables`: `pgfs.pgfs_inode` / `pgfs.pgfs_data` / `pgfs.pgfs_data_chunk` / `pgfs.pgfs_lock` が `distributed`、`pgfs.pgfs_settings` が `local`
- 分散キー: `parent_id` / `id` / `data_id` / `target_id` の順
- colocation: 4 distributed テーブルは default colocation group (BIGINT 分散キー + 同 shard_count で自動 co-located)
- shard 数: `citus.shard_count` (default 32)
- `pg_dist_node`: coordinator (本マシン) のみ、`shouldhaveshards = true`

## multinode_probe.sh (Citus 仕様の挙動確認)

設計上「これは Citus がやってくれるはず」と仮定した挙動を docker 上の 2 ノード Citus で実機確認するスクリプト。分散化の設計途中で「per-worker citus_set_coordinator_host は必要か?」の判断に使いました。

```bash
# linux_client 上で
bash tests/citus/multinode_probe.sh
```

検証する仮説 (詳細は [multinode_probe.sh](multinode_probe.sh) ヘッダ + 各 section コメント):

- **[Q2]** `citus_add_node` の auto-sync で worker 側 `pg_dist_node` に coordinator (groupid=0) 行が同期されるか → **✅ Yes** (per-worker 呼び出し不要)
- **[Q2b]** worker への `citus_set_coordinator_host` 無しで `citus_add_local_table_to_metadata` が通るか → **✅ Yes**
- **[Schema]** coordinator の `CREATE SCHEMA` が worker に DDL 伝搬するか → **✅ 伝搬**
- **[DropCascade]** `DROP SCHEMA CASCADE` で worker shard も掃除されるか → **✅ 掃除される**
- **[Idempotent]** `citus_add_node` 再呼出しの挙動 → ✅ idempotent (重複しない)
- **[Distribute]** `create_distributed_table` で worker に shard が作られるか → ✅ Task Count + shards_on_node で確認
- **[Shouldhaveshards]** 多ノード時の coordinator / worker のデフォルト → ✅ coordinator=false / worker=true

### 仕組み

- `citusdata/citus:latest` を docker pull (実行時に決定、`PGFS_PROBE_IMAGE` 環境変数で上書き可)
- bridge ネットワーク + ポートマップで 2 コンテナを起動 (coord:15432, worker1:15433)
- coordinator から super 接続で Citus 設定 → 13 セクションの probe SQL を順次実行
- 終了時 trap でコンテナ / イメージ / docker daemon を **元の状態に** 戻す (元から daemon 動いていれば触らない)
- ログは `/tmp/citus_multinode_probe.log`

## test_matrix.sh (mkfs 多ノード Citus 18 ケースマトリックス)

mkfs --citus / --worker / --clean の組み合わせで `EnsureDatabaseAsync` の 3 つの分岐 (新規セットアップ / 既存維持ガード / --clean による drop→再構築) が意図通りか確認します。

```bash
# linux_client 上で (mkfs.pgfs バイナリが <repo>/bin/Publish/mkfs.pgfs に必要)
bash tests/citus/test_matrix.sh
```

### マトリックス (3 × 6 = 18 ケース)

**Initial state**:
- I1: DB なし
- I2: 1 ノード Citus 構成 (= 先に `mkfs --clean --citus` 済)
- I3: coord + worker 構成 (= 先に `mkfs --clean --citus --worker w1` 済)

**Target operation**:
- Ta: `mkfs --clean` (no citus) — 非 Citus に reset / 構築
- Tb: `mkfs --clean --citus` — 1 ノード Citus に reset / 構築
- Tc: `mkfs --clean --citus --worker w1` — coord+worker に reset / 構築
- Td: `mkfs` (no --clean, no citus) — 既存維持 (新規時はフル構築)
- Te: `mkfs --citus` (no --clean) — 既存維持 (新規時は 1 ノード Citus 構築)
- Tf: `mkfs --citus --worker w1` (no --clean) — 既存維持 (新規時は coord+worker 構築)

各 case で setup_I* → run_mkfs → verify_state (coord pgfs DB 存在 / 5 テーブル / pg_dist_node 件数 / shouldhaveshards / citus_tables の distributed/local 件数) を確認。

### 仕組み

- `--network host` モードで 2 PG コンテナを host のユニークポート (15432 / 15433) で listen させる
  - bridge + ポートマップ方式だと `citus_set_coordinator_host('localhost', host_port)` で workers が誤った "localhost" を見る問題が出るため、host network で 3 者全員が同じアドレスで互いを参照できる構成にする
- POSTGRES_HOST_AUTH_METHOD=trust + citus.node_conninfo=sslmode=disable で SSL/auth 設定を素通り
- mkfs は host (linux_client) で実行され、localhost:15432 で coord に接続、`--worker localhost:15433` で worker を指定
- ログは `/tmp/citus_test_matrix.log`
- 終了時 trap でコンテナ / イメージ / docker daemon を元の状態に戻す

### 実績

**18/18 PASS** (Citus 14.0.0 + docker on linux_client)。

## race_multinode.sh (クロスクライアント排他制御の残検証)

Api に組み込んだクロスクライアント排他制御を多ノード Citus 環境で実機検証するスクリプト。docker で coord + worker1 を立てて mount.pgfs を 2 プロセス並列に動かし、排他制御の検証チェックリストの 4 項目を一気通貫で確認します。

```bash
# linux_client 上で (mkfs.pgfs / mount.pgfs バイナリが bin/Publish/ に必要)
bash tests/citus/race_multinode.sh
```

### 確認項目 (4 件)

| Test | 内容 | 検証している保証 |
|---|---|---|
| Test 1 | 多ノード Citus 上で [tests/linux/e2e.sh](../linux/e2e.sh) 34 ケース | 分散 + 排他制御が多ノードで機能、cross-shard hop も含めて regression なし |
| Test 2 | 2 client から同じファイルへ並行 dd (8MiB × 4 round、urandom vs zero) → md5/size が両 client で一致 | `LockData(dataId)` で writer 同士が直列化、PG 行ロックと相まってチャンク level の torn write 無し / cross-client 整合性 |
| Test 3 | 2 client から同じ親に並行 mkdir 同名 (20 round × serial + parallel) | `(parent_id, name)` UK が cross-shard 整合性を保証 (シャード跨ぎでも全 client から見た「同じ親に同名 2 つ」は不可能) |
| Test 4 | 上記 1-3 完走後の `pgfs_lock` 行数 + relation size | DELETE しない方針なので累積するが、1 行 ~50 bytes × 数千 = 1MB 以下に収まる (設計値 100 万行で 100MB 以下と整合) |

### 仕組み

- `--network host` モードで 2 PG コンテナを 15532/15533 で listen (test_matrix.sh と同じ理由)
- `mkfs --clean --citus --worker localhost:15533` で初期化
- pgfs.toml × 2 を `/tmp/pgfs{1,2}.toml` に生成 (`database.notify_enabled = true` で cross-client 通知有効)
- mount.pgfs × 2 を `-m /tmp/pgfs{1,2} -f` で background 起動
- [tests/linux/e2e.sh](../linux/e2e.sh) の `pg_exec` は本来 ssh pgsql_server 経由のハードコードだが、`PGFS_TEST_PG_EXEC` 環境変数でオーバライド可能にしたので docker container 経由の psql に差し替える ([test_fallback_uname_gname](../linux/e2e.sh) 対応)
- 終了時 trap で fusermount3 → docker 停止 → daemon 元状態に戻す
- ログは `/tmp/citus_race_multinode.log` (本体) + `/tmp/pgfs{1,2}.mount.log` (mount.pgfs)

### 実績

**4/4 PASS** (Citus 14.0.0 + docker on linux_client)。pgfs_lock rows=178 / size=768kB。

## cross-shard rename の動作確認

`test_rename_into_subdir` (Linux/Windows 両 e2e) が **異なる親への rename** をカバーしているので、Citus 環境で通っていれば cross-shard rename も実証済み。実装は [Api.cs](../../src/lib/src/Api/Api.cs) の `Rename` の DELETE+INSERT (OVERRIDING SYSTEM VALUE) 経路。

cross-shard rename を SQL レベルで明示確認したい場合は [verify.sql](verify.sql) の EXPLAIN ブロックを参照。

## 関連ドキュメント

- 設計: [docs/support_for_citus.md](../../docs/support_for_citus.md)
- 設計判断: [docs/history.md](../../docs/history.md)
- DDL: [docs/ddl/](../../docs/ddl/README.md)
- mkfs CLI: [docs/Mkfs.md](../../docs/Mkfs.md)
