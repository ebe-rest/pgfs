namespace Pgfs.Assign;

using System.Runtime.Versioning;
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
	/// The value is a 4-byte little-endian partial mask of <see cref="FileAttributes"/>.
	/// The key is a concise pgfs-specific one, not Samba's `user.DOSATTRIB`.
	/// </summary>
	public const string WinAttrsXattrKey = "user.win_attrs";

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
		if (raw.Length < 4) {
			// Treat a corrupt value as unset.
			return FileAttributes.None;
		}
		var mask = BitConverter.ToInt32(raw, 0);
		return (FileAttributes)mask & WinAttrsMask;
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
		var masked = attributes & WinAttrsMask;
		var bytes = BitConverter.GetBytes((int)masked);
		api.SetXAttr(inodeId, WinAttrsXattrKey, bytes, createOnly: false, replaceOnly: false);
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
		// owner +w (root is treated as Administrator)
		var uname = inode.UserName == "root" ? "Administrator" : inode.UserName;
		if (string.Equals(uname, users.DefaultUname, StringComparison.OrdinalIgnoreCase)
			&& (mode & Mode.S_IWUSR) != 0) {
			return true;
		}
		// group +w (root -> Administrators)
		var gname = inode.GroupName == "root" ? "Administrators" : inode.GroupName;
		if (string.Equals(gname, users.DefaultGname, StringComparison.OrdinalIgnoreCase)
			&& (mode & Mode.S_IWGRP) != 0) {
			return true;
		}
		return false;
	}
}
