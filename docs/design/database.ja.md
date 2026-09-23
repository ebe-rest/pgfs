# データベーススキーマ設計

> **道順**: [docs/README.md](../README.md) › **本書**
>
> **この doc が正である範囲**: **DB スキーマ設計** — 各テーブルの役割と列の意味、PK / インデックスの
> 選び方 (Citus の制約込み)、**タイムスタンプ規約 (常に UTC)**、**実占有バイトと `st_blocks`**。
> 「なぜこの形か」はここが正。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../ddl/README.md](../ddl/README.md) | **DDL 本体** (テーブル 1 つ = 1 ファイル) |
> | [../Mkfs.md](../Mkfs.md) | DDL を適用する CLI と既定値 |
> | [support_for_citus.md](support_for_citus.md) | 分散キーの選択・shard 数・排他制御 |
> | [data-id-lifecycle.md](data-id-lifecycle.md) | `data_id` の寿命 (create で確定し unlink まで不変) |
> | [audit-log.md](audit-log.md) | `{prefix}audit` の月次パーティション |
> | [xattr-bytea.md](xattr-bytea.md) | xattr 列のバイト列透過 |

PostgreSQLファイルシステムドライバ（PGFS）のデータベーススキーマ設計です。

> **DDL ファイル**: 各テーブルの CREATE 文は [docs/ddl/](../ddl/README.md) にテーブル単位で分割して置いてあります。手動でセットアップしたい場合や設計を確認したい場合の参照に使えます。
>
> **Citus (水平分散) 対応**: 分散戦略、bytea 化、`pgfs_lock` テーブル等は [docs/support_for_citus.md](support_for_citus.md) を参照 (Phase 1+2+3 完了、`mkfs --citus` で opt-in)。

-----

## 🏛️ PGFS データベーススキーマ設計概案

* 監査目的のため、作成日時、作成ユーザー名、更新日時、更新ユーザー名を持ちます。
* 外部キーや外部制約、トリガーは障害発生時の手作業による管理を複雑にするため作成しません。ただし、シーケンスは使用します。

### ⏱️ タイムスタンプ規約: `TIMESTAMP` 列の値は常に UTC

タイムスタンプ列はすべて `TIMESTAMP` (= `timestamp without time zone`) で、**保持する値は常に UTC 壁時計**です。FUSE / Dokan へ返すときは UTC として epoch に変換するため (`FileSystem` の `DateTimeKind.Utc` 指定)、書き込み側もこれに揃える必要があります。ローカル時刻が混ざると、**ホストの UTC オフセット分ずれた mtime/ctime** が見えます (JST なら 9 時間) 。

守り方は 2 つだけです。

| 生成側 | 書き方 |
|---|---|
| SQL 内で「今」を入れる | `current_timestamp AT TIME ZONE 'UTC'` (素の `current_timestamp` はセッション TimeZone のローカル壁時計) |
| C# から `DateTime` を渡す | `Pg.UtcNow` / `Pg.ToDbUtc(value)` で **UTC 壁時計 + `DateTimeKind.Unspecified`** に正規化する |

`Kind=Utc` の `DateTime` をそのままパラメータにしてはいけません。Npgsql はそれを `timestamptz` として送るため、PG が `timestamp` 列へ代入する際に**セッション TimeZone でキャスト**し、ローカル壁時計が保存されます。同じ理由で、DB 経過時間を SQL で計算するときも `now() AT TIME ZONE 'UTC'` と比較します (`StatusAdmin.ListMounts` の uptime / heartbeat 経過)。

