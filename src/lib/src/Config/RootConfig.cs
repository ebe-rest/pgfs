namespace Pgfs.Lib.Config;

/// <summary>
/// The aggregate of all Config. <see cref="ConfigLoader.BuildRootConfig"/> builds it by integrating CLI / TOML / DB / Default.
///
/// <para>
/// Each sub-Config's properties are mutable. Items that can be rewritten while mounted (e.g. volume_label) are set directly,
/// and the convention is to call <see cref="ConfigStore.Save{T}"/> immediately afterward.
/// </para>
/// </summary>
public sealed class RootConfig
{
	public required SettingFileConfig Setting { get; init; }
	public required LoggingConfig Logging { get; init; }
	public required DatabaseConfig Database { get; init; }
	public required MountConfig Mount { get; init; }
	public required FileSystemConfig FileSystem { get; init; }
	public required AuditConfig Audit { get; init; }

	/// <summary>true with `-?` / `-h` / `--help`. A signal for the receiver to <c>ShowHelp()</c> → exit.</summary>
	public bool Help { get; set; }

	/// <summary>true with `--clean`. mkfs only (no effect on mount / assign).</summary>
	public bool Clean { get; set; }
}
