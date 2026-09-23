# Making xattr byte strings transparent (a parallel-array KVS)

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: the design and implementation of replacing the xattr
> store, from JSONB plus Base64 to two parallel arrays
> (`xattr_names TEXT[]` + `xattr_values BYTEA[]`). The options taken and rejected and why, the schema
> difference in `{prefix}inode`, rewriting the Api onto array operations, the in-memory representation, the
> migration approach and the round-trip cases worth verifying all belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [database.md](database.md) | The summary of the database schema including `{prefix}inode` (it also owns the current definition of the xattr columns) |
> | [../ddl/README.md](../ddl/README.md) | The DDL itself, per table ([pgfs_inode.sql](../ddl/pgfs_inode.sql)) |
> | [permission-interop.md](permission-interop.md) | The design of the side that carries a POSIX ACL as `system.posix_acl_access` |
> | [../Mount.md](../Mount.md) | How xattrs behave and what limits them, as the user sees it |
> | [../tests.md](../tests.md) | The test hub. The counts and how to run them, `test_xattr_binary` included |
> | [support_for_citus.md](support_for_citus.md) | The approach to Citus distribution (the background to deciding not to create a new type) |

A design note on replacing the extended-attribute (xattr) store of `pgfs_inode`, from **JSONB plus Base64**
to **two parallel arrays (`xattr_names TEXT[]` + `xattr_values BYTEA[]`)**. This document is the source of
truth for the implementation.

Status: **implemented**. 36/36 PASS in the e2e on real hardware (`test_xattr_binary` was added for a faithful
round trip of NUL and high bytes). The verification also caught and fixed a regression where CreateHardLink
was missing a column.

## Why

It used to keep `{ name: Base64(value) }` in `pgfs_inode.xattrs JSONB`
([Api.EncodeXattrValue/DecodeXattrValue](../../src/core/src/Api/Api.cs)).

- An xattr value is **an arbitrary byte string at the OS level** (NUL included: the trailing NUL of
  `security.selinux`, and `security.capability` / `system.posix_acl_access` are raw binary). A byte string
  does not go into a JSON string as-is, hence the Base64 wrapping.
- The Base64 decode was **a heuristic with no header and no key check** - try `Convert.FromBase64String` and
  fall back to UTF-8 on failure. In normal operation every value is Base64 so no ambiguity arises, but the
  fallback path has the latent ambiguity of "plain text that happens to be valid Base64 decodes wrongly".
- Base64 inflates the size by about 33% and makes the contents unreadable from SQL.

-> Holding the values **faithfully as bytea** removes both the encoding round trip and the decision heuristic.

## The option taken and the ones rejected

**Taken: parallel arrays** - `xattr_names TEXT[]` + `xattr_values BYTEA[]` carried on the inode row (the same
index pairs them). Why:

- It creates no new type (no `CREATE TYPE`, so no Citus type propagation either).
- Npgsql round-trips `text[]<->string[]` and `bytea[]<->byte[][]` **out of the box** (no type registration to do).
- Staying on the inode row keeps the current caching design of "fetch every xattr together when the inode is
  loaded" intact (which matters for SELinux, which queries frequently). The volume is small, so no index is
  needed and a linear scan in the application after the fetch is fine.

Rejected:
- **HSTORE**: it is `text => text` only and cannot hold a bytea value.
- **An array of a composite type `xattr_entry[]` (name text, value bytea)**: tidy in terms of consistency, but
  it costs `CREATE TYPE` plus Citus type propagation plus registering the composite type with Npgsql. The
  parallel arrays reach the same goal more lightly.
- **A child table `pgfs_xattr`**: the most relational option, but loading an inode would need a join or a
  second query, and it adds another distributed table. Overkill at this size (small volume, no index needed).
- **Packing by hand into a single bytea**: values contain NUL, so a `\0` separator breaks down. It would need
  length-prefix framing, which means hand-written parsing hell and invisibility from SQL. Rejected.

## The schema difference

In `pgfs_inode`,

```sql
xattrs JSONB NOT NULL DEFAULT '{}'::JSONB
```

is replaced by

```sql
xattr_names  TEXT[]  NOT NULL DEFAULT '{}'::TEXT[],
xattr_values BYTEA[] NOT NULL DEFAULT '{}'::BYTEA[]
```

The places to update:
- [docs/ddl/pgfs_inode.sql](../ddl/pgfs_inode.sql) (the column definitions and the `'{}'::JSONB` in the sample INSERT)
- The inode `ColumnInfo("xattrs", "JSONB", ...)` in [Initializer.cs](../../src/mkfs/src/Initializer.cs)