* 列 DEFAULT も `DEFAULT (current_timestamp AT TIME ZONE 'UTC')` です ([docs/ddl/](../ddl/README.md))。
* 監査ログの月次パーティション境界も UTC 基準で決まります (`occurred_at` が UTC なので一致)。
* **既存 FS の移行**: `mkfs` は既存テーブルの DEFAULT を書き換えないため、この規約より前に作った FS はローカル時刻の行と古い DEFAULT を持ちます。UTC 以外のタイムゾーンのホストで作成した FS を移行するなら、DEFAULT の付け替え (`ALTER TABLE … ALTER COLUMN … SET DEFAULT (current_timestamp AT TIME ZONE 'UTC')`) と既存行のシフト (`UPDATE … SET st_mtime = st_mtime - interval 'N hours'`、N = 作成時のホストの UTC オフセット) が必要です。UTC のホスト (docker コンテナ等) で作った FS はズレが 0 なので DEFAULT の付け替えだけで済みます。

### 1\. ディレクトリエントリと inode 属性 (`pgfs_inode`)

DDL: [docs/ddl/pgfs_inode.sql](../ddl/pgfs_inode.sql)

ファイルシステムの中核となるテーブルで、すべてのファイル、ディレクトリ、シンボリックリンク、ハードリンクの情報（inode的なメタデータ）を保持します。

