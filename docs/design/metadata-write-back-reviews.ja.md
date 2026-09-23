# metadata write-back のレビュー記録 (ラウンド A / B)

> **道順**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) ›
> [metadata-write-back.md](metadata-write-back.md) › **本書**
>
> **この doc が正である範囲**: 1e ステージ 2 に対する敵対的レビューで出た指摘と、その修正の
> **時系列の記録**。ラウンド A (A-1〜A-10) とラウンド B (B-1〜B-13) の as-built はここが正。
> **新しいレビュー結果と修正はこの doc に追記する。**
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [metadata-write-back.md](metadata-write-back.md) | **確定設計と実装ステータス**。「いまどう動くか」はそちら |
> | [write-back.md](write-back.md) | データ本体の遅延書き (1d)。**ライブ無効化の二相化**はレビュー由来なので本書にある |
> | [windows-parity.md](windows-parity.md) | 同じ指摘の Windows 側の受け入れ (B-1 / B-7 / B-9 / B-12 の配線) |
> | [../tests.md](../tests.md) | 回帰テストの件数と実行方法 |

## ステージ 2 の敵対的レビュー結果 (**A-1〜A-10 / B-1〜B-13 とも 対応済**)

> レンズ 2 本 (**耐久性・クラッシュ一貫性・POSIX** / **並行性・状態遷移・運用**) + テスト作成で出た指摘を
> 重複を畳んでまとめたもの。**ラウンド A (A-1〜A-10) は 修正済** (下 §ラウンド A の修正)、
> **ラウンド B (B-1〜B-13) も 対応済** (下の各 §B-N が as-built)。`write_back_metadata = on` の経路だけに効く
> (既定 off の回帰はゼロ = e2e 43+1skip / writeback.sh 8/8 で確認済み)。
> **上位 5 件はレビュー側でコードに当てて確証を取った** (CONFIRMED)。

### ラウンド A (安全側・先に直す)

| # | 指摘 | 該当 | 深刻度 |
|---|---|---|---|
| A-1 | **ハードリンク兄弟経由の write がクラッシュ不要で無音に消える**。`DirtyFile.InodeId` は「最初に書いた inode」で固定 (`init`) なのに flush はその inode の生存だけを見て、消えていたら dirty を破棄する。`echo A > f; ln f g; echo B > g; rm f` で **B が消え `cat g` が A** を返す (close は 0・報告点なし)。1d では close が同期 flush していたので race が要ったが、**close-no-flush では決定的に負ける**。兄弟の穴として `st_size` / `st_mtime` も間違った inode に書かれる (`g` に書いて `fsync(g)` しても `g` のサイズが古い) | `Api.cs:2271-2277` / `:2288` / `DirtySet.cs:210` | **Critical** |
| A-2 | **エラーステートが実運用で発火しない**。`consecutiveFlushFailures` は**プロセス全体で 1 個**のカウンタで、**データ側の flush も同じカウンタを叩く** (`Api.cs:2251,2256`)。恒久失敗が 1 件あっても他が成功するたびにリセットされ 5 連続に届かない。→ 報告経路 ③ が丸ごと死ぬ。**back-pressure (④) の健全性は ③ に完全依存**しているので、③ を直せば ④ も直る | `Api.WriteBackMetadata.cs:677-698` | **Critical** |
| A-3 | **`RENAME_EXCHANGE` が置換に化けてデータを破壊する**。FUSE 層は `RENAME_NOREPLACE`(=1) しか見ておらず、交換要求が「target 削除 + source 移動」として**単一 tx で原子的に commit** される (errno は 0)。ステージ 2 以前から存在するが単一 tx 化で不可逆性が増した。**未対応フラグに `-EINVAL` を返すだけで塞げる** | `src/fuse/src/FileSystem.cs:418-465` | **Critical** |
| A-4 | **同期 close 印 (ヒューリスティック b) が漏れて上限で無効化される**。`MarkSyncOnClose` は 1 箇所 (`TruncateData`)、`ClearSyncOnClose` も 1 箇所 (`FlushInode`) だけで、**削除 / 純キャンセル / 置換 / discard でクリアしていない**。inode id は再利用されないので単調増加し、**上限 4096 到達で以後すべての truncate に印が付かない = (b) が黙って無効化**される | `Api.WriteBackMetadata.cs:717-734` / `Api.cs:2077,2462` | **High** |
| A-5 | **印の粒度が間違っている**。`truncate` が壊すのは **data_id 単位**のチャンクなのに印は inode 単位なので、**ハードリンク兄弟経由の close が印を見ない**。「fd 単位でなく inode 単位」という判断 (差分 3) は正しかったが、正しい粒度は **`DirtyFile` (data 単位)** | `Api.WriteBackMetadata.cs:741-751` | Medium |
| A-6 | **印を flush の後に落とすので、flush 中に積まれた dirty のぶんまで印が消える** (capture-and-clear にすべき) | `Api.cs:2069-2078` | Medium |
| A-7 | **`truncate -s 0 f; cmd >> f` がゼロ長ゴミになる** (テストで再現・`wbmeta.sh` の唯一の FAIL)。coreutils の `truncate` は open → `ftruncate` → **close** するので印がその close で消費され、続く別 fd の append は遅延に戻る。**ステージ 2 が塞ぐと宣言した「旧を即消して新を遅らせる」窓**。A-4/A-5/A-6 と同根 (印を「その inode が次に書かれて flush されるまで」残せば閉じる) | `test_meta_truncate_syscall_close_is_synchronous` | **High** |
| A-8 | **期限判定が sweep の外なので数時間ブロックし得る**。`ApplyInodeBackPressure` / `FlushAllForShutdown` は **pending 全件に flush を撃ってから**期限を見る。監査パーティション ensure の恒久失敗は `lock_timeout = 5s` の DDL 1 発 = 最悪 5 秒/件で、しかも**失敗をキャッシュしない**ので毎回撃ち直す → pending 4096 件で **round 1 だけで約 5.7 時間** = 「無期限ブロックはハングと区別できないから期限を切った」という設計目標が破れる。unmount も同型なので systemd の `TimeoutStopSec` で SIGKILL → **喪失レポートも exit 4 も出ないまま全 pending 喪失** | `Api.WriteBackMetadata.cs:644-667` / `:768-782` / `Api.cs:2607-2637` | **High** |
| A-9 | **`(カウンタ, エラーステート)` が非原子**なので「カウンタ 0 なのにエラーステート ON」= 自力で戻れない恒久 read-only に落ちる順序がある (解除契機が flush 成功だけで、その flush を生む write 自体をブロックしているため) | `Api.WriteBackMetadata.cs:677-711` | High |
| A-10 | **既存ファイルの上書きは close 後もクラッシュで「ちぎれる」**。`Mount.md` の契約表は「単一ファイルの原子性は向上する」と書いているが、それが成り立つのは **pending 生まれのファイルだけ**。persisted inode への上書きは interval ごとに別 tx で部分 commit されるので、**前半が新・後半が旧のキメラ**になり得る (契約表がカバーしていない)。**技術的に塞ぐなら「persisted inode への write が始まった時点で同期 close 印を付ける」** = close-no-flush を新規作成限定にする。**取り分は create 側なので性能は落ちない見込み** (要実測) | `docs/Mount.md:133-139` / `Api.cs:2093-2106,2217-2233` | **High** |

### ラウンド A の修正 (as-built)

> 上の表の指摘に対して実際に入ったもの。**指摘の表そのものは記録として残す** (何を直したかは、直す前に
> 何が起きていたかとセットでないと読めないため。表の中の行番号も当時のまま = 修正前を指す)。
> 検証環境は **dev サーバ実機** (DB `xdata_ebe_db` / schema `pgfs_test` / Citus rf=2・docker 不使用)。
> 結果は Linux e2e (off/on とも 43 passed + 1 skip)・`writeback.sh` 8/8・
> `wbmeta.sh` **17/17** (16 → 17・A-1 の回帰テストを追加)・`negcache.sh` 7/7。

