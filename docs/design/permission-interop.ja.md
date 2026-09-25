# 権限・所有権・ACL の Linux↔Windows 相互運用 設計

> **道順**: [docs/README.ja.md](../README.ja.md) › **本書**
>
> **この doc が正である範囲**: **権限・所有権・ACL の内部モデルと相互運用の設計判断** — POSIX ACL を正準とし
> Windows をその投影ビューとする決定、名前正規化と principal マッピング、ACL の評価順、新規作成時の
> owner / group の決め方、「名前ベースで持つ」運用前提。**どの OS でどう見えるべきか**はここが正。
>
> **隣接する doc とその担当範囲**:
>
> | doc | そちらに書くもの |
> |---|---|
> | [permission-interop-diagram.html](permission-interop-diagram.html) | 本設計の**図** (HTML/SVG) |
> | [../Mount.ja.md](../Mount.ja.md) | **Linux 側**の現行 CLI 契約とオペレーション一覧 |
> | [../Assign.ja.md](../Assign.ja.md) | **Windows 側**の現行 CLI 契約とオペレーション一覧 |
> | [windows-parity.ja.md](windows-parity.ja.md) | Windows 展開の **as-built と未実装の段取り** |
> | [audit-log.ja.md](audit-log.ja.md) | ACL / 所有権**変更の監査行** |
> | [settings-matrix.ja.md](settings-matrix.ja.md) | `mount.self_uname` / `self_gname` / `file_system.unknown_name` 等の**設定項目** |
> | [xattr-bytea.ja.md](xattr-bytea.ja.md) | ACL を運ぶ **xattr のバイト列透過** |
> | [../next.ja.md](../next.ja.md) | 残課題の**優先順位** (punch-list) |

> **ステータス**: 設計合意済 (合意後に**改訂**)。**実装は主目的まで完了** — 即時 5 項目 + ACL 本体 3-0〜3-3 実装済 (回帰 Windows 26/26・Linux 35/35、進捗は下表「合意事項」)。**Windows の判定 (3-4 の Windows 側) は v0.2.1 で実装** (§Windows の判定)。**Linux の named ACL の厳密 enforce のみ要件待ちで保留**。設計判断は本書を正とし、**実装詳細は [Mount.ja.md](../Mount.ja.md) / [Assign.ja.md](../Assign.ja.md) / [FileSystemUtils.cs](../../src/dokan/src/FileSystemUtils.cs) を参照**。
> **改訂点**: 旧版の「双方向フル往復 + `opaque` で Windows 固有 ACL を verbatim 保持」を撤回し、
> **POSIX 正準 + Windows は投影ビュー (lossy projection)** に変更。併せて名前正規化・principal マッピング表を確定。

PGFS は **認証システムではなく、名前ベース ACL を保持するストレージ**である。UID/GID/SID/GUID を持たず、
ユーザー/グループ DB も持たず、LDAP/AD にも依存しない。認証は各 OS / PostgreSQL に委譲し、ACL の判定は
クライアントドライバ (mount / assign) が行う。内部モデルは **POSIX ACL** を正準とし、**Windows ACL はその投影
ビュー**として描画する。

> **2026-09-19 静的照合**: 以下の合意事項は設計目標を含む。Linux の `Access` は未実装で、named ACL の厳密 enforce・default ACL の作成時継承は完成していない。Windows の作成者にはプロセスの既定名を使う経路があり、ACL 投影の成功と要求元ごとのアクセス制御を同一視しない。今回の実機挙動は未検証。**`Access` を実装しないのは意図した設計** — アクセス可否の判定はマウント時の `default_permissions` でカーネルに委ねる。**いま残っている制限は [CHANGELOG.ja.md §既知の制限](../../CHANGELOG.ja.md) が正**で、展開案は [Windows 設計](windows-parity.ja.md) を参照する。

## 合意事項 (10 項目)