| カラム名          | データ型        | NULL       | DEFAULT             | INDEX   | 説明                                                    |
|:--------------|:------------|:-----------|---------------------|---------|:------------------------------------------------------|
| `id`          | `BIGSERIAL` | `NOT NULL` |                     | 複合 PK の一部 | 一意のノードID。全てのファイル/ディレクトリの識別子。ルートは 0 でDB構築時に INSERT     |
| `parent_id`   | `BIGINT`    | `NOT NULL` |                     | IX1 UK1 | 親ディレクトリの `inode_id`。ルートは 0 。                          |
| `name`        | `TEXT`      | `NOT NULL` |                     | IX2 UK1 | 親ディレクトリ内でのファイル名。UTF-8で統一。                             |
| `uname`       | `TEXT`      | `NOT NULL` |                     | IX3     | オーナーユーザー名。                                            |
| `gname`       | `TEXT`      | `NOT NULL` |                     | IX4     | オーナーグループ名。                                            |
| `st_mode`     | `INTEGER`   | `NOT NULL` |                     |         | ファイルの種類とパーミッション (chmod)。                              |
| `st_nlink`    | `INTEGER`   | `NOT NULL` | `1`                 |         | ハードリンク数。                                              |
| `st_size`     | `BIGINT`    | `NOT NULL` | `0`                 |         | ファイルサイズ（バイト）。ディレクトリやシンボリックリンクは **0 のまま** (理由は下記注)。      |
| `st_mtime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |         | 最終更新時刻（マイクロ秒精度）。                                      |
| `st_ctime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |         | iノード最終変更時刻（マイクロ秒精度）。                                  |
| `link_target` | `TEXT`      | `NULL`     |                     |         | シンボリックリンクやジャンクションの場合のリンク先パス。                          |
| `is_junction` | `BOOLEAN`   | `NOT NULL` | `FALSE`             |         | Windowsジャンクションの場合 `TRUE`。                             |
| `data_id`     | `BIGINT`    | `NULL`     |                     |         | ファイルデータ本体を参照するID（`pgfs_data` の ID）。ディレクトリの場合は `NULL`。 |
| `xattr_names`  | `TEXT[]`  | `NOT NULL` | `{}`                |         | 拡張属性 (xattr) の名前配列。同じ index の `xattr_values` とペア。予約キー: `user.pgfs_acl` (正準 ACL ドキュメント JSON)、`user.win.attrs` (Windows 属性 JSON `{hidden,system,archive}`)。詳細は [permission-interop.md](permission-interop.md)。 |
| `xattr_values` | `BYTEA[]` | `NOT NULL` | `{}`                |         | xattr の値配列 (bytea で忠実保持、NUL 含む任意バイト列可)。`xattr_names` と同じ index でペア。旧 `xattrs JSONB`+Base64 から移行。設計は [xattr-bytea.md](xattr-bytea.md)。 |
| `created_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |         | 作成日時                                                  |
| `created_by`  | `TEXT`      | `NOT NULL` |                     |         | 作成ユーザー名                                               |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |         | 更新日時                                                  |
| `updated_by`  | `TEXT`      | `NOT NULL` |                     |         | 更新ユーザー名                                               |

* inode の PK は `(parent_id, id)` であり、`id` 単独の検索用 INDEX を持つ。`(parent_id, name)` は UNIQUE である。
* ルートディレクトリID：`BIGSERIAL` の自動採番既定値は 1 から始まるが、0 の明示 INSERT は可能である。初期データでは `id=0` を指定する ([PostgreSQL serial 型](https://www.postgresql.org/docs/17/datatype-numeric.html#DATATYPE-SERIAL))。
* 最終アクセス時刻 `st_atime` は持ちません。`st_mtime`と同じ値を返します。
* **ディレクトリの `st_size` は 0 固定**（エントリ数や 4096 にしない）。理由: (1) `st_size` を「エントリ数」と解釈する標準ツールは無く（`du` は `st_blocks`、`ls -l` の数字が変わるだけの装飾）、機能上の必要が無い。(2) 読み取り時 COUNT は `getattr` 毎に DB 往復が増えて InodeCache のヒット効果を潰す。(3) 書き込み時に親の件数を維持する方式は全 mutating 操作に UPDATE を足す侵襲があり、装飾目的には見合わない。よって最も安全・低コストな 0 据え置きを採用。

-----

### 2\. データ本体の参照管理 (`pgfs_data`)

DDL: [docs/ddl/pgfs_data.sql](../ddl/pgfs_data.sql)

ハードリンクや bytea チャンク分割を管理するためのテーブルです。

| カラム名         | データ型        | NULL       | DEFAULT             | INDEX | 説明                                    |
|:-------------|:------------|:-----------|---------------------|-------|:--------------------------------------|
| `id`         | `BIGSERIAL` | `NOT NULL` |                     | PK    | データ参照ID。複数のファイル（ハードリンク）から参照される可能性あり。  |
| `chunk_size` | `INTEGER`   | `NOT NULL` |                     |       | ファイルが分割される bytea チャンクのサイズ (バイト)。 |
| `total_size` | `BIGINT`    | `NOT NULL` |                     |       | **格納済み payload のバイト数** (= 全チャンクの `length(payload)` 合計)。論理サイズではない (それは `pgfs_inode.st_size`)。スパースファイルでは穴の分のチャンク行が無いので `st_size` より小さくなる。詳細は下記「実占有バイトと `st_blocks`」。 |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |       | 作成日時                                  |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | 作成ユーザー名                               |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |       | 更新日時                                  |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | 更新ユーザー名                               |

#### 実占有バイトと `st_blocks` (スパースファイル)

本節の「実占有」は **格納済み payload 長の合計**を指し、PG の TOAST 圧縮後の物理量、行・インデックス・WAL のディスク量ではない。`pgfs_data.total_size` はこの値を保持し、FUSE の **`st_blocks`** (= `du` が見る値) を `ceil(total_size / 512)` で返します。論理サイズ (`st_size`) から `st_blocks` を導くと、**スパースファイルで実体の何百倍も報告してしまう**ためです (`truncate -s 1G` したファイルはチャンク行 0 = 実占有 0 なのに 1 GiB と報告される)。

* **維持のしかた**: チャンク payload を変える操作 (チャンク UPSERT / 末端チャンクの切り詰め / 末尾チャンクの削除) が payload 長の**増減 (delta)** を返し、1 操作 = 1 回の `UPDATE … SET total_size = GREATEST(0, total_size + delta) … RETURNING total_size` で反映します。`sum(length(payload))` の再集計はチャンク数に比例するため、書き込みのたびに走らせると大きいファイルで二次的に遅くなります。`length(bytea)` 自体は TOAST ポインタの raw size を読むだけなので detoast は起きません。
* **読み出し**: 実占有バイトは inode 行に無いので、`Api.GetOccupiedBytes` が `{prefix}data` から 1 行だけ引いてメモリ上の `Inode` にキャッシュします。ディレクトリ列挙は `ListChildren` が `id = ANY(…)` で **1 クエリまとめ先読み**して getattr の N+1 を避けます。Citus でも `{prefix}data` の分散キーは `id` なので router クエリで済みます (inode ↔ data の JOIN は分散キーが違うため使いません)。
* **穴の扱い**: 穴の read はゼロ埋めで返し、**チャンク行を作りません** (読んだだけで実体化しない)。穴に書くと**その位置を含む 1 チャンクだけ**が実体化し、そのチャンク内は先頭からのゼロパディングが入ります (`decode(repeat('00', …))`)。
* **`du` が見えない範囲**: Windows (Dokan) 側は `st_blocks` 相当を返していないので、この値は Linux/FUSE でのみ効きます。
* **既存 FS の移行**: この規約より前に作った FS は `total_size` が 0 のままなので `du` が 0 を返します。1 回だけ次を流せば実占有バイトが入ります (以後は自動で維持されます)。

```sql
UPDATE {prefix}data d
   SET total_size = s.sum_len,
       updated_at = (current_timestamp AT TIME ZONE 'UTC')
  FROM (SELECT data_id, sum(length(payload)) AS sum_len FROM {prefix}data_chunk GROUP BY data_id) s
 WHERE d.id = s.data_id AND d.total_size <> s.sum_len;
