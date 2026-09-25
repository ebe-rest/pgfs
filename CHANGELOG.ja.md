# 変更履歴

> **道順**: [docs/README.ja.md](docs/README.ja.md) › **本書**
>
> **この doc が正である範囲**: **リリースタグ単位**の利用者向け差分。移行が必要な変更・既知の制限・
> データが壊れ得た修正を必ず書く。**内部リファクタは載せない**。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [docs/history.ja.md](docs/history.ja.md) | 専用 doc を持たない完了項目の**経緯** |
> | 各 `docs/design/*.md` | 機能ごとの設計と as-built |

このファイルは**リリースタグ単位**の変更を記録する。粒度は「利用者が見て分かる差分」で、
内部リファクタは原則載せない (経緯は [docs/history.ja.md](docs/history.ja.md) と各設計 doc が正)。

## [v0.2.1] - 2026-09-25

### ⚠ 移行が必要な変更

- **`mkfs --clean` は消す前に確認を取るようになった** (接続先・DB の中の schema 全部・Citus の worker を見せて y/N)。**スクリプトから呼ぶなら `--yes` (`-y`) を足す** — 非対話で無いと何も消さずに exit 3。
  また**接続中のもの (生きているマウント / 他の接続) があると消さない**。v0.2.0 までは黙って切っていた。待たずに切るなら `--now` (対話なら「再試行しますか」を繰り返す)。
- **mkfs は設定ファイルの場所 (`-f <path>`) が必須になった**。ファイルがあれば読み込んで終了時にそこへ書き戻し、無ければ新規作成、
  `--clean` なら読まずに上書きする。**既定の探索パス (`~/.config/pgfs` / `%LOCALAPPDATA%\pgfs` など) は mkfs では使わない**。
  v0.2.0 までは探索パスで見つけた toml を読み込み、**その場所へ書き戻していた**ため、常駐マウント用の toml を上書きする事故が起き得た。
  スクリプトから mkfs を呼んでいるなら `-f pgfs.toml` を足せば従来 (カレントに書く) と同じになる。
- **既存の FS に `--clean` なしで mkfs を打ち直しても、FS 固有の設定 (`audit.enabled` / `app.statfs` / `app.plperlu` / `file_system.*`) を変えなくなった**。
  DB の値を使い、CLI で違う値を渡すと Warning を出す (変えるなら `pgfsctl config set`)。v0.2.0 までは CLI か既定値で**黙って上書き**していた
  (例: 後から audit を on にした FS に `--audit` なしで打つと off に戻った)。
- **`mount.fallback_uname` / `mount.fallback_gname` を廃止した**。このホストに無い名前は OS の値 (Linux: カーネルの overflowuid / overflowgid、
  Windows: `ANONYMOUS LOGON`) として見せ、作った人の名前が分からないときは DB に `file_system.unknown_name` (既定 `(unknown)`) と書く。
  指定しても専用の Warning を出して無視する。el9 などで `nobody` / `nobody` に変えていた場合は、何もしなくても OS の値で同じ見え方になる。
- **作るときだけの指示 (`database.citus` / `shard_count` / `shard_replication_factor` / `tablespace` / `tablespace_path`) を DB に保存しなくなった**。
  既存 FS に残っている行は読まれないだけ (消さなくてよい)。`pgfsctl status` の Citus 表示は DB の実体 (`citus` 拡張の有無) から出す。
- **`mkfs --clean` は、消す worker を DB の実体 (`pg_dist_node`) で決めるようになった**。v0.2.0 までは `--worker` で渡した worker だけを消していて、付け忘れると worker 側に DB が残った
  (配布用 toml には workers を書かないので、付け忘れは普通に起きる)。**worker に 1 つでも繋がらなければ何も消さずに止まる** — mkfs を動かすホストから
  `pg_dist_node` のノード名で届く必要がある。使っていない worker は先に `citus_remove_node` で外す。
- **既存の DB に mkfs を打ち直したとき、`--citus` / `--worker` / `--tablespace` を効かせなくなった** (Warning を出す)。Citus かどうかは DB の実体で決まり、
  Citus でない DB に `--citus` を付けても Citus にならない (v0.2.0 までは新しい schema のテーブルを分散しようとして失敗した)。
  既存の DB のときは `--worker` の各ノードにロールや tablespace も作らない (v0.2.0 までは作っていた)。Citus にする / worker を足すなら `--clean` で作り直すか `citus_add_node`。
