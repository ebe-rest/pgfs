# pgfsctl 仕様

> **道順**: [docs/README.md](README.md) › **本書**
>
> **この doc が正である範囲**: **`pgfsctl` の仕様** — `config` (get / list / set) と `status`
> (Layer 1 クラスタ / Layer 2 FS 統計 / Layer 3 稼働プロセス) の CLI と出力、および `prune` (異常終了が残したものの掃除)。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [design/control-plane.md](design/control-plane.md) | コントロールプレーンの**設計** (制御メッセージ / `{prefix}mounts` / live 反映) |
> | [design/settings-matrix.md](design/settings-matrix.md) | 全設定項目と reload ポリシー (何が live で変えられるか) |
> | [Mkfs.md](Mkfs.md) / [Mount.md](Mount.md) / [Assign.md](Assign.md) | 他ツールの仕様 |
> | [../tests/docker/README.md](../tests/docker/README.md) | docker ランナー (`control_plane_ctl.sh` / `status.sh`) |

PGFS の **実行時コントロールプレーン CLI** `pgfsctl` の仕様です。`config`（設定の参照・変更）と `status`（クラスタ稼働状況 / FS 統計）の 2 サブコマンドを持ちます。

設計の正は [docs/design/control-plane.md](design/control-plane.md)。実装は [src/ctl/](../src/ctl/)（`Pgfs.Ctl`・出力 `pgfsctl`）で、ロジックは Core の [ConfigAdmin](../src/core/src/Config/ConfigAdmin.cs) / [StatusAdmin](../src/core/src/Api/StatusAdmin.cs)（GUI も再利用可）。

## 役割と位置づけ

- `mkfs.pgfs` / `mount.pgfs` / `assign.pgfs` が「FS を作る・マウントする」ツールなのに対し、`pgfsctl` は**動いている FS を運用する**ツール（systemctl 風の admin コマンド）。
- **FUSE / Dokan に依存せず Core のみ**を参照するので、Linux / Windows どちらでも動く。
- 命名は `{役割}.pgfs` 規約（`mkfs.pgfs` 等）の**意図的な例外**で、admin ツールらしい 1 語 `pgfsctl`（[runtime-control-plane.md §Phase 3 P3-1](design/runtime-control-plane.md)）。

## 接続オプション（全サブコマンド共通）

接続先は `mount.pgfs` と同じ [ConfigLoader](../src/core/src/Config/ConfigLoader.cs) で解決します（CLI + `pgfs.toml`）。

| オプション | 意味 |
|---|---|
| `-c` / `--connection <connstr>` | PGFS 接続文字列（kv 形 / `postgresql://` URL 形）|
| `-s` / `--schema <name>` | スキーマ名（既定 `public`）|
| `-x` / `--prefix <prefix>` | テーブル接頭辞（既定 `pgfs_`）|
| `-f` / `--setting-file <toml>` | 設定ファイル名 |
| `--setting-path <dirs>` | 設定ファイル探索パス（カンマ区切り）|

`pgfs.toml` が探索パス上にあれば CLI 未指定分はそこから補完されます（優先順位は CLI > TOML > DB > 既定）。詳細は [settings-matrix.md](design/settings-matrix.md)。

---

## `config` — 設定の参照・変更

```
pgfsctl config list [--json] [接続オプション]
pgfsctl config get <scope.key> [--json] [接続オプション]
pgfsctl config set <scope.key> <value> [接続オプション]
```

- **`config list`** … 全設定項目を `scope.key` 順に「実効値 + 出所 + 永続化先 + reload ポリシー」で一覧。接続文字列の Password はマスク。
- **`config get <scope.key>`** … 単一項目を同様に表示。
- **`config set <scope.key> <value>`** … 値を検証（`Field` の Parse）して、下のマトリクスに従い**永続化 / ライブ反映 / 拒否**を出し分ける。

出所（source）は `config`（CLI/TOML）/ `db`（`{prefix}settings`）/ `default` の 3 値。`--json` は機械可読出力（Phase 5 GUI 用）。

### `config set` の挙動（`(SaveTo, Reload)` で分岐）

