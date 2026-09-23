# `data_id` のライフサイクル — ハードリンク共有 / `st_ino` 安定性の是正

> **道順**: [docs/README.ja.md](../README.ja.md) › **本書**
>
> **状態**: 設計合意済・実装は §実装ステータス を参照。
>
> **この doc が正である範囲**: `data_id` を「ファイルの実体 id」として **create 時に確定し最終 unlink まで
> 解放しない**という不変条件の正。`{prefix}data` 行の遅延作成 (行が無い = 中身が空)、ハードリンクによる
> 実体共有、`truncate 0` を全リンクへ配ること、`st_ino` が `data_id` 由来で安定することはここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [database.ja.md](database.ja.md) | `{prefix}inode` / `{prefix}data` / `{prefix}data_chunk` の**スキーマそのもの** (列・制約・分散キー) |
> | [metadata-write-back.ja.md](metadata-write-back.ja.md) | メタデータ write-back (1e) の設計。pending inode 台帳と**予約 id の使われ方** |
> | [write-back.ja.md](write-back.ja.md) | データ write-back (1d) の dirty 台帳。`truncate 0` が捨てる未 flush dirty の所在 |
> | [cache.ja.md](cache.ja.md) | `data_id` を鍵にしたキャッシュ invalidate の仕組み |
> | [../tests.ja.md](../tests.ja.md) | テストの一覧・実行方法・件数。本書は個々のテストが**何を守るか** |

## 背景 — 1 つの根から 3 つの不具合が出ている

pgfs は「ハードリンク = 同じ `data_id` を指す inode が複数ある状態」で表現する。
つまり **`data_id` がファイルの実体 id** である。ところが実装では `data_id` の確保・解放が
**ファイルの寿命ではなくリンク単位の操作**に紐付いていたため、次の 3 つが起きていた
(いずれも dev サーバ実機で再現済)。

| # | 症状 | 実機再現 |
|---|---|---|
| 1 | **`truncate 0` が共有実体を消し、兄弟にデータ消失と dangling 参照を残す** | `echo HELLO > t1; ln t1 t2; truncate -s 0 t1` → 共有 data 行が DROP され、**t2 は `data_id` も `st_size=6` も保ったまま**残る。`cat t2` は NUL 6 バイト。`rm t1` しても t2 の `st_nlink` は 2 のまま |
| 2 | **空ファイルのハードリンクがリンクにならない** | `: > e1; ln e1 e2` の直後に **`st_nlink` が両方 1・`st_ino` も別**。`data_id` が NULL の間は**リンク関係を表現する手段が無い** |
| 3 | **中身を書くと `st_ino` が変わる** | `: > e1` で `st_ino = 144591` (= `inode.id`)、`echo HELLO > e1` で `st_ino = 9223372036854900517` (= `data_id` 由来)。`truncate 0` → 再 write でも同じ。**`find -samefile` / `rsync -H` / `tar` / バックアップの重複排除が誤動作する** |

原因はすべて同じで、**`data_id` が「中身がある間だけ存在する id」になっていた**こと:

- `Api.TruncateData` の `newLength == 0` 分岐が `DropAllChunks` + **`DropDataRow`** + **`ClearInodeDataId`(自分の inode だけ)** + `UpdateSizeInTx`(同じく自分だけ) を行う
- `Api.CreateFile` は `data_id: null` で inode を作り、**初回 write の `EnsureDataRow` まで採番しない**
- `src/fuse/src/FileSystem.cs` の `st_ino` は `DataId != null` なら `data_id | 2^63`、null なら `inode.Id`

## 決定

**`data_id` を「ファイルの実体 id」として create 時に確定し、最終 unlink まで解放しない。**
ただし **`{prefix}data` 行の作成は遅延**する (create で INSERT を増やさない)。
`st_ino` は **`data_id` 由来のまま**とする (この決定によって結果的に安定する)。

### 新しい不変条件

> **通常ファイルの `inode.data_id` は create 時に確定し、最終 unlink まで不変である。**
> **`{prefix}data` 行は遅延作成され、「行が無い」= 「中身が空」と解釈する。**

ディレクトリと symlink は従来どおり `data_id` を持たない (`st_ino` は `inode.Id` 由来)。

この不変条件は**既存機構にそのまま乗る**:

