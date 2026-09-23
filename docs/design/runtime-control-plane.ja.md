# 実行時コントロールプレーン + キャッシュ高速化 設計プラン

> **道順**: [docs/README.md](../README.md) › **本書**
>
> **この doc が正である範囲**: 「運用フェーズ」5 テーマの**全体構成とフェーズ間の関係**。
> **機能ごとの設計・as-built・変更記録は持たない** — 下の 6 本が正で、ここは索引である。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [cache.md](cache.md) | ① キャッシュ (1a inode LRU / 1b content read / 1c read-ahead) |
> | [write-back.md](write-back.md) | 1d データ本体の遅延書き |
> | [metadata-write-back.md](metadata-write-back.md) | 1e メタデータの遅延書き (確定設計 + 実装ステータス) |
> | [metadata-write-back-reviews.md](metadata-write-back-reviews.md) | 1e のレビュー記録 (ラウンド A / B-1〜B-13) |
> | [control-plane.md](control-plane.md) | ②③④ 登録表 / 制御 NOTIFY / `pgfsctl config`・`status` |
> | [gui.md](gui.md) | ⑤ GUI 運用ダッシュボード |
> | [performance.md](performance.md) | ① の土台になった実測と改善候補 |
> | [settings-matrix.md](settings-matrix.md) | 設定項目の一覧と reload ポリシーの値 |
> | [database.md](database.md) / [../ddl/](../ddl/README.md) | `{prefix}mounts` の DDL |
>

> 「運用フェーズ」の 5 テーマ —
> ① キャッシュ充足での高速化 / ② マウントしたまま設定変更を反映 / ③ 設定変更サブコマンド /
> ④ 状態確認サブコマンド / ⑤ これらの GUI ラッピング — を **一本の設計**として束ねる。
> 本書はまず **決定事項** を固定し、以降に詳細設計を追記していく (設計書 → 合意 → 実装 → full e2e)。
> **現行状態（2026-09-21 実機再走）**: 1a/1b/1d、1e ステージ 1・1.5・2、Phase 2〜4、GUI 5a/5b は実装されている。1c read-ahead、GUI 5c/5d は未完了。
> **1e の未修正指摘は全件クローズした** — ラウンド A / B の指摘も、レビューに挙がった 15 件も残っていない。
> かつてここに書いていた「既存テスト 15/16 の FAIL」は **A-7 で解消済み**で、いま `wbmeta.sh` は **27 件すべて緑**である。
> **Linux 9 スイートは全緑** (e2e off/on 49+1skip / crossclient 14 / writeback 9 / wbmeta 27 / negcache 7 / handles 11 / prune 6 / startup 5)、**docker と Citus の 3 本も再走済み**。件数の正は [tests.md](../tests.md)。
> 現行 CLI 契約は [Mount.md](../Mount.md)、Windows の追加設計は [windows-parity.md](windows-parity.md) を参照する。以下の「確定設計」は当時の計画を残し、実装との差は as-built 節で示す。

## この 5 テーマの構造 (なぜ 1 本にまとめるか)

5 個の別件に見えるが、依存構造は次の通り:

- **②③④ は同じ土台を共有する** — いずれも「動作中の mount/assign プロセスに触る手段 (コントロールチャネル)」を必要とする。設計着手時は存在しなかったが、現在は NOTIFY 制御チャネルと mounts 登録表が実装済みである。
- **① は半独立** — 単体で速度効果が出る。ただし content キャッシュの invalidation で「data-write NOTIFY」を足す所が ② の土台と合流する。
- **⑤ は ③④ の上に乗る** — `config`/`status` を機械可読 (`--json`) にしておけば、GUI はその薄いフロントになる。

```
  ⑤ GUI  ─────────────────────────────┐ (③④ のラッパ)
  ③ config 変更 ─┐   ④ status 確認 ─┐  │
                 ▼                  ▼  ▼
  ② ライブ反映 ── コントロールプレーン (土台) ──┐
       NOTIFY 制御メッセージ + pgfs_mounts 登録表 │
                 ▲                              │
  ① キャッシュ ──┘ (data-write NOTIFY で合流)    ▼
                                         Pgfs.Core / PostgreSQL
```

## 現行実装の案内

