# 設定項目マトリックス

> **道順**: [docs/README.md](../README.md) › **本書**
>
> **この doc が正である範囲**: 全設定項目を「キー / CLI / TOML / DB 保存先 / 既定 / 型 / 参照タイミング」で
> 横串に見る**一覧**と、解決の優先順位、reload ポリシー (Live / NextMount / Format) の分類、
> ライフサイクル別の集約、短縮形の衝突といった既知の問題。項目そのものの宣言は
> [Schema.cs](../../src/core/src/Config/Schema.cs) が正で、**利用者向けの既定値表は [../Mkfs.md](../Mkfs.md) が正**である。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../Mkfs.md](../Mkfs.md) | `mkfs.pgfs` の CLI 一覧と**既定値表** (+ TOML 例)。既定値を変えたらそちらも直す |
> | [../Mount.md](../Mount.md) / [../Assign.md](../Assign.md) | 各ツールの CLI 仕様と利用者から見た挙動 |
> | [../Pgfsctl.md](../Pgfsctl.md) | `pgfsctl config get / set / list` の使い方 |
> | [fstab-support.md](fstab-support.md) | `-o key=val,...` 経由のオプションと、短縮形衝突の判定ロジック・処理順 |
> | [control-plane.md](control-plane.md) | reload ポリシーの仕組みと Live 反映の設計 (設定を動かす側) |
> | [settings-and-plperlu.md](settings-and-plperlu.md) | 設定スコープ再編 (`app.*` / DB 権威化) の設計経緯と決定 |

> **🗒 旧 `Pgfs.Core.Models.*Settings` ツリーは削除済み**。各設定項目の真の宣言は [src/core/src/Config/Schema.cs](../../src/core/src/Config/Schema.cs) に集約 (`Schema.<Scope>.<Key>` の static `Field<T>` 記述子)。本ドキュメントは「CLI フラグ / TOML キー / DB 保存先 / 既定値 / 参照タイミング」を一覧で横串に見るためのもの。**コードと食い違ったら Schema が正**。将来は Schema 由来の自動生成に置き換える案あり。

> 2026-09-19 に現行コードと静的照合した。既定値の変更はない。Live は反映経路の分類である。**write-back の live 切替えは二相 flip で drain を待つ** (修正済) ので、**切替えでデータを失わないことは実機で確かめてある** — 回帰は `control_plane` スイートの `test_cp_write_back_live_flip` / `test_cp_metadata_flip_completes` (件数の正は [tests.md](../tests.md))。

---

## 凡例

- **キー**: `pgfs_settings` テーブル / TOML での階層キー (`scope.key`)。コードの `Field.Scope` / `Field.Key` と一致
- **CLI**: 受け付ける引数。複数あれば最も一般的なものを太字
- **TOML**: `pgfs.toml` から読む (= `SaveTo = File`)
- **DB**: `pgfs_settings(scope, key, value)` 行として永続化される (= `SaveTo = Db`)
- **既定**: `Field.DefaultFn()` の返り値
- **型**: 値の C# 型 (`Field<T>` の T)
- **参照タイミング**: いつコード側が `RootConfig` から読むか (起動時 / mkfs 時 / 動的)
- **備考**: 制約、依存関係、既知の罠

## 優先順位

[`ConfigLoader`](../../src/core/src/Config/ConfigLoader.cs) は 1 度に CLI / TOML / DB / Default を統合する。優先順位は CLI > TOML > DB > Default。Phase は `CLI → TOML → DB` の順に走り、「上位ソースが既に値を入れていれば skip」方式で実現する。`AssignPositional` / `ParseDashOOptions` も「未読込時のみ反映」なので、明示 `-c` / `-m` / `-f` が positional より優先される。

---

## 1. ルート (`-` / `--` 直書き、親なし)

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `help` | `-?` `-h` **`--help`** | ❌ | ❌ | false | bool | CLI parse 直後 | 使い方表示 → 即終了 |
| `clean` | **`--clean`** | ❌ | ❌ | false | bool | mkfs `Initializer.cs` の冒頭 | DROP DATABASE → 再作成。短縮形を意図的に持たせていない (誤実行防止) |

