# pgfs Linux e2e tests

> **道順**: [docs/README.ja.md](../../docs/README.ja.md) › [docs/tests.ja.md](../../docs/tests.ja.md) (テストのハブ) › **本書**
>
> 全テストの一覧 / 環境要件 / docker 統合の検討は [docs/tests.ja.md](../../docs/tests.ja.md) (ハブ) を参照。本 README はこのディレクトリのランナー (`e2e.sh` / `flow.ps1` / `run.cmd`) の操作詳細を扱う。

mount.pgfs (Linux) でマウント済みの PGFS に対して、実装済み機能 ([docs/Mount.ja.md](../../docs/Mount.ja.md)) を一括で動作確認する e2e テスト。

## ファイル

| ファイル | 内容 |
|---|---|
| [e2e.sh](e2e.sh) | bash テスト本体 (Linux 上で実行・**マウント済み前提**) |
| [writeback.sh](writeback.sh) | **write-back 専用** (9 件)。`mount.write_back` の耐久性契約を見るため**自分でマウントを張り替える** |
| [wbmeta.sh](wbmeta.sh) | **メタデータ write-back 専用** (**27 件**・ラウンド A 修正 + B-1〜B-9 後も全件緑)。`mount.write_back_metadata` の契約。自分でマウントを張り替え、DB 側は psql で直接確認する |
| [startup.sh](startup.sh) | **起動まわりの契約専用** (10 件)。`-o` の扱い / `started (pid N)` / 所有者解決の fallback。自分でマウントを張り替える |
| [prune.sh](prune.sh) | **`pgfsctl prune` 専用** (11 件)。異常終了が残したものの掃除。**psql 必須** (人工の残骸を作るため)。自分でマウントを張り替え、`kill -9` で本物の `.fuse_hidden` 残骸も作る |
| [handles.sh](handles.sh) | **ハンドルリーク専用** (**11 件**)。`pgfsctl status` の `handles` 行を見て、借りたハンドルが必ず返っていることを確認する。**psql 必須** (スナップショットを ping で撃たせるため)。自分でマウントを張り替える |
| [crossclient.sh](crossclient.sh) | **cross-client 専用** (**18 件**)。同じ DB-FS を **2 マウント**して可視性を見る (Windows の [crossclient.ps1](../windows/crossclient.ps1) の Linux 版)。自分で 2 つマウントを張る |
| [negcache.sh](negcache.sh) | **negative lookup キャッシュ専用** (7 件)。`mount.negative_cache_ttl_ms` の可視性契約。自分でマウントを張り替え、他クライアントは psql 直接 INSERT で代用 |
| [run.cmd](run.cmd) | テスト**だけ**実行 (マウント済み前提) |
| [flow.ps1](flow.ps1) | **全フロー**: rsync → publish → mount → test → unmount (PowerShell) |
| [flow.cmd](flow.cmd) | `flow.ps1` の cmd ラッパー |

## 0 件で緑にしない (2026-09-21 追加)

**どのスイートも「1 件も走らなかった」「1 件も PASS しなかった」なら `exit 1`** にしてある
(Windows 側の [tests/windows/README.ja.md](../windows/README.ja.md) §0 件で緑にしない と同じ形)。
**「落ちなかった」と「確かめた」は違う**のを、スクリプト側で守るため。

塞いだ穴は実測した 2 つ。**どちらも塞ぐ前は exit 0** だった:

| 形 | 塞ぐ前 | 今 |
|---|---|---|
| `TEST_FILTER` のタイポ | `TEST_FILTER=zzz_nonexistent bash tests/linux/startup.sh` → `0 passed, 0 failed, 0 skipped (out of 0)` / **exit 0** | `1 件も実行されませんでした (TEST_FILTER='zzz_nonexistent' が何にも一致していない?)` / **exit 1** |
| 前提不足で全件 skip | `PGFS_PSQL` 無しで `wbmeta.sh` の DB 依存テスト → `0 passed, 0 failed, 1 skipped` / **exit 0** | `1 件も PASS しませんでした (1 件 skip = 環境不足であってテストの成功ではない)` / **exit 1** |

**一部だけ skip は従来どおり成功扱い** (`e2e.sh` の `test_fallback_uname_gname` のように、環境によって
必ず skip になるものがあるため。49 passed / 1 skipped → exit 0)。

### psql の前提はスイートごとに違う (2026-09-21 実測)

