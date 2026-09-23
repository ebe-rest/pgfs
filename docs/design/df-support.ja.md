# df 対応 (statfs の実空き容量レポート)

> **道順**: [docs/README.ja.md](../README.ja.md) › **本書**
>
> **この doc が正である範囲**: `df` / `statfs` が**バックエンドの実ディスク空き容量**を返す仕組みの正。
> plperlu で作る `pgfs_statfs()` / `fs_free()`、3 段フォールバック、`app.statfs` の 3 モードと公称
> フォールバック、Citus 多ノードの集約、実機検証の結果はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../Mkfs.ja.md](../Mkfs.ja.md) | `--statfs` / `--statfs-mode` の**CLI としての指定方法と既定値** |
> | [../Mount.ja.md](../Mount.ja.md) | StatFS が属する FUSE オペレーションの**利用者向け挙動** |
> | [settings-and-plperlu.ja.md](settings-and-plperlu.ja.md) | `app.plperlu` 上位ゲートと plperlu×statfs の挙動マトリクス (本書の関数が満たすべき前提条件) |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | `app.statfs` の既定値と reload ポリシー (§9 app.*) |
> | [database.ja.md](database.ja.md) | `{prefix}settings` (`app.statfs` の保存先) と `pg_database_size` 由来の現行 used |
> | [support_for_citus.ja.md](support_for_citus.ja.md) | `run_command_on_all_nodes` を使う分散セットアップ全般 |
> | [../tests.ja.md](../tests.ja.md) | テストの一覧と実行環境の要件 (本書の `statfs.sh` を含む) |

`df /mnt/pgfs` (FUSE statvfs / Windows のボリューム空き容量) が、バックエンド PostgreSQL の
**テーブルスペースの実ディスク空き容量**を返すようにするための設計メモ。Citus 多ノード時は
全ノードから集めて合算する。

ステータス: **実装済み・全経路 実機検証通過 (非 Citus フルチェーン + Citus 多 worker 集約)**。本書が実装の正。Citus 多 worker 集約の検証で **worker 集約分岐の型バグ (`sum(bigint)`→numeric を bigint 宣言に渡して落ちる) を捕捉・修正**した (下記)。

実装: [Schema.Statfs.Mode](../../src/core/src/Config/Schema.cs) (`--statfs`) / [Initializer.CreateStatfsFunctionsAsync](../../src/mkfs/src/Initializer.cs) (関数生成) / [Api.GetStatFs](../../src/core/src/Api/Api.cs) (呼び出し + 数秒キャッシュ + 公称フォールバック) / [mount StatFS](../../src/fuse/src/FileSystem.cs) + [assign GetDiskFreeSpace](../../src/dokan/src/FileSystem.cs) の配線。

### 実機プロトタイプ検証 (2026-06-03, `pgsql_server`)

scratch DB で `CREATE EXTENSION plperlu` → `df` シェルアウトする plperlu 関数を作り、`data_directory`
に対して実行して確認 (検証後 scratch DB を DROP、本番 `pgfs` DB は不変):

- ✅ `CREATE EXTENSION plperlu` / `CREATE FUNCTION ... LANGUAGE plperlu` 成功
- ✅ 関数は **postgres OS ユーザ権限**で動き、ssh ユーザでは権限拒否される `0700` の `data_directory` を読めた
- ✅ 戻り値 `total=2029890568192 / avail=1683165110272` が `sudo -u postgres df` / `df /database` と**完全一致**
- ⚠️ **`Filesys::Df` は未インストール** → 3 段フォールバックの **tier1 不在、tier2 (`df` コマンド) が実働**。
  `df` 経路で正値が取れるので CPAN 追加は不要。実装は tier2 を主経路として書いてよい。

### 実装後の実機検証 (2026-06-03, 非 Citus フルチェーン)

Windows から `mkfs --statfs require`(非 Citus, schema=pgfs) を `pgsql_server` の scratch DB `pgfs_dftest` に対して実行 →
**pgfs (非 superuser) ロール**で `SELECT total, avail FROM pgfs.pgfs_statfs()` を呼び、実 `df` と突き合わせ
(検証後 DROP DATABASE):

