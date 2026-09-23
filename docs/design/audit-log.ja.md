# 監査ログ (audit log)

> **道順**: [docs/README.md](../README.md) › **本書**
>
> **この doc が正である範囲**: 監査ログ機能の設計と実装ステータス。`{prefix}audit` のスキーマと `detail` の中身、
> 月次 RANGE パーティションを「当月が無ければ op 前に ensure する」手順、記録する 6 操作とフック位置、
> 呼び出し元コンテキスト (uid / uname / domain / host / ip) の取り方、`audit.enabled` の on/off、
> **メタデータ write-back を有効にすると監査は耐久ではない**という契約はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../ddl/README.md](../ddl/README.md) | `{prefix}audit` の DDL 本体 ([pgfs_audit.sql](../ddl/pgfs_audit.sql)) |
> | [database.md](database.md) | スキーマ全体の設計とタイムスタンプ規約。本書は audit テーブルの分だけを持つ |
> | [metadata-write-back.md](metadata-write-back.md) | メタデータ遅延書き (1e) 本体の設計。監査行を flush tx へ畳む側 |
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | 1e のレビュー記録 (`writeback_loss` や取り消しペアの同期化に至った経緯) |
> | [windows-parity.md](windows-parity.md) | Windows (Dokan) 側の as-built。`GetRequestor` の取得位置と残差分 |
> | [settings-matrix.md](settings-matrix.md) | `audit.enabled` の CLI / 保存先 / reload ポリシーの一覧 |
> | [../Mkfs.md](../Mkfs.md) | 利用者向けの `mkfs --audit` 仕様と既定値 |
> | [support_for_citus.md](support_for_citus.md) | audit を含む Citus 分散配置そのものの方針 |
> | [../tests.md](../tests.md) | テストのハブ。件数・実行方法・環境要件はそちら |

chmod / chown / 削除 / リネーム / 作成 / ハードリンクといった **メタデータ変更操作** を、専用テーブル
`{prefix}audit` に 1 操作 = 1 行で記録する機能 (「監査ログ」要件に対応)。

当初の構想 ([docs/next.md](../next.md) の旧 §監査ログ 実装プラン) は「`Logger.Information` にテキスト 1 行
出すだけ」だったが、設計協議の結果 **DB の日付パーティションテーブルに構造化して残す** 方針に変更した。
本ドキュメントが現行の正。

## 設計判断 (協議で確定したもの)

| # | 論点 | 決定 |
|---|---|---|
| 1 | 「誰が」 | 呼び出し元 **IP / ホスト名 / UID / ユーザー名 / ドメイン (Windows のみ)** を記録 |
| 2 | 出力先 | ログファイルではなく **DB テーブル** `{prefix}audit` |
| 3 | パーティション | `occurred_at` で **月次 RANGE 分割** + アプリ自動作成 + `DEFAULT` 退避 |
| 4 | 書き込みタイミング | **操作と同一トランザクション (アトミック)** |
| 5 | on/off | DB 保存設定 `audit.enabled`。**mkfs (`--audit`) で初期化**、`pgfsctl config set audit.enabled true/false` で Live 変更 |
| 6 | prefix | 不要 (構造化 DB 行なので grep 用トークンは持たない) |
| 7 | Citus 配置 | `occurred_at` で **分散** (検索時の日時指定を必須とする前提) |
| 8 | caller IP の意味 | **自ホスト名** + **PG から見た接続元 IP** (`inet_client_addr()`) |

## スキーマ `{prefix}audit`

`occurred_at` をパーティションキーとする RANGE パーティションテーブル。

| カラム | 型 | 内容 |
|---|---|---|
| `id` | BIGSERIAL | 行 ID (PK の一部) |
| `occurred_at` | TIMESTAMP NOT NULL DEFAULT current_timestamp | 操作時刻 (ゾーン無し、他テーブルと統一)。**パーティションキー / Citus 分散キー** |
| `op` | TEXT NOT NULL | `create` / `delete` / `rename` / `chmod` / `chown` / `hardlink` |
| `target_id` | BIGINT | 対象 inode の id |
| `parent_id` | BIGINT NULL | 親ディレクトリ id (create / delete / rename) |
| `name` | TEXT NULL | エントリ名 |
| `detail` | JSONB NOT NULL DEFAULT '{}' | op 固有値 (新 mode 8 進, 新 uname/gname, old→new parent 等) |
| `caller_ip` | INET NULL | PG から見た接続元 IP (`inet_client_addr()`、ローカル接続なら NULL) |
| `caller_host` | TEXT NULL | pgfs プロセスが動くホスト名 (プロセス起動時に 1 回解決) |
| `caller_uid` | BIGINT NULL | 呼び出し元 UID (FUSE per-call context / Dokan) |
| `caller_uname` | TEXT NULL | 呼び出し元ユーザー名 |
| `caller_domain` | TEXT NULL | ドメイン / ワークグループ (Windows のみ、Linux は NULL) |

