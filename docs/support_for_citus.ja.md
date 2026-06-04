# Citus 対応設計

PostgreSQL を **水平分散** ([Citus](https://www.citusdata.com/)) で動かすときの設計判断をまとめたドキュメント。Citus 関連の判断はここを正とする。

英語版は [support_for_citus.md](support_for_citus.md) を参照してください。

> 動機: 1 PostgreSQL instance の disk 容量上限 (現実的に数 TB〜数十 TB) を超えるファイルシステムを作りたい。Citus を coordinator + N worker で組めば、worker を追加することで容量と書き込みスループットを並列スケールできる。

Citus 対応は 3 つの領域にまたがり、それぞれ単独でも有用:

| 領域 | 内容 | 非 Citus 環境への影響 |
|---|---|---|
| **ストレージ** | データ本体を `bytea` で保持 (Large Object ではなく) | 単 PG でも完結。Citus の前提だが単体でも価値がある |
| **分散** | distributed / local テーブル設定 + mkfs `--citus` フラグ + `pgfs_lock` テーブル | Citus 環境のみ。単 PG 運用は引き続き動く |
| **ロック** | `pgfs_lock` 上の `SELECT FOR UPDATE` ベース排他制御を Api に組み込み | 単 PG / Citus どちらでも動く。Notify と組み合わせて cross-client 整合性を取る |

---

## ストレージ: Large Object ではなく `bytea`

データ本体は [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) の `ReadData` / `WriteData` / `TruncateData` / `ReleaseData` (補助の `ReadChunkSlice` / `WriteChunkSlice` / `TruncateChunk` / `DropAllChunks`) に集約。DDL は [docs/ddl/pgfs_data_chunk.sql](ddl/pgfs_data_chunk.sql)、初期化コードは [src/mkfs/src/Initializer.cs](../src/mkfs/src/Initializer.cs) の `CreateDataChunkTableAsync`、モデルは [src/lib/src/Models/Chunk.cs](../src/lib/src/Models/Chunk.cs)。

### なぜ bytea か

PG の Large Object は `pg_largeobject` というシステムカタログに直書きされる:

```
pgfs_data_chunk(data_id, chunk_index, lo_oid OID)  →  pg_largeobject(loid, pageno, data BYTEA(2KB))
```

`pg_largeobject` はシステムカタログなので **Citus は分散できない**。worker を増やしても LO 本体は永久に coordinator 1 台に集中し、容量の壁を越えられない。

→ データ本体を **bytea で持つユーザーテーブル** に置く。bytea はユーザーテーブルの列なので、`create_distributed_table` の対象にできる。

### スキーマ

```sql
CREATE TABLE pgfs_data_chunk (
    data_id     BIGINT  NOT NULL,
    chunk_index INTEGER NOT NULL,
    payload     BYTEA   NOT NULL,
    created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    created_by  TEXT      NOT NULL,
    updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    updated_by  TEXT      NOT NULL,
    PRIMARY KEY (data_id, chunk_index)
);
```

- 1 chunk = 1 行 = 1 bytea (デフォルト `file_system.default_chunk_size = 1MB`)
- 1MB bytea は確実に TOAST 化される (PG の TOAST しきい値 ~2KB)
- chunk_size は `file_system.default_chunk_size` で調整可

### 実装メモ

- **`repeat(bytea, integer)` は PG に存在しない** (`repeat(text, integer)` だけ) ので、0 パディングは `decode(repeat('00', N), 'hex')` で組み立てる。
- WriteData は 1 SQL/チャンクの upsert (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`)。CASE で「中央 overlay / 末尾上書き / 0 パディング + 連結」の 3 ケースに分岐。並行 WriteFile (CopyFileEx 等) の race は PG の行ロックで自動直列化される。
- 各チャンクの payload 長は「これまで書き込まれたバイト数」と等しい。途中の穴は length(payload) が write offset まで届いていない状態で表現される (substring が短い結果を返したらゼロ埋め)。
- `substring(payload from N for M)` は PG 13+ の partial TOAST detoast を発動し、ネットワーク転送量は要求分のみ = LO の `lo_seek + lo_read` 相当のサーバ側部分読み。

### Api のマッピング (LO 方式との対比)

| LO | bytea |
|---|---|
| `lo_create()` → `INSERT pgfs_data_chunk(lo_oid)` | `INSERT pgfs_data_chunk(payload)` |
| `lo_open(oid, INV_READ); lo_seek(off); lo_read(len); lo_close(fd)` | `SELECT substring(payload from @off+1 for @len) FROM pgfs_data_chunk WHERE ...` |
| `lo_open(oid, INV_WRITE); lo_seek(off); lo_write(buf); lo_close(fd)` | full-chunk: `UPDATE ... SET payload = @new WHERE ...` / partial: `SET payload = overlay(payload placing @new from @off+1 for @len)` |
| `lo_truncate64(fd, len); lo_close(fd)` | `UPDATE ... SET payload = substring(payload for @len) WHERE ...` |
| `lo_unlink(oid)` | `DELETE FROM pgfs_data_chunk WHERE ...` |

### 性能特性

| 操作 | LO | bytea |
|---|---|---|
| 4KB sequential read | `lo_seek+lo_read` 2 RTT | `substring(...)` 1 RTT |
| 4KB random read (chunk 跨ぎなし) | 同上 | 同上 (partial TOAST detoast) |
| 1MB full chunk write | `lo_open+lo_write+lo_close` 3 RTT | `UPDATE SET payload=...` 1 RTT |
| 4KB partial chunk write | `lo_seek+lo_write` 2 RTT (page 単位 COW) | `UPDATE SET payload=overlay(...)` 1 RTT (TOAST 全体 rewrite) |

通常用途 (cp / 連続書き) では bytea がやや有利、partial-heavy workload では LO がやや有利。FUSE / Dokan からの read/write は 4KB〜128KB 単位が多いので、chunk_size 1MB なら大半は full-chunk で差は小さい。

### 既存データの移行

新規環境は `mkfs --clean` で再構築。生データが入った環境を移すなら:

```sql
ALTER TABLE pgfs_data_chunk ADD COLUMN payload BYTEA;

DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT data_id, chunk_index, lo_oid FROM pgfs_data_chunk WHERE payload IS NULL LOOP
        UPDATE pgfs_data_chunk SET payload = lo_get(r.lo_oid)
          WHERE data_id = r.data_id AND chunk_index = r.chunk_index;
        PERFORM lo_unlink(r.lo_oid);
    END LOOP;
END $$;

ALTER TABLE pgfs_data_chunk DROP COLUMN lo_oid;
ALTER TABLE pgfs_data_chunk ALTER COLUMN payload SET NOT NULL;
```

オフライン移行で十分。マウント中の hot migration は当面サポートしない。

### 廃案

1. **独自 LO テーブル + Citus 分散**: ページサイズを小さくした bytea を別テーブルで保持する案。要するに「ページサイズ可変の bytea」であって、`pgfs_data_chunk` の chunk_size を 2KB にしたものと等価。やる意味なし。
2. **各 worker に PG instance を立てて application 層で sharding + replication**: 手動 sharding になり、replication / failover / 整合性チェックを全部自前で書く必要がある。実質「分散 FS を自力で実装」で、pgfs が PostgreSQL に依存する利点 (RDBMS の運用ノウハウ、SQL での監査、ACID) を失う。

→ シンプルに bytea 化が最適。

---

## テーブル分散

`mkfs --citus [--worker host[:port],...]` で 1 ノード / 多ノード両構成に対応。実装は以下に分散:

- [Schema.Database.Citus](../src/lib/src/Config/Schema.cs) (BoolField) + [Schema.Database.Workers](../src/lib/src/Config/Schema.cs) (StringListField) — CLI / TOML 入口
- [DatabaseConfig.Workers](../src/lib/src/Config/DatabaseConfig.cs) — `List<(string Host, int Port)>` に正規化
- [Initializer.InitializeAsync](../src/mkfs/src/Initializer.cs) のファサード + [Initializer.EnsureDatabaseAsync](../src/mkfs/src/Initializer.cs) — worker bootstrap + coordinator DB ensure + Citus topology を一括
- 各 [CreateXxxTableAsync](../src/mkfs/src/Initializer.cs) — テーブル新規作成時のみ `create_distributed_table` / `citus_add_local_table_to_metadata` を続けて呼ぶ (per-table 責務)
- Api 側の Citus 互換化 ([Api.Rename](../src/lib/src/Api/Api.cs) / [Api.EnsureDataRow](../src/lib/src/Api/Api.cs) / [Api.WriteChunkSlice](../src/lib/src/Api/Api.cs) / [ConfigStore.Save](../src/lib/src/Config/ConfigStore.cs))
- DDL 変更 ([pgfs_inode.sql](ddl/pgfs_inode.sql) PK 複合化 + [pgfs_lock.sql](ddl/pgfs_lock.sql))

検証は [tests/citus/](../tests/citus/README.md): [multinode_probe.sh](../tests/citus/multinode_probe.sh) (Citus 仕様 — auto-sync / DDL 伝搬 / shard 配置 — の挙動確認用 one-off probe) と [test_matrix.sh](../tests/citus/test_matrix.sh) (mkfs の 18 ケースマトリックス: 3 initial × 6 target)。

### テーブル別の分散戦略

| テーブル | 分散方式 | キー | 理由 |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | `(parent_id, name)` UK が shard 内で閉じる (Citus は cross-shard UK を保証しない) / 同一ディレクトリの ListChildren が 1 shard で完結 / path traversal は cross-shard hop が起きるが InodeCache が吸収 |
| `pgfs_data` | distributed | `id` | 単純な lookup。ハードリンク兄弟は同じ data_id を共有するので `pgfs_data_chunk` と co-locate |
| `pgfs_data_chunk` | distributed | `data_id` | 1 ファイルの全 chunk が同 shard → sequential read/write が 1 shard 内で完結。`colocate_with => 'pgfs_data'` で `pgfs_data` と同じ shard に |
| `pgfs_lock` | distributed | `target_id` | `SELECT FOR UPDATE` を coordinator 集中させず worker に分散 |
| `pgfs_settings` | **local (= 非分散)** | — | 全 worker からの読み書きが無いので分散不要。`citus_add_local_table_to_metadata` で metadata 登録だけしておくと、将来 distributed テーブルから JOIN したくなったときに参照可能 |

### mkfs の Citus フラグ

| フラグ | 意味 | デフォルト |
|---|---|---|
| `--citus` | Citus 化を有効にする (extension + create_distributed_table 系を呼ぶ) | false |
| `-w` / `--worker` / `--workers` | カンマ区切りの worker spec `host[:port]` | (空) |

```bash
# 1 ノード Citus (coordinator のみ、shard も coordinator が持つ)
mkfs.pgfs --clean --citus -c "Host=coord;..." --super "..."

# 多ノード Citus (coordinator + worker1 + worker2)
mkfs.pgfs --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

mkfs --citus は **DB を新規作成するときのみ** Citus のセットアップを行う:

1. **worker bootstrap**: 各 `--worker` 上に super 接続で `EnsureUser` → (`--clean` なら DropDatabase) → `CREATE DATABASE pgfs` → `CREATE EXTENSION IF NOT EXISTS citus`
2. **coordinator DB ensure**: coordinator 上に super 接続で `CREATE DATABASE pgfs`
3. **Citus topology**:
   ```sql
   CREATE EXTENSION IF NOT EXISTS citus;
   SELECT citus_set_coordinator_host(coordHost, coordPort);
   SELECT citus_add_node(workerHost, workerPort);   -- foreach worker
   SELECT citus_set_node_property(coordHost, coordPort, 'shouldhaveshards', true);  -- workers が 0 件のときだけ
   ```
4. その後の `EnsureSchemaAsync` / `CreateXxxTableAsync` で **Citus の DDL 伝搬** により workers にも CREATE SCHEMA / shard tables が自動展開される。
5. `create_distributed_table` / `citus_add_local_table_to_metadata` は各 `CreateXxxTableAsync` がテーブル新規作成 (`Task<bool>` で判定) 直後に呼ぶ:

```sql
SELECT create_distributed_table('pgfs.pgfs_inode',      'parent_id');
SELECT create_distributed_table('pgfs.pgfs_data',       'id');
SELECT create_distributed_table('pgfs.pgfs_data_chunk', 'data_id', colocate_with => 'pgfs.pgfs_data');
SELECT create_distributed_table('pgfs.pgfs_lock',       'target_id');
SELECT citus_add_local_table_to_metadata('pgfs.pgfs_settings');
```

`citus_add_local_table_to_metadata` の意義: `pgfs_settings` は coordinator のみに置くが、metadata 登録しておくと (a) 将来 distributed テーブルから JOIN したくなったときに使える、(b) Citus の backup/restore ツールが認識する、(c) `citus_tables` view 等の調査系で見える。コストはほぼゼロ。

### EnsureDatabaseAsync の構造

```text
EnsureDatabaseAsync:
  [pre] coordinator pgfs DB の存在チェック
    既存 → LogExistingCitusStateAsync (Citus 時のみ) → return
    不在 → 以下を続行

  [Step 1] 各 worker 上に pgfs DB + Citus 拡張を確保 (--citus + worker 指定時のみ実体動作)
    foreach worker:
      super to worker maintenance:  EnsureDatabaseOnAsync(worker pgfs DB)
      super to worker pgfs DB:      CREATE EXTENSION IF NOT EXISTS citus
      // CREATE SCHEMA はやらない (coordinator-host 伝搬が不要なのと同じ理由で Citus の DDL 伝搬に任せる)

  [Step 2] coordinator pgfs DB を CREATE

  [Step 3] Citus トポロジ (coordinator 側のみ、auto-sync で workers に伝搬)
    super to coord pgfs DB:
      CREATE EXTENSION IF NOT EXISTS citus
      if (coord (groupid=0) not in pg_dist_node):
        citus_set_coordinator_host(coordHost, coordPort)
      foreach worker:
        citus_add_node(workerHost, workerPort)   ← idempotent + auto-sync workers
      if (workers.empty):
        citus_set_node_property(coord, 'shouldhaveshards', true)
```

EnsureSchemaAsync は coordinator のみで CREATE SCHEMA を発行 → Citus の DDL 伝搬で workers 側にも自動で作られる。同様に各 `CreateXxxTableAsync` の CREATE TABLE も coordinator のみで発行 → `create_distributed_table` 呼び出しで shard が workers に作られる。

### 設計の核となる制約 (覚えておくべき判断)

1. **`--citus` × カスタム `--tablespace` は両立可能**。per-table `TABLESPACE` 句をやめ **`CREATE DATABASE WITH TABLESPACE` で既定 tablespace を継承**させる方式にしたため、shard も worker DB の既定 tablespace を継承する。tablespace はノードローカルなので [EnsureTablespaceAsync](../src/mkfs/src/Initializer.cs) が **coordinator + 全 worker** に作成 (Citus は CREATE TABLESPACE を伝搬しない)。LOCATION dir は `app.plperlu` 許可時に plperlu auto-mkdir (postgres 所有 0700) で自動作成。多ノードは各 worker に dir が必要 (mkfs は SQL のみで remote mkdir 不可だが、各 worker 接続で plperlu mkdir が走る)。設計は [settings-and-plperlu.md](settings-and-plperlu.md)。
2. **DB 既存 (--clean なし or --clean しても drop 失敗等) なら Citus 関連 mutate は全部スキップ**: `EnsureDatabaseAsync` の冒頭で coordinator DB の存在チェックを行い、既存なら現状把握 ([LogExistingCitusStateAsync](../src/mkfs/src/Initializer.cs)) だけして即 return。`citus_add_node` / `shouldhaveshards` / `create_distributed_table` / `citus_add_local_table_to_metadata` は一切呼ばない。これにより「mkfs 再実行で稼働中クラスタを壊さない」「空 DB への誤 `mkfs --citus --worker w1` のやり直しは `--clean` 必須」「冪等に何度叩いてもクラスタ状態は不変」が保証される。
3. **DB 新規作成時のみ全セットアップを走らせる** — 上記 (2) の対偶。
4. **`create_distributed_table` / `citus_add_local_table_to_metadata` は per-table メソッドの責務**: `CreateXxxTableAsync` がテーブル新規作成したら続けて呼ぶ (`CreateTableAsync` は `Task<bool>` で「作成 / スキップ」を返す)。既存テーブルスキップ時は distribute も触らない。
5. **`citus_set_coordinator_host` の worker 伝搬は不要**: probe ([multinode_probe.sh](../tests/citus/multinode_probe.sh)) で確認 — coordinator 側で `citus_add_node('worker', port)` を呼ぶだけで、worker 側 `pg_dist_node` に coordinator (groupid=0) 行が auto-sync される。`citus_add_local_table_to_metadata` も worker への明示 set_coordinator_host 無しで成立。

### 実装で確定したつまずきポイント

- **Citus は unique constraint に分散キーが含まれることを要求する**。`pgfs_inode` の PK は当初 `(id)` だったが、`parent_id` 分散にすると Citus が `cannot create constraint on "pgfs_inode"` で蹴る。PK を `(parent_id, id)` の複合に変更し、`WHERE id = @id` 検索用に id 単独 INDEX を別途追加。id は BIGSERIAL でグローバルに一意 (sequence は coordinator) なので `(parent_id, id)` も実質 id だけで一意。
- **`id` 単独 UNIQUE 制約は単 PG モードでもあえて張らない**: Citus 環境では UK / PK / EXCLUSION のどれを使っても id 単独制約は作れない (上の通り)。CHECK は subquery 不可なので「他行との一意性」は表現不能、trigger で疑似実装すると毎 INSERT で cross-shard lookup が走って性能崩壊。「単 PG モード時だけ id 単独 UK を張る」分岐は技術的に可能だが、(a) 単 PG / Citus でスキーマがズレる、(b) 単 PG → Citus への in-place 移行が UK で詰まる、(c) 起動時の防御的 `GROUP BY id HAVING COUNT > 1` も遅くなるだけで体感的利益が無い、という理由で **両モード共通で UK なし、BIGSERIAL の sequence + tx 規律で一意性を保つ**。根拠: BIGSERIAL の sequence は coordinator 1 つだけで二重払い出ししない / id バイパスは `Api.Rename` の `OVERRIDING SYSTEM VALUE` 経路 1 箇所だけで、そこは DELETE+INSERT が同 tx 内で完結する設計なので中間状態で同じ id が二重に存在しない。**将来「念のため id 一意性を担保しよう」という提案を再投入する前にこの判断を確認すること** — 既に意識して捨てた選択肢。
- **root inode の ON CONFLICT は `(parent_id, name)` UK ターゲット**。`ON CONFLICT (id)` は (id) 単独 UK が無いと使えないが、Citus 制約上 (id) 単独 UK は作れない。`(parent_id, name)` UK は分散キーを含むので OK。root は (0, '/') で一意。
- **coordinator は常に `pg_dist_node` に登録する必要がある** (`citus_add_local_table_to_metadata` の前提): 判定は **「`pg_dist_node` 全体が空か」ではなく「coordinator (`groupid = 0`) 行があるか」**で行う必要がある — 「`pg_dist_node` が空のときだけ登録」だと、ユーザーが `citus_add_node` で worker だけ先に追加した構成 (pg_dist_node 非空、coordinator 未登録) で後段の local 登録が死ぬ。mkfs --citus は `SELECT count(*) FROM pg_dist_node WHERE groupid = 0` が 0 のときに `citus_set_coordinator_host(host, port)` を呼ぶ。
- **`shouldhaveshards = true` は worker 0 件のときだけセット**: 1 ノード構成では `create_distributed_table` が "replication_factor (1) exceeds number of worker nodes (0)" で死ぬので、coordinator 自身を shard ホストにする必要がある。worker が居る構成では shard は worker 側に置くのが本来なので coordinator のデフォルト (`shouldhaveshards = false`) を尊重して触らない。判定は `SELECT count(*) FROM pg_dist_node WHERE groupid <> 0 AND noderole = 'primary'` = 0。coordinator 登録判定とは独立。
- **`FOR UPDATE` には分散キーが必須**: `SELECT ... WHERE id = @id FOR UPDATE` は Citus で "could not run distributed query with FOR UPDATE/SHARE commands" で死ぬ。`Api.Rename` と `Api.EnsureDataRow` の FOR UPDATE は `WHERE parent_id = @parent_id AND id = @id` に変更し、parent_id は呼び出し側の `Inode.ParentId` (Rename ではキャッシュが無ければ事前に broadcast SELECT で取る) から渡す。
- **`ON CONFLICT DO UPDATE` 句の式は IMMUTABLE 限定**: distributed/metadata-registered table への `DO UPDATE SET col = current_timestamp` は "functions used in the DO UPDATE SET clause ... must be marked IMMUTABLE" で死ぬ。`WriteChunkSlice` と `ConfigStore.Save` の `updated_at = current_timestamp` をクライアント側生成の `@now` に置き換え、`EXCLUDED.updated_at` 参照に変更 (VALUES の定数なので IMMUTABLE 扱い)。単 PG でも同じ SQL がそのまま動くので Citus フラグでの分岐は不要。
- **cross-shard rename は DELETE + INSERT OVERRIDING SYSTEM VALUE**: `Rename` で parent_id を変更する経路は Citus が UPDATE を拒否する (分散キー書き換え)。同 tx 内で旧行を行ロック (FOR UPDATE) → DELETE → INSERT で新 shard に移送、`OVERRIDING SYSTEM VALUE` で BIGSERIAL の id を温存。parent 不変ケースは従来の UPDATE が単一 shard 内で軽量なのでそのまま。

### `pgfs_inode` の path traversal コスト

`/a/b/c` を `parent_id` 分散の `pgfs_inode` で解決すると:

1. root (id=0) を取る → 1 shard
2. root の子 `a` を取る (`WHERE parent_id=0 AND name='a'`) → parent_id=0 が乗っている shard へ (=root を持つ shard)
3. `a` の子 `b` を取る → parent_id=a.id が乗っている shard へ。**a の id を hash した shard なので別 shard の可能性大**
4. `b` の子 `c` を取る → 同じく hash で決まる shard へ

depth N で最悪 N 回の cross-shard hop。実運用では:

- **`InodeCache.byPath` が効くと 2 回目以降は 0 hop** ([src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs))
- cold start (プロセス再起動直後等) で初回 path 解決のみ遅い
- ディレクトリの深さは実用上 10〜20 程度 → 1 リクエスト 10〜20 shard hop ≈ 数十 ms (許容範囲)

InodeCache のヒット率が悪化するワークロード (大量の random path access 等) では `mount.cache_max_entries` でサイズを増やす、または coordinator 側で path → inode_id の materialized view を維持する案も検討余地あり。

### cross-shard rename

`Rename(id, newParentId, newName)` で newParentId が現在の shard と違うと行が shard を跨ぐ。Citus は **distribution key の変更を伴う UPDATE をサポートしない** ので、`UPDATE pgfs_inode SET parent_id = @new` は直接通らない可能性がある。対処は同 tx 内で新 shard へ INSERT + 旧行 DELETE、`OVERRIDING SYSTEM VALUE` で inode_id を温存。代替案として `pgfs_inode` を `id` 分散にすれば rename は単純な UPDATE で済むが `(parent_id, name)` UK が壊れる (cross-shard で UK 保証なし)。trade-off は下の未解決事項に。

---

## クロスクライアントロック (`pgfs_lock`)

lock ヘルパは [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) の `LockTargets` / `LockData` / `LockInode` / `LockInodes`。各 mutating Api メソッドの冒頭で適切なロックを取り、tx 終了 (COMMIT/ROLLBACK) で自動解放する。多ノード Citus + 2 client 並行 race は [tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh) でカバー。

### 動機

複数クライアント (例: Linux mount.pgfs + Windows pgfs.assign が同じ pgfs DB を共有) が **同じファイルを同時に書く** と race が起こる:

- `WriteData`: 2 client が同じ chunk を read-modify-write すると lost-update
- `TruncateData`: write 中に truncate されると不整合
- `Rename`: 2 client が同じファイルを別パスへ rename しようとすると 1 つは ON CONFLICT で弾かれるが、メモリ側の InodeCache が古い state を保持する

[Notify (LISTEN/NOTIFY)](history.md) は **変更の事後通知** であって race の防止ではない。書き込み中の排他制御として別途必要。

### なぜ自作テーブル + `SELECT FOR UPDATE` か

| 観点 | `pg_advisory_xact_lock` | 自作 TTL テーブル + 心拍 | **`pgfs_lock` + SELECT FOR UPDATE (採用)** |
|---|---|---|---|
| 取得コスト | μs (in-memory hash) | ms + 心拍 | ms (lock 行 cache 済みなら速い) |
| 待ち | server-side block | client polling (LISTEN/NOTIFY で擬似ブロック) | **server-side block** |
| 自動解放 | session / tx 終了 | TTL 待ち (最大 30s 遅延) | **tx 終了** |
| TTL race | なし | あり (心拍遅延 + TTL race で二重取得) | **なし** |
| 専用接続要 | (xact 版は不要) | 必要 (心拍用) | **不要** |
| Citus 分散 | coordinator local only | 分散可 (target_id key) | **分散可** |
| 可視性 | `pg_locks` view | `SELECT FROM pgfs_lock` | `pg_locks` + (pg_stat_activity で待ち手) |
| 多 tx 跨ぎ保持 | session 版で可 | 可 | 不可 (tx 終了で解放) |

**結論**: `SELECT FOR UPDATE` 案が in-memory advisory lock の良さ (sub-ms / server-side block / TTL race なし / 自動解放) と TTL テーブル案の良さ (Citus 分散可) を両取り。pgfs の書き込みは「1 操作 = 1 tx」なので tx 跨ぎ保持は不要 = `FOR UPDATE` で十分。

`pg_advisory_xact_lock` を選ばなかった理由: coordinator local で Citus で worker に lock 取得を分散できない (coordinator のセッション table がボトルネック)、multi-coordinator HA Citus では coordinator 間で見えない。自作 TTL テーブルを選ばなかった理由: TTL race (Redis SETNX with TTL の問題) を完全に潰すには fencing token 等で複雑度が上がる、心拍用の専用 connection + バックグラウンドタスクが必要、取得時の client polling で latency が悪化。

### なぜ単一 BIGINT (`target_id`) なのか

Citus の `create_distributed_table` は **ハッシュ分散で、ハッシュの入力は単一カラムの値のみ**。複合キー (例: `(kind TEXT, id BIGINT)`) を直接の分散キーにはできない。`pgfs_lock` を distributed にしたい (coordinator-local ボトルネック回避) 以上、分散キーは 1 カラムでなければならず、必然的に `target_id BIGINT` の 1 列構成になる。

帰結として **inode の id と data の id (どちらも BIGSERIAL) が同じ数値空間で衝突し得る**。inode_id = 5 と data_id = 5 を単一の `pgfs_lock(target_id)` で扱うと、片方を lock した tx がもう片方の lock 取得を意図せず block する。

### 採用した衝突回避: 符号による namespace 分離

`data lock = target_id = data_id (正)`、`inode lock = target_id = -inode_id (負)` で値の符号で namespace を分ける ([Api.cs](../src/lib/src/Api/Api.cs) の `LockData` / `LockInode` ヘルパ参照)。`pg_advisory_xact_lock(ns, key)` の 2-key 形式と違って single column PK しか持てないので、namespace は値の側で表現するしかない。

代替案として `pgfs_inode_lock` を別テーブルに切り出せば、inode lock を `parent_id` 分散 (data と別 colocation group) にして inode 操作の cross-shard hop を削れ、運用統計も分けやすい。テーブルを 1 つ余計に作るほど運用パターンが固まっていないので、まずは単一 `pgfs_lock` + 符号 namespace で始め、inode lock の workload が支配的と分かったら `pgfs_inode_lock` に再編する余地を残す。

### スキーマ

```sql
CREATE TABLE pgfs_lock (
    target_id BIGINT PRIMARY KEY
);
```

1 列 PK のみ。lock 取得は「行ロックを取る」だけなので追加列は不要。Citus 環境では `SELECT create_distributed_table('pgfs_lock', 'target_id');`。

### 使い方

```sql
BEGIN;
-- target_id 行を確保 (なければ作る)
INSERT INTO pgfs_lock(target_id) VALUES (@data_id) ON CONFLICT DO NOTHING;
-- 行ロックを取る。他 tx が同じ行を SELECT FOR UPDATE すると block される
SELECT 1 FROM pgfs_lock WHERE target_id = @data_id FOR UPDATE;
-- ここで実際の書き込み
UPDATE pgfs_data_chunk SET payload = ... WHERE data_id = @data_id AND chunk_index = @ci;
COMMIT;   -- 行ロックは tx 終了で自動解放
```

### 実装上の判断

- **`LockTargets` の SQL は INSERT ON CONFLICT DO NOTHING + SELECT 1 ... FOR UPDATE の 2 文**: 各文とも target_id 単独 WHERE なので単一 shard 完結 (Citus でも安全)。CTE で 1 文に畳む案は試さなかった (シンプルさ優先)。
- **複数 lock 取得は `LockInodes(params long[])` で target_id 昇順固定**: 入力の inode_id を負号反転 → 重複排除 → 昇順ソート → 順に SELECT FOR UPDATE。共通の inode を別順序で取る race でもデッドロックしない。
- **`WriteData` / `TruncateData` は `EnsureDataRow` の後にロック**: `inode.DataId` が null の場合 `EnsureDataRow` が data 行を作って `inode.data_id` を埋める (内側で inode 行 FOR UPDATE で並行 race を直列化)。そのあとで `LockData(dataId)` を取る。order は固定 (inode 先 → data 後) なので別 tx と衝突しない。
- **`Update*` 系 (Mode/Owner/Size/Timestamps) は単発 `Pg.Execute` から tx ベースに変更**: 元は connection を 1 SQL 分だけ open する構造で lock を持てなかった。`using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE に変更。
- **`SetXAttr` / `RemoveXAttr` は lock 対象外**: メタデータ並行更新の lost-update リスクが低い + JSON merge / strip は単一 UPDATE で atomic。必要があれば後付け可能。
- **`DeleteInode` の data drop パスには data lock を追加していない**: lock 対象表が inode のみだったので踏襲。delete-while-open race は理論上残るが、「open 中に削除されたファイルへの I/O は undefined」を前提とする。

### lock 取得が必要なパス

| Api メソッド | lock 対象 | 理由 |
|---|---|---|
| `WriteData(inode, off, src)` | data: `inode.DataId` | 同じファイルの並行書き込みを直列化 |
| `TruncateData(inode, len)` | data: `inode.DataId` | 同上 |
| `ReleaseData(dataId)` | data: `dataId` | data 解放中の他クライアント書き込みを防ぐ |
| `UpdateMode/Owner/Size/Timestamps(id)` | inode: `id` | メタデータ並行更新の lost-update を防ぐ |
| `Rename(id, newParent, newName)` | inode: `id`, `oldParent`, `newParent` | 旧親 / 新親 / 対象 inode の 3 ロック。昇順固定でデッドロック回避 |
| `DeleteInode(inode)` | inode: `inode.Id`, `inode.ParentId` | 親の children list と本体の同時変更を直列化 |
| `CreateHardLink(source, newParent, newName)` | inode: `source.Id`, `newParent` | hardlink 兄弟の nlink 更新と新規 inode 作成を直列化 |

複数 lock を取るパス (`Rename` / `DeleteInode` / `CreateHardLink`) では **target_id 昇順固定** でデッドロック回避。

### lock 行の累積

`INSERT ON CONFLICT DO NOTHING` で行が増えるだけで `DELETE` しない方針。

- 1 行 ~50 bytes (PRIMARY KEY index 込み)
- 100 万ファイル × 2 (data + inode) ≈ 100MB → 無視できる
- 削除を入れると「lock 行 DELETE される ⇄ 別 tx が SELECT FOR UPDATE 取りに来る」で race の余地が生まれる
- 必要なら別途 cron で `DELETE` (現実的には不要)

### 注意点

- **co-location の非対称**: `mkfs --citus` 後の `citus_tables` を見ると `pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` / `pgfs_lock` の全 4 distributed テーブルが **同じ colocation_id (default group)** に入る (BIGINT 分散キー + 同 shard_count なので Citus が自動 co-located)。効くケースと効かないケース:
  - **data lock を取るケース** (target_id = data_id): `pgfs_lock` の分散キー値と `pgfs_data` の分散キー値 (id) が同じ数値 → hash も同じ → **同一 shard に着地、ネット越し越境なし**
  - **inode lock を取るケース** (target_id = -inode_id): `pgfs_lock` の hash(-inode_id) と `pgfs_inode` の hash(parent_id) は別物 → **lock shard と inode shard はずれる** (1 ネット越し)

  当面は許容するが、inode lock が支配的な workload が見えてきたら `pgfs_inode_lock` を別テーブルにして `parent_id` 分散 + `pgfs_inode` と co-located にすることで非対称を解消できる。
- **Citus で複数 shard を跨ぐロック**: `WHERE target_id IN (a, b)` で a と b が別 shard だと取得順序が非決定的になりデッドロックリスクが上がる。**1 SQL = 1 ロック単位** を守る (ヘルパは 1 件ずつ取る形)。
- **取り忘れ**: `LockData` / `LockInode` を呼ばずに `WriteData` 等を書くと race が起きる。コードレビューで全 mutation path を確認する規律が必要。`Api.WriteData(...)` の冒頭で `LockData` を呼ぶ規約を [Api.cs](../src/lib/src/Api/Api.cs) のクラス doc にも明記する。
- **アプリ側のデッドロック**: 同じスレッドが入れ子で `LockData(A)` → `LockData(B)` のように複数取ると、別スレッドが逆順で取ると hang。**lock の取得順は target_id 昇順** をプロジェクト規約として固定する。

---

## 検証

[tests/citus/](../tests/citus/README.md) スイートがカバーする内容:

- **1 ノード / 多ノードのセットアップ** (`mkfs --clean --citus`、`--worker` 有無)
- **Linux / Windows e2e が Citus でもそのまま通る** (分散が機能に影響しない)
- **mkfs マトリックス** (3 initial × 6 target = 18 ケースの 新規 / 既存維持 / `--clean` 再構築) — [test_matrix.sh](../tests/citus/test_matrix.sh)
- **shard ターゲティング** (`EXPLAIN ANALYZE`) — [verify.sql](../tests/citus/verify.sql) で `WHERE parent_id = N AND id = N` は Task Count 1、`WHERE id = N` 単独は全 shard 走査を確認
- **cross-shard rename** (INSERT + DELETE 経路) — rename-into-subdir の e2e ケース
- **`pgfs_settings` が coordinator のみで metadata 登録済み** (verify.sql の `citus_table_type='local'`)
- **DDL 伝搬 / `citus_add_node` auto-sync / 明示 set_coordinator_host 無しの `citus_add_local_table_to_metadata`** — [multinode_probe.sh](../tests/citus/multinode_probe.sh)
- **2 client からの並行 race** (write race md5 一致、mkdir race EEXIST、lock 行累積が妥当範囲) — [race_multinode.sh](../tests/citus/race_multinode.sh)

---

## 未解決事項 / 将来の検討

- `pgfs_inode` の `parent_id` 分散による path traversal の cross-shard hop が、InodeCache ヒット率の低いワークロードでどれくらい遅くなるかの実測。
- cross-shard rename の inode_id 保存: INSERT + DELETE 方式だと BIGSERIAL の id が変わる。`OVERRIDING SYSTEM VALUE` で同じ id を維持する SQL の設計。
- multi-coordinator HA Citus (Enterprise) を視野に入れるかどうか。入れるなら advisory lock + coordinator local の組み合わせは別途検討が必要。
- bytea の partial read (`substring`) が PG 13+ で本当に partial TOAST detoast されるかの実測。可能なら chunk_size を大きくしても OK。
- `pgfs_inode` を `id` 分散にして `(parent_id, name)` UK を別レイヤで保証する案 (例: `unique_violation` を application 側でリトライ)。`parent_id` 分散の rename コストが許容できなければこちらに振る。
- Citus version の依存: 検証時の Citus バージョンを記録。`create_distributed_table` のシグネチャ変更等あるため。

## 参照

- [Citus docs](https://docs.citusdata.com/) — distributed table 一般
- [docs/database.md](database.md) — 現行スキーマ
- [docs/history.md](history.md) — Notify (cross-client change notification) の設計
- [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs) — データアクセス実装
- [docs/performance.md](performance.md) — InodeCache の性能改善案 (cross-shard hop 緩和に効く)
