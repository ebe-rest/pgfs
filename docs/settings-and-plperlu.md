# Settings-scope rework + plperlu gate + tablespace auto-mkdir (design)

Design for a set of configuration-model changes and the Citus custom-tablespace support that follows from
them. **The configuration model is the area [the CLAUDE guidance] flags as "handle carefully"**, so settle the
design here before implementing. The source of truth for settings is
[Schema.cs](../src/lib/src/Config/Schema.cs) / [settings-matrix.md](settings-matrix.md).

For the Japanese version see [settings-and-plperlu.ja.md](settings-and-plperlu.ja.md).

Status: **implemented and verified on real hardware**. On a throwaway PG on linux_client we verified the
plperlu matrix / `--deny-plperlu` / `--plperlu false` / require+deny error / no DB keys in the generated toml +
the distribution comment / `--citus`+custom tablespace + plperlu auto-mkdir (creating the dir owned by postgres
0700) / mount sanity. An mkfs against a production-equivalent PostgreSQL also confirmed that `app.statfs` /
`database.citus` / `app.plperlu` / the `file_system` size keys are stored in `pgfs_settings`, and that the
generated toml carries the distribution comment without emitting the size keys.

## Motivation

1. **Settings that should be a single value for the whole fs live only in toml** → if two clients connect with
   different values, things break. The post-mkfs `pgfs.toml` emitted `[file_system] cluster_size /
   default_chunk_size / max_file_size`, but these are **identifying information** for "how this FS was created",
   not arbitrary client settings. Meanwhile the `pgfs_settings` side (audit.enabled / file_system.version /
   volume_label / mount.fallback_* / statfs) is correctly DB-authoritative. → Make the size keys DB-authoritative too.
2. We want an **upper gate for whether plperlu (untrusted) may be used**. `--statfs nominal` was effectively
   "don't use plperlu", but tablespace auto-mkdir also uses plperlu, so factor the decision into one place.
