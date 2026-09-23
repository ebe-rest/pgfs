# 権限・所有権・ACL の Linux↔Windows 相互運用 設計

> **道順**: [docs/README.md](../README.md) › **本書**
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
> | [../Mount.md](../Mount.md) | **Linux 側**の現行 CLI 契約とオペレーション一覧 |
> | [../Assign.md](../Assign.md) | **Windows 側**の現行 CLI 契約とオペレーション一覧 |
> | [windows-parity.md](windows-parity.md) | Windows 展開の **as-built と未実装の段取り** |
> | [audit-log.md](audit-log.md) | ACL / 所有権**変更の監査行** |
> | [settings-matrix.md](settings-matrix.md) | `mount.fallback_uname` / `fallback_gname` 等の**設定項目** |
> | [xattr-bytea.md](xattr-bytea.md) | ACL を運ぶ **xattr のバイト列透過** |
> | [../next.md](../next.md) | 残課題の**優先順位** (punch-list) |

> **ステータス**: 設計合意済 (合意後に**改訂**)。**実装は主目的まで完了** — 即時 5 項目 + ACL 本体 3-0〜3-3 実装済 (回帰 Windows 26/26・Linux 35/35、進捗は下表「合意事項」)。**named ACL の厳密 enforce (3-4) のみ要件待ちで保留**。設計判断は本書を正とし、**実装詳細は [Mount.md](../Mount.md) / [Assign.md](../Assign.md) / [FileSystemUtils.cs](../../src/dokan/src/FileSystemUtils.cs) を参照**。
> **改訂点**: 旧版の「双方向フル往復 + `opaque` で Windows 固有 ACL を verbatim 保持」を撤回し、
> **POSIX 正準 + Windows は投影ビュー (lossy projection)** に変更。併せて名前正規化・principal マッピング表を確定。

PGFS は **認証システムではなく、名前ベース ACL を保持するストレージ**である。UID/GID/SID/GUID を持たず、
ユーザー/グループ DB も持たず、LDAP/AD にも依存しない。認証は各 OS / PostgreSQL に委譲し、ACL の判定は
クライアントドライバ (mount / assign) が行う。内部モデルは **POSIX ACL** を正準とし、**Windows ACL はその投影
ビュー**として描画する。

> **2026-09-19 静的照合**: 以下の合意事項は設計目標を含む。Linux の `Access` は未実装で、named ACL の厳密 enforce・default ACL の作成時継承は完成していない。Windows の作成者にはプロセスの既定名を使う経路があり、ACL 投影の成功と要求元ごとのアクセス制御を同一視しない。今回の実機挙動は未検証。**`Access` を実装しないのは意図した設計** — アクセス可否の判定はマウント時の `default_permissions` でカーネルに委ねる。**いま残っている制限は [CHANGELOG.md §既知の制限](../../CHANGELOG.md) が正**で、展開案は [Windows 設計](windows-parity.md) を参照する。

## 合意事項 (10 項目)

