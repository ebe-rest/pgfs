# pgfs ドキュメント索引

> **道順**: **本書が入口**。ここから各 doc へ降りる。
>
> **この doc が正である範囲**: **全 doc の索引と分類**。どの doc が何の「正」かを一覧で示す。
> 個々の設計・仕様・実測値はここに書かず、必ずリンク先へ置く (索引に中身を書くと二重化して腐る)。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [next.ja.md](next.ja.md) | **次に何をするか** (punch-list)。現在地の要約もここ |
> | [architecture.ja.md](architecture.ja.md) | プロジェクト構成・依存・ビルド手順 |
> | [history.ja.md](history.ja.md) | 専用 doc を持たない完了項目の経緯 |
> | [../README.ja.md](../README.ja.md) | **公開向け**の入口 (リリース対象 doc だけを並べる) |

pgfs の全ドキュメント一覧。**次にやることは [next.ja.md](next.ja.md)** (現在地 / 次にやること)。

階層化: **機能設計・リファレンスは [`design/`](design/)** に集約。**ツール仕様 (`Mkfs`/`Mount`/`Assign`) と起点系、`ddl/` は `docs/` 直下に据え置き**。

## 読み順

- 現在の利用方法: [Mount](Mount.ja.md) / [Assign](Assign.ja.md) / [Pgfsctl](Pgfsctl.ja.md) → [設定一覧](design/settings-matrix.ja.md)。
- いま残っている制限: [CHANGELOG.ja.md](../CHANGELOG.ja.md) §既知の制限 → [runtime-control-plane の as-built](design/runtime-control-plane.ja.md) → [テスト記録](tests.ja.md)。
- Windows への機能展開: [Windows 設計](design/windows-parity.ja.md) → [Assign の現状](Assign.ja.md) → 同設計の受入条件。

## 🚩 起点・全体像

| ドキュメント | 内容 |
|---|---|
| [next.ja.md](next.ja.md) | **次にやること** — punch-list / 現在地 / 完了索引 |
| [architecture.ja.md](architecture.ja.md) | プロジェクト構成 (Core/Fuse/Dokan + 薄い exe) / 依存 / Core 内部構成 / ビルド・実行手順 |
| [history.ja.md](history.ja.md) | 専用 doc を持たない完了項目の経緯アーカイブ (新規エントリは現行パスで追記) |

## 仕様 (ツール CLI)

| ドキュメント | 内容 |
|---|---|
| [Mkfs.ja.md](Mkfs.ja.md) | `mkfs.pgfs` の仕様 |
| [Mount.ja.md](Mount.ja.md) | `mount.pgfs` (Linux/macOS) の仕様 |
| [Assign.ja.md](Assign.ja.md) | `assign.pgfs` (Windows) の仕様 |
| [Pgfsctl.ja.md](Pgfsctl.ja.md) | `pgfsctl` (実行時コントロールプレーン CLI — `config` / `status` / `prune`) の仕様 |
| [tests.ja.md](tests.ja.md) | **テストのハブ** — 全テスト一覧 / 実行方法 / 環境要件 / docker 統合の検討 |

## [design/](design/) — 機能設計・リファレンス

