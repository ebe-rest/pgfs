namespace Pgfs.Assign;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Lib.Api;
using Lib.Models;

/// <summary>
/// Translates PGFS's Linux-style inode attributes (st_mode, etc.) into Windows <see cref="FileAttributes"/>.
/// Windows-specific logic (root -&gt; administrator name resolution, SetOwnership, etc.) also lives here.
///
/// Uses constants from <see cref="Lib.Api.Mode"/> such as S_IFDIR / S_IFREG / S_IFLNK / S_IRWXU.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileSystemUtils
{
	/// <summary>
	/// Hidden / System / Archive have no Linux-side equivalent, so they are stored in an xattr.
	/// The value is JSON <c>{ "hidden": bool, "system": bool, "archive": bool }</c>. The <c>user.</c> namespace
	/// makes it visible to Linux getfattr too. <c>compressed</c> is a future addition.
	/// </summary>
	public const string WinAttrsXattrKey = "user.win.attrs";

	/// <summary>The bits held in the xattr. The rest (ReadOnly / Directory / ReparsePoint / Normal) are managed via other routes.</summary>
	public const FileAttributes WinAttrsMask = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

	/// <summary>
	/// Builds Windows FileAttributes from an inode's st_mode and name.
	/// Hidden / System / Archive decision logic:
	/// <list type="bullet">
	/// <item>The xattr <c>user.win_attrs</c> is **present** (even if 0) -&gt; the Windows side has explicitly
	///       touched the attributes, so trust the value as-is.</item>
	/// <item>The xattr <c>user.win_attrs</c> is **absent** -&gt; not touched yet, so apply the fallback
	///       heuristic of inferring a Linux-style dotfile as Hidden.</item>
	/// </list>
	/// </summary>
	public static FileAttributes ToAttributes(Inode inode, WindowsUserResolver users, Api api) {
		var attr = FileAttributes.None;

		if (inode.IsDirectory) {
			attr |= FileAttributes.Directory;
		} else if ((inode.Mode & Mode.S_IFLNK) == Mode.S_IFLNK) {
			attr |= FileAttributes.ReparsePoint;
		} else {
			// Regular file: set Normal only when no other flags are present.
			attr |= FileAttributes.Normal;
		}

		// Hidden / System / Archive: prefer the xattr; fall back to the dotfile heuristic if absent.
		var stored = LoadWinAttrs(api, inode.Id);
		if (stored is FileAttributes winAttrs) {
			attr |= winAttrs;
		} else if (inode.Name.StartsWith('.')) {
			attr |= FileAttributes.Hidden;
		}

		if (!IsWritable(inode, users)) {
			attr |= FileAttributes.ReadOnly;
		}

		// Normal cannot coexist with other flags (strip it once Hidden / ReadOnly / Archive etc. are set).
		if (attr != FileAttributes.Normal && (attr & FileAttributes.Normal) != 0) {
			attr &= ~FileAttributes.Normal;
		}

		return attr;
	}

	/// <summary>
	/// Reads the Windows attributes (Hidden / System / Archive) stored in the xattr.
	/// Returns null if unset (ToAttributes then falls back to the dotfile heuristic).
	/// </summary>
	public static FileAttributes? LoadWinAttrs(Api api, long inodeId) {
		var raw = api.GetXAttr(inodeId, WinAttrsXattrKey);
		if (raw == null) {
			return null;
		}
		WinAttrsDto? dto = null;
		try {
			dto = JsonSerializer.Deserialize<WinAttrsDto>(raw);
		} catch (JsonException) {
			// Treat a corrupt value as touched-but-none (do not revive the heuristic).
			return FileAttributes.None;
		}
		if (dto == null) {
			return FileAttributes.None;
		}
		var attr = FileAttributes.None;
		if (dto.hidden) { attr |= FileAttributes.Hidden; }
		if (dto.system) { attr |= FileAttributes.System; }
		if (dto.archive) { attr |= FileAttributes.Archive; }
		return attr;
	}

	/// <summary>
	/// Extracts the Hidden / System / Archive part from the given <see cref="FileAttributes"/> and stores it
	/// in the xattr. Even when the mask is empty (all OFF), it writes 4 bytes of 0 rather than **deleting**
	/// the xattr. This is because we want a two-valued treatment: "xattr present = the Windows side
	/// explicitly touched the attributes" vs "xattr absent = not touched yet (apply the heuristic)". It
	/// relates to the dotfile fallback in <see cref="ToAttributes"/>: deleting the xattr right after a
	/// dotfile is un-hidden in Explorer would revive the heuristic on the next read and re-hide it — a UX
	/// bug this design avoids.
	/// </summary>
	public static void SaveWinAttrs(Api api, long inodeId, FileAttributes attributes) {
		var dto = new WinAttrsDto {
			hidden = (attributes & FileAttributes.Hidden) != 0,
			system = (attributes & FileAttributes.System) != 0,
			archive = (attributes & FileAttributes.Archive) != 0,
		};
		var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
		api.SetXAttr(inodeId, WinAttrsXattrKey, bytes, createOnly: false, replaceOnly: false);
	}

	/// <summary>The JSON shape of user.win.attrs (docs/permission-interop.md). compressed is a future addition.</summary>
	private sealed class WinAttrsDto
	{
		public bool hidden { get; set; }
		public bool system { get; set; }
		public bool archive { get; set; }
	}

	/// <summary>
	/// Determines whether the currently running user can write to the inode.
	/// </summary>
	public static bool IsWritable(Inode inode, WindowsUserResolver users) {
		var mode = inode.Mode;
		// other +w
		if ((mode & Mode.S_IWOTH) != 0) {
			return true;
		}
		// owner +w. Both the stored name and DefaultUname are normalized + Linux-name mapped, so compare names directly
		// (the root <-> Administrator alias is absorbed by the WindowsUserResolver mapping).
		if (string.Equals(inode.UserName, users.DefaultUname, StringComparison.OrdinalIgnoreCase)
			&& (mode & Mode.S_IWUSR) != 0) {
			return true;
		}
		// group +w
		if (string.Equals(inode.GroupName, users.DefaultGname, StringComparison.OrdinalIgnoreCase)
			&& (mode & Mode.S_IWGRP) != 0) {
			return true;
		}
		return false;
	}

	// ------------------------------------------------------------------
	// ACL projection (docs/permission-interop.md)
	// ------------------------------------------------------------------

	/// <summary>The xattr key holding the canonical ACL document (POSIX-only / allow).</summary>
	public const string PgfsAclXattrKey = "user.pgfs_acl";

	/// <summary>Reads the inode's canonical ACL document. null if unset / malformed (project from the mode base 3 classes only).</summary>
	public static PgfsAcl? LoadPgfsAcl(Api api, long inodeId) {
		var raw = api.GetXAttr(inodeId, PgfsAclXattrKey);
		if (raw == null) {
			return null;
		}
		return PgfsAcl.Parse(raw);
	}

	/// <summary>
	/// Projects an inode into a Windows security descriptor (owner / group / DACL) (docs/permission-interop.md).
	/// owner/group are uname/gname -&gt; SID; the DACL is the st_mode base 3 classes (owner/group/Everyone allow ACEs)
	/// plus the named entries of the canonical ACL document. deny / inheritance / ACE order are not held (POSIX projection).
	/// </summary>
	public static FileSystemSecurity BuildSecurity(Inode inode, WindowsUserResolver users, Api api) {
		FileSystemSecurity sec = inode.IsDirectory ? new DirectorySecurity() : new FileSecurity();
		var ownerSid = users.UserSidOf(inode.UserName);
		var groupSid = users.GroupSidOf(inode.GroupName);
		sec.SetOwner(ownerSid);
		sec.SetGroup(groupSid);

		var mode = inode.Mode;
		var isDir = inode.IsDirectory;
		// base 3 classes (from st_mode)
		AddAllow(sec, ownerSid, (mode & Mode.S_IRUSR) != 0, (mode & Mode.S_IWUSR) != 0, (mode & Mode.S_IXUSR) != 0, isDir);
		AddAllow(sec, groupSid, (mode & Mode.S_IRGRP) != 0, (mode & Mode.S_IWGRP) != 0, (mode & Mode.S_IXGRP) != 0, isDir);
		var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
		AddAllow(sec, everyone, (mode & Mode.S_IROTH) != 0, (mode & Mode.S_IWOTH) != 0, (mode & Mode.S_IXOTH) != 0, isDir);

		// named ACL (canonical document)
		var acl = LoadPgfsAcl(api, inode.Id);
		if (acl != null) {
			foreach (var e in acl.Entries) {
				var sid = e.PrincipalType == "group" ? users.GroupSidOf(e.PrincipalName) : users.UserSidOf(e.PrincipalName);
				AddAllow(sec, sid, e.CanRead, e.CanWrite, e.CanExecute, isDir);
			}
		}
		return sec;
	}

	/// <summary>Maps rwx to Windows rights and adds one allow ACE. Does nothing if the rights are empty.</summary>
	private static void AddAllow(FileSystemSecurity sec, SecurityIdentifier sid, bool r, bool w, bool x, bool isDir) {
		var rights = MapRights(r, w, x);
		if (rights == 0) {
			return;
		}
		sec.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
	}

	/// <summary>POSIX rwx -&gt; Windows <see cref="FileSystemRights"/>. No inheritance flags (read projection).</summary>
	private static FileSystemRights MapRights(bool r, bool w, bool x) {
		FileSystemRights rights = 0;
		if (r) {
			rights |= FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions;
		}
		if (w) {
			rights |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes;
		}
		if (x) {
			rights |= FileSystemRights.ExecuteFile;
		}
		return rights;
	}

	/// <summary>
	/// Reverse-projects a received Windows security descriptor into pgfs owner/group/mode/canonical ACL and saves it
	/// (docs/permission-interop.md). If a group SID is set as the owner, dispatch to owner=nobody / group=that-group.
	/// The DACL owner/group/Everyone allow ACEs map to the st_mode base 3 classes, named allow ACEs to user.pgfs_acl.
	/// deny / ACE order / inheritance are dropped on projection. Only the requested <paramref name="sections"/> are
	/// updated. Returns false on failure.
	/// </summary>
	public static bool ApplySecurity(Inode inode, FileSystemSecurity security, AccessControlSections sections, WindowsUserResolver users, Api api) {
		var newUname = inode.UserName;
		var newGname = inode.GroupName;
		var ownerOrGroupChanged = false;

		var hasOwner = (sections & AccessControlSections.Owner) != 0;
		var hasGroup = (sections & AccessControlSections.Group) != 0;
		var hasAccess = (sections & AccessControlSections.Access) != 0;

		if (hasGroup) {
			var gsid = security.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier;
			if (gsid != null) {
				newGname = users.GnameOf(gsid);
				ownerOrGroupChanged = true;
			}
		}
		if (hasOwner) {
			var osid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
			if (osid != null) {
				var ownerIsGroup = users.IsGroupSid(osid);
				// A group was set as the owner: owner=nobody / group=that-group.
				if (ownerIsGroup) {
					newUname = "nobody";
					newGname = users.GnameOf(osid);
					ownerOrGroupChanged = true;
				}
				if (!ownerIsGroup) {
					newUname = users.UnameOf(osid);
					ownerOrGroupChanged = true;
				}
			}
		}

		if (ownerOrGroupChanged) {
			if (!api.UpdateOwner(inode.Id, newUname, newGname)) {
				return false;
			}
		}

		if (!hasAccess) {
			return true;
		}

		// Classify the DACL using the SD's own owner/group SID; fall back to resolving from the updated names.
		var ownerSid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? users.UserSidOf(newUname);
		var groupSid = security.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier ?? users.GroupSidOf(newGname);
		var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

		var ownerBits = 0;
		var groupBits = 0;
		var otherBits = 0;
		var acl = new PgfsAcl();
		var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
		foreach (FileSystemAccessRule rule in rules) {
			// deny ACEs are dropped on projection.
			if (rule.AccessControlType != AccessControlType.Allow) {
				continue;
			}
			var sid = rule.IdentityReference as SecurityIdentifier;
			if (sid == null) {
				continue;
			}
			var (r, w, x) = RightsToRwx(rule.FileSystemRights);
			if (sid.Equals(ownerSid)) {
				ownerBits |= RwxToBits(r, w, x, Mode.S_IRUSR, Mode.S_IWUSR, Mode.S_IXUSR);
				continue;
			}
			if (sid.Equals(groupSid)) {
				groupBits |= RwxToBits(r, w, x, Mode.S_IRGRP, Mode.S_IWGRP, Mode.S_IXGRP);
				continue;
			}
			if (sid.Equals(everyone)) {
				otherBits |= RwxToBits(r, w, x, Mode.S_IROTH, Mode.S_IWOTH, Mode.S_IXOTH);
				continue;
			}
			// named ACL entry
			var isGroup = users.IsGroupSid(sid);
			var ptype = "user";
			var pname = users.UnameOf(sid);
			if (isGroup) {
				ptype = "group";
				pname = users.GnameOf(sid);
			}
			acl.Entries.Add(new PgfsAclEntry { PrincipalType = ptype, PrincipalName = pname, Rights = RwxString(r, w, x) });
		}

		// Replace only the base 3 classes (keep the type / setuid / setgid / sticky bits).
		var newMode = (inode.Mode & ~0x1FF) | ownerBits | groupBits | otherBits;
		if (newMode != inode.Mode) {
			if (!api.UpdateMode(inode.Id, newMode)) {
				return false;
			}
		}
		// Sync the named ACL with the DACL (overwrite even if empty).
		api.SetXAttr(inode.Id, PgfsAclXattrKey, acl.ToBytes(), createOnly: false, replaceOnly: false);
		return true;
	}

	/// <summary>Extracts POSIX rwx from Windows rights (the ReadData / WriteData / ExecuteFile bits).</summary>
	private static (bool r, bool w, bool x) RightsToRwx(FileSystemRights rights) {
		var r = (rights & FileSystemRights.ReadData) != 0;
		var w = (rights & FileSystemRights.WriteData) != 0;
		var x = (rights & FileSystemRights.ExecuteFile) != 0;
		return (r, w, x);
	}

	/// <summary>Folds each rwx bit into the given mode bits.</summary>
	private static int RwxToBits(bool r, bool w, bool x, int rb, int wb, int xb) {
		var bits = 0;
		if (r) { bits |= rb; }
		if (w) { bits |= wb; }
		if (x) { bits |= xb; }
		return bits;
	}

	/// <summary>Renders rwx as a "rwx" / "r-x" string.</summary>
	private static string RwxString(bool r, bool w, bool x) {
		var rc = '-';
		if (r) { rc = 'r'; }
		var wc = '-';
		if (w) { wc = 'w'; }
		var xc = '-';
		if (x) { xc = 'x'; }
		return new string(new[] { rc, wc, xc });
	}
}
