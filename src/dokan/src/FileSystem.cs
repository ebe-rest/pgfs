namespace Pgfs.Dokan;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DokanNet;
using Core.Api;
using Core.Models;
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
///   - GetFileSecurity / SetFileSecurity — project the inode (uname/gname/mode + canonical ACL) to/from a Windows SD.
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
	private readonly object stopLock = new();
	private bool stopping;

	/// <summary>
	/// **The actual mount target** received from the driver in <see cref="Mounted"/> (`P:\` or `C:\mnt\pgfs`).
	/// <see cref="DokanInstance.NotifyUpdate"/> and friends demand "an absolute path including the mount
	/// target", so the driver's declaration is authoritative rather than the configured value (the
	/// MountManager can swap the drive letter out).
	/// </summary>
	private string? actualMountPoint;

	public FileSystem(Api api, string mountPoint) {
		this.api = api;
		this.mountPoint = mountPoint;
		// Name-resolution fallback is mount.fallback_uname / fallback_gname (stored in the DB).
		// Unknown uname/gname should map to a low-privilege account such as Guest / Guests; mapping them to
		// the running process's user would cause an "others' files = mine" incident.
		this.users = new WindowsUserResolver(api.Config.Mount.FallbackUname, api.Config.Mount.FallbackGname);
		this.logger = new Logger.Instance();
		this.dokan = new Dokan(this.logger);
		// **On Windows `.fuse_hidden*` is not kept out of the enumeration.** libfuse leaves no such leftovers here,
		// so there is no reason to hide them, and hiding them only leaves "it does not show up in the enumeration
		// yet cannot be deleted" (`Remove-Item` and Explorer go through the enumeration so they cannot delete it,
		// while calling `DeleteFile` directly can = **there is a way to delete it and nobody can find it**).
		// The source of truth is docs/design/namespace-policy.md.
		this.api.HideLibfuseLeftovers = false;
	}

	public void Dispose() {
		this.instance?.Dispose();
		this.dokan.Dispose();
	}

	// **The context of one handle was moved into Core's OpenFileContext** (handle-context stage A).
	// Its contents - "the resolved inode + the audit subject settled in CreateFile + WRITE_THROUGH" - are
	// unchanged; only the place it lives was lifted into Core so the FUSE side can use the same type.
	// The behaviour was not changed. The source of truth is docs/design/handle-context.md.

	/// <summary>
	/// Resolves the caller (DOMAIN\user) for the audit log. **It can only be called inside CreateFile** -
	/// DokanNet's <c>GetRequestor</c> duplicates the requesting thread's impersonation token, so from any other
	/// callback it always fails with <c>Invalid token for impersonation</c> (measured: calling it from Cleanup /
	/// MoveFile / SetFileAttributes produced 3,400 failures in a single e2e run, and **every caller on the
	/// Windows audit rows was null**).
	/// The settled subject is put on <see cref="Core.Api.OpenFileContext"/>, and the mutating operations after
	/// that rebuild it with <see cref="ApplyAuditContext"/>.
	/// Windows has no numeric uid, so Uid is null, the account name goes into Uname and the domain into Domain.
	/// </summary>
	private (AuditContext? Audit, string? OwnerUname) CaptureCaller(ref DokanFileInfo info) {
		try {
			using var identity = info.GetRequestor();
			if (identity == null) {
				Core.Logging.Logger.Warning("could not obtain the caller (GetRequestor)");
				return (null, null);
			}
			// The owner is decided from **`.User` (the SID of the requesting account)**. `.Owner` can be
			// Administrators in an elevated process, so it is not used (which would erase the actual creator from the record).
			// UnameOf does the normalization and the Windows-to-pgfs mapping (falling back to the fallback name when it cannot resolve).
			string? owner = null;
			if (identity.User is SecurityIdentifier sid) {
				owner = this.users.UnameOf(sid);
			}
			// The audit subject is only for when audit is enabled. **The owner resolution must not depend on whether audit is on.**
			AuditContext? audit = null;
			if (this.api.Config.Audit.Enabled) {
				var name = identity.Name;
				if (string.IsNullOrEmpty(name)) {
					Core.Logging.Logger.Warning("the audit requestor was empty (recording it as an unknown caller)");
				}
				if (!string.IsNullOrEmpty(name)) {
					var (domain, uname) = SplitDomainUser(name);
					audit = new AuditContext { Uid = null, Uname = uname, Domain = domain };
				}
			}
			return (audit, owner);
		} catch (Exception ex) {
			Core.Logging.Logger.Warning("failed to resolve the caller: ", ex.Message);
			return (null, null);
		}
	}

	/// <summary>
	/// Rebuilds the audit subject held on the handle into <see cref="AuditContext.Current"/> (called at the
	/// start of a mutating operation).
	/// When no subject was obtained, or there is no handle, it stays **null** = it is recorded as an unknown caller.
	/// It is never implicitly promoted to the mount process's owner (which would leave someone else's operation under this name).
	/// </summary>
	private void ApplyAuditContext(ref DokanFileInfo info) {
		if (!this.api.Config.Audit.Enabled) {
			AuditContext.Current = null;
			return;
		}
		if (info.Context is OpenFileContext open) {
			AuditContext.Current = open.Audit;
			return;
		}
		Logger.Debug("audit: the handle carries no subject, recording it as an unknown caller");
		AuditContext.Current = null;
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
			// **UserModeLock is not set.** Setting it would make LockFile / UnlockFile our own job, but pgfs keeps no
			// ledger of range locks, so it would be "always succeeds" = **lying that a lock was taken when it was not**.
			// Leaving it off makes **the Dokan driver handle byte-range locks on the kernel side**
			// (DokanNet: "Enable Lockfile/Unlockfile operations. Otherwise Dokan will take care of it.").
			// Cross-client range locks are a separate design (docs/design/windows-parity.md, the lock / access control section).
		});
		builder.Validate();
		this.instance = builder.Build(this);

		// Receive cross-client change notifications (database.notify_enabled=true) and ask Explorer to redraw.
		// NotifyChannel itself is already started in the Api ctor, and it handles InodeCache invalidation.
		// Here the role is to call DokanInstance.NotifyUpdate to inform the Windows kernel / Explorer.
		this.api.OsBridge = this.PropagateRemoteChange;

		Logger.Info("PGFS mounted: ", this.mountPoint);

		// **The stop signal (Ctrl+C) is not subscribed to here.** The subscription lives on the assign.pgfs (tool
		// exe) side. Subscribing here would take the handler off the moment the first press left Run, leaving the
		// longest shutdown flush (Api.Dispose = 30 seconds by default plus retries) **unguarded** - a second
		// Ctrl+C would become .NET's default immediate exit, and neither the loss report nor the {prefix}mounts
		// gravestone would be left behind (the Windows wiring of B-12).
		// A stop is requested by calling RequestStop() from outside. Even when it arrives together with Unmounted, the stop runs only once.
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
			// A notification is the hardest kind of thing to diagnose when it "arrived yet nothing is visible", so
			// **what was notified and how** is left in Debug.
			if (Logger.DebugEnabled) {
				Logger.Debug("remote change: inode=", info.InodeIds.Count, " (resolved ", info.ResolvedPaths.Count,
					") / parent=", info.ParentIds.Count, " (resolved ", info.ResolvedParentPaths.Count, ")");
			}
			foreach (var kv in info.ResolvedPaths) {
				// **A deleted target is not invalidated by NotifyUpdate** (Windows treats it as an attribute change, so
				// the cache entry of the deleted file stays). Core's notification payload carries no op or kind, so
				// **a deletion is detected by fetching it again and seeing whether it is gone** (`GetById` runs after the
				// invalidate of step 2, so it reads the database).
				// Note: even so, **`Test-Path` can keep returning true from the cache on the Windows client side**
				// (measured). An enumeration that re-asks the filesystem (FindFiles) does keep up.
				if (this.api.GetById(kv.Key) == null) {
					this.NotifyDeleted(inst, kv.Value);
					continue;
				}
				this.NotifyUpdate(inst, kv.Value);
			}
			foreach (var kv in info.ResolvedParentPaths) {
				// A NotifyUpdate on the parent directory itself, to prompt a re-enumeration of the child list
				this.NotifyUpdate(inst, kv.Value);
			}
		} catch (Exception ex) {
			Logger.Warn("error in PropagateRemoteChange: ", ex.Message);
		}
	}

	/// <summary>
	/// One path's worth of <see cref="DokanInstance.NotifyUpdate"/>. **The bool return value is not thrown
	/// away** - if a notification does not arrive, stale content stays in Explorer, so a failure is left in
	/// the log where it can be diagnosed (without failing the write).
	/// </summary>
	private void NotifyUpdate(DokanInstance inst, string apiPath) {
		var target = this.ToNotifyPath(apiPath);
		if (target == null) {
			Logger.Debug("skipping NotifyUpdate (the mount target is not settled yet): ", apiPath);
			return;
		}
		if (!inst.NotifyUpdate(target)) {
			Logger.Warn("NotifyUpdate failed: ", target);
			return;
		}
		if (Logger.DebugEnabled) { Logger.Debug("NotifyUpdate: ", target); }
	}

	/// <summary>
	/// The notification for a target another mount **deleted**. Without <c>NotifyDelete</c> the cache entry on the Windows side stays.
	/// <para>
	/// **The kind (file or directory) is not known** - it is already gone so it cannot be read from the
	/// database, and Core's notification payload carries neither an op nor a kind (extending the payload is a
	/// candidate in docs/design/windows-parity.md, the notification section).
	/// So it is notified as a file, and if that is refused it is retried once as a directory.
	/// </para>
	/// </summary>
	private void NotifyDeleted(DokanInstance inst, string apiPath) {
		var target = this.ToNotifyPath(apiPath);
		if (target == null) {
			Logger.Debug("skipping NotifyDelete (the mount target is not settled yet): ", apiPath);
			return;
		}
		if (inst.NotifyDelete(target, isDirectory: false)) {
			if (Logger.DebugEnabled) { Logger.Debug("NotifyDelete(file): ", target); }
			return;
		}
		if (inst.NotifyDelete(target, isDirectory: true)) {
			if (Logger.DebugEnabled) { Logger.Debug("NotifyDelete(dir): ", target); }
			return;
		}
		Logger.Warn("NotifyDelete failed: ", target);
	}

	/// <summary>
	/// Converts an <see cref="Api"/>-style `/`-separated path into the form DokanInstance.Notify* expects.
	/// Notify* demands **an absolute path including the mount target** (`P:\dir\file`), so the real mount
	/// target received in <see cref="Mounted"/> is prefixed.
	/// Passing a root-relative `\dir\file` makes the notification fall over with an unknown destination (which
	/// is what it used to be).
	/// Before the mount (while the real mount target is not settled) it cannot be converted, so null.
	/// </summary>
	private string? ToNotifyPath(string apiPath) {
		var root = this.actualMountPoint;
		if (string.IsNullOrEmpty(root)) {
			return null;
		}
		// "P:" / "P:\" -> "P:\" (directly under a drive the separator is required) / "C:\mnt\pgfs\" -> "C:\mnt\pgfs"
		var prefix = root.TrimEnd('\\');
		if (prefix.Length == 2 && prefix[1] == ':') {
			prefix += "\\";
		}
		if (string.IsNullOrEmpty(apiPath) || apiPath == "/") {
			return prefix;
		}
		var relative = apiPath.Replace('/', '\\').TrimStart('\\');
		if (prefix.EndsWith('\\')) {
			return prefix + relative;
		}
		return prefix + "\\" + relative;
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


	/// <summary>
	/// Takes the inode out of the handle context. **Stage B: with a handle, it is resolved afresh by
	/// <c>InodeId</c> every time** (docs/design/handle-context.md).
	/// <para>
	/// Up to stage A it kept returning "the <see cref="Inode"/> object resolved at open time", so it went on
	/// **reading and writing with a stale Size / DataId** even after another mount's change or a flush's
	/// <c>data_id</c> reassignment (problem 2). <c>WriteToEndOfFile</c> (append) in particular uses
	/// <c>inode.Size</c> as-is for the write offset, so it **overwrites and corrupts whatever the other side
	/// appended** - `test_x_append_handle_sees_peer_growth` in `tests/windows/crossclient.ps1` reproduces it
	/// as **a loss of 11 bytes down to 6**.
	/// </para>
	/// <para>
	/// **The path is only used on the paths that have no handle** (an attribute query, for example). There, a
	/// throwaway context with an unknown subject is built and put on as before. The identification is
	/// concentrated in the single <see cref="Pgfs.Core.Api.Api.TryResolveHandle"/> in Core, and **null when it
	/// is gone** = the caller returns <c>FileNotFound</c> (keeping an old handle from picking up "a different
	/// file re-created under the same name" is the whole point of stage B, so it must not fall back to the path here).
	/// </para>
	/// </summary>
	/// <summary>
	/// Puts the context of the handle <c>CreateFile</c> opened onto it and **counts it in Core's reference count** (stage C-1).
	/// <para>
	/// The point is **to gather the counting into this one place**. `CreateFile` has many branches (open an
	/// existing one / open with overwrite / create a new one / a directory), and **missing a single one leaves
	/// the count never coming back**.
	/// **The throwaway contexts <see cref="Resolve"/> builds do not come through here** - there is nowhere to
	/// close them - and <see cref="Pgfs.Core.Api.OpenFileContext.Counted"/> remembers that they are not counted.
	/// </para>
	/// </summary>
	private void AttachHandle(Inode inode, AuditContext? audit, bool writeThrough, ref DokanFileInfo info) {
		var open = new OpenFileContext(inode, audit, writeThrough);
		info.Context = open;
		this.api.OpenHandle(open);
	}

	private Inode? Resolve(string path, ref DokanFileInfo info) {
		if (info.Context is OpenFileContext open) {
			return this.api.TryResolveHandle(open);
		}
		var inode = this.api.GetByPath(path);
		if (inode == null) {
			return null;
		}
		// The paths that hold no handle (an attribute query, for example) build a context with an unknown subject.
		info.Context = new OpenFileContext(inode, null);
		return inode;
	}

	// ------------------------------------------------------------------
	// Mounted / Unmounted
	// ------------------------------------------------------------------

	public NtStatus Mounted(ReadOnlyNativeMemory<char> mountPointPtr, ref DokanFileInfo info) {
		var reported = mountPointPtr.Span.ToString();
		Logger.Info("Mounted: ", reported);
		// The notification destination is settled here (the configured value = the request, this = where it was actually assigned).
		if (!string.IsNullOrEmpty(reported)) {
			this.actualMountPoint = reported;
		}
		return DokanResult.Success;
	}

	public NtStatus Unmounted(ref DokanFileInfo info) {
		Logger.Info("Unmounted");
		this.RequestStop();
		return DokanResult.Success;
	}

	/// <summary>
	/// Releases the wait in <see cref="Run"/> (= proceeds to the unmount). The stop signal (the handler on the
	/// assign.pgfs side) and <see cref="Unmounted"/> can race, so Signal is called **exactly once**
	/// (signalling a CountdownEvent past 0 throws InvalidOperationException).
	/// <para>From the second call on it does nothing = **calling it again is safe**. It is harmless after
	/// disposal too, so the stop-signal handler may call it without caring whether it has been disposed.</para>
	/// </summary>
	public void RequestStop() {
		lock (this.stopLock) {
			if (this.stopping) { return; }
			this.stopping = true;
			this.running.Signal();
		}
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
		volumeSerialNumber = this.StableVolumeSerial(label);
		return DokanResult.Success;
	}

	/// <summary>
	/// A volume serial **that does not change across remounts**.
	/// <para>
	/// It used to be <c>label.GetHashCode()</c>, but .NET's <c>string.GetHashCode</c> is **randomised per process**, so every
	/// remount looked like "a different volume" from Windows. Tracking by shortcuts (.lnk) and tools that identify a file by
	/// the pair of <c>FileIndex</c> and volume serial would take the same file for a different one. It is derived with FNV-1a
	/// from **what decides the filesystem** (the database name / the schema / the prefix / the label). The host name is not
	/// included, so that clients pointing at the same filesystem under different names do not get different serials.
	/// </para>
	/// </summary>
	private uint StableVolumeSerial(string label) {
		var db = this.api.Config.Database;
		var identity = $"{db.Connection.Database}/{db.SchemaName}/{db.Prefix}/{label}";
		var hash = 2166136261u;
		foreach (var b in System.Text.Encoding.UTF8.GetBytes(identity)) {
			// Wrapping around is what FNV-1a relies on (no exception even in a checked build).
			unchecked {
				hash ^= b;
				hash *= 16777619u;
			}
		}
		return hash;
	}

	public NtStatus GetDiskFreeSpace(
		out long freeBytesAvailable,
		out long totalNumberOfBytes,
		out long totalNumberOfFreeBytes,
		ref DokanFileInfo info
	) {
		// df's capacity/free use the real measurement if the server-side statfs() (plperlu) exists, else the nominal capacity (docs/df-support.md).
		var (capacity, free) = this.api.GetStatFs();
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
		// Throwing a Core exception back at the driver makes DokanNet round it into a generic error
		// (STATUS_DEVICE_NOT_FUNCTIONING) and leaves nothing but `Throw` in the log. **It must be caught here and
		// turned into an NTSTATUS.**
		// For example, a create during write-back's error state comes up as an InvalidOperationException
		// (observed in the metadata write-back + defer conflict test).
		try {
			return this.CreateFileCore(fileNamePtr, access, share, mode, options, attributes, ref info);
		} catch (Core.Api.Api.ParentVanishedException ex) {
			// The parent directory was deleted by another client just before the creation. **It means something
			// different from AlreadyExists (= a name collision)**, so it is returned with the NTSTATUS for a missing
			// path. Swallowing it and creating anyway would leave an orphan in the database that the filesystem cannot
			// reach (a parent deletion can orphan a create).
			Core.Logging.Logger.Warning("CreateFile: the parent disappeared just before the creation: ", NormalizePath(fileNamePtr), " ", ex.Message);
			return DokanResult.PathNotFound;
		} catch (Exception ex) {
			Core.Logging.Logger.Error("CreateFile failed: ", NormalizePath(fileNamePtr), " ", ex.Message);
			return DokanResult.Error;
		}
	}

	private NtStatus CreateFileCore(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileAccess access,
		FileShare share,
		FileMode mode,
		FileOptions options,
		FileAttributes attributes,
		ref DokanFileInfo info
	) {
		// The caller can only be obtained **inside this CreateFile**. The settled subject is put on the handle
		// (OpenFile), and the mutating operations after that (Cleanup / MoveFile / SetFileAttributes / SetFileSecurity) use it.
		var (audit, ownerUname) = this.CaptureCaller(ref info);
		AuditContext.Current = audit;
		// FILE_FLAG_WRITE_THROUGH is a per-handle durability demand. It is carried along in the handle context so
		// a full barrier can be raised on every WriteFile (keeping the "once written, it is persisted" contract even with write-back on).
		var writeThrough = (options & FileOptions.WriteThrough) == FileOptions.WriteThrough;
		var path = NormalizePath(fileNamePtr);
		var existing = this.api.GetByPath(path);
		// Windows-shareable concept: the open/create logic is OS-independent.
		var wantsDirectory = info.IsDirectory || (options & FileOptions.DeleteOnClose) == 0
								 && (attributes & FileAttributes.Directory) == FileAttributes.Directory;

		// **Do not let Windows create a name that can be created but not handled** (docs/design/namespace-policy.md).
		// It is only checked **when creating a new one** - **existing ones can be opened**, so a reserved name or a
		// trailing-dot file created from Linux can still be read from Windows (rejecting those would break the shared namespace).
		var creating = mode == FileMode.CreateNew || mode == FileMode.Create || mode == FileMode.OpenOrCreate;
		if (creating && existing == null && FileSystemUtils.IsUnsafeWindowsName(Pgfs.Core.Utility.PathParser.SplitParent(path).Name)) {
			Core.Logging.Logger.Warning("refused to create a name Windows cannot handle: ", path,
				" (a reserved name, or a trailing space or dot. Existing ones can still be opened)");
			return DokanResult.InvalidName;
		}

		switch (mode) {
		case FileMode.CreateNew:
			// The local non-existence check (the GetByPath above) is only the fast path, and **it is no exclusion against another mount**.
			// CREATE_NEW's "fail if it already exists" is settled by the database's unique constraint (exclusive: true).
			if (existing != null) {
				return DokanResult.FileExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, exclusive: true, audit, ownerUname, writeThrough, ref info);

		case FileMode.Create:
			if (existing != null) {
				// Discard the contents of an existing file (not allowed for directories).
				if (existing.IsDirectory) {
					return DokanResult.AccessDenied;
				}
				// Returning success when the truncate failed would look like "overwritten successfully" while the old content is still there.
				if (!this.api.TruncateData(existing, 0)) {
					Core.Logging.Logger.Error("CreateFile(Create): the truncate failed: ", path);
					return DokanResult.Error;
				}
				this.AttachHandle(existing, audit, writeThrough, ref info);
				return DokanResult.AlreadyExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, exclusive: false, audit, ownerUname, writeThrough, ref info);

		case FileMode.Open:
			if (existing == null) {
				return wantsDirectory switch {
					true  => DokanResult.PathNotFound,
					false => DokanResult.FileNotFound,
				};
			}
			// Do not let a directory request open a regular file (NotADirectory, as DokanNet's contract requires).
			if (wantsDirectory && !existing.IsDirectory) {
				return DokanResult.NotADirectory;
			}
			this.AttachHandle(existing, audit, writeThrough, ref info);
			info.IsDirectory = existing.IsDirectory;
			return DokanResult.Success;

		case FileMode.OpenOrCreate:
			if (existing != null) {
				if (wantsDirectory && !existing.IsDirectory) {
					return DokanResult.NotADirectory;
				}
				this.AttachHandle(existing, audit, writeThrough, ref info);
				info.IsDirectory = existing.IsDirectory;
				return DokanResult.AlreadyExists;
			}
			return this.DoCreate(path, wantsDirectory, attributes, exclusive: false, audit, ownerUname, writeThrough, ref info);

		case FileMode.Truncate:
			if (existing == null) {
				return DokanResult.FileNotFound;
			}
			if (existing.IsDirectory) {
				return DokanResult.AccessDenied;
			}
			if (!this.api.TruncateData(existing, 0)) {
				Core.Logging.Logger.Error("CreateFile(Truncate): the truncate failed: ", path);
				return DokanResult.Error;
			}
			this.AttachHandle(existing, audit, writeThrough, ref info);
			return DokanResult.Success;

		case FileMode.Append:
			if (existing == null) {
				return this.DoCreate(path, wantsDirectory, attributes, exclusive: false, audit, ownerUname, writeThrough, ref info);
			}
			if (existing.IsDirectory) {
				return DokanResult.AccessDenied;
			}
			this.AttachHandle(existing, audit, writeThrough, ref info);
			// info.WriteToEndOfFile is read-only. With FileMode.Append the Dokan kernel side sets the
			// WriteToEndOfFile flag on the subsequent WriteFile calls for us.
			return DokanResult.Success;

		default:
			return DokanResult.InvalidParameter;
		}
	}

	/// <param name="exclusive">
	/// <c>true</c> = the equivalent of CREATE_NEW. **A name collision is decided by the database's unique
	/// constraint rather than by a local check**, so even with a simultaneous create from another mount or
	/// another client only one of them succeeds (it falls back to a write-through create even when metadata
	/// write-back is enabled = synchronization heuristic c).
	/// </param>
	private NtStatus DoCreate(string path, bool asDirectory, FileAttributes attributes, bool exclusive, AuditContext? audit, string? ownerUname, bool writeThrough, ref DokanFileInfo info) {
		var (parentPath, name) = Pgfs.Core.Utility.PathParser.SplitParent(path);
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

		// **The owner is decided from the requester.** When it cannot be obtained it falls back (to nobody by
		// default) and **is never passed off as the running process's user** - that would create the accident of a
		// file someone else made looking like one's own.
		var uname = ownerUname;
		if (uname == null) {
			uname = this.users.FallbackUname;
			Core.Logging.Logger.Warning("the requester cannot be resolved, falling the owner back to: ", path, " -> ", uname);
		}
		// **The group is inherited from the parent directory.** A Windows token's primary group is effectively
		// `Domain Users` / `None` and is not used in permission decisions either, so inheriting makes the mode's
		// group bits mean something.
		// This deliberately differs from the Linux default (the creator's primary group) (docs/design/windows-parity.md).
		var gname = string.IsNullOrEmpty(parent.gname) switch {
			true  => this.users.FallbackGname,
			false => parent.gname,
		};
		// The permissions default to a Linux-like 0755 (a directory) / 0644 (a file)
		Inode? created;
		if (asDirectory) {
			// **mkdir is deliberately left asynchronous** (Core's `Api.CreateDirectory` uses `exclusive: false`).
			// Making it synchronous would mean **no pending directory can come into existence in principle, which
			// turns the ancestor-chain INSERT and the adoption of an existing id for dir/dir (Rekey) into unreachable
			// code** (the ruling of as-built difference 5 of stage 2; `test_meta_exclusive_create_is_write_through` in
			// `wbmeta.sh` pins that ruling down).
			// **By default the database's unique constraint `(parent_id, name)` rejects a same-name mkdir.**
			// Only with `write_back_metadata` on does room remain for a collision to turn into a success through
			// pending adoption (docs/design/windows-parity.md, on not making `mkdir` synchronous).
			created = this.api.CreateDirectory(parent.Id, name, uname, gname, 0x1ED); // 0755
			if (created == null) {
				return DokanResult.AlreadyExists;
			}
			info.IsDirectory = true;
		} else {
			var mode = ((attributes & FileAttributes.ReadOnly) != 0) switch {
				true  => 0x124 /*0444*/,
				false => 0x1A4 /*0644*/,
			};
			created = this.api.CreateFile(parent.Id, name, uname, gname, mode, exclusive);
			if (created == null) {
				// The loser of an exclusive create (a unique constraint violation) lands here too = the correct failure of CREATE_NEW.
				return DokanResult.AlreadyExists;
			}
		}
		// Cache it under the full path so the immediately following getattr does not hit the DB.
		this.api.InodeCache.Put(created, path);
		this.AttachHandle(created, audit, writeThrough, ref info);
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// Cleanup / CloseFile
	// ------------------------------------------------------------------

	public void Cleanup(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		this.ApplyAuditContext(ref info);
		// write-back: settle whatever is unflushed at the point the handle is closed (no need to write when it is waiting to be deleted).
		this.FlushOnCleanup(ref info);
		// When DeletePending is set, the actual deletion happens here (the Windows way of doing it).
		// Stage B: the target to delete is **resolved afresh from the handle's id** as well (do not delete from a stale snapshot).
		// If it is already gone the re-resolution returns null and this whole clause is skipped.
		if (info.DeletePending && info.Context is OpenFileContext open && this.api.TryResolveHandle(open) is Inode inode) {
			try {
				if (inode.IsDirectory) {
					if (this.api.IsDirectoryEmpty(inode.Id)) {
						this.api.DeleteInode(inode);
						this.NotifyLocalDelete(fileNamePtr, isDirectory: true);
					}
				} else {
					this.api.DeleteInode(inode);
					this.NotifyLocalDelete(fileNamePtr, isDirectory: false);
				}
			} catch (Exception ex) {
				Core.Logging.Logger.Error("Cleanup delete failed: ", ex);
			}
		}
	}

	/// <summary>
	/// Notifies the Windows side about a target this mount deleted itself.
	/// <para>
	/// Measured: although it is gone from the database, **a file another process (an antivirus or an indexer)
	/// held a handle to stays in the client-side cache for more than 10 seconds, still shows up in the
	/// enumeration and can still be opened**.
	/// From the filesystem side the only lever is to prompt an invalidation with `NotifyDelete`, so it is fired
	/// for a local deletion too (the same path as a remote deletion). The deletion itself has succeeded even if
	/// it does not get through, so a failure is only logged.
	/// </para>
	/// </summary>
	private void NotifyLocalDelete(ReadOnlyNativeMemory<char> fileNamePtr, bool isDirectory) {
		var inst = this.instance;
		if (inst == null) {
			return;
		}
		var target = this.ToNotifyPath(NormalizePath(fileNamePtr));
		if (target == null) {
			return;
		}
		if (!inst.NotifyDelete(target, isDirectory)) {
			Logger.Debug("the NotifyDelete of a local deletion failed: ", target);
		}
	}

	public void CloseFile(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		// Stage C-1: **this is Dokan's only close point**. `Cleanup` is the trigger for a deletion, not a close,
		// and another handle may still be alive, so it is not counted there (the same story as putting it on `Flush` breaking FUSE).
		if (info.Context is OpenFileContext open) { this.api.CloseHandle(open); }
		info.Context = null;
	}

	/// <summary>
	/// The write-back flush at Cleanup time. A handle waiting to be deleted has nothing worth writing back, so it is skipped.
	/// Cleanup has no return value, so a failure is only logged (it is retried at the next flush trigger).
	/// <para>
	/// The decision is left to <see cref="Pgfs.Core.Api.Api.CloseInode"/>: when <c>write_back_metadata</c> is
	/// enabled, **close does not flush synchronously** (decision 2). When it is disabled, it flushes
	/// synchronously as it does with data write-back alone.
	/// </para>
	/// </summary>
	private void FlushOnCleanup(ref DokanFileInfo info) {
		if (info.DeletePending) { return; }
		if (info.Context is not OpenFileContext open) { return; }
		try {
			// Stage B: call the handle version (Core resolves it afresh by id. **A no-op when it is gone**).
			this.api.CloseInode(open);
		} catch (Exception ex) {
			Core.Logging.Logger.Error("Cleanup flush failed: ", ex);
		}
	}

	// ------------------------------------------------------------------
	// GetFileInformation / FindFiles / FindFilesWithPattern
	// ------------------------------------------------------------------

	/// <summary>
	/// The identifier Windows uses to decide "is this the same file" (<c>ByHandleFileInformation.FileIndex</c> =
	/// the <c>nFileIndex</c> of <c>GetFileInformationByHandle</c>). **It is the same formula as Linux's <c>st_ino</c>.**
	/// <para>
	/// <b>The body (<c>data_id</c>) is used</b>. pgfs keeps **one link = one inode row**, so putting `inode.Id`
	/// in would make **the siblings of a hardlink look like different files** (measured: two names pointing at
	/// the same body returned `28639` and `28640` and looked like different things although `nlink = 2`).
	/// `data_id` is **settled at create time and immutable until the final unlink**
	/// (docs/design/data-id-lifecycle.md), so writing the contents does not change the value.
	/// </para>
	/// <para>
	/// <b>The top bit is a namespace tag</b>. `inode.id` and `data_id` come from **separate sequences** and
	/// both start at 1, so returning the raw numbers would make **"the file with data_id 5" and "the directory
	/// with inode 5" the same value**. The file side gets `0x8000_0000_0000_0000` set to separate them -
	/// **the same formula as [FUSE's st_ino](../../fuse/src/FileSystem.cs)**, so **the same file returns the
	/// same value on both operating systems**. **This value is not stored in the database** (it is only
	/// computed on the way out, so the sequences hold nothing but the plain ids).
	/// </para>
	/// <para>
	/// <c>FileIndex</c> is a <c>long</c> (signed), so this value is **negative in C#**. Windows treats it as an
	/// opaque 64-bit value, so there is no real harm (confirmed by measurement). The POSIX side's
	/// <c>st_ino</c> is a <c>ulong</c>, so the same bit pattern shows up there as a huge positive number.
	/// </para>
	/// <para>
	/// <b>Things with no body (directories and symbolic links) keep `inode.Id`</b>.
	/// </para>
	/// </summary>
	private static long FileIndexOf(Inode inode) {
		if (inode.DataId is not long dataId) { return inode.Id; }
		return unchecked((long)((ulong)dataId | FileSystem.DataIndexFlag));
	}

	/// <summary>
	/// The mark (the top bit) that separates the data_id space from the inode id space in `FileIndex`.
	/// **The same value as FUSE's `st_ino`** (`src/fuse/src/FileSystem.cs`).
	/// </summary>
	private const ulong DataIndexFlag = 0x8000_0000_0000_0000UL;

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
			FileIndex = FileSystem.FileIndexOf(inode),
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
			Core.Logging.Logger.Error("ReadFile failed: ", path, " ", ex);
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
			bytesWritten = this.WriteAt(inode, offset, buffer.Span, ref info);
			// A WRITE_THROUGH handle must be **persisted by the time this returns**.
			// With write-back off, FlushInode is effectively a no-op; with it on, it becomes a full barrier that
			// writes out everything down to the pending ancestors and the dirty chunks. A failure is returned as a
			// failure of the write itself (it does not pretend to succeed).
			if (info.Context is OpenFileContext open && open.WriteThrough) {
				this.api.FlushInode(inode);
			}
			return DokanResult.Success;
		} catch (Exception ex) {
			Core.Logging.Logger.Error("WriteFile failed: ", path, " ", ex);
			bytesWritten = 0;
			return DokanResult.Error;
		}
	}

	/// <summary>
	/// Hands one <c>WriteFile</c> to Core. **Core decides where the end is** (<see cref="Pgfs.Core.Api.Api.AppendData"/>).
	/// <para>
	/// Reading <c>inode.Size</c> here to decide where to append would **grow the implementation of "where is
	/// the end" to two places, Dokan and FUSE**. For the same reason identification (which inode) was moved
	/// into Core in stage B, **where to append (where to write) is concentrated in Core too**. The limit of
	/// atomicity is settled by the doc on `Api.AppendData`.
	/// </para>
	/// </summary>
	private int WriteAt(Inode inode, long offset, ReadOnlySpan<byte> source, ref DokanFileInfo info) {
		if (!info.WriteToEndOfFile) {
			return this.api.WriteData(inode, offset, source);
		}
		if (info.Context is OpenFileContext open) {
			return this.api.AppendData(open, source);
		}
		// The path with no handle context (Resolve always puts one on, so this normally does not happen) goes to the end of the resolved inode.
		return this.api.WriteData(inode, inode.Size, source);
	}

	// ------------------------------------------------------------------
	// FlushFileBuffers
	// ------------------------------------------------------------------

	public NtStatus FlushFileBuffers(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		// With write-through it is persisted by PostgreSQL's commit = there is nothing to do.
		// With write-back (mount.write_back) whatever is unflushed is settled here - on the Windows side this and
		// Cleanup are the only things holding up the durability contract (docs/design/runtime-control-plane.md, the data write-back section).
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			// **No fail-open**: returning Success while the target to synchronize cannot even be determined lets the
			// application read FlushFileBuffers succeeding as "it is persisted" (which is a lie while write-back is on).
			// The same treatment as resolving FlushPath's fail-open on the Linux side.
			Core.Logging.Logger.Error("FlushFileBuffers: cannot resolve what to synchronize: ", path);
			return DokanResult.FileNotFound;
		}
		try {
			// A flush on a directory handle materializes "the pending children directly below plus its own pending
			// ancestors" (the same barrier as FUSE's fsyncdir. It means something with metadata write-back).
			if (inode.IsDirectory) {
				this.api.FlushDirectory(inode);
				return DokanResult.Success;
			}
			this.api.FlushInode(inode);
			return DokanResult.Success;
		} catch (Exception ex) {
			Core.Logging.Logger.Error("FlushFileBuffers failed: ", path, " ", ex);
			return DokanResult.Error;
		}
	}

	// ------------------------------------------------------------------
	// SetFileAttributes / SetFileTime
	// ------------------------------------------------------------------

	public NtStatus SetFileAttributes(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileAttributes attributes,
		ref DokanFileInfo info
	) {
		this.ApplyAuditContext(ref info);
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		// **ReadOnly touches the write bits and nothing else.** It used to **rebuild** the mode as 0444 / 0644 /
		// 0755, which **lost the existing mode**:
		//   - adding ReadOnly to a `0750` directory and taking it off again left `0755` (group/other widened)
		//   - **adding ReadOnly to a directory made it 0444, dropping the x bit so Linux could no longer enter it**
		// The Windows side holds no mode so it could not notice, and it was **broken only as seen from Linux**.
		var mode = ReadOnlyApplied(inode.Mode, (attributes & FileAttributes.ReadOnly) != 0);
		// **A request that changes nothing returns success without touching the database (idempotent).**
		// `Remove-Item` calls SetFileAttributes before a deletion in order to "take ReadOnly off", so firing an
		// UPDATE here every time would mean **the deletion of a pending inode whose flush is permanently failing
		// fails first at the attribute change, and the one recovery path there is - "delete it on the loser's
		// side and recover" - becomes unusable from Windows**
		// (observed in the metadata write-back + defer conflict test).
		if (this.IsAttributeChangeNoop(inode, mode, attributes)) {
			Logger.Debug("SetFileAttributes: nothing changes, so the database is left alone: ", path);
			return DokanResult.Success;
		}
		try {
			if (!this.api.UpdateMode(inode.Id, mode)) {
				return DokanResult.Error;
			}
			// Hidden / System / Archive have no counterpart on the Linux side, so they are stored in an xattr (`user.win_attrs`).
			// When the mask is empty the xattr is deleted to keep the JSONB clean ([FileSystemUtils.SaveWinAttrs](FileSystemUtils.cs)).
			FileSystemUtils.SaveWinAttrs(this.api, inode.Id, attributes);
			return DokanResult.Success;
		} catch (Exception ex) {
			// Throwing the exception back at the driver makes DokanNet round it into a generic error (and the log shows nothing but Throw).
			Core.Logging.Logger.Error("SetFileAttributes failed: ", path, " ", ex.Message);
			return DokanResult.Error;
		}
	}

	/// <summary>
	/// Whether an attribute change request is effectively a no-op (the mode and Hidden/System/Archive are all as they already are).
	/// The current Hidden/System/Archive are read from the xattr alone (the dot-file heuristic is not a stored
	/// value, so "specifying Hidden on a file that only looks Hidden through the heuristic" does need storing =
	/// it is not a no-op).
	/// </summary>
	/// <summary>
	/// Reflects the <c>ReadOnly</c> attribute in **the write bits of <c>st_mode</c> and nothing else**.
	/// <list type="bullet">
	///   <item><b>Setting it</b>: drops **all** the write bits (0222). <see cref="FileSystemUtils.IsWritable"/>
	///     judges it writable **if +w is on any of owner / group / other**, so dropping only owner would not
	///     look ReadOnly from Windows.</item>
	///   <item><b>Clearing it</b>: restores **owner's write (0200) only**.</item>
	/// </list>
	/// <para>
	/// <b>Not rebuilding it to a default value</b> is the point. It used to assign 0444 / 0644 / 0755, so
	/// <b>adding ReadOnly to a directory made it 0444, dropping the x bit so `cd` from Linux stopped working</b>.
	/// The Windows side holds no mode so it could not notice, and it was **broken only as seen from Linux**.
	/// The case of a mode such as `0750` widening to `0755` over a round trip is fixed at the same time.
	/// </para>
	/// <para>
	/// <b>What comes back exactly over a round trip is a mode with write on the owner alone</b> (0755 / 0644 /
	/// 0750 / 0600 and so on - everything created from Windows is like that). **When group / other had write,
	/// those two are lost over the round trip** - there is nowhere on the POSIX side to remember "the original
	/// write bits". Remembering them would mean adding them to the win-attrs xattr, but **it does not happen
	/// with a mode created from Windows**, so for now it is accepted and written down in the docs (docs/Assign.md).
	/// </para>
	/// </summary>
	private static int ReadOnlyApplied(int mode, bool readOnly) {
		if (readOnly) { return mode & ~0x92; }
		return mode | 0x80;
	}

	private bool IsAttributeChangeNoop(Inode inode, int desiredMode, FileAttributes attributes) {
		if (inode.Mode != desiredMode) {
			return false;
		}
		const FileAttributes mask = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
		var stored = FileSystemUtils.LoadWinAttrs(this.api, inode.Id);
		if (stored is not FileAttributes current) {
			// An unsaved dot file **already looks Hidden through the heuristic**, so a "clear Hidden" request has to be
			// saved as well or it falls back to the heuristic (`test_dotfile_unhide_sticks`). It must not be made a no-op.
			if (inode.Name.StartsWith('.')) {
				return false;
			}
			// An unsaved regular file. If the request does not include Hidden/System/Archive either, there is nothing to write.
			return (attributes & mask) == 0;
		}
		return (current & mask) == (attributes & mask);
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

	/// <summary>
	/// Whether an operation that **destroys a body that already exists** should be refused during write-back's error state (true to refuse).
	/// <para>
	/// The decision is left to Core's <see cref="Api.CanDestroy"/>. **The adapter must not decide by looking at
	/// `WriteBackErrorState` alone** - whether something is pending or persisted is Core's internal state, and
	/// ignoring that **also stops the deletion of a pending entry and blocks B-1's means of recovery (unlink on the loser's side)**.
	/// </para>
	/// </summary>
	private bool BlocksDestroy(Inode target, string what) {
		if (this.api.CanDestroy(target, out var reason)) {
			return false;
		}
		Core.Logging.Logger.Error("refusing to ", what, " because write-back is in its error state: ", target.name, " (", reason, ")");
		return true;
	}

	public NtStatus DeleteFile(ReadOnlyNativeMemory<char> fileNamePtr, ref DokanFileInfo info) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			return DokanResult.AccessDenied;
		}
		// The actual deletion happens in Cleanup, so this only answers "can it be deleted".
		// **The ban on destruction during the error state is refused here** - Cleanup is void and cannot report a
		// failure, so getting that far would turn into the lie of "success although nothing was deleted" (the same
		// shape as the fail-open resolved on the Linux side).
		if (!this.BlocksDestroy(inode, "delete")) {
			return DokanResult.Success;
		}
		return DokanResult.Error;
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
		if (this.BlocksDestroy(inode, "delete the directory")) {
			return DokanResult.Error;
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
		this.ApplyAuditContext(ref info);
		var oldPath = NormalizePath(oldNamePtr);
		var newPath = NormalizePath(newNamePtr);
		var inode = this.api.GetByPath(oldPath);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		// **A rename onto the same target is a no-op success** (as with the POSIX rename). Core's replacement path
		// is "delete the target -> UPDATE the source", so passing the same target would delete itself. On Linux the
		// kernel VFS rejects a rename of the same inode, but **the Dokan path has no such protection**, so it is
		// closed at the boundary.
		// Core's own breakwater (the same-id check in Api.Rename) is being handled on the Linux side.
		if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) {
			return DokanResult.Success;
		}
		var (newParentPath, newName) = Pgfs.Core.Utility.PathParser.SplitParent(newPath);
		if (string.IsNullOrEmpty(newName)) {
			return DokanResult.InvalidName;
		}
		var newParent = this.api.GetByPath(newParentPath);
		if (newParent == null || !newParent.IsDirectory) {
			return DokanResult.PathNotFound;
		}
		var existing = this.api.GetByPath(newPath);
		// **A rename gets the same name check as a create** (the same condition as IsUnsafeWindowsName in
		// CreateFileCore). Before, rename skipped it, and through `\\?\` or Git Bash's `mv a CON` / `mv a 'b.'` the
		// names blocked on create could still be made from Windows. **Replacing an existing name** creates no new
		// name, so it is let through as on the create side.
		if (existing == null && FileSystemUtils.IsUnsafeWindowsName(newName)) {
			Core.Logging.Logger.Warning("rejected a rename to a name Windows cannot handle: ", oldPath, " -> ", newPath,
				" (a reserved name / a trailing space or dot)");
			return DokanResult.InvalidName;
		}
		if (existing != null && existing.Id == inode.Id) {
			// A different name pointing at the same inode (a link to the same data, for example). Letting it into the replacement delete would delete itself.
			return DokanResult.Success;
		}
		if (existing != null) {
			if (!replace) {
				return DokanResult.FileExists;
			}
			if (existing.IsDirectory != inode.IsDirectory) {
				return existing.IsDirectory switch {
					true  => DokanResult.AccessDenied,
					false => DokanResult.NotADirectory,
				};
			}
			if (existing.IsDirectory && !this.api.IsDirectoryEmpty(existing.Id)) {
				return DokanResult.DirectoryNotEmpty;
			}
			// A replacement **deletes the target**, so it is refused during the error state (when the target is persisted).
			if (this.BlocksDestroy(existing, "rename over")) {
				return DokanResult.Error;
			}
			// Persist the source's unflushed work before the replacement erases the old target (the same reason as Rename on the Fuse side).
			// A pending source is not flushed here, because the Api does "delete the target + materialize + dirty data
			// + audit" in a single tx (synchronization heuristic a). On a failure it aborts with the target untouched.
			try {
				this.api.PrepareRenameReplace(inode);
			} catch (Exception ex) {
				Core.Logging.Logger.Error("MoveFile: the flush before the replacement failed: ", oldPath, " ", ex.Message);
				return DokanResult.Error;
			}
		}
		// Deleting what is being replaced and the rename happen in the same tx on the Api side (deleting first in a separate tx would open a crash window)
		if (!this.api.Rename(inode.Id, newParent.Id, newName, existing)) {
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
		// Shrinking destroys, growing is new data. Core refuses both during the error state, so they are refused here first.
		if (this.BlocksDestroy(inode, "truncate")) {
			return DokanResult.Error;
		}
		if (!this.api.TruncateData(inode, length)) {
			return DokanResult.Error;
		}
		return DokanResult.Success;
	}

	/// <summary>
	/// The equivalent of FILE_ALLOCATION_INFORMATION. **It is a different quantity from EOF**, so it is not wired straight to SetEndOfFile.
	/// <list type="bullet">
	///   <item>the request is smaller than the current EOF -> truncate to that position (a shrinking allocation on Windows shrinks EOF too)</item>
	///   <item>the request is at or above the current EOF -> **the logical content does not change** (it is a reservation hint, so a no-op success)</item>
	/// </list>
	/// pgfs is a sparse set of bytea chunks, so there is no concept of physically reserving a "reserved area".
	/// A no-op success does not guarantee that capacity was reserved in advance (docs/design/windows-parity.md, the allocation section).
	/// </summary>
	public NtStatus SetAllocationSize(ReadOnlyNativeMemory<char> fileNamePtr, long length, ref DokanFileInfo info) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		if (inode.IsDirectory) {
			return DokanResult.AccessDenied;
		}
		if (length >= inode.Size) {
			Logger.Debug("SetAllocationSize: a growing request does not change EOF: ", path, " ", length);
			return DokanResult.Success;
		}
		if (this.BlocksDestroy(inode, "shrink the allocation")) {
			return DokanResult.Error;
		}
		if (!this.api.TruncateData(inode, length)) {
			return DokanResult.Error;
		}
		return DokanResult.Success;
	}

	// ------------------------------------------------------------------
	// LockFile / UnlockFile - **never called, because UserModeLock is not set** (the driver handles them).
	// Returning Success if they ever were called would be lying that a lock was taken, so NotImplemented is returned.
	// ------------------------------------------------------------------

	public NtStatus LockFile(ReadOnlyNativeMemory<char> fileNamePtr, long offset, long length, ref DokanFileInfo info) {
		return DokanResult.NotImplemented;
	}

	public NtStatus UnlockFile(ReadOnlyNativeMemory<char> fileNamePtr, long offset, long length, ref DokanFileInfo info) {
		return DokanResult.NotImplemented;
	}

	// ------------------------------------------------------------------
	// GetFileSecurity — project an inode (uname/gname/mode + canonical ACL) into a Windows SD.
	// ------------------------------------------------------------------

	public NtStatus GetFileSecurity(
		ReadOnlyNativeMemory<char> fileNamePtr,
		out FileSystemSecurity? security,
		AccessControlSections sections,
		ref DokanFileInfo info
	) {
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			security = null;
			return DokanResult.FileNotFound;
		}
		try {
			security = FileSystemUtils.BuildSecurity(inode, this.users, this.api);
			return DokanResult.Success;
		} catch (Exception) {
			// If projection fails, defer to the kernel's default ACL (do not break access itself).
			security = null;
			return DokanResult.NotImplemented;
		}
	}

	public NtStatus SetFileSecurity(
		ReadOnlyNativeMemory<char> fileNamePtr,
		FileSystemSecurity security,
		AccessControlSections sections,
		ref DokanFileInfo info
	) {
		this.ApplyAuditContext(ref info);
		var path = NormalizePath(fileNamePtr);
		var inode = this.Resolve(path, ref info);
		if (inode == null) {
			return DokanResult.FileNotFound;
		}
		try {
			if (!FileSystemUtils.ApplySecurity(inode, security, sections, this.users, this.api)) {
				return DokanResult.Error;
			}
			return DokanResult.Success;
		} catch (Exception ex) {
			Core.Logging.Logger.Warning("SetFileSecurity failed: ", path, " ", ex.Message);
			return DokanResult.Error;
		}
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