| 項目 | 現在の実装 | 残る制約 |
|---|---|---|
| 設定 | RootConfig の Live 項目を ApplySingleLive が変更 | write-back off の drain・受付競合は未修正 |
| 通信 | 制御 LISTEN は常時起動、データ通知だけ notify_enabled で制御 | 通知の適用 ACK・再接続後の完全再同期は保証しない |
| 管理 | pgfsctl config/status、mounts の heartbeat・config/stats | heartbeat は直近スナップショット |
| キャッシュ | inode LRU、content read、negative cache | read-ahead 未実装、OS キャッシュとは別層 |
| write-back | DirtySet と DirtyNamespace、明示同期、背景・終了時 flush | data/metadata とも既定 off。下記レビューの不具合あり |
| GUI | Core を直接呼ぶ読み取りダッシュボード | 設定編集・配布整備は未完了 |

実装根拠は [Api.cs](../../src/core/src/Api/Api.cs)、[Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs)、[GUI](../../src/gui/) である。

## 設計着手時点の状態（履歴・現行仕様ではない）

| 項目 | 現状 | 参照 |
|---|---|---|
| 設定の適用 | **起動時に一度だけ**解決 (CLI → toml → DB `pgfs_settings` → default)、以後 `RootConfig` は実質 immutable。**ライブ再読込なし** | [ConfigLoader.cs](../../src/core/src/Config/ConfigLoader.cs) `BuildRootConfig` / 各 `Program.cs` |
| 動作中 mount への通信路 | **IPC なし**。`LISTEN/NOTIFY` のみで **一方向** (他クライアント → mount が listen、inode/path invalidate だけ。返信不可) | [NotifyChannel.cs](../../src/core/src/Api/NotifyChannel.cs) / [Api.cs](../../src/core/src/Api/Api.cs) `OnRemoteChange` |
| サブコマンド | mkfs/mount/assign は **薄い exe を 1 個ずつ**。ディスパッチテーブルなし。新コマンドは「薄い exe を足す」のが既定 | [src/mkfs](../../src/mkfs/src/Program.cs) / [mount](../../src/mount/src/Program.cs) / [assign](../../src/assign/src/Program.cs) |
| InodeCache | `RichDictionary` 3 本 (id/path/children)。`mount.cache_max_entries` 既定 1024 **だが容量は初期ヒントだけで LRU 退避は未実装**。TTL なし、NOTIFY で手動 invalidate | [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) |
| ファイル本体キャッシュ | **皆無**。read/write 毎に新規 tx で DB 往復 (partial TOAST detoast は効く) | [Api.cs](../../src/core/src/Api/Api.cs) `ReadData`/`WriteData` |
| statfs | **5 秒 TTL キャッシュあり** (DB に状態を持たせる前例) | [Api.cs](../../src/core/src/Api/Api.cs) `StatFsCacheTtl` |
| NOTIFY 発火点 | create/delete/chmod/chown/hardlink/rename 等の **メタデータ操作**。データ write での発火は **未** (① の content キャッシュで要追加) | [Api.cs](../../src/core/src/Api/Api.cs) |

## 決定事項 (合意)

### 決定 1: コントロールチャネルは **DB 集約** (NOTIFY + 登録表)
動作中プロセスへの通信路は、既存の `LISTEN/NOTIFY` バックボーンを拡張して実現する。
- 既存 NOTIFY を **制御メッセージ (reload / ping) も運べる**よう拡張。
- 各 mount/assign が起動時に **`{prefix}mounts` 登録表**へ自己登録 + heartbeat。
- **採用理由**: 現行の DB 中心設計にそのまま乗る / クラスタ横断で全 mount を一望できる / 新しい IPC 面 (socket・named pipe) を増やさない / `statfs` の TTL キャッシュという前例がある。
- **トレードオフ (承知の上)**: 状態取得は heartbeat 経由なので厳密同期ではなく「直近」値。低レイテンシの厳密 req-rep が要るワークロードが出てきたら、ローカルソケットを **後付けのハイブリッド**として足す余地は残す (今はやらない)。
- **不採用**: ローカルソケット単独 (ホスト単位でクラスタ横断が見えない・IPC 面増)。

### 決定 2: 着手順は **キャッシュ先行** (提案順どおり)
① → 土台 → ③(+②) → ④ → ⑤ の順。① は半独立で速度効果が早く、`data-write NOTIFY` の追加で ② の土台に自然合流する。詳細は下「Phase 構成」。