- **`database.notify_enabled` の既定を `true` にした** (他マウントの変更通知が既定で on)。v0.2.0 までは既定 `false` で、
  複数マウントでは `--notify` を付けないと他マウントの create / delete / rename がいつまでも見えなかった。
  **1 マウントだけの運用で通知のコスト (LISTEN 接続 1 本・書き込みごとの `pg_notify`) を省きたいときは `--no-notify`** か
  TOML の `notify_enabled = false`。既存の toml に `notify_enabled = false` が書いてあれば、それが優先されて従来どおり off のまま。
- **Windows (assign) でも POSIX の権限 (mode + ACL) を判定するようになった** (`app.enforce_permissions`・既定 `true`)。v0.2.0 までは
  Windows からは mode に関係なく読み書き・作成・削除ができた (`root:root 0755` の root 直下にも書けた)。上げると、**Windows から書けていたものが拒否され得る**。
  判定は所有者 → 名前付きユーザー → グループ → other の順で、所有者名が Linux 側と一致しないユーザーは other の権利になる。
  **昇格したプロセス (Administrators が有効) は素通し** (Linux の root 相当。UAC で制限された普段のプロセスは素通しにならない)。
  困ったら `pgfsctl config set app.enforce_permissions false` で走行中に v0.2.0 と同じ挙動へ戻せる。Linux の判定 (`default_permissions`) は変わらない。

### 変更

- **`mkfs --purge` を追加した** (消して終わる)。消す相手は `-f` の設定ファイルの接続先で、Citus なら全 worker の同名 DB も消す。ロール / tablespace / 設定ファイルは残す。`--clean` との同時指定は不可。
- **`pgfsctl status` の Filesystem 節に `target` (接続先 host:port/db) を足し、`citus` にノード (`coordinator host:port` / `worker host:port`) を DB の実体から出すようにした** (`--json` は `target` / `citus_nodes`)。消す前にどこを消すかを確かめる用。
- **Windows の「読み取り専用」属性を、誰も書けない (owner / group / other の w が全部落ちている) ときだけ立てるようにした**。v0.2.0 までは
  「マウントしているユーザーが書けるか」で決めていたので、**他人の所有の `0644` などが読み取り専用に見え、Windows から (昇格しても) 削除できなかった**。
  誰が書けるかは v0.2.1 の権限の判定が決める。
- **他のマウントが root (`/`) の属性 (mode / 所有者 / 時刻) を変えたとき、再マウントしなくても見えるようにした**。v0.2.0 までは root を起動時に 1 回読んだきりで、
  root を `chmod` しても変更したホスト以外は古い値のままだった (root 直下のファイルの出入りは見えていた)。
- **mkfs が書き出す配布用 `pgfs.toml` の中身を整理した**。接続の核 (connection / schema / prefix) と、**CLI か読み込んだ TOML で明示した
  項目だけ**を書く。既定値のままの項目は書かない (後の版で既定が変わっても追従するように)。**`mount_point` も明示したときだけ**書くので、
  Linux で吐いた toml を Windows にそのまま配れる。
- **mkfs に mount / assign 向けの設定を渡せるようにした** (`mkfs --notify` / `--write-back` など)。v0.2.0 までは CLI で受け付けても
  toml に書かれずに黙って捨てられていた。`mkfs --help` にも `[written to pgfs.toml for mount/assign]` の注記つきで載る。
- `--no-notify` を追加した (mkfs / mount / assign 共通)。
- **`--version` がプログラムの版を表示して終了するようになった** (mkfs / mount / assign / pgfsctl)。v0.2.0 まで mkfs の `--version` は FS フォーマットの版 (`file_system.version`) の指定だったので、そちらは **`--fs-version`** に改名した (移行: mkfs に `--version` を渡すスクリプトは `--fs-version` に直す)。
- **`mount.self_uname` / `mount.self_gname` を追加した** (toml / CLI)。マウントを動かしているユーザー自身が作ったものに付ける名前 (補完) で、
  名前が引けても引けなくても上書きする (例: Entra ID で自分の SID を名前に引く権限が無い / 使いたくない名前)。他のユーザーの要求には使わない。
  この名前は自分の uid / SID として見せる。DB は全員共有の `pgfs` ロールで認証しているので、これは認証ではなく「このクライアントがこう名乗る」宣言。
