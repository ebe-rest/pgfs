# ハンドル文脈 (OpenFileContext) の共通化 設計

> **道順**: [docs/README.md](../README.md) › **本書**
>
> **この doc が正である範囲**: ハンドル文脈 (`OpenFileContext` / `HandleTable`) の設計・段階・
> 未解決の論点・分担。**段階 A〜D の進め方はここが正**。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [windows-parity.md](windows-parity.md) | Windows 展開設計 (§共通クラスと責務 が段階 D の元案) |
> | [metadata-write-back.md](metadata-write-back.md) | 同期 close 印 (A-4〜A-7)。**ハンドルには移さない**判断の根拠 |
> | [fuse-binding.md](fuse-binding.md) | `fuse_file_info` の扱い |

**設計案。段階 A〜C-3 は実装済み** 。**段階 D (共通操作クラス) だけが未着手**である。
本文には**当時の設計案のまま残している節**があるので、**as-built は各段階の §実装ステータス / §as-built を見ること**
(C-1 / C-2 / C-3 はこの doc の後半にある)。**冒頭のこの行と本文が食い違っていたのを 直した** —
索引側のラベルも同時に直してある。
Windows 側の as-built は [windows-parity.md §実装ステータス](windows-parity.md) を参照。

## なぜやるか (実測した問題)

いまは **開いたファイルの識別がパス優先**で、ハンドルに載っているのは「解決済み `Inode` オブジェクト」だけ。
そのせいで次が起きている (すべて 2026-09-19 までに実機で確認したか、コードで確定した事実):

| # | 問題 | 根拠 |
|---|---|---|
| 1 | **開いた後に名前が変わると別のファイルを触る** | `Read` / `Write` は `fi.fh` を使わずパスで引く (FUSE)。Dokan も `Resolve` がパス起点。別クライアントが rename → 同名再作成すると既存 fd が別 inode を指し得る |
| 2 | **ハンドルが古い属性を持ち続ける** | Dokan の `info.Context` に載せた `Inode` はリモート invalidate 後も再取得されない。`Size` / `DataId` が古いまま使われる |
| 3 | **呼び出し元を取れる場所が 1 箇所しかない** | Dokan の `GetRequestor` は `CreateFile` の中でしか成功しない (実測: 他コールバックからは 1 e2e で 3400 件失敗)。**ハンドルに載せる以外に持ち回る方法が無い** |
| 4 | **ハンドル単位の同期方針を持てない** | `FILE_FLAG_WRITE_THROUGH` は「このハンドルの書き込みは即永続」というハンドル属性。現状は Dokan 側だけが `OpenFile.WriteThrough` として持ち、Core は知らない |
| 5 | **同じ data を指すリンクで `FileIndex` が一致しない** | `ByHandleFileInformation.FileIndex` に `inode.Id` を入れている。Windows は「同じファイルか」をこれで判定する |
| 6 | **削除後 I/O の生存管理が無い** | Windows は最後のハンドルが閉じるまで読み書きを許す。pgfs は最初の `Cleanup` で DB から消す |

**1〜4 はすでに OS 層で部分的に手当てしている**が、Dokan と FUSE で別々に持っているので二重実装になっている。
Windows 側の `FileSystem.OpenFile` (inode + 監査主体 + WriteThrough) が事実上の先行実装で、これを Core へ引き上げる。

## 設計

### `OpenFileContext` (Core・新規)

**1 ハンドル = 1 インスタンス**。OS 層が open 時に作り、close で捨てる。

| 要素 | 型 | 意味 | 備考 |
|---|---|---|---|
| `InodeId` | `long` | **安定識別子**。open 時に確定し、以後この値で引く | パスは open と名前操作のときだけ使う |
| `Inode` | `Inode?` | 属性のスナップショット | **再取得可能**にする (キャッシュ invalidate 後は捨てて引き直す) |
| `Access` | enum | read / write / delete などの要求 | Dokan の `FileAccess`、FUSE の `O_*` を共通語彙へ |
| `Durability` | enum | `Default` / `WriteThrough` | 4 の受け皿 |
| `Audit` | `AuditContext?` | **open 時に確定した主体** | 3 の受け皿。Windows は `CreateFile` でしか取れない |
| `TruncatedByThisHandle` | bool | このハンドルが truncate を発行したか | **診断のみ**。1e の「同期 close 印」の**安全性は inode / data キーのまま**にする (下の §同期 close 印はハンドルに移さない) |
| `DeletePending` | bool | 削除予約済み | 6 の入口 |

**ハンドル id は 1 から採番する**。`fi.fh` の `0` は現行コードが**未設定の番兵**として使っている
(`FSyncDir` の逆引きが `fi.fh != 0` で判定)。**しかもルート inode は `id = 0`** なので、
現行の「`fh` に inode id を載せる」実装では **`OpenDir("/")` の `fh` が 0 になり逆引きをすり抜ける**。
**ただし現状は到達不能**: 逆引きが要るのは「他クライアントの rename でパスが引けない」場合だけで、
**ルートは rename できない**ので `GetByPath("/")` が null になる経路が無い。
**段階 A で `fh` の意味がハンドル id になり 1 始まりになるときに消える**ので、ここだけ先に採番をずらすことはしない
(`Open` / `Create` と規約が食い違って次の罠になる)。

**Core に持ち込まないもの**: Windows のトークン / SID そのもの、`DokanFileInfo`、`fuse_file_info`。
文字列と id まで落として持つ (現行の `FileSystem.OpenFile` と同じ方針)。

### OS 層のマッピング

| | Windows (Dokan) | Linux (FUSE) |
|---|---|---|
| 置き場所 | `DokanFileInfo.Context` (現行 `OpenFile` を置き換え) | `fuse_file_info.fh` に **ハンドル表のキー**を載せる |
| 生成 | `CreateFile` | `open` / `create` / `opendir` |
| 破棄 | `CloseFile` | `release` / `releasedir` |
| 備考 | 既に `OpenFile` があるので載せ替えるだけ | `fh` は 64bit 整数なので **Core 側にハンドル表 (id → context) が要る** |

FUSE は `fi.fh` に直接オブジェクトを載せられないので、**Core にハンドル表 (id → context)** を置き、`fh` にはそのキーを入れる。
Phase 0 ② で `FlushPath` が `fi.fh` に inode id を載せているので、**キーの意味を「inode id」から「ハンドル id」へ変える**改修になる
(fsync / fsyncdir の逆引きはハンドル表経由に変わる)。**ディレクトリも対象**で、`OpenDir` が `fh` を載せ `FSyncDir` が逆引きしている。

#### `fh` の採番は **1 始まり** (決定, Linux 側の指摘)

**ハンドル id は 0 を使わない。** 0 を「載せていない」の番兵に予約する。

理由: 現状 `fi.fh` には **inode id をそのまま載せている**が、**ルート inode は `id = 0`** なので
「ルートのハンドル」と「`fh` を載せていない」が区別できない。レビューで記録した
**`FSyncDir` の逆引きがルートで無効になる**問題の正体である。

段階 A で `fh` の意味が「inode id」から「ハンドル id」へ変わるので、**採番を 1 始まりにすればこの罠は
自然に消える**。**逆に 0 始まりにすると罠がそのまま残る**ので、ここは設計として固定する。

### ハンドル表の後始末 (リークさせない)

**`release` / `releasedir` は best-effort で、エラー経路では呼ばれないことがある**。放置すると
`OpenFileContext` が `Inode` を握り続け、pin と同じで LRU も効かないまま増える。設計に次を含める:

| 項目 | 方針 |
|---|---|
| 消す責任 | OS 層の close コールバック (`CloseFile` / `release` / `releasedir`) が第一。**表側にも上限を持つ** |
| 上限 | 新ノブ (仮 `mount.max_open_handles`) で件数上限。超過したら**新規 open を拒否** (黙って古いものを捨てない) |
| 可視化 | **`pgfsctl status` Layer 3 に「開いているハンドル数」を出す** (1e の pending 件数と同じ枠) |
| 縮退 | 上限到達は「漏れている」ことの兆候なので、**Warning ログ + status に出す**。自動回収はしない |

## 段階 (挙動を変えない順)

| 段階 | 内容 | 受入条件 |
|---|---|---|
| **A. 抽出** | `OpenFileContext` を Core に作り、Dokan の `OpenFile` を置き換える。FUSE はハンドル表を作って `fh` を載せ替える (**ファイルとディレクトリの両方**)。**挙動は変えない** | 両 OS の既存スイートが全部緑。**件数は増え続けるので [tests.md](../tests.md) を正とする** |
| **B. 識別をハンドルへ** | `Read` / `Write` / `Flush` / `SetEndOfFile` を **`InodeId` 起点**に変える (パス解決をやめる)。属性は**毎回引き直す** | ① 問題 1 の再現: 「A で開いたまま B が rename + 同名再作成 → A の read/write/fsync が最初の inode に向かう」(Windows は `crossclient.ps1`、Linux は e2e) ② **問題 2 の再現: flush による `data_id` 付け替え (materialize / A-1 の兄弟付け替え) を跨いだ write が、付け替え後の data に届く** ③ **性能を測る** (下) |
| **C. 生存管理** | 最終ハンドルの解放まで実体を保つ (`DeletePending` + 参照カウント)。削除後 I/O を Windows の意味論に合わせる | 「開いたまま削除 → 既存ハンドルで読める → 最後の close で消える」。Windows は実測済みの delete-pending 挙動 (別ハンドル保持中は列挙に出るが open は失敗) と整合させる |
| **D. 共通操作クラス** | [windows-parity.md §共通クラスと責務](windows-parity.md) の `FileSystemOperationsBase` + OS 派生を、C までで揃った文脈の上に置く | 既存テスト緑 + 重複コード (create / flush / close / rename の手順) が 1 箇所に寄る |

**A だけでも価値がある**: 3 (監査主体) と 4 (WriteThrough) が Core の語彙になるので、
**write-back の背景 flush に「誰が・どのハンドルの要求で」を持ち回れる**ようになる (1e の監査の積み残し)。

**段階 B は性能を測ってから決める** — と当初書いたが、**懸念の中身は静的確認で否定された**。
測る目的が「B の可否を決める」から「**B に回帰が無いことを示す**」に変わった。
根拠は下の **§段階 B の受入条件と API 面 ④** を参照。

## 段階 B の受入条件と API 面

段階 A が land した直後に両担当で擦り合わせた内容。**①② は両担当で一致**、
**③〜⑤ は Dokan 側 (Core 担当) の回答**で Linux 側の確認待ちである。
段階 B は **A と違って挙動が変わる**ので、何が変わってよくて何が変わってはいけないかを先に固定する。

### ① 変わらなければならない (新規テストで検出する)

