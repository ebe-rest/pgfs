namespace Pgfs.Mkfs;

using Core;
using Core.Config;
using Core.Logging;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// mkfs.pgfs entry point.
///
/// Flow:
///   1. <c>-f &lt;path&gt;</c> is required. The default search paths are not used (this prevents reading a toml
///      meant for another FS and writing back into it).
///   2. Build a <see cref="RootConfig"/> from CLI / TOML / defaults via <see cref="ConfigLoader"/>
///      (mkfs runs even when no database exists yet, so no <see cref="ConfigStore"/> is passed). The TOML is
///      read only when the <c>-f</c> file exists and <c>--clean</c> is not given (a missing file means "create new").
///   3. Initialize the PostgreSQL side via <see cref="Initializer.InitializeAsync"/> (create the
///      database, create tables, insert the root inode, insert <c>pgfs_settings</c> rows for SaveTo=Db).
///   4. Write the SaveTo=File settings out as TOML to the <c>-f</c> location.
///
/// <c>--purge</c> only does the deletion part of step 3 and stops (it does not write the TOML either). Before deleting,
/// <c>--clean</c> / <c>--purge</c> check what is connected and ask for confirmation; if the user aborts, the exit code
/// is 3 (nothing was deleted). Exit codes: 0 = success / 1 = failure / 2 = bad arguments / 3 = aborted.
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args) {
		try {
			// --help prints immediately without touching the database or the TOML, then exits.
			var cliOnly = new ConfigLoader(args, Schema.AllFields, null, skipToml: true);
			if (cliOnly.Resolve(Schema.Root.Help)) {
				ShowHelp();
				return 0;
			}
			if (cliOnly.Resolve(Schema.Root.PrintVersion)) {
				Console.WriteLine(AppInfo.Banner);
				return 0;
			}

			// The settings file location is required (-f). mkfs does not use the default search paths - searching would
			// read a toml meant for another FS (such as the one a resident mount uses) and, worse, write back into it.
			// If the file exists it is read and written back to the same place; if not, it is treated as a new file.
			if (!cliOnly.WasProvided(Schema.Setting.File)) {
				await Console.Error.WriteLineAsync(
					"Error: mkfs requires the settings file location (-f <path>). An existing file is read and written back; a missing one is created.");
				return 2;
			}
			var tomlPath = Path.GetFullPath(cliOnly.Resolve(Schema.Setting.File));
			var clean = cliOnly.Resolve(Schema.Root.Clean);
			var purge = cliOnly.Resolve(Schema.Root.Purge);
			var exists = File.Exists(tomlPath);
			// --clean (delete and rebuild) and --purge (delete and stop) cannot be combined. The intent is unclear, so do nothing.
			if (clean && purge) {
				await Console.Error.WriteLineAsync("Error: --clean and --purge cannot be combined (use --clean to rebuild, --purge to delete and stop). Nothing was done.");
				return 2;
			}
			// --purge takes the connection of what it deletes from the -f settings file (the toml actually in use is required,
			// so that a mistyped connection string cannot delete some other FS).
			if (purge && !exists) {
				await Console.Error.WriteLineAsync($"Error: --purge takes the connection of what it deletes from the settings file. {tomlPath} does not exist. Nothing was done.");
				return 2;
			}
			var loader = cliOnly;
			if (exists && !clean) {
				loader = new ConfigLoader(args, Schema.AllFields, null);
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
			if (loader.WasProvided(Schema.Setting.SearchPath)) {
				Logger.Warning("mkfs does not use --setting-path (it only looks at the settings file given by -f)");
			}
			switch (exists, clean) {
				case (true, false) when purge:
					Logger.Information($"--purge: deleting what the settings file {tomlPath} connects to (the file itself is kept)");
					break;
				case (false, _):
					Logger.Information($"settings file {tomlPath} does not exist; creating a new one");
					break;
				case (true, true):
					Logger.Information($"--clean: not reading the existing {tomlPath}; it will be overwritten");
					break;
				default:
					Logger.Information($"read the settings file {tomlPath} (it is written back there on exit)");
					break;
			}

			var initializer = new Initializer(config, loader.WasProvided);
			await initializer.InitializeAsync();
			if (purge) {
				Logger.Lifecycle("done (exit 0)");
				return 0;
			}

			// Write SaveTo=File settings out to the TOML. The target is the -f location (the place it was read from = the place written back to).
			WriteTomlFile(tomlPath, config, loader);
			Logger.Information($"wrote the settings file: {tomlPath}");
			Logger.Information("");
			Logger.Information("filesystem creation complete.");

			Logger.Lifecycle("done (exit 0)");
			return 0;
		} catch (MkfsAbortedException ex) {
			// The user answered No, or could not answer because it is non-interactive. Nothing was deleted, so no stack trace is printed.
			await Console.Error.WriteLineAsync(ex.Message);
			Logger.Lifecycle("aborted (exit 3)");
			return 3;
		} catch (Exception ex) {
			await Console.Error.WriteLineAsync($"Error: {ex.Message}");
			await Console.Error.WriteLineAsync(ex.ToString());
			Logger.Lifecycle("exited with error (exit 1)");
			return 1;
		}
	}

	/// <summary>
	/// Writes the <see cref="SaveTarget.File"/> values of <see cref="RootConfig"/> out to the TOML (the toml for distribution).
	/// Only **the connection core (connection / schema / prefix)** and **the items given explicitly on the CLI / in the TOML**
	/// are written. Items left at their defaults are not written - writing them would pin the defaults on every client,
	/// so a later version changing a default would not be followed.
	/// mount_point is also written only when explicit (the Linux default `/mnt/pgfs` then needs no rewrite when distributed to Windows).
	/// Numbers / bools / strings use native TOML types; everything else (connection string, LogLevel,
	/// LoggingOutput, StringList) is stringified via <see cref="Field{T}.Format"/>.
	/// </summary>
	private static void WriteTomlFile(string path, RootConfig config, ConfigLoader loader) {
		var doc = new TomlTable();
		var m = config.Mount;
		AddField(doc, loader, Schema.Mount.MountPoint, m.MountPoint);
		AddField(doc, loader, Schema.Mount.MaxWrite, m.MaxWrite);
		AddField(doc, loader, Schema.Mount.CacheMaxEntries, m.CacheMaxEntries);
		AddField(doc, loader, Schema.Mount.CacheDataMaxBytes, m.CacheDataMaxBytes);
		AddField(doc, loader, Schema.Mount.NegativeCacheTtlMs, m.NegativeCacheTtlMs);
		AddField(doc, loader, Schema.Mount.WriteBack, m.WriteBack);
		AddField(doc, loader, Schema.Mount.WriteBackMaxBytes, m.WriteBackMaxBytes);
		AddField(doc, loader, Schema.Mount.WriteBackIntervalMs, m.WriteBackIntervalMs);
		AddField(doc, loader, Schema.Mount.WriteBackMetadata, m.WriteBackMetadata);
		AddField(doc, loader, Schema.Mount.WriteBackMetadataExclusiveCreate, m.WriteBackMetadataExclusiveCreate);
		AddField(doc, loader, Schema.Mount.WriteBackMaxInodes, m.WriteBackMaxInodes);
		AddField(doc, loader, Schema.Mount.WriteBackFlushTimeoutMs, m.WriteBackFlushTimeoutMs);
		var d = config.Database;
		AddField(doc, loader, Schema.Database.Connection, d.Connection, always: true);
		AddField(doc, loader, Schema.Database.SchemaName, d.SchemaName, always: true);
		AddField(doc, loader, Schema.Database.Prefix, d.Prefix, always: true);
		// tablespace / tablespace_path / the file_system sizes are SaveTo=Db (DB-authoritative), so AddField rejects them.
		AddField(doc, loader, Schema.Database.RetryMaxAttempts, d.RetryMaxAttempts);
		AddField(doc, loader, Schema.Database.RetryInitialDelayMs, d.RetryInitialDelayMs);
		AddField(doc, loader, Schema.Database.RetryMaxDelayMs, d.RetryMaxDelayMs);
		AddField(doc, loader, Schema.Database.NotifyEnabled, d.NotifyEnabled);
		// database.workers is not written. It is mkfs-only and means nothing on other clients; writing it would make a later
		// mkfs without --clean read it from the toml and register workers on its own (hit in Citus matrix I1+Te).
		// Up to v0.2.0 it was not written either.
		AddField(doc, loader, Schema.Logging.MinLevel, config.Logging.MinLevel);
		AddField(doc, loader, Schema.Logging.Output, config.Logging.Output);

		// For reference when distributing to other clients, keep the mkfs parameters that built this FS as a leading comment.
		// SaveTo=None items (--clean / --super / setting-file selection etc.) are excluded; the connection-string Password is masked.
		var body = Toml.FromModel(doc);
		var header = BuildRedistributionComment(loader);
		File.WriteAllText(path, header + body);
	}

	/// <summary>Builds the mkfs command for redistribution reference as comment lines (leading `#`). Empty if loader is null.</summary>
	private static string BuildRedistributionComment(ConfigLoader? loader) {
		if (loader == null) {
			return "";
		}
		var cmd = loader.DescribeMkfsCommandLine();
		var sb = new System.Text.StringBuilder();
		sb.Append("# The mkfs parameters that created this filesystem (reference for distribution to other clients).\n");
		sb.Append("# --clean / --super (super-user-connection) are excluded; the connection-string Password is masked.\n");
		sb.Append("#   ").Append(cmd).Append('\n');
		sb.Append('\n');
		return sb.ToString();
	}

	private static void AddField<T>(TomlTable doc, ConfigLoader loader, Field<T> f, T value, bool always = false) {
		if (f.SaveTo != SaveTarget.File) {
			return;
		}
		if (!always && !loader.WasProvided(f)) {
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
		const string intro = """
			mkfs.pgfs — initialize a PGFS filesystem on PostgreSQL

			Usage: mkfs.pgfs [options]
			""";
		const string footer = "See docs/Mkfs.md for details.";
		Console.Write(HelpText.Build(Tool.Mkfs, intro, footer));
	}
}
