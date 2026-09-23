namespace Pgfs.Mount;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Core;
using Core.Api;
using Core.Config;
using Core.Logging;

/// <summary>
/// mount.pgfs entry point. Mounts PGFS via FUSE on Linux / macOS.
///
/// On Windows, use <see cref="Pgfs.Assign"/> (DokanNet) instead.
///
/// The flow:
///   1. Assemble a <see cref="RootConfig"/> from CLI / TOML / DB / Default with <see cref="ConfigLoader"/>
///   2. Check the Windows/FUSE dependencies
///   3. Prepare the <see cref="Api"/>, assemble the <see cref="FileSystem"/>, then <see cref="Pgfs.Fuse.Fuse.Mount"/>
///   4. Wait until it is unmounted
///
/// Implementation notes:
///   Pgfs.Fuse / Tmds.LibC have no Windows runtime, so referring to those types at the point <c>Main</c> is
///   JITted on Windows kills the process with a <c>FileNotFoundException</c>.
///   The Linux-specific mount logic is therefore split out into <see cref="RunFuseMountAsync"/> and JITted
///   lazily with <c>[MethodImpl(MethodImplOptions.NoInlining)]</c>.
///   Main itself holds no direct reference to Pgfs.Fuse / FileSystem.
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
			// When started as a `mount(8)` helper, the environment is stripped almost bare, PATH included (measured:
			// only 8 to 11 of LANG / LOGNAME / PWD / SHLVL / SUDO_* / TERM / USER / _).
			// Pgfs.Fuse's `HasFusermount` looks for `fusermount3` on `$PATH`, so with no PATH `CheckDependencies`
			// returns false and it exits immediately with "the FUSE dependencies were not found".
			// A minimal PATH is filled in right after start-up before going on. It is left alone when it is set explicitly.
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

			// Assemble the RootConfig with the new ConfigLoader. A POCO aggregate that merges CLI / TOML / DB / Default.
			// The Setting / Logging / Database / Mount / FileSystem scopes have all been turned into Config.
			var config = ConfigLoader.BuildRootConfigWithStore(passThroughArgs, out var loader);

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

			// The start-up banner (always on stderr, so the start and the end of the process can be followed even when
			// the log goes to a file).
			// **`started (pid ...)` is not printed here.** When daemonizing, this process exits the moment the mount is
			// established, so printing it here would leave **a pid that stops existing immediately** on the terminal
			// (a script that uses it to watch or to stop would always miss).
			// It is printed once the real daemon's pid is known (RunAsParentAsync below, or the foreground path).
			Logger.Lifecycle(AppInfo.Banner);
			// ----- end of what can be shared -----

			// FUSE is Linux/macOS only. On Windows the DokanNet build (assign.pgfs) is pointed at.
			if (OperatingSystem.IsWindows()) {
				Console.Error.WriteLine("mount.pgfs cannot be used on Windows. Use assign.pgfs (the DokanNet build).");
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
			if (!daemonize) {
				// In the foreground (--foreground) and in child mode this process runs the FUSE loop itself, so its own
				// pid is the real daemon's pid.
				Logger.Lifecycle($"started (pid {Environment.ProcessId})");
			}
			if (daemonize) {
				// **The warning about a past loss is emitted by the parent.** The child runs after throwing stdout/stderr
				// away, so a warning on the child side reaches nobody unless --log-output was given (which is exactly what
				// B-2 is about). This point, before the fork, is the only place that still holds a terminal.
				StatusAdmin.WarnPastLossToConsole(config.Database.Connection.ConnectionString, config.Database.SchemaName, config.Database.GetPrefix());
				return await RunAsParentAsync(args);
			}

			// From here on it is Linux/macOS only. It contains references to Pgfs.Fuse / FileSystem, so it is split
			// into a separate method to avoid the JIT error on Windows.
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
	/// Starts a child (with `--foreground-internal`) as the parent process, waits for the MOUNTED signal from
	/// the child and then exits. This satisfies the `mount(8)` helper convention (the parent exits once the
	/// mount is complete).
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
				return (child.ExitCode != 0) switch {
					true  => child.ExitCode,
					false => 1,
				};
			}

			if (line == MountedSignal) {
				// The mount succeeded. The parent exits at once and releases mount(8). The child lives on in the background.
				// **This is where `started (pid ...)` is printed for the first time**, and what it prints is **the child's
				// (the real daemon's) pid**, not the parent's - the parent disappears right after this, so printing the
				// parent's pid would make a script that watches or stops it always miss (actually hit during the B-1
				// performance measurements).
				Logger.Lifecycle($"started (pid {child.Id})");
				return 0;
			}

			// Relay any non-MOUNTED output as-is (log messages, etc.).
			Console.Out.WriteLine(line);
		}
	}


	[MethodImpl(MethodImplOptions.NoInlining)]
	private static async Task<int> RunFuseMountAsync(RootConfig config, bool isChild) {
		// Check that libfuse3 / fusermount3 are visible (Linux/macOS specific).
		if (!Pgfs.Fuse.Fuse.CheckDependencies()) {
			Console.Error.WriteLine("the FUSE dependencies were not found:");
			Console.Error.WriteLine(Pgfs.Fuse.Fuse.InstallationInstructions);
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
		// Leaving the scope (unmount / an exception / an early return) runs Api.Dispose -> deregister from
		// {prefix}mounts + stop the heartbeat + release the NotifyChannel. It is essential because FileSystem only
		// has the base Dispose and does not release the api.
		using var apiLifetime = api;

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

		using var fileSystem = new Pgfs.Fuse.FileSystem(api);
		var mountOptions = new Pgfs.Fuse.MountOptions {
			SingleThread = false,
			Options = fuseOptionsString,
			// The maximum size of one WRITE request = the granularity of a write tx. The default 0 leaves it to
			// libfuse's negotiation (measured: it goes up to the kernel limit of 1 MiB by default). It is set in the
			// init callback.
			MaxWrite = config.Mount.MaxWrite,
		};
		var maxWriteLabel = (config.Mount.MaxWrite == 0) switch {
			true  => "(libfuse default)",
			false => config.Mount.MaxWrite.ToString(),
		};
		Logger.Information("FUSE max_write: ", maxWriteLabel);

		try {
			using var fuseMount = Pgfs.Fuse.Fuse.Mount(mountPoint, fileSystem, mountOptions);
			Logger.Information("PGFS mounted successfully. To unmount, run fusermount3 -u, or stop it with Ctrl+C.");

			// The staged handling of a stop signal (B-12). It used to swallow every press no matter how many came, so
			// a second press during a long shutdown flush (the default mount.write_back_flush_timeout_ms = 30 seconds)
			// had no effect, and an operator who could not wait was left with nothing but SIGKILL
			// (= neither the loss report nor the {prefix}mounts gravestone is left behind, the worst possible ending).
			//   1st press: a graceful unmount (as before)
			//   2nd press: cut the flush's persistence short (Api.AbandonFlush) = give up at the next check point and
			//              exit 4 **after emitting the loss report**
			//   3rd press onwards: go back to the default behaviour (= immediate exit). By this point the reporting
			//              path is given up on.
			// When stopping through systemd, keep TimeoutStopSec > mount.write_back_flush_timeout_ms
			// (if it is shorter, SIGKILL arrives right after a single SIGTERM and all of these stages are skipped).
			var stopSignals = 0;
			// The return value = swallow this signal (true) / go back to the default behaviour (false).
			bool OnStopSignal(string name) {
				var count = System.Threading.Interlocked.Increment(ref stopSignals);
				if (count >= 3) {
					Logger.Warning(name, " received ", count, " times. Going back to the default behaviour (immediate exit).");
					return false;
				}
				if (count == 2) {
					Logger.Warning(name, " received twice. Cutting the flush wait short (whatever is unflushed will be lost).");
					api.AbandonFlush();
				}
				if (count == 1) {
					Logger.Information(name, " received. Trying to unmount.");
				}
				try {
					fuseMount.LazyUnmount();
				} catch (Exception ex) {
					Logger.Warning("error during LazyUnmount: ", ex);
				}
				return true;
			}

			Console.CancelKeyPress += (_, e) => {
				e.Cancel = OnStopSignal("Ctrl+C");
			};

			// A stop through systemd, or a server restart, arrives as SIGTERM (CancelKeyPress only picks up SIGINT).
			// Left at .NET's default behaviour (immediate exit) the process would disappear without running
			// write-back's FlushAll or the {prefix}mounts deregistration, so it is put on the same graceful unmount as SIGINT.
			using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
				ctx.Cancel = OnStopSignal("SIGTERM");
			});

			// Child mode: tell the parent (the mount(8) helper) "the mount succeeded" in a single line.
			// The parent exits once it has read this line.
			// After the parent exits, the child's stdout/stderr pipes close. To avoid blocking on a full buffer, the
			// console output target is switched to null once the signal has been sent
			// (the Logger goes through Console.Error too, so this costs the logs during FUSE operations but keeps it
			//  from hanging).
			if (isChild) {
				Console.Out.WriteLine(MountedSignal);
				Console.Out.Flush();
				Console.SetOut(TextWriter.Null);
				Console.SetError(TextWriter.Null);
			}

			await fuseMount.WaitForUnmountAsync();
			Logger.Information("PGFS unmounted.");
			// Write out whatever write-back has not flushed (a retry with a deadline; if anything is left, the Api
			// enumerates "what is lost" in the Error log). fusermount3 -u / umount(8) complete on the kernel side, so
			// the filesystem cannot refuse with EBUSY - **the exit code is the last reporting path left**, so it is
			// Disposed explicitly here and the result is read (a using's disposal happens after the return value is
			// evaluated, which is too late. A double Dispose is harmless).
			api.Dispose();
			if (api.UnflushedAtShutdown > 0) {
				Logger.Error("unmounted with ", api.UnflushedAtShutdown, " unflushed item(s) still left (the contents are in the Error log above).");
				return 4;
			}
			return 0;
		} catch (Exception ex) {
			Logger.Error("error while mounting: ", ex);
			return 3;
		}
	}

	private static void ShowHelp() {
		const string intro = """
			mount.pgfs — mount a PostgreSQL-backed filesystem via FUSE (Linux/macOS)

			Usage: mount.pgfs [options]
			       mount.pgfs <source> <mountpoint> [-o opts]   (via fstab / mount(8))
			""";
		const string footer = """
			fstab / mount(8) invocation:
			  positional 1 (source):
			    starts with "postgresql:"  -> connection string (same as -c)
			    otherwise                  -> setting file path (same as --setting-file)
			  positional 2 (target)        -> mount point (same as -m)
			  -o key=val,flag,...          fstab-style combined options, e.g.
			    -o connection=postgresql://...,cache-max-entries=4096,allow_other,_netdev
			  When invoked by mount(8) (stdout is a pipe), mount.pgfs daemonizes by
			  forking a child. See docs/fstab-support.md (argument table / short-flag
			  collisions are documented there).

			Note: as a mount(8) helper, -f / -s mean --fake / --sloppy, so use the long
			  forms --setting-file / --schema in that context.

			Unmount:
			  fusermount3 -u <mount-point>   normal unmount
			  umount <mount-point>           symmetric command for an fstab mount
			  Ctrl+C                         stop a running mount.pgfs (LazyUnmount)

			If mount.pgfs is killed abnormally, a stale kernel mount may remain
			("Transport endpoint is not connected"). Run
			  fusermount3 -u <mount-point>   (or sudo umount <mount-point>)
			before remounting.

			On Windows use assign.pgfs (DokanNet). See docs/Mount.md.
			""";
		Console.Write(HelpText.Build(Tool.Mount, intro, footer));
	}
}