- **PK は `(occurred_at, id)` の複合**。パーティションキー `occurred_at` を PK に含める必要がある (Postgres の
  partitioned table 制約) のに加え、Citus 分散時も分散キーを unique 制約に含める必要があるため
  (`pgfs_inode` の `(parent_id, id)` と同じ事情)。`id` 単独 INDEX を別途持つ。
- 固定列は「いつ・誰が・何を・どれに」を検索する軸。op ごとに変わる値は `detail` JSONB に寄せる
  (1 行 JSONB のシンプル設計)。
- **監査は `occurred_at` で読むこと (`id` 順は操作順ではない)**。`occurred_at` は**操作時刻**だが `id` は
  **INSERT 時に採番**されるので、書き込みが遅延する経路では両者の順序が乖離する。具体的には
  メタデータ write-back ([metadata-write-back.md §1e](metadata-write-back.md)) が有効なとき、
  監査行は操作時にキャプチャされて **flush tx でまとめて INSERT** される (flush 失敗で latch されれば
  さらに遅れる) ため、後に起きた操作の `id` が先に採番され得る。ORDER BY / 範囲検索は常に
  `occurred_at` を使い、`id` は同一 `occurred_at` 内の tie-break にとどめる。

### `detail` の中身 (op 別)

| op | detail の例 |
|---|---|
| `create` | `{"mode":"40755","kind":"dir","uname":"alice","gname":"staff"}` |
| `delete` | `{}` (固定列の target_id / parent_id / name で十分)。**メタデータ write-back (1e) で「別の inode に置き換えられた」削除だけ** `{"reason":"write_back_metadata_rename_replace","replaced_by":99}` (rename-over-existing の置換) / `{"reason":"write_back_metadata_name_conflict","replaced_by":99}` (cross-client の同名衝突を last-flush-wins で解決) が付く |
| `rename` | `{"old_parent":12,"new_parent":34,"new_name":"b.txt"}` |
| `chmod` | `{"mode":"100644"}` (8 進文字列) |
| `chown` | `{"uname":"bob","gname":"dev"}` |
| `hardlink` | `{"source_id":7,"new_parent":34,"new_name":"link"}` |

## パーティション戦略 (月次 + アプリが当月を lazy ensure / DEFAULT 無し)

- 親テーブルは `PARTITION BY RANGE (occurred_at)`。mkfs は **親テーブルだけ** 作り、パーティションは作らない。
- 月次パーティション (`{prefix}audit_YYYY_MM`、範囲 `[当月1日, 翌月1日)`) は **アプリが INSERT 前に
  `CREATE TABLE IF NOT EXISTS ... PARTITION OF ...` で確保** する。プロセス内のメモリ集合 (`yyyy_MM`) にキャッシュ
  し、その月の初回だけ DDL を発行する。「現在日時は 1 つしか無い」ので起動時の全パーティション先読みは不要 —
  当月 (と稼働中に跨いだ月) だけを lazy に作れば足りる。
- **DEFAULT パーティションは作らない**。理由: DEFAULT に行が溜まると、その範囲をカバーする月パーティションを
  後から `CREATE` しようとして PG が `updated partition constraint for default partition ... would be violated by
  some row` でエラーにする (= DEFAULT が罠になる)。INSERT 前に必ず当月を ensure するので DEFAULT は不要。
- ensure に失敗すると月パーティションが無いまま INSERT が `no partition found` で失敗し、**操作 tx ごと
  ロールバック** する (= 監査を残せないなら操作も成立させない、というアトミック方針)。

### Citus との両立 (注意点 — out-of-band ensure)

`occurred_at` 分散 + 同一 tx 書き込み + アプリ自動パーティション作成の 3 つは素直に組むと衝突する:
Citus は **分散書き込みトランザクションの途中での DDL (パーティション CREATE) を拒否** することがある。

回避策: **パーティション確保 (`EnsureAuditPartition`) は操作 tx とは別コネクション (autocommit) で実行** し、
メモリキャッシュで月初回だけに絞る。監査行の INSERT 自体は操作 tx 内で行う。これにより同一 tx 内では DDL が
走らない。

- Citus 時は親テーブルを `create_distributed_table('{prefix}audit', 'occurred_at')` で分散。月次パーティションは
  親が分散済みなら Citus が自動で分散する。
