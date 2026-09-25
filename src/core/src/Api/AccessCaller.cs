namespace Pgfs.Core.Api;

using System.Collections.Generic;

/// <summary>
/// **The subject of a permission check** (who is trying to access). Used by <see cref="PermissionEvaluator"/>.
/// <para>
/// Every name is held **in the same form as a stored name** (normalized and with the Linux name mapping applied).
/// The check compares names, so the side that obtains them (Dokan's <c>CreateFile</c> etc.) passes them through
/// <c>UnameOf</c> / <c>GnameOf</c> before filling this in.
/// **When a name is unknown, use the empty string** - the empty string is never stored as an owner name, so it
/// matches nobody (filling in `file_system.unknown_name` would match files whose owner name is unknown).
/// </para>
/// <para>
/// The design is defined in docs/design/permission-interop.md, the Windows evaluation section.
/// </para>
/// </summary>
public sealed class AccessCaller
{
	/// <summary>The user name (in stored-name form). The empty string if unknown.</summary>
	public required string Uname { get; init; }

	/// <summary>The names of the groups the caller belongs to (in stored-name form). Unknown ones are left out.</summary>
	public required IReadOnlyList<string> Gnames { get; init; }

	/// <summary>
	/// **Whether the check is bypassed** (the equivalent of root on Linux). On Windows this is true when the token has
	/// Administrators **enabled** (elevated). A token restricted by UAC has Administrators as deny-only, so it is false.
	/// </summary>
	public bool Bypass { get; init; }

	/// <summary>The value used when the subject could not be obtained. **Checked with the rights of other** (not bypassed).</summary>
	public static readonly AccessCaller Unknown = new() { Uname = "", Gnames = [], Bypass = false };
}
