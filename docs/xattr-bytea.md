# Transparent bytea xattr (parallel-array KVS)

Design for replacing the extended-attribute (xattr) store of `pgfs_inode` from **JSONB + Base64** with
**two parallel arrays (`xattr_names TEXT[]` + `xattr_values BYTEA[]`)**. This document is the source of truth.

For the Japanese version see [xattr-bytea.ja.md](xattr-bytea.ja.md).

Status: **implemented**. Verified on real hardware via the linux_client e2e, including a faithful round-trip
of NUL / high bytes (`test_xattr_binary`).

## Motivation

The JSONB version held `{ name: Base64(value) }` in `pgfs_inode.xattrs JSONB`
([Api.EncodeXattrValue/DecodeXattrValue](../src/lib/src/Api/Api.cs)).

- An xattr value is **an arbitrary byte string at the OS level** (it may contain NUL; the trailing NUL of
  `security.selinux`, and `security.capability` / `system.posix_acl_access` are raw binary). A byte string
  does not fit in a JSON string as-is, so it was wrapped in Base64.
- Base64 decode was a **heuristic** with no header and no key discrimination: try `Convert.FromBase64String`
  and fall back to UTF-8 on failure. In normal operation every value is Base64 so there is no ambiguity, but
  the fallback path carries a latent ambiguity ("plaintext that happens to be valid Base64 is mis-decoded").
- Base64 inflates size by ~33% and the content is unreadable from SQL.

→ Holding the value **faithfully as bytea** removes both the encode round-trip and the discrimination heuristic.

## Chosen and rejected options

**Chosen: A' parallel arrays** — `xattr_names TEXT[]` + `xattr_values BYTEA[]` embedded in the inode row
(the same index is a pair). Reasons:

- No new type (`CREATE TYPE` unnecessary = no Citus type propagation either).
- Npgsql round-trips `text[]↔string[]` / `bytea[]↔byte[][]` **out of the box** (no type registration).
- Stays embedded in the inode row → does not break the "load all xattrs together when loading the inode"
  cache design (which helps the case where SELinux queries frequently). The volume is small, so no index is
  needed; do a linear search on the app side after fetching.

Rejected:
- **HSTORE**: `text => text` only, cannot hold bytea values.
- **composite-type array `xattr_entry[]` (name text, value bytea)**: clean consistency, but the overhead of
  `CREATE TYPE` + Citus type propagation + Npgsql composite-type registration. A' reaches the same goal more lightly.
- **child table `pgfs_xattr`**: the most relational, but loading an inode needs a join/extra query and a +1
  distributed table. Overkill for this scale (small volume, no index).
- **manual pack into a single bytea**: values contain NUL, so `\0` separators break; length-prefix framing is
  mandatory, leading to home-grown parsing hell + invisible from SQL. Rejected.

## Schema diff

Replace `pgfs_inode`'s

```sql
xattrs JSONB NOT NULL DEFAULT '{}'::JSONB
```

with

```sql
xattr_names  TEXT[]  NOT NULL DEFAULT '{}'::TEXT[],
xattr_values BYTEA[] NOT NULL DEFAULT '{}'::BYTEA[]
```

Places to update:
- [docs/ddl/pgfs_inode.sql](ddl/pgfs_inode.sql) (column definition + the `'{}'::JSONB` in the INSERT sample)
- the inode `ColumnInfo("xattrs", "JSONB", ...)` in [Initializer.cs](../src/mkfs/src/Initializer.cs)

**Invariants**: `cardinality(xattr_names) = cardinality(xattr_values)`, and `xattr_names` is unique within
itself. All guaranteed by Api's single-statement UPDATE keeping both arrays in sync (the app never does a
read-modify-write).

Citus: only the column type changes. The inode distribution (PK `(parent_id, id)`, distribution key
`parent_id`) is unchanged, and no type propagation is needed.

## Api rewrite (JSONB operators → array operations)

Atomicity is preserved by a **single UPDATE statement with no subquery**, the same as the JSONB `||`/`-`
(`array_position` and array slices applied directly to the columns; the multi-shard UPDATE on `WHERE id = @id`
follows the same idiom as the existing xattr update). It depends on the RHS being evaluated against the
pre-update row.

