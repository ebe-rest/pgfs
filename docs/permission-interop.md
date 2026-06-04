# Linux↔Windows interop design for permissions, ownership, and ACLs

> Japanese: [permission-interop.ja.md](permission-interop.ja.md)

PGFS is **not an authentication system; it is storage that holds name-based ACLs**. It has no UID/GID/SID/GUID,
no user/group database, and no dependency on LDAP/AD. Authentication is delegated to each OS / PostgreSQL, and
ACL decisions are made by the client driver (mount / assign). The internal model takes **POSIX ACLs as canonical**
and renders the **Windows ACL as a projection view (a lossy projection)** of it.

## Design decisions (10 items)

| # | Item | Decision |
|---|---|---|
| 1 | Name normalization | On store and on match: **strip domain + fullwidth ASCII→halfwidth + lowercase**. **The caller's name is normalized too**, so even on Linux `alice`=`Alice`=`Ａlice` are treated as the same |
| 2 | Domain stripping | Strip both `\` (NetBIOS) and `@domain` (UPN) |
| 3 | Principal mapping | Fully bidirectional. **The DB stores Linux names** (root/nobody/nogroup). root↔Administrator(s), other↔Everyone, **nobody/nogroup↔ANONYMOUS LOGON** |
| 4 | Windows ACL | A **projection view** (no opaque verbatim storage / no full bidirectional round-trip) |
| 5 | ACL model | POSIX-only / allow only. `acl[]={principal_type,principal_name,rights}`, with `mode + acl[]` side by side |
| 6 | Windows-attr xattr | JSON `{hidden,system,archive}` in `user.win.attrs` (user namespace). `compressed` is future work |
| 7 | ReadOnly | ReadOnly if the accessor itself cannot write (owner/group/other writability evaluated against the normalized caller) |
| 8 | owner=group | If a group arrives as owner, owner=nobody / group=that group |
| 9 | Anonymous access | Documented policy only; no code change (pgfs has no anonymous connection path) |
| 10 | ACL decision | POSIX-order evaluation in the client driver (Linux too does not defer to the kernel; the pgfs layer decides, normalization included) |

## What is stored / what is not

**Stored**: `owner_name` (= `pgfs_inode.uname`), `group_name` (= `gname`), `mode` (= `st_mode`), `acl[]`, `xattr[]`.
**Not kept**: UID / GID / SID / GUID (each OS resolves them from the name at runtime; nothing is persisted in the DB).

## Name normalization (★ the core)

owner / group / principal names are normalized **both on store and on match** in the following order. **The same
normalization is applied to the caller's (accessor's) name too**, so the pgfs layer overrides Linux's native
case-sensitivity and both OSes treat case and width identically. Implemented in
[NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs).

1. **Strip domain**: `DOMAIN\name` (NetBIOS) and `name@domain` (UPN) → `name`
2. **Fullwidth ASCII → halfwidth**: U+FF01–FF5E to U+0021–007E (e.g. `Ａlice` → `Alice`)
3. **Lowercase**

```
DOMAIN\Alice  /  ALICE  /  alice@example.com  /  Ａlice   →   alice
```

> Note: Linux can treat user names as case-sensitive (names with uppercase are possible in theory), but this design
> treats `Alice` and `alice` as the same via lowercasing. Since Linux account names that contain uppercase are
> folded together, **lowercase naming is assumed operationally**.

## Principal mapping (well-known, bidirectional)

**The DB stores Linux names.** Each driver converts both ways: "store (each OS name → Linux name + normalize)" and
"render/resolve (Linux name → each OS name)".

| PGFS (stored) | Linux | Windows |
|---|---|---|
| `root` (user) | `root` | `Administrator` |
| `root` (group) | `root` | `Administrators` |
| `nobody` (user) | `nobody` | `ANONYMOUS LOGON` (S-1-5-7) |
| `nogroup` (group) | `nogroup` | `ANONYMOUS LOGON` (reused, since there is no dedicated group SID) |
| `other` (class) | the other:: class | `Everyone` |

- **Unknown names**: even if name resolution fails, the **ACL itself is not rewritten**. It is stored under the
  original name and resolved to `nobody` / `nogroup` (rendered as `ANONYMOUS LOGON` on Windows) **only at evaluation time**.
- The root↔Administrator(s) display decision lives in [`IsWritable`](../src/assign/src/FileSystemUtils.cs); it is
  **extended to the store direction too**, and nobody/nogroup/other follow the table.

## ACL model (POSIX-only / allow only)

```
File
 ├─ owner_name        (normalized)
 ├─ group_name        (normalized)
 ├─ mode              (the owner/group/other 3 base classes = canonical)
 ├─ acl[]             (named user/group + mask)
 └─ xattr[]

