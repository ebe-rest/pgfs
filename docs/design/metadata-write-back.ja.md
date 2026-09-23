# metadata write-back — メタデータの遅延書き (Phase 1e)

> **道順**: [docs/README.ja.md](../README.ja.md) › [runtime-control-plane.ja.md](runtime-control-plane.ja.md) › **本書**
>
> **この doc が正である範囲**: メタデータ (create / 属性 / rename) の遅延書きの**確定設計**と
> **実装ステータス (ステージ 1 / 1.5 / 2)**。pending-born 主義、pending inode 台帳
> (`DirtyNamespace`)、同期化ヒューリスティック 3 つ、flush tx の不変条件、エラー報告の底はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [metadata-write-back-reviews.ja.md](metadata-write-back-reviews.ja.md) | **敵対的レビューの結果と修正の記録** (ラウンド A / B-1〜B-13)。時系列の as-built はすべてそちら |
> | [write-back.ja.md](write-back.ja.md) | **データ本体**の遅延書き (1d)。1e はこれを前提にする |
> | [data-id-lifecycle.ja.md](data-id-lifecycle.ja.md) | `data_id` の寿命 (create で確定し unlink まで不変)。1e の予約 id と関わる |
> | [audit-log.ja.md](audit-log.ja.md) | 監査行の書き方。1e は「操作時にキャプチャして flush で書く」 |
> | [control-plane.ja.md](control-plane.ja.md) | `mount.write_back_metadata` の実行時切り替えと統計 |
> | [../Mount.ja.md](../Mount.ja.md) | 利用者向けの挙動と、**契約として明文化した喪失** |
> | [runtime-control-plane.ja.md](runtime-control-plane.ja.md) | 運用フェーズ全体の構成 (ハブ) |

## 設計

### 確定設計 — メタデータ write-back: **pending-born 主義で 1 ファイル = 1 tx**

> 以下は **確定設計・投影**である。現在はステージ 2 まで実装済みであり、現行の差分・実測・未修正事項は後続の as-built とレビュー節を参照。1d (データ write-back) の拡張。
> 設計は敵対的レビュー 2 レンズ (並行性・cross-client 整合 / データ安全・POSIX 契約・監査) の指摘を
> 織り込んで確定した。**投影ベンチの結果、取り分は「小ファイル × Citus」に限られる** (下記・正は
> [performance.ja.md §1e 投影ベンチ](performance.ja.md)) — 1d ほどの費用対効果は無く、実装するかは要判断。

#### 狙い — rsync の残コスト ≈2,100 メタデータ tx を背景化する

1d 実測で rsync (599 MB / 745 ファイル) は 1.4× 止まり。残コストは `create` / `chmod` / `utimens` / `rename` の
write-through tx (≈2,100 本) で、これが前景 (rsync のシステムコール) を 1 本ずつブロックしている。
本設計は rsync の 1 ファイル (create temp → write → close → chmod → utimens → rename) を
**丸ごと 1 つの背景 flush tx** (inode INSERT + data 行 + chunks + 属性 + audit) に畳む。

* tx 本数: ≈2,845 (メタ ≈2,100 + close flush 745) → **≈750** (ファイル単位 flush は 1d 決定 4 を踏襲)。
* 本数削減以上に効くのは **前景から DB 往復が消える**こと (close も rename も即返り、flush は背景でパイプライン)。
* ~~期待値は **2〜4×** と見込む~~ → **投影ベンチ実施済み (2026-08-10・正は [performance.ja.md §1e 投影ベンチ](performance.ja.md))**:
  **4 KB ファイル × Citus rf=2 で SQL 側 3.7× / 実世界投影 ≈2.1×** (FUSE 側固定費 ≈10.5 ms/file が残るため)。
  **768 KB では 1.21×** (データ書き込みが支配的)、**参照ワークロード (599 MB/745 mixed の rsync) への投影は ≈1.1×**、
  **単一ローカル PG では絶対差 1.3 ms/file で wall-clock にほぼ出ない**。設計時の「メタデータ tx が支配的」は
  過大評価だった (実際は 179 s 中 ≈12 s)。**1e は「小ファイル多数 × Citus / 高レイテンシ DB」向けの対策**であり、
  1d (6.1×) ほどの汎用的な費用対効果は無い — 実装の優先度はこの前提で判断する。

#### 決定事項

| # | 判断 | 決定 | 根拠 |
|---|---|---|---|
| 1 | 遅延スコープ | **pending-born 純化**: このマウントが作りまだ flush していない inode (= pending) への操作だけ遅延する。**persisted inode への操作は全部 write-through のまま** (chmod/utimens 含む) | persisted への属性遅延 (dirty-attrs) は「他クライアントの rename (= DELETE+INSERT で shard 移動) と交差すると chmod が rows=0 で黙って消える」「chmod 600 の遅延 = cross-client の権限窓」を作る。一方 rsync には不要 — close が flush しなくなるので temp は属性変更時もまだ pending → coalesce に乗る。**得るものが無くリスクだけ** |
| 2 | close の耐久性契約 | **metadata=on のとき close (FUSE `Flush`) は同期 flush しない** (マークのみ)。fsync / fsyncdir が唯一の硬いバリア | ここを緩めないと rename が write-through に落ちて 2 tx/ファイル止まり = 効果半減。NFS の close-to-open より弱くなるため **opt-in ノブ + 契約の明文化** (下記) で受ける |
| 3 | 同期化ヒューリスティック | **例外 3 つを最初から内蔵**: (a) rename-over-existing (b) O_TRUNC open された persisted ファイルの close (c) O_EXCL create | ext4 delayed allocation が 2009 年に踏んだ事故と同型の穴を既定で塞ぐ (下記) |
| 4 | 名前衝突の解決 | ディレクトリ = **既存 id を採択し自分の pending 子ツリーを付け替える (inode 版 Rekey)** / ファイル = **明示 DELETE + INSERT + conflict を audit に記録**。`ON CONFLICT DO NOTHING` は禁止 | DO NOTHING は inode 行だけ落ちて data 行・chunk が孤児化する。dir の同時 materialize は敗者の flush 済み子ツリーが丸ごと不可視化するため Rekey が必須 |
| 5 | ロック階層 | **NSGate (namespace flush 直列化) → DirtyFile.Gate → DB tx** の順を全経路で固定。pending を参照する write-through 操作も NSGate 参加者 | 順序を規定しないと背景 flush (NSGate→Gate) と fsync (Gate→NSGate) の相互待ちで mount 全体が復旧不能ハング |
| 6 | 監査 | **操作時にキャプチャ・flush tx で batch INSERT**。`occurred_at` = 操作時刻、純キャンセルもペア記録 | 下記 §監査 |
| 7 | ノブ | `mount.write_back_metadata` (bool・既定 **false**・`mount.write_back = true` が前提) | データ write-back と独立に事故半径を制御する |

