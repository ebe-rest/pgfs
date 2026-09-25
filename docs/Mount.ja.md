# mount.pgfs 仕様

> **道順**: [docs/README.ja.md](README.ja.md) › **本書**
>
> **この doc が正である範囲**: **`mount.pgfs` (Linux/macOS) の仕様** — CLI とマウントオプション
> (`-o` の分類)、実装している FUSE 操作の一覧、**write-back を有効にしたときの耐久性契約**
> (何が失われ得るか)、停止シグナルの扱い。**利用者から見た挙動はここが正**。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [Mkfs.ja.md](Mkfs.ja.md) | 設定項目の**既定値**。本書の表は抜粋 |
> | [design/write-back.ja.md](design/write-back.ja.md) / [design/metadata-write-back.ja.md](design/metadata-write-back.ja.md) | write-back の**設計と実装ステータス**。本書は契約だけ |
> | [design/fuse-binding.ja.md](design/fuse-binding.ja.md) | FUSE バインディングの内部 |
> | [design/fstab-support.ja.md](design/fstab-support.ja.md) | `/etc/fstab` / `mount(8)` 経由の起動 |
> | [design/handle-context.ja.md](design/handle-context.ja.md) | ハンドル文脈の共通化 (`fh` の意味づけ) |
> | [Assign.ja.md](Assign.ja.md) | Windows (Dokan) 側の同じ層 |

PGFS ファイルシステムを **FUSE 経由でマウント** する Linux / macOS 用ツール `mount.pgfs` の仕様です。

このドキュメントは現行実装 ([src/mount/](../src/mount/)) で確定した仕様をまとめたもので、Windows 用の [Pgfs.Assign](../src/assign/) (DokanNet 版) とは別の実行ファイルです。共通部分はすべて [`Pgfs.Core.Api.Api`](../src/core/src/Api/Api.cs) に集約されています。

## 役割

PGFS が初期化された PostgreSQL データベース ([docs/Mkfs.ja.md](Mkfs.ja.md) で構築) を、Linux/macOS のディレクトリツリーとしてユーザー空間に見せます。

```
PostgreSQL (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings)
        ↑↓ Npgsql + Dapper
    Pgfs.Core.Api.Api（クロスプラットフォーム）
        ↑↓
    Pgfs.Fuse.FileSystem : Pgfs.Fuse.FuseFileSystemBase（Linux/macOS 固有）
        ↑↓ FUSE
    Linux カーネル / macOS macFUSE
        ↑↓
    cd /mnt/pgfs && ls
```

## ビルドと実行

```bash
# ビルド
dotnet build src/mount/Mount.csproj

# 起動（既定: localhost:5432 に接続して /mnt/pgfs にマウント）
sudo dotnet run --project src/mount

# 別のマウントポイント
sudo dotnet run --project src/mount -- -m /mnt/myfs

# 接続文字列を明示
sudo dotnet run --project src/mount -- \
    -c "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs" \
    -m /mnt/pgfs

# ヘルプ / 版
dotnet run --project src/mount -- --help
dotnet run --project src/mount -- --version
```

> **アンマウント**: `fusermount3 -u /mnt/pgfs` (Linux) または Ctrl+C (LazyUnmount)。

### プロセスがクラッシュしたあとのアンマウント

mount.pgfs のプロセスがクラッシュしたり SIGKILL で落ちると、**カーネル側のマウントエントリだけが残った状態**になります。`mount` コマンドで確認すると以下のように見え、そのままでは再マウントできません:

```bash
$ mount | grep pgfs
/dev/fuse on /mnt/pgfs type fuse (rw,nosuid,nodev,relatime,user_id=1000,group_id=1000)

$ ls /mnt/pgfs
ls: cannot access '/mnt/pgfs': Transport endpoint is not connected
```

このときは **手動でアンマウント**してください:

```bash
# 通常はこれで OK（普段のアンマウントと同じ）
fusermount3 -u /mnt/pgfs

# パーミッションエラーが出る場合は強制
fusermount3 -uz /mnt/pgfs    # -z = lazy (busy でも次の利用で外す)
# または
sudo umount /mnt/pgfs
sudo umount -l /mnt/pgfs     # lazy
```

`mount` コマンドで `/dev/fuse on .../mnt/pgfs` が消えたら復旧完了です。その後通常通り `mount.pgfs -m ...` で再マウントできます。

### 前提

- **.NET 10 SDK**
- **libfuse3** / **fusermount3** がインストールされていること
  - Ubuntu/Debian: `sudo apt install fuse3 libfuse3-3`
  - Arch: `sudo pacman -S fuse3`
  - 起動時に `Fuse.CheckDependencies()` で確認し、不足していれば終了する
- マウントポイントが事前に作成されていること
  - `sudo mkdir -p /mnt/pgfs && sudo chown $USER /mnt/pgfs`
- [docs/Mkfs.ja.md](Mkfs.ja.md) で DB 側の初期化が済んでいること

### Windows 上での挙動

ビルドだけは Windows でも通します（クロスコンパイル目的）。実行すると先頭で `OperatingSystem.IsWindows()` に引っかかって `assign.pgfs` (DokanNet 版) に誘導するエラーを出して終了します。Windows でマウントする場合は [src/assign/](../src/assign/) を使ってください。

## 設定

設定モデルは [`Pgfs.Core.Config.RootConfig`](../src/core/src/Config/RootConfig.cs) を共有しています。Mkfs と同じ TOML 設定ファイル ([pgfs.toml.example](../pgfs.toml.example)) と同じコマンドラインオプションが使えます。詳細は [docs/Mkfs.ja.md](Mkfs.ja.md) を参照。

mount.pgfs が特に使うのは:

| 設定 | 引数オプション | 既定 |
|---|---|---|
| `database.connection` | `-c`, `--connection` | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` |
| `mount.mount_point` | `-m`, `--mount-point` | Linux/macOS: `/mnt/pgfs` |
| `mount.cache_max_entries` | `--cache-max-entries` | `1024` |
| `mount.max_write` | `--max-write` | `0` = libfuse のネゴシエーション任せ (実測では既定でカーネル上限 1 MiB まで上がる)。FUSE の 1 WRITE 要求の最大バイト数。write-back 時の flush tx 粒度とは別。下げたい / libfuse の版差で固定したいときに使う (`-o max_write` も受け付けるが、libfuse3 はこれをマウントオプションとして拒否するので **pgfs がこの Field に流して init コールバックで設定する**) |
| `logging.level` | `--log-level` | `information` |
| `database.notify_enabled` | `--notify` / `--no-notify` | **`true`** (v0.2.1〜。v0.2.0 までは `false`)。1 マウントだけの運用で通知のコストを省きたいときは `--no-notify`。詳細は下記「他クライアント変更通知」 |

`pgfs.toml` は Mkfs と同じ TOML を使えます。

## write-back (`mount.write_back` / `mount.write_back_metadata`)

> 現行コードとの照合: 2026-09-19 (ラウンド A 修正後)。かつてここに挙げていた 4 点 (ハードリンク経由の dirty 喪失 / 同期 close 印 / 失敗検出 / 待機期限) は **ラウンド A で修正済**で、dev サーバ実機で検証した ([§ラウンド A の修正](design/metadata-write-back-reviews.ja.md))。**ラウンド B (B-1〜B-13) も対応済**である。以下は実装の動作と意図を説明するものであり、全シナリオの耐久性を保証しない。残っている制限は [CHANGELOG.ja.md §既知の制限](../CHANGELOG.ja.md) を参照。

書き込みをメモリに溜めて「1 ファイル = 1 トランザクション」で書く高速化オプション。**どちらも既定 off** で、
off のままなら従来どおりの write-through (各 write のトランザクションを同期 commit) です。設計の正は
[write-back.ja.md](design/write-back.ja.md) / [metadata-write-back.ja.md](design/metadata-write-back.ja.md)、全項目は
[settings-matrix.ja.md](design/settings-matrix.ja.md)。

| 設定 | 引数オプション | 既定 | 意味 |
|---|---|---|---|
| `mount.write_back` | `--write-back` | `false` | データ (チャンク) を write-back する。`fsync` / `close` / 時間 / dirty 上限 / unmount で flush |
| `mount.write_back_max_bytes` | `--write-back-max-bytes` | `64 MiB` | dirty バイトの flush 開始閾値。失敗時にも上限以下を保証するものではない |
| `mount.write_back_interval_ms` | `--write-back-interval-ms` | `1000` | 背景 flush の間隔。`0` で時間トリガ無効 |
| `mount.write_back_metadata` | `--write-back-metadata` | `false` | **メタデータも** write-back する (`write_back` が前提)。**`close` の耐久性契約が変わる** — 下記 |
| `mount.write_back_metadata_exclusive_create` | `--write-back-metadata-exclusive-create` | `write_through` | `O_EXCL` / `CREATE_NEW` 付き create の扱い。`defer` にすると `rsync` / `cp` が **3.28×** 速くなる代わりに **cross-client の排他を失う** — 下記 |
| `mount.write_back_max_inodes` | `--write-back-max-inodes` | `4096` | pending inode 数の flush 開始閾値。期限切れ時は警告して超過のまま続行する |
| `mount.write_back_flush_timeout_ms` | `--write-back-flush-timeout-ms` | `30000` | back-pressure / unmount の再試行期限。**期限は sweep の 1 巡の中でも見る**が、実行中の DB 呼出しは打ち切れないので厳密な上限ではない (`0` = 1 巡だけ試行) |

**bool フラグは値を取りません**。`--write-back true` と書くと `true` が positional 引数に落ちるので、
**裸のフラグ**で渡してください (`--write-back --write-back-metadata`)。

### `mount.write_back` を on にしたまま同じファイルを複数マウントから書かないこと (契約)

**`mount.write_back = true` のマウントが未 flush のデータを抱えているあいだに、他のマウントが同じ実体を
書くと、その書き込みは flush で消えます。** dirty バッファが**チャンクの完全な像**として持たれており、
flush がその像を丸ごと書き戻すためです。

- **消える範囲はバイト単位ではなくチャンク単位**で、**「write-back 側が読んだことのあるチャンク」全体**が対象です
  (既定のチャンクは 1 MiB)。**書いていない領域まで巻き添えになります。**
- **`--notify` を付けていても防げません。** 通知は内容キャッシュを落としますが、**dirty は意図的に残す**ためです。
- **`st_size` の巻き戻りは塞いであります**。以前は他マウントの `truncate` を flush が
  巻き戻し、消えたはずのバイトが読めました。**サイズは正しくなりましたが、バイトの踏み潰しは残っています。**

**同じファイルを複数マウントから書く構成では `write_back` を off (既定) にしてください。**
単一マウントからの読み書き、および**マウントごとに書くファイルが分かれている**構成は影響を受けません。
仕組みと実測は [design/write-back.ja.md §cross-client の契約](design/write-back.ja.md) が正です。

### `mount.write_back_metadata` を on にすると何を失うか (契約)

`write_back_metadata = true` は「このマウントが作った inode (create / mkdir / symlink) を DB に書かず
メモリの台帳に持ち、`create → write → close → chmod → utimens → rename` を丸ごと 1 tx に畳む」動作です。
**`close(2)` は同期 flush をやめ、`fsync(2)` / `fsyncdir(2)` だけが硬いバリアになります**。
具体的に失うものは次の 5 点で、**これを許容できないなら off のまま**にしてください
(既定 off の理由もこれです)。過去の ≈2.1× は投影値であり、ステージ 2 の実測記録は
`rsync` 1.00× / 非 `O_EXCL` create の総合 1.30× です (**いずれも 2026-08-12・A-10 の修正より前の値**。
persisted への上書きを同期 close に戻した後は未実測)。詳細は [performance.ja.md](design/performance.ja.md)。

| 項目 | 失うもの |
|---|---|
| **close の耐久性** | **このマウントが新規作成したファイル (pending-born) に限り**、`close` 済みでも flush 前にクラッシュすると (背景試行間隔は既定 1000ms だが、障害時の喪失窓に時間上限はない) **ファイルごと消える** (中身が空になるのではなく、そのファイルが存在しなかったことになる)。`fsync` / `fsyncdir` した分だけが残る。**既存ファイルの上書きは `close` で書き切る** (下記) |
| **ファイル間の因果** | 削除や rename (write-through) が pending の作成を**追い越す**ため、複数ファイルを触る操作 (`git checkout` / `rsync --delete` 等) がクラッシュすると「旧も新も無い」= どの直列履歴にも現れない状態があり得る。**pending inode の実体化は 1 tx** (最終名・属性・その flush が取り込んだデータ・監査)。`rm f; cp new f` では旧を削除した後に新を失う窓もある |
| **他クライアントからの可視性** | pending inode は flush まで**他クライアントに見えない** (背景 flush の遅延・DB 障害・interval=0 では可視化の時間上限を保証しない)。`mkdir` をロックプリミティブとして使うツールは 2 クライアントが同時に成功し得る。**`O_EXCL` 付き create は既定 (`write_through`) では同期作成**なので排他は維持される (`git index.lock` 等)。**`mount.write_back_metadata_exclusive_create = defer` にするとこの保証も失われる** (同一マウント内の排他だけが残る) — 下記。同名 create が cross-client で競合したときは last-flush-wins (衝突は監査 + 統計に記録) |
| **`du` / `df` / `status`** | pending 分は flush まで DB 側の集計に出ない (`df` / `pgfsctl status` の used)。自クライアントの `stat` / `du` はメモリ上の値も使う |
| **既存テストとの関係** | [tests/linux/writeback.sh](../tests/linux/writeback.sh) の「close 後にプロセスを kill しても残る」テストは **`write_back_metadata` off の契約**。on では「fsync 済みは残る / close だけは消え得る」が正しい挙動 |

**単一ファイルの原子性が上がるのは pending-born (このマウントが作ったファイル) だけ**です。
既に DB にある実体 (persisted) への上書きは複数の flush tx に分かれ得るため、クラッシュで
「前半が新・後半が旧」のキメラになり得ます。そのため **persisted inode への write が始まった時点で
同期 close 印を付け、その `close` は同期 flush に格上げ**します。
= `write_back_metadata = on` で `close` の耐久性を失うのは **新規作成したファイル**であり、
既存ファイルの上書きは off のときと同じく `close` で書き切ります。

**契約が緩まない例外 (同期化ヒューリスティック)** — 「旧を即消して新を遅らせる」形の事故
を避けるため、次の処理を実装しています:

1. **rename-over-existing** (`mv new existing` / エディタ保存 / `sed -i` / dpkg): 「置換対象の削除 + 新ファイルの
   実体化 + データ + 監査」を**単一 tx で同期実行**する。
2. **`O_TRUNC` / `truncate(2)` で旧チャンクを捨てたファイル**: **inode と data 本体の両方**に同期 close 印を
   付け、その close を同期 flush に格上げします。印が落ちるのは **実際に未 flush を書き切ったときだけ**なので、
   `truncate -s 0 f; cmd >> f` のように truncate と追記が別 fd でも、ハードリンクの兄弟経由でも保護されます。
   印が上限 (4096) に達したときは印を捨てず、**全 close を同期に格上げ**して縮退します。
3. **`O_EXCL` create**: ロックプリミティブなので遅延させず同期作成し、排他判定を DB の一意制約に委ねる。
4. **persisted inode への上書き** (追加): 既に DB にある実体へ write が始まった時点で同期 close 印を
   付け、その `close` を同期 flush に格上げする (= close-no-flush は新規作成したファイル限定)。

### `mount.write_back_metadata_exclusive_create` (追加)

`O_EXCL` / `CREATE_NEW` 付き create を pending にするかどうかのノブ。**既定 `write_through` は上の 3 の挙動**
(同期作成) で、変える必要はありません。

| 値 | 速度 | 排他 |
|---|---|---|
| **`write_through` (既定)** | `rsync` / `cp` に 1e の効果は出ない (実測 1.00×) | cross-client の `O_EXCL` 排他が**効く** |
| `defer` | `rsync` が **3.28×** (37.1 → 11.3 ms/file。[performance.ja.md](design/performance.ja.md)) | cross-client の排他を**失う**。同一マウント内の排他だけが残る |

`rsync` も `cp` も新規宛先を `O_CREAT|O_EXCL` で開くため、既定のままでは代表的な bulk copy に
1e の畳み込みが 1 回も効きません。`defer` はそれを効かせる代わりに、**2 クライアントの排他作成が
両方成功する**状態を受け入れる選択です。

- **複数クライアントで同じ FS を使うなら `defer` にしないでください**。ロックファイルを使うツール
  (`git index.lock` 等) が 2 台で同時に「自分が取った」と判断します。
- **同一マウント内の排他は維持されます** (pending 台帳が `(親, 名前)` の衝突を `EEXIST` で弾く)。
- **衝突したときは黙って上書きしません**。先に DB へ届いた側 (勝者) は無傷のまま、後から flush する側
  (敗者) は flush が失敗し続け、`fsync` が `-EIO` を返し `pgfsctl status` に赤が出ます。
  **回復は敗者側でそのファイルを削除する**ことです (pending の取り消しなので DB には触りません)。
  削除するとエラーステートも解除され、後続の書き込み / 作成が通るようになります。
- `defer` で起動したときに他の稼働マウントが `{prefix}mounts` に居ると **警告**を出します (拒否はしません)。

### エラーの報告経路 (close が -EIO を返せなくなるぶん)

1. **flush 失敗を latch したファイルの `close` は同期 flush を再試行**し、再試行も失敗した場合に `-EIO` を返す (クリーンなファイルの
   close だけがマークのみ)。
2. **unmount は `mount.write_back_flush_timeout_ms` を期限に flush を再試行**する。期限内に書き切れなければ
   **何が失われるか (パス / id / サイズ / エラー) を `Error` ログに列挙**し (先頭 32 件。残りは
   「他 N 件」ではなく **合計・dir/file の別・論理バイト数**まで出す)、`mount.pgfs` は **exit code 4**
   で終了する設計である。**ただし exit 4 は呼び出し元には届かない** — デーモン化した親はマウント成立時点で
   `0` を返して先に終了しているため。代わりに **喪失を DB 側に残す**: `{prefix}mounts` の行を
   DELETE せず `stats` に `unflushedLoss` / `endedAt` (**UTC・末尾 `Z`**。ログ行の時刻はローカルなので、印が無いと時差ぶんずれた別の実行に見える) を載せて墓標として残し、`audit.enabled` なら
   `op = writeback_loss` の監査行も書く。**次回マウント時に親プロセスの stderr で警告**し、
   `pgfsctl status` にも赤で出る。墓標は**自動では消えない**ので、確認したら
   `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0` で消すこと。**期限は sweep の 1 巡の中でも見る**が、実行中の 1 トランザクションは打ち切れないので
   指定時間を多少超過し得る。喪失ログは pending inode / dirty データそれぞれ最大 32 件と監査行件数で、全件一覧ではない。デーモン起動の親の終了コードには終了時の 4 は伝わらない。`fusermount3 -u` / `umount(8)` はカーネル側で完了するので FS 側から EBUSY で拒否はできない
   (拒否すると「絶対に unmount できないマウント」になる) — ログと exit code が最後の報告経路。
3. **flush が 5 回連続で失敗すると mount がエラーステート**になり、**新規の write / create に加えて
   persisted な実体を壊す操作 (`unlink` / `rmdir` / `truncate` / rename の置換) も `-EIO` で拒否**する。
   **このマウントの未 flush を捨てるだけの操作 (pending の削除・truncate) は通る** ので、
   衝突した pending を削除して回復する経路は塞がりません。**`rm -rf` は pending と persisted が混在する
   ディレクトリで部分的に失敗します** (持っているものを黙って消さないほうを優先しています)
   (成功済みだが永遠に flush できないデータを積み増さないため)。`pgfsctl status` の Layer 3 に
   `!! write-back ERROR STATE` を赤で表示。**連続失敗のカウンタは flush 対象ごと**なので、他ファイルの成功が
   恒久失敗を隠すことはない (修正)。解除は**閾値に達した対象が 1 つも無くなったとき**。
   エラーステートそのものは mount 全体の状態である。
4. **back-pressure はブロッキング**。dirty バイト / pending inode が上限を超えたら、上限を下回るか
   `mount.write_back_flush_timeout_ms` を使い切るまで write / create を待たせる。**期限は sweep の 1 巡の中でも
   見る** (修正) が、期限切れには警告して上限超過のまま続行するので、厳密な時間・メモリ上限ではない。

### 停止シグナルの段階

unmount 時の flush は既定で最大 `mount.write_back_flush_timeout_ms` (30000 ms) 粘るため、その間
`mount.pgfs` は「止まらないプロセス」に見えます。**停止シグナル (SIGTERM / SIGINT) は回数で段階が上がります**
(SIGTERM と SIGINT は同じカウンタを共有)。

| 回数 | 動作 |
|---|---|
| 1 発目 | graceful unmount。未 flush は期限まで書き切ろうとする |
| 2 発目 | **flush の粘りを打ち切る**。次の期限判定で諦め、**喪失レポートを出してから** exit 4 で終了する (進行中の 1 トランザクションは打ち切らないので多少は待つ) |
| 3 発目以降 | 既定動作 (即時終了) に戻る。**喪失レポートも `{prefix}mounts` の墓標も残りません** |

> **systemd で止める場合**: `TimeoutStopSec` は **`mount.write_back_flush_timeout_ms` より長く**してください。
> 短いと SIGTERM の直後に SIGKILL が来るので、この段階分けも喪失レポートも全部飛びます
> (例: `write_back_flush_timeout_ms = 30000` なら `TimeoutStopSec=60s`)。

## 実装している FUSE 操作

[src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) は [`Pgfs.Fuse.FuseFileSystemBase`](../src/fuse/src/FuseFileSystemBase.cs) を継承し、以下を override しています。

| 操作 | 対応 | 備考 |
|---|---|---|
| `GetAttr` | ✅ | inode → `stat` 構造体マッピング |
| `OpenDir` | ✅ | 存在・種別確認と `fi.fh` への inode id 保存 |
| `ReadDir` | ✅ | `.` / `..` + 子 inode 列挙 |
| `ReleaseDir` | ✅ | no-op |
| `MkDir` | ✅ | `Api.CreateDirectory` |
| `RmDir` | ✅ | 空チェック後 `Api.DeleteInode` |
| `Create` | ✅ | `Api.CreateFile` （空ファイル） |
| `Unlink` | ✅ | `Api.DeleteInode`（最後のデータ参照なら bytea チャンクと data 行も解放） |
| `Rename` | ⚠️ | `Api.Rename`（置換は同一 tx）。`RENAME_NOREPLACE` 以外のフラグは `-EINVAL` で拒む。**`RENAME_EXCHANGE` による交換そのものは未実装** |
| `ChMod` | ✅ | 種別ビットを保持して `Api.UpdateMode` |
| `Chown` | ✅ | `UserResolver` で uid/gid → uname/gname 解決後 `Api.UpdateOwner` |
| `Truncate` | ✅ | `Api.TruncateData`（bytea チャンクもまとめて切り詰め） |
| `UpdateTimestamps` | ✅ | `mtime` のみ DB 反映、`atime` は無視（要件） |
| `Open` | ✅ | 存在・種別確認、必要時 `O_TRUNC`、`fi.fh` に inode id 保存 |
| `Flush` | ✅ | close 時に `Api.CloseInode`。メタデータ write-back 有効時は原則非同期 |
| `FSync` | ✅ | `Api.FlushInode`。失敗を `-EIO` で返す |
| `FSyncDir` | ✅ | `Api.FlushDirectory`。自身の pending 祖先と直下の pending 子を実体化する |
| `Release` | ✅ | `CloseInode` による best-effort 処理。エラーをアプリへ返す経路ではない |
| `Read` | ✅ | `Api.ReadData`（bytea チャンク経由、穴は 0 埋め） |
| `Write` | ✅ | `Api.WriteData`（bytea チャンク経由、初回時は data 行を自動作成） |
| `StatFS` | ✅ | `Api.GetStatFs` 経由。mkfs `--statfs` で `{prefix}statfs()` (plperlu) を作っていればテーブルスペースの**実ディスク空き**、無ければ公称容量 (`max_file_size` − `pg_database_size`)。詳細 [docs/df-support.ja.md](design/df-support.ja.md) |
| `GetXAttr` | ✅ | `xattr_names`/`xattr_values` 並行配列から取得 (キャッシュは in-memory 探索、DB は `xattr_values[array_position(xattr_names,@name)]`)。値は bytea 透過。`system.posix_acl_access` は特別扱い (下記 ACL) |
| `SetXAttr` | ✅ | 単一 UPDATE で既存 index 差し替え or 末尾追記 (`array_position`+スライス、原子)、`XATTR_CREATE` / `XATTR_REPLACE` フラグ尊重。`system.posix_acl_access` は特別扱い |
| `ListXAttr` | ✅ | `xattr_names` をそのまま列挙、NUL 終端形式で返す |
| `RemoveXAttr` | ✅ | 単一 UPDATE で name の index を両配列から除去 (スライス連結、原子)。`system.posix_acl_access` は named を空に (setfacl -b 相当) |
| POSIX ACL (`system.posix_acl_access`) | ✅ | setfacl/getfacl と往復。`st_mode` 基本3クラス + 正準 ACL (`user.pgfs_acl` の named) ⇄ ACL バイナリ。mask 自動算出、named 無しは ENODATA。Windows DACL と同じ正準ストアを共有 (下記 ACL / [permission-interop.ja.md](design/permission-interop.ja.md)) |
| `SymLink` | ✅ | `Api.CreateSymlink`（`S_IFLNK | 0777`, `link_target` 列に格納） |
| `ReadLink` | ✅ | `inode.LinkTarget` を NUL 終端で返す |
| `Link` | ⚠️ | `Api.CreateHardLink`。既存 data_id の共有は実装されている。**空ファイルの null data_id・dirty 喪失・`truncate` / `O_TRUNC` による共有の分裂は 修正済** ([data-id-lifecycle.ja.md](design/data-id-lifecycle.ja.md))。**残るのは属性 (mode / 所有者) を兄弟間で共有しないこと**だけで、`Api.UpdateMode` は自分の inode 行 1 つしか更新しない ([CHANGELOG.ja.md](../CHANGELOG.ja.md) §既知の制限) |

凡例: ✅ 対応処理あり、⚠️ 制限・不具合あり、❌ 未実装 (`-ENOSYS`)。実機検証済みを意味する分類ではない。

この表は主要操作の実装一覧であり、FUSE 全操作の対応表ではありません。`Access` / `FAllocate` は基底の `-ENOSYS` のままです。分散バイト範囲ロック等も未実装であり、DB 更新用の `pgfs_lock` とは別機能です。

## 他クライアント変更通知 (Notify)

`database.notify_enabled=true` (v0.2.1 から既定) のとき、PostgreSQL `LISTEN` / `NOTIFY` 経由で他クライアントの書き込みを受信し、ローカル `InodeCache` を invalidate する ([src/core/src/Api/NotifyChannel.cs](../src/core/src/Api/NotifyChannel.cs))。複数の `mount.pgfs` / `assign.pgfs` から同じ PG/pgfs を共有マウントしているときに、書き込みクライアントの変更が他クライアントの `stat` / `ls` に反映される。

**Linux 側の制約**: 現行 `Pgfs.Fuse` は OS 通知ブリッジを登録していません。受信時に
`InodeCache` と `ContentCache` を無効化しますが、カーネルのページ／dentry キャッシュまで
能動的に無効化する実装はありません。

- `attr_timeout=0` はカーネル属性キャッシュの設定です。Core のキャッシュを毎回 DB 再取得する指定ではありません。
- 他クライアントからの変更は、データ通知を送受信する双方で `database.notify_enabled=true` が必要です。
- **送出キューが溢れたときは「キャッシュを全部捨てろ」を 1 件送ります** (追加)。送出キュー
  (1024 件) が満杯になると通知は捨てられ、**その通知は二度と届きません**。`InodeCache` の positive
  エントリには **TTL が無い**ので、落とさない限り**受け手が永久に古い値を返し続けます**。捨てた瞬間に
  「どの id か分からないので全部捨てろ」という 1 件へ畳み、**送出ワーカが次に動いたときに必ず送ります**
  (停止時も積み残しぶんを送ってから終わります)。受け手は `InodeCache` と `ContentCache` の
  **clean なエントリだけ**を捨てます — **未 flush の dirty と、メタデータ write-back の pending inode
  (DB に行が無い) は残します**。捨てる側は安全なので、多めに撃っても壊れません。
- **通知の切断からの再同期は未検証です** (上の再同期は「送り手が捨てたことに気づいた」場合の話で、
  **受け手が LISTEN を落としていた場合は誰も気づけません**)。
- **キャッシュの復帰と画面の復帰は別です。** 全破棄を受けると `InodeCache` / `ContentCache` は
  捨てられるので、**次に開けば正しい値になります**。しかし **OS 側へ「これが変わった」と伝える経路
  (`Api.OsBridge`) には、全破棄のとき渡せるものがありません** — 「どの id が変わったか分からない」
  というのが全破棄の意味だからです。Linux (Mount) は OS 通知ブリッジを登録していないので、
  この経路自体がありません。
  **なお Windows (Assign) でも、渡せたとしても画面は直りません** — **他マウント由来の変更は
  FileSystemWatcher / エクスプローラのイベントになりません** (2026-09-21 に実機で測定。ローカル操作では
  イベントが出るのに、リモート由来は 1 件も出ない)。**「通知で画面が直る」経路がそもそも無い**ので、
  全破棄に限らず**開いたままのウィンドウは古い表示のまま**です。正は [Assign.ja.md](Assign.ja.md)。
- リモート変更を inotify へ通知する処理はありません。ローカル VFS 操作による通知まで「FUSE では発火しない」と一括りにはできません。キャッシュ無効化 API の追加だけでリモート inotify が保証されるわけでもありません。参照: [libfuse の fsnotify 設計メモ](https://github.com/libfuse/libfuse/wiki/Fsnotify-and-FUSE)。pgfs 実機での通知種別ごとの動作は未検証です。
- 制御用 LISTEN (`set` / `reload` / `ping`) は `notify_enabled` に関係なく起動します。データ変更通知の opt-in とは別です。

詳細仕様 / ペイロード / 受信処理は [history.ja.md](history.ja.md) 「他クライアント変更通知 (Notify)」を参照。

## アーキテクチャ

### レイヤ

```
┌─────────────────────────────────────────────────────────┐
│ Pgfs.Mount.Program           ── 引数解析・OS 判定・マウント │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Fuse.FileSystem        ── FUSE コールバック (Linux) │
│   - FillStat / ResolveOwner    ※Linux 固有              │
│   - CurrentUserNames           ※Windows でも応用可能     │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.Api             ── DB 操作 (クロスプラットフォーム) │
│   - GetByPath / ListChildren / CreateDirectory / ...    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Api.InodeCache      ── inode メモリキャッシュ    │
├─────────────────────────────────────────────────────────┤
│ Pgfs.Core.Utility.Pg          ── Npgsql + Dapper ラッパ   │
└─────────────────────────────────────────────────────────┘
```

### Windows でも共有可能な箇所

FileSystem.cs 内のロジックには `// Windows-shareable:` というコメントで Windows (DokanNet) 側 [`Pgfs.Assign`](../src/assign/) でも転用可能な部分を明示しています。

