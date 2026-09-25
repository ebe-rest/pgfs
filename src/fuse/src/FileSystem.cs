namespace Pgfs.Fuse;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Core.Api;
using Core.Logging;
using Core.Models;
using Core.Utility;
using Pgfs.Fuse;
using Tmds.Linux;
using static Tmds.Linux.LibC;

/// <summary>
/// The PGFS FUSE filesystem implementation running on Pgfs.Fuse (Linux/macOS).
///
/// Cross-platform DB operations go through <see cref="Api"/>, and this class is purely a
/// "FUSE -&gt; Api" adapter.
/// Because it deals with Linux-specific APIs (mode_t, stat, statvfs, errno, uid_t/gid_t), this type
/// only works on Linux/macOS. The Windows counterpart lives in <c>Pgfs.Assign</c>.
///
/// Places that could be shared with Windows are marked with a `// Windows-shareable:` comment.
///
/// Data I/O (Open/Read/Write/Truncate, the <c>pgfs_data_chunk</c> bytea chunks), SymLink/Link/ReadLink,
/// extended attributes (xattr), and statfs are all implemented (verified by e2e).
/// Access permission checks (<c>Access</c>) are delegated to the kernel via <c>default_permissions</c> at mount time.
/// </summary>
public sealed class FileSystem : FuseFileSystemBase
{
	private readonly Api api;

	/// <summary>uid/gid &lt;-&gt; uname/gname resolver (Linux/macOS-specific).</summary>
	private readonly UserResolver users;

	/// <summary>uname/gname used for new inodes. Derived from the process's real user.</summary>
	private readonly string defaultUname;
	private readonly string defaultGname;

	public FileSystem(Api api) {
		this.api = api;
		// A name this host does not have is shown as overflowuid / gid, and an id without a name is written as file_system.unknown_name (fallback_* was removed in v0.2.1).
		this.processUid = (uint)getuid();
		this.users = new UserResolver(api.Config.FileSystem.UnknownName, api.Config.Mount.SelfUname, api.Config.Mount.SelfGname, this.processUid, (uint)getgid());
		this.selfUname = NameNormalizer.Normalize(api.Config.Mount.SelfUname ?? "");
		this.selfGname = NameNormalizer.Normalize(api.Config.Mount.SelfGname ?? "");
		var name = Environment.UserName;
		if (string.IsNullOrEmpty(name)) {
			name = "pgfs";
		}
		// When the caller cannot be obtained (a request from inside the kernel, for example), the owner is the process itself. Our own name (self_*) when set.
		this.defaultUname = NameNormalizer.Normalize(name);
		if (this.selfUname.Length > 0) {
			this.defaultUname = this.selfUname;
		}
		var processGid = (uint)getgid();
		this.defaultGname = this.users.GnameOf(processGid);
		if (this.selfGname.Length > 0) {
			this.defaultGname = this.selfGname;
		}
	}

	/// <summary>The uid of the process running the mount (used to decide whether to apply our own name, self_*).</summary>
	private readonly uint processUid;
	/// <summary>Our own name (<c>mount.self_uname</c>). Not used when empty.</summary>
	private readonly string selfUname;
	/// <summary>Our own group name (<c>mount.self_gname</c>). Not used when empty.</summary>
	private readonly string selfGname;

	/// <summary>The name for a uid. **For ourselves (the same uid as the process), self_uname when set**, otherwise the OS name (unknown_name if there is none).</summary>
	private string NameOfCaller(uint uid) {
		if (uid == this.processUid && this.selfUname.Length > 0) {
			return this.selfUname;
		}
		return this.users.UnameOf(uid);
	}

	/// <summary>The name for a gid. **For a request from ourselves (the same uid as the process), self_gname when set**, otherwise the OS name.</summary>
	private string GroupOfCaller(uint uid, uint gid) {
		if (uid == this.processUid && this.selfGname.Length > 0) {
			return this.selfGname;
		}
		return this.users.GnameOf(gid);
	}

	public override bool SupportsMultiThreading => true;

	// ------------------------------------------------------------------
	// Path / string utilities
	// ------------------------------------------------------------------

	/// <summary>
	/// Extracts a <see cref="mode_t"/> into a <see cref="uint"/>.
	///
	/// Note: Tmds.LibC 0.3.0's <c>mode_t.op_Explicit(mode_t -&gt; uint16)</c> has an IL-level bug where it
	/// calls itself infinitely (which crashes with a stack overflow). So <c>(int)mode</c> or
	/// <c>(ushort)mode</c> cannot be used.
	///
	/// mode_t is a single-field struct (<c>struct { private uint __value }</c>), so reinterpreting its bits
	/// with <see cref="Unsafe.As{TFrom,TTo}"/> extracts the value.
	/// </summary>
	private static uint ModeToUInt32(mode_t mode) {
		return Unsafe.As<mode_t, uint>(ref mode);
	}

	/// <summary>Converts the UTF-8 byte-span path FUSE passes in into a string.</summary>
	// Windows-shareable: the byte-span -> string conversion itself is OS-independent.
	private static string PathToString(ReadOnlySpan<byte> path) {
		// FUSE paths are always absolute and `/`-separated. The trailing NUL is not included.
		return Encoding.UTF8.GetString(path);
	}

	/// <summary>Splits a path into "the parent path + the last element".</summary>
	// Windows-shareable: splitting a path is OS-independent once the separator is a parameter. Fixed to `/` here.

	// ------------------------------------------------------------------
	// Resolving the handle context (handle-context stage B)
	// ------------------------------------------------------------------

	/// <summary>
	/// Looks the handle context up from <c>fi</c>. **Null when there is none on it.**
	/// <para>
	/// In stage B <c>fh</c> is "the id in the handle table" and is **the primary means of identification**
	/// (docs/design/handle-context.md, the acceptance criteria and API surface of stage B). The callbacks that
	/// arrive with a null <c>fi</c> (GetAttr / ChMod / Chown / Truncate / UpdateTimestamps) get null from here
	/// and the caller falls back to the path.
	/// </para>
	/// </summary>
	private OpenFileContext? ContextOf(FuseFileInfoRef fiRef) {
		if (fiRef.IsNull) { return null; }
		return this.api.Handles.Get(fiRef.Value.fh);
	}

	/// <summary>
	/// Gathers the contract of ⑤ - "**with <c>fh</c>, start from the ctx's <c>InodeId</c>; without <c>fh</c>, resolve path -> id once**" - into one place.
	/// <para>
	/// The one and only point at which the two see different things is **"a rename plus a re-create under the
	/// same name"**, and **it is right that they differ there** (the fd points at the inode as it was at open
	/// time, the path points at the new inode).
	/// </para>
	/// </summary>
	private Inode? ResolveByHandleOrPath(FuseFileInfoRef fiRef, string path) {
		var context = this.ContextOf(fiRef);
		if (context != null) { return this.api.TryResolveHandle(context); }
		return this.api.GetByPath(path);
	}

