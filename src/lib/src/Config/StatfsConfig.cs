namespace Pgfs.Lib.Config;

/// <summary>
/// The mode for `df` (statfs) real-free-space reporting. <see cref="Schema.Statfs.Mode"/> is
/// <see cref="SaveTarget.Db"/>, so mkfs (`--statfs`) persists it to <c>pgfs_settings</c> and mount / assign
/// read it from the DB at startup.
///
/// <para>
/// The values are <c>auto</c> / <c>require</c> / <c>nominal</c>, used to decide whether mkfs creates the
/// <c>{prefix}statfs()</c> function. mount / assign always try that function inside
/// <see cref="Pgfs.Lib.Api.Api.GetStatFs"/> and fall back to the nominal capacity when it is missing or fails,
/// so this value does not need to drive a branch there (it is for the record + a future optimization). The
/// authoritative design is [docs/df-support.md](../../../../docs/df-support.md).
/// </para>
/// </summary>
public sealed class StatfsConfig
{
	public string Mode { get; set; } = "auto";
}