## Phase 構成 (build order)

| Phase | テーマ | 内容 | schema 影響 |
|---|---|---|---|
| **1** | ① キャッシュ | 1a InodeCache の容量/LRU 配線 ✅ → 1b read(content) キャッシュ + data-write NOTIFY ✅ → 1c read-ahead (未着手) → 1d write-back ✅ (実装完了・実機検証済) → **1e メタデータ write-back (ステージ 2 まで実装・未修正事項あり)** | なし (1b で NOTIFY payload 拡張のみ) |
| **2** | 土台 | NOTIFY 制御メッセージ (reload/ping) + `{prefix}mounts` 登録表 + Field の reload ポリシー | **あり** (`{prefix}mounts` 追加) |
| **3** ✅ | ③ + ② | 単一 `pgfsctl` の `config` サブコマンド (get/set/list/`--json`)。set 時に NOTIFY (`reload`/inline `set`) → ライブ反映成立。制御 LISTEN を notify_enabled から分離 (常時 ON)。**実装完了・実機 e2e 緑** | なし |
| **4** ✅ | ④ | 単一 `pgfsctl status [--json]`。**Layer 1 (稼働一覧) + Layer 2 (FS 統計) 実装完了・実機 e2e 緑** (DB 由来・read-only・mount 無改修)。**Layer 3 (稼働プロセスのキャッシュ統計 + 実効設定) も実装完了・実機 e2e 緑** (heartbeat スナップショット方式・4d-1〜4d-4) | なし (mounts.config/stats 列は既存) |
| **5** | ⑤ | GUI (③④ のラッパ)。**Avalonia (cross-platform desktop)・Core in-process 直呼び。5a 雛形 + 5b 読み取りダッシュボード MVP 実装完了 (ビルド緑・目視確認は手動)。残 5c config set / 5d 仕上げ** | なし |

---

## Phase 1 詳細: ① キャッシュ充足での高速化