- **パス分解** (`SplitParent`): 区切り文字を引数化すれば OS 非依存。Mount は `/` 固定。
- **新規 inode の uname/gname 決定** (`CurrentUserNames`): 呼び出し元を所有者にする方針は共通化可能。FUSE では `fuse_get_context`、Dokan では要求元トークンが対応候補です。
- **バイト列パス → string 変換** (`PathToString`): UTF-8 デコード自体は OS 非依存。

逆に **Linux 固有** なのは:

- `Tmds.Linux.stat` / `statvfs` / `mode_t` / `uid_t` / `gid_t` / `timespec` を扱う部分（`FillStat`, `StatFS`）
- `getuid()` / `getgid()` の呼び出し（`Tmds.Linux.LibC` 経由）
- `RENAME_NOREPLACE` などの Linux 固有フラグ

これらは Windows 側では NTSTATUS / FileAttributes / DOKAN_FILE_INFO 等に置き換えになります。

## 設計判断・差分・暫定実装

### スレッディング

`SupportsMultiThreading => true` を返し、Pgfs.Fuse の binding はマルチスレッドで FUSE コールバックを呼び出します。`InodeCache` は `lock(this)` で同期、`Pg.cs` は `NpgsqlDataSource` の接続プールに任せます。`Api` の更新は複数 SQL を含む tx と write-back 台帳で処理されます。コールバック全体が単一 SQL で原子的という意味ではありません。

