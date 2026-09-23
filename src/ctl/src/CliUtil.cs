namespace Pgfs.Ctl;

using System.Collections.Generic;
using Core.Config;

/// <summary>The connection resolution helper shared by the pgfsctl subcommands.</summary>
public static class CliUtil
{
	/// <summary>
	/// Resolves the connection options (`-c`/`-s`/`-x`/`-f` and so on) with the same
	/// <see cref="ConfigLoader"/> (store=null) that mount.pgfs uses, and extracts whether `--json` was given.
	/// The Loader's warnings go to stderr.
	/// </summary>
	public static (bool Json, ConfigLoader Loader, string Conn, string Schema, string Prefix) Resolve(string[] opts) {
		var json = false;
		var loaderArgs = new List<string>();
		foreach (var o in opts) {
			if (o == "--json") {
				json = true;
				continue;
			}
			loaderArgs.Add(o);
		}
		var loader = new ConfigLoader(loaderArgs.ToArray(), Schema.AllFields, null);
		foreach (var w in loader.Warnings) {
			Console.Error.WriteLine($"warning: {w}");
		}
		var db = loader.BuildDatabaseConfig();
		return (json, loader, db.Connection.ConnectionString, db.SchemaName, db.GetPrefix());
	}
}
