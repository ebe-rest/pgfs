# Reorganizing the settings scopes plus the plperlu gate plus the tablespace auto-mkdir (the design)

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the design and the decisions** of the settings-model
> change agreed. The new scope `app` (the `app.plperlu` gate and the move from `statfs.mode`),
> making `database.citus` and the `file_system` size settings database-authoritative, the behaviour matrix of
> plperlu against statfs, the auto-mkdir of a custom Citus tablespace, and the distribution comment left in the
> toml mkfs generates all belong here. **The current value of an individual setting is not tracked here.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [settings-matrix.md](settings-matrix.md) | The current list of every setting (the CLI / the TOML / the database / the default / when it is read). The decisions here are reflected there |
> | [../Mkfs.md](../Mkfs.md) | The user-facing `mkfs.pgfs` CLI specification and the defaults table |
> | [df-support.md](df-support.md) | The df/statfs implementation `app.statfs` switches between (the plperlu function, the three-stage fallback) |
> | [support_for_citus.md](support_for_citus.md) | The design of the Citus distribution itself. The background of the tablespace restriction |
> | [database.md](database.md) | The design of the database schema including `{prefix}settings` |
> | [control-plane.md](control-plane.md) | The route for reading and writing the settings at runtime (`pgfsctl config` / the reload policy) |

The design memo for the series of settings-model changes agreed in review, and the Citus
custom tablespace support that follows from it. **The settings model is the area the project instructions single
out as one to proceed with carefully**, so it is agreed in this document before being implemented. The source of
truth for the settings is [Schema.cs](../../src/core/src/Config/Schema.cs) /
[settings-matrix.md](settings-matrix.md).

The status: **implemented and verified on real hardware**. 28/28 PASS on a throwaway PG on the
Linux client (the plperlu matrix / `--deny-plperlu` and `--plperlu false` / the require+deny error / the
database keys not appearing in the generated toml plus the distribution comment / `--citus` plus a custom
tablespace plus the plperlu auto-mkdir (creating the directory owned by postgres at 0700) / a mount sanity
check).
Chunk 1 was also **confirmed on real hardware with an mkfs against the production PG server**
(app.statfs (then pgfs.statfs) / database.citus / app.plperlu / the file_system size settings being saved in
`pgfs_settings`, and the generated toml carrying the distribution comment with the size settings absent).

## The motivation

1. **A setting that ought to be one value for the whole FS exists only in the toml** -> two clients connecting
   with different values cause an accident.
   The `pgfs.toml` after an mkfs on real hardware carried
   `[file_system] cluster_size / default_chunk_size / max_file_size`, but those are **identity information**
   about "how this FS was made", not per-client settings. The `pgfs_settings` side (audit.enabled /
   file_system.version / volume_label / mount.fallback_* / statfs.mode) is database-authoritative and correct.
   -> Make the size settings database-authoritative too.
2. **A higher gate on whether plperlu (untrusted) may be used** is wanted. `--statfs nominal` was effectively
   "do not use plperlu" today, but the tablespace auto-mkdir uses plperlu too, so the decision is carved out
   into one place.
3. **Being able to confirm from the database afterwards whether "this FS is Citus"** (today it is only
   `database.citus` in the toml).

## What changes

### (A) The new scope `app` - the settings for the application behaviour

| The key | The type | The default | SaveTo | What it means |
|---|---|---|---|---|
| `app.plperlu` | bool | **true (allow)** | **Db** | Whether the use of plperlu (untrusted Perl) is permitted. The higher gate on whether mkfs may use plperlu for the statfs function and the tablespace auto-mkdir. Saved in the database (reused on a re-run of mkfs, plus a record that "this FS was made with plperlu permitted"). It is not written into the toml |

The CLI (agreed):

| The form | The result | The kind |
|---|---|---|
| `--plperlu` | allow (true) | Canonical. Bare = true, optionally taking a following `true`/`false` |
| `--plperlu true` | allow | The same (with a value) |
| `--plperlu false` | deny | The same (with a value) |
| `--allow-plperlu` | allow | **A bare-only alias fixed at true** (it takes no value) |
| `--deny-plperlu` | deny | **A bare-only alias fixed at false** (it takes no value) |

