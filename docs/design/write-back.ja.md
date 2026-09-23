# write-back — データ本体の遅延書き (Phase 1d)

> **道順**: [docs/README.ja.md](../README.ja.md) › [runtime-control-plane.ja.md](runtime-control-plane.ja.md) › **本書**
>
> **この doc が正である範囲**: データ本体 (`{prefix}data_chunk`) の write-back キャッシュの設計・
> 実装状況・変更記録。dirty チャンクの表現、flush の粒度、`data_id` のブロック事前予約、
> `mount.write_back` 系のノブ、ライブ無効化の二相化はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [metadata-write-back.ja.md](metadata-write-back.ja.md) | **メタデータ**の遅延書き (1e)。pending inode 台帳と名前空間 |
> | [cache.ja.md](cache.ja.md) | **読み取り側**のキャッシュ (1a / 1b / 1c) |
> | [control-plane.ja.md](control-plane.ja.md) | `mount.write_back` を実行時に切り替える経路と、統計の出し方 |
> | [performance.ja.md](performance.ja.md) | 実測の数字 (6.1× 等) と改善候補の全体 |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | `mount.write_back*` の既定値と reload ポリシー |
> | [../Mount.ja.md](../Mount.ja.md) | 利用者向けの挙動と耐久性の契約 |
> | [runtime-control-plane.ja.md](runtime-control-plane.ja.md) | 運用フェーズ全体の構成 (ハブ) |

## 設計

### 確定設計 — write-back キャッシュ: **チャンクをメモリで組み立てて 1 回だけ書く**

> 以下は **実装前の確定設計**である。現行との差分は後続の「1d 実装ステータス」を参照。2026-07-25 に生 SQL のベンチで**取り分の出どころを測り直し**、設計判断を確定した。
> 実測値の正は [performance.ja.md](performance.ja.md)。

#### 出発点の訂正 — 主因は「tx の本数」ではなく「同じ行を何度も育てること」

このメモの初版は「Citus が遅いのは 1 FS 操作 = 1 分散トランザクションだから、**tx の本数を減らす**のが唯一の道」と
書いていた。**追測で訂正**した (詳細は [performance.ja.md §真の主因](performance.ja.md)):

| 1 MiB のファイルデータを書く形 | 単一 PG | Citus rf=2 |
|---|---|---|
| **今の形**: 128 KiB × 8 回の partial UPSERT・各回別 tx | 48.9 ms | **621.9 ms** (= 1.61 MB/s。rsync 実測 1.62 と一致) |
| **write-back**: メモリで 1 MiB 組み立て → 1 文・ファイル単位 tx | 7.5 ms | **77.1 ms** (12.98 MB/s) |
| 倍率 | **6.5×** | **8.1×** |

* 内訳: **増幅の寄与 7.5×** (同じ行に 8 回 partial UPSERT 480.8 ms vs 8 行に分けて書く 63.7 ms) に対し、
  **commit 本数の寄与は 17%** (12.6 ms/commit × 8)。**取り分の本体は増幅の除去**。
* rsync が遅かったのは「1 MiB チャンクを 128 KiB ずつ 8 回 UPSERT で育てる」= **bytea が TOAST 対象なので
  部分更新でも毎回 TOAST チェーン全体が書き換わる**から。`dd bs=1M` が速かったのは元から 1 回で書けていたから。
* **したがって write-back は Citus 専用の対策ではない**。単一 PG でも 6.5×。

#### 決定事項

