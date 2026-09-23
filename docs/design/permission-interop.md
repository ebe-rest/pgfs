# The Linux-Windows interoperability of the permissions, the ownership and the ACLs (the design)

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the internal model and the interoperability design
> decisions of the permissions, the ownership and the ACLs** - the decision to make the POSIX ACL canonical and
> Windows a projected view of it, the name normalization and the principal mapping, the order in which an ACL is
> evaluated, how the owner and the group of a new file are decided, and the operating premise of "holding them
> by name". **How it should look on which operating system** is authoritative here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [permission-interop-diagram.html](permission-interop-diagram.html) | **The diagram** of this design (HTML/SVG) |
> | [../Mount.md](../Mount.md) | The current CLI contract and the operation list of **the Linux side** |
> | [../Assign.md](../Assign.md) | The current CLI contract and the operation list of **the Windows side** |
> | [windows-parity.md](windows-parity.md) | **The as-built and the staging of what is unimplemented** for taking it to Windows |
> | [audit-log.md](audit-log.md) | **The audit rows for changes** to the ACLs and the ownership |
> | [settings-matrix.md](settings-matrix.md) | **The settings** such as `mount.fallback_uname` / `fallback_gname` |
> | [xattr-bytea.md](xattr-bytea.md) | **The byte-string transparency of the xattr** that carries the ACL |
> | [../next.md](../next.md) | **The priority** of what remains (the punch list) |

> **The status**: the design is agreed (**revised** the same day). **The implementation is complete
> as far as the main purpose** - the 5 immediate items plus the ACL proper 3-0 to 3-3 are implemented (the
> regressions were Windows 26/26 and Linux 35/35; the progress is in the "the agreements" table below).
> **Only the strict enforcement of a named ACL (3-4) is held back, awaiting a requirement.** This document is the
> source of truth for the design decisions, and **for the implementation details see
> [Mount.md](../Mount.md) / [Assign.md](../Assign.md) /
> [FileSystemUtils.cs](../../src/dokan/src/FileSystemUtils.cs)**.
> **What was revised**: the old version's "a full bidirectional round trip plus keeping the Windows-specific ACL
> verbatim in an `opaque`" was withdrawn and changed to **POSIX canonical plus Windows as a projected view
> (a lossy projection)**. The name normalization and the principal mapping table were settled at the same time.

PGFS is **not an authentication system but storage that holds name-based ACLs**. It holds no UID, GID, SID or
GUID, holds no user or group database, and depends on neither LDAP nor AD. Authentication is delegated to each
operating system and to PostgreSQL, and the ACL decision is made by the client driver (mount / assign). The
internal model is canonically a **POSIX ACL**, and **a Windows ACL is drawn as a projected view** of it.

> **The static comparison of 2026-09-19**: the agreements below include design goals. Linux's `Access` is
> unimplemented, and the strict enforcement of a named ACL and the inheritance of a default ACL at creation time
> are not complete. On Windows there is a path where the creator uses the process's default name, so a
> successful ACL projection is not to be equated with per-requestor access control. The real-hardware behaviour
> was not verified this time. **Not implementing `Access` is deliberate design** - the access decision is
> delegated to the kernel through the `default_permissions` given at mount time.
> **[CHANGELOG.md, the known limitations](../../CHANGELOG.md) is the source of truth for the limitations that
> remain**, and for the proposals see [the Windows design](windows-parity.md).

## The agreements (10 items)