| (SaveTo, Reload) | 例 | 永続化 | ライブ反映 |
|---|---|---|---|
| Db, Live | `audit.enabled` / `app.statfs` | `{prefix}settings` 書込み | ✅ 即時（`set` NOTIFY）|
| File, Live | `logging.level`/`output` / `cache_*` / `retry_*` | **なし（ephemeral）** | ✅ 即時（`set` NOTIFY）|
| Db, NextMount | `fallback_*` / `tablespace*` / `citus` / `plperlu` | `{prefix}settings` 書込み | 次回マウント |
| File, NextMount | `mount_point` / `connection` / `schema` / `prefix` / `notify_enabled` | なし（remote toml 不可）| 次回マウント（手元 toml 編集を案内）|
| Format | `file_system.*` | 拒否 | — |
| None | `--clean` / `foreground` / `setting.*` | 拒否 | — |

- ライブ反映は走行中 mount が NOTIFY を受けて即適用する。**制御チャネルは `notify_enabled` に関係なく常時 ON**（[P3-0](design/runtime-control-plane.md)）なので、単一クライアント mount（既定 notify OFF）にも届く。
- File 保管 Live は **DB 行を作らないエフェメラル反映**（remount でベースライン = toml に戻る。永続化したい場合は各クライアントの `pgfs.toml` を編集）。
- 出力の mount 数は `{prefix}mounts` 登録表の現在行数であり、適用成功数ではない（NOTIFY は ack を取らない）。
- **write-back の Live off は二相 flip で drain を待つ** (修正済。第 1 相で受付を止め、書き切ってからモードを落とす)。ただし **CLI の成功を全 mount の保存成功と解釈しない** — NOTIFY は ack を取らないので、届いたかは `status` の実効設定で確かめる。
- `status` の Layer 3 (実効設定) には **`mount.write_back_metadata_exclusive_create`** も出る (追加)。`defer` になっているマウントを見つける場所はここ。
- **メタデータ write-back の実効モード** (追加): `write-back(m): on (受付停止中 = 実効 off)` と出る。live off の二相 flip の第 1 相では、**設定は on のままでも新規 pending を受け付けていない**ため。遷移時に即書きされるので、flip が長くても正しく見える。
- **`handles.inodes` は両 OS で動く** (追加・handle-context C-1): **開かれている実体 (inode) の数**。
  ハンドル数ではなく**実体の種類数**なので、同じファイルを 3 本開いていれば 1 である。両アダプタが
  `Api.OpenHandle` / `CloseHandle` を呼んで数えている。
- **`handles` 行の `open` / `peak` は FUSE のマウントでしか埋まらない** (追加・handle-context ⑥):
  `handles      : N open / peak M / K inodes` のうち **`open` / `peak` は Core のハンドル表 (`HandleTable`)**
  に載っている数である。**Dokan はこの表を通さず `DokanFileInfo.Context` にオブジェクトを直接載せる**ので、
  **Windows のマウントは常に `0 open / peak 0`** になる (「開いていない」ではなく「数えていない」)。
  **`inodes` へ寄せることはしない** — **2 つは別のものを測っている**からで、`open` (ハンドル数) と
  `inodes` (実体数) が別々に見えると、**ハンドルだけ漏れているのかカウントだけ漏れているのかを切り分けられる**
  (Linux 側と合意)。
- **エラーステートの鮮度** (追加): 赤行に `[heartbeat 42s 前の情報]` を併記する。エラーステートは heartbeat 経由でしか届かないので、**DB に書けない障害では赤が出ないまま緑に見える**。Layer 3 のホスト行の `[stale: heartbeat 5m 前]` (赤) が唯一の手掛かりになるので、そこを先に見ること。遷移そのものは heartbeat 周期 (30 秒) を待たず即書きされる。
- **喪失した unmount の墓標** (追加): `{prefix}mounts` に残った行を Layer 1 の `LIVE` 欄で `ENDED` と表示し、続けて赤で `!! write-back UNFLUSHED LOSS (N mount(s))` と内訳を出す。**自動では消えない**ので、確認したら `DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0` で消す。

reload ポリシー（Live / NextMount / Format）の全項目割り当ては [settings-matrix.md §reload ポリシー](design/settings-matrix.md)。

### 例

```bash
# 走行中 mount のログレベルを debug に（File+Live・ephemeral）
pgfsctl config set logging.level debug -c "$CONN" -s pgfs

# 監査ログを走行中に有効化（Db+Live・永続 + live）
pgfsctl config set audit.enabled true -c "$CONN" -s pgfs

# 全設定を JSON で
pgfsctl config list --json -c "$CONN" -s pgfs
```

---

## `status` — 稼働状況 / FS 統計

```
pgfsctl status [--json] [接続オプション]
```

DB 由来の read-only 情報を 1 コマンドでセクション表示します（mount 不要）。3 層すべて実装済み。

