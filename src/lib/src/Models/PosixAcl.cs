namespace Pgfs.Lib.Models;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

/// <summary>
/// One entry of the Linux <c>system.posix_acl_access</c> / <c>system.posix_acl_default</c> xattr binary. The layout is
/// <c>struct posix_acl_xattr_entry { __le16 e_tag; __le16 e_perm; __le32 e_id; }</c>.
/// </summary>
public struct PosixAclEntry
{
	public ushort Tag;
	public ushort Perm;
	public uint Id;
}

/// <summary>
/// Encode/decode of the POSIX ACL xattr binary (<c>struct posix_acl_xattr_header</c> + an array of entries).
/// The conversion to / from <c>st_mode</c> (the base 3 classes) and <see cref="PgfsAcl"/> (named entries) is done by
/// the caller (mount/FileSystem), which also resolves names ↔ ids. See [docs/permission-interop.md](../../../../docs/permission-interop.md).
/// </summary>
public static class PosixAcl
{
	public const uint Version = 2;            // POSIX_ACL_XATTR_VERSION
	public const ushort TagUserObj = 0x01;    // ACL_USER_OBJ (owner; id=undefined)
	public const ushort TagUser = 0x02;       // ACL_USER (named user; id=uid)
	public const ushort TagGroupObj = 0x04;   // ACL_GROUP_OBJ (owning group)
	public const ushort TagGroup = 0x08;      // ACL_GROUP (named group; id=gid)
	public const ushort TagMask = 0x10;       // ACL_MASK
	public const ushort TagOther = 0x20;      // ACL_OTHER
	public const uint UndefinedId = 0xFFFFFFFF;
	public const ushort PermRead = 4;
	public const ushort PermWrite = 2;
	public const ushort PermExec = 1;

	/// <summary>Split the xattr binary into entries. Returns null on version mismatch / bad size.</summary>
	public static List<PosixAclEntry>? Parse(ReadOnlySpan<byte> blob) {
		if (blob.Length < 4) {
			return null;
		}
		var version = BinaryPrimitives.ReadUInt32LittleEndian(blob);
		if (version != Version) {
			return null;
		}
		var rest = blob[4..];
		if (rest.Length % 8 != 0) {
			return null;
		}
		var list = new List<PosixAclEntry>(rest.Length / 8);
		for (var off = 0; off + 8 <= rest.Length; off += 8) {
			list.Add(new PosixAclEntry {
				Tag = BinaryPrimitives.ReadUInt16LittleEndian(rest[off..]),
				Perm = BinaryPrimitives.ReadUInt16LittleEndian(rest[(off + 2)..]),
				Id = BinaryPrimitives.ReadUInt32LittleEndian(rest[(off + 4)..]),
			});
		}
		return list;
	}

	/// <summary>Assemble the xattr binary (header + entries) from an entry list.</summary>
	public static byte[] Build(IReadOnlyList<PosixAclEntry> entries) {
		var buf = new byte[4 + (entries.Count * 8)];
		BinaryPrimitives.WriteUInt32LittleEndian(buf, Version);
		var off = 4;
		foreach (var e in entries) {
			BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), e.Tag);
			BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), e.Perm);
			BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4), e.Id);
			off += 8;
		}
		return buf;
	}

	/// <summary>rwx → e_perm.</summary>
	public static ushort PermFromRwx(bool r, bool w, bool x) {
		ushort p = 0;
		if (r) { p |= PermRead; }
		if (w) { p |= PermWrite; }
		if (x) { p |= PermExec; }
		return p;
	}
}