| # | The item | The decision |
|---|---|---|
| 1 | The name normalization | **Strip the domain, convert full-width ASCII to half-width and lowercase**, both when storing and when comparing. **The caller's name is normalized too** -> even on Linux, `alice` = `Alice` = `Ａlice` |
| 2 | Stripping the domain | Both `\` (NetBIOS) and `@domain` (UPN) are stripped |
| 3 | The principal mapping | Fully bidirectional. **The database stores the Linux names** (root/nobody/nogroup). root <-> Administrator(s), other <-> Everyone, **nobody/nogroup <-> ANONYMOUS LOGON** |
| 4 | The Windows ACL | **Demoted to a projected view** (the opaque and the bidirectional round trip are withdrawn) |
| 5 | The ACL model | POSIX-only, allow only. `acl[]={principal_type,principal_name,rights}`, with `mode` and `acl[]` coexisting |
| 6 | The Windows attributes xattr | JSON `{hidden,system,archive}` in `user.win.attrs` (the user namespace). `compressed` is for the future |
| 7 | ReadOnly | If the accessor itself cannot write, it is ReadOnly (the owner/group/other writability is evaluated against the normalized caller) |
| 8 | owner = a group | owner = nobody and group = the one in question |
| 9 | Anonymous access | The policy is only stated; no code change (pgfs has no anonymous connection path) |
| 10 | The ACL decision | Evaluated in POSIX order in the client driver (Linux too decides in the pgfs layer, including the normalization, rather than delegating to the kernel) |

## What is stored and what is not

**Stored**: `owner_name` (= `pgfs_inode.uname`), `group_name` (= `gname`), `mode` (= `st_mode`), `acl[]` and
`xattr[]`.
**Not held**: the UID / GID / SID / GUID (each operating system merely resolves them from the names at runtime;
nothing is left in the database).

## How the owner and the group of a new file are decided (it differs by operating system)

**Even on the same FS, "how the owner and the group of a newly created file are decided" differs per operating
system.** The stored form (names only) is common, but how they are decided matches each operating system's
concepts.

| | Linux (mount.pgfs) | Windows (assign.pgfs) |
|---|---|---|
| The owner (`uname`) | `fuse_get_context()->uid` resolved through `UserResolver.UnameOf` | The requestor's `WindowsIdentity.User` (a SID) resolved through `WindowsUserResolver.UnameOf` |
| The group (`gname`) | The caller's **gid** (= effectively the primary group) | **Inherited from the parent directory's `gname`** |
| When it cannot be resolved | `mount.fallback_uname` / `fallback_gname` (`nobody` / `nogroup` by default) | The same (**it never masquerades as the mount process's user**) |

**Why Windows inherits the group**: a Windows token's primary group is in practice almost always
`Domain Users` or `None`, and it is not used in the access decision either. Storing it as it is fills the
`gname` with a noise value and makes the group bits of the `mode` meaningless. Inheriting from the parent gives
"the same tree is the same group", which is a meaningful value from Linux too.
-> **A file created on Linux and a file created on Windows therefore get their group from different places**, so
the `gname` can be mixed within one directory (both are normalized names, so reading and writing work either
way).
The background of the design is in
[windows-parity.md, how the owner and the group are decided](windows-parity.md).

**⚠ The domain is flattened**: the normalization drops the domain, so **`CORP\alice` and `LOCAL\alice` become
the same `alice`**. That was already the case for the read projection, but since Windows's
**write side (deciding the owner)** also became requestor-derived,
**same-named users from different domains are stored as the same owner**. Verification in a real domain
environment has not been done.

## The name normalization (★ the core)

An owner / group / principal name is normalized in the following order, **both when stored and when compared**.
**The same normalization is applied to the caller's (the accessing subject's) name too**, so Linux's native
case sensitivity is overridden in the pgfs layer and case and width are treated the same on both operating
systems.

1. **Strip the domain**: `DOMAIN\name` (NetBIOS) and `name@domain` (UPN) -> `name`
2. **Full-width ASCII -> half-width**: U+FF01-FF5E to U+0021-007E (for example `Ａlice` -> `Alice`)
3. **Lowercase**

```
DOMAIN\Alice  /  ALICE  /  alice@example.com  /  Ａlice   ->   alice
```

> A caution: Linux can treat user names case-sensitively (a name containing uppercase is theoretically
> possible), but this design treats `Alice` and `alice` as the same through lowercasing. A Linux account name
> containing uppercase is treated as the same, so **lowercase naming is an operating premise**.

## The principal mapping (well-known, bidirectional)

**The database stores the Linux names.** Each driver converts in both directions: "storing" (each OS's name ->
the Linux name plus the normalization) and "drawing/resolving" (the Linux name -> each OS's name).

| PGFS (stored) | Linux | Windows |
|---|---|---|
| `root` (user) | `root` | `Administrator` |
| `root` (group) | `root` | `Administrators` |
| `nobody` (user) | `nobody` | `ANONYMOUS LOGON` (S-1-5-7) |
| `nogroup` (group) | `nogroup` | `ANONYMOUS LOGON` (the same is reused because there is no dedicated group SID) |
| `other` (class) | The other:: class | `Everyone` |

- **How an unknown name is treated**: even when the name resolution fails, **the ACL itself is not rewritten**.
  It is stored under its original name, and **only at evaluation time** is it resolved to `nobody` / `nogroup`
  (`ANONYMOUS LOGON` in the Windows drawing).
- root <-> Administrator(s) existed only in the display decision of the existing
  [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs), so it is **extended to the storing direction** too,
  and nobody/nogroup/other follow the table.

## The ACL model (POSIX-only, allow only)

```
File
 ├─ owner_name        (normalized)
 ├─ group_name        (normalized)
 ├─ mode              (the three basic classes owner/group/other = canonical)
 ├─ acl[]             (named users and groups plus the mask)
 └─ xattr[]

