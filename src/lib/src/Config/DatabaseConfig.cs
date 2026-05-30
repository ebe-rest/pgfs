namespace Pgfs.Lib.Config;

using Npgsql;

/// <summary>
/// Settings for the PostgreSQL connection / schema / connection retry. The values are filled by <see cref="ConfigLoader"/>
/// integrating CLI / TOML / Default.
///
/// <para>
/// The superuser connection used only at mkfs time (<see cref="SuperConnection"/>) is <see cref="SaveTarget.None"/>,
/// so it is persisted neither to TOML nor DB and its value comes only via CLI / Default. Not referenced by mount / assign
/// (= mkfs only).
/// </para>
/// </summary>
public sealed class DatabaseConfig
{
	public NpgsqlConnectionStringBuilder Connection { get; set; } = new();
	public NpgsqlConnectionStringBuilder SuperConnection { get; set; } = new();
	public string SchemaName { get; set; } = "";
	public string Prefix { get; set; } = "";
	public string TablespaceName { get; set; } = "";
	public string TablespacePath { get; set; } = "";
	public int RetryMaxAttempts { get; set; }
	public int RetryInitialDelayMs { get; set; }
	public int RetryMaxDelayMs { get; set; }
	public bool NotifyEnabled { get; set; }
	public bool Citus { get; set; }

	/// <summary>
	/// The list of Citus worker nodes, normalized to (host, port) tuple form.
	/// Empty means a 1-node (coordinator only) setup.
	/// </summary>
	public List<(string Host, int Port)> Workers { get; set; } = new();

	/// <summary>
	/// Returns <see cref="Prefix"/> normalized with a trailing `_`. If empty, stays empty.
	/// </summary>
	public string GetPrefix() {
		var prefix = this.Prefix.Trim();
		if (prefix.Length == 0) {
			return "";
		}
		if (prefix[^1] == '_') {
			return prefix;
		}
		return prefix + "_";
	}
}