- **`mount.write_back` をライブで off にしたあとの drain (期限内に書き切れなかったぶんの書き出し) が終わったとき、`pgfsctl status` にその場で反映するようにした**。v0.2.0 までは次の heartbeat (最大 30 秒) まで「未 flush が残っている」と出続けた。
- テストの環境依存を 3 件直した: Linux e2e の名前解決のテストが DB / schema を決め打ちしていた (`PGFS_SCHEMA` / `PGFS_PREFIX` / `PGFS_DB` か設定ファイルから取る) / メタデータ write-back の監査のテストが `audit.enabled` を立てる INSERT に NOT NULL の列を渡しておらず、元から on の環境でしか検査になっていなかった / write-back のライブ off のテストが drain の終わりを待っていなかった。
- **Windows でログオン時に常駐させるスクリプトを同梱した** ([scripts/windows/](scripts/windows/pgfs-mount.ps1): `pgfs-mount.ps1` / `pgfs-mount.cmd` / `register-logon-task.ps1`)。多重実行ガード付きで assign.pgfs を隠しウィンドウで上げ、タスクは実行時間の制限なし・`IgnoreNew` で登録する。手順は [docs/Assign.ja.md §ログオン時に常駐させる](docs/Assign.ja.md)。
- `tests/windows/permissions.ps1` を追加した (Windows の権限判定 12 件。ユーザーを作らずに、昇格したシェルで他人の所有のファイルを仕込み、Administrators を無効にした制限トークンで確かめる)。
- **mkfs に `--root-access owner|everyone` を追加した**。`everyone` で root ディレクトリを `1777` (誰でも書ける・消せるのは作った本人だけ) で作る。既定は従来どおり `owner` (`root:root 0755`)。root を新しく作るときだけ効き、既にあれば Warning を出して変えない。

### 既知の制限

- **Windows では途中のディレクトリの実行権 (探索) を見ない**。Windows は既定で全員が「走査チェックのバイパス」を持つのに合わせたためで、
  **`0700` のディレクトリの奥にある `0644` のファイルは、Windows からだけ (パスを直接指定すれば) 読める**。Linux (`default_permissions`) は経路の各ディレクトリの x を見る。
- **Windows では、全員の書き込み権を落とした (`chmod a-w`) ファイルは削除できない** (読み取り専用属性が立ち、Windows は読み取り専用のファイルの削除を断る)。
  Linux では親ディレクトリの w があれば消せる。Windows から消すなら先に読み取り専用を外す。
- **`MAXIMUM_ALLOWED` だけで開く要求は判定していない** (未検証)。

## [v0.2.0] - 2026-09-23

> **v0.1.0 からの差分**。柱は ① プロジェクト構成の作り直し (libfuse 内製化)
> ② 運用フェーズ = キャッシュ / write-back / `pgfsctl` / GUI ③ Windows 実装の底上げ
> ④ ハードリンクまわりの正しさの修正。

### ⚠ 移行が必要な変更

- **`vendor/Tmds.Fuse` submodule を廃止した。** libfuse バインディングを `Pgfs.Fuse`
  ([src/fuse/](src/fuse/)) に内製化したため、**`git clone --recurse-submodules` は不要**になった。
  v0.1.0 のクローンを使い続ける場合は `git submodule deinit -f vendor/Tmds.Fuse` で外してよい。
  クレジットは [src/fuse/NOTICES.md](src/fuse/NOTICES.md) が原典を指す。
  **Linux では実行時に libfuse3 (`libfuse3.so.3`) が必要** — `dlopen` するので、
  ディストリの `fuse3` 相当パッケージを入れておくこと。