| # | 契約 | Linux | Windows |
|---|---|---|---|
| 1 | A が open したまま B が rename + 同名再作成 → **A の read / write / fsync は最初の inode に着弾する** (問題 1) | e2e に追加 | [crossclient.ps1](../../tests/windows/crossclient.ps1) に追加。**別マウント = 別プロセス**なので Windows の共有ロックに阻まれず再現できる |
| 2 | flush による `data_id` 付け替え (materialize / A-1 の兄弟付け替え) を跨いだ write が、**付け替え後の data に届く** (問題 2) | e2e | crossclient。Windows では `FileSystem.Resolve` が **キャッシュした `Inode` を返し続ける**のが同じ問題の姿である |

**新規テストは修正前ビルドに当てて、落ちること + 落ち方のメッセージが原因を指していることまで
確認してから land する** ([next.md §引き継ぐ作法](../next.md))。

### ② 変わってはいけない (既存スイートが緑)

- **1e の同期化規律 A-4〜A-7 は動かさない**。とくに `truncate -s 0 f; cmd >> f` (truncate と append が
  別 fd) の窓が戻らないこと。印は **inode / data の両キー**のままで、`OpenFileContext` に
  `TruncatedByThisHandle` を足さない (§同期 close 印はハンドルに移さない)。
- **POSIX の「unlink 後も fd が生きている」は段階 B では実現しない**。ハンドルの解決に失敗したら
  **現状と同じくエラー**を返す (いまもパス解決が `ENOENT` で落ちるので回帰ではない)。生存管理は段階 C。
- 件数の正は [tests.md](../tests.md)。着手時点では **Linux 7 スイート** (e2e off 47+1skip /
  e2e on 47+1skip / writeback 9 / wbmeta 27 / negcache 7 / crossclient 8 / startup 5) と
  **Windows 5 スイート 59 件** (e2e 35 / crossclient 7 / writeback 6 / wbmeta 5 = 4+1skip / control-plane 6)。

### ③ Core の API 面 (先に凍結する)

`OpenFileContext` に**安定識別子**を足し、`Api` の 6 本に **ハンドル文脈を受ける版**を足す。

```csharp
// OpenFileContext (段階 B で追加)
public long InodeId { get; }        // open 時に確定し以後不変。これが判定の主
public Inode? Inode { get; set; }   // スナップショット。解決のたびに詰め直す

// Api (既存の Inode 版は残したまま、ctx 版を足す)
public int  ReadData      (OpenFileContext h, long offset, Span<byte> destination);
public int  WriteData     (OpenFileContext h, long offset, ReadOnlySpan<byte> source);
public bool TruncateData  (OpenFileContext h, long newLength);
public void FlushInode    (OpenFileContext h);
public void CloseInode    (OpenFileContext h);
public void FlushDirectory(OpenFileContext h);
```

| 決めたこと | 理由 |
|---|---|
| **渡すのは `OpenFileContext` であってハンドル id ではない** | Dokan は表を通さず `DokanFileInfo.Context` にオブジェクトを載せる。id 版にすると **Windows だけ毎回ハンドル表を引く**ことになる。FUSE は各コールバックの頭で `Handles.Get(fi.fh)` して ctx に変換する |
| **既存の `Inode` 版は「使い捨て文脈」として同じ芯を通す** | 識別の実装を 2 つにしない。`fh` がある経路は段階 B 以降かならず ctx 版へ寄せる |
| **解決 (`InodeId` → `Inode`) は `Api` の中に閉じる** | 内部方針 (毎回 `GetById` / 世代印つきスナップショット) を後から変えても **アダプタ側は無変更**で済む。これが ④ の前提でもある |
| **`HandleTable` は FUSE 専用のまま** | Dokan の `FileSystem.Resolve` は属性問い合わせ等で**場当たりに ctx を作る**経路があり、表に入れると close されず漏れる。`pgfsctl status` Layer 3 の「開いているハンドル数」も当面 **FUSE 側の値** (Windows は n/a) になる |

#### ③ の実装ステータス (as-built)

**Core は land 済み** ([Api.Handle.cs](../../src/core/src/Api/Api.Handle.cs) 新規 +
[OpenFileContext.cs](../../src/core/src/Api/OpenFileContext.cs))。**この時点ではまだ挙動は変わらない** —
アダプタが ctx 版を呼び始めるまでは既存の `Inode` 版が使われるためである。

- `OpenFileContext` のコンストラクタは **`Inode` を非 null で受ける**ようにした。`InodeId` は
  open 時に確定する値なので、**null を許すと「識別子を持たないハンドル」が作れてしまう**。
  既存の呼び出し (Dokan 7 箇所 / FUSE 3 箇所) はすべて解決済み inode を渡していたので影響は無い。
- **解決は 2 本に分けた**。設計時は 1 本のつもりだったが、**close 経路だけ意味が違う**ため:

  | | 使う場所 | inode が消えていたとき |
  |---|---|---|
  | `ResolveHandle` | `ReadData` / `WriteData` / `TruncateData` | **`Api.StaleHandleException`** (OS 層で ESTALE 相当へ落とす) |
  | `TryResolveHandle` | `FlushInode` / `CloseInode` / `FlushDirectory` | **no-op** (`null` を返すだけ) |

  理由: **消えた inode への flush は「書くものが無い」であってエラーではない** (dirty は削除側で
  discard 済み)。段階 A までの FUSE `FlushPath` も「本当に消えていたら 0 を返す」で運用してきた。
  ここを `ResolveHandle` に揃えると **unlink 済みファイルの `close` が毎回 `-EIO` を返す**ことになる。
- `TryResolveHandle` は解決に失敗したら `handle.Inode` に **`null` を詰める** (古いスナップショットを残さない)。
- **検証**: Windows で `dotnet build pgfs.sln -c Release` が 0 エラー (新規の警告なし)。
  **実機スイートは回していない** — 挙動が変わるのはアダプタを反転させる次の一手からで、
  そこで両 OS の全スイートを回す。

### ④ 性能ゲートは「先に測る」+ 実装と並行でよい

測定は Linux 実機でしかできないので **FUSE 側が [performance.md](performance.md) の
作法 (デーモンの消滅まで待つ / 毎回 md5 で整合性を確認する) で回す**。

**ただしゲートの中身は変わった (Linux 側の静的確認)。** 当初の懸念
「id 起点にすると毎回 `GetById` になり、**pending inode は pin で必ず当たるが persisted は LRU から
落ちうる**ので条件次第で DB 往復が増える」は、**[InodeCache](../../src/core/src/Api/InodeCache.cs) を
読む限り成り立たない**:

- **`byId` が権威**である。`EvictIfOverCapacity` は LRU 退避のあとに
  **`byId` に存在しない id を指す `byPath` / `childrenByParent` を掃除して整合させる**。
  したがって **`byPath` に当たるなら `byId` にも必ず当たる** — **id 起点が path 起点より
  ミスしやすくなることは構造上ありえない** (逆は起きる。id で load した inode は `byId` にしか載らない)。
  唯一の非対称は `Invalidate(id, null)` が `byPath` を残す経路だが、**そのとき両方ミスする**
  (`get(path)` は `byPath` → `byId[id]` = null でミスに落ちる) ので順序は変わらない。
- **ヒット時のコストはむしろ下がる**。`get(path)` は `byPath` → `byId` の**辞書 2 回**、
  `get(id)` は `byId` の**1 回**。さらに `Read` / `Write` の先頭の `PathToString`
  (= `Encoding.UTF8.GetString` + string アロケーション) が**丸ごと不要になる**。
  これは [performance.md](performance.md) の改善候補 5 (ROI 中 / 工数 **極高**) が、
  **ホットパスの 2 本に限っては段階 B のついでに片付く**ということでもある。
- **ミス時も増えない**。id ミスは `selectInodeByIdQuery` の **SELECT 1 本**、
  path ミスは `load(parser, ...)` の**パスチェーン走査**である。

→ **ゲートは通った**。FUSE アダプタ反転後に取り直して **tx/file = 32.1 で変化なし**
(= DB 往復は増えていない)、時間も全ワークロードが振れ幅の内側だった
([performance.md §結果: 段階 B (FUSE アダプタ反転後)](performance.md))。
**時間の改善は主張しない** — W1 は 2.12 → 1.95 s と縮んだが振れ幅 36% の内側でノイズと区別できない。

以下は判断の経緯。**段階 A の基線は取得済み**
([performance.md §実測: handle-context 段階 B の基線](performance.md))— 単一 PG で
W1 rsync 4 KB × 300 = 2.12 s / W3 write 64 MiB = 0.88 s / W4 cold read 64 MiB = 0.23 s、
**W1 の `xact_commit` = 32.1 tx/file**。

**判定は tx/file に置く。** 時間の振れ幅が 4〜36% なのに対し tx/file は **0.3%** で、
しかも「DB 往復が増えたか」を直接見る指標だからである。削れるのは 1 コールバックあたり
**辞書 1 回 + UTF-8 デコード 1 回**で、チャンク I/O (ms 単位) に対して桁が違うので、
**時間で「速くなった」は出ない見込み**。

**ただし ③ のシグネチャは測定を待たずに凍結できる** — 解決が `Api` の中に閉じているので、
結果が悪ければ内部を差し替えるだけで FUSE / Dokan のアダプタは書き直さずに済む。
したがって **測定と Core 実装は並行**でよく、**land 前のゲート**として結果を見る。

### ⑤ ハンドル無しで来る呼び出しの契約

- FUSE で `fi` が null になり得るのは **`GetAttr` / `ChMod` / `Chown` / `Truncate` /
  `UpdateTimestamps` の 5 本** (`FuseFileInfoRef` を取るもの)。**`Read` / `Write` は libfuse が必ず
  `fi` を渡す**ので対象外である。
- **read-ahead は 1c が未実装なので現時点で経路が無い**。契約だけ先に決めておく形になる。
- 契約: **`fh` 有り = ctx の `InodeId` 起点 / `fh` 無し = パス → id を 1 回引いてから同じ芯**。
  見えるものが食い違うのは **「rename + 同名再作成」の一点だけ**で、**そこは食い違うのが正しい**
  (fd は open 時の inode を指し、パスは新しい inode を指す)。これを明記しておけば
  「`fh` の有無で見え方が違う」を仕様として言い切れる。

### 実装ステータス — FUSE アダプタ (FUSE 側)

**反転済み**。[src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs):

| コールバック | 段階 B での識別 |
|---|---|
| `Read` / `Write` | **`fh` のみ** (libfuse が必ず `fi` を渡す)。表から引けないときだけ `ReadByPath` / `WriteByPath` に落ち、**Warning を出す** (落ちたら段階 A の窓が開いている) |
| `Flush` / `FSync` (`FlushPath`) / `FSyncDir` | **`fh` が主・パスが従**に反転。ctx 版は `TryResolveHandle` なので**消えていたら no-op で 0** (段階 A の「本当に消えていたら 0」を保つ) |
| `Truncate` | `fh` 有りは ctx 版 (`ResolveHandle` = 消えていたら `-ESTALE`)、無しはパス |
| `GetAttr` / `ChMod` / `Chown` / `UpdateTimestamps` | `ResolveByHandleOrPath` で ⑤ の契約どおり |

