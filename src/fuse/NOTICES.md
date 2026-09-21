# NOTICES — Pgfs.Fuse

The in-house libfuse P/Invoke binding under `src/fuse/src/` (`LibFuse.cs`,
`LibFuse.structs.cs`, `FuseMount.cs`, `Fuse.cs`, `IFuseFileSystem.cs`,
`FuseFileSystemBase.cs`, `FuseFileSystemStringBase.cs`, `FuseFileInfo.cs`,
`FuseFileInfoRef.cs`, `MountOptions.cs`, `DirectoryContent.cs`,
`ReadDirFlags.cs`, `TimespecExtensions.cs`, `FuseException.cs`, `IFuseMount.cs`)
is **derived from Tmds.Fuse (MIT License)** by Tom Deseyn, via the
actively-maintained fork **securefolderfs-community/Tmds.Fuse**.

pgfs internalized this binding in v0.2.0 — retiring the previous
`vendor/Tmds.Fuse` git submodule — and carries these downstream patches:

- write `fuse_config.use_ino = 1` in the FUSE `init` callback (hardlink `st_ino`
  identity),
- expose `fuse_get_context` (caller uid/gid/pid) for owner resolution + audit,
- arbitrary `-o` mount-option passthrough to libfuse.

References:
- Tmds.Fuse — https://github.com/tmds/Tmds.Fuse
- securefolderfs fork — https://github.com/securefolderfs-community/Tmds.Fuse

libfuse itself (LGPL-2.1) is **not** vendored: pgfs binds its C ABI at runtime
via `dlopen`/`dlvsym`, and the user supplies `libfuse3.so.3`.
