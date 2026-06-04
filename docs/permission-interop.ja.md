# 権限・所有権・ACL の Linux↔Windows 相互運用 設計

> 英語版: [permission-interop.md](permission-interop.md)

PGFS は **認証システムではなく、名前ベース ACL を保持するストレージ**である。UID/GID/SID/GUID を持たず、
ユーザー/グループ DB も持たず、LDAP/AD にも依存しない。認証は各 OS / PostgreSQL に委譲し、ACL の判定は
クライアントドライバ (mount / assign) が行う。内部モデルは **POSIX ACL を正準**とし、**Windows ACL はその投影
ビュー (lossy projection)** として描画する。

## 設計判断 (10 項目)

| # | 項目 | 決定 |
|---|---|---|
| 1 | 名前正規化 | 保存時・照合時に **ドメイン除去 + 全角ASCII→半角 + 小文字化**。**呼び出し元名も正規化** → Linux でも `alice`=`Alice`=`Ａlice` を同一視 |
| 2 | ドメイン除去 | `\`(NetBIOS)と `@domain`(UPN)を両方除去 |
| 3 | principal マッピング | 双方向フル。**DB は Linux 名で保存** (root/nobody/nogroup)。root↔Administrator(s)、other↔Everyone、**nobody/nogroup↔ANONYMOUS LOGON** |
| 4 | Windows ACL | **投影ビュー** (opaque/双方向往復は採らない) |
| 5 | ACL モデル | POSIX-only / allow のみ。`acl[]={principal_type,principal_name,rights}`、`mode + acl[]` 併存 |
| 6 | Win属性 xattr | `user.win.attrs` (user 名前空間) に JSON `{hidden,system,archive}`。`compressed` は将来 |
| 7 | ReadOnly | アクセス自身が書込不可なら ReadOnly (owner/group/other writable を正規化済み呼出元で評価) |
| 8 | owner=group | owner にグループが来たら owner=nobody / group=該当 |
| 9 | 匿名アクセス | ポリシー明記のみ・コード変更なし (pgfs は匿名接続経路を持たない) |
| 10 | ACL 判定 | クライアントドライバで POSIX 順評価 (Linux もカーネル委譲せず pgfs 層で正規化込み判定) |

## 保存するもの / 保持しないもの

**保存**: `owner_name` (= `pgfs_inode.uname`)、`group_name` (= `gname`)、`mode` (= `st_mode`)、`acl[]`、`xattr[]`。
**保持しない**: UID / GID / SID / GUID (実行時に各 OS が名前から解決するだけ。DB には一切残さない)。

## 名前正規化 (★ 中核)

owner / group / principal 名は、**保存時も照合時も**次の順で正規化する。**呼び出し元(アクセス主体)の名前にも
同じ正規化を適用**するので、Linux 本来の case-sensitive を pgfs 層で上書きし、両 OS で大小・全半角を同一視する。
実装は [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs)。

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
- root↔Administrator(s) の表示判定は [`IsWritable`](../src/assign/src/FileSystemUtils.cs) にあり、**保存方向にも拡張**して
  nobody/nogroup/other も表に従う。

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
  スキーマ追加なし。正準モデルは [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs)。

## ACL 評価 (クライアントドライバ / POSIX 順)

```
owner → named user → group / named group → other
```

- **Windows ACE 順評価は採らない** (POSIX 準拠順で判定)。
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
group SID 判定は [WindowsUserResolver.IsGroupSid](../src/assign/src/WindowsUserResolver.cs) (= LookupAccountSid)。

## Windows 属性

- **ReadOnly のみ mode と連携**: アクセスする自身が書き込み不可 (owner/group/other の writable 判定を
  §名前正規化済みの呼び出し元に対して評価) なら ReadOnly とする ([`IsWritable`](../src/assign/src/FileSystemUtils.cs) の考え方を踏襲)。
- **Hidden / System / Archive**: xattr `user.win.attrs` (**user 名前空間 = Linux の `getfattr` からも可視**) に
  **JSON** で保存。

  ```json
  { "hidden": true, "system": false, "archive": true }
  ```

- **compressed**: 今後の課題 (現時点は非対応。キーに含めない)。
- **Linux 側**: DOS 属性は基本なし。Hidden は dotfile (`.name`) からの推定 fallback、System/Archive は無視。

## 匿名アクセス

- **ポリシー**: anonymous / guest / null session は非サポート (認証失敗扱い)。
- pgfs は元々**匿名接続経路を持たない** (DB 認証 + OS マウントが必須) ため、コード変更はなし。
- `nobody` / `nogroup` は「**未解決 principal の評価結果**」であって、匿名接続の許可ではない。

## Windows は投影ビュー

POSIX ACL を正準とし、Windows ACL はその **射影 (projection)**。**deny ACE / ACE 順 / 継承フラグは採らない**ので、
Windows 固有の ACL 構造は投影で落ちる (原 OS に戻しても復元しない)。

- **メリット**: 実装が大幅に簡素 (verbatim 保存・復元、deny、ACE 順の保持が不要)。
- **トレードオフ**: Windows ⇄ Windows での ACL **完全一致は保証しない** (複雑な DACL は POSIX モデルへ縮約される)。

## データモデル (物理)

```
pgfs_inode
 ├─ uname   TEXT     ← owner_name (正規化済み / Linux 名)
 ├─ gname   TEXT     ← group_name (正規化済み / Linux 名)
 ├─ st_mode INTEGER  ← mode (基本3クラス = 正準)
 └─ xattr_names TEXT[] / xattr_values BYTEA[]  (並行配列 KVS、値は bytea。xattr-bytea.md)
      ├─ user.pgfs_acl   : { "v":1, "entries":[{principal_type,principal_name,rights}...], "default":[...] } (JSON バイト列)
      └─ user.win.attrs  : { "hidden":bool, "system":bool, "archive":bool } (JSON バイト列)
