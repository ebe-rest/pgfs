namespace Pgfs.Mount;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Lib;
using Lib.Api;
using Lib.Config;
using Lib.Logging;

/// <summary>
/// mount.pgfs entry point. Mounts PGFS via FUSE on Linux / macOS.
///
/// On Windows, use <see cref="Pgfs.Assign"/> (DokanNet) instead.
///
/// Flow:
///   1. Build a <see cref="RootConfig"/> from CLI / TOML / DB / defaults via <see cref="ConfigLoader"/>.
///   2. Check the Windows / FUSE dependencies.
///   3. Prepare an <see cref="Api"/>, build a <see cref="FileSystem"/>, and <see cref="Tmds.Fuse.Fuse.Mount"/>.
///   4. Wait until unmount.
///
/// Implementation note:
///   Tmds.Fuse / Tmds.LibC have no Windows runtime, so if <c>Main</c> references those types at JIT time
///   on Windows it dies with a <c>FileNotFoundException</c>. The Linux-specific mount logic is therefore
///   split out into <see cref="RunFuseMountAsync"/> and JITed lazily via
///   <c>[MethodImpl(MethodImplOptions.NoInlining)]</c>. Main itself holds no direct reference to
///   Tmds.Fuse / FileSystem.
/// </summary>
public static class Program
{
	/// <summary>
	/// Internal flag for daemonization. A parent process launched via `mount(8)` re-launches a child with
	/// this flag. The child continues mounting in the foreground as usual; without this argument the parent
	/// runs the launch-child -> parent-exit logic.
	/// </summary>
	private const string ForegroundInternalFlag = "--foreground-internal";

	/// <summary>
	/// Signal string the child sends to the parent to report "mount succeeded". Printed as the first line
	/// of the child's stdout.
	/// </summary>
	private const string MountedSignal = "PGFS_MOUNTED_OK";