`psql` が**対話シェルの alias にしか無い**環境では、`bash tests/linux/*.sh` から素の `psql` は引けない。
**そのときの挙動はスイートごとに違う**ので、`PGFS_PSQL=/usr/local/pgsql/bin/psql` を常に渡すのが安全:

| スイート | `psql` を引けないとき |
|---|---|
| `prune.sh` / `handles.sh` / `negcache.sh` | **前提チェックで `exit 2`** (1 件も走らない。黙って緑にはならない) |
| `wbmeta.sh` | **前提チェックが無い**。走りはするが **DB 側の検査が個別に `skip` へ落ちる** — 全体は緑に見えるのに、確かめているのは FS 側だけになる |
| `e2e.sh` / `writeback.sh` / `crossclient.sh` / `startup.sh` | `psql` を使わない (`startup.sh` は fallback の 1 件だけ skip) |

### writeback.sh の使い方

`e2e.sh` と違い**マウントを自分で張り替える** (fsync/close 後に `kill -9` してデータが残るかを見るため)。
対象 FS は壊してよいものを指すこと (触るのは `$MOUNT_ROOT/wbtest` 配下だけ)。

```bash
bash tests/linux/writeback.sh
PGFS_SETTING_FILE=~/pgfs_test.toml MOUNT_ROOT=~/mnt/pgfs bash tests/linux/writeback.sh
TEST_FILTER=fsync bash tests/linux/writeback.sh
```

環境変数: `PGFS_SETTING_FILE` (既定 `$HOME/pgfs_test.toml`) / `MOUNT_ROOT` (既定 `$HOME/mnt/pgfs`) /
`PGFS_BIN` (既定 `./bin/Debug`) / `TEST_FILTER`。

`e2e.sh` 側は **`write_back` on / off の両モードで同じ結果になるべき**なので、両方で回すこと
(マウント時に `--write-back` を付けるかどうかだけの違い)。

> ⚠ **bool フラグは値を消費しない**。`--write-back true` と書くと `true` が positional 引数に落ち、
> 2 個並べると positional[1] が `mount.mount_point` に化けてマウントが失敗する (`fuse: failed to
> access mountpoint true`)。**必ず裸のフラグで渡す** (`--write-back --write-back-metadata`)。

### テストを書くときに踏んだ罠 (Linux)

**テストが嘘をつく形**を 2 つ実際に踏んだので残しておく。

- **`psql -At` でも `INSERT 0 1` のコマンドタグが付いてくる。** `returning id` の戻りを
  そのまま使うと `"NNN\nINSERT 0 1"` になり、次の `where id = ...` が**構文エラー**になる。
  `q()` はエラーを握り潰して空文字を返すので、**「行が無い」と誤判定**する。
  **`| head -1` を通すこと。** 実害は `prune.sh` で出た — **live ゲートのテストが嘘の FAIL**、
  **孤児 data 削除のテストが嘘の PASS**。**後者のほうが危ない** (通ったことにして先へ進む)。
- **`pgrep -x mount.pgfs | head -1` で自分のデーモンの pid を取らない。** 別のマウントポイントで
  動いているデーモン (crossclient の B 側、消し忘れ) を掴み、**以降の `status` 読みが丸ごと他人の行**
  になる。`handles.sh` でこれを踏んで**6 件が謎の FAIL** になった。
  **`{prefix}mounts` から「このマウントポイントの最新行」で引くこと。**

- **「dirty を持ったまま」を bash から作れない。** write-back の cross-client を測るテストは
  **書いたあと flush させずに相手を動かす**必要があるが、bash の素直な書き方は 2 つとも壊れる:
  - **`exec 8> file` は O_TRUNC 付き。** 開いた瞬間にファイルが 0 になり、**シナリオが丸ごと崩れたまま緑**になる。
  - **`exec 8<> file` + `printf >&8` は切らないが、リダイレクトが複製した fd を閉じる。**
    **flush はファイル単位**なので、その close で **dirty が全部 flush される**。
  - **確認のために同じファイルを開くのも同じ罠。** `head -c 4 "$f"` を挟んだだけで dirty が消え、
    「dirty を作れていない」と誤判定した。**未 flush の確認は相手マウントと DB だけで行う。**
  - **対策は python から `os.open(path, os.O_WRONLY)` + `os.pwrite` して fd を持ったまま待つこと**
    (`crossclient.sh` の `test_xc_writeback_flush_does_not_undo_remote_truncate` が実例)。
  - Windows 側も**同じ形**を踏んでいる (`.NET FileStream` の既定 4096B バッファで小さい書き込みが
    `Dispose` まで FS に届かない)。詳細は [tests/windows/README.ja.md](../windows/README.ja.md) の
    §テストを書くときに踏んだ罠 (Windows)。**共通の教訓は「書いたつもりが FS に届いていない」で、
    どちらもテストが緑になる向きに転ぶ**こと。
