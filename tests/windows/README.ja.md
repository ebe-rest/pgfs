# pgfs Windows e2e tests

> **道順**: [docs/README.ja.md](../../docs/README.ja.md) › [docs/tests.ja.md](../../docs/tests.ja.md) (テストのハブ) › **本書**
>
> 全テストの一覧 / 環境要件 / docker 統合の検討は [docs/tests.ja.md](../../docs/tests.ja.md) (ハブ) を参照。本 README はこのディレクトリのランナー (`e2e.ps1` / `flow.ps1` / `run.cmd`) の操作詳細を扱う。

assign.pgfs (Windows / Dokan) でマウント済みの PGFS に対して、実装済み機能 ([docs/Assign.ja.md](../../docs/Assign.ja.md)) を一括で動作確認する e2e テスト。

Linux 版 ([tests/linux/](../linux/README.ja.md)) と対になる Windows 版で、テストの構造はほぼ同じ。Windows 固有の操作 (ReadOnly / Hidden / System / Archive 属性 / ボリューム情報 / ワイルドカード検索) を追加し、pgfs の Dokan アダプタで公開していない native symlink / hardlink / 任意 xattr と POSIX chmod/chown は対象外。Windows 自体にリンク機能がないという意味ではない。

## ファイル

| ファイル | 内容 |
|---|---|
| [e2e.ps1](e2e.ps1) | テスト本体 (PowerShell) |
| [run.cmd](run.cmd) | テスト**だけ**実行 (マウント済み前提) |
| [flow.ps1](flow.ps1) | **全フロー**: (任意ビルド) → mount → test → unmount |
| [flow.cmd](flow.cmd) | `flow.ps1` の cmd ラッパー |
| [crossclient.ps1](crossclient.ps1) | **2 マウント同時**の排他・可視性テスト (自分で 2 つマウントして片付けるまで行う) |
| [writeback.ps1](writeback.ps1) | **write-back on** の耐久性テスト (自分でマウントし、強制終了 → 再マウントして内容を確認する) |
| [wbmeta.ps1](wbmeta.ps1) | **metadata write-back + B-1 ノブ** の契約テスト (2 マウントで占有者を作る) |
| [control_plane.ps1](control_plane.ps1) | **`pgfsctl config` / `status`** の受入 (live 反映・実効値・二相 flip) |
| [prune.ps1](prune.ps1) | **`pgfsctl prune`** の通し (C-2 の保持 → kill → 孤児 data → 掃除) と、**利用者が付けた `.fuse_hidden*` という名前のファイルを消さないこと**。**自分でマウントして kill する** |
| [wbcross.ps1](wbcross.ps1) | **write-back のマウントが dirty を抱えている間に他マウントが同じ実体を書いたとき**の契約 (2 マウント)。truncate の巻き戻りが起きないこと + **チャンク単位の踏み潰しが起きること** (後者は**望ましい挙動ではなく現状を固定する契約テスト**) |

## cross-client テスト (2 マウント)

```powershell
pwsh -NoProfile -File tests\windows\crossclient.ps1              # P: と R: にマウントして全件
pwsh -NoProfile -File tests\windows\crossclient.ps1 -MountB S:   # 空いているドライブレターを指定
pwsh -NoProfile -File tests\windows\crossclient.ps1 -NoNotify    # notify 無し (可視性テストは SKIP)
```

同じ DB-FS を 2 つの `assign.pgfs` でマウントし、**単一マウントでは見られない契約**を見る:

- **排他** (notify の有無に関係なく実行): 同時 `CREATE_NEW` で成功は必ず 1 つ (勝者の内容も壊れない) / 同時 `mkdir` で実体が 1 つ
- **可視性** (`--notify` 付きで起動したときのみ実行): 片方の create / 上書き / delete / 置換 rename がもう片方から見える
- **ハンドル文脈** (handle-context 段階 B・`--notify` 必須): 開いたままの **append ハンドル**が相手マウントの伸長を見ていること (見ていないと**相手が書いたぶんを上書きして壊す** = 問題 2 の再現) / rename + 同名再作成を跨いでも最初の inode を指すこと (問題 1 のガード)