#### 対象操作の線引き

| 操作 | 扱い |
|---|---|
| `create` / `mkdir` / `symlink` | **遅延** — inode id を `IdReservation` (`{prefix}inode.id` 用にもう 1 本) で予約し、pending inode として台帳 + InodeCache に登録。DB 未 INSERT |
| pending inode への `chmod` / `chown` / `utimens` | **coalesce** — pending 状態への上書き。tx ゼロ |
| pending inode への `rename` (移動先に既存 target **なし**) | **coalesce** — 名前/親の付け替えのみ |
| pending inode への `unlink` / `rmdir` | **純キャンセル** — DB に何も書かない (audit はペアを残す。下記) |
| persisted inode への rename / unlink / rmdir / chmod / chown / utimens / xattr / hardlink | **write-through (現状維持)** |
| pending を参照する write-through 操作 (rename 先が pending dir / hardlink source が pending 等) | **同 tx で参照先を materialize** してから実行 |

#### 同期化ヒューリスティック 3 つ (ext4 の教訓)

close-no-flush を素で入れると「**旧を即消して新を遅らせる**」構造ができる — write-through 側の破壊操作
(rename-replace の target 削除 / O_TRUNC) は即時 DB 反映なのに、新内容の永続化は interval 待ちのため、
クラッシュで**旧・新の両方を失う** (ext4 delayed allocation の 2009 年の事故と同型)。

| # | 例外 | 内容 |
|---|---|---|
| a | **rename-over-existing** (target が DB に実在) | 「target 削除 + source の materialize + source の dirty data flush + audit」を**単一 tx で同期実行**。エディタ保存・`sed -i`・dpkg の tmp+rename パターンを守る。target 無しの rename は coalesce のまま (rsync の新規ツリーはこちらに乗る) |
| b | **O_TRUNC** で open された persisted ファイル | 旧チャンクは open 時に消えているので close を遅らせるとクラッシュでゼロ長ゴミが残る → **close で同期 flush** |
| c | **O_EXCL create** | mkdir / O_EXCL create は git `index.lock` 等の**ロックプリミティブ**。遅延すると 2 クライアントが同時にロック取得成功し得る (リポジトリ破損級) → **同期 materialize**。単一クライアント内の check-then-act 窓 (存在確認と flush 完了の交差) もこれで閉じる |

#### pending 台帳と可視性 (自クライアント)

* **DirtyNamespace 台帳** (仮称) を `DirtySet` と対で持つ。pending inode の唯一の正はこの台帳で、
  InodeCache は「DB + 台帳の再マージ」で常に復元できるビューに徹する。**fsyncdir 用に
  parent_id → pending children の索引**を持つ (`DirtySet` は data_id キーで親からの逆引きが無い)。
* InodeCache への pin は「**byId エントリを LRU 退避しない**」だけに限定し、**NOTIFY invalidate は全種素通し**
  (マージ済み children リストまで pin すると他クライアントの変更が永久不可視になる)。
* `ListChildren` は DB 結果と台帳の pending children をマージして `PutChildren`。**台帳 generation ガード付き**
  (snapshot 時と Put 時で generation が変わっていたら Put しない — ContentCache と同じ手法)。マージは
  name で dedupe し pending 優先 (flush commit 直後の二重出現を防ぐ)。
* `IsDirectoryEmpty` (rmdir) にも **pending 子のマージを義務化** (DB だけ見ると pending 子持ちの dir を消せる)。
* 台帳エントリは状態機械 **`Dirty → Flushing → Persisted`** を持つ。flush は開始時 snapshot だけを書く。
  ~~`FlushingRedirty` (Flushing 中に来た変更を次回へ回す状態)~~ → **実装では採用しなかった**:
  `Flushing` 中の coalesce は拒否し、呼び出し側が NSGate で flush 完了を待って write-through に落とす
  (理由と代償は下 §1e 実装ステータス 差分 1)。したがって本節と決定 1 の表の「coalesce する」記述は
  すべて **「`Dirty` のときだけ coalesce・`Flushing` 中は write-through」** と読むこと。
* **materialize 後も台帳に「persisted」状態で少し残す** (即 Forget しない)。create の存在確認 (overlay → DB) が
  flush 完了と交差する瞬間の check-then-act 窓を**狭める**ため。ただしこれは **best-effort であって
  正しさの根拠にはしない** — 残骸は件数上限で間引かれるし、実装レビューでも「Persisted 残骸に依存している
  正しさ」は見つからなかった (存在確認は overlay が無くても DB 側で解決できる)。O_EXCL の保証は
  同期 materialize (ヒューリスティック c) が担う。

#### flush tx の不変条件

1. **ancestor の生存検証**: pending ancestor dirs は親→子順に同 tx で INSERT し、persisted ancestor は
   `{prefix}lock` (−inode_id) を取って生存を確認する。**FK を張らない設計なので、これが「他クライアントの
   rmdir × 自分の pending 子」で到達不能な孤児 inode を作らない唯一の防御**。親が消えていたら pending を
   破棄 (= クラッシュ時と同じ「無かったこと」扱い) + 統計 + log (黙殺しない)。
2. **inode INSERT は data 行より先・必ず同一 tx**。data 側の既存 flush には「inode が消えていたら dirty を
   破棄して commit」する経路があり、pending inode を誤ってそこに通すと fsync 済み相当のデータを黙って捨てる。
3. 名前衝突は決定 4 のとおり。**Rekey 時は pending audit 行の target_id / 台帳 / キャッシュキー / lock target も
   付け替える**。
4. `created_at` / `st_ctime` は**操作時刻を明示指定** (DB DEFAULT = flush 時刻に任せない。1d の mtime と同じ方針)。

#### fsync / fsyncdir の完全バリア化 (前提条件)

本設計の耐久性契約は「fsync すれば残る」に全乗りするため、以下は**拡張ではなく必須要件**:

* `FlushInode` を「pending ancestors (親→子) + pending inode 本体 + coalesce 済み属性/rename + dirty data +
  audit を 1 tx」に拡張する。**data 無し inode (`DataId == null`) でも materialize する**
  (現行は即 return = 空ファイル・ディレクトリの fsync が嘘をつく)。
* **`FSyncDir` の override を追加** (現行は基底の -ENOSYS。カーネルは ENOSYS を以後成功扱いにするため、
  「file fsync → dir fsync」の定石が黙って無効になっている)。範囲は「そのディレクトリ直下の pending children +
  自身の pending ancestors」。