```

#### チャンク管理 (`pgfs_data_chunk`)

DDL: [docs/ddl/pgfs_data_chunk.sql](../ddl/pgfs_data_chunk.sql)

データ本体を bytea チャンクで格納します。1 チャンク = 1 bytea (デフォルト 1MB)。旧設計では Large Object (`pg_largeobject`) を使っていましたが、Citus 分散ができないため Phase 1 で bytea に置き換えました (詳細は [docs/support_for_citus.md](support_for_citus.md))。

| カラム名          | データ型        | NULL       | INDEX | 説明                                                    |
|:--------------|:------------|:-----------|-------|:------------------------------------------------------|
| `data_id`     | `BIGINT`    | `NOT NULL` | PK    | 親データ参照ID。                                             |
| `chunk_index` | `INTEGER`   | `NOT NULL` | PK    | 0から始まるチャンクの順番。                                        |
| `payload`     | `BYTEA`     | `NOT NULL` |       | チャンクのバイナリデータ本体。長さは「これまで書き込まれたバイト数」(末端は半端、途中の穴は length(payload) が write offset まで届いていない状態で表現される)。1MB クラスは PG TOAST 化されるので部分読み (`substring`) はサーバ側 partial detoast が効く。 |
| `created_at`  | `TIMESTAMP` | `NOT NULL` |       | 作成日時                                                  |
| `created_by`  | `TEXT`      | `NOT NULL` |       | 作成ユーザー名                                               |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` |       | 更新日時                                                  |
| `updated_by`  | `TEXT`      | `NOT NULL` |       | 更新ユーザー名                                               |

> データフロー: `pgfs_inode.data_id` $\rightarrow$ `pgfs_data.id` $\rightarrow$ `pgfs_data_chunk.payload` (bytea データ本体)

-----

### 3\. 設定値ストア (`pgfs_settings`)

DDL: [docs/ddl/pgfs_settings.sql](../ddl/pgfs_settings.sql)

設定モデル整理でフラットな (scope, key) PK の単純な key-value ストアに変更しました。1 行 = 1 つの [`Pgfs.Core.Config.Field`](../../src/core/src/Config/Field.cs) の永続化値です。読み書きは [`Pgfs.Core.Config.ConfigStore`](../../src/core/src/Config/ConfigStore.cs) の `LoadAll` / `Save<T>` が担当します。

