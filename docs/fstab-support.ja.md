# /etc/fstab 対応 (Linux)

`mount.pgfs` を `/etc/fstab` および `mount(8)` から呼べるようにするための仕様。バイナリ名 `mount.pgfs` は既に Linux の `mount(8)` が探す `/sbin/mount.<type>` 規約と一致しているため、対応すべきは **呼び出し規約の受け口** と **デーモン化** の 2 点に絞られる。

英語版は [fstab-support.md](fstab-support.md) を参照してください。

`-o allow_other` 等の FUSE オプションは、活発フォーク [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) (`securefolderfs-community/Tmds.Fuse`) の `MountOptions.Options` 経由で libfuse に渡る。このフォークは `use_ino` を init callback で設定する downstream パッチも含む。

---

## 動機

手動の `mount.pgfs -c <conn> -m <mountpoint>` に加え、`/etc/fstab` に行を書いて起動時マウント / 短い `mount /mnt/pgfs` で上げられるようにする。副次効果として `systemd` の `*.mount` ユニットや autofs との連携も自然に可能になる (どちらも内部で `/sbin/mount.<type>` を呼ぶため)。

---

## `mount(8)` の helper 呼び出し規約

`mount -t pgfs <source> <target> -o <opts>` または fstab 行の処理時、`mount(8)` は以下のように helper を呼び出す:

```text
/sbin/mount.pgfs <source> <target> [-i] [-f] [-n] [-s] [-v] [-N <ns>] [-t <type>] [-o <opts>]
```

