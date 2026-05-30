namespace Pgfs.Lib.Config;

/// <summary>
/// Audit log settings. The values are filled by <see cref="ConfigLoader"/> integrating CLI / TOML / DB / Default.
/// <see cref="Schema.Audit.Enabled"/> is <see cref="SaveTarget.Db"/>, so normally mkfs (`--audit`) saves it to
/// <c>pgfs_settings</c>, and mount / assign read it from the DB at startup.
///
/// <para>
/// The consumer (<see cref="Pgfs.Lib.Api.Api"/>) looks at <c>config.Audit.Enabled</c> to decide whether to INSERT an audit
/// row into <c>{prefix}audit</c> after each mutating operation succeeds, within the same tx. The design of record is
/// [docs/audit-log.md](../../../../docs/audit-log.md).
/// </para>
/// </summary>
public sealed class AuditConfig
{
	public bool Enabled { get; set; }
}
