PostgreSQL ファイルシステムドライバ (PGFS) のデータベーススキーマ設計です。

> **DDL ファイル**: 各テーブルの CREATE 文は [docs/ddl/](ddl/README.ja.md) にテーブル単位で分割して置いてあります。参照用・手動セットアップ用・設計確認用に使えます。
>
> **Citus (水平分散) 対応**: 分散戦略・bytea 化・`pgfs_lock` テーブル等は [docs/support_for_citus.ja.md](support_for_citus.ja.md) を参照 (`mkfs --citus` で opt-in)。

-----

## 🏛️ PGFS データベーススキーマ設計

* 監査のため、作成日時・作成ユーザー名・更新日時・更新ユーザー名を各行に持ちます。
* 外部キーや外部制約、トリガーは障害発生時の手作業による管理を複雑にするため作成しません。ただしシーケンスは使用します。

### 1. ディレクトリエントリと inode 属性 (`pgfs_inode`)

DDL: [docs/ddl/pgfs_inode.sql](ddl/pgfs_inode.sql)

ファイルシステムの中核となるテーブル。すべてのファイル / ディレクトリ / シンボリックリンク / ハードリンクの (inode 的な) メタデータを保持します。

| カラム名      | データ型     | NULL       | DEFAULT             | INDEX   | 説明                                                       |
|:--------------|:------------|:-----------|---------------------|---------|:-----------------------------------------------------------|
| `id`          | `BIGSERIAL` | `NOT NULL` |                     | PK      | 一意のノード ID。全ファイル/ディレクトリの識別子。ルートは 0 で構築時に INSERT。 |
| `parent_id`   | `BIGINT`    | `NOT NULL` |                     | IX1 UK1 | 親ディレクトリの `id`。ルートは 0。                          |
| `name`        | `TEXT`      | `NOT NULL` |                     | IX2 UK1 | 親ディレクトリ内でのファイル名。UTF-8 で統一。              |
| `uname`       | `TEXT`      | `NOT NULL` |                     | IX3     | オーナーユーザー名。                                        |
| `gname`       | `TEXT`      | `NOT NULL` |                     | IX4     | オーナーグループ名。                                        |
| `st_mode`     | `INTEGER`   | `NOT NULL` |                     |         | ファイルの種類とパーミッション (chmod)。                    |
| `st_nlink`    | `INTEGER`   | `NOT NULL` | `1`                 |         | ハードリンク数。                                            |
| `st_size`     | `BIGINT`    | `NOT NULL` | `0`                 |         | ファイルサイズ (バイト)。ディレクトリやシンボリックリンクは **0 のまま** (理由は下記注)。 |
| `st_mtime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | 最終更新時刻 (マイクロ秒精度)。                             |
| `st_ctime`    | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | inode 最終変更時刻 (マイクロ秒精度)。                       |
| `link_target` | `TEXT`      | `NULL`     |                     |         | シンボリックリンクやジャンクションの場合のリンク先パス。    |
| `is_junction` | `BOOLEAN`   | `NOT NULL` | `FALSE`             |         | Windows ジャンクションの場合 `TRUE`。                       |
| `data_id`     | `BIGINT`    | `NULL`     |                     |         | ファイルデータ本体を参照する ID (`pgfs_data.id`)。ディレクトリは `NULL`。 |
| `xattr_names`  | `TEXT[]`  | `NOT NULL` | `{}`                |         | 拡張属性 (xattr) の名前配列。同じ index の `xattr_values` とペア。予約キー: `user.pgfs_acl` (正準 ACL ドキュメント JSON)、`user.win.attrs` (Windows 属性 JSON `{hidden,system,archive}`)。詳細は [permission-interop.ja.md](permission-interop.ja.md)。 |
| `xattr_values` | `BYTEA[]` | `NOT NULL` | `{}`                |         | xattr の値配列 (bytea で忠実保持、NUL 含む任意バイト列可)。`xattr_names` と同じ index でペア。設計は [xattr-bytea.ja.md](xattr-bytea.ja.md)。 |
| `created_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | 作成日時                                                   |
| `created_by`  | `TEXT`      | `NOT NULL` |                     |         | 作成ユーザー名                                             |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |         | 更新日時                                                   |
| `updated_by`  | `TEXT`      | `NOT NULL` |                     |         | 更新ユーザー名                                             |

