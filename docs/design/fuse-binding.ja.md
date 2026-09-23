# v0.2.0 (B): FUSE 内製バインディング設計

> **道順**: [docs/README.ja.md](../README.ja.md) › [v0.2.0-plan.ja.md](v0.2.0-plan.ja.md) › **本書**
>
> **この doc が正である範囲**: libfuse3 の**内製バインディング** (`Pgfs.Fuse`) の設計と as-built の正。
> `dlopen` + `dlvsym` によるシンボル解決 (symbol versioning)、構造体レイアウト (`fuse_config` /
> `fuse_file_info` 等)、op テーブルの配線範囲、設計判断 §4 と計画との差分はここに書く。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [v0.2.0-plan.ja.md](v0.2.0-plan.ja.md) | **親計画** (決定1 内製化 / 決定2 Lib 分割 / A の移動先確定) |
> | [../architecture.ja.md](../architecture.ja.md) | Core/Fuse/Dokan のプロジェクト構成・依存・ビルド手順 |
> | [fstab-support.ja.md](fstab-support.ja.md) | `mount(8)` / fstab 経由の起動と、そこからの `-o` 伝播 |
> | [../Mount.ja.md](../Mount.ja.md) | 利用者向けの CLI 契約とマウントオプションの分類 |
> | [windows-parity.ja.md](windows-parity.ja.md) | 同じ機能を Dokan 側へ展開するときの設計 |
> | [../tests.ja.md](../tests.ja.md) | e2e の一覧と実行方法 (本書の検証方針が依拠する実体) |
>
> 由来と実装の所在 (doc ではないので表に入れない): 原典 fork = [ebe-rest/Tmds.Fuse](https://github.com/ebe-rest/Tmds.Fuse)
> (クリーン tip `d454274`)。`LibFuse.cs` / `LibFuse.structs.cs` / `FuseMount.cs` / `IFuseFileSystem.cs` を
> [src/fuse/src/](../../src/fuse/src/) へ挙動保存で移植 (クレジットは [NOTICES.md](../../src/fuse/NOTICES.md))。
> pgfs の FS 実装は [src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs) (25 op・path ベース)。

> **実装済 + 両 OS e2e 緑**。本書は [v0.2.0-plan.ja.md](v0.2.0-plan.ja.md) の決定1 (FUSE 内製化) の詳細設計 (B)。実装状況は直下 §実装ステータス。
> A (Lib 分割) は設計クローズ済。本書は **libfuse3 公開 API 全体 × pgfs 使用箇所** の対象表を作り、
> その上に「自前の最小 libfuse P/Invoke バインディング」(= `vendor/Tmds.Fuse` fork の置換) を設計する。
> 動詞 (関数) ベースで列挙し、構造体は §2 に参照形式で分離する。**設計判断 §4 ①〜⑥ は 確定。次は実装。**

## 実装ステータス (実装済 + 両 OS e2e 緑)

Lib を `Pgfs.Core` / `Pgfs.Fuse` / `Pgfs.Dokan` に分割し、libfuse バインディングを旧 `vendor/Tmds.Fuse` fork から **挙動保存で内製移植** (namespace `Tmds.Fuse`→`Pgfs.Fuse`)、submodule は廃止 (クレジット = [../src/fuse/NOTICES.md](../../src/fuse/NOTICES.md))。

- **Windows フルソリューション ビルド緑** (Core/Fuse/Dokan + 薄い exe 3 本)。
- **Linux full e2e 36/36 PASS** (docker 単一 PG: mkfs→mount→tests。hardlink=use_ino / fallback=fuse_get_context / xattr-ACL 全緑 = binding 等価を実証)。
- **Windows Dokan e2e 27/27 PASS** (assign.pgfs を pgsql_server にマウント。Get/SetFileSecurity 投影・df 含む)。

**post-green cleanup (適用済・両 OS 緑で再検証)**:
- ✅ **③ typed `fuse_config`**: offset-64 の magic poke を廃し `((fuse_config*)cfg)->use_ino = 1` に。struct は [LibFuse.structs.cs] に追加 (libfuse 3.x レイアウト・use_ino@64)。**Linux hardlink e2e (36/36) で検証**。
- ✅ **`FuseFileInfo` KEEPCACHE bit**: `3`→`4` (`1<<2`) に修正 (writepage=1/direct_io=2/keep_cache=4)。pgfs は当該 bit を読まないため latent 修正。
- ✅ **`PosixAcl` を Core→Fuse へ移動**: 正準 (PgfsAcl=Core) / 投影 (PosixAcl=Fuse) の分離を完成 (Core は PosixAcl を参照していなかった)。
- ⏸ **symlink 引数順は変更せず**: binding の swap は libfuse の `(content, linkpath)` を直感的な managed `SymLink(path=リンク位置, target=中身)` に整える**意図的な適応**でありバグではない ([FileSystem.cs] にコメント)。よって修正不要と判断。
- ⬜ **残: `ServiceResolver` の `#if WINDOWS` 分割** (getservbyname native = ws2_32/libc)。service 名→port の稀な fallback の純度改善なので、Core は OS 別コンパイルのまま据え置き (機能は完結)。

**as-built の設計差分 (計画 §3-3 / §3-8 / §4 との違い)**:
- **op 配線 = 30 op + `init` = 31** ([FuseMount.cs](../../src/fuse/src/FuseMount.cs) の `ops.*` 配線)。§3-3 / §4#5 は「25+init に簡素化 (`flush`/`fsync`/`fsyncdir`/`access`/`fallocate` を null)」を計画したが、**挙動保存を優先し fork と同じ 31 を配線** (当該 5 op は ENOSYS 返し)。§1-B / §2 / §3-3 / §4#5 の「25+init のみ」は計画値で、pgfs が意味を持って実装するのは 25 op (= §0 の ground truth と一致)。
- **ファイル構成 = 平置き 3 ファイル** `LibFuse.cs` (シンボル解決) / `LibFuse.structs.cs` (struct + op デリゲート型を集約) / `FuseMount.cs` (op 配線 + ライフサイクル)。§3-8 案の `Native/{LibFuse,FuseStructs,FuseOperations}` 3 分割ではなく、struct と op を `LibFuse.structs.cs` にまとめた (§3-8 が「`Native/` か平置きかは実装時裁量」とした範囲内)。

## 目的とスコープ

- **自前の最小 libfuse P/Invoke バインディング**を `Pgfs.Fuse` 内に持ち、`vendor/Tmds.Fuse` submodule + fork を廃止する ([v0.2.0-plan.ja.md 決定1](v0.2.0-plan.ja.md))。
- **高レベル API (`fuse.h`) のみ**。low-level / session API (`fuse_lowlevel.h`) は不使用 (現行 fork も同じ)。
- libfuse の **C ABI はインターフェース** (バインドするだけ・ソースは vendor しない)。`libfuse.so` は利用者が用意 (LGPL・`dlopen` 動的リンク)。

## 0. 現行 fork の実測サーフェス (置換対象の ground truth)

`vendor/Tmds.Fuse` を実測した結果 (置換で再現すべき最小集合):

- **シンボル解決**: `dlopen("libfuse3.so.3", RTLD_NOW)` → **バージョン付き `dlvsym`** (既定タグ `"FUSE_3.0"`、`fuse_new` のみ `"FUSE_3.1"`)。`[DllImport]` は不使用。libc プリミティブは `Tmds.LibC` 0.5.0 (`dlopen`/`dlvsym`/`stat`/`statvfs`/`timespec`/`mode_t`/errno) に依存。
- **使用 libfuse シンボル 10 本** (§1-A 表)。`fuse_main` / `fuse_opt_parse` / `fuse_parse_cmdline` / `fuse_session_*` は不使用。
- **`fuse_operations` 40 スロット中 31 配線** (30 op + `init`)、9 null。pgfs が意味を持って実装するのは **25 op** (残り `flush`/`fsync`/`fsyncdir`/`access`/`fallocate` は配線だけで ENOSYS)。
- **managed↔native**: 型付きデリゲート + `Marshal.GetFunctionPointerForDelegate`、`&ops` (stack-local `fuse_operations`) を `fuse_new` に `op_size = sizeof(...)` 付きで渡す。
- **errno**: 負 errno 直返し、各コールバックを `try { … } catch { return -EIO; }` で包む。`release` のみ void→0。
- **既知パッチ**: (a) `init` で `fuse_config.use_ino=1` を offset 64 に書く / (b) `fuse_get_context` (uid/gid/pid) / (c) `MountOptions.Options` の `-o` verbatim passthrough。

---

## 1. libfuse3 公開 API 全体 × pgfs 使用箇所 (動詞ベース)

凡例 — **pgfs**: ✅使用 / ➖配線のみ(中身 ENOSYS) / ✗不使用。**内製**: 持つ=○ / 持たない=✗ / 条件付き=△。

### 1-A. 高レベル ライフサイクル / セットアップ (`fuse.h`, `fuse_common.h`)

| 関数 | C シグネチャ (略) | pgfs | 内製 | 備考 |
|---|---|---|---|---|
| `fuse_new` | `fuse* (fuse_args*, const fuse_operations*, size_t, void*)` | ✅ | ○ | **`FUSE_3.1`** 版。op テーブル登録の本体 |
| `fuse_mount` | `int (fuse*, const char*)` | ✅ | ○ | マウントポイントへ接続 |
| `fuse_loop` | `int (fuse*)` | ✅ | ○ | single-thread イベントループ |
| `fuse_loop_mt` | `int (fuse*, int clone_fd)` | ✅ | ○ | multi-thread (pgfs FileSystem は `SupportsMultiThreading=true`) |
| `fuse_unmount` | `void (fuse*)` | ✅ | ○ | teardown |
| `fuse_destroy` | `void (fuse*)` | ✅ | ○ | teardown |
| `fuse_exit` | `void (fuse*)` | ✅ | ○ | 強制アンマウント時にループ脱出 |
| `fuse_get_context` | `fuse_context* (void)` | ✅ | ○ | uid/gid (creator-owner + audit)。**optional 解決** (無くてもマウント可) |
| `fuse_main` (`fuse_main_real`) | マクロ → `int (argc, argv, ops, size, priv)` | ✗ | ✗ | pgfs は `fuse_new`+`fuse_mount`+`fuse_loop` を直に組むため不要 |
| `fuse_get_session` | `fuse_session* (fuse*)` | ✗ | ✗ | low-level セッション取得。不要 |
| `fuse_daemonize` | `int (int foreground)` | ✗ | ✗ | pgfs は自前の子プロセス分離 (mount exe 側) でデーモン化 |
| `fuse_set_signal_handlers` / `fuse_remove_signal_handlers` | `int/void (fuse_session*)` | ✗ | ✗ | low-level session 前提。pgfs は .NET 側で Ctrl+C 処理 |
| `fuse_lib_help` | `void (fuse_args*)` | ✗ | ✗ | help は pgfs 自前 (`HelpText`) |
| `fuse_getgroups` | `int (int size, gid_t list[])` | ✗ | ✗ | 補助グループ列挙。未使用 |
| `fuse_interrupted` | `int (void)` | ✗ | ✗ | 割り込み検出。未使用 |
| `fuse_invalidate_path` | `int (fuse*, const char*)` | ✗ | △ | kernel キャッシュ無効化。**将来 Notify 連携で検討余地** (現状は InodeCache invalidate のみ) |
| `fuse_clean_cache` / `fuse_start_cleanup_thread` / `fuse_stop_cleanup_thread` | — | ✗ | ✗ | remember キャッシュ管理。未使用 |
| `fuse_apply_conn_info_opts` / `fuse_parse_conn_info_opts` | — | ✗ | ✗ | conn_info の opt 適用。未使用 |
| `fuse_version` / `fuse_pkgversion` | `int/const char* (void)` | ✗ | △ | 診断用。**入れてもよい** (依存判定/ログに有用) |

### 1-B. 高レベル操作 — `struct fuse_operations` の全 op (libfuse 3.x、宣言順)

42 スロット (3.10+)。現 fork の struct は `fallocate` (#40) まで宣言し `op_size` で安全に切る。

| # | op | C シグネチャ (略) | fork 配線 | pgfs 実装 | 内製 | Api 呼出 / 備考 |
|---|---|---|---|---|---|---|
| 1 | `getattr` | `(path, stat*, ffi*)` | ✅ | ✅ | ○ | `Api.GetByPath`→`FillStat`。`st_ino` は DataId+符号 namespace (use_ino 依存) |
| 2 | `readlink` | `(path, buf, size)` | ✅ | ✅ | ○ | `inode.LinkTarget` |
| 3 | `mknod` | `(path, mode, dev)` | null | ✗ | ✗ | 通常ファイルは `create` 経由。FIFO/dev 不要 |
| 4 | `mkdir` | `(path, mode)` | ✅ | ✅ | ○ | `Api.CreateDirectory` |
| 5 | `unlink` | `(path)` | ✅ | ✅ | ○ | `Api.DeleteInode` |
| 6 | `rmdir` | `(path)` | ✅ | ✅ | ○ | `Api.IsDirectoryEmpty`+`DeleteInode` |
| 7 | `symlink` | `(target, path)` | ✅ | ✅ | ○ | `Api.CreateSymlink`。**fork は引数逆順だった→内製で C 順 (target,linkpath) に正す** |
| 8 | `rename` | `(path, newpath, flags)` | ✅ | ✅ | ○ | `RENAME_NOREPLACE`=1 を処理 |
| 9 | `link` | `(oldpath, newpath)` | ✅ | ✅ | ○ | `Api.CreateHardLink` |
| 10 | `chmod` | `(path, mode, ffi*)` | ✅ | ✅ | ○ | `Api.UpdateMode`、型ビット保持 |
| 11 | `chown` | `(path, uid, gid, ffi*)` | ✅ | ✅ | ○ | `-1`(uint.Max)=変更なし。`UserResolver`→`Api.UpdateOwner` |
| 12 | `truncate` | `(path, off, ffi*)` | ✅ | ✅ | ○ | `Api.TruncateData` |
| 13 | `open` | `(path, ffi*)` | ✅ | ✅ | ○ | `O_TRUNC`(0x200) 処理。`fi.fh` 未使用 |
| 14 | `read` | `(path, buf, size, off, ffi*)` | ✅ | ✅ | ○ | `Api.ReadData`。非負バイト数返し |
| 15 | `write` | `(path, buf, size, off, ffi*)` | ✅ | ✅ | ○ | `Api.WriteData` |
| 16 | `statfs` | `(path, statvfs*)` | ✅ | ✅ | ○ | `Api.GetStatFs` (df 対応) |
| 17 | `flush` | `(path, ffi*)` | ➖ | ✗ | △ | DB 同期書込なのでバッファ無し。**配線不要** (null で ENOSYS、害なし) |
| 18 | `release` | `(path, ffi*)` | ✅ | ✅(no-op) | ○ | void→0。`fi.fh` 未使用 |
| 19 | `fsync` | `(path, datasync, ffi*)` | ➖ | ✗ | △ | 同上。**配線不要** |
| 20 | `setxattr` | `(path, name, val, size, flags)` | ✅ | ✅ | ○ | `XATTR_CREATE/REPLACE`。`system.posix_acl_access` 特別扱い |
| 21 | `getxattr` | `(path, name, buf, size)` | ✅ | ✅ | ○ | size-probe protocol。ACL 合成 |
| 22 | `listxattr` | `(path, list, size)` | ✅ | ✅ | ○ | NUL 連結 + size-probe |
| 23 | `removexattr` | `(path, name)` | ✅ | ✅ | ○ | ACL clear 特別扱い |
| 24 | `opendir` | `(path, ffi*)` | ✅ | ✅ | ○ | 存在確認のみ。`fi.fh` 未使用 |
| 25 | `readdir` | `(path, buf, filler, off, ffi*, flags)` | ✅ | ✅ | ○ | `Api.ListChildren`。filler で `.`/`..`/children。offset 無視 |
| 26 | `releasedir` | `(path, ffi*)` | ✅ | ✅(no-op) | ○ | |
| 27 | `fsyncdir` | `(path, datasync, ffi*)` | ➖ | ✗ | △ | **配線不要** |
| 28 | `init` | `(conn_info*, config*)` | ✅ | ✗(fork内) | ○ | **use_ino patch のため必須**。pgfs FS は override せず fork 内で処理 |
| 29 | `destroy` | `(private_data)` | null | ✗ | ✗ | teardown は .NET 側。不要 |
| 30 | `access` | `(path, mask)` | ➖ | ✗ | ✗ | **`default_permissions` でカーネル委譲**。配線不要 |
| 31 | `create` | `(path, mode, ffi*)` | ✅ | ✅ | ○ | `Api.CreateFile`。`fi.fh` 未設定 |
| 32 | `lock` | `(path, ffi*, cmd, flock*)` | null | ✗ | ✗ | POSIX ロック。未使用 |
| 33 | `utimens` | `(path, timespec[2], ffi*)` | ✅ | ✅ | ○ | `UTIME_OMIT/NOW`。atime 無視→`Api.UpdateTimestamps` |
| 34 | `bmap` | `(path, blocksize, idx*)` | null | ✗ | ✗ | block デバイス用。不要 |
| 35 | `ioctl` | `(path, cmd, arg, ffi*, flags, data)` | null | ✗ | ✗ | 未使用 |
| 36 | `poll` | `(path, ffi*, pollhandle*, reventsp)` | null | ✗ | ✗ | 未使用 |
| 37 | `write_buf` | `(path, bufvec*, off, ffi*)` | null | ✗ | ✗ | zero-copy 書込。`write` で足りる |
| 38 | `read_buf` | `(path, bufvecp, size, off, ffi*)` | null | ✗ | ✗ | zero-copy 読込。`read` で足りる |
| 39 | `flock` | `(path, ffi*, op)` | null | ✗ | ✗ | BSD ロック。未使用 |
| 40 | `fallocate` | `(path, mode, off, len, ffi*)` | ➖ | ✗ | △ | **配線不要** (null で ENOSYS) |
| 41 | `copy_file_range` | `(in, fi_in, off_in, out, fi_out, off_out, len, flags)` | — | ✗ | ✗ | fork struct に無し。将来 op |
| 42 | `lseek` | `(path, off, whence, ffi*)` | — | ✗ | ✗ | fork struct に無し。SEEK_DATA/HOLE。将来 op |

**内製で配線する op = pgfs 実装 25 + `init` (use_ino)**:
`getattr, readlink, mkdir, unlink, rmdir, symlink, rename, link, chmod, chown, truncate, open, read, write, statfs, release, setxattr, getxattr, listxattr, removexattr, opendir, readdir, releasedir, create, utimens` + `init`。
**null のままにする (libfuse が自前で ENOSYS / カーネル処理)**: `mknod, flush, fsync, fsyncdir, destroy, access, lock, bmap, ioctl, poll, write_buf, read_buf, flock, fallocate, copy_file_range, lseek`。

### 1-C. オプション解析 (`fuse_opt.h`)

| 関数 | pgfs | 内製 | 備考 |
|---|---|---|---|
| `fuse_opt_add_arg` | ✅ | ○ | arg vector に `""` と `-o<opts>` を積む |
| `fuse_opt_free_args` | ✅ | ○ | teardown |
| `fuse_opt_parse` / `fuse_opt_add_opt` / `fuse_opt_insert_arg` / `fuse_opt_match` | ✗ | ✗ | pgfs は自前の `ConfigLoader`/`ParseDashOOptions` で解析済。libfuse には `-o` 文字列だけ渡す |
| `struct fuse_opt` | ✗ | ✗ | opt テンプレート。未使用 |
| `struct fuse_args` | ✅(間接) | ○ | §2 で型定義。`fuse_opt_add_arg`/`fuse_new` が参照 |

### 1-D. 低レベル / セッション API (`fuse_lowlevel.h`) — pgfs 全不使用

`struct fuse_lowlevel_ops` (lookup/forget/setattr/... ~40 op) / `fuse_session_new` / `fuse_session_mount` / `fuse_session_loop[_mt]` / `fuse_session_unmount` / `fuse_session_destroy` / `fuse_session_exit/reset/exited/fd` / `fuse_session_process_buf` / `fuse_reply_*` (err/entry/attr/buf/...) / `fuse_lowlevel_notify_*` (inval_inode/inval_entry/store/retrieve/poll) / `fuse_req_ctx/userdata/interrupted` — **すべて ✗**。高レベル API を使うため低レベル層には一切触れない。内製でも持たない。

### 1-E. バッファ / 通知 / ログ / シグナル (`fuse_common.h`, `fuse_log.h`)

| 関数 / 型 | pgfs | 内製 | 備考 |
|---|---|---|---|
| `fuse_buf` / `fuse_bufvec` / `fuse_buf_size` / `fuse_buf_copy` | ✗ | ✗ | `write_buf`/`read_buf` 不使用なので不要 |
| `fuse_notify_poll` / `fuse_pollhandle_destroy` | ✗ | ✗ | poll 不使用 |
| `fuse_log` / `fuse_set_log_func` / `fuse_log_enable_syslog` / enum `fuse_log_level` | ✗ | △ | libfuse 内部ログ。pgfs は自前 Logger。**取り込んで自前 sink に流す手はある** (低優先) |
| `fuse_set_signal_handlers` / `fuse_remove_signal_handlers` | ✗ | ✗ | session 前提。.NET 側で処理 |
| `fuse_loop_config` (`fuse_loop_cfg_*`) | ✗ | △ | `fuse_loop_mt` の設定。現状デフォルトで足りる |

### 1-F. コンテキスト

| 関数 / 型 | pgfs | 内製 | 備考 |
|---|---|---|---|
| `fuse_get_context` → `struct fuse_context` | ✅ | ○ | uid/gid を creator-owner と audit に使用。pid は受けるが未使用。**コールバック実行中のみ有効** |

---

## 2. 構造体サーフェス (使用する動詞が参照するものだけ)

凡例 — **型定義**: 内製で C# 型を定義する=○ / untyped (raw `IntPtr`+offset) =△ / libc 由来=libc。

| struct | 由来 | pgfs 触る | 型定義 | レイアウト注意 |
|---|---|---|---|---|
| `fuse_args` | fuse_opt.h | 間接 | ○ | `int argc; char** argv; int allocated;` |
| `fuse_operations` | fuse.h | — | ○ | **関数ポインタの並び順が ABI**。宣言順を厳守し `op_size=sizeof` を渡す。配線は §1-B の 25+init のみ、残スロットは `IntPtr.Zero` |
| `fuse_file_info` | fuse_common.h | ✅ | ○ | `flags` と bitfield ワード。**fork の `KEEPCACHE=3` は疑義 → 実 header (`fuse_common.h`) で bit 位置を再検証**。pgfs は `flags` (O_TRUNC) のみ読む・`fh` 不使用 |
| `fuse_context` | fuse.h | ✅ | ○ (partial) | `fuse* fuse; uid_t uid; gid_t gid; pid_t pid; void* private_data; mode_t umask;`。先頭 uid/gid/pid だけ読めればよい |
| `fuse_config` | fuse.h | ✅(1 箇所) | △ | **`use_ino` を offset 64 に `=1` で書くだけ**。型は起こさず raw poke。**offset 64 は libfuse 3.x で安定だが実 header で再確認**。代替: `fuse_config` を正しく型定義して `cfg->use_ino` で書く (脆い offset 依存を排除) — §3-6 で選ぶ |
| `fuse_conn_info` | fuse_common.h | ✗ | △ | `init(conn,cfg)` で受けるが pgfs は触らない。raw `IntPtr` で素通し |
| `stat` | sys/stat.h | ✅ | libc/○ | `getattr` で full 充填。`st_ino`/`st_mode`/`st_nlink`/`st_uid`/`st_gid`/`st_size`/`st_atim`/`st_mtim`/`st_ctim`/`st_blocks` 他 |
| `statvfs` | sys/statvfs.h | ✅ | libc/○ | `statfs` で `f_bsize`/`f_frsize`/`f_blocks`/`f_bfree`/`f_bavail`/`f_files`/`f_ffree`/`f_favail`/`f_namemax` |
| `timespec` | time.h | ✅ | libc/○ | `utimens` の atime/mtime。`UTIME_OMIT`/`UTIME_NOW` 判定 |
| `mode_t`/`uid_t`/`gid_t`/`pid_t`/`off_t`/`size_t` | sys/types.h | ✅ | libc/○ | スカラ。**fork の `mode_t→uint` 無限再帰バグ (Tmds.LibC 0.3.0) は自前定義なら回避** |

**`stat`/`statvfs`/`timespec` の出どころ** = §3-7 (Tmds.LibC を残すか自前 P/Invoke か) で決める。

---

## 3. 内製バインディング設計 (案 + trade-off)

### 3-1. シンボル解決方式

| 案 | 方法 | 長所 | 短所 |
|---|---|---|---|
| **(A) `dlopen`+`dlvsym` (現行踏襲)** ★推奨 | runtime に `libfuse3.so.3` を開きバージョン付き解決 | **symbol versioning に対応** (`fuse_new`=`FUSE_3.1`/他=`FUSE_3.0`)。これが**必須**: 素の `dlsym`/`DllImport` は default version を引き、`fuse_new` で誤バージョンを掴むと ABI 不整合 | 自前で関数ポインタ→デリゲート変換が要る |
| (B) `[DllImport]`/`[LibraryImport]("libfuse3.so.3")` | P/Invoke 直 | 記述が簡潔・marshalling 生成 | **`dlvsym` のバージョン指定ができない** → `fuse_new` の versioned symbol を正しく掴めない懸念。`.so.3` 固定名のロードも調整要 |
| (C) `NativeLibrary.Load` + `GetExport` | .NET API | DllImport より柔軟 | `GetExport` は **versioned symbol 非対応** ((B) と同じ穴) |

→ **推奨 (A)**: 現行が `dlvsym` を選んだ理由 (symbol versioning) は本質的。`libc` の `dlopen`/`dlvsym` 自体は §3-7 の libc 依存方針に従う。

### 3-2. managed↔native ブリッジ

| 案 | 方法 | 長所 | 短所 |
|---|---|---|---|
| **(A) デリゲート + `GetFunctionPointerForDelegate` (現行踏襲)** | 各 op をインスタンスメソッド→デリゲート→関数ポインタ | **インスタンス `FileSystem` のクロージャをそのまま掴める** (pgfs は path ベースで private_data 不使用)。実装が素直 | デリゲートを GC から守る保持が要る (フィールド保持で解決済) |
| (B) `[UnmanagedCallersOnly]` static + `delegate*` テーブル | .NET 5+ の静的関数ポインタ | thunk が軽い・AOT 親和 | **static なので instance を直接掴めない** → `fuse_new` の `private_data` に GCHandle を載せ、各コールバックで `fuse_get_context()->private_data` から復元する配線が必要。pgfs は単一マウント=単一 FS なので static フィールドでも可だが設計が増える |

→ **推奨 (A) 踏襲**。pgfs は1プロセス1マウント1 FS で、現行のデリゲート方式が最小コスト。AOT は元々 `PublishAot=false` 方針なので (B) の利点は薄い。

### 3-3. op テーブル配線範囲

- **配線する 26 = 実装 25 op + `init`** (§1-B 太字)。`init` は **use_ino のためだけ**に配線 (pgfs FS は op を override しない)。
- **null のまま 16** (§1-B)。libfuse が ENOSYS を返す/カーネルが処理する。`access` は `default_permissions` 委譲なので**敢えて配線しない**。
- 現 fork は `flush`/`fsync`/`fsyncdir`/`access`/`fallocate` も配線して ENOSYS を返していたが、**内製では null にして簡素化** (挙動同一・コード削減)。

### 3-4. errno / 例外規約

- **負 errno 直返し**を踏襲。各コールバックを `try { … } catch (Exception e) { Log(e); return -EIO; }` で包む。
- `release`/`releasedir` は FS 側 void → コールバック 0 返し。
- errno 定数は §3-7 の libc 方針に従う (`Tmds.LibC` の定数 or 自前 const)。

### 3-5. マウントオプション passthrough

- arg vector を `fuse_opt_add_arg` で構築: `[0]=""`、`[1]="-o" + 結合文字列`。
- 結合は **`attr_timeout=0` (既定) + `MountConfig.FuseFlags` (fstab/CLI 由来)**。現行どおり。
- **`use_ino` は `-o` で渡さない** (libfuse3 では default-on、渡すと `fuse_new` が unknown option で失敗) → `init` の `fuse_config.use_ino=1` で担保。

### 3-6. 持ち込む既知ハック / 修正 (内製での扱い)

| 項目 | 現 fork | 内製での方針 |
|---|---|---|
| `fuse_config.use_ino=1` | offset 64 に raw poke | **(B) 採用**: `fuse_config` を正しく型定義し `cfg->use_ino` で書く。脆い offset 依存を排除。要 `fuse_config` 全フィールド型起こし (実 `fuse.h` で検証) |
| `fuse_get_context` | optional 解決 + partial mirror | 踏襲。uid/gid/pid を読む `fuse_context` を型定義 |
| **`symlink` 引数順** | (linkname, target) **逆順だった** | **C 順 `(target, linkpath)` に正す** = fork のバグ修正 |
| **`FuseFileInfo` bitfield** | `KEEPCACHE=3` 疑い | **実 `fuse_common.h` で bit 位置を再確認**して正す。pgfs は `flags` のみ読むので実害は小だが正しく定義 |
| `mode_t→uint` | Tmds.LibC 0.3.0 の再帰バグ回避で `Unsafe.As` | **自前 `mode_t` 定義なら不要** (素の uint32) |

### 3-7. libc 依存 (`Tmds.LibC` を残すか)

依存している libc 機能 = `dlopen`/`dlvsym`、`stat`/`statvfs`/`timespec`/`mode_t` 等の型、errno 定数、`UTIME_NOW`/`UTIME_OMIT`。

| 案 | 内容 | 長所 | 短所 |
|---|---|---|---|
| **(A) `Tmds.LibC` (MIT) 継続** ★推奨(暫定) | 現行どおり NuGet 依存 | `stat`/`statvfs`/`timespec` の正確なレイアウトと errno を**実証済みで貰える**。内製化の主目的 (FUSE 結合の内製) は達成 | 「single-file・依存最小」の理念からは managed 依存が1つ残る (ただし MIT で公開上の問題なし) |
| (B) 自前 libc P/Invoke | `dlopen`/`dlvsym`/必要な型を自前定義 | 完全に依存ゼロ | `stat`/`statvfs` のアーキ別レイアウト (x86-64/ARM64/glibc/musl) を自前で正確に起こす手間とリスク大 |

→ **推奨 (A) 暫定継続**。FUSE 内製化の主眼は「libfuse バインディングの内製」であり、libc プリミティブは別問題。(B) は `stat` レイアウト移植が高リスクなので、内製 FUSE 層が安定してから別タスクで検討。

### 3-8. `Pgfs.Fuse` 内のファイル構成 (確定)

内製バインディングを次の単位に割る (現 fork の `LibFuse.cs`/`LibFuse.structs.cs`/`FuseMount.cs` を踏襲しつつ整理):

| ファイル | 責務 |
|---|---|
| `Native/LibFuse.cs` | `dlopen`/`dlvsym` シンボル解決 + 使用 10 本の関数ポインタ保持 (versioned: FUSE_3.0/3.1) |
| `Native/FuseStructs.cs` | `fuse_args` / `fuse_operations` / `fuse_file_info` / `fuse_context` / `fuse_config` の C# 型定義 |
| `Native/FuseOperations.cs` | op デリゲート型 (25+init) と `fuse_operations` テーブル構築 (`GetFunctionPointerForDelegate`) |
| `FuseMount.cs` | `fuse_new`→`fuse_mount`→`fuse_loop[_mt]`→teardown のライフサイクル + errno ラップ + use_ino (`init`)・get_context |
| `IFuseFileSystem.cs` / `FuseFileSystemBase.cs` | managed 側 FS インターフェース (25 op・現 fork の API をほぼ踏襲し pgfs `FileSystem.cs` の変更を最小化) |
| `MountOptions.cs` / `FuseFileInfo.cs` / `DirectoryContent.cs` / `ReadDirFlags.cs` / `TimespecExtensions.cs` | 補助型 (現 fork から移植・整理) |

※ `Native/` サブ名前空間にするか平置きかは実装時に決めてよい。

---

## 4. 設計判断 (決定)

1. ✅ **シンボル解決 = (A) `dlopen`+`dlvsym` 踏襲** (§3-1)。symbol versioning (FUSE_3.0 / fuse_new=FUSE_3.1) 対応が本質。
2. ✅ **ブリッジ = (A) デリゲート + `GetFunctionPointerForDelegate` 踏襲** (§3-2)。1 プロセス 1 マウント 1 FS の pgfs に最小コスト (AOT は元々無効方針)。
3. ✅ **`use_ino` = (B) `fuse_config` を型定義して `cfg->use_ino`** (§3-6)。脆い offset 64 poke を廃し `fuse_config` 全フィールドを正しく型起こし (実 `fuse.h` で検証)。
4. ✅ **libc 依存 = (A) `Tmds.LibC` (MIT) 継続 (暫定)** (§3-7)。`stat`/`statvfs`/`timespec`/errno は実証済みを使う。自前 libc は FUSE 層安定後に別タスク。
5. ✅ **op 配線範囲 = 実装 25 + `init` のみ** (§3-3)。将来 op (`copy_file_range`/`lseek`) の枠は今作らない (YAGNI・必要時に追加)。
6. ✅ **`Pgfs.Fuse` 内のファイル構成 = §3-8 の構成で確定**。`Native/{LibFuse,FuseStructs,FuseOperations}` + `FuseMount` + `IFuseFileSystem`/`FuseFileSystemBase` + 補助型。`Native/` のサブ名前空間化は実装時の裁量。

**→ ①〜⑥ すべて確定。(B) 設計クローズ。次は実装フェーズ。**

## 5. 検証方針

- 内製バインディングが fork と**挙動等価**であることを full e2e で確認: Linux 36/36 + multinode race + audit (`fuse_get_context` 経路) + xattr/ACL (`getxattr` size-probe・`system.posix_acl_access`)。
- `use_ino` 回帰: `ln a b` 後の `stat -c '%i'` でハードリンク間 `st_ino` 一致 + `attr_timeout=0` での `st_nlink` 即時反映。
- symlink 引数順の修正を `ln -s` の実機テストで確認 (fork のバグが内製で直っていること)。

## 6. `fuse_config` のミラーとオフセットの確かめ方

[LibFuse.structs.cs](../../src/fuse/src/LibFuse.structs.cs) の `fuse_config` は **libfuse の構造体の部分ミラー**で、
`FuseMount.Init` がネイティブのポインタに被せて書く。**必要なフィールドまでしか写していない**ので、
先を触るときはオフセットを確かめること。**1 つずれると `show_help` や `modules` ポインタを踏む。**

### 実測で確定しているオフセット (x86-64 SysV・libfuse **3.10.2**)

このマシンに入っているのは **`fuse3-3.10.2-9.el9`** である (`/usr/lib64/libfuse3.so.3 -> libfuse3.so.3.10.2`)。

| offset | 型 | フィールド |
|---:|---|---|
| 0〜20 | int / unsigned int ×6 | `set_gid` / `gid` / `set_uid` / `uid` / `set_mode` / `umask` |
| 24 / 32 / 40 | double | `entry_timeout` / `negative_timeout` / `attr_timeout` |
| 48 / 52 / 56 | int | `intr` / `intr_signal` / `remember` |
| 60 | int | `hard_remove` |
| **64** | int | **`use_ino`** (このミラーが以前から書いていた値) |
| 68 / 72 / 76 / 80 / 84 | int | `readdir_ino` / `direct_io` / `kernel_cache` / `auto_cache` / `ac_attr_timeout_set` |
| 88 | double | `ac_attr_timeout` |
| **96** | int | **`nullpath_ok`** |
| 100 / 104 / 112 | int / `char*` / int | `show_help` / `modules` / `debug` |

> ⚠ **3.10 には `no_rofd_flush` が無い。** あのフィールドは **3.15 で入った**ので、
> **3.15 以降のヘッダで数えると `ac_attr_timeout` から後ろが全部ずれる。**
> **必ず「入っている版」のヘッダで数えること。**

### 確かめ方 (ヘッダが無くてもできる)

このマシンには **`fuse3-devel` が入っておらず `/usr/include/fuse3/fuse.h` が無い**。それでも
**書いてから読み返す**ことで鎖を検算できる。`Init` の中で:

1. **libfuse が入れた既定値が読めるか** — `intr_signal` が **`SIGUSR1` (10)** になっていること。
   libfuse が `fuse_new` で入れる値で、pgfs は上書きしていない。
2. **自分が渡した値が読めるか** — `attr_timeout` が **0** になっていること。
   これは **pgfs が `-o attr_timeout=0` で渡した値**なので、**ここが読めれば double 3 本の位置まで合っている**
   = 前半の鎖が繋がっている。`entry_timeout` は libfuse 既定の **1** が読める。
3. そのうえで書き、**読み返して期待どおりか**を見る。

**実測ログ** (2026-09-21):

```
intr_signal=10  entry_timeout=1  attr_timeout=0  remember=0  ac_attr_timeout_set=0
hard_remove=1   use_ino=1        nullpath_ok=1
```

**2 が効く**のがこの手順の肝である。1 だけだと「libfuse の既定値が偶然そこにあった」を否定できないが、
**こちらが渡した値が読めたなら、その位置は本当にそのフィールドである。**

### 実行時の安全弁 (立てるときは一緒に入れる)

**検証済みの前半より先を書くときは、書く前に指紋を見る**こと。`intr_signal == SIGUSR1` を確かめ、
違えば**その先には何も書かず警告だけ出す**。ミラーが実物とずれていた場合に、
**ポインタを踏む前に気づける**。

> **注**: `nullpath_ok` は**現在は立てていない**。理由と、立てようとして戻ってきた経緯は
> [handle-context.ja.md §なぜ `hard_remove` を立てないか](handle-context.ja.md) が正。
> **本節はオフセットと検算手順の記録**であり、「立てるべき」という意味ではない。
> 低レベル API への移行を検討するときに、**この計算をやり直さずに済むように残してある。**