	public static async Task<int> Main(string[] args) {
		try {
			// When launched as a `mount(8)` helper, the env is stripped down to almost nothing including PATH
			// (in practice only 8-11 vars: LANG / LOGNAME / PWD / SHLVL / SUDO_* / TERM / USER / _).
			// Tmds.Fuse's `HasFusermount` searches `$PATH` for `fusermount3`, so without PATH
			// `CheckDependencies` returns false and we exit immediately with "FUSE dependencies not found".
			// Supply a minimal PATH right at startup before proceeding. Leave it alone if already set.
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH"))) {
				Environment.SetEnvironmentVariable("PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
			}

			var isChild = args.Contains(ForegroundInternalFlag);
			var passThroughArgs = args.Where(a => a != ForegroundInternalFlag).ToArray();

			// ----- Preprocessing shareable with Windows starts here -----
			// Build a lite Loader (store=null) and decide Help first. We do not want to connect to the DB for
			// --help, so exit here before building the full Loader (= a ConfigStore connection).
			var liteForHelp = new ConfigLoader(passThroughArgs, Schema.AllFields, null);
			if (liteForHelp.Resolve(Schema.Root.Help)) {
				ShowHelp();
				return 0;
			}

			// Build the RootConfig via ConfigLoader: a POCO aggregate that merges CLI / TOML / DB / defaults.
			// Every scope (Setting / Logging / Database / Mount / FileSystem) is modeled as Config.
			var config = BuildRootConfig(passThroughArgs, out var loader);

			// Override the minimum log level and output target from the settings.
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
			// ----- End of the shareable part -----

			// FUSE is Linux/macOS only. On Windows, point the user at the DokanNet version (pgfs.assign).
			if (OperatingSystem.IsWindows()) {
				Console.Error.WriteLine("mount.pgfs is not available on Windows. Use pgfs.assign (the DokanNet version).");
				return 1;
			}

			// Parent process: launch the child, wait for the MOUNTED signal, then exit
			// (a mount(8) helper is expected to exit once the mount completes).
			//
			// With `--foreground`, no child is launched and this process runs the FUSE loop in the foreground.
			// Via fstab / mount(8), the convention with no flag is to daemonize by default
			// (same as libfuse / sshfs / ntfs-3g). `--foreground` is recommended for tests / manual use.
			//
			// `isChild` (= just after the parent launched itself as a child) always enters the foreground.
			var daemonize = !isChild && !config.Mount.Foreground;
			if (daemonize) {
				return await RunAsParentAsync(args);
			}

			// From here on it is Linux/macOS only. It references Tmds.Fuse / FileSystem, so it is split into a
			// separate method to avoid a JIT error on Windows.
			var rc = await RunFuseMountAsync(config, isChild);
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
	/// Builds a <see cref="RootConfig"/> via <see cref="ConfigLoader"/>, merging CLI / TOML / DB / defaults
	/// and automatically picking up every Field from <see cref="Schema.AllFields"/>.
	///
	/// <para>
	/// ConfigStore (the side that reads DB-sourced Config rows) needs the resolved <c>database.*</c> from
	/// CLI / TOML, so this builds in two stages: (1) a lite Loader with store=null resolves CLI / TOML /
	/// defaults -> obtains database.connection / schema / prefix; (2) a ConfigStore is built from that
	/// connection info and a full Loader is built again to apply DB-saved Fields (mount.fallback_*, etc.).
	/// The lite side (store=null) already resolves all of CLI/TOML/defaults, so stage (2) just overrides
	/// stage (1) (some duplicated work, but the same result).
	/// </para>
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

	/// <summary>
	/// Launches a child (`--foreground-internal`) as the parent process, waits for the child's MOUNTED
	/// signal, then exits. This satisfies the `mount(8)` helper convention (parent exits once the mount
	/// completes).
	/// </summary>
	private static async Task<int> RunAsParentAsync(string[] originalArgs) {
		var selfPath = Environment.ProcessPath;
		if (string.IsNullOrEmpty(selfPath)) {
			Console.Error.WriteLine("Error: cannot launch self as a child process because Environment.ProcessPath is unavailable.");
			return 1;
		}

		var psi = new ProcessStartInfo {
			FileName = selfPath,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (var a in originalArgs) {
			psi.ArgumentList.Add(a);
		}
		psi.ArgumentList.Add(ForegroundInternalFlag);

		using var child = Process.Start(psi);
		if (child == null) {
			Console.Error.WriteLine("Error: failed to launch the child process.");
			return 1;
		}

		// Wait up to 30 seconds for the child's MOUNTED signal (= MountedSignal on the first line).
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (true) {
			using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
			string? line;
			try {
				line = await child.StandardOutput.ReadLineAsync(cts.Token);
			} catch (OperationCanceledException) {
				Console.Error.WriteLine("Error: the mount-complete signal did not arrive within 30 seconds.");
				try { child.Kill(entireProcessTree: true); } catch { }
				return 1;
			}

			if (line == null) {
				// The child hit EOF (= exited early). Drain stderr and return its exit code.
				var err = await child.StandardError.ReadToEndAsync();
				if (!string.IsNullOrEmpty(err)) {
					Console.Error.Write(err);
				}
				await child.WaitForExitAsync();
				return child.ExitCode != 0 ? child.ExitCode : 1;
			}

			if (line == MountedSignal) {
				// Mount succeeded. The parent exits immediately to release mount(8); the child lives on in the background.
				return 0;
			}

			// Relay any non-MOUNTED output as-is (log messages, etc.).
			Console.Out.WriteLine(line);
		}
	}


	[MethodImpl(MethodImplOptions.NoInlining)]
	private static async Task<int> RunFuseMountAsync(RootConfig config, bool isChild) {
		// Check that libfuse3 / fusermount3 are visible (Linux/macOS-specific).
		if (!Tmds.Fuse.Fuse.CheckDependencies()) {
			Console.Error.WriteLine("FUSE dependencies not found:");
			Console.Error.WriteLine(Tmds.Fuse.Fuse.InstallationInstructions);
			return 1;
		}

		// Initialize the API (connect to the DB and prepare the inode cache) — the logic itself is shareable with Windows.
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

		// Assemble the FUSE options.
		//   - Default: `attr_timeout=0` (disables the kernel attr cache; without it, a `stat a` right after
		//             `ln a b` returns a stale st_nlink).
		//   - Added: flags received via fstab `-o allow_other,default_permissions,ro,rw,...`.
		// The defaults are listed first, so even if the user overrides the same key with `-o`, last-wins
		// applies (libfuse's option parsing is comma-separated and the later value wins).
		// `use_ino` had to be explicit on libfuse 2.x, but on libfuse 3.x it is on by default and passing it
		// makes fuse_new fail with `unknown option`, so we do not pass it.
		var optParts = new List<string> { "attr_timeout=0" };
		optParts.AddRange(config.Mount.FuseFlags);
		var fuseOptionsString = string.Join(",", optParts);

		Logger.Information($"mounting PGFS: {mountPoint}");
		Logger.Information("FUSE options: ", fuseOptionsString);

		using var fileSystem = new FileSystem(api);
		var mountOptions = new Tmds.Fuse.MountOptions {
			SingleThread = false,
			Options = fuseOptionsString,
		};

		try {
			using var fuseMount = Tmds.Fuse.Fuse.Mount(mountPoint, fileSystem, mountOptions);
			Logger.Information("PGFS mounted successfully. To unmount, run fusermount3 -u or stop with Ctrl+C.");

			Console.CancelKeyPress += (_, e) => {
				e.Cancel = true;
				Logger.Information("received Ctrl+C; attempting to unmount.");
				try {
					fuseMount.LazyUnmount();
				} catch (Exception ex) {
					Logger.Warning("error during LazyUnmount: ", ex);
				}
			};

			// Child mode: report "mount succeeded" to the parent (mount(8) helper) on a single line.
			// Once the parent reads this line it exits.
			// After the parent exits, the child's stdout/stderr pipes close. To avoid blocking on a full
			// buffer, switch the console output to null after sending the signal (Logger also goes via
			// Console.Error, so logs during FUSE operations are dropped instead of hanging).
			if (isChild) {
				Console.Out.WriteLine(MountedSignal);
				Console.Out.Flush();
				Console.SetOut(TextWriter.Null);
				Console.SetError(TextWriter.Null);
			}

			await fuseMount.WaitForUnmountAsync();
			Logger.Information("PGFS unmounted.");
			return 0;
		} catch (Exception ex) {
			Logger.Error("error while mounting: ", ex);
			return 3;
		}
	}

	private static void ShowHelp() {
		Console.WriteLine("""
			Usage: mount.pgfs [options]
			       mount.pgfs <source> <mountpoint> [-o opts]     (via fstab / mount(8))

			Mounts a PostgreSQL-backed filesystem via FUSE (Linux/macOS).

			Common options:
			  -?, -h, --help                Show help.
			  -f, --setting-file <path>     Settings file (TOML) path. Default: pgfs.toml
			                                (the short form `-f` is only valid for direct invocation; via
			                                 fstab/mount(8) it is silently swallowed as `--fake`, so use --setting-file)

			Database (PGFS user) connection:
			  -c, --connection <connstr>    Connection string for the target DB as the PGFS user.

			Mount:
			  -m, --mount-point <path>      Mount point (default: /mnt/pgfs).
			  --cache-max-entries <n>       inode cache limit (default: 1024).
			  --foreground                  Run in the foreground (do not daemonize). Via fstab/mount(8) it
			                                 daemonizes by default, so add this for tests/manual use.

			Logging:
			  --log-level <level>           Minimum log level (default: warning).

			Launching via fstab / mount(8):
			  First positional argument (source) =
			    starts with `postgresql:`  -> connection string (same as -c)
			    otherwise                  -> settings file path (same as --setting-file)
			  Second positional argument (target) = mount point (same as -m).
			  -o key=val,flag,...          fstab-style bundled options.
			    e.g. -o connection=postgresql://...,cache-max-entries=4096,allow_other,_netdev
			  An fstab entry automatically daemonizes (detaches a child process) when stdout is a pipe
			  (i.e. when invoked from mount(8)). See docs/fstab-support.md.
			  (For the argument mapping / short-form collisions, see the relevant section of docs/fstab-support.md.)

			mount.pgfs is not available on Windows. Use pgfs.assign (the DokanNet version).
			See docs/Mount.md for details.

			Unmounting:
			  fusermount3 -u <mount-point>      Normal unmount.
			  umount <mount-point>              The symmetric command when mounted via fstab.
			  Ctrl+C                            Stop a running mount.pgfs (LazyUnmount).

			Note: if mount.pgfs terminates abnormally (crash / kill / etc.), only the kernel-side mount entry
			   is left behind and you get "Transport endpoint is not connected". In that case, manually run
			   `fusermount3 -u <mount-point>` (or `sudo umount <mount-point>`) before remounting.
			""");
	}
}
