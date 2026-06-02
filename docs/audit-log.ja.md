# 監査ログ (audit log)

chmod / chown / 削除 / リネーム / 作成 / ハードリンクといった **メタデータ変更操作** を、専用テーブル `{prefix}audit` に 1 操作 = 1 行で記録する機能。

英語版は [audit-log.md](audit-log.md) を参照してください。

`Logger.Information` でテキスト 1 行を出すのではなく、**DB の日付パーティションテーブルに構造化して残す**。本ドキュメントが正。

## 設計判断

| # | 論点 | 決定 |
|---|---|---|
| 1 | 「誰が」 | 呼び出し元 **IP / ホスト名 / UID / ユーザー名 / ドメイン (Windows のみ)** を記録 |
| 2 | 出力先 | ログファイルではなく **DB テーブル** `{prefix}audit` |
| 3 | パーティション | `occurred_at` で **月次 RANGE 分割** + アプリ自動作成 + `DEFAULT` 無し |
| 4 | 書き込みタイミング | **操作と同一トランザクション (アトミック)** |
| 5 | on/off | DB 保存設定 `audit.enabled`。**mkfs (`--audit`) で初期化**、将来は管理ツール |
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

- **PK は `(occurred_at, id)` の複合**。パーティションキー `occurred_at` を PK に含める必要がある (Postgres の partitioned table 制約) のに加え、Citus 分散時も分散キーを unique 制約に含める必要があるため (`pgfs_inode` の `(parent_id, id)` と同じ事情)。`id` 単独 INDEX を別途持つ。
- 固定列は「いつ・誰が・何を・どれに」を検索する軸。op ごとに変わる値は `detail` JSONB に寄せる (1 行 JSONB のシンプル設計)。

### `detail` の中身 (op 別)

| op | detail の例 |
|---|---|
| `create` | `{"mode":"40755","kind":"dir","uname":"alice","gname":"staff"}` |
| `delete` | `{}` (固定列の target_id / parent_id / name で十分) |
| `rename` | `{"old_parent":12,"new_parent":34,"new_name":"b.txt"}` |
| `chmod` | `{"mode":"100644"}` (8 進文字列) |
| `chown` | `{"uname":"bob","gname":"dev"}` |
| `hardlink` | `{"source_id":7,"new_parent":34,"new_name":"link"}` |

## パーティション戦略 (月次 + アプリが当月を lazy ensure / DEFAULT 無し)

- 親テーブルは `PARTITION BY RANGE (occurred_at)`。mkfs は **親テーブルだけ** 作り、パーティションは作らない。
- 月次パーティション (`{prefix}audit_YYYY_MM`、範囲 `[当月1日, 翌月1日)`) は **アプリが INSERT 前に `CREATE TABLE IF NOT EXISTS ... PARTITION OF ...` で確保** する。プロセス内のメモリ集合 (`yyyy_MM`) にキャッシュし、その月の初回だけ DDL を発行する。「現在日時は 1 つしか無い」ので起動時の全パーティション先読みは不要 — 当月 (と稼働中に跨いだ月) だけを lazy に作れば足りる。
- **DEFAULT パーティションは作らない**。理由: DEFAULT に行が溜まると、その範囲をカバーする月パーティションを後から `CREATE` しようとして PG が `updated partition constraint for default partition ... would be violated by some row` でエラーにする (= DEFAULT が罠になる)。INSERT 前に必ず当月を ensure するので DEFAULT は不要。
- ensure に失敗すると月パーティションが無いまま INSERT が `no partition found` で失敗し、**操作 tx ごとロールバック** する (= 監査を残せないなら操作も成立させない、というアトミック方針)。

### Citus との両立 (注意点 — out-of-band ensure)

`occurred_at` 分散 + 同一 tx 書き込み + アプリ自動パーティション作成の 3 つは素直に組むと衝突する: Citus は **分散書き込みトランザクションの途中での DDL (パーティション CREATE) を拒否** することがある。

回避策: **パーティション確保 (`EnsureAuditPartition`) は操作 tx とは別コネクション (autocommit) で実行** し、メモリキャッシュで月初回だけに絞る。監査行の INSERT 自体は操作 tx 内で行う。これにより同一 tx 内では DDL が走らない。

- Citus 時は親テーブルを `create_distributed_table('{prefix}audit', 'occurred_at')` で分散。月次パーティションは親が分散済みなら Citus が自動で分散する。
- 残リスク: 操作 tx (inode は `parent_id` 分散) に audit (`occurred_at` 分散) の INSERT を混ぜると **複数の分散テーブルに跨る分散トランザクション (2PC)** になる。機能的には Citus がサポートするが、多ノード e2e で実際にコミットできることを検証する (下記テスト参照)。

## 呼び出し元コンテキストの配線

操作ごとに変わる「誰が」は OS 層 (Mount / Assign) でしか取れないため、`Pgfs.Lib.Models.AuditContext` を **ambient (`AsyncLocal`)** で Api に渡す。各 FUSE / Dokan コールバックの先頭で `AuditContext.Current` をセットし、Api はフック時にそれを読む。

`AuditContext` には per-call で変わる `Uid` / `Uname` / `Domain` のみを載せる。`caller_host` はプロセス定数なので Api がコンストラクタで 1 回 `Dns.GetHostName()` 解決し、`caller_ip` は INSERT 内で `inet_client_addr()` をサーバ側評価する (どちらも `AuditContext` には入れない)。