ただし「複数操作の組み合わせ」(`MkDir` の親存在チェック → CreateDirectory) は **非原子**です。同時に同じパスへ `MkDir` が来ると DB の `UNIQUE(parent_id, name)` で 1 つだけ成功し、もう一方は `Api.CreateDirectory` が null を返すので `-EEXIST` が返ります。これは妥当な挙動。

### 認証情報の扱い

- **新規 inode の uname/gname**: `Fuse.TryGetCallerContext` の uid/gid を名前に解決します。取得できない場合にプロセス由来の既定値へフォールバックします (`src/fuse/src/FileSystem.cs:116`)。
- **uid/gid 解決**: [`UserResolver`](../src/fuse/src/UserResolver.cs) は正引き・逆引きの結果を `ConcurrentDictionary` にキャッシュします。キャッシュミス時の libc 呼び出しは **`getpwnam_r` / `getpwuid_r` / `getgrnam_r` / `getgrgid_r` の 4 経路すべてが `_r` 版**です (非再入版から切り替え)。
- **`Chown`**: 渡された uid/gid を `UnameOf`/`GnameOf` で名前に解決し、`Api.UpdateOwner` で DB を書き換える。`uid == 0xFFFFFFFF (-1)` の慣習 (=変更しない) を尊重。

### データ I/O (Read / Write)

