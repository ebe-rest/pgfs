# キャッシュ — inode LRU / content read キャッシュ / read-ahead

> **道順**: [docs/README.ja.md](../README.ja.md) › [runtime-control-plane.ja.md](runtime-control-plane.ja.md) › **本書**
>
> **この doc が正である範囲**: 読み取り側のキャッシュ (Phase 1a / 1b / 1c) の設計・実装状況・変更記録。
> `InodeCache` の上限と LRU、content (本体) read キャッシュ、data-write NOTIFY による cross-client
> invalidate、negative キャッシュ、read-ahead (1c・未着手) はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [write-back.ja.md](write-back.ja.md) | **書き込み側**のキャッシュ (1d)。dirty チャンクと flush |
> | [metadata-write-back.ja.md](metadata-write-back.ja.md) | メタデータの遅延書き (1e)。pending inode 台帳 |
> | [control-plane.ja.md](control-plane.ja.md) | キャッシュ上限を実行時に変える経路 (`pgfsctl config`) と統計の出し方 (Layer 3) |
> | [performance.ja.md](performance.ja.md) | 実測と改善候補の全体。ここは設計、あちらは数字 |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | `mount.cache_*` の既定値と reload ポリシー |
> | [runtime-control-plane.ja.md](runtime-control-plane.ja.md) | 運用フェーズ全体の構成 (ハブ) |

## 設計

読み取り側のキャッシュ 2 本 (1a / 1b)。フェーズ全体の位置づけは
[runtime-control-plane.ja.md §Phase 1](runtime-control-plane.ja.md) の一覧表を参照。

### 1a 確定設計 — InodeCache の上限 / LRU 配線

**問題**: [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) は `byId`/`byPath`/`childrenByParent` の 3 辞書を `cache_max_entries` で初期化するが、これは `RichDictionary` (= `Dictionary` ラッパ) の**初期容量ヒントにすぎず退避が無い** → 明示 `Invalidate*` 以外で消えず**上限なく増える**。`cache_max_entries` が事実上効いていない。

**決定 (判断 1〜3)**:

1. **退避ポリシ = LRU**。既存 `Inode.CacheTime` (Get/Put 毎に `UpdateCacheTime()` 更新済) を recency に使う (新規状態を増やさない)。
2. **上限は `byId` を権威に 1 本**。`byId.Count > cache_max_entries` で LRU 退避し、退避後に `byPath`/`childrenByParent` から **byId に存在しない id を指すエントリを掃除**して整合させる (3 辞書を byId の 1 上限で間接 bound)。root は `CacheTime = DateTime.MaxValue` 固定なので**決して退避されない**。**root はパス解決の起点として別持ち**していて、`Invalidate(0)` / `InvalidateAll` で「読み直す」印が立ち、次の `GetRoot()` が DB から読み直して差し替える (v0.2.1。それまでは起動時に 1 回読んだきりで、**他クライアントの root の chmod / chown / mtime が再マウントまで見えなかった**。読み直せなければ古い root で続け、次の問い合わせでもう一度読む)。
3. **スレッド安全は現状維持** (`lock(this)` 配下で退避)。`lock(this)` + ロック内 DB クエリの解消 ([performance.ja.md #2](performance.ja.md)) は **別ステップ**に切る (1a に混ぜない)。

**退避機構 = overflow 時の batch**:
- トリガ: byId に書く 2 箇所 (`put` / `PutChildren`) の直後。
- `byId.Count > cap` のときだけ `CacheTime` 昇順に **low-water (`cap - cap/8`) まで一括退避**。通常時 (cap 以下) は即 return = 毎操作コストほぼゼロ、overflow 時のみ O(n log n) ソート。
- just-added 分は `put` 内で `UpdateCacheTime()` 済 = 最新なので自分は落とさない。`cache_max_entries <= 0` は退避無効 (= 従来どおり無制限) として扱う。

**不変**: 既存の `Invalidate*` / NOTIFY invalidate のセマンティクスは変えない。content (本体) キャッシュ = 1b は別レイヤ。

**実機検証 (2026-06-14, docker e2e)**: 既定 cap で **36/36** (回帰) + `CACHE_MAX_ENTRIES=8` で退避を全テスト中発火させて **36/36** (退避 + カスケード掃除が壊さないことを確認)。`tests/docker/run.sh` に `CACHE_MAX_ENTRIES` 注入口を追加。

**① と ② の接点**: 1b で足す「data-write NOTIFY」は、Phase 2 の制御メッセージと同じ NOTIFY 経路に乗る。さらに正しい invalidation が入れば、将来 `attr_timeout=0` ([Mount Program.cs](../../src/mount/src/Program.cs) でハードリンク nlink のため設定) を少し緩めて更に速くする発展もある。

### 1b 確定設計 — content (本体) read キャッシュ + data-write NOTIFY

**目的**: `data_chunk` の read を毎回 DB 往復させず、ホットなチャンクをインメモリ保持して read を加速する (1a のメタとは別レイヤ)。

**構造 — `ContentCache` (新規, Core.Api)**:
- キー `(data_id, chunk_index)` → **フル chunk payload** (`byte[]`)。ハードリンクは data_id 共有なので data_id 単位が正しい。
- **byte 予算 LRU**: 合計バイトが `mount.cache_data_max_bytes` (新 Field・既定 64MiB・`0` で無効) を超えたら `CacheTime` 昇順に low-water (予算の 7/8) まで一括退避 (1a と同じ batch 方式)。

**read path (`ReadData`)**: 各チャンクで `Get(data_id, idx)` → hit ならフルから slice をコピー / miss なら `ReadFullChunk` で**フル payload を読んで** `PutIfGeneration` → コピー (以後の部分 read も hit)。穴 (行なし / payload 短) は従来どおり 0 埋め。`cache_data_max_bytes=0` のときは従来の `substring` 部分 detoast 経路 (キャッシュなし) を維持。

**write/truncate/release = write-invalidate**: 操作後に `InvalidateData(data_id)` で該当 data_id のチャンクを全破棄 → 次の read が DB から読み直す。1b は read 加速が目的なので write-through merge はしない (1d で別途)。

**cross-client invalidation (data-write NOTIFY)**: 既存の `WriteData`/`TruncateData` の `Notify(...)` は **inode id しか運んでいない**。NotifyMessage に **`"d"` = data_ids** を追加し write/truncate で data_id も載せる。受信側 `OnRemoteChange` が `msg.DataIds` を `ContentCache.InvalidateData`。`notify_enabled=false` (単一client既定) では自 client の write-invalidate だけで整合 (他に書く者がいない)。

**stale read 競合の封じ (重要・グローバル世代カウンタ)**: read が古い chunk を読んでいる最中に他スレッド/他 client が write+invalidate すると、古い payload をキャッシュに焼く危険がある。`ContentCache.Generation` を read 開始時に捕捉 → `PutIfGeneration(..., gen)` は**世代が変わっていなければだけ採用**、`InvalidateData` は世代を `++`。これで「最後の write より古い payload がキャッシュに残る」状態は不可能 (= 永続 stale なし)。並行 write 中の単一 read が新旧混在するのは **page cache 同等**として許容 (実 FS も read() スナップショットは保証しない)。**O(1) 状態** (per-data_id 辞書不要)。

**新 Field**: `mount.cache_data_max_bytes` (LongField・SaveTo=File・AppliesTo=Mount|Assign・既定 64MiB)。[settings-matrix.ja.md](settings-matrix.ja.md) / [Mkfs.ja.md](../Mkfs.ja.md) に追記。

**不変**: `pgfs_lock` / tx 排他は不変。content キャッシュは DB の上の純キャッシュで、書き込み一貫性 (1d) には踏み込まない。

**実機検証 (2026-06-14, docker e2e)**: 既定 (64MiB) で **36/36** + `CACHE_DATA_MAX_BYTES=4096` (予算 < 1 chunk = 退避/世代 invalidate/write-invalidate を常時発火) で **36/36** + メタ/本体両極小 (`CACHE_MAX_ENTRIES=8` + `CACHE_DATA_MAX_BYTES=4096`) で **36/36**。`tests/docker/run.sh` に `CACHE_DATA_MAX_BYTES` 注入口を追加。

---


## 実装ステータス (as-built)

**1a / 1b はいずれも実装完了・実機 e2e 緑 (2026-06-14)**。1a = `InodeCache` の上限と LRU 配線、
1b = content read キャッシュ + data-write NOTIFY による cross-client invalidate (グローバル世代ガード付き)。
negative キャッシュはその後に追加され、回帰は [tests/linux/negcache.sh](../../tests/linux/negcache.sh) が持つ。

**1c (read-ahead) は未着手**。

> 個別の as-built がこの粒度に留まっているのは、1a / 1b が分割前の
> [runtime-control-plane.ja.md](runtime-control-plane.ja.md) でも確定設計しか持っていなかったためである
> (実装の詳細は当時 punch-list 側に書かれていた)。**追記するときはこの章に書く**。

## 変更記録

時系列の記録はここに追記する (設計と as-built は上の 2 章が正)。

- [runtime-control-plane.ja.md](runtime-control-plane.ja.md) が 1,802 行に肥大したため、
  機能ごとに分割してこの doc を切り出した。設計の内容は分割前のまま。
  §実装ステータス は分割時に新設した (分割前は独立した節を持っていなかった)。