`Api.StaleHandleException` は **`-ESTALE`** に落とす (`-EIO` ではない)。

**ホットパスから `PathToString` が消えた** — `Read` / `Write` はエラー時しかパスを文字列化しない。
[performance.md](performance.md) の改善候補 5 (工数 **極高**) が、この 2 本に限って反転のついでに片付いた形である。

**テスト** ([tests/linux/crossclient.sh](../../tests/linux/crossclient.sh) に 2 件追加・8 → 10 件):

- `test_xc_open_fd_sticks_to_inode` — **問題 1 の再現**。**段階 A のビルドで落ちることを確認済み**
  (`開いたままの fd への write が最初の inode に届いていない ... = 'ORIGINAL'` = 書き込みが
  後から作られた同名ファイルへ着弾していた)。**同一マウント内の rename では再現しない** —
  カーネルの dentry が一緒に動くのでパス起点でも正しい inode に当たってしまう。
  **B から rename されると A のカーネルは名前が変わったことを知らない**ので、そこで初めて割れる。
- `test_xc_open_fd_follows_data_repoint` — **問題 2 のガード (再現テストではない)**。
  **段階 A のビルドでも緑**である: Linux は段階 A でも `Read` / `Write` が毎回 `GetByPath` で
  引き直していたため。問題 2 が牙を剥くのは**解決結果を抱え続ける Windows の `FileSystem.Resolve`** の側で、
  こちらは「`ResolveHandle` をスナップショット使い回しに変えたら落ちる」ためのガードとして置いてある。

**回帰**: Linux 7 スイート全緑 — e2e off 47+1skip / e2e on 47+1skip / writeback 9 / wbmeta 27 /
negcache 7 / **crossclient 10** / startup 5。

**⑥ の Linux 側 (リーク回帰テスト) は land 済み** — 下の §⑥ のリーク回帰 を参照。
~~**段階 C (生存管理) は未着手**である~~ → **段階 C は C-1 / C-2 / C-3 とも実装済み**。
この行は段階 B を land した時点の記述である。**現在地は冒頭の行が正。**

### 実装ステータス — Dokan アダプタ (Dokan 側)

**反転済み**。[src/dokan/src/FileSystem.cs](../../src/dokan/src/FileSystem.cs) の変更は **3 箇所だけ** —
Dokan は識別が `Resolve` 1 本に集約されていたので、そこを id 起点にすれば全コールバックが反転する。

| 箇所 | 段階 B での識別 |
|---|---|
| `Resolve` | **ハンドルがあれば `Api.TryResolveHandle` で毎回引き直す** (段階 A までは「open 時に解決した `Inode` をそのまま返す」)。**パスを使うのはハンドルが無い経路だけ** (属性の問い合わせ等) |
| `Cleanup` の削除 (`DeletePending`) | 消す相手も id から引き直す。**既に消えていればスキップ** |
| `FlushOnCleanup` | ctx 版の `Api.CloseInode(open)` へ (消えていたら no-op) |

`ReadFile` / `WriteFile` / `FlushFileBuffers` / `GetFileInformation` / `SetEndOfFile` /
`SetAllocationSize` / `SetFileAttributes` / `SetFileTime` / `GetFileSecurity` / `SetFileSecurity` は
**すべて `Resolve` 経由**なので、呼び出し側は 1 行も変えていない。

**ctx 版 6 本を使うのは `CloseInode` だけ**である。`IsDirectory` / `WriteToEndOfFile` の事前判定に
inode が要る経路で ctx 版を呼ぶと、**同じ呼び出しで 2 回解決する** (`Resolve` で 1 回 + ctx 版の中で 1 回)。
識別の実装は Core の `TryResolveHandle` 1 本のままなので、③ の「識別を 2 つにしない」は満たしている。

**stale の扱い**: Windows は従来どおり **`FileNotFound`** を返す (`STATUS_FILE_INVALID` にはしない)。
段階 A まで「解決できない = `FileNotFound`」だったので、**エラーコードを変えない**。

**テスト** ([tests/windows/crossclient.ps1](../../tests/windows/crossclient.ps1) に 2 件追加・7 → 9 件):

- `test_x_append_handle_sees_peer_growth` — **問題 2 の再現** (Linux 側では書けない方)。
  `FILE_APPEND_DATA` で開いたハンドルは Dokan から `WriteToEndOfFile = true` で降り、pgfs は
  **その時点の `inode.Size` を書き込みオフセットにする**。**段階 A のビルドに当てて落ちることを確認済み**で、
  落ち方は `期待 'AAAA1BBBBBB2' / 実際 'AAAA12' (len=6)` = **相手マウントが伸ばした 6 バイトを
  上書きして消し、サイズまで縮んだ** (データ損失そのもの)。
  - **.NET の `FileMode.Append` では再現しない** — FileStream が自分で末尾へシークして
    **明示オフセットで書く**ので `WriteToEndOfFile` が立たない。Win32 `CreateFileW` を
    **`FILE_APPEND_DATA` だけ**で開く必要がある (テストは P/Invoke している)。
  - **伸ばすのは別マウントから**でなければならない。同一マウント内だと `InodeCache` の
    **同じ `Inode` インスタンス**が更新されるのでハンドル側も一緒に新しくなり、検出力がゼロになる
    (`InodeCache.Invalidate` は**エントリを削除する**ので、notify 経由なら次の load で別インスタンスになる)。
- `test_x_handle_follows_inode_after_peer_rename` — **問題 1 のガード (再現ではない)**。
  **段階 A のビルドでも緑**である: Dokan は段階 A の時点でハンドルに解決済み `Inode` を載せていたので、
  パスで引き直してはいなかった。**id 起点へ反転させても壊れないこと**を守るために置く。
  問題 1 の本物の再現は **FUSE 側** ([tests/linux/crossclient.sh](../../tests/linux/crossclient.sh)) にある。

**回帰**: Windows 5 スイート全緑 — e2e 35/35 / **cross-client 9/9** / write-back 6/6 /
metadata write-back 4 passed + 1 skip / control-plane 6/6 (windows_client + Dokan 2.3.1 →
pgsql_server の `pgfs` スキーマ)。skip は既知の `test_meta_defer_conflict_recovers_by_unlink`
(Windows が `DeleteFile` を最後のハンドルまで遅らせるため、この環境では回復を観測できない。契約は Linux 側で検証済み)。

### ⑥ `HandleTable.Count` を出す

段階 B のついでに **`pgfsctl status` Layer 3 へ「開いているハンドル数」を出す** (Core + status は
Dokan 側)。これが出れば **ハンドルリークの回帰を Linux の e2e から書ける**
(FUSE 側が書く)。値の意味は ③ のとおり **FUSE 側のハンドル数**である。


#### ⑥ の実装ステータス (as-built, Dokan 側)

**入った** ([HandleTable.cs](../../src/core/src/Api/HandleTable.cs) / [Api.cs](../../src/core/src/Api/Api.cs) の
`BuildStatsJson` / [StatusCommand.cs](../../src/ctl/src/StatusCommand.cs))。

- `{prefix}mounts.stats` の heartbeat スナップショットに **`handles: { open, peak }`** を足した。
  `pgfsctl status` のテキスト出力は `handles      : N open / peak M`。
- **`peak` も出す**理由: `open` だけだと「**いま**開いている数」しか分からず、**既に静かになったマウントで
  過去に漏れていたか**を問えない。`Rent` のたびに best-effort で更新する (厳密な同時性は要らないので
  ロックを取らない)。
- **件数は `ConcurrentDictionary.Count` ではなく `Interlocked` のカウンタ**で持つ。前者は **全ロックを取る**ので、
  診断のために open / close のホットパスを止めることになる。id は払い出しごとに一意で `Return` は
  存在したときだけ減らすため、辞書の件数と必ず一致する。
- **Windows は常に `0 open / peak 0`** — Dokan は表を通さず `DokanFileInfo.Context` にオブジェクトを
  載せるため。「開いていない」ではなく**「数えていない」**であることを [Pgfsctl.md](../Pgfsctl.md) に明記した。
  Windows でも数えたくなったら、Dokan アダプタが `Rent` / `Return` を通るようにするのが先である
  (`FileSystem.Resolve` が作る**使い捨て文脈は close されない**ので、そのまま借りると漏れる)。
- **テスト**: [control_plane.ps1](../../tests/windows/control_plane.ps1) の `test_cp_status_json_shape` に
  **キーの存在チェック**を足した (件数は 6 のまま)。**値の検証ではない** — Windows では常に 0 なので、
  数える意味があるのは Linux 側だけである。ここで守りたいのは「**キーが消えて Linux のリーク回帰が
  黙って無意味になる**」ことで、`null` 判定なので修正前ビルドに当てる類のテストではない。
- **リーク回帰は FUSE 側が書く** (`HandleTable` を実際に使うのは FUSE 側なので)。

## append の末尾は誰が決めるか

**段階 B の副産物**として出てきた別の穴。ハンドルの識別を直しても、**「どこへ書くか」を OS 側が
決めている経路が残っていた**。

| | 末尾を決めていたのは | 症状 |
|---|---|---|
| Linux | **カーネル** (`generic_write_checks` が `i_size_read`) | 他マウントの伸長を知らないオフセットが降りてくる。実測で **B の 6 バイトを上書きして消した** |
| Windows | **pgfs** (Dokan は `WriteToEndOfFile` と言うだけ) | 段階 A まではハンドルが握った古い `Inode.Size` を使っていた。実測で **11 → 6 バイトの損失** |

**「誰が末尾を決めるか」が OS で違い、どちらも他マウントの伸長を見ていなかった**のが本体である。

**決定 (案 ②)**: **`Api.AppendData(OpenFileContext, source)` を Core に置き、末尾は Core が
解決し直して決める**。FUSE は `fi.flags & O_APPEND` で、Dokan は `WriteToEndOfFile` でここへ寄せる。
**追記先を決める実装が 1 箇所**になるので、不可分化 (案 ③) へ上げるときも直すのは 1 箇所で済む。

- **write-back 中は手元の dirty サイズが権威** (`AppendOffsetOf`)。`Inode.Size` だけを見ると、
  **その inode が LRU から落ちて DB から読み直された**ときに flush 前の dirty 領域の途中へ書いてしまう。
- **契約の正は [Mount.md §append の契約](../Mount.md)**。不可分を保証するのは
  **① write-through かつ ② 1 回のコールバックに収まる書き込み**のときだけである。
