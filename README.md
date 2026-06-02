# pgfs

A project implementing a **FUSE filesystem backed by PostgreSQL storage** in C# (.NET 10). The goal is to provide the same behavior on Linux / macOS / Windows.

[日本語版 README はこちら](README.ja.md)

## Overview

- The entire filesystem (directory entries, inode attributes, file data, settings) is stored in **PostgreSQL tables** (`bytea` chunks).
- Mounted via **FUSE (`Tmds.Fuse`)** on Linux / macOS, and via **Dokan (`DokanNet`)** on Windows.
- Settings are read from **TOML** (`pgfs.toml`) and the DB `pgfs_settings` table, and can be overridden with CLI arguments.
- For the DB schema, see [docs/database.md](docs/database.md).

> **Status**: all three pillars (Mkfs / Mount / Assign) are implemented, passing Linux e2e 35/35 + Windows e2e 26/26 + the race verification 4/4 on multi-node Citus + the audit-log dedicated suite 12/12. Citus (horizontal distribution) and the audit log are complete, and the Linux↔Windows interop for permissions/ownership/ACLs is implemented as well ([docs/permission-interop.md](docs/permission-interop.md)). For the list of what to do next, see [docs/next.md](docs/next.md).

## Solution layout

| Project | Path | Role | Platform | Status |
|---|---|---|---|---|
| **Lib** | [src/lib/](src/lib/) | core library (Models / Api / Logging / Collections / Utility / Objects) | cross-platform | implemented |
| **Mkfs** | [src/mkfs/](src/mkfs/) | CLI that initializes the PostgreSQL-side tables etc. (`mkfs.pgfs`) | cross-platform | implemented, verified on Linux |
| **Mount** | [src/mount/](src/mount/) | mount tool for Linux/macOS (`mount.pgfs`, Tmds.Fuse) | Linux / macOS | all FUSE operations implemented (incl. data I/O, xattr, symlink, hard link, POSIX ACL), verified on Linux |
| **Assign** | [src/assign/](src/assign/) | mount tool for Windows (`pgfs.assign`, DokanNet) | Windows | all Dokan operations implemented (ACL via Get/SetFileSecurity projection is supported too; only ADS is unsupported), verified on Windows |

## Requirements