* `FlushPath` の fail-open (inode が引けないと成功 0 = fsync の嘘) を **open 時に `fi.fh` へ載せた data_id の
  逆引き**で塞ぐ (Phase 0)。

#### エラー報告と耐久の底

close で -EIO を返せなくなるぶん、報告経路を 4 段で確保する (fsync しないアプリ = rsync が exit 0 のまま
データを失う事故を防ぐ):

1. **error latch 済みファイルの close は同期 flush + 報告に格下げ限定** (クリーンなファイルの close だけマークのみ)。
2. **unmount は期限付き retry、失敗残があれば EBUSY 相当で拒否**。現行 `FlushAll` の「警告して継続」は
   metadata=on では「ファイルそのものがログ 1 行で消える」に化けるため不可。
   → **as-built: EBUSY での拒否は実現不能** (`fusermount3 -u` / `umount(8)` はカーネル側で完了し FUSE
   デーモンに拒否権が無い)。「期限付き retry + 失われる内容の列挙 + exit code 4」に置き換えた
   (下 §1e 実装ステータス ステージ 2 差分 6)。
3. **flush 連続失敗 N 回で mount をエラーステートに**: 新規 write をブロックし、heartbeat stats 経由で
   `pgfsctl status` に赤表示。監査パーティション ensure の恒久失敗 (mount ロールの CREATE 権限剥奪等) が代表例 —
   write-through なら操作が即 -EIO で気づけたものが、write-back では「成功済みの操作が永遠に flush できない」に化ける。
4. **back-pressure は本物に**: pending inode 数上限 (`mount.write_back_max_inodes`) 超過で「flush 成功か
   timeout まで write/create をブロック」。one-shot で 1 巡して戻る方式は flush が失敗し続けると上限を突破し続ける。
   40P01 分散デッドロック ([tests.ja.md §既知のフレーク](../tests.ja.md)) は flush 失敗として指数バックオフで再試行。

配線レベルの前提が 2 つ: **SIGTERM ハンドラ** (現行は `Console.CancelKeyPress` = SIGINT のみで、
systemd 経由の再起動では FlushAll が走らない。`PosixSignalRegistration` で追加し、PG と同居するホストでは
停止順序を運用 doc に明記) と、**`write_back_metadata` の live off は二相** (新規 pending の受付停止 →
FlushAll → モード切替。一相だと flip と並行の create が flush 主体のいない pending として残る)。

#### 監査

* 操作時に **`fuse_get_context` (IP/uid/uname/domain) + 操作時刻をキャプチャ**して pending op に保持し、
  flush tx で batch INSERT する。背景 flush スレッドで `WriteAudit` をそのまま呼ぶと occurred_at・caller が
  全部 flush 側 (背景スレッド・flush 用コネクション) に化けて監査が嘘をつく。
* partition ensure は**行ごとの occurred_at の月**で呼ぶ (error latch で月を跨ぐ pending があり得る)。
* **純キャンセル (create → unlink) も `audit.enabled` 時は create/delete のペアを次回 flush tx に同乗して残す**。
  残さないと「interval 内に作って・読ませて・消す」が監査ゼロで成立する回避チャネルになる。
* audit の `id` (flush 時採番) と `occurred_at` (操作時刻) の順序は乖離し得る → **「audit は occurred_at で読む」**を
  [audit-log.ja.md](audit-log.ja.md) に明記する (実装ターンで)。

#### 契約として明文化する喪失 (ドキュメント必須)

> ✅ **利用者向けの記述は [Mount.ja.md §write-back](../Mount.ja.md) に置いた** (ステージ 2)。
> 下の表はその原典。既定 off の理由・同期化ヒューリスティックの例外・エラー報告経路も同じ節にある。

| 項目 | 内容 |
|---|---|
| close の耐久性 | close 済みでも interval (既定 1000ms) + flush 時間の窓で**ファイルごと消える**。fsync / fsyncdir だけが硬い |
| ファイル間の因果 | write-through の削除/rename が pending の作成を**追い越す**ため、複数ファイル操作 (`git checkout` / `rsync --delete` 等) のクラッシュは「旧も新も無い」= どの直列履歴にも無い状態があり得る。単一ファイルの原子性は逆に向上する (最終名・最終属性・データ・監査ごとアトミックに出現し、ゼロ長 temp が残らない) |
| 他クライアント可視性 | flush まで不可視 (窓 ≤ interval + back-pressure)。同名 create の cross-client 競合は last-flush-wins (conflict は audit + 統計に記録)。O_EXCL は同期 materialize なので保証維持 |
| du / df / status | pending は flush まで反映されない。`GetAttr` の `st_blocks` は dirty バッファ長から概算を返す (st_size だけ返して st_blocks=0 の不整合 stat を避ける) |
| 既存テストとの関係 | [tests/linux/writeback.sh](../../tests/linux/writeback.sh) の close 耐久テストは **metadata=off の回帰として維持**。on 用には「fsync 済みは残る / close-only は消えるのが契約どおり」の別テストを追加する |

#### 前提条件: Phase 0 (既存バグ修正・本設計と独立に価値がある) — ✅ 実装完了

設計レビューで見つかった**現行コードの穴**。metadata write-back を入れなくても直す価値があるため先行した。
**4 件とも実装済み・実機検証済** (Linux e2e **43 passed / 1 skip (44 件)** + writeback.sh 8/8 + SIGTERM 手動確認):

| # | 内容 | 場所 | as-built |
|---|---|---|---|
| 0-1 | **rename-over-existing が非原子**: FUSE 層が target を `DeleteInode` **別 tx** で先に消してから rename していた (write-through でもクラッシュ窓 + 旧チャンク即 DROP) | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) `Rename` | `Api.Rename(id, parent, name, replaceTarget)` overload を追加し、**置換対象の削除 (tx 内共通化した `DeleteInodeInTx`) と rename を同一 tx** に。tx 内で占有者を読み直し、別 inode に入れ替わっていたら中断 (巻き添え削除防止)。置換前に source の dirty を `FlushInode` で永続化 (ヒューリスティック a の前半)。**Dokan `MoveFile` も同じ overload に切替** (ビルドのみ・Windows e2e は未走)。回帰テスト `test_rename_replace_existing` (ハードリンク兄弟の nlink 復帰含む) |
| 0-2 | **`FlushPath` の fail-open**: inode が引けないと成功 0 を返す = 他クライアントの rename と重なると fsync が嘘をつく | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) `FlushPath` | Open/Create で **`fi.fh` に inode id** を載せ、path が引けないとき id で逆引きして flush。両方引けないときだけ 0 (unlink 済み = dirty は discard 済み) |
| 0-3 | **SIGTERM で FlushAll が走らない** (`Console.CancelKeyPress` = SIGINT のみ配線) | [Program.cs](../../src/mount/src/Program.cs) | `PosixSignalRegistration.Create(SIGTERM, ...)` で SIGINT と同じ graceful unmount (LazyUnmount → FlushAll → mounts deregister) に接続。実機で kill -TERM → ログ + unmount + mounts 行 DELETE を確認 |
| 0-4 | **`FSyncDir` 未配線** (-ENOSYS → カーネルが以後 no-op 成功扱い) | [FileSystem.cs](../../src/fuse/src/FileSystem.cs) | override を追加 (現状メタデータは write-through なので本体は `return 0`)。1e 本体でここが pending children の flush 点になる |