**`database.notify_enabled` は既定 false** で、その構成では他マウントの変更は**見えないのが仕様**
(各マウントの `InodeCache` / read キャッシュは独立で、invalidate は LISTEN/NOTIFY でしか来ない)。
そのためスクリプトは自分が起動するマウントに `--notify` を付け、付けられない場合 (既存マウントの再利用 / `-NoNotify`) は
可視性テストを **SKIP** する。実測でも notify 無しでは 4 件とも「古いまま見える」状態だった。

ドライブレターは**空いているものを指定すること** (既定の `R:` が埋まっていると Dokan が
`Something's wrong with the Dokan driver` で起動に失敗する)。

### ハンドル文脈のテストを足すときの注意

- **.NET の `FileMode.Append` は使えない** — FileStream が自分で末尾へシークして**明示オフセットで書く**ので、
  Dokan には `WriteToEndOfFile = false` で降りてくる。**ハンドルが握っている属性の鮮度**を見たいときは
  Win32 `CreateFileW` を **`FILE_APPEND_DATA` だけ**で開いて OS に末尾を決めさせる (スクリプトは P/Invoke している)。
- **`[uint64]0x8000000000000000` とは書けない** — PowerShell は **16 進リテラルを符号付きで読む**ので
  (`0x80000000` は Int32 の -2147483648、`0x8000000000000000` は Int64 の最小値)、uint へのキャストで落ちる。
  **`[Convert]::ToUInt64("8000000000000000", 16)` か、末尾に `L` を付けて Int64 にしてからキャスト**する。
  3 回踏んだ。
- **相手マウントから変更する**こと。同一マウント内だと `InodeCache` の**同じ `Inode` インスタンス**が
  更新されてハンドル側も一緒に新しくなり、**検出力がゼロ**になる。
- **長さの観測は列挙 (`Get-ChildItem -Filter`) で行う**。`Get-Item` / `Test-Path` は Windows
  クライアント側の FCB に答えられることがある (`Wait-Gone` と同じ理由)。

## 0 件で緑にしない (2026-09-21 追加)

**どのスイートも「1 件も走らなかった」「1 件も PASS しなかった (= 全件 skip)」なら exit 1** にしてある。
`-Filter` のタイポや前提不足で**緑のまま通過**すると、**何も確かめていないのに通ったように見える**からである。
**一部だけ skip は従来どおり成功扱い** (環境によって必ず skip になるものがあるため)。

**Linux 側にも同じガードを入れた** ([tests/linux/README.ja.md](../linux/README.ja.md) §0 件で緑にしない)。
ただし **`psql` を引けないときの挙動はスイートごとに違う** (2026-09-21 に Linux 側で実測) —
`negcache.sh` / `prune.sh` / `handles.sh` は**前提チェックで `exit 2`** (黙って緑にはならない)、
`wbmeta.sh` は**走りはするが DB 側の検査が個別に `skip` へ落ちて緑に見える**。
どちらにせよ `PGFS_PSQL=/usr/local/pgsql/bin/psql` を明示するのが安全。
**「落ちない」と「確かめた」は違う**、を仕組みで守る形。

## テストを書くときに踏んだ罠 (Windows)

### `FileStream` の既定バッファで、書いたつもりが FS に届かない (2026-09-21 実測)

**`FileStream` の既定 `bufferSize` は 4096 なので、それより小さい `Write` は .NET のバッファに溜まり、
`Dispose` / `Flush` まで `WriteFile` が発行されない。** つまり **FS のコールバックは呼ばれていない**。

**「ハンドルを開いたまま dirty を持たせる」形のテストは、これで丸ごと成立しなくなる。**
write-back の検証で 4 バイトを書いてハンドルを保持したつもりが、実際に書き込みが降りるのは
`Dispose` の瞬間 = **他マウントの操作より後**で、**測りたかった競合が起きないまま緑になった**
(2026-09-21 に H-2 の実測で 3 シナリオ連続で偽陰性を出し、うち 1 つは「再現しない」と報告まで
してしまった)。

**`Write` の戻り値や `written=4` は「届いた」証拠にならない。** .NET のバッファに入っただけでも成功を返す。

対策:

- **P/Invoke で `CreateFileW` + `WriteFile` を直接叩く** (バッファを挟まない)。
- **届いたことをログで確認してから次の手順へ進む** — `database` の設定が `level = "all"` なら
  `WriteFileProxy : \<name> Return : Success NumberOfBytesWritten : N` が出るので、**手順の前後で
  件数を数える**。増えていなければ **INCONCLUSIVE として緑にしない**。

```powershell
$before = @(Select-String -Path $LogFile -Pattern "WriteFileProxy : \\$name Return").Count
# ... P/Invoke で書く ...
$after  = @(Select-String -Path $LogFile -Pattern "WriteFileProxy : \\$name Return").Count
if ($after -le $before) { Fail "書き込みが FS に届いていない (シナリオ不成立)"; return }
```

**Linux 側にも同型の罠がある** — bash の `exec 8> file` は **`O_TRUNC` 付き**なので、開いた瞬間に
ファイルが 0 になってシナリオが崩れる ([tests/linux/README.ja.md](../linux/README.ja.md))。
**共通の教訓は「書いたつもりが FS に届いていない」で、どちらもテストが緑になる向きに転ぶ。**

### 観測側が壊れていても「0 件」になる — negative control を先に置く (2026-09-21 実測)

**「イベントが 1 件も来ない」は「FS が通知していない」と「こちらが受け取れていない」を区別しない。**

FileSystemWatcher でリモート変更の種別を測ったとき、**ローカル操作ですら 0 件**になった。原因は
`Register-ObjectEvent -Action` の **ブロックが呼び出し元の script スコープを見ない**ことで、
`$script:Events` に書けていなかっただけである (**`$global:` を使う**)。
**このまま報告していれば「pgfs はイベントを出さない」という誤った結論になっていた。**

**対策は、確実にイベントが出る対象を先に測ること。** 通常の NTFS (`$env:TEMP` 配下) を同じ watcher で
監視し、**そこで 1 件も取れなければ測定を中止する** (`exit 2`)。取れてから pgfs を測る。

```powershell
$ctlEvents = Phase "NTFS: create file" { Set-Content (Join-Path $ctl "c.txt") "x" }
if ($ctlEvents -eq 0) { Say "control で 0 件 = watcher の配線が壊れている。測定を中止" "Red"; exit 2 }
```

**これは「届いたことを確認してから進む」の観測側版**である。書き込み側 (上の `FileStream`) と
観測側の両方に同じ穴があり、**どちらもテストが「異常なし」に見える向きに転ぶ**。

### 前提を作るオプションは呼び出し側に書かせない

**`--write-back-interval-ms 0` の付け忘れを 1 日に 4 回やった** (H-2 の 3 シナリオ + `exit 4` の測定)。
既定は 1000 ms なので、**背景 flush が先に dirty を書き切ってしまい、測りたい競合が起きない**。
**4 回とも「再現しない」という誤った結論に見えた**。

**注意では止まらないので、ヘルパに埋めて呼び出し側に書かせない**:

```powershell
# wbcross.ps1
function Start-MountHoldingDirty($point) {
	return Start-Mount $point "--write-back --write-back-interval-ms 0"
}
```

**「このテストが成立するための前提」をオプション文字列として毎回手で書く形になっていたら、そこが穴**である。
Linux 側も同じ理由で `crossclient.sh` の `mount_a_writeback()` に埋めてある。

## シナリオの方針