- ✅ `pgfs_statfs() = (2029890568192, 1683160715264)` が `df -B1 /database` と**完全一致**
- 🐛 **検証で捕捉して修正**: 関数を `SECURITY DEFINER` にしないと、`fs_free` 内の
  `current_setting('data_directory')` / `pg_tablespace_location` が **呼び出しロール (pgfs) 権限で実行されて
  `permission denied`** になる (これらは superuser / `pg_read_all_settings` 限定)。`{prefix}fs_free` と
  `{prefix}statfs` を `SECURITY DEFINER` (owner=postgres) にして解決。Citus の `run_command_on_workers` も
  superuser 必須なので `statfs` の DEFINER 化は必須。
### docker 実マウント越し検証 (2026-06-03, tests/docker ハーネス)

`tests/docker` (単一 PG = postgres:17, mount コンテナ) で **実 FUSE マウント越しの `df`** を確認:

- ✅ **auto-degrade**: postgres:17 は plperlu 非搭載 → `--statfs auto` が `plperlu is not available` を掴んで
  公称フォールバック、mkfs は成功。coord に statfs 系関数 0 件。`df /mnt/pgfs` = `1.0T`(= `max_file_size` 公称)。
- ✅ **本物経路 (live)**: coord に `postgresql-plperl-17` を入れて `--statfs require` で再 mkfs → remount → 
  `df /mnt/pgfs` が **`393G`(= docker ホスト実ディスク)** に変化。`pgfs_statfs()` の戻り値もコンテナ `df $PGDATA` と完全一致。
  → FUSE → `StatFS` → `Api.GetStatFs` → `pgfs_statfs()` の**フルチェーンが実マウントで実空きを返す**ことを確定。

### 残課題 (未検証 / 不足)

- **Citus 多 worker の `run_command_on_workers` 集約**: 未検証。`citusdata/citus:latest` は **plperl 非搭載**のため、
  検証には plperl 入りカスタム citus image (citus + `postgresql-plperl-N`) が要る。1 ノード Citus は `nworkers=0`
  分岐でローカル `fs_free` = 検証済み経路と同一。
- **自動テスト**: `tests/linux/e2e.sh` の `test_statfs` を total>0 / avail<=total の値検証まで強化済みだが、
  実測 vs 公称の区別や Citus 集約を回す**専用テスト (tests/citus/statfs.sh 相当)** は未整備。
- **Windows e2e**: `GetDiskFreeSpace` の専用テストは無い (空き容量 > 0 程度のアサート追加余地)。
- `auto`+plperlu 不在の degrade は docker で確認済み。

---

## 動機 / 現状

現状の [`FileSystem.StatFS`](../../src/fuse/src/FileSystem.cs) は次の擬似ディスク情報を返している:

- 容量 = [`Api.GetCapacityBytes`](../../src/core/src/Api/Api.cs) = `file_system.max_file_size` (公称容量 / nominal)
- 使用 = [`Api.GetTotalUsedBytes`](../../src/core/src/Api/Api.cs) = `pg_database_size(...)`
- 空き = 容量 − 使用

つまり「公称容量 − DB サイズ」であって、**裏の FS の実空き容量ではない**。`df` を本物にしたい。

### なぜ単純な SQL で取れないか

PostgreSQL には「テーブルスペースの裏 FS の空き容量」を返す SQL が無い。`pg_tablespace_size()` は
**使用量**しか返さない。空きを知るには裏ディレクトリを `statvfs(2)` する必要があり、これは
SQL からは届かない。よって **サーバサイドの関数**で `statvfs` する必要がある。

---

## 採用方針: plperlu の「素の CREATE FUNCTION」(拡張にしない)

検討した 3 案のうち **③ 拡張化せず素の `CREATE FUNCTION` を plperlu で作る**を採用。