	// ------------------------------------------------------------------
	// uid/gid resolution (Linux-specific)
	// ------------------------------------------------------------------

	/// <summary>
	/// Resolves an inode's uname/gname to Linux uid/gid (via libc getpwnam/getgrnam).
	/// Falls back to the process's uid/gid when resolution fails.
	/// </summary>
	private (uint uid, uint gid) ResolveOwner(Inode inode) {
		return (this.users.UidOf(inode.UserName), this.users.GidOf(inode.GroupName));
	}

	/// <summary>
	/// Determines the uname/gname to use when creating a new inode, from the "caller" of that operation
	/// (the uid/gid from fuse_get_context). This makes the creating user the owner even under a root mount
	/// (fstab / sudo mount). Falls back to the process default (defaultUname/defaultGname) only when the
	/// caller cannot be obtained or resolved.
	/// </summary>
	// Windows-shareable: "make the creator the owner" is a concept shared by both OSes.
	//                    Only the fuse_get_context call is Linux-specific.
	private (string uname, string gname) CurrentUserNames() {
		if (Fuse.TryGetCallerContext(out var uid, out var gid, out _)) {
			// Normalize the stored name (docs/permission-interop.md), same rule as defaultUname.
			var uname = NameNormalizer.Normalize(this.NameOfCaller(uid));
			if (!string.IsNullOrEmpty(uname)) {
				return (uname, this.GroupOfCaller(uid, gid));
			}
		}
		return (this.defaultUname, this.defaultGname);
	}

	/// <summary>
	/// For the audit log, sets the caller (uid -&gt; uname) of the FUSE operation currently being handled into
	/// <see cref="AuditContext.Current"/>. Called at the start of each mutating callback. When auditing is
	/// disabled or the caller cannot be obtained, sets null (= the audit row's caller_* become NULL).
	/// Linux has no domain/workgroup concept, so Domain is always null.
	/// </summary>
	private void SetAuditContext() {
		if (!this.api.Config.Audit.Enabled) {
			AuditContext.Current = null;
			return;
		}
		if (!Fuse.TryGetCallerContext(out var uid, out _, out _)) {
			AuditContext.Current = null;
			return;
		}
		AuditContext.Current = new AuditContext {
			Uid = uid,
			Uname = this.NameOfCaller(uid),
			Domain = null,
		};
	}

	// ------------------------------------------------------------------
	// GetAttr
	// ------------------------------------------------------------------

	public override int GetAttr(ReadOnlySpan<byte> path, ref stat stat, FuseFileInfoRef fiRef) {
		var p = PathToString(path);
		try {
			// Stage B: if there is an fh it is the primary source (the contract of ⑤). Without one, resolve from the path as before.
			var inode = this.ResolveByHandleOrPath(fiRef, p);
			if (inode == null) {
				return -ENOENT;
			}
			this.FillStat(inode, ref stat);
			return 0;
		} catch (Exception ex) {
			Logger.Error("GetAttr failed: ", p, " ", ex);
			return -EIO;
		}
	}

	/// <summary>Fills a POSIX stat struct from an inode. Linux-specific.</summary>
	private void FillStat(Inode inode, ref stat s) {
		var (uid, gid) = this.ResolveOwner(inode);
		// Each wrapper struct of Tmds.Linux has a fixed type it casts implicitly from (mode_t is ushort,
		// uid/gid_t are uint, nlink_t/ino_t/fs*cnt_t are ulong, off_t/blk* are long).
		// An assignment is converted explicitly to that source type before being handed over.
		// POSIX wants st_ino to agree across hardlinks, but the pgfs schema keeps the inode rows under separate
		// ids, so when the data body is shared (DataId != null) the same value is derived from the data_id.
		// Directories and the like (DataId == null) use inode.Id as-is. To keep inode.Id and data_id from
		// colliding, the file side gets the sign bit (0x8000_0000_0000_0000) set, which splits the two namespaces.
		// For the kernel to use this value, Mount/Program.cs has to pass `-o use_ino`.
		// The default is the directory path (inode.Id as-is). For a file, the sign bit is set on the data_id.
		var inoValue = 0UL;
		if (inode.Id >= 0) { inoValue = (ulong)inode.Id; }
		if (inode.DataId is long dataId && dataId >= 0) {
			inoValue = (ulong)dataId | 0x8000_0000_0000_0000UL;
		}
		s.st_ino = inoValue;
		s.st_mode = (ushort)(inode.Mode & 0xFFFF);
		s.st_nlink = (ulong)Math.Max(1, inode.NLink);
		s.st_uid = uid;
		s.st_gid = gid;
		// A directory's st_size stays 0 (Size stays at the DB default 0). It is not set to the entry count or 4096:
		// no standard tool interprets st_size as a count (du reads st_blocks); computing COUNT on read would trash the
		// InodeCache with a DB round-trip per getattr, and maintaining it on write would intrude on every mutating op.
		// Not worth it for cosmetics, so it stays 0. See docs/database.md (pgfs_inode note).
		s.st_size = inode.Size;
		// st_blocks is "the number of blocks actually occupied" = the value du looks at. Deriving it from st_size
		// would report a thousand times the real thing for a sparse file (which has no chunk rows for its holes),
		// so it comes from {prefix}data.total_size (the actually occupied bytes). The details are in docs/design/database.md.
		s.st_blocks = (this.api.GetOccupiedBytes(inode) + 511) / 512;
		s.st_blksize = 4096L;
		var mtime = (inode.Mtime == default) switch {
			true  => DateTime.UtcNow,
			false => DateTime.SpecifyKind(inode.Mtime, DateTimeKind.Utc),
		};
		var ctime = (inode.Ctime == default) switch {
			true  => mtime,
			false => DateTime.SpecifyKind(inode.Ctime, DateTimeKind.Utc),
		};
		s.st_mtim = mtime.ToTimespec();
		s.st_ctim = ctime.ToTimespec();
		// Per docs/database.md, there is no st_atime, so return mtime.
		s.st_atim = mtime.ToTimespec();
	}

	// ------------------------------------------------------------------
	// ReadDir / OpenDir / ReleaseDir
	// ------------------------------------------------------------------

