# fstab 対応 (Linux)

> **道順**: [docs/README.md](../README.md) › [../Mount.md](../Mount.md) › **本書**
>
> **この doc が正である範囲**: `/etc/fstab` と `mount(8)` 経由で `mount.pgfs` を起動するための仕様の正。
> helper 呼び出し規約 (位置引数・helper context 判定)、`-o` の受け口、子プロセス分離 (`MOUNTED`
> シグナル方式) と daemon 化、`/sbin/mount.pgfs` の install、起動時マウントの実機検証はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [../Mount.md](../Mount.md) | **現行の CLI 契約と `-o` オプションの分類・既定値**。個々のオプションの意味はそちらが正 |
> | [fuse-binding.md](fuse-binding.md) | libfuse バインディング本体の設計 (dlopen / `fuse_config.use_ino` / op 配線) |
> | [settings-matrix.md](settings-matrix.md) | 設定項目の網羅 (本書の抜粋表の出どころ) と既定値 |
> | [../architecture.md](../architecture.md) | Config モデル全体の構成 (§Config) とビルド・配置 |
>
> 設定モデルの実装の所在: [src/core/src/Config/](../../src/core/src/Config/) — 本書が参照する
> `ConfigLoader.cs` の各メソッドは本文中でリンクし、項目定義は [Schema.cs](../../src/core/src/Config/Schema.cs) にある。

`mount.pgfs` を `/etc/fstab` から呼べるようにするための仕様メモ。バイナリ名 `mount.pgfs` は既に Linux の `mount(8)` が探す `/sbin/mount.<type>` 規約と一致しているため、対応すべきは **呼び出し規約の受け口** と **デーモン化** の 2 点に絞られる。

ステータス: **完了 (2026-06-02 起動時マウント実機検証済み)** — 位置引数 / `-o` パーサ / 子プロセス分離 (`MOUNTED` シグナル方式) / `/sbin/mount.pgfs` への install / `mount -t pgfs` / `mount -t fuse.pgfs` 経由のマウント・**非 root ユーザーからのアクセス** (`-o allow_other`)・umount まで確認済み。**起動時マウントも確認済み**: `linux_client` を再起動するだけで fstab 行から `/mnt/pgfs` が自動マウントされた (`findmnt` に `/dev/fuse … default_permissions,allow_other` で出現、一般ユーザーから `ls -al /mnt/pgfs` でエントリ読取可)。libfuse バインディングは `Pgfs.Fuse` ([src/fuse/](../../src/fuse/)) に**内製化**済み (旧 Tmds.Fuse fork 由来・[NOTICES](../../src/fuse/NOTICES.md))。`MountOptions.Options` 経由で libfuse に任意 `-o` を渡せ、`use_ino` は init callback で `fuse_config.use_ino=1` を設定する。

---

> **2026-09-19 静的照合**: 本書は当初設計と過去の実機記録を含む。現在は daemon 分離を実装済み。現行オプション処理は [Mount.md](../Mount.md) が正。**`-o max_write` の転送残存と安全・同期系オプションの無視は、どちらも対応済み** (後者は警告で明示する形)。**起動時マウントと OS フラグ適用は未検証のまま**である。

## 動機（設計着手時点）

現状 `mount.pgfs` は `dotnet publish` で出る single-file self-contained バイナリだが、起動方法は手動の `mount.pgfs -c <conn> -m <mountpoint>` のみ。これを `/etc/fstab` に書いて起動時マウント / `mount /mnt/pgfs` の短いコマンドで上げられるようにしたい。

副次効果として `systemd` の `mountpoint.mount` ユニットや autofs との連携も自然に可能になる (どちらも内部で `/sbin/mount.<type>` を呼ぶため)。

---

## `mount(8)` の helper 呼び出し規約

`mount -t pgfs <source> <target> -o <opts>` または fstab 行の処理時、`mount(8)` は以下のように helper を呼び出す:

```text
/sbin/mount.pgfs <source> <target> [-i] [-f] [-n] [-s] [-v] [-N <ns>] [-t <type>] [-o <opts>]
```