| # | 判断 | 決定 | 根拠 |
|---|---|---|---|
| 1 | dirty チャンクの表現 | **フルチャンクバッファ + 書込レンジ (extent) リスト** | 増幅除去の本体。read 経路が `ContentCache.Get` のままで済む (分岐ゼロ) |
| 2 | flush 時の SQL の形 | **dirty チャンク 1 個 = 1 文**。全域被覆なら `payload = $1` の全置換、部分被覆なら **今と同じ overlay UPSERT を 1 回** | 既存 1 MiB 行に対し overlay 24.8 ms / 全置換 29.4 ms / フル read 0.4 ms。**わざわざ read して全置換にする必要はない** |
| 3 | `data_id` の採番 | **ブロック事前予約** (下記) | 新規ファイルでも dirty を `data_id` キーにできる = 判断 1 の前提が成立。同期 tx が消える |
| 4 | flush の粒度 | **ファイル単位を既定**。ファイル横断バッチは**将来のノブ** (実装するが既定 off) | 横断の追加利得は **+8.8% のみ** (63.6 → 58.0 ms/MiB)。喪失窓とロック保持時間の増加に見合わない |
| 5 | shard グループを揃える最適化 (起点ディレクトリ単位でまとめる等) | **不採用** (設計記録として残す) | **2PC の参加者数 = 触ったワーカー数**で上限はノード数。揃えると PREPARE 3→2 に減るが**時間差は測定限界以下**。将来ワーカーが増えたら再評価 |

#### 決定 1+2: dirty チャンクの表現と flush の形

新しい 3 つ目のキャッシュは作らない。**`ContentCache` のエントリに dirty 状態を足し、`InodeCache` のエントリに
dirty フィールドを足し、両者を束ねる `DirtySet` を 1 つ持つ**。

```
DirtySet (mount ごとに 1 つ)
├─ files: data_id → FileDirty {
│      chunks: chunk_index → { byte[] buf, List<(off,len)> written, bool full }
│      truncateTo?      // truncate は「それ以降の chunk を消す」境界として保持
│      occupiedDelta    // Σ (payload 長の増減) — flush で 1 回の UPDATE に集約
│      state            // Clean | Dirty | Flushing | FlushingRedirty
│      error?           // flush が失敗したら latch (次の fsync/flush/release で返す)
│  }
└─ inodes: inode_id → { size?, mtime?, ctime? }
```

* **`written` extent を持つ理由**: 「フルチャンクを組み立てた」のか「一部だけ書いた」のかを区別して、
  flush で**今と同じ意味論**を再現するため。全域なら全置換、部分なら overlay 1 回。
  これがないと、スパース領域への部分書き込みが「穴 (行なし)」を「0 埋めチャンク」に変えてしまい、
  `total_size` / `st_blocks` ([database.ja.md §実占有バイトと st_blocks](database.ja.md)) の意味が変わる。
* **partial write でチャンクが手元に無い場合も DB を読まない**。extent があるので overlay で書けば足りる
  (read は 0.4 ms と安いが、**読まずに済むなら読まない**方が単純)。
* **同一ファイルの dirty は必ず同一 tx で flush**。サイズと内容が食い違う中間状態を作らない。
* **順序**: `truncate` は extent ログを切る境界として扱う (最終状態マップだけだと truncate → write の順序が壊れる)。
* `occupiedDelta` は **flush 時に `SELECT length(payload)` 抜きで確定できる** — 手元に旧 payload 長があるため。
  今の per-chunk `SELECT length(payload)` ([Api.cs](../../src/core/src/Api/Api.cs) `WriteChunkSlice`) が消える。

#### 決定 3: `data_id` のブロック事前予約

今は初回 write 時に `EnsureDataRow` が**同期 tx**で `{prefix}data` を INSERT して `data_id` を採番している
(Citus では inode ロック + INSERT + inode UPDATE で数十 ms)。write-back では **dirty を `data_id` キーにしたい**
(判断 1 の前提) ので、**採番を DB 往復なしで済ませる**。

`{prefix}data.id` は `BIGSERIAL` = シーケンスなので、**まとめて先に取っておける** (実測: Citus の分散シーケンスでも動く):