| # | 入ったもの | 該当 |
|---|---|---|
| A-1 | `DirtyFile.InodeId` を `init` → `set` にし、flush tx で **固定していた inode が消えていたら同じ data 本体を参照する兄弟 inode を探して付け替える** (`ResolveFlushSibling`)。付け替えは `FlushRetargetException` で `FlushLocked` のリトライループへ戻して **tx を張り直す** (ロック順 inode → data を守るため、取得済みの data ロックの後ろに別 inode のロックを足さない)。参照する inode が 1 つも残っていないときだけ従来どおり破棄する。あわせて **`st_nlink > 1` の inode の flush は size/mtime を `WHERE data_id = ...` で全リンクに配る** (どのリンク経由で stat しても同じ値になる・`CreateHardLink` の `st_nlink` +1 と同じ形。Citus では multi-shard UPDATE になるので `st_nlink > 1` のときだけ) | `Api.cs` `FlushTransaction` / `UpdateInodeSizeAndMtimeInTx` / `HasHardLinkSiblings` / `DirtySet.cs` |
| A-2 / A-9 | (**追い修正あり** — 下 §A-2 / A-9 の追い修正) 連続失敗カウンタを **プロセス全体で 1 個 → flush 対象ごと** (`flushFailuresByTarget`・キーは `LockTargets` と同じ符号規約で inode は負値・data は正値) に変更。他の成功でリセットされなくなり、**恒久的に失敗する 1 件が自力で閾値 5 に到達する**。カウンタとエラーステートは `flushFailureGate` の下で一緒に動かし、**解除条件を「閾値に達した対象が 1 つも無い」というカウンタの状態から導く** ので「カウンタは空なのにエラーステートが残る」順序が存在しない | `Api.WriteBackMetadata.cs` `NoteFlushFailure` / `NoteFlushSuccess` |
| A-3 | FUSE の `Rename` で **`RENAME_NOREPLACE` 以外のフラグが立っていたら `-EINVAL`** を返す (先頭でフラグを検査してから何もしない)。`RENAME_EXCHANGE` (2) / `RENAME_WHITEOUT` (4) の要求が「target 削除 + source 移動」として単一 tx で commit される破壊を塞ぐ | `src/fuse/src/FileSystem.cs` `Rename` |
| A-4 / A-5 | 同期 close 印を **inode 単位 → inode と data 本体の両方** (同じ符号規約) に積む。inode 側だけだと兄弟経由の close が印を見ず、data 側だけだと `truncate 0` で data 行ごと消える対象を追えないため両方要る。**上限 4096 に達したときは黙って印を捨てず、`syncOnCloseOverflow` を立てて全 close を同期に格上げする** (fail-safe。データ安全の仕掛けが静かに無効化されるより、性能を落として 1d の挙動に縮退するほうがよい)。**削除・純キャンセルでも印を落とす** ので単調増加しない | `Api.WriteBackMetadata.cs` `MarkSyncOnClose` / `AddSyncOnCloseKey` / `ClearSyncOnClose` / `RequiresSyncClose` |
| A-6 / A-7 | 印を落とす条件を **「実際に未 flush を書き切ったときだけ」** に変更 (`FlushInode` が flush 前に `HasUnflushed` を取り、flush 後にも見る)。① 何も書いていない close では落とさない → `truncate -s 0 f; cmd >> f` の印が truncate 側の close で消費されなくなる (**`wbmeta.sh` の唯一の FAIL が解消**) ② flush 中に積まれた dirty が残っていれば印も残す | `Api.cs` `FlushInode` / `HasUnflushed` |
| A-8 | 期限判定を **1 巡の中へ**。`FlushAll(DateTime? deadline)` を足して `FlushAllPendingInodes` / data ループ / `ApplyInodeBackPressure` の内側で期限を見る。1 件あたりの失敗が遅い障害 (監査パーティション ensure の `lock_timeout` 5 秒) でも round 1 が何時間にもならない | `Api.cs` `FlushAll` / `Api.WriteBackMetadata.cs` `ApplyInodeBackPressure` / `FlushAllForShutdown` |
| A-10 | **persisted inode (既に DB にある実体) への write が始まったら同期 close 印を付ける** = close-no-flush を **pending-born 限定**にする。既存ファイルの上書きが interval ごとに別 tx で部分 commit され「前半が新・後半が旧」のキメラになる窓を塞ぐ。close-no-flush の取り分は create 側なので bulk copy の性能は落ちない見込み → **2026-09-19 に実測** ([performance.md §A-10 の影響](performance.md))。**1 回ずつの上書きは総計で差が出ない** (測定限界以下)。代償は **1 ファイルを開き直して上書きし続けるワークロード** に集中し、A-10 無しとの比は **125×** になるが、**その値は metadata off の既定とぴったり同じ** (112.8 ms/close) = 既定より遅くはしていない | `Api.cs` `WriteDataBuffered` / `IsPendingBorn` |
| (追加) | **`Api.Rename` に source == replaceTarget の no-op 成功判定**。置換は「target を削除してから source を UPDATE する」ので同一対象だと自分を消す。Linux は VFS が `rename("a","a")` を弾くので到達しないが、**Dokan 経路には同じ保護が無い**ため Core を最後の防波堤にする (Dokan 側からの依頼) | `Api.cs` `Rename` |

### ラウンド B (ノブ + 残り)

| # | 指摘 | 深刻度 |
|---|---|---|
| B-1 | **ノブ `mount.write_back_metadata_exclusive_create` (`write_through` / `defer`・既定 `write_through`) を追加** (決定)。`defer` で `O_EXCL` create も pending にする。同一マウント内の排他は台帳の `TryAdd` が (parent, name) 衝突で EEXIST を返すので維持され、失うのは cross-client の排他だけ。**`defer` 由来の inode が flush 時に名前衝突したら last-flush-wins で相手を消してはいけない** — アプリに「自分だけが作った」と返している以上、相手 (ロックファイルかもしれない) を黙って DELETE + INSERT で潰すのは最悪なので、**種別違い衝突と同じ error latch** にする。狙いと実測は [performance.md](performance.md) | 機能 |
| B-2 | ✅ **修正済** (下 §B-2 の as-built)。**喪失レポートと exit 4 が既定の起動方法では誰にも届かない**。daemonize すると親は `PGFS_MOUNTED_OK` 読取後に `return 0`、子は `Console.SetOut/SetError(TextWriter.Null)` してから `return 4`。しかも `Dispose` が `{prefix}mounts` の行を DELETE するので **DB 側にも痕跡が残らない** → 喪失を DB 側 (監査行 `op=writeback_loss` or mounts 行に `unflushedLoss`) に残す + 起動時警告 | High |
| B-3 | ✅ **修正済** (下 §B-3 の as-built)。**`create → read → unlink` が痕跡ゼロで成立する**。純キャンセルの監査ペアはメモリキューだけなので `kill -9` / PG 障害で消える = 設計 §監査 が明示的に塞ぐと書いた回避チャネルが復活。件数は少ないので **`audit.enabled` 時は即 write-through** が妥当 | High |
| B-4 | ✅ **解消** (下 §B-4)。**`audit.enabled` を live で off にすると以後の unmount が必ず期限満了 + exit 4**。`FlushOrphanAudits` が `!auditEnabled` で return するのにキューは `UnflushedCount()` に数えられるため、**実喪失ゼロなのに「以下は失われます」レポート**が出て systemd が failed 扱いにする | Medium |
| B-5 | ✅ **修正済** (下 §B-5 の as-built)。**エラーステートの報告が heartbeat 経由なので、DB に書けない障害では `status` が古い緑を出す**。赤行に heartbeat 経過秒を併記 + 遷移時は即書き | Medium |
| B-6 | ✅ **修正済** (下 §B-6 の as-built)。**喪失レポートが 32 件打ち切り + パスなし** (親も pending なら DB に行が無くパス復元不能)。パス組み立て + 「他 N 件 (内訳)」を出す | Medium |
| B-7 | ✅ **修正済** (下 §B-7 の as-built)。**エラーステートが破壊操作を止めない** (`TruncateData` / `DeleteInode` / rename 置換は通る)。「新しいデータは受け付けないが古いデータは消し続ける」状態になる | Medium |
| B-8 | ✅ **修正済** (下 §B-8 の as-built)。**live 設定変更が NOTIFY リスナースレッドを最大 `flush_timeout_ms` 占有**する (二相 flip の `FlushAll` / back-pressure をその場で実行)。その間 **他クライアントの invalidate が一切処理されない**。重い live 適用は専用ワーカへ | Medium |
| B-9 | ✅ **修正済** (下 §B-9 の as-built)。**二相 flip に check-then-act が残る** (`MetadataWriteBack` の判定と `TryAdd` が不可分でない) ので flip 完走後に pending が生まれ得る。`TryAdd` 側に intake フラグを渡して台帳ロック内で判定する。あわせて **実効モード (`intakeClosed`) が status に出ない** (flip 中は `on` と表示される) | Medium |
| B-10 | ✅ **修正済** (下 §B-10 の as-built)。**`EnsureFlushDataRow` の Rekey で `chunk_size` の不一致を見ていない** (`DirtyFile.ChunkSize` は `init`)。勝者行の chunk_size が違うとチャンク境界がズレたデータ破壊。確率は低い (クライアント間で `default_chunk_size` が違う前提) | Medium |
| B-11 | ✅ **修正済** (下 §B-11 の as-built)。**`NoteFlushSuccess` が commit 後の後処理の後**にあるので、`Notify` 等で例外が出ると commit 済みなのに「flush に失敗しました」と出てカウンタもリセットされない。`tx.Commit()` 直後へ移す。あわせて **`Notify` を NSGate の外へ** (DB がハングすると全メタデータ操作が詰まる) | Low |
| B-12 | ✅ **修正済** (下 §B-12 の as-built)。**SIGTERM が常に無視される**ので、長い shutdown flush 中に 2 発目を送っても効かず SIGKILL しか残らない。2 発目以降は「即諦めて喪失レポートを出して終了」に格上げ + `TimeoutStopSec > write_back_flush_timeout_ms` の指針を doc に | Low |
| B-13 | ✅ **対応済** (下 §B-13)。ドキュメント: ~~`Mount.md` の「その fd の close は同期」が as-built と食い違う~~ (** A-4〜A-7 の修正と同時に `Mount.md` を as-built へ更新済**) / 喪失レポートの「最大 32 件」を明記 / 「ファイル間の因果」に `rm f; cp new f` の具体例 / **監査は write-back 有効時は耐久でない**ことを `audit-log.md` に明記 | Low |