- テスト開始時に `$MountRoot\test` を再帰削除してから作成
- 全テストは `$MountRoot\test\` 配下でのみ実行する
- 各テストはユニークな接頭辞 (`t01_` / `t02_` ...) を使い相互非干渉
- 最後 (正常終了でも失敗でも) に `$MountRoot\test` を再帰削除

## カバー範囲

[docs/Assign.ja.md](../../docs/Assign.ja.md) で ✅ / ⚠️ になっている Dokan オペレーションを exercise する。

| カテゴリ | テスト |
|---|---|
| **ディレクトリ操作** | mkdir/rmdir、ネストディレクトリ、100 ファイルディレクトリ、非空 rmdir 拒否 |
| **ファイル基本** | new/del、small write/read、append、FileMode.Create で上書き (O_TRUNC 相当) |
| **データ I/O (bytea)** | 2 MiB round-trip (チャンクまたぎ)、truncate 縮小/伸長/ゼロ |
| **名前変更** | Move-Item、サブディレクトリへの Move-Item |
| **所有者 / グループ** (追加) | 新規ファイルの所有者が**要求元アカウント**であること、グループが**親ディレクトリから継承**されること |
| **CopyFileEx** (追加) | `Copy-Item` での 2 MiB 双方向コピーがハッシュ一致すること (エクスプローラのコピーと同じ経路) |
| **byte-range lock** (追加) | 同一マウント内で `FileStream.Lock` が**ドライバに強制される**こと (2 本目が失敗し、`Unlock` 後は取れる) |
| **属性 / 時刻** | SetFileAttributes (ReadOnly / Hidden / System / Archive)、xattr `user.win.attrs` 往復、ドットファイル fallback、un-hide の永続化、SetFileTime (LastWriteTime) |
| **ボリューム / パターン** | GetVolumeInformation 経由のドライブ情報、FindFilesWithPattern (`-Filter`) |
| **Windows 基礎** (追加) | `CreateNew` の排他 (2 回目が失敗し勝者の内容が壊れない)、`FileStreamOptions.PreallocationSize` が EOF を伸ばさない、同一パスへの rename が no-op |
| **並行性** | 異なるファイルへの並列書き込み、同じファイルからの並列読み出し、並列 mkdir |

## 実行シェル (pwsh を優先する)

`flow.cmd` / `run.cmd` は **pwsh (PowerShell 7) があればそれを使う**。Windows PowerShell 5.1 は環境によって
`Microsoft.PowerShell.Security` / `Microsoft.PowerShell.Utility` のロードに失敗することがあり
(`TypeData "System.Security.AccessControl.ObjectSecurity": The member ... is already present`)、
その場合 `Get-Acl` / `Get-FileHash` が使えず **pgfs とは無関係に 3 件が FAIL する**
(`test_getfilesecurity_projection` / `test_setfilesecurity_roundtrip` / `test_concurrent_reads_same_file`)。
`e2e.ps1` 自体は 5.1 でも動く書き方を保っているが、`PreallocationSize` を使う
`test_allocation_size_does_not_extend_eof` だけは .NET 6+ が要るので 5.1 では SKIP になる。

## metadata write-back / B-1 ノブのテスト (2 マウント)

```powershell
pwsh -NoProfile -File tests\windows\wbmeta.ps1            # A=P:(defer) / B=R:(write-through) の 4 件
pwsh -NoProfile -File tests\windows\wbmeta.ps1 -MountB S:
```

`mount.write_back_metadata` と `mount.write_back_metadata_exclusive_create = defer` の契約を見る。
**Linux 版が psql の fault injection で作る「占有者」を、こちらは本物の別マウントで作る**。

| テスト | 見るもの |
|---|---|
| `test_meta_defer_exclusive_within_mount` | defer でも同一マウント内の `CreateNew` 排他は維持される |
| `test_meta_close_no_flush_loses_content` | close だけのファイルは強制終了で失われる (contract) |
| `test_meta_defer_conflict_does_not_clobber_occupier` | **B-1 の要件**: 敗者の flush は失敗し、占有者の内容は無傷 |
| `test_meta_error_state_blocks_persisted_delete` | **B-7**: エラーステート中に persisted を削除できないこと (ファイルが残り、**呼び出し元にも失敗が返る**)。`Api.CanDestroy` を `DeleteFile` で見て断る |
| `test_meta_defer_conflict_recovers_by_unlink` | **SKIP**: Windows が `DeleteFile` を遅らせるため、この環境では回復を観測できない (Core 側の latch バグは修正済み) |

**テストの順序が重要**: 衝突テストで latch すると以降の create / write が `-EIO` になるので、latch しない契約テストを先に置いている。

## 削除の可視性 (`Assert-Absent` が列挙 + open で見る理由)

pgfs の削除の実体は **Dokan の `Cleanup` 契機**で、さらに DB (Citus) の DELETE に ≈100 ms かかる。
そのうえ **Windows クライアント側の FCB キャッシュ**が、DB から消えた後も名前を返し続けることがある
(別プロセス = AV / インデクサ が並行して開いていたケース。実測で 10 秒以上・FS への問い合わせは 0 件)。
そのため `Assert-Absent` は 10 秒の有界リトライで **親ディレクトリの列挙 → それでも出るなら open を試す**
順に見る (open が失敗すれば delete-pending = pgfs 側は削除済みとみなす)。
1 回だけ `Test-Path` する書き方は確実にフレークするので使わないこと。
**それでも `test_touch_unlink` は 3 回に 1 回ほど FAIL する** (列挙にも出て open もできてしまう)。
pgfs 側の削除は DB で確認済みで、原因はクライアント側キャッシュ。既知の間欠 FAIL として扱う。

## 既知の Assign 側の問題

テスト整備中に判明したもの。e2e は迂回路 (.NET API 直叩き) で通しているが、別途修正対象:

| 問題 | 状態 | メモ |
|---|---|---|
| `Copy-Item` (CopyFileEx) で 2 MiB ファイルが `IOException` | ✅ 解消済 (確認) | **原因は `Api.EnsureChunk` の SELECT-then-INSERT race** (CopyFileEx の並行 WriteFile が同じ `(data_id, chunk_index)` を 2 回 INSERT して PK 制約で死ぬ)。`INSERT ... ON CONFLICT DO NOTHING` 化で修正済み ([history.ja.md](../../docs/history.ja.md))。**この表の行だけが残っていた**。2026-09-19 に 2 MiB / 8 MiB・双方向・上書き・`robocopy /COPYALL`・ツリーコピーで再現しないことを確認し、`test_copy_item_round_trip` として実経路をテストに戻した |
| `dokanctl /u` が "Admin rights required" で失敗 | 〇 (回避) | Dokan の仕様で `dokanctl /u` は管理者権限必須。`flow.ps1` は `Process.Kill()` でフォールバック (強制終了であり、write-back の未 flush データは失われ得る。正常終了・耐久性の検証には使えない) |

## 記録上の結果と未検証範囲

**件数と実機結果の正は [docs/tests.ja.md](../../docs/tests.ja.md)**。ここには書かない — スイートに 1 件足すたびに
両方を直すことになり、**片方が必ず腐る**。実際 「e2e 34 件 / cross-client 6/6 / metadata
write-back 3 passed」のまま取り残されているのが見つかった (実測は e2e 38 / cross-client 10 / wbmeta 5)。
**この doc が持つのは実行方法とスイートごとの狙いだけ**にする (Linux 側 README と同じ形)。
**この節が抱えていた「未検証」は 2026-09-21 にすべて実測で閉じた。**

(**exit 4 の実発火も確認済** — metadata write-back の `defer` で A に pending な create を持たせ、
**B が同名を `CreateNew` で占有**してから `dokanctl /u` で**正常停止**すると **exit 4** が返る。
**未 flush を残さずに止めれば exit 0** なので契約として確かめられている。
`--write-back-interval-ms 0` が必須で、既定 1000 ms のままだと**背景 flush が先に pending を実体化して衝突が起きない**。
詳細は [windows-parity.ja.md](../../docs/design/windows-parity.ja.md) の「正常停止と喪失報告」。)
(**Explorer / FileSystemWatcher のイベント種別も実測済** — **他マウント由来は 1 件もイベントにならない**。
ローカル操作では `Created`/`Changed`/`Renamed`/`Deleted` が出る。詳細は [Assign.ja.md](../../docs/Assign.ja.md) の Notify 行。)
(`write_back` / `write_back_metadata` を on にした受入は **2026-09-21 に実機で完了** — [writeback.ps1](writeback.ps1) 6/6 と
[wbmeta.ps1](wbmeta.ps1) 4 passed + 1 skip。skip は Windows が `DeleteFile` を遅延させるため回復を観測できないもの。)
Linux で後から加わったキャッシュ・write-back・live 設定の Windows 受入条件は [Windows 展開設計](../../docs/design/windows-parity.ja.md) を参照する。

## カバーしていないもの

[docs/Assign.ja.md](../../docs/Assign.ja.md) の TODO 表の ❌ 項目は未実装のため対象外:

- named ACL による厳密なアクセス拒否・default ACL 継承 (GetFileSecurity / SetFileSecurity の投影・逆投影は既存テスト対象)
- Alternate Data Streams (`file.txt:stream`)
- xattr (DokanNet に xattr API なし)
- symlink / hardlink (DokanNet の `IDokanOperations2` に未対応)
- POSIX 互換 range lock
- Junction (再解析ポイント)

未実装機能の残一覧は [docs/next.ja.md](../../docs/next.ja.md) を参照。

## 使い方

### A. 全フロー (推奨)

`flow.cmd` または `flow.ps1` が `mount → test → unmount` を一括実行します。

```cmd
tests\windows\flow.cmd
```

PowerShell から:

```powershell
.\tests\windows\flow.ps1
```

オプション (どちらの呼び方でも同じ):

| オプション | 動作 |
|---|---|
| `pattern` (位置引数) | テスト名フィルタ。例: `flow.cmd concurrent` で並行テストだけ |
| `-Build` | 事前に `dotnet publish pgfs.sln -c Release` を実行する (既定: スキップ) |
| `-NoMount` | マウント済みとしてテストだけ実行 (mount 操作を一切しない) |
| `-KeepMounted` | テスト後にアンマウントしない (調査用) |

> **Linux 版との違い**: Linux 側の `flow.ps1` は既定でビルドを実行するが、Windows 側は既定でスキップする。Windows は開発機本体で IDE / `dotnet build` を使う前提のため、毎回ビルドし直す必要はない。明示的にビルドしたい場合のみ `-Build` を渡す。

組み合わせ可能:

```cmd
tests\windows\flow.cmd -Build              REM 先に publish してから実行
tests\windows\flow.cmd concurrent          REM "concurrent" を含むテストだけ
tests\windows\flow.cmd -KeepMounted        REM テスト後マウント維持
```

マウントポイントや設定ファイルを変えたい場合は PowerShell 直呼び:

```powershell
.\tests\windows\flow.ps1 -MountPoint R: -SettingFile C:\path\to\pgfs.toml
```

### B. テストだけ (マウント済み前提)

別ターミナルで `assign.pgfs.exe` を稼働させている場合:

```cmd
tests\windows\run.cmd
tests\windows\run.cmd concurrent
```

### C. 手動フロー (各ステップを個別に)

```cmd
REM 1. ビルド (任意 — IDE でビルド済みならスキップ)
dotnet publish pgfs.sln -c Release

