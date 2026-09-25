namespace Pgfs.Dokan;

using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Principal;
using DokanNet;
using Core.Api;
using Core.Models;
using FileAccess = DokanNet.FileAccess;

/// <summary>
/// **Permission checks for access from Windows** (since v0.2.1).
/// <para>
/// Dokan does **not** check access against the security descriptor returned by <c>GetFileSecurity</c>, so
/// when <c>app.enforce_permissions</c> is true, the check is done here in POSIX order (<see cref="PermissionEvaluator"/>).
/// Only **<c>CreateFile</c> (the target being opened / the parent when creating or deleting) and <c>MoveFile</c> (the destination's parent)**
/// need checking - <c>WriteFile</c> / attribute changes / delete-on-close / SD changes after the open are stopped by the Windows
/// I/O manager against **the access granted to the handle**.
/// </para>
/// <para>
/// The design reference is docs/design/permission-interop.md, the section on checks on Windows.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class FileSystem
{
	private const FileAccess ReadBits = FileAccess.ReadData | FileAccess.ReadExtendedAttributes | FileAccess.GenericRead | FileAccess.GenericAll;
	private const FileAccess WriteBits = FileAccess.WriteData | FileAccess.AppendData | FileAccess.WriteExtendedAttributes
		| FileAccess.DeleteChild | FileAccess.GenericWrite | FileAccess.GenericAll;
	private const FileAccess ExecuteBits = FileAccess.Execute | FileAccess.GenericExecute | FileAccess.GenericAll;

	/// <summary>Whether the check is enabled (switched at run time by <c>pgfsctl config set</c>).</summary>
	private bool Enforcing => this.api.Config.App.EnforcePermissions;

	/// <summary>
	/// Builds the subject for the check from the caller's token (can only be called inside <c>CreateFile</c> - <see cref="CaptureCaller"/>).
	/// Only the token's **enabled** groups are used (<see cref="WindowsIdentity.Groups"/> excludes deny-only groups and the logon SID).
	/// Everyone is covered by the mode's other bits, so it is not added as a name (<c>other</c>).
	/// </summary>
	private AccessCaller BuildCaller(WindowsIdentity identity, string? owner) {
		var uname = owner ?? "";
		if (uname == this.users.UnknownName) {
			uname = "";
		}
		var gnames = new List<string>();
		foreach (var reference in identity.Groups ?? []) {
			if (reference is not SecurityIdentifier sid) {
				continue;
			}
			var name = this.users.GnameOf(sid);
			if (name == this.users.UnknownName || name == "other") {
				continue;
			}
			gnames.Add(name);
		}
		// Our own group name (self_gname) is not an OS group, so it is only added to requests that come as ourselves.
		if (this.users.HasSelfGname && uname.Length > 0 && uname == this.users.DefaultUname) {
			gnames.Add(this.users.DefaultGname);
		}
		var bypass = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
		return new AccessCaller { Uname = uname, Gnames = gnames, Bypass = bypass };
	}

	/// <summary>
	/// The subject when the caller could not be obtained. When checking, <see cref="AccessCaller.Unknown"/> (= checked with other's rights, not let through);
	/// when not checking, null.
	/// </summary>
	private AccessCaller? UnknownCaller() {
		if (!this.Enforcing) {
			return null;
		}
		return AccessCaller.Unknown;
	}

	/// <summary>Maps a <c>CreateFile</c> request to r/w/x. Directories use the same bits (ListDirectory = ReadData and so on).</summary>
	private static int NeedOf(FileAccess access) {
		var need = 0;
		if ((access & ReadBits) != 0) { need |= PermissionEvaluator.Read; }
		if ((access & WriteBits) != 0) { need |= PermissionEvaluator.Write; }
		if ((access & ExecuteBits) != 0) { need |= PermissionEvaluator.Execute; }
		return need;
	}

	/// <summary>
	/// The check for <c>CreateFile</c>. Returns true to allow. When it denies, the reason is logged.
	/// When <paramref name="caller"/> is null (a subject taken while checking was off), it does not check.
	/// </summary>
	private bool PermitsOpen(string path, Inode? existing, FileMode mode, FileAccess access, FileOptions options, AccessCaller? caller) {
		if (caller == null || !this.Enforcing) {
			return true;
		}
		if (existing == null) {
			return this.PermitsCreate(path, mode, caller);
		}
		// CREATE_NEW on something that exists -> returns FileExists, so it is not checked here (there is no reason to hide that it exists either).
		if (mode == FileMode.CreateNew) {
			return true;
		}
		var acl = FileSystemUtils.LoadPgfsAcl(this.api, existing.Id);
		var need = NeedOf(access);
		// Create / Truncate on an existing file throws away the content = a write. Needed even when the request has no w.
		if (mode == FileMode.Create || mode == FileMode.Truncate) {
			need |= PermissionEvaluator.Write;
		}
		if (!PermissionEvaluator.Allows(existing, acl, caller, need)) {
			return this.Deny(path, caller, "rwx=" + need);
		}
		// **DeleteChild on a sticky directory is for that directory's owner only.** When Windows is refused Delete on a file,
		// it **reopens the parent with DeleteChild and, if that succeeds, deletes it anyway** (the meaning of NTFS's FILE_DELETE_CHILD; observed).
		// On POSIX the parent's w means deleting children only when it is not sticky, so on a sticky directory DeleteChild is not mapped to w.
		if (existing.IsDirectory
			&& (access & (FileAccess.DeleteChild | FileAccess.GenericAll)) != 0
			&& (existing.Mode & Mode.S_ISVTX) != 0
			&& !caller.Bypass
			&& !PermissionEvaluator.IsOwner(existing, caller)) {
			return this.Deny(path, caller, "DeleteChild (a sticky directory: owner only)");
		}
		// Changing times / attributes: the owner or someone with w.
		if ((access & FileAccess.WriteAttributes) != 0
			&& !PermissionEvaluator.IsOwner(existing, caller)
			&& !PermissionEvaluator.Allows(existing, acl, caller, PermissionEvaluator.Write)) {
			return this.Deny(path, caller, "WriteAttributes (owner or w)");
		}
		// Changing permissions: the owner only (the same as POSIX chmod).
		if ((access & FileAccess.ChangePermissions) != 0 && !caller.Bypass && !PermissionEvaluator.IsOwner(existing, caller)) {
			return this.Deny(path, caller, "ChangePermissions (owner)");
		}
		// Changing the owner: only a bypassing subject (the same as POSIX chown).
		if ((access & FileAccess.SetOwnership) != 0 && !caller.Bypass) {
			return this.Deny(path, caller, "SetOwnership (Administrators)");
		}
		var deleting = (access & FileAccess.Delete) != 0 || (options & FileOptions.DeleteOnClose) != 0;
		if (!deleting) {
			return true;
		}
		return this.PermitsUnlink(path, existing, caller);
	}

	/// <summary>Creating: the parent's w. When there is no parent / it is not a directory, <c>DoCreate</c> returns PathNotFound, so it is let through.</summary>
	private bool PermitsCreate(string path, FileMode mode, AccessCaller caller) {
		var creating = mode == FileMode.CreateNew || mode == FileMode.Create || mode == FileMode.OpenOrCreate || mode == FileMode.Append;
		if (!creating) {
			return true;
		}
		var parent = this.api.GetByPath(Core.Utility.PathParser.SplitParent(path).ParentPath);
		if (parent == null || !parent.IsDirectory) {
			return true;
		}
		if (PermissionEvaluator.Allows(parent, FileSystemUtils.LoadPgfsAcl(this.api, parent.Id), caller, PermissionEvaluator.Write)) {
			return true;
		}
		return this.Deny(path, caller, "create (parent's w)");
	}

	/// <summary>Deleting / renaming away: the parent's w + the sticky rule. The root has no parent, so it is not checked (whether it can be deleted is decided for other reasons).</summary>
	private bool PermitsUnlink(string path, Inode target, AccessCaller caller) {
		if (path == "/") {
			return true;
		}
		var parent = this.api.GetByPath(Core.Utility.PathParser.SplitParent(path).ParentPath);
		if (parent == null) {
			return true;
		}
		if (PermissionEvaluator.CanUnlink(parent, FileSystemUtils.LoadPgfsAcl(this.api, parent.Id), target, caller)) {
			return true;
		}
		return this.Deny(path, caller, "delete / rename (parent's w + sticky)");
	}

	/// <summary>
	/// The check for <c>MoveFile</c> (the destination's parent). The source's parent was already checked by <see cref="PermitsOpen"/>
	/// when the rename handle was opened with <c>Delete</c>. When overwriting, the sticky rule is also applied to the one being overwritten.
	/// </summary>
	private bool PermitsMove(string newPath, Inode newParent, Inode? existing, ref DokanFileInfo info) {
		if (!this.Enforcing || info.Context is not OpenFileContext { Caller: AccessCaller caller }) {
			return true;
		}
		var acl = FileSystemUtils.LoadPgfsAcl(this.api, newParent.Id);
		if (!PermissionEvaluator.Allows(newParent, acl, caller, PermissionEvaluator.Write)) {
			return this.Deny(newPath, caller, "rename destination (parent's w)");
		}
		if (existing == null || PermissionEvaluator.CanUnlink(newParent, acl, existing, caller)) {
			return true;
		}
		return this.Deny(newPath, caller, "overwriting the rename destination (sticky)");
	}

	private bool Deny(string path, AccessCaller caller, string what) {
		Core.Logging.Logger.Information("access denied (no permission): ", path, " request=", what, " subject=", caller.Uname,
			" groups=", string.Join(",", caller.Gnames));
		return false;
	}
}
