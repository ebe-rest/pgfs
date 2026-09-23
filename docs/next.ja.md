# 次にやること

> **道順**: [docs/README.md](README.md) › **本書**
>
> **この doc が正である範囲**: **次に何を着手するか** (punch-list) と、現在地の 1 行要約。
> **終わったことの詳細は持たない** — 行き先の索引だけを [§完了済み](#完了済み-リンクのみ) に置く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [README.md](README.md) | **全 doc の索引**。入口はここ |
> | [history.md](history.md) | 専用 doc を持たない完了項目の**経緯**アーカイブ |
> | [tests.md](tests.md) | テストの件数・実行方法・スイートの副作用 |
> | 各 `design/*.md` | 機能ごとの設計と as-built。**punch-list から外したものはここへ移す** |

## 現在地 (1 行)

**v0.2.0 (Core/Fuse/Dokan 分割 + FUSE 内製化) は実装完了**、**運用フェーズは Phase 5 GUI と 1c read-ahead を除いて
実装済み**である。キャッシュ (1a/1b)・データ write-back (1d)・metadata write-back (1e 段階 1・2 + レビュー
ラウンド A / B の全指摘)・土台 (2a〜2c)・config (Phase 3)・status Layer 1〜3 (Phase 4) はいずれも実機で緑、
**Phase 5 の GUI は 5a/5b まで完了** (5c/5d が残)。

[handle-context.md](design/handle-context.md) は **段階 A〜C まで完了**で、`O_APPEND` まわりの 3 件と
`st_size` の巻き戻りも閉じている。**再実行していないテストは無い** — Linux 9 スイート / Windows 7 スイート /
docker ランナー / Citus 2 スイートをすべて現行 HEAD で走らせてある (件数の正は [tests.md](tests.md))。

---

## v0.2.0 — FUSE 内製バインディング + ライブラリの OS 層分割

正は [v0.2.0-plan.md](design/v0.2.0-plan.md)。v0.1.0 の次版。**実装完了・両 OS で e2e 緑**。

| フェーズ | 状態 | 内容 |
|---|---|---|
| 決定 1: FUSE 内製化 | ✅ 合意 | libfuse への最小 P/Invoke を自前で持ち、`vendor/Tmds.Fuse` submodule + fork を廃止 (Tmds.Fuse はクレジット) |
| 決定 2: ライブラリ分割 | ✅ 合意 | `Pgfs.Core` (SQL 共通・OS `#if` ゼロ) + `Pgfs.Fuse` (Linux/libfuse) + `Pgfs.Dokan` (Windows/Dokan)、機構名で命名 |
| **A: 設計** (詰めポイント / 命名 / 移動先) | ✅ **クローズ** | 詰めポイント 7 件 + 命名規約 + `.cs` ごとの移動先を実物に当てて確定 |
| **B: FUSE 内製バインディング設計** | ✅ **クローズ** | 正は [fuse-binding.md](design/fuse-binding.md)。libfuse3 公開 API × pgfs 使用 (動詞単位) + 構造体面 + 設計判断 6 件 |
| **実装 + 両 OS e2e** | ✅ **緑 (2026-06-07)** | Core/Fuse/Dokan 4 分割 + libfuse 内製化 + vendor submodule 廃止。**Linux full e2e 36/36 + Windows Dokan 27/27 PASS**、Windows でフル sln 緑 |
| **緑化後の後片付け** | ✅ おおむね適用 | 型付き fuse_config (offset-64 の直接書きを廃止) / KEEPCACHE (3→4) / PosixAcl を Fuse へ移動、**両 OS で再検証緑 (Linux 36/36)**。symlink は設計どおり正しく変更なし。残: `ServiceResolver` の `#if` 分割 (任意) と macOS |

A の主な決定 (詳細と根拠は [v0.2.0-plan.md](design/v0.2.0-plan.md)):

- **依存は一方向**: `ツール exe → 機構ライブラリ (Fuse/Dokan) → Core → PG`。ツール実行ファイル
  (`mkfs.pgfs` / `mount.pgfs` / `assign.pgfs`) は「OS の呼び出し規約へのアダプタ」(argv 規約・デーモン化・
  install 名) である。
- **Linux 層は `Pgfs.Fuse`** と命名 (Dokan と対称な機構名。`Pgfs.Posix` は採らない)。
- **Windows OS 共通部** (`WindowsUserResolver` / `FileSystemUtils` / win attrs) は当面 `Pgfs.Dokan` に同居させ、
  WinFsp を足すなら `FileSystem.cs` 以外を `Pgfs.Windows` へ抽出する (YAGNI)。
- **命名規約**: `AssemblyName` は小文字 `{role}.pgfs`、`RootNamespace` は Pascal の `Pgfs.{Role}`。
- **OS 分岐の芯**: コンパイル時 `#if WINDOWS` は `ServiceResolver.cs` (native getservbyname = ws2_32 対 libc)
  だけ。Schema の既定マウントポイントと ConfigLoader のヘルパ文脈は実行時 `OperatingSystem.Is*` のまま
  (無害)。

### v0.2.0 の残り (いずれも任意・急がない)

1. **`ServiceResolver` の `#if WINDOWS` 分割** — native `getservbyname` (ws2_32 / libc) を各機構層から注入する
   インタフェースにする。Core を真に「OS `#if` ゼロ」にする純度改善 (OS 別コンパイルで機能は完結しているので
   必須ではない)。詳細は [fuse-binding.md §実装ステータス](design/fuse-binding.md) /
   [v0.2.0-plan.md](design/v0.2.0-plan.md)。
2. **macOS 対応** (libfuse 系 = macFUSE / fuse-t)。`Pgfs.Fuse` に同居できる。

## 機能追加 (要件にあって未実装)

| # | 項目 | 内容 | 規模 |
|---|---|---|---|
| 4 | **ACL・権限の Linux↔Windows 相互運用** — 主目的は達成済み・残りのみ | 設計の正は [permission-interop.md](design/permission-interop.md)、図は [permission-interop-diagram.html](design/permission-interop-diagram.html)。直近 5 件 + ACL 本体 3-0〜3-3 (正準モデル [PgfsAcl](../src/core/src/Models/PgfsAcl.cs) / Windows 読み書き投影 / Linux POSIX ACL [PosixAcl](../src/fuse/src/PosixAcl.cs)) は完了・回帰済み。**残**: `system.posix_acl_default` の Windows 継承変換と、OS 間ラウンドトリップの自動テスト。3-4 (名前付き ACL の厳密適用) は要件待ちで保留 ([permission-interop.md §Phase 3-4 の再評価](design/permission-interop.md)) | 残りのみ |
| 5 | **ジャンクション / native リンク** (Assign 側) | `pgfs_inode.is_junction` 列は既にあり、Linux 側は symlink で代替している。**2026-09-19 に到達性 PoC 実施 = 現行バインディングでは作成も読み取りも不可**: `IDokanOperations2` にリンク系コールバックが無く、reparse データの取得・設定の入口も無い。実測で `mklink /H` / `/J` / `/D` と `File.CreateSymbolicLink` がすべて失敗し、**Linux 由来の symlink は列挙から消え、名前直指定では空ファイルとして開く**。→ **DokanNet / Dokany への追加か WinFsp への切り替えが前提**。正は [windows-parity.md §native リンクの到達性 PoC](design/windows-parity.md) | 大 (バインディングかバックエンドの変更を伴う) |
| 6 | **ADS (Alternate Data Streams)** (Windows) | Dokan 経由の NTFS 互換 `:streamname`。データモデルの拡張が要る | 大 |

## 実行時コントロールプレーン + キャッシュ

「運用フェーズ」5 テーマを 1 本に束ねた設計。ハブは
[runtime-control-plane.md](design/runtime-control-plane.md)。決定は ① 制御チャネルを
**DB に集約 (NOTIFY + `{prefix}mounts` 登録表)**、② 着手順は **キャッシュを先に**。

| フェーズ | テーマ | 状態 |
|---|---|---|
| 1 | ① キャッシュ (1a 容量/LRU 配線 → 1b read キャッシュ + データ書き込み NOTIFY → 1c read-ahead → 1d write-back → 1e metadata write-back) | **1a + 1b + 1d 完了 (いずれも実機 e2e 緑)**。1d = `mount.write_back` (既定 off)、**`dd bs=128k` で 6.1×・rsync で 1.4×** を実測 (2026-07-25)。**1e は段階 1 + 1.5 + 2 完了・両モードで実機緑** (2026-08-12。`mount.write_back_metadata` は既定 off、as-built は [metadata-write-back.md](design/metadata-write-back.md))。段階 2 = close-no-flush + 同期ヒューリスティクス 3 種 + エラーの 4 段フロア + 二相フリップ。**自動テストは wbmeta.sh で全緑**。1c (read-ahead) は未着手 |
| 2 | 土台 (NOTIFY 制御メッセージ + `{prefix}mounts` 登録表 + Field の reload policy Live/NextMount/Format) | **2a/2b/2c 実機 e2e 緑**。即時 reload は DB 保存の Live フィールド (audit/statfs) に効く。ファイル保存の Live は Phase 3 |
| 3 | ③ `config` サブコマンド (+ ② ライブ反映) | **✅ 完了・実機 e2e 緑 (2026-06-20)**。3a 制御 LISTEN 常時 ON / 3b 単一 `pgfsctl` + Core `ConfigAdmin` で config get/list/set --json / 3c notify OFF の mount に対するライブ反映を実機検証 |
| 4 | ④ `status` サブコマンド (DB 由来の read-only 部分を先行可) | **Layer 1+2+3 すべて完了・実機 e2e 緑**。`pgfsctl status [--json]` = クラスタで動いているものの一覧 + FS 統計 (Layer 1+2) + 走行中プロセスのキャッシュ統計と実効設定 (Layer 3。heartbeat スナップショットで `{prefix}mounts.stats` / `config` を埋める)。正は [control-plane.md](design/control-plane.md) |
| 5 | ⑤ GUI (③④ のラッパ) | **Avalonia**。Core (StatusAdmin/ConfigAdmin) をインプロセスで直接呼び、依存は最小 (素の MVVM)。**5a スケルトン + 5b 読み取りダッシュボード MVP は実装済み (ビルド緑・目視確認は手動)** — 接続バー / Mounts (L1) / Filesystem (L2) / Process 詳細 (L3) / Config 一覧、3 秒ポーリング + ping する Refresh 付き。**残: 5c config set / 5d 仕上げ**。正は [gui.md](design/gui.md) |

**次の一手**: **Phase 5 GUI の実装** (Avalonia。MVP は読み取りダッシュボード)。GUI は docker e2e に載らないので
**目視確認は手動** — `dotnet run --project src/gui/Gui.csproj` で起動し、PG へ Connect して Mounts /
Filesystem / Process 詳細 / Config が出るかを見る (Layer 1 と 3 を見るには mount が 1 つ走っていること。
Layer 2 と Config は mount 無しでも出る)。**残: 5c** (Config 画面からのライブ config set) → **5d**
(接続ダイアログ・エラー表示・publish 設定)。正は [gui.md](design/gui.md)。

**Phase 5 の後 or 並行の候補**:

1. **パス探索と bytea 部分読みの実測** ([performance.md](design/performance.md)) — Layer 3 のキャッシュ
   ヒット率が実測の足場になる。

## 運用と検証

テストの全体像・環境要件・docker 集約の現状分析は [docs/tests.md](tests.md) を参照。

| # | 項目 | 内容 |
|---|---|---|
| 8 | **テストの docker 集約** (単一 PG ✅ / 多ノードは #9 と統合) | 単一 PG での Linux e2e の完全 docker 化は完了 ([tests/docker/](../tests/docker/README.md))。**残**: 多ノード Citus + race + audit を同じ枠に広げる (`race_multinode.sh` が雛形)。Windows e2e は Dokan がカーネルドライバなので対象外。分析は [docs/tests.md §docker 集約の検討](tests.md) |
| 9 | **多ノード Citus 上の Windows e2e** | Phase 3 の残検証 ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh)) は Linux スイートしか回していない。docker Citus 越しに Windows スイートを回す枠が未整備 |
| 10 | **flow.cmd のハーネス出力問題** (一部緩和) | 長時間実行をバックグラウンド化すると PowerShell ツールの `Write-Host` が stdout に届かない問題。**`flow.cmd` / `run.cmd` を pwsh 優先へ変更**し、リダイレクト (`> log 2>&1`) で全出力を捕捉できることを確認 (PS 5.1 が `Get-Acl` / `Get-FileHash` を読めずに出ていた偽 FAIL 3 件も解消)。**残**: `Write-Host` を `Write-Output` / `*-Information` へ書き換える作業は未着手 |
| 22 | **timestamp UTC 統一の残り** (本体は ✅) | `TIMESTAMP` 列は常に UTC で保存する方針に統一 ([database.md §timestamp 規約](design/database.md) / 回帰テスト `test_timestamp_utc_roundtrip`)。**残**: ① **非 UTC ホストで作成済みの既存 FS の移行** (列 DEFAULT の差し替え + 既存行のシフト。手順は database.md。docker 作成の FS は offset 0 なので DEFAULT だけでよい) ② `test_timestamp_utc_roundtrip` の Linux 相当 (非 UTC ホストでのラウンドトリップ) が Windows 側に無く、`SetFileTime` のラウンドトリップしか見ていない ③ 新テストを docker ランナー (`CACHE_MAX_ENTRIES=8` 版) でも回して DB 読み戻し経路を緑にする |
| 24 | **Citus 排他制御 / rf の残り** (本体は ✅) | `{prefix}lock` を Citus local 化して行ロックを 1 箇所に集約し、rf 非依存にした ([support_for_citus.md §排他制御と replication factor](design/support_for_citus.md))。mkfs に `--shard-count` / `--rf` / `--distribute-existing` を追加。**inode UPDATE の router 化も完了** (分散キーを WHERE に含めて Task Count 8→1・分散デッドロック解消)。**残**: ① ~~FUSE `max_write`~~ → **空振り** (libfuse3 が既定でカーネル上限 1 MiB までネゴシエート済み。ノブとしては実装済・既定 0)。**遅さの根っこは訂正済み**: 「worker の commit レイテンシ × 2PC 参加ノード数」ではなく **チャンク行の read-modify-write 増幅** (寄与 7.5×・commit 本数の寄与は 17%)。単一 PG でも同じ増幅が起きる ([performance.md](design/performance.md))。効く順は **Phase 1d write-back (投影 6.5〜8.1×)** → `writeback_cache` (カーネル側) → worker の `synchronous_commit` ② **`tests/citus/race_multinode.sh` / `audit.sh` を rf≥2 で回す** ③ `WHERE data_id` の 3 箇所 (hardlink の nlink 再計算) は原理的に multi-shard = 並行 hardlink でデッドロックの余地 ④ ロックが coordinator 1 行に集中する上限の実測 (現状 0.11 ms/回) ⑤ 参照テーブル版への切り替え余地 |
| 25 | ~~**`test_concurrent_writes_diff_files` の 40P01 flake**~~ → ✅ **根治** | 原因 = rf≥2 のシャード単位直列化のもとで、書き込み tx が **先頭の `UPDATE inode SET data_id` で inode シャードを掴んでから chunk/data を待つ** (hold-and-wait。`citus_lock_waits` の実測で特定)。対処 = ① **シャード接触順の正規化** (data_id リンクを tx の最後に畳み、chunk/data → inode に統一。デッドロックが約 99% 減) + ② **create/write/flush の 40P01/40001 有界リトライ** (残りを吸収)。検証 = 再現ループ 180 周 0 fail + 両モードで e2e 緑。規約と詳細は [support_for_citus.md §書き込み tx のシャード接触順](design/support_for_citus.md) / [tests.md §根治した flake](tests.md)。**理論上残るサイクル** (DeleteInode の逆順・hardlink の multi-shard UPDATE) は未観測で、リトライが受ける |
| 26 | **write-back (Phase 1d) の残り** (本体は ✅) | 正は [write-back.md](design/write-back.md)。**残**: ① docker ランナー (`tests/docker/run.sh`) への `WRITE_BACK` 注入口 ② flush 失敗時に `-EIO` を返すことの自動テスト (経路は実装済み。DB を落とす手段が要る) ③ cross-client の last-flush-wins テスト ④ ファイル跨ぎのバッチ (`write_back_batch_files`) は **意図して未実装** (+8.8% の上積みでは損失ウィンドウの拡大とロック保持時間の伸びに見合わない) |
| 23 | **実占有バイトと `st_blocks` の残り** (本体は ✅) | `pgfs_data.total_size` を実占有バイトとして維持し `st_blocks` (= `du`) に反映 ([database.md §実占有バイトと st_blocks](design/database.md) / 回帰テスト `test_sparse_du_blocks`)。**残**: ① **既存 FS のバックフィル** (`total_size` が 0 のままだと `du` が 0。SQL は database.md) ② **Windows (Dokan) 側は原理的に非対応** — **2026-09-19 に PoC 実施: 現行バインディングでは返す経路が無い**。出力構造体 (`ByHandleFileInformation` / `FindFileInformation`) に AllocationSize の枠が無く、ドライバが EOF から合成する (実測: 1 バイト占有のスパースファイルが 8,389,120 と申告された)。解消には DokanNet / Dokany への追加か WinFsp への切り替えが要る。正は [windows-parity.md §AllocationSize の到達性 PoC](design/windows-parity.md) |

