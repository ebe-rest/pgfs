namespace Pgfs.Lib.Config;

/// <summary>
/// The persistence target of a setting value. Each <see cref="Field{T}"/> declares exactly one destination (TOML / DB / not persisted).
/// </summary>
public enum SaveTarget
{
	/// <summary>Not persisted. For transient flags set only on the CLI (Foreground, Help, Clean, etc.).</summary>
	None,
	/// <summary>Saved to pgfs.toml. Written at `mkfs` time, read at `mount`/`assign` startup.</summary>
	File,
	/// <summary>Saved to the pgfs_settings table. For values you want a single FS-wide value for (e.g. fallback_uname).</summary>
	Db,
}