- **Citus では `inode` と `data_chunk` を join できない。** 分散キーが違うので
  `complex joins are only supported when all distributed tables are co-located ...` で弾かれる。
  `q()` はエラーを握り潰して**空文字を返す**ので、**前提チェックが常に落ちる / 常に通る**形になる。
  **2 回に分けて引くこと** (`data_id` を引いてから chunk を引く。`PruneAdmin.ScanOrphanData` が
  同じ理由で 2 クエリに分けている)。

**どちらも「テストが落ちたのにコードは正しい」**形で、いちばん時間を取られる。
**落ちたらまず手で同じことを撃ってみる**のが早い。

### startup.sh の使い方

`mount.pgfs` の**起動時の契約**を見る。マウントを自分で張り替える。

```bash
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/startup.sh
```

環境変数: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` /
`PGFS_PSQL` (`PSQL` も可・fallback のテストにだけ使う。引けなければその 1 件だけ skip)。

内訳 (4 件):

| テスト | 何を見るか |
|---|---|
| `test_dash_o_max_write_maps_to_field` | `-o max_write=N` を libfuse へ**転送しない** (転送すると `fuse_new` 失敗でマウントごと落ちる) |
| `test_dash_o_unapplied_options_warn` | `noexec` / `sync` / `dirsync` は**警告する**。`nosuid` / `nodev` / `relatime` には**警告しない** (ノイズになる) |
| `test_dash_o_fuse_breaking_options_are_not_forwarded` | `max_read` / `max_readahead` を libfuse へ転送しない。**マウントできただけでは不十分**で、2 秒待って**まだ生きている**ことまで見る (`max_read` は成功を返した直後にセッションが落ちて exit 0 で消えるため) |
| `test_started_pid_is_the_daemon` | `started (pid N)` が**実デーモン**の pid (フォーク前の親ではない) |
| `test_unknown_owner_falls_back` | 解決できない uname/gname が fallback (65534) に落ちる = `getpw*_r` の「見つからない」をエラーと取り違えていない |

### crossclient.sh の使い方

同じ DB-FS を **2 マウント** (`MOUNT_ROOT` = A / `MOUNT_ROOT2` = B) して、片方の変更が
もう片方から見えるかを確認する。触るのは各マウントの `xctest` 配下だけ。

```bash
bash tests/linux/crossclient.sh
MOUNT_ROOT2=~/mnt/pgfs2 PGFS_XC_WAIT=20 bash tests/linux/crossclient.sh
TEST_FILTER=hardlink bash tests/linux/crossclient.sh
```

環境変数: `PGFS_SETTING_FILE` / `MOUNT_ROOT` (A・既定 `$HOME/mnt/pgfs`) /
**`MOUNT_ROOT2`** (B・既定 `$HOME/mnt/pgfs2`) / `PGFS_BIN` / `TEST_FILTER` /
**`PGFS_XC_WAIT`** (可視性を待つ上限秒・既定 10)。

> ⚠ **両マウントを `--notify` で起動する**。cross-client の可視性は
> `database.notify_enabled` に完全依存で、既定 (false) では他マウントの作成 / 上書き /
> 削除 / 置換 rename が**いつまでも見えない** ([Assign.ja.md](../../docs/Assign.ja.md) の注記と同じ)。
> スクリプトが自分で付けるので呼び出し側の設定は要らない。

内訳 (7 件):

| 群 | 件数 | テスト |
|---|---|---|
| 可視性 | 4 | `test_xc_create_visible` / `test_xc_delete_visible` / `test_xc_overwrite_visible` / `test_xc_rename_replace_visible` |
| **ハードリンクの実体共有** | 2 | `test_xc_hardlink_size_propagates` / `test_xc_hardlink_truncate_propagates` — **配布した兄弟 id を通知に載せる修正の回帰ガード** ([data-id-lifecycle.ja.md](../../docs/design/data-id-lifecycle.ja.md))。**B 側で先に stat してキャッシュに載せてから A で書く**のが要点で、載せずに書くと B は DB から読み直すので不具合を素通りする |
| 排他 | 1 | `test_xc_exclusive_create_races` — 同名 `O_EXCL` を A/B から同時に撃って**成功が 1 件だけ**であることを 5 ラウンド |
| **孤児化の防御** | 1 | `test_xc_create_under_removed_parent_fails` — 他クライアントが消したディレクトリの下に **create / mkdir / ln -s / ln の 4 経路すべて**で子を作れないこと。**ENOENT であること**まで見る (「失敗すればよい」だと EEXIST でも通ってしまい、実際に見落とした)。**この 1 件だけ notify OFF で張り直す** (ON だと rmdir 通知で A のキャッシュが落ちて窓が閉じる) |

**可視性は必ずポーリングで待つ** (`wait_until`)。待たずに assert すると「遅い」を
「壊れている」と誤判定する — Windows 側で実際にこの形の誤検出を 2 件踏んでいる。

**起動時に通知チャネルの事前確認をする** (`preflight_notify`)。繋がっていない状態で可視性テストを
回すと 4 件が揃って落ちるが、症状は「見えない」としか言わないので**通知の未接続を可視性バグと
誤読する** (Windows 側が実際に踏んだ)。**`connected` だけを見てはいけない** — 制御チャネルの LISTEN は
`database.notify_enabled` に関係なく常時張られるので、**notify OFF でも `connected: true` になる**
(実測)。**`data_enabled` と両方**を見ること。psql が引けない環境では確認をスキップする
(テスト自体は走らせる)。

### negcache.sh の使い方

negative lookup キャッシュ (`mount.negative_cache_ttl_ms`) 専用。マウントを自分で張り替え、
**他クライアントの役は psql の直接 INSERT で代用**する。触るのは `$MOUNT_ROOT/negcache` 配下だけ。

```bash
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/negcache.sh
```

環境変数: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` +
**`PGFS_PSQL`** (`PSQL` も可)。

