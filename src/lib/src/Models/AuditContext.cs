namespace Pgfs.Lib.Models;

using System.Threading;

/// <summary>
/// The caller context representing the "who" of the audit log. A holder for passing per-call info that can only be obtained at the
/// OS layer (Mount = FUSE / Assign = Dokan) to <see cref="Api"/> ambiently (via <see cref="AsyncLocal{T}"/>).
///
/// <para>
/// The OS layer sets <see cref="Current"/> at the start of each callback, and the Api reads it when hooking. Read operations are
/// not audited, so leaving it unset (= a null value) does no harm. caller_host (= the pgfs process's host name) and caller_ip
/// (= the connecting host as seen by PG) are not included here. The former is resolved once by the Api at process startup, and the
/// latter is evaluated server-side via <c>inet_client_addr()</c> inside the INSERT. The design of record is
/// [docs/audit-log.md](../../../../docs/audit-log.md).
/// </para>
/// </summary>
public sealed class AuditContext
{
	/// <summary>The caller's UID (FUSE per-call context / Dokan's requesting process). null if unknown.</summary>
	public long? Uid { get; init; }

	/// <summary>The caller's user name. null if unknown.</summary>
	public string? Uname { get; init; }

	/// <summary>The caller's domain / workgroup (Windows only). null on Linux or when unknown.</summary>
	public string? Domain { get; init; }

	private static readonly AsyncLocal<AuditContext?> current = new();

	/// <summary>The caller context bound to the current execution flow. The OS layer sets it at the start of a callback.</summary>
	public static AuditContext? Current {
		get => current.Value;
		set => current.Value = value;
	}
}