```sql
-- GetXAttr (on cache miss): bytea or NULL
SELECT xattr_values[array_position(xattr_names, @name)]
FROM {inode} WHERE id = @id;

-- existence check (for createOnly/replaceOnly)
SELECT array_position(xattr_names, @name) IS NOT NULL FROM {inode} WHERE id = @id;

-- SetXAttr: replace the value at the same index if present, otherwise append (atomic)
UPDATE {inode} SET
  xattr_names = CASE WHEN array_position(xattr_names, @name) IS NULL
                     THEN array_append(xattr_names, @name) ELSE xattr_names END,
  xattr_values = CASE WHEN array_position(xattr_names, @name) IS NULL
                      THEN array_append(xattr_values, @value)
                      ELSE xattr_values[1:array_position(xattr_names, @name)-1]
                           || @value
                           || xattr_values[array_position(xattr_names, @name)+1:] END,
  updated_at = current_timestamp
WHERE id = @id;

-- RemoveXAttr: remove the index of name from both arrays (atomic)
UPDATE {inode} SET
  xattr_names  = xattr_names[1:array_position(xattr_names, @name)-1]
               || xattr_names[array_position(xattr_names, @name)+1:],
  xattr_values = xattr_values[1:array_position(xattr_names, @name)-1]
               || xattr_values[array_position(xattr_names, @name)+1:],
  updated_at = current_timestamp
WHERE id = @id AND array_position(xattr_names, @name) IS NOT NULL;

-- ListXAttr (on cache miss)
SELECT xattr_names FROM {inode} WHERE id = @id;
```

`@value` is a bytea parameter (Npgsql binds `byte[]` to bytea). The slice `arr[1:0]` is the empty array, and
`arr[n+1:]` is an open-ended tail slice (PostgreSQL 9.x+).

Private helpers to retire: `EncodeXattrValue` / `DecodeXattrValue` / `ParseXAttrFromJson` /
`ListXAttrNamesFromJson`. Rewrite the cache path (the branch where `GetXAttr`/`ListXAttr` use `inodeCache.Get`)
to index-search the two in-memory arrays.

## In-memory representation (Inode model / cache)

Replace `string xattrs` / `string Xattrs` in [Models/Inode.cs](../src/lib/src/Models/Inode.cs) with

```csharp
public string[] xattr_names  { get; set; } = System.Array.Empty<string>();
public byte[][] xattr_values { get; set; } = System.Array.Empty<byte[]>();
```

(Dapper maps automatically by column name). Provide one name→value lookup helper (e.g.
`TryGetXattr(name, out byte[])`). Places to update:
- [InodeCache.cs](../src/lib/src/Api/InodeCache.cs): change `inode.xattrs` in the central `inodeSelectColumns`
  to `inode.xattr_names, inode.xattr_values`. Change the root inode default (`xattrs = "{}"`) to empty arrays.
- the cross-shard rename INSERT in [Api.cs](../src/lib/src/Api/Api.cs): change `xattrs = old.Xattrs` to
  `xattr_names = old.xattr_names` / `xattr_values = old.xattr_values` (Npgsql binds the arrays directly). A
  normal `InsertInode` is a fresh row, so it uses empty arrays (leave it to the column DEFAULT).

> Note: when including the two array columns in a SELECT, dropping one breaks the NUL/high-byte round-trip.
> Always fetch both arrays together, including in hard-link creation (`CreateHardLink`).

## Migration

Because this is still development, there is **no in-place migration**. After the schema change the assumption
is to rebuild with `mkfs --clean` (both the real `pgfs` DB and the docker test DB).

(optional / reference) a one-off SQL if you want to convert from the existing JSONB:

```sql
UPDATE {prefix}inode SET
  xattr_names  = ARRAY(SELECT jsonb_object_keys(xattrs)),
  xattr_values = ARRAY(SELECT decode(xattrs ->> k, 'base64')
                       FROM jsonb_object_keys(xattrs) AS k);
-- NB: mind the order match. If you actually do this, align with LATERAL.
```

## Tests

Add xattr round-trip cases to the Linux e2e ([tests/linux/e2e.sh](../tests/linux/e2e.sh)):

- set/get/list/remove of an ASCII text value (`user.comment=hello`)
- a faithful round-trip of a **value containing NUL** (`printf 'a\0b'`) (it must not break after dropping Base64)
- a round-trip of **raw binary** (including high bytes)
- a round-trip of an empty value (`setfattr -v ''`)
- list order with many keys (10+) and consistency after a remove
- the existing `system.posix_acl_access` continues to pass (regression)

The Windows e2e is out of scope for xattr (POSIX-specific), so nothing is added there.

## Related

- [permission-interop.md](permission-interop.md) — delivering the POSIX ACL via `system.posix_acl_access`.
  This change makes that bytea round-trip more straightforward.
- [database.md](database.md) — the `pgfs_inode` schema summary (update the description of the xattr columns).
