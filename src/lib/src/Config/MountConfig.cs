namespace Pgfs.Lib.Config;

/// <summary>
/// The aggregate of settings referenced when mounting. A POCO. The values are filled by <see cref="ConfigLoader"/>
/// integrating CLI / TOML / DB / Default.
///
/// <para>
/// Properties are mutable (<c>{ get; set; }</c>) so that items which can change while mounted (e.g. volume_label, the kind
/// that may be rewritten via OS APIs in the future) can be handled. The rewrite + persistence pair is centralized in the thin
/// <c>Api.SetXxx(...)</c> wrappers, which call <see cref="ConfigStore.Save"/> inside.
/// </para>
/// </summary>
public sealed class MountConfig
{
	public string MountPoint { get; set; } = "";
	public int CacheMaxEntries { get; set; }
	public string FallbackUname { get; set; } = "";
	public string FallbackGname { get; set; } = "";
	public bool Foreground { get; set; }

	/// <summary>
	/// The FUSE flags received via `-o key=val,flag,...` (`allow_other`, `default_permissions`, `ro`, `rw`,
	/// `nonempty`, `auto_unmount`, etc.). <see cref="ConfigLoader"/> accumulates them in `ParseDashOOptions`, and
	/// mount.pgfs wires them into <c>Tmds.Fuse.MountOptions.Options</c>. Not placed in Schema and not managed by a Field
	/// (because it is an arbitrary key set).
	/// </summary>
	public System.Collections.Generic.List<string> FuseFlags { get; set; } = new();
}