* ルートディレクトリ ID: PostgreSQL の `BIGSERIAL` は自動採番開始が `1` のため `id` = 0 は通常挿入できませんが、初期データ投入時に明示的に 0 を指定して `INSERT` します。
* 最終アクセス時刻 `st_atime` は持ちません。`st_mtime` と同じ値を返します。
* **ディレクトリの `st_size` は 0 固定** (エントリ数や 4096 にしない)。理由: (1) `st_size` を「エントリ数」と解釈する標準ツールは無く (`du` は `st_blocks`、`ls -l` の数字が変わるだけの装飾)、機能上の必要が無い。(2) 読み取り時 COUNT は `getattr` 毎に DB 往復が増えて InodeCache のヒット効果を潰す。(3) 書き込み時に親の件数を維持する方式は全 mutating 操作に UPDATE を足す侵襲があり、装飾目的には見合わない。よって最も安全・低コストな 0 据え置きを採用。

-----

### 2. データ本体の参照管理 (`pgfs_data`)

DDL: [docs/ddl/pgfs_data.sql](ddl/pgfs_data.sql)

ハードリンクや bytea チャンク分割を管理するためのテーブルです。

| カラム名     | データ型     | NULL       | DEFAULT             | INDEX | 説明                                          |
|:-------------|:------------|:-----------|---------------------|-------|:----------------------------------------------|
| `id`         | `BIGSERIAL` | `NOT NULL` |                     | PK    | データ参照 ID。複数のファイル (ハードリンク) から参照される可能性あり。 |
| `chunk_size` | `INTEGER`   | `NOT NULL` |                     |       | ファイルが分割される bytea チャンクのサイズ (バイト)。 |
| `total_size` | `BIGINT`    | `NOT NULL` |                     |       | このデータ本体が持つファイルの論理的な合計サイズ。 |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | 作成日時                                      |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | 作成ユーザー名                                |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | 更新日時                                      |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | 更新ユーザー名                                |

#### チャンク管理 (`pgfs_data_chunk`)

DDL: [docs/ddl/pgfs_data_chunk.sql](ddl/pgfs_data_chunk.sql)

データ本体を bytea チャンクで格納します。1 チャンク = 1 bytea (デフォルト 1MB)。

| カラム名      | データ型     | NULL       | INDEX | 説明                                                       |
|:--------------|:------------|:-----------|-------|:-----------------------------------------------------------|
| `data_id`     | `BIGINT`    | `NOT NULL` | PK    | 親データ参照 ID。                                          |
| `chunk_index` | `INTEGER`   | `NOT NULL` | PK    | 0 から始まるチャンクの順番。                               |
| `payload`     | `BYTEA`     | `NOT NULL` |       | チャンクのバイナリデータ本体。長さは「これまで書き込まれたバイト数」(末端は半端、途中の穴は length(payload) が write offset まで届いていない状態で表現される)。1MB クラスは PG TOAST 化されるので部分読み (`substring`) はサーバ側 partial detoast が効く。 |
| `created_at`  | `TIMESTAMP` | `NOT NULL` |       | 作成日時                                                   |
| `created_by`  | `TEXT`      | `NOT NULL` |       | 作成ユーザー名                                             |
| `updated_at`  | `TIMESTAMP` | `NOT NULL` |       | 更新日時                                                   |
| `updated_by`  | `TEXT`      | `NOT NULL` |       | 更新ユーザー名                                             |

> データフロー: `pgfs_inode.data_id` $\rightarrow$ `pgfs_data.id` $\rightarrow$ `pgfs_data_chunk.payload` (bytea データ本体)

-----

### 3. 設定値ストア (`pgfs_settings`)

DDL: [docs/ddl/pgfs_settings.sql](ddl/pgfs_settings.sql)

フラット `(scope, key)` PK の単純な key-value ストア。1 行 = 1 つの [`Pgfs.Lib.Config.Field`](../src/lib/src/Config/Field.cs) の永続化値です。読み書きは [`Pgfs.Lib.Config.ConfigStore`](../src/lib/src/Config/ConfigStore.cs) の `LoadAll` / `Save<T>` が担当します。