## 2. `setting.*` — 設定ファイル自身の置き場所

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `setting.file` | **`-f`** `--setting` `--setting-file` + **positional[0] (postgresql: 以外)** | ❌ | ❌ | `pgfs.toml` | string | `LoadFromFile` 開始時 |  positional[0] が `postgresql:` で始まらないときも流れ込む。短縮形 `-f` は **直接実行時のみ有効** (helper context では `mount(8)` 由来の `--fake` として silent 飲み込み — [docs/fstab-support.md §短縮形の衝突](fstab-support.md#the-short-form-collisions)) |
| `setting.search_path` | `--setting-path` `--setting-search-path` `--setting-file-path` `--setting-file-search-path` | ❌ | ❌ | `.` / `$HOME/.config/pgfs` / `$HOME/.config` / `$HOME` / `$LOCALAPPDATA/pgfs` / `$APPDATA/pgfs` | List&lt;string&gt; | `LoadFromFile` で `setting.file` が絶対パスでない場合の探索 | OS 標準ディレクトリ群 |

## 3. `logging.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `logging.level` | `--log-level` `--log-min-level` `--min-log-level` | ✅ | ❌ | `Information` | `Level.Enum` | 起動時および Live set 時に `Logger.MinLevel` へ反映 | `all` `trace` `debug` `information` `warning` `error` `critical` `none` |
| `logging.output` | `--log-output` | ✅ | ❌ | `stderr` | `SettingLoggingOutput` | 起動時および Live set 時に [LogSink.Configure](../../src/core/src/Logging/LogSink.cs) で `Logger.Output` に反映 | `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`。cycle = `none\|hourly\|daily\|monthly`、pattern 中の `*` が日付に展開 (hourly=`yyyyMMddHH` / daily=`yyyyMMdd` / monthly=`yyyyMM` / none=空)、`~` はホーム展開。例 `daily:~/pgfs/log/pgfs-*.log`。**Warning 以上は実効シンクが stderr でない限り stderr にも terse 形式 (`pgfs: [Warning] ...`、日時なし) で出る** (file/stdout sink でも警告を見落とさないため)。同様に **起動バナー (プログラム名 + バージョン + Copyright) とプロセス生死マーカー (`started`/`exited`) は常に stderr へ** (`Logger.Lifecycle`、ログがファイルでも起動/終了を追える)。起動時に解釈済みパラメータ (`param: scope.key = value`、接続文字列の Password はマスク) と未知オプション警告も出力 |

## 4. `mount.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `mount.mount_point` | **`-m`** `--mount-point` + **positional[1]** | ✅ | ❌ | `/mnt/pgfs` (Linux/macOS) / `P:` (Windows) | string | `mount.pgfs Program.Main` の FUSE ループ突入直前、`assign.pgfs` の DokanNet 起動時 | |
| `mount.max_write` | `--max-write` | ✅ | ❌ | `0` | int | FUSE の 1 WRITE 要求の最大バイト数 (`fuse_conn_info.max_write`)。`0` = libfuse のネゴシエーション任せ | mount (FUSE) のみ。**実測では libfuse3 が既定でカーネル上限 1 MiB までネゴシエートする**ので、下げたい / 版差で固定したいとき用のノブ。`-o max_write` は libfuse3 が拒否するので init で設定する。[performance.md](performance.md) |
| `mount.cache_max_entries` | `--cache-max-entries` | ✅ | ❌ | 1024 | int | `InodeCache` のメタデータ上限 (byId 権威・超過で LRU 退避 → byPath/childrenByParent をカスケード掃除) | inode メタの件数上限。詳細 [cache.md §1a](cache.md) |
| `mount.cache_data_max_bytes` | `--cache-data-max-bytes` | ✅ | ❌ | 67108864 (64MiB) | long | `ContentCache` の本体 read キャッシュのバイト予算 (超過で LRU 退避)。`0` で無効 | ファイル本体 (`data_chunk`) のインメモリ read キャッシュ。詳細 [cache.md §1b](cache.md) |
| `mount.negative_cache_ttl_ms` | `--negative-cache-ttl-ms` | ✅ | ❌ | 0 (無効) | int | negative lookup (ENOENT) を TTL の間キャッシュして DB 往復を省く。`0` で無効 | 自クライアントの create/rename は即時無効化 (単一クライアント安全)。**他クライアントの新規ファイルは最大 TTL 不可視** (`notify_enabled` なら通知で即時無効化)。実測は [performance.md §negative lookup キャッシュ](performance.md) |
| `mount.write_back` | `--write-back` | ✅ | ❌ | false | bool | 書き込みをメモリに溜め、**ファイル 1 つ = 1 トランザクション**で flush する (`fsync` / `close` / 時間 / dirty 上限 / unmount が契機) | 実測 **`dd bs=128k` で 6.1× / rsync で 1.4×** (Citus rf=2)。増幅除去が本体。**未 flush 分はクラッシュで失われる**ので既定 off。詳細 [write-back.md](write-back.md) |
| `mount.write_back_max_bytes` | `--write-back-max-bytes` | ✅ | ❌ | 67108864 (64MiB) | long | dirty バイトの flush 開始閾値。超過すると書き込み側で flush を試行する (失敗時にも上限以下を保証するものではない) | `cache_data_max_bytes` とは**別勘定** (dirty は LRU 退避の対象外)。`write_back` 有効時のみ意味を持つ |
| `mount.write_back_interval_ms` | `--write-back-interval-ms` | ✅ | ❌ | 1000 | int | この時間より長く dirty のままのファイルを背景 flush する。`0` で時間トリガ無効 | 背景 flush の試行間隔であり、DB 障害・競合時の喪失窓の上限ではない。`write_back` 有効時のみ意味を持つ |
| `mount.write_back_metadata` | `--write-back-metadata` | ✅ | ❌ | false | bool | メタデータ (`create`/`mkdir`/`symlink` + **それらへの**属性変更/rename) も pending に溜め、**1 ファイル = 1 tx** で flush する | **`mount.write_back = true` が前提** (単独 on は warning + 無効)。persisted inode へのメタデータ操作は write-through のまま (本体は data write-back の対象)。実測は rsync 1.00× / 非 O_EXCL create の総合 1.30× (**A-10 の修正より前の値**)。レビュー指摘 (A-1〜A-10 / B-1〜B-13) は全件対応済・既定 off。詳細 [metadata-write-back.md §1e](metadata-write-back.md) |
| `mount.write_back_metadata_exclusive_create` | `--write-back-metadata-exclusive-create` | ✅ | ❌ | `write_through` | enum (`write_through` / `defer`) | `O_EXCL` / `CREATE_NEW` 付き create を pending にするか。`defer` で 1e の畳み込みが `rsync` にも効く (実測 **3.28×**) | **`defer` は cross-client の排他を失う** (別マウントは pending を見られないので 2 クライアントの排他作成が両方成功する)。同一マウント内の排他は台帳が維持する。衝突した敗者は flush で error latch し、占有者を消さない。単一クライアント運用と分かっている bulk copy 向け。`write_back_metadata` 有効時のみ意味を持つ |
| `mount.write_back_max_inodes` | `--write-back-max-inodes` | ✅ | ❌ | 4096 | int | pending inode 数の flush 開始閾値。超過すると作成側で flush を試行する (失敗時には閾値を超え得る) | 小ファイル多数ではバイトより件数が先に膨らむため、`write_back_max_bytes` とは別に件数で bound する。`write_back_metadata` 有効時のみ意味を持つ |
| `mount.write_back_flush_timeout_ms` | `--write-back-flush-timeout-ms` | ✅ | ❌ | 30000 | int | back-pressure / unmount の再試行期限。**期限は sweep の 1 巡の中でも見る**が、実行中の DB 呼出しは打ち切れないので厳密な時間上限ではない | `0` = 待たない (1 巡だけ試して続行 = 1d までの挙動)。期限切れの back-pressure は警告して続行する。unmount 時の残留は種類ごと最大 32 件を Error ログに出し `mount.pgfs` の実マウントプロセスが **exit 4** で終了する (daemon 起動親の終了コードではない) (`fusermount3 -u` はカーネル側で完了するので FS からは EBUSY で拒否できない)。`write_back` 有効時のみ意味を持つ |
| `mount.fallback_uname` | `--fallback-uname` | ❌ | ✅ | `nobody` | string | `UserResolver` / `WindowsUserResolver` 構築 | OS で uname が解決できないときの逃げ先。FS 全体で 1 値 (= DB 保管) |
| `mount.fallback_gname` | `--fallback-gname` | ❌ | ✅ | `nogroup` | string | 同上 | |
| `mount.foreground` | **`--foreground`** | ❌ | ❌ | false | bool | `mount.pgfs Program.Main` の子プロセス分離判定 | true で前景固定、false でデーモン化。短縮形 `-f` は [setting.file と MountHelperFlagsNoValue との衝突](#既知の問題) のため意図的に持たない |

## 5. `file_system.*` — mkfs 時に凍結、以後 readonly

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `file_system.version` | `--version` | ❌ | ✅ | `1.0.0` | string | (現状コード上の参照なし) | mkfs 時凍結用。将来の互換性チェック余地 |
| `file_system.volume_label` | `--volume-label` | ❌ | ✅ | `pgfs` | string | `assign.pgfs` の `GetVolumeInformation` | Windows のドライブ名 |
| `file_system.cluster_size` | `--cluster-size` | ❌ | ✅ | 4096 | long | `Api.StatFs` (`f_bsize`) | バイト単位。** DB 権威化** (SaveTo=File→Db)。クライアント間で値がズレると事故るため。生成 toml には書かない |
| `file_system.default_chunk_size` | `--default-chunk-size` | ❌ | ✅ | 1048576 (1 MiB) | long | (現状 chunk_size は pgfs_data の `chunk_size` 列を真とするため、設定値は新規 data 行作成時のみ初期値として使用) | ** DB 権威化**。値ズレは chunk 境界解釈不一致でデータ破損しうる |
| `file_system.max_file_size` | `--max-file-size` | ❌ | ✅ | 1099511627776 (1 TiB) | long | `Api.StatFs` (`f_blocks`) ほか | ** DB 権威化** |

## 6. `database.*`

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `database.connection` | **`-c`** `--connection` `--connection-string` + **positional[0] (postgresql: で始まる)** | ✅ | ❌ | `Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer` | `NpgsqlConnectionStringBuilder` (`ConnectionField` が parse/format) | 全プロセスの DB アクセス | mkfs / mount / assign すべてで必須 |
| `database.super_connection` | `-su` `--su` `--super` `--super-connection` `--super-connection-string` `--super-user` `--super-user-connection` `--super-user-connection-string` | ❌ | ❌ | `Host=localhost;...Username=postgres;Password=postgres;Database=template1;...` | 同上 | mkfs `Initializer` の DB / ROLE / EXTENSION 作成 | `SaveTo=None` でファイルにも DB にも書かない (資格情報の安全)。**`--super` を明示しないときは `database.connection` の Host/Port/SslMode を継承**し、super と user が同じサーバを向くようにする (user をリモートに向けたのに super が localhost の別 DB を DROP/CREATE する事故を防ぐ)。super 資格情報・maintenance DB は既定のまま (postgres / template1)。明示時はその値を完全に尊重 |
| `database.schema` | **`-s`** `--schema` `--schema-name` | ✅ | ❌ | `public` | string | `Api` の全 SQL 修飾 | 短縮形 `-s` は **直接実行時のみ有効** (helper context では `mount(8)` 由来の `--sloppy` として silent 飲み込み — [docs/fstab-support.md §短縮形の衝突](fstab-support.md#the-short-form-collisions)) |
| `database.prefix` | **`-x`** `--prefix` `--table-prefix` `--table-name-prefix` | ✅ | ❌ | `pgfs_` | string | テーブル名生成 (`pgfs_inode` 等) | `Database.GetPrefix()` 経由で末尾 `_` を正規化 |
| `database.tablespace` | `--tablespace` `--tablespace-name` | ❌ | ✅ | `pg_default` | string | mkfs `Initializer.EnsureTablespaceAsync` (coordinator + 全 worker) | **Citus でもカスタム可** (〜)。per-table 句を廃し `CREATE DATABASE WITH TABLESPACE` 継承。** DB 権威化** (設定ファイルで書き換えさせない fs 識別情報。生成 toml 非出力) |
| `database.tablespace_path` | `--tablespace-path` | ❌ | ✅ | `""` | string | mkfs `Initializer.EnsureTablespaceAsync` | 空文字なら新規作成しない。指定時、`app.plperlu` 許可なら plperlu auto-mkdir (postgres 所有 0700)。** DB 権威化** |
| `database.retry_max_attempts` | `--retry-max-attempts` | ✅ | ❌ | 5 | int | 起動時と Live set の `Retry.Configure` | この設定は接続オープンの再試行用。create/write/flush には別途 40P01/40001 の限定的な tx 再試行がある |
| `database.retry_initial_delay_ms` | `--retry-initial-delay-ms` | ✅ | ❌ | 200 | int | 同上 | 指数バックオフの初期値 |
| `database.retry_max_delay_ms` | `--retry-max-delay-ms` | ✅ | ❌ | 2000 | int | 同上 | バックオフ上限 |
| `database.notify_enabled` | `--notify` `--notify-enabled` | ✅ | ❌ | `false` | bool | 起動時にデータ変更通知の送受信を選択 (制御 LISTEN は常時起動) | 他クライアント変更通知 (LISTEN/NOTIFY)。1 クライアント運用では OFF 推奨。詳細 [docs/Mount.md](../Mount.md) / [docs/Assign.md](../Assign.md) |
| `database.citus` | **`--citus`** | ❌ | ✅ | `false` | bool | mkfs `Initializer` が PGFS テーブルを Citus 分散登録するか | **DB 権威化** (SaveTo=File→Db)。「この FS は Citus 化済み」を後追い確認する bool。mount/assign は条件分岐に使わない。詳細 [docs/support_for_citus.md](support_for_citus.md) |
| `database.workers` | `-w` `--worker` `--workers` | ✅ | ❌ | 空リスト | List&lt;string&gt; (Field) | mkfs の Citus worker bootstrap | `host[:port],...`。mount/assign の live 変更対象ではない |
| `database.shard_count` | `--shard-count` | ❌ | ✅ | `0` | int | mkfs が `create_distributed_table` 前に `citus.shard_count` をこの値に設定 (`0` = クラスタ既定に従う) | mkfs 専用。セッション GUC なので同じ DB を共有する他アプリに影響しない。詳細 [support_for_citus.md](support_for_citus.md) |
| `database.shard_replication_factor` | `--shard-replication-factor`, `--rf` | ❌ | ✅ | `0` | int | shard 1 つを何ノードに置くか (`0` = クラスタ既定に従う) | mkfs 専用。**ストレージ冗長の選択**で、排他制御 (`{prefix}lock` = 非分散) には影響しない |
| `database.distribute_existing` | `--distribute-existing` | ❌ | ❌ | `false` | bool | `--citus` 併用時に既存テーブルも Citus 化する | mkfs 専用のアクションフラグ (SaveTo=None)。既定は新規作成したテーブルだけ Citus 化 |

## 7. `audit.*` — 監査ログ

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `audit.enabled` | **`--audit`** | ❌ | ✅ | `false` | bool | mkfs で初期化、起動時読込み + Live set/reload | メタデータ変更を `{prefix}audit` に記録。Db + Live であり `pgfsctl config set audit.enabled true/false` で変更できる。詳細 [docs/audit-log.md](audit-log.md) |

## 8. `app.*` — アプリ挙動 (df モード / plperlu ゲート)

| キー | CLI | TOML | DB | 既定 | 型 | 参照タイミング | 備考 |
|---|---|---|---|---|---|---|---|
| `app.statfs` | **`--statfs`** `--statfs-mode` | ❌ | ✅ | `auto` | string (`auto`/`require`/`nominal`) | mkfs が `{prefix}statfs()` (plperlu) を作る/消す分岐に使用 + `pgfs_settings` に保存。`Api.GetStatFs` は `nominal` のときサーバ問い合わせをスキップ | **scope/key を `statfs.mode` → `pgfs.statfs` → `app.statfs` と移動** (C# は `Schema.Statfs.Mode` のまま)。`df` が実ディスク空きを返すモード。`auto`=plperlu あれば実測/無ければ公称、`require`=plperlu 必須(無ければ mkfs 失敗)、`nominal`=常に公称容量。plperlu 使用可否は `app.plperlu` が上位ゲート。詳細 [docs/df-support.md](df-support.md) / [docs/settings-and-plperlu.md](settings-and-plperlu.md) |
| `app.plperlu` | **`--plperlu [true\|false]`** `--allow-plperlu` (bare) / `--deny-plperlu` (bare, 否定) | ❌ | ✅ | `true` | bool | mkfs が statfs 実測関数 / tablespace auto-mkdir に plperlu を使ってよいかの上位ゲート | untrusted plperlu の許可。`require`+`deny` は矛盾で mkfs エラー。auto+deny は nominal 相当。matrix は [docs/settings-and-plperlu.md](settings-and-plperlu.md) |

---

## reload ポリシー (Live / NextMount / Format)

各 `Field` は `Field.Reload` (`enum ReloadPolicy`) を持ち、**走行中の `pgfsctl config set` でどう反映されるか**を決める (設計の正は [runtime-control-plane.md §Phase 3 確定設計](runtime-control-plane.md))。これが「マウント中に変えられるか」の唯一の真。

| 種別 | 意味 | 該当フィールド |
|---|---|---|
| **Live** | 走行中の mount に即反映 (`config set`) | `logging.level` / `logging.output` / `database.retry_max_attempts` / `database.retry_initial_delay_ms` / `database.retry_max_delay_ms` / `mount.cache_max_entries` / `mount.cache_data_max_bytes` / `mount.negative_cache_ttl_ms` / `mount.write_back` / `mount.write_back_max_bytes` / `mount.write_back_interval_ms` / `mount.write_back_metadata` / `mount.write_back_max_inodes` / `mount.write_back_flush_timeout_ms` / `mount.write_back_metadata_exclusive_create` / `app.statfs` / `audit.enabled` |
| **NextMount** (既定) | 再マウントで反映 | `mount.mount_point` / `mount.max_write` / `mount.fallback_uname` / `mount.fallback_gname` / `database.connection` / `database.schema` / `database.prefix` / `database.tablespace` / `database.tablespace_path` / `database.notify_enabled` / `database.citus` / `database.workers` / `database.shard_count` / `database.shard_replication_factor` / `app.plperlu` |
| **Format** | mkfs 専用・以後不変 | `file_system.version` / `volume_label` / `cluster_size` / `default_chunk_size` / `max_file_size` |

> **この表が載せるのは `SaveTo != None` のフィールドだけ**である。**CLI 専用 (`SaveTo=None`) の 7 つ**
> — `help` / `clean` / `setting.file` / `setting.search_path` / `mount.foreground` /
> `database.super_connection` / `database.distribute_existing` — は**設定ファイルにも DB にも残らない**ので、
> 「走行中に変えられるか」という問い自体が立たない。**全 44 フィールドと突き合わせたとき、
> この 7 つが表に無いのは意図どおり**である (2026-09-21 に機械的に突合して確認した。
> 同じ突合をすると同じ 7 つが出るので、ここに書いておく)。

`config set` の反映経路は `(SaveTo, Reload)` で分岐する (詳細は runtime-control-plane.md のマトリクス):

- **Db + Live** (`audit.enabled` / `app.statfs`): `pgfs_settings` 書込み (永続) + `set` NOTIFY (即時 live)。
- **File + Live** (logging / cache / retry / write-back): `set` NOTIFY のみ = **エフェメラル live** (DB 行を作らない。永続化は手元 `pgfs.toml` 編集)。
- **Db + NextMount**: `pgfs_settings` 書込み (次回マウントで反映)。
- **File + NextMount**: `config set` 不可 (リモート toml は触れない) → 手元 `pgfs.toml` 編集を案内。
- **Format / None**: `config set` 拒否。

> 制御メッセージは `notify_enabled` に関係なく届く (Phase 3 P3-0: 制御 LISTEN は常時 ON、`notify_enabled` はデータ変更通知だけをゲート)。

## ライフサイクル別の集約

### 「mkfs 時に凍結される設定」(= ファイルシステム作成時点で固定)

- `file_system.version`
- `file_system.volume_label`
- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`
- `database.tablespace` / `database.tablespace_path` / `database.prefix` / `database.schema`

`audit.enabled` / `app.statfs` の値は Db + Live で変更できる。ただしサーバ側 statfs 関数の作成・削除は mkfs が担当し、Live set は DDL を実行しない。tablespace/schema 等は Format ではないが既存 FS の構造変更を行う設定ではない。
- `database.citus` (DB 保管、`--citus`。 File→Db)
- `app.plperlu` (DB 保管、`--plperlu`/`--allow-plperlu`/`--deny-plperlu`。plperlu 許可ゲート)
- **DB 権威の FS サイズ系**: `file_system.cluster_size` / `default_chunk_size` / `max_file_size` も  DB 保管化 (上記 §5 参照、生成 toml には書かない)

これらは mkfs 後に書き換えても、後続のマウントが従う保証はない (`pgfs_data.chunk_size` のように DB 側の列が真になっている場合は特に)。

### 「マウント起動時に読み、以降の扱いは reload ポリシー次第」

起動時に `RootConfig` へ解決されるのは共通だが、走行中の可変性は上「reload ポリシー」に従う:

- **NextMount (再マウントで反映)**: `mount.mount_point` / `mount.foreground` / `database.connection`
- **Live (`config set` で走行中に反映)**: `logging.level` / `logging.output` / `database.retry_*` / `mount.cache_max_entries` / `mount.cache_data_max_bytes` (全 Live 項目は上表を参照)
- `mount.fallback_uname` / `mount.fallback_gname` は DB 保管だが現状 NextMount

→ Phase 3 (runtime-control-plane.md) 以前は「マウント中に書き換える設定は存在しない」前提だったが、**`pgfsctl config set` + Live reload の導入で Live 項目は走行中に書き換わる** (`RootConfig` の該当プロパティは mutable・`Api.ApplySingleLive` が差し替える)。NextMount/Format は従来どおり不変。

### 「mkfs 時のみ参照、永続化しない」

- `clean` (CLI)
- `database.super_connection` (mkfs 時のみ。資格情報を保存しない)
- `help`

---

## 既知の問題

### `-f` / `-s` の helper context での silent 飲み込み

`mount(8)` helper の内部フラグ (`-i` `-f` `-n` `-s` `-v` `-N <ns>` `-t <type>`) は **helper context で起動された場合のみ** `ParseArguments` の最初で silent に読み飛ばされる ( context 依存に変更)。判定は「親プロセス comm が `mount` かつ positional 引数あり」の AND 条件。直接実行時はこの飲み込みが効かず、`-f` は `setting.file` の、`-s` は `database.schema` の短縮形として有効。

詳細 (全短縮形の一覧・判定ロジック・処理順) は [docs/fstab-support.md §短縮形の衝突](fstab-support.md#the-short-form-collisions) と [§`ConfigLoader.ParseCli` の処理順](fstab-support.md#the-processing-order-of-configloaderparsecli) を参照。

`mount.foreground` は  `-f` を Options から外したので衝突対象外 (`--foreground` のみ)。`-f` は **setting.file 専用**の短縮形。

### `database.super_connection` 値の永続化

`SaveTo=None` のため mkfs 後は破棄され、再 mkfs 時に再度指定が要る。これは意図的 (資格情報の安全)。

### `file_system.default_chunk_size` の使われ方

`Api` の chunk 操作は `pgfs_data.chunk_size` 列 (= 各データ行に保存された値) を真とする。設定値は新規 `data` 行を INSERT する際の初期値として使うのみで、既存ファイルには影響しない。