- **スキーマを 1 つ追加した (`{prefix}mounts`)。** 稼働中マウントの揮発レジストリで、
  `pgfsctl status` と `pgfsctl prune` の土台になる。**既存のファイルシステムには
  [docs/ddl/pgfs_mounts.sql](docs/ddl/pgfs_mounts.sql) を流す** (`pgfs.pgfs_mounts` を自分の
  `<スキーマ>.<接頭辞>mounts` に読み替える)。**Citus では続けて
  `SELECT citus_add_local_table_to_metadata('<スキーマ>.<接頭辞>mounts');` も流す**
  (`{prefix}lock` / `{prefix}settings` と同じく、分散せずメタデータに登録する local テーブル)。
  **`mkfs` の再実行は使わないこと** — `--clean` を付けなくても、DB に保存してある設定
  (`audit.enabled` / `app.plperlu` / `database.citus` / tablespace / `file_system.*` など) を
  **その実行の CLI 指定か既定値で上書きし**、`pgfs.toml` も書き換える (下 §既知の制限)。
  流すまでは mount が warning を出して登録を skip するだけで、読み書きには影響しない。ただし
  **`pgfsctl prune` は生きているマウントを判定できないので、データを消す側には `--force` でも触らない**。

### 追加

- **`pgfsctl`** — 実行時コントロールプレーンの CLI ([docs/Pgfsctl.ja.md](docs/Pgfsctl.ja.md))。
  - `pgfsctl config get / list / set` — **稼働中のマウントへライブ反映**できる
    (制御チャネルは常時 ON なので `--notify` を付けていないマウントにも届く)。
  - `pgfsctl status [--json]` — クラスタ稼働一覧 / FS 統計 / 稼働プロセスのキャッシュ統計と実効設定。
  - `pgfsctl prune [--apply]` — **異常終了が残したものを掃除する**。`{prefix}mounts` の古い行 /
    どの inode からも参照されていない data 行 / libfuse の `.fuse_hidden*` の残骸が対象。
    **既定は dry-run**。**データを消す側は、生きているマウントが 1 つでもあれば触らない**
    (`--force` で上書き可・全マウントを止めてから)。同一ホストのマウントは pid の生存で、別ホストの
    マウントは猶予 (`--mounts-older-than`・既定 3600 秒・**下限 600 秒**) を超えるまで生きているとみなすので、
    **heartbeat が遅れただけのマウントを死んだ扱いにしない**。**`{prefix}mounts` を読めない
    (未移行の既存 FS など) ときは、生きているマウントを判定できないので `--force` でもデータ側に触らない**。
    残骸と判定するのは libfuse の厳密な書式 (`.fuse_hidden` + 16 桁の 16 進) だけで、利用者が
    `.fuse_hidden_notes.txt` と名付けたファイルは触らない。**喪失を記録した行 (墓標) は消さない。**
    マウントは自分の登録行が消えていたら heartbeat の周期で登録し直す。安全弁の詳細は
    [docs/Pgfsctl.ja.md](docs/Pgfsctl.ja.md)。
- **write-back キャッシュ** (`mount.write_back`・**既定 off**)。実測 `dd bs=128k` で **6.1×**、
  `rsync` で 1.4×。正は [docs/design/write-back.ja.md](docs/design/write-back.ja.md)。
- **メタデータ write-back** (`mount.write_back_metadata`・**既定 off**)。close の同期 flush をやめ、
  pending inode をまとめて 1 tx で書く。`fsync` / `fsyncdir` が唯一の硬いバリアになる。
  同期化ヒューリスティック (rename-over-existing / `O_TRUNC` / `O_EXCL`) を内蔵。
- **キャッシュ** — inode の LRU (`mount.cache_max_entries`) / 内容キャッシュ
  (`mount.cache_data_max_bytes`) / negative キャッシュ (`mount.negative_cache_ttl_ms`)。
- **GUI (Avalonia)** — 読み取りダッシュボード。接続バー / Mounts / Filesystem / Process detail / Config。
  **設定変更はまだできない** (下 §既知の制限)。
- **Windows (assign.pgfs)**:
  - 新規 inode の **所有者を要求元アカウントから導出**する (グループは親ディレクトリから継承)。
  - **byte-range lock** をドライバに委ねるようにした (従来は「常に成功」= 取れていないロックを
    取れたと嘘をついていた)。
  - `FILE_FLAG_WRITE_THROUGH` を WriteFile ごとの完全バリアとして配線。
  - 未 flush を残したままアンマウントしたら **exit 4** で報告する。
  - マウント先が使えない場合 (使用中のドライブレター等) を**起動前に**弾き、空き候補を出す。
- **停止シグナルの段階化** (mount.pgfs / assign.pgfs 共通)。1 発目 = アンマウント要求、
  **2 発目 = flush の待機を打ち切り、何が失われるかを列挙してから exit 4**、3 発目以降 = 即時終了。