> ⚠ `psql` が**対話シェルの PATH にしかない**環境 (エイリアスや `.bashrc` の PATH 追加) では、
> 非対話で走るこのスクリプトから引けず、前提チェックで `psql で DB に接続できません` と出て
> **1 件も実行されないまま exit 0 で終わる**。パスを明示すること。

### wbmeta.sh の使い方

メタデータ write-back (`mount.write_back_metadata`) 専用。`writeback.sh` と同じくマウントを張り替え、
さらに **DB 側の整合 (pending の materialize 結果) を psql で直接確認する**。触るのは
`$MOUNT_ROOT/wbmeta` 配下だけ。

```bash
bash tests/linux/wbmeta.sh
PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/wbmeta.sh
TEST_FILTER=truncate bash tests/linux/wbmeta.sh
```

環境変数: `PGFS_SETTING_FILE` / `MOUNT_ROOT` / `PGFS_BIN` / `TEST_FILTER` +
**`PGFS_PSQL`** (既定 `psql`。DB 確認テストはこれが引けないと skip)。

内訳 (27 件):

| 群 | 件数 | テスト |
|---|---|---|
| ステージ 1 (pending 台帳の整合) | 4 | `test_pending_same_name_eexist` / `test_pending_truncate_zero_consistency` / `test_pending_pin_does_not_blow_cache` / `test_pending_mkdir_visible_in_ls` |
| 耐久性契約 (on) | 3 | `test_meta_fsync_survives_crash` / `test_meta_close_only_is_lost_on_crash` (**「消える」ことを assert**) / `test_meta_fsyncdir_persists_pending_children` |
| ヒューリスティック (a) rename-over-existing | 2 | `test_meta_rename_over_existing_pending_source` / `..._excl_source` |
| ヒューリスティック (b) truncate → 同期 close | 2 | `test_meta_otrunc_close_is_synchronous` / `test_meta_truncate_syscall_close_is_synchronous` (**2026-09-19 のラウンド A 修正で緑化**。それ以前は未修正の不具合の再現テストだった) |
| ヒューリスティック (c) O_EXCL は write-through | 1 | `test_meta_exclusive_create_is_write_through` |
| 回帰 (予約 data_id の read) | 1 | `test_meta_read_after_close_no_flush` |
| B-9 (実効モード) | 1 | `test_meta_live_flip_publishes_effective_mode` (二相 flip の受付停止中が status に出る。**flip を意図的に遅くする**ため 800 件の pending を抱えさせる) |
| B-7 (破壊操作の遮断) | 1 | `test_meta_error_state_blocks_destroy_but_allows_cancel` (**persisted の unlink/truncate が止まる**ことと **pending の unlink が通る**ことを同じテストで見る) |
| B-6 (喪失レポート) | 1 | `test_meta_loss_report_has_paths_and_breakdown` (パス付き + 打ち切りの内訳。**pending ディレクトリを種別違いで衝突させて** 41 件の喪失を 1 回の injection で作る) |
| B-5 (エラーステートの即書き) | 1 | `test_meta_error_state_is_published_immediately` (heartbeat 周期 30 秒を待たずに `{prefix}mounts` へ載ること / 解除も即) |
| B-4 (偽の喪失を作らない) | 1 | `test_meta_audit_live_off_does_not_fake_loss` (audit を live off した後の正常 unmount で B-2 の墓標ができないこと。**現状は構造的に起きないので回帰ガード**) |
| B-3 (取り消し監査の即書き) | 1 | `test_meta_cancel_audit_is_written_immediately` (**unmount 前の時点で** DB に create/delete の 2 行があること。`audit.enabled` を一時的に on にして戻す) |
| B-2 (喪失の DB 記録) | 1 | `test_meta_loss_is_recorded_in_db` (**墓標が残る** / 次回マウントの**親** stderr に警告 / 墓標を消すと警告も消える) |
| B-1 ノブ (`write_back_metadata_exclusive_create`) | 3 | `test_meta_exclusive_create_defer_is_pending` / `..._same_mount_eexist` / `..._conflict_latches` (**占有者を消さないこと**と **unlink での回復**が主眼) |
| ハードリンク兄弟 (ラウンド A の A-1) | 1 | `test_meta_hardlink_sibling_survives_unlink` (**兄弟経由の write が unlink で無音に消えない**こと。`>>` で書き fd を開いたまま `ln` / `rm` を挟む) |
| 二相 flip / エラーの底 | 3 | `test_meta_live_off_flushes_pending_then_write_through` / `test_meta_backpressure_blocking_inodes` / `test_meta_error_state_blocks_and_clears` |