各フラグの意味と mount.pgfs での扱いは [mount(8) と mount.pgfs の引数対応表](#mount8-と-mountpgfs-の引数対応表) を参照。

helper は **マウント完了後に exit する**ことが期待される (mtab 更新のため)。フォアグラウンドで居座ると `mount` コマンドが返らない。

---

## `mount(8)` 経由マウント時の引数の流れ

fstab を書いておいて **マウント先だけ** (`sudo mount /mnt/pgfs`) で叩けるのは、`mount(8)` が間で展開してくれているから。`mount.pgfs` 自身は fstab を読まない (= 標準的な FUSE helper の責務分担)。

### fstab エントリ例

```text
postgresql://pgfs@pgsql_server/pgfs   /mnt/pgfs   pgfs   _netdev,allow_other,cache-max-entries=4096   0 0
```

| 列 | 内容 | 値 |
|---|---|---|
| 1 | source (fs spec) | `postgresql://pgfs@pgsql_server/pgfs` |
| 2 | target (マウントポイント) | `/mnt/pgfs` |
| 3 | type | `pgfs` |
| 4 | options | `_netdev,allow_other,cache-max-entries=4096` |
| 5 | dump (使わない) | 0 |
| 6 | pass (使わない) | 0 |

### 実行フロー

ユーザーが `sudo mount /mnt/pgfs` と打つと:

1. **`mount(8)`** が引数 `/mnt/pgfs` を「fstab 第 2 列のマウントポイント」と解釈
2. `/etc/fstab` を走査して 2 列目が一致する行を探す → 上記エントリにヒット
3. 1 列目 (source) / 3 列目 (type) / 4 列目 (options) を読み込み
4. type が `pgfs` なので `/sbin/mount.pgfs` を helper として fork+exec
5. **`mount.pgfs` が受け取る argv はフル展開**:
   ```text
   /sbin/mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs -o _netdev,allow_other,cache-max-entries=4096
   ```
   (mount(8) 側の `-n` `-v` `-f` 等が指定されていれば併せて付与される)

`mount.pgfs` 内では positional[0] / positional[1] / `-o` パーサが粛々と値を流す。`-o` の中身は `ParseDashOOptions` で展開される (`_netdev` は無視、`allow_other` は `FuseFlags` 経由で libfuse に転送、`cache-max-entries=4096` は `mount.cache_max_entries` を上書き)。

このとき [`IsMountHelperContext`](../src/lib/src/Config/ConfigLoader.cs) は親 comm = `mount` と positional 存在を確認して **true** を返し、`MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` が有効化される (= helper context 専用の silent 飲み込み)。

### `mount(8)` を経由せず `mount.pgfs` を直接叩く場合

`sudo mount.pgfs /mnt/pgfs` のように打つと **動かない**。`mount.pgfs` 単独では fstab を読まないため:

- positional[0] = `/mnt/pgfs` → 先頭が `postgresql:` でないので **設定ファイルパスとして解釈** ([§位置引数](#1-位置引数-source--target-の受け入れ))
- `LoadFromFile` が `/mnt/pgfs` を TOML として開こうとする → ディレクトリで失敗、または不存在で silent 無視
- `database.connection` が未設定のまま PG 接続を試みる → デフォルト (localhost:5432) に当たって失敗

マウント先だけで動かしたい場合は **必ず `mount(8)` を経由する** (`sudo mount /mnt/pgfs` または `sudo mount -a` で fstab 一括) こと。

---

## `mount(8)` と `mount.pgfs` の引数対応表

`mount.pgfs` は (a) `mount(8)` helper として呼ばれた場合 と (b) ユーザーが直接実行した場合 の両方を受ける。同じ短縮形が異なる意味を持つことがあるため、起動時に [`IsMountHelperContext`](../src/lib/src/Config/ConfigLoader.cs) で context を判定し、helper context のときだけ mount(8) 内部用フラグを silent に読み飛ばす。

### Helper context の判定

以下 **両方** が true なら helper context:

1. `args` のどれかが `-` で始まらない (= positional 引数が存在する)
2. 親プロセスの comm (Linux: `/proc/<ppid>/comm`) が `"mount"`

どちらかが false なら直接実行扱いで `MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` を適用しない (= `-f` / `-s` を本来の短縮形として効かせる)。Linux 以外では親プロセス取得が空振りするため常に直接実行扱い (= helper context 機構を持たない `pgfs.assign` (Windows) でも同じコードが安全に動く)。

### `mount(8)` が helper に渡す引数の処理

| `mount(8)` 引数 | `mount(8)` の意味 | helper context | 直接実行 | 備考 |
|---|---|---|---|---|
| **positional[0]** (= fstab 1 列目 `<source>`) | FS の「データ源」 | `postgresql:` で始まる → `database.connection`、それ以外 → `setting.file` (TOML パス) | 同左 | 両義サポート ([§位置引数](#1-位置引数-source--target-の受け入れ)) |
| **positional[1]** (= fstab 2 列目 `<target>`) | マウントポイント | `mount.mount_point` に流す (未設定時のみ) | 同左 | |
| `-o <opts>` (= fstab 4 列目) | カンマ区切りオプション | [`ParseDashOOptions`](../src/lib/src/Config/ConfigLoader.cs) でカンマ split → `Field.CliOptions` / `Field.EffectiveDashOName` と照合 / `allow_other` 等は `MountConfig.FuseFlags` へ / `_netdev` / `noauto` / `noatime` 等は無視 | 同左 | 詳細 [§-o パーサ](#2--o-keyvalflag-パーサ) |
| `-i, --internal-only` | helper を呼ばない指示 | silent 読み飛ばし | (mount.pgfs 固有フラグ未割当のため) 未知オプション warning | mount(8) は実際には helper に渡さないので保険 |
| `-f, --fake` | dry-run | silent 読み飛ばし → 実際はマウントしてしまう (TODO: fake 尊重) | `setting.file` の短縮形として有効 | |
| `-n, --no-mtab` | `/etc/mtab` を更新しない | silent 読み飛ばし | 未知オプション warning | FUSE では mtab は fusermount3 が書く |
| `-s, --sloppy` | 未知オプションを無視 | silent 読み飛ばし | `database.schema` の短縮形として有効 | direct 側はもともと未知オプションを warning + 継続なので実質 sloppy デフォルト |
| `-v, --verbose` | 詳細ログ | silent 読み飛ばし | 未知オプション warning | TODO: `-v` で `--log-level debug` 相当に格上げ |
| `-N <namespace>` | 別 mount namespace | silent 読み飛ばし (値も) | 未知オプション warning + 値が positional になる可能性 | namespace 切替は親側で |
| `-t <type>` | FS タイプ | silent 読み飛ばし (値も) | 同上 | helper 名で既に決まっている |

### `mount.pgfs` 固有のフラグ (= `mount(8)` 経由では渡って来ない)

[settings-matrix.md](settings-matrix.md) に網羅があるが、主要なものだけ抜粋:

| `mount.pgfs` フラグ | マップ先 | 備考 |
|---|---|---|
| `-c <conn>` / `--connection` / `--connection-string` | `database.connection` | kv 形式 (`Host=...;Port=...`) を渡したいときはこちら |
| `-m <path>` / `--mount-point` | `mount.mount_point` | positional[1] と同義 |
| `-f <path>` / `--setting-file` / `--setting` | `setting.file` | TOML パス。**`-f` は直接実行時のみ有効** (helper context では `--fake` として silent 飲み込み)。positional[0] (非 `postgresql:`) でも同じ意味 |
| `-s <name>` / `--schema` / `--schema-name` | `database.schema` | **`-s` は直接実行時のみ有効** (helper context では `--sloppy`)。`--schema` は常時 OK |
| `-x` / `--prefix` | `database.prefix` | テーブル名接頭辞 |
| `--cache-max-entries <N>` | `mount.cache_max_entries` | |
| `--fallback-uname <name>` / `--fallback-gname <name>` | `mount.fallback_uname` / `_gname` | OS で uname 解決不能時の逃げ先 |
| `--log-level <level>` / `--log-output <spec>` | `logging.level` / `logging.output` | |
| `--foreground` | `mount.foreground` | デーモン化を抑制。短縮形 `-f` は setting.file と衝突するため [意図的に持たない](#短縮形の衝突) |
| `--retry-max-attempts` / `--retry-initial-delay-ms` / `--retry-max-delay-ms` | `database.retry_*` | 接続オープン時の transient エラー再試行 |
| `-?` / `-h` / `--help` | `help` | 使い方表示 → 即終了 |

### 短縮形の衝突

`mount(8)` 由来の短縮形が `mount.pgfs` 固有のフラグと同じスペルになる箇所:

| 短縮形 | `mount(8)` での意味 | `mount.pgfs` での意味 | helper context での挙動 | 直接実行での挙動 |
|---|---|---|---|---|
| `-f` | `--fake` (dry-run) | `setting.file` の短縮形 | silent 飲み込み (mount(8) 側が勝つ) | `--setting-file` として有効 |
| `-s` | `--sloppy` | `database.schema` の短縮形 | silent 飲み込み (mount(8) 側が勝つ) | `--schema` として有効 |
| `-n` `-v` `-i` `-N` `-t` | mount(8) の各内部フラグ | (未割当) | silent 飲み込み | 未知オプション warning |

`mount.foreground` は `--foreground` のみで受ける (短縮形なし)。`-f` を持たせると setting.file と重複し、どちらが優先されるかが Field 宣言順依存になるため。`-f` は **setting.file 専用** の短縮形と整理。

### `ConfigLoader.ParseCli` の処理順

引数を 1 つずつ走査し、最初に該当した分岐で確定する:

1. **helper context のときのみ** `MountHelperFlagsNoValue.Contains(arg)` → silent continue (`-i` `-f` `-n` `-s` `-v`)
2. **helper context のときのみ** `MountHelperFlagsWithValue.Contains(arg)` → 次トークンも読み飛ばし、continue (`-N` `-t`)
3. `arg == "-o"` → 次トークンを `ParseDashOOptions` に委譲
4. `all.FirstOrDefault(s => s.Options.Contains(arg, ...))` で `mount.pgfs` 固有フラグを照合
5. 4 で見つからず `-` で始まらない → 位置引数として消費 (positional[0] / [1])
6. 4 で見つからず `-` で始まる → 未知オプション warning

helper context 判定の前計算は `ConfigLoader` コンストラクタの冒頭で 1 回だけ行う (= `args` 全体を見るので `args.Any(a => !a.StartsWith('-'))` で positional 有無を判定し、その後で親プロセス名を読む)。

---

## 実装

### 1. 位置引数 (source / target) の受け入れ

[`ConfigLoader.ParseCli`](../src/lib/src/Config/ConfigLoader.cs) は位置引数を 2 つまで拾う:

| 位置 | 役割 | マップ先 |
|---|---|---|
| 1 つ目 | source | `postgresql:` で始まる → `Database.Connection.Value`、それ以外 → `Setting.File.Value` (= 設定ファイルパス)。どちらも未読込時のみ反映。明示 `-c` / `-f` / `-o connection=` が優先 |
| 2 つ目 | target (マウントポイント) | `Mount.MountPoint.Value` (空の場合のみ。明示 `-m` や `-o mount_point=` が優先) |

source の判別ヒューリスティック:
- `postgresql://user@host:port/dbname` → URL 形式の接続文字列とみなして `Connection` に流す
- `/etc/pgfs.toml` / `pgfs.toml` 等 → 設定ファイルパスとみなして `Setting.File` に流す (続けて `LoadFromFile` が TOML を読む)
- 接続文字列を kv 形式 (`Host=...;Port=...`) で渡したいときは `-c` で明示する

これにより 3 通りの呼び出し方が等価に動く:

```bash
# (a) 従来通り: 接続 URL を source に
mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs

# (b) 設定ファイルを source に (TOML に connection / mount_point を持たせる)
mount.pgfs /etc/pgfs.toml /mnt/pgfs
mount.pgfs /etc/pgfs.toml       # mount_point も TOML 任せ

# (c) -f / -m を明示 (どちらでも)
mount.pgfs -f /etc/pgfs.toml -m /mnt/pgfs
```

#### Connection 列の書式

`postgresql://` URL 形式が標準。ホスト名にカンマを含めるとパーサが壊れるので注意。fstab 4 列目の `,` と区別がつかなくなる場合は `-o connection=...` 経由に逃がす。

### 2. `-o key=val,flag,...` パーサ

```text
args の中の "-o" の次トークンを取り出して
カンマで split → 各要素を "=" で split → key/value
key を s.Options に一致する Setting にマップして value を流し込む

例: -o noatime,_netdev,allow_other,connection=postgres://...,cache-max-entries=4096

  noatime              → カーネル側フラグ。FUSE に渡さず無視 (もしくは mount syscall flags に渡す)
  _netdev              → fstab 用のヒント (起動時にネットワークを待つ)。無視で良い
  allow_other          → FUSE フラグ。Tmds.Fuse の MountOptions に転送
  connection=...       → Database.Connection.Value に書き戻す
  cache-max-entries=4096 → Mount.CacheMaxEntries.Value に書き戻す
```

マッピング表:

| `-o` キー | マップ先 / 扱い |
|---|---|
| `connection=...` | `Database.Connection.Value` |
| `super_connection=...` | `Database.SuperConnection.Value` (mount で使うかは要検討。基本は mkfs 専用) |
| `mount_point=...` | `Mount.MountPoint.Value` |
| `cache-max-entries=...` | `Mount.CacheMaxEntries.Value` |
| `log-level=...` | `Logging.Level.Value` |
| `setting-file=...` | `Setting.File.Value` |
| `allow_other` | FUSE `MountOptions` (Tmds.Fuse 側) |
| `default_permissions` | FUSE `MountOptions` |
| `ro` / `rw` | FUSE `MountOptions` |
| `nosuid` / `nodev` / `noexec` | カーネル側フラグ (現状無視で問題ない) |
| `_netdev` / `noauto` / `user` / `users` | fstab 専用フラグ、helper では無視 |
| `noatime` / `relatime` / `atime` | atime ポリシー (将来 inode の `atime` 列を入れたら実装) |

未知の `-o` キーは `ConfigLoader.ParseDashOOptions` が `Warnings` に積み、`Console.Error` 出力相当に流す (silent drop しない)。

### 3. デーモン化

`mount(8)` は helper の exit を待つが、[`Program.RunFuseMountAsync`](../src/mount/src/Program.cs) は `await fuseMount.WaitForUnmountAsync()` でブロックし続けるので、fstab 経由だと `mount` コマンドが永遠に返らない。そのため mount.pgfs は子プロセスを分離する (sshfs 方式):

- 親 (mount(8) から呼ばれた側) は `Process.Start(自分自身, 元の args + "--foreground-internal")` で子を起動
- 親は子の stdout を少しだけ読み、「マウント成功」シグナル (`PGFS_MOUNTED_OK` 行) を見たら exit
- 子は通常通りマウントし `WaitForUnmountAsync` で永続

.NET だけで完結し、Windows 互換も保てる (Windows は `Assign` を使うので関係ないが、同じコードパスが安全に動く)。`--foreground` を付けると子分離せず、自身がフォアグラウンドで FUSE ループを回す (テスト / 手動運用で推奨)。

systemd ジェネレータ方式 (`*.mount` ユニットを吐いて exit、実体は常駐サービス) も検討したが、大袈裟で fstab 単体で完結しないため却下した。

### 4. アンマウント

fstab 経由でマウントしたものは `umount /mnt/pgfs` で外す。これは `mount(8)` の対称コマンドで、`/sbin/umount.<type>` が無ければ FUSE 経由 (`fusermount3 -u`) でアンマウントされる。`umount.pgfs` を別途実装する必要は無い: `fusermount3 -u` がカーネル側のマウントを切ると、子プロセスの `WaitForUnmountAsync` が戻り、子も自然に exit する。

---

## インストール

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs

# fuse.pgfs 名でも引きたい場合 (mount -t fuse.pgfs ... や fstab で fuse.pgfs と書く場合)
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
```

インストーラ (`install.sh` / Makefile / RPM / DEB) は将来。当面は手作業で良い。

`/etc/fuse.conf` に `user_allow_other` を入れておくと、一般ユーザーが `allow_other` 付きでマウントできる:

```text
# /etc/fuse.conf
user_allow_other
```

---

## fstab エントリ例

### システム mount (root が起動時にマウント)

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  pgfs  noatime,_netdev,allow_other,cache-max-entries=8192  0 0
```

- `_netdev` を入れておくとネットワーク起動後にマウントが走る (PG リモートの場合に必須)
- `0 0` は dump 不要 / fsck 不要

### ユーザー mount

```fstab
postgresql://pgfs@localhost/myfs  /home/me/pgfs  pgfs  user,noauto,allow_other  0 0
```

- `noauto` で起動時はマウントしない
- `user` で一般ユーザー (fstab に書いた本人) が `mount /home/me/pgfs` できる
- `allow_other` を使うなら `/etc/fuse.conf` の `user_allow_other` が必要

### fuse.pgfs 形式

`/sbin/mount.fuse.pgfs` シンボリックリンクを置いた場合:

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  fuse.pgfs  noatime,_netdev  0 0
```

`mount(8)` は `fuse.*` 型を見ると `mount.fuse.<name>` を探すフォールバックを持っているので、これでも動く。

---

## 動作確認

```bash
# バイナリを作る
dotnet publish -c Release
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
sudo mkdir -p /mnt/pgfs

# mount(8) 経由 (どちらの type でも可)
sudo mount -t pgfs       -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs
sudo mount -t fuse.pgfs  -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs

mountpoint -q /mnt/pgfs   # → 0 (マウント済み)
ls -al /mnt/pgfs          # → 一般ユーザーでも読める (`-o allow_other`)
sudo umount /mnt/pgfs     # → アンマウント
```

直接呼び出し (`/sbin/mount.pgfs` install 前) でもフローを確認できる:

```bash
# 位置引数 + -o, no -f → デフォルトの子プロセス分離モード
bin/Publish/mount.pgfs \
    "Host=pgsql_server;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer" \
    /mnt/pgfs \
    -o cache-max-entries=2048,allow_other,_netdev,noatime \
    --log-level info
# → 親プロセスは子からの MOUNTED シグナル受信後すぐに exit 0
mountpoint -q /mnt/pgfs                # → 0 (マウント済み)
ps -ef | grep "Publish/mount.pgfs"     # → 子が --foreground-internal 付きで生存
ls /mnt/pgfs                           # → ディレクトリ内容が見える
fusermount3 -u /mnt/pgfs               # → アンマウント (子も自然終了)
```

`-o allow_other` / `-o attr_timeout=N` 等は [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) の `MountOptions.Options` 経由で libfuse に渡る。`_netdev` / `noatime` 等の fstab/カーネル側ヒントは silent に無視。未知の `-o` キーは stderr に警告。

> `mount(8)` から呼ばれた helper は env が PATH を含めて剥がされる (実測値で LANG, LOGNAME, PWD, SHLVL, SUDO_*, TERM, USER, _ の ~11 個のみ)。Tmds.Fuse の `HasFusermount` は `$PATH` から `fusermount3` を探すので、PATH 無しだと `CheckDependencies` が false を返す。`Mount/Program.cs` の `Main` 冒頭で PATH が空なら最低限の PATH を補う。

---

## Tmds.Fuse のフォーク採用と downstream パッチ

本家 `tmds/Tmds.Fuse 0.1.0-190711-50` は 2019 年から更新無く、`MountOptions` が `SingleThread` しか公開しないため `-o allow_other` 等を libfuse に渡せない。活発フォーク [`securefolderfs-community/Tmds.Fuse`](https://github.com/securefolderfs-community/Tmds.Fuse) (.NET 10 ターゲット) をさらに [`ebe-rest/Tmds.Fuse`](https://github.com/ebe-rest/Tmds.Fuse) にフォークし、[vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/) に submodule で取り込み。フォーク自体は `MountOptions.Options` (任意の `-o` 文字列) を追加するが、libfuse 3 では `-o use_ino` を `fuse_new` 経由で渡しても unknown と判定されるため (libfuse 3 では `fuse_config.use_ino` を init callback で設定する形式)、当方では [vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs) の `Init(IntPtr conn, IntPtr cfg)` callback で `*(int*)(cfg + 64) = 1` を書く downstream パッチを当てている (offset 64 は libfuse 3.x の `fuse_config.use_ino` の位置 / libfuse 3.0 以降安定)。

これで以下が一気に解消:

| 課題 | 解消方法 |
|---|---|
| `-o allow_other` で root mount + 非 root アクセス | フォークの `MountOptions.Options` 経由で libfuse に伝播 |
| `attr_timeout=0` を渡して kernel attr キャッシュ無効化 | 同上 (Mount.Program で default `attr_timeout=0` を付与) |
| `use_ino` でハードリンク間 `st_ino` 一致 | downstream パッチで `fuse_config.use_ino=1` を init で設定 |

### submodule のセットアップ / 追従

```bash
# 新規 clone 時 (downstream パッチが自動で来る)
git clone --recurse-submodules https://github.com/ebe-rest/pgfs.git
# 既存 clone を初期化
git submodule update --init --recursive
```

`ebe-rest/Tmds.Fuse` を upstream (`securefolderfs-community/Tmds.Fuse`) に追従させたいとき:

```bash
cd vendor/Tmds.Fuse
# 初回のみ upstream remote を追加 (clone 直後は origin = ebe-rest のみ)
git remote add upstream https://github.com/securefolderfs-community/Tmds.Fuse.git
# upstream の最新を取得し、downstream パッチを上に rebase
git fetch upstream
git rebase upstream/master
# fork に反映
git push origin master --force-with-lease
# 親リポでは新しい submodule SHA を記録
cd ../..
git add vendor/Tmds.Fuse
git commit -m "Update Tmds.Fuse fork from upstream"
```

rebase 中に `FuseMount.cs` の `Init` 周辺で衝突したら、`fuse_config.use_ino=1` の書き込みを残す方向で解決する。Linux e2e が通るか確認。

upstream に PR が取り込まれれば downstream パッチは外せる。

---

## 既知の制約

### 起動時マウント (`/etc/fstab` 行 + 再起動) は未検証

`sudo mount -t pgfs ...` (手動マウント) は通っているが、システム起動時の `mount -a` で fstab 行が処理される経路はまだ手で踏んでいない。`_netdev` 付きで PG リモートを待たせる挙動の確認も含めて、ユーザー側で実機検証する形にしている。

---

## 関連項目

- 既存の Mount 仕様 → [Mount.md](Mount.md)
- 設定モデル全般 → [src/lib/src/Config/](../src/lib/src/Config/) と [architecture.md](architecture.md)
- ConfigLoader / Schema の現状動作 → [src/lib/src/Config/ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs) / [Schema.cs](../src/lib/src/Config/Schema.cs)