### 変更

- **プロジェクトを 4 つに分割した** — `Pgfs.Core` (SQL 共通・OS 分岐ゼロ) / `Pgfs.Fuse` (Linux) /
  `Pgfs.Dokan` (Windows) / 薄い実行ファイル。正は
  [docs/design/v0.2.0-plan.ja.md](docs/design/v0.2.0-plan.ja.md) と
  [docs/design/fuse-binding.ja.md](docs/design/fuse-binding.ja.md)。
- **タイムスタンプを UTC に統一**した (`TIMESTAMP` 列は常に UTC で保存)。
- **`st_blocks` が実占有バイトを返す**ようになった (`du` が実際のサイズを見る)。
- ドキュメントを `docs/design/` に階層化した。索引は
  [docs/README.ja.md](docs/README.ja.md)。

### 修正

- **開いたファイルの識別がパス優先だった問題を直した**。開いている最中に rename / 同名再作成が
  起きると、以後の読み書きが別のファイルに着弾していた。**open 時に確定した inode id** で引き直す
  ようにした。設計と実測は [docs/design/handle-context.ja.md](docs/design/handle-context.ja.md)。
- **URL 形式の接続文字列 (`postgresql://user:pass@host:port/db`) で起動できなかった問題を直した**。
  ドキュメントと fstab の例は URL 形式を前提にしていたが、Npgsql は URL を解釈しないため、`-c` や
  fstab の 1 列目に URL を書くと **`Format of the initialization string does not conform to specification`
  / `Couldn't set postgresql://...` で起動すらしなかった** (後者のメッセージには**パスワードが平文で入る**)。
  libpq の接続 URI の書式 (パーセントエンコード / 複数ホスト / `[::1]` の IPv6 / `?sslmode=` などの
  クエリ) を解釈して kv 形に変換するようにした。**解釈できないクエリのパラメータ (綴り間違いの
  `sslmod=` など) は黙って捨てず、どのパラメータかを示して起動を止める**。起動ログ・生成 `pgfs.toml` の
  先頭コメント・`pgfsctl config list` では、URL 形式のパスワードも伏せる。
- **同梱の設定サンプルが、そのままでは動かなかった問題を直した**。
  [pgfs.toml.example](pgfs.toml.example) が接続を `database.connection.host = "..."` のような
  **テーブル形式**で書いていたが、**設定ファイルは `スコープ.キー = 値` の 2 段しか解釈しない**。
  コピーして使うと接続文字列が化け、**`Format of the initialization string does not conform to
  specification` という原因を指さない例外**で起動に失敗した。サンプルを **1 行の接続文字列**に直し、
  [docs/Mkfs.ja.md](docs/Mkfs.ja.md) の「設定ファイル側で個別に書ける」という記述も削除した。
  **テーブル形式を書いた場合は、値を採らずに「1 行で書いてください」と警告する**ようにしたので、
  古いサンプルをコピー済みでも何が悪いか分かる。
- **変更通知が届かないと、他クライアントが永久に古い値を返し続ける問題を直した**。
  通知の送出に失敗したとき (DB の瞬断・`pg_notify` のペイロード上限 8000 バイト超) や、受け手の
  LISTEN の接続が切れていたあいだの通知は**二度と届かない**。受け手の inode キャッシュには
  **TTL が無い**ため、`ls` で親を引き直すかキャッシュから溢れるまで**古いサイズや古い内容を返し続けた**。
  送れなかった側は「キャッシュを全部捨てろ」を 1 件に畳んで次の送出で必ず送り (停止時の積み残しぶんも
  送る)、受け手は **LISTEN を張り直したときに自分から** clean なキャッシュを捨てるようにした。
  捨てるのは clean だけで、未 flush の dirty とメタデータ write-back の pending inode は残す。
- **他マウントがファイルを縮めた後の上書きで、書いたバイトが読めなくなる問題を直した**。
  他のマウントが `truncate` した後に、**サイズを伸ばさない上書き** (`O_TRUNC` を伴わない write) を
  すると、**`st_size` が更新されず書いたバイトが到達不能**になった (`stat` は 0、`cat` は空を返す。
  チャンクは DB に残る)。原因は**サイズの判定に手元のキャッシュを使っていた**こと。
  `database.notify_enabled` を有効にしていても、通知が着くまでの窓で同じことが起きた。
  **単一マウントでの読み書きには影響しない。**