REM 2. (別ターミナル) マウント
bin\Publish\assign.pgfs.exe -f pgfs.toml -m P:

REM 3. テスト
tests\windows\run.cmd

REM 4. アンマウント
"C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe" /u P:
```

## テストの絞り込み

第 1 引数にフィルタ文字列を渡すと、テスト名に含まれるものだけ実行する:

```cmd
tests\windows\run.cmd concurrent     # 並列アクセス 3 テスト
tests\windows\run.cmd truncate       # truncate 系 3 テスト
tests\windows\run.cmd rename         # rename 系 2 テスト
```

## 環境変数 / パラメータ

### run.cmd (テストだけ)

| 変数 | 既定 |
|---|---|
| `MOUNT_ROOT` | `P:\` |

例:

```cmd
set MOUNT_ROOT=R:\
tests\windows\run.cmd
```

### flow.ps1 (全フロー)

CLI パラメータと環境変数の両方で設定可能 (優先順位: **CLI 引数 > 環境変数 > 既定**)。`MOUNT_ROOT` は `run.cmd` と共通。

| パラメータ | 環境変数 | 既定 | 用途 |
|---|---|---|---|
| `-MountPoint` | `MOUNT_ROOT` | `P:` | assign.pgfs に渡すマウントポイント (`P:` / `P:\` どちらでも可) |
| `-AssignBinary` | `ASSIGN_BINARY` | `bin\Publish\assign.pgfs.exe` | assign.pgfs.exe のフルパス |
| `-SettingFile` | `ASSIGN_SETTING_FILE` | `pgfs.toml` | 設定ファイルのフルパス |
| `-Filter` (位置 0) | - | (なし) | テスト名フィルタ |
| `-Build` | - | (off) | `dotnet publish` を先に走らせる |
| `-NoMount` | - | (off) | マウント済み前提でテストだけ |
| `-KeepMounted` | - | (off) | テスト後にアンマウントしない |

## PowerShell から直接実行する場合

```powershell
.\tests\windows\e2e.ps1 -MountRoot P:\
.\tests\windows\e2e.ps1 -MountRoot P:\ -Filter concurrent
```

## 出力例

```
=== assign.pgfs ===
  starting: ...\bin\Publish\assign.pgfs.exe -f ...\pgfs.toml -m P:
  mount pid=12345
  mounted at P:\