| # | 項目 | 決定 |
|---|---|---|
| 1 | 名前正規化 | 保存時・照合時に **ドメイン除去 + 全角ASCII→半角 + 小文字化**。**呼び出し元名も正規化** → Linux でも `alice`=`Alice`=`Ａlice` を同一視 |
| 2 | ドメイン除去 | `\`(NetBIOS)と `@domain`(UPN)を両方除去 |
| 3 | principal マッピング | 双方向フル。**DB は Linux 名で保存** (root/nobody/nogroup)。root↔Administrator(s)、other↔Everyone、**nobody/nogroup↔ANONYMOUS LOGON** |
| 4 | Windows ACL | **投影ビューに格下げ** (opaque/双方向往復は撤回) |
| 5 | ACL モデル | POSIX-only / allow のみ。`acl[]={principal_type,principal_name,rights}`、`mode + acl[]` 併存 |
| 6 | Win属性 xattr | `user.win.attrs` (user 名前空間) に JSON `{hidden,system,archive}`。`compressed` は将来 |
| 7 | ReadOnly | ~~アクセス自身が書込不可なら ReadOnly (owner/group/other writable を正規化済み呼出元で評価)~~ → **v0.2.1 で「誰も書けない (owner / group / other の w が全部落ちている) ときだけ ReadOnly」に変更**。誰が書けるかは権限の判定 (§Windows の判定) が決める |
| 8 | owner=group | owner=nobody / group=該当 |
| 9 | 匿名アクセス | ポリシー明記のみ・コード変更なし (pgfs は匿名接続経路を持たない) |
| 10 | ACL 判定 | クライアントドライバで POSIX 順評価 (Linux もカーネル委譲せず pgfs 層で正規化込み判定) |

## 保存するもの / 保持しないもの

**保存**: `owner_name` (= `pgfs_inode.uname`)、`group_name` (= `gname`)、`mode` (= `st_mode`)、`acl[]`、`xattr[]`。
**保持しない**: UID / GID / SID / GUID (実行時に各 OS が名前から解決するだけ。DB には一切残さない)。

## 新規作成時の owner / group の決め方 (OS で違う)

**同じ FS でも「新しく作ったファイルの所有者 / グループがどう決まるか」は OS ごとに違う**。
保存形式 (名前のみ) は共通だが、決め方は OS の概念に合わせてある。

| | Linux (mount.pgfs) | Windows (assign.pgfs) |
|---|---|---|
| owner (`uname`) | `fuse_get_context()->uid` を `UserResolver.UnameOf` で解決 | 要求元の `WindowsIdentity.User` (SID) を `WindowsUserResolver.UnameOf` で解決 |
| group (`gname`) | 呼び出し元の **gid** (= 実質 primary group) | **親ディレクトリの `gname` を継承** |
| 解決できないとき | 見せ方は OS から (Linux: overflowuid / overflowgid)。DB に書くときは `file_system.unknown_name` (`(unknown)`) | 見せ方は `ANONYMOUS LOGON`。DB に書くときは同左 (**マウントプロセスの user には化かさない**。自分の名乗りは `mount.self_uname` / `self_gname` で、自分の SID のときだけ) |

**Windows が group を継承する理由**: Windows の token primary group は実運用でほぼ `Domain Users` / `None` で、
アクセス判定にも使われない。そのまま保存すると `gname` がノイズ値で埋まり `mode` の group ビットが無意味になる。
親から継承すれば「同じツリーは同じグループ」になり、Linux から見ても意味のある値になる。
→ **Linux で作ったファイルと Windows で作ったファイルでは group の出どころが違う**ので、
同一ディレクトリ内で `gname` が混在し得る (どちらも正規化済みの名前なので読み書きはできる)。
設計の背景は [windows-parity.ja.md §所有者 / グループの決め方](windows-parity.ja.md)。

**⚠ ドメインは潰れる**: 正規化でドメインを落とすので、**`CORP\alice` と `LOCAL\alice` は同じ `alice` になる**。
読み取り投影では以前からそうだったが、 Windows の**書き込み (所有者の決定)** も要求元由来になったため、
**別ドメインの同名ユーザーが同一の owner として保存される**。ドメイン環境での実地検証は未実施。

## 名前正規化 (★ 中核)

owner / group / principal 名は、**保存時も照合時も**次の順で正規化する。**呼び出し元(アクセス主体)の名前にも
同じ正規化を適用**するので、Linux 本来の case-sensitive を pgfs 層で上書きし、両 OS で大小・全半角を同一視する。

1. **ドメイン除去**: `DOMAIN\name` (NetBIOS) と `name@domain` (UPN) → `name`
2. **全角 ASCII → 半角**: U+FF01–FF5E を U+0021–007E へ (例 `Ａlice` → `Alice`)
3. **小文字化**

```
DOMAIN\Alice  /  ALICE  /  alice@example.com  /  Ａlice   →   alice
```

> 注意: Linux はユーザー名を case-sensitive に扱える (大文字を含む名前も理論上可) が、本設計では小文字化により
> `Alice` と `alice` を同一視する。大文字を含む Linux アカウント名は同一視されるため、**小文字命名を運用前提**とする。

## principal マッピング (well-known, 双方向)

**DB は Linux 名で保存**する。各ドライバが「保存(各OS名→Linux名 + 正規化)」と「描画/解決(Linux名→各OS名)」の
両方向で変換する。

| PGFS (保存) | Linux | Windows |
|---|---|---|
| `root` (user) | `root` | `Administrator` |
| `root` (group) | `root` | `Administrators` |
| `nobody` (user) | `nobody` | `ANONYMOUS LOGON` (S-1-5-7) |
| `nogroup` (group) | `nogroup` | `ANONYMOUS LOGON` (専用グループ SID が無いため同一を流用) |
| `other` (class) | other:: クラス | `Everyone` |

- **不明名の扱い**: 名前解決に失敗しても **ACL 自体は書き換えない**。保存は元の名前のまま、**評価時のみ**
  `nobody` / `nogroup` (Windows 描画では `ANONYMOUS LOGON`) に解決する。
- root↔Administrator(s) は当初は表示判定 (旧 `IsWritable`・v0.2.1 で廃止) だけにあったので、
  **保存方向にも拡張**し、nobody/nogroup/other も表に従う。

## ACL モデル (POSIX-only / allow のみ)

```
File
 ├─ owner_name        (正規化済み)
 ├─ group_name        (正規化済み)
 ├─ mode              (owner/group/other の基本3クラス = 正準)
 ├─ acl[]             (named user/group + mask)
 └─ xattr[]