**The invariants**: `cardinality(xattr_names) = cardinality(xattr_values)`, and `xattr_names` is unique.
All of it is upheld by the Api's single-statement UPDATEs keeping both arrays in step (the application never
does a read-modify-write).

Citus: only the column types change. The distribution of inode (the PK `(parent_id, id)` with `parent_id` as
the distribution key) is unchanged, and no type propagation is needed.

## Rewriting the Api (from JSONB operators to array operations)

Atomicity is kept, as with the current JSONB `||`/`-`, by **a single UPDATE statement with no subquery**
(`array_position` and array slices applied directly to the columns; a multi-shard UPDATE with `WHERE id = @id`
is the same practice as the existing xattr updates). It relies on the RHS being evaluated against the row's
pre-update values.

```sql
-- GetXAttr (on a cache miss): bytea or NULL
SELECT xattr_values[array_position(xattr_names, @name)]
FROM {inode} WHERE id = @id;

-- The existence check (for createOnly/replaceOnly)
SELECT array_position(xattr_names, @name) IS NOT NULL FROM {inode} WHERE id = @id;

-- SetXAttr: replace the value at the same index when it exists, append when it is new (atomic)
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

-- RemoveXAttr: remove the name's index from both arrays (atomic)
UPDATE {inode} SET
  xattr_names  = xattr_names[1:array_position(xattr_names, @name)-1]
               || xattr_names[array_position(xattr_names, @name)+1:],
  xattr_values = xattr_values[1:array_position(xattr_names, @name)-1]
               || xattr_values[array_position(xattr_names, @name)+1:],
  updated_at = current_timestamp
WHERE id = @id AND array_position(xattr_names, @name) IS NOT NULL;

-- ListXAttr (on a cache miss)
SELECT xattr_names FROM {inode} WHERE id = @id;
```

`@value` is a bytea parameter (Npgsql binds a `byte[]` to bytea). The slice `arr[1:0]` is an empty array, and
`arr[n+1:]` is an open-ended slice (PostgreSQL 9.x+).

The private helpers that go away: `EncodeXattrValue` / `DecodeXattrValue` / `ParseXAttrFromJson` /
`ListXAttrNamesFromJson`. The cached paths (the branch where `GetXAttr`/`ListXAttr` use `inodeCache.Get`) are
rewritten to search the two in-memory arrays by index.

## The in-memory representation (the Inode model and the cache)

In [Models/Inode.cs](../../src/core/src/Models/Inode.cs), `string xattrs` / `string Xattrs` become

```csharp
public string[] xattr_names  { get; set; } = System.Array.Empty<string>();
public byte[][] xattr_values { get; set; } = System.Array.Empty<byte[]>();
```

(Dapper maps them automatically by column name.) One name-to-value lookup helper is provided (for example
`TryGetXattr(name, out byte[])`).
The places to update:
- [InodeCache.cs](../../src/core/src/Api/InodeCache.cs): `inode.xattrs` in the central `inodeSelectColumns`
  becomes `inode.xattr_names, inode.xattr_values`. The root inode's default (`xattrs = "{}"`) becomes empty arrays.
- The cross-shard rename INSERT in [Api.cs](../../src/core/src/Api/Api.cs): `xattrs = old.Xattrs` becomes
  `xattr_names = old.xattr_names` / `xattr_values = old.xattr_values` (Npgsql binds the arrays directly).
  The ordinary `InsertInode` is a new row, so it gets empty arrays (left to the column DEFAULT).

## Migration

This is still a development stage, so **there is no in-place migration**. After the schema change the
assumption is to rebuild with `mkfs --clean` (both the `pgfs` database on the server and the docker test
database).

(Optional, for reference) the one-off SQL if you do want to convert from the existing JSONB:

```sql
UPDATE {prefix}inode SET
  xattr_names  = ARRAY(SELECT jsonb_object_keys(xattrs)),
  xattr_values = ARRAY(SELECT decode(xattrs ->> k, 'base64')
                       FROM jsonb_object_keys(xattrs) AS k);
-- Note: mind that the orders match. If you really do this, line them up with LATERAL.
```

## Testing

Round-trip cases for xattrs were added to the Linux e2e ([tests/linux/e2e.sh](../../tests/linux/e2e.sh)):

- set/get/list/remove of an ASCII text value (`user.comment=hello`)
- A faithful round trip of **a value containing NUL** (`printf 'a\0b'`) (it must not break now that Base64 is gone)
- A round trip of **raw binary** (containing high bytes)
- A round trip of an empty value (`setfattr -v ''`)
- Many keys (10+): the list order and the consistency after a remove
- The existing `system.posix_acl_access` continuing to work (a regression)

The Windows e2e does not cover xattrs (they are POSIX-specific), so nothing was added there.
