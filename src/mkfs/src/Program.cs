namespace Pgfs.Mkfs;

using Lib;
using Lib.Config;
using Lib.Logging;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// mkfs.pgfs entry point.
///
/// Flow:
///   1. Build a <see cref="RootConfig"/> from CLI / TOML / defaults via <see cref="ConfigLoader"/>
///      (mkfs runs even when no database exists yet, so no <see cref="ConfigStore"/> is passed).
///   2. When <c>--clean</c> is given, skip reading the TOML (= build from CLI / defaults only).
///   3. Initialize the PostgreSQL side via <see cref="Initializer.InitializeAsync"/> (create the
///      database, create tables, insert the root inode, insert <c>pgfs_settings</c> rows for SaveTo=Db).
///   4. Write the SaveTo=File settings out to the TOML.
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args) {
		try {
			// --help prints immediately without touching the database, then exits.
			var liteForHelp = new ConfigLoader(args, Schema.AllFields, null);
			if (liteForHelp.Resolve(Schema.Root.Help)) {
				ShowHelp();
				return 0;
			}

			// --clean starts "as if no settings file exists": apply CLI args only and skip the TOML.
			var clean = liteForHelp.Resolve(Schema.Root.Clean);
			var loader = liteForHelp;
			if (clean) {
				Logger.Information("--clean: not reading the existing pgfs.toml");
				loader = new ConfigLoader(args, Schema.AllFields, null, skipToml: true);
			}
			var config = loader.BuildRootConfig();

			Logger.MinLevel = config.Logging.MinLevel;
			LogSink.Configure(config.Logging.Output);

			// Startup banner (program name + version + copyright) and lifecycle markers. Always goes to
			// stderr so it stays visible even when logs are written to a file.
			Logger.Lifecycle(AppInfo.Banner);
			Logger.Lifecycle($"started (pid {Environment.ProcessId})");

			// Report the received parameters (how they were interpreted) and any warnings. Unknown options
			// (typos such as --cutus) are warned about here.
			foreach (var line in loader.DescribeProvided()) {
				Logger.Information($"  param: {line}");
			}
			foreach (var warning in loader.Warnings) {
				Logger.Warning(warning);
			}

			var initializer = new Initializer(config);
			await initializer.InitializeAsync();

			// Write SaveTo=File settings out to the TOML. The target is (1) the path ConfigLoader read,
			// (2) the settings file name (`setting.file` from CLI / defaults), or (3) the final fallback `pgfs.toml`.
			var tomlPath = config.Setting.Path;
			if (string.IsNullOrEmpty(tomlPath)) {
				tomlPath = config.Setting.File;
			}
			if (string.IsNullOrEmpty(tomlPath)) {
				tomlPath = "pgfs.toml";
			}
			WriteTomlFile(tomlPath, config);
			Logger.Information($"wrote the settings file: {tomlPath}");
			Logger.Information("");
			Logger.Information("filesystem creation complete.");

			Logger.Lifecycle("done (exit 0)");
			return 0;
		} catch (Exception ex) {
			await Console.Error.WriteLineAsync($"Error: {ex.Message}");
			await Console.Error.WriteLineAsync(ex.ToString());
			Logger.Lifecycle("exited with error (exit 1)");
			return 1;
		}
	}

	/// <summary>
	/// Writes the <see cref="SaveTarget.File"/> values of <see cref="RootConfig"/> out to the TOML.
	/// Each sub-config property is matched against a <see cref="Schema"/> Field and added one line at a time.
	/// Numbers / bools / strings use native TOML types; everything else (connection string, LogLevel,
	/// LoggingOutput, StringList) is stringified via <see cref="Field{T}.Format"/>.
	/// </summary>
	private static void WriteTomlFile(string path, RootConfig config) {
		var doc = new TomlTable();
		AddField(doc, Schema.Mount.MountPoint, config.Mount.MountPoint);
		AddField(doc, Schema.Mount.CacheMaxEntries, config.Mount.CacheMaxEntries);
		AddField(doc, Schema.Database.Connection, config.Database.Connection);
		AddField(doc, Schema.Database.SchemaName, config.Database.SchemaName);
		AddField(doc, Schema.Database.Prefix, config.Database.Prefix);
		AddField(doc, Schema.Database.TablespaceName, config.Database.TablespaceName);
		AddField(doc, Schema.Database.TablespacePath, config.Database.TablespacePath);
		AddField(doc, Schema.Database.RetryMaxAttempts, config.Database.RetryMaxAttempts);
		AddField(doc, Schema.Database.RetryInitialDelayMs, config.Database.RetryInitialDelayMs);
		AddField(doc, Schema.Database.RetryMaxDelayMs, config.Database.RetryMaxDelayMs);
		AddField(doc, Schema.Logging.MinLevel, config.Logging.MinLevel);
		AddField(doc, Schema.Logging.Output, config.Logging.Output);
		AddField(doc, Schema.FileSystem.ClusterSize, config.FileSystem.ClusterSize);
		AddField(doc, Schema.FileSystem.DefaultChunkSize, config.FileSystem.DefaultChunkSize);
		AddField(doc, Schema.FileSystem.MaxFileSize, config.FileSystem.MaxFileSize);

		File.WriteAllText(path, Toml.FromModel(doc));
	}

	private static void AddField<T>(TomlTable doc, Field<T> f, T value) {
		if (f.SaveTo != SaveTarget.File) {
			return;
		}
		if (!doc.TryGetValue(f.Scope, out var existing) || existing is not TomlTable scope) {
			scope = new TomlTable();
			doc[f.Scope] = scope;
		}
		scope[f.Key] = ToTomlValue(f, value);
	}

	private static object ToTomlValue<T>(Field<T> f, T value) {
		// Primitives are written as native TOML types; everything else is written as the Format() string.
		if (value is bool b) {
			return b;
		}
		if (value is int i) {
			return (long)i;
		}
		if (value is long l) {
			return l;
		}
		if (value is string s) {
			return s;
		}
		return f.Format(value);
	}

	private static void ShowHelp() {
		Console.WriteLine("""
			Usage: mkfs.pgfs [options]

			Initializes a PGFS filesystem on PostgreSQL.

			Common options:
			  -?, -h, --help                Show help.
			  -f, --setting-file <path>     Settings file (TOML) path. Default: pgfs.toml
			  --clean                       Ignore the existing pgfs.toml and DROP DATABASE -> recreate.
			                                The tablespace and role are preserved (no short form).

			Database (PGFS user) connection:
			  -c, --connection <connstr>    Connection string for the target DB as the PGFS user.

			Database (superuser) connection:
			  --su, --super, --super-connection <connstr>
			                                Connection string for the maintenance DB as a superuser.

			Schema / table:
			  -s, --schema <name>           Schema name (default: public).
			  -x, --prefix <prefix>         Table name prefix (default: pgfs_).
			  --tablespace <name>           Tablespace name (default: pg_default).
			  --tablespace-path <path>      Path for a new tablespace (when needed).

			Filesystem parameters:
			  --volume-label <label>        Volume label (default: pgfs).
			  --cluster-size <bytes>        Cluster size (default: 4096).
			  --default-chunk-size <bytes>  Chunk split size (default: 1048576).
			  --max-file-size <bytes>       Maximum file size (default: 1099511627776).

			See docs/Mkfs.md for details.
			""");
	}
}