- `IdReservation` ([IdReservation.cs](../../src/core/src/Api/IdReservation.cs)) が **DB 往復なしの採番**を既に持つ
  (ブロック単位・64 → 4096 の 2 倍成長。実測 1000 個で 4.0 ms)。create の inode INSERT は
  既に `@data_id` 列を取るので、**採番した値を渡すだけで往復は増えない**
- `EnsureDataRow` は既に **「予約 data_id はあるが行が無い」→ `CreateDataRowWithId`** の分岐を持つ
- `EnsureDirtyFile` も同じ状態を「予約済みで未 flush」として扱う
- `ReadData` は `offset >= inode.Size` で先に返すので、空ファイル (行なし) で data 行に触らない
  (1e ステージ 2 で「予約 data_id の read が `-EIO`」を潰した経路と同じ)

## 変更点

| 対象 | 変更 |
|---|---|
| `Api.CreateFile` | `dataId: null` → **予約器から採番した id** を渡す。**採番に失敗したら null にフォールバック**して create 自体は成功させる (既存 FS / DB 一時障害でも create を落とさない) |
| `Api.TruncateData` (`newLength == 0`) | `DropDataRow` と `ClearInodeDataId` を**やめる**。`DropAllChunks` + data 行の `total_size = 0` に閉じる |
| 〃 | **`st_size = 0` を全リンクへ配る** — `st_nlink > 1` のときだけ `WHERE data_id = @shared_data_id` (先例 = `UpdateInodeSizeAndMtimeInTx`。`st_nlink > 1` に限るのは Citus で multi-shard になるため) |
| `Api.CreateHardLink` | `source.DataId == null` (= 本変更より前に作られた空ファイル) なら、**tx 内で採番して source に付けてから**リンクする。これで #2 が既存 FS でも直る |
| `DirtyNamespace.TruncateToZero` / `Api.TruncatePendingToZeroLocked` | `Inode.DataId = null` を**やめる** (`Size = 0` だけにする)。「行が無い = 空」で整合するため |
| `src/fuse/src/FileSystem.cs` (`st_ino`) | **変更しない**。`data_id` が不変になったことで安定する |
| `src/dokan` | **変更なし** (`FileMode.Create` / `Truncate` は同じ `TruncateData` を通る) |

### 採らなかった案

| 案 | 却下理由 |
|---|---|
| **A. truncate で data 行を消さないだけ** | #1 と `st_nlink` は直るが、**#2 (空ファイルの hardlink) と #3 (`st_ino` の不安定) が残る**。3 件は同根なので一緒に閉じるほうが安い |
| **B. 解放時に兄弟を新しい行へ付け替える** | inode は `parent_id` 分散なので**兄弟が別 shard** = cross-shard UPDATE。しかも `LinkDataIdInTx` が「inode shard は tx 末尾で触る」規約 (40P01 の閉路対策) を作っているのに、それを巻き戻す。得られる結果は A と同じ |
| **C. 兄弟の `data_id` も一緒に NULL にする** | **直らない**。次の write で `EnsureDataRow` が新しい行を作りやはり分裂し、`st_nlink` も壊れたまま |
| **D. create 時に `{prefix}data` 行を INSERT する** | 全 create に INSERT が 1 本増える。**1e の「create は pending で DB 往復ゼロ」と正面衝突**する。採番だけ先にする (= 本決定) で同じ効果が得られる |
| **`st_ino` に inode 側の安定 id 列を足す** | `data_id` の寿命から独立させられるが **DDL 変更 + 既存 FS の移行**が要る。v0.2.0 直前に入れる変更ではない。**v0.3 以降の選択肢として残す** (下 §残る論点) |

## write-back / メタデータ write-back (1e) との相互作用

- **`truncate 0` は兄弟の未 flush dirty も捨てる**。`PrepareTruncateWriteBack` が `DiscardDirtyData(dataId)` を
  呼び、台帳は「1 data_id = 1 DirtyFile」なので**共有実体に対する未 flush が全リンクぶん消える**。
  **これは意図した挙動**である (共有実体の truncate なので POSIX 的に正しい)。
- **`MarkSyncOnClose` は `PrepareTruncateWriteBack` の後**という順序を保つこと (前者が `FlushInode` を
  呼んで印を落とすため)。壊すと `wbmeta.sh` の `test_meta_truncate_syscall_close_is_synchronous` が落ちる。
  印は A-4〜A-7 で **inode と data の両キー**に積むので、`data_id` が消えなくなることで
  **data 側キーの寿命が変わる** (= 印が inode の寿命と揃う)。