| 方式 | 実測 | 直列化 |
|---|---|---|
| `SELECT nextval(seq) FROM generate_series(1,1000)` | **4.0 ms / 1000 個** | **不要** (各 nextval が原子的。id は一意・非連続でよい) |
| `WITH a AS (SELECT n, nextval(seq) min) SELECT min, setval(seq, min+n)` で `[min,max)` を予約 | 1.4 ms / 500 個 | **必要** (並行予約が重なる) |

**採用は前者 (ロック不要・一意な id を N 個)**。連続区間である必要はなく、`long[]` を持つだけなので
1 万個でも 80 KB。予約量は **需要に応じて伸ばす** (使い切ったら次はより多く取り、上限で打ち止め — ID を
ブロック予約してメモリから配る定石)。**枯渇したら同期で取り直す** (= 最悪でも今と同じコスト)。

* **`{prefix}data` 行の INSERT 自体は flush tx に同梱**する (予約済み id を明示指定して INSERT)。
  → 新規ファイルの「初回 write で +1 tx」が消える。
* **未使用の予約 id は捨てる** (プロセス終了時)。シーケンスに穴が空くだけで無害。
* **cross-client の競合は今と同じ手順で守る**: flush tx の中で inode ロックを取り `data_id` を読み直し、
  **他クライアントが既に別の `data_id` を確定させていたら自分のチャンクをそちらに付け替える**
  (手元に payload があるので付け替えは可能)。今の `EnsureDataRow` の「lost race → 既存を使う」と同じ意味論。
* 同じ仕組みは将来 `{prefix}inode.id` にも使える (= `create` の write-back 化の下敷き。今回はやらない)。

#### write-back に載せる操作 / 載せない操作

| 操作 | 扱い | 理由 |
|---|---|---|
| ファイル内容 (`write`) / `truncate` のサイズ変更 | **write-back** | 本数が最も多く、FUSE 的にも `fsync` までは遅延が許される |
| `st_size` / `st_mtime` / `st_ctime` / `total_size` | **write-back** (内容と同梱) | 内容と一緒に見えないと不整合になる |
| `create` / `unlink` / `rename` / `mkdir` / `link` / `symlink` | **write-through (現状維持)** → **1e で pending-born に限り遅延に拡張 (設計確定・下 §1e)** | 名前空間の可視性・監査ログ・`{prefix}lock` の意味論に直結。頻度も低い |
| `chmod` / `chown` / `xattr` | **write-through (現状維持)** → **1e でも persisted inode へは write-through を維持** (pending inode へは coalesce) | 権限は遅延させたくない |

**監査ログへの影響なし**: [audit-log.ja.md](audit-log.ja.md) が記録するのはメタデータ操作
(create/delete/rename/chmod/chown/hardlink) だけで、すべて write-through 側に残る。

#### 一貫性・耐久性の担保 (実装の本体)

1. **read は dirty を見る**: dirty payload は `ContentCache` の同じエントリに載るので、read 経路は無変更で
   最新が見える (= read-after-write が保たれる)。`Generation` による stale 封じもそのまま効く。
2. **`fsync` / `Flush`(close) / `release` で同期 flush** ← **⚠ 前提条件: 現状これらは未実装**。
   [src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs) は `FSync` / `Flush` を override しておらず
   基底 ([FuseFileSystemBase.cs](../../src/fuse/src/FuseFileSystemBase.cs)) の `-ENOSYS` を返す。
   **カーネルは fsync の ENOSYS を「以後 no_fsync」として成功扱いにする**ので、write-through の今は無害だが、
   **write-back 化した瞬間 `fsync(2)` が嘘をつく**。バインディング側の配線は既にある
   ([FuseMount.cs](../../src/fuse/src/FuseMount.cs) `_fsync` / `_flush`) ので、**override の実装が 1d の必須項目**。
   Dokan 側は `FlushFileBuffers` / `Cleanup` / `CloseFile` が既にあるので受け皿は揃っている
   ([src/dokan/src/FileSystem.cs](../../src/dokan/src/FileSystem.cs))。