1. **Mounts（クラスタ稼働一覧・Layer 1）** … `{prefix}mounts` 登録表から host / pid / mode（`fuse`/`dokan`）/ mountpoint / uptime / heartbeat 経過 / live?（経過 < 90s）。テーブル不在（Phase 2 前の mkfs）は「table not present」と表示。
2. **Filesystem（FS 統計・Layer 2）** … schema/prefix/version/volume_label、inode 数、file 数、chunk 数、使用バイト（`sum(length(payload))`）、cluster_size、max_file_size、audit on/off、Citus 有無（+ `pg_dist_node` 数）。集計失敗値は `?`。
3. **Process detail（稼働プロセス詳細・Layer 3）** … 各 mount が直近 heartbeat 時に書いたスナップショット（[P4-2/P4-4](design/runtime-control-plane.md)）。**inode キャッシュ**（entries / capacity / hit率 / hits・misses・evictions）、**content キャッシュ**（chunk 数 / bytes / max / hit率 / hits・misses・evictions）、**write-back**（dirty bytes/files、flush/failure、pending inode/監査、error state 等）、**handles**（開いているハンドル数 / これまでの最大数 / **開かれている実体の数**）、**notify**（listen / data / connected）、**実効 config**（走行中 `RootConfig` の運用関連サブセット = logging/retry/cache/statfs/audit/version 等・values-only・password 非含）。live でない行は `[stale]` 付き。

> 鮮度は「直近 heartbeat 値」。snapshot は register 直後 + 30s heartbeat + `ping` 制御 NOTIFY 受信で更新される（req-rep はしない）。CLI が即時を要するときは `pg_notify('{schema}_{prefix}notify','{"c":"ping"}')` を送って更新を要求できるが、応答待ちをしないため直後の status に反映済みとは限らない。

`--json` 出力は `{ "mounts": { "table_present", "rows": [ { …, "stats": {…}, "config": {…} } ] }, "fs": {…} }`（各 mount 行に `stats`/`config` を入れ子オブジェクトで同梱）。

### 例

```bash
pgfsctl status -c "$CONN" -s pgfs            # セクション表示
pgfsctl status --json -c "$CONN" -s pgfs     # GUI / スクリプト用
```

---

## `prune` — 異常終了が残したものの掃除 (追加)

```
pgfsctl prune [--apply] [--force] [--mounts-older-than <sec>] [--json] [接続オプション]
```

**既定は dry-run**。数えて見せるだけで何も消しません。`--apply` で実行します。
**消すものを先に目で見られない掃除コマンドは危ない**ので、この既定は変えません。

対象は 3 つで、どれも **「異常終了したマウントが残したものを、誰も掃除しない」** という同じ形です
(設計は [handle-context.md §段階 C の Core 設計](design/handle-context.md)):

| 残骸 | 何が残るか | 害 |
|---|---|---|
| **`{prefix}mounts` の古い行** | 正常終了でしか消えないので、`kill -9` のたびに溜まる | **`status` の見た目だけ** |
| **孤児 data 行** | 段階 C-2 が「開いている間は実体を残す」ので、その最中にデーモンが死ぬと残る | 名前空間には出ないが **`df` に乗る** |
| **`.fuse_hidden*`** | `hard_remove = 0` の libfuse が open 中の `unlink` を逃がした跡 | **中身ごと永久に残り、他マウントの `ls` にも出る** |

> **`.fuse_hidden*` は名前を厳密に見ます** — libfuse が作るのは **`.fuse_hidden` + 16 桁の 16 進**で、
> **この書式に一致するものだけ**が対象です。`.fuse_hidden_notes.txt` のように利用者が自分で付けた名前は
> **残骸として扱いません** (そうしたファイルは `ls` にも普通に見えています)。
> 判定は列挙側と同じ関数 (`Api.IsLibfuseHidden`) に一本化してあります。

### 安全弁 (**種類ごとに live 判定を分ける**)

- **`{prefix}mounts` の行**は heartbeat が `--mounts-older-than` (既定 **3600 秒**) より古ければ消します。
  **これは「別ホストの行を生きているとみなす上限」でもあります** (下記)。**下限は 600 秒**で、それより
  小さい値 (0・負の値を含む) は拒否します — 小さくすると**別ホストで生きているマウントを死んだ扱いにして、
  使用中の実体を消す**ためです。