=== e2e tests ===
=== pgfs Windows e2e tests ===
Mount root: P:\
Test root:  P:\test

PASS: test_mkdir_rmdir
PASS: test_nested_directories
...

===========================================
Results: 27 passed, 0 failed, 0 skipped (out of 27)

=== unmount ===
  ...\dokanctl.exe /u P:
  unmounted
  mount log: tests\windows\mount.log (123 lines)

  ALL PASSED
```

## ログ

`flow.ps1` 実行中、`assign.pgfs.exe` の stdout / stderr は以下に保存される (gitignore 推奨):

- `tests\windows\mount.log` — stdout (Trace SQL ログ等)
- `tests\windows\mount.err.log` — stderr

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 全テスト成功 |
| 1 | 一つ以上失敗 |
| 2 | `MountRoot` が存在しない (マウントされていない) |
| 3 | `TestRoot` を作成できない (mount が書き込み不可) |
| 11 | `assign.pgfs.exe` が見つからない (flow のみ) |
| 99 | テスト実行前に例外 (flow のみ) |

## 前提

- **.NET 10 SDK** (ビルド時のみ)
- **Dokan 2.x** ドライバ ([Dokan releases](https://github.com/dokan-dev/dokany/releases) の `DokanSetup_redist.exe`)
  - flow.ps1 は `dokanctl.exe` を `C:\Program Files\Dokan\Dokan Library-*\` 配下から自動検出する
  - 見つからない場合は `Stop-Process` でフォールバック (アンマウントが汚くなる可能性あり)
- マウントポイント (`P:` 等) が既存ドライブと衝突していないこと
- PostgreSQL に PGFS が初期化済み ([docs/Mkfs.ja.md](../../docs/Mkfs.ja.md))
- `pgfs.toml` に DB 接続情報があること

## write-back テスト (耐久性)

```powershell
pwsh -NoProfile -File tests\windows\writeback.ps1                     # data write-back on (6 件)
pwsh -NoProfile -File tests\windows\writeback.ps1 -WriteBackMetadata  # metadata write-back も on
pwsh -NoProfile -File tests\windows\writeback.ps1 -MountPoint S:
```

`mount.write_back` を **有効にした** マウントで、耐久性の契約を「**プロセスを強制終了 → 再マウント →
内容が残っているか**」で確認する (Linux 版 [writeback.sh](../linux/writeback.sh) と対)。
`Stop-Process -Force` は `kill -9` 相当で、`Cleanup` も `Dispose` も走らない。

| テスト | 見るもの |
|---|---|
| `test_wb_flush_survives_kill` | `FlushFileBuffers` (`FileStream.Flush($true)`) の後なら残る |
| `test_wb_close_survives_kill` | ハンドルを閉じた後なら残る (**metadata on では契約が変わる**ので assert しない) |
| `test_wb_writethrough_survives_kill` | `FILE_FLAG_WRITE_THROUGH` は flush も close もせずに殺されても残る |
| `test_wb_unflushed_is_lost_without_barrier` | **negative control**: バリアを通っていない書き込みは失われる |
| `test_wb_graceful_unmount_persists` | 正常アンマウントは未 flush を書き切り、**exit 0** で終わる |
| `test_wb_large_write_flush_roundtrip` | 3 MiB (チャンクまたぎ) が flush 後にハッシュ一致 |

**マウントは `--write-back --write-back-interval-ms 0` で張る**。背景 flush の時間トリガを切らないと、
negative control が「1 秒待つかどうか」のタイミング勝負になり、スイート全体が契約を検証できなくなる。

**実行前に対象のマウントポイントを空けておくこと** (このスクリプトは自分で張り替えるので、既にマウント済みだと exit 2)。

## 強制終了するテストは `{prefix}mounts` に行を残す

`writeback.ps1` / `wbmeta.ps1` は耐久性を測るために `Stop-Process -Force` (= `kill -9` 相当) を使う。
このとき `Api.Dispose` を通らないので **マウント登録行 (`{prefix}mounts`) が DELETE されずに残る**
(喪失を伴う正常 unmount が残す「墓標」とは別物で、こちらは単なる残骸)。

溜まると `pgfsctl status` が読めなくなる (実測: 38 行溜まって Layer 1 が埋まった)。掃除は運用の明示操作:

```sql
-- 墓標 (stats->>'unflushedLoss' > 0) は消さない。稼働中を巻き込まないよう heartbeat で猶予を取る。
DELETE FROM <schema>.<prefix>mounts
 WHERE COALESCE((stats->>'unflushedLoss')::int, 0) = 0
   AND heartbeat_at < (now() AT TIME ZONE 'UTC') - INTERVAL '10 minutes';