ACL Entry
 ├─ principal_type : user | group
 ├─ principal_name : (正規化済み)
 └─ rights         : r / w / x
```

- **mode** が基本3クラス (owner/group/other) の正準。`chmod` / 単純な SetSecurity はここを書く。
- **acl[]** は named user/group エントリと **mask** (= named ∪ group の union を都度再計算)。
- **allow のみ**。Windows の **deny ACE は採用しない** (投影で落とす。→ §Windows 投影ビュー)。
- **物理保存**: inode の xattr `user.pgfs_acl` に JSON で持つ (`entries[]` と、ディレクトリの継承用 `default[]`)。
  スキーマ追加なし。`opaque` は廃止 (旧版から削除)。

## ACL 評価 (クライアントドライバ / POSIX 順)

```
owner → named user → group / named group → other
```

- **Windows ACE 順評価は採用しない** (POSIX 準拠順で判定)。
- **Linux もカーネル `default_permissions` に丸投げしない**。pgfs ドライバが、呼び出し元 uid → 名前解決 →
  §名前正規化 → entries 照合、の順で自前判定する (カーネルの数値 uid 比較では §名前正規化の大小・全半角同一視が
  効かないため。決定 [1] と [10] の整合)。

## 所有者 (owner にグループが来た場合の振り分け)

Windows で所有者にグループ SID が指定された場合 (例 `Developers`):

```
Windows: Owner = Developers (group SID)
   ↓