3. **flush のトリガ**: (a) dirty バイト上限超過、(b) 時間窓、(c) `fsync` / `Flush` / `release`、
   (d) unmount / `Api.Dispose`、(e) `pgfsctl` からの制御メッセージ (任意)。

   > **⚠ 当初あった (e)「他クライアントの NOTIFY で該当 `data_id` が来たら先に flush してから invalidate」は
   > 実装していない。実装と doc を突き合わせて、この行のほうを落とした。**
   > `Api.OnRemoteChange` は `contentCache.InvalidateData(dataId)` を呼ぶだけで、**dirty は意図的に残す**。
   >
   > **入れても cross-client の踏み潰しは直らない**ことを実測で確かめてある。B が書いた後に A が
   > 自分の像を書き戻す順序は変わらないので、**flush を早めても最終バイト列は同じ**になる。
   > 直すには**「先に flush する」ではなく「何を flush するか」を変える**必要がある (下の §cross-client の契約)。
> ### cross-client の契約 (2026-09-21 に両 OS で実測)
>
> **dirty バッファは「チャンクの完全な像」である** (`Api.LoadChunkBase` の (3) — 部分書き込みのときは
> フルチャンクを読んで土台にする)。**flush はその像を丸ごと書き戻す** (`WriteFullChunkFlush`)。
> したがって **write-back を有効にしたマウントが dirty を抱えているあいだに他マウントが同じ実体を書くと、
> その書き込みは flush で消える**。消える範囲は**バイト単位ではなくチャンク単位**で、
> **「A が読んだことのあるチャンク」全体**が対象になる。
>
> 実測 (A = `write_back` on・B = write-through。既定チャンク 1MiB):
>
> | シナリオ | 結果 |
> |---|---|
> | B が全体を上書き → A が flush | **B の書き込みが丸ごと消える** |
> | B が A の書いていない領域を書く → A が flush | **B の書き込みが消える** (A の像に含まれるため) |
> | A が 1MiB 境界に 4 バイト書く → B が別チャンクを書く → A が flush | **またいだ 2 チャンク (2MiB) の範囲で B の書き込みが消える** |
>
> **`st_size` の巻き戻りだけは 塞いだ** (`DirtyFile.WriteEnd`)。以前は
> **他マウントの `truncate` を flush が巻き戻し、消えたはずのバイトが読めた**。
>
> **バイトの踏み潰しは未修正である。** 塞ぐには dirty を「完全な像」ではなく
> **「実際に書いたバイト範囲」**として持つ必要があり、これは設計変更になる。
> **それまでは「同じファイルを複数マウントから書くなら write-back を使わない」が契約**である。

4. **遅延エラー報告**: write-back では `WriteData` が DB エラーを返せなくなる。**per-file にエラーを latch し、
   次の `fsync` / `Flush` / `release` で `-EIO` を返す** (NFS と同じ契約)。latch は 1 度返したらクリアする。
5. **並行性**: `SupportsMultiThreading => true` なので **flush 中に同じファイルへ write が来る**。
   `Clean → Dirty → Flushing → (書き込みが来たら) FlushingRedirty → Dirty` の状態機械を持ち、
   **flush は「開始時点の dirty のスナップショット」だけを書き、Flushing 中に来た分は次の flush に回す**。
6. **ロック順序**: 横断バッチを有効にする場合、**全対象を 1 回の `LockTargets` に渡す**
   ([Api.cs](../../src/core/src/Api/Api.cs) — 入力を昇順ソートするのでこれで順序が固定される)。
   ファイルごとに分けて取ると多クライアントでデッドロックし得る。
7. **back-pressure**: dirty は read 予算 (`cache_data_max_bytes`) とは**別勘定** (dirty は捨てられないので LRU 対象外)。
   **上限に達したら write をブロック**して flush を待つ (無制限に育てない)。