注意点:

- **pending を作るには「`O_EXCL` も `O_TRUNC` も付かない create」が必要**。`cp` は新規宛先を
  `O_CREAT|O_EXCL` で開く (strace で確認) ためヒューリスティック (c) で write-through になり、
  `> file` は `O_TRUNC` 付き。テスト内では python の `os.open(p, O_CREAT|O_WRONLY)`
  (`plain_create`) と `mkdir` を使う。
- `test_meta_error_state_blocks_and_clears` は **psql で「種別違いの同名行」を直接 INSERT** して
  flush を恒久失敗させる (fault injection)。行は `created_by = 'wbmeta-test'` で印を付け、
  **EXIT trap (`cleanup_injection`) で必ず削除**する。途中で kill された場合の手動確認は
  `select * from <schema>.<prefix>inode where created_by = 'wbmeta-test'`。
- `insert ... returning` を `psql -At` で撃つと `INSERT 0 1` のステータス行も stdout に来るので、
  値が要るときは CTE (`with ins as (insert ... returning id) select id from ins`) で包む。

## シナリオの方針

- テスト開始時に `$MOUNT_ROOT/test` を `rm -rf` してから `mkdir`
- 全テストは `$MOUNT_ROOT/test/` 配下でのみ実行する
- 各テストはユニークな接頭辞 (`t01_` / `t02_` ...) を使い相互非干渉
- 最後 (正常終了でも失敗でも) に `$MOUNT_ROOT/test` を `rm -rf`

## カバー範囲

[docs/Mount.ja.md](../../docs/Mount.ja.md) で ✅ になっている全 FUSE オペレーションを exercise する。

