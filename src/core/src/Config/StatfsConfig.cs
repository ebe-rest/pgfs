namespace Pgfs.Core.Config;

/// <summary>
/// The mode for `df` (statfs) real-free-space reporting. <see cref="Schema.Statfs.Mode"/> is
/// <see cref="SaveTarget.Db"/>, so mkfs (`--statfs`) persists it to <c>pgfs_settings</c> and mount / assign
/// read it from the DB at startup.
///
/// <para>
/// The value is <c>auto</c> / <c>require</c> / <c>nominal</c>. Used to decide whether mkfs creates the
/// <c>{prefix}statfs()</c> function.
/// mount / assign always try that function inside <see cref="Pgfs.Core.Api.Api.GetStatFs"/> and fall back to
/// the nominal capacity when it is missing or fails, so this value does not have to be used for branching
/// (it is a record, and there for a future optimization). The design of record is
/// [docs/df-support.md](../../../../docs/design/df-support.md).
/// </para>
/// </summary>
public sealed class StatfsConfig
{
	public string Mode { get; set; } = "auto";
}