#### 新規に必要な設定

| キー | 意味 | 既定 |
|---|---|---|
| `mount.write_back_metadata` | メタデータ write-back の有効/無効。`mount.write_back = true` が前提 (単独 on は warning + 無効) | **`false`** |
| `mount.write_back_max_inodes` | pending inode 数の上限 (超過でブロッキング back-pressure) | 4096 |

追加時は [settings-matrix.ja.md](settings-matrix.ja.md) / [Mkfs.ja.md](../Mkfs.ja.md) / [pgfs.toml.example](../../pgfs.toml.example) へ
反映する (実装ターンで)。Layer 3 status に pending inode 数 / 名前衝突数 / エラーステートを追加。

#### 実装順

1. **Phase 0** (上表 4 件。独立コミット・既存 e2e で回帰)
2. **投影ベンチ** — 生 SQL で「1 ファイル 1 tx」形を再現し、rsync 745 ファイル相当の投影値を取る
3. 本体: 台帳 + 可視性 (ListChildren マージ / IsDirectoryEmpty / pin) → flush tx (不変条件 + Rekey) →
   同期化ヒューリスティック 3 つ → エラーの底 (エラーステート / back-pressure / 二相 flip) → テスト


## 実装ステータス (as-built)

### ステージ別 (as-built・ステージ 1 + 1.5・〜11)