- **`EnsureDirtyFile` の `ReserveDirtyFile` 経路は既存 FS 専用のフォールバックになる**
  (新しく作られたファイルは既に `data_id` を持っているため)。**消さない**。
- **B-10 の `chunk_size` 突き合わせ (`EnsureFlushDataRow` の Rekey 検査) は消さない**。
  Rekey 経路を通る頻度は下がるが、cross-client の競合経路は残る。

## 既存 FS の扱い

- **既に分裂した実体は直らない** (消えたチャンクは復元できない)。修復コマンドは**作らない**
  (この不具合は v0.2.0 で塞いだ。それより前に分裂していた実体は、該当ファイルを作り直すしかない)。
- **本変更より前に作られた `data_id` が NULL のファイル**は、
  ① 初回 write で従来どおり `EnsureDataRow` が採番する ② ハードリンク時は `CreateHardLink` が採番して付ける
  — のいずれかで新しい不変条件に合流する。**`st_ino` はその瞬間に 1 回だけ変わる** (既存の挙動と同じ)。

## テスト

タイミング依存ではないので**自動テストを書く**。

| テスト | 内容 |
|---|---|
| `test_truncate_keeps_hardlink_shared` (Linux e2e) | `echo A > f; ln f g; echo B > g` → `cat f` が `B` / `st_nlink` が両方 2 / 再マウント後も同じ |
| `test_truncate_zero_updates_all_links` (Linux e2e) | `truncate -s 0 f` の後、**兄弟 g の `st_size` も 0** になる |
| `test_empty_file_hardlink_shares` (Linux e2e) | `: > e1; ln e1 e2` → `st_nlink` が両方 2・`st_ino` が一致 |
| `test_ino_stable_across_write` (Linux e2e) | `: > e1` の `st_ino` と `echo HELLO > e1` 後の `st_ino` が一致 |
| `test_truncate_zero_then_rewrite` (Windows e2e) | `truncate 0` → 再書き込みでサイズとハッシュが一致すること |
| `test_file_index_is_data_id_and_stable` (Windows e2e・追加) | **`FileIndex` が data_id 由来 (最上位ビットが立つ) で、最初の write を跨いで変わらない**こと。**「Dokan は `st_ino` / `nlink` を公開しない」は誤り**だった — `ByHandleFileInformation.FileIndex` と `NumberOfLinks` がそれに当たる |

## 実装ステータス

- **設計合意 + 実装完了** (ビルド 0 error)。入ったものは下表。

| 変更 | 場所 |
|---|---|
| create 時に `data_id` を採番して inode に載せる (行は作らない・採番失敗時は null にフォールバック) | `Api.CreateFile` / 新 `Api.RentDataId` |
| `truncate 0` で `DropDataRow` / `ClearInodeDataId` をやめ、`DropAllChunks` + `total_size = 0` に閉じる | `Api.TruncateData` / 新 `Api.ResetDataTotalSizeInTx` |
| `st_size` をハードリンクがあれば全リンクへ配る (**0 以外の truncate も同様**) | 新 `Api.UpdateTruncatedSizeInTx` / 新 `Api.UpdateSizeForAllLinksInTx` |
| 兄弟の `InodeCache` エントリを `data_id` で無効化 (pinned = pending は触らない) | 新 `InodeCache.InvalidateByDataId` |
| 旧 FS 互換: `data_id` を持たない source へ hardlink 時に採番して付ける | 新 `Api.AttachDataIdForLinkInTx` (`Api.CreateHardLink` から) |
| pending 側も `Inode.DataId = null` をやめる | `DirtyNamespace.TruncateToZero` / `Api.TruncatePendingToZeroLocked` |
| `st_ino` (`data_id` 由来) | **変更なし** — `data_id` が不変になったことで安定 |
| `src/dokan` | **変更なし** |

**実装中に決めたこと**: `st_size` の全リンク配布は **0 以外の truncate にも適用**した。
設計時は `truncate 0` だけを見ていたが、`truncate -s 3` でも兄弟が古い `st_size` を抱える同じ欠陥がある。
判定は `HasHardLinkSiblings` (キャッシュ上の `st_nlink` だけを見る既存ヘルパ) を再利用しており、
**`st_nlink == 1` のときは従来どおり単一行 UPDATE** なので Citus の multi-shard は増えない。