### B-1 の確定設計 + as-built (**実装済**)

> ノブ `mount.write_back_metadata_exclusive_create` の設計。**設計どおり実装し、
> dev サーバ実機で検証した** (差分は下の §as-built 差分)。実測は **`rsync` が 3.28×**
> ([performance.md §B-1 `defer` の実測](performance.md))。
> 狙いは「`rsync` / `cp` が `O_CREAT|O_EXCL` を使うせいでヒューリスティック (c) が全部 write-through に
> 落とし、1e の畳み込みが 1 回も起きない」([performance.md §1e ステージ 2 の効果](performance.md)) を、
> **失う排他を明示したうえで**選べるようにすること。

##### 値と既定

| 値 | 意味 |
|---|---|
| **`write_through` (既定)** | 現行と同じ。`O_EXCL` 付き create は pending にせず同期作成し、排他判定を DB の一意制約に委ねる |
| `defer` | `O_EXCL` 付き create も pending にする。**同一マウント内の排他は維持され、cross-client の排他だけを失う** |

**既定は変えない**。cross-client の排他は「失ってよい」と言える人だけが降りるべき段差であり、
既定で降ろすと `git index.lock` が 2 クライアントで同時に取れる FS になる。

##### 型と reload ポリシー

- `Field.cs` に **`EnumField`** (許可値の配列を持ち、`Parse` が許可外を例外にする record) を追加する。
  CLI / TOML / DB のどの入口も `Parse` を通るので、**検証が 1 箇所で済む**。`StringField` + 適用時チェックにすると
  「DB には入るが mount 時に落ちる」設定を作れてしまう。
- フィールド定義: `Scope = "mount"` / `Key = "write_back_metadata_exclusive_create"` /
  `CliOptions = ["--write-back-metadata-exclusive-create"]` / `SaveTo = File` /
  `AppliesTo = Mount | Assign` / `DefaultFn = () => "write_through"`。
- **`Reload = Live`**。`write_back_metadata` 本体のような**二相 flip は要らない** — このノブは
  *これから来る create* の行き先を変えるだけで、既に台帳に載っている pending の扱いは変えないため
  (下の「印は create 時に確定させる」)。drain も intake 停止も不要。

##### 印は create 時に確定させる (Live 変更との競合を断つ)

`PendingInode` に **`BornExclusive` (bool・`init`)** を足し、**`O_EXCL` で作られた pending かどうかを
生成時に焼き付ける**。flush 時にノブを読み直してはいけない — 読み直すと、`defer` で作った inode が
flush までの間に `write_through` へ切り替えられた瞬間に「last-flush-wins で相手を消してよい」に化け、
**アプリに「自分だけが作った」と返した約束が後から破られる**。

経路: `FileSystem.Create` (`fi.flags & O_EXCL`) → `Api.CreateFile(..., exclusive)` → `Api.InsertInode`。
`InsertInode` の分岐を次のように変える。

```
現行: if (MetadataWriteBack && !exclusive)                      → pending
B-1 : if (MetadataWriteBack && (!exclusive || ExclusiveDefer))  → pending  (exclusive を TryAdd まで渡す)
```

##### 何が保たれ、何が失われるか

- **保たれる (同一マウント内)**: `DirtyNamespace.TryAdd` が `(parent, name)` の衝突で null を返し、
  呼び出し側が **EEXIST** にする。これは `defer` でも一切変えない。同じマウントを使う限り
  `O_EXCL` の意味論は完全に維持される。
- **失われる (cross-client)**: 別マウントは pending inode を見られない (DB に行が無い) ので、
  **両方の `O_EXCL` create が成功を返す**。これが `defer` の代償そのものであり、隠してはいけない。

##### 衝突したときに何が起きるか (**Windows 側の受入ポイント**)

flush tx で名前衝突を解決する `AdoptOrRemoveOccupant` に、**`BornExclusive` なら占有者を消さない**分岐を足す
(種別違い・非空ディレクトリと同じ扱い = **throw して error latch**)。

| 局面 | 挙動 |
|---|---|
| 勝者 (先に flush した側) | **無傷**。DB に行があり、内容も属性もそのまま |
| 敗者 (後から flush した側) | flush が失敗し続ける。pending は台帳に残り、**内容は DB に届かない** |
| 敗者マウントの状態 | 同じ対象の失敗が `FlushFailureStateThreshold` (5) に達すると**エラーステート** = 新規 write / create が `-EIO`・`pgfsctl status` Layer 3 に赤表示。**A-2 でカウンタを対象ごとに分離したので、他ファイルが成功していても閾値に到達する** (ラウンド A を先にやった理由がここに出る) |
| 運用の回復手段 | **敗者側でそのファイルを `unlink` する**。pending inode の削除は `CancelPendingInode` の純キャンセルなので DB を触らずに台帳から消え、error latch も解ける (削除経路は `ThrowIfWriteBackErrorState` を通らないので、エラーステート中でも実行できる) |

**監査行は出ない**。flush tx は rollback するので、その tx で監査を書くことはできない。
衝突の事実は `DirtyNamespace.CountConflict` の統計・Error ログ・unmount 時の喪失レポートに現れる。
(監査に残したいなら B-3「孤児監査の即書き」と同じ仕組みが要る = 別項目。)

##### 位置づけと、運用に気づかせる仕掛け

このノブは **「単一クライアント運用と分かっている bulk copy のためのチューニング」** である。
**複数マウントで同じ FS を使うなら `defer` にしない**。Windows 側の確認 で、
cross-client の相互排除として実際に効いているのは **`CREATE_NEW` の DB 一意制約だけ**だと分かっている
(Dokan アダプタは share mode を強制せず `LockFile` / `UnlockFile` も常に成功を返すため。
[windows-parity.md](windows-parity.md))。`defer` はその最後の 1 本を抜く操作になる。

そのため次の 2 つを実装に含める。

1. **`pgfsctl status` の実効設定に出す**。ここは**自動ではない** — `Api.BuildConfigJson` は
   スキーマを走査せず**手書きの辞書**なので、`["mount.write_back_metadata_exclusive_create"]` の 1 行を
   明示的に足さないと Layer 3 に出ない (他の `write_back_*` と同じ扱い)。運用が最初に見る場所なので必須。
2. **`defer` で起動したとき、`{prefix}mounts` に他の生きたマウントが居れば Warning を出す**。
   判定材料 (`{prefix}mounts` の登録と heartbeat) は Core が持っているので、**Core 側 (`Api` の起動処理) に置く**。
   Dokan / FUSE のどちらから起動しても同じ警告が出るようにするためで、アダプタ側には置かない。
   **強制はしない** (起動を拒否すると、正当な単一クライアント運用が残骸の登録行で止まる)。

##### 範囲外

- **`mkdir` は対象外**。`Api.CreateDirectory` は今も `exclusive: false` 固定で、このノブでも変えない
  (同期化すると pending ディレクトリが生まれず祖先チェーン INSERT が到達不能になる — ステージ 2 as-built 差分 5)。
  `mkdir` をロックプリミティブに使うツールは **`write_back_metadata = on` の時点で** cross-client 排他を
  失っている。これはこのノブの前からの性質であり、[Mount.md](../Mount.md) の可視性の行に既に書いてある。
- `O_EXCL` 以外の create (`plain_create` / `O_TRUNC`) の扱いは変えない。

##### 実装時に一緒に更新する doc