| カテゴリ | テスト |
|---|---|
| **ディレクトリ操作** | mkdir/rmdir、ネストディレクトリ、100 ファイルディレクトリ、非空 rmdir 拒否 |
| **ファイル基本** | touch/unlink、small write/read、append (O_APPEND)、O_TRUNC で上書き |
| **データ I/O (bytea)** | 2 MiB round-trip (チャンクまたぎ)、truncate 縮小/伸長/ゼロ |
| **名前変更** | 通常 mv、サブディレクトリへの mv |
| **権限** | chmod (ファイル/ディレクトリ)、chown 自分自身 |
| **シンボリックリンク** | 相対パス、dangling、絶対パス |
| **ハードリンク** | 基本、片方削除後の生存、サブディレクトリまたぎ |
| **xattr** | set/get、list、remove、上書き |
| **メタデータ** | StatFS (df)、utime (touch -d / UTIME_NOW) |
| **並行性** | 異なるファイルへの並列書き込み、同じファイルからの並列読み出し、並列 mkdir |
| **名前解決 fallback** | DB 直接 INSERT で存在しない uname/gname の inode を作って `stat` が `nobody`/`nogroup` を返すこと (`ssh pgsql_server` で psql 経由、未到達なら SKIP) |

## 使い方

### A. 全フロー (推奨)

`flow.cmd` または `flow.ps1` が `rsync → publish → mount → test → unmount` を一括実行します。

```cmd
tests\linux\flow.cmd
```

PowerShell から:

```powershell
.\tests\linux\flow.ps1
```

オプション (どちらの呼び方でも同じ):

| オプション | 動作 |
|---|---|
| `xattr` (位置引数) | テスト名フィルタ。例: `flow.cmd xattr` で xattr 系 4 件だけ |
| `-NoSync` | rsync をスキップ (既に同期済み) |
| `-NoBuild` | `dotnet publish` をスキップ |
| `-NoMount` | マウント済みとしてテストだけ |
| `-KeepMounted` | テスト後にアンマウントしない (調査用) |

組み合わせ可能:

```cmd
tests\linux\flow.cmd -NoBuild              REM rsync + mount + test + unmount
tests\linux\flow.cmd -NoSync -NoBuild      REM mount + test + unmount
tests\linux\flow.cmd hardlink -KeepMounted REM hardlink テスト後、マウント維持
```

接続先や パスを変えたい場合は環境変数または PowerShell パラメータ:

```powershell
.\tests\linux\flow.ps1 -Remote other-host -MountPoint /tmp/m
```

### B. テストだけ (マウント済み前提)

既に別ターミナルで mount.pgfs を稼働させている場合:

```cmd
tests\linux\run.cmd
tests\linux\run.cmd xattr
```

### C. 手動フロー (各ステップを個別に)

下記は環境変数の意味例。実際のパスは `$REMOTE` (ホスト) / `$REMOTE_REPO` (リモート checkout dir) / `$REMOTE_DOTNET` / `$REMOTE_SETTING_FILE` / `$MOUNT_POINT` を各環境に合わせて設定する。

```cmd
REM 1. 同期
bash -c "rsync -avz --delete ./ $REMOTE:$REMOTE_REPO/"

REM 2. ビルド
bash -c "ssh $REMOTE $REMOTE_DOTNET publish $REMOTE_REPO/pgfs.sln"

REM 3. (別ターミナル) マウント
bash -c "ssh $REMOTE $REMOTE_REPO/bin/Publish/mount.pgfs --setting-file $REMOTE_SETTING_FILE --mount-point $MOUNT_POINT"

REM 4. テスト
tests\linux\run.cmd

REM 5. アンマウント
bash -c "ssh $REMOTE fusermount3 -u $MOUNT_POINT"
```

## テストの絞り込み

第 1 引数にフィルタ文字列を渡すと、テスト名に含まれるものだけ実行する:

```cmd
tests\linux\run.cmd xattr          # xattr 系 4 テスト
tests\linux\run.cmd hardlink       # ハードリンク 3 テスト
tests\linux\run.cmd concurrent     # 並行アクセス 3 テスト
```

## 環境変数 / パラメータ

### run.cmd (テストだけ)

| 変数 | 既定 |
|---|---|
| `REMOTE` | `linux_client` |
| `REMOTE_REPO` | `~/project/pgfs_cs` (実値はホスト個別) |
| `MOUNT_POINT` | `~/mnt/pgfs` (同上) |

例:

```cmd
set REMOTE=other-host
set MOUNT_POINT=/tmp/pgfs_mount
tests\linux\run.cmd
```

### flow.ps1 (全フロー)

CLI パラメータと環境変数の両方で設定可能 (優先順位: **CLI 引数 > 環境変数 > 既定**)。環境変数は `run.cmd` と共通のものは同名。