**実装中に見つけて塞いだ穴**: `PrefetchOccupiedBytes` は `{prefix}data` 行が見つからない inode を
`continue` で飛ばし、`OccupiedBytesLoaded` を立てないままにしていた。従来は空ファイル = `data_id` が
null なので手前の分岐で 0 が確定していたが、**create 時に `data_id` を持たせるとまだ書かれていない
ファイルが全部ここに落ちる** — つまり `ls -l` で空ファイル 1 つにつき `GetOccupiedBytes` の往復が
1 回増えるところだった。新しい不変条件 (**行が無い = 中身が空**) をそのまま適用して 0 を確定させた。

### Linux 実機の回帰で出た切れ目 — **`truncate` は直したが `write` を直していなかった**

FUSE 側の実機回帰で e2e の新規 2 件が FAIL した。症状は
**「チャンクは共有できているのに、読み手が古い `st_size` で切られて中身が空に見える」**:

```
: > a; ln a b       → a,b とも data_id 共有 / st_nlink 2 / st_size 0   ← ここまで正しい
echo "shared" > a   → a: st_size=7 / b: st_size=0 (data_id は同じ)
cat b               → 空
```

`st_size` の全リンク配布を **`truncate` にしか入れていなかった**のが原因で、**通常の write にも同じ配布が要る**。
2 経路それぞれに穴があった:

| 経路 | 穴 | 修正 |
|---|---|---|
| write-through | `FinishWriteInodeInTx` が単一行 UPDATE のまま | 新 `PropagateWriteToLinksInTx` を tx 末尾で呼ぶ (`st_nlink > 1` のときだけ `WHERE data_id = @data_id AND id <> @id`) |
| write-back (1e) | `UpdateInodeSizeAndMtimeInTx` の `if (sharedDataId != null && linkDataId == null)` により、**data 行を同じ tx で INSERT するとき (= その実体への最初の書き込み) は配布が効かない** | リンクと配布は排他ではないので、link 句を畳んだ tx では**単一行 UPDATE の後に配布の 1 文を足す** |

`st_nlink > 1` の判定は `HasHardLinkSiblings` に **手元の `Inode` を優先するオーバーロード**を足した
(書き込み / truncate の経路は対象行を読んだ直後なので、キャッシュから落ちていても 1 に見えない)。

### `rm` の multi-shard 検索を戻した

`DeleteInodeInTx` の `SELECT id FROM inode WHERE data_id = @data_id` は、以前は
`data_id` が NULL のファイル (= 一度も書いていない) でブロックごとスキップされていたが、
**create 時に `data_id` を確定させたことで全 unlink で走るようになった**。`data_id` は inode の
分散キーではないので Citus では multi-shard で、`rm -rf` 主体の負荷で効いてくる。

**DELETE に `RETURNING st_nlink` を付け、消す直前の `st_nlink` が 1 以下なら兄弟検索をまるごと省く**
(新 `ReleaseOrRelinkDataInTx`)。**判定にキャッシュを使わない**のが要点で、同じ tx の DB 値を見るので
他クライアントが張ったハードリンクを見落とさない (見落とすと兄弟の実体を消してしまう = 直そうとした
不具合そのものになる)。`st_nlink` が 2 以上なのに兄弟が居なかった場合 (旧不具合で壊れた nlink) は
従来どおり実体を解放する。

### 兄弟のキャッシュを落とす + 配布の判定を DB の `st_nlink` に寄せる

Linux 実機の 2 巡目で `test_empty_file_hardlink_shares` だけが残った。**DB は正しく配れており
(`a`/`b` とも `st_size=7`)、FS 越しの `stat b` だけが 0 を返していた** — つまり
**兄弟の `InodeCache` を無効化していなかった**。`truncate` 側は `InvalidateByDataId` を呼んでいたが、
**write 経路からは 1 度も呼ばれていなかった**。

| 経路 | 追加した後始末 |
|---|---|
| write-through | commit 後に `InvalidateHardLinkSiblings` (= `InodeCache.InvalidateByDataId`) |
| write-back (flush) | 同じ位置で `InvalidateByDataId` |