## コード品質・規約

| # | 項目 | 内容 | 規模 |
|---|---|---|---|
| 11 | **コーディング規約 v4 への追従** | 合計 40 箇所程度: `else` 約 20 + 三項演算子約 20。書き換えパターンは `FirstList.cs` (switch 式・タプル分解・Try* 抽出) と [Api.cs](../src/core/src/Api/Api.cs) (Logger ガードのブレース化)。詳細は [coding-style.md](design/coding-style.md) | 小 (ファイル単位) |

## 性能改善

正は [performance.md](design/performance.md)。

| # | 項目 | 内容 |
|---|---|---|
| 17 | **パス探索のクロスシャード往復の実測** | 多ノード Citus で `parent_id` 分散にしたときの探索コスト。InodeCache のヒット率込みで測る |
| 18 | **bytea 部分読みの実測** | PG 13+ の partial TOAST detoast が期待どおり効くか。chunk_size を上げる余地 |
| 19 | **書き込みパスの tx 削減 3 点セット** (正は [performance.md](design/performance.md)) | ① ~~negative lookup キャッシュ~~ → ✅ **実装済み**: `mount.negative_cache_ttl_ms` (既定 0 = 無効・ライブ反映可)。**-3.2 tx/file・1.08×** を実測 (4 KB × 300 の rsync。消せるのは同名の 2 回目以降だけで、1 ファイルあたり約 3.4 回の初回 ENOENT は原理的に残る)。テストは [tests/linux/negcache.sh](../tests/linux/negcache.sh) 7/7 ② ~~未同定の約 14 tx/file の同定~~ → ✅ **説明がついた**: 大半は隠れクエリではなく **Citus の 2PC が xact_commit に二重計上されていた**もの (生 SQL と突き合わせて確認)。実 tx は 1 ファイルあたり約 11〜12 本 ③ **back-pressure の flush 粒度** (64 MB のファイルが約 1.2 tx/MiB に割れる。`write_back_max_bytes` の既定見直し / flush のバッチ化) は未着手 |