PGFS:    owner_name = nobody     group_name = developers
```

owner は常に「ユーザー」意味として扱い、グループが来たら **owner は nobody**、グループ名は **group フィールド**へ
振り分ける (素朴に owner_name へ入れると Linux の owner 解決 (getpwnam) で nobody 化し group 情報が失われるため)。

## Windows 属性

- **ReadOnly のみ mode と連携**: **誰も書けない (owner / group / other の w が全部落ちている) なら ReadOnly** とする ([`IsReadOnly`](../../src/dokan/src/FileSystemUtils.cs)・v0.2.1)。
  当初は「アクセスする自身が書き込み不可なら ReadOnly」だったが、属性を返す `GetFileInformation` は呼び出し元を持たないので**マウントしたユーザー**で評価するしかなく、
  **他人の所有の `0644` が読み取り専用に見えて Windows から削除できなかった** (Windows は読み取り専用のファイルの削除を断る。POSIX では削除は親の w で決まる)。
- **Hidden / System / Archive**: xattr `user.win.attrs` (**user 名前空間 = Linux の `getfattr` からも可視**) に
  **JSON** で保存。

  ```json
  { "hidden": true, "system": false, "archive": true }
  ```

- **compressed**: 今後の課題 (現時点は非対応。キーに含めない)。
- **Linux 側**: DOS 属性は基本なし。Hidden は dotfile (`.name`) からの推定 fallback、System/Archive は無視。

## 匿名アクセス

- **ポリシー**: anonymous / guest / null session は非サポート (認証失敗扱い)。
- pgfs は元々**匿名接続経路を持たない** (DB 認証 + OS マウントが必須) ため、現時点でのコード変更はなし。
- `nobody` / `nogroup` は「**未解決 principal の評価結果**」であって、匿名接続の許可ではない。

## Windows は投影ビュー (旧 opaque を撤回)

POSIX ACL を正準とし、Windows ACL はその **射影 (projection)**。**deny ACE / ACE 順 / 継承フラグは採用しない**ので、
Windows 固有の ACL 構造は投影で落ちる (原 OS に戻しても復元しない)。

- **メリット**: 実装が大幅に簡素 (`opaque` の保存・復元、deny、ACE 順の保持が不要)。
- **トレードオフ**: Windows ⇄ Windows での ACL **完全一致は保証しない** (複雑な DACL は POSIX モデルへ縮約される)。

## データモデル (物理)

```
pgfs_inode
 ├─ uname   TEXT     ← owner_name (正規化済み / Linux 名)
 ├─ gname   TEXT     ← group_name (正規化済み / Linux 名)
 ├─ st_mode INTEGER  ← mode (基本3クラス = 正準)
 └─ xattr_names TEXT[] / xattr_values BYTEA[]  (並行配列 KVS、値は bytea。docs/xattr-bytea.md)
      ├─ user.pgfs_acl   : { "v":1, "entries":[{principal_type,principal_name,rights}...], "default":[...] } (JSON バイト列)
      └─ user.win.attrs  : { "hidden":bool, "system":bool, "archive":bool } (JSON バイト列)
