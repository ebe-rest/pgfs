namespace Pgfs.Lib.Config;

using System.Collections.Generic;

/// <summary>
/// Settings about where the setting file (pgfs.toml) itself lives. <see cref="ConfigLoader"/> resolves it from
/// CLI / Default to decide which TOML path to read; a bootstrap concern. Not persisted (`SaveTo=None`).
/// </summary>
public sealed class SettingFileConfig
{
	public string File { get; set; } = "";
	public List<string> SearchPath { get; set; } = new();

	/// <summary>
	/// The absolute (or cwd-relative) path of the TOML that was actually loaded. <see cref="ConfigLoader"/> writes it after resolving.
	/// null if none was found, or if `File` was empty to begin with.
	/// </summary>
	public string? Path { get; set; }
}