- **データを消す側 (孤児 data / `.fuse_hidden*`) は、生きているマウントが 1 つでもあれば触りません。**
  `--force` で上書きできますが、**全マウントを止めてから**使うものです。
- **`{prefix}mounts` を読めないとき (未移行の既存 FS など) は、`--force` でもデータを消す側に触りません。**
  誰が生きているか分からない = 「居ない」ではなく「見えない」からです。先に移行
  ([CHANGELOG.md](../CHANGELOG.md) §移行が必要な変更) を流してください。JSON では
  `applied.skipped_because_unknown = true` になります。
- **マウントは、自分の登録行が消えていたら heartbeat の周期 (30 秒) で登録し直します**。起動時の DB の
  瞬断で登録に失敗したマウントも同じです。以前は一度消えると以後ずっと見えず、上の「生きている
  マウントが居れば触らない」の数に入りませんでした。
- **喪失を記録した行 (墓標) は消しません。** 書き戻せなかった件数を運用に伝えるために残してあります
  (B-2)。確認したら手で消してください。

> **なぜ「heartbeat が古い」だけで消さないのか**: **DB 障害で heartbeat を落とした生きているマウント**の
> 行を消すと、**次の実行からそのマウントが見えなくなり、「使用中の実体」を消すカスケード**になります。
> **表示だけの害である `mounts` の掃除のために、データを消す側の判断材料を壊さない**、という切り分けです。

> **pending inode を巻き込まないための条件でもあります**。メタデータ write-back の pending inode は
> **走行中のマウントのメモリにしか無い**ので、その実体は「参照されていない」ように見えます。
> 生きているマウントが 1 つも無いことを条件にすれば構造的に避けられます。


#### `kill -9` の直後は 90 秒だけ掃除できなかった (修正済)

**`kill -9` された行は heartbeat が新しいまま残る**ので、**死んだ直後の 90 秒間は live に見え**、
**データを消す側が止まっていた** (= クラッシュ直後に掃除できない)。レビュー ④ の
「同一ホストなら pid の生存も見る」をそのまま実装して塞いだ:

- **同じホストの行は pid の生存で判定する**。プロセスが居ない → 死んだ行とみなす。
- **pid が再利用されている**可能性があるので、**プロセス名が `assign.pgfs` / `mount.pgfs` か**も見る。
- **別ホストの行は pid を確かめようがない**ので、**猶予 (`--mounts-older-than`・既定 3600 秒) を
  超えるまでは生きているとみなす**。

#### heartbeat を落としただけの生きているマウントを死んだ扱いにしていた (修正済)

上の修正は「新しい heartbeat を持つ死んだ行を**降格**する」向きだけで、**逆向き (古い heartbeat を
持つ生きている行の**昇格**) が効いていませんでした**。2 つを AND で繋いでいたためです。

結果、**heartbeat の 90 秒と猶予の 3600 秒のあいだが「live でも stale でもない」空白帯**になり、
**DB が詰まって heartbeat を数分書けなかっただけのマウント**が死んだ扱いになりました。そこで
`--apply` を撃つと、**そのマウントが開いている最中の実体 (`{prefix}data` + chunk) を消します**。

判定を**ホストで分ける**形に変えました。

| 行 | いまの判定 |
|---|---|
| **同じホスト** | **pid の生存が答え。heartbeat は見ない** (プロセス名が `assign.pgfs` / `mount.pgfs` かも見る) |
| **別ホスト** | pid を確かめようがないので、**猶予を超えるまでは生きているとみなす**。超えた行は同じ実行で `{prefix}mounts` から消える対象でもあるので、そこまで待てば判断材料そのものが無くなる |

> **表示の live (`status`) と、データを消してよいかの live (`prune`) は別の問いです。**
> `status` は「いま動いていそうか」を 90 秒で見せれば足りますが、`prune` は**間違えるとデータが消える**ので、
> 同じしきい値を流用しません。

### 例

```bash
pgfsctl prune                      # dry-run: 何が残っているかを見る
pgfsctl prune --apply              # 掃除する (データ側は live が居ると飛ばす)
pgfsctl prune --apply --force      # 全マウントを止めたうえで、データ側も強制的に掃除
pgfsctl prune --mounts-older-than 86400 --apply   # mounts の行は 1 日より古いものだけ
pgfsctl prune --json               # 機械可読 (dry_run / mounts / orphan_data / fuse_hidden / applied)
```

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 引数不正 / 未知サブコマンド / `config set` の拒否（Format/File+NextMount/未知キー/検証失敗）/ 接続失敗等 |