- 残リスク: 操作 tx (inode は `parent_id` 分散) に audit (`occurred_at` 分散) の INSERT を混ぜると **複数の
  分散テーブルに跨る分散トランザクション (2PC)** になる。機能的には Citus がサポートするが、多ノード e2e で
  実際にコミットできることを検証する (下記テスト参照)。

### ⚠ ensure は「その tx で監査行を 1 行も INSERT していない」段階で呼ぶこと (検出されないデッドロック)

上の out-of-band ensure には**踏むと復旧不能になる制約**がある。`pg_locks` 実測 (2026-08-10 / PG 17.5):

| 文 | 親テーブル `{prefix}audit` に取るロック |
|---|---|
| 監査行の `INSERT` | `RowExclusiveLock` (パーティション側にも同じ) |
| `CREATE TABLE ... PARTITION OF` | `RowExclusiveLock` と**衝突する**ロック (別セッションの INSERT tx が開いている間ブロックする) |

つまり **同一プロセスの操作 tx が親に `RowExclusiveLock` を持った状態で `EnsureAuditPartition` を呼ぶと、
別コネクションの DDL が自分の tx を待ち、自分の tx はその DDL の完了を待つ**。待ちグラフが 2 セッションに
分断されるので **PG のデッドロック検出器は閉路を見つけられず、既定 `lock_timeout = 0` では無限待ち**になる。

- 現行の write-through 経路は「1 tx = 監査行 1 行」かつ `WriteAudit` が **INSERT の前**に ensure するので
  親をまだ掴んでおらず、偶然安全 (= この順序は仕様として守ること)。
- **1 tx に複数の月の監査行が混ざる形** (メタデータ write-back の pending が error latch で月を跨いだ場合など)
  では 2 件目の ensure が親を掴んだ後に走るので**確実に踏む**。よって
  [metadata-write-back.md §1e](metadata-write-back.md) の flush tx は
  **書く監査行の月集合を tx を開く前に一括 ensure** する。
- 防御として `EnsureAuditPartition` の DDL には **`SET lock_timeout = '5s'`** を張ってある
  (規約が破れてもハングせず例外で落ちる)。FUSE のロック (NSGate 等) を握ったままハングすると
  `kill -9` 以外で復旧できないため。

## 呼び出し元コンテキストの配線

操作ごとに変わる「誰が」は OS 層 (Mount / Assign) でしか取れないため、`Pgfs.Core.Models.AuditContext` を
**ambient (`AsyncLocal`)** で Api に渡す。各 FUSE / Dokan コールバックの先頭で `AuditContext.Current` を
セットし、Api はフック時にそれを読む。

`AuditContext` には per-call で変わる `Uid` / `Uname` / `Domain` のみを載せる。`caller_host` はプロセス定数なので
Api がコンストラクタで 1 回 `Dns.GetHostName()` 解決し、`caller_ip` は INSERT 内で `inet_client_addr()` を
サーバ側評価する (どちらも `AuditContext` には入れない)。

| 値 | Linux (Mount) | Windows (Assign) |
|---|---|---|
| caller_uid | `fuse_get_context()->uid` (`Pgfs.Fuse` binding の `Fuse.TryGetCallerContext`) | (NULL — Windows に数値 uid 概念が無い) |
| caller_uname | uid を `UserResolver.UnameOf` で解決 | Dokan `info.GetRequestor()` の `DOMAIN\user` の user 部 |
| caller_domain | (NULL — Linux に domain/workgroup 概念が無い) | 同 `DOMAIN\user` の DOMAIN 部 |
| caller_host | Api がプロセス起動時に `Dns.GetHostName()` | 同左 |
| caller_ip | INSERT 内で `inet_client_addr()` (サーバ側評価) | 同左 |

- **`Pgfs.Fuse` binding の拡張**: 内製 libfuse バインディング ([src/fuse/](../../src/fuse/)、v0.2.0 で旧 Tmds.Fuse fork から移植) に
  `fuse_get_context()` の P/Invoke を持たせ、公開 API `Fuse.TryGetCallerContext(out uid, out gid, out pid)` で
  読めるようにしてある。解決できない libfuse でもマウントを壊さないよう、シンボル解決は
  null 許容 (`TryCreateDelegate`) にしてある。
- caller_host は「どのマシンが操作したか」、caller_ip は「PG から見た接続元」。複数クライアントが同一の
  DB-FS を叩く構成で「どのクライアントの誰が」を後から追える。
- OS 層は各 mutating コールバックの先頭で `AuditContext.Current` を立てる
  (audit 無効時や取得失敗時は null = caller_* が NULL になるだけ)。