| 案 | コンパイル | 配布 | mkfs との相性 |
|---|---|---|---|
| ① C 拡張 | 必要 (.so) | **PG メジャー × OS × arch ごとにバイナリ**を各ノードの pkglibdir へ。Citus 導入と同等の build/配布運用 (server ヘッダ必要) | ✗ 重い |
| ② PL 関数の拡張 | 不要 | `.control` + `.sql` のテキスト 2 枚を**各ノードの SHAREDIR に物理配置**。`CREATE EXTENSION` は Citus 自動伝播するがファイル配置は別途 | △ ファイル配置で mkfs に ssh/scp が要る = 大改造 |
| **③ 素の CREATE FUNCTION (plperlu)** | 不要 | **SQL テキストのみ**。Citus は `run_command_on_all_nodes($$ CREATE FUNCTION ... $$)` で全ノードに配布 | ✅ **mkfs は純 SQL のまま対応可** |

### ③ を選ぶ決め手

- **mkfs は現状「純 SQL ツール」** (Npgsql 接続のみ。ノード FS に触る手段を持たない)。拡張(①②)は各ノードの
  `SHAREDIR/extension/` にファイルを置く必要があり、mkfs に ssh 接続情報を引数で渡す**大改造**になる。
- ③ は関数本体が SQL テキストなので、**`run_command_on_all_nodes` で全ノードに SQL だけで配れる**。
  ファイルコピーも ssh も不要。mkfs は既存の `--super` 経路 (Citus セットアップ) に SQL を数文足すだけ。
- コンパイル不要 / arch 非依存。
- 唯一の譲歩: 拡張ではないので **後から追加したワーカーには自動伝播しない** → mkfs (または将来のマウント時
  ensure) が全ノードに再 `CREATE OR REPLACE` する。Citus 拡張セットアップと同じノリで吸収する。

### サーバ前提 (確認済み)

`pgsql_server` (PG 17.5 ソースビルド) の PL 状況:

- `plpgsql` インストール済み / `plperl` `plperlu` **available** (`plperl.so` 在中、未インストール) / `plpython3u` **無し** (`--with-python` 抜きビルド)
- → **Perl 一択**。`CREATE EXTENSION IF NOT EXISTS plperlu` で有効化 (superuser、`plperl.so` は PG ビルド時から
  各ノードに在るのでファイルコピー不要)。

---

## 関数設計 (シグネチャ固定)

スキーマ + テーブル接頭辞に合わせて配置する (例 `pgfs.pgfs_statfs`)。クライアントが呼ぶ入口
`{prefix}statfs()` の**シグネチャは不変**に保ち、中身だけモード/環境で差し替える (Windows 対応や
C 化への差し替え余地を残すため)。

| 関数 | 言語 | 役割 |
|---|---|---|
| `{prefix}statvfs(dir text) → (total bigint, avail bigint)` | **plperlu** | 純粋に `dir` を statvfs して総容量 / 利用可能バイトを返す (untrusted な部分はここだけ) |
| `{prefix}fs_free(tablespace text) → (total, avail)` | plpgsql | **そのノードの** tablespace ディレクトリを解決し `{prefix}statvfs` を呼ぶ |
| `{prefix}statfs() → (total, avail)` | plpgsql | **C# が呼ぶ入口**。非 Citus はローカル、Citus は全ノード集約 |

### ディレクトリ解決 (`fs_free` 内、ノードローカル)

- `pg_default` / 空 / `pg_global` → `current_setting('data_directory')` (実体は `base/`)
- 名前付き tablespace → `pg_tablespace_location(oid)`
- **各ノードが自分の `data_directory` を解決する**ため、tablespace 名を渡してノード側で解決する
  (coordinator のパスを配ってはいけない)。

### statvfs 本体の 3 段フォールバック (plperlu 内、実行時)

```perl
# {prefix}statvfs(dir) の中身イメージ
# 1) Filesys::Df があれば使う
my $r = eval { require Filesys::Df;
               my $d = Filesys::Df::df($_[0], 1);  # block size 1 = バイト単位
               return [$d->{blocks}, $d->{bavail}]; };
return $r if $r && @$r;
# 2) 無ければ df コマンドにシェルアウト (untrusted の強み)
my @o = `df -B1 --output=size,avail "$_[0]" 2>/dev/null`;
# ... 2 行目を parse して [size, avail] ...
# 3) df も無ければ undef → 呼び出し側が nominal にフォールバック
return undef;
```

### Citus 集約 (`statfs` 内)