## 将来の検討 (設計メモ級・優先度低)

| # | 項目 | 内容 |
|---|---|---|
| 19 | **`pgfs_inode_lock` の分離** | Phase 3 は単一 `pgfs_lock` + 符号名前空間で始めた。非対称コスト ([support_for_citus.md §Phase 3 の注意点](design/support_for_citus.md)) が inode ロック主体のワークロードで出るなら別テーブルにする |
| 20 | **マルチコーディネータ HA Citus の検討** | Enterprise Citus が射程に入るか |
| 21 | **`pgfs_inode` を id で分散** | `(parent_id, name)` UK をアプリ層で保証する代替案。クロスシャード rename のコストが問題になったら再検討 |

---

## 完了済み (リンクのみ)

このリストの前提・周辺として直近で効いている**完了項目の行き先索引**。詳細と検証結果はリンク先の doc が正。
より古い経緯は [history.md](history.md)。

| 完了項目 | 正のドキュメント |
|---|---|
| **v0.2.0** (Core/Fuse/Dokan 分割 + libfuse 内製化) | 上記 / [v0.2.0-plan.md](design/v0.2.0-plan.md) / [fuse-binding.md](design/fuse-binding.md) |
| Mount の `-o` 対応 | [Mount.md §マウントオプション](Mount.md) |
| xattr のバイト列透過 | [xattr-bytea.md](design/xattr-bytea.md) |
| ACL・権限の相互運用 3-0〜3-3 | [permission-interop.md](design/permission-interop.md) (残りは上記 #4) |
| df 対応 + Citus 複数 worker 集約 | [df-support.md](design/df-support.md) |
| 設定スコープ再編 + plperlu ゲート + tablespace | [settings-and-plperlu.md](design/settings-and-plperlu.md) / [settings-matrix.md](design/settings-matrix.md) |
| 単一 PG 構成の docker 集約 | [tests/docker/](../tests/docker/README.md) (残りは上記 #8) |
| /etc/fstab 経由の起動時マウント | [fstab-support.md](design/fstab-support.md) |
| Config 仕上げ (自動ヘルプ / Field 自己点検 / 二重パース除去) | [history.md §Config 仕上げ](history.md) |
| UserResolver キャッシュ | [performance.md](design/performance.md) |
| 監査ログ / Citus Phase 1+2+3 / モデル整理 / Notify・retry・fallback ほか | [history.md](history.md) / [audit-log.md](design/audit-log.md) / [support_for_citus.md](design/support_for_citus.md) |
| metadata write-back (1e) の設計確定と段階 1・1.5・2 | [metadata-write-back.md](design/metadata-write-back.md) |
| 1e のレビュー ラウンド A (A-1〜A-10) / ラウンド B (B-1〜B-13) | [metadata-write-back-reviews.md](design/metadata-write-back-reviews.md) |
| B-12 の Windows 配線 (2 回目の停止シグナルで flush を諦める) | [metadata-write-back-reviews.md](design/metadata-write-back-reviews.md) |
| データ write-back (1d) の設計と実測 (`dd bs=128k` 6.1×) | [write-back.md](design/write-back.md) |
| `data_id` ライフサイクルの是正 (ハードリンク共有 / `st_ino` の安定性) | [data-id-lifecycle.md](design/data-id-lifecycle.md) |
| ハンドル文脈の共通化 (段階 A〜C) + append の契約 | [handle-context.md](design/handle-context.md) / [Mount.md](Mount.md) |
| Windows 実装の底上げ (排他作成 / 所有者導出 / バイト範囲ロックほか) | [windows-parity.md](design/windows-parity.md) |
| 削除の可視性 — 既知 flake 2 件の決着 (FS ではなくテストの当て方の問題だった) | [windows-parity.md](design/windows-parity.md) |
| 名前空間の方針 (予約名 / 末尾の空白・ドット / `.fuse_hidden*`) | [namespace-policy.md](design/namespace-policy.md) |
| テストの件数・実行方法・スイートの副作用 (`{prefix}mounts` に残る行) | [tests.md](tests.md) |