3. We want to **check from the DB later whether this FS is Citus** (not only from toml's `database.citus`).

## Changes

### (A) new scope `app` — application-behavior settings

| Key | Type | Default | SaveTo | Meaning |
|---|---|---|---|---|
| `app.plperlu` | bool | **true (allow)** | **Db** | whether plperlu (untrusted Perl) may be used. The upper gate for whether mkfs may use plperlu for the statfs functions / tablespace auto-mkdir. Stored in the DB (reused on mkfs re-run + records "this FS was created with plperlu allowed"). Not written to toml |

CLI:

| Form | Result | Kind |
|---|---|---|
| `--plperlu` | allow (true) | canonical. bare=true; optionally takes a following `true`/`false` |
| `--plperlu true` | allow | same (explicit value) |
| `--plperlu false` | deny | same (explicit value) |
| `--allow-plperlu` | allow | a **bare-only fixed-true alias** (takes no value) |
| `--deny-plperlu` | deny | a **bare-only fixed-false alias** (takes no value) |

Implementation: add the concept of a **fixed-false alias set (negated CliOptions)** to
[Field](../src/lib/src/Config/Field.cs)/`BoolField`. Extend the bool parse in
[ConfigLoader](../src/lib/src/Config/ConfigLoader.cs):
- match on the canonical / positive alias (`--plperlu` / `--allow-plperlu`) → set true and **consume the next
  arg as the value if it is a `true`/`false` literal**, otherwise leave it bare=true.
- match on the negated alias (`--deny-plperlu`) → set false (does not consume a value).
- other existing bool flags (`--foreground`, etc.) have positive aliases only, so their behavior is unchanged
  (bare=true; no value consumed unless a literal follows).

### (B) put the statfs mode under `app.statfs` (scope unification)

It is application behavior, so it goes under the `app` scope (same scope as `app.plperlu`). The value is
`auto|require|nominal`, SaveTo=Db. The C# reference is `Schema.Statfs.Mode`, the CLI is `--statfs` /
`--statfs-mode`, and on `pgfs_settings` it is `app / statfs`.

### (C) make `database.citus` DB-authoritative (name unchanged)

Hold the bool of "this FS is Citus-ified" in the DB. The name stays `database.citus`; only SaveTo changes:

- `Scope="database", Key="citus", **SaveTo=Db**`, CLI `--citus` unchanged, AppliesTo=Mkfs.
- The value is only "whether it is used" as a bool (no details like a worker list — nothing reads them).

### (D) make the `file_system` size keys DB-authoritative

Change `SaveTo` from File → **Db** (the scope `file_system` stays; it lines up with version/volume_label which
are already Db):

- `file_system.cluster_size`
- `file_system.default_chunk_size`
- `file_system.max_file_size`

**The resolution precedence (CLI>TOML>DB>Default) is unchanged.** Instead, **settings with SaveTo=Db are not
written to the mkfs-generated toml** (rule below). Because they do not appear in the generated toml, an ordinary
client's toml has no size keys and the DB takes effect.

- mkfs's [WriteTomlFile/AddField](../src/mkfs/src/Program.cs) writes out **only SaveTo=File**, so making the
  size keys Db **removes them from the generated toml automatically** (no extra code; the explicit `AddField`
  calls become no-ops, so they are removed).
- Drop the size keys, statfs, and citus from [pgfs.toml.example](../pgfs.toml.example) and the toml samples in
  the docs (so as not to imply "you can set these in toml").
- However, **if you hand-write them in toml they are read and obeyed per spec** (TOML>DB). If that breaks
  things it is on whoever wrote them. The escape hatch of writing a value in toml during development is kept
  (and stated as spec).

## plperlu × statfs behavior matrix

The design of the statfs functions (`{prefix}statfs` / `fs_free`), the three-tier fallback, and the Citus
multi-worker aggregation have [df-support.md](df-support.md) as the source of truth. This section shows only
the interaction with the `app.plperlu` gate.

| `--statfs` | `app.plperlu` = allow | `app.plperlu` = deny |
|---|---|---|
| `auto` | try it, and on failure continue as nominal | fall back to nominal |
| `nominal` | create no stored functions | create no stored functions |
| `require` | try it, and error on failure | **error** (require with plperlu disallowed is a contradiction) |

## tablespace auto-mkdir + lifting the Citus constraint

Remove the `--citus` + `--tablespace≠pg_default` prohibition guard in
[Initializer.ValidateConfigCombinations](../src/mkfs/src/Initializer.cs), and make **CREATE DATABASE WITH
TABLESPACE the default** (drop the per-table `TABLESPACE` clause; shards inherit the worker DB's default). Run
`EnsureTablespaceAsync` on the coordinator + every worker.

When the LOCATION dir does not exist and `app.plperlu` = allow, **mkdir via plperlu before `CREATE TABLESPACE`
would fail**:

```sql
DO LANGUAGE plperlu $PL$
  use File::Path qw(make_path);   # recursive create OK
  make_path($ENV{PGFS_TS_DIR});   # NB: how the path is passed is settled at impl time (DO takes no args, so
  chmod 0700, $ENV{PGFS_TS_DIR};  #   make it a function or embed a literal. Created by the postgres OS user = owned by postgres)
$PL$;
```

- plperlu runs as the postgres OS user, so the created dir is **owned by postgres, 0700** = matching CREATE
  TABLESPACE's requirement (no more manual `sudo mkdir/chown/chmod` by the operator).
- Citus pushes the mkdir DO to every node with `run_command_on_all_nodes` (the dir is node-local). Single-node
  Citus works on one host. Multi-node assumes the parent dir is writable by postgres (if not, make_path also
  fails → explicit error).
- With `app.plperlu` = deny there is no auto-mkdir; a missing dir produces the usual explicit `CREATE TABLESPACE`
  failure.
- df synergy: `pgfs_fs_free` resolves the real dir via `pg_tablespace_location`, so `df` under `--statfs
  require` returns the **real free space of the tablespace itself** (for pg_default it is data_directory).

## Migration

Because this is still development, there is no in-place migration. After the schema/setting-key change the
assumption is to rebuild with `mkfs --clean`. The `pgfs_settings` rows change (scope,key) (the statfs mode is
`app/statfs`, `database.citus` becomes SaveTo=Db, and the file_system size keys newly appear as DB rows).

## Places to touch

- [Schema.cs](../src/lib/src/Config/Schema.cs): add the `app` nested class (`Plperlu` / `Statfs`), change the
  SaveTo of the `FileSystem` size keys, change the SaveTo of `Database.Citus`.
- [ConfigLoader.cs](../src/lib/src/Config/ConfigLoader.cs): the extension where bool optionally consumes
  `true|false`, and alias handling.
- [RootConfig](../src/lib/src/Config/RootConfig.cs) + each `*Config` POCO: add `AppConfig`, re-home
  `StatfsConfig`/`DatabaseConfig`. Wire up the Build* methods.
- [Initializer.cs](../src/mkfs/src/Initializer.cs): reference the plperlu gate, tablespace auto-mkdir, remove
  the guard, drop the per-table TABLESPACE clause + switch to CREATE DATABASE WITH TABLESPACE, add the new keys
  to PopulateSettingsRows.
- [settings-matrix.md](settings-matrix.md): update the table.
- Help ([HelpText](../src/lib/src/Config/HelpText.cs)) is auto-generated from Schema, so it follows along.

## (F) leave the "distribution mkfs parameters" as a comment in the mkfs-generated toml

When mkfs writes out the toml ([WriteTomlFile](../src/mkfs/src/Program.cs)), leave **the mkfs command /
parameters meant for distributing to other clients as a comment** at the top. Prepend `#` lines before the
Toml.FromModel output text.

- **Excluded args**: `--clean` (re-running destroys the DB) / the `--super` family
  (`--super-user-connection`, etc.; super credentials are not distributed & clients do not need super).
- Example included: `--connection ... --schema pgfs --prefix pgfs_ --citus --statfs require --volume-label pgfs ...`
- The connection string's Password is already emitted in plaintext in `[database].connection` so this leaks no
  more, but the comment side **masks the Password** the same way as
  [DescribeProvided](../src/lib/src/Config/ConfigLoader.cs).

## Decisions

1. **CLI**: as in the table above (`--plperlu [true|false]` canonical / `--allow-plperlu` bare=allow /
   `--deny-plperlu` bare=deny). `--deny-plperlu` is the fixed-false negated alias.
2. **DB authority for the file_system size keys**: the resolution precedence is unchanged; **SaveTo=Db ones are
   not written to the generated toml + are dropped from docs/samples** (hand-written ones are read per spec = at
   your own risk).
3. **`app.plperlu` SaveTo = Db** (not written to toml).