既存の [performance.md](performance.md) が土台 (#2 InodeCache の lock 競合、#8 writeback_cache)。本節はそれを「速度向上」観点で再編。

| 小項目 | 内容 | ROI / リスク | performance.md |
|---|---|---|---|
| **1a** InodeCache の容量配線 | 現状 `cache_max_entries` は初期ヒントのみで退避なし → **本当に上限を効かせ LRU 退避**。同時に #2 の `lock(this)` → `ConcurrentDictionary` 化 (DB 取得はロック外) も検討 | 高 / 低 (ほぼバグ修正) | #2 |
| **1b** content (read) キャッシュ | `data_chunk` の読みをインメモリにページキャッシュ。**多クライアント整合のため invalidation が必須** → 「data-write NOTIFY」を追加 (現状データ write は未発火)。または inode に version/mtime 列で世代チェック | 高 / 中 | — |
| **1c** read-ahead | sequential read 検出で先読み | 中 / 中 | — |
| **1d** write-back | FUSE `writeback_cache` でカーネルに dirty 結合させ PG WRITE 回数を削減。**`pgfs_lock` 排他・cross-client 一貫性・クラッシュ時喪失範囲の検証が前提** → **✅ 実装完了・実機検証済 (アプリ層実装 — 下 §1d)** | 中〜高 / 高 | #8 |
| **1e** メタデータ write-back | create/属性/rename を pending として溜め、1 ファイル = 1 tx で flush。**ステージ 2 まで実装 + レビュー指摘 (A-1〜A-10 / B-1〜B-13) は 全件対応済** — 下 §1e | 高 / 高 | — |

実装順 (リスク昇順): **1a (配線) → 1b/1c (read 側) → 1d (write 側) → 1e (namespace 側)**。

### 1a / 1b (read 側のキャッシュ)

**→ [cache.md](cache.md) に分離した**。`InodeCache` の上限と LRU、content read キャッシュ、
data-write NOTIFY による cross-client invalidate、negative キャッシュ、1c read-ahead (未着手) はそちらが正。

### 1d (データ本体の write-back)

**→ [write-back.md](write-back.md) に分離した**。dirty チャンクの表現 / flush の粒度 /
`data_id` のブロック事前予約 / `mount.write_back` 系のノブ / 実測 (`dd bs=128k` で 6.1×) はそちらが正。

### 1e (メタデータの write-back)

**→ [metadata-write-back.md](metadata-write-back.md) に分離した**。pending-born 主義 / pending inode 台帳 /
同期化ヒューリスティック 3 つ / flush tx の不変条件 / ステージ 1・1.5・2 の as-built はそちらが正。

### 1e のレビュー記録 (ラウンド A / B)

**→ [metadata-write-back-reviews.md](metadata-write-back-reviews.md) に分離した**。
ラウンド A (A-1〜A-10) と ラウンド B (B-1〜B-13) の指摘と修正の as-built、**データ write-back の
ライブ無効化の二相化**はそちらが正。分離時点で 711 行あり、repo 内 2 位の大きさだった。

## Phase 2〜4 詳細: 土台 / config / status

**→ [control-plane.md](control-plane.md) に分離した**。`{prefix}mounts` 登録表 / 制御 NOTIFY /
Field の reload ポリシー / `pgfsctl config` / `status` (Layer 1〜3) の設計と as-built はそちらが正。

## Phase 5 詳細: ⑤ GUI

**→ [gui.md](gui.md) に分離した**。`config` / `status` を読む薄い運用フロント (Avalonia)。
技術選定 (P5-1〜P5-4) / 画面構成 / 5a・5b の as-built はそちらが正。

## 開いている設計判断 (次の議論ラウンド)

0. ~~**制御チャネルが `notify_enabled` ゲート**~~ → ✅ **決定 (P3-0)**: 制御 LISTEN は常時 ON、`notify_enabled` はデータ変更通知の送受信だけをゲート。単一クライアント mount にも `config set` が届く。Phase 3 詳細参照。
1. ~~**`config`/`status` を 1 個の `pgfsctl` か 2 exe か**~~ → ✅ **決定 (P3-1)**: 単一 `pgfsctl` (Core-only・サブコマンド方式)。`{役割}.pgfs` 命名規約を意図的に破る例外。Phase 3 詳細参照。
2. ~~**File 対象 (toml) 設定を `config set` でどう扱うか**~~ → ✅ **決定 (P3-2)**: File+Live は NOTIFY インライン同梱でエフェメラル live 反映 (DB 行を作らず remount 復活 footgun 無し)。File+NextMount は手元 toml 編集案内。Phase 3 詳細のマトリクス参照。
3. ~~**`{prefix}mounts` を誰が作るか**~~ → ✅ **決定 (Phase 2)**: mkfs が作成。既存 FS は再 mkfs (no --clean) で冪等・非破壊に追加、それまで mount は warning skip。
4. ~~**content キャッシュ (1b) の invalidation**~~ → ✅ **決定 (1b 実装済)**: data-write NOTIFY (`"d"`=data_id) で cross-client invalidate + **グローバル世代ガード**で stale read 競合を封じる。inode version/mtime 列は不採用。実機 e2e 緑。
5. ~~**write-back (1d)** を入れるか~~ → ✅ **決定 (採用)**: 生 SQL のベンチで **単一 PG 6.5× / Citus rf=2 8.1×** の投影が出た (主因はチャンク行の read-modify-write 増幅・[performance.md](performance.md))。dirty 表現 / flush 粒度 / `data_id` 予約まで確定済 = [§1d 確定設計](write-back.md)。**実装完了・実機検証済 (§1d 実装ステータス)**。
6. **heartbeat 間隔 / stale 判定閾値**。
7. ~~**GUI 技術選定** (Phase 5 で)~~ → ✅ **決定 (P5-1)**: Avalonia (cross-platform desktop)。MVP = 読み取りダッシュボード先行。Phase 5 詳細参照。
8. ~~**メタデータ write-back (1e)** の可視性・監査整合をどう扱うか~~ → ✅ **決定**: pending-born 主義 + close 契約の緩和 + 同期化ヒューリスティック 3 つ + 1 ファイル 1 tx。敵対的レビュー 2 レンズ (並行性 / データ安全・POSIX 契約・監査) の指摘を織り込んで確定 = [§1e 確定設計](metadata-write-back.md)。**ステージ 2 まで実装済み・レビュー指摘未修正**。現状は §1e の as-built / 敵対的レビュー結果を参照。