	public override int OpenDir(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		var p = PathToString(path);
		var inode = this.api.GetByPath(p);
		if (inode == null) {
			return -ENOENT;
		}
		if (!inode.IsDirectory) {
			return -ENOTDIR;
		}
		// **Borrow an id from the handle table and put it on fh** (handle-context stage A).
		// It used to carry the raw inode id, but **the root inode has id = 0**, so it could not be told apart from
		// "nothing on it" and FSyncDir's reverse lookup was dead at the root.
		// The handle table's ids start at 1 and 0 is the sentinel, so that trap is gone.
		// **Whatever is borrowed must be returned in ReleaseDir** (otherwise the OpenFileContext leaks).
		// **Stage C-1: raise the body's reference count separately from the registration in the handle table.**
		// Do not fold it into `Rent` / `Return` (Dokan does not go through the table, and it also builds throwaway contexts).
		var context = new OpenFileContext(inode, null);
		this.api.OpenHandle(context);
		fi.fh = this.api.Handles.Rent(context);
		return 0;
	}

	public override int ReadDir(ReadOnlySpan<byte> path, ulong offset, ReadDirFlags flags, DirectoryContent content, ref FuseFileInfo fi) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (!inode.IsDirectory) {
				return -ENOTDIR;
			}
			content.AddEntry(".");
			content.AddEntry("..");
			foreach (var child in this.api.ListChildren(inode.Id)) {
				content.AddEntry(child.Name);
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("ReadDir failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int ReleaseDir(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		// Return the handle borrowed in OpenDir (leaving it in the table leaks the OpenFileContext).
		// **Stage C-1: drop the reference count here as well. The `Release` family is the only release point.**
		var context = this.api.Handles.Return(fi.fh);
		if (context != null) { this.api.CloseHandle(context); }
		return 0;
	}

	/// <summary>
	/// <c>fsync(2)</c> on a directory fd. With metadata write-back this is the only synchronization point that
	/// materializes "the pending children directly under this directory plus its own pending ancestors", and
	/// without it the standard idiom of "fsync the file, then fsync the parent dir" does not hold.
	/// <para>
	/// Leaving it to the base -ENOSYS makes **the kernel learn "no fsyncdir is needed from now on" and never
	/// call it again** (turning it silently into a no-op), so the override itself has been in place since the write-through days.
	/// </para>
	/// <para>
	/// **When the path cannot be resolved, it is looked up in reverse from the handle borrowed in
	/// <see cref="OpenDir"/> (<c>fi.fh</c>)**: if another client renames this directory, the state becomes "the
	/// directory and its pending children are alive but the old path no longer resolves", and looking only at
	/// the path makes **fsyncdir return success without flushing a single pending child** (fail-open). It is
	/// the same shape of hole that was closed in <see cref="FlushPath"/>.
	/// </para>
	/// </summary>
	public override int FSyncDir(ReadOnlySpan<byte> path, bool onlyData, ref FuseFileInfo fi) {
		var p = PathToString(path);
		try {
			// **Stage B: the handle borrowed in OpenDir became primary and the path secondary.**
			// The ctx version is TryResolveHandle, so when it is gone this is a no-op that returns 0
			// (the pending work was discarded by the deleting side = "there is nothing to write", not an error).
			var context = this.api.Handles.Get(fi.fh);
			if (context != null) {
				this.api.FlushDirectory(context);
				return 0;
			}
			// Only the path with no fh on it falls back to the path.
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return 0;
			}
			this.api.FlushDirectory(inode);
			return 0;
		} catch (Exception ex) {
			Logger.Error("FSyncDir failed: ", p, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// MkDir / RmDir
	// ------------------------------------------------------------------

	public override int MkDir(ReadOnlySpan<byte> path, mode_t mode) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			var (parentPath, name) = Pgfs.Core.Utility.PathParser.SplitParent(p);
			if (string.IsNullOrEmpty(name)) {
				return -EINVAL;
			}
			var parent = this.api.GetByPath(parentPath);
			if (parent == null) {
				return -ENOENT;
			}
			if (!parent.IsDirectory) {
				return -ENOTDIR;
			}
			// Duplicate check (Api.CreateDirectory also does ON CONFLICT DO NOTHING, but detect it earlier here).
			if (this.api.GetByPath(p) != null) {
				return -EEXIST;
			}

			var (uname, gname) = this.CurrentUserNames();
			var inode = this.api.CreateDirectory(parent.Id, name, uname, gname, (int)ModeToUInt32(mode));
			if (inode == null) {
				return -EEXIST;
			}
			// Cache it under the full path so the immediately following getattr does not hit the DB.
			this.api.InodeCache.Put(inode, p);
			return 0;
		} catch (Pgfs.Core.Api.Api.ParentVanishedException) {
			// The parent was deleted by another client just before the creation. This is neither EEXIST (= a null return) nor EIO.
			return -ENOENT;
		} catch (Exception ex) {
			Logger.Error("MkDir failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int RmDir(ReadOnlySpan<byte> path) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (!inode.IsDirectory) {
				return -ENOTDIR;
			}
			if (!this.api.IsDirectoryEmpty(inode.Id)) {
				return -ENOTEMPTY;
			}
			if (!this.api.DeleteInode(inode)) {
				return -EIO;
			}
			this.api.InodeCache.Invalidate(inode.Id, p);
			return 0;
		} catch (Exception ex) {
			Logger.Error("RmDir failed: ", p, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// Create / Unlink
	// ------------------------------------------------------------------

	public override int Create(ReadOnlySpan<byte> path, mode_t mode, ref FuseFileInfo fi) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			var (parentPath, name) = Pgfs.Core.Utility.PathParser.SplitParent(p);
			if (string.IsNullOrEmpty(name)) {
				return -EINVAL;
			}
			var parent = this.api.GetByPath(parentPath);
			if (parent == null) {
				return -ENOENT;
			}
			if (!parent.IsDirectory) {
				return -ENOTDIR;
			}
			if (this.api.GetByPath(p) != null) {
				return -EEXIST;
			}

			var (uname, gname) = this.CurrentUserNames();
			// O_EXCL is a **locking primitive**, as in `git index.lock`. Even with metadata write-back it is created
			// synchronously rather than deferred, and the exclusion decision is left to the database's unique
			// constraint (synchronization heuristic c).
			// O_EXCL is octal 0200 = 0x80 across Linux.
			const int O_EXCL = 0x80;
			var exclusive = (fi.flags & O_EXCL) != 0;
			Pgfs.Core.Models.Inode? inode;
			try {
				inode = this.api.CreateFile(parent.Id, name, uname, gname, (int)ModeToUInt32(mode), exclusive);
			} catch (Pgfs.Core.Api.Api.ParentVanishedException) {
				// The parent was deleted by another client just before the creation. It means something different from EEXIST (= a null return).
				return -ENOENT;
			}
			if (inode == null) {
				return -EEXIST;
			}
			this.api.InodeCache.Put(inode, p);
			// As in Open, borrow an id from the handle table and put it on (handle-context stage A).
			// **Stage C-1: raise the body's reference count separately from the registration in the handle table.**
			// Do not fold it into `Rent` / `Return` (Dokan does not go through the table, and it also builds throwaway contexts).
			var context = new OpenFileContext(inode, null);
			this.api.OpenHandle(context);
			fi.fh = this.api.Handles.Rent(context);
			return 0;
		} catch (Exception ex) {
			Logger.Error("Create failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int Unlink(ReadOnlySpan<byte> path) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (inode.IsDirectory) {
				return -EISDIR;
			}
			if (!this.api.DeleteInode(inode)) {
				return -EIO;
			}
			this.api.InodeCache.Invalidate(inode.Id, p);
			return 0;
		} catch (Exception ex) {
			Logger.Error("Unlink failed: ", p, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// Rename
	// ------------------------------------------------------------------

	public override int Rename(ReadOnlySpan<byte> path, ReadOnlySpan<byte> newPath, int flags) {
		this.SetAuditContext();
		var oldP = PathToString(path);
		var newP = PathToString(newPath);
		// The only flag supported is RENAME_NOREPLACE. Silently ignoring RENAME_EXCHANGE (2) / RENAME_WHITEOUT (4)
		// or an unknown flag would commit an exchange request as "delete the target + move the source" in a single
		// tx, and the target would be lost irreversibly. Unsupported flags are refused with -EINVAL.
		const int RENAME_NOREPLACE = 1;
		if ((flags & ~RENAME_NOREPLACE) != 0) {
			Logger.Warning("Rename: unsupported flags 0x", flags.ToString("x"), " ", oldP, " -> ", newP);
			return -EINVAL;
		}
		try {
			var inode = this.api.GetByPath(oldP);
			if (inode == null) {
				return -ENOENT;
			}
			var (newParentPath, newName) = Pgfs.Core.Utility.PathParser.SplitParent(newP);
			if (string.IsNullOrEmpty(newName)) {
				return -EINVAL;
			}
			var newParent = this.api.GetByPath(newParentPath);
			if (newParent == null) {
				return -ENOENT;
			}
			if (!newParent.IsDirectory) {
				return -ENOTDIR;
			}
			// Check for an existing target.
			var existing = this.api.GetByPath(newP);
			if (existing != null) {
				// Replacement is forbidden when the RENAME_NOREPLACE flag (Linux-specific, 1) is set
				if ((flags & RENAME_NOREPLACE) != 0) {
					return -EEXIST;
				}
				// The two must be of the same kind.
				if (existing.IsDirectory != inode.IsDirectory) {
					return existing.IsDirectory switch {
						true  => -EISDIR,
						false => -ENOTDIR,
					};
				}
				// An existing directory target must be empty.
				if (existing.IsDirectory && !this.api.IsDirectoryEmpty(existing.Id)) {
					return -ENOTEMPTY;
				}
				// Persist the source's unflushed work before the replacement erases the old target
				// (replacing while still holding write-back's dirty data would lose both the old and the new one on a crash).
				// A failure throws -> the catch below turns it into -EIO. The target is left untouched.
				// **A pending source is not flushed here** - the Api side does "delete the target + materialize + dirty
				// data + audit" in a single tx (synchronization heuristic a).
				this.api.PrepareRenameReplace(inode);
			}

			// Deleting what is being replaced and the rename happen in the same tx on the Api side (deleting first in a separate tx would open a crash window)
			if (!this.api.Rename(inode.Id, newParent.Id, newName, existing)) {
				return -EIO;
			}
			this.api.InodeCache.InvalidatePrefix(oldP);
			this.api.InodeCache.Invalidate(inode.Id, newP);
			return 0;
		} catch (Exception ex) {
			Logger.Error("Rename failed: ", oldP, " -> ", newP, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// ChMod / Chown / Truncate / UpdateTimestamps
	// ------------------------------------------------------------------

	public override int ChMod(ReadOnlySpan<byte> path, mode_t mode, FuseFileInfoRef fiRef) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			// Stage B: if there is an fh it is the primary source (the contract of ⑤).
			var inode = this.ResolveByHandleOrPath(fiRef, p);
			if (inode == null) {
				return -ENOENT;
			}
			// Keep the file-type bits and replace only the permission bits.
			var newMode = (inode.Mode & ~0xFFF) | ((int)ModeToUInt32(mode) & 0xFFF);
			if (!this.api.UpdateMode(inode.Id, newMode)) {
				return -EIO;
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("ChMod failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int Chown(ReadOnlySpan<byte> path, uint uid, uint gid, FuseFileInfoRef fiRef) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			// Stage B: if there is an fh it is the primary source (the contract of ⑤).
			var inode = this.ResolveByHandleOrPath(fiRef, p);
			if (inode == null) {
				return -ENOENT;
			}
			// Resolve the uid/gid to names and write them back to the database.
			// uid == 0xFFFFFFFF (-1) is the Linux convention for "do not change".
			var newUname = (uid == uint.MaxValue) switch {
				true  => inode.UserName,
				false => this.users.UnameOf(uid),
			};
			var newGname = (gid == uint.MaxValue) switch {
				true  => inode.GroupName,
				false => this.users.GnameOf(gid),
			};
			if (!this.api.UpdateOwner(inode.Id, newUname, newGname)) {
				return -EIO;
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("Chown failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int Truncate(ReadOnlySpan<byte> path, ulong length, FuseFileInfoRef fiRef) {
		var p = PathToString(path);
		try {
			// Stage B: with an fh, go to the ctx version. **If it is gone, -ESTALE** (thrown by ResolveHandle).
			var context = this.ContextOf(fiRef);
			if (context != null) {
				if (this.api.ResolveHandle(context).IsDirectory) {
					return -EISDIR;
				}
				if (!this.api.TruncateData(context, (long)length)) {
					return -EIO;
				}
				return 0;
			}
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (inode.IsDirectory) {
				return -EISDIR;
			}
			// Truncate the data body (chunks) as well.
			if (!this.api.TruncateData(inode, (long)length)) {
				return -EIO;
			}
			return 0;
		} catch (Api.StaleHandleException ex) {
			Logger.Warning("Truncate: the inode the handle points at, ", ex.InodeId, ", is already gone: ", p);
			return -ESTALE;
		} catch (Exception ex) {
			Logger.Error("Truncate failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int UpdateTimestamps(ReadOnlySpan<byte> path, ref timespec atime, ref timespec mtime, FuseFileInfoRef fiRef) {
		var p = PathToString(path);
		try {
			// Stage B: if there is an fh it is the primary source (the contract of ⑤).
			var inode = this.ResolveByHandleOrPath(fiRef, p);
			if (inode == null) {
				return -ENOENT;
			}
			// Handle the special utimensat values:
			//   UTIME_OMIT (tv_nsec = 1073741822): do not change.
			//   UTIME_NOW  (tv_nsec = 1073741823): use the current time.
			// Passing these to ToDateTime() throws InvalidOperationException, so always check them first.
			// Requirement: there is no st_atime, so atime is ignored; only mtime is written to the DB.
			if (mtime.IsOmit()) {
				return 0; // Neither mtime nor atime changes, so do nothing.
			}
			var newMtime = mtime.IsNow() switch {
				true  => DateTime.UtcNow,
				false => mtime.ToDateTime(),
			};
			if (!this.api.UpdateTimestamps(inode.Id, newMtime)) {
				return -EIO;
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("UpdateTimestamps failed: ", p, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// Open / Release / Read / Write
	// ------------------------------------------------------------------

	public override int Open(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		var p = PathToString(path);
		var inode = this.api.GetByPath(p);
		if (inode == null) {
			return -ENOENT;
		}
		if (inode.IsDirectory) {
			return -EISDIR;
		}
		// FUSE normally sends O_TRUNC as a separate syscall (truncate), but with the `atomic_o_trunc` mount
		// option enabled, or depending on the `cp` implementation, O_TRUNC can be passed directly to Open.
		// As a safeguard, handle it on the Open side too.
		// O_TRUNC is octal 01000 = 0x200 on Linux.
		const int O_TRUNC = 0x200;
		if ((fi.flags & O_TRUNC) != 0 && inode.Size > 0) {
			if (Logger.IsTraceEnabled) { Logger.Trace("Open with O_TRUNC: truncating ", p, " (id:", inode.Id, " size:", inode.Size, ")"); }
			if (!this.api.TruncateData(inode, 0)) {
				return -EIO;
			}
		}
		// **Borrow an id from the handle table and put it on fh** (handle-context stage A).
		// Flush / FSync / Release look the context up by this id. **Whatever is borrowed must be returned in Release.**
		// **Stage C-1: raise the body's reference count separately from the registration in the handle table.**
		// Do not fold it into `Rent` / `Return` (Dokan does not go through the table, and it also builds throwaway contexts).
		var context = new OpenFileContext(inode, null);
		this.api.OpenHandle(context);
		fi.fh = this.api.Handles.Rent(context);
		return 0;
	}

	/// <summary>
	/// Called on every <c>close(2)</c> (several times when the fd has been duplicated).
	/// <para>
	/// When <c>write_back_metadata</c> is **disabled**, the unflushed work is committed synchronously here, as
	/// it is with data write-back alone (the base implementation returns <c>-ENOSYS</c>, and the kernel
	/// **treats an ENOSYS from fsync as success** and remembers "no_fsync from now on", so removing the
	/// override makes <c>fsync(2)</c> lie silently).
	/// </para>
	/// <para>
	/// When <c>write_back_metadata</c> is **enabled**, **close does not flush synchronously** (decision 2 =
	/// close-no-flush). The decision is concentrated in <see cref="Pgfs.Core.Api.Api.CloseInode"/>, which
	/// degrades to a synchronous flush plus an <c>-EIO</c> report only on an error latch, an already truncated
	/// file, or an error state.
	/// </para>
	/// </summary>
	public override int Flush(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		return this.FlushPath(path, fi.fh, "Flush", onClose: true);
	}

	/// <summary>
	/// <c>fsync(2)</c>. pgfs does not distinguish <paramref name="onlyData"/> (everything is written in the same tx).
	/// **With metadata write-back close is loosened, so this and <see cref="FSyncDir"/> are the only hard barriers.**
	/// </summary>
	public override int FSync(ReadOnlySpan<byte> path, bool onlyData, ref FuseFileInfo fi) {
		return this.FlushPath(path, fi.fh, "FSync", onClose: false);
	}

	/// <summary>
	/// Writes out write-back's unflushed work. On a failure, <c>-EIO</c> (because it cannot be returned at write time).
	/// When <paramref name="onClose"/> is true (close / release), the close-no-flush decision is left to the Api.
	/// </summary>
	private int FlushPath(ReadOnlySpan<byte> path, ulong fh, string op, bool onClose) {
		var p = PathToString(path);
		try {
			// **Stage B: the handle became primary and the path secondary.**
			// The ctx version is TryResolveHandle (a no-op when it is gone), so stage A's behaviour of "return 0 when
			// it really is gone" is preserved as-is. Aligning this with ResolveHandle would regress into
			// **every close of an unlinked file returning -EIO**.
			var context = this.api.Handles.Get(fh);
			if (context != null) {
				if (onClose) {
					this.api.CloseInode(context);
					return 0;
				}
				this.api.FlushInode(context);
				return 0;
			}
			// Only the paths with no fh on them (an environment without Flush, or one already Returned) fall back to the path.
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return 0; // It really is gone (unlinked and so on) = the dirty data was discarded by the deleting side
			}
			if (onClose) {
				this.api.CloseInode(inode);
				return 0;
			}
			this.api.FlushInode(inode);
			return 0;
		} catch (Exception ex) {
			Logger.Error(op, " failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override void Release(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		// Flush is called on every close, so normally nothing is left here. This is a best-effort safety net for
		// what slips through (an environment without Flush, or after an error). The return value is ignored by the
		// kernel, so it is swallowed.
		// It is triggered by a close, so it is treated like Flush (with metadata write-back it only marks, as a rule).
		this.FlushPath(path, fi.fh, "Release", onClose: true);
		// **The handle borrowed in Open / Create is returned here.** Flush is called once per duplicated fd, but
		// Release is only called on the last close, so this is where it has to be returned.
		// **Stage C-1's reference count is dropped here for the same reason** - dropping it in `Flush` would give
		// the count back **the moment bash's `exec 9< file` closes the intermediate fd**
		// (measured in the leak regression of §⑥).
		var context = this.api.Handles.Return(fi.fh);
		if (context != null) { this.api.CloseHandle(context); }
	}

	public override int Read(ReadOnlySpan<byte> path, ulong offset, Span<byte> buffer, ref FuseFileInfo fi) {
		try {
			// **Stage B: fh is the primary means of identification**. libfuse always passes fi to Read / Write, so the
			// path is only consulted here in the abnormal case where the handle has vanished from the table (the
			// fallback below).
			// PathToString (a UTF-8 decode plus a string allocation) disappearing from the hot path is a by-product of
			// this inversion (docs/design/performance.md, improvement candidate 5).
			var context = this.api.Handles.Get(fi.fh);
			if (context != null) {
				return this.api.ReadData(context, (long)offset, buffer);
			}
			return this.ReadByPath(path, offset, buffer);
		} catch (Api.StaleHandleException ex) {
			// **-ESTALE rather than -EIO**. The handle has been left dangling by a rename plus a re-create under the
			// same name, or by another client's unlink, and unless the kind points at the cause the reproduction test
			// cannot be read.
			Logger.Warning("Read: the inode the handle points at, ", ex.InodeId, ", is already gone");
			return -ESTALE;
		} catch (Exception ex) {
			Logger.Error("Read failed: ", PathToString(path), " ", ex);
			return -EIO;
		}
	}

	/// <summary>
	/// The safety net taken only when <c>fh</c> could not be resolved in the handle table. **It falls back to
	/// stage A's behaviour (starting from the path)**, so coming through here opens the window in which "an
	/// open fd lands on a different file". If it happens, investigate the cause.
	/// </summary>
	private int ReadByPath(ReadOnlySpan<byte> path, ulong offset, Span<byte> buffer) {
		var p = PathToString(path);
		Logger.Warning("Read: fh is not in the handle table, falling back to the path: ", p);
		var inode = this.api.GetByPath(p);
		if (inode == null) {
			return -ENOENT;
		}
		if (inode.IsDirectory) {
			return -EISDIR;
		}
		return this.api.ReadData(inode, (long)offset, buffer);
	}

	public override int Write(ReadOnlySpan<byte> path, ulong off, ReadOnlySpan<byte> span, ref FuseFileInfo fi) {
		try {
			// **Stage B: fh is the primary means of identification** (the same as Read; the reasoning is in that comment).
			var context = this.api.Handles.Get(fi.fh);
			if (context != null) {
				// **With O_APPEND the off that came down is thrown away** (docs/Mount.md, the append contract).
				// On Linux **the kernel decides** the write offset for `O_APPEND` (`generic_write_checks` reads
				// `i_size_read(inode)`), but that `i_size` **knows nothing about another mount's appends**, so using it
				// as-is tramples the other side's bytes (measured: 120 bytes lost).
				// **Core decides where the end is** - during write-back the dirty size is authoritative, so `Inode.Size`
				// must not be consulted here (in the adapter).
				if (IsAppend(fi.flags)) { return this.api.AppendData(context, span); }
				return this.api.WriteData(context, (long)off, span);
			}
			return this.WriteByPath(path, off, span, IsAppend(fi.flags));
		} catch (Api.StaleHandleException ex) {
			Logger.Warning("Write: the inode the handle points at, ", ex.InodeId, ", is already gone");
			return -ESTALE;
		} catch (Exception ex) {
			Logger.Error("Write failed: ", PathToString(path), " ", ex);
			return -EIO;
		}
	}

	/// <summary>
	/// Whether this is a write from a handle opened with <c>O_APPEND</c>.
	/// <para>**`O_APPEND` is octal 02000 = 0x400 across Linux**. `fi.flags` is filled in not only by `Open` but
	/// **in the `Write` callback as well** (measured: the handle of a `>>` is `0x8401`, a non-append one is
	/// `0x8001`). That is why deciding whether this is an append needs no state from `OpenFileContext`.</para>
	/// </summary>
	private static bool IsAppend(int flags) {
		const int O_APPEND = 0x400;
		return (flags & O_APPEND) != 0;
	}

	/// <summary>The write version of <see cref="ReadByPath"/>. **Coming through here means it has fallen back to stage A's behaviour.**</summary>
	private int WriteByPath(ReadOnlySpan<byte> path, ulong off, ReadOnlySpan<byte> span, bool append) {
		var p = PathToString(path);
		Logger.Warning("Write: fh is not in the handle table, falling back to the path: ", p);
		var inode = this.api.GetByPath(p);
		if (inode == null) {
			return -ENOENT;
		}
		if (inode.IsDirectory) {
			return -EISDIR;
		}
		// **The meaning of the append is preserved** even without a handle (a throwaway context goes through the same core in Core).
		if (append) {
			return this.api.AppendData(new OpenFileContext(inode, null), span);
		}
		return this.api.WriteData(inode, (long)off, span);
	}

	// ------------------------------------------------------------------
	// StatFS
	// ------------------------------------------------------------------

	public override int StatFS(ReadOnlySpan<byte> path, ref statvfs statfs) {
		try {
			// df's capacity/free use the real measurement if the server-side statfs() (plperlu) exists, else the nominal capacity (docs/df-support.md).
			var (capacity, free) = this.api.GetStatFs();

			var blockSize = (uint)4096;
			// f_bsize / f_frsize / f_namemax are ulong_t; implicit cast accepts uint32 only.
			statfs.f_bsize = blockSize;
			statfs.f_frsize = blockSize;
			statfs.f_blocks = (ulong)capacity / blockSize;
			statfs.f_bfree = (ulong)free / blockSize;
			statfs.f_bavail = statfs.f_bfree;
			statfs.f_files = (ulong)1_000_000;
			statfs.f_ffree = (ulong)1_000_000;
			statfs.f_favail = statfs.f_ffree;
			statfs.f_namemax = 255u;
			return 0;
		} catch (Exception ex) {
			Logger.Error("StatFS failed: ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// Extended attributes (xattr)
	// ------------------------------------------------------------------

	public override int GetXAttr(ReadOnlySpan<byte> path, ReadOnlySpan<byte> name, Span<byte> data) {
		var p = PathToString(path);
		var n = Encoding.UTF8.GetString(name);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			// POSIX ACL (system.posix_acl_access) is synthesized from st_mode + the canonical ACL.
			// A "minimal ACL" with no named entries returns ENODATA, matching the convention that getfacl derives it from mode.
			if (n == PosixAclAccessName) {
				var acl = this.LoadPgfsAcl(inode.Id);
				if (acl == null || acl.Entries.Count == 0) {
					return -61;
				}
				var blob = this.BuildPosixAccessAcl(inode, acl);
				if (data.Length == 0) {
					return blob.Length;
				}
				if (blob.Length > data.Length) {
					return -34;
				}
				blob.AsSpan().CopyTo(data);
				return blob.Length;
			}
			var value = this.api.GetXAttr(inode.Id, n);
			if (value == null) {
				// ENODATA / ENOATTR is 61 on Linux.
				return -61;
			}
			// When data.Length == 0: return only the required buffer size (FUSE convention).
			if (data.Length == 0) {
				return value.Length;
			}
			if (value.Length > data.Length) {
				return -34; // ERANGE
			}
			value.AsSpan().CopyTo(data);
			return value.Length;
		} catch (Exception ex) {
			Logger.Error("GetXAttr failed: ", p, " ", n, " ", ex);
			return -EIO;
		}
	}

	public override int SetXAttr(ReadOnlySpan<byte> path, ReadOnlySpan<byte> name, ReadOnlySpan<byte> data, int flags) {
		var p = PathToString(path);
		var n = Encoding.UTF8.GetString(name);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			// POSIX ACL (system.posix_acl_access) is reverse-projected into st_mode base 3 classes + canonical ACL named entries.
			if (n == PosixAclAccessName) {
				return this.SetPosixAccessAcl(inode, data);
			}
			// XATTR_CREATE = 1, XATTR_REPLACE = 2 (Linux-specific constants).
			const int XATTR_CREATE = 1;
			const int XATTR_REPLACE = 2;
			var createOnly = (flags & XATTR_CREATE) != 0;
			var replaceOnly = (flags & XATTR_REPLACE) != 0;
			if (!this.api.SetXAttr(inode.Id, n, data, createOnly, replaceOnly)) {
				if (createOnly) {
					return -EEXIST;
				}
				if (replaceOnly) {
					return -61; // ENODATA
				}
				return -EIO;
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("SetXAttr failed: ", p, " ", n, " ", ex);
			return -EIO;
		}
	}

	public override int ListXAttr(ReadOnlySpan<byte> path, Span<byte> list) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			var names = this.api.ListXAttr(inode.Id);

			// FUSE format: concatenated NUL-terminated strings.
			var needed = 0;
			foreach (var n in names) {
				needed += Encoding.UTF8.GetByteCount(n) + 1;
			}
			if (list.Length == 0) {
				return needed;
			}
			if (needed > list.Length) {
				return -34; // ERANGE
			}
			var pos = 0;
			foreach (var n in names) {
				var bytes = Encoding.UTF8.GetBytes(n);
				bytes.AsSpan().CopyTo(list.Slice(pos, bytes.Length));
				list[pos + bytes.Length] = 0;
				pos += bytes.Length + 1;
			}
			return needed;
		} catch (Exception ex) {
			Logger.Error("ListXAttr failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int RemoveXAttr(ReadOnlySpan<byte> path, ReadOnlySpan<byte> name) {
		var p = PathToString(path);
		var n = Encoding.UTF8.GetString(name);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			// Removing the extended ACL (setfacl -b) = clear the named entries of the canonical ACL (keep the mode).
			if (n == PosixAclAccessName) {
				this.api.SetXAttr(inode.Id, PgfsAclXattrKey, new PgfsAcl().ToBytes(), false, false);
				return 0;
			}
			if (!this.api.RemoveXAttr(inode.Id, n)) {
				return -61; // ENODATA
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("RemoveXAttr failed: ", p, " ", n, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// POSIX ACL <-> canonical ACL (docs/permission-interop.md)
	// ------------------------------------------------------------------

	private const string PosixAclAccessName = "system.posix_acl_access";
	private const string PgfsAclXattrKey = "user.pgfs_acl";

	private PgfsAcl? LoadPgfsAcl(long inodeId) {
		var raw = this.api.GetXAttr(inodeId, PgfsAclXattrKey);
		if (raw == null) {
			return null;
		}
		return PgfsAcl.Parse(raw);
	}

	/// <summary>Synthesizes the POSIX ACL binary from st_mode base 3 classes + the canonical ACL named entries (named expected). Order: USER_OBJ->USER*->GROUP_OBJ->GROUP*->MASK->OTHER.</summary>
	private byte[] BuildPosixAccessAcl(Inode inode, PgfsAcl acl) {
		var mode = inode.Mode;
		var entries = new List<PosixAclEntry>();
		entries.Add(new PosixAclEntry { Tag = PosixAcl.TagUserObj, Id = PosixAcl.UndefinedId,
			Perm = PosixAcl.PermFromRwx((mode & Mode.S_IRUSR) != 0, (mode & Mode.S_IWUSR) != 0, (mode & Mode.S_IXUSR) != 0) });
		foreach (var e in acl.Entries) {
			if (e.PrincipalType == "user") {
				entries.Add(new PosixAclEntry { Tag = PosixAcl.TagUser, Id = this.users.UidOf(e.PrincipalName),
					Perm = PosixAcl.PermFromRwx(e.CanRead, e.CanWrite, e.CanExecute) });
			}
		}
		entries.Add(new PosixAclEntry { Tag = PosixAcl.TagGroupObj, Id = PosixAcl.UndefinedId,
			Perm = PosixAcl.PermFromRwx((mode & Mode.S_IRGRP) != 0, (mode & Mode.S_IWGRP) != 0, (mode & Mode.S_IXGRP) != 0) });
		foreach (var e in acl.Entries) {
			if (e.PrincipalType == "group") {
				entries.Add(new PosixAclEntry { Tag = PosixAcl.TagGroup, Id = this.users.GidOf(e.PrincipalName),
					Perm = PosixAcl.PermFromRwx(e.CanRead, e.CanWrite, e.CanExecute) });
			}
		}
		// mask = group_obj union of all named perms.
		ushort mask = PosixAcl.PermFromRwx((mode & Mode.S_IRGRP) != 0, (mode & Mode.S_IWGRP) != 0, (mode & Mode.S_IXGRP) != 0);
		foreach (var e in acl.Entries) {
			mask |= PosixAcl.PermFromRwx(e.CanRead, e.CanWrite, e.CanExecute);
		}
		entries.Add(new PosixAclEntry { Tag = PosixAcl.TagMask, Id = PosixAcl.UndefinedId, Perm = mask });
		entries.Add(new PosixAclEntry { Tag = PosixAcl.TagOther, Id = PosixAcl.UndefinedId,
			Perm = PosixAcl.PermFromRwx((mode & Mode.S_IROTH) != 0, (mode & Mode.S_IWOTH) != 0, (mode & Mode.S_IXOTH) != 0) });
		return PosixAcl.Build(entries);
	}

	/// <summary>Reverse-projects the POSIX ACL binary into st_mode base 3 classes + canonical ACL named entries. The mask is not stored; it is recomputed on read.</summary>
	private int SetPosixAccessAcl(Inode inode, ReadOnlySpan<byte> data) {
		var entries = PosixAcl.Parse(data);
		if (entries == null) {
			return -22; // EINVAL
		}
		var ownerBits = 0;
		var groupBits = 0;
		var otherBits = 0;
		var acl = new PgfsAcl();
		foreach (var e in entries) {
			if (e.Tag == PosixAcl.TagUserObj) {
				ownerBits = ClassBits(e.Perm, Mode.S_IRUSR, Mode.S_IWUSR, Mode.S_IXUSR);
				continue;
			}
			if (e.Tag == PosixAcl.TagGroupObj) {
				groupBits = ClassBits(e.Perm, Mode.S_IRGRP, Mode.S_IWGRP, Mode.S_IXGRP);
				continue;
			}
			if (e.Tag == PosixAcl.TagOther) {
				otherBits = ClassBits(e.Perm, Mode.S_IROTH, Mode.S_IWOTH, Mode.S_IXOTH);
				continue;
			}
			if (e.Tag == PosixAcl.TagMask) {
				continue;
			}
			if (e.Tag == PosixAcl.TagUser) {
				acl.Entries.Add(new PgfsAclEntry { PrincipalType = "user", PrincipalName = this.users.UnameOf(e.Id), Rights = RwxFromPerm(e.Perm) });
				continue;
			}
			if (e.Tag == PosixAcl.TagGroup) {
				acl.Entries.Add(new PgfsAclEntry { PrincipalType = "group", PrincipalName = this.users.GnameOf(e.Id), Rights = RwxFromPerm(e.Perm) });
				continue;
			}
		}
		var newMode = (inode.Mode & ~0x1FF) | ownerBits | groupBits | otherBits;
		if (newMode != inode.Mode) {
			if (!this.api.UpdateMode(inode.Id, newMode)) {
				return -EIO;
			}
		}
		this.api.SetXAttr(inode.Id, PgfsAclXattrKey, acl.ToBytes(), false, false);
		return 0;
	}

	private static int ClassBits(ushort perm, int rb, int wb, int xb) {
		var bits = 0;
		if ((perm & PosixAcl.PermRead) != 0) { bits |= rb; }
		if ((perm & PosixAcl.PermWrite) != 0) { bits |= wb; }
		if ((perm & PosixAcl.PermExec) != 0) { bits |= xb; }
		return bits;
	}

	private static string RwxFromPerm(ushort perm) {
		var rc = '-';
		if ((perm & PosixAcl.PermRead) != 0) { rc = 'r'; }
		var wc = '-';
		if ((perm & PosixAcl.PermWrite) != 0) { wc = 'w'; }
		var xc = '-';
		if ((perm & PosixAcl.PermExec) != 0) { xc = 'x'; }
		return new string(new[] { rc, wc, xc });
	}

	// ------------------------------------------------------------------
	// Symbolic links
	// ------------------------------------------------------------------

	public override int SymLink(ReadOnlySpan<byte> path, ReadOnlySpan<byte> target) {
		this.SetAuditContext();
		// Pgfs.Fuse 0.1's FuseMount.Symlink(path*, path*) hands libfuse's (target, linkname) to
		// IFuseFileSystem.SymLink in the reverse order (arg2 then arg1 in the IL).
		// So in this override arg1 = linkPath (where it is created) and arg2 = linkContent (what the link holds).
		// The parameter names path/target come from libfuse and are misleading, so they are rebound here to
		// variables that mean something.
		var linkPath = PathToString(path);
		var linkContent = PathToString(target);
		try {
			var (parentPath, name) = Pgfs.Core.Utility.PathParser.SplitParent(linkPath);
			if (string.IsNullOrEmpty(name)) {
				return -EINVAL;
			}
			var parent = this.api.GetByPath(parentPath);
			if (parent == null) {
				return -ENOENT;
			}
			if (!parent.IsDirectory) {
				return -ENOTDIR;
			}
			if (this.api.GetByPath(linkPath) != null) {
				return -EEXIST;
			}
			var (uname, gname) = this.CurrentUserNames();
			var inode = this.api.CreateSymlink(parent.Id, name, uname, gname, linkContent);
			if (inode == null) {
				return -EEXIST;
			}
			this.api.InodeCache.Put(inode, linkPath);
			return 0;
		} catch (Pgfs.Core.Api.Api.ParentVanishedException) {
			return -ENOENT;
		} catch (Exception ex) {
			Logger.Error("SymLink failed: ", linkContent, " -> ", linkPath, " ", ex);
			return -EIO;
		}
	}

	public override int ReadLink(ReadOnlySpan<byte> path, Span<byte> buffer) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (inode.LinkTarget == null) {
				return -EINVAL;
			}
			var bytes = Encoding.UTF8.GetBytes(inode.LinkTarget);
			// FUSE convention: write a NUL-terminated string into buffer and return 0 (success).
			if (bytes.Length + 1 > buffer.Length) {
				// If it does not fit, write a truncated prefix (still reserving the NUL terminator).
				bytes.AsSpan(0, buffer.Length - 1).CopyTo(buffer);
				buffer[buffer.Length - 1] = 0;
				return 0;
			}
			bytes.AsSpan().CopyTo(buffer);
			buffer[bytes.Length] = 0;
			return 0;
		} catch (Exception ex) {
			Logger.Error("ReadLink failed: ", p, " ", ex);
			return -EIO;
		}
	}

	// ------------------------------------------------------------------
	// Hard links
	// ------------------------------------------------------------------

	public override int Link(ReadOnlySpan<byte> fromPath, ReadOnlySpan<byte> toPath) {
		this.SetAuditContext();
		var src = PathToString(fromPath);
		var dst = PathToString(toPath);
		try {
			var source = this.api.GetByPath(src);
			if (source == null) {
				return -ENOENT;
			}
			if (source.IsDirectory) {
				return -EPERM; // Hard links to directories are forbidden.
			}
			var (parentPath, name) = Pgfs.Core.Utility.PathParser.SplitParent(dst);
			if (string.IsNullOrEmpty(name)) {
				return -EINVAL;
			}
			var parent = this.api.GetByPath(parentPath);
			if (parent == null) {
				return -ENOENT;
			}
			if (!parent.IsDirectory) {
				return -ENOTDIR;
			}
			if (this.api.GetByPath(dst) != null) {
				return -EEXIST;
			}
			var linked = this.api.CreateHardLink(source, parent.Id, name);
			if (linked == null) {
				return -EEXIST;
			}
			this.api.InodeCache.Put(linked, dst);
			return 0;
		} catch (Pgfs.Core.Api.Api.ParentVanishedException) {
			return -ENOENT;
		} catch (Exception ex) {
			Logger.Error("Link failed: ", src, " -> ", dst, " ", ex);
			return -EIO;
		}
	}
}