- **②でも「喪失 → 交錯」への変化は起きる**ので、**契約文は実装と同じコミットで land する**
  (Linux 側の指摘。挙動が変わったのに契約が追いついていない状態を作らない)。

~~**案 ③ は段階 C と同時に設計する**~~ → **段階 C を待たずに単独で入った**。
見積もっていた「inode 行のロック」は**要らなかった** — `LockData` が既に同じ実体への書き手を
直列化しているので、**ロック取得後に plain SELECT を 1 本撃つだけ**で足りる。したがって Citus の
shard 接触順の検討も不要だった。下の §案 ③ の実装ステータス を参照。

**実測の根拠** (FUSE 側):

- `max_write` は **negotiate 値** (実測ホストでは **1 MiB**。128 KiB ではない)。200 KiB の追記は分割されず、
  4 MiB は 1 MiB × 4 回に割れた。
- **カーネルは最初の `i_size` から全コールバックのオフセットを一度に決める** — 4 MiB の追記中に
  相手から 10 バイト × 12 を割り込ませても A のオフセットは厳密に累積のままで、**相手の 120 バイトが
  丸ごと消えた**。
- したがって **`max_write` 超えの追記は「喪失させない」までが限界**で、ローカル FS と同じ不可分性
  (1 回の `write(2)` の間 `i_rwsem` を握る) は FUSE のコールバックからは作れない。

### 案 ③ の実装ステータス (as-built)

**入った**。**末尾は書き込みトランザクションの中で確定させる**。

```csharp
// WriteDataThroughOnce
this.LockData(conn, tx, dataId);
if (appendAtEnd) { offset = this.ReadSizeInTx(conn, tx, inode.Id) ?? inode.Size; }
```

- **inode 行のロックは要らなかった**。当初「inode 行をロックして末尾を確定させる = Citus の shard
  接触順を崩すのでデッドロック retry 前提」と見積もっていたが、**`LockData` が既に同じ実体への
  書き手を直列化している**ので、**ロック取得後に plain SELECT を 1 本撃つだけ**で足りる。
  **plain SELECT はデッドロックの辺を作らない**ので、shard 接触順の規約 (inode は tx 末尾で 1 回) も
  壊さない。**この見落としのおかげで、段階 C を待たずに単独で入れられた。**
- **Citus でも待ち明けの値が見える**ことを実機で確認済み (rf=2)。プローブのログで、
  2 回目以降の「lock 後に読んだ `st_size`」が**手元のキャッシュより常に相手のコミット分だけ先**を
  行っていた。
- **合図に負のオフセットを使わない**。`WriteData` の入口が `offset < 0` を 0 で返すので、
  **FUSE から見ると短い write = 書き込み失敗 (`-EIO`)** になる (Linux 側がプローブで踏んだ)。
  `WriteDataAppend` という別の入口を作り、`appendAtEnd` フラグで `WriteDataThrough` へ伝える。
- **write-back 有効時は対象外**。tx が無いので tx 内で決めようがなく、相手のバイトがまだ DB に
  無い以上、原理的に不可分にできない。**手元の dirty サイズが権威**のまま (`AppendOffsetOf`)。
- **同じハンドルからの並行 append も同じ仕組みで閉じる** (全員が `LockData` で直列化されてから
  末尾を読む)。実測では 4 分割のコールバックは**直列**だったが、**並行でないことの証明ではない**ので
  設計は並行前提のままにしてある。

**実測** (2 マウント同時追記・A が 4 MiB / B が 10 バイト × 12):

| | 差 |
|---|---|
| 対応前 | **−4,194,304** |
| `st_size` の単調化のみ | Linux **−40** / Windows **−10** |
| **末尾を tx の中で確定 (現行)** | **0** (Linux 3 回 / Windows 3 回とも緑) |

**契約の正は [Mount.md §append の契約](../Mount.md)**。「交錯する (順序が混ざる)」と書く予定だった
部分は**不要になった** — 各コールバックが直前のコミットの続きから書くので、混ざるのは
「**他マウントの追記が分割の隙間に挟まる**」であって、1 回の `write(2)` のバイトが失われたり
前後したりはしない。

## 未解決の論点 (実装前に決める)

1. ~~**誰が持つか**~~ → **決定**: **別クラス `HandleTable` を作り、`Api` が
   `private readonly` フィールドとして持つ**。`Api` は既に
   `InodeCache` / `ContentCache` / `DirtySet` / `DirtyNamespace` / `IdReservation` の **5 つを
   まったく同じ形で持っている**ので、前例に揃えるだけで「`Api` を太らせない」と
   「flush 経路 (`CloseInode` / `FlushInode`) から引ける」を同時に満たす。新しい流儀を増やさない。
2. ~~**同期 close 印をハンドルへ移すか**~~ → **移さないことに決定 (Linux 側レビュー)**。§同期 close 印はハンドルに移さない を参照。
3. **削除後 I/O の契約**: Linux (POSIX) は「unlink 後も fd が生きている」が正、Windows は「最後のハンドルまで名前も残る」。
   **どちらに寄せるか**ではなく、**Core は参照カウントだけ持ち、名前の見え方は OS 層が決める**という分担にしたい。
4. **ロック**: ハンドル表はロックフリー (ConcurrentDictionary) で足りるが、C の参照カウントは inode 単位の排他が要る。
   Citus の shard 接触順の規約 ([support_for_citus.md](support_for_citus.md)) を壊さないこと。

#### ⑥ のリーク回帰 (as-built, FUSE 側)

**[tests/linux/handles.sh](../../tests/linux/handles.sh) を新設した (6 件)**。
e2e ではなく専用スイートにしたのは、**スナップショットを制御 NOTIFY の ping で撃たせるのに
psql が要る**からである ([e2e.sh](../../tests/linux/e2e.sh) はスタンドアロンを保つ)。

| テスト | 見るもの |
|---|---|
| `test_handles_idle_is_zero` | 何もしていないマウントは `0 open` |
| `test_handles_file_open_close_balances` | fd 3 本で 3 open → 閉じて 0 |
| **`test_handles_dup_fd_survives_first_close`** | **fd を複製して片方だけ閉じても残ったほうが生きている** |
| `test_handles_readdir_balances` | `ls` を 20 回しても 0 に戻る (OpenDir / ReleaseDir) |
| `test_handles_zero_after_mixed_workload` | 混在ワークロードのあと 0 |
| `test_handles_peak_records_concurrent_opens` | `peak` は閉じても下がらない (単調増加) |

**検出力を 2 つの壊し方で確かめた** (プローブは撤去済み):

- **`Return` を `Release` から `Flush` へ移す** → **3 件が落ちた**。
  これは実際に起き得る壊し方である — **bash の `exec 9< file` は開いた後に中間 fd を閉じる**ので、
  `Flush` が 1 回降り、**fd がまだ生きているのにハンドルが表から消える**。
  これが「返す場所は `Release` でなければならない」(段階 A) の実測である。
- **`ReleaseDir` から `Return` を外す** → `test_handles_readdir_balances` が
  **`ls を 20 回した後に open が 0 に戻らない (got '20')`** で落ちた (数も一致)。

## 同期 close 印はハンドルに移さない (決定)

当初「ハンドル側に持てば『別 fd で truncate → 別 fd で append』の窓が自然に閉じる」と書いたが、**逆**だった
(Linux 側レビューでの指摘):

- A-7 が塞いだのは **truncate と append が別の fd** で起きるケース。印をハンドルに持つと
  **truncate した fd の close で印ごと消える**ので、続く append の fd には印が無く、**A-7 の修正前と同じゼロ長ゴミに戻る**。
- A-5 が印を **inode と data の両キー**に積んでいるのは、**ハードリンクの兄弟 inode を開いた別のハンドル**にも効かせるため。
  ハンドル単位では表現できない。

→ **安全性の担保は inode / data キーのまま**。ハンドルに持つのは「このハンドルが truncate を発行した」という
**診断情報 (`TruncatedByThisHandle`) だけ**。1e の規律 (A-4〜A-7) は動かさない。

## 触るファイル (見積り)

- 新規: `src/core/src/Api/OpenFileContext.cs` (+ ハンドル表)
- `src/dokan/src/FileSystem.cs`: `OpenFile` を置き換え (**現行の 3 フィールドはそのまま移せる**)
- `src/fuse/src/FileSystem.cs`: `open` / `create` / `release` で `fh` を載せ替え、`Read` / `Write` / `FlushPath` を id 起点へ
- `src/core/src/Api/Api.cs`: `CloseInode` / `FlushInode` がハンドル文脈を受け取れるように (段階 B 以降)
- テスト: 段階 B / C で「開いたまま rename / 削除」「data_id 付け替えを跨いだ write」の再現テストを両 OS に追加

## 実装の進め方

> **as-built**: 段階 A〜C は A → B → C の順で実装した (**段階 D は未着手**)。Core のハンドル表・
> FUSE アダプタ・Dokan アダプタは、同じ Core の設計の上で別々の作業として作った。

- **段階 A は挙動不変**で、受入条件は「両 OS の既存スイートが全部緑」だった。
- **段階 A だけでは問題 1 (開いた fd が rename 後に別ファイルへ着弾) は閉じない**。
  閉じるのは段階 B。A の価値は 3 (監査主体) と 4 (WriteThrough) が Core の語彙になること。
- 3 (削除後 I/O の契約) と 4 (ロック) は段階 C、性能実測は段階 B のゲートだった。

## 段階 C の Core 設計 (Dokan 側・実装済み)

> **as-built**: この節の案は合意され、**C-1 / C-2 / C-3 とも実装済み**。
> **実際に入ったものは各段階の §C-1 / §C-2 / §C-3 が正**で、以下は提案時点の記述である。
> **とくに C-2 の受入条件から `hard_remove = 1` は落ちた** (下の §なぜ `hard_remove` を立てないか)。

Linux 側の要求 3 つ (参照カウント / `DeletePending` / 最終解放で実体を消す) に対する Core 側の設計案。

### 核心は「名前が無い inode をどこに置くか」

POSIX の「unlink したが fd は生きている」を FS 自身で実現するには、**名前が消えた実体の置き場**が要る。
pgfs は **`{prefix}inode` の 1 行が「名前 + 属性」**で、**名前を消す = 行を消す**なので、そのままでは表現できない。
libfuse が `.fuse_hidden` へ rename してしのいでいるのは、まさにこの穴を外から埋めているためである。

**3 案ある。**