ACL Entry
 ├─ principal_type : user | group
 ├─ principal_name : (normalized)
 └─ rights         : r / w / x
```

- **mode** is the canonical form of the three basic classes (owner/group/other). A `chmod` or a simple
  SetSecurity writes here.
- **acl[]** holds the named user and group entries plus the **mask** (= the union of the named entries and the
  group, recomputed each time).
- **Allow only.** Windows's **deny ACEs are not taken** (they are dropped in the projection; see the Windows
  projected view section).
- **The physical storage**: it is held as JSON in the inode's `user.pgfs_acl` xattr (the `entries[]` plus the
  `default[]` for a directory's inheritance). No schema addition. The `opaque` is gone (removed from the old
  version).

## Evaluating the ACL (in the client driver, in POSIX order)

```
owner -> a named user -> the group / a named group -> other
```

- **Windows's ACE-order evaluation is not taken** (it is decided in POSIX-conformant order).
- **Linux does not hand it wholesale to the kernel's `default_permissions` either.** The pgfs driver decides for
  itself in the order: the caller's uid -> the name resolution -> the name normalization -> matching the
  entries (because a numeric uid comparison in the kernel does not get the case and width equivalence of the
  name normalization; consistent with decisions [1] and [10]).

## The owner (routing when a group arrives as the owner)

When a group SID is given as the owner on Windows (for example `Developers`):

```
Windows: Owner = Developers (a group SID)
   ↓
PGFS:    owner_name = nobody     group_name = developers
```

The owner is always treated as meaning "a user", and when a group arrives, **the owner becomes nobody** and the
group name is routed into the **group field** (putting it naively into owner_name would turn into nobody at
Linux's owner resolution (getpwnam) and the group information would be lost).

## The Windows attributes

- **Only ReadOnly links to the mode**: if the accessor itself cannot write (the owner/group/other writability
  evaluated against the normalized caller), it is ReadOnly (following the thinking of the current
  [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs)).
- **Hidden / System / Archive**: stored as **JSON** in the xattr `user.win.attrs`
  (**the user namespace = visible from Linux's `getfattr` too**).

  ```json
  { "hidden": true, "system": false, "archive": true }
  ```

- **compressed**: a future matter (unsupported at present; it is not in the key).
- **On the Linux side**: there are basically no DOS attributes. Hidden falls back to being inferred from a
  dotfile (`.name`), and System and Archive are ignored.

## Anonymous access

- **The policy**: anonymous, guest and null sessions are unsupported (treated as an authentication failure).
- pgfs **has no anonymous connection path** to begin with (database authentication plus an OS mount are
  mandatory), so there is no code change at present.
- `nobody` / `nogroup` are **the evaluation result of an unresolved principal**, not a permission for an
  anonymous connection.

## Windows is a projected view (the old opaque is withdrawn)

The POSIX ACL is canonical and a Windows ACL is its **projection**. **Deny ACEs, the ACE order and the
inheritance flags are not taken**, so the Windows-specific ACL structure is dropped in the projection (it is not
restored even going back to the original operating system).

- **The advantage**: the implementation is far simpler (no saving and restoring an `opaque`, no deny, no
  preserving the ACE order).
- **The trade-off**: a **complete match is not guaranteed** for a Windows-to-Windows ACL (a complex DACL is
  reduced into the POSIX model).

## The data model (physical)

```
pgfs_inode
 ├─ uname   TEXT     ← owner_name (normalized / the Linux name)
 ├─ gname   TEXT     ← group_name (normalized / the Linux name)
 ├─ st_mode INTEGER  ← mode (the three basic classes = canonical)
 └─ xattr_names TEXT[] / xattr_values BYTEA[]  (a parallel-array KVS whose values are bytea; see xattr-bytea.md)
      ├─ user.pgfs_acl   : { "v":1, "entries":[{principal_type,principal_name,rights}...], "default":[...] } (a JSON byte string)
      └─ user.win.attrs  : { "hidden":bool, "system":bool, "archive":bool } (a JSON byte string)