PGFS のデータ本体は `pgfs_data` + `pgfs_data_chunk` (1 行 = 1 bytea) に分割される設計で、Mount 側からの読み書きは **実装済み**:

- `Read` は `Api.ReadData` が担当します。read キャッシュ有効時または write-back 有効時はフルチャンクを読み、メモリで切り出します。両方無効時は `substring(payload from N for M)` による部分読みです。穴は 0 埋めします。
- `Write` は `Api.WriteData` が担当し、write-back 無効時にはチャンク行を必要に応じて自動作成、書き込み後に `st_size` を伸ばす。1 SQL/チャンクの upsert (`INSERT ... ON CONFLICT DO UPDATE SET payload = CASE ... END`) で完結、並行 write race は PG の行ロックで自動直列化。
- `Truncate` (および `Open(O_TRUNC)`) は `Api.TruncateData` が末尾チャンクの payload を `substring` で切り詰め (or 0 パディングで拡張)、不要チャンクは DELETE。
- `Create` は空ファイルの inode を作成。最初の `Write` 時にチャンク行が作られる。

> bytea 化の経緯は [docs/support_for_citus.ja.md](design/support_for_citus.ja.md) (Citus Phase 1, 完了)。旧設計の Large Object (`pg_largeobject`) は Citus で分散できないため移行した。