| カラム名         | データ型        | NULL       | DEFAULT             | INDEX | 説明                                                                              |
|:-------------|:------------|:-----------|---------------------|-------|:--------------------------------------------------------------------------------|
| `scope`      | `TEXT`      | `NOT NULL` |                     | PK    | スコープ名。例: `"mount"`, `"file_system"`。                                            |
| `key`        | `TEXT`      | `NOT NULL` |                     | PK    | スコープ内のキー名。例: `"fallback_uname"`, `"volume_label"`。                              |
| `value`      | `JSONB`     | `NOT NULL` | `'null'::JSONB`     |       | JSONB ネイティブ表現の値。文字列 → `"..."` / 数値 → `42` / bool → `true`/`false` / null は未設定。 |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |       | 作成日時                                                                            |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | 作成ユーザー名                                                                         |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP AT TIME ZONE 'UTC'` |       | 更新日時                                                                            |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | 更新ユーザー名                                                                         |

* `(scope, key)` で PK。UPSERT は `ON CONFLICT (scope, key) DO UPDATE` で行う。
* `Schema.Field<T>` の `SaveTo` フラグが `Db` を含むものだけが行として保存される。`SaveTo=File` のみのものは `pgfs.toml` 側へ書き出される。
* 旧 (`id` + `parent_id` の階層形) からの変更は破壊的なので、移行時は `mkfs --clean` で再構築する必要があります。
* Citus 環境では `citus_add_local_table_to_metadata` で metadata 登録されたローカルテーブル。distributed テーブルからの JOIN や Citus 系の調査 view で見えるが、shard は持たない (coordinator のみ)。

-----

### 4\. ロックトークン (`pgfs_lock`)

DDL: [docs/ddl/pgfs_lock.sql](../ddl/pgfs_lock.sql)


> **Citus では分散しない**: `citus_add_local_table_to_metadata` で coordinator に 1 コピーだけ置く citus local table にする。分散テーブルは `citus.shard_replication_factor > 1` のとき行ロック (`SELECT … FOR UPDATE`) を拒否するため、ロック機構を複製数から独立させるために非分散にしている。metadata 登録することで worker を入口にしたクエリもこの 1 行へルーティングされ、入口ノードが違っても排他が成立する。**行ロックはこのテーブルだけ**に集約し、`pgfs_inode` 等には `FOR UPDATE` を打たない。詳細は [support_for_citus.md §排他制御と replication factor](support_for_citus.md)。

cross-client 排他制御用のロックトークンテーブル。Citus Phase 2 で先行作成。Phase 3 で Api の `LockData` / `LockInode` / `LockInodes` へ組込み済みである。ただし全競合の解消を意味しない。**親削除と write-through create の競合は 修正済み**で、レビューに挙がった 15 件は全件クローズしている。**いま残っている制限は [CHANGELOG.md §既知の制限](../../CHANGELOG.md) が正。**

| カラム名      | データ型   | NULL       | INDEX | 説明                                                            |
|:----------|:-------|:-----------|-------|:--------------------------------------------------------------|
| `target_id` | `BIGINT` | `NOT NULL` | PK    | ロック対象 ID。data lock は `data_id` (正)、inode lock は `-inode_id` (負) で namespace を分ける。 |

監査列は無し (データではなくロック token なので)。`INSERT ... ON CONFLICT DO NOTHING` で行を作成し、`SELECT 1 FROM pgfs_lock WHERE target_id = @id FOR UPDATE` で行ロック → tx 終了で自動解放、という設計。行は累積する (DELETE しない) が 100 万件で ~100MB なので問題なし。詳細は [docs/support_for_citus.md](support_for_citus.md) Phase 3 を参照。

### 5\. 監査ログ (`pgfs_audit`)

DDL: [docs/ddl/pgfs_audit.sql](../ddl/pgfs_audit.sql) / 設計の正: [docs/audit-log.md](audit-log.md)

メタデータ変更操作 (create / delete / rename / chmod / chown / hardlink) を 1 操作 = 1 行で記録する。`occurred_at` をキーとする **月次 RANGE パーティション** テーブル (DEFAULT は作らず、INSERT 前にアプリが当月パーティションを `CREATE ... IF NOT EXISTS` で ensure)。`audit.enabled` (mkfs `--audit`) で opt-in。記録は操作と同一トランザクション (アトミック)。

| カラム名        | データ型      | NULL       | 説明                                                             |
|:------------|:----------|:-----------|:---------------------------------------------------------------|
| `id`          | `BIGSERIAL` | `NOT NULL` | 行 ID (PK の一部)                                                    |
| `occurred_at` | `TIMESTAMP` | `NOT NULL` | 操作時刻 (ゾーン無し)。**パーティションキー / Citus 分散キー**。PK は `(occurred_at, id)` の複合 |
| `op`          | `TEXT`      | `NOT NULL` | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink`  |
| `target_id`   | `BIGINT`    | `NULL`     | 対象 inode の id                                                   |
| `parent_id`   | `BIGINT`    | `NULL`     | 親ディレクトリ id (create / delete / rename)                          |
| `name`        | `TEXT`      | `NULL`     | エントリ名                                                          |
| `detail`      | `JSONB`     | `NOT NULL` | op 固有値 (新 mode 8 進 / 新 uname/gname / old→new parent 等)          |
| `caller_ip`   | `INET`      | `NULL`     | PG から見た接続元 IP (`inet_client_addr()`)                          |
| `caller_host` | `TEXT`      | `NULL`     | pgfs プロセスのホスト名                                                |
| `caller_uid`  | `BIGINT`    | `NULL`     | 呼び出し元 UID (Linux: `fuse_get_context`、Windows は数値 uid 無しで NULL) |
| `caller_uname`| `TEXT`      | `NULL`     | 呼び出し元ユーザー名                                                   |
| `caller_domain`| `TEXT`     | `NULL`     | ドメイン / ワークグループ (Windows のみ)                               |