```

## 実装状況

| 決定 | 実装箇所 |
|---|---|
| [1] 名前正規化 | 共通 [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs) (全半角→半角 + domain 除去 + 小文字)。[UserResolver](../src/mount/src/UserResolver.cs) / [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) / [mount/FileSystem](../src/mount/src/FileSystem.cs) の保存・解決・呼び出し元に挿入 |
| [2] ドメイン/UPN 除去 | `NameNormalizer.StripDomain` が `\` と `@` 両対応 |
| [3] principal マッピング | [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) の well-known alias (MapWinUserToPgfs/MapWinGroupToPgfs/MapPgfsUserToWin/MapPgfsGroupToWin)。nobody/nogroup ↔ `NT AUTHORITY\ANONYMOUS LOGON` |
| [6] Win属性 xattr | `user.win.attrs` に JSON `{hidden,system,archive}`。[FileSystemUtils](../src/assign/src/FileSystemUtils.cs) の Load/SaveWinAttrs |
| [7] ReadOnly | [`IsWritable`](../src/assign/src/FileSystemUtils.cs) を正規化済み名比較に簡素化 (root↔Administrator エイリアスはマッピングで吸収) |
| [9] 匿名ポリシー | 本書に明記 (コード変更なし) |
| [5] ACL 正準モデル | [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs) (Lib): `user.pgfs_acl` JSON (entries[]/default[])。allow のみ |
| [4] 投影ビュー (Windows 読み) | [FileSystemUtils.BuildSecurity](../src/assign/src/FileSystemUtils.cs) + [GetFileSecurity](../src/assign/src/FileSystem.cs)。owner/group SID + mode 由来 ACE + named ACL を SD に投影 |
| [4] 投影 (Windows 書き) / [8] owner=group | [SetFileSecurity](../src/assign/src/FileSystem.cs) + [ApplySecurity](../src/assign/src/FileSystemUtils.cs): SD → mode(基本3クラス) + acl[](named) + owner/group。owner が group SID なら nobody/該当へ。deny は投影で落とす |
| [4][5] Linux POSIX ACL | [PosixAcl](../src/lib/src/Models/PosixAcl.cs) コーデック + [mount/FileSystem](../src/mount/src/FileSystem.cs) で `system.posix_acl_access` ⇄ st_mode(基本3クラス) + `user.pgfs_acl`(named) + mask 算出。named 無しは ENODATA |

回帰: Windows e2e **26/26** (属性 + ACL 投影/逆投影含む) / Linux e2e **35/35** (POSIX ACL named user 含む) / race **4/4** / 監査専用 [audit.sh](../tests/citus/audit.sh) **12/12** (caller_uname も正規化経由) すべて PASS。

ACL/所有権変更は [監査ログ](audit-log.md) の対象 (chmod/chown フック)。

### named ACL の enforcement について

Get/Set とも mode + 正準 ACL が往復し、`ls -l` / Windows の Security タブ / `getfacl` すべて正準ストアを反映する。
enforcement の現状は次のとおり:

- **Windows**: カーネルが返却 SD を見て判定する。
- **Linux**: カーネルが mode (+ FUSE が要求すれば ACL) で判定する。名前正規化 ([1]) は**保存名側**で効くため、
  所有者一致判定は正規化済みの名前で行われる。
- named ACL を **Linux カーネルに厳密 enforce** させるには `-o default_permissions` + ACL を効かせるか、ドライバが
  各 op で判定する必要がある。現状 named ACL は表示・往復はできるが、Linux での enforce はカーネル設定依存。

→ 表示・相互運用が主目的なら現状で実用十分。厳密 enforce が要るワークロードが出たら、(a) Linux mount に ACL を
効かせる / (b) ドライバ評価、を改めて設計する。`system.posix_acl_default` の Windows 継承変換、cross-OS 往復の
自動テストも同様に **要件が出てから** 対応する (現状 `system.posix_acl_default` は Linux 内 round-trip のみのパススルー)。

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

## 関連

- [docs/Assign.md](Assign.md) / [docs/Mount.md](Mount.md) — 各 OS のオペレーション一覧
- [docs/audit-log.md](audit-log.md) — ACL/所有権変更の監査
- [docs/settings-matrix.md](settings-matrix.md) — fallback_uname/gname 等
- [docs/permission-interop-diagram.html](permission-interop-diagram.html) — 本設計の図 (HTML/SVG)
- [docs/next.md](next.md) — #3 (xattr バイト列透過) / #4 (本設計) との関係
