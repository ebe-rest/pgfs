namespace Pgfs.Assign;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using DokanNet;
using Lib.Api;
using Lib.Models;
using LTRData.Extensions.Native.Memory;
using FileAccess = DokanNet.FileAccess;

/// <summary>
/// PGFS filesystem implementation running on DokanNet (Windows).
///
/// Cross-platform DB operations go through <see cref="Api"/>, and this class is purely a
/// "DokanNet -&gt; Api" adapter.
/// FileSystem.cs has the same role as the Linux side (<see cref="Pgfs.Mount.FileSystem"/>); only the
/// method names and argument types differ.
///
/// Places that could be shared with Windows are marked with a `// Windows-shareable:` comment.
///
/// Operations delegated or not implemented:
///   - GetFileSecurity / SetFileSecurity — return DokanResult.NotImplemented so the kernel generates a default ACL.
///   - FindStreams — Alternate Data Streams are not supported; returns DokanResult.NotImplemented.
///   - LockFile / UnlockFile — return Success and let the kernel handle it via the UserModeLock option.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileSystem : IDokanOperations2, IDisposable
{
	private readonly Api api;
	private readonly string mountPoint;
	private readonly WindowsUserResolver users;
	private readonly Dokan dokan;
	private readonly Logger.Instance logger;

	private DokanInstance? instance;
	private readonly CountdownEvent running = new(1);

	public FileSystem(Api api, string mountPoint) {
		this.api = api;
		this.mountPoint = mountPoint;
		// Name-resolution fallback is mount.fallback_uname / fallback_gname (stored in the DB).
		// Unknown uname/gname should map to a low-privilege account such as Guest / Guests; mapping them to
		// the running process's user would cause an "others' files = mine" incident.
		this.users = new WindowsUserResolver(api.Config.Mount.FallbackUname, api.Config.Mount.FallbackGname);
		this.logger = new Logger.Instance();
		this.dokan = new Dokan(this.logger);
	}

	public void Dispose() {
		this.instance?.Dispose();
		this.dokan.Dispose();
	}

	/// <summary>
	/// For the audit log, sets the caller (DOMAIN\user) of the current Dokan operation into
	/// <see cref="AuditContext.Current"/>. Called at the start of each mutating operation. Sets null when
	/// auditing is disabled or retrieval fails. Windows has no numeric uid, so Uid is null, the account name
	/// goes in Uname, and the domain/workgroup goes in Domain.
	/// </summary>
	private void SetAuditContext(ref DokanFileInfo info) {
		if (!this.api.Config.Audit.Enabled) {
			AuditContext.Current = null;
			return;
		}
		try {
			using var identity = info.GetRequestor();
			var name = identity?.Name;
			if (string.IsNullOrEmpty(name)) {
				AuditContext.Current = null;
				return;
			}
			var (domain, uname) = SplitDomainUser(name);
			AuditContext.Current = new AuditContext { Uid = null, Uname = uname, Domain = domain };
		} catch (Exception ex) {
			Lib.Logging.Logger.Warning("failed to get the audit requestor: ", ex.Message);
			AuditContext.Current = null;
		}
	}

	/// <summary>Splits "DOMAIN\\user" into (domain, user). domain is null if there is no separator.</summary>
	private static (string? domain, string uname) SplitDomainUser(string name) {
		var idx = name.IndexOf('\\');
		if (idx < 0) {
			return (null, name);
		}
		return (name.Substring(0, idx), name.Substring(idx + 1));
	}

	/// <summary>Builds and mounts the Dokan instance, blocking until unmount.</summary>
	public void Run() {
		var builder = new DokanInstanceBuilder(this.dokan);
		builder.ConfigureLogger(() => this.logger);
		builder.ConfigureOptions(o => {
			o.TimeOut = TimeSpan.FromMinutes(5);
			o.MountPoint = this.mountPoint;
			if (this.logger.DebugEnabled) {
				o.Options |= DokanOptions.DebugMode;
			}
			o.Options |= DokanOptions.RemovableDrive;
			o.Options |= DokanOptions.MountManager;
			o.Options |= DokanOptions.UserModeLock;
		});
		builder.Validate();
		this.instance = builder.Build(this);

		// Receive cross-client change notifications (database.notify_enabled=true) and ask Explorer to redraw.
		// NotifyChannel itself is already started in the Api ctor, and it handles InodeCache invalidation.
		// Here the role is to call DokanInstance.NotifyUpdate to inform the Windows kernel / Explorer.
		this.api.OsBridge = this.PropagateRemoteChange;

		Logger.Info("PGFS mounted: ", this.mountPoint);

		// Unmount on Ctrl+C.
		Console.CancelKeyPress += (_, e) => {
			e.Cancel = true;
			Logger.Info("received Ctrl+C; attempting to unmount.");
			this.running.Signal();
		};

		this.running.Wait();
	}

	// ------------------------------------------------------------------
	// Cross-client change notification -> Explorer refresh
	// ------------------------------------------------------------------

	/// <summary>
	/// Called when a cross-client change notification arrives via <see cref="NotifyChannel"/>.
	/// Converts the resolved paths to Windows form (`\`-separated) and calls
	/// <see cref="DokanInstance.NotifyUpdate"/>. Explorer / shell extensions pick this up and refresh the display.
	/// Exceptions are swallowed internally (failing a write because notification failed would be backwards).
	/// </summary>
	private void PropagateRemoteChange(RemoteChangeInfo info) {
		var inst = this.instance;
		if (inst == null) {
			return;
		}
		try {
			foreach (var kv in info.ResolvedPaths) {
				var winPath = ToWindowsPath(kv.Value);
				inst.NotifyUpdate(winPath);
			}
			foreach (var kv in info.ResolvedParentPaths) {
				// NotifyUpdate on the parent directory itself prompts a re-enumeration of its children.
				var winPath = ToWindowsPath(kv.Value);
				inst.NotifyUpdate(winPath);
			}
		} catch (Exception ex) {
			Logger.Warn("error in PropagateRemoteChange: ", ex.Message);
		}
	}

	/// <summary>
	/// Converts an <see cref="Api"/>-style `/`-separated path into the `\`-separated path
	/// DokanInstance.Notify* expects. The root is a lone `\`.
	/// </summary>
	private static string ToWindowsPath(string apiPath) {
		if (string.IsNullOrEmpty(apiPath) || apiPath == "/") {
			return "\\";
		}
		return apiPath.Replace('/', '\\');
	}

	// ------------------------------------------------------------------
	// Path utilities
	// ------------------------------------------------------------------

	/// <summary>
	/// Normalizes the `\foo\bar`-style Windows path DokanNet passes in into `/foo/bar`,
	/// because <see cref="Api"/> expects OS-independent `/`-separated paths.
	/// </summary>
	// Windows-shareable concept: normalizing the path separator is each mounter's job, but Api is neutral.
	private static string NormalizePath(ReadOnlyNativeMemory<char> p) {
		var raw = p.Span.ToString();
		if (string.IsNullOrEmpty(raw)) {
			return "/";
		}
		var normalized = raw.Replace('\\', '/');
		if (!normalized.StartsWith('/')) {
			normalized = "/" + normalized;
		}
		return normalized;
	}

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

	private Inode? Resolve(string path, ref DokanFileInfo info) {
		if (info.Context is Inode cached) {
			return cached;
		}
		var inode = this.api.GetByPath(path);
		if (inode != null) {
			info.Context = inode;
		}
		return inode;
	}

	// ------------------------------------------------------------------
	// Mounted / Unmounted
	// ------------------------------------------------------------------

	public NtStatus Mounted(ReadOnlyNativeMemory<char> mountPointPtr, ref DokanFileInfo info) {
		Logger.Info("Mounted: ", mountPointPtr.Span.ToString());
		return DokanResult.Success;
	}

	public NtStatus Unmounted(ref DokanFileInfo info) {
		Logger.Info("Unmounted");
		try {
			this.running.Signal();
		} catch (InvalidOperationException) {
			// already signaled
		}
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// GetVolumeInformation / GetDiskFreeSpace
	// ------------------------------------------------------------------

	public NtStatus GetVolumeInformation(
		NativeMemory<char> volumeLabel,
		out FileSystemFeatures features,
		NativeMemory<char> fileSystemName,
		out uint maximumComponentLength,
		ref uint volumeSerialNumber,
		ref DokanFileInfo info
	) {
		var label = this.api.Config.FileSystem.VolumeLabel;
		volumeLabel.SetString(label);
		fileSystemName.SetString("PGFS");
		features = FileSystemFeatures.CasePreservedNames
				 | FileSystemFeatures.UnicodeOnDisk
				 | FileSystemFeatures.SupportsObjectIDs;
		maximumComponentLength = 255;
		volumeSerialNumber = (uint)label.GetHashCode();
		return DokanResult.Success;
	}

	public NtStatus GetDiskFreeSpace(
		out long freeBytesAvailable,
		out long totalNumberOfBytes,
		out long totalNumberOfFreeBytes,
		ref DokanFileInfo info
	) {
		var capacity = this.api.GetCapacityBytes();
		var used = this.api.GetTotalUsedBytes();
		var free = Math.Max(0, capacity - used);
		freeBytesAvailable = free;
		totalNumberOfBytes = capacity;
		totalNumberOfFreeBytes = free;
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// CreateFile — the Dokan core that handles open / create / delete-reservation all together
	// ------------------------------------------------------------------

	public NtStatus CreateFile(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileAccess access,
		FileShare share,
		FileMode mode,
		FileOptions options,
		FileAttributes attributes,
		ref DokanFileInfo info
	) {
		this.SetAuditContext(ref info);
		var path = NormalizePath(fileNamePtr);
		var existing = this.api.GetByPath(path);
		// Windows-shareable concept: the open/create logic is OS-independent.
		var wantsDirectory = info.IsDirectory || (options & FileOptions.DeleteOnClose) == 0
								 && (attributes & FileAttributes.Directory) == FileAttributes.Directory;

		switch (mode) {
		case FileMode.CreateNew:
			if (existing != null) {
				return DokanResult.FileExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, ref info);

		case FileMode.Create:
			if (existing != null) {
				// Discard the contents of an existing file (not allowed for directories).
				if (existing.IsDirectory) {
					return DokanResult.AccessDenied;
				}
				this.api.TruncateData(existing, 0);
				info.Context = existing;
				return DokanResult.AlreadyExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, ref info);

		case FileMode.Open:
			if (existing == null) {
				return wantsDirectory ? DokanResult.PathNotFound : DokanResult.FileNotFound;
			}
			info.Context = existing;
			info.IsDirectory = existing.IsDirectory;
			return DokanResult.Success;

		case FileMode.OpenOrCreate:
			if (existing != null) {
				info.Context = existing;
				info.IsDirectory = existing.IsDirectory;
				return DokanResult.AlreadyExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, ref info);

		case FileMode.Truncate:
			if (existing == null) {
				return DokanResult.FileNotFound;
			}
			if (existing.IsDirectory) {
				return DokanResult.AccessDenied;
			}
			this.api.TruncateData(existing, 0);
			info.Context = existing;
			return DokanResult.Success;

		case FileMode.Append:
			if (existing == null) {
				return this.DoCreate(path, wantsDirectory, attributes, ref info);
			}
			if (existing.IsDirectory) {
				return DokanResult.AccessDenied;
			}
			info.Context = existing;
			// info.WriteToEndOfFile is read-only. For FileMode.Append, the Dokan kernel side sets the
			// WriteToEndOfFile flag on subsequent WriteFile calls.
			return DokanResult.Success;

		default:
			return DokanResult.InvalidParameter;
		}
	}

	private NtStatus DoCreate(string path, bool asDirectory, FileAttributes attributes, ref DokanFileInfo info) {
		var (parentPath, name) = SplitParent(path);
		if (string.IsNullOrEmpty(name)) {
			return DokanResult.InvalidName;
		}
		var parent = this.api.GetByPath(parentPath);
		if (parent == null) {
			return DokanResult.PathNotFound;
		}
		if (!parent.IsDirectory) {
			return DokanResult.PathNotFound;
		}

		var uname = this.users.DefaultUname;
		var gname = this.users.DefaultGname;
		// Permissions default to Linux-style 0755 (directory) / 0644 (file).
		Inode? created;
		if (asDirectory) {
			created = this.api.CreateDirectory(parent.Id, name, uname, gname, 0x1ED); // 0755
			if (created == null) {
				return DokanResult.AlreadyExists;
			}
			info.IsDirectory = true;
		} else {
			var mode = (attributes & FileAttributes.ReadOnly) != 0 ? 0x124 /*0444*/ : 0x1A4 /*0644*/;
			created = this.api.CreateFile(parent.Id, name, uname, gname, mode);
			if (created == null) {
				return DokanResult.AlreadyExists;
			}
		}
		// Cache it under the full path so the immediately following getattr does not hit the DB.
		this.api.InodeCache.Put(created, path);
		info.Context = created;
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// Cleanup / CloseFile
	// ------------------------------------------------------------------

	public void Cleanup(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		this.SetAuditContext(ref info);
		// If DeletePending is set, the actual deletion happens here (the Windows convention).
		if (info.DeletePending && info.Context is Inode inode) {
			try {
				if (inode.IsDirectory) {
					if (this.api.IsDirectoryEmpty(inode.Id)) {
						this.api.DeleteInode(inode);
					}
				} else {
					this.api.DeleteInode(inode);
				}
			} catch (Exception ex) {
				Lib.Logging.Logger.Error("Cleanup delete failed: ", ex);
			}
		}
	}

	public void CloseFile(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		info.Context = null;
	}

	// ------------------------------------------------------------------
	// GetFileInformation / FindFiles / FindFilesWithPattern
	// ------------------------------------------------------------------

	public NtStatus GetFileInformation(
		ReadOnlyNativeMemory<char> fileNamePtr,
		out ByHandleFileInformation fileInfo,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			fileInfo = default;
			return DokanResult.FileNotFound;
		}
		fileInfo = new ByHandleFileInformation {
			Attributes = FileSystemUtils.ToAttributes(inode, this.users, this.api),
			CreationTime = ToLocal(inode.CreatedAt),
			LastAccessTime = ToLocal(inode.Mtime),
			LastWriteTime = ToLocal(inode.Mtime),
			Length = inode.Size,
			NumberOfLinks = Math.Max(1, inode.NLink),
			FileIndex = inode.Id,
		};
		return DokanResult.Success;
	}

	public NtStatus FindFiles(
		ReadOnlyNativeMemory<char> fileNamePtr,
		out IEnumerable<FindFileInformation> files,
		ref DokanFileInfo info
	) {
		return this.FindFilesWithPattern(fileNamePtr, default, out files, ref info);
	}

	public NtStatus FindFilesWithPattern(
		ReadOnlyNativeMemory<char> fileNamePtr,
		ReadOnlyNativeMemory<char> searchPatternPtr,
		out IEnumerable<FindFileInformation> files,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			files = [];
			return DokanResult.PathNotFound;
		}
		if (!inode.IsDirectory) {
			files = [];
			return DokanResult.NotADirectory;
		}
		var pattern = searchPatternPtr.Span.ToString();
		var children = this.api.ListChildren(inode.Id);
		files = children
			.Where(c => string.IsNullOrEmpty(pattern) || DokanHelper.DokanIsNameInExpression(pattern, c.Name, true))
			.Select(c => new FindFileInformation {
				FileName = c.Name.AsMemory(),
				// Do not provide a ShortFileName (default = empty); leave it to the Dokan/Windows kernel.
				// Putting `c.Name` in directly raises a "Destination is too short" exception for long names,
				// and passing through only 8.3-conformant names gives no guarantee against collisions with
				// the auto-generated side, so set everything to default — effectively disabling 8.3 symbolic
				// access (DOS/16-bit applications are not a target).
				ShortFileName = default,
				Attributes = FileSystemUtils.ToAttributes(c, this.users, this.api),
				CreationTime = ToLocal(c.CreatedAt),
				LastAccessTime = ToLocal(c.Mtime),
				LastWriteTime = ToLocal(c.Mtime),
				Length = c.Size,
			})
			.ToList();
		return DokanResult.Success;
	}

	public int DirectoryListingTimeoutResetIntervalMs => 0;

	// ------------------------------------------------------------------
	// ReadFile / WriteFile
	// ------------------------------------------------------------------

	public NtStatus ReadFile(
		ReadOnlyNativeMemory<char> fileNamePtr,
		NativeMemory<byte> buffer,
		out int bytesRead,
		long offset,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			bytesRead = 0;
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			bytesRead = 0;
			return DokanResult.AccessDenied;
		}
		try {
			bytesRead = this.api.ReadData(inode, offset, buffer.Span);
			return DokanResult.Success;
		} catch (Exception ex) {
			Lib.Logging.Logger.Error("ReadFile failed: ", path, " ", ex);
			bytesRead = 0;
			return DokanResult.Error;
		}
	}

	public NtStatus WriteFile(
		ReadOnlyNativeMemory<char> fileNamePtr,
		ReadOnlyNativeMemory<byte> buffer,
		out int bytesWritten,
		long offset,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			bytesWritten = 0;
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			bytesWritten = 0;
			return DokanResult.AccessDenied;
		}
		try {
			var writeOffset = info.WriteToEndOfFile ? inode.Size : offset;
			bytesWritten = this.api.WriteData(inode, writeOffset, buffer.Span);
			return DokanResult.Success;
		} catch (Exception ex) {
			Lib.Logging.Logger.Error("WriteFile failed: ", path, " ", ex);
			bytesWritten = 0;
			return DokanResult.Error;
		}
	}

	// ------------------------------------------------------------------
	// FlushFileBuffers
	// ------------------------------------------------------------------

	public NtStatus FlushFileBuffers(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		// The PostgreSQL side is already persisted by transaction commit. No explicit fsync needed.
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// SetFileAttributes / SetFileTime
	// ------------------------------------------------------------------

	public NtStatus SetFileAttributes(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileAttributes attributes,
		ref DokanFileInfo info
	) {
		this.SetAuditContext(ref info);
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		// ReadOnly is reflected into st_mode's write bits (so it is visible bidirectionally with the Linux side).
		var mode = inode.Mode & ~0x1FF; // Keep the type bits, clear the permission bits.
		if ((attributes & FileAttributes.ReadOnly) != 0) {
			mode |= 0x124; // 0444
		} else {
			mode |= inode.IsDirectory ? 0x1ED /*0755*/ : 0x1A4 /*0644*/;
		}
		if (!this.api.UpdateMode(inode.Id, mode)) {
			return DokanResult.Error;
		}
		// Hidden / System / Archive have no Linux-side equivalent, so store them in the xattr (`user.win_attrs`)
		// (see [FileSystemUtils.SaveWinAttrs](FileSystemUtils.cs)).
		FileSystemUtils.SaveWinAttrs(this.api, inode.Id, attributes);
		return DokanResult.Success;
	}

	public NtStatus SetFileTime(
		ReadOnlyNativeMemory<char> fileNamePtr,
		DateTime? creationTime,
		DateTime? lastAccessTime,
		DateTime? lastWriteTime,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		// Requirement: there is no st_atime, so lastAccessTime is ignored; only mtime.
		if (lastWriteTime is DateTime mt) {
			this.api.UpdateTimestamps(inode.Id, mt.ToUniversalTime());
		}
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// DeleteFile / DeleteDirectory — on Windows: "reserve deletion -> actually delete in Cleanup"
	// ------------------------------------------------------------------

	public NtStatus DeleteFile(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			return DokanResult.AccessDenied;
		}
		// The actual deletion happens in Cleanup, so here just report "whether deletion is allowed".
		return DokanResult.Success;
	}

	public NtStatus DeleteDirectory(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.PathNotFound;
		}
		if (!inode.IsDirectory) {
			return DokanResult.NotADirectory;
		}
		if (!this.api.IsDirectoryEmpty(inode.Id)) {
			return DokanResult.DirectoryNotEmpty;
		}
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// MoveFile / SetEndOfFile / SetAllocationSize
	// ------------------------------------------------------------------

	public NtStatus MoveFile(
		ReadOnlyNativeMemory<char> oldNamePtr,
		ReadOnlyNativeMemory<char> newNamePtr,
		bool replace,
		ref DokanFileInfo info
	) {
		this.SetAuditContext(ref info);
		var oldPath = NormalizePath(oldNamePtr);
		var newPath = NormalizePath(newNamePtr);
		var inode = this.api.GetByPath(oldPath);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		var (newParentPath, newName) = SplitParent(newPath);
		if (string.IsNullOrEmpty(newName)) {
			return DokanResult.InvalidName;
		}
		var newParent = this.api.GetByPath(newParentPath);
		if (newParent == null || !newParent.IsDirectory) {
			return DokanResult.PathNotFound;
		}
		var existing = this.api.GetByPath(newPath);
		if (existing != null) {
			if (!replace) {
				return DokanResult.FileExists;
			}
			if (existing.IsDirectory != inode.IsDirectory) {
				return existing.IsDirectory ? DokanResult.AccessDenied : DokanResult.NotADirectory;
			}
			if (existing.IsDirectory && !this.api.IsDirectoryEmpty(existing.Id)) {
				return DokanResult.DirectoryNotEmpty;
			}
			this.api.DeleteInode(existing);
		}
		if (!this.api.Rename(inode.Id, newParent.Id, newName)) {
			return DokanResult.Error;
		}
		this.api.InodeCache.InvalidatePrefix(oldPath);
		this.api.InodeCache.Invalidate(inode.Id, newPath);
		return DokanResult.Success;
	}

	public NtStatus SetEndOfFile(ReadOnlyNativeMemory<char> fileNamePtr, long length, ref DokanFileInfo info) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			return DokanResult.AccessDenied;
		}
		if (!this.api.TruncateData(inode, length)) {
			return DokanResult.Error;
		}
		return DokanResult.Success;
	}

	public NtStatus SetAllocationSize(ReadOnlyNativeMemory<char> fileNamePtr, long length, ref DokanFileInfo info) {
		// It is an allocation "hint", so for now behave the same as SetEndOfFile.
		return this.SetEndOfFile(fileNamePtr, length, ref info);
	}

	// ------------------------------------------------------------------
	// LockFile / UnlockFile — delegated to the kernel via Dokan's UserModeLock option
	// ------------------------------------------------------------------

	public NtStatus LockFile(ReadOnlyNativeMemory<char> fileNamePtr, long offset, long length, ref DokanFileInfo info) {
		return DokanResult.Success;
	}

	public NtStatus UnlockFile(ReadOnlyNativeMemory<char> fileNamePtr, long offset, long length, ref DokanFileInfo info) {
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// GetFileSecurity / SetFileSecurity — NotImplemented for now, letting the kernel generate a default ACL
	// ------------------------------------------------------------------

	public NtStatus GetFileSecurity(
		ReadOnlyNativeMemory<char> fileNamePtr,
		out FileSystemSecurity? security,
		AccessControlSections sections,
		ref DokanFileInfo info
	) {
		security = null;
		return DokanResult.NotImplemented;
	}

	public NtStatus SetFileSecurity(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileSystemSecurity security,
		AccessControlSections sections,
		ref DokanFileInfo info
	) {
		// Provisional: reflecting ACLs into the DB is not implemented.
		return DokanResult.NotImplemented;
	}

	// ------------------------------------------------------------------
	// FindStreams — ADS not supported
	// ------------------------------------------------------------------

	public NtStatus FindStreams(ReadOnlyNativeMemory<char> fileNamePtr, out IEnumerable<FindFileInformation> streams, ref DokanFileInfo info) {
		streams = [];
		return DokanResult.NotImplemented;
	}

	// ------------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------------

	private static DateTime ToLocal(DateTime utc) {
		// PostgreSQL TIMESTAMP is treated as UTC. Passing Local to Dokan tends to line up better with the
		// Windows Explorer display.
		if (utc.Kind == DateTimeKind.Unspecified) {
			utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
		}
		return utc.ToLocalTime();
	}
}