INDEX は `id` 単独 / `op` / `target_id`。外部キー・トリガーは作らない (他テーブルと同様)。

### 6\. 実行中マウントのレジストリ (`pgfs_mounts`)

DDL: [docs/ddl/pgfs_mounts.sql](../ddl/pgfs_mounts.sql) / 設計の正: [docs/design/runtime-control-plane.md](runtime-control-plane.md) の Phase 2

各 mount.pgfs / assign.pgfs プロセスが起動時に 1 行 INSERT、定期 heartbeat で `heartbeat_at` を更新、正常終了で DELETE する **揮発レジストリ**。**例外: unmount の期限内に書き切れず未 flush を失った場合は DELETE せず残す** (B-2)。`stats` に `ended` / `endedAt` / `unflushedLoss` / `lost` を載せた**墓標**で、`pgfsctl status` と次回マウント時の警告がここを見る。自動では消えないので運用が削除する。**列は足していない** (DDL は mkfs 集約なので、列を増やすと既存 FS で再 mkfs が要るため `stats` JSONB に載せた)。status サブコマンド (運用) がクラスタ横断で稼働マウントを一覧するための土台。テーブルが無い既存 FS では mount が warning skip するだけで動作に影響しない (DDL は mkfs 集約)。Citus 時は `pgfs_settings` と同じく coordinator local + metadata 登録 (分散しない)。

| カラム名 | データ型 | NULL | 説明 |
|:--|:--|:--|:--|
| `mount_id` | `TEXT` | `NOT NULL` | プロセス一意 ID (PK)。ランダム hex |
| `host` | `TEXT` | `NOT NULL` | pgfs プロセスのホスト名 |
| `pid` | `BIGINT` | `NOT NULL` | プロセス ID |
| `mountpoint` | `TEXT` | `NOT NULL` | マウント先 |
| `mode` | `TEXT` | `NOT NULL` | `fuse` / `dokan` |
| `started_at` | `TIMESTAMP` | `NOT NULL` | 起動時刻 |
| `heartbeat_at` | `TIMESTAMP` | `NOT NULL` | 最終 heartbeat (既定 30s 間隔)。stale 判定に使う |
| `config` | `JSONB` | `NOT NULL` | 実効設定スナップショット |
| `stats` | `JSONB` | `NOT NULL` | キャッシュ統計等 |

外部キー・トリガーは作らない。