| | やり方 | Citus | スキーマ | 残骸の見え方 |
|---|---|---|---|---|
| **O-1 孤児の親** | `parent_id` を予約値へ移し、ツリーから外す | ❌ **破綻**。`parent_id` は**分散キー**で、Citus は分散キーの `UPDATE` を許さない (DELETE + INSERT なら可能だが監査と data_id の整合が重い) | 変更なし | 予約の親の下に残る |
| **O-2 列を足す** | `unlinked_at` を立て、`name` を一意な内部名へ書き換える。`lookup` / `readdir` は `unlinked_at IS NULL` で除外 | ⭕ 分散キーを触らない | **列追加** (mkfs の冪等追加。`{prefix}mounts` に前例あり) | **行として残る** (フィルタ漏れがあると見える) |
| **O-3 実体だけ残す (推し)** | inode 行は**いまどおり消す**。**`{prefix}data` と chunk を参照カウントが 0 になるまで消さない**。開いているハンドルは `OpenFileContext` のスナップショット + `DataId` で読み書きを続ける | ⭕ 触らない | **変更なし** | **列挙に出ない** (inode 行が無いので名前空間に痕跡が残らない) |

**推しは O-3。** 理由:

1. **pgfs はデータが `data` / `data_chunk` に分離している**ので、POSIX の「inode は生きているが名前が無い」を
   **「data は生きているが inode 行が無い」**で表現できる。**この分離は元からある資産**で、新しい概念を足さない。
2. **段階 A / B で作ったものがそのまま効く**。ハンドルは既に `InodeId` と属性スナップショットを持っており、
   `DataId` も握っている。**読み書きは data_id 起点**なので、inode 行が無くても続けられる。
3. **スキーマ変更も Citus の分散キーも触らない**。O-2 の「フィルタの付け忘れが新しい穴になる」も避けられる。
4. **残骸が名前空間に出ない**。`.fuse_hidden*` が他マウントの `ls` に出る (実測) のが今の実害で、
   O-1 / O-2 は**置き場所を変えるだけで見える可能性が残る**。O-3 の残骸は「どの inode からも参照されない
   data 行」で、**`df` には効くが列挙には出ない**。

### O-3 の具体

| | 動き |
|---|---|
| `unlink` (参照ゼロ) | いまと同じ。inode 行 + data + chunk を消す |
| `unlink` (**開いているハンドルがある**) | **inode 行だけ消す**。`data` 行と chunk は残し、ハンドルに `DeletePending` を立てる |
| 開いているハンドルの `read` / `write` | `ResolveHandle` が inode 行を引けなくても、**`DeletePending` なら ctx のスナップショットを返す** (`StaleHandleException` を投げない) |
| `fstat` | ctx のスナップショットから返す (DB に書き戻す先が無いので、サイズ等は**メモリだけで進む**) |
| 最終解放 (`Release` / `CloseFile`) | 参照カウントが 0 になった時点で **`data` + chunk を 1 tx で消す** |
| ハードリンク兄弟が居る | **何も消さない**。`st_nlink > 0` の兄弟が残っている間は今までどおり (既存の nlink 管理のまま) |

### 参照カウントの置き場と入口

**inode 単位**で数える。**`HandleTable` とは別**にする — ハンドルは複数あっても実体は 1 つで、
**Dokan はハンドル表を通らない**ため (§実装ステータス — Dokan アダプタ)。

- `Api` が `private readonly` で 6 つ目の相棒として持つ (`InodeCache` / `ContentCache` / `DirtySet` /
  `DirtyNamespace` / `IdReservation` / `HandleTable` と同じ形)。
- **入口は両アダプタ共通の明示呼び出し**にする: `Api.OpenHandle(ctx)` / `Api.CloseHandle(ctx)`。
  `HandleTable.Rent` / `Return` に相乗りさせない — **Dokan の `Resolve` が作る使い捨て文脈**まで
  数えてしまい、close されないので**カウントが戻らない**。
- **副産物**: Windows でも `handles.open` が意味のある値になる (いまは常に 0)。

### Citus の shard 接触順 (案 ③ とは違って、ここでは要る)

案 ③ が plain SELECT で済んだのは**例外**である。段階 C の最終解放は **inode / data / chunk を消す tx** で、
**既存の削除経路と同じロック順**に載せる必要がある。`LockInode` → `LockData` の順を崩さないこと
([support_for_citus.md](support_for_citus.md))。**参照カウント自体はメモリなので DB ロックを増やさない。**

### cross-mount の割り切り (**契約に書く**)

**参照カウントはマウントごとのメモリ**である。したがって:

- **同一マウント内の open-then-unlink は POSIX どおり**になる (fd が生きている間は読み書きできる)。
- **他マウントが unlink した場合は、こちらの fd は死ぬ** (`-ESTALE`)。相手のメモリにあるカウントを
  こちらは見られないし、**相手は「誰かが開いている」ことを知る手段が無い**。
- これを DB で数えると **open / close のたびに tx が 1 本増える**。ホットパスの代償が大きすぎるので採らない。
- **NFS も同じ問題を silly rename で扱っている**ので、割り切りとしては素直である。

### 異常終了が残したものの掃除 (**`.fuse_hidden*` / 孤児 data / `{prefix}mounts` を 1 本にまとめる**)

3 つとも **「異常終了したマウントが残したものを、誰も掃除しない」** という同じ形である。

| 残骸 | いまの状態 | 掃除の条件 |
|---|---|---|
| `.fuse_hidden*` (Linux) | `kill -9` で**中身ごと永久に残る**。他マウントの `ls` に出る | **段階 C 後は誰も作らない**ので一度きり。**いま開いているマウントがある間は消せない** |
| 孤児 data 行 (O-3 の残骸) | 段階 C で生まれ得る | **どの inode からも参照されない** かつ **所有マウントが生きていない** |
| `{prefix}mounts` の残骸 | `kill -9` のたびに溜まる (テストで 11 行溜まった)。**表示だけの害** | **heartbeat が切れている** かつ **喪失の墓標 (B-2) ではない** |

**共通する危険は 1 つ**: **いま生きているマウントが使っている最中のものを消すと壊す**。
したがって**どれも `{prefix}mounts` の生存と突き合わせる**必要がある。

**設計案**: `pgfsctl` に **1 つのサブコマンド**を足す (仮 `pgfsctl prune`)。

- **既定は dry-run**。何を消すかを一覧で出すだけ。`--apply` で実行する。
- **live なマウントが 1 つでもあれば、既定では孤児 data と `.fuse_hidden*` に触らない**
  (`{prefix}mounts` の残骸だけは heartbeat 切れが判定できるので消せる)。
  `--force` は**全マウントを止めてから**の運用として doc に書く。
- **喪失の墓標 (`unflushedLoss > 0`) は消さない**。B-2 が次回マウントの警告に使っている。
- **`mount` の起動時に自動実行はしない**。「自分以外に live が無い」を起動時に判定できても、
  **同時に起動している最中の相手**を見落とす窓がある。**明示的に撃つもの**にする。

### 段階と受入条件 (案)

| | 内容 | 受入条件 |
|---|---|---|
| **C-1** | 参照カウント + `Api.OpenHandle` / `CloseHandle` を両アダプタから呼ぶ。**挙動は変えない** | 両 OS の全スイート緑 / `handles.open` が Windows でも動く |
| **C-2** | `DeletePending` + O-3 の保持 + 最終解放の削除。~~FUSE は `hard_remove = 1` を立てる~~ → **採らない** (下の §なぜ `hard_remove` を立てないか)。代わりに **`.fuse_hidden*` を列挙から外す** (代案 H) | **`test_open_unlink_read_still_works` が緑のまま** (いまは libfuse が通している / C-2 後は pgfs が通す) / **`.fuse_hidden*` が他マウントに見えない** (段階 C と同じコミットで入れる) / Windows の delete-pending 意味論が変わらない |
| **C-3** | `pgfsctl prune` (掃除の統合) | dry-run が live を巻き込まない / 墓標を消さない / 実際に残骸が消える |

### C-1 の実装ステータス (as-built, Dokan 側)

**Core と Dokan 側は入った。挙動は変えていない** (数えるだけ)。**FUSE 側の配線は FUSE 側**。

- 新規 [OpenInodes.cs](../../src/core/src/Api/OpenInodes.cs) — **inode 単位の参照カウント**
  (`Acquire` / `Release` / `CountOf` / `Count`)。`Release` は**残りの参照数**を返し、**0 になったキーは
  表から落とす** (開いたことのある inode を全部覚えていると、長く動いているマウントで際限なく増える)。
- `Api.OpenHandle(ctx)` / `Api.CloseHandle(ctx)` を**両アダプタ共通の入口**として公開した。
  **`HandleTable.Rent` / `Return` には相乗りさせていない** — Dokan は表を通らないうえ、
  `FileSystem.Resolve` が**使い捨ての文脈**を作る経路を持つため。
- `OpenFileContext.Counted` を足した。**数えていない文脈を閉じてもカウントを減らさない**ための印で、
  **二重 open / 二重 close も同じ印で吸収する**。`Api` が管理し、**アダプタは触らない**。
- Dokan 側は **`AttachHandle` 1 箇所に集約**した。`CreateFile` は分岐が多く (既存を開く / 上書きで開く /
  新規作成 / ディレクトリ)、**1 つ書き漏らすとカウントが戻らない**。解放は **`CloseFile` だけ** —
  `Cleanup` は削除の契機であって close ではない (FUSE で `Flush` に置くと壊れるのと同じ話)。
- `pgfsctl status` の `handles` に **`inodes`** (開かれている実体の数) を足した。
  **テキスト出力は `handles      : N open / peak M / K inodes`**。**C-1 より前のスナップショットには
  `inodes` が無い**ので、無いときは項目ごと出さない (`NodeLong` は欠けた値を -1 で返すため、
  そのまま出すと **-1 という値に見える**)。
  **`open` / `peak` はいまも FUSE のハンドル表の値**で、Windows では 0 のまま。
  **両 OS が `OpenHandle` を呼ぶようになったら `open` をこのカウントへ寄せる**のが自然だが、
  **いま変えると Linux 側の `handles.sh` が FUSE の配線前に赤くなる**ので、C-1 の完了後に判断する。

**テスト**: [control_plane.ps1](../../tests/windows/control_plane.ps1) に
`test_cp_open_inodes_counted` を追加 (6 → **7 件**)。開く前 0 → 3 本開いて 3 → 閉じて 0。
**`OpenHandle` の呼び出しを外したビルドに当てて落ちることを確認済み**で、落ち方は
`3 本開いたのに inodes が 3 にならない (CreateFile の分岐で数え漏らし?)` と原因を指す。

**回帰**: Windows 5 スイート全緑 — e2e 35/35 / cross-client 10/10 / write-back 6/6 /
metadata write-back 4 passed + 1 skip / **control-plane 7/7**。

### C-2 の実装ステータス (Core・as-built, Dokan 側)

**Core 側は入った。FUSE 側は FUSE 側**が持つ。
~~`hard_remove = 1` ほか~~ → **`hard_remove` は採らないことで決着した** (下の
§なぜ `hard_remove` を立てないか)。**FUSE 側に入ったのは代案 H = `.fuse_hidden*` を列挙から外す**ほうである。

