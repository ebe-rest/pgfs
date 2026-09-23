# GUI — 運用ダッシュボード (Avalonia)

> **道順**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **本書**
>
> **この doc が正である範囲**: `src/gui/` (`Pgfs.Gui` / 出力 `pgfsgui`) の設計・実装状況・変更記録。
> GUI の技術選定、画面構成、Core の呼び方 (in-process 直呼び) はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [control-plane.md](control-plane.md) | GUI が読む `StatusAdmin` / `ConfigAdmin` そのものの設計 (Phase 2/3/4) |
> | [../Pgfsctl.md](../Pgfsctl.md) | CLI (`pgfsctl config` / `status`) の仕様。GUI と同じ Core API を使う |
> | [runtime-control-plane.md](runtime-control-plane.md) | 運用フェーズ全体の構成とフェーズ間の関係 (ハブ) |
> | [settings-matrix.md](settings-matrix.md) | GUI の Config 画面が並べる設定項目そのもの |

`config` / `status` を読む薄い運用フロント。**DB 集約・クラスタ横断**の思想に沿い、1 画面で全 mount を一望する。

## 設計

### 確定設計

合意: 技術 = **Avalonia (cross-platform desktop)** / MVP = **読み取りダッシュボード先行**。

**決定 P5-1 — 技術 = Avalonia**: 3 OS (Linux/Win/mac) で同一バイナリ動作 = プロジェクト目標「同一動作」に最も合致。候補比較 (WinForms/WPF=Windows のみ・MAUI=Linux desktop 弱い・Web=cluster 横断だが別性格・TUI=非グラフィカル) の上で desktop ネイティブの Avalonia を採用。

**決定 P5-2 — Core を in-process 直呼び (shell-out しない)**: GUI は pgfsctl と同じく **Core のみ参照** (FUSE/Dokan 非依存)。[StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) (ListMounts/GetFsStats) と [ConfigAdmin](../../src/core/src/Config/ConfigAdmin.cs) (list/get/set) を直接呼ぶ (型が豊か・サブプロセス不要)。`pgfsctl --json` は外部/スクリプト consumer 用に残す。接続解決は [ConfigLoader](../../src/core/src/Config/ConfigLoader.cs) (CLI + toml) を流用 + UI の接続バー。

**決定 P5-3 — 依存最小 + 素の MVVM**: `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` のみ。MVVM は素の `INotifyPropertyChanged` (プロジェクト方針「むやみに依存を足さない」。ReactiveUI 等は入れない)。`net10.0`・新規 `src/gui/` (`Pgfs.Gui` / 出力 `pgfsgui`)。命名は admin ツールの 1 語 (pgfsctl と同じく `{役割}.pgfs` 規約の例外)。

**決定 P5-4 — 更新モデル = ポール + ping refresh**: タイマで `ListMounts`/`GetFsStats` を数秒間隔ポール。「Refresh」ボタンは **ping 制御 NOTIFY を撃って Layer 3 snapshot を即更新**してから読む (status.sh と同じ手)。

**画面 (運用ダッシュボード)**: ① 接続バー (connection/schema/prefix・toml/CLI prefill + Connect) / ② Mounts (Layer 1 グリッド) / ③ Filesystem (Layer 2 パネル) / ④ Process detail (行選択で Layer 3 = inode/content キャッシュ統計 hit率 + notify + 実効 config) / ⑤ Config (③ 設定の list・出所/SaveTo/Reload 付き。set は 5c)。

**テストの現実 (過去フェーズと異なる点)**: ビルドは Windows で通る。実行は Avalonia なので 3 OS 可だが **GUI は docker-e2e に載らない** (表示が要る)。検証は **手動/目視 + ViewModel のデータ取得を実 DB に当てる smoke** (ConfigAdmin/StatusAdmin の offline smoke と同じ流儀)。

**実装サブステップ**:
- **5a**: Avalonia プロジェクト雛形 (`src/gui/` 新設・Core 参照・NuGet 復元 + 空ウィンドウ・全 sln ビルド緑)。
- **5b** (MVP): 読み取りダッシュボード — Layer 1 (Mounts) + Layer 2 (FS) + Layer 3 (Process detail) + Config list view。タイマポール + ping refresh。**MVP はここまで**。
- **5c**: config set (live 反映) を Config 画面から。(SaveTo,Reload) マトリクスに沿って永続化/案内を出し分け。
- **5d**: 仕上げ (接続ダイアログ・エラー表示・stale 行の見せ方)。

**開いた点 (実装時)**: ① プロジェクト出力名 (`pgfsgui` で進める。要望あれば変更)。② Linux 実行時の表示前提 (X11/Wayland) の確認は手動検証時。③ config 一覧の編集 UI 形 (5c で詰める)。

## 実装ステータス (as-built)

- **5a 完了**: `src/gui/` 新設 (`Pgfs.Gui` / 出力 `pgfsgui`・Core 参照)。Avalonia ボイラープレート (Program / App.axaml / MainWindow.axaml + 各 .cs・FluentTheme・空ウィンドウ)。**Avalonia は 12.0.5 を採用** — 当初 11.2.3 は推移的依存 `Tmds.DBus.Protocol` 0.20.0 が NU1903 (HIGH・GHSA-xrw6-gwf8-vvr9) を出したため、新規プロジェクトでレガシーが無い利点を活かし現行ライン 12.0.5 へ。**gui + 全 sln ビルド緑・脆弱性警告ゼロ**。Release single-file publish 設定 (Ctl 同型) は GUI publish = 5d で詰めるため未追加。起動は手動 (`dotnet run --project src/gui/Gui.csproj`・表示が要るので docker-e2e 非対象)。空ウィンドウの目視確認済。
- **5b 完了 (ビルド緑 / 目視確認は手動)**: 読み取りダッシュボード (MVP)。素の MVVM (`ObservableObject`/`RelayCommand`・依存追加なし・反射バインディング)。`MainViewModel` が Core を in-process 直呼び — `StatusAdmin.ListMounts/GetFsStats` (3s タイマポール) + `ConfigAdmin.List` (Connect/Refresh 時)。Refresh は `NotifyChannel.Publish(Control="ping")` (ConfigAdmin.FireSet と同型) → 700ms 後に再読込で Layer 3 snapshot を即更新。画面 = 接続バー / Mounts(L1 グリッド・選択可) / Filesystem(L2 パネル) / Process detail(L3 = 選択 mount の inode/content キャッシュ統計 hit率 + notify + 実効 config・`MountViewModel` が stats/config JSON をパース) / Config(`ConfigItem` 一覧 key/value/src/reload)。DB 呼びは `Task.Run` で UI を止めない。**データ層は status.sh で e2e 緑なので、GUI 固有はバインディング/レイアウトの目視確認**。`AvaloniaUseCompiledBindingsByDefault=false` (MVP・compiled 化は 5d 候補)。

---


## 変更記録

時系列の記録はここに追記する (設計と as-built は上の 2 章が正)。

- [runtime-control-plane.md](runtime-control-plane.md) が 1,802 行に肥大したため、
  機能ごとに分割してこの doc を切り出した。内容は分割前のまま。