詳しい各フラグの意味と mount.pgfs での扱いは [mount(8) と mount.pgfs の引数対応表](#mount8-と-mountpgfs-の引数対応表) を参照。

helper は **マウント完了後に exit する**ことが期待される (mtab 更新のため)。フォアグラウンドで居座ると `mount` コマンドが返らない。

---

## `mount(8)` 経由マウント時の引数の流れ

fstab を書いておいて `sudo mount /mnt/pgfs` のように **マウント先だけ** で叩けるのは、`mount(8)` が間で展開してくれているから。`mount.pgfs` 自身は fstab を読まない (= 標準的な FUSE helper の責務分担)。

### fstab エントリ例

```text
postgresql://pgfs@pgsql_server/pgfs   /mnt/pgfs   pgfs   _netdev,allow_other,default_permissions,cache-max-entries=4096   0 0
```

| 列 | 内容 | 値 |
|---|---|---|
| 1 | source (fs spec) | `postgresql://pgfs@pgsql_server/pgfs` |
| 2 | target (マウントポイント) | `/mnt/pgfs` |
| 3 | type | `pgfs` |
| 4 | options | `_netdev,allow_other,default_permissions,cache-max-entries=4096` |
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
   /sbin/mount.pgfs postgresql://pgfs@pgsql_server/pgfs /mnt/pgfs -o _netdev,allow_other,default_permissions,cache-max-entries=4096
   ```
   (mount(8) 側の `-n` `-v` `-f` 等が指定されていれば併せて付与される)

`mount.pgfs` 内では positional[0] / positional[1] / `-o` パーサが粛々と値を流す。`-o` の中身は `ParseDashOOptions` で展開される (`_netdev` は無視、`allow_other` は `FuseFlags` 経由で libfuse に転送、`cache-max-entries=4096` は `mount.cache_max_entries` を上書き)。

このとき [`IsMountHelperContext`](../../src/core/src/Config/ConfigLoader.cs) は親 comm = `mount` と positional 存在を確認して **true** を返し、`MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` が有効化される (= helper context 専用の silent 飲み込み)。

### `mount(8)` を経由せず `mount.pgfs` を直接叩く場合

`sudo mount.pgfs /mnt/pgfs` のように打つと **動かない**。`mount.pgfs` 単独では fstab を読まないため:

- positional[0] = `/mnt/pgfs` → 先頭が `postgresql:` でないので **設定ファイルパスとして解釈** ([§位置引数](#1-位置引数-source--target-の受け入れ))
- `LoadFromFile` が `/mnt/pgfs` を TOML として開こうとする → ディレクトリで失敗、または不存在で silent 無視
- `database.connection` が未設定のまま PG 接続を試みる → デフォルト (localhost:5432) に当たって失敗

マウント先だけで動かしたい場合は **必ず `mount(8)` を経由する** (`sudo mount /mnt/pgfs` または `sudo mount -a` で fstab 一括) こと。

---

## `mount(8)` と `mount.pgfs` の引数対応表

`mount.pgfs` は (a) `mount(8)` helper として呼ばれた場合 と (b) ユーザーが直接実行した場合 の両方を受ける。同じ短縮形が異なる意味を持つことがあるため、起動時に [`IsMountHelperContext`](../../src/core/src/Config/ConfigLoader.cs) で context を判定し、helper context のときだけ mount(8) 内部用フラグを silent に読み飛ばす。

### Helper context の判定 (実装)

以下 **両方** が true なら helper context:

1. `args` のどれかが `-` で始まらない (= positional 引数が存在する)
2. 親プロセスの comm (Linux: `/proc/<ppid>/comm`) が `"mount"`

どちらかが false なら直接実行扱いで `MountHelperFlagsNoValue` / `MountHelperFlagsWithValue` を適用しない (= `-f` / `-s` を本来の短縮形として効かせる)。Linux 以外では親プロセス取得が空振りするため常に直接実行扱い (= helper context 機構を持たない `assign.pgfs` (Windows) でも同じコードが安全に動く)。

### `mount(8)` が helper に渡す引数の処理

| `mount(8)` 引数 | `mount(8)` の意味 | helper context | 直接実行 | 備考 |
|---|---|---|---|---|
| **positional[0]** (= fstab 1 列目 `<source>`) | FS の「データ源」 | `postgresql:` で始まる → `database.connection`、それ以外 → `setting.file` (TOML パス) | 同左 | 両義サポート ([§位置引数](#1-位置引数-source--target-の受け入れ)) |
| **positional[1]** (= fstab 2 列目 `<target>`) | マウントポイント | `mount.mount_point` に流す (未設定時のみ) | 同左 | |
| `-o <opts>` (= fstab 4 列目) | カンマ区切りオプション | [`ParseDashOOptions`](../../src/core/src/Config/ConfigLoader.cs) でカンマ split → `Field.CliOptions` / `Field.EffectiveDashOName` と照合 / `allow_other` 等は `MountConfig.FuseFlags` へ / `_netdev` / `noauto` / `noatime` 等は無視 | 同左 | 詳細 [§-o パーサ](#2--o-keyvalflag-パーサ) |
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

`mount.foreground` も以前は `-f` を持っていたが、setting.file との重複 (どちらが優先されるかは Field 宣言順依存) のため  CliOptions から外した (`--foreground` のみ受ける)。`-f` は **setting.file 専用** の短縮形と整理。

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

## 必要な実装変更

### 1. 位置引数 (source / target) の受け入れ

[`ConfigLoader.ParseCli`](../../src/core/src/Config/ConfigLoader.cs) は位置引数を 2 つまで拾う:

| 位置 | 役割 | マップ先 |
|---|---|---|
| 1 つ目 | source | `postgresql:` で始まる → `Database.Connection.Value`、それ以外 → `Setting.File.Value` (= 設定ファイルパス)。どちらも未読込時のみ反映。明示 `-c` / `-f` / `-o connection=` が優先 |
| 2 つ目 | target (マウントポイント) | `Mount.MountPoint.Value` (空の場合のみ。明示 `-m` や `-o mount_point=` が優先) |

source の判別ヒューリスティック (追加):
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

「Mount オプション `-o`」の残作業と統合する。実装方針:

```csharp
// args の中の "-o" の次トークンを取り出して
// カンマで split → 各要素を "=" で split → key/value
// key を s.Options に一致する Setting にマップして value を流し込む

// 例: -o noatime,_netdev,allow_other,connection=postgres://...,cache-max-entries=4096
//
//   noatime              → カーネル側フラグ。FUSE に渡さず無視 (もしくは mount syscall flags に渡す)
//   _netdev              → fstab 用のヒント (起動時にネットワークを待つ)。無視で良い
//   allow_other          → FUSE フラグ。Pgfs.Fuse の MountOptions に転送
//   connection=...       → Database.Connection.Value に書き戻す
//   cache-max-entries=4096 → Mount.CacheMaxEntries.Value に書き戻す
```

マッピング表 (初期):

| `-o` キー | マップ先 / 扱い |
|---|---|
| `connection=...` | `Database.Connection.Value` |
| `super_connection=...` | `Database.SuperConnection.Value` (mount で使うかは要検討。基本は mkfs 専用) |
| `mount_point=...` | `Mount.MountPoint.Value` |
| `cache-max-entries=...` | `Mount.CacheMaxEntries.Value` |
| `log-level=...` | `Logging.Level.Value` |
| `setting-file=...` | `Setting.File.Value` |
| `allow_other` | FUSE `MountOptions` (Pgfs.Fuse 側) |
| `default_permissions` | FUSE `MountOptions` |
| `ro` / `rw` | FUSE `MountOptions` |
| `nosuid` / `nodev` / `noexec` | カーネル側フラグ (パーサーは無視する。実マウントへの適用は未検証であり、問題なしとは保証しない) |
| `_netdev` / `noauto` / `user` / `users` | fstab 専用フラグ、helper では無視 |
| `noatime` / `relatime` / `atime` | atime ポリシー (将来 inode の `atime` 列を入れたら実装) |

未知の `-o` キーは `ConfigLoader.ParseDashOOptions` が `Warnings` に積み、`Console.Error` 出力相当に流す (silent drop しない)。

### 3. デーモン化

`mount(8)` は helper の exit を待つ。設計着手時の [`Program.RunFuseMountAsync`](../../src/mount/src/Program.cs) は `await fuseMount.WaitForUnmountAsync()` でブロックし続けるので、fstab 経由だと `mount` コマンドが永遠に返らない。

#### 選択肢

**A. libfuse3 の自動 daemonize を使う**
- libfuse3 はオプション `-f` (foreground) を渡さない限り `fork()` してデーモン化する
- Tmds.Fuse 0.1.0-190711 がこのフラグをどう露出しているかは未調査。`MountOptions` に該当プロパティがあれば最小工数で済む
- 要調査: Tmds.Fuse のソース / `Pgfs.Fuse.Fuse.Mount` の挙動

**B. 子プロセスを起動して親が exit (sshfs 方式)**
- `Process.Start(自分自身, 元の args + "--foreground-internal")` で子を起動
- 親は子の stdout/stderr を少しだけ読んで「マウント成功」のシグナルを待ったら exit
- 子は通常通り `WaitForUnmountAsync` で永続
- .NET だけで完結。`fork()` が無い Windows 互換性も保てる (Windows では `Assign` を使うので関係ないが)
- **推奨**

**C. systemd ユニット生成に倒す**
- `mount.pgfs` は `/run/systemd/generator/mnt-pgfs.mount` のような unit を吐いて exit
- 実体は `pgfs-mountd.service` のような常駐サービス
- 設計が大袈裟。fstab だけで完結しないので却下

採用は **方式 B**。

#### 子プロセス起動の擬似コード

```csharp
// 親 (mount(8) から呼ばれた側)
if (!args.Contains("--foreground-internal")) {
    var childArgs = args.Concat(new[] { "--foreground-internal" }).ToArray();
    var childProc = new Process {
        StartInfo = new ProcessStartInfo {
            FileName = Environment.ProcessPath!,  // 自分自身
            Arguments = string.Join(" ", childArgs.Select(QuoteArg)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
    };
    childProc.Start();

    // 子からの "MOUNTED" シグナルを待つ (タイムアウト 30 秒)
    var line = await childProc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (line == "MOUNTED") {
        return 0;  // 親 exit、mount(8) に成功を返す
    } else {
        // 子の stderr を吸い出してから失敗 exit
        var err = await childProc.StandardError.ReadToEndAsync();
        Console.Error.Write(err);
        return childProc.ExitCode != 0 ? childProc.ExitCode : 1;
    }
}

// 子 (--foreground-internal 付き)
// ... 通常のマウント処理 ...
using var fuseMount = Pgfs.Fuse.Fuse.Mount(mountPoint, fileSystem, mountOptions);
Console.WriteLine("MOUNTED");  // 親に成功通知
Console.Out.Flush();
// stdout/stderr を /dev/null か syslog に切り替えて永続
await fuseMount.WaitForUnmountAsync();
```

### 4. アンマウント

fstab 経由でマウントしたものは `umount /mnt/pgfs` で外す。これは `mount(8)` の対称コマンドで、`/sbin/umount.<type>` が無ければ FUSE 経由 (`fusermount3 -u`) でアンマウントされる。

現状 `umount.pgfs` を別途実装する必要は無い。`fusermount3 -u` がカーネル側のマウントを切ると、子プロセスの `WaitForUnmountAsync` が戻り、子も自然に exit する。

---

## インストール

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs

# fuse.pgfs 名でも引きたい場合 (mount -t fuse.pgfs ... や fstab で fuse.pgfs と書く場合)
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs
```

インストーラ (`install.sh` か Makefile / RPM / DEB) は将来。当面は手作業で良い。

`/etc/fuse.conf` に `user_allow_other` を入れておくと、一般ユーザーが `allow_other` 付きでマウントできるようになる:

```text
# /etc/fuse.conf
user_allow_other
```

---

## fstab エントリ例

### システム mount (root が起動時にマウント)

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  pgfs  noatime,_netdev,allow_other,default_permissions,cache-max-entries=8192  0 0
```

- `_netdev` を入れておくとネットワーク起動後にマウントが走る (PG リモートの場合に必須)
- `0 0` は dump 不要 / fsck 不要

### ユーザー mount

```fstab
postgresql://pgfs@localhost/myfs  /home/me/pgfs  pgfs  user,noauto,allow_other,default_permissions  0 0
```

- `noauto` で起動時はマウントしない
- `user` で一般ユーザー (fstab に書いた本人) が `mount /home/me/pgfs` できる
- `allow_other` を使うなら `/etc/fuse.conf` の `user_allow_other` が必要。**`allow_other` には必ず `default_permissions` を添える** — pgfs はアクセス可否を自分では判定しない (カーネルに委ねる) ので、無いと mode が強制されず、全ローカルユーザーが全ファイルを読み書きできる (付け忘れは起動時に警告する)

### fuse.pgfs 形式

`/sbin/mount.fuse.pgfs` シンボリックリンクを置いた場合:

```fstab
postgresql://pgfs@localhost/myfs  /mnt/pgfs  fuse.pgfs  noatime,_netdev  0 0
```

`mount(8)` は `fuse.*` 型を見ると `mount.fuse.<name>` を探すフォールバックを持っているので、これでも動く。

---

## テスト手順 (実装後)

1. `dotnet publish -c Release` でバイナリを作る
2. `sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs`
3. `sudo mkdir -p /mnt/pgfs`
4. 手動マウント確認:
   ```bash
   sudo mount -t pgfs "postgresql://pgfs@localhost/myfs" /mnt/pgfs \
       -o allow_other,default_permissions,cache-max-entries=4096
   ```
5. `mount | grep pgfs` でエントリ確認
6. `ls /mnt/pgfs` で読めることを確認
7. `sudo umount /mnt/pgfs` でアンマウント確認
8. `/etc/fstab` に行を追加して `sudo mount -a` で起動時相当の挙動を確認
9. 再起動して起動時マウントが効くか確認 — **実施済み (下記参照)**

### 起動時マウントの実機検証 (2026-06-02 実施)

`linux_client` に fstab 行を入れて **再起動**したところ、追加コマンドなしで `/mnt/pgfs` が自動マウントされた:

```text
$ findmnt | grep pgfs
└─/mnt/pgfs   /dev/fuse   fuse   rw,nosuid,nodev,relatime,user_id=0,group_id=0,default_permissions,allow_other

$ ls -al /mnt/pgfs/
合計 4
drwxr-xr-x 1 root     root        0  5月 28 18:22 .
drwxr-xr-x 5 root     root     4096  6月  2 20:23 ..
drwxrwxr-x 1 root     root        0  6月  3  2026 aaaa
drwxrwxr-x 1 user     user        0  6月  3  2026 bbbb
```

- `default_permissions,allow_other` が `findmnt` に出ている = fstab 4 列目の `-o` オプションが起動時にも正しく展開された
- 一般ユーザー (`user`) から `ls -al` でエントリが読める = `allow_other` + `/etc/fuse.conf` の `user_allow_other` が効いている
- 子プロセス分離 (方式 B) が systemd 起動シーケンス上でも `mount(8)` に成功を返している

### 直接呼び出しでのスモークテスト (実施)

`/sbin/mount.pgfs` の install を前にバイナリを直接叩いてフロー検証済み:

```bash
# 位置引数 + -o, no -f → デフォルトの子プロセス分離モード
bin/Publish/mount.pgfs \
    "Host=pgsql_server;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs;SslMode=Prefer" \
    /mnt/pgfs \
    -o cache-max-entries=2048,allow_other,_netdev,noatime \
    --log-level info
# → 親プロセスは 1.4 秒で exit 0 (子からの MOUNTED シグナル受信後)
mountpoint -q /mnt/pgfs   # → 0 (マウント済み)
ps -ef | grep "Publish/mount.pgfs"     # → 子が --foreground-internal 付きで生存
ls /mnt/pgfs              # → ディレクトリ内容が見える
fusermount3 -u /mnt/pgfs  # → アンマウント (子も自然終了)
```

`-o allow_other` / `-o attr_timeout=N` 等は `Pgfs.Fuse` の `MountOptions.Options` 経由で libfuse に渡る。
`_netdev` / `noatime` 等の fstab/カーネル側ヒントは silent に無視。
未知の `-o` キーは stderr に警告。

### `mount(8)` 経由 (sudo) でのスモークテスト (実施)

```bash
sudo install -m 755 bin/Publish/mount.pgfs /sbin/mount.pgfs
sudo ln -sf /sbin/mount.pgfs /sbin/mount.fuse.pgfs

# fstab 形式 (どちらでも可)
sudo mount -t pgfs       -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs
sudo mount -t fuse.pgfs  -o cache-max-entries=2048,allow_other,_netdev,noatime postgresql://pgfs:pgfs@pgsql_server:5432/pgfs /mnt/pgfs

mountpoint -q /mnt/pgfs   # → 0 (マウント済み)
ls -al /mnt/pgfs           # → 一般ユーザーでも読める (`-o allow_other`)
# ※ 上は当時の実機記録のまま。**例として使うなら `default_permissions` も付けること** (無いと mode が強制されない)
# ※ 当時のコードも URL 形式を Npgsql にそのまま渡しており、**URL 形式は通らなかったはず**である (v0.2.0 公開前の調査で判明し、URL 形式を解釈するよう修正した。記録と実際の実行が食い違った原因は未確認)
sudo umount /mnt/pgfs      # → アンマウント
```

注: 当初は `mount -t pgfs` が `FUSE 依存が見つかりません` で落ちていた。原因は `mount(8)` が helper を呼ぶときの env が PATH を含めて剥がされていること (実測値で LANG, LOGNAME, PWD, SHLVL, SUDO_*, TERM, USER, _ の 11 個のみで PATH 無し)。`Pgfs.Fuse` の `HasFusermount` は `$PATH` から `fusermount3` を探すので false を返し、`CheckDependencies` が落ちていた。`Mount/Program.cs` の `Main` 冒頭で PATH が空なら `/usr/local/sbin:...:/bin` を補うようにして解消。

## libfuse バインディング (内製・旧 Tmds.Fuse フォーク由来)

v0.2.0 で libfuse の P/Invoke バインディングを **`Pgfs.Fuse` ([src/fuse/](../../src/fuse/)) に内製化**した。以前は本家 `tmds/Tmds.Fuse 0.1.0-190711-50` (2019 年で更新停止・`MountOptions` が `SingleThread` のみ) ではなく活発フォーク [`securefolderfs-community/Tmds.Fuse`](https://github.com/securefolderfs-community/Tmds.Fuse) をさらに `ebe-rest/Tmds.Fuse` にフォークして `vendor/Tmds.Fuse` submodule で参照していたが、**submodule は廃止**し、バインディング一式 (`LibFuse.cs` / `FuseMount.cs` / `IFuseFileSystem.cs` 等) を `src/fuse/src/` に挙動保存で移植した。クレジットは [src/fuse/NOTICES.md](../../src/fuse/NOTICES.md)、op/シンボルの全体像と既知ハックは [fuse-binding.md](fuse-binding.md) を正とする。

これで以下が一気に解消:

| 課題 | 解消方法 |
|---|---|
| `-o allow_other` で root mount + 非 root アクセス | フォークの `MountOptions.Options` 経由で libfuse に伝播 |
| `attr_timeout=0` を渡して kernel attr キャッシュ無効化 | 同上 (Mount.Program で default `attr_timeout=0` を付与) |
| `use_ino` でハードリンク間 `st_ino` 一致 | downstream パッチで `fuse_config.use_ino=1` を init で設定 |

実機確認: `sudo mount -t pgfs ...` 後、一般ユーザーから `ls -la /mnt/pgfs` が exit 0、`stat -c '%i' file3` が `9223372036854775813` (= `0x8000_0000_0000_0005`, data_id 5 + 高ビット) を返す。

### バインディングの保守

`src/fuse/src/` のバインディングは通常の C# ソースとして編集・ビルドする (submodule ではない)。libfuse のレイアウト変更時は `FuseMount.cs` の `Init` (`fuse_config.use_ino`) と `FuseOperations` の op テーブルを見直す。詳細は [fuse-binding.md](fuse-binding.md)。

> **submodule は不要** (v0.2.0〜): `git submodule` 操作も `--recurse-submodules` clone も要らない。libfuse バインディングはリポ内 `src/fuse/` にある。`libfuse3.so.3` 本体は同梱せず、実行時に `dlopen` する (利用者が `fuse3` 等で用意)。

## 既知の制約

### 起動時マウント (`/etc/fstab` 行 + 再起動) — 検証済み (2026-06-02)

`linux_client` の再起動だけで fstab 行から `/mnt/pgfs` が自動マウントされることを実機確認済み ([§起動時マウントの実機検証](#起動時マウントの実機検証-2026-06-02-実施))。これで fstab 対応のコア + 検証はすべてクローズ。