ACL Entry
 ├─ principal_type : user | group
 ├─ principal_name : (normalized)
 └─ rights         : r / w / x
```

- **mode** is the canonical source for the 3 base classes (owner/group/other). `chmod` / a simple SetSecurity writes here.
- **acl[]** holds named user/group entries and the **mask** (= recomputed each time as the union of named ∪ group).
- **allow only**. Windows **deny ACEs are not adopted** (dropped in projection; see §Windows is a projection view).
- **Physical storage**: held as JSON in the inode xattr `user.pgfs_acl` (`entries[]`, plus `default[]` for directory
  inheritance). No schema change. The canonical model is [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs).

## ACL evaluation (client driver / POSIX order)

```
owner → named user → group / named group → other
```

- **Windows ACE-order evaluation is not adopted** (decided in POSIX-conformant order).
- **Linux too does not hand everything to the kernel `default_permissions`.** The pgfs driver decides on its own:
  caller uid → name resolution → §Name normalization → entry match (the kernel's numeric uid comparison would not
  honor §Name normalization's case/width folding; consistent with decisions [1] and [10]).

## Owner (routing when a group arrives as owner)

When a group SID is specified as the owner on Windows (e.g. `Developers`):

```
Windows: Owner = Developers (group SID)
   ↓
PGFS:    owner_name = nobody     group_name = developers
```

The owner is always treated as a "user"; when a group arrives, **owner becomes nobody** and the group name is routed
to the **group field** (putting it naively into owner_name would fold it to nobody under Linux owner resolution
(getpwnam) and lose the group information). The group-SID test is
[WindowsUserResolver.IsGroupSid](../src/assign/src/WindowsUserResolver.cs) (= LookupAccountSid).

## Windows attributes

- **Only ReadOnly is linked to mode**: if the accessor itself cannot write (owner/group/other writability evaluated
  against the §normalized caller), it is ReadOnly (following the idea of
  [`IsWritable`](../src/assign/src/FileSystemUtils.cs)).
- **Hidden / System / Archive**: stored as **JSON** in the xattr `user.win.attrs` (**user namespace = visible from
  Linux `getfattr` too**).

  ```json
  { "hidden": true, "system": false, "archive": true }
  ```

- **compressed**: future work (currently unsupported; not included in the key).
- **Linux side**: essentially no DOS attributes. Hidden is a heuristic fallback from a dotfile (`.name`); System/Archive
  are ignored.

## Anonymous access

- **Policy**: anonymous / guest / null session are unsupported (treated as an authentication failure).
- pgfs has no anonymous connection path in the first place (DB authentication + an OS mount are required), so there is
  no code change.
- `nobody` / `nogroup` are the **evaluation result for an unresolved principal**, not a grant of anonymous access.

## Windows is a projection view

POSIX ACLs are canonical; the Windows ACL is a **projection** of them. **deny ACEs / ACE ordering / inheritance flags
are not adopted**, so Windows-specific ACL structure is dropped in projection (it is not restored even when returned
to the original OS).

- **Upside**: a much simpler implementation (no verbatim store/restore, no deny, no ACE-ordering preservation).
- **Trade-off**: an exact Windows ⇄ Windows ACL match is **not guaranteed** (a complex DACL is reduced into the POSIX model).

## Data model (physical)

```
pgfs_inode
 ├─ uname   TEXT     ← owner_name (normalized / Linux name)
 ├─ gname   TEXT     ← group_name (normalized / Linux name)
 ├─ st_mode INTEGER  ← mode (the 3 base classes = canonical)
 └─ xattr_names TEXT[] / xattr_values BYTEA[]  (parallel-array KVS, values are bytea; xattr-bytea.md)
      ├─ user.pgfs_acl   : { "v":1, "entries":[{principal_type,principal_name,rights}...], "default":[...] } (JSON byte string)
      └─ user.win.attrs  : { "hidden":bool, "system":bool, "archive":bool } (JSON byte string)