**入ったもの** (案 O-3 のとおり: inode 行は消し、`{prefix}data` と chunk を残す):

| 場所 | 動き |
|---|---|
| `ReleaseOrRelinkDataInTx` | 最後の参照を消すとき、**その inode をまだ開いているハンドルがあれば実体を残す** (`OpenInodes.MarkOrphan`) |
| `Api.CloseHandle` | 参照カウントが **0 になった時点で実体を落とす** (`DropOrphanData` = dirty を捨て、chunk と data 行を 1 tx で削除) |
| `Api.TryResolveHandle` | inode 行が引けなくても、**孤児なら ctx のスナップショットを返す** (`StaleHandleException` を投げない) |

**判定を `DeleteInodeInTx` の内側に置いたので、3 つの削除経路すべてに効く** —
`unlink` / `rmdir` / **`rename` の置換** (write-through と pending の両方)。レビュー ① の
「rename 置換が抜けている」はこの位置取りで構造的に閉じている。

**`DeletePending` は `OpenFileContext` ではなく Core が inode キーで持つ** (設計案からの変更)。
**同じ実体を複数のハンドルが開いている**とき、ctx 単位だと「どのハンドルが印を持つか」が決まらない。
実体の生死は inode 単位の事実なので、`OpenInodes` の中に置いた。

**最終解放の前に「本当に消えているか」を確かめる** (`DropOrphanIfGone`)。印は削除の tx の中で付けるので、
**その tx が rollback した / 同じ名前で作り直された**ときに印だけが残り得る。確認しないと
**生きているファイルの実体を消す**。

#### ⚠ Windows から突けるのは `rename` の置換だけ (2026-09-21 実測)

**`unlink` 側ではこの窓は開かない。** Windows は**最後のハンドルが閉じるまで削除を FS へ降ろさない**ためで、
`DeleteOnClose` の 2 本目を先に閉じても、1 本目が生きている間は**列挙に出続ける** (= pgfs は削除要求を
受け取っていない)。**最初これを e2e テストにしたが、修正前ビルドでも緑だった**ので捨てた
(検出力ゼロのテストを残さない)。

**`rename` の置換だけが、開いているハンドルを残したまま名前を消す。**
[e2e.ps1](../../tests/windows/e2e.ps1) の `test_rename_over_open_victim_keeps_body` (36 → **37 件**) が
そこを突く。**修正前ビルドに当てて落ちることを確認済み**で、落ち方は
`上書きされた側のハンドルが実体を失った (read が失敗): FileNotFoundException`。

**回帰**: Windows 5 スイート全緑 — e2e 37/37 / cross-client 10/10 / write-back 6/6 /
metadata write-back 4 passed + 1 skip / control-plane 7/7。

#### FUSE 側へ (FUSE 側)

- **`Unlink` / `Rmdir` / `Rename` はいまのままでよい** — `Api.DeleteInode` が内側で判断する。
- **`Release` で `Api.CloseHandle` を呼ぶ** (C-1 で配線済み)。**0 が返った時点で Core が実体を落とす**。
- ~~**`hard_remove = 1` を立てるのはこのあと**。立てた瞬間に `unlink` も rename-over も
  Core の保持経路に乗る。~~ → **立てない**。**①まで行って戻ってきた** (下の
  §なぜ `hard_remove` を立てないか)。**立てると `read` が FUSE 側まで来なくなり、rename-over も壊れる。**
  **入ったのは代案 H** = `.fuse_hidden*` を列挙から外す。
- **注意**: `Release` の中で **flush が先に走る** (`FlushPath` → `CloseInode`) と、**孤児になった inode に
  対する flush** になる。chunk はまだ生きているので実害は無いはずだが、**そこで `-EIO` を返さない**ことを
  確かめてほしい (`FinishWriteInodeInTx` は親が引けないと早期 return する)。


### なぜ `hard_remove` を立てないか (①まで行って戻ってきた記録)

**結論: `hard_remove = 1` は libfuse の高レベル API とは相性が悪い。** 立てない。
代わりに **`.fuse_hidden*` を列挙から外す** (下 §代案 H) + **残骸は `pgfsctl prune` で掃除する**。

**次に誰かが同じ道を通る** (`.fuse_hidden` を見て「pgfs 自身で消せるのでは」と思うのは自然) ので、
**どこまで行って何で戻ったか**を残す。

| 段 | やったこと | 結果 |
|---|---|---|
| 1 | `hard_remove = 1` を立てる | **`read` がこちらまで来ない** (到達回数 0)。高レベル API は**全操作にパスを要求する**ので、unlink 済み node は `get_path` の時点で `-ENOENT` になる |
| 2 | `fuse_config.nullpath_ok` を立てる (offset **96**・3.10.2 の上流ヘッダから計算し、書き戻して実測で検算) | **`read` は通る** (`path=(null)` で到達)。`dd` / `head` / `wc` / `od` は unlink 後も読める |
| 3 | しかし **`cat` が落ちる** | `strace` で `fstat(0, ...) = -1 ENOENT`。`nullpath_ok` の契約では **`getattr` の path が NULL になるのは `fi != NULL` のときだけ**だが、**カーネルは `fstat(2)` の `GETATTR` にファイルハンドルを載せてこない** (`vfs_getattr(&file->f_path, ...)` を通るので `FUSE_GETATTR_FH` が立たない) |
| 4 | **①: 孤児のパスをメモリに覚えて `fstat` に答える** | **消した名前が `stat` で蘇った** (`[ -e held.txt ]` が真・`stat` が 1 バイトを返す)。`ls` には出ないので**「一覧に出ないが stat はできる」**という半端な状態で、`.fuse_hidden` より悪い |

**④ が詰みである理由**: 高レベル API では

- `fuse_lib_lookup` → `lookup_path` → `fuse_fs_getattr(path, ..., fi = NULL)`
- `fuse_lib_getattr` (= `fstat`) → `fuse_fs_getattr(path, ..., fi = NULL)`

の**どちらも「同じパス・`fi` は NULL」で降りてくる**。こちらから見て**まったく同じ**なので、
**「`fstat` には答えるが `lookup` には答えない」が書けない**。

→ **libfuse が「覚える」のではなく「改名する」のは、この分離を作るため**だった。FS 側で同じことを
するには**実際に名前を変える**必要があり、それは**設計 O-1 (孤児の親へ `parent_id` を移す)** である。
そして **O-1 は Citus の分散キー制約で破綻する** (分散キーの `UPDATE` が許されない)。**一周して戻る。**

**低レベル API (`fuse_lowlevel_ops`) へ移れば話は別**だが、**いまそこまでやる価値は無い**。

#### 代案 H — `.fuse_hidden*` を列挙から外す (採用)

- **[Api.IsLibfuseHidden](../../src/core/src/Api/Api.cs) で判定し、`ListChildren` から外す。**
  **当時は Core に置いて両 OS に効かせた** — 実害は「**他マウントの `ls` に出る**」ことで、その他マウントには
  Windows も含まれるため。

  > **⚠ 変わった。いまは Linux (FUSE) だけで隠す。** `Api.HideLibfuseLeftovers`
  > (既定 `true`) を Dokan が `false` にする。**Windows では libfuse が `.fuse_hidden*` を作らない**ので
  > 隠す理由が無く、隠すと**「列挙に出ないのに消せない」だけが残る** — しかも Windows では
  > **`Remove-Item` / エクスプローラのように列挙を経由する経路から消せない**
  > (`[System.IO.File]::Delete` = Win32 `DeleteFile` 直接なら消せる)。
  > **「消せない」より「消す方法があるのに誰も辿り着けない」ほうが厄介**という判断である。
  > **決定と代替案の比較は [namespace-policy.md](namespace-policy.md) が正**。
- **パス解決 (`GetByPath`) からは外さない。** 外すと**その fd を生かしている当のマウントが自分の
  隠しファイルを引けなくなる** (libfuse は隠し名で `getattr` / `release` を撃つ)。
- **判定は libfuse の書式に厳密に合わせる** (`.fuse_hidden` + **16 桁の 16 進**)。接頭辞だけで弾くと
  **利用者が作った `.fuse_hidden...` という名前まで消える**。
- **`kill -9` の永久残骸は [`pgfsctl prune`](../Pgfsctl.md) が担当**する (C-3 で入った)。

**代償 (doc に書く)** — **変更後は Linux にだけ残る**:

| | 内容 |
|---|---|
| 両 OS | **行は DB に残る**ので `df` / `du` の使用量には乗る。掃除は `prune` |
| **Linux のみ** | **列挙に出ないのに `rmdir` が `ENOTEMPTY` になる**ことがある (隠しファイルが残っている親)。**見た目は空なのに消せない**ので、迷ったら `prune` を撃つ |
| **Windows** | **隠さないので、この代償は無い** — 列挙に出るし `Remove-Item` で消せるし `rmdir` も通る (変更) |
| 両 OS | **POSIX 意味論は libfuse が今までどおり通す**ので、`cat` も `fstat` も壊れない |

> **当時の見積もりが外れた点を残しておく**: この代償を書いたとき、**「迷ったら `prune` を撃つ」が
> どの環境でもできる**前提だった。実際には **Windows に psql が無く**、`prune` はデータ側を触るので
> **全マウントを止める必要がある**。さらに **`.fuse_hidden*` は libfuse が作るもの**という前提で
> 書いたが、**利用者が同じ書式の名前を自分で付ければ同じ状態になる** (判定は書式だけを見るので、
> 誰が作ったかは区別しない)。**この 2 つが、 Dokan 側で隠さないことにした理由である。**

### C-3 の実装ステータス (as-built, Dokan 側)

**入った**。[PruneAdmin.cs](../../src/core/src/Api/PruneAdmin.cs) (Core) + `pgfsctl prune`
([PruneCommand.cs](../../src/ctl/src/PruneCommand.cs))。**CLI 仕様の正は [Pgfsctl.md](../Pgfsctl.md)**。

**3 つの残骸を 1 本にまとめた** — `{prefix}mounts` の古い行 / 孤児 data 行 / `.fuse_hidden*`。
**live 判定を種類ごとに分ける** (レビュー ④) もそのとおり実装した:

- `mounts` の行 = heartbeat が `--mounts-older-than` (既定 3600 秒) より古ければ消す。
- **データを消す側 = 生きているマウントが 1 つでもあれば飛ばす** (`--force` で上書き)。
- **墓標 (`unflushedLoss > 0`) は消さない。**

**Citus で anti-join が使えない**ので、孤児 data の検出は **`data` の id 一覧と `inode.data_id` の
DISTINCT を別々に引いて手元で差を取る**。同じ制約は実測で踏んでいる (inode 同士の join でも
`the query contains a join that requires repartitioning`)。prune は管理コマンドなので往復 2 回で足りる。

