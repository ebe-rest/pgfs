namespace Pgfs.Mount;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Lib.Api;
using Lib.Logging;
using Lib.Models;
using Lib.Utility;
using Tmds.Fuse;
using Tmds.Linux;
using static Tmds.Linux.LibC;

/// <summary>
/// PGFS FUSE filesystem implementation running on Tmds.Fuse (Linux/macOS).
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
		// Name-resolution fallback is mount.fallback_uname / fallback_gname (stored in the DB).
		// The owner of a new inode is a separate concept and uses the running process's user
		// (changeable later via chmod/chown).
		this.users = new UserResolver(api.Config.Mount.FallbackUname, api.Config.Mount.FallbackGname);
		var name = Environment.UserName;
		if (string.IsNullOrEmpty(name)) {
			name = "pgfs";
		}
		// The owner name of a new inode is normalized before storage too (docs/permission-interop.md).
		this.defaultUname = NameNormalizer.Normalize(name);
		// The gname for a new inode is resolved from the running process's gid.
		var processGid = (uint)getgid();
		this.defaultGname = this.users.GnameOf(processGid);
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

	/// <summary>Splits a path into "parent path + leaf element".</summary>
	// Windows-shareable: path splitting is OS-independent if the separator is parameterized. Here it is fixed to `/`.
	private static (string parentPath, string name) SplitParent(string path) {
		if (path == "/" || string.IsNullOrEmpty(path)) {
			return ("/", "");
		}
		var i = path.LastIndexOf('/');
		if (i < 0) {
			return ("/", path);
		}
		if (i == 0) {
			return ("/", path[1..]);
		}
		return (path[..i], path[(i + 1)..]);
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
			var uname = NameNormalizer.Normalize(this.users.UnameOf(uid));
			if (!string.IsNullOrEmpty(uname)) {
				return (uname, this.users.GnameOf(gid));
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
			Uname = this.users.UnameOf(uid),
			Domain = null,
		};
	}

	// ------------------------------------------------------------------
	// GetAttr
	// ------------------------------------------------------------------

	public override int GetAttr(ReadOnlySpan<byte> path, ref stat stat, FuseFileInfoRef fiRef) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
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
		// Tmds.Linux wrapper structs have fixed implicit-cast source types (mode_t is ushort,
		// uid/gid_t is uint, nlink_t/ino_t/fs*cnt_t is ulong, off_t/blk* is long).
		// Assignments must be explicitly converted to the implicit-cast source type first.
		// st_ino must match across hard links per POSIX, but the pgfs schema keeps inode rows under
		// separate ids, so when the data body is shared (DataId != null) we return a value based on data_id.
		// Directories etc. (DataId == null) use inode.Id directly. To avoid collisions between inode.Id and
		// data_id, the file side sets the sign bit (0x8000_0000_0000_0000) to separate the namespaces.
		// For the kernel to use this value, Mount/Program.cs must pass `-o use_ino`.
		ulong inoValue;
		if (inode.DataId is long dataId && dataId >= 0) {
			inoValue = (ulong)dataId | 0x8000_0000_0000_0000UL;
		} else {
			inoValue = inode.Id >= 0 ? (ulong)inode.Id : 0UL;
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
		s.st_blocks = (inode.Size + 511) / 512;
		s.st_blksize = 4096L;
		var mtime = inode.Mtime == default ? DateTime.UtcNow : DateTime.SpecifyKind(inode.Mtime, DateTimeKind.Utc);
		var ctime = inode.Ctime == default ? mtime : DateTime.SpecifyKind(inode.Ctime, DateTimeKind.Utc);
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
		// We could stash the inode id in fi.fh for later calls, but we do not for now.
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
		return 0;
	}

	// ------------------------------------------------------------------
	// MkDir / RmDir
	// ------------------------------------------------------------------

	public override int MkDir(ReadOnlySpan<byte> path, mode_t mode) {
		this.SetAuditContext();
		var p = PathToString(path);
		try {
			var (parentPath, name) = SplitParent(p);
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
			var (parentPath, name) = SplitParent(p);
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
			var inode = this.api.CreateFile(parent.Id, name, uname, gname, (int)ModeToUInt32(mode));
			if (inode == null) {
				return -EEXIST;
			}
			this.api.InodeCache.Put(inode, p);
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
		try {
			var inode = this.api.GetByPath(oldP);
			if (inode == null) {
				return -ENOENT;
			}
			var (newParentPath, newName) = SplitParent(newP);
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
				// If the RENAME_NOREPLACE flag (Linux-specific, 1) is set, replacement is forbidden.
				const int RENAME_NOREPLACE = 1;
				if ((flags & RENAME_NOREPLACE) != 0) {
					return -EEXIST;
				}
				// The two must be of the same kind.
				if (existing.IsDirectory != inode.IsDirectory) {
					return existing.IsDirectory ? -EISDIR : -ENOTDIR;
				}
				// An existing directory target must be empty.
				if (existing.IsDirectory && !this.api.IsDirectoryEmpty(existing.Id)) {
					return -ENOTEMPTY;
				}
				this.api.DeleteInode(existing);
			}

			if (!this.api.Rename(inode.Id, newParent.Id, newName)) {
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
			var inode = this.api.GetByPath(p);
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
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			// Resolve uid/gid to names and write them back to the DB.
			// uid == 0xFFFFFFFF (-1) is the Linux convention for "do not change".
			var newUname = uid == uint.MaxValue ? inode.UserName : this.users.UnameOf(uid);
			var newGname = gid == uint.MaxValue ? inode.GroupName : this.users.GnameOf(gid);
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
		} catch (Exception ex) {
			Logger.Error("Truncate failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int UpdateTimestamps(ReadOnlySpan<byte> path, ref timespec atime, ref timespec mtime, FuseFileInfoRef fiRef) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
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
			var newMtime = mtime.IsNow() ? DateTime.UtcNow : mtime.ToDateTime();
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
		return 0;
	}

	public override void Release(ReadOnlySpan<byte> path, ref FuseFileInfo fi) {
		// no-op
	}

	public override int Read(ReadOnlySpan<byte> path, ulong offset, Span<byte> buffer, ref FuseFileInfo fi) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (inode.IsDirectory) {
				return -EISDIR;
			}
			return this.api.ReadData(inode, (long)offset, buffer);
		} catch (Exception ex) {
			Logger.Error("Read failed: ", p, " ", ex);
			return -EIO;
		}
	}

	public override int Write(ReadOnlySpan<byte> path, ulong off, ReadOnlySpan<byte> span, ref FuseFileInfo fi) {
		var p = PathToString(path);
		try {
			var inode = this.api.GetByPath(p);
			if (inode == null) {
				return -ENOENT;
			}
			if (inode.IsDirectory) {
				return -EISDIR;
			}
			var written = this.api.WriteData(inode, (long)off, span);
			return written;
		} catch (Exception ex) {
			Logger.Error("Write failed: ", p, " ", ex);
			return -EIO;
		}
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
		// Tmds.Fuse 0.1's FuseMount.Symlink(path*, path*) passes libfuse's (target, linkname) to
		// IFuseFileSystem.SymLink in reverse order (arg2 -> arg1 at the IL level).
		// So in this override, arg1 = linkPath (where to create it) and arg2 = linkContent (the link body).
		// The parameter names path/target come from libfuse and are misleading, so rebind them to meaningful variables here.
		var linkPath = PathToString(path);
		var linkContent = PathToString(target);
		try {
			var (parentPath, name) = SplitParent(linkPath);
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
			} else {
				bytes.AsSpan().CopyTo(buffer);
				buffer[bytes.Length] = 0;
			}
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
			var (parentPath, name) = SplitParent(dst);
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
		} catch (Exception ex) {
			Logger.Error("Link failed: ", src, " -> ", dst, " ", ex);
			return -EIO;
		}
	}
}