```

## 実装の適用可否 (即時 / Phase)

| 決定 | 区分 | 主な変更箇所 |
|---|---|---|
| [1] 名前正規化 | **✅ 実装済** | 共通 [NameNormalizer](../../src/core/src/Utility/NameNormalizer.cs) を新設 (全半角→半角 + domain 除去 + 小文字)。[UserResolver](../../src/fuse/src/UserResolver.cs) / [WindowsUserResolver](../../src/dokan/src/WindowsUserResolver.cs) / [mount/FileSystem](../../src/fuse/src/FileSystem.cs) の保存・解決・呼び出し元に挿入 |
| [2] ドメイン/UPN 除去 | **✅ 実装済** | `NameNormalizer.StripDomain` が `\` と `@` 両対応。Windows 側の旧 `StripDomain` は撤去 |
| [3] principal マッピング | **✅ 実装済** | [WindowsUserResolver](../../src/dokan/src/WindowsUserResolver.cs) に well-known alias (MapWinUserToPgfs/MapWinGroupToPgfs/MapPgfsUserToWin/MapPgfsGroupToWin)。nobody/nogroup ↔ `NT AUTHORITY\ANONYMOUS LOGON` |
| [6] Win属性 xattr | **✅ 実装済** | `user.win_attrs` (4byte) → `user.win.attrs` (JSON `{hidden,system,archive}`)。[FileSystemUtils](../../src/dokan/src/FileSystemUtils.cs) の Load/SaveWinAttrs |
| [7] ReadOnly | **✅ 実装済 (v0.2.1 で条件を変更)** | [`IsReadOnly`](../../src/dokan/src/FileSystemUtils.cs) = w が全部落ちているときだけ。旧 `IsWritable` (マウントしたユーザーが書けるか) は廃止 |
| [9] 匿名ポリシー | **✅ 記載のみ** | 本書に明記 (コード変更なし) |
| [5] ACL モデル (正準ドキュメント) | **✅ 3-0 実装済** | [PgfsAcl](../../src/core/src/Models/PgfsAcl.cs) (Lib): `user.pgfs_acl` JSON (entries[]/default[])。allow のみ |
| [4] 投影ビュー (Windows 読み) | **✅ 3-1 実装済** | [FileSystemUtils.BuildSecurity](../../src/dokan/src/FileSystemUtils.cs) + [GetFileSecurity](../../src/dokan/src/FileSystem.cs)。owner/group SID + mode 由来 ACE + named ACL を SD に投影。Windows e2e に `test_getfilesecurity_projection` 追加 (25/25 PASS) |
| [4] 投影 (Windows 書き) / [8] owner=group | **✅ 3-2 実装済** | [SetFileSecurity](../../src/dokan/src/FileSystem.cs) + [ApplySecurity](../../src/dokan/src/FileSystemUtils.cs): SD → mode(基本3クラス) + acl[](named) + owner/group。owner が group SID なら nobody/該当へ ([IsGroupSid](../../src/dokan/src/WindowsUserResolver.cs) = LookupAccountSid)。deny は投影で落とす。Windows e2e に `test_setfilesecurity_roundtrip` (26/26)。owner=group の Explorer 操作は SeRestorePrivilege 依存のため e2e 未カバー (ロジックは実装済) |
| [4][5] Linux POSIX ACL | **✅ 3-3 実装済 (access)** | [PosixAcl](../../src/fuse/src/PosixAcl.cs) コーデック + [mount/FileSystem](../../src/fuse/src/FileSystem.cs) で `system.posix_acl_access` ⇄ st_mode(基本3クラス) + `user.pgfs_acl`(named) + mask 算出。named 無しは ENODATA。Linux e2e に `test_posix_acl_named_user` (35/35)。**`system.posix_acl_default` は現状パススルー** (Linux 内 round-trip のみ、Windows 継承変換は未対応) |
| [10] ドライバ評価 | **Windows は v0.2.1 で実装 / Linux は保留** | Windows (Dokan) は SD で判定しないので、POSIX 順の自前判定を入れる — 下記「Windows の判定」。Linux は `default_permissions` のカーネル判定のまま (named ACL の厳密 enforce は要件待ち) |

> **回帰**: 即時適用 5 項目 ([1][2][3][6][7]) 実装後、Windows e2e **24/24** (属性系含む) / Linux e2e **34/34** + race **4/4** (chmod/chown/fallback 含む) / 監査専用 [audit.sh](../../tests/citus/audit.sh) **12/12** (caller_uname も正規化経由) すべて PASS。
>
> 監査連携: ACL/所有権変更は [監査ログ](audit-log.ja.md) の対象。chmod/chown フックに加え `setacl` op を検討 (Phase)。

### Phase 3-4 (ドライバ評価) の再評価

決定 [10] は「クライアントドライバで POSIX 順評価、Linux もカーネル委譲しない」だったが、3-1〜3-3 を終えた時点で次の状況:

- **読み/書きの往復は完成**: Windows は `Get/SetFileSecurity` で SD ⇄ (mode + 正準 ACL)、Linux は `system.posix_acl_access` ⇄ (mode + 正準 ACL)。`ls -l` / Security タブ / getfacl すべて正準ストアを反映する。
- **enforcement の現状**: Windows はカーネルが返却 SD を見て判定、Linux はカーネルが mode (+ FUSE が要求すれば ACL) で判定。名前正規化 ([1]) は**保存名側**で効くため、所有者一致判定は既に正規化済みの名前で行われる。
- **独自評価が要るのは**: named ACL を **Linux カーネルに enforce させる**には `-o default_permissions` + ACL を効かせるか、ドライバが各 op で判定する必要がある。現状 named ACL は表示・往復はできるが Linux での enforce はカーネル設定依存。

→ **3-4 は「named ACL の enforce をどこまで厳密にやるか」の問題に縮小**した。表示・相互運用が主目的なら現状で実用十分。厳密 enforce が要るワークロードが出たら、(a) Linux mount に ACL を効かせる / (b) ドライバ評価、を改めて設計する。**当面 3-4 は保留 (要件が出てから)** とし、3-1〜3-3 で相互運用の主目的は達成とする。

### Windows の判定 (v0.2.1 で実装)

**前提の訂正**: 上の再評価に「Windows はカーネルが返却 SD を見て判定」とあるが、**これは誤り**である。Dokan は
`GetFileSecurity` が返した SD で**アクセス判定をしない** (判定はユーザーモードのファイルシステムに任されている)。v0.2.0 では
`root:root 755` の root 直下へ Windows から書けることを実測した ([CHANGELOG.ja.md](../../CHANGELOG.ja.md))。
Linux は従来どおり `default_permissions` でカーネルが mode を判定する (本節は Linux の挙動を変えない)。

**決めたこと**:

| 項目 | 決定 |
|---|---|
| 方式 | **Dokan 側で POSIX 順に自前判定する** (owner → named user → group / named group → other)。Windows の `AccessCheck` に投影 SD を渡す案は、ACE の和集合で判定するので「owner が group より狭い」ときに POSIX と結果が変わるため採らない |
| 判定する場所 | 評価器 (mode + uname / gname + 正準 ACL + 呼び出し元 → 許される r/w/x) は **Core** に置く (将来 Linux の [10] でも使えるように)。Dokan はアクセスマスク → r/w/x の読み替えと、どのコールバックで何を見るかだけを持つ |
| 設定 | **`app.enforce_permissions`** (bool・**既定 `true`**・**DB 保存**・`Live`)。FS 全体で揃える項目なので DB に置く ([settings-matrix.ja.md](settings-matrix.ja.md) の「FS 固有は DB」)。切り替えは `pgfsctl config set app.enforce_permissions false` (走行中に反映)。mkfs のフラグは持たない。**Windows (assign) にだけ効く** — Linux の判定はマウントオプション側 |
| 移行 | v0.2.0 で作った FS には行が無い → 既定の `true` が効く。**上げた時点で Windows から書けていたものが拒否され得る** (CHANGELOG の「移行が必要な変更」に書く) |

**呼び出し元** (`CreateFile` の中だけで取れる — [Assign.ja.md](../Assign.ja.md) の `GetRequestor` の制約):

- ユーザー = トークンの User SID → `WindowsUserResolver.UnameOf` (正規化 + マッピング + `mount.self_uname`)。
- グループ = トークンの**有効な**グループ SID → `GnameOf` (SID ごとにキャッシュ済み)。
- **Administrators が有効なトークン (昇格済み) は素通し** (Linux の root 相当)。UAC で制限されたトークンは Administrators が deny-only なので**素通しにならない**。
- 確定した主体 (uname / gname の集合 / 素通しか) は `OpenFileContext` に載せ、`MoveFile` / `SetFileSecurity` / `SetFileAttributes` / `DeleteFile` はそれを使う (監査の主体と同じ持ち回り)。

**アクセスマスクの読み替え** (`CreateFile` で、開く対象に対して):

| 要求 | 必要なもの |
|---|---|
| `ReadData` (= `ListDirectory`) / `ReadExtendedAttributes` / `GenericRead` | r |
| `WriteData` (= `AddFile`) / `AppendData` (= `AddSubdirectory`) / `WriteExtendedAttributes` / `GenericWrite` | w |
| `Execute` (= `Traverse`) / `GenericExecute` | x |
| `WriteAttributes` (時刻・属性) | 所有者 **か** w |
| `ChangePermissions` (WRITE_DAC) | 所有者 |
| `SetOwnership` (WRITE_OWNER) | 素通し (Administrators) のみ (POSIX の chown と同じ) |
| `Delete` / `DeleteOnClose` | 親の w + sticky 規則 (下) |
| `ReadAttributes` / `ReadPermissions` / `Synchronize` / `MaximumAllowed` | 常に可 (stat に相当) |

**親ディレクトリを見る操作**:

- 作る (`CreateNew` / `Create` / `OpenOrCreate` で無かったとき): 親の w。
- 消す / rename の元: 親の w。**親が sticky (`01000`) なら、対象の所有者・親の所有者・素通しのどれか**でないと不可。
- rename の先: 先の親の w。上書きするときは先の対象にも sticky 規則。
- 中身の切り詰め (`Truncate` / 既存への `Create`): 対象の w。

**判定しないもの (意図した差)**:

- **途中のディレクトリの x (探索)**。Windows は既定で全員が「走査チェックのバイパス」権限を持つので、それに合わせる。Linux (`default_permissions`) は経路の各ディレクトリの x を見るので、**`0700` のディレクトリの奥にある `0644` のファイルは、Windows からだけ読める**。
- 読み取り専用属性の表示 (決定 [7]) は、呼び出し元の取れない `GetFileInformation` から呼ばれるので、**主体によらない条件** (w が全部落ちているか) で決める (下の as-built)。

**正準 ACL の扱い**: named エントリは mode の group ビットで絞らない (保存形は mask を持たず、Linux の投影で mask を**算出**しているため — 決定 [5])。
名前の分からない所有者 (`file_system.unknown_name` = `(unknown)`) は誰とも一致しないので、その所有者のファイルは other の権利で判定される。

**拒否のしかた**: `DokanResult.AccessDenied` (= `STATUS_ACCESS_DENIED`。Explorer は「アクセス許可が必要です」を出す)。拒否は Information でログに残す (要求・パス・主体・必要だった権利)。

**検証の計画 (テスト用のユーザーを作らずにできる)**: Windows のテストは**昇格していないシェル**で動くので、呼び出し元は普段のユーザー (素通しにならない)。
「ほかの人のファイル」は Linux のマウントから `root` / `nobody` の所有で作れば足りる。crossclient に次を足す: `root:root 0644` は読めて書けない /
`0600` は読めない / named ACL でそのユーザーに rw を付ければ書ける / group `users` の `0664` は group で書ける / `root:root 1777` の中の `root` のファイルは
消せない (自分のファイルは消せる) / `app.enforce_permissions=false` で全部通る。昇格シェルでの素通しは手動で 1 回確かめる。
**修正前のビルドで拒否系が「書けてしまう」で落ちることを先に確かめる**。

**as-built**: 評価器は [PermissionEvaluator](../../src/core/src/Api/PermissionEvaluator.cs) + [AccessCaller](../../src/core/src/Api/AccessCaller.cs) (Core)、
Dokan 側は [FileSystem.Access.cs](../../src/dokan/src/FileSystem.Access.cs)。検証は [tests/windows/permissions.ps1](../../tests/windows/permissions.ps1) (14 件 = 最初の 12 件 + 読み取り専用属性の 2 件 (下の as-built)。**修正前のビルドでは拒否系 7 件が「`ok` だった」で落ちる**ことを確認済み)。
計画との差分:

- **検証はクロスクライアントではなく単独スイートにした**。仕込み (他人の所有・mode・named ACL) は**昇格したシェルから SetFileSecurity** で作れる (所有者に Administrator の SID を渡せば `root` になる) ので、
  Linux 側のマウントは要らなかった。確かめる操作は **Administrators を無効にした制限トークン** (`CreateRestrictedToken`) で行う (昇格したシェルは素通しで何も確かめられないため)。
  sticky だけは Windows から立てられないので、`-StickyDir` に root 所有の `1777` を渡したときだけ見る。
- **sticky のディレクトリへの `DeleteChild` を所有者だけにした** (計画に無かった)。Windows はファイルへの `Delete` を断られると**親を `DeleteChild` で開き直し、開けたら消す**
  (NTFS の FILE_DELETE_CHILD。sticky のテストが「消せてしまう」で落ちて判明)。sticky でなければ `DeleteChild` は親の w と同じ意味なのでそのまま。
- **rename の先の親は `CreateFile` で止まる**ことが多い。Windows は rename の前に先の親を書き込み要求で開きにくる。`MoveFile` の判定は二重の守りとして残した。
- **「読み取り専用」属性 (決定 [7]) は「誰も書けないときだけ」に変えた**。当初はマウントしたプロセスのユーザー基準のままにしていて、他人の所有で書けないファイルが読み取り専用に見え、
  **Windows は読み取り専用のファイルの削除を断る**ので昇格しても Windows から消せなかった (v0.2.0 からの挙動。テストの後片付けで判明)。
  残る差は持ち主が全員の w を落とした (`chmod a-w`) ファイルだけ — Windows では消せず、Linux では親の w があれば消せる。
  **「消せない」を両 OS で揃えたいなら immutable 属性を別に持つ**案がある (未着手)。テストは permissions.ps1 の 2 件 (修正前のビルドで落ちることを確認済み)。

## 具体例: Windows で所有者を「Users」に設定

```
Windows: Owner = Users (BUILTIN\Users グループ)
   ↓ [8] owner=group 振り分け + [1] 正規化(domain除去 + 全半角 + 小文字)
