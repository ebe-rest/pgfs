# 性能改善候補

> **道順**: [docs/README.ja.md](../README.ja.md) › **本書**
>
> **この doc が正である範囲**: 性能の**実測値と改善候補**の正。測った数字・測定条件・そこから言えること
> (と、後で訂正された結論) と、**計測の作法** (デーモンの消滅まで待つ / 毎回 md5 で整合性を確認する /
> `xact_commit` は相対値としてのみ見る) はここに書く。機構そのものの設計は各 doc が正である。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [write-back.ja.md](write-back.ja.md) | データ write-back (1d) の**設計と実装ステータス**。ここは数字、あちらは機構 |
> | [metadata-write-back.ja.md](metadata-write-back.ja.md) | メタデータ write-back (1e) の確定設計・ステージ・不変条件 |
> | [metadata-write-back-reviews.ja.md](metadata-write-back-reviews.ja.md) | 1e のレビュー指摘 (ラウンド A / B-1〜) と修正の記録。A-10 / B-1 の**根拠**はそちら |
> | [cache.ja.md](cache.ja.md) | 読み取り側キャッシュ (inode LRU / content / negative) の設計 |
> | [support_for_citus.ja.md](support_for_citus.ja.md) | Citus 分散の設計と分散デッドロックの扱い |
> | [control-plane.ja.md](control-plane.ja.md) | 計測に使うノブの切り替え経路と統計の出し方 (`pgfsctl config` / `status`) |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | 本書で振ったノブ (`mount.*`) の既定値と reload ポリシー |
> | [../tests.ja.md](../tests.ja.md) | テストスイートの一覧・件数・実行環境の要件 (本書の計測は同じマウント手順を前提にする) |

FUSE / DokanNet コールバックは秒間 100〜10000 回呼ばれるホットパス。以下は調査済みで効果が見込める改善案。優先度順:

1. ~~**`UserResolver` の uid/gid キャッシュ**~~ ✅ **実装済み (確認済み)** — [src/fuse/src/UserResolver.cs](../../src/fuse/src/UserResolver.cs) は `unameToUid` / `gnameToGid` / `uidToUname` / `gidToGname` の 4 本の `ConcurrentDictionary` を `GetOrAdd` で持ち、`getpwnam` / `getgrnam` / `getpwuid` / `getgrgid` は **(name/id, 結果) ペアにつき 1 回だけ**呼ぶ (fallback 名は ctor で先行解決)。Assign 側 [WindowsUserResolver.cs](../../src/dokan/src/WindowsUserResolver.cs) も `unameToSid` / `gnameToSid` / `sidToUname` / `sidToGname` の 4 本でキャッシュ済み。**唯一の未キャッシュ syscall は `IsGroupSid` → `LookupAccountSid` だが、呼び出し元は [FileSystemUtils.ApplySecurity](../../src/dokan/src/FileSystemUtils.cs) (= SetFileSecurity = chmod/chown 時のみの cold パス) で、hot な `BuildSecurity` (GetFileSecurity) は呼ばない**ので追加キャッシュの ROI は低い。当初 doc の「`GetAttr` ごとに getpwnam を呼ぶ可能性」は実装で既に解消済みだった。
2. **`InodeCache` の `lock(this)` → `ConcurrentDictionary` 化** — [src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs) は単一 `lock(this)` で byId/byPath 両辞書を保護、しかも `lock` 内で DB クエリを実行しているケースがある。FUSE マルチスレッドで lock contention 必至。`ConcurrentDictionary` に置換し、DB 取得はロック外で実行する設計に変える。**ROI: 高 / 工数: 高（Lazy パターンで重複クエリ抑制が必要）**
3. **`PathParser` の `Lazy<T>` 5 連発を eager 化** — [src/core/src/Utility/PathParser.cs](../../src/core/src/Utility/PathParser.cs) で `FromPath` ごとに `Lazy<>` を 5 つ生成。ホットパス専用 eager コンストラクタを追加してパスあたりのアロケーションを削減。**ROI: 中 / 工数: 中**
4. **Dapper を生 `NpgsqlDataReader` に置換（ホット SELECT のみ）** — [src/core/src/Api/InodeCache.cs](../../src/core/src/Api/InodeCache.cs) の inode 取得や `Api.ListChildren` などのホットクエリだけ手書きリーダにすると、リフレクションマッピングを回避できる。**ROI: 高 / 工数: 高（保守性低下）**
5. **`Encoding.UTF8.GetString(path)` を `Span<byte>` のまま扱う** — [src/fuse/src/FileSystem.cs:75](../../src/fuse/src/FileSystem.cs#L75) `PathToString`。`InodeCache.byPath` のキーを `byte[]` ハッシュにすれば UTF-8 デコードを省略可。設計影響が大きいので最後。**ROI: 中 / 工数: 極高**
6. **`Inode.Children` (`FirstList<Inode>`) を削除または `List<T>` 化** — 実質未参照なのに毎 inode 生成時に `new FirstList<Inode>()` (1377 行クラス) が走る。`Inode` のコンストラクタコスト削減になる可能性。**ROI: 低 / 工数: 低**
7. **`Setting<T>.Value` getter の毎回デシリアライズキャッシュ** — 設定読み取りは起動時のみなのでホットパスではない。**ROI: 低 / 工数: 低**
8. **FUSE `writeback_cache` (`FUSE_CAP_WRITEBACK_CACHE`) の有効化** — 現状 pgfs は writeback_cache を有効化しておらず、FUSE 既定の write-through のまま動く。そのため **アプリの `write()` 1 回ごとに FUSE WRITE 要求 → [FileSystem.Write](../../src/fuse/src/FileSystem.cs#L565) → `Api.WriteData` で PG へ bytea チャンク同期書き込み** が発生し、小さい逐次 write が多いワークロードで PG ラウンドトリップが線形に増える。`-o writeback_cache` を立てると **カーネルのページキャッシュが dirty ページをバッファ/結合し、write-back スレッドがまとめて遅延フラッシュ**するため、デーモン/PG への WRITE 回数を削減できる (sync/async の「非同期化」を担うのは pgfs ではなくカーネルのこの機構)。**注意点 (要検証)**: (a) writeback_cache 有効時はカーネルが dirty 中の size/mtime を管理するため、`attr_timeout=0` (ハードリンク st_nlink 即時反映のため設定) との相互作用、(b) cross-client (notify) で他クライアントの書き込みが見えるまでの一貫性、(c) クラッシュ時の未フラッシュデータ喪失範囲の拡大、を確認する必要がある。**ROI: 中〜高 (write 多発時) / 工数: 中 (Pgfs.Fuse の binding の init callback で `fuse_config` または conn->want にフラグを立てる + 一貫性検証)**

ベンチマークなしで実装する場合は **1 → 6 → 3** の順がリスクが低い。2 と 4 は本格的にやるなら BenchmarkDotNet で計測した上で。8 は write 主体のワークロードがあるなら計測の価値あり (read 主体なら効果薄)。

## 実測: 598MB / 700 ファイルの `rsync -avh` (2026-07-25, dev サーバ)

同一ソース (pgfs リポジトリ自身) を同じコマンドでコピーした結果。単一 PG は localhost の PostgreSQL 17.5、Citus は同一 LAN の共有クラスタ (coordinator + worker 3 台 / shard 8)。

| 構成 | スループット | 所要 | 備考 |
|---|---|---|---|
| 単一 PG | **13.46 MB/s** | 44 秒 | timestamp UTC + 実占有バイト + ロック集約を入れた後。入れる前は 14.08 MB/s = **今回の機能追加のコストは測定ノイズ域** |
| Citus rf=1 | **3.11 MB/s** | 3m12s | coordinator の commit 数 = **34,517** (≒ 49 tx/ファイル) |
| Citus rf=2 | **2.27 MB/s** | 4m23s | rf=1 との差は 2PC 参加ノードが倍になる分 |
| Citus rf=2 (UPDATE router 化前) | 2.08 MB/s | 4m48s | + **分散デッドロック 2 件** ([support_for_citus.ja.md](support_for_citus.ja.md) 参照) |

読み出し側 (cold): 83MB のファイルを単一 PG から `md5sum` で **231 MB/s**、Citus rf=2 のツリー全体 (598MB) で **≈35 MB/s**。

### レイテンシの内訳 (どこで時間を使っているか)

> ⚠ **この小節の結論は後で訂正された**。ここで「主因 = worker の commit レイテンシ × 2PC 参加ノード数」と
> 結論したが、追測で **主因はチャンク行の read-modify-write 増幅** (commit 本数の寄与は 17%) と判明した。
> 下の「真の主因は 2PC ではなく…」を先に読むこと。以下の測定値そのものは有効
> (`dd bs=1M` は**増幅が起きない形**なので、増幅を含む rsync の遅さを説明できていなかった)。

`dd bs=1M count=20 conv=fsync` (= pgfs のチャンクサイズと同じ 1 MiB 単位の書き込み) を 3 回ずつ測った結果と、**pgfs を介さない生の SQL** との比較:

| 測定対象 | 単一 PG | Citus rf=2 | 倍率 |
|---|---|---|---|
| pgfs の 1 MiB チャンク書き込み (= 1 FS 操作 = 1 tx) | **15〜22 ms** (64 MB/s) | **276〜280 ms** (3.6 MB/s) | **≈17×** |
| 生の 1 MiB bytea INSERT (pgfs 抜き・psql から) | **1.8 ms** | **53 ms** | **≈29×** |
| ローカル commit 1 回 (`INSERT` × 50) | coordinator **0.07 ms** | worker **10 ms** | **≈150×** |

* **根っこは worker ノードの commit レイテンシ**。coordinator は 0.07 ms/commit なのに worker は **10 ms/commit** (`synchronous_commit=on` / `fsync=on` / `wal_sync_method=fdatasync` は全ノード同じなので、差はストレージ性能)。2PC は参加ノードごとに PREPARE と COMMIT PREPARED で 2 回 WAL を同期するので、worker 2 台なら **それだけで 40 ms** が下限になる。
* **pgfs 抜きでも 29 倍**なので、この差は pgfs の SQL の形ではなく**クラスタの分散書き込みコストそのもの**。pgfs はさらに 1 FS 操作あたり複数文 (lock 取得 / inode UPDATE / data 行 / チャンク UPSERT / 実占有バイト更新) を同じ tx に積むので、17 倍に収まっている。
* rf を下げると参加ノードが減るので効く (rf=1 で 3.11 MB/s vs rf=2 で 2.27 MB/s)。

### FUSE の `max_write` — 期待した効果は無かった (負の結果)

「libfuse の既定は 128 KiB だから 1 MiB に上げれば write tx が 1/8 になる」という仮説を立てて `fuse_conn_info.max_write` を設定できるようにしたが、**trace で実測したら既定でも 1 MiB の WRITE が来ていた** (`UPSERT data_chunk … len:1048576` × 20/20MiB。`--max-write 0` と `--max-write 1048576` で完全に同じ)。**libfuse3 はカーネル上限 (FUSE_MAX_PAGES = 1 MiB) までネゴシエートする**ので、仮説の前提が誤りだった。

* 実装は「明示的に固定 / 下げる」ためのノブとして残し、**既定は `0` = libfuse のネゴシエーション任せ (挙動不変)** にした。
* 副産物として分かったこと: **libfuse3 は `-o max_write=…` を受け付けない** (`fuse_new` が 0 を返してマウント失敗する)。設定するなら init コールバックで `fuse_conn_info` に書くしかない。
* 書き込み tx を減らす方向で残っているのは上記 8 の `writeback_cache` (カーネルが dirty ページを結合する) と Phase 1d の write-back キャッシュ。**こちらは「FUSE の 1 write = 1 tx」という構造自体を変えるので、まだ効く余地がある**。
* クラスタ側の手としては、テスト用途なら worker の `synchronous_commit` を `off` / `local` にすると 2PC の fsync が消えるので大きく変わるはず (未検証)。

### 真の主因は 2PC ではなく **チャンク行の read-modify-write 増幅** (追測で判明)

上の「レイテンシの内訳」を書いた時点では **主因を「1 FS 操作 = 1 分散トランザクション」= 2PC のコスト**と結論していた。
その後 **生 SQL で今の `WriteData` の形をそのまま再現して測り直したところ、主因は別物**だと分かった。
以下はすべて `xdata_ebe_db` (dev サーバ) 上の psql 実測、**1 MiB のファイルデータを書くのに要した時間**に正規化してある。

**なぜ 128 KiB 単位なのか**: rsync の実測は 598 MB で ≈4,700 チャンク tx = **1 回の FUSE WRITE ≈ 128 KiB**。
`dd bs=1M` は 1 MiB で来るが (max_write の項)、アプリの `write()` が 128 KiB ならそのまま 128 KiB で来る。
このとき pgfs は **1 MiB のチャンク行を 128 KiB ずつ 8 回 UPSERT で育てる** ([WriteChunkSlice](../../src/core/src/Api/Api.cs) の
`overlay` / 連結)。**bytea は 1 MiB = TOAST 対象なので、部分更新でも毎回 TOAST チェーン全体が書き換わる**。
= 1 MiB のファイルを書くのに平均 4.5 MiB 分の行書き換えが発生する。

| 形 (1 MiB のファイルデータを書く) | 単一 PG (非分散) | Citus rf=2 |
|---|---|---|
| **今の形**: 128 KiB × 8 回の partial UPSERT・各回別 tx | **48.9 ms** (20.4 MB/s) | **621.9 ms** (**1.61 MB/s**) |
| **write-back**: メモリで 1 MiB 組み立て → 1 文で書く・ファイル単位 tx | **7.5 ms** (133.7 MB/s) | **77.1 ms** (12.98 MB/s) |
| 倍率 | **6.5×** | **8.1×** |

* Citus 側の「今の形」は `{prefix}lock` の取得 (INSERT + `FOR UPDATE`) / `SELECT chunk_size` /
  `SELECT length(payload)` / チャンク UPSERT / `total_size` UPDATE / inode UPDATE を **実際の SQL のまま**
  並べたもの。出た **1.61 MB/s は rsync 実測の 1.62〜2.27 MB/s と一致する** → このモデルは実物を説明できている。
* **write-back は Citus 専用の対策ではない**。単一 PG でも 6.5 倍。増幅は分散とは無関係に TOAST の性質から来る。

内訳の切り分け (Citus rf=2 / 1 MiB あたり):

| 測定 | 時間 | 意味 |
|---|---|---|
| 同じ行に 128 KiB × 8 回 partial UPSERT・**1 tx** | 480.8 ms | **増幅のみ** (commit 1 回) |
| 8 行に分けて 128 KiB × 8 回・1 tx | 63.7 ms | **増幅なし** (同じ 1 MiB を書く) |
| 上記 8 回を各別 tx にした場合 | 581.7 ms | 増幅 + commit 8 回 |

* **増幅の寄与 = 480.8 / 63.7 ≈ 7.5×**。**commit 本数の寄与 = 581.7 − 480.8 ≈ 101 ms (12.6 ms/commit) = 全体の 17%**。
  → 「tx の本数を減らす」だけでは 17% しか取れない。**取り分の本体は「同じ行を何度も育てるのをやめる」こと**。
* 単発の文のコスト (既存 1 MiB 行に対して・Citus rf=2): **フル payload の SELECT = 0.4 ms** /
  **overlay で 128 KiB 差し替え = 24.8 ms** / **payload 全置換 1 MiB = 29.4 ms**。
  → **read はほぼ無料、書き込みは「回数」がコスト**。部分書き込みでも flush で 1 回にまとめれば overlay のままで十分安い
  (わざわざ read して全置換にする必要はない)。

### 2PC の参加者数はノード数で上限が決まる (= tx をまとめても増えない)

`citus.log_remote_commands` で `PREPARE TRANSACTION` を数えた結果:

| tx の形 | PREPARE 回数 |
|---|---|
| 単一 shard グループだけ触る tx (件数は 1 でも 6 でも同じ) | **2** (= rf=2 の placement = 2 ノード) |
| 8 shard に散る id を 6 件触る tx | **3** (= ワーカー全 3 台) |

* **参加者数 = 触ったワーカーノード数**で、shard 数や文の本数では増えない。ワーカー 3 台なら上限 3。
* よって **1 tx に仕事を詰めても 2PC の固定費はほぼ増えない**。実測でも
  「40 × 1 MiB を 1 tx」= 56.8 ms/MiB (8 shard に散る) vs 59.2 ms/MiB (単一 shard に揃える) で**差は測定限界以下**
  (PREPARE は 3→2 に減るが並列に走るので時間に出ない)。
* → **shard グループを揃える最適化 (例: 起点ディレクトリ単位でまとめる) は、このクラスタ規模では無意味**。
  `{prefix}inode` / `data` / `data_chunk` は同一コロケーショングループ (実測 `colocationid=10`・8 shard) なので
  「ディレクトリの inode shard に data_id を合わせる」ことは**技術的には可能**
  (`get_shard_id_for_distribution_column` で写像が引ける)。**ワーカーが増えて PREPARE 数が参加ノード数に比例して
  増えるようになったら再評価する**価値がある — が、その場合ディレクトリ単位にデータが偏る副作用を伴う。
* ファイル横断で 1 tx にまとめる効果も **+8.8% だけ** (ファイル単位 63.6 ms/MiB → 全ファイル 1 tx 58.0 ms/MiB)。

### 分かったこと

* **UPDATE の router 化は「デッドロックの構造的解消」には効いたが、スループットには +9% しか効かない**。
* **主因はチャンク行の read-modify-write 増幅** (上記)。**2PC / commit 本数の寄与は 17%** で、当初の結論
  「主因は 1 FS 操作 = 1 分散トランザクション」は**過大評価だった**。Citus が単一 PG より遅いのは事実
  (worker の commit 10 ms / 1 MiB 転送 × placement 数) だが、**増幅を止めれば単一 PG も Citus も同じ倍率で速くなる**。
* **書き込み粒度を上げる案は空振りだった** (下記「`max_write`」参照。libfuse3 が既に 1 MiB までネゴシエートしていた)。
  ただし**空振りの理由も上と整合する**: `dd` は元から 1 MiB で来ていた = 増幅が起きていなかったので速かった。
  rsync が遅いのは 128 KiB で来て増幅していたから。
* 次に効くのは **Phase 1d の write-back キャッシュ** ([write-back.ja.md](write-back.ja.md))。
  投影値は上表の通り **単一 PG 6.5× / Citus rf=2 8.1×**。上記 8 の `writeback_cache` (カーネル側) は
  「小さい write を結合する」別の軸で、pgfs 側 write-back と併用できる。
* rsync のような**メタデータ主体のワークロードは実行ごとのばらつきが大きい** (同一構成で 2.27 / 1.62 MB/s)。比較するなら `dd` の 1 MiB 書き込みのような単一操作のレイテンシで見るほうが再現性が高い。
  **ただし `dd bs=1M` は増幅が起きない形なので、write-back の効果測定には向かない** —
  `dd bs=128k` か rsync のような「チャンク未満の write が続く」形で測ること。

## 実測: メタデータ write-back (1e) の投影ベンチ (2026-08-10, dev サーバ)

設計 ([metadata-write-back.ja.md §1e](metadata-write-back.ja.md)) の**実装前ゲート**。pgbench (-c 1 = rsync 相当の単一クライアント・各 120〜200 ファイル × 2 周) で 2 つの形を生 SQL で再現した:

* **今の形** = write_back(1d)=on・メタデータ write-through。1 ファイル = **5 tx** (create / close-flush / chmod / utimens / rename 同一親)。`{prefix}lock` の INSERT + `SELECT FOR UPDATE` も実装どおり再現。
* **1e の形** = coalesce 済みの最終状態を **1 tx** (lock ×3 + ancestor 生存確認 + inode INSERT (最終名・最終属性) + data INSERT + chunk INSERT)。

| シナリオ | 今の形 | 1e の形 | 倍率 |
|---|---|---|---|
| **Citus rf=2・4 KB/ファイル** | 28.8 ms/file | **7.8 ms/file** | **3.7×** |
| Citus rf=2・768 KB/ファイル | 74.9 ms/file | 61.9 ms/file | 1.21× |
| 単一 PG (localhost)・4 KB | 2.0 ms/file | 0.74 ms/file | 2.7× (絶対差 1.3 ms) |

実マウントのアンカー (4 KB × 300 ファイルを `rsync -a`・write_back=on・Citus rf=2): **39.3 ms/file**。
モデルの 28.8 ms との差 ≈ **10.5 ms/file が FUSE / mount プロセス / rsync 側の固定費**。

### 分かったこと (1e のゲート判定材料)

1. **1e の取り分は「小ファイル × Citus (worker への RTT がある構成)」に集中する**。メタデータ短縮の絶対値は **≈21 ms/file** (Citus rf=2) で:
   * 4 KB ファイル: SQL 側 3.7× → 実世界投影 39.3 → ≈18 ms/file = **≈2.1×**
   * 768 KB ファイル: データ書き込みが支配的で **1.21×**
   * 参照ワークロード (599 MB / 745 files mixed・rsync 179 s) への投影: 21 ms × 745 ≈ 15.6 s 短縮 = **≈1.1×**
2. **単一ローカル PG では wall-clock にほぼ出ない** (絶対差 1.3 ms/file に対し FUSE 側固定費 10.5 ms/file)。**1d と違い、1e は「Citus / 高レイテンシ DB 向け」の対策** — 1d は増幅除去なので単一 PG にも 6.5× 効いたが、1e は tx 往復の削減なので DB が近いと効かない。
3. **「rsync の残りはメタデータ tx が支配的」という 推測は過大だった**。実際は ≈2,100 tx × ~5.8 ms ≈ 12 s (179 s 中)。残りはデータ flush (≈46 s 推定) と FUSE / クライアント側固定費。
4. 1e を実装しても小ファイルの下限は **FUSE 側固定費 (≈10.5 ms/file) が決める**。次に効くのはそちらの内訳調査 (lookup / getattr / syscall 往復)。
5. 副次実測: 実マウント経由の `rm -rf` (unlink write-through) ≈ **9 ms/file**。

ベンチの再現手順: bench スクリプトは使い捨て (`pgfs_test` に `created_by='bench'` で流して削除、非分散比較は `pgfs_bench` スキーマを作って drop)。ペイロードは md5 連結のランダム bytea (圧縮で不当に速くならないように)。

## 実測: ボトルネック切り分け (2026-08-10, dev サーバ・Citus rf=2・write_back=on)

1e 投影ベンチで残った疑問「参照 rsync の時間は結局どこに行っているのか」を、**`pg_stat_database` の
`active_time` / `xact_commit` デルタ** (pg_stat_statements は preload されておらず使えない) + trace ログの
SQL census + ワークロード分離で切り分けた。

| 実験 | WALL | SQL (active_time) | commits |
|---|---|---|---|
| pgfs リポ (841 files / 600 MB) を rsync | 103.1 s | **98.5 s (96%)** | 24,271 (**28.9/file**) |
| 同一内容を再 rsync (no-op・キャッシュ温) | 0.7 s | ≈0 | 2 |
| 1 GiB ゼロファイル (`--sparse` なし) | 104.1 s | 100.8 s | 2,170 |
| 1 GiB ゼロファイル (`--sparse` あり) | **1.9 s** | 0.1 s | 30 |
| 64 MB 単発 | 6.5 s | 6.2 s | 80 (≈1.2/MiB) |
| 4 KB × 10 (背景 flush 完了まで待って計測) | 0.5 s | ≈0.4 s | 253 (**25.3/file**) |

### 分かったこと

1. **ボトルネックは DB 側で壁時計の 96%**。前節で「FUSE / クライアント側固定費 ≈10.5 ms/file」と
   推定した分も、実体の大半は **autocommit の SELECT 群 (= DB 往復)** だった (誤帰属を訂正)。
2. **小ファイル 1 個 = 実測 ≈25 tx** (設計モデルの 5 tx の 5 倍)。判明分の内訳: **negative lookup
   (parent_id, name) SELECT ≈6.4** (rsync の lstat / create 前後 / rename 先チェック。**ENOENT は
   キャッシュされない**ため毎回 DB に行く) + メタデータ write 4 + flush 1。**残り ≈14 tx/file が未特定** —
   特定には pg_stat_statements の preload が必要 (`shared_preload_libraries` は現在 citus のみ。共用 DB
   なので管理者への依頼事項)。候補: getattr の実占有 SUM / `attr_timeout=0` による再取得 / rename 前後の再 lookup。
3. **大ファイルは ≈100 ms/MiB (スループット上限 ≈10 MB/s)・≈1.2 tx/MiB**。64 MB 単発が 80 tx に
   なるのは `write_back_max_bytes` (既定 64 MiB) と同サイズで **back-pressure が細切れ flush を
   連発する**ため (設計どおりの挙動だが、粒度は要再考)。ゼロ埋めデータでも 100 ms/MiB のまま =
   TOAST 圧縮の恩恵はほぼ無い。
4. **スパースファイルは `--sparse` の有無で 104 s ↔ 1.9 s**。参照ワークロード (179 s) に 1 GiB スパースが
   同居していたので、`--sparse` なしで転送していたなら過大計上の主因はこれ。
5. **温まった読み取り側はゼロコスト** (no-op rsync 0.7 s / SQL 0)。InodeCache + カーネルキャッシュが効いている。

### 次に効く順 (1e 本体より安い可能性が高い)

1. **negative lookup キャッシュ** — ENOENT を短 TTL (+ NOTIFY invalidate) でキャッシュ。≈6.4 tx/file を
   ほぼ消せる。実装は InodeCache に「不存在マーカー」を足すだけで、1e の台帳より桁違いに小さい。
2. **未特定 ≈14 tx/file の特定** — pg_stat_statements の preload を管理者に依頼するのが最短。
   それまでは trace ログの Pg 層ロギングを autocommit SELECT 全種に広げる手もある。
3. **大ファイルの flush 粒度** — back-pressure の「1 チャンクずつ」を「まとまった塊」に。
   `write_back_max_bytes` の既定引き上げ / flush 単位のバッチ化。
4. **1e (メタデータ write-back)** — 消せるのは 25 tx 中 4 (メタ write ≈21 ms/file)。上 1〜3 を先に
   消した後なら相対効果は上がる (床が下がるため)。

計測ノート: `pg_stat_database` のデルタは接続中の別セッションのノイズを拾い得る (個人 DB なので今回は
無視できる規模)。統計反映にラグがあるため **rsync 終了後 3〜4 s 待ってからスナップショット**を取ること
(待たないと背景 flush と統計が落ちて 1/6 くらいに見える — 実際に踏んだ)。

## 実測: negative lookup キャッシュ (2026-08-10 実装) + tx 計上の訂正

### 訂正 — 「未特定 ≈14 tx/file」の大半は隠れクエリではなく 2PC の計上分だった

前節で `xact_commit` ベースに「小ファイル 1 個 ≈25 tx (未特定 ≈14)」と書いたが、生 SQL の対照実験
(chmod 相当の 1 tx を psql で発行 → `xact_commit` は **+2**) で、**Citus では書き込み tx 1 本が
coordinator の統計上 ≈2 commits に計上される** (2PC 参加バックエンド / citus_internal の COMMIT PREPARED)
ことが分かった。数え直すと:

- **クライアントから見た実 tx ≈ 11〜12/file** = negative lookup ≈6.4 + メタデータ write 4 + flush 1
  (trace ログの census とも一致)。
- `xact_commit` の 25〜30/file は「実 11〜12 × 書き込み系の 2 倍計上 + Citus メンテナンスデーモン等のノイズ」。
- **隠れた大物クエリは無かった** (`GetOccupiedBytes` は inode 単位でキャッシュ済み等は確認済み)。
- 教訓: **`xact_commit` は Citus 上では per-op の精密な census に使えない** (傾向把握用)。
  精密化するなら pg_stat_statements の preload (管理者依頼) が必要。

### negative lookup キャッシュ (`mount.negative_cache_ttl_ms`) — 実装と実測

🟣 #19 ① を実装した (既定 **0 = 無効**・opt-in)。ENOENT を「path → (親 id, 期限)」で InodeCache に記録し、
TTL 内の再 lookup は DB に行かない。無効化は 3 系統: **自クライアントの create/rename/削除**
(`Put` / `InvalidateChildren` — マーカーの記録が lookup の SELECT と同一 lock 区間にあるため、
「SELECT 後・記録前に create が割り込んで stale ENOENT が残る」レースは構造的に起きない) /
**readdir** (`PutChildren` = DB の最新一覧が正) / **リモート通知・live reload** (TTL 0 で全クリア)。

実測 (4 KB × 300 の `rsync -a`・Citus rf=2・write_back=on):

| | TTL=0 (無効) | TTL=3000 | 差 |
|---|---|---|---|
| WALL | 15.9 s (53 ms/file) | 14.7 s (49 ms/file) | **1.08×** |
| commits | 30.1/file | 26.9/file | **−3.2 tx/file** |

* 消えるのは **同名パスへの 2 回目以降の negative lookup ≈3/file** (kernel lookup → FUSE Create の存在確認、
  rsync の lstat → rename 先チェック等の繰り返し)。**初回の ENOENT 確認 ≈3.4/file は原理的に消せない**
  (本当に存在しないことを 1 度は DB に聞くしかない)。
* 伸びが小さいのは lookup が軽い read (router SELECT ≈1.3 ms) だから。**worker への RTT が大きい構成ほど効く**。
* テストは [tests/linux/negcache.sh](../../tests/linux/negcache.sh) 7/7 PASS (可視性契約 + live reload)。
  e2e (既定 off) は 43/44 — FAIL 1 は既知の 40P01 フレーク ([tests.ja.md §既知のフレーク](../tests.ja.md)) で
  3 周中 1 回の再現率も既知の 15〜20%/周と整合、本変更とは無関係 (既定 off なので経路同一)。

### 改めて「次に効く順」

1. ~~negative lookup キャッシュ~~ → ✅ 実装済み (上記。取り分 ≈3 tx/file・1.08×)
2. **1e (メタデータ write-back)** — 実 11〜12 tx/file のうち **メタ write 4 + flush 1 を 1 tx に畳む**。
   negative cache 適用後の残り実 tx の過半がここ。tx 計上の訂正により、**1e の相対的な価値は投影時の
   見立てより上がった** (実 tx の内訳が「lookup 6.4 + write 5」と判明したため)
3. 初回 ENOENT 確認 (≈3.4/file) は削れない床。大ファイルは別軸 (≈100 ms/MiB / back-pressure 粒度)

---

## 実測: 1e ステージ 2 の効果 (2026-08-12, dev サーバ・Citus rf=2 / shard 8)

ステージ 2 (close-no-flush + 同期化ヒューリスティック 3 つ + エラーの底) を入れた状態で
**メタデータ write-back の取り分が実際に出るか**を測った。結論は **「代表的な bulk copy には効かない」**。

### 計測の前提 (ここを外すと数字が嘘になる)

* **デーモンの drain を必ず待つ**。`fusermount3 -u` は**カーネル側の unmount が終われば即返る**が、
  `mount.pgfs` はそのあとに残り pending を flush して終了する。**プロセスの消滅まで待たないと
  write-back の仕事を測り落として不当に速く見える** (最初の計測でこれを踏み、数字を訂正した)。
  * **待つ相手を間違えないこと**。起動時に stderr へ出る `started (pid N)` は
    **フォーク前の親プロセスの pid** で、マウント成立後すぐに終了する (2026-09-19 実測)。
    これを待つと待ち時間ゼロになり、**drain 中の DB を読んで「ファイルが消えた」ように見える**
    (B-1 の計測で実際に踏み、`md5=NG` と件数不足を データ損失と誤認しかけた)。
    `pgrep -x mount.pgfs` のように**プロセス名**で実デーモンの消滅を待つ
    (`pgrep -f` は計測スクリプトを書き出した親シェルのコマンドラインにも当たる)。
* **整合性を毎回確認する**。マウントを張り替えて (= キャッシュ空・DB から読み直し) 送り元と md5 集合を
  照合する。「速いが壊れている」を数字で弾けないと計測の意味がない (全ケースで 300 ファイル一致)。
* `xact_commit` は共用 DB 全体のカウンタなので相対値の目安としてのみ見る。

### 結果 (4 KB × 300 ファイル・**前景 + drain の合計**)

| ワークロード | metadata off | metadata **on** | 倍率 |
|---|---|---|---|
| `rsync -a` | 11.8 s (39.2 ms/file) | 11.8 s (39.3 ms/file) | **1.00×** |
| `open(O_CREAT)` + write + close (O_EXCL なし) | 4.84 s (16.1 ms/file) | 3.72 s (12.4 ms/file) | **1.30×** |

* **rsync はまったく速くならない** (原因は下記)。
* O_EXCL を使わない create でも **総合 1.30×** にとどまる。**前景は 13× 速くなる** (4.29 s → 0.32 s) が、
  **仕事が drain に移るだけ**で総量は変わらない。
* drain の 3.4 s ÷ 300 = **11.3 ms/file ≒ Citus の 1 tx 分**。つまり
  **メタデータの畳み込み自体は設計どおり動いている** (複数 tx → 1 tx)。

### なぜ rsync に効かないか — **`rsync` も `cp` も `O_CREAT|O_EXCL` を使う**

`strace` で実測 (2026-08-12):

```
openat(AT_FDCWD, ".f1.awJpho", O_RDWR|O_CREAT|O_EXCL, 0600)          = 1   # rsync の temp
rename(".f1.awJpho", "f1")                                           = 0
openat(AT_FDCWD, "/home/user/mnt/pgfs/dst/f1", O_WRONLY|O_CREAT|O_EXCL, 0644) = 4   # cp の宛先
```

ステージ 2 の**同期化ヒューリスティック (c) は `O_EXCL` create を write-through にする** (§1e)。
したがって **rsync / cp が作るファイルは 1 つも pending にならず**、後続の `chmod` / `utimens` /
`rename` も persisted inode への操作なので write-through のままで、**畳み込みが 1 回も起きない**。

* **設計の §狙い (rsync の create→write→close→chmod→utimens→rename を 1 tx に畳む) と
  ヒューリスティック (c) は両立しない**。投影ベンチは生 SQL で tx 列を再現したものなので
  **syscall のフラグを見ておらず、この矛盾を検出できなかった**。
* A/B 確認: `O_EXCL` 付き create は close 直後に DB 行あり (write-through) / `O_EXCL` なしは行なし
  (pending)。`pgfsctl status` でも 99 ファイルの rsync で `metadata flushes = 1` / `data flushes = 99`。

### B-1 `defer` の実測 (2026-09-19・**ノブ実装後**)

`mount.write_back_metadata_exclusive_create = defer` で `rsync` を測り直した。
上の「計測の前提」どおり **デーモンの消滅まで待った合計**で、毎回 md5 を照合している。

環境: dev サーバ (`xdata_ebe_db` / schema `pgfs_test` / **Citus rf=2 / shard 8**)。
ワークロード: **4 KB × 500 ファイルを `rsync -a`**。3 回ずつ実行。

| 設定 | 前景 (rsync の戻り) | **総計 (drain 込)** | ms/file | 倍率 |
|---|---|---|---|---|
| `write_through` (既定) | 18.49 / 18.29 / 18.55 s | **18.62 / 18.42 / 18.57 s** | 37.1 | 1.00× |
| **`defer`** | 1.24 / 1.24 / 1.23 s | **5.66 / 5.79 / 5.67 s** | 11.3 | **3.28×** |

全 6 回とも 500 ファイル・md5 一致。

* **投影 (39 → ≈12 ms/file = 約 3×) は当たった**。実測 37.1 → 11.3 ms/file = **3.28×**。
  drain 後の 11.3 ms/file は「Citus の 1 tx 分」とほぼ一致し、**1 ファイル = 1 tx に畳めている**ことを裏付ける。
* **前景だけ見ると 14.9× だが、この数字を使ってはいけない**。仕事が drain に移るだけで、
  総計で見ないと嘘になる (ステージ 2 の計測で同じ罠を踏んでいる)。
* この 3.28× は **cross-client の `O_EXCL` 排他を失うことの対価**である。既定を変えない理由は
  [metadata-write-back-reviews.ja.md §B-1 の確定設計](metadata-write-back-reviews.ja.md) の「位置づけ」を参照。

### この計測から言えること

1. **1e は「ライフサイクル全体を遅延できるファイル」を ≈12 ms/file にする**。今回のワークロードが
   1.3× に留まったのは create + write だけで**畳み込む相手が少ない**から。
2. **取り分が最大なのは close 後に write-through のメタデータ操作が多いワークロード** = まさに rsync
   (現状 39 ms/file ≒ 同期 5 tx)。`O_EXCL` を deferrable にできれば **39 → ≈12 ms/file = 約 3×** が
   見込める (投影) → **2026-09-19 に実測し 3.28× を確認した** (下 §B-1 `defer` の実測)。
3. **既定 off を維持する根拠はむしろ強まった**: 既定構成では rsync/cp に 0% / その他の create に 1.3% で、
   失う耐久性契約 ([Mount.ja.md §write-back](../Mount.ja.md)) に見合わない。

## 実測: A-10 (persisted inode への上書きを同期 close に戻したこと) の影響 (2026-09-19, dev サーバ)

ラウンド A の **A-10** は「既に DB にある実体 (persisted) への write が始まったら同期 close 印を付ける」=
close-no-flush を **pending-born 限定**にした修正である ([metadata-write-back-reviews.ja.md §ラウンド A の修正](metadata-write-back-reviews.ja.md))。
狙いは「既存ファイルの上書きが interval ごとに別 tx で部分 commit され、クラッシュすると
**前半が新・後半が旧のキメラ**が残る」窓を塞ぐこと。**性能影響が未実測のまま残っていた**ので測った。

環境: dev サーバ (`xdata_ebe_db` / schema `pgfs_test` / **Citus rf=2 / shard 8**)。
上の **§計測の前提 (ここを外すと数字が嘘になる)** どおり **デーモンの消滅まで待った総計**で、
毎回マウントを張り替えて (= キャッシュ空・DB から読み直し) 中身を照合している (全 run 整合 OK)。

A/B は **A-10 の 1 行 (`WriteDataBuffered` の `MarkSyncOnClose`) を落としたビルド**との比較。
比較対象として **metadata write-back off (= 1d のみ・既定に近い構成)** も測った。

### A-10 が効く範囲は「truncate しない上書き」だけ

同期化ヒューリスティック **(b)** が `truncate` / `O_TRUNC` の close を**元から**同期に格上げしている
(`Api.cs:2771`)。したがって `cat src > f` のような **`O_TRUNC` 付きの上書きは A-10 の前から同期**であり、
A-10 が挙動を変えるのは **`O_TRUNC` も `O_CREAT` も付けない純粋な上書き** (`open(f, "r+b")` /
`dd conv=notrunc` / DB ファイル / mmap 書き戻しなど) に限られる。以下のワークロードはその形で書いている。
**bulk copy (create 主体) は pending-born なので A-10 の影響を受けない。**

### ワークロード A: 別々の 300 ファイルを 1 回ずつ上書き (4 KB)

| 設定 | 前景 | **総計 (drain 込)** | ms/file |
|---|---|---|---|
| metadata off (1d のみ) | 7.26 / 6.87 s | **7.34 / 6.89 s** | 24.5 / 23.0 |
| metadata on・**A-10 無し** | 0.63 / 0.65 / 0.64 s | **7.02 / 7.24 / 6.96 s** | 23.4 / 24.1 / 23.2 |
| metadata on・**A-10 あり (現行)** | 7.31 / 6.41 / 5.96 s | **7.32 / 6.42 / 5.97 s** | 24.4 / 21.4 / 19.9 |

**総計は 3 条件とも 19.9〜24.5 ms/file のばらつきの中に収まり、A-10 のコストは測定限界以下**。
前景だけ見ると 0.64 → 6.6 s = **10× 遅い**が、これは**仕事が drain から前景に戻っただけ**である
(ステージ 2 / B-1 の計測と同じ罠なので、前景の数字を単独で使わないこと)。

### ワークロード B: 1 つの persisted ファイルを 300 回 open/write/close (4 KB ずつ位置をずらす)

A-10 の**最悪ケース**。close のたびに同期 flush が走るので、coalesce の相手が最も多い形。

| 設定 | 前景 | **総計 (drain 込)** | ms/close |
|---|---|---|---|
| metadata off (1d のみ) | 33.77 / 33.81 s | **33.85 / 33.88 s** | 112.8 / 112.9 |
| metadata on・**A-10 無し** | 0.07 s ×3 | **0.27 s ×3** | 0.9 |
| metadata on・**A-10 あり (現行)** | 33.83 / 34.48 / 33.55 s | **33.84 / 34.56 / 33.57 s** | 112.8 / 115.2 / 111.9 |

**A-10 無しとの比は 125×**。ただし **A-10 あり = metadata off の既定と同じ値** (112.8 対 112.8) であり、
**A-10 は「ステージ 2 が入れた安全でない高速化を取り消して 1d の挙動に戻した」だけで、既定より遅くはしていない**。

1 close あたりの 113 ms は tx のオーバーヘッドではなく、**育っていくチャンクの全置換 (read-modify-write 増幅)**
が主である (このワークロードはファイルが 1.2 MiB まで育つので、chunk 0 が埋まった後は毎回 1 MiB 前後を
書き直す。上の **§真の主因は 2PC ではなく チャンク行の read-modify-write 増幅** と整合)。
**「A-10 が 113 ms を作っている」のではなく、A-10 がそれを毎 close 払わせている**。

### この計測から言えること

1. **A-10 の代償は「1 ファイルを何度も開き直して上書きするワークロード」に集中する**。
   1 回ずつの上書き (ワークロード A) では総計に差が出ない。
2. **その代償を払っても既定 (metadata off) より遅くはならない**。A-10 無しの 0.9 ms/close は
   キメラを許容して初めて出る数字なので、**比較の基準にしてはいけない**。
3. 速くしたいならこの経路は **`fsync` を挟まないまま close を繰り返す形をやめる**か、
   back-pressure の flush 粒度 (🟣 #19 ③) 側で削るのが筋で、A-10 を戻す話ではない。

---

## 実測: handle-context 段階 B の基線 (2026-09-20, dev サーバ・**単一 PG**)

[handle-context.ja.md §段階 B の受入条件と API 面 ④](handle-context.ja.md) のゲート用。段階 B は
`Read` / `Write` の識別を**パス起点から `InodeId` 起点へ反転**させるので、**回帰が無いことを示す**
ために段階 A の状態で基線を取った。**段階 B 実装後に同じスクリプトで取り直して比較する。**

### なぜ単一 PG で測るか (Citus ではなく)

測りたいのは**クライアント側の解決コスト** (`byPath` → `byId` の辞書 2 回 + `PathToString` の
UTF-8 デコードが、`byId` の 1 回に置き換わる分) である。Citus では 1 チャンク書き込みが
**単一 PG の ≈17×** かかる (上の §レイテンシの内訳) ため、**クライアント側の差が DB のレイテンシに
完全に埋もれる**。非分散スキーマ `pgfs_bench` を作って測り、計測後に drop した。

### 基線 (段階 A・5 回・**前景 + drain の合計**)

| ワークロード | 中央値 | 最小 | 最大 | 振れ幅 (max−min)/中央値 |
|---|---|---|---|---|
| W1 `rsync -a` 4 KB × 300 | **2.12 s** (7.1 ms/file) | 1.91 s | 2.68 s | **36.3%** |
| W2 cold read 4 KB × 300 (`md5sum`) | **0.29 s** | 0.28 s | 0.30 s | 6.9% |
| W3 write 64 MiB `dd bs=1M conv=fsync` | **0.88 s** (73 MB/s) | 0.86 s | 0.90 s | 4.5% |
| W4 cold read 64 MiB `dd bs=128k` | **0.23 s** (278 MB/s) | 0.23 s | 0.24 s | 4.3% |

全 20 回とも **300 ファイル / md5 一致** (W3/W4 は 64 MiB の md5 一致)。整合性の確認は毎回
**マウントを張り替えたあと** (= キャッシュ空・DB から読み直し) に行っている。

### tx 基線 (W1 の `xact_commit` デルタ・3 回)

| | 合計 | /file |
|---|---|---|
| W1 `rsync -a` 4 KB × 300 | 9673 / 9644 / 9645 | **32.2 / 32.1 / 32.1** |

**これが段階 B でいちばん効く指標である。** 振れ幅 0.3% と時間計測より 2 桁鋭く、しかも
「DB 往復が増えたか」を直接見る。時間が誤差に埋もれても、**tx/file が動けば必ず気づける**。
(`xact_commit` は共用 DB 全体のカウンタなので相対値としてのみ見ること。)

### 検出力 — この基線で何が言えて何が言えないか

- **言える**: W3 / W4 / tx/file で **5% を超える回帰**。W3/W4 は振れ幅 4〜5%、tx/file は 0.3%。
- **言えない**: W1 の **20% 未満の変化**。共用サーバでの 300 ファイル rsync は振れ幅 36% で、
  この形のままでは基線として粗い。W1 で差を主張するなら回数を増やすこと。
- **そもそも時間で差が出る見込みは薄い**。削れるのは 1 コールバックあたり
  **辞書 1 回 + UTF-8 デコード 1 回**で、チャンク I/O (ms 単位) に対して桁が違う。
  **だから「速くなった」ではなく「遅くなっていない」を示す計測**であり、
  主たる判定は上の tx/file に置く。

再現: 使い捨てスクリプト (`pgfs_bench` スキーマを作って mkfs → 4 KB × 300 と 64 MiB を
`/dev/urandom` から生成 → W1〜W4 → drop)。`fusermount3 -u` のあと **`pgrep -x mount.pgfs` で
実デーモンの消滅を待つ** (§計測の前提)。

### 結果: 段階 B (FUSE アダプタ反転後) — **回帰なし**

同じスクリプト・同じ手順で取り直した。

| ワークロード | 段階 A | **段階 B** | 差 |
|---|---|---|---|
| W1 `rsync -a` 4 KB × 300 | 2.12 s | **1.95 s** | 振れ幅 (36%) の内側。**差を主張しない** |
| W2 cold read 4 KB × 300 | 0.29 s | **0.28 s** | 同上 |
| W3 write 64 MiB `bs=1M` | 0.88 s | **0.87 s** | 同上 |
| W4 cold read 64 MiB `bs=128k` | 0.23 s | **0.23 s** | 変化なし |
| **W1 の `xact_commit`** | 32.2 / 32.1 / 32.1 | **32.1 / 32.2 / 32.1** | **同一** |

全 20 回とも張り替え後の md5 一致。

**ゲートは通った**。判定に置いた **tx/file が動いていない** = **DB 往復は増えていない**。
これは §段階 B の性能ゲート の静的確認 (`byPath` に当たるなら `byId` にも必ず当たる) と整合する。

**時間の改善は主張しない**。W1 は 2.12 → 1.95 s と縮んでいるが、W1 の振れ幅は 36% あり
**この差はノイズと区別できない**。予告どおり「速くなった」ではなく**「遅くなっていない」**が結論である。