- **非 Citus**: `SELECT * FROM {prefix}fs_free('<configured tablespace>')`
- **Citus**: `run_command_on_all_nodes($$ SELECT total||','||avail FROM {prefix}fs_free('<ts>') $$)` の
  結果を **`SUM(total)` / `SUM(avail)`** で合算。

集約セマンティクスの注意 (df に出す前提で割り切る):
- ノード横断の空きを **SUM** すると総和になる。単一の大ファイルは 1 ノードの空きを超えられない
  (チャンクは `data_id` で共置) が、`df` の総和表記は分散 FS の慣習 (Ceph/Gluster) に倣う → **注記する**。
- tablespace の FS は他 DB / 他テーブルとも共有 → 出るのは「pgfs 専用の空き」ではなく「ディスクの空き」。
  `df` の意味としてはむしろ妥当。
- `replication_factor > 1` だと空きが二重計上になる → 実効容量で割る配慮が要る (将来検討)。

---

## C# 側の配線

[`FileSystem.StatFS`](../../src/fuse/src/FileSystem.cs) (および assign 側の Windows ボリューム空き容量) は、
新しい `Api` メソッド経由で `SELECT total, avail FROM {prefix}statfs()` を呼ぶ。

**C# 側でもう一段フォールバック**: 関数が存在しない / エラー / `avail IS NULL` のときは、現行の
[`GetCapacityBytes`](../../src/core/src/Api/Api.cs) − [`GetTotalUsedBytes`](../../src/core/src/Api/Api.cs)
(= 公称容量 − DB サイズ) に戻す。これで「素の PG・古い接続先・df 無し」でも壊れない。

全体のフォールバック連鎖:

```
Filesys::Df  →  df コマンド  →  (関数が無い/失敗)  →  C# 側で公称容量 (nominal)
```

**キャッシュ**: `statfs` はツールが連打する。`run_command_on_all_nodes` を毎回叩くと重いので、
C# 側で結果を**数秒キャッシュ**する。

**権限**: untrusted PL の関数作成は superuser (mkfs `--super`)。実行は pgfs ユーザなので
`SECURITY DEFINER` + `GRANT EXECUTE` を付与。

---

## mkfs オプション `--statfs` (3 モード)

filesystem 全体の性質なので **DB に保存して全クライアントで共通化**する (`audit.enabled` と同じ `SaveTo.Db`)。