8. **クラッシュ時は未 flush 分が失われる** (write-back の本質)。喪失窓は上限バイト / 時間窓で明示的に切り、
   既定は控えめにする。`mount.write_back` の既定を `false` にするのはこのため。
9. **`{prefix}lock` の意味論は不変**。flush 1 回につき対象 `data_id` / inode のロックを 1 回取るので、
   **ロック取得回数も flush の回数まで減る**。

#### 新規に必要な設定

| キー | 意味 | 既定 |
|---|---|---|
| `mount.write_back` | 有効/無効 | **`false`** (検証後に既定 on を検討) |
| `mount.write_back_max_bytes` | dirty バイト上限 (超過で flush + back-pressure) | 64 MiB |
| `mount.write_back_interval_ms` | 時間窓 (0 = 時間トリガなし) | 1000 |
| `mount.write_back_batch_files` | 1 tx にまとめるファイル数上限 (**1 = ファイル単位 = 既定**) | 1 |

追加は [settings-matrix.ja.md](settings-matrix.ja.md) / [Mkfs.ja.md](../Mkfs.ja.md) 既定値表 / [pgfs.toml.example](../../pgfs.toml.example) にも反映する。
Layer 3 status (下記 §Phase 4) に **dirty バイト / dirty ファイル数 / flush 回数 / flush 失敗数**を出す
(`{prefix}mounts.stats` のスナップショットに足す)。


## 実装ステータス (as-built)


**実装完了・実機検証済** (dev サーバ / Citus rf=2・shard 8)。設計との差分と実測値を以下に記録する。

#### 実測 (Citus rf=2 実機・20 MiB の `dd conv=fsync`)

| ワークロード | write_back off | write_back on | 倍率 |
|---|---|---|---|
| `dd bs=128k` (チャンク未満の write が続く = 増幅する形) | 12.29 / 12.63 s | **2.07 / 2.02 s** | **6.1×** |
| `dd bs=1M` (元から増幅しない形) | 5.65 s | **2.02 s** | **2.8×** |
| `rsync -a` (599 MB / 745 ファイル) | 263 s (2.27 MB/s) | **179 s (3.19 MB/s)** | **1.4×** |

* **`dd bs=1M` でも 2.8× 効く**のは、write-back が「チャンクごとの lock 取得 / `SELECT chunk_size` /
  `SELECT length(payload)` / `total_size` UPDATE / inode UPDATE」もファイル 1 回に畳むため
  (生 SQL の投影では見えていなかった分)。
* **rsync が 1.4× で頭打ちなのは想定どおり** — `create` / `chmod` / `chown` / `utimens` / `rename` は
  設計どおり write-through のままで、rsync のコストはそちらが支配的
  (≈2,100 のメタデータ tx)。**メタデータ側の write-back 化は将来の別テーマ**
  (id ブロック予約の仕組みは `{prefix}inode.id` にもそのまま使える)。
* 生 SQL の投影 8.1× に対し実測 6.1× (`bs=128k`)。差は FUSE 往復と `fsync` の同期 flush。

#### 設計からの差分 (as-built)

1. **dirty チャンクは「書込レンジ (extent) リスト」ではなく「有効長 (`Len`) + DB 上の長さ (`PrevLength`)」だけを持つ**。
   フルチャンク像を保つ方針にした結果、必要な情報は「どこまで有効か」と「DB 上は何バイトか」だけになった
   (穴・`total_size` の意味論はこの 2 値で再現できる)。
2. **既存チャンクへの部分書き込みでは DB から 1 回読む** (設計メモでは「読まない」としていた)。
   読まないと dirty バッファが「チャンクの完全な像」にならず、**read 経路が未書き込み範囲をゼロで返してしまう**
   (実測 0.4 ms なのでコストは無視できる)。`offsetInChunk == 0 && len >= chunkSize` なら読まない。
   回帰テストは `test_partial_chunk_overwrite` / `test_partial_overwrite_after_remount`。