```

reaper の自動化は [docs/next.ja.md](../../docs/next.ja.md) 🟡 #27 の課題。

**プロセスの撃ち方について**: 本ディレクトリのスクリプトは `Start-Process -PassThru` で得た
**自分が起動した PID だけ**を `Stop-Process -Id` / `Process.Kill()` で落とす。
コマンドラインやプロセス名で検索して撃つ形にはしない (無関係なプロセスを巻き込む)。

## コントロールプレーンのテスト (`pgfsctl config` / `status`)

```powershell
pwsh -NoProfile -File tests\windows\control_plane.ps1
pwsh -NoProfile -File tests\windows\control_plane.ps1 -Filter reload
```

| テスト | 見るもの |
|---|---|
| `test_cp_config_list_and_get` | `config list` / `config get` が既知のキーを返す |
| `test_cp_status_json_shape` | `status --json` の live 行に **実効設定**が出る (`mode=dokan`・B-1 のノブを含む) |
| `test_cp_live_reload_reflected` | Live 項目の `config set` が**稼働中のマウントに効く** |
| `test_cp_invalid_enum_rejected` | 許可外の値が `set` の時点で弾かれ、実効値が変わらない |
| `test_cp_write_back_live_flip` | `mount.write_back` の **live on → off (二相 flip)** を跨いでデータが無傷 |
| `test_cp_metadata_flip_completes` | **metadata write-back の flip が完走する** — pending 300 件を抱えた状態で off にして、受付再開 / pending 0 / ファイル 300 件が無傷 |

**マウントに `--notify` を付けない**のが意図的なポイント。制御チャネルの LISTEN は常時 ON なので、
`database.notify_enabled = false` のマウントにも `config set` は届く (Phase 3a)。そこを一緒に確認している。

**反映は heartbeat スナップショット経由**なので、`status` の実効値に出るまで **最大 1 周期 (既定 30 秒)** かかる。
待ち時間の既定は `-ReflectTimeoutSec 75`。

**設定を書き換えるテスト**なので、変更した項目は `finally` で必ず元に戻す (この `pgfs.toml` が指す FS は
運用テスト用で、`audit.enabled` を落としたままにすると監査が止まる)。

### flip テストの作法 (Linux 側と合わせている)

`test_cp_metadata_flip_completes` は Linux 側の作法に合わせて **窓を広げてから観測**する:

1. `mount.write_back_interval_ms = 0` で背景 flush の時間トリガを止める
2. pending を数百件作る (実体化に時間がかかるので第 1 相が秒オーダーになる)
3. `config set` を投げる (**ack を待たずに返る**ので投げた直後から poll してよい)
4. `status --json` を 0.5 秒間隔で poll

**第 1 相 (受付停止中) を掴めるかはタイミング依存**なので、観測できたら記録するだけにして、
**完走 (enabled=false かつ effective=false かつ受付再開 かつ pending 0)** を assert している。
「flip 完走後に pending が生まれ得る」(B-9 ①) は shell から決定的に踏ませられないので**テストを書かない**
— 不可分性は Core 側が台帳ロックの中で担保しており、Linux 側も同じ判断。