- **⚠ Windows の取得位置 (修正)**: Dokan の `info.GetRequestor()` は **`CreateFile` コールバックの中でしか成功しない**
  (要求スレッドの偽装トークンを複製するため)。それ以外のコールバックから呼ぶと
  `Invalid token for impersonation - it cannot be duplicated` で必ず失敗する。
  修正前は `Cleanup` / `MoveFile` / `SetFileAttributes` / `SetFileSecurity` の先頭で取り直していたため全件失敗しており
  (Windows e2e 1 周で **3400 件**)、**v0.2.0 より前の assign.pgfs から行われた操作の監査行は
  `caller_uname` / `caller_domain` が NULL** になっている (Linux 側は `fuse_get_context` が毎コールバックで取れるので影響なし)。
  現在は `CreateFile` で確定した主体をハンドル (`FileSystem.OpenFile`) に保持し、以降の操作はそれを立て直す。
  as-built は [windows-parity.md §実装ステータス](windows-parity.md)。

## フック箇所 ([src/core/src/Api/Api.cs](../../src/core/src/Api/Api.cs))

成功 (`rows > 0` / 戻り値非 null) を確認した後、**同一 tx 内** で `WriteAudit` を呼ぶ。

| 操作 | メソッド | op | 記録 |
|---|---|---|---|
| create (dir/file/symlink) | `InsertInode` (共通経路) | `create` | target=新 id, parent, name, detail(mode/kind/uname/gname) |
| delete | `DeleteInode` | `delete` | target=inode.Id, parent, name |
| rename | `Rename` | `rename` | target=id, detail(old→new parent, new_name) |
| chmod | `UpdateMode` | `chmod` | target=id, detail(mode 8 進) |
| chown | `UpdateOwner` | `chown` | target=id, detail(uname/gname) |
| hardlink | `CreateHardLink` | `hardlink` | target=新 id, detail(source_id, new_parent, new_name) |

- **除外**: `WriteData` / `UpdateSize` / `UpdateTimestamps` (データ I/O・付随更新で騒がしい)。
- `InsertInode` は元々 tx を張っていなかった (`Pg.Query` 直叩き) ため、監査を同一 tx にするべく
  **`conn` + `tx` 化** した (conflict 時は rollback)。
- `audit.enabled` が false のときは `WriteAudit` は即 return (フック自体は常に呼ぶが no-op)。

## on/off 設定 `audit.enabled`

- `Schema.Audit.Enabled` (`scope=audit`, `key=enabled`, `BoolField`, `SaveTo=Db`, CLI `--audit`, 既定 false)。
- mkfs が `PopulateSettingsRows` で `pgfs_settings` に保存。mount / assign は起動時に `ConfigLoader` の
  DB フェーズ経由で読み、Api が `config.Audit.Enabled` でフックを有効化する。
- `pgfsctl config set audit.enabled true/false` は DB 永続化 + NOTIFY による Live 反映を行う。各 mount の適用完了 ACK はない。
- **`op = writeback_loss`** (追加): unmount の期限内に書き切れず**失われた**未 flush の記録。
  1 回の unmount = 1 行で、対象はマウントそのものなので `target_id` は null・`name` は mountpoint。
  `detail` に件数・内訳 (最大 32 件)・`timeout_ms` を入れる。`audit.enabled` が off なら書かれないので、
  そのときの手掛かりは `{prefix}mounts` の墓標だけになる (詳細は
  [metadata-write-back-reviews.md §B-2 の as-built](metadata-write-back-reviews.md))。
- **メタデータ write-back を有効にすると監査は耐久ではない** (明記)。`mount.write_back_metadata = true`
  のとき、pending inode に対する操作の監査行は **その inode の実体化 tx で初めて DB に入る**。
  つまり **`fsync` / `fsyncdir` していない操作は、クラッシュすると操作そのものと一緒に監査行も消える**
  (「操作は残ったが監査だけ消えた」ではなく「どちらも無かったことになる」)。unmount 時に書き切れなかったぶんは
  `op = writeback_loss` の行が残るが、**これは「失われた」ことの記録であって個々の操作の記録ではない**
  (件数とパスのサマリしか持たない)。**監査を証跡として使う運用では `write_back_metadata` を off のままにすること。**
- **メタデータ write-back の制約**: pending の監査行は操作時にメモリへ捕捉し、実体化 tx で保存する。**取り消し (interval 内に作って消した pending) の create/delete ペアだけは 同期で書く** (B-3。それ以前は orphan キュー待ちで、`create → read → unlink` が痕跡ゼロで成立していた)。**それ以外の orphan キュー分は依然 flush 待ちであり、クラッシュ時には失われ得る**。live off 中は orphan flush が return して残留し得る。操作成功が監査の即時永続化を意味するとは限らない。**この制約は利用者向けの言い方で [CHANGELOG.md §既知の制限](../../CHANGELOG.md) にも載せてある。**