The implementation: the concept of **a set of aliases fixed at false (negated CliOptions)** is added to
[Field](../../src/core/src/Config/Field.cs) / `BoolField`. The bool parsing of
[ConfigLoader](../../src/core/src/Config/ConfigLoader.cs) is extended:
- Matching the canonical form or a positive alias (`--plperlu` / `--allow-plperlu`) sets true and
  **consumes the following argument as the value if it is a `true`/`false` literal**, otherwise it stays
  bare = true (`--allow-plperlu` can be operated as taking no value, but for simplicity of implementation it is
  acceptable to unify on "a positive alias can swallow a value").
- Matching a negated alias (`--deny-plperlu`) sets false (it does not swallow a value).
- The other existing bool flags (`--foreground` and so on) have only positive aliases, so their behaviour is
  unchanged (bare = true, no value consumed unless a literal follows).

### (B) `statfs.mode` -> `app.statfs` (a scope move)

It is application behaviour, so it moves to the `app` scope (the same scope as `app.plperlu`). The values stay
`auto|require|nominal` and SaveTo=Db is unchanged.

- Old: `Scope="statfs", Key="mode"` (`statfs / mode` in `pgfs_settings`)
- New: `Scope="app", Key="statfs"` (`app / statfs` in `pgfs_settings`). The C# reference stays
  `Schema.Statfs.Mode`.
- The CLI `--statfs` / `--statfs-mode` is unchanged.
- (How it was decided: it was first moved to `pgfs.statfs`, but that left the `pgfs` scope holding statfs alone,
  so it was changed again to `app.statfs`. The `pgfs` scope was dropped.)

### (C) Making `database.citus` database-authoritative (with no rename)

A bool for "whether this FS has been made Citus" is held in the database. A rename to `pgfs.citus` was
considered but **the rename was cancelled** (it stays `database.citus`). Only the SaveTo changes:

- Old: `Scope="database", Key="citus", SaveTo=File`
- New: `Scope="database", Key="citus", **SaveTo=Db**`, with the CLI `--citus` unchanged and AppliesTo=Mkfs.
- The value is only "a bool for whether it is in use" (it does not hold the details such as the worker list,
  because nothing reads them).

### (D) Making the `file_system` size settings database-authoritative

The `SaveTo` changes from File to **Db** (the scope `file_system` stays; version and volume_label are already
Db so they line up):

- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`

**The resolution precedence (CLI > TOML > the database > the default) does not change.** Instead,
**a setting with SaveTo=Db is not written into the toml mkfs generates** (the rule below). Since it does not
appear in the generated toml, an ordinary client's toml has no size settings and the database takes effect.

- mkfs's [WriteTomlFile/AddField](../../src/mkfs/src/Program.cs) writes out **only the SaveTo=File ones** to
  begin with, so making the size settings Db **removes them from the generated toml automatically** (no extra
  code is needed; the 3 explicit `AddField` calls become no-ops and are removed).
- The size settings, statfs and citus are **taken out** of [pgfs.toml.example](../../pgfs.toml.example) and the
  toml samples in the documents (so as not to give the impression that they can be set in the toml).
- But **if it is written into the toml by hand it is still read and obeyed as specified** (TOML > the
  database). If that causes an accident, it is the writer's responsibility. The escape hatch of being able to
  write a value into the toml while developing is kept (stated as part of the specification).

## The behaviour matrix of plperlu against statfs (agreed)

[df-support.md](df-support.md) is the source of truth for the design of the statfs function
(`{prefix}statfs` / `fs_free`), the three-stage fallback and the multi-worker aggregation on Citus. This section
shows only how it combines with the `app.plperlu` gate.

| `--statfs` | `app.plperlu` = allow | `app.plperlu` = deny |
|---|---|---|
| `auto` | Try it, and carry on as the equivalent of nominal if it does not work (as today) | Fall back to nominal |
| `nominal` | No stored function is created (as today) | No stored function is created (as today) |
| `require` | Try it, and error if it does not work | **An error** (require contradicts plperlu being disallowed) |

## The tablespace auto-mkdir plus removing the Citus restriction

The guard in [Initializer.ValidateConfigCombinations](../../src/mkfs/src/Initializer.cs) forbidding `--citus`
plus `--tablespace≠pg_default` is removed, and **CREATE DATABASE WITH TABLESPACE becomes the default** (the
per-table `TABLESPACE` clause is dropped, and the shards inherit the worker database's default).
`EnsureTablespaceAsync` runs on the coordinator plus every worker.

When the LOCATION directory does not exist and `app.plperlu` = allow, **the mkdir happens through plperlu before
the `CREATE TABLESPACE` fails**:

```sql
DO LANGUAGE plperlu $PL$
  use File::Path qw(make_path);   # recursive creation is fine (agreed)
  make_path($ENV{PGFS_TS_DIR});   # note: how the path is passed is settled at implementation time (a DO takes
  chmod 0700, $ENV{PGFS_TS_DIR};  #   no arguments, so it becomes a function or a literal. It is created as the
