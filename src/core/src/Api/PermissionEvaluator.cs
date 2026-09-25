namespace Pgfs.Core.Api;

using System;
using Models;

/// <summary>
/// **Permission checks in POSIX order** (owner -> named user -> group / named group -> other).
/// <para>
/// Windows (Dokan) does not check access against the security descriptor the filesystem returns, so assign uses
/// this to check on its own (it started when Windows could write directly under a `root:root 755` root).
/// On Linux the kernel checks the mode through `default_permissions`, so this is not used there for now (it lives
/// in Core for the future decision [10]).
/// The design is defined in docs/design/permission-interop.md, the Windows evaluation section.
/// </para>
/// <para>
/// Names are compared case-insensitively (both the stored names and the subject are normalized, so in practice it is
/// an exact match).
/// **The named entries of the canonical ACL are not narrowed by the group bits of the mode** - the stored form has
/// no mask, and the mask is computed in the Linux projection (decision [5]).
/// </para>
/// </summary>
public static class PermissionEvaluator
{
	/// <summary>Read (r).</summary>
	public const int Read = 4;

	/// <summary>Write (w).</summary>
	public const int Write = 2;

	/// <summary>Execute / search (x).</summary>
	public const int Execute = 1;

	/// <summary>
	/// Whether <paramref name="caller"/> has <paramref name="need"/> (a combination of r/w/x) on <paramref name="inode"/>.
	/// Always true when <paramref name="need"/> is 0.
	/// </summary>
	public static bool Allows(Inode inode, PgfsAcl? acl, AccessCaller caller, int need) {
		if (need == 0 || caller.Bypass) {
			return true;
		}
		var mode = inode.Mode;
		// 1. owner - on a match, only the owner bits decide (it does not fall through to group / other).
		if (IsOwner(inode, caller)) {
			return Has((mode >> 6) & 7, need);
		}
		// 2. named user - on a match, only that entry decides.
		if (acl != null) {
			foreach (var e in acl.Entries) {
				if (e.PrincipalType != "user" || !Same(e.PrincipalName, caller.Uname)) {
					continue;
				}
				return Has(RightsOf(e), need);
			}
		}
		// 3. group class - allowed if **any one matching** entry among the owning group and the named groups has all of need.
		//    If some matched but none has it, denied (it does not fall through to other). As in POSIX, rights are not unioned.
		var ownGroup = InGroup(caller, inode.GroupName);
		if (ownGroup && Has((mode >> 3) & 7, need)) {
			return true;
		}
		var matched = ownGroup;
		if (acl != null) {
			foreach (var e in acl.Entries) {
				if (e.PrincipalType != "group" || !InGroup(caller, e.PrincipalName)) {
					continue;
				}
				if (Has(RightsOf(e), need)) {
					return true;
				}
				matched = true;
			}
		}
		if (matched) {
			return false;
		}
		// 4. other
		return Has(mode & 7, need);
	}

	/// <summary>Whether the caller is the owner (a bypassing subject is not treated as the owner - the caller checks <see cref="AccessCaller.Bypass"/> separately).</summary>
	public static bool IsOwner(Inode inode, AccessCaller caller) {
		return caller.Uname.Length > 0 && Same(inode.UserName, caller.Uname);
	}

	/// <summary>
	/// Whether <paramref name="target"/> inside <paramref name="parent"/> can be deleted / renamed.
	/// Needs w on the parent. **If the parent is sticky (`01000`), the caller must be the owner of the target, the owner of
	/// the parent, or bypassing** (same as `/tmp`).
	/// The x (search) of intermediate directories is not checked (to match Windows's "bypass traverse checking").
	/// </summary>
	public static bool CanUnlink(Inode parent, PgfsAcl? parentAcl, Inode target, AccessCaller caller) {
		if (caller.Bypass) {
			return true;
		}
		if (!Allows(parent, parentAcl, caller, Write)) {
			return false;
		}
		if ((parent.Mode & Mode.S_ISVTX) == 0) {
			return true;
		}
		return IsOwner(target, caller) || IsOwner(parent, caller);
	}

	private static bool Has(int granted, int need) => (granted & need) == need;

	private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private static bool InGroup(AccessCaller caller, string? gname) {
		if (string.IsNullOrEmpty(gname)) {
			return false;
		}
		foreach (var g in caller.Gnames) {
			if (Same(g, gname)) {
				return true;
			}
		}
		return false;
	}

	private static int RightsOf(PgfsAclEntry e) {
		var bits = 0;
		if (e.CanRead) { bits |= Read; }
		if (e.CanWrite) { bits |= Write; }
		if (e.CanExecute) { bits |= Execute; }
		return bits;
	}
}