3. **flush の SQL は 1 チャンク = 1 文の「payload 全置換」**。ただし DB 側が手元より長い場合
   (他クライアントが伸ばした) は末尾を残すため `overlay` に落ちる `CASE` を付けた。
4. **read キャッシュ無効 (`cache_data_max_bytes = 0`) でも write-back は動く**。dirty は read 予算とは
   別勘定で LRU 退避の対象外にし、read 経路は `UseChunkCache` (= read キャッシュ有効 **または** write-back 有効)
   でフルチャンク経由に切り替える。回帰テストは `test_dirty_visible_without_read_cache`。
5. **`truncate` / `unlink` / `release` は「flush」ではなく「discard」**。データ本体が消えるので書くのは無駄。
   `truncate` は 0 なら discard、0 以外なら先に flush してから既存経路に渡す。
6. **`utimens` / `UpdateSize` の前に flush する**。dirty の `st_mtime` が後から被さって明示指定を
   上書きするのを防ぐ (`FlushBeforeMetadataWrite`)。
7. **`st_mtime` は「書き込み時刻」を書く** (flush 時刻ではない)。メモリと DB を一致させるため。
8. **`write_back_batch_files` は実装しなかった** (設計では「実装するが既定 off」)。ファイル横断バッチの
   追加利得は +8.8% で、喪失窓とロック保持時間の増加に見合わないと判断。必要になったら
   `FlushTransaction` を複数 `DirtyFile` 対応に広げる (ロックは 1 回の `LockTargets` にまとめること)。

#### 実装中に見つけて直したバグ (回帰テスト付き)

**キャッシュに無いチャンクを「全域上書き」すると実占有バイトが二重計上され `du` が倍になる**。

* 原因: 全域上書きでは payload を DB から読まない最適化をしているが、そのとき
  **「DB 上の payload 長」を 0 とみなしていた**ため、flush 時の差分が `新しい長さ - 0` になり
  `{prefix}data.total_size` に丸ごと足し込まれていた (2 MiB のファイルが `du` で 4 MiB)。
* 修正: 全域上書きのときは payload の代わりに **`length(payload)` だけを読む**
  (`Api.LoadChunkBase` → `LoadChunkLength`。`length(bytea)` は TOAST ポインタの raw size を読むだけで
  detoast は起きない)。部分書き込みではフルチャンクを読むのでその長さを使う。
* **キャッシュに載っている間は露見しない** (clean → dirty 昇格でその長さが引き継がれるため)。
  よって回帰テストは **マウントを張り替えてキャッシュを空にする**必要があり、e2e ではなく
  [writeback.sh](../../tests/linux/writeback.sh) の `test_full_chunk_overwrite_du_after_remount` に置いた
  (修正を外すと `du が 2097152 -> 4194304 に増えた` で落ちることを確認済み)。

#### 主な追加・変更ファイル

| ファイル | 役割 |
|---|---|
| [DirtySet.cs](../../src/core/src/Api/DirtySet.cs) (新規) | ファイル単位の未 flush 台帳 (`DirtyFile` = flush gate / dirty チャンク集合 / size・mtime / data 行 pending / エラー latch) |
| [IdReservation.cs](../../src/core/src/Api/IdReservation.cs) (新規) | `{prefix}data.id` のブロック事前予約 (`nextval` × N を 1 文・ロック不要・2 倍成長) |
| [ContentCache.cs](../../src/core/src/Api/ContentCache.cs) | dirty バッファ (`WriteDirty` / `SnapshotDirty` / `MarkFlushed` / `DiscardDirty` / `RekeyData`)、dirty は退避対象外・別勘定 |
| [Api.cs](../../src/core/src/Api/Api.cs) | `WriteDataBuffered` / `FlushInode` / `FlushData` / `FlushAll` / `FlushTransaction` / back-pressure / 背景 flush ループ / `Dispose` の全 flush |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | `Flush` / `FSync` の override (**新規実装**。従来は基底の `-ENOSYS`) + `Release` の保険 |
| [FileSystem.cs (dokan)](../../src/dokan/src/FileSystem.cs) | `FlushFileBuffers` を実 flush に + `Cleanup` で flush |
| [StatusCommand.cs](../../src/ctl/src/StatusCommand.cs) | Layer 3 に `write-back` 行 (dirty バイト / チャンク / ファイル / flush 回数 / 失敗数) |