```

## Implementation status

| Decision | Where it is implemented |
|---|---|
| [1] Name normalization | The shared [NameNormalizer](../src/lib/src/Utility/NameNormalizer.cs) (fullwidth→halfwidth + domain stripping + lowercase). Inserted into store/resolve/caller paths of [UserResolver](../src/mount/src/UserResolver.cs) / [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) / [mount/FileSystem](../src/mount/src/FileSystem.cs) |
| [2] Domain/UPN stripping | `NameNormalizer.StripDomain` handles both `\` and `@` |
| [3] Principal mapping | The well-known aliases in [WindowsUserResolver](../src/assign/src/WindowsUserResolver.cs) (MapWinUserToPgfs/MapWinGroupToPgfs/MapPgfsUserToWin/MapPgfsGroupToWin). nobody/nogroup ↔ `NT AUTHORITY\ANONYMOUS LOGON` |
| [6] Windows-attr xattr | JSON `{hidden,system,archive}` in `user.win.attrs`. [FileSystemUtils](../src/assign/src/FileSystemUtils.cs) Load/SaveWinAttrs |
| [7] ReadOnly | [`IsWritable`](../src/assign/src/FileSystemUtils.cs) simplified to a normalized-name comparison (the root↔Administrator alias is absorbed by the mapping) |
| [9] Anonymous policy | Documented here (no code change) |
| [5] Canonical ACL model | [PgfsAcl](../src/lib/src/Models/PgfsAcl.cs) (Lib): the `user.pgfs_acl` JSON (entries[]/default[]). allow only |
| [4] Projection view (Windows read) | [FileSystemUtils.BuildSecurity](../src/assign/src/FileSystemUtils.cs) + [GetFileSecurity](../src/assign/src/FileSystem.cs). Projects owner/group SIDs + mode-derived ACEs + named ACL onto the SD |
| [4] Projection (Windows write) / [8] owner=group | [SetFileSecurity](../src/assign/src/FileSystem.cs) + [ApplySecurity](../src/assign/src/FileSystemUtils.cs): SD → mode (3 base classes) + acl[] (named) + owner/group. If the owner is a group SID, route to nobody/that group. deny is dropped in projection |
| [4][5] Linux POSIX ACL | The [PosixAcl](../src/lib/src/Models/PosixAcl.cs) codec + [mount/FileSystem](../src/mount/src/FileSystem.cs): `system.posix_acl_access` ⇄ st_mode (3 base classes) + `user.pgfs_acl` (named) + computed mask. No named entries returns ENODATA |

Regression: Windows e2e **26/26** (attributes + ACL projection/reverse-projection), Linux e2e **35/35** (incl. the POSIX
ACL named user), race **4/4**, and the dedicated [audit.sh](../tests/citus/audit.sh) **12/12** (caller_uname also goes
through normalization) all PASS.

ACL/ownership changes are subject to the [audit log](audit-log.md) (the chmod/chown hooks).

### On named-ACL enforcement

Both Get and Set round-trip mode + the canonical ACL, and `ls -l` / the Windows Security tab / `getfacl` all reflect the
canonical store. The current enforcement situation:

- **Windows**: the kernel decides based on the returned SD.
- **Linux**: the kernel decides based on mode (+ the ACL if FUSE asks). Because name normalization ([1]) takes effect on
  the **stored-name side**, owner-match decisions are made with normalized names.
- To make the Linux kernel **strictly enforce** named ACLs, one would need `-o default_permissions` + an enabled ACL, or
  the driver to decide on each op. Today named ACLs can be displayed and round-tripped, but Linux enforcement depends on
  the kernel configuration.

→ For a goal of display and interop, the current state is practically sufficient. If a workload that needs strict
enforcement appears, design either (a) enabling ACLs on the Linux mount or (b) driver-side evaluation at that point.
The Windows-inheritance translation of `system.posix_acl_default` and automated cross-OS round-trip tests are likewise
addressed **once requirements arrive** (today `system.posix_acl_default` is a pass-through with a Linux-internal round-trip only).

## Worked example: setting the owner to "Users" on Windows

```
Windows: Owner = Users (the BUILTIN\Users group)
   ↓ [8] owner=group routing + [1] normalization (strip domain + fullwidth/halfwidth + lowercase)
PGFS:    owner_name = "nobody"    group_name = "users"
   ↓ [3] mapping / [1] the caller is normalized too
Linux:   owner = nobody / group = users (resolved with getgrnam)
Windows: owner = the projection of ANONYMOUS LOGON / group = Users
```

→ With owner=group routing plus normalization, the group `users` is preserved and case is folded.

## Operational assumption (the name-based premise, made explicit)

- pgfs holds principals as **normalized name strings**. For Linux and Windows to point at "the same person/group", both
  machines must be able to **resolve the same name (lowercase, no domain)** (via AD/LDAP/manual sync).
- If a name cannot be resolved, the ACL entry is preserved but becomes **nobody/nogroup at evaluation time** and is not enforced.

## Related

- [docs/Assign.md](Assign.md) / [docs/Mount.md](Mount.md) — the per-OS operation lists
- [docs/audit-log.md](audit-log.md) — auditing of ACL/ownership changes
- [docs/settings-matrix.md](settings-matrix.md) — fallback_uname/gname and so on
- [docs/permission-interop-diagram.html](permission-interop-diagram.html) — the diagram for this design (HTML/SVG)
- [docs/next.md](next.md) — relation to #3 (xattr byte-stream passthrough) / #4 (this design)