#### `.fuse_hidden*` (開いているファイルを消したとき) の見え方

**開いているファイルを `unlink` すると、libfuse はそれを「隠し名への rename」にすり替えます**
(`.fuse_hidden` + 16 桁の 16 進)。POSIX の「名前は消えるが fd は生きている」を成立させるためで、
**pgfs では隠し名が DB の実ファイルになります**。

- **列挙 (`ls` / `readdir`) からは外してあります** — 外さないと**他マウント (Windows を含む) の `ls` に出ます**。
  判定は **libfuse の書式に厳密**なので、利用者が作った `.fuse_hidden...` という名前は隠れません。
- **パスで直接指せば見えます** (`stat .fuse_hiddenXXXX` は通る)。**その fd を生かしている当のマウントが
  自分の隠しファイルを引く**ために必要なためです。
- **デーモンを `kill -9` すると中身ごと残ります** (後始末は死んだプロセスのメモリの中にあるため)。
  掃除は **[`pgfsctl prune`](Pgfsctl.ja.md)**。
- **列挙に出ないのに `rmdir` が `ENOTEMPTY` になる**ことがあります (隠しファイルが残っている親)。
  **見た目は空なのに消せない**ときは `prune` を撃ってください。

> **`hard_remove = 1` (= pgfs 自身で POSIX 意味論を実現する) は採らない**と決めています。理由と、
> どこまで試してどう詰んだかは
> [handle-context.ja.md §なぜ `hard_remove` を立てないか](design/handle-context.ja.md) に実測つきで残してあります。

#### append (`O_APPEND`) の契約

**追記は「いまの末尾」に着弾し、他マウントと並行していてもバイトは失われません** (write-through のとき)。

pgfs は **OS が決めたオフセットを使いません**。使うと**相手のバイトを上書きして消します**:

- **Linux**: カーネルが自分の `i_size` から決めて降ろしてくる。他マウントの追記を知らないので古い
  (実測: B が 6 バイト伸ばした後の A の追記が `off=5` で降り、B の 6 バイトが消えた)。
- **Windows**: Dokan は `WriteToEndOfFile` と言うだけなので pgfs が決める。ハンドルが握った古い
  `Inode.Size` を使っていた頃は同じ損失が出た (11 → 6 バイト)。

**末尾は書き込みトランザクションの中で確定させます** (`Api.AppendData` → `WriteDataThrough(appendAtEnd)`)。
`LockData` を取った時点で**同じ実体への書き手は直列化されている**ので、そこで読む `st_size` は
**直前のコミットまで含んだ本当の末尾**です。**行ロックは足していません** — plain SELECT なので
デッドロックの辺を作らず、Citus の shard 接触順 (inode は tx 末尾で 1 回) の規約も壊しません。
**Citus rf=2 の実機で、待ち明けに相手のコミット分が見えることを確認済み**です。

**保証の範囲**:

| | 保証 |
|---|---|
| **write-through (`mount.write_back` = off)** | **バイトは失われない**。他マウントと並行でも、`max_write` を超えて分割されても (各コールバックが tx の中で末尾を取り直すため) |
| **write-back 有効時** | **保証しない**。tx が無く、**相手マウントのバイトがまだ DB に無い**ので、原理的に「本当の末尾」を知る手段がありません。**手元の dirty サイズが権威**になります |

> **1 回の `write(2)` 全体は不可分ではありません。** `max_write` を超える書き込みは FUSE が複数
> コールバックに分割するので (negotiate 値は環境で変わる。実測したホストでは **1 MiB**、4 MiB の追記が
> 1 MiB × 4 回)、**その隙間に他マウントの追記が挟まり得ます**。挟まってもバイトは失われません
> (各コールバックが直前のコミットの続きから書く) が、**1 回の `write(2)` が連続した領域になることは
> 保証しません**。ローカル FS は 1 回の `write(2)` の間ずっと `i_rwsem` を握るので連続しますが、
> **FUSE のコールバックは独立した要求なので pgfs 側からその保証は作れません**
> (ハンドルに「この `write(2)` は継続中」を持たせても、途中でプロセスが死ぬとロックが残ります)。

> **これは FUSE 側の性質であり、Windows (Dokan) は分割しません** (2026-09-21 に Windows 実機で実測)。
> 64KB / 1MB / 16MB / **64MB** のすべてで、1 回の `WriteFile` が **1 回のコールバック**として届きました
> (生の Win32 `WriteFile` / `FILE_FLAG_WRITE_THROUGH` / `FILE_APPEND_DATA` / .NET `FileStream` の 4 経路とも
> 同じ。`NoCache=False, PagingIo=False` = キャッシュ経由でもページング I/O でもない同期の降り方)。
> **つまり「1 回の書き込みが不可分になる上限」は OS で違います** — 上の `max_write` の話を Windows に、
> Windows の 64MB を Linux に、それぞれ持ち込まないこと。Dokan 側の as-built は
> [Assign.ja.md](Assign.ja.md) の append 行が正です。

**実測** (2 マウントから同時に追記。A が 4 MiB を 1 回で、B が 10 バイト × 12 を割り込ませる):

| | 結果 |
|---|---|
| 対応前 | **−4,194,304 バイト** (`st_size` が巻き戻り、チャンクは DB にあるのに FS から到達不能) |
| `st_size` の単調化のみ | Linux −40 / Windows −10 バイト (巻き戻りは消滅。**追記先を tx の外で決めていたぶんが残る**) |
| **末尾を tx の中で確定 (現行)** | **0 (両 OS で緑)** |

回帰は [tests/linux/crossclient.sh](../tests/linux/crossclient.sh) と
[tests/windows/crossclient.ps1](../tests/windows/crossclient.ps1) の
`..._concurrent_append_keeps_all_bytes` 系。**順序 (挟まり方) は見ません** — 割り込みのタイミングに
依存して安定しないので、**契約で守ると言った総バイト数だけ**を assert します。

> **`.NET の FileMode.Append` / 自前でシークしてから書くアプリはこの契約の対象外です。**
> FileStream が **open 時に末尾へシークして明示オフセットで書く**ので、**アプリが自分で決めた
> オフセット**になります。FS は言われた位置に書くだけで、そこが末尾かどうかは知りません。
> シェルの `>>` (素の `O_APPEND`) や Win32 の `FILE_APPEND_DATA` を使ってください。

### 拡張属性・シンボリックリンク・ハードリンク

すべて実装済み:

- 拡張属性は `pgfs_inode.xattr_names TEXT[]` + `xattr_values BYTEA[]` の並行配列に格納 (`Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr`、値は bytea 忠実保持)。`GetXAttr` / `ListXAttr` はキャッシュにある `Inode.xattr_names`/`xattr_values` を in-memory 探索する (SELinux の `security.selinux` 頻繁プローブ対策)。設計は [xattr-bytea.ja.md](design/xattr-bytea.ja.md)。
- シンボリックリンクは `link_target` 列に格納 (`Api.CreateSymlink`)。`ReadLink` は NUL 終端で返す。
- ハードリンクは同じ `data_id` を共有する複数 inode を作成 (`Api.CreateHardLink`)。`st_nlink` は全リンクで同期更新。

