namespace Pgfs.Assign;

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Lib;
using Lib.Api;
using Lib.Config;
using Lib.Logging;

/// <summary>
/// pgfs.assign entry point. Mounts PGFS as a drive via DokanNet on Windows.
///
/// On Linux / macOS, use <see cref="Pgfs.Mount"/> (Tmds.Fuse) instead.
///
/// Flow:
///   1. Build a <see cref="RootConfig"/> from CLI / TOML / DB / defaults via <see cref="ConfigLoader"/>.
///   2. Check that this is Windows.
///   3. The DokanNet driver's dependency check happens when the Dokan instance is constructed.
///   4. Prepare an <see cref="Api"/>, build a <see cref="FileSystem"/>, and start the DokanInstance.
///   5. Wait until unmount.
///
/// Implementation note:
///   As on the Mount side, references to DokanNet-related types are split into
///   <see cref="RunDokanMountAsync"/> so that <c>--help</c> still works without a load failure on Linux/macOS.
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args) {
		try {
			// Build a lite Loader (store=null) and decide Help first (we do not want to connect to the DB for --help).
			var liteForHelp = new ConfigLoader(args, Schema.AllFields, null);
			if (liteForHelp.Resolve(Schema.Root.Help)) {
				ShowHelp();
				return 0;
			}

			var config = BuildRootConfig(args, out var loader);

			Logger.MinLevel = config.Logging.MinLevel;
			LogSink.Configure(config.Logging.Output);

			// Report the received parameters (how they were interpreted) and any warnings. Unknown options
			// (typos) are warned about here.
			foreach (var line in loader.DescribeProvided()) {
				Logger.Information($"  param: {line}");
			}
			foreach (var warning in loader.Warnings) {
				Logger.Warning(warning);
			}

			// Startup banner + lifecycle markers (always stderr, so process start/exit stays traceable even
			// when logs go to a file).
			Logger.Lifecycle(AppInfo.Banner);
			Logger.Lifecycle($"started (pid {Environment.ProcessId})");

			if (!OperatingSystem.IsWindows()) {
				Console.Error.WriteLine("pgfs.assign is Windows-only. On Linux/macOS, use mount.pgfs (the Tmds.Fuse version).");
				return 1;
			}

			var rc = await RunDokanMountAsync(config);
			Logger.Lifecycle($"exited (code {rc})");
			return rc;
		} catch (Exception ex) {
			await Console.Error.WriteLineAsync($"Error: {ex.Message}");
			await Console.Error.WriteLineAsync(ex.ToString());
			Logger.Lifecycle("exited with error (code 1)");
			return 1;
		}
	}

	/// <summary>
	/// Builds a <see cref="RootConfig"/> via <see cref="ConfigLoader"/>. See the same-named function in mount.pgfs for details.
	/// </summary>
	private static RootConfig BuildRootConfig(string[] args, out ConfigLoader loader) {
		// (1) Determine database.* with a lite Loader.
		var lite = new ConfigLoader(args, Schema.AllFields, null);
		var liteDb = lite.BuildDatabaseConfig();
		// (2) Full Loader: build a ConfigStore and load again.
		var store = new ConfigStore(
			liteDb.Connection.ConnectionString,
			liteDb.SchemaName,
			liteDb.GetPrefix()
		);
		var full = new ConfigLoader(args, Schema.AllFields, store);
		loader = full;
		return full.BuildRootConfig();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	[SupportedOSPlatform("windows")]
	private static async Task<int> RunDokanMountAsync(RootConfig config) {
		Api api;
		try {
			api = new Api(config);
		} catch (Exception ex) {
			Logger.Error("failed to initialize the Api: ", ex);
			return 2;
		}

		var mountPoint = config.Mount.MountPoint;
		if (string.IsNullOrWhiteSpace(mountPoint)) {
			Console.Error.WriteLine("mount_point is not set. Specify it with --mount-point or in pgfs.toml.");
			return 1;
		}

		Logger.Information($"mounting PGFS: {mountPoint}");

		try {
			using var fileSystem = new FileSystem(api, mountPoint);
			fileSystem.Run();
			Logger.Information("PGFS unmounted.");
			return await Task.FromResult(0);
		} catch (Exception ex) {
			Logger.Error("error while mounting: ", ex);
			return 3;
		}
	}

	private static void ShowHelp() {
		Console.WriteLine("""
			Usage: pgfs.assign [options]

			Mounts a PostgreSQL-backed filesystem via DokanNet (Windows).

			Common options:
			  -?, -h, --help                Show help.
			  -f, --setting-file <path>     Settings file (TOML) path. Default: pgfs.toml

			Database (PGFS user) connection:
			  -c, --connection <connstr>    Connection string for the target DB as the PGFS user.

			Mount:
			  -m, --mount-point <path>      Mount point (default: P:).
			                                A drive letter ("P:") or a directory path ("C:\\mnt\\pgfs").

			Logging:
			  --log-level <level>           Minimum log level (default: warning).

			pgfs.assign is not available outside Windows. Use mount.pgfs (the Tmds.Fuse version).
			See docs/Assign.md for details.
			""");
	}
}
