# xattr の bytea 透過化 (並行配列 KVS)

`pgfs_inode` の拡張属性 (xattr) ストアを **JSONB + Base64** から **2 本の並行配列
(`xattr_names TEXT[]` + `xattr_values BYTEA[]`)** に置き換える設計。本書が実装の正。

英語版は [xattr-bytea.md](xattr-bytea.md) を参照してください。

ステータス: **実装完了**。linux_client 実機 e2e で NUL / 高位バイトの忠実往復 (`test_xattr_binary`) を含め検証済み。

## 動機

JSONB 版は `pgfs_inode.xattrs JSONB` に `{ 名前: Base64(値) }` で保持していた
([Api.EncodeXattrValue/DecodeXattrValue](../src/lib/src/Api/Api.cs))。

- xattr の値は **OS 上は任意バイト列** (NUL を含む。`security.selinux` の末尾 NUL、
  `security.capability` / `system.posix_acl_access` は生バイナリ)。JSON string にバイト列は
  そのまま乗らないので Base64 で包んでいた。
- Base64 復号は **ヘッダもキー判定も無く** `Convert.FromBase64String` を try して失敗時 UTF-8
  fallback という**ヒューリスティック**。通常運用は全値 Base64 なので曖昧さは出ないが、fallback 経路
  には「平文が偶然 valid Base64 だと誤復号する」潜在的曖昧さがある。
- Base64 は ~33% のサイズ膨張 + SQL から中身が読めない。

→ 値を **bytea で忠実に**持てば符号化往復も判定ヒューリスティックも消える。

## 採用案と不採用案

**採用: A' 並行配列** — `xattr_names TEXT[]` + `xattr_values BYTEA[]` を inode 行に同梱
(同じ index がペア)。理由:

- 新しい型を作らない (`CREATE TYPE` 不要 = Citus の型伝搬も不要)。
- Npgsql が `text[]↔string[]` / `bytea[]↔byte[][]` を**標準で**往復 (型登録の手間なし)。
- inode 行に同梱のまま → 「inode ロード時に全 xattr を一緒に取る」キャッシュ設計を壊さない
  (SELinux が高頻度で引く問題に効く)。量が少ないので index 不要、取得後にアプリ側で線形探索。

不採用:
- **HSTORE**: `text => text` 専用で bytea 値を持てない。
- **複合型配列 `xattr_entry[]` (name text, value bytea)**: 整合は綺麗だが `CREATE TYPE` + Citus 型伝搬
  + Npgsql 複合型登録の手間。A' で同じゴールに軽く着く。
- **子テーブル `pgfs_xattr`**: 最も RDB 的だが inode ロードに join/別クエリが必要・分散テーブル +1。
  この規模 (少量・index 不要) には過剰。
- **単一 bytea に手動 pack**: 値に NUL が入るので `\0` 区切りは破綻。length-prefix フレーミングが必須で
  自前パース地獄 + SQL 不可視。却下。

## スキーマ差分

`pgfs_inode` の

```sql
xattrs JSONB NOT NULL DEFAULT '{}'::JSONB
```

を

```sql
xattr_names  TEXT[]  NOT NULL DEFAULT '{}'::TEXT[],
xattr_values BYTEA[] NOT NULL DEFAULT '{}'::BYTEA[]
```

に置換する。更新箇所:
- [docs/ddl/pgfs_inode.sql](ddl/pgfs_inode.sql) (列定義 + INSERT サンプルの `'{}'::JSONB`)
- [Initializer.cs](../src/mkfs/src/Initializer.cs) の inode `ColumnInfo("xattrs", "JSONB", ...)`

**不変条件**: `cardinality(xattr_names) = cardinality(xattr_values)`、`xattr_names` 内は一意。
すべて Api の単一文 UPDATE が両配列を同時に保つことで担保 (アプリ read-modify-write はしない)。

Citus: 列の型が変わるだけ。inode の分散 (PK `(parent_id, id)`、分散キー `parent_id`) は不変、型伝搬も不要。

## Api の書き換え (JSONB 演算子 → 配列演算)

原子性は JSONB `||`/`-` と同じく **サブクエリ無しの単一 UPDATE 文**で保つ
(`array_position` と配列スライスを列に対して直接適用。`WHERE id = @id` のマルチシャード
UPDATE は既存 xattr 更新と同じ作法)。RHS は更新前の行の値で評価される点に依存する。