> 実装順 3 のうち **「台帳 + 可視性 → flush tx」までが実装済み** (ステージ 1)、その後の
> 敵対的レビュー 3 レンズ + コードレビューの指摘を反映した修正 (ステージ 1.5) まで完了。
> **この節はステージ 1 + 1.5 時点の記録**で、同期化ヒューリスティック 3 つ / close-no-flush /
> エラーの底 / 二相 flip は **[ステージ 2 の as-built](#ステージ別-as-builtステージ-2) が正**。
> 検証: build 0 error / Linux e2e **43 passed + 1 skip** (`write_back_metadata` off / on 両方) /
> [writeback.sh](../../tests/linux/writeback.sh) 8/8 / [wbmeta.sh](../../tests/linux/wbmeta.sh) 4/4。

#### 主な追加・変更ファイル

| ファイル | 役割 |
|---|---|
| [DirtyNamespace.cs](../../src/core/src/Api/DirtyNamespace.cs) (新規) | pending inode 台帳 (`byId` + `parent_id → name` 索引 / 状態機械 / 行スナップショット / 監査行 / 世代カウンタ / 統計) |
| [Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs) (新規・`Api` の partial) | pending 化 / coalesce / 純キャンセル / materialize / flush tx (不変条件 + 名前衝突解決 + Rekey) / 監査キャプチャ |
| [Api.cs](../../src/core/src/Api/Api.cs) | 各操作の振り分け・`ListChildren` マージ・`IsDirectoryEmpty`・`FlushInode`/`FlushDirectory`・統計/実効 config |
| [InodeCache.cs](../../src/core/src/Api/InodeCache.cs) | pin (byId のみ退避対象外) + pending 解決フック (id / parent+name) |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | `FSyncDir` を本実装 (`fi.fh` 逆引き付き) / `OpenDir` で `fi.fh` に dir の inode id |
| [Schema.cs](../../src/core/src/Config/Schema.cs) | `mount.write_back_metadata` / `mount.write_back_max_inodes` |
| [wbmeta.sh](../../tests/linux/wbmeta.sh) (新規 4 件) | メタデータ write-back 専用テスト |

#### 設計からの差分 (as-built)

1. **状態機械から `FlushingRedirty` を落とした**。設計は「flush は開始時 snapshot だけを書き、Flushing 中に
   来た rename 等は次回へ」としていたが、実装は **`Flushing` 中の coalesce を拒否** (`CoalesceResult.Busy`) し、
   呼び出し側が **NSGate で flush 完了を待って write-through に落とす**。状態機械は
   `Dirty → Flushing → Persisted` の 3 状態。
   * 理由: 当て直し (commit 後に差分を write-through で当てる) は **`DirtyFile.Gate` を保持したまま別 tx を
     開く**形になり、ロック階層 `NSGate → Gate → tx` の逆辺 (`Gate → NSGate`) の温床になる。しかも
     実機 e2e で一度も踏まれない = テストできないコードになる。
   * 代償は「flush 中に chmod/utimens/rename が来ると tx が 1 本増える」だけ。pending の寿命
     (interval 既定 1000ms) に対し flush 中の窓は数十 ms なので実害は小さい。
   * 副作用: coalesce が**常に成功する前提の記述** (決定 1 の表・上記 §pending 台帳と可視性) は
     「Dirty のときだけ coalesce・Flushing 中は write-through」に読み替えること。
2. **決定 2 (close は同期 flush しない) はステージ 1 では未実装** → **ステージ 2 で実装済み**
   (下 §1e 実装ステータス ステージ 2)。close-no-flush は同期化ヒューリスティック (a)(b)(c) が塞ぐ前提の
   穴とセットで、片方だけ入れると「旧も新も失う」窓を意図的に開けることになるため 4 点同時に入れた。
   → **ステージ 1 時点では 1e の性能上の取り分は出ていない** (rsync の chmod/utimens は close 後 =
   persisted なので coalesce に乗らない)。台帳・可視性・flush tx・祖先チェーン・純キャンセルは
   `mkdir` / `symlink` (close を持たない) 経由で完全に exercise される。
3. **flush tx は「開始時に固定した行スナップショット」だけを見る**。lock 対象 / 祖先の生存確認 /
   `(parent, name)` の占有者検索 / INSERT のすべてが snapshot 由来で、live な `Inode` を読まない。
   live を読むと coalesce rename と交差して **ロックも生存確認もしていない別の親の配下に INSERT** し得る。
4. **pending 兄弟との名前衝突は EEXIST**。設計に明記していなかったが、write-through にフォールバックすると
   pending 兄弟は DB に行が無いので `ON CONFLICT` が発火せず**同名 inode が 2 つ**でき、後の flush が
   勝った側の行と data 行と全チャンクを DELETE する (write-through では原理的に作れない破壊)。
5. **名前衝突の解決は同種のみ**。決定 4 の dir/dir (既存 id 採択 = Rekey) と file/file (明示 DELETE + INSERT)
   はそのまま。**種別違い** (pending dir vs 既存 file 等) と**非空ディレクトリ**は
   「巻き添え削除もデータ破棄も選べない」ので **error latch で拒否** (throw)。
   tx 内でメモリを書き換えないため、Rekey / 置換 forget / 通知は `PendingFlushOutcome` に積んで **commit 後**に適用する
   (tx 内で書き換えると rollback 後に「実在ディレクトリの id を持つ pending」が残り、その `rmdir` が
   純キャンセルで DB を触らないまま成功して次の `ls` でディレクトリが復活する)。
6. **監査パーティションの ensure を flush tx の外に出した**。設計は「行ごとの occurred_at の月で ensure」
   だけを言っていたが、**tx の内側から呼ぶと mount 全体がハングする**。
   `pg_locks` 実測 (2026-08-10、dev サーバ PG 17.5): 監査行の INSERT は親テーブルに `RowExclusiveLock` を
   取り、**その tx が開いている間、別セッションの `CREATE TABLE ... PARTITION OF` はブロックする**。
   `EnsureAuditPartition` は Citus 制約のため**別コネクションの autocommit** で DDL を撃つので、
   自 tx が親を掴んだまま呼ぶと**待ちグラフが 2 セッションに分断されて PG のデッドロック検出器が
   閉路を見つけられず、既定 `lock_timeout = 0` で無限待ち**になる。しかもこの flush は NSGate 配下なので
   **`kill -9` 以外で復旧できない**。対処は ① 書く監査行の月集合を洗い出して **tx を開く前に一括 ensure**
   ② DDL に `SET lock_timeout = '5s'` (規約が破れてもハングせず例外で落ちる)。
   → **この落とし穴は 1e 固有ではない**ので [audit-log.ja.md](audit-log.ja.md) にも記録した。
7. **`{prefix}data` 行の `created_at` も操作時刻を明示指定** (不変条件 ④ を data 行にも適用)。
8. **Persisted 残骸は best-effort** (上記 §pending 台帳と可視性 に反映済)。件数上限
   (4096) を超えたら古い順に間引く。
9. **`cache_max_entries` と `write_back_max_inodes` の関係**: pending inode は pin されて LRU 退避できないので、
   **実効的なキャッシュ上限は `cache_max_entries + pending 数`**。`cache_max_entries` (既定 1024) が
   `write_back_max_inodes` (既定 4096) より小さいと「pin 以外を全部捨てても上限に届かない」状態になり、
   put ごとに `byId` 全体をソートして子リストキャッシュを消し続ける
   (実測: pending 2000 件で `evictions=979` / `childrenLists=0` / 500 mkdir が 0.76 → 0.98 秒)。
   → 退避を **`byId.Count - pinned.Count <= low-water` で打ち切る** + root を明示的に退避対象外にする +
   **起動時と live 変更時に warning** を出す (修正後の実測: `evictions=0` / 劣化なし / 0.68 秒)。
10. **監査行はすべて台帳ロック配下の API 越しに触る** (Add / 列挙 / Clear)。無同期の `List<T>` を
    操作スレッド・背景 flush・fsync の 3 者で共有すると `Collection was modified` で**正当な fsync が -EIO**
    になり、Add と Clear の同時進行で監査行が欠落する。per-entry 256 / 孤児キュー 8192 の**件数上限**も付けた
    (error latch した pending を touch し続けても 1 tx で大量 INSERT にならないように)。
11. **孤児監査 (純キャンセルの create/delete ペア) は fsync / fsyncdir / 背景ループ / unmount で流す**。
    背景ループは **`write_back` の状態に関わらず**孤児監査だけは流す (さもないと
    `write_back_interval_ms = 0` で unmount まで 1 行も出ない)。
12. **`rmdir` の空判定を tx 内でも作り直す** (`AssertDirectoryEmptyInTx`)。tx 外の判定と DELETE の間に
    子が INSERT されると到達不能な孤児サブツリーが残る (1e では pending が滞留するぶん窓が伸びる)。
    `CancelPendingInode` も pending 子を再確認して非空なら取り消さない。
13. **discard (祖先が消えていた) は同期契機で `-EIO`**。`PendingDiscardedException` を投げ、背景 flush 経路
    (`TryFlushPending`) だけが warning に落とす。黙って 0 を返すと fsync が嘘をつく。
14. **`FSyncDir` は `fi.fh` 逆引き付き**。`OpenDir` で `fi.fh` に dir の inode id を載せ、path が引けない
    ときは id で逆引きする。他クライアントの rename で「dir と pending 子は生きているのに旧パスでは
    引けない」状態になったとき、path だけを見ていると pending 子を 1 件も flush せずに成功を返す
    (Phase 0-2 で file 側 `FlushPath` を塞いだのと同型の穴)。

#### テスト (実施済み) と置けなかったもの

* [wbmeta.sh](../../tests/linux/wbmeta.sh) **4 件** (新規・マウントを自分で張り替え、DB は psql で直接確認):
  pending 兄弟と同名 create の EEXIST / pending の `truncate 0` 後に `inode.data_id` と `{prefix}data` 行が
  整合 (+ `du` 0) / pending の pin が `cache_max_entries` を溢れさせても退避が暴走しない (`evictions` で判定) /
  pending create が並行 `ls` に必ず出る。
  **差別力の実測**: 後者 2 件は該当修正を外すと FAIL する (`data 行 0 の孤児参照` / `evictions=139`)。
* **回帰テストを置けなかった 2 件** (どちらも修正自体は保持):
  ① 「pending 兄弟と同名の create が write-through にすり抜ける」race — FUSE 層の存在確認
  (`GetByPath`) を通り抜ける窓は 8 スレッド barrier でも再現せず、仮に踏んでも dir 同士は flush 時の
  既存 id 採択で自己修復し、file 同士は「同名 race の敗者が消える」とシェルから区別できない。
  ② 台帳 generation の ListChildren ガード — `InsertInodePending` が毎回 `InvalidateChildren` するため、
  generation 抜きでも 60 回 × 並行 `ls` で再現しない。
  → **どちらも FS 越しには判定不能で、unit テスト層が本来の置き場所** (このリポには unit テストプロジェクトが無い)。

### ステージ別 (as-built・ステージ 2)

> ステージ 2 の scope = **close-no-flush + 同期化ヒューリスティック 3 つ + エラーの底 4 段 +
> live off の二相化を同時に**入れる。close-no-flush だけを入れると「旧を即消して新を遅らせる」窓を
> 意図的に開けることになるため、4 点セットが前提。
> 検証: build 0 error (warning 31 = ベースライン) / Linux e2e **43 passed + 1 skip**
> (`write_back_metadata` off / on 両方) / [writeback.sh](../../tests/linux/writeback.sh) 8/8 /
> [wbmeta.sh](../../tests/linux/wbmeta.sh) 4/4 + 手動シナリオ (下記)。
> **ステージ 2 の自動テストは 追加済** (wbmeta.sh 16 件 / 下記 §ステージ 2 のテスト)。

#### 主な追加・変更

| ファイル | 役割 |
|---|---|
| [Api.WriteBackMetadata.cs](../../src/core/src/Api/Api.WriteBackMetadata.cs) | 二相 flip / エラーステート / 同期 close の判定と印 / ブロッキング back-pressure / unmount の期限付き retry + 喪失レポート / rename-over-existing の単一 tx / write-through create 前の pending 兄弟 materialize |
| [Api.cs](../../src/core/src/Api/Api.cs) | `CloseInode` (close 契機の唯一の入口) / create の `exclusive` 分岐 / `TruncateData` の同期 close 印 / `Rename` のヒューリスティック a フック / `ReadData` のチャンクサイズ解決 / エラーステートの write・create ブロック / stats・実効 config |
| [DirtyNamespace.cs](../../src/core/src/Api/DirtyNamespace.cs) | `HasErrorLatch` / `DescribePending` / `TryReindex` が Persisted 残骸に負けないよう修正 |
| [DirtySet.cs](../../src/core/src/Api/DirtySet.cs) | `DescribeDirty` (喪失レポート用) |
| [FileSystem.cs (fuse)](../../src/fuse/src/FileSystem.cs) | `Flush` / `Release` を `CloseInode` 経由に / `Create` で `O_EXCL` 判定 / `Rename` の前処理を `PrepareRenameReplace` に |
| [FileSystem.cs (dokan)](../../src/dokan/src/FileSystem.cs) | `Cleanup` を `CloseInode` 経由に / `FlushFileBuffers` が dir なら `FlushDirectory` / `MoveFile` の前処理を `PrepareRenameReplace` に (ビルドのみ) |
| [Program.cs (mount)](../../src/mount/src/Program.cs) | unmount 後に明示 `Dispose` → 未 flush 残があれば **exit 4** |
| [StatusCommand.cs](../../src/ctl/src/StatusCommand.cs) | エラーステートを赤で表示 |
| [Schema.cs](../../src/core/src/Config/Schema.cs) | `mount.write_back_flush_timeout_ms` (既定 30000) を追加 |
| [Mount.ja.md](../Mount.ja.md) | **利用者向けの契約** (§write-back — 何を失うか / 例外 3 つ / 報告経路 4 段 / 既定 off の理由) |

#### 設計からの差分・判断 (ステージ 2)

1. **close-no-flush は「データもメタデータも書かない」**。判定は `Api.CloseInode` に集約し、FUSE `Flush` /
   `Release`・Dokan `Cleanup` はそこへ委譲する。`write_back_metadata` が false のときは 1d の挙動
   (close で同期 flush) を**厳密に維持**する (writeback.sh の close 耐久テストはこちらの契約)。
2. **ヒューリスティック (a) は真に単一 tx にできた** — ただし **pending source のときだけ**。
   仕組みは「台帳の上で先に置換先の名前へ付け替え (tx ゼロ) → そのまま同期 flush」。pending は DB に行が
   無いので rename は *INSERT 時の名前の選択*でしかなく、flush tx の file/file 衝突解決
   (明示 DELETE + INSERT) がそのまま**原子的な置換**になる。実測 (dev サーバ): `mv tmp target` 1 回で
   置換対象の行・data 行・チャンクが消え、新 inode + data + chunk + 監査 4 行が 1 tx で出現。
   * **persisted source は従来どおり 2 tx** (`FlushInode` → rename)。ただし順序が
     「新データ commit → target 削除 + rename commit」なので**喪失窓は無い** (クラッシュしても旧 target は
     健在で、source も旧名で残る)。失うのは原子性 (temp が見える瞬間) と tx 1 本ぶんの速度だけ。
   * **ディレクトリの置換は対象外** (非空 dir の巻き添え削除 / dir 同士は既存 id 採択という別の判断が要る)。
     エディタ保存 / `sed -i` / dpkg / rsync が踏むのはファイルの置換だけ。
   * 副作用として **`TryReindex` が `Persisted` 残骸に負けていた**のを直した。直す前は「このマウントで
     一度 materialize した名前」への rename が常に write-through に落ち、(a) が**実機で 1 回も発火しなかった**。
     残骸は「存在確認の窓を狭める best-effort」であって正しさの根拠ではない (行は DB にあるので
     `ls` / `stat` は DB 側で解決できる) ため、ぶつかったら台帳から外して先へ進む。
   * 監査は **1 削除 = 1 行**にした。`DeleteInodeInTx` に `writeAudit: false` を足し、置換の delete は
     操作時キャプチャ側 (`reason: write_back_metadata_rename_replace` / `replaced_by`) だけを書く
     (両方書くと同じ削除が 2 行出るうえ、背景 flush スレッドでは caller が取れない)。
   * 置換で消えた inode の**ハードリンク兄弟のキャッシュ無効化**を追加 (ステージ 1 は `out _` で捨てていた =
     `st_nlink` が stale になる)。
3. **ヒューリスティック (b) の印は fd 単位ではなく inode 単位**。設計は「`Open` で `fi.flags & O_TRUNC` を
   見て印を付ける」だったが、実装は **`Api.TruncateData` の中**で付ける。`O_TRUNC` 付き open も
   `truncate(2)` も `ftruncate(2)` も同じ経路を通るので、1 か所で全部拾える (FUSE は O_TRUNC を別 syscall で
   送ってくることもあり、fi を見る実装では取りこぼす)。印は flush 成功で落ち、上限 4096 件。
   → 設計より広い (縮める truncate も対象) が、失うものが同じなので意図的にそうしている。
4. **ヒューリスティック (c) は「pending にして即 materialize」ではなく write-through 作成**。
   即 materialize では、他クライアントが同名を先に INSERT していた場合に flush tx の衝突解決
   (file/file = DELETE + INSERT) が働いて**両方の O_EXCL create が成功**してしまう = ロックの意味が消える。
   DB の一意制約に判定を委ねる (= `ON CONFLICT DO NOTHING` が 0 行 → EEXIST) のが唯一正しい形。
5. **`mkdir` は遅延 (pending) のまま** — レビュー側の裁定 (mkdir も同期 materialize) に**反対した**。理由:
   ① `mkdir` を同期にすると **pending ディレクトリが原理的に生まれない**ので、1e の中核である祖先チェーンの
   同 tx INSERT (不変条件 ①) と dir/dir の既存 id 採択 (決定 4 の Rekey) が**到達不能コード**になる
   (テストできないコードを残すのはステージ 1 で `FlushingRedirty` を落としたのと同じ判断)
   ② `mkdir` ロックは POSIX の保証ではなく慣習で、実在のロックプリミティブ (git `index.lock` / dpkg /
   `lockfile`) はほぼ `O_EXCL` create ③ 失敗しても FS は壊れない (dir/dir は flush 時の既存 id 採択で
   自己修復し、失うのはアプリ層の相互排他だけ) ④ 現行の回帰テスト
   ([wbmeta.sh](../../tests/linux/wbmeta.sh) の pin 検証) が pending ディレクトリで書かれている。
   **§対象操作の線引き の表 (mkdir = 遅延) に寄せ、§同期化ヒューリスティック の表の「mkdir /
   O_EXCL create はロックプリミティブ」の記述のうち mkdir は採らなかった**。
   喪失は [Mount.ja.md](../Mount.ja.md) の契約表に「`mkdir` をロックに使うツールは 2 クライアントが同時に成功し得る」と明記した。
   **切り替えは 1 行** (`Api.CreateDirectory` の `exclusive: false` → `true`) なので、方針を変えるならそこだけ。
6. **エラーの底 4 段の実装形**:
   | # | 検出 | ブロック | 報告 |
   |---|---|---|---|
   | ① error latch | `DirtyFile.Error` / `PendingInode.Error` / エラーステート (`RequiresSyncClose`) | しない | close を**同期 flush に格下げ**して `-EIO` (クリーンなファイルの close だけがマークのみ) |
   | ② unmount | `Api.Dispose` → `FlushAllForShutdown` が `UnflushedCount()` を見る | `mount.write_back_flush_timeout_ms` を期限に指数バックオフで retry | 残ったら **Error ログに「失われる pending inode / dirty データ」を最大 32 件列挙** + `mount.pgfs` **exit 4** |
   | ③ エラーステート | flush 失敗 5 連続 (`NoteFlushFailure`。**discard は数えない**) | `WriteData` / `InsertInode` の先頭で `-EIO` | Error ログ + heartbeat stats の `writeBack.errorState` → `pgfsctl status` に **赤で `!! write-back ERROR STATE`**。flush 1 回成功で自動解除 |
   | ④ back-pressure | `PendingCount > write_back_max_inodes` | **上限を下回るまでブロック** (期限は同じ設定。前進が無ければ指数バックオフ) | 期限切れは warning + 続行 (無期限ブロックは mount のハングと区別できない) |
   * **設計の「unmount は EBUSY 相当で拒否」は実現できない**ので上記②の形にした:
     `fusermount3 -u` / `umount(8)` は**カーネル側で unmount を完了**させ、FUSE デーモンは拒否権を持たない。
     待ち続ける実装は「絶対に unmount できないマウント」を作るだけなので、
     **期限 + 何が失われるかの列挙 + exit code** に置き換えた (`--foreground` / systemd から観測できる)。
   * ③ は設計が挙げた「監査パーティション ensure の恒久失敗」だけでなく、**ステージ 1.5 で残した
     「種別違い衝突・非空 dir 衝突の無限リトライ」もここで可視化される** (実機で確認: 種別違いの行を psql で
     作って fsync × 5 → 赤表示 + 新規 write が `-EIO` → 行を消して fsync → 自動解除)。
7. **live off は二相**。`metadataIntakeClosed` (volatile) を立てて `MetadataWriteBack` を実効 false にし
   (= 新規 create は write-through へ)、`FlushAll` で抱えている pending を書き切ってから
   `config.Mount.WriteBackMetadata` を落とす。
8. **ついでに塞いだ穴 2 つ** (close-no-flush で踏み抜きやすくなるもの):
   * **write-through create / hardlink が pending 兄弟を潰す窓**: `MaterializePendingChildLocked` で
     `(parent, name)` を占めている pending を先に実体化し、**NSGate を INSERT 完了まで保持**する
     (id 予約失敗のフォールバック / 二相 flip 中の create / `O_EXCL` / `ln` が該当)。
   * **`ReadData` が予約 data_id で落ちる**: `{prefix}data` 行が無い状態で `LoadChunkSize` (`QuerySingle`) を
     呼んで `-EIO` にしていた。1d では close が必ず flush するので露見しなかったが、close-no-flush では
     「close 済みだが DB に行が無いファイルを読む」が**日常**になる (実測: 12 テストが FAIL した)。
     `ResolveChunkSizeForRead` で DB → 台帳 → 既定の順に解決する。
9. **新規設定 1 本**: `mount.write_back_flush_timeout_ms` (既定 30000)。back-pressure のブロック上限と
   unmount の flush 期限を**同じノブ**にした (どちらも「flush の成功を待ってよい時間」なので分ける理由が無い)。

#### 手動で確認したシナリオ (dev サーバ・schema `pgfs_test`)

`--write-back --write-back-metadata --write-back-interval-ms 0` (背景 flush 無効 = 判定が決定的) で、
FS 越しの挙動と psql での DB 状態を突き合わせた。テスト自動化はこの一覧が起点になる:

1. **close-no-flush**: `echo hello > f` → close 後も inode 行が DB に無い / `cat` は読める → `fsync` で行が出現。
2. **(a) rename-over-existing**: persisted target + pending temp → `mv` 1 回で行が 1 本だけ入れ替わり
   (旧 data 行・チャンクは消え、新 total_size = 11)、ログに「単一 tx で実体化」。監査は
   create(temp) / rename / delete(reason=rename_replace) の 4 行で caller uname も操作者。
3. **(b) O_TRUNC**: persisted ファイルを `O_TRUNC` で開いて書いて **close だけ** → DB の `st_size` が
   即更新 (= 同期 flush に格下げ) + ログに格下げ記録。
4. **(c) O_EXCL**: `O_EXCL` create 直後 (fsync 前) に DB 行あり。通常 create / `mkdir` は行なし・`ls` には出る。
5. **二相 off**: `pgfsctl config set mount.write_back_metadata false` → pending 3 件が全部 DB に出る。
6. **エラーステート**: 種別違いの同名行を psql で作る → fsync × 5 が `-EIO` → 新規 write も `-EIO` →
   `pgfsctl status` に赤の ERROR STATE (理由付き) → 行を消して fsync → 解除され write 復帰。
7. **back-pressure**: `--write-back-max-inodes 8` で 40 ファイル作成 → ハングせず 0.37 秒で完走、
   `ls` 40 件 / DB 33 件 (残り ≤8 が pending)。
8. **unmount**: 正常系は全 pending が DB に入り exit 0。**flush できない pending を残した場合**
   (`--write-back-flush-timeout-ms 2000` + 種別違い衝突) は 2 秒粘ってから
   「失われる pending inode: id/parent/name/size/error」「失われる dirty データ」を Error ログに列挙し **exit 4**。
9. **hardlink が pending 名を潰さない**: pending 名への `ln` は EEXIST。
10. **status**: `write-back(m): on / pending N of 4096 inodes / … / conflict / cancel / discard` +
    実効 config に `mount.write_back_flush_timeout_ms`。

#### ステージ 2 のテスト (追加・wbmeta.sh 4 → 16 件)

上の手動シナリオを [wbmeta.sh](../../tests/linux/wbmeta.sh) に自動化した (内訳表は
[tests/linux/README.ja.md](../../tests/linux/README.ja.md))。結果は**当時 15 passed / 1 failed** だった
(+ 回帰: [writeback.sh](../../tests/linux/writeback.sh) 8/8・Linux e2e は `write_back_metadata`
off / on の両方で 43 passed + 1 skip)。

> **✅ その 1 failed は A-7 で解消済み。** いま `wbmeta.sh` は **27 件すべて緑**である
> (2026-09-21 に再走して確認)。塞いだのは
> [metadata-write-back-reviews.ja.md の A-6 / A-7](metadata-write-back-reviews.ja.md) で、
> **印を落とす条件を「実際に未 flush を書き切ったときだけ」に変えた** — 何も書いていない close では
> 落とさないので、**`truncate` 側の close で印が消費されなくなった**。
> 以下は**その不具合が何だったか**の記録である。

* **当時の FAIL 1 件は未修正の不具合の再現テストだった** — `test_meta_truncate_syscall_close_is_synchronous`。
  ヒューリスティック (b) の印は `Api.TruncateData` で inode 単位に付くが、**印を消費するのは
  「truncate を発行した fd の close」**なので、`truncate(2)` と書き込みが別 fd に分かれる
  `truncate -s 0 f; cmd >> f` (coreutils の `truncate` は open → ftruncate → **close** する) が
  素通りする。実測: `truncate -s 0` で DB の `st_size` が **同期で 0 になり**、続く append は
  pending のまま → `kill -9` で **ゼロ長ゴミ** (旧も新も失う)。
  ステージ 2 の scope が禁じた「旧を即消して新を遅らせる」窓がこの経路で開いている。
  → 対処案: 印を「close 1 回で消す」のではなく **flush 成功まで inode に残す** / または
  truncate 自体を pending 側に寄せる。**close-no-flush の契約 (close は境界でない) の範囲内という
  解釈も可能**なので、ゼロ長ゴミを許容するか否かの裁定が要る。
  → **決着済**: 下 §ラウンド A の修正 の A-6 / A-7 で「印は実際に未 flush を書き切ったときだけ落とす」
  に変更して窓を塞いだ (許容はしなかった)。このテストは緑化し `wbmeta.sh` は 17/17 になった。
* テストで判明した副次的な事実 (テストの書き方に影響する):
  * **`cp` は新規宛先を `O_CREAT|O_EXCL` で開く** (strace 確認) → ヒューリスティック (c) で
    write-through になり pending にならない。`> file` は `O_TRUNC` 付き。
    **pending を作るには `O_EXCL` も `O_TRUNC` も付かない create** (python `os.open(p, O_CREAT|O_WRONLY)`)
    か `mkdir` を使う。
  * `mv` の宛先が**存在しない**場合は (a) が発火せず pending のまま (DB 行が出ない) = (a) のテストの
    差別力の裏付け。
* **自動化しなかったもの**: 手動シナリオ 8 の後半 (unmount の期限 + 喪失レポート + exit 4)。
  `mount.pgfs` は自分でフォークして常駐するため**テストスクリプトから終了コードを取れない**
  (`ssh dev sudo` 経由や mount(8) 経由でも同じ)。`--foreground` で起動して PID を掴めば取れるが、
  その場合はスクリプト側で unmount と待ち合わせの機構を別に作る必要があるため未着手。

#### ステージ 2 の後に残っている穴
* **persisted source の rename-over-existing は 2 tx** (喪失窓は無いが原子的ではない)。1 tx にするには
  データ flush を rename tx に同梱する改造が要る。
* **ディレクトリの rename-over-existing は単一 tx 化の対象外** (write-through 2〜3 tx のまま)。
  pending target を置換する場合も「target を materialize してから削除」で 3 tx になる
  (純キャンセル + coalesce rename に畳めるはずだが未実装)。
* **データ側 back-pressure (`write_back_max_bytes`) は one-shot のまま**。件数側だけブロッキングにした
  (1d の挙動を変えないため)。バイト上限も flush 失敗時に突破し続ける点は同じ。
* **エラーステートのしきい値 5 は定数** (設定にしていない)。テストから狙って踏ませるには psql で
  衝突行を作る必要がある。
* adoption (dir/dir の既存 id 採択) 時の追加ロックが昇順一括取得の後になる点 (40P01 リトライに委ねている)。
* **Dokan 側は on モードも実機検証済 (2026-09-21)** — `CloseInode` / `FlushDirectory` / `PrepareRenameReplace` は配線済で、
  既定 off の回帰に加えて **[wbmeta.ps1](../../tests/windows/wbmeta.ps1) が実 2 マウントで on モードを通る**
  (4 passed + 1 skip。skip は `unlink` による回復の観測で、Windows が `DeleteFile` を遅延させるため
  この環境では観測できないもの)。件数の正は [tests.ja.md](../tests.ja.md)。
* `mkdir` の cross-client 可視性 (上記差分 5 の裁定)。


## 変更記録

時系列の記録は [metadata-write-back-reviews.ja.md](metadata-write-back-reviews.ja.md) が持つ
(敵対的レビューのラウンド A / B の修正記録が 700 行を超えるため、切り出してある)。
**それ以外の時系列はここに追記する。**

- [runtime-control-plane.ja.md](runtime-control-plane.ja.md) が 1,802 行に肥大したため、
  機能ごとに分割してこの doc を切り出した。内容は分割前のまま。
  レビュー記録はさらに [metadata-write-back-reviews.ja.md](metadata-write-back-reviews.ja.md) へ分けた。