`settings-matrix.md` (全項目マトリクス) / `Mkfs.md` の既定値表 + TOML 例 / `pgfs.toml.example` /
[Mount.md](../Mount.md) の「失うもの」表の**可視性の行** (`O_EXCL` は同期作成なので排他が維持される、と
書いてあるので `defer` の場合の例外を足す) / [Pgfsctl.md](../Pgfsctl.md) (status に出る項目が増えるため)。

##### as-built 差分 (実装して分かったこと)

設計との差は 1 点だけで、あとは設計どおりに入った。

1. **`defer` 警告の「他の稼働マウント」判定が素朴**。`{prefix}mounts` の行数を数えているだけなので、
   **異常終了 (`kill -9` など) で DELETE されなかった残骸の行も数える**。実際、テストで `kill -9` を
   繰り返している dev 環境では「他に稼働中のマウントが 91 件あります」と出た (実際の稼働は 1 件)。
   **heartbeat の鮮度で絞るべき**だが、`{prefix}mounts` の残骸掃除そのものが未着手 (status の表示にも
   同じ問題がある) なので、**この項目だけ先に直すと整合が取れない**。残骸掃除とまとめて扱う。
   警告が過剰に出ても実害は無い (拒否はしないため) ので、現状のまま残す。

検証 (dev サーバ実機・`xdata_ebe_db` / schema `pgfs_test` / Citus rf=2):
Linux e2e は `write_back_metadata` **off / on / on+defer の 3 通りとも 43 passed + 1 skip**、
`writeback.sh` 8/8、`wbmeta.sh` **20/20** (17 → 20)、`negcache.sh` 7/7。

##### テスト計画

`tests/linux/wbmeta.sh` に 3 件。cross-client は**2 マウントを立てずに、psql で衝突行を直接 INSERT する
fault injection** で決定的に作れる (既存の `test_meta_error_state_blocks_and_clears` と同じ手)。

1. `test_meta_exclusive_create_defer_is_pending` — `defer` で `O_EXCL` create したとき、close 直後に DB に行が無い
   (= pending になっている) こと。既定 `write_through` の `test_meta_exclusive_create_is_write_through` と対になる。
2. `test_meta_exclusive_create_defer_same_mount_eexist` — `defer` でも**同一マウント内の 2 回目の `O_EXCL` create は
   EEXIST** になること (失ったのは cross-client だけ、の回帰)。
3. `test_meta_exclusive_create_defer_conflict_latches` — pending のまま psql で同名行を INSERT → flush を促す →
   **占有者の行が消えていない**こと + 敗者がエラーステートに入ること + **unlink で回復できる**こと。

性能の測り直し (`defer` で `rsync`) は実装後に別途。計測の作法は
[performance.md §1e ステージ 2 の効果](performance.md) の「計測の前提」を踏襲する
(**デーモンの消滅まで待つ** / **毎回 md5 で整合性を確認する**)。

### B-12 の as-built (**実装済**)

> **2 発目の停止シグナルで flush の粘りを打ち切る**。

**問題**: `mount.pgfs` の SIGTERM / SIGINT ハンドラは **何発来ても常に握り潰していた** (`ctx.Cancel = true` /
`e.Cancel = true`)。shutdown flush は既定 `mount.write_back_flush_timeout_ms` = 30000 ms 粘るので、
その間「止まらないプロセス」に見える。待てない運用者に残る手は **SIGKILL だけ**だが、SIGKILL では
**喪失レポートも `{prefix}mounts` の墓標 (B-2) も残らない** — 一番知りたいときに一番情報が消える。

**修正** (`src/mount/src/Program.cs`): 停止シグナルを数えて段階的に扱う。SIGINT と SIGTERM で**同じカウンタ**を使う
(運用者にとっては同じ「止めろ」なので、種類を変えて 2 回送ったときに段階が進まないのは驚きになる)。

| 回数 | 動作 |
|---|---|
| 1 発目 | 従来どおり graceful unmount (`LazyUnmount`) |
| 2 発目 | **`Api.AbandonFlush()`** で粘りを打ち切る + もう一度 `LazyUnmount`。次の期限判定で抜け、**喪失レポートを出してから** exit 4 |
| 3 発目以降 | シグナルを握り潰すのをやめる (`Cancel = false`) = .NET 既定の即時終了。ここまで来たら報告経路は諦める |

`Api.AbandonFlush()` は `flushAbandoned` (volatile) を立てるだけで、**進行中の 1 tx は中断しない**
(中断すると DB 側が中途半端になり得る)。効くのは**期限判定**で、そのために
`DeadlineReached` を `static` からインスタンスメソッドへ変え、`flushAbandoned` が立っていたら
無条件に「期限到達」を返すようにした。`FlushAllForShutdown` / `FlushAllPendingInodes` /
`FlushAll` のデータループ / `ApplyInodeBackPressure` の判定が**すべて同じ関数を通る**ので、
打ち切りはどの段にいても効く。`ReportUnflushedLoss` は打ち切り経路でも必ず通る
(**2 発目は「待つのをやめろ」であって「黙って捨てろ」ではない**)。

**フラグは立てっぱなしにする**。一度「もう待たない」と言われた後に粘りを再開してよい理由が無い。

**systemd の指針**: `TimeoutStopSec` は **`mount.write_back_flush_timeout_ms` より長く**取ること。
短いと SIGTERM の直後に systemd が SIGKILL を撃つので、この段階分けも喪失レポートも全部飛ぶ。
(例: `write_back_flush_timeout_ms = 30000` なら `TimeoutStopSec=60s`)。

**自動テストは書いていない**。「長い shutdown flush の最中に 2 発目を送る」を決定的に作るには
flush を人為的に遅くする必要があり、書けるのはタイミング依存のテストだけだったため (B-8 / B-9 ① と同じ判断)。

**実機で手動確認した** (dev サーバ・schema `pgfs_test`・Citus rf=2・2026-09-19):
`--write-back --write-back-metadata --write-back-interval-ms 600000 --write-back-flush-timeout-ms 600000`
で起動して空ファイルを 1200 個作り (= pending を抱えたまま背景 flush が来ない状態)、
`kill -TERM` → 1 秒後にもう一度 `kill -TERM`。ログは

```
[Information] SIGTERM を受信しました。アンマウントを試みます。
[Warning]     SIGTERM を 2 回受信しました。flush の待機を打ち切ります (未 flush は失われます)。
[Warning]     write-back: 打ち切り要求を受けたので unmount 時の flush を中止します (残り 1154 件)
[Error]       write-back: 期限内に flush できなかった未 flush が 1154 件あります。…
```

で、**2 発目から 4 ms で打ち切り、プロセスは 1 秒以内に消えた** (期限は 600 秒に設定してある)。
`{prefix}mounts` の墓標 (B-2) にも `unflushedLoss: 1154` とパス付きの `lost` 配列が入り、
**打ち切り経路でも報告は残る**ことを確認した。

### B-12 の Windows 配線 (**実装済**)

> **2 発目の停止シグナルを Windows でも効かせる**。Core の `Api.AbandonFlush()` を `assign.pgfs` から呼ぶ。

**踏み込んで分かったこと**: Windows は「2 発目が握り潰されていた」のではなく、**flush 中はハンドラが居なかった**。
`Console.CancelKeyPress` の購読は `Pgfs.Dokan.FileSystem.Run()` の中だけにあり、`finally` で解除していた。
1 発目で `Run()` はすぐ返るので、**一番長い区間 — `api.Dispose()` の shutdown flush (既定
`mount.write_back_flush_timeout_ms` 30 秒 + retry) — が無防備**で、そこへ Ctrl+C が来ると .NET 既定の
即時終了になる。つまり Linux の SIGKILL に相当する終わり方 (喪失レポートも `{prefix}mounts` の墓標 (B-2) も
exit 4 も残らない) が、**Ctrl+C 2 回で起きていた**。

**修正**: 購読を **tool exe (`src/assign/src/Program.cs`) へ引き上げ**、マウント〜`api.Dispose()` 完了までを
1 つのハンドラでカバーする。段数・意味・ログ文言は `mount.pgfs` (Linux) と同一にした:

| 回数 | 動作 |
|---|---|
| 1 発目 | アンマウント要求 (`FileSystem.RequestStop()`) |
| 2 発目 | **`Api.AbandonFlush()`** で粘りを打ち切る。次の期限判定で抜け、**喪失レポートを出してから** exit 4 |
| 3 発目以降 | 握り潰しをやめて .NET 既定 (即時終了) に戻す。ここまで来たら報告経路は諦める |