```sql
-- GetXAttr (キャッシュミス時): bytea or NULL
SELECT xattr_values[array_position(xattr_names, @name)]
FROM {inode} WHERE id = @id;

-- 存在判定 (createOnly/replaceOnly 用)
SELECT array_position(xattr_names, @name) IS NOT NULL FROM {inode} WHERE id = @id;

-- SetXAttr: 既存なら同 index の値を差し替え、新規なら末尾に追記 (原子)
UPDATE {inode} SET
  xattr_names = CASE WHEN array_position(xattr_names, @name) IS NULL
                     THEN array_append(xattr_names, @name) ELSE xattr_names END,
  xattr_values = CASE WHEN array_position(xattr_names, @name) IS NULL
                      THEN array_append(xattr_values, @value)
                      ELSE xattr_values[1:array_position(xattr_names, @name)-1]
                           || @value
                           || xattr_values[array_position(xattr_names, @name)+1:] END,
  updated_at = current_timestamp
WHERE id = @id;

-- RemoveXAttr: name の index を両配列から除去 (原子)
UPDATE {inode} SET
  xattr_names  = xattr_names[1:array_position(xattr_names, @name)-1]
               || xattr_names[array_position(xattr_names, @name)+1:],
  xattr_values = xattr_values[1:array_position(xattr_names, @name)-1]
               || xattr_values[array_position(xattr_names, @name)+1:],
  updated_at = current_timestamp
WHERE id = @id AND array_position(xattr_names, @name) IS NOT NULL;

-- ListXAttr (キャッシュミス時)
SELECT xattr_names FROM {inode} WHERE id = @id;
```

`@value` は bytea パラメータ (Npgsql が `byte[]` を bytea にバインド)。スライス `arr[1:0]` は空配列、
`arr[n+1:]` は末尾省略スライス (PostgreSQL 9.x+)。

廃止する private ヘルパ: `EncodeXattrValue` / `DecodeXattrValue` / `ParseXAttrFromJson` /
`ListXAttrNamesFromJson`。キャッシュ経路 (`GetXAttr`/`ListXAttr` が `inodeCache.Get` を使う分岐) は
in-memory の 2 配列を index 探索する形に書き換える。

## In-memory 表現 (Inode モデル / キャッシュ)

[Models/Inode.cs](../src/lib/src/Models/Inode.cs) の `string xattrs` / `string Xattrs` を

```csharp
public string[] xattr_names  { get; set; } = System.Array.Empty<string>();
public byte[][] xattr_values { get; set; } = System.Array.Empty<byte[]>();
```

に置換 (Dapper が列名で自動マップ)。名前→値の探索ヘルパを 1 つ用意 (例 `TryGetXattr(name, out byte[])`)。
更新箇所:
- [InodeCache.cs](../src/lib/src/Api/InodeCache.cs): 中央の `inodeSelectColumns` の `inode.xattrs` を
  `inode.xattr_names, inode.xattr_values` に。root inode 既定 (`xattrs = "{}"`) を空配列に。
- [Api.cs](../src/lib/src/Api/Api.cs) のクロスシャード rename INSERT: `xattrs = old.Xattrs` を
  `xattr_names = old.xattr_names` / `xattr_values = old.xattr_values` に (Npgsql が配列を直接バインド)。
  通常の `InsertInode` は新規なので空配列 (列 DEFAULT に任せる)。

> 注: 配列 2 列を SELECT に含めるとき、片方を取りこぼすと NUL/高位バイト往復が壊れる。
> ハードリンク作成 (`CreateHardLink`) を含め、両配列を必ず一緒に取ること。

## 移行

開発段階なので **in-place 移行はしない**。スキーマ変更後は `mkfs --clean` で作り直す前提
(実機 `pgfs` DB / docker テスト DB とも再構築)。

(任意・参考) 既存 JSONB から変換したい場合の一回限り SQL:

```sql
UPDATE {prefix}inode SET
  xattr_names  = ARRAY(SELECT jsonb_object_keys(xattrs)),
  xattr_values = ARRAY(SELECT decode(xattrs ->> k, 'base64')
                       FROM jsonb_object_keys(xattrs) AS k);
-- ※ 順序の一致に注意。実際にやるなら LATERAL で揃える。
```

## テスト

Linux e2e ([tests/linux/e2e.sh](../tests/linux/e2e.sh)) に xattr の往復ケースを追加:

- ASCII テキスト値 (`user.comment=hello`) の set/get/list/remove
- **NUL を含む値** (`printf 'a\0b'`) の忠実往復 (Base64 廃止後も壊れないこと)
- **生バイナリ** (high byte を含む) の往復
- 空値 (`setfattr -v ''`) の往復
- 多数キー (10+ 個) の list 順・remove 後の整合
- 既存 `system.posix_acl_access` が引き続き通ること (回帰)

Windows e2e は xattr 非対象 (POSIX 固有) なので追加なし。

## 関連

- [permission-interop.md](permission-interop.md) — POSIX ACL を `system.posix_acl_access` 経由で配送。
  本変更でその bytea 往復がより素直になる。
- [database.md](database.md) — `pgfs_inode` スキーマ要約 (xattr 列の記述を更新する)。
