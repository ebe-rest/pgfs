# pgfs

> **Route**: **this document is the entrance to the public repository** › [docs/README.md](docs/README.md)
> (the documentation index) › each document
>
> **What this document is the source of truth for**: what pgfs is, the shortest path to running it, and the
> list of the released documents.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [docs/README.md](docs/README.md) | **The index of every document** |
> | [CHANGELOG.md](CHANGELOG.md) | The differences per release, the migration steps and the known limitations |
> | [docs/Mkfs.md](docs/Mkfs.md) / [docs/Mount.md](docs/Mount.md) / [docs/Assign.md](docs/Assign.md) | The user-facing specification of each CLI |

A project implementing **a FUSE filesystem that uses PostgreSQL as its backing store** in C# (.NET 10). The
goal is to offer the same behaviour on Linux, macOS and Windows.

## The overview

- The whole filesystem (the directory entries, the inode attributes, the file data and the settings) is stored
  in **PostgreSQL tables** (`bytea` chunks).
- It is mounted with **FUSE (an in-house libfuse binding)** on Linux (the runtime compatibility on macOS is
  unverified) and with **Dokan (`DokanNet`)** on Windows.
- The settings are read from **TOML** (`pgfs.toml`) and the `pgfs_settings` table in the database, and can be
  overridden with the CLI arguments.
- For the database schema see [docs/design/database.md](docs/design/database.md).

> **Where it stands**: on top of Mkfs / Mount / Assign, the caches, the write-back,
> `pgfsctl config` / `status` / `prune` and a read-only GUI are implemented.
> **Every finding raised against Linux and the shared Core is closed**, and
> **the Windows acceptance with the write-back (data and metadata) enabled is done too**.
> **What remains is not "a defect not yet fixed" but a limitation chosen deliberately** - the list is in
> [CHANGELOG.md, the known limitations](CHANGELOG.md). **[docs/tests.md](docs/tests.md) is the source of truth
> for the test counts and the verification records.**

> **The groundwork so far**: all three pillars (Mkfs / Mount / Assign) are implemented. The Linux and Windows
> e2e suites, the race verification on a multi-node Citus and the suite dedicated to the audit log all pass
> (**the counts grow, so they are not written here; [docs/tests.md](docs/tests.md) is authoritative**). Citus
> (the horizontal distribution) is done through Phase 1+2+3, the audit log is done, and the interoperability of
> the ACLs and the permissions between Linux and Windows is implemented
> ([docs/design/permission-interop.md](docs/design/permission-interop.md)).
> **v0.2.0 split `Lib` into the OS mechanism layers (`Core` / `Fuse` / `Dokan`) and brought the libfuse binding
> in house** (re-verified with the e2e green on both operating systems -
> [docs/design/v0.2.0-plan.md](docs/design/v0.2.0-plan.md)). For what comes next see
> [docs/next.md](docs/next.md).

## The solution structure

| Project | Path | Role | Platform | State |
|---|---|---|---|---|
| **Core** | [src/core/](src/core/) | The core library independent of the OS and the mechanism (Models / Api / Config / Logging / Collections / Utility / Objects) | Cross-platform | Implemented |
| **Fuse** | [src/fuse/](src/fuse/) | The FS mechanism layer for Linux and macOS (the in-house libfuse binding + the FUSE FileSystem + the PosixAcl projection) | Linux / macOS | Implemented, verified on Linux |
| **Dokan** | [src/dokan/](src/dokan/) | The FS mechanism layer for Windows (the Dokan FileSystem + the Windows SID/ACL projection) | Windows | Implemented, verified on Windows |
| **Mkfs** | [src/mkfs/](src/mkfs/) | The CLI that initialises the tables and so on on the PostgreSQL side (`mkfs.pgfs`, a thin exe over Core) | Cross-platform | Implemented, verified on Linux |
| **Mount** | [src/mount/](src/mount/) | The mount tool for Linux and macOS (`mount.pgfs`, a thin exe over Fuse) | Linux / macOS | The main operations and the ACL projection are implemented; there are known limitations and unsupported operations |
| **Assign** | [src/assign/](src/assign/) | The mount tool for Windows (`assign.pgfs`, a thin exe over Dokan) | Windows | The main operations and the ACL projection are implemented; links, ADS and so on are unsupported |
| **Ctl** | [src/ctl/](src/ctl/) | `pgfsctl config` / `status` / `prune` | Cross-platform | Implemented |
| **Gui** | [src/gui/](src/gui/) | The `pgfsgui` operations screen | Cross-platform | A read-only MVP; editing the settings and the distribution are unfinished |