併せて `FileSystem.SignalStop` を **public `RequestStop()`** に変え (2 回目以降は何もしないので破棄後に
呼んでも無害 = ハンドラ側が生死を気にしなくてよい)、`Run()` からは購読を外した。例外経路でも
**購読を外す前に** `api.Dispose()` を撃つ (`using var apiLifetime` の破棄は `finally` の後なので、
そこだけハンドラ不在に戻ってしまう)。

**Windows 固有の落とし穴** (Linux と揃わない部分):

- **コンソールの × / ログオフ / シャットダウンはこのハンドラを通らない**。`CTRL_CLOSE_EVENT` 系は Windows が
  **~5 秒**で打ち切るため、既定 30 秒の flush 期限とは噛み合わない。Linux の `TimeoutStopSec` に当たる
  調整先が無いので、**長い flush を抱えるなら × ではなく Ctrl+C で止める**運用にする。
- **`Stop-Process` は `TerminateProcess`** でハンドラも Dispose も走らない (= `kill -9` 相当)。
- **Ctrl+Break も同じハンドラに来る** (`ConsoleSpecialKey.ControlBreak`)。`e.Cancel = true` が Break でも
  効くことは実測で確認した。

**自動テストは書いていない** (Linux の B-12 と同じ判断 — 決定的に作るには flush を人為的に遅くする必要があり、
書けるのはタイミング依存のテストだけになる)。加えて Windows では **`AttachConsole` +
`GenerateConsoleCtrlEvent(CTRL_C_EVENT)` で撃った Ctrl+C が相手に届かない**ことを実測した
(`GenerateConsoleCtrlEvent` は true を返し、対象のコンソールにも正しく attach できているのに、相手は反応しない)。
**`CTRL_BREAK_EVENT` は届く**ので実機確認はそちらで行った。自動化するならこの違いが前提になる。

**実機で手動確認した** (windows_client / Dokan 2.3.1 / PG=pgsql_server の `pgfs` スキーマ・Citus rf=2・2026-09-19):
`--write-back --write-back-metadata --write-back-interval-ms 600000 --write-back-flush-timeout-ms 600000`
で起動し (= 背景 flush が来ない状態)、空ファイルを 1200 個作って pending を溜めてから Ctrl+Break を 2 回。

```
[Information] Ctrl+Break を受信しました。アンマウントを試みます。
[Warning]     Ctrl+Break を 2 回受信しました。flush の待機を打ち切ります (未 flush は失われます)。
[Warning]     write-back: 打ち切り要求を受けたので unmount 時の flush を中止します (残り 1057 件)
[Error]       write-back: 期限内に flush できなかった未 flush が 1057 件あります。以下は失われます…
[Error]         失われる pending file: /abandon_probe/f00168 (id:9443 size:0 state:Dirty error:(none))
[Information] exited (code 4)
```

**2 発目 (22:45:47.370) から 152 ms で終了**し (期限は 600 秒に設定してある)、`{prefix}mounts` の墓標にも
`unflushedLoss: 1057` が入った (`pgfsctl status` が `!! write-back UNFLUSHED LOSS` として拾う)。
**Windows でも exit 4 は呼び出し元に届く** (assign は前景プロセス)。

### B-11 の as-built (**実装済**)

> **成功の確定を commit 直後へ** + **通知 (`pg_notify`) を NSGate の外へ**。

##### ① 成功を commit 直後に確定させる

**問題**: `FlushLocked` / `FlushPendingChain` のどちらも `NoteFlushSuccess` が
**commit 後の後始末 (Rekey / キャッシュ無効化 / 通知) より後**にあった。後始末で例外が出ると
**commit 済みなのに**「flush に失敗しました」のログが出て、`FlushLocked` では
`NoteFlushFailure` まで走る。**恒久的に後始末で落ちる状況では 5 回で
エラーステート = 新規 write を `-EIO`** という、実体の無い障害を作れてしまう。

**修正**: 両経路とも **`NoteFlushSuccess` を commit 直後 (後始末の前) へ**移した。
`FlushLocked` は `try` の範囲を **commit までに絞り**、後始末を `catch` の外に出している
(後始末の例外が `NoteFlushFailure` を通らないことを構造で担保する)。

##### ② 通知を NSGate の外へ

**問題**: `Notify` は `pg_notify` の **DB 往復**。メタデータ flush の後始末 (`FinishPendingFlush`) は
**NSGate 保持のまま**これを呼ぶので、DB がハングしている間ずっと NSGate が握られ、
**全メタデータ操作が道連れで詰まる**。

**修正**: 送信側の通知を **専用ワーカ + キュー**に載せた (B-8 の制御ワーカと同じ形)。
`Notify` は `NotifyMessage` を積むだけになり、DB を触るのはワーカスレッドだけになる。
**単一消費者なので送出順は保たれる**。送出はどの経路でも commit の後なので、
「commit より先に通知が出る」順序は作らない。

- **キューは有界 (1024) で、満杯なら捨てる**。producer は NSGate や `DirtyFile.Gate` を握っていることが
  あるので**絶対に待たせない**。通知は取りこぼしても他クライアントのキャッシュが古くなるだけで
  (`NotifyChannel.Publish` 自体も失敗を握り潰す設計)、詰まったときに捨てるほうが正しい。
  捨てたことは累計付きで warning に残す (10 秒に 1 回へ間引く)。
- `Dispose` では **`notifyChannel` を閉じる前に**キューを閉じてワーカの終了を待つ (最大 2 秒)。
  積み残しを最後まで撃たせるため。

**回帰の注意**: 通知が非同期になったので、**他クライアントへ届くまでの遅延はワーカのスケジューリング分だけ
伸びる**。cross-client の可視性テストは元から待ちを入れているので影響しないが、
「操作 → 即座に他クライアントで見える」を仮定するテストを書かないこと。

### B-10 の as-built (**実装済**)

> **flush 先の `chunk_size` が手元の分割と違ったら書かない**。

**問題**: `EnsureFlushDataRow` の Rekey (data_id の競合に負けて勝者行へ付け替える経路) は
**勝者行の `chunk_size` を見ていなかった**。手元の dirty は `DirtyFile.ChunkSize` で分割済みで、
`ChunkSize` は `init` なので後から合わせられない。境界の違う行へ `chunk_index` ごとに UPSERT すると
**別の位置に別の長さで上書き**する = 無音のデータ破壊になる。

**修正**: 付け替える前に勝者行の `chunk_size` を読み、手元と違えば
`FlushChunkSizeMismatchException` を投げて **flush を失敗させる** (dirty は手元に残る)。
同じ検査を「行は既に存在する」経路にも入れた (その行も write-through や他クライアントが
作っている可能性がある。`chunk_size` は既に読んでいるので追加コストは無い)。

**捨てずに失敗させる**のが要点。設定の不一致なので再試行しても直らないが、
黙って壊すより連続失敗 → エラーステートに上げて**運用に気づかせる**ほうがよい
(メッセージに「`file_system.default_chunk_size` を揃えてください」と出す)。

**自動テストは書いていない**。`chunk_size` は `{prefix}data` の行ごとの列で、
`file_system.default_chunk_size` は FS 共通 (DB 保管) なので、**同じ FS に違う分割の
クライアントを同時に立てる**状況を shell から作れない。Windows 側とも
「Linux 側で見る・Windows は既存スイートの回帰だけ」で合意している。

### B-13 (**ドキュメント**)

| 対象 | 入れたもの |
|---|---|
| [Mount.md](../Mount.md) §write-back | 喪失レポートの**上限 32 件は pending / dirty それぞれ**であること (残りは内訳サマリ) / 「ファイル間の因果」に **`rm f; cp new f`** の具体例 / 停止シグナルの段階分け (B-12) と `TimeoutStopSec` の指針 |
| [audit-log.md](audit-log.md) | **write-back 有効時の監査は耐久ではない** (pending inode の監査行は flush tx で初めて DB に入るので、`fsync` していない分はクラッシュで操作ごと消える。`op = writeback_loss` は「消えた」ことの記録であって操作そのものの記録ではない) |
| ~~`Mount.md` の「その fd の close は同期」~~ |  A-4〜A-7 の修正と同時に as-built へ更新済 |

### data write-back のライブ無効化を二相化 (**実装済**)

> B-9 で metadata 側だけ二相にしたので、**data 側 (`mount.write_back`) も同じ形に揃えた**。
> 元の指摘は レビュー記録の
> 「data write-back の live off は drain 成功前にモードを落とす」。

**問題**: `mount.write_back` の live off は **一相**だった (`WriteBack = false` を代入してから
`FlushAll`)。2 つの穴がある。

1. **flip と並行して走っている write が「モードは off なのに dirty を積む」窓**に入る
   (metadata 側の B-9 ① と同型)。