| # | 項目 | 決定 |
|---|---|---|
| 1 | 名前正規化 | 保存時・照合時に **ドメイン除去 + 全角ASCII→半角 + 小文字化**。**呼び出し元名も正規化** → Linux でも `alice`=`Alice`=`Ａlice` を同一視 |
| 2 | ドメイン除去 | `\`(NetBIOS)と `@domain`(UPN)を両方除去 |
| 3 | principal マッピング | 双方向フル。**DB は Linux 名で保存** (root/nobody/nogroup)。root↔Administrator(s)、other↔Everyone、**nobody/nogroup↔ANONYMOUS LOGON** |
| 4 | Windows ACL | **投影ビューに格下げ** (opaque/双方向往復は撤回) |
| 5 | ACL モデル | POSIX-only / allow のみ。`acl[]={principal_type,principal_name,rights}`、`mode + acl[]` 併存 |
| 6 | Win属性 xattr | `user.win.attrs` (user 名前空間) に JSON `{hidden,system,archive}`。`compressed` は将来 |
| 7 | ReadOnly | アクセス自身が書込不可なら ReadOnly (owner/group/other writable を正規化済み呼出元で評価) |
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
| 解決できないとき | `mount.fallback_uname` / `fallback_gname` (既定 `nobody` / `nogroup`) | 同左 (**マウントプロセスの user には化かさない**) |

**Windows が group を継承する理由**: Windows の token primary group は実運用でほぼ `Domain Users` / `None` で、
アクセス判定にも使われない。そのまま保存すると `gname` がノイズ値で埋まり `mode` の group ビットが無意味になる。
親から継承すれば「同じツリーは同じグループ」になり、Linux から見ても意味のある値になる。
→ **Linux で作ったファイルと Windows で作ったファイルでは group の出どころが違う**ので、
同一ディレクトリ内で `gname` が混在し得る (どちらも正規化済みの名前なので読み書きはできる)。
設計の背景は [windows-parity.md §所有者 / グループの決め方](windows-parity.md)。

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
- root↔Administrator(s) は既存 [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs) の表示判定だけにあるので、
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

- **ReadOnly のみ mode と連携**: アクセスする自身が書き込み不可 (owner/group/other の writable 判定を
  §名前正規化済みの呼び出し元に対して評価) なら ReadOnly とする (現行 [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs) の考え方を踏襲)。
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
| [7] ReadOnly | **✅ 実装済** | [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs) を正規化済み名比較に簡素化 (root↔Administrator エイリアスはマッピングで吸収) |
| [9] 匿名ポリシー | **✅ 記載のみ** | 本書に明記 (コード変更なし) |
| [5] ACL モデル (正準ドキュメント) | **✅ 3-0 実装済** | [PgfsAcl](../../src/core/src/Models/PgfsAcl.cs) (Lib): `user.pgfs_acl` JSON (entries[]/default[])。allow のみ |
| [4] 投影ビュー (Windows 読み) | **✅ 3-1 実装済** | [FileSystemUtils.BuildSecurity](../../src/dokan/src/FileSystemUtils.cs) + [GetFileSecurity](../../src/dokan/src/FileSystem.cs)。owner/group SID + mode 由来 ACE + named ACL を SD に投影。Windows e2e に `test_getfilesecurity_projection` 追加 (25/25 PASS) |
| [4] 投影 (Windows 書き) / [8] owner=group | **✅ 3-2 実装済** | [SetFileSecurity](../../src/dokan/src/FileSystem.cs) + [ApplySecurity](../../src/dokan/src/FileSystemUtils.cs): SD → mode(基本3クラス) + acl[](named) + owner/group。owner が group SID なら nobody/該当へ ([IsGroupSid](../../src/dokan/src/WindowsUserResolver.cs) = LookupAccountSid)。deny は投影で落とす。Windows e2e に `test_setfilesecurity_roundtrip` (26/26)。owner=group の Explorer 操作は SeRestorePrivilege 依存のため e2e 未カバー (ロジックは実装済) |
| [4][5] Linux POSIX ACL | **✅ 3-3 実装済 (access)** | [PosixAcl](../../src/fuse/src/PosixAcl.cs) コーデック + [mount/FileSystem](../../src/fuse/src/FileSystem.cs) で `system.posix_acl_access` ⇄ st_mode(基本3クラス) + `user.pgfs_acl`(named) + mask 算出。named 無しは ENODATA。Linux e2e に `test_posix_acl_named_user` (35/35)。**`system.posix_acl_default` は現状パススルー** (Linux 内 round-trip のみ、Windows 継承変換は未対応) |
| [10] ドライバ評価 | **Phase 3-4 (要否再評価)** | 3-1〜3-3 で Get/Set とも mode/ACL が往復・enforcement はカーネル (mode + 返却 SD)。名前正規化は保存名で効くため、各 op の独自判定が本当に要るかは要検討。下記「3-4 の再評価」参照 |

> **回帰**: 即時適用 5 項目 ([1][2][3][6][7]) 実装後、Windows e2e **24/24** (属性系含む) / Linux e2e **34/34** + race **4/4** (chmod/chown/fallback 含む) / 監査専用 [audit.sh](../../tests/citus/audit.sh) **12/12** (caller_uname も正規化経由) すべて PASS。
>
> 監査連携: ACL/所有権変更は [監査ログ](audit-log.md) の対象。chmod/chown フックに加え `setacl` op を検討 (Phase)。

### Phase 3-4 (ドライバ評価) の再評価

決定 [10] は「クライアントドライバで POSIX 順評価、Linux もカーネル委譲しない」だったが、3-1〜3-3 を終えた時点で次の状況:

- **読み/書きの往復は完成**: Windows は `Get/SetFileSecurity` で SD ⇄ (mode + 正準 ACL)、Linux は `system.posix_acl_access` ⇄ (mode + 正準 ACL)。`ls -l` / Security タブ / getfacl すべて正準ストアを反映する。
- **enforcement の現状**: Windows はカーネルが返却 SD を見て判定、Linux はカーネルが mode (+ FUSE が要求すれば ACL) で判定。名前正規化 ([1]) は**保存名側**で効くため、所有者一致判定は既に正規化済みの名前で行われる。
- **独自評価が要るのは**: named ACL を **Linux カーネルに enforce させる**には `-o default_permissions` + ACL を効かせるか、ドライバが各 op で判定する必要がある。現状 named ACL は表示・往復はできるが Linux での enforce はカーネル設定依存。

→ **3-4 は「named ACL の enforce をどこまで厳密にやるか」の問題に縮小**した。表示・相互運用が主目的なら現状で実用十分。厳密 enforce が要るワークロードが出たら、(a) Linux mount に ACL を効かせる / (b) ドライバ評価、を改めて設計する。**当面 3-4 は保留 (要件が出てから)** とし、3-1〜3-3 で相互運用の主目的は達成とする。

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
