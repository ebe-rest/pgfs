# Citus 対応設計メモ

> **道順**: [docs/README.ja.md](../README.ja.md) › **本書**
>
> **この doc が正である範囲**: **Citus 水平分散の設計判断** — bytea 化 (Phase 1)、テーブル別の分散戦略と
> 分散キーの選択、shard 数 / replication factor、`{prefix}lock` + `SELECT FOR UPDATE` による排他制御 (Phase 3)、
> 書き込み tx の shard 接触順。「なぜ Citus 上でこの形にしたか」と単 PG との差はここが正。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [database.ja.md](database.ja.md) | **スキーマ本体** (各テーブルの役割・列の意味・タイムスタンプ規約) |
> | [../ddl/README.ja.md](../ddl/README.ja.md) | **DDL 本体** (テーブル 1 つ = 1 ファイル) |
> | [../Mkfs.ja.md](../Mkfs.ja.md) | `--citus` / `--shard-count` / `--rf` / `--distribute-existing` の **CLI 仕様と既定値** |
> | [performance.ja.md](performance.ja.md) | 性能の**実測と改善候補** (cross-shard hop の緩和、InodeCache) |
> | [df-support.ja.md](df-support.ja.md) | 多ノードの**空き容量集約** (`pgfs_statfs` / plperlu) |
> | [raid.ja.md](raid.ja.md) | **複数 PostgreSQL** を 1 FS に束ねる別レイヤ (本書は単一 DB 内の分散) |
> | [../tests.ja.md](../tests.ja.md) | Citus 環境での**テスト一覧・件数・実行方法** |
> | [../history.ja.md](../history.ja.md) | 専用 doc を持たない完了項目の経緯 (Notify の設計など) |
>
> **外部参照**: [Citus docs](https://docs.citusdata.com/) — distributed table 一般 (実装コードへのリンクは各節に置く)

PostgreSQL を **水平分散** ([Citus](https://www.citusdata.com/)) で動かすときの設計判断をまとめたドキュメント。実装に着手する前にここを正として参照する。

> 動機: 1 PostgreSQL instance の disk 容量上限 (現実的に数 TB〜数十 TB) を超えるファイルシステムを作りたい。Citus を coordinator + N worker で組めば、worker を追加することで容量と書き込みスループットを並列スケールできる。

## 全体方針

3 段階で順を追って実装する。**1 段ずつ完結させてから次へ進む**。途中で挫折しても各段階は単独で有用。

| Phase | 内容 | 既存環境への影響 | 状態 |
|---|---|---|---|
| **Phase 1** | Large Object → bytea 化 | 単 PG でも完結。Citus 移行の前提だが、これ単体でも価値がある | **完了** (Linux 34/34・Windows 24/24 ALL PASSED) |
| **Phase 2** | Citus 化 (distribute / reference 設定 + mkfs に `--citus` 追加 + pgfs_lock テーブル先行作成) | Citus 環境のみ。単 PG 運用は引き続き動く | **完了** (Linux 34/34・Windows 24/24 が単 PG モードと Citus モードの両方で ALL PASSED) |
| **Phase 3** | `SELECT FOR UPDATE` ベースの排他制御を `pgfs_lock` 上で実装し Api 側に組み込む | 単 PG / Citus どちらでも動く。Notify と組み合わせて cross-client 整合性を取る | **完了** (Api 側に `LockData` / `LockInode` / `LockInodes` を組み込み、Linux 34/34・Windows 24/24 維持) |

各 Phase の詳細は以降のセクション。

---

## Phase 1: Large Object → bytea 化 (完了)

**完了**。実装は [src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs) の `ReadData` / `WriteData` / `TruncateData` / `ReleaseData` および補助の `ReadChunkSlice` / `WriteChunkSlice` / `TruncateChunk` / `DropAllChunks` に集約。DDL は [docs/ddl/pgfs_data_chunk.sql](../ddl/pgfs_data_chunk.sql)、初期化コードは [src/mkfs/src/Initializer.cs](../../src/mkfs/src/Initializer.cs) の `CreateDataChunkTableAsync`、モデルは [src/core/src/Models/Chunk.cs](../../src/core/src/Models/Chunk.cs)。

実装メモ:

- **`repeat(bytea, integer)` は PG に存在しない**。`repeat(text, integer)` だけなので、0 パディングは `decode(repeat('00', N), 'hex')` で組み立てる。
- WriteData は 1 SQL/チャンクで完結する upsert を採用 (`INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`)。CASE で「中央 overlay / 末尾上書き / 0 パディング + 連結」の 3 ケースに分岐。並行 WriteFile (CopyFileEx 等) の race は PG の行ロックで自動直列化される。
- 各チャンクの payload 長は **「これまで書き込まれたバイト数」と等しい** = LO セマンティクスをそのまま踏襲。途中の穴は length(payload) が write offset まで届いていない状態で表現される (substring が短い結果を返したらゼロ埋め)。
- 1MB bytea は PG の TOAST しきい値 (~2KB) を超えるため自動的に TOAST 化される。`substring(payload from N for M)` は PG 13+ の partial detoast を発動し、ネットワーク転送量は要求分のみ。

### なぜ bytea にするか

PG の Large Object は `pg_largeobject` というシステムカタログに直書きされる:

```
pgfs_data_chunk(data_id, chunk_index, lo_oid OID)  →  pg_largeobject(loid, pageno, data BYTEA(2KB))
```

`pg_largeobject` はシステムカタログなので **Citus は分散できない**。worker を増やしても LO 本体は永久に coordinator 1 台に集中し、容量の壁を越えられない。

→ data 本体を **bytea で持つユーザーテーブル** に置き換える必要がある。bytea はユーザーテーブルの列なので、Citus で `create_distributed_table` の対象にできる。

### 新スキーマ

```sql
CREATE TABLE pgfs_data_chunk (
    data_id     BIGINT  NOT NULL,
    chunk_index INTEGER NOT NULL,
    payload     BYTEA   NOT NULL,   -- 旧 lo_oid を直に bytea として持つ
    created_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    created_by  TEXT      NOT NULL,
    updated_at  TIMESTAMP NOT NULL DEFAULT current_timestamp,
    updated_by  TEXT      NOT NULL,
    PRIMARY KEY (data_id, chunk_index)
);
```

- 1 chunk = 1 行 = 1 bytea (デフォルト `file_system.default_chunk_size = 1MB`)
- 1MB bytea は確実に TOAST 化される (PG の TOAST しきい値 ~2KB)
- chunk_size は `file_system.default_chunk_size` で調整可。bytea 化後も同設定をそのまま流用する

### Api 側の書き換え

| 旧 (LO) | 新 (bytea) |
|---|---|
| `lo_create()` → `INSERT pgfs_data_chunk(lo_oid)` | `INSERT pgfs_data_chunk(payload)` で一括 |
| `lo_open(oid, INV_READ); lo_seek(off); lo_read(len); lo_close(fd)` | `SELECT substring(payload from @off+1 for @len) FROM pgfs_data_chunk WHERE ...` |
| `lo_open(oid, INV_WRITE); lo_seek(off); lo_write(buf); lo_close(fd)` | full-chunk 書き: `UPDATE ... SET payload = @new WHERE ...` / partial: `SET payload = overlay(payload placing @new from @off+1 for @len)` |
| `lo_truncate64(fd, len); lo_close(fd)` | `UPDATE ... SET payload = substring(payload for @len) WHERE ...` |
| `lo_unlink(oid)` | `DELETE FROM pgfs_data_chunk WHERE ...` |

部分読み (`substring`) は **PG 13 以降の partial TOAST detoast** が効くので、LO の `lo_seek + lo_read` 相当のサーバ側部分読みになる (= ネットワーク転送量は要求分のみ)。

### 性能差 (実測前の予想)

| 操作 | LO | bytea |
|---|---|---|
| 4KB sequential read | `lo_seek+lo_read` 2 RTT | `substring(...)` 1 RTT |
| 4KB random read (chunk 跨ぎなし) | 同上 | 同上 (partial TOAST detoast) |
| 1MB full chunk write | `lo_open+lo_write+lo_close` 3 RTT | `UPDATE SET payload=...` 1 RTT |
| 4KB partial chunk write | `lo_seek+lo_write` 2 RTT (page 単位 COW) | `UPDATE SET payload=overlay(...)` 1 RTT (TOAST 全体 rewrite) |

通常用途 (cp / 連続書き) では bytea がやや有利、partial-heavy workload では LO がやや有利。FUSE / Dokan からの read/write は 4KB〜128KB 単位が多いので、chunk_size 1MB なら大半は full-chunk になり差は小さい。

### 移行手順

開発機は生データ無しなので `mkfs --clean` で再構築。生データが入った環境を移すなら:

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

→ オフライン移行で十分。マウント中の hot migration は当面サポートしない。

### 廃案: 「LO を維持して worker に分散・複製」

検討したが採用しない理由:

1. **独自 LO テーブル + Citus 分散**: ページサイズを小さくした bytea を別テーブルで保持する案。これは要するに **「ページサイズ可変の bytea」** であって、`pgfs_data_chunk` の chunk_size を 2KB にしたものと等価。**やる意味なし**。
2. **各 worker に PG instance を立てて application 層で sharding + replication**: 手動 sharding になり、replication / failover / 整合性チェックを全部自前で書く必要がある。実質「分散 FS を自力で実装」しているのと同じで、pgfs が PostgreSQL に依存する利点 (RDBMS の運用ノウハウ、SQL での監査、ACID) を失う。

→ **シンプルに bytea 化が最適**。

---

## Phase 2: Citus 化 (完了, 多ノード対応含む)

**完了**。`mkfs --citus [--worker host[:port]],...]` で 1 ノード / 多ノード両構成に対応。実装は以下に分散:

- [Schema.Database.Citus](../../src/core/src/Config/Schema.cs) (BoolField) + [Schema.Database.Workers](../../src/core/src/Config/Schema.cs) (StringListField) — CLI / TOML 入口
- [DatabaseConfig.Workers](../../src/core/src/Config/DatabaseConfig.cs) — `List<(string Host, int Port)>` に正規化
- [Initializer.InitializeAsync](../../src/mkfs/src/Initializer.cs) のファサード + [Initializer.EnsureDatabaseAsync](../../src/mkfs/src/Initializer.cs) — Phase 1 (worker bootstrap) + Phase 2 (coordinator DB ensure) + Phase 3 (Citus topology) を内側で一括
- 各 [CreateXxxTableAsync](../../src/mkfs/src/Initializer.cs) — テーブル新規作成時のみ `create_distributed_table` / `citus_add_local_table_to_metadata` を続けて呼ぶ (per-table 責務)
- Api 側の Citus 互換化 ([Api.Rename](../../src/core/src/Api/Api.cs) / [Api.EnsureDataRow](../../src/core/src/Api/Api.cs) / [Api.WriteChunkSlice](../../src/core/src/Api/Api.cs) / [ConfigStore.Save](../../src/core/src/Config/ConfigStore.cs))
- DDL 変更 ([pgfs_inode.sql](../ddl/pgfs_inode.sql) PK 複合化 + [pgfs_lock.sql](../ddl/pgfs_lock.sql) 新設)

検証は [tests/citus/](../../tests/citus/README.ja.md) に 2 種類:
- [multinode_probe.sh](../../tests/citus/multinode_probe.sh) — Citus 仕様 (auto-sync / DDL 伝搬 / shard 配置 等) の挙動確認用 one-off probe
- [test_matrix.sh](../../tests/citus/test_matrix.sh) — mkfs の **18 ケース** マトリックス (3 initial × 6 target)。Citus 14 (docker) + linux_client 上で **18/18 PASS** (2026-05-26)

### 設計の核となる制約 (覚えておくべき判断)

1. **`--citus` × カスタム `--tablespace` は両立可能** (禁止ガードを撤廃)。per-table `TABLESPACE` 句をやめ **`CREATE DATABASE WITH TABLESPACE` で既定 tablespace を継承**させる方式に変更したため、shard も worker DB の既定 tablespace を継承する。tablespace はノードローカルなので [EnsureTablespaceAsync](../../src/mkfs/src/Initializer.cs) が **coordinator + 全 worker** に作成 (Citus は CREATE TABLESPACE を伝搬しない)。LOCATION dir は `app.plperlu` 許可時に plperlu auto-mkdir (postgres 所有 0700) で自動作成。多ノードは各 worker に dir が必要 (mkfs は SQL のみで remote mkdir 不可だが、各 worker 接続で plperlu mkdir が走る)。設計は [settings-and-plperlu.ja.md](settings-and-plperlu.ja.md)。
2. **DB 既存 (--clean なし or --clean しても drop 失敗等) なら Citus 関連 mutate は全部スキップ**: `EnsureDatabaseAsync` の冒頭で coordinator DB の存在チェックを行い、既存なら現状把握 ([LogExistingCitusStateAsync](../../src/mkfs/src/Initializer.cs)) だけして即 return。`citus_add_node` / `citus_set_coordinator_host` / `shouldhaveshards` と worker bootstrap は **一切呼ばない** (訂正, スキップされるのは **topology 系だけ**で、`create_distributed_table` / `citus_add_local_table_to_metadata` は「新規作成したテーブル」に対しては実行される。既存テーブルまで Citus 化したいときは `--distribute-existing`)。これにより:
   - 一度立ち上げた Citus クラスタを mkfs 再実行で意図せず壊さない
   - `mkfs --citus --worker w1` を間違って空 DB に走らせた → やり直しは `--clean` 必須
   - 「mkfs を冪等に何度叩いてもクラスタ状態は不変」が保証される
3. **DB 新規作成時のみ Phase 1+2+3 を走らせる**: 上記 (2) の対偶。`--clean` 経由 or 初回 setup のときだけ Phase 1 (worker DB bootstrap) → Phase 2 (coord DB CREATE) → Phase 3 (Citus topology) の全部が走る。
4. **`create_distributed_table` / `citus_add_local_table_to_metadata` は per-table メソッドの責務**: `CreateXxxTableAsync` がテーブル新規作成したら続けて呼ぶ (`CreateTableAsync` は `Task<bool>` で「作成 / スキップ」を返す)。既存テーブルスキップ時は distribute も触らない (DB 既存ケースの整合と一致)。
5. **Phase 4 (worker への `citus_set_coordinator_host` propagation) は不要**: probe ([multinode_probe.sh](../../tests/citus/multinode_probe.sh) section 6/10) で Citus 14 において確認: coordinator 側で `citus_add_node('worker', port)` を呼ぶだけで、worker 側 `pg_dist_node` に coordinator (groupid=0) 行が auto-sync される。`citus_add_local_table_to_metadata` も worker への明示 set_coordinator_host 無しで成立。

### EnsureDatabaseAsync の Phase 構造

```text
EnsureDatabaseAsync:
  [pre] coordinator pgfs DB の存在チェック
    既存 → LogExistingCitusStateAsync (Citus 時のみ) → return
    不在 → 以下を続行

  [Phase 1] 各 worker 上に pgfs DB + Citus 拡張を確保 (--citus + worker 指定時のみ実体動作)
    foreach worker:
      super to worker maintenance:
        EnsureDatabaseOnAsync(worker pgfs DB)
      super to worker pgfs DB:
        CREATE EXTENSION IF NOT EXISTS citus
      // CREATE SCHEMA はやらない (Phase 4 不要と同じ理由で、Citus の DDL 伝搬に任せる)

  [Phase 2] coordinator pgfs DB を CREATE

  [Phase 3] Citus トポロジ (coordinator 側のみ、auto-sync で workers に伝搬)
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

実装上気付いたつまずきポイント:

- **Citus は unique constraint に分散キーが含まれることを要求する**。`pgfs_inode` の PK は当初 `(id)` だったが、`parent_id` 分散にすると Citus が `cannot create constraint on "pgfs_inode"` で蹴る。PK を `(parent_id, id)` の複合に変更し、`WHERE id = @id` 検索用に id 単独 INDEX を別途追加。id は BIGSERIAL でグローバルに一意 (sequence は coordinator) なので `(parent_id, id)` も実質 id だけで一意。
- **`id` 単独 UNIQUE 制約は単 PG モードでもあえて張らない (決定)**: Citus 環境では UK / PK / EXCLUSION のどれを使っても id 単独制約は作れない (上の通り)。CHECK は subquery 不可なので「他行との一意性」は表現不能、trigger で疑似実装すると毎 INSERT で cross-shard lookup が走って性能崩壊。「単 PG モード時だけ id 単独 UK を張る」分岐は技術的に可能だが、(a) 単 PG / Citus でスキーマがズレる、(b) 単 PG → Citus への in-place 移行が UK で詰まる、(c) アプリ側で起動時に `SELECT id, COUNT(*) FROM pgfs_inode GROUP BY id HAVING COUNT > 1` を回すような defensive check も「遅くなるだけで体感的な利益が無い」、という理由で **両モード共通で UK なし、BIGSERIAL の sequence + tx 規律で一意性を保つ** という選択をした。根拠: BIGSERIAL の sequence は coordinator 1 つだけで二重払い出ししない / id バイパスは `Api.Rename` の `OVERRIDING SYSTEM VALUE` 経路 1 箇所だけで、そこは DELETE+INSERT が同 tx 内で完結する設計なので中間状態で同じ id が二重に存在することはない。**将来「念のため id 一意性を担保しよう」という提案を再投入する前にこの判断を確認すること** — 既に意識して捨てた選択肢。
- **root inode の ON CONFLICT は `(parent_id, name)` UK ターゲットに変更**。`ON CONFLICT (id)` は (id) 単独 UK が無いと使えないが、Citus 制約上 (id) 単独 UK は作れない。`(parent_id, name)` UK は分散キーを含むので OK。root は (0, '/') で一意。
- **coordinator は常に `pg_dist_node` に登録する必要がある** (`citus_add_local_table_to_metadata` の前提): `citus_add_local_table_to_metadata` は coordinator が pg_dist_node に居ること (groupid=0 の行が存在) を要求する。判定は **「`pg_dist_node` 全体が空か」ではなく「coordinator (`groupid = 0`) 行があるか」**で行う必要がある — 旧実装の「`pg_dist_node` が空のときだけ登録」だと、ユーザーが `citus_add_node` で worker だけ先に追加した構成 (= pg_dist_node 非空、でも coordinator 未登録) で後段の local 登録が死ぬ落とし穴があった。mkfs --citus は **`SELECT count(*) FROM pg_dist_node WHERE groupid = 0`** が 0 のときに `citus_set_coordinator_host(host, port)` を呼ぶ。
- **`shouldhaveshards = true` は worker 0 件のときだけセット**: 1 ノード構成 (`pg_dist_node` に worker が居ない) では `create_distributed_table` が "replication_factor (1) exceeds number of worker nodes (0)" で死ぬので、coordinator 自身を shard ホストにする必要がある (`citus_set_node_property(host, port, 'shouldhaveshards', true)`)。worker が居る構成では shard は worker 側に置くのが本来の使い方なので、coordinator のデフォルト (`shouldhaveshards = false`) を尊重して触らない。判定は **`SELECT count(*) FROM pg_dist_node WHERE groupid <> 0 AND noderole = 'primary'`** = 0。coordinator 登録判定とは独立。
- **`FOR UPDATE` には分散キーが必須**: `SELECT ... WHERE id = @id FOR UPDATE` は Citus で "could not run distributed query with FOR UPDATE/SHARE commands" で死ぬ。`Api.Rename` と `Api.EnsureDataRow` の FOR UPDATE は `WHERE parent_id = @parent_id AND id = @id` に変更し、parent_id は呼び出し側の `Inode.ParentId` (Rename ではキャッシュが無ければ事前に broadcast SELECT で取る) から渡す。
- **`ON CONFLICT DO UPDATE` 句の式は IMMUTABLE 限定**: distributed/metadata-registered table への `DO UPDATE SET col = current_timestamp` は "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked IMMUTABLE" で死ぬ。`WriteChunkSlice` と `ConfigStore.Save` の `updated_at = current_timestamp` をクライアント側で生成する `@now` に置き換え、`EXCLUDED.updated_at` 参照に変更 (VALUES の定数なので IMMUTABLE 扱い)。単 PG でも同じ SQL がそのまま動くので Citus フラグでの分岐は不要。
- **cross-shard rename は DELETE + INSERT OVERRIDING SYSTEM VALUE**: `Rename` で parent_id を変更する経路は Citus が UPDATE を拒否する (分散キー書き換え)。同 tx 内で旧行を行ロック (FOR UPDATE) → DELETE → INSERT で新 shard に移送、`OVERRIDING SYSTEM VALUE` で BIGSERIAL の id を温存。parent 不変ケースは従来の UPDATE が単一 shard 内で軽量なのでそのまま。

### テーブル別の分散戦略

| テーブル | 分散方式 | キー | 理由 |
|---|---|---|---|
| `pgfs_inode` | distributed | `parent_id` | `(parent_id, name)` UK が shard 内で閉じる (Citus は cross-shard UK を保証しない) / 同一ディレクトリの ListChildren が 1 shard で完結 / path traversal は cross-shard hop が起きるが InodeCache が吸収 |
| `pgfs_data` | distributed | `id` | 単純な lookup。ハードリンク兄弟は同じ data_id を共有するので `pgfs_data_chunk` と co-locate |
| `pgfs_data_chunk` | distributed | `data_id` | 1 ファイルの全 chunk が同 shard → sequential read/write が 1 shard 内で完結。`colocate_with => 'pgfs_data'` で `pgfs_data` と同じ shard に |
| `pgfs_lock` | **local (= 非分散)** | — | Phase 3 で導入 (当初は `target_id` 分散)。** citus local へ変更**: 分散テーブルは `shard_replication_factor > 1` のとき行ロックを拒否するため、rf に依存しないロック機構にするには非分散が必要。`citus_add_local_table_to_metadata` で metadata 登録すると worker を入口にしたクエリもこの 1 行へルーティングされ、入口ノードが違っても排他が成立する (下記「排他制御と replication factor」) |
| `pgfs_settings` | **local (= 非分散)** | — | 全 worker からの読み書きが無いので分散不要。`citus_add_local_table_to_metadata` で metadata 登録だけしておくと、将来分散テーブルから JOIN したくなったときに参照可能 |

### 排他制御と replication factor

**行ロックは `{prefix}lock` だけに集約する**。`{prefix}inode` 等の分散テーブルには `FOR UPDATE` を打たない。

理由: Citus の shard 複製 (`citus.shard_replication_factor > 1`) は **statement-based replication** なので、placement 間で結果がズレ得る操作を拒否する。行ロックはその代表で、`SELECT … FOR UPDATE` は分散キー等値フィルタを付けても `0A000 could not run distributed query with FOR UPDATE/SHARE commands` になる。ロック機構を rf ごとに切り替えるのは筋が悪いので、**ロック対象のテーブルだけ非分散**にして rf から独立させた。

| 選択肢 | 行ロック | 別ノード入口どうしの直列化 | ロック 100 回 (coordinator 入口) | placement |
|---|---|---|---|---|
| 分散 (rf=1) | ✅ | — (rf=1 は複製なし) | 32 ms | 1 |
| 分散 (rf≥2) | ❌ 拒否される | — | — | rf |
| **citus local** (採用) | ✅ | ✅ 実測 2.0 秒待つ | **11 ms** | 1 (coordinator) |
| 参照テーブル | ✅ | ✅ 実測 2.0 秒待つ | 61 ms (全 placement を触る) | 全ノード |

* **citus local を採ったのは**「実在する 1 行に Citus がルーティングする」ため。ノードローカルな状態である advisory lock と違い、**worker を入口にしても同じ行を掴む** (Citus 11+ は全ノードが metadata を持ちクエリ入口になれる = multi-coordinator 相当)。実測で「citus-test1 が保持中に citus-test2 が待つ」を確認した。
* 参照テーブルでも成立するが、ロックのたびに全 placement を触るので 5.5 倍遅く、かつ placement が 1 つでも欠けるとロックが失敗する (冗長を増やしたのにロックの可用性が下がる)。coordinator ノード障害に強いのは参照テーブル側なので、そこを重視するなら切り替え候補。
* トレードオフ: ロックは **coordinator の 1 行に集中する**。従来 doc の「`target_id` 分散で worker に分散させる」設計根拠は撤回した。実測 0.11 ms/回なので、毎秒数千のメタデータ操作までは詰まらない見込み。
* これに伴い **`{prefix}inode` への `FOR UPDATE` を 2 箇所撤去**した (`Api.Rename` の旧行取得 / `Api.EnsureDataRow` の data 行作成直列化)。どちらも直前に `LockInodes` / `LockInode` で `{prefix}lock` を取っているので排他は等価。
* 結果として **`{prefix}inode` / `data` / `data_chunk` / `audit` の rf は自由**に選べる (rf=1 は RAID0 相当、rf=N は N 重ミラー相当という純粋なストレージ冗長の選択)。

### 書き込み tx の shard 接触順は chunk/data → inode (40P01 根治)

rf ≥ 2 の Citus は statement-based replication の一貫性維持のため、**同一 shard への変更を shard 単位で
直列化する**。つまり「行が違えば衝突しない」は分散テーブルでは成り立たず、**tx がどの順で shard を
触るか**がそのままロック順になる。同一ディレクトリ配下の inode 行は全部同じ shard に落ちる
(分散キー = `parent_id`) ので、並行書き込みは必ず inode shard で合流する。

かつて write-through / flush の tx は冒頭の `UPDATE inode SET data_id` (EnsureDataRow) で inode shard を
掴んだまま chunk/data shard の書き込みへ進んでいた = **「inode を持って data を待つ」hold-and-wait**。
逆順で待つ tx と合わさると分散デッドロック (40P01) の閉路になり、5 並行 create+write で 15〜20%/周の
フレークを起こしていた (`citus_lock_waits` の実測で特定。経緯は [tests.ja.md §根治済み](../tests.ja.md))。

**規約**: 書き込み tx は shard を **`{prefix}lock` (coordinator local) → chunk/data (colocated) →
inode (最後に 1 文)** の順で触る。data_id リンクは tx 冒頭で打たず、末尾の size/mtime UPDATE に畳む
(`FinishWriteInodeInTx` / `UpdateInodeSizeAndMtimeInTx` の link 引数 / `LinkDataIdInTx`)。
新しい書き込み経路を足すときもこの順序を崩さないこと。

**安全網**: それでも 2PC の COMMIT 段など Citus 内部で閉路が残り得るため、create / write / flush の
自己完結 tx は **40P01 / 40001 を最大 4 回・線形バックオフ + ジッタで再実行**する
(`Api.IsRetryableDeadlock`)。victim は全 rollback 済みなので再実行は安全。
効果の実測: deadlock 発生 33 件/60 周 → **2 件/120 周 (≈99% 減) + 残余はリトライが吸収 = fail 0**。

### shard 数 / 複製数の指定

`create_distributed_table` 実行時のセッション GUC が確定値になるので、mkfs が同一コマンドにまとめて送る:

| オプション | 意味 | 既定 |
|---|---|---|
| `--shard-count <n>` | `citus.shard_count` | `0` = クラスタ既定に従う (何も設定しない) |
| `--shard-replication-factor <n>` / `--rf <n>` | `citus.shard_replication_factor` = shard 1 つを何ノードに置くか | `0` = クラスタ既定に従う |

`SET` を別呼び出しにすると接続プールから別の物理接続を引く可能性があるため、**`SET …; SELECT create_distributed_table(…)` を 1 コマンドで**発行している。クラスタ全体 (`postgresql.conf`) やロール/DB 単位の設定を書き換えないので、**同じ DB を共有する他アプリの分散テーブルには影響しない**。

### 既存テーブルを後から Citus 化する (`--distribute-existing`)

mkfs は既定で「**新規作成したテーブルだけ**」Citus 化する。したがって既存スキーマに後から `--citus` を流しても何も起きない。既存テーブルの分散はデータを shard へ再配置する重い操作なので、`--distribute-existing` を明示したときだけ実行する。

* 判定は `pg_dist_partition` に行があるか (分散は `partmethod='h'`、参照 / local は `'n'`) の 1 本で、**冪等**。2 回目以降は「既に Citus 管理下 — スキップ」とログに出る。
* **coordinator の DB が既存のときは topology 系 mutate (`citus_add_node` / `citus_set_coordinator_host` / `shouldhaveshards`) を触らない**のは従来どおり。共有クラスタの構成を壊さないための安全弁で、`--distribute-existing` はテーブルの Citus 化だけを解禁する。
* 実測 (dev サーバ / 共有 Citus 13.1 クラスタ): 3 MiB のファイルを入れた非 Citus FS に `--citus --distribute-existing --rf 2 --shard-count 4` を流すと、4 テーブルが distributed + 3 テーブルが local になり、**ファイルの md5 は不変** (in-place 変換でデータ保持)。

### UPDATE は必ず分散キーを WHERE に含める (router 化)

`{prefix}inode` の分散キーは `parent_id` なので、`WHERE id = @id` だけの UPDATE は **Citus が全 shard に配る**:

```
EXPLAIN UPDATE {prefix}inode SET … WHERE id = 5                     → Task Count: 8   (shard 数)
EXPLAIN UPDATE {prefix}inode SET … WHERE parent_id = 0 AND id = 5   → Task Count: 1   (router)
```

これが 2 つの実害を出していた:

1. **1 メタデータ更新が「shard 数 × placement 数」のリモート文**になる (shard 8 / rf=2 なら 16 文)。
2. **shard ロックの取得順が非決定的**になり、並行時に `40P01 canceling the transaction since it was involved in a distributed deadlock` が起きる。実測: 700 ファイルの `rsync -a` (chmod + utime が毎ファイルに走る) で `ChMod` と `UpdateTimestamps` が各 1 回落ちた (rsync は exit 23)。

対策として、inode を更新する 9 経路すべてで WHERE に `parent_id` を含めるようにした (`UpdateMode` / `UpdateOwner` / `UpdateSize` / `UpdateTimestamps` / `SetXAttr` / `RemoveXAttr` / `ClearInodeDataId` / `UpdateSizeInTx` / `TouchMtimeInTx`)。分散キーは `Api.ResolveParentId` が **InodeCache → 無ければ DB 1 読み** で解決するので、呼び出し元 (FUSE / Dokan) のシグネチャは変えていない。単 PG では条件が 1 つ増えるだけ (PK が `(parent_id, id)` なので同じ経路)。

**残る multi-shard な UPDATE** は `WHERE data_id = …` の 3 箇所 (`DeleteInode` / `CreateHardLink` の `st_nlink` 再計算)。ハードリンクの兄弟 inode は別ディレクトリ = 別 shard になり得るので原理的に broadcast になる。頻度が低いので現状は許容しているが、並行 hardlink 操作ではデッドロックの余地が残る。

### mkfs の Citus 対応

mkfs に 2 つの新フラグ:

| フラグ | 意味 | デフォルト |
|---|---|---|
| `--citus` | Citus 化を有効にする (extension + create_distributed_table 系を呼ぶ) | false |
| `-w` / `--worker` / `--workers` | カンマ区切りの worker spec `host[:port]` | (空) |

使用例:

```bash
# 1 ノード Citus (coordinator のみ、shard も coordinator が持つ)
mkfs.pgfs -f pgfs.toml --clean --citus -c "Host=coord;..." --super "..."

# 多ノード Citus (coordinator + worker1 + worker2)
mkfs.pgfs -f pgfs.toml --clean --citus \
    -c     "Host=coord;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    --super "Host=coord;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
    --worker "w1:5432,w2:5432"
```

mkfs --citus は **DB を新規作成するときのみ** Citus のセットアップを行う:

0. **`--clean` の削除** (v0.2.1〜): **消すノードは DB の実体で決める** — coordinator の対象 DB があれば、その DB の `pg_dist_node` (groupid 0 = coordinator を除く全ノード) の同名 DB → coordinator の DB の順に DROP する。
   **`--citus` / `--worker` は消す側では使わない** (作り直す側の指示)。coordinator の DB が無いときだけ `--worker` を当てにする。**消す前に全 worker へ繋がるか確かめ、1 つでも繋がらなければ何も消さない**
   (途中で止まると coordinator だけ消えて worker にゴミが残るため。使っていない worker は先に `citus_remove_node`)。消したあと接続プールを空にする (`Pg.ClearPools` — DROP で切られた接続を作り直した DB への操作が拾わないように)。
1. **worker bootstrap** (Phase 1): 各 `--worker` 上に super 接続で `EnsureUser` → `CREATE DATABASE pgfs` → `CREATE EXTENSION IF NOT EXISTS citus`
2. **coordinator DB ensure** (Phase 2): coordinator 上に super 接続で `CREATE DATABASE pgfs`
3. **Citus topology** (Phase 3):
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

`citus_add_local_table_to_metadata` の意義: `pgfs_settings` は coordinator のみに置くが、Citus に metadata 登録しておくと (a) 将来 distributed テーブルから JOIN したくなったときに使える、(b) Citus の backup/restore ツールが認識する、(c) `citus_tables` view 等の調査系で見える、というメリット。コストはほぼゼロ。

**DB 既存時の動作** (`--clean` なし or `--clean` でも drop されなかった場合):

mkfs は `EnsureDatabaseAsync` の冒頭でcoordinator DB 存在チェックを行い、**既に存在するなら Citus 関連は read-only で現状把握だけして即 return**。worker bootstrap も Citus topology 設定も触らない。これは「mkfs を冪等に何度叩いてもクラスタ状態は不変」を保証するための設計。詳細は上の「設計の核となる制約」参照。

### `pgfs_inode` の path traversal コスト

`/a/b/c` を解決するのに `pgfs_inode` を `parent_id` 分散すると:

1. root (id=0) を取る → 1 shard
2. root の子 `a` を取る (`WHERE parent_id=0 AND name='a'`) → parent_id=0 が乗っている shard へ (=root を持つ shard)
3. `a` の子 `b` を取る → parent_id=a.id が乗っている shard へ。**a の id を hash した shard なので別 shard の可能性大**
4. `b` の子 `c` を取る → 同じく hash で決まる shard へ

depth N のパスで最悪 N 回の cross-shard hop。実運用では:

- **`InodeCache.byPath` が効くと 2 回目以降は 0 hop** ([src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs))
- cold start (e.g. プロセス再起動直後) で初回 path 解決のみ遅い
- ディレクトリの深さは実用上 10〜20 程度 → 1 リクエスト 10〜20 × shard 跨ぎ ≈ 数十 ms (許容範囲)

InodeCache のヒット率が悪化するワークロード (例: 大量の random path access) ではボトルネックになり得る。その場合は:

- `InodeCache` の `byId` / `byPath` のサイズを `mount.cache_max_entries` で増やす (default 1024 → 数万に)
- coordinator 側で path → inode_id の materialized view を維持する案も検討余地あり

### cross-shard rename

`Rename(id, newParentId, newName)` で newParentId が現在の shard と違うと、行が shard を跨ぐ移動になる。Citus は **distribution key の変更を伴う UPDATE をサポートしない** ので、`UPDATE pgfs_inode SET parent_id = @new` が直接は通らない可能性がある (Citus のバージョン依存)。

対処:

```sql
BEGIN;
INSERT INTO pgfs_inode (parent_id, name, ...) SELECT @newParent, @newName, ... FROM pgfs_inode WHERE id = @id;
DELETE FROM pgfs_inode WHERE id = @id;
COMMIT;
```

= 「新規 INSERT + 古い行 DELETE」で別 shard 間移動を実現。ただし inode_id が変わってしまうので、inode_id を保持する SQL に書き換える (PRIMARY KEY を BIGSERIAL から `id` を明示指定に変える等) 必要あり。**実装時の主要な検討事項**。

代替案として inode_id 自体を分散キーにする (= `pgfs_inode` を `id` 分散) と rename が単純な UPDATE で済むが、`(parent_id, name)` UK が壊れる (cross-shard で UK 保証なし)。trade-off は採用前に再検討。

---

## Phase 3: `pgfs_lock` テーブル + `SELECT FOR UPDATE` (完了)

**完了**。実装は [src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs) の `LockTargets` / `LockData` / `LockInode` / `LockInodes` ヘルパに集約 (file 中段、`QualifiedTable` のすぐ下)。各 mutating Api メソッドの冒頭で適切なロックを取り、tx 終了 (COMMIT/ROLLBACK) で自動解放する。Linux 34/34・Windows 24/24 (回帰なし、単 PG モード) を維持。多ノード Citus + 2 client 並行 race も [tests/citus/race_multinode.sh](../../tests/citus/race_multinode.sh) で 4/4 PASS (Linux e2e 34/34、write race md5 一致、mkdir race EEXIST、pgfs_lock 累積サイズ妥当)。

実装上の判断:

- **`LockTargets` の SQL は INSERT ON CONFLICT DO NOTHING + SELECT 1 ... FOR UPDATE の 2 文**: 各文とも target_id 単独 WHERE なので単一 shard 完結 (Citus でも安全)。CTE で 1 文に畳む案は試さなかった (シンプルさ優先)。
- **複数 lock 取得は `LockInodes(params long[])` で target_id 昇順固定**: 入力の inode_id を負号反転 → 重複排除 → 昇順ソート → 順に SELECT FOR UPDATE。これで「Rename(A, parent_of_A, newParent)」と「Rename(B, ...)」が共通の inode を別順序で取る race でもデッドロックしない。
- **`WriteData` / `TruncateData` は `EnsureDataRow` の後にロック**: `inode.DataId` が null の場合 `EnsureDataRow` が data 行を作って `inode.data_id` を埋める (内側で inode 行 FOR UPDATE で並行 race を直列化済み)。そのあとで `LockData(dataId)` を取って WriteData/TruncateData 同士の race を直列化する。inode 行ロック + data 行ロックを同 tx で持つことになるが、order は固定 (inode 先 → data 後) なので別 tx と衝突しない。
- **`Update*` 系 (Mode/Owner/Size/Timestamps) は単発 `Pg.Execute` から tx ベースに変更**: 元は connection を 1 SQL 分だけ open → close する構造で lock を持てなかった。`using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE に変更。
- **`SetXAttr` / `RemoveXAttr` は lock 対象外**: docs の lock 対象表に含めない判断 (要件 / メタデータ並行更新の lost-update リスクが低い + JSON merge / strip は単一 UPDATE で atomic)。必要があれば後付け可能。
- **`DeleteInode` の data drop パスには data lock を**追加していない**: docs の lock 対象表が inode のみだったので踏襲。delete-while-open race (open file handle が掴んでいる data に対する WriteData が並行) は理論上残るが、要件として「open 中に削除されたファイルへの I/O は undefined」を前提とする。

### 動機

複数クライアント (例: Linux mount.pgfs + Windows assign.pgfs が同じ pgfs DB を共有) が **同じファイルを同時に書く** と race が起こる:

- `WriteData`: 2 client が同じ chunk を read-modify-write すると lost-update
- `TruncateData`: write 中に truncate されると不整合
- `Rename`: 2 client が同じファイルを別パスへ rename しようとすると 1 つは ON CONFLICT で弾かれるが、メモリ側の InodeCache が古い state を保持する

[Notify (LISTEN/NOTIFY)](../history.ja.md) は **変更の事後通知** であって race の防止ではない。書き込み中の排他制御として別途必要。

### なぜ自作テーブル + `SELECT FOR UPDATE` か

検討した 3 案の比較:

| 観点 | `pg_advisory_xact_lock` | 自作 TTL テーブル + 心拍 | **`pgfs_lock` + SELECT FOR UPDATE (採用)** |
|---|---|---|---|
| 取得コスト | μs (in-memory hash) | ms + 心拍 | ms (lock 行 cache 済みなら速い) |
| 待ち | server-side block | client polling (LISTEN/NOTIFY で擬似ブロックは可能) | **server-side block** |
| 自動解放 | session / tx 終了 | TTL 待ち (最大 30s 遅延) | **tx 終了** |
| TTL race | なし | あり (心拍遅延 + TTL race で二重取得発生) | **なし** |
| 専用接続要 | (xact 版は不要) | 必要 (心拍用) | **不要** |
| Citus 分散 | coordinator local only | 分散可 (target_id key) | **分散可** |
| 可視性 | `pg_locks` view | `SELECT FROM pgfs_lock` | `pg_locks` + (誰が待ってるかは pg_stat_activity) |
| 多 tx 跨ぎ保持 | session 版で可 | 可 | 不可 (tx 終了で解放) |

**結論**: `SELECT FOR UPDATE` 案が in-memory advisory lock の良さ (sub-ms / server-side block / TTL race なし / 自動解放) と、TTL テーブル案の良さ (Citus 分散可) を両取りできる。pgfs の書き込みは「1 操作 = 1 tx」なので tx 跨ぎ保持は不要 = `FOR UPDATE` で十分。

`pg_advisory_xact_lock` を選ばなかった理由:

- coordinator local: Citus で worker に lock 取得を分散できない → coordinator のセッション table がボトルネック
- multi-coordinator HA Citus では coordinator 間で見えない

自作 TTL テーブルを選ばなかった理由:

- TTL race (有名な Redis SETNX with TTL の問題) を完全に潰すには fencing token 等が必要で複雑度が上がる
- 心拍用の専用 connection + バックグラウンドタスクが必要
- 取得時の client polling (or LISTEN/NOTIFY による擬似ブロック) で latency が悪化

### なぜ単一 BIGINT (`target_id`) なのか

Citus の `create_distributed_table` は **ハッシュ分散で、ハッシュの入力は単一カラムの値のみ**を受け付ける。複合キー (例: `(kind TEXT, id BIGINT)` で kind = 'inode' / 'data' を分ける案) を直接の分散キーにはできない。`pgfs_lock` を distributed にしたい (Phase 3 動機: coordinator-local ボトルネック回避) 以上、分散キーは 1 カラムでなければならず、必然的に `target_id BIGINT` の 1 列構成になる。

帰結として、**inode の id と data の id (どちらも BIGSERIAL) が同じ数値空間で衝突し得る**問題に向き合う必要がある。inode_id = 5 と data_id = 5 をどちらも単一の `pgfs_lock(target_id)` で扱うと、片方を lock した tx がもう片方の lock 取得を意図せず block してしまう。

### 採用した衝突回避: 符号による namespace 分離

`data lock = target_id = data_id (正)`、`inode lock = target_id = -inode_id (負)` で値の符号で namespace を分ける ([Api.cs](../../src/core/src/Api/Api.cs) の `LockData` / `LockInode` ヘルパ参照、下に実装イメージあり)。`pg_advisory_xact_lock(ns, key)` の 2-key 形式と違って single column PK しか持てないので、namespace は **値の側で表現** するしかない。

代替案として **`pgfs_inode_lock` を別テーブルに切り出す**選択肢もある。後者の方が:
- inode lock を `parent_id` 分散 (data と別 colocation group) にして inode 操作の cross-shard hop を削れる
- inode 用 lock 表と data 用 lock 表で運用統計を分けやすい

…という利点があるが、Phase 2 時点では「テーブルを 1 つ余計に作るほど運用パターンが固まっていない」と判断し、まずは単一 `pgfs_lock` + 符号 namespace で始める。inode lock の workload が支配的だと分かったら `pgfs_inode_lock` に再編する余地として残す。

### スキーマ

```sql
CREATE TABLE pgfs_lock (
    target_id BIGINT PRIMARY KEY
);
```

実体は 1 列 PK のみ。lock 取得は「行ロックを取る」だけなので追加列は不要。

Citus 環境では:
```sql
SELECT create_distributed_table('pgfs_lock', 'target_id');
```

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

[Api.cs](../../src/core/src/Api/Api.cs) に薄いヘルパを置く:

```csharp
private void LockData(NpgsqlConnection conn, NpgsqlTransaction tx, long dataId) {
    conn.Execute(
        $"INSERT INTO {this.QualifiedTable("lock")}(target_id) VALUES (@id) ON CONFLICT DO NOTHING; " +
        $"SELECT 1 FROM {this.QualifiedTable("lock")} WHERE target_id = @id FOR UPDATE",
        new { id = dataId }, tx);
}

private void LockInode(NpgsqlConnection conn, NpgsqlTransaction tx, long inodeId) {
    // inode と data の namespace を分けるため、inode は負の領域を使う (-inode_id)。
    // または別テーブル (pgfs_inode_lock) を分けるならそれでも可。`SELECT pg_advisory_xact_lock(ns, key)`
    // の 2-key 形式と違って single column PK なので、namespace は値で分ける必要がある。
    conn.Execute(
        $"INSERT INTO {this.QualifiedTable("lock")}(target_id) VALUES (@id) ON CONFLICT DO NOTHING; " +
        $"SELECT 1 FROM {this.QualifiedTable("lock")} WHERE target_id = @id FOR UPDATE",
        new { id = -inodeId }, tx);
}
```

(namespace 分離の根拠と代替案の比較は上の「なぜ単一 BIGINT (`target_id`) なのか」セクション参照。)

### lock 取得が必要なパス

| Api メソッド | lock 対象 | 理由 |
|---|---|---|
| `WriteData(inode, off, src)` | data: `inode.DataId` | 同じファイルの並行書き込みを直列化 |
| `TruncateData(inode, len)` | data: `inode.DataId` | 同上 |
| `ReleaseData(dataId)` | data: `dataId` | LO 解放中の他クライアント書き込みを防ぐ |
| `UpdateMode/Owner/Size/Timestamps(id)` | inode: `id` | メタデータ並行更新の lost-update を防ぐ |
| `Rename(id, newParent, newName)` | inode: `id`, `oldParent`, `newParent` | 旧親 / 新親 / 対象 inode の 3 ロック。`昇順固定` でデッドロック回避 |
| `DeleteInode(inode)` | inode: `inode.Id`, `inode.ParentId` | 親の children list と本体の同時変更を直列化 |
| `CreateHardLink(source, newParent, newName)` | inode: `source.Id`, `newParent` | hardlink 兄弟の nlink 更新と新規 inode 作成を直列化 |

複数 lock を取るパス (`Rename` / `DeleteInode` / `CreateHardLink`) では **target_id 昇順固定** でデッドロック回避。

### lock 行の累積

`INSERT ON CONFLICT DO NOTHING` で行が増えるだけで、`DELETE` しない方針。

- 1 行 ~50 bytes (PRIMARY KEY index 込み)
- 100 万ファイル × 2 (data + inode) = 約 100MB → 無視できる
- 削除を入れると「lock 行 DELETE される ⇄ 別 tx が SELECT FOR UPDATE 取りに来る」で race の余地が生まれる
- 必要なら別途 cron で `DELETE` (現実的には不要)

### 注意点

- **co-location の非対称** (Phase 2 検証で確認、2026-05-26): `mkfs --citus` 後の `citus_tables` を見ると `pgfs_inode` / `pgfs_data` / `pgfs_data_chunk` / `pgfs_lock` の全 4 distributed テーブルが **同じ colocation_id (default group)** に入る (BIGINT 分散キー + 同 shard_count なので Citus が自動 co-located にする)。これが効くケースと効かないケース:
  - **data lock を取るケース** (target_id = data_id): `pgfs_lock` の分散キー値と `pgfs_data` の分散キー値 (id) が同じ数値 → hash も同じ → **同一 shard に着地、ネット越し越境なし**
  - **inode lock を取るケース** (target_id = -inode_id): `pgfs_lock` の hash(-inode_id) と `pgfs_inode` の hash(parent_id) は別物 → **lock shard と inode shard はずれる** (1 ネット越し)

  当面は許容するが、inode lock が支配的な workload が見えてきたら `pgfs_inode_lock` を別テーブルにして `parent_id` 分散 + `pgfs_inode` と co-located にすることで非対称を解消できる。
- **Citus で複数 shard を跨ぐロック**: `WHERE target_id IN (a, b)` で a と b が別 shard だと取得順序が非決定的になりデッドロックリスクが上がる。**1 SQL = 1 ロック単位** を守る (上のヘルパは 1 件ずつ取る形)。
- **取り忘れ**: `LockData` / `LockInode` を呼ばずに `WriteData` 等を書くと race が起きる。コードレビューで全 mutation path を確認する規律が必要。`Api.WriteData(...)` の冒頭で `LockData` を呼ぶ規約を [Api.cs](../../src/core/src/Api/Api.cs) のクラス doc にも明記する。
- **アプリ側のデッドロック**: 同じスレッドが入れ子で `LockData(A)` → `LockData(B)` のように複数取ると、別スレッドが逆順で取ると hang。**lock の取得順は target_id 昇順** をプロジェクト規約として固定する。

---

## 移行時の検証項目

Phase ごとに e2e で確認すること。

### Phase 1 (bytea 化) 検証

- [x] Linux 34/34、Windows 24/24 を維持 (達成)
- [x] `test_large_file_round_trip` で 2 MiB ファイルの byte 単位 round-trip 一致 (Linux/Windows 両 e2e に含まれる)
- [x] `test_truncate_*` で truncate 後の payload 長が一致 (Linux/Windows 両 e2e に含まれる)
- [ ] partial read (random offset) のレスポンスタイムが LO 版から劣化していないこと (BenchmarkDotNet なしで、`time dd if=... bs=4K count=1 skip=1000` 等で粗く確認) — **未実施 (性能評価は別タスク)**
- [ ] DB size (`pg_database_size`) が LO 版と概ね一致 (TOAST 圧縮効率も大差なし) — **未実施**

### Phase 2 (Citus 化) 検証

- [x] Citus + 1 ノード (worker なし、coordinator のみ) でセットアップ、`mkfs --clean --citus` で初期化 (達成、Citus 13.1.1 on pgsql_server / Citus 14.0.0 on docker)
- [x] Citus + 多ノード (coordinator + 1 worker) でセットアップ、`mkfs --clean --citus --worker w1` で初期化 (達成、Citus 14.0.0 on docker)
- [x] Linux / Windows e2e がそのまま通る (= 分散テーブル化が機能に影響しない) — Linux 34/34、Windows 24/24 ALL PASSED (1 ノード Citus + pgsql_server)
- [x] mkfs マトリックステスト: 3 initial × 6 target = 18 ケースの組合せで「新規 / 既存維持 / --clean による再構築」が意図通り — [tests/citus/test_matrix.sh](../../tests/citus/test_matrix.sh) で 18/18 PASS (2026-05-26)
- [x] `EXPLAIN ANALYZE` で path 解決クエリが「期待した shard」へ届いているか確認 — [tests/citus/verify.sql](../../tests/citus/verify.sql) で確認 (`WHERE parent_id = N AND id = N` は Task Count 1、`WHERE id = N` 単独は 32 shards 走査)
- [x] cross-shard rename が動く (新規 INSERT + 旧 DELETE 経路) — `test_rename_into_subdir` (Linux/Windows 両 e2e) が parent 変更 rename をカバー、Citus モードで通過済み
- [x] `pgfs_settings` が coordinator にしかないこと、`citus_tables` view に登録されていることを確認 — verify.sql で確認 (citus_table_type='local')
- [x] DDL 伝搬: coordinator の CREATE/DROP SCHEMA が workers に自動伝搬すること — [multinode_probe.sh](../../tests/citus/multinode_probe.sh) section 8/13 で確認
- [x] `citus_add_node` の auto-sync: worker 側 `pg_dist_node` に coordinator (groupid=0) 行が自動追加されること — multinode_probe.sh section 6 で確認 (Phase 4 不要を実証)
- [x] `citus_add_local_table_to_metadata` が worker 側への明示 `citus_set_coordinator_host` 無しで通ること — multinode_probe.sh section 10 で確認
- [ ] worker を停止して片肺 → 該当 shard へのアクセスがエラー応答する (整合性 OK) — **未実施** (Phase 3 排他制御の検証時に併せて実施予定)
- [ ] worker 復旧 → アクセス復帰 — **未実施** (同上)
- [ ] 多ノード Citus 上での Linux/Windows e2e 通過 — **未実施** (現状 1 ノード Citus でのみ実機 e2e 通過、多ノード Citus は mkfs test_matrix までで stop)

### Phase 3 (lock) 検証

- [x] 単一 client で全 e2e を維持 (lock 取得が write path に入ってもパフォーマンス劣化が許容範囲内) — Linux 34/34・Windows 24/24 ALL PASSED (2026-05-26、単 PG モード)
- [x] 多ノード Citus (docker 上 coord + worker1) での Linux e2e 通過 — **34/34 PASS** ([tests/citus/race_multinode.sh](../../tests/citus/race_multinode.sh) Test 1、2026-05-26)
- [x] 2 client から同じファイルへの並行 write race 試験 — `dd /dev/urandom→mount1` + `dd /dev/zero→mount2` を 8MiB × 4 round、両 client から見る md5/size が常に一致 (race_multinode.sh Test 2)
- [x] 2 client から同じディレクトリへの並行 mkdir race — 20 round × (serial + parallel) で常に片方が EEXIST (`(parent_id, name)` UK が cross-shard 整合性を保証) (race_multinode.sh Test 3)
- [x] `pgfs_lock` 行が `DELETE` されず累積するが、サイズが現実的範囲に収まることを確認 — 上記 4 テスト後で **rows=178 / size=768kB** = 設計値 (1 行 ~50 bytes、100 万行で 100MB 以下) と整合 (race_multinode.sh Test 4)

---

## 未解決事項 / 将来の検討

- **`pgfs_inode` の分散キーを `parent_id` にすると path traversal が cross-shard hop を起こす** ことの実測コスト。InodeCache のヒット率が低いワークロードでどれくらい遅くなるか。
- **cross-shard rename の inode_id 保存** : INSERT + DELETE 方式だと BIGSERIAL の id が変わる。`OVERRIDING SYSTEM VALUE` 等で同じ id を維持する SQL の設計。
- **multi-coordinator HA Citus** (Enterprise) を視野に入れるかどうか。入れるなら advisory lock + coordinator local の組み合わせは別途検討が必要。
- **bytea の partial read (`substring`) が PG 13+ で本当に partial TOAST detoast されるか** の実測。可能なら chunk_size を大きくしても OK。
- **`pgfs_inode` を `id` 分散にして `(parent_id, name)` UK を別レイヤで保証する案** (例: `unique_violation` を application 側でリトライ等) も再検討余地あり。`parent_id` 分散の rename コストが許容できなければこちらに振る。
- **Citus version の依存**: 検証時の Citus バージョンを記録。`create_distributed_table` のシグネチャ変更等あるため。