$PL$;                             #   postgres OS user = owned by postgres)
```

- plperlu runs as the postgres OS user, so the created directory is **owned by postgres at 0700** = exactly what
  CREATE TABLESPACE requires (what an operator used to do by hand with `sudo mkdir/chown/chmod` becomes
  unnecessary).
- On Citus the mkdir DO is fired at every node with `run_command_on_all_nodes` (the directory is node-local). A
  single-node Citus works on one machine. Multi-node presupposes that the parent directory is writable by
  postgres (if it is not, make_path fails too -> an explicit error).
- With `app.plperlu` = deny there is no auto-mkdir, and a missing directory is an explicit error through a
  failing `CREATE TABLESPACE` as before.
- A synergy with df: `pgfs_fs_free` looks up the real directory with `pg_tablespace_location`, so a `df` with
  `--statfs require` returns **the real free space of the tablespace** (for example /database/tbs/pgfs), where
  today pg_default is forced and it is the data directory.

## The migration

It is still in development, so there is no in-place migration. After a schema or settings-key change, the
premise is rebuilding with `mkfs --clean`.
The rows of `pgfs_settings` change their (scope, key) (statfs/mode -> app/statfs; `database.citus` only changes
to SaveTo=Db; the `file_system` size settings newly appear as database rows).

## Where to touch

- [Schema.cs](../../src/core/src/Config/Schema.cs): add the `app` nested class (`Plperlu`), merge `Statfs` into
  `Pgfs` (`Pgfs.Statfs` / `Pgfs.Citus`), change the SaveTo of the `FileSystem` size settings and move
  `Database.Citus`.
- [ConfigLoader.cs](../../src/core/src/Config/ConfigLoader.cs): extend bool to optionally swallow a
  `true|false`, and handle the aliases.
- [RootConfig](../../src/core/src/Config/RootConfig.cs) plus the `*Config` POCOs: add `AppConfig` and move
  `StatfsConfig` / `DatabaseConfig`. Wire up the Build* methods.
- [Initializer.cs](../../src/mkfs/src/Initializer.cs): consult the plperlu gate, add the tablespace auto-mkdir,
  remove the guard, drop the per-table TABLESPACE clause in favour of CREATE DATABASE WITH TABLESPACE, and add
  the new keys to PopulateSettingsRows.
- [settings-matrix.md](settings-matrix.md): update the table.
- The help ([HelpText](../../src/core/src/Config/HelpText.cs)) is generated from the Schema and follows
  automatically.

## (F) Leaving the "mkfs parameters for distribution" as a comment in the toml mkfs generates

When mkfs writes out the toml ([WriteTomlFile](../../src/mkfs/src/Program.cs)), it leaves **the mkfs command and
parameters for distributing to other clients as a comment** at the top. `#` lines are prepended before the
output text of Toml.FromModel.

- **The arguments excluded**: `--clean` (re-running it destroys the database) and the `--super` family
  (`--super-user-connection` and so on; the super credentials are not distributed and a client does not need
  super).
- An example of what is included:
  `--connection ... --schema pgfs --prefix pgfs_ --citus --statfs require --volume-label pgfs ...`
- The Password of the connection string is already in plain text in `[database].connection`, so no more
  information leaks, but it is safer to **mask the Password** on the comment side as
  [DescribeProvided](../../src/core/src/Config/ConfigLoader.cs) does (settled at implementation time).

## The decisions (resolving the open questions)

1. **The CLI**: as in the table above (`--plperlu [true|false]` canonical / `--allow-plperlu` bare = allow /
   `--deny-plperlu` bare = deny). `--deny-plperlu` is a negated alias fixed at false.
2. **Making the `file_system` size settings database-authoritative**: the resolution precedence does not change;
   **a SaveTo=Db one is not written into the generated toml and is taken out of the documents and the samples**
   (writing it by hand is read as specified = at one's own risk).
3. **The SaveTo of `app.plperlu` = Db** (it is not written into the toml).