| 値 | Linux (Mount) | Windows (Assign) |
|---|---|---|
| caller_uid | `fuse_get_context()->uid` (フォーク追加の `Fuse.TryGetCallerContext`) | (NULL — Windows に数値 uid 概念が無い) |
| caller_uname | uid を `UserResolver.UnameOf` で解決 | Dokan `info.GetRequestor()` の `DOMAIN\user` の user 部 |
| caller_domain | (NULL — Linux に domain/workgroup 概念が無い) | 同 `DOMAIN\user` の DOMAIN 部 |
| caller_host | Api がプロセス起動時に `Dns.GetHostName()` | 同左 |
| caller_ip | INSERT 内で `inet_client_addr()` (サーバ側評価) | 同左 |

- **Tmds.Fuse フォークの拡張**: 現行フォーク ([vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/)) は `fuse_get_context()` を公開していなかったので、P/Invoke を追加し公開 API `Fuse.TryGetCallerContext(out uid, out gid, out pid)` で読めるようにした。解決できない libfuse でもマウントを壊さないよう、シンボル解決は null 許容 (`TryCreateDelegate`) にしてある。
- caller_host は「どのマシンが操作したか」、caller_ip は「PG から見た接続元」。複数クライアントが同一の DB-FS を叩く構成で「どのクライアントの誰が」を後から追える。
- OS 層は各 mutating コールバックの先頭で `SetAuditContext()` を呼んで `AuditContext.Current` を立てる (audit 無効時や取得失敗時は null = caller_* が NULL になるだけ)。

## フック箇所 ([src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs))

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
- `InsertInode` は元々 tx を張っていなかった (`Pg.Query` 直叩き) ため、監査を同一 tx にするべく **`conn` + `tx` 化** した (conflict 時は rollback)。
- `audit.enabled` が false のときは `WriteAudit` は即 return (フック自体は常に呼ぶが no-op)。

## on/off 設定 `audit.enabled`

- `Schema.Audit.Enabled` (`scope=audit`, `key=enabled`, `BoolField`, `SaveTo=Db`, CLI `--audit`, 既定 false)。
- mkfs が `PopulateSettingsRows` で `pgfs_settings` に保存。mount / assign は起動時に `ConfigLoader` の DB フェーズ経由で読み、Api が `config.Audit.Enabled` でフックを有効化する。
- 将来は管理ツールで `pgfs_settings` の `(audit, enabled)` 行を切り替える (本実装では mkfs 初期化のみ)。

## 実装

3 レイヤ:

1. **設定 / スキーマ**: [Schema.Audit.Enabled](../src/lib/src/Config/Schema.cs) + [AuditConfig](../src/lib/src/Config/AuditConfig.cs) + `ConfigLoader` 配線、[docs/ddl/pgfs_audit.sql](ddl/pgfs_audit.sql)、mkfs [CreateAuditTableAsync](../src/mkfs/src/Initializer.cs) + 設定行投入。
2. **Api フック**: [AuditContext](../src/lib/src/Models/AuditContext.cs) ambient、[Api.WriteAudit / EnsureAuditPartition](../src/lib/src/Api/Api.cs)、6 メソッドにフック、`InsertInode` の tx 化。
3. **呼び出し元プラミング**: Tmds.Fuse フォークに `fuse_get_context` ([LibFuse.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/LibFuse.cs) / `Fuse.TryGetCallerContext`)、[mount/FileSystem.cs](../src/mount/src/FileSystem.cs) の 9 コールバック・[assign/FileSystem.cs](../src/assign/src/FileSystem.cs) の CreateFile/Cleanup/SetFileAttributes/MoveFile で `SetAuditContext`。

## テスト

監査ログは 3 レベルで検証する (全体は [docs/tests.md](tests.md) のテストハブを参照)。

- **既存回帰**: Linux e2e 35/35・Windows e2e 26/26・Citus race multinode 4/4・Citus mkfs matrix 18/18 が緑のまま (監査有効構成で検証)。
- **Citus 同一 tx commit**: `Initializer.CreateAuditTableAsync` が `--audit` に関係なく常時テーブルを作成し、`--citus` 時は `occurred_at` で分散する (Citus 分散テーブル数 = 5: inode / data / data_chunk / lock / audit)。多ノード Citus 上で監査有効のまま操作が同一トランザクションでコミットできることを `race_multinode.sh` (4/4) で実証しており、2PC リスクは顕在化しない。
- **監査専用テスト — [tests/citus/audit.sh](../tests/citus/audit.sh)**: 監査固有の検証を共有 e2e ではなくこの専用スクリプトに分けたため、既存スイートの件数は不変。docker 2 ノード Citus + mount.pgfs × 1 を立て、12 個のチェックを実行し (詳細は [tests/citus/README.md](../tests/citus/README.md))、docker Citus 上で 12/12 PASS する:
  - **A**: 各 op (create / delete / rename / chmod / chown / hardlink) で `pgfs_audit` に期待行が入る。create 行の `name` / `detail.kind` を精査。
  - **B**: `caller_uid` / `caller_uname` が呼び出し元の実値 (`fuse_get_context` 経由) と一致し、`caller_host` / `caller_ip` が記録される。
  - **C1**: mkfs 直後は当月パーティションが無い → 最初の op が ensure-before-insert で自動生成する。DEFAULT パーティションは持たない。
  - **C2**: 未カバー月への直接 INSERT は拒否される → 同じ DDL でその月のパーティションを足せば INSERT が通る (= 月境界は同一コードパス上の別 key にすぎない)。
  - **D**: `audit.enabled=false` では 1 行も記録されない。一方でテーブル自体は常時作成される。

> **月跨ぎの注記**: `occurred_at` を実時刻で未来月にはできないため、本質である「当月パーティションが無ければ op 前に ensure する」を C1 (当月の自動生成) + C2 (任意月の追加で INSERT 成立) で決定的に検証する。実カレンダーの月境界は翌月 key で同じ `EnsureAuditPartition` が走るだけ。
