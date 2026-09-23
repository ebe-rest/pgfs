# コントロールプレーン — 登録表 / 制御 NOTIFY / config / status

> **道順**: [docs/README.ja.md](../README.ja.md) › [runtime-control-plane.ja.md](runtime-control-plane.ja.md) › **本書**
>
> **この doc が正である範囲**: 実行時に設定を読み書きし、稼働状況を見るための土台と CLI の設計・
> 実装状況・変更記録。`{prefix}mounts` 登録表、制御 NOTIFY、`Field` の reload ポリシー、
> `pgfsctl config` / `status` (Layer 1〜3) はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../Pgfsctl.ja.md](../Pgfsctl.ja.md) | **利用者向けの CLI 仕様** (オプション・出力例)。本書は設計側 |
> | [gui.ja.md](gui.ja.md) | 同じ Core API (`StatusAdmin` / `ConfigAdmin`) を読む GUI 側 |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | 設定項目そのものの一覧と既定値 (reload ポリシーの値も) |
> | [cache.ja.md](cache.ja.md) / [write-back.ja.md](write-back.ja.md) / [metadata-write-back.ja.md](metadata-write-back.ja.md) | ここで読み書きする「対象」の側 |
> | [database.ja.md](database.ja.md) / [../ddl/](../ddl/README.ja.md) | `{prefix}mounts` の DDL |
> | [runtime-control-plane.ja.md](runtime-control-plane.ja.md) | 運用フェーズ全体の構成とフェーズ間の関係 (ハブ) |
>
> **章立て**: この doc は 3 フェーズが並ぶので、固定 3 章 (設計 → 実装ステータス → 変更記録) を
> **フェーズごとに**適用している。時系列の記録だけは末尾の [§変更記録](#変更記録) に集約する。

## Phase 2: 土台 (登録表 + 制御 NOTIFY + reload ポリシー)

### Phase 2 確定設計 + 実装計画

下の 2-1〜2-3 スケッチを、実コード調査の上で実装可能まで確定したもの。**実装は 2a → 2b → 2c の順**(各 Core ビルド緑 + 実機 e2e 回帰で確定、1a/1b と同じリズム)。

**P2-1. `Field` に reload ポリシー (`enum ReloadPolicy { Live, NextMount, Format }`)**
- [Field.cs](../../src/core/src/Config/Field.cs) の基底 record に `public ReloadPolicy Reload { get; init; } = ReloadPolicy.NextMount;` を追加 (既定は保守的に NextMount)。
- **Live (走行中に再適用)**: `logging.level`→`Logger.MinLevel` / `logging.output`→`LogSink.Configure` / `database.retry_*`→`Retry.Configure` (いずれも static 再呼出可) / `app.statfs`→`config.Statfs.Mode` を読み直すだけ (GetStatFs が毎回参照) / `mount.cache_max_entries`→**InodeCache に `SetCapacity` を追加** / `mount.cache_data_max_bytes`→**ContentCache に `SetMaxBytes` を追加** / `audit.enabled`→**Api.auditEnabled を mutable 化**。
- **NextMount (再マウントで反映)**: `database.connection`/`super_connection`/`schema`/`prefix`/`tablespace*`/`citus`/`workers`/`notify_enabled` / `mount.mount_point`/`foreground`/FuseFlags / `mount.fallback_uname`/`fallback_gname` (当面: resolver 再構築が要るので Live 化は後回し)。
- **Format (mkfs 専用・以後不変)**: `file_system.version`/`volume_label`/`cluster_size`/`default_chunk_size`/`max_file_size`。

**P2-2. `{prefix}mounts` 登録表 + heartbeat**
- mkfs の [Initializer.CreateTableAsync](../../src/mkfs/src/Initializer.cs) + Citus local 登録 (`citus_add_local_table_to_metadata`、`pgfs_settings` と同じ) でmkfs 時に作成。
- mount/assign は **起動時 INSERT → 定期 UPDATE (heartbeat) → 正常終了で DELETE**。heartbeat は [NotifyChannel](../../src/core/src/Api/NotifyChannel.cs) と同じ `Task.Run` + `CancellationTokenSource` + `Pg.Execute` パターン。間隔は当面定数 30s (将来 Field 化可)。
- **既存 FS 互換**: テーブルが無い (再 mkfs していない FS) 場合は **warning ログを出して登録/heartbeat をスキップ**し、mount 自体は成功させる (= mount 時に DDL を撃たない方針。DDL は mkfs に集約)。
- 列は 2-1 のとおり (`config`/`stats` は JSONB に寄せる)。

**P2-3. NOTIFY 制御メッセージ + reload 適用**
- [NotifyMessage](../../src/core/src/Api/NotifyChannel.cs) に `[JsonPropertyName("c")] string? Control` を追加 (`"reload"` / `"ping"`)。`Api` に `PublishControl(op)` を足す。
- `OnRemoteChange`: `Control=="reload"` → `Api.ReloadLiveConfig()` (新規) / `Control=="ping"` → heartbeat 即時書き戻し。
- **`Api.ReloadLiveConfig()`**: `new ConfigStore(...).LoadAll(...)` で `pgfs_settings` を再読込 → **Reload=Live のフィールドだけ** 再 parse して live ターゲットに適用 + `config` POCO も更新 (後続 read が新値を見る)。

**実装サブステップ**:
- **2a**: `ReloadPolicy` enum + Field タグ付け + InodeCache.SetCapacity / ContentCache.SetMaxBytes / Api.auditEnabled mutable 化。**挙動不変** (タグと setter を足すだけ) なのでビルド緑が確認。
- **2b**: `{prefix}mounts` (mkfs 作成 + Citus local) + Api の register/heartbeat/deregister (テーブル不在は warning skip)。
- **2c**: NotifyMessage `Control` + `PublishControl` + `OnRemoteChange` 分岐 + `Api.ReloadLiveConfig()`。reload を撃つ口は Phase 3 の `config set` だが、2c では**テスト用に発火経路**(例: SIGHUP or 一時的な CLI) を 1 つ用意して live 反映を実機確認。

**開いている点 (実装前に確認したい)**: ① heartbeat 間隔を定数 30s で始めてよいか (Field 化は後で) / ② 既存 FS は「mount は warning skip / 登録は再 mkfs 後」で良いか (それとも mount-time `CREATE IF NOT EXISTS` で自動治癒したいか) / ③ 2c の reload 発火を Phase 3 まで待たず暫定でどう撃つか (SIGHUP 受けるのが Linux 的に自然)。

**実装完了 (2a/2b/2c・Core ビルド緑)**: 3 点の決着 = ① heartbeat **30s 定数**で開始。② 既存 FS は **再 mkfs (no --clean) で冪等・非破壊に登録表追加**、それまで mount は warning skip。③ reload 発火は **psql `pg_notify('<channel>','{"c":"reload"}')`** でテスト (本番発火は Phase 3 の config set)。**重要 — `ReloadLiveConfig` は `ConfigStore.LoadAll` が SaveTo=Db のみ返すため、即時反映できるのは DB 保管の Live (`audit.enabled` / `app.statfs`)**。File 保管 Live (logging/cache/retry) の runtime override 経路は Phase 3 で決める (switch は全 Live キーを実装済・setter も 2a で用意済なので、経路が決まれば即有効化)。

**実機検証 (2026-06-14, docker e2e)**: 標準 36/36 + caps stress 36/36 (2a 無回帰 / 1a/1b 回帰)。**2b: register = `pgfs_mounts` 1 行 / deregister = unmount 後 0 行**。deregister は当初 0 にならず、**mount/assign が `Api` を Dispose していない**バグを実機 e2e が発見 → `using var api` + `Api.Dispose` 冪等化で修正。`run.sh` に register/deregister アサートを追加。**2c (live reload): `tests/docker/control_plane.sh` で実機緑** — notify ON で mount → psql から `pg_notify('{schema}_{prefix}notify','{"c":"reload"}')` → `ReloadLiveConfig` が `audit.enabled` を走行中に有効化 (直後の mkdir で監査行 **0 → 1**)。これで NOTIFY 制御 → reload → Live 適用の全鎖を実証。

### 2-1. `{prefix}mounts` 登録表 (案)

| 列 | 型 | 内容 |
|---|---|---|
| `mount_id` | TEXT PK | プロセス一意 ID。現 NOTIFY の sender_id (8 桁 hex) を再利用 |
| `host` | TEXT | ホスト名 |
| `pid` | INT | プロセス ID |
| `mountpoint` | TEXT | マウント先 |
| `mode` | TEXT | `'fuse'` / `'dokan'` |
| `started_at` | TIMESTAMPTZ | 起動時刻 |
| `heartbeat_at` | TIMESTAMPTZ | 最終 heartbeat |
| `config` | JSONB | 実効設定スナップショット (値 + 出所) |
| `stats` | JSONB | キャッシュ統計等 (任意) |

- 起動時 INSERT、定期 + `ping` 受信時に `heartbeat_at` 更新、**正常 unmount で DELETE**。stale 行は heartbeat 経過で判定/掃除。
- Citus: `pgfs_settings` と同様 **local table** (coordinator) とし分散しない (登録/heartbeat は低頻度)。
- 設定永続化の嗜好 (1 行 JSONB 寄り) に倣い、可変状態は `config`/`stats` を **JSONB に寄せる**。

### 2-2. NOTIFY 制御メッセージ
現 payload `{s, i, p, x}` (sender / inode / parent / path-prefix) に **制御種別**を追加:

```jsonc
{ "s": "<sender>", "t": "ctl", "op": "reload" | "ping", "scope": "<任意>" }
```

[Api.cs](../../src/core/src/Api/Api.cs) `OnRemoteChange` で分岐:
- `ctl/reload` → `pgfs_settings` を再読込し **reload ポリシー `Live` のフィールドだけ**再適用。
- `ctl/ping` → 即時に `heartbeat_at` / `stats` を書き戻し (④ status の鮮度確保)。

### 2-3. Field の reload ポリシー (②③ を「正直」にする鍵)
[Field](../../src/core/src/Config/Field.cs) は `SaveTarget` (None/File/Db) を持つ。ここに **reload 種別**を 1 段追加:

| 種別 | 意味 | 例 |
|---|---|---|
| `Live` | 走行中に再適用可 | `logging.level`/`output`, `mount.cache_max_entries`, `mount.fallback_uname`/`gname`, `audit.enabled`, `app.statfs` |
| `NextMount` | 再マウントで反映 | `mount.mount_point`, FuseFlags, `database.connection`, `schema`/`prefix` |
| `Format` | mkfs 専用・以後不変 | `file_system.cluster_size`/`chunk_size`/`max_file_size` |

ライブ適用の実体は Core 側の可変ランタイム状態の差し替え (`Logger.MinLevel` / `LogSink.Configure` / InodeCache 容量再設定 / fallback 名 / audit フラグ / statfs フラグ)。FUSE/Dokan の再初期化は不要 (= OS 層は無改修で済む範囲が `Live`)。マトリックスは [settings-matrix.ja.md](settings-matrix.ja.md) に reload 列を追加して正にする。

---

## Phase 3: config サブコマンド (+ ライブ反映)

### Phase 3 確定設計

以下の問題記述は実装前の状態である。現在は制御 LISTEN 常時 ON。GUI の shell-out 案は Phase 5 の Core 直接呼出しに変更済みである。

実コード照合の上で **前提発見 1 + 設計判断 3** を確定 (下「開いている設計判断」1〜3 をクローズ)。**実装は 3a → 3b → 3c の順** (各 Core ビルド緑 + 実機 e2e 回帰、1a/1b/2a-c と同じリズム)。

**前提発見 P3-0 — 制御チャネルが `notify_enabled` にゲートされている**
[Api.cs](../../src/core/src/Api/Api.cs) は `notifyChannel` を `config.Database.NotifyEnabled` が true のときだけ生成・Start する (Api ctor)。よって **単一クライアント mount (既定 notify OFF) には reload/set/ping が一切届かない** (Phase 2c の reload デモが notify ON を要求したのはこのため)。`TryRegisterMount` は notify_enabled 非依存で走るので status (登録表 read) は単一でも動くが、live `config set` (Phase 3 の本丸) は届かない。

**決定 P3-0 — 制御 LISTEN を常時 ON に分離**: NotifyChannel の LISTEN は **notify_enabled に関係なく常時張る**。`notify_enabled` は **データ変更通知 (i/p/x/d ペイロードの Publish/handle) だけ**をゲートする。
- 実装: Api ctor で `notifyChannel` を常に生成・Start。データ変更を送る `Notify(...)` は新フラグ `this.dataNotifyEnabled = config.Database.NotifyEnabled` で早期 return (単一クライアントが無駄に pg_notify を撃たないため)。`PublishControl` / `OnRemoteChange` の制御分岐は常に有効。
- コスト: mount あたり常時 idle LISTEN 接続 1 本 (statfs TTL キャッシュ / heartbeat と同じ「DB 常駐」割り切り)。
- 制御チャネルとデータ通知は同一 NOTIFY channel (`{schema}_{prefix}notify`) を共有し続ける (NotifyMessage の `c` で判別、既存どおり)。

**決定 P3-1 — exe 構成 = 単一 `pgfsctl`**: `config` / `status` を **1 個の `pgfsctl`** (Core-only 参照・FUSE/Dokan 不要) にサブコマンドとして載せる。
- 両 OS で動く (FUSE/Dokan 非依存)。Phase 5 GUI は 1 バイナリに `--json` で shell-out。
- ディスパッチ: `switch(args[0])` で `config` / `status` に振る薄い層を新設 (現行に無いサブコマンド機構だが trivial)。各サブコマンド内の引数解釈は既存 [ConfigLoader](../../src/core/src/Config/ConfigLoader.cs) を流用。
- 命名: `AssemblyName`=`pgfsctl` / `RootNamespace`=`Pgfs.Ctl`。`{役割}.pgfs` 命名規約 (mkfs.pgfs 等) を **意図的に破る例外** (admin ツールは systemctl 風の 1 語が自然)。csproj は mkfs と同型 (OutputType=Exe, Core.csproj 参照のみ)。新規プロジェクトは `src/ctl/`、出力は `bin/Publish/pgfsctl(.exe)`。

**決定 P3-2 — `config set` の File+Live 反映経路 = NOTIFY インライン同梱**: File 保管 Live (logging/cache/retry) は remote toml を触れないので DB 永続しない。値を **NOTIFY 制御メッセージにインライン同梱** (`{"c":"set","k":"logging.level","v":"debug"}`) し、走行中 mount が直接 live ターゲットへ適用する。
- DB 行を作らないので **remount で復活する footgun が無く純粋にエフェメラル** (toml がベースライン、永続化は手元 toml 編集を案内)。
- [NotifyMessage](../../src/core/src/Api/NotifyChannel.cs) に `[JsonPropertyName("k")] string? Key` / `[JsonPropertyName("v")] string? Value` を追加 (null 時 payload 非出力)。
- 受信側 `OnRemoteChange`: `Control=="set"` → 該当 Field を Schema から引き、`Reload==Live` なら `ApplySingleLive(field, Value)`。[ReloadLiveConfig](../../src/core/src/Api/Api.cs) の switch を `ApplySingleLive(Field, string raw)` に抽出し、reload (DB 全 Live ループ) と set (単一 Field) で共用する。

**`config set <scope.key> <value>` の挙動マトリクス** (Schema の `(SaveTo, Reload)` で分岐):

| field の (SaveTo, Reload) | 永続化 | live 反映 | pgfsctl の案内 |
|---|---|---|---|
| **Db, Live** (`audit.enabled` / `app.statfs`) | pgfs_settings 書込み | ✅ `set` NOTIFY → 即時 | persisted + applied live to N mounts |
| **File, Live** (logging/cache/retry の 5) | **なし (ephemeral)** | ✅ `set` NOTIFY → 即時 | applied live to N mounts (ephemeral; edit pgfs.toml to persist) |
| Db, NextMount (`fallback_*`/`tablespace*`/`citus`/`plperlu`) | pgfs_settings 書込み | 次回マウント | persisted; applies on next mount |
| File, NextMount (`mount_point`/`connection`/`schema`/`prefix`/`notify_enabled`/`workers`) | なし (remote toml 不可) | 次回マウント | cannot reach remote toml; edit pgfs.toml locally; applies next mount |
| Format (`file_system.*`) | 拒否 | — | mkfs-only, immutable after format |
| None (`--clean`/`foreground`/`super_connection`/`setting.*`) | 拒否 | — | not a settable runtime field |

- **N (適用 mount 数) は `{prefix}mounts` 登録表の現在行数**。NOTIFY は fire-and-forget で ack が無いため真の「適用済み数」は取れない (決定 1 の「直近」値割り切り)。pgfsctl は「登録表に M mount → 制御メッセージを発火した」と正直に報告する。
- 値検証は `Field.NormalizeRaw` (Parse→Format) を流用。super 権限不要 (pgfs ユーザが pgfs_settings を所有)。

**`config get` / `config list`**:
- `config get <scope.key>` … その field の DB 保管値 (あれば) / default / SaveTo / Reload を表示。
- `config list` … 全 field を scope.key 順に DB 値 or default + SaveTo + Reload。手元に toml があれば値の出所も付す。`--json` で機械可読 (⑤ GUI 用)。
- **走行中 mount の実効値ビュー**は Phase 4 `status` (= `{prefix}mounts.config` スナップショット) の担当。config get/list は DB+default 焦点に留め、責務を分ける。

**実装サブステップ**:
- **3a** (Core のみ・挙動不変): 制御 LISTEN 常時 ON 化 (P3-0) + NotifyMessage に `k`/`v` + `OnRemoteChange` の `set` 分岐 + ReloadLiveConfig の switch → `ApplySingleLive` 抽出。notify OFF でも LISTEN が 1 本増えるだけで既存 e2e は不変。
- **3b**: `pgfsctl` プロジェクト新設 (Core-only) + `config get/list/set` + `--json`。set は上マトリクスに従って永続化/NOTIFY 発火を出し分け。接続情報は mount と同じ `-c/--connection` + toml 探索を ConfigLoader で流用。
- **3c**: 実機 e2e — **notify OFF の単一クライアント mount** に対し `pgfsctl config set logging.level debug` で live 反映、`pgfsctl config set audit.enabled true` で監査 0→1 (Phase 2c の [control_plane.sh](../../tests/docker/control_plane.sh) を notify OFF 経路へ拡張)。

ライブ反映 (②) は本コマンドの set → NOTIFY → 各 mount の `OnRemoteChange` で完結する (新たな機構は不要、Phase 2 の土台 + P3-0 の常時 LISTEN に乗るだけ)。

### 実装ステータス (as-built)

- **3a 完了**: 制御 LISTEN 常時 ON 化 (P3-0) + NotifyMessage `k`/`v` + `OnRemoteChange` の `set` 分岐 + `ReloadLiveConfig` の switch を `ApplySingleLive(field, raw)` に抽出 (reload と set で共用) + `ApplyLiveSet` + `Notify` を `dataNotifyEnabled` でゲート。制御専用 LISTEN の失敗は warning + 続行 (notify ON のときだけ従来どおり起動中止)。**全 sln ビルド緑・挙動不変** (既存 e2e は notify ON 経路不変 / notify OFF は idle LISTEN が 1 本増えるだけ)。
- **3b 完了**: `pgfsctl` (src/ctl・Core-only・`Pgfs.Ctl`) + `config get/list/set` + `--json`。ロジックは Core の公開 [ConfigAdmin](../../src/core/src/Config/ConfigAdmin.cs) (GUI 再利用可)。supporting: `Field.FormatDefaultRaw`/`FormatJsonFromRaw`、`ConfigStore.SaveRaw` (非ジェネリック UPSERT)、`ConfigLoader.GetMergedRaw`。**オフライン smoke**: マトリクス分岐 (Format/File+NextMount/unknown を拒否) 検証 + 実 DB に対する `config list/get` で db/config/default 出所判定 + Password マスクを確認。
- **3c 完了 (実機 e2e 緑)**: docker (実 FUSE) で [control_plane_ctl.sh](../../tests/docker/control_plane_ctl.sh) PASS — **notify OFF の単一クライアント mount** に対し ① 制御 LISTEN が常時 ON (P3-0 直接確認) ② `pgfsctl config set audit.enabled true` (Db+Live) で監査行 **0→1** (永続 + live) ③ `pgfsctl config set logging.level trace` (File+Live) のインライン set が mount に適用 (P3-2)。**回帰**: 同セッションで run.sh **36/36** + control_plane.sh (2c notify ON reload) PASS。`Dockerfile.mount` に pgfsctl publish を追加。
- **既知の注意**: `LogLevelField` 等の寛容パーサ (`Level.Parse`) は不正値で例外を投げず既定にフォールバックするため、`config set logging.level <garbage>` は拒否されず既定相当が適用される (mkfs/mount CLI と同じ Field 検証セマンティクス)。`config set` の DB 永続は `pgfs_settings` 接続が必要 (失敗時は exception → exit 1)。

---

## Phase 4: status サブコマンド

3 層の情報を集約 (`--json`):
1. **クラスタ稼働一覧** … `{prefix}mounts` の行 (host/pid/mountpoint/mode/uptime/heartbeat 経過/live?)。
2. **FS 統計 (DB 由来・mount 不要)** … schema/prefix/version, inode 数, 総バイト, chunk 数, df (statfs 再利用), audit on/off, Citus node/shard。← **read-only でここだけ先行リリース可**。
3. **稼働プロセス詳細** … `ping` で heartbeat を最新化してから読む実効設定 + 出所 / キャッシュ統計 (entries・hit 率) / NOTIFY 接続 / DB 健全性。

### Phase 4 確定設計

実コード照合 + 合意した 3 判断。`{prefix}mounts` には既に `config`/`stats` JSONB 列がある (Layer 3 のスナップショット置き場) が、**現状 mount は host/pid/mountpoint/mode/started_at/heartbeat_at しか書かず config/stats は空** という現状を踏まえた段階実装。

**決定 P4-1 — 着手範囲 = Layer 1+2 先行 (read-only・mount 改修不要)**: クラスタ稼働一覧 (Layer 1) + FS 統計 (Layer 2) をまず実装。両方 DB 由来の read-only なので mount 側は無改修で動く (設計の「先行可」サブセット)。**Layer 3 (稼働プロセスのキャッシュ統計/実効設定) は次の増分**に切る。

**決定 P4-2 — Layer 3 の鮮度取得 (将来) = heartbeat スナップショット**: Layer 3 を実装する際は、mount が heartbeat (30s) ごとに自分の実効設定/キャッシュ統計を `{prefix}mounts.stats` (+`config`) JSONB に書き、status は登録表を読むだけにする (req-rep しない)。決定 1 の「直近値」割り切りに一致し、既存 `stats` 列をそのまま使う。`ping` req-rep は低レイテンシ要求が出たときの後付け余地として残す (今はやらない)。

**決定 P4-3 — 出力 = 単一 `pgfsctl status [--json]` + セクション**: 1 コマンドで「稼働一覧」「FS 統計」をセクション表示。`--json` は機械可読 (Phase 5 GUI 用)。サブターゲット分割 (`status mounts` / `status fs`) はしない (systemctl status 風)。

**実装構成**:
- **Core `StatusAdmin`** (新規・公開、`ConfigAdmin` と対) が DB 読みを担当:
  - `ListMounts()` → `{prefix}mounts` を `now() - heartbeat_at` / `now() - started_at` 込みで SELECT し、各行に uptime 秒・heartbeat 経過秒・live? (経過 < 閾値 = 3×heartbeat=90s) を付けて返す。テーブル不在は空 + 注記。
  - `FsStats()` → DB 集約: `{prefix}inode` 件数 / `{prefix}data_chunk` 件数 / 使用バイト (statfs と同じ集計を再利用) / `file_system.version`・`volume_label`・`cluster_size`・`max_file_size` (pgfs_settings) / `audit.enabled` / Citus 有無 + `pg_dist_node` 数 (best-effort)。
- **pgfsctl `status`** は薄いフロント (text/`--json`)。接続解決は config と同じ ConfigLoader。
- **実装サブステップ**: **4a** Core `StatusAdmin` (ListMounts + FsStats) → **4b** pgfsctl `status` + `--json` → **4c** 実機 e2e (`control_plane_ctl.sh` 系で mount 中に status が稼働行/統計を返すのを確認)。
- **doc**: 完了時に `Pgfsctl.md` (config + status の CLI 仕様) を新設し README / docs/README 一覧へ登録。

**開いた点 (Layer 3 着手時に詰める)**: ~~stats JSONB に載せる項目 (entries / hit率 / NOTIFY 接続状態) と InodeCache/ContentCache の統計取得 API~~ → ✅ **下 §Phase 4 Layer 3 確定設計** で確定。

### 実装ステータス (as-built)

- **4a/4b 完了 **: Core [StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) (`ListMounts` = DB の `now()` 差分で uptime/heartbeat 経過/live? 付与・テーブル不在は graceful / `GetFsStats` = inode/file/chunk 数・使用バイト `sum(length(payload))`・version/label/cluster/max + audit + citus(+pg_dist_node)) + pgfsctl `status [--json]` (セクション text / JSON)。接続解決は [CliUtil](../../src/ctl/src/CliUtil.cs) に共通化 (config/status 共有)。**オフライン smoke**: 実 DB で Layer 2 統計が正確、`{prefix}mounts` 不在 FS では graceful degrade を確認。
- **4c 完了 (実機 e2e 緑)**: docker (実 FUSE) で [status.sh](../../tests/docker/status.sh) PASS — Layer 1 (table_present / live な fuse mount 1 行 / unmount で deregister 0 行) + Layer 2 (ファイル作成で inodes 1→3・used_bytes>0・chunk_count≥1)。
- **doc**: [Pgfsctl.ja.md](../Pgfsctl.ja.md) 新設 (config + status の CLI 仕様)、README / docs/README 一覧へ登録済。
- **Layer 3 完了 (4d-1〜4d-4・実機 e2e 緑)** — 下 §Phase 4 Layer 3 確定設計の末尾 as-built を参照。

### Phase 4 Layer 3 確定設計

実コード照合 ([InodeCache](../../src/core/src/Api/InodeCache.cs) / [ContentCache](../../src/core/src/Api/ContentCache.cs) / [Api](../../src/core/src/Api/Api.cs) heartbeat / [StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) / `{prefix}mounts` DDL) + 合意 (スコープ = **stats + config 両方** / 統計項目 = 下フルセット)。**schema 影響なし** — `{prefix}mounts` には mkfs 作成済の `config`/`stats` JSONB 列が既にある (現状は空 `'{}'`) ので Layer 3 はこれを populate するだけ。

**決定 P4-4 — 鮮度 = heartbeat スナップショット (P4-2 を具体化)**: mount が **register 時 + 30s heartbeat ごと + ping 受信時**に、自分の `InodeCache`/`ContentCache` 統計と実効設定を JSON 化して `{prefix}mounts.stats`/`.config` に書く。`status` は登録表を読むだけ (req-rep しない)。鮮度は「直近 heartbeat 値」(決定 1 の割り切り)。register 時にも書くので起動直後から status に出る。

**決定 P4-5 — キャッシュ統計 API (挙動不変)**: 両キャッシュに累積カウンタ (`long`) を足し、既存 `lock(this)` 配下で増分する (ホットパス影響ほぼゼロ)。スナップショット用に読み取り専用の `Stats()` を公開:
- `ContentCache.Stats()` → `entries` (chunk 数) / `bytes` (currentBytes) / `maxBytes` / `hits` / `misses` / `evictions` / `generation`。hit/miss は `Get` の戻り (非 null = hit / null = miss) で **1 チョークポイント**。
- `InodeCache.Stats()` → `entries` (byId.Count = 権威) / `pathEntries` / `childrenLists` / `capacity` (cacheMaxEntries) / `hits` / `misses` / `evictions`。hit/miss は **Load 境界** = キャッシュで解決 (hit) / DB inode クエリに落ちた (miss)。複数 Load オーバーロードが集約される `load()` に置く。
- hit率は保存せず status 表示時に `hits/(hits+misses)` で算出。`evictions` は `EvictIfOverCapacity`/`EvictIfOverBudget` が落とした件数の累積。

**決定 P4-6 — スナップショット内容 (values-only)**:
- `stats` JSONB: `{ "inode":{entries,capacity,hits,misses,evictions}, "content":{entries,bytes,maxBytes,hits,misses,evictions,generation}, "notify":{control_listen,data_enabled,connected}, "snapshot_at":<ISO8601> }`。
- `config` JSONB: 走行中 `RootConfig` の**実効値** (= 実際に動いている mount の値)。`pgfsctl config list` (DB+default) と違い **File+Live の ephemeral set 適用後 / CLI・toml override の結果が見える**のが新規価値。**v1 は値のみ** — 出所 (provenance: CLI/toml/DB/default) は `RootConfig` が読込後に保持しないため後回し (出所が要るなら ConfigLoader に provenance を持たせる別増分)。

**実装構成**:
- **Api**: `WriteHeartbeat` を `SET heartbeat_at, stats=@stats, config=@config` に拡張。register の INSERT / ping 受信 (`OnRemoteChange`) でも同 JSON を書く。JSON 構築は System.Text.Json (NotifyMessage で既出)。`notify.connected` は NotifyChannel の接続状態、`data_enabled` は `dataNotifyEnabled`。
- **StatusAdmin**: `ListMounts` の SELECT に `config`/`stats` を追加し `MountInfo` に raw JSON (or typed) で載せる。列不在は graceful (既存方針)。
- **pgfsctl status**: live な mount ごとに Layer 3 セクション (キャッシュ統計 + 実効設定)。非 live は最終スナップショット + 経過注記。`--json` 拡張。

**実装サブステップ** (a/b/c リズム):
- **4d-1**: `InodeCache.Stats()` / `ContentCache.Stats()` + カウンタ (挙動不変・全 sln ビルド緑)。
- **4d-2**: Api が register/heartbeat/ping で stats+config JSON を書く。
- **4d-3**: StatusAdmin が config/stats を読み、pgfsctl status が Layer 3 を表示 (+`--json`)。
- **4d-4**: 実機 e2e ([status.sh](../../tests/docker/status.sh) 拡張) — mount → ファイル作成 + read で content cache を温め、status が `content.entries>0` / `inode.hits>0` / 実効 config を返すのを assert。

**開いた点 (実装時に確認)**: ① `config` スナップショットに載せる field の範囲 (全 `RootConfig` か settings-matrix の運用関連サブセットか)。② InodeCache の hit/miss を Load 境界に置く際の path-walk (`load(parser,…)`) の数え方 (各 hop の DB クエリを miss にカウントするか、Load 1 回を 1 単位にするか)。

### 実装ステータス (as-built)

実装完了・実機 e2e 緑。開いた点 2 つの決着: ① **運用関連サブセット** ([Api.BuildConfigJson](../../src/core/src/Api/Api.cs) に 13 キー = logging/retry/notify_enabled/mount_point/cache×2/statfs/audit/version/label。password 非含)。② **Load 1 回 = 1 単位** (private `load(long?,string?)` 境界で hit/miss。path-walk は内部の DB クエリ数によらず 1 miss)。

- **4d-1**: [InodeCache](../../src/core/src/Api/InodeCache.cs) / [ContentCache](../../src/core/src/Api/ContentCache.cs) に累積カウンタ (hits/misses/evictions) + 読み取り専用 `Stats()` (`InodeCacheStats` / `ContentCacheStats`)。Content は `Get` 戻りで hit/miss (1 チョークポイント)、Inode は Load 境界。すべて既存 `lock(this)` 配下。**挙動不変・Core ビルド緑**。
- **4d-2**: [Api.WriteHeartbeat](../../src/core/src/Api/Api.cs) を `heartbeat_at` + `stats`/`config` JSONB 書込に拡張 (register 直後 + 30s heartbeat + ping 受信の 3 経路)。`BuildStatsJson` (inode/content/notify/snapshot_at) + `BuildConfigJson` (実効値・values-only)。[NotifyChannel.Connected](../../src/core/src/Api/NotifyChannel.cs) 追加。**schema 影響なし** (mounts.config/stats 列は mkfs 作成済)。
- **4d-3**: [StatusAdmin.ListMounts](../../src/core/src/Api/StatusAdmin.cs) が `config::text`/`stats::text` も読み `MountInfo` に載せる。[StatusCommand](../../src/ctl/src/StatusCommand.cs) が Layer 3 セクション (inode/content キャッシュ統計 + hit率算出 + notify 接続状態 + 実効 config の key=value) を text + `--json` (JsonNode で入れ子 embed) で出す。stale mount は `[stale]`。
- **4d-4** (実機 e2e 緑)**: docker (実 FUSE) で [status.sh](../../tests/docker/status.sh) 拡張 PASS — read で content キャッシュを温め → ping 制御 NOTIFY (常時 LISTEN・P3-0) で snapshot 即更新 → `content chunks=1` / `inode hits=33` / 実効 config (`audit.enabled`) / `notify listen=on`。**回帰**: run.sh **36/36** PASS・deregister 0 行 (read ホットパスカウンタ + heartbeat snapshot 書込の無回帰)。

---


## 変更記録

時系列の記録はここに追記する (設計と as-built は上の各フェーズの章が正)。

- [runtime-control-plane.ja.md](runtime-control-plane.ja.md) が 1,802 行に肥大したため、
  機能ごとに分割してこの doc を切り出した。内容は分割前のまま。