2. **flush が失敗したときに dirty が置き去りになる**。背景ループ `RunFlushLoopAsync` は
   `if (!Mount.WriteBack) { continue; }` で data の flush を止めるので、**以後 flush 契機が来ない**。
   さらに `UseChunkCache` が `contentCache.Enabled || Mount.WriteBack` なので、
   `mount.cache_data_max_bytes = 0` だと **同じマウントの `read` が DB の古い内容を返す**
   (メモリに新しい dirty があるのに、そこを見に行かない)。

**修正**: `ApplyWriteBackLive(bool)` を追加し、off を三段にした。

| 相 | やること |
|---|---|
| ① | `dataIntakeClosed = true` — 以後の `WriteData` は **write-through** に流れる。即 `PublishStatsNow` |
| ② | `FlushAll(deadline)` で抱えている dirty を書き切る (期限 = `mount.write_back_flush_timeout_ms`。設定変更 1 回で無期限ブロックしないため) |
| ③ | `WriteBack = false` → `dataIntakeClosed = false` の順に戻す。**残った dirty があれば `dataDrainPending` を立てる**。即 `PublishStatsNow` |

**`dataDrainPending` が要点**。立っている間は「設定は off だが drain だけは続ける」状態で、
① 背景ループが `FlushIdle(TimeSpan.Zero)` を回し続け ② `UseChunkCache` が true のままになり
③ `FlushBeforeMetadataWrite` / `PrepareTruncateWriteBack` も従来どおり効く。
dirty が捌けたら `ClearDrainIfEmpty` が落とす。**「設定だけ off にして dirty を置き去りにしない」**
のが B-9 との差分で、`Error` ログにも残す。

`pgfsctl status` の `write-back` 行に **`(受付停止中 = 実効 off)`** / **`(drain 継続中 = 未 flush あり)`**
を併記する (スナップショットのキーは `intakeClosed` / `effective` / `drainPending`)。

**テスト**: [writeback.sh](../../tests/linux/writeback.sh) の
`test_live_off_flushes_and_publishes_effective_mode` (8 → 9 件目)。**一相に戻したビルドで FAIL することを確認済**。

**テストで踏んだ落とし穴**: **1d 単体ではシェルから dirty を抱えたまま flip を撃てない**。
`close` が flush 契機で、しかも **fd の複製が閉じるたびに FUSE の `Flush` が飛ぶ**ので、
`exec 9> f; printf ... >&9` を繰り返すと **1 回ごとに flush が走る** (実測: 8192 回の `printf` で
`flushes = 8192`・所要 10 分)。そのため**テストは metadata write-back を併用して pending inode を
400 件溜め、第 2 相の `FlushAll` を意図的に長くしている**。

### B-9 の as-built (**実装済**)

> 二相 flip の **check-then-act を潰す** + **実効モードを status に出す**。

##### ① 受付停止の判定を台帳ロックの中へ

**問題**: `InsertInode` が `MetadataWriteBack` (= 受付停止フラグ + 設定) を読んでから
`TryAdd` に到達するまでの間に **flip が完走し得る**ので、**flip が終わった後に pending が生まれる**。

**修正**: 受付停止フラグを `Api` の `volatile bool` から **`DirtyNamespace` の中 (台帳ロックの下)** へ移し、
**`TryAdd` がロックの中で判定して弾く**。flip 側も `SetIntakeClosed` で同じロックを取るので、
判定と登録が不可分になり窓が消える。

`TryAdd` の戻りが null の理由を呼び出し側が区別できるよう `PendingAddResult`
(`Added` / `NameConflict` / `IntakeClosed`) を返すようにした。**名前衝突は EEXIST、受付停止は
write-through フォールバック**で、取り違えると片方が壊れる (名前衝突を write-through に落とすと
同名 inode が 2 つできる — ステージ 1.5 で塞いだ破壊)。

##### ② 実効モードを status に出す

`{prefix}mounts.stats.writeBackMetadata` に **`intakeClosed`** と **`effective`** を足し、
`pgfsctl status` は `write-back(m): on (受付停止中 = 実効 off)` と出す。

**あわせて遷移時に即 heartbeat を書く** (B-5 と同じ考え方)。stats は heartbeat (30 秒) でしか
DB に届かないので、書かないと **flip の間ずっと status が「on」という嘘**を出す
— **flip が長いときほど見たい情報なのに、そのときほど見えない**。

**テスト**: `test_meta_live_flip_publishes_effective_mode` (27 件目)。**flip を意図的に遅くする**
(800 件の pending を抱えさせる。Citus 上では 1 件 10 ms 強なので第 1 相が数秒続く) ことで、
受付停止が status に出ることを決定的に観測する。修正前のコードでは「flip 中ずっと on という嘘」で FAIL する。

**① の check-then-act 自体には自動テストを書いていない**。窓を shell から決定的に踏ませる方法が無く、
書けるのはタイミング依存のテストだけだったため (B-8 と同じ判断)。不可分性は構造で担保している。

**テストで踏んだ落とし穴**: 状態をスカラー select で引いたため、`{prefix}mounts` に混ざる
**他のテストが残した heartbeat の新しい行**とあわせて複数行が返り、比較が永久に成立しなかった
(通しのときだけ落ちる)。**条件に合う行が 1 件以上あるかを `count` で見る**形に直した。
B-5 のテストで同じ罠を踏んで count 形にしていたのに、ここで書き戻してしまっていた。

### B-8 の as-built (**実装済**)

> 重い live 設定変更を **NOTIFY リスナースレッドから外す**。

**問題**: `reload` / `set` / `ping` の制御メッセージを、NOTIFY のコールバック上で**そのまま実行**していた。
`write_back_metadata` の二相 flip (`FlushAll` + back-pressure) は最大 `write_back_flush_timeout_ms` かかるので、
**その間ほかのクライアントの invalidate が 1 件も処理されない**。

**修正**: `Api` に単一消費者のワーカ (`BlockingCollection<ControlWork>` + `Task`) を置き、
**制御メッセージだけ**をそこへ回す。**データ変更通知 (invalidate) はインラインのまま** — 軽くて遅延に敏感なため。

* **順序は保つ** (単一消費者)。`set` が 2 回来たら来た順に適用する。
* **契約は変わらない**。`pgfsctl config set` はもともと ack を取らず、発火数を「直近」値として報告するだけ。
* **ワーカは例外で死なせない** (握って続行)。死なせると以後の live 変更が全部効かなくなる。
* キューが `ControlQueueWarnDepth` (32) を超えたら警告する。
* `Dispose` で `CompleteAdding` → 最大 2 秒 join。閉じた後の enqueue は黙って捨てる (shutdown 中なので)。

##### テストについて (正直に書く)

**新しい自動テストは追加していない**。「リスナーが占有されない」ことを shell から決定的に観測する方法が無く、
書けるのはタイミング依存のテストだけだったため。**性質は構造で担保している** (重い適用がコールバック経路に
存在しない)。回帰は既存の live 設定テスト
(`wbmeta.sh` の `test_meta_live_off_flushes_pending_then_write_through` / `negcache.sh` の
`test_live_reload_off_clears`) が見ている。

計測を 2 回外したことも記録しておく: ① `config set` 直後の `ls` の応答時間を測ったが、**FUSE の要求経路は
元々リスナースレッドと別**なので何も証明していない ② 修正前後でログのスレッド ID を比べたが、
**スレッド ID は実行ごとに変わる**ので A/B の証拠にならない。

### B-7 の as-built (**実装済**)

> エラーステート中の**破壊操作**を止める。ただし **「unlink を例外にする」のではなく、
> 「pending を捨てる操作は元々 destructive ではない」** という規則で分ける。

##### 規則

| 操作 | エラーステート中 | 理由 |
|---|---|---|
| pending inode の unlink / rmdir | **通す** | 純キャンセル = **DB を一切触らない**。pending が減るので状況は良くなる方向。**B-1 の回復手段がこれ** |
| pending の truncate | **通す** | メモリの dirty を捨てるだけ |
| **persisted** inode の unlink / rmdir | **止める** (`-EIO`) | 「新しいデータは受け付けないのに古いデータは消え続ける」がまさにこれ |
| **persisted** の truncate | **止める** | 縮小は既存チャンクの破壊、拡大は新規データ。どちらも受け付けない |
| rename-over-existing (**target が persisted**) | **止める** | 置換対象が消える |

判定は `ThrowIfErrorStateBlocksDestroy(target, what)` の 1 箇所に置き、`DeleteInode` / `TruncateData` /
`Rename`(置換あり) の入口で呼ぶ。`mount.write_back` が off なら素通り (書き込みが DB へ直接行くので
止める理由が無い。`ThrowIfWriteBackErrorState` と同じ逃げ道)。

##### 受け入れた副作用 (doc に明記)