- **ハードリンクの実体共有が壊れる問題を直した** (詳細は
  [docs/design/data-id-lifecycle.ja.md](docs/design/data-id-lifecycle.ja.md))。同じ根から 3 つ出ていた:
  - `truncate` / `>` によるゼロ化が**共有実体を消し、兄弟のリンクにデータ消失と壊れた参照を残す**
    (`cat` が NUL を返す・`st_nlink` も壊れる)。
  - **空ファイルのハードリンクがリンクにならない** (`st_nlink` が両方 1 のまま)。
  - **中身を書くと `st_ino` が変わる** (`rsync -H` / `find -samefile` / `tar` が誤動作する)。
- **消されたディレクトリの下にファイルを作れてしまう問題を直した**。他クライアントが親を消した後に
  作成すると、**FS からは到達できない孤児**が残り SQL でしか掃除できなかった。
- **ハードリンク兄弟のサイズ・更新時刻が伝播しない問題を直した** (同一マウント / 別マウントの両方。
  write-through の経路。write-back の flush の経路には残る穴がある — 下 §既知の制限)。
- Windows: 同時 `CREATE_NEW` の排他が DB の一意制約で決まるようにした (従来はローカルの不存在判定で、
  別マウントに勝てなかった)。
- Windows: **監査ログの呼び出し元が常に空だった**問題 (`GetRequestor` は `CreateFile` の中でしか
  成功しない)。

### 既知の制限

- **`mount.write_back` を on にしたまま、同じファイルを複数マウントから書かないこと。**
  write-back のマウントが未 flush のデータを抱えているあいだに他のマウントが同じ実体を書くと、
  **その書き込みが flush で消える** (2026-09-21 に Linux / Windows の両方で実測)。dirty バッファが
  **チャンクの完全な像**として持たれており、flush がそれを丸ごと書き戻すためである。
  **サイズそのものは巻き戻さない** (他マウントの `truncate` は `st_size` に効き続ける) が、
  write-back 側が抱えるチャンクの中のバイトは flush で上書きされる。そのため**他マウントが
  `truncate` で消したバイトが、後でファイルを伸ばしたときに元の内容のまま読める**ことがある
  (ゼロ埋めにならない。`du` も余分に数える)。**機密を消すつもりの `truncate` をこの構成に頼らないこと。**
  **消える範囲はバイト単位ではなくチャンク単位**で、**「write-back 側が読んだことのあるチャンク」全体**
  (既定 1 MiB) が対象になる。**`--notify` を付けても防げない** (通知は内容キャッシュを落とすが
  dirty は意図的に残すため)。**既定は off** なので、明示的に on にしていなければ影響しない。
  単一マウントからの読み書き、マウントごとに書くファイルが分かれている構成も影響を受けない。
  詳細は [docs/Mount.ja.md](docs/Mount.ja.md) §`mount.write_back` を on にしたまま…。
- **未 flush を残して終了した記録 (墓標) は、SQL でしか消せない。** write-back が書き戻せないまま
  マウントが終わると、`{prefix}mounts` の行を**削除せず**に残して失われた件数を記録する
  (`endedAt` は UTC で末尾に `Z` が付く。ログ行の時刻はローカルなので読み替えること)。これは
  **意図した動作** — 事故の記録が黙って消えないようにするためで、**`pgfsctl prune` も消さない**。
  ただし**消す手段が `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0`
  しか無い**ので、**psql を持たない環境 (典型的には Windows) では片付けられず、マウントのたびに
  同じ警告がログに出続ける**。**警告が出ていること自体は不具合ではない** (過去に喪失があった記録)。
  内容を確認したうえで上の `DELETE` を流せば止まる。名指しで消す導線は将来の課題。
- **append の不可分性には上限がある** — 追記は「いまの末尾」に着弾し、**write-through なら他マウントと
  並行していてもバイトは失われない** (末尾を書き込みトランザクションの中で確定させる)。ただし
  **1 回の `write(2)` 全体は不可分ではない**: `max_write` (環境により negotiate・実測ホストでは 1 MiB) を
  超える書き込みは複数コールバックに分割され、**その隙間に他マウントの追記が挟まり得る**
  (バイトは失われないが、連続した領域になることは保証しない)。**write-back 有効時は保証しない** —
  相手のバイトがまだ DB に無いので原理的に不可分にできない。**自前でシークしてから書くアプリ
  (.NET の `FileMode.Append` など) は対象外**。詳細は [docs/Mount.ja.md §append の契約](docs/Mount.ja.md)。