| カラム名     | データ型     | NULL       | DEFAULT             | INDEX | 説明                                                       |
|:-------------|:------------|:-----------|---------------------|-------|:-----------------------------------------------------------|
| `scope`      | `TEXT`      | `NOT NULL` |                     | PK    | スコープ名。例: `"mount"`, `"file_system"`。               |
| `key`        | `TEXT`      | `NOT NULL` |                     | PK    | スコープ内のキー名。例: `"fallback_uname"`, `"volume_label"`。 |
| `value`      | `JSONB`     | `NOT NULL` | `'null'::JSONB`     |       | JSONB ネイティブ表現の値。文字列 → `"..."` / 数値 → `42` / bool → `true`/`false` / null は未設定。 |
| `created_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | 作成日時                                                   |
| `created_by` | `TEXT`      | `NOT NULL` |                     |       | 作成ユーザー名                                             |
| `updated_at` | `TIMESTAMP` | `NOT NULL` | `CURRENT_TIMESTAMP` |       | 更新日時                                                   |
| `updated_by` | `TEXT`      | `NOT NULL` |                     |       | 更新ユーザー名                                             |

* PK は `(scope, key)`。UPSERT は `ON CONFLICT (scope, key) DO UPDATE` で行う。
* `Schema.Field<T>` の `SaveTo` フラグが `Db` を含むものだけが行として保存される。`SaveTo=File` のみのものは `pgfs.toml` 側へ書き出される。
* Citus 環境では `citus_add_local_table_to_metadata` で metadata 登録されたローカルテーブル。distributed テーブルからの JOIN や Citus 系の調査 view で見えるが、shard は持たない (coordinator のみ)。

-----

### 4. ロックトークン (`pgfs_lock`)

DDL: [docs/ddl/pgfs_lock.sql](ddl/pgfs_lock.sql)

cross-client 排他制御用のロックトークンテーブル。

| カラム名    | データ型 | NULL       | INDEX | 説明                                                       |
|:------------|:---------|:-----------|-------|:-----------------------------------------------------------|
| `target_id` | `BIGINT` | `NOT NULL` | PK    | ロック対象 ID。data lock は `data_id` (正)、inode lock は `-inode_id` (負) で namespace 分け。 |

監査列は無し (データではなくロック token なので)。`INSERT ... ON CONFLICT DO NOTHING` で行を作成し、`SELECT 1 FROM pgfs_lock WHERE target_id = @id FOR UPDATE` で行ロック → tx 終了で自動解放、という設計。行は累積する (DELETE しない) が 100 万件で ~100MB なので問題なし。詳細は [docs/support_for_citus.ja.md](support_for_citus.ja.md) を参照。

### 5. 監査ログ (`pgfs_audit`)

DDL: [docs/ddl/pgfs_audit.sql](ddl/pgfs_audit.sql) / 設計の正: [docs/audit-log.ja.md](audit-log.ja.md)

メタデータ変更操作 (create / delete / rename / chmod / chown / hardlink) を 1 操作 = 1 行で記録する。`occurred_at` をキーとする **月次 RANGE パーティション** テーブル (DEFAULT は作らず、INSERT 前にアプリが当月パーティションを `CREATE ... IF NOT EXISTS` で ensure)。`audit.enabled` (mkfs `--audit`) で opt-in。記録は操作と同一トランザクション (アトミック)。

| カラム名        | データ型     | NULL       | 説明                                                       |
|:----------------|:------------|:-----------|:-----------------------------------------------------------|
| `id`            | `BIGSERIAL` | `NOT NULL` | 行 ID (PK の一部)                                          |
| `occurred_at`   | `TIMESTAMP` | `NOT NULL` | 操作時刻 (ゾーン無し)。**パーティションキー / Citus 分散キー**。PK は `(occurred_at, id)` の複合。 |
| `op`            | `TEXT`      | `NOT NULL` | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink` |
| `target_id`     | `BIGINT`    | `NULL`     | 対象 inode の id                                           |
| `parent_id`     | `BIGINT`    | `NULL`     | 親ディレクトリ id (create / delete / rename)               |
| `name`          | `TEXT`      | `NULL`     | エントリ名                                                 |
| `detail`        | `JSONB`     | `NOT NULL` | op 固有値 (新 mode 8 進 / 新 uname/gname / old→new parent 等) |
| `caller_ip`     | `INET`      | `NULL`     | PG から見た接続元 IP (`inet_client_addr()`)                |
| `caller_host`   | `TEXT`      | `NULL`     | pgfs プロセスのホスト名                                    |
| `caller_uid`    | `BIGINT`    | `NULL`     | 呼び出し元 UID (Linux: `fuse_get_context`、Windows は数値 uid 無しで NULL) |
| `caller_uname`  | `TEXT`      | `NULL`     | 呼び出し元ユーザー名                                       |
| `caller_domain` | `TEXT`      | `NULL`     | ドメイン / ワークグループ (Windows のみ)                   |

INDEX は `id` 単独 / `op` / `target_id`。外部キー・トリガーは作らない (他テーブルと同様)。