```

## Whether each decision is applied (immediately or in a phase)

| The decision | The status | The main places changed |
|---|---|---|
| [1] The name normalization | **✅ Implemented** | The shared [NameNormalizer](../../src/core/src/Utility/NameNormalizer.cs) was created (full-width to half-width plus stripping the domain plus lowercasing). It was inserted into the storing, the resolving and the caller of [UserResolver](../../src/fuse/src/UserResolver.cs) / [WindowsUserResolver](../../src/dokan/src/WindowsUserResolver.cs) / [the FUSE FileSystem](../../src/fuse/src/FileSystem.cs) |
| [2] Stripping the domain / UPN | **✅ Implemented** | `NameNormalizer.StripDomain` handles both `\` and `@`. The old `StripDomain` on the Windows side was removed |
| [3] The principal mapping | **✅ Implemented** | The well-known aliases went into [WindowsUserResolver](../../src/dokan/src/WindowsUserResolver.cs) (MapWinUserToPgfs/MapWinGroupToPgfs/MapPgfsUserToWin/MapPgfsGroupToWin). nobody/nogroup <-> `NT AUTHORITY\ANONYMOUS LOGON` |
| [6] The Windows attributes xattr | **✅ Implemented** | `user.win_attrs` (4 bytes) became `user.win.attrs` (the JSON `{hidden,system,archive}`). The Load/SaveWinAttrs of [FileSystemUtils](../../src/dokan/src/FileSystemUtils.cs) |
| [7] ReadOnly | **✅ Implemented** | [`IsWritable`](../../src/dokan/src/FileSystemUtils.cs) was simplified to a comparison of normalized names (the root <-> Administrator alias is absorbed by the mapping) |
| [9] The anonymous policy | **✅ Stated only** | Stated in this document (no code change) |
| [5] The ACL model (the canonical document) | **✅ 3-0 implemented** | [PgfsAcl](../../src/core/src/Models/PgfsAcl.cs) (in the library): the `user.pgfs_acl` JSON (entries[]/default[]). Allow only |
| [4] The projected view (the Windows read) | **✅ 3-1 implemented** | [FileSystemUtils.BuildSecurity](../../src/dokan/src/FileSystemUtils.cs) plus [GetFileSecurity](../../src/dokan/src/FileSystem.cs). The owner and group SIDs plus the mode-derived ACEs plus the named ACL are projected into the SD. `test_getfilesecurity_projection` was added to the Windows e2e suite (25/25 PASS) |
| [4] The projection (the Windows write) / [8] owner = a group | **✅ 3-2 implemented** | [SetFileSecurity](../../src/dokan/src/FileSystem.cs) plus [ApplySecurity](../../src/dokan/src/FileSystemUtils.cs): the SD -> the mode (the three basic classes) plus the acl[] (named) plus the owner and group. If the owner is a group SID it goes to nobody plus that group ([IsGroupSid](../../src/dokan/src/WindowsUserResolver.cs) = LookupAccountSid). Deny is dropped in the projection. `test_setfilesecurity_roundtrip` was added to the Windows e2e suite (26/26). An Explorer operation with owner = a group depends on SeRestorePrivilege so it is not covered by the e2e suite (the logic is implemented) |
| [4][5] The Linux POSIX ACL | **✅ 3-3 implemented (access)** | The [PosixAcl](../../src/fuse/src/PosixAcl.cs) codec plus [the FUSE FileSystem](../../src/fuse/src/FileSystem.cs) convert `system.posix_acl_access` <-> the st_mode (the three basic classes) plus the `user.pgfs_acl` (named) plus computing the mask. With no named entries it is ENODATA. `test_posix_acl_named_user` was added to the Linux e2e suite (35/35). **`system.posix_acl_default` is passed through as it is today** (a round trip within Linux only; converting the Windows inheritance is unsupported) |
| [10] The driver's evaluation | **Phase 3-4 (to be re-evaluated)** | With 3-1 to 3-3, the mode and the ACL round-trip through both Get and Set, and the enforcement is the kernel's (the mode plus the returned SD). The name normalization takes effect on the stored names, so whether each op really needs its own decision needs consideration. See "re-evaluating 3-4" below |

> **The regressions**: after implementing the 5 immediately applicable items ([1][2][3][6][7]),
> the Windows e2e suite **24/24** (including the attributes), the Linux e2e suite **34/34** plus the race
> **4/4** (including chmod/chown/fallback) and the audit-dedicated
> [audit.sh](../../tests/citus/audit.sh) **12/12** (with the caller_uname going through the normalization too)
> all PASSed.
>
> The audit integration: ACL and ownership changes are subject to [the audit log](audit-log.md). On top of the
> chmod/chown hooks, a `setacl` op is under consideration (a phase).

### Re-evaluating Phase 3-4 (the driver's evaluation)

Decision [10] was "evaluate in POSIX order in the client driver, without Linux delegating to the kernel", but at
the point 3-1 to 3-3 were finished the situation is:

- **The read and write round trip is complete**: Windows does the SD <-> (the mode plus the canonical ACL)
  through `Get/SetFileSecurity`, and Linux does `system.posix_acl_access` <-> (the mode plus the canonical ACL).
  `ls -l`, the Security tab and getfacl all reflect the canonical store.
- **Where the enforcement stands**: Windows has the kernel decide from the returned SD, and Linux has the kernel
  decide from the mode (plus the ACL if FUSE asks for it). The name normalization ([1]) takes effect on
  **the stored-name side**, so the owner-match decision is already made with normalized names.
- **Where its own evaluation is needed**: **making the Linux kernel enforce** a named ACL requires either
  `-o default_permissions` plus making the ACL take effect, or the driver deciding on each op. Today a named ACL
  can be displayed and round-tripped, but its enforcement on Linux depends on the kernel configuration.

-> **3-4 has shrunk into the question of "how strictly to enforce a named ACL".** If the display and the
interoperability are the main purpose, what there is now is enough in practice. If a workload that needs strict
enforcement appears, (a) making the ACL take effect on the Linux mount or (b) the driver's evaluation is
designed again. **3-4 is held back for now (until a requirement appears)**, and the main purpose of the
interoperability is taken as achieved by 3-1 to 3-3.

## A concrete example: setting the owner to "Users" on Windows

```
Windows: Owner = Users (the BUILTIN\Users group)
   ↓ [8] the owner = a group routing plus [1] the normalization (stripping the domain, the width and the case)