- **Windows の削除は遅延する** — `Remove-Item` が返っても、Windows は最後のハンドルが閉じるまで
  ファイルシステムに削除を伝えない。pgfs 側は受け取り次第消して通知する (実測 0.95 秒で他マウントへ
  着弾)。NTFS でも同じ挙動で、pgfs 固有ではない。
- **Windows で複数マウントするなら `--notify` が事実上必須** — 既定 (`database.notify_enabled = false`)
  では他マウントの変更がいつまでも見えない。
- **native link (ハードリンク / ジャンクション / シンボリックリンク) は Windows で作成も読み取りも
  できない** — 現行の Dokan バインディングに入口が無い。Linux 由来のシンボリックリンクは
  Windows の列挙から消える。**ただし Linux で作ったハードリンクは、Windows からも「同じファイル」と
  判定される** (`FileIndex` を `data_id` 由来にした。`st_ino` と同じ式)。
- **ハードリンクの兄弟どうしで、権限・所有者・明示的なタイムスタンプ変更が共有されない** —
  POSIX では同じ実体を指すリンクは `st_mode` / `st_uid` / `st_gid` / `st_mtime` を共有するが、
  pgfs は**リンクごとに独立した行**を持つ。**中身・サイズ・`st_ino` は共有する**
  (`data_id` を不変にして是正済み) し、**書き込みによる `st_mtime` の更新も兄弟へ配る**が、
  **`chmod` / `chown` / `touch` (`utimens`) は撃った側のリンクにしか効かない**。影響は 2 つある:
  - **片方を `0600` にしても、もう片方の名前からは元の権限で開ける。**
    **ハードリンクを権限の境界として使わないこと。**
  - **`touch` で更新したつもりの時刻が兄弟の側では古いまま**なので、**`make` やバックアップのように
    タイムスタンプで判断する道具が、兄弟の名前を見ると「更新されていない」と誤認する。**
- **ADS (代替データストリーム) 未対応**。
- **交換 rename (`RENAME_EXCHANGE`) は未対応** — Linux の `renameat2(2)` で `RENAME_EXCHANGE` /
  `RENAME_WHITEOUT` を指定した rename は **`EINVAL` で拒否する**。対応しているのは `RENAME_NOREPLACE`
  だけである。**黙って通常の置換に落とさない**のは、**交換のつもりの操作が置換として commit されると
  入れ替え先が不可逆に消える**ためで、拒否は意図した動作である。フラグを付けない通常の rename と
  `RENAME_NOREPLACE` は従来どおり動く。
- **打ち消された保留メタデータの監査記録は、書き戻す前にプロセスが落ちると残らないことがある** —
  メタデータ write-back (`mount.write_back_metadata`・**既定 off**) が保留している変更を**打ち消す操作**
  (作ってすぐ消す等) が起きると、監査行が一度キューに載る。**取り消しの作成・削除の対だけは同期で書く**
  ので残るが、**それ以外のキュー分は書き戻しを待つ**ので、**待っているあいだに異常終了するとその監査行は
  残らない**。**操作が成功したことは、監査行がその時点で永続化されたことを意味しない。**
  **既定のまま (off) なら発生しない。** 詳細は [docs/design/audit-log.ja.md](docs/design/audit-log.ja.md)。
- **Linux では、未 flush を残して終わったことが終了コードで伝わらない** — `mount.pgfs` は**未 flush を
  残したままアンマウントすると `exit 4`** で終わる。ただし **`mount(8)` / `/etc/fstab` 経由の既定
  (デーモン化) では、親プロセスがマウント成立の時点で `0` を返して先に終了する**ので、**この `4` は
  呼び出し元に届かない**。**届くのは `--foreground` で起動したときだけ**である。**代わりの報告経路が
  4 つある**: `{prefix}mounts` に残る記録 (墓標) / 監査行 (`op = writeback_loss`) /
  **次回マウント時の警告** / `pgfsctl status` の喪失表示。**Windows (`assign.pgfs`) では `exit 4` が
  そのまま届く。** ただし**この 4 経路はすべて DB に書く**ので、**flush できなかった理由が DB 障害
  そのものだったときは、どれも書けない** (喪失はログの Error 行にしか残らない。既定のログ出力は
  stderr なので、デーモン化していると誰にも届かない)。write-back を使うなら `--log-output` で
  ファイルにも残すこと。