| パラメータ | 環境変数 | 既定 | 用途 |
|---|---|---|---|
| `-Remote` | `REMOTE` | `linux_client` | SSH 接続先 |
| `-RemoteRepo` | `REMOTE_REPO` | `~/project/pgfs_cs` (実値はホスト個別) | リモート側リポジトリパス |
| `-MountBinary` | `REMOTE_MOUNT_BINARY` | `${RemoteRepo}/bin/Publish/mount.pgfs` | mount.pgfs バイナリパス |
| `-MountPoint` | `MOUNT_POINT` | `~/mnt/pgfs` (実値はホスト個別) | マウントポイント |
| `-SettingFile` | `REMOTE_SETTING_FILE` | `~/pgfs.toml` (実値はホスト個別) | 設定ファイルパス |
| `-DotnetPath` | `REMOTE_DOTNET` | `~/dotnet/10.0.300/dotnet` (実値はホスト個別) | dotnet バイナリ |
| `-Filter` (位置 0) | - | (なし) | テスト名フィルタ |
| `-NoSync` | - | - | rsync スキップ |
| `-NoBuild` | - | - | publish スキップ |
| `-NoMount` | - | - | マウント済み前提でテストだけ |
| `-KeepMounted` | - | - | テスト後にアンマウントしない |

例 (環境変数で別ホストに向ける):

```powershell
$env:REMOTE = "other-host"; $env:MOUNT_POINT = "/tmp/pgfs_mount"
.\tests\linux\flow.ps1 -NoBuild
```

## Linux 上で直接実行する場合

```bash
bash tests/linux/e2e.sh /mnt/pgfs
TEST_FILTER=xattr bash tests/linux/e2e.sh /mnt/pgfs
```

### 環境変数

| 変数 | 用途 |
|---|---|
| `TEST_FILTER` | テスト名に部分一致するものだけ実行 |
| `PGFS_TEST_PG_EXEC` | `test_fallback_uname_gname` が使う psql 呼び出し全文をオーバライド (既定: pgsql_server 上のソースビルド psql を ssh 経由で叩く形)。docker Citus に向けるときは `docker exec -i <container> psql ...` を設定する。[tests/citus/race_multinode.sh](../citus/race_multinode.sh) が使用 |

## 出力例

```
=== mount.pgfs ===
  mounted at /mnt/pgfs

=== e2e tests ===
=== pgfs Linux e2e tests ===
Mount root: /mnt/pgfs
Test root:  /mnt/pgfs/test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...
PASS: test_concurrent_mkdir_diff_dirs
PASS: test_fallback_uname_gname

===========================================
Results: 35 passed, 0 failed, 0 skipped (out of 35)

=== unmount ===
  unmounted
  mount log: tests\linux\mount.log (6 lines)

  ALL PASSED
```

## 現状

**件数と最新の実行結果は [docs/tests.ja.md](../../docs/tests.ja.md) を正とする** (このファイルに二重に持つと必ず片方が腐るため)。
は e2e が **43 passed / 0 failed / 1 skipped (44 件)**、`wbmeta.sh` が **27/27**。

過去の記録: は 35 件で **35 passed / 0 failed / 0 skipped** (単 PG モード + 1 ノード Citus +
多ノード Citus on docker、いずれも 35/35。POSIX ACL `test_posix_acl_named_user` を追加)。
多ノード Citus 検証は [tests/citus/race_multinode.sh](../citus/README.ja.md) 経由。

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 全テスト成功 |
| 1 | 一つ以上失敗 / **1 件も実行されなかった** / **1 件も PASS しなかった** (上記 §0 件で緑にしない) |
| 2 | `MOUNT_ROOT` が存在しない (マウントされていない) |
| 3 | `TEST_ROOT` を作成できない (mount が書き込み不可) |

## 前提パッケージ

xattr 系テストで `attr` パッケージ (`getfattr` / `setfattr`) を使用。未インストールの場合、xattr テストは `SKIP` 表示で飛ばされる:

```bash
sudo apt install attr   # Debian/Ubuntu
sudo dnf install attr   # Fedora/RHEL
```

## カバーしていないもの

[docs/Mount.ja.md](../../docs/Mount.ja.md) の TODO 表の ❌ 項目は未実装のため対象外:

- macOS 動作確認 (libfuse の macOS 対応次第)
- Access チェック (`Access` 操作)
- Mount オプション `-o` (フル対応)

未実装機能の残一覧は [docs/next.ja.md](../../docs/next.ja.md) を参照。