- **.NET 10 SDK** or later ([download](https://dotnet.microsoft.com/download))
- **PostgreSQL 17** (the database side)
- **Linux**: libfuse3 (for Tmds.Fuse) — only if you use the Mount project
- **Windows**: [Dokan 2.x](https://github.com/dokan-dev/dokany/releases) — only if you use the Assign project
- **macOS**: macFUSE (limited support)

## Build

The whole solution builds cleanly (0 warnings, 0 errors).

```pwsh
# all projects at once
dotnet build pgfs.sln

# individually
dotnet build src/lib/Lib.csproj
dotnet build src/mkfs/Mkfs.csproj
dotnet build src/mount/Mount.csproj   # for Linux/macOS execution (only cross-builds on Windows)
dotnet build src/assign/Assign.csproj # for Windows execution (only cross-builds on Linux/macOS)
```

Build artifacts are output under [bin/](bin/) (`<BaseOutputPath>$(MSBuildThisFileDirectory)..\..\bin\</BaseOutputPath>`):

| Configuration | Output | Contents |
|---|---|---|
| `dotnet build -c Debug` | `bin/Debug/` | `lib.pgfs.dll` + `{mkfs,mount,assign}.pgfs.{dll,exe}` + dependency dlls (framework-dependent) |
| `dotnet build -c Release` | `bin/Release/` | the Release version of the above (framework-dependent) |
| `dotnet publish -c Release` | `bin/Publish/` | the 3 single-file self-contained `{mkfs,mount,assign}.pgfs[.exe]` (host RID auto, ~38 MB each) |

Assembly names are all lowercase dot-separated (`lib.pgfs`, `mkfs.pgfs`, `mount.pgfs`, `assign.pgfs`), so a Debug build can be launched directly as e.g. `./bin/Debug/mkfs.pgfs.exe`.

### Mount (Linux/macOS)

```pwsh
dotnet build src/mount/Mount.csproj
```

The build also works on Windows (for cross-compilation); execution is Linux/macOS only. See [docs/Mount.md](docs/Mount.md) for details. All FUSE operations are implemented, including data read/write, extended attributes, symbolic links, and hard links.

### Assign (Windows)

```pwsh
dotnet build src/assign/Assign.csproj
```

The build also works on Linux/macOS (for cross-compilation); execution is Windows only. The Dokan2 kernel driver must be installed beforehand. See [docs/Assign.md](docs/Assign.md) for details.

### Publishing self-contained executables

`dotnet publish` outputs **single-file self-contained** executables for the host OS under [bin/Publish/](bin/Publish/) (~38 MB each).

```pwsh
# individually
dotnet publish src/mkfs/Mkfs.csproj -c Release
dotnet publish src/mount/Mount.csproj -c Release
dotnet publish src/assign/Assign.csproj -c Release

# the whole solution (the 3 exes Mkfs/Mount/Assign end up in bin/Publish/)
dotnet publish pgfs.sln -c Release
```

The RID is **auto-detected from the host OS** via `UseCurrentRuntimeIdentifier=true`. To build for another OS, override with `-r <RID>` (e.g. `dotnet publish ... -c Release -r linux-x64`).

> AOT publish (`PublishAot=true`) does not work for now because Dapper / Tomlyn / Tmds.Fuse / DokanNet depend on reflection. All projects are set to `PublishAot=false` / `PublishTrimmed=false`.

## Usage

### 1. Prepare PostgreSQL

Start PostgreSQL 17 and make it connectable as a superuser (usually `postgres`).

### 2. Initialize PGFS

`mkfs.pgfs` creates the user / database / schema / tables / initial data that PGFS uses.

```bash
# initialize with defaults (localhost:5432 / postgres superuser / create a new pgfs user + DB)
dotnet run --project src/mkfs

# specify the superuser connection explicitly
dotnet run --project src/mkfs -- \
    --super-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=template1"

# specify the PGFS user connection explicitly
dotnet run --project src/mkfs -- \
    --connection "Host=localhost;Port=5432;Username=pgfs;Password=pgfs;Database=pgfs"

# change the schema / prefix
dotnet run --project src/mkfs -- -s myschema -x myfs_

# help
dotnet run --project src/mkfs -- --help
```

When done, `pgfs.toml` (the settings file) is written to the current directory. See [docs/Mkfs.md](docs/Mkfs.md) for details.

### 3. Mount

On Linux/macOS, mount with `mount.pgfs`.

```bash
# prepare the mount point
sudo mkdir -p /mnt/pgfs
sudo chown $USER /mnt/pgfs

# mount
dotnet run --project src/mount -- -m /mnt/pgfs

# in another terminal
ls -la /mnt/pgfs

# unmount
fusermount3 -u /mnt/pgfs
```

Data I/O, extended attributes, symbolic links, and hard links are all supported. See [docs/Mount.md](docs/Mount.md) for details.

On Windows, mount to a drive with `pgfs.assign` (the Dokan2 driver is required):

```pwsh
dotnet run --project src\assign -- -m P:
# in another terminal:
Get-ChildItem P:\
```

See [docs/Assign.md](docs/Assign.md) for details.

## Documentation

Each document has an English (`.md`) and a Japanese (`.ja.md`) version.

- [docs/next.md](docs/next.md) — **what's next** (in priority order, updated per piece of work)
- [docs/architecture.md](docs/architecture.md) — project layout / dependency packages / Lib internals / build & run
- [docs/Mkfs.md](docs/Mkfs.md) — the `mkfs.pgfs` specification
- [docs/Mount.md](docs/Mount.md) — the `mount.pgfs` specification (Linux/macOS)
- [docs/Assign.md](docs/Assign.md) — the `pgfs.assign` specification (Windows)
- [docs/database.md](docs/database.md) — the PostgreSQL schema design
- [docs/ddl/](docs/ddl/README.md) — the per-table DDL
- [docs/coding-style.md](docs/coding-style.md) — the C# coding conventions
- [docs/settings-matrix.md](docs/settings-matrix.md) — the settings matrix (CLI / TOML / DB / default / read timing)
- [docs/performance.md](docs/performance.md) — performance improvement candidates
- [docs/support_for_citus.md](docs/support_for_citus.md) — the design notes for Citus (horizontal distribution)
- [docs/history.md](docs/history.md) — the key design decisions behind the current design
- [docs/fstab-support.md](docs/fstab-support.md) — `/etc/fstab` support
- [docs/audit-log.md](docs/audit-log.md) — the audit log
- [docs/tests.md](docs/tests.md) — **the test hub** (the full catalog / how to run / environment requirements / docker integration). For each runner, see [tests/linux/](tests/linux/README.md) / [tests/windows/](tests/windows/README.md) / [tests/citus/](tests/citus/README.md)

## License

See [LICENSE](LICENSE) (the MIT license).