**併せて配布の判定を「キャッシュの `st_nlink`」から「DB の `st_nlink`」へ移した**。
キャッシュが古くて 1 に見えると配布そのものが飛び、**まさにこの doc が直している不具合に戻る**。
判定値は既に走っている UPDATE の `RETURNING st_nlink` から只で取れる:

| 判定 | 以前 | 現在 |
|---|---|---|
| truncate の配布 | `HasHardLinkSiblings` (キャッシュ) | `UpdateSizeInTx` の `RETURNING st_nlink` |
| write の配布 / 兄弟キャッシュ無効化 | (配布はキャッシュ判定・無効化は無し) | `FinishWriteInodeInTx` の `RETURNING st_nlink` |
| 削除時の実体解放 | 兄弟の multi-shard 検索 | `DELETE ... RETURNING st_nlink` |

これで **`data_id` を共有する実体に対する判定はすべて DB の値**になった。往復は 1 回も増えていない。
`HasHardLinkSiblings` の `Inode` 優先オーバーロードは不要になったので削除した
(flush 経路の `HasHardLinkSiblings(long)` は A-1 の既存設計のまま残置。§残る論点 を参照)。

### 検証

- **ビルド 0 error**。
- **Windows 実機 (windows_client / Dokan 2.3.1 / PG=pgsql_server・Citus rf=2・audit on) で全スイート緑**:
  e2e **35/35** (新規 `test_truncate_zero_then_rewrite` を含む) / cross-client **6/6** /
  write-back **6/6** / metadata write-back **4 passed + 1 skip** (既知 SKIP) / control-plane **6/6**。
- **Linux 実機の回帰は FUSE 側が担当** (e2e off/on + `writeback.sh` + `wbmeta.sh` + `negcache.sh`)。
  新規 4 件 (`test_truncate_keeps_hardlink_shared` / `test_truncate_zero_updates_all_links` /
  `test_empty_file_hardlink_shares` / `test_ino_stable_across_write`) は **Linux でしか確認できない**
  (Dokan は `st_ino` / `st_nlink` を公開しないため)。**本修正の本丸はこの 4 件**である。


### Windows も同じ式に揃えた

**Dokan の `ByHandleFileInformation.FileIndex` が `inode.Id` のままだった** ので、
**ハードリンクの兄弟が「別のファイル」と判定されていた** (実測: 同じ `data_id` を指す 2 名が
`28639` / `28640`)。`data_id | 0x8000_0000_0000_0000` = **FUSE の `st_ino` と同じ式**に変更し、
**同じファイルが両 OS で同じ値**を返すようにした。

本 doc の #2 (空ファイルのハードリンク) と #3 (中身を書くと `st_ino` が変わる) が Linux で閉じているのと
同じ理由 — **`data_id` が create で確定し最終 unlink まで不変**であること — が、そのまま Windows にも効く。
実測でも**空ファイルの `FileIndex` が最初の write を跨いで変わらない**ことを確認した。

as-built は [windows-parity.ja.md §Windows の `FileIndex` を `data_id` 由来にした](windows-parity.ja.md)。

## 残る論点