PGFS:    owner_name = "nobody"    group_name = "users"
   ↓ [3] マッピング / [1] 呼び出し元も正規化
Linux:   owner = nobody / group = users (getgrnam で解決)
Windows: owner = ANONYMOUS LOGON の投影 / group = Users
```

→ owner が group の場合の振り分け + 正規化で、group `users` が保たれ大小も吸収される。

## 運用前提 (名前ベースの明文化)

- pgfs は principal を**正規化済み名前文字列**で持つ。Linux と Windows で「同じ人/グループ」を指すには、両機で
  **同じ名前 (小文字・ドメイン無し) が解決できる**こと (AD/LDAP/手動同期) が前提。
- 名前が解決できない場合、ACL エントリは保持されるが **評価時に nobody/nogroup** となり enforcement されない。

## ドメイン名は所有者からは落ち、監査には残る (整理)

**`NameNormalizer` は `DOMAIN\name` (NetBIOS) と `name@domain` (UPN) の domain 部を意図的に落とす**
(上の決定 [1][2])。**名前ベースの ACL ストアとして「同じ人」を text 一致で表す**ための正規化なので、
これは仕様である。**「未検証の不具合」ではない。**

そのうえで、**何が失われて何が残るか**をコードで確かめた結果:

| | domain |
|---|---|
| **所有者 (`{prefix}inode.uname` / `gname`)** | **落ちる**。`CORP\alice` と `LOCAL\alice` は**どちらも `alice`** になり、**別人として区別できない** |
| **監査 (`{prefix}audit.caller_domain`)** | **残る**。Dokan 側が `CreateFile` で `DOMAIN\user` を分解して `AuditContext.Domain` に入れ、監査行に別列で書く |

**つまり「誰がやったか」の記録は保てるが、「誰のものか」は同名衝突する。**

**ドメイン環境で困るのはどんな場合か** (実機未検証・**環境が無いと確かめられない**):

- 複数ドメインの**同名ユーザー**が同じ FS を使う。所有者が混ざる。
- Windows 側で `CORP\alice` に見せたいが、別ドメインの `alice` も同じ所有者に見える。

**直すとしたら**、正規化を変えて `domain\name` のまま保存することになるが、**Linux 側の uname とは
一致しなくなる** (Linux に `CORP\alice` というユーザーは居ない) ので、**クロス OS の「同じ人」が壊れる**。
**決定 [1][2] のトレードオフそのもの**なので、変えるなら permission-interop の設計から見直すこと。