## What you need

- **The .NET 10 SDK** or later ([download](https://dotnet.microsoft.com/download))
- **PostgreSQL 17** (on the database side)
- **Linux**: libfuse3 (`fuse3` or your distribution's equivalent), only if you use Mount. `mount.pgfs` `dlopen`s
  `libfuse3.so.3` at runtime (it is not bundled)
- **Windows**: [Dokan 2.x](https://github.com/dokan-dev/dokany/releases), only if you use the Assign project
- **macOS**: the runtime compatibility is unverified. The current binding assumes `libfuse3.so.3`, so installing
  macFUSE alone is not guaranteed to work

## Building

```pwsh
# Every project at once
dotnet build pgfs.sln

# Individually
dotnet build src/core/Core.csproj
dotnet build src/mkfs/Mkfs.csproj
dotnet build src/mount/Mount.csproj   # for running on Linux/macOS (on Windows only the cross-build passes)
dotnet build src/assign/Assign.csproj # for running on Windows (on Linux/macOS only the cross-build passes)
```

The build artifacts go under [bin/](bin/) (the
`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>` setting):

| Configuration | Output | Contents |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `core.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + the dependency dlls (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | The same in Release (framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | The CLIs as single-file self-contained `{mkfs,mount,assign}.pgfs[.exe]` and `pgfsctl[.exe]` (the host RID is detected automatically). The publish settings for the GUI in the same form are not in place yet |

Every assembly name is lower case separated by dots (`core.pgfs`, `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs`), so
a Debug build can be started directly as `./bin/Debug/mkfs.pgfs.exe`.

### Mount (Linux/macOS)

```pwsh
dotnet build src/mount/Mount.csproj
```

The build also passes on Windows (for cross-compilation); it only runs on Linux and macOS. For the details see
[docs/Mount.md](docs/Mount.md). The entry points for the data I/O, the xattrs, the links and so on are
implemented. `Access` is **deliberately not implemented** - deciding whether an access is allowed is left to
the kernel through `default_permissions` at mount time. `FAllocate` is unimplemented (it returns `-ENOSYS`).
**The list of the limitations chosen deliberately is in
[CHANGELOG.md, the known limitations](CHANGELOG.md).**

### Assign (Windows)

```pwsh
dotnet build src/assign/Assign.csproj
```

The build also passes on Linux and macOS (for cross-compilation); it only runs on Windows. The Dokan2 kernel
driver has to be installed beforehand. For the details see [docs/Assign.md](docs/Assign.md).

### Publishing a self-contained executable

`dotnet publish` produces a **single-file self-contained** executable for the host OS under
[bin/Publish/](bin/Publish/) (about 38 MB each).

```pwsh
# Individually
dotnet publish src/mkfs/Mkfs.csproj -c Release
dotnet publish src/mount/Mount.csproj -c Release
dotnet publish src/assign/Assign.csproj -c Release

# The whole solution (the publish settings differ between the CLIs and the GUI)
dotnet publish pgfs.sln -c Release
```

The RID is **detected from the host OS** through `UseCurrentRuntimeIdentifier=true`. To build for another OS,
override it with `-r <RID>` (for example `dotnet publish ... -c Release -r linux-x64`).

> AOT publishing (`PublishAot=true`) does not work for now, because of the reflection and P/Invoke dependencies
> of Dapper, Tomlyn, the in-house libfuse binding and DokanNet. The CLI projects are set to `PublishAot=false`
> and `PublishTrimmed=false`. Distributing the GUI is a separate matter.

## Using it

### 1. Prepare PostgreSQL

Start PostgreSQL 17 and make it reachable as a superuser (usually `postgres`).

### 2. Initialise PGFS

`mkfs.pgfs` creates the user, the database, the schema, the tables and the initial data that PGFS uses.

```bash
# With the defaults (localhost:5432 / the postgres superuser / creating the pgfs user and database)
dotnet run --project src/mkfs

# Giving the superuser connection explicitly
dotnet run --project src/mkfs -- \
    --super-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1"

# Giving the PGFS user connection explicitly
dotnet run --project src/mkfs -- \
    --connection "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs"

# Changing the schema and the prefix
dotnet run --project src/mkfs -- -s myschema -x myfs_

# Help
dotnet run --project src/mkfs -- --help
```

When it finishes, `pgfs.toml` (the settings file) is written into the current directory. For the details see
[docs/Mkfs.md](docs/Mkfs.md).

### 3. Mount

On Linux you mount it with `mount.pgfs`. macOS is unverified.

```bash
# Prepare the mount point
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# Mount
dotnet run --project src/mount -- -m /mnt/pgfs

# From another terminal
ls -la /mnt/pgfs

# Unmount
fusermount3 -u /mnt/pgfs
```

The data I/O, the extended attributes, the symlinks and the hardlinks are all supported. For the details see
[docs/Mount.md](docs/Mount.md).

On Windows you mount it onto a drive with `assign.pgfs` (the Dokan2 driver is required):

```pwsh
dotnet run --project src\assign -- -m P:
# From another terminal:
Get-ChildItem P:\
```

For the details see [docs/Assign.md](docs/Assign.md).

## The documentation

- [docs/README.md](docs/README.md) - the documentation index and the reading order
- [docs/next.md](docs/next.md) - **what comes next** (in priority order)
- [docs/architecture.md](docs/architecture.md) - the project structure / the dependency packages / the inside of
  Core / building and running
- [docs/Mkfs.md](docs/Mkfs.md) - the specification of `mkfs.pgfs`
- [docs/Mount.md](docs/Mount.md) - the specification of `mount.pgfs` (Linux/macOS)
- [docs/Assign.md](docs/Assign.md) - the specification of `assign.pgfs` (Windows)
- [docs/Pgfsctl.md](docs/Pgfsctl.md) - the specification of `pgfsctl` (the CLI of the runtime control plane -
  `config` / `status` / `prune`)
- [docs/design/database.md](docs/design/database.md) - the PostgreSQL schema design
- [docs/ddl/](docs/ddl/README.md) - the DDL per table
- [docs/design/windows-parity.md](docs/design/windows-parity.md) - the design for taking the Linux features to
  Windows (the candidates, the recommendations and the acceptance conditions)
- [docs/design/coding-style.md](docs/design/coding-style.md) - the C# coding conventions
- [docs/design/performance.md](docs/design/performance.md) - the implementation and measurement records of the
  performance work, and the remaining candidates
- [docs/design/support_for_citus.md](docs/design/support_for_citus.md) - the design notes for Citus (the
  horizontal distribution; Phase 1+2+3 done)
- [docs/design/fstab-support.md](docs/design/fstab-support.md) - the `/etc/fstab` support (the past records of
  verifying a mount at boot and the current constraints)
- [docs/history.md](docs/history.md) - how the current design came about (the model
  rearrangement / the Tmds.Fuse fork / fstab / Retry / the race fixes / Citus and so on)
- [docs/tests.md](docs/tests.md) - **the hub of the tests** (the list of every test / how to run them / the
  environment requirements). For each runner see [tests/linux/](tests/linux/README.md) /
  [tests/windows/](tests/windows/README.md) / [tests/citus/](tests/citus/README.md)

## The licence

See [LICENSE](LICENSE) (the MIT licence).