#### テスト (実施済み)

* **[tests/linux/e2e.sh](../../tests/linux/e2e.sh) を両モードで実機実行**: `write_back` off **42 passed / 1 skip**、
  on **42 passed / 1 skip** (件数は 38→43 に増加。skip は環境要因の `test_fallback_uname_gname`)。
* **e2e に 5 件追加** (両モードで同じ結果になるべき整合テスト):
  `test_partial_chunk_overwrite` (チャンク部分上書きで周囲が壊れない) /
  `test_full_chunk_overwrite_du` (全域上書きで内容・サイズ・実占有が壊れない) /
  `test_append_after_close` (close を挟んだ追記で実占有バイトが二重計上・欠落しない) /
  `test_write_read_without_sync` (未 flush の read-after-write) /
  `test_truncate_discards_unflushed` (捨てた dirty が復活しない)。
* **[tests/linux/writeback.sh](../../tests/linux/writeback.sh) を新規追加** (8 件)。**マウントを自分で張り替える**
  ので e2e とは別スクリプト: `fsync` 後の `kill -9` 耐性 / `close` 後の `kill -9` 耐性 / 正常 unmount の flush /
  back-pressure (上限 64 KiB) / read キャッシュ無効時の dirty 可視性 / 再マウント後の部分上書き (seed 経路) /
  **再マウント後の全域上書きで `du` が二重計上されない** (上記バグの回帰) / `pgfsctl status` の write-back 表示。
* **性能の測り方**: `dd bs=128k` か rsync で測ること。**`dd bs=1M` はチャンク境界に揃うので増幅が起きず、
  改善幅を過小評価する**。

#### 残作業

1. **docker ランナー (`tests/docker/run.sh`) に `WRITE_BACK` 注入口**を足して単一 PG でも両モードを回す
   (`CACHE_DATA_MAX_BYTES` と同じ形)。この環境に docker が無いため未実施。
2. **Windows (Dokan) 側の検証** — **off モードは 2026-09-19 に実機緑** (Windows e2e 30/30 ×3 + cross-client 6/6 ×2・pgsql_server の Citus rf=2)。
   `FlushFileBuffers` / `Cleanup` を実際に通す **on モードの受入も完了** — [writeback.ps1](../../tests/windows/writeback.ps1) が
   **negative control 付き**で通る (2026-09-21 再走: 6/6。バリア無しの書き込みが失われることも同じスイートで見ている)。
   Windows 側の as-built は [windows-parity.ja.md §実装ステータス](windows-parity.ja.md)。
3. **flush 失敗時に `-EIO` が返ることの自動テスト** — DB を落として書き込む形が要るので未整備 (経路は実装済)。
4. **cross-client の last-flush-wins テスト** (A が未 flush のまま B が同じファイルを書く)。
5. **メタデータ操作の write-back 化** (rsync 系をさらに速くする道) → **設計確定済み。下 §1e が正**。


## 変更記録

時系列の記録はここに追記する (設計と as-built は上の 2 章が正)。

- [runtime-control-plane.ja.md](runtime-control-plane.ja.md) が 1,802 行に肥大したため、
  機能ごとに分割してこの doc を切り出した。内容は分割前のまま。
  **ライブ無効化の二相化** の as-built は分割前は 1e のレビュー節にあったため、
  [metadata-write-back-reviews.ja.md](metadata-write-back-reviews.ja.md) 側に残っている。
