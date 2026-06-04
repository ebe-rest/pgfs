# 設定スコープ再編 + plperlu ゲート + tablespace auto-mkdir (設計)

一連の設定モデル変更と、それに連なる Citus カスタム tablespace 対応の設計。**設定モデルは
[CLAUDE 指針] が「慎重に」と指定する領域**なので、本書で設計を固めてから実装する。設定項目の正は
[Schema.cs](../src/lib/src/Config/Schema.cs) / [settings-matrix.md](settings-matrix.md)。

英語版は [settings-and-plperlu.md](settings-and-plperlu.md) を参照してください。

ステータス: **実装・実機検証済み**。linux_client の throwaway PG で plperlu マトリクス /
`--deny-plperlu` / `--plperlu false` / require+deny エラー / 生成 toml の DB キー非出力 + 配布コメント /
`--citus`+カスタム tablespace + plperlu auto-mkdir (dir を postgres 所有 0700 で作成) / mount サニティを検証。
本番相当 PostgreSQL の mkfs でも `app.statfs` / `database.citus` / `app.plperlu` / `file_system` サイズ系が
`pgfs_settings` に保存され、生成 toml に配布コメントが出てサイズ系が出ないことを確認済み。

## 動機

1. **fs 全体で 1 値であるべき設定が toml にしか無い** → 2 クライアントが違う値で繋ぐと事故る。
   mkfs 後の `pgfs.toml` に `[file_system] cluster_size / default_chunk_size / max_file_size` が
   出ていたが、これらは「この FS をどう作ったか」の**識別情報**であってクライアント任意設定ではない。
   一方 `pgfs_settings` 側 (audit.enabled / file_system.version / volume_label / mount.fallback_* /
   statfs) は DB 権威で正しい。→ サイズ系も DB 権威にする。
2. **plperlu (untrusted) を使ってよいか**の上位ゲートが欲しい。`--statfs nominal` が事実上の
   「plperlu 使わない」だったが、tablespace auto-mkdir でも plperlu を使うため、判断を 1 か所に
   切り出す。
3. **「この FS は Citus か」を後から DB で確認したい** (toml の `database.citus` だけでなく)。

## 変更内容

### (A) 新スコープ `app` — アプリ挙動の設定

| キー | 型 | 既定 | SaveTo | 意味 |
|---|---|---|---|---|
| `app.plperlu` | bool | **true (allow)** | **Db** | plperlu (untrusted Perl) の使用を許可するか。mkfs が statfs 関数 / tablespace auto-mkdir に plperlu を使ってよいかの上位ゲート。DB に保存 (mkfs 再実行時の再利用 + 「この FS は plperlu 許可で作った」記録)。toml には書かない |

CLI:

| 形 | 結果 | 種別 |
|---|---|---|
| `--plperlu` | allow (true) | canonical。bare=true、後続に `true`/`false` を任意に取る |
| `--plperlu true` | allow | 〃 (値指定) |
| `--plperlu false` | deny | 〃 (値指定) |
| `--allow-plperlu` | allow | **bare 専用の固定 true 別名** (値は取らない) |
| `--deny-plperlu` | deny | **bare 専用の固定 false 別名** (値は取らない) |

実装: [Field](../src/lib/src/Config/Field.cs)/`BoolField` に **固定 false の別名集合 (negated CliOptions)** の概念を足す。
[ConfigLoader](../src/lib/src/Config/ConfigLoader.cs) の bool パースを拡張:
- canonical / 正の別名 (`--plperlu` / `--allow-plperlu`) にマッチ → true をセットし、**直後の引数が `true`/`false`
  リテラルならそれを値として消費**、そうでなければ bare=true のまま。
- negated 別名 (`--deny-plperlu`) にマッチ → false をセット (値は飲まない)。
- 既存の他 bool フラグ (`--foreground` 等) は正の別名のみなので挙動不変 (bare=true、リテラルが続かなければ値消費なし)。

### (B) statfs モードを `app.statfs` に置く (スコープ統一)

アプリ挙動なので `app` スコープへ (`app.plperlu` と同 scope)。値は `auto|require|nominal`、SaveTo=Db。
C# 参照は `Schema.Statfs.Mode`、CLI は `--statfs` / `--statfs-mode`。`pgfs_settings` 上は `app / statfs`。

### (C) `database.citus` を DB 権威化 (名前は据え置き)

「この FS は Citus 化されている」かの bool を DB に持つ。名前は `database.citus` のまま、SaveTo だけ変更:

- `Scope="database", Key="citus", **SaveTo=Db**`、CLI `--citus` 不変、AppliesTo=Mkfs。
- 値は「使ってるかどうかの bool」のみ (worker 一覧など詳細は持たない — 参照しないので)。

### (D) `file_system` サイズ系を DB 権威化

`SaveTo` を File → **Db** に変更 (scope `file_system` は据え置き。version/volume_label が既に Db なので並ぶ):

- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`

**解決優先順 (CLI>TOML>DB>Default) は変えない**。代わりに **SaveTo=Db の設定は mkfs 生成 toml に書かない**
(下記ルール)。生成 toml に出ないので通常クライアントの toml にサイズ系は無く、DB が効く。

- mkfs の [WriteTomlFile/AddField](../src/mkfs/src/Program.cs) は **SaveTo=File のみ**書き出すので、サイズ系を
  Db にすれば**自動的に生成 toml から消える** (追加コード不要。明示の `AddField` 呼び出しは no-op になるので除去)。
- [pgfs.toml.example](../pgfs.toml.example) と各 doc の toml サンプルからサイズ系・statfs・citus を**外す**
  (「toml で設定できる」と誤解させない)。
- ただし**手書きで toml に書いた場合は仕様どおり読んでその通り動く** (TOML>DB)。事故っても書いた人の責任。
  開発中にちょっと値を変えたいときに toml に書ける逃げ道は残す (仕様として明記)。

## plperlu × statfs の挙動マトリクス

statfs 関数 (`{prefix}statfs` / `fs_free`) の設計・3 段フォールバック・Citus 多 worker 集約は
[df-support.md](df-support.md) を正とする。本節は `app.plperlu` ゲートとの掛け合わせのみ示す。

| `--statfs` | `app.plperlu` = allow | `app.plperlu` = deny |
|---|---|---|
| `auto` | やってみて、ダメなら nominal 相当で続行 | nominal に倒す |
| `nominal` | ストアドを作らない | ストアドを作らない |
| `require` | やってみて、ダメならエラー | **エラー** (plperlu 不許可で require は矛盾) |

## tablespace auto-mkdir + Citus 制約撤廃

[Initializer.ValidateConfigCombinations](../src/mkfs/src/Initializer.cs) の `--citus` + `--tablespace≠pg_default`
禁止ガードを撤廃し、**CREATE DATABASE WITH TABLESPACE をデフォルト**にする (per-table `TABLESPACE` 句を廃止、
shard は worker DB 既定を継承)。`EnsureTablespaceAsync` を coordinator + 全 worker で実行。

LOCATION dir が無いとき、`app.plperlu` = allow なら **`CREATE TABLESPACE` が失敗する前に plperlu で mkdir**:

```sql
DO LANGUAGE plperlu $PL$
  use File::Path qw(make_path);   # 再帰作成 OK
  make_path($ENV{PGFS_TS_DIR});   # ※パスの渡し方は実装で確定 (DO は引数を取らないので
  chmod 0700, $ENV{PGFS_TS_DIR};  #   関数化 or リテラル埋め込み。postgres OS ユーザで作成 = 所有 postgres)
$PL$;
```

- plperlu は postgres OS ユーザで動くので、作成 dir は **postgres 所有 0700** = CREATE TABLESPACE の要求条件に一致
  (運用者が手で `sudo mkdir/chown/chmod` していたのが不要に)。
- Citus は `run_command_on_all_nodes` で全ノードに mkdir DO を流す (dir はノードローカル)。1 ノード Citus は
  1 台で成立。多ノードは親 dir が postgres から書ける前提 (書けなければ make_path も失敗 → 明示エラー)。
- `app.plperlu` = deny なら auto-mkdir せず、dir 不在は従来どおり `CREATE TABLESPACE` 失敗で明示エラー。
- df 相乗効果: `pgfs_fs_free` が `pg_tablespace_location` で実体 dir を引くので、`--statfs require` 時の `df` が
  **tablespace 実体の実空き**を返す (pg_default の場合は data_directory)。

## 移行

開発段階なので in-place 移行はしない。スキーマ/設定キー変更後は `mkfs --clean` で作り直す前提。
`pgfs_settings` の行は (scope,key) が変わる (statfs モードが `app/statfs`、`database.citus` は SaveTo=Db 化、
file_system サイズ系が新たに DB 行として増える)。

## 触る場所

- [Schema.cs](../src/lib/src/Config/Schema.cs): `app` nested class 新設 (`Plperlu` / `Statfs`)、`FileSystem`
  サイズ系の SaveTo 変更、`Database.Citus` の SaveTo 変更。
- [ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs): bool が `true|false` を任意に飲む拡張、別名処理。
- [RootConfig](../src/lib/src/Config/RootConfig.cs) + 各 `*Config` POCO: `AppConfig` 追加、`StatfsConfig`/
  `DatabaseConfig` の所属替え。Build* の配線。
- [Initializer.cs](../src/mkfs/src/Initializer.cs): plperlu ゲート参照、tablespace auto-mkdir、ガード撤廃、
  per-table TABLESPACE 句廃止 + CREATE DATABASE WITH TABLESPACE 化、PopulateSettingsRows に新キー。
- [settings-matrix.md](settings-matrix.md): 表を更新。
- ヘルプ ([HelpText](../src/lib/src/Config/HelpText.cs)) は Schema から自動生成なので追従。

## (F) mkfs 生成 toml に「配布用 mkfs パラメータ」をコメントで残す

mkfs が toml を書き出すとき ([WriteTomlFile](../src/mkfs/src/Program.cs))、**他クライアントへ配る用の mkfs コマンド/
パラメータをコメント**として先頭に残す。Toml.FromModel の出力テキストの前に `#` 行を prepend する。

- **除外する引数**: `--clean`（再実行で DB を壊す）/ `--super` 系 (`--super-user-connection` 等、super 資格情報は
  配布しない & クライアントは super 不要)。
- 含める例: `--connection ... --schema pgfs --prefix pgfs_ --citus --statfs require --volume-label pgfs ...`
- 接続文字列の Password は既に `[database].connection` に平文で出ているので情報漏洩は増えないが、コメント側は
  [DescribeProvided](../src/lib/src/Config/ConfigLoader.cs) と同様に **Password をマスク**して出す。

## 決定事項

1. **CLI**: 上表どおり (`--plperlu [true|false]` canonical / `--allow-plperlu` bare=allow / `--deny-plperlu`
   bare=deny)。`--deny-plperlu` は固定 false の negated 別名。
2. **file_system サイズ系の DB 権威**: 解決優先は変えず、**SaveTo=Db のものは生成 toml に書かない + doc/サンプルから外す**
   (手書きは仕様どおり読む = 自己責任)。
3. **`app.plperlu` の SaveTo = Db** (toml には書かない)。