### POSIX ACL (`system.posix_acl_access`)

`setfacl` / `getfacl` と往復する。`system.posix_acl_access` の getxattr/setxattr を特別扱いし、ACL バイナリと
**`st_mode` の基本3クラス + 正準 ACL ドキュメント (`user.pgfs_acl` の named エントリ)** を相互変換する
([src/fuse/src/FileSystem.cs](../src/fuse/src/FileSystem.cs) の `BuildPosixAccessAcl` / `SetPosixAccessAcl`、コーデックは
[PosixAcl](../src/fuse/src/PosixAcl.cs))。

- entry 順は USER_OBJ → USER* → GROUP_OBJ → GROUP* → MASK → OTHER。mask は group_obj ∪ 全 named を都度再計算。
- named エントリが無い「最小 ACL」は ENODATA を返し、getfacl が mode から導出する慣習に合わせる。
- この正準ストアは Windows の DACL (`Get/SetFileSecurity`) と共有される。POSIX 正準・Windows 投影ビューの設計は
  [permission-interop.ja.md](design/permission-interop.ja.md)。`system.posix_acl_default` は現状パススルー (Linux 内 round-trip のみ)。

### `st_atime`

要件: 最終アクセス時刻は保持せず、`st_mtime` と同じ値を返す。実装もそのとおりです (`FillStat` で `s.st_atim = mtime.ToTimespec()`)。

### マウントオプション (`-o key=val,flag,...`)

`mount -t pgfs` / fstab / 直接起動のいずれでも `-o` を受け付けます。パースは
[ConfigLoader.ParseDashOOptions](../src/core/src/Config/ConfigLoader.cs) が担い、各キーを次の**クラス**の
いずれか 1 つに分類します (互換マップの正はこのメソッド)。`mount(8)` helper 呼び出し規約・fstab エントリ書式・
起動時自動マウントの詳細は [fstab-support.ja.md](design/fstab-support.ja.md) を参照。

| クラス | 例 | 扱い |
|---|---|---|
| **(1) FUSE passthrough** | `allow_other` `allow_root` `default_permissions` `ro` `auto_unmount` `kernel_cache` `auto_cache` / 値あり: `umask=022` `uid=` `gid=` `fsname=` `subtype=` `entry_timeout=` `attr_timeout=` | libfuse へ verbatim 転送 ([Program.RunFuseMountAsync](../src/mount/src/Program.cs) が `attr_timeout=0` の後ろに連結。後勝ちで上書き可) |
| **(2) 受理して無視** | `rw` `nonempty` `direct_io` `defaults` `nofail` `noauto` `_netdev` `user(s)` `owner` `group` `noatime` 系 `nostrictatime` `lazytime`/`nolazytime` `mand`/`nomand` `iversion`/`noiversion` `comment=` `nosuid`/`nodev` `exec`/`async` 等 | カーネル mount 層 / fstab 慣習で、**落ちても意味が変わらない**もの。FUSE には**渡さない** (黙って受理)。`nosuid`/`nodev` は **fusermount3 が非特権マウントに必ず付ける**ので指定の有無で結果が変わらない (実機の `/proc/self/mountinfo` で確認) |
| **(2″) 受理するが適用できない** | `noexec` `suid` `dev` `sync` `dirsync` / **`max_read=` `max_readahead=`** | **Warning を出して**無視。**落ちると意味が変わる**ので黙らない。実機確認: `-o noexec` を付けても `/proc/self/mountinfo` に載らず、**そのマウント上のスクリプトが実行できた** = 実行制限が効いていない。`suid`/`dev` は逆向きで、要求しても fusermount3 が `nosuid,nodev` を強制する |
| **(2′) userspace 接頭辞** | `x-systemd.automount` `x-systemd.requires=` `x-gvfs-show` `x-mount.mkdir` | `x-` 接頭辞を一括で (2) と同じく無視 (systemd / gvfs 等が解釈する fstab 拡張) |
| **(3) pgfs 設定** | `-o schema=foo` `-o cache-max-entries=2048` | `-`/`_` を正規化して設定 [Field](../src/core/src/Config/Field.cs) に流す (`Scope.Key` / dash-o 名で照合) |
| **(4) 未知** | `-o allwo_other` (タイポ) | **Warning ログ**を出して無視 (起動時に気づけるように) |
| **(5) 非対応のマウント操作** | `remount` `bind` `rbind` `move` | **専用 Warning** (「pgfs では未対応」) を出して無視。タイポ (4) と区別し "remount したつもり" の誤解を防ぐ |

ポイント:
- `-o ro` は libfuse 経由でカーネルがマウントを `MS_RDONLY` 化し、書き込みをカーネルが弾きます (FS 層の改修不要)。`rw` は既定なので無視。
- (1) は現行パーサーが転送するキーの一覧であり、すべての libfuse3 で受理される保証ではありません。`fuse_new` は未知オプションで失敗するため、`nonempty` (libfuse3 で廃止) / `direct_io` (libfuse3 では mount-wide ではなく per-file `fi->direct_io` へ移行) のような値は (1) ではなく (2) で握りつぶします。
- **(1) の一覧は 2026-09-20 に実機で 1 つずつ確かめました** (このサーバ・libfuse3 3.10.2)。残っているものはすべて「指定してマウントが成立し、維持される」ことを確認済みです (`allow_other` / `allow_root` は `/etc/fuse.conf` の `user_allow_other` が要るので、非 root ではこの環境で失敗します — これは仕様どおり)。
- **`-o max_read=N` と `-o max_readahead=N` は (1) から外しました**。`max_readahead` は `max_write` と同じく `fuse: unknown option(s)` で `fuse_new` が失敗します。**`max_read` はもっと悪く**、マウントは成立して親に成功を返した直後に**セッションが終了し、プロセスが exit 0 で消えます** (4096 / 65536 / 131072 / 1048576 のいずれでも同じ = 値に依りません)。**fstab から見ると「mount は成功したのに何もマウントされていない」**という、いちばん気づきにくい壊れ方になります。**原因は未解明**ですが、現バインディングでは渡してはいけないので (2″) に移して警告します。
- **`-o max_write=N` は (1) ではなく (3) へ流します** (修正)。libfuse は `max_write` を**マウントオプションとしては受け付けず** (init コールバックで設定する項目)、転送すると `fuse: unknown option(s)` → `fuse_new` 失敗で**マウント自体が落ちます** (実機確認)。現在は `mount.max_write` に流れるので、`-o max_write=65536` / `--max-write 65536` / TOML の `mount.max_write` のどれでも同じ結果になります。
- `defaults` / `nofail` / `x-systemd.*` は実機 fstab で踏む定番です。(2)/(2′) で握りつぶすので警告は出ません。
- `Pgfs.Fuse.MountOptions` 自体は `SingleThread` のみ持ち、今は `false` (マルチスレッド) 固定です。