| 項目 | 値 |
|---|---|
| CLI | `--statfs <auto\|require\|nominal>` (エイリアス `--statfs-mode`) |
| scope.key | **`app.statfs`** (旧 `statfs.mode` → `pgfs.statfs` → `app.statfs`、C# は `Schema.Statfs.Mode`) |
| Field 型 | `StringField` (enum 型は無いので mkfs で値検証。`auto/require/nominal` 以外はエラー) |
| 既定 | `auto` |
| SaveTo | **`Db`** (全クライアント共通の FS プロパティ。mkfs が `pgfs_settings` に保存、mount/assign が起動時に読む) |
| AppliesTo | `Tool.Mkfs` (CLI は mkfs のみ。mount/assign は DB から読むだけ) |

### モード挙動

| モード | plperlu | 作る関数 | plperlu 不在時 |
|---|---|---|---|
| **`auto`** (既定) | 入れば使う | 入れば実測 (`statvfs`+集約)、無ければ nominal stub | nominal stub にフォールバック |
| **`require`** | 必須 | 実測関数 | **mkfs 失敗** (`CREATE EXTENSION plperlu` 不可でエラー終了) |
| **`nominal`** | 入れない | nominal stub のみ | — (そもそも plperlu を触らない) |

(ユーザー指定の must/never/default を pgfs 流に改名: must→`require` / never→`nominal` / default→`auto`。
値 `nominal` は既存コードの「公称容量 (nominal)」と語を揃えた。)

### nominal stub

`auto` で plperlu が無い場合、および `nominal` の場合は、入口 `{prefix}statfs()` を**プレーン SQL 関数**として作る:

```sql
CREATE OR REPLACE FUNCTION {schema}.{prefix}statfs()
RETURNS TABLE(total bigint, avail bigint) LANGUAGE sql AS $$
  SELECT
    (SELECT (value::text)::bigint FROM {schema}.{prefix}settings
       WHERE scope='file_system' AND key='max_file_size') AS total,
    -- avail = 公称容量 − DB サイズ (現行 C# と同じ計算をサーバ側で)
    GREATEST(0,
      (SELECT (value::text)::bigint FROM {schema}.{prefix}settings
         WHERE scope='file_system' AND key='max_file_size')
      - pg_database_size(current_database())) AS avail;
$$;
```

(max_file_size が無制限 `-1` の場合の扱いは C# の `GetCapacityBytes` と合わせる。stub と実測で
`{prefix}statfs()` のシグネチャは同一なので、C# は常に同じ呼び方で済む。)

---

## Windows の PostgreSQL 対応 (後付け可能)

この機能は **PG サーバ側**で動くので、効くのは *PG ホストの OS*。pgfs クライアント (Linux/Windows) は無関係。

- **Windows ホストの PG**: `os.statvfs` も `df` も無い → 上記 3 段フォールバックがそのまま効き、`auto` なら
  自動で **nominal に degrade**。つまり Windows PG でも今すぐ壊れず動く (公称容量表示)。本物の Windows 実測は
  後から `{prefix}statvfs` の**中身だけ**差し替えれば追加できる (plperlu から Win32 `GetDiskFreeSpaceEx`
  相当を呼ぶ等)。**シグネチャ `{prefix}statfs()` 不変なのでクライアントもスキーマも触らない**。
- **Citus on Windows**: Citus は実質 **Linux 専用** (配布は Linux パッケージ + Linux docker のみ、Windows ビルドの
  Citus 拡張は無い)。よって「Windows 多ノード集約」は考慮不要。実環境の `pgsql_server` は Linux なので分散ケースは
  常に Linux。Windows クライアント (Dokan) → Linux Citus サーバの構図では、df も**サーバ側実測値をそのまま受け取る**。
  - 注: 「Citus = Linux 専用」は要一次情報確認 (将来 Windows サーバで分散を本気で検討する場合のみ)。現用途では不要。

---

## 実装 TODO (チェックリスト)

1. ✅ `Schema` に `statfs.mode` (`StringField`, `SaveTo=Db`, `AppliesTo=Mkfs`, 既定 `auto`) + mkfs 値検証。
2. ✅ mkfs CLI `--statfs` / `--statfs-mode` 配線 ([Schema.Statfs.Mode](../../src/core/src/Config/Schema.cs))。
3. ✅ `Initializer.CreateStatfsFunctionsAsync`:
   - `auto`/`require`: `CREATE EXTENSION IF NOT EXISTS plperlu` (require で不可なら失敗、auto は nominal フォールバック)
     → `{prefix}statvfs` (plperlu) / `{prefix}fs_free` / `{prefix}statfs` を作成。**`fs_free`/`statfs` は `SECURITY DEFINER`**。
   - `nominal`: 既存の `{prefix}statfs`/`fs_free`/`statvfs` を **DROP** して公称容量に確実に戻す (require→nominal の再 mkfs を権威にする)。`auto`+plperlu 不在: 関数を作らず公称フォールバック。実装簡素化のため SQL stub は置かない。`Api.GetStatFs` は `app.statfs=nominal` のときサーバ問い合わせ自体をスキップする。
   - Citus 時は `run_command_on_all_nodes` で全ノードに statvfs/fs_free を配置、`statfs` 入口は coordinator のみ
     (worker 集約は `run_command_on_workers`、1 ノードはローカル)。
4. ✅ `Api.GetStatFs()` (→ `SELECT total, avail FROM {prefix}statfs()`) + 数秒キャッシュ + 関数不在/失敗時の公称フォールバック。
5. ✅ `FileSystem.StatFS` (mount) と assign `GetDiskFreeSpace` を `GetStatFs()` 経由に差し替え。
6. **検証済**: 非 Citus フルチェーン実機一致 / `require`→`nominal` で関数 DROP / `auto`+plperlu 不在の degrade /
   docker 実マウント越し `df` (公称 1T → require で実 393G)。残: Citus 多 worker 集約 (要 plperl 入り citus image) /
   専用自動テスト (tests/citus/statfs.sh) / Windows `GetDiskFreeSpace` テスト。
7. (将来) replication_factor>1 の二重計上補正 / Windows 実測 / マウント時の全ノード ensure / C 化。

---

## Citus 多 worker 集約の検証 (2026-06-03 完了)

`{prefix}statfs()` の Citus 分岐 (`run_command_on_workers` で shard 保持 worker の `fs_free` を合算) が
唯一の未検証経路だった。`citusdata/citus:latest` に plperl が無いのが障壁 (既製の citus+plperl イメージは
無い) なので、[tests/docker/Dockerfile.citus-plperl](../../tests/docker/Dockerfile.citus-plperl) で自前ビルドし、
[tests/citus/statfs.sh](../../tests/citus/statfs.sh) (coord + worker1) で検証した。**linux_client 実機 7/7 PASS**。

### 🐛 検証で捕捉した製品バグ — worker 集約分岐の型不一致

`{prefix}statfs()` の Citus worker 集約分岐 ([StatfsEntrySql](../../src/mkfs/src/Initializer.cs)) は

```sql
SELECT sum(split_part(r.result, ',', 1)::bigint), sum(...)::bigint FROM run_command_on_workers(...)
```

としていたが、PostgreSQL の **`sum(bigint)` は `numeric` を返す**。関数は `RETURNS TABLE(total bigint, avail bigint)`
宣言なので、実行時に `structure of query does not match function result type` (`numeric` vs `bigint`) で落ちる。
**非 Citus / 単ノード Citus (nworkers=0) はローカル `fs_free` 直返しでこの分岐を通らない**ため、多 worker
Citus を立てて初めて顕在化した。修正は `sum(...)::bigint` と宣言型へ明示キャストするだけ。

### 検証手順 (再現用)

```bash
docker build -t pgfs-citus-plperl -f tests/docker/Dockerfile.citus-plperl tests/docker/   # statfs.sh が未指定なら自動ビルド
MKFS_BIN=<publish>/mkfs.pgfs bash tests/citus/statfs.sh                                    # coord:15552 + worker1:15553
```

### アサーション (statfs.sh、7 項目すべて PASS)

| # | 内容 | 結果 |
|---|---|---|
| R1 | `--statfs require` で `pgfs_statfs()` が coordinator に存在 | ✅ |
| R2 | `fs_free`/`statvfs` が worker1 にも配布済み (`run_command_on_all_nodes`) | ✅ |
| R3 | coordinator の `pgfs_statfs()` が `total>0 / 0<avail<=total` を返す | ✅ (393GiB/216GiB) |
| R4 | 戻り値が worker1 の `fs_free()` と一致 (集約 = worker 1 台分・coord 二重計上なし) | ✅ (total 厳密一致) |
| R5 | **機構の証明**: `citus.enable_ddl_propagation=off` で coord ローカルの `fs_free` だけ DROP しても (worker には残存) `pgfs_statfs()` が値を返す = `run_command_on_workers` 経由 | ✅ |
| A1 | `--statfs auto` (plperl 有効) も実測値を返す | ✅ |
| N1 | `--statfs nominal` で `statfs`/`fs_free`/`statvfs` が coord/worker 双方から DROP 済み (公称フォールバック) | ✅ |

備考: worker1 は `--network host` で coord と同一物理ディスクなので戻り値 ≈ ホスト `df`。worker を 2 台に
すると同一ディスクを二重計上する (テスト構成の限界。集約 *機構* の検証は worker1 台で十分)。R5 の DROP は
`citus.enable_ddl_propagation=off` で囲まないと Citus が worker にも DROP を伝搬してしまい (= worker の
`fs_free` まで消え `sum()` が NULL→空)、機構の証明にならない点に注意。

### Windows df テスト

[tests/windows/e2e.ps1](../../tests/windows/e2e.ps1) `test_disk_free_space`: `GetDiskFreeSpace` (DriveInfo 経由) が
`total>0` / `0<=avail<=total` を返すことをアサート。**実機 Dokan マウント越しで検証済み (2026-06-03、Windows e2e 27/27 PASS)**。
pgsql_server の `pgfs` DB は statfs 関数未導入なので公称フォールバック経路 (`P: total=1TiB`)。実 statfs 経路は上記
docker/Citus で実証済み。