**`Apply` は `.fuse_hidden*` の inode を消したあとに data 側だけ再スキャンする。** 消した瞬間に
その実体が孤児になるので、**同じ実行で拾えないと 2 回撃つ必要が出る**ため。inode を先に消すのは、
途中で落ちたときに「名前はあるのに実体が無い」行を残さないため ([data-id-lifecycle.md](data-id-lifecycle.md)
が直した不具合と同じ形)。

**実機確認 (2026-09-21・pgsql_server の `pgfs` スキーマ)**:

| | 結果 |
|---|---|
| dry-run | 古い `mounts` **104 行** / 墓標 **1 件 (保護)** / 孤児 data 0 / `.fuse_hidden` 0 を正しく表示 |
| 人工の残骸を作って dry-run | 孤児 data **1 行** と `.fuse_hidden0000000900000001` **1 件**を検出 |
| **live なマウントがある状態で `--apply`** | **`mounts` 104 行だけ削除。データ側は飛ばした** (メッセージつき) |
| マウントを止めて `--apply` | `.fuse_hidden` **1 件** + 孤児 data **2 行** (再スキャンぶんを含む) を削除 |
| DB で検証 | data 行 0 / chunk 0 / `.fuse_hidden` 0 / **墓標は残存** |

**自動テストはまだ無い。** 残骸を作るのに **psql が要る** (`.fuse_hidden` は Linux でしか自然に生まれない)
ので、**Linux 側のスイートに置くのが素直**である。Windows の e2e / control-plane から psql は叩けない。

### レビュー反映 (Linux 側の指摘を受けて確定)

**O-3 で進める** (Linux 側が同意)。そのうえで**設計を 4 点直した**。指摘の原文は下の
§Linux 側のレビュー を参照。

**① `rename` の置換も O-3 の保持を発火させる (抜けていた)**

`hard_remove = 0` が守っているのは `unlink` だけではない。**rename で上書きされる側 (victim) が
開かれているときも libfuse は `.fuse_hidden` へ逃がしている** (Linux 側が実測)。
**`hard_remove = 1` を立てた瞬間、`unlink` を直しても rename-over が壊れる。**

→ **O-3 の保持は `Unlink` / `Rmdir` と `Rename` の置換経路の両方から発火させる。**
POSIX ではどちらも「名前が消えるが実体は生きている」で同じ扱いである。
**既存の `test_rename_replace_existing` はこの穴を検出できない** — 置換の結果を見るだけで
**fd を開いていない**ため。**C-2 と同じコミットで Linux 側がテストを足す**。

**② cross-mount の割り切りは「いまもそう」である (書き方を変える)**

契約に書くのは同じだが、**「段階 C でそうなる」ではなく「いまもそうである」**と書く。
でないと**段階 C で劣化したと読まれる**。実測 (Linux 側):

```
A が open したまま B が rm → A の fd から読む → 失敗 (No such file or directory)
```

理由は構造的で、**`.fuse_hidden` へ逃がすのは unlink した側のデーモン**であり、
**B は「A が開いている」ことを知る手段が無い**ので素の unlink をする。
段階 C で変わるのは**同一マウント内が libfuse 頼みから pgfs 自前になる**ことだけである。

**③ unlink 後の論理サイズは DB に残らない (契約に書く)**

O-3 は inode 行を消すので **`st_size` の置き場が無くなり**、DB に残るのは
`data.total_size` = **占有バイト**だけになる。**スパースファイルでは論理サイズと一致しない**。
開いている間は ctx のスナップショットで足りるが、**デーモンが落ちると孤児 data の論理サイズは
分からなくなる**。prune は消すだけなので実害は小さいが、**「unlink 後のサイズはメモリにしか無い」**を
契約に一言入れる。

**④ prune の live 判定を残骸の種類ごとに分ける**

「heartbeat が切れている」だけを条件にすると、**DB 障害で heartbeat を落とした生きているマウント**の
行を消してしまう。**行を消すと次の prune からそのマウントが live に見えなくなり、
「使用中の孤児 data」を消すカスケード**が起きる。

| 残骸 | live 判定 |
|---|---|
| `{prefix}mounts` の行 (**表示だけの害**) | heartbeat 切れ + 墓標でないこと。**猶予を長めに取る** |
| 孤児 data / `.fuse_hidden*` (**データを消す**) | **`{prefix}mounts` の行が存在しないこと**を条件にする (heartbeat の新旧では判断しない)。同一ホストなら **pid の生存**も見る |

**「表示だけの害」である `mounts` の掃除のために、データを消す側の判断材料を壊さない。**

**⑤ 割り切りを契約にするならテストも置く**

cross-mount で fd が死ぬことを契約にするなら、**黙って変わったときに気づける網**が要る。
いまは「たまたまそうなっている」だけである。**C-2 で pgfs 自前の経路になる = 挙動が変わりやすい**ので、
そのときに Linux 側が足す。

**受入条件への追加** (C-2):

- **rename で上書きされる側を開いたまま置換 → victim の fd から読める** (①)
- **cross-mount で他マウントが unlink したら fd が死ぬ** — 契約どおりであることの固定 (⑤)

### 未決だった論点と、その決着

1. ~~**O-3 でよいか**~~ → **合意**。ただし §レビュー反映 ① (rename 置換) を取り込むこと。
2. ~~**cross-mount の割り切りを契約に書いてよいか**~~ → **合意**。ただし ② のとおり**「いまもそうである」**と
   書き、⑤ のテストを足す。
3. ~~**`pgfsctl prune` の既定**~~ → **合意** (dry-run + `--apply`、`--force` も可)。ただし ④ のとおり
   **live 判定を残骸の種類ごとに分ける**。

## 段階 C の Core 設計への FUSE 側からのレビュー

上の **§段階 C の Core 設計** に対するレビュー。**実測で裏を取ったものだけ**を根拠に書く。

### 賛成する点

- **O-3 (実体だけ残す) に賛成。** 新しい列も分散キーの更新も要らず、**残骸が名前空間に出ない**のが決め手である。
  `.fuse_hidden*` が他マウントの `ls` に出るのが今の実害なので、そこが構造的に消えるのは O-1 / O-2 に無い利点である。
- **`Release` が唯一の解放点 / `Rent` `Return` に相乗りさせない**に賛成。
  **`Flush` で数えると fd の複製で壊れる**ことは実測済みである (§⑥ のリーク回帰: `Return` を `Flush` へ移すと
  **bash の `exec 9< file` が中間 fd を閉じて `Flush` を 1 回降ろす**ため、fd が生きているのにカウントが戻る)。
- **prune の既定 dry-run / 起動時に自動実行しない**に賛成。「同時に起動している相手を見落とす窓」は実在する。

### ⚠ 指摘 1 (重い): **`rename` で上書きされる側が設計から抜けている**

**`hard_remove = 0` が守っているのは `unlink` だけではない。** libfuse は
**rename で上書きされる側が開かれているときも `.fuse_hidden` へ逃がしている**。

実測 (同一マウント・`dst.txt` を開いたまま `mv src.txt dst.txt`):

```
現行 (hard_remove=0):  victim の fd から読む → 'VICTIM' 読めた
                       ディレクトリに .fuse_hidden0000000400000001 が出て、close 後に消える
hard_remove=1:         victim の fd から読む → 失敗 (No such file or directory)
```

**C-2 で `hard_remove = 1` を立てた瞬間、`unlink` を直しても rename-over が壊れる。**
POSIX ではどちらも「名前が消えても fd は生きる」で同じ扱いなので、**O-3 の保持は `Rename` の
置換経路からも発火させる必要がある**。

- **受入条件に足してほしい**: 「**開いているファイルを rename で上書きしても、既存の fd から読める**」。
- **いま Linux 側にこのテストは無い。** `test_rename_replace_existing` は**置換の結果を見るだけで fd を開いていない**ので、
  この穴を検出できない。**C-2 と同じコミットで足す** (いま足すと赤いテストになるため)。

### 指摘 2: cross-mount の割り切りは **regression ではない** (実測)

**「他マウントが unlink したらこちらの fd は死ぬ」は、いまも既にそうである。**

```
A が open したまま B が rm → A の fd から読む → 失敗 (No such file or directory)
```

理由は構造的である — **`.fuse_hidden` へ逃がすのは unlink した側のデーモン**であり、
**B は「A が開いている」ことを知る手段が無い**ので、B は素の unlink をする。

→ **割り切りを契約に書くことに賛成。** ただし **「段階 C でそうなる」ではなく「いまもそうである」と書いてほしい。**
でないと**段階 C で劣化したと読まれる**。実際には**同一マウント内が libfuse 頼みから pgfs 自前に変わるだけ**で、
cross-mount の挙動は**変わらない**。

### 指摘 3: O-3 では **unlink 後の論理サイズが DB に残らない**

inode 行を消すと `st_size` の置き場が無くなる。残るのは `data.total_size` だが、
これは**占有バイト**であって、**スパースファイルでは論理サイズと一致しない**
(`AddOccupiedBytes` の doc コメントにそう書いてある)。

- 単一マウントで開いている間は **ctx のスナップショットで足りる**ので、**実用上の問題は無い**。
- ただし**デーモンが落ちると、孤児 data 行の論理サイズは分からなくなる**。prune は消すだけなので実害は小さいが、
  **契約に「unlink 後のサイズはメモリにしか無い」と一言書くのが正直**だと思う。

### 指摘 4: **heartbeat 切れ ≠ プロセスが死んでいる**

prune の条件が「`{prefix}mounts` の heartbeat が切れている」だけだと、**DB 障害で heartbeat を落とした
生きているマウント**の行を消してしまう。**行を消した後は prune からそのマウントが live に見えなくなる**ので、
**次の prune が「使用中の孤児 data」を消す**カスケードがありうる。

- `{prefix}mounts` の行削除と、孤児 data / `.fuse_hidden*` の削除とで、**live の判定を分けてほしい**。
- 少なくとも**猶予を長く取る** (heartbeat 間隔の数十倍) か、**同一ホストなら pid の生存も見る**。
  「表示だけの害」である `{prefix}mounts` の掃除のために、**データを消す側の判断材料を壊さない**こと。

### 指摘 5: 割り切りを契約に書くなら、**その挙動を固定するテストも要る**

cross-mount で fd が死ぬことを契約にするなら、**それが黙って変わったときに気づける網**が要る。
いまは「たまたまそうなっている」だけで、テストが無い。**C-2 で足すのが自然**である
(そのときには pgfs 自前の経路になっているので、**挙動が変わりやすい**)。

### 未決 3 つへの回答

| 未決 | Linux 側の回答 |
|---|---|
| **O-3 でよいか** | **よい。** ただし指摘 1 (rename 置換) を設計に取り込むこと |
| **cross-mount の割り切りを契約に書いてよいか** | **よい。** ただし**「いまもそうである」と書く** (指摘 2) + **テストを足す** (指摘 5) |
| **prune の既定** | **dry-run + `--apply` でよい。** `--force` も用意してよいが、**live 判定を残骸の種類ごとに分ける** (指摘 4) |

