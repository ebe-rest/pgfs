namespace Pgfs.Lib.Models;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One entry of the canonical ACL document (the <c>user.pgfs_acl</c> xattr). Corresponds to a POSIX named ACL entry
/// (the owner/group/other base classes are canonical in <c>st_mode</c>, so they are not held here).
/// See [docs/permission-interop.md](../../../../docs/permission-interop.md).
/// </summary>
public sealed class PgfsAclEntry
{
	/// <summary>"user" | "group".</summary>
	[JsonPropertyName("principal_type")] public string PrincipalType { get; set; } = "user";

	/// <summary>The normalized principal name (Linux name).</summary>
	[JsonPropertyName("principal_name")] public string PrincipalName { get; set; } = "";

	/// <summary>A subset of "rwx" (e.g. "r-x" / "rw-"). Allow only (no deny — deny is dropped on projection).</summary>
	[JsonPropertyName("rights")] public string Rights { get; set; } = "";

	public bool CanRead => this.Rights.Contains('r');
	public bool CanWrite => this.Rights.Contains('w');
	public bool CanExecute => this.Rights.Contains('x');
}

/// <summary>
/// The canonical ACL document. POSIX-only / allow only. It coexists with <c>st_mode</c> (the base 3 classes) and holds
/// the named user/group entries plus a <c>default</c> list for directory inheritance. Both the Windows DACL and the
/// Linux POSIX ACL are projected to / converted from this document.
/// </summary>
public sealed class PgfsAcl
{
	[JsonPropertyName("v")] public int V { get; set; } = 1;
	[JsonPropertyName("entries")] public List<PgfsAclEntry> Entries { get; set; } = new();
	[JsonPropertyName("default")] public List<PgfsAclEntry> Default { get; set; } = new();

	/// <summary>Restore from a UTF-8 JSON byte sequence. Returns null if malformed.</summary>
	public static PgfsAcl? Parse(ReadOnlySpan<byte> json) {
		try {
			return JsonSerializer.Deserialize<PgfsAcl>(json);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Serialize to a UTF-8 JSON byte sequence.</summary>
	public byte[] ToBytes() {
		return JsonSerializer.SerializeToUtf8Bytes(this);
	}
}