- **`st_mode` / `uname` / `gname` と、明示的な `utimens` の `st_mtime` はハードリンク兄弟に配っていない**
  (2026-09-21 に実測で確認)。**POSIX ではハードリンクは同じ inode を指すので、これらは共有されるのが正しい。**

  **配る経路と配らない経路が混在している**ので、そこを取り違えないこと:

  | 経路 | 兄弟に配るか | 実装 |
  |---|---|---|
  | **write (`st_size` + `st_mtime`)** | **配る** | `Api.UpdateInodeSizeAndMtimeInTx` (`HasHardLinkSiblings` で `sharedDataId` を渡す) |
  | **`truncate` (`st_size`)** | **配る** | `Api.UpdateSizeForAllLinksInTx` / `UpdateTruncatedSizeInTx` |
  | **`chmod` (`st_mode`)** | **配らない** | `Api.UpdateMode` |
  | **`chown` (`uname` / `gname`)** | **配らない** | `Api.UpdateOwner` |
  | **`utimens` / `touch` (`st_mtime`)** | **配らない** | `Api.UpdateTimestamps` |

  実測 (dev サーバ `pgfs_test`・現行 HEAD):

  ```
  echo hello > a; ln a b       → a,b とも mode=644 / nlink=2 / size=6 / mtime 一致
  echo more >> a               → a,b とも size / mtime が揃って動く        ← 配っている
  chmod 600 a                  → a mode=600 / **b mode=644**               ← 配らない
  touch -d '2020-01-02 ...' a  → a mtime=2020-01-02 / **b mtime=変わらず** ← 配らない
  ```

  **「`st_mtime` を共有しない」と書くと言い過ぎになる** — 書き込みでは配っている。
  **配らないのは明示的な変更 (`utimens`) だけ**である (2026-09-21 に Dokan 側が指摘・実測で確認)。

  根拠は [Api.UpdateMode](../../src/core/src/Api/Api.cs) / [Api.UpdateOwner](../../src/core/src/Api/Api.cs) /
  [Api.UpdateTimestamps](../../src/core/src/Api/Api.cs) が **`WHERE parent_id = @parent_id AND id = @id` で
  1 行しか更新しない**こと。**`st_size` の配布 (`Api.UpdateSizeForAllLinksInTx`) と同じ形が要る。
  直すのはこの 3 本であって、書き込み経路は既に正しい。**

  **これは意図した離脱ではなく未実装である。** 本書 §変更点 が `st_size` を全リンクへ配ることにした理由
  (**兄弟が古い値を抱える**) がそのまま当てはまり、**どの doc にも「配らない」判断は書かれていない**。
  **実装中に「`truncate 0` だけを見ていたが `truncate -s 3` でも同じ欠陥がある」と気づいて一般化した**
  のと同型で、**そのとき一般化の範囲が `st_size` の中に留まっていた**。

  **利用者への影響は 2 つあり、読む人が違う。**
  **① 権限**: **`chmod` が片方の名前にしか効かない**ので、**ハードリンクを権限の境界として使うと、
  制限したつもりの実体がもう一方の名前から元の権限で開ける**。
  **② タイムスタンプ**: **`touch` で明示的に立てた時刻が兄弟に届かない**ので、
  **`make` やバックアップが兄弟の名前を見ると「更新されていない」と誤認する**。
  利用者向けの言い方は [CHANGELOG.ja.md §既知の制限](../../CHANGELOG.ja.md) にある (Dokan 側が記載)。

  **条件が要らない点で、踏み潰し (`write_back` on が前提・既定 off) より重い** —
  **既定の構成のまま `chmod` しただけで起きる。**

  **塞ぐときの形**: `st_nlink > 1` のときだけ `WHERE data_id = @shared_data_id` に広げる
  (**先例 = `UpdateSizeForAllLinksInTx`**。`st_nlink > 1` に限るのは **Citus で multi-shard になる**ため)。
  **`st_ctime` も一緒に配る**。**メタデータ write-back の台帳側 (`CoalesceMode` / `CoalesceOwner` /
  `CoalesceTimestamps`) にも同じ配布が要る** — そちらは tx を張らずに台帳で畳むので、
  **DB 側だけ直すと write-back on のときに配布が落ちる**。
  **回帰は `truncate` の先例と対にする** (`test_truncate_zero_updates_all_links` の隣に
  `chmod` / `chown` / `touch` 版を置く)。

- **`st_ino` の安定性を `data_id` の不変性に依存させ続けてよいか**。本決定では依存させる。
  独立させるなら inode 側に安定 id 列を持たせる (DDL 変更 + 既存 FS の移行が要る) — **v0.3 以降**。
- **ディレクトリの `st_ino`** は `inode.Id` 由来のままで、`data_id` とは別名前空間 (符号ビットで分離)。
  ここは本変更で触らない。
- **他マウントの兄弟キャッシュは落ちない**。書き込みの通知は `Notify(inodeIds: [自分], dataIds: [data_id])`
  で、**兄弟の inode id を載せていない**。受け側は `dataIds` で content キャッシュを捨てるが
  `InodeCache` は自分の分しか無効化しないので、**別マウントでハードリンク兄弟を stat すると
  古い `st_size` を返し得る**。閉じるなら受け側で `dataIds` を見て `InvalidateByDataId` を撃つのが素直
  (走査が O(キャッシュ件数) なので、データ書き込みごとに撃つ前に実測すること)。自動テストは未整備。
- **flush 経路 (write-back) の配布判定だけキャッシュ依存が残っている** (`HasHardLinkSiblings(long)`)。
  A-1 の既存設計で、そこも `RETURNING st_nlink` に寄せれば揃う。