- **GUI は読み取り専用** — 設定変更は `pgfsctl config set` を使う。
- **`mkfs` を再実行すると、DB に保存した設定が上書きされる** — `--clean` を付けなくても、
  `audit.enabled` / `app.plperlu` / `app.statfs` / `database.citus` / tablespace / `file_system.*` /
  `fallback_*` を**その実行の CLI 指定か既定値で**書き直し、`pgfs.toml` も生成物で置き換える。
  元の作成時と同じオプションを全部付けないと、**監査が黙って止まる・拒否したはずの plperlu が入る**。
  既存 FS に手を入れるときは `mkfs` を流さず、必要な DDL を直接流すこと (上 §移行)。
- **`-o allow_other` だけでは権限が強制されない** — pgfs はアクセス可否を自分では判定せず、
  `default_permissions` でカーネルに委ねる。**`allow_other` を付けるなら `default_permissions` も
  付けること** (付けないと全ローカルユーザーが全ファイルを読み書きできる)。付け忘れは起動時に警告する。
- **制御チャネルに認証が無い** — `pgfsctl config set` / `status` が使う NOTIFY は、**同じ DB に接続できる
  任意のロール**が撃てる。そのため DB に接続できる人は、全マウントの Live 設定 (`audit.enabled` など) を
  `{prefix}settings` に痕跡を残さずに変えられる。**DB への接続権限そのものを、マウントを操作できる人に
  限ること。**
- **`pgfsctl config set` は全マウントに一斉に効く** — 対象のマウントを選べない。とくに
  **`mount.write_back` を live で on にすると、稼働中の全マウントが write-back になり、上の「同じファイルを
  複数マウントから書かない」の条件にそのまま入る**。また `database.tablespace*` / `database.citus` /
  `app.plperlu` / `database.shard_*` のような **mkfs でしか使わない項目**にも「次回マウントで反映」と
  答えるが、実際にはどこにも効かない。
- **write-back の flush は、ハードリンク兄弟へのサイズ・時刻の配布を手元のキャッシュの `st_nlink` で決める** —
  キャッシュから落ちている / 別マウントでリンクを張られてキャッシュが古い、のときは兄弟に配らない
  (write-through の経路は DB の値で判定するので漏れない)。
- **Windows では名前の大文字小文字を区別しない** — DB と Linux は区別するので、Linux で `README` と
  `readme` を両方作ると、Windows からはワイルドカード検索で両方が当たり、開く・消す対象を取り違え得る。
  **大文字小文字だけ違う名前を共存させないこと。**
- **Windows から ACL を編集すると、Windows で解決できない所有者 / グループ / 名前付き ACL が化ける** —
  解決できない principal は全部 `ANONYMOUS LOGON` に投影され、SetFileSecurity が mode と名前付き ACL を
  書き直すので、**Linux 側の group のビットや `setfacl` のエントリが変わり得る**。両 OS の利用者名が
  揃っていない環境では、**権限の変更は Linux 側で行うこと。**
- **OS をまたぐと扱えない名前がある** — Linux の不正な UTF-8 のバイト列の名前は U+FFFD に潰れて
  **別の名前と衝突する** (Shift_JIS の zip の展開など)。`:` や `\` を含む名前は Windows から開けない
  か、別の名前に化ける。UTF-8 で 1024 バイトを超える名前が 1 つあると、**そのディレクトリの `ls` が
  Linux で EIO になる** (Windows からは作れる)。
- macOS 未対応。

## [v0.1.0] - 2026-06-06

最初の公開版。`mkfs.pgfs` / `mount.pgfs` (Linux・FUSE) / `assign.pgfs` (Windows・Dokan) の三本柱、
bytea チャンクによるデータ格納、監査ログ、Citus 水平分散、POSIX ACL と Windows ACL の相互運用、
`df` 対応、xattr バイト列透過、`/etc/fstab` 対応。