### シャットダウン

`Ctrl+C` / `SIGTERM` 受信時に `LazyUnmount` を試み、FUSE ループ終了後に `Api.Dispose` で未 flush を処理します。書き残しの報告と終了コードの制限は上記 write-back 節を参照してください。`SIGKILL` 等で正常終了経路を通らなかった場合は `fusermount3 -u <mountpoint>` で手動 unmount してください。

## 既知の制限・TODO

| 項目 | 状態 | メモ |
|---|---|---|
| データ I/O (Read/Write) | ✅ | `Api.ReadData` / `Api.WriteData` を `pgfs_data_chunk` の `bytea` チャンクで実装 (Phase 1 で Large Object から移行) |
| `uid/gid` ↔ `uname/gname` の双方向解決 | ✅ | [src/fuse/src/UserResolver.cs](../src/fuse/src/UserResolver.cs) で libc P/Invoke |
| `Chown` の uname/gname 反映 | ✅ | `Api.UpdateOwner` を呼ぶ。`uid == -1` は変更しない慣習も尊重 |
| 拡張属性 (xattr) | ✅ | `Api.GetXAttr` / `SetXAttr` / `ListXAttr` / `RemoveXAttr` 実装。値は **`xattr_names TEXT[]` + `xattr_values BYTEA[]` の並行配列**で bytea 忠実保持 (NUL 含む任意バイト列も無加工で往復)。設計は [xattr-bytea.ja.md](design/xattr-bytea.ja.md) |
| シンボリックリンク | ✅ | `Api.CreateSymlink` / `ReadLink` 実装 |
| ハードリンク | ✅ | `Api.CreateHardLink` 実装。`Unlink` で残り inode の `st_nlink` を更新 |
| Truncate のチャンク削減 | ✅ | `Api.TruncateData` が新サイズを超える `bytea` チャンク行を削除し、末端チャンクを `substring`/`overlay` で詰める (Phase 1 で Large Object から移行) |
| POSIX ACL (setfacl/getfacl) | ✅ | `system.posix_acl_access` ⇄ `st_mode` + 正準 ACL (`user.pgfs_acl`)。Windows DACL と同じ正準ストアを共有。詳細は上記「POSIX ACL」/ [permission-interop.ja.md](design/permission-interop.ja.md)。named ACL の厳密 enforce は要件待ち |
| macOS 動作確認 | ❌ | libfuse の macOS 対応次第。macFUSE が必要 |
| アクセスチェック (`Access`) | ❌ | 当面マウント時に `default_permissions` を渡せばカーネル側で判断される想定 |
| Mount オプション `-o` | ✅ | `-o key=val,flag,...` を分類 (FUSE passthrough / 受理して無視 + `x-` 接頭辞 / pgfs 設定 / 未知=Warning / 非対応マウント操作=明示 Warning)。詳細は上記「マウントオプション」。実装は [ConfigLoader.ParseDashOOptions](../src/core/src/Config/ConfigLoader.cs) |
| 接続失敗時の再接続 | ✅ | [Retry](../src/core/src/Utility/Retry.cs) で `Pg.OpenConnection` 系を包む。指数バックオフ、`database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` で調整。任意のクエリを再試行するものではない。別途、create / write / flush に `40P01`・`40001` の bounded tx retry がある ([support_for_citus.ja.md](design/support_for_citus.ja.md)) |
| OS に存在しない uname / gname の見せ方 | ✅ | v0.2.1〜。**カーネルの overflowuid / overflowgid** (`/proc/sys/kernel/overflow*`・通常 65534 = Debian 系 `nobody` / `nogroup`、RHEL 系 `nobody` / `nobody`) として見せる (設定は持たない。旧 `mount.fallback_*` は廃止)。名前の無い uid / gid (コンテナの uid など) で作ったものは DB に `file_system.unknown_name` (`(unknown)`) と書く。**自分の名乗りは `mount.self_uname` / `self_gname`** (自分が作ったものに付け、その名前は自分の uid / gid として見せる)。注意: 名前の無い uid は、自分が作ったファイルでも overflowuid の所有に見えるので `default_permissions` の下では書けない (名前で持つ設計の制限・v0.2.0 からの挙動)。実装は [src/fuse/src/UserResolver.cs](../src/fuse/src/UserResolver.cs)、e2e の `test_fallback_uname_gname` で検証 |
| Read/Write のキャッシュ・バッチ | ⚠️ | bytea チャンク方式。read cache / ファイル単位 write-back は実装済み。read-ahead とファイル横断 flush バッチは未実装 |
| xattr 値のバイナリ表現 | ✅ | `xattr_values BYTEA[]` に**生バイト列を忠実保持** (旧 Base64+JSONB から移行)。NUL 含む任意バイト列が無加工で往復し、SQL でも bytea として直接見える。設計・検証は [xattr-bytea.ja.md](design/xattr-bytea.ja.md) |

## 動作確認シナリオ（Linux 想定）

```bash
# 1. PostgreSQL に PGFS を初期化（[docs/Mkfs.md](Mkfs.md) 参照）
dotnet run --project src/mkfs -- -f pgfs.toml

# 2. マウントポイントを準備
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# 3. マウント
dotnet run --project src/mount -- -m /mnt/pgfs

# 4. 別ターミナルでアクセス: メタデータ系
ls -la /mnt/pgfs                # ルートディレクトリ
mkdir /mnt/pgfs/hello           # ディレクトリ作成
ls -la /mnt/pgfs                # hello が見える
rmdir /mnt/pgfs/hello           # 削除
touch /mnt/pgfs/empty.txt       # 空ファイル作成
chmod 600 /mnt/pgfs/empty.txt   # パーミッション変更
chown $USER:$USER /mnt/pgfs/empty.txt  # オーナー変更
rm /mnt/pgfs/empty.txt          # ファイル削除

# 5. データ I/O
echo "hello world" > /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # → hello world
truncate -s 5 /mnt/pgfs/test.txt
cat /mnt/pgfs/test.txt          # → hello

# 6. シンボリックリンク
ln -s /etc/hostname /mnt/pgfs/host
readlink /mnt/pgfs/host         # → /etc/hostname

# 7. ハードリンク
echo data > /mnt/pgfs/orig.txt
ln /mnt/pgfs/orig.txt /mnt/pgfs/copy.txt
ls -l /mnt/pgfs                 # 両方とも st_nlink=2 になっている
rm /mnt/pgfs/orig.txt
cat /mnt/pgfs/copy.txt          # → data （データはまだ生きている）

# 8. 拡張属性
setfattr -n user.tag -v hello /mnt/pgfs/copy.txt
getfattr -d /mnt/pgfs/copy.txt  # → user.tag="hello"

# 9. アンマウント
fusermount3 -u /mnt/pgfs
# または mount.pgfs を起動しているターミナルで Ctrl+C

# プロセスがクラッシュした場合は手動でアンマウントが必要:
mount | grep pgfs                # /dev/fuse on /mnt/pgfs が残っていたら
fusermount3 -u /mnt/pgfs
# または sudo umount /mnt/pgfs
```