| ドキュメント | 内容 |
|---|---|
| [design/v0.3.0-options.ja.md](design/v0.3.0-options.ja.md) | **(候補の洗い出し・未実装)** v0.3.0 で「選択」だったものをオプションにする候補と分類 (①-a 昇格だけ / ①-b 新規分岐 / ② 既定値) |
| [design/v0.2.0-plan.ja.md](design/v0.2.0-plan.ja.md) | v0.2.0 設計 — `Lib` を `Core`/`Fuse`/`Dokan` に分割 + FUSE 内製化 (決定・詰めポイント・移動先・命名規約) |
| [design/metadata-write-back.ja.md](design/metadata-write-back.ja.md) | metadata write-back (メタデータの遅延書き・Phase 1e) |
| [design/metadata-write-back-reviews.ja.md](design/metadata-write-back-reviews.ja.md) | 1e のレビュー記録 (ラウンド A / B-1〜B-13 の as-built) |
| [design/write-back.ja.md](design/write-back.ja.md) | write-back (データ本体の遅延書き・Phase 1d) |
| [design/cache.ja.md](design/cache.ja.md) | キャッシュ (inode LRU / content read / read-ahead) |
| [design/control-plane.ja.md](design/control-plane.ja.md) | コントロールプレーン (登録表 / 制御 NOTIFY / pgfsctl config・status) |
| [design/gui.ja.md](design/gui.ja.md) | GUI 運用ダッシュボード (Avalonia・Phase 5) |
| [design/runtime-control-plane.ja.md](design/runtime-control-plane.ja.md) | **実装・残課題・設計履歴** 実行時コントロールプレーン + キャッシュ — ①キャッシュ高速化 / ②ライブ設定反映 / ③config・④status サブコマンド / ⑤GUI。DB 集約 (NOTIFY+登録表) で統一 |
| [design/windows-parity.ja.md](design/windows-parity.ja.md) | **(「Windows 基礎」段は実装済 / それ以外は未実装)** Linux 機能の Windows 展開設計 — 現状差分 / 候補 / 推奨理由 / 共通基底・OS 派生 / 段階と受入条件 |
| [design/fuse-binding.ja.md](design/fuse-binding.ja.md) | FUSE 内製バインディング設計 — libfuse3 公開 API × pgfs 使用の対象表 / op / 構造体 |
| [design/handle-context.ja.md](design/handle-context.ja.md) | **(段階 A〜C 実装済 / 段階 D 未着手)** ハンドル文脈 (`OpenFileContext`) の共通化 — パス優先の識別をやめる / 監査主体・同期方針をハンドルに持つ / 段階 A〜D |
| [design/namespace-policy.ja.md](design/namespace-policy.ja.md) | 名前空間の方針 — 見え方と実体がずれる 3 件 (`.fuse_hidden*` の隠蔽 / Windows の予約名 / 末尾の空白・ドット) を**まとめて**決めた記録。案と trade-off / 当時の決定と崩れた前提 / 外してはいけない線 |
| [design/support_for_citus.ja.md](design/support_for_citus.ja.md) | Citus (水平分散) 対応 — bytea 化 / 分散戦略 / `pgfs_lock` 排他制御 (Phase 1+2+3) |
| [design/raid.ja.md](design/raid.ja.md) | **(アイデア段階)** 複数 PostgreSQL を 1 FS にまとめる RAID — パス単位配置 / 複製数 (0/n/-1) / 名前空間マージ |
| [design/audit-log.ja.md](design/audit-log.ja.md) | 監査ログ — `{prefix}audit` 月次パーティション / 呼び出し元コンテキスト / フック箇所 |
| [design/permission-interop.ja.md](design/permission-interop.ja.md) | 権限・所有権・ACL の Linux↔Windows 相互運用 (図: [design/permission-interop-diagram.html](design/permission-interop-diagram.html)) |
| [design/df-support.ja.md](design/df-support.ja.md) | `df` (statfs) 実空き容量 — plperlu `SECURITY DEFINER` / mkfs `--statfs` / 3 段フォールバック / Citus 集約 |
| [design/fstab-support.ja.md](design/fstab-support.ja.md) | `/etc/fstab` 対応 |
| [design/xattr-bytea.ja.md](design/xattr-bytea.ja.md) | xattr バイト列透過 (並行配列 KVS) |
| [design/settings-and-plperlu.ja.md](design/settings-and-plperlu.ja.md) | 設定スコープ再編 + `app.plperlu` ゲート + tablespace auto-mkdir |
| [design/database.ja.md](design/database.ja.md) | DB スキーマ (テーブル単位 DDL は [ddl/](ddl/README.ja.md)) |
| [design/settings-matrix.ja.md](design/settings-matrix.ja.md) | 全設定項目 × (CLI/TOML/DB/既定/参照タイミング) のマトリックス |
| [design/coding-style.ja.md](design/coding-style.ja.md) | コーディング規約 (v4 条件分岐ルール含む) |
| [../CHANGELOG.ja.md](../CHANGELOG.ja.md) | **変更履歴** (リリースタグ単位・移行手順と既知の制限を含む) |
| [design/data-id-lifecycle.ja.md](design/data-id-lifecycle.ja.md) | `data_id` のライフサイクル (ハードリンク共有 / `st_ino` 安定性の是正) |
| [design/performance.ja.md](design/performance.ja.md) | 性能改善の実装・測定記録と残候補 |

DDL は分類上 design 相当だが、テーブル単位 SQL なので [`ddl/`](ddl/README.ja.md) として `docs/` 直下に据え置き。