### C-1 の FUSE 側 (as-built, FUSE 側)

**配線済み** ([FileSystem.cs](../../src/fuse/src/FileSystem.cs))。**挙動は変えていない** (数えるだけ)。

| コールバック | 呼ぶもの |
|---|---|
| `Open` / `Create` / `OpenDir` | `OpenFileContext` を作って **`api.OpenHandle(context)`** → そのあと `Handles.Rent(context)` |
| `Release` / `ReleaseDir` | `Handles.Return(fh)` が返した文脈に **`api.CloseHandle(context)`** |

**`Rent` / `Return` には相乗りさせていない** (Core 側の方針どおり。同じ場所で 2 行になるが意味が違う)。
**`Flush` では呼ばない** — `Flush` は fd の複製ごとに降りるので、**`exec 9< file` が中間 fd を閉じた時点で
カウントが戻ってしまう** (§⑥ のリーク回帰で実測済み)。

**テストは [handles.sh](../../tests/linux/handles.sh) に 4 件追加 (6 → 10)**:

| テスト | 見るもの |
|---|---|
| `test_handles_inodes_counted` | 別々の 3 ファイルで `inodes` = 3 → 閉じて 0 |
| **`test_handles_inodes_dedupe_same_file`** | **同じファイルを 3 本開くと `open` は 3 だが `inodes` は 1** |
| `test_handles_inodes_dup_fd_is_one` | dup しても 1。**1 本目の close で減らないこと**も見る |
| `test_handles_inodes_readdir_balances` | **ディレクトリの fd を開いて 1 になり**、閉じて 0。`ls` 20 回でも 0 |

**検出力を 2 つの壊し方で確認済み** (プローブは撤去):

- **`OpenHandle` を 3 箇所から外す** → **4 件すべて落ちた** (`inodes が 3 にならない (got '0')` 等)。
- **`ReleaseDir` から `CloseHandle` を外す** → `readdir` の 1 件が `got '1'` で落ちた
  (同じディレクトリなので 20 ではなく 1 で止まる = 実体カウントの意味と整合)。

> ⚠ **`readdir` の件は当初「`ls` 20 回して 0 に戻る」だけだった**が、それだと
> **`OpenHandle` の数え漏らしを検出できない** (数えていなければいつでも 0 だから)。
> **ディレクトリの fd を 1 本開いて「上がること」も見る**形に直した
> (Linux は `open(2)` でディレクトリを O_RDONLY で開けるので `OpenDir` が走る)。

**テキスト出力にも `inodes` が出る** (後から追加。当初は JSON にだけ入っていた)。
実体カウントのテストは `{prefix}mounts.stats` の **JSON** から読み、
**テキストの描画は `test_handles_status_text_shows_inodes` の 1 件で見る** —
JSON だけ見ていると**描画が壊れても気づかない**ため。**C-1 より前のスナップショットには
`inodes` が無く、そこでは項目ごと出さない**のが正しい (欠けた値を描くと `-1 inodes` になる)。
疑似的に `inodes` を落とした行を入れて、**項目が消えること**を確認済み。

> ⚠ **`MOUNT_PID` は登録表から「このマウントポイントの最新行」で引く。**
> `pgrep -x mount.pgfs | head -1` にすると、**別のマウントポイントで動いているデーモン**
> (crossclient の B 側や消し忘れ) を掴み、**以降の status 読みが丸ごと他人の行**になる。
> 実際に踏んで**6 件が謎の FAIL**になった。別マウントを故意に残した状態でも緑になることを確認済み。

## 段階 C の Linux 側 — 実測と設計 (2026-09-20・FUSE 側)

**段階 C は「Linux に POSIX の意味論を足す」作業ではない。もう動いている。**
段階 C がやるのは、**その実現を libfuse の回避策から pgfs 自身へ移して、副作用を消すこと**である。

### 実測: libfuse が `.fuse_hidden` へ rename してしのいでいる

`fuse_config.hard_remove` は **pgfs では設定していない** (既定 0)。
[FuseMount.Init](../../src/fuse/src/FuseMount.cs) が触っているのは `use_ino` だけである。
既定 0 の libfuse は、**開いたままのファイルの `unlink` を同一ディレクトリへの rename にすり替える**。

実測 (2 マウント・`pgfs_test`):

```
$ exec 9< f.txt ; rm f.txt
$ ls -a                      # A (unlink した側)
.  ..  .fuse_hidden0000000300000001
$ ls -a                      # B (別マウント)
.  ..  .fuse_hidden0000000300000001
$ cat <&9
KEEPME                       # ← 読める。POSIX の意味論は満たされている
$ exec 9<&- ; ls -a          # close 後は A/B とも消える
.  ..
```

### だから何が困るのか (実測した副作用 3 つ)

| # | 副作用 | 実測 |
|---|---|---|
| 1 | **他マウントから見える** | `.fuse_hidden0000000300000001` が B の `ls` に出る。**DB の実ファイル**なので当然である |
| 2 | **デーモンが死ぬと永久に残る** | `kill -9` の後、`.fuse_hidden0000000400000002` が**中身 `ORPHAN` ごと残存**した。掃除する主体はどこにも無い (libfuse の後始末は死んだプロセスのメモリの中) |
| 3 | **unlink ごとに rename + release で unlink** | 1 回の削除が 2 回の名前空間操作になる。write-back のときは pending の付け替えも挟む |

**2 が一番重い**。クラッシュのたびに**誰も消せないゴミが DB に増え続け、全クライアントに見え、
`df` の使用量にも乗る**。現状これを掃除する経路は `mkfs` にも `mount` にも `pgfsctl` にも無い
(`grep -r fuse_hidden` の結果は `fuse_config` の宣言 1 件のみ)。

**名前の衝突は起きなかった**: 2 マウントが同じディレクトリで同時に unlink-while-open しても
`...00000001` / `...00000002` と別名になった (notify off でも同じ)。**共有 DB のルックアップで
既存名を見つけて番号を進めている**と見えるが、**libfuse 側の機構は追っていない**。
`hard_remove` を立てればこの経路ごと無くなるので、深追いしていない。

### 順序の制約 — **`hard_remove = 1` を段階 C より先に立ててはいけない** (実測)

一時的に `hard_remove = 1` を立てて確かめた (プローブは撤去済み):

```
$ exec 9< f.txt ; rm f.txt
$ ls -a                      # 隠しファイルは作られない
.  ..
$ cat <&9
cat: -: No such file or directory     # ← 読めない
```

**いま立てると open-then-unlink が壊れる。** 段階 B で `Read` / `Write` が `InodeId` 起点に
なったので、inode 行が消えれば [ResolveHandle](../../src/core/src/Api/Api.Handle.cs) が
`StaleHandleException` を投げる (② の「段階 B ではエラーのまま」がそのとおり効いている)。
**`hard_remove = 1` は段階 C の生存管理とセットでしか立てられない。**

### Linux 側の設計

**Core への要求 (= Dokan 側に作ってもらうもの)**:

| 要求 | なぜ Linux 側では持てないか |
|---|---|
| **inode 単位の参照カウント** (open で ++、release で --) | 両 OS が同じ数え方をしないと、Dokan と FUSE で削除の時点がずれる |
| **`DeletePending` 印** (名前は消えたが実体は生きている状態) | `TryResolveHandle` が「消えた」と言う条件を変えるので `Api` の中の話である |
| **最終解放で実体を消す** (カウント 0 かつ `DeletePending`) | 削除は tx の話であり、監査行・`data_chunk`・ハードリンク兄弟の扱いが絡む |

**FUSE アダプタ側 (= こちらが持つもの)**:

1. `fuse_config.hard_remove = 1` を立てる (**Core が上の 3 つを満たしてから**)。
2. `Unlink` / `Rmdir` は「名前を消して `DeletePending` を立てる」に変える。
3. `Release` で参照カウントを落とす。**`Release` は最後の close でしか呼ばれない**ので、
   段階 A で確かめたとおり**ここが唯一の解放点**である (`Flush` は fd の複製ごとに呼ばれるので不可)。
4. 解決失敗の扱いを分ける。**`DeletePending` で生きているハンドルは成功**、
   **本当に消えているハンドルは `-ESTALE`**。いまは後者しかない。

**名前の見え方は OS 層が決める** (§未解決の論点 3 の分担どおり)。Linux は
「**unlink した瞬間に名前が消え、開いている fd だけが実体を見る**」= POSIX そのままでよい。
Windows の delete-pending (別ハンドル保持中は列挙に出るが open は失敗) とは**違ってよい**。

### 先に決めること (未決)

- **既存の `.fuse_hidden*` 残骸をどうするか。** 段階 C を入れても**過去に漏れたぶんは残る**。
  掃除を `mount` の起動時にやるか、`pgfsctl` にサブコマンドを足すか、手動の SQL を doc に書くだけにするか。
  **`hard_remove` を立てた後は誰も作らない**ので、一度きりの掃除で足りる。
  ⚠ **機械的に消してはいけない** — **いま別のマウントが開いている最中のもの**を消すと、
    そのマウントの fd が壊れる。`{prefix}mounts` の生存と突き合わせるか、**全マウントが落ちている
    ときにだけ実行する**という運用にするか、どちらかを決めること。
- **write-back 有効時の `DeletePending`。** pending の inode が `DeletePending` になったときに
  台帳 (`DirtySet` / `DirtyNamespace`) をどう畳むか。A-1 (ハードリンク兄弟への付け替え) と
  同じ場所を触るので、[metadata-write-back.md](metadata-write-back.md) 側と整合を取ること。

### 回帰テストの置き場

**段階 C に入る前に、いまの挙動を固定するテストを置くべきである** (いまは 1 件も無い)。
`hard_remove` を立てた瞬間に壊れるので、**壊れたことに気づく網**が先に要る。

- `tests/linux/e2e.sh`: **open したまま unlink して fd から読める**こと (単一マウントで足りる)。
  → **`test_open_unlink_read_still_works` として land 済み**。
  `hard_remove = 1` のビルドに当てて **`expected 'STILL_HERE', got ''` で落ちること**を確認した。
  **対になる「close 後に `.fuse_hidden*` が消えていること」はテストにしていない** —
  後始末が走るのは close ではなく**カーネルが inode を FORGET したとき**で、その時点はカーネルが決める
  (実測で 1 秒で消える回と 5 秒待っても残る回の両方があった)。**境界の無いものを待って assert すると、
  遅いだけの回を壊れていると数える。**
- `tests/linux/crossclient.sh`: **`.fuse_hidden*` が他マウントに見えないこと** —
  これは**段階 C を入れて初めて緑になる**ので、**段階 C と同じコミットで入れる**。
  いま入れると赤いテストを置くことになる。