PGFS:    owner_name = "nobody"    group_name = "users"
   ↓ [3] the mapping / [1] the caller is normalized too
Linux:   owner = nobody / group = users (resolved with getgrnam)
Windows: owner = the projection of ANONYMOUS LOGON / group = Users
```

-> With the routing for an owner that is a group plus the normalization, the group `users` is kept and the case
is absorbed.

## The operating premise (stating the name basis)

- pgfs holds a principal as a **normalized name string**. For "the same person or group" to be meant on Linux
  and Windows alike, the premise is that **the same name (lowercase, with no domain) resolves on both machines**
  (through AD/LDAP/manual synchronization).
- When a name cannot be resolved, the ACL entry is kept but **evaluates as nobody/nogroup** and is not enforced.

## The domain drops out of the owner and survives in the audit (sorted out)

**`NameNormalizer` deliberately drops the domain part of `DOMAIN\name` (NetBIOS) and `name@domain` (UPN)**
(decisions [1] and [2] above). It is the normalization that lets a name-based ACL store express "the same
person" through a text match, so this is the specification. **It is not "an unverified defect".**

On top of that, what is lost and what survives was confirmed in the code:

| | The domain |
|---|---|
| **The owner (`{prefix}inode.uname` / `gname`)** | **Dropped.** `CORP\alice` and `LOCAL\alice` **both become `alice`** and **cannot be told apart as different people** |
| **The audit (`{prefix}audit.caller_domain`)** | **Survives.** The Dokan side splits the `DOMAIN\user` in `CreateFile` and puts it in `AuditContext.Domain`, writing it into a separate column of the audit row |

**In other words, the record of "who did it" can be kept, but "whose it is" collides on the name.**

**What goes wrong in a domain environment** (unverified on real hardware - **it cannot be confirmed without such
an environment**):

- **Same-named users** from several domains use the same FS. The owners get mixed up.
- One wants it to look like `CORP\alice` on Windows, but another domain's `alice` looks like the same owner.

**Fixing it** would mean changing the normalization to store `domain\name` as it is, but then
**it would no longer match the uname on the Linux side** (there is no user `CORP\alice` on Linux), so
**"the same person" across operating systems would break**.
**That is exactly the trade-off of decisions [1] and [2]**, so changing it means revisiting the
permission-interop design from the start.