* **`rm` / `rmdir` / `truncate` が `-EIO` を返し得る**。Linux / Windows とも新しい挙動。
* **`rm -rf` が pending と persisted の混在ディレクトリで部分的に失敗する** (pending の子は消えて
  persisted の子で止まる)。中途半端に見えるが、**「持っているものを黙って消さない」を優先**した。

##### なぜ「unlink を例外」にしなかったか

B-1 の回復手段 (衝突した敗者を `unlink` する) と正面衝突するため、**例外を作りたくなる**場面である。
だが回復対象は**定義上 pending** (まだ DB に無いから衝突している) なので、
**「pending を捨てる操作は元々 destructive ではない」**と整理すれば例外は要らない。
例外を積むと「なぜこれだけ通るのか」が後から読めなくなる。

##### `Api.CanDestroy` (追加・Windows 側の依頼)

判定を**副作用なしの述語**としても公開した。`ThrowIfErrorStateBlocksDestroy` はこれを呼ぶだけになる。

**なぜ要るか**: Dokan の `Cleanup` は `void` で `Api.DeleteInode` の例外を呼び出し元へ返せないため、
**消えていないのに `Remove-Item` が成功して返る** (fail-open と同じ嘘)。戻り値を返せる
`DeleteFile` / `SetEndOfFile` / `MoveFile` の時点で断るために、実行前に問い合わせる口が要る。

**アダプタ側で同じ判定を組み立ててはいけない**。外から見えるのは `WriteBackErrorState` だけで、
**「pending かどうか」は Core の内部状態**である。エラーステートだけで判断すると
**pending の削除まで止めて B-1 の回復手段を塞ぐ** (`SetFileAttributes` で一度踏んだ形)。

**テスト**: `test_meta_error_state_blocks_destroy_but_allows_cancel` (26 件目)。
**persisted の unlink / truncate が止まること**と **pending の unlink が通ること**を同じテストで見る
(片方だけだと規則を取り違えても緑になる)。実測: `rm: cannot remove '...': Input/output error` /
truncate `errno=5` / pending の `rm` は成功 / その後の create も成功。

### B-6 の as-built (**実装済**)

> 喪失レポートを **パス付き** + **打ち切りの内訳付き** にする。

| 変更前 | 変更後 |
|---|---|
| `id:93519 parent:93518 name:f0.txt kind:file size:3 …` | `失われる pending file: /mp2/many/f0.txt (id:93519 size:3 state:Dirty error:…)` |
| 32 件で黙って打ち切り | `… 他 9 件 (pending 合計 41 件 = dir 1 / file 40・論理 120 バイト)` |
| dirty データはパス無し | `失われる dirty データ: /mp2/many/f0.txt (data_id:… chunks:1 …)` + `… 他 8 件 (dirty 合計 40 ファイル / 40 チャンク・論理 120 バイト)` |

* **パスは `InodeCache.TryGetPath` で組み立てる**。pending inode は pin されているので自身と
  pending 祖先は必ずキャッシュに居るが、**persisted な祖先が LRU で落ちていると解決できない**。
  そのときは `?/name` にして、**復元できなかったことが分かる**ようにした
  (もっともらしい嘘のパスを出すより、解決できなかったと言うほうがよい)。
* **内訳は「他 N 件」で終わらせない**。合計・dir/file の別・論理バイト数まで出す
  (丸めた数は受け取った側が突合も訂正もできない)。
* **DB 側の記録 (B-2 の墓標 `stats.lost` と監査 `writeback_loss.detail.lost`) も同じ文字列**を持つ。
  運用が実際に見るのはログよりこちらなので、両方に同じものを入れる。dirty データぶんは
  `lostData` / `lost_data` として別に持たせた。
* 整形は `Api` 側に置いた。`DirtyNamespace` / `DirtySet` は**ロック配下でスナップショットを返すだけ**にし、
  パス解決 (`InodeCache` を触る) はロックの外で行う (台帳ロックの中からキャッシュを触ると
  ロック順が逆転する経路を作りかねない)。

**テスト**: `test_meta_loss_report_has_paths_and_breakdown` (25 件目)。**pending ディレクトリを
種別違いで衝突させる**と配下の pending 子もまとめて実体化できなくなるので、1 回の injection で
41 件の喪失を作れる。検証は B-2 の墓標 (`stats->'lost'`) を読む形にした — ログファイルを掘らずに済み、
**運用が実際に見る DB 側の記録そのもの**を確かめられる。

### B-5 の as-built (**実装済**)

> エラーステートの遷移を **heartbeat 周期 (30 秒) を待たずに** `{prefix}mounts` へ書き、
> `pgfsctl status` 側には**その情報の鮮度**を併記する。

##### 直せないもの (先に書く)

**DB に一切書けない障害では、遷移の即書きも当然できない**。その場合 `status` は最後に届いた値
(= 古い緑) を出し続ける。これは原理的に避けられないので、**読み手が「古い」と気づける**ようにする側で対処した。

##### 入ったもの

| 経路 | 内容 |
|---|---|
| **遷移時の即書き** | エラーステートに**入った / 解除した**瞬間に `WriteHeartbeat()` を撃つ。**ロックの外で呼ぶ** — `flushFailureGate` の下で DB I/O をやると、DB が詰まったときに失敗カウンタの更新ごと止まる。そのため `NoteFlushFailure` / `NoteFlushSuccess` / `ClearFlushFailure` は「遷移したか」を bool で受け取り、ロックを抜けてから書く |
| **赤行に鮮度を併記** | `!! write-back ERROR STATE (since ...) **[heartbeat 42s 前の情報]**` と出す。エラーステートは heartbeat 経由でしか届かないので、heartbeat が古ければこの赤も古い |
| **stale を赤くする** | Layer 3 のホスト行の `[stale]` を `[stale: heartbeat 5m 前]` にして**赤**にした。DB に書けない障害では赤が出ないまま緑に見えるので、「この数値は古い」と分かることが唯一の手掛かりになる |

##### テストで踏んだ 2 つの落とし穴 (どちらもテスト側の欠陥)

1. **`mount_pid` が呼び出し元のシェルを掴んでいた**。`pgrep -f "mount\.pgfs .*$SETTING_FILE"` は
   同じ文字列を含むシェルにも当たり、実測で 3 件マッチして `head -1` が**シェルの pid** を返した。
   `unmount_crash` はこれを `kill -9` するので、**無関係のプロセスを落としかねない**。
   `pgrep -x mount.pgfs` (プロセス名) に変更した (`wbmeta.sh` / `negcache.sh` / `writeback.sh` の 3 本)。
   これは B-1 の性能計測で踏んだ `pgrep -f` の罠とまったく同じもので、**テスト側にも同じ罠が埋まっていた**。
2. **名前だけでディレクトリの id を引いていた**。手動検証で作った `~/mnt/pgfs/b5` と
   テストの `~/mnt/pgfs/wbmeta/b5` が同名で 2 行あり、`limit 1` が前者を掴んで injection が効かず、
   **通しで実行したときだけ落ちた**。親を `TEST_ROOT` に限定して引くようにした。

### B-4 (**B-3 の副作用で解消 + 潜在の作り込みを修正**)

**症状は既に発生しなくなっていた**。B-4 は「孤児監査キューが `audit.enabled = false` で書かれないのに
`UnflushedCount()` には数えられるので、**実喪失ゼロなのに unmount が期限満了 + 喪失レポート + exit 4**
になる」という指摘だったが、**B-3 でキューの唯一の生産者 (`QueueCancelAudits`) が消えた**ため、
キューは常に空で、この経路には入らない。**コードを読んで確認した** (残る呼び出し元は
`FlushOrphanAudits` 自身の失敗時の積み直しだけ = 自己参照なので永遠に空)。

ただし**作りとしての穴は残っていた**ので直した: `FlushOrphanAudits` の先頭にあった
`if (!this.auditEnabled) { return; }` を外した。理由は 2 つある。

1. ここに積まれている行は**監査が有効だった時点で捕捉されたもの**なので、その後 off にしたことは
   書かない理由にならない。将来キューに積む経路を足した瞬間に B-4 が復活する。
2. より悪いのは、**`config set audit.enabled false` が捕捉済みの証跡を消す手段になる**こと。
   新規の捕捉側は従来どおり `auditEnabled` で止まるので、off にすれば監査は増えない。

**テスト**: `test_meta_audit_live_off_does_not_fake_loss` (23 件目)。監査を on にして「作って消す」→
`pgfsctl config set audit.enabled false` で live off → もう一度「作って消す」→ 正常 unmount し、
**B-2 の墓標ができていないこと**で偽の喪失を検出する。現状は構造的に起きないので、
**キューに積む経路を将来足したときに同じ穴を open し直さないための回帰ガード**である。