## 実装状況 (実装済・**実機で完全クローズ**)

3 フェーズすべて実装済み:

1. **設定 / スキーマ** ✅: [Schema.Audit.Enabled](../../src/core/src/Config/Schema.cs) + [AuditConfig](../../src/core/src/Config/AuditConfig.cs) + `ConfigLoader` 配線、
   [docs/ddl/pgfs_audit.sql](../ddl/pgfs_audit.sql)、mkfs [CreateAuditTableAsync](../../src/mkfs/src/Initializer.cs) + 設定行投入。
2. **Api フック** ✅: [AuditContext](../../src/core/src/Models/AuditContext.cs) ambient、[Api.WriteAudit / EnsureAuditPartition](../../src/core/src/Api/Api.cs)、
   6 メソッドにフック、`InsertInode` の tx 化。
3. **呼び出し元プラミング** ✅: Pgfs.Fuse binding に `fuse_get_context` ([LibFuse.cs](../../src/fuse/src/LibFuse.cs) / `Fuse.TryGetCallerContext`)、
   [mount/FileSystem.cs](../../src/fuse/src/FileSystem.cs) の 9 コールバック・[assign/FileSystem.cs](../../src/dokan/src/FileSystem.cs) の
   CreateFile/Cleanup/SetFileAttributes/MoveFile/SetFileSecurity。
   **Windows 側は 取得位置を変更**: `CreateFile` で `CaptureRequestor` → ハンドル保持 → 他コールバックは
   `ApplyAuditContext` で立て直す (理由は上の ⚠ 注)。

## テスト

### 完了

- **既存回帰** ✅: Linux e2e 34/34・Windows e2e 24/24・Citus race multinode 4/4・Citus mkfs matrix 18/18
  が緑のまま (監査有効構成で検証)。
- **Citus 同一 tx commit** ✅: `Initializer.CreateAuditTableAsync` が `--audit` 無関係に常時テーブル
  作成し `--citus` 時 `occurred_at` 分散 (Citus 分散テーブル数 = 5: inode/data/data_chunk/lock/audit)。
  多ノード Citus 上で監査有効のまま操作が同一 tx でコミットできることを `race_multinode.sh` (4/4) で実証。
  2PC リスクは顕在化せず。

### 監査専用テスト — [tests/citus/audit.sh](../../tests/citus/audit.sh) ✅ 12/12 PASS (2026-05-31)

既存スイートの件数 (34/24) は監査追加でも不変 = 監査固有の検証は専用スクリプト
[tests/citus/audit.sh](../../tests/citus/audit.sh) に分けた。docker 2 ノード Citus + mount.pgfs × 1 を立て、
次を検証する (詳細は [tests/citus/README.md](../../tests/citus/README.md) §audit.sh)。**linux_client で 12/12 PASS 済み**
(caller_uid / uname / host / ip と create detail.mode=100664 / kind=file を確認):

- **A**: 各 op (create/delete/rename/chmod/chown/hardlink) で `pgfs_audit` に期待行が入る。create 行の name / detail.kind を精査。
- **B**: caller_uid / caller_uname が呼び出し元の実値 (fuse_get_context) と一致、caller_host / caller_ip が記録される。
- **C1**: mkfs 直後は当月パーティション無し → 最初の op で自動生成 (ensure-before-insert)。DEFAULT パーティションは持たない。
- **C2**: 未カバー月への直接 INSERT は拒否 → 同じ DDL でその月のパーティションを足せば INSERT が通る (= 月境界は別 key で同一コードパス)。
- **D**: `audit.enabled=false` (mkfs を `--audit` 無しで打ち直し) では 1 行も記録されない (テーブル自体は常時作成)。

> **月跨ぎの注記**: `occurred_at = DateTime.Now` を実時刻で未来月にできないため、本質である
> 「当月パーティションが無ければ op 前に ensure する」機構を C1 (当月の自動生成) + C2 (任意月の追加で
> INSERT 成立) で決定的に検証する。実カレンダーの月境界は翌月 key で同じ `EnsureAuditPartition` が走るだけ。

**完了**: linux_client の多ノード Citus docker 上で `bash tests/citus/audit.sh` を実行し **12/12 PASS**。Citus 同一 tx commit (2PC リスク) は A/B/C が多ノード Citus 上で成立した時点で同時実証された。これをもって**監査ログは機能・回帰・専用テストともすべて完了 (完全クローズ)**。