### B-3 の as-built (**実装済**)

> 純キャンセルの監査ペアを**その場で DB に書く**。設計の §監査 が塞ぐと書いた
> 「`create → read → unlink` が痕跡ゼロで成立する」回避チャネルを実際に塞いだ。

**修正前に何が起きていたか (実測)**: `write_back_metadata = on` + `audit.enabled` で
interval 内に「作って・読んで・消す」と、監査行は**メモリの孤児キューに積まれるだけ**だった。
dev サーバでの実測では **`kill -9` の前の時点で既に audit 行が 0 件**で、`kill -9` 後も 0 件。
つまりクラッシュを待つまでもなく、**背景 flush が走る前に落とせば痕跡が残らない**。

**修正**: `CancelPendingInode` が `WriteCancelAudits` で create/delete ペアを**同期で 1 tx 書く**。
件数は「interval 内に作って消したファイル」だけなので、同期化のコストは実質ゼロ。

決めたこと:

* **書けなかったら取り消し自体を失敗させる** (例外 → `-EIO`)。write-through の監査と同じ
  「監査を残せないなら操作も成立させない」方針で、DB が落ちていれば persisted な inode の unlink も
  同様に失敗するので挙動は一貫している。
* **呼ぶのは台帳から `Forget` する前**。先に消すと、書けなかったときに「消えたのに痕跡が無い」状態が残る。
* **パーティション ensure は tx の外**。Citus では分散書き込み tx 内の DDL が拒否され、tx 内に入れると
  `lock_timeout` 待ちで mount 全体が固まる (ステージ 1.5 の知見をそのまま踏襲)。
* 孤児キュー (`QueueOrphanAudits` / `FlushOrphanAudits`) そのものは**残す**。ただし
  **この修正で積む経路は無くなった** — `QueueCancelAudits` が唯一の生産者だったため、
  現在の呼び出し元は `FlushOrphanAudits` 自身の書き込み失敗時の積み直しだけである
  (**当初ここに「同期点を持たない経路がまだある」と書いたが、確かめずに書いた誤りだった**。
  B-4 を見るときに実体を確認して訂正)。残してあるのは積み直しの受け皿としてと、
  同期点を持たない経路を将来足すときの着地点として。

**テスト**: `tests/linux/wbmeta.sh` の `test_meta_cancel_audit_is_written_immediately` (22 件目)。
**`unmount する前の時点で** DB に create/delete の 2 行があること**を見るのが肝で、
`kill -9` 後に見るだけのテストだと「背景 flush が間に合っていただけ」と区別できない。
テストは `audit.enabled` を一時的に on にして戻す (監査テーブルが無い FS では skip)。

### A-2 / A-9 の追い修正: 消えた対象の失敗カウンタ (2026-09-19・**Windows 実機で発覚**)

> ラウンド A で入れた「flush 対象ごとの連続失敗カウンタ」に**落とし忘れ**があった。
> **Dokan 側の実 2 マウント検証で発覚**し、Linux でも同じ手順で再現した。

**症状**: `defer` の名前衝突で error latch した pending を **`unlink` しても回復しない**。
pending 自体は消え unmount の exit も 4 → 0 に戻るのに、**エラーステートが解除されず後続の create が
`-EIO` のまま**になる。

**原因**: エラーステートの解除条件を「閾値に達した対象が 1 つも無い」というカウンタの状態から導いている
(A-9 の修正) のに、**対象が消えたときにカウンタを落としていなかった**。二度と成功し得ない対象の
カウンタが閾値のまま残り、解除条件が永久に成立しない。A-4 の「同期 close 印を削除・取消で落とし忘れる」と
**まったく同じ形の抜け**である (対象が消えたら印も落とす、という規律が片方にしか入っていなかった)。

**修正**: `ClearFlushFailure(inodeId, dataId)` を足し、**対象が消える全経路**で呼ぶ —
純キャンセル (`CancelPendingInode`) / 破棄 (`FinishPendingDiscard`) / dirty の破棄 (`DiscardDirtyData`) /
inode 消滅による flush 破棄 (`FlushLocked`) / write-through の削除 (`DeleteInodeThrough`)。
解除判定は `ReevaluateErrorStateLocked` に切り出して成功時と共通化した。

**テストの教訓**: 既存の `test_meta_exclusive_create_defer_conflict_latches` は
**「`unlink` の戻り値が 0 であること」しか見ていなかった**ので、この不具合を素通りさせた。
**回復は「次の操作が通ること」で見る**ように直した (latch 中の create が拒否される → `unlink` →
create が通る)。あわせて、エラーステートの閾値 (同じ対象で 5 連続) を超えるまで `fsync` を撃つようにした
(1 回だけだと latch はするがエラーステートに入らず、回復の assert が意味を持たない)。

### B-2 の as-built (**実装済**)

> **喪失を DB 側に残し、次のマウントで端末に警告する**。exit 4 そのものは届くようにできないので、
> 報告経路を DB とログに移した。

##### 直せなかったもの (先に書く)

**exit 4 が呼び出し元に届くようにはできない**。デーモン化した `mount.pgfs` は、親がマウント成立時点で
`0` を返して**先に終了している**ため、その数時間後の unmount で子が `4` を返しても待っている人がいない。
ここは「届く経路を別に用意する」以外に手が無く、下の 3 つがその代替である。

##### 入ったもの

| 経路 | 内容 |
|---|---|
| **`{prefix}mounts` の墓標** | 喪失があった unmount は**行を DELETE せず残す**。`stats` に `ended` / `endedAt` / `unflushedLoss` / `lost` (最大 32 件) を載せる。**列は足さない** — このテーブルの DDL は mkfs に集約されていて、列を増やすと既存 FS で再 mkfs が要るため。`stats` は JSONB なので既存 FS でもそのまま動く |
| **監査行 `op = writeback_loss`** | 1 回の unmount = 1 行。対象はマウントそのものなので `target_id` は null、`name` に mountpoint。`detail` に件数・内訳・`timeout_ms`。**`audit.enabled` が off なら書かれない**ので、そのときの手掛かりは墓標だけになる |
| **次回マウント時の警告** | **親プロセス (fork 前) の stderr に出す**。子は stdout/stderr を捨ててから走るので、子側のログは `--log-output` を付けていない限り誰にも届かない。端末を持っているのは fork 前の親だけである (`StatusAdmin.WarnPastLossToConsole`)。Api の中でも同じ内容を Error ログに出すので、ログファイルがあればそちらにも残る |
| **`pgfsctl status`** | 墓標を `LIVE` 欄で `ENDED` と表示し、続けて赤で `!! write-back UNFLUSHED LOSS (N mount(s))` と内訳を出す |

##### 墓標を自動で消さない理由

**読まれていない事故の記録が勝手に消えるのが最悪**だから。消すのは運用の明示操作に委ねる:

```sql
DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0;
```

墓標が増えるのは**喪失が起きたときだけ**なので、異常終了 (`kill -9`) で残る stale 行とは増え方が違う
(stale 行の掃除は別課題。B-1 の `defer` 警告が過大に出る件と同根)。

##### テスト

`tests/linux/wbmeta.sh` の `test_meta_loss_is_recorded_in_db` (21 件目)。種別違いの同名行を psql で入れて
flush を恒久失敗させ、短い期限で unmount する。墓標の有無・`unflushedLoss` / `endedAt`・
**次回マウントの親 stderr に警告が出ること**・**墓標を消すと警告も消えること**を見る。

### 破れなかった防御 (2 レンズで確認)

* ロック階層 `NSGate → DirtyFile.Gate → DB tx` の**逆辺はステージ 2 でも存在しない**。`AssertNsGateHeld` は新規 `*Locked` メソッドにも漏れなく入っている。
* ヒューリスティック (a) は **pending source の単一 tx / persisted source の 2 tx とも喪失窓を作れない** (順序が「新データ commit → target 削除 + rename commit」なので、どの中断点でも旧 target は健在)。
* (c) の write-through 化は正しい (「pending にして即 materialize」だと flush tx の file/file 衝突解決で**両方の O_EXCL create が成功**してしまう)。
* flush tx の 6 つの不変条件 (スナップショット固定 / 親→子順 INSERT / キャッシュ迂回の生存確認 / `{prefix}lock` 昇順一括 / 監査 ensure の tx 外 / commit 後にのみメモリ反映) はいずれも回避経路を作れない。
* `FSyncDir` / `FlushPath` の `fi.fh` 逆引き、`rmdir` の三重の空判定、`Flushing` 状態の固着なし、unmount 時に FUSE ワーカが既に join 済み。

