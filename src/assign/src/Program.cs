namespace Pgfs.Assign;

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Core;
using Core.Api;
using Core.Config;
using Core.Logging;
using Dokan;
using Logger = Pgfs.Dokan.Logger;

/// <summary>
/// The assign.pgfs entry point. Mounts PGFS on a drive through DokanNet, on Windows.
///
/// On Linux / macOS, use <see cref="Pgfs.Mount"/> (Pgfs.Fuse) instead.
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

			var config = ConfigLoader.BuildRootConfigWithStore(args, out var loader);

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
				Console.Error.WriteLine("assign.pgfs is Windows-only. On Linux/macOS use mount.pgfs (the Pgfs.Fuse build).");
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
		// Leaving the scope (unmount / an exception / an early return) runs Api.Dispose -> deregister from
		// {prefix}mounts + stop the heartbeat + release the NotifyChannel. It is essential because FileSystem does
		// not release the api.
		// On the normal path it is Disposed explicitly below so the loss report can be read (a using's disposal
		// happens after the return value is evaluated, which is too late). A double Dispose is harmless.
		using var apiLifetime = api;

		var mountPoint = config.Mount.MountPoint;
		if (string.IsNullOrWhiteSpace(mountPoint)) {
			Console.Error.WriteLine("mount_point is not set. Specify it with --mount-point or in pgfs.toml.");
			return 1;
		}

		var mountPointError = DescribeUnusableMountPoint(mountPoint);
		if (mountPointError != null) {
			Console.Error.WriteLine(mountPointError);
			Logger.Error(mountPointError);
			return 1;
		}

		Logger.Information($"mounting PGFS: {mountPoint}");

		// The staged handling of a stop signal (Ctrl+C / Ctrl+Break). The number of stages and their meaning are
		// aligned with B-12 of mount.pgfs (Linux). **Putting the subscription in the tool exe** is the crux: back
		// when it was subscribed only inside FileSystem.Run(), the handler came off right after the first press
		// left Run, and the longest shutdown flush (api.Dispose = the default mount.write_back_flush_timeout_ms of
		// 30 seconds plus retries) was unguarded for its whole duration - a second Ctrl+C then became .NET's
		// default immediate exit, and neither the loss report, nor the {prefix}mounts gravestone (B-2), nor exit 4
		// was left behind. It ended in the way that erased the most information exactly when it was wanted most.
		//   1st press: request an unmount (as before)
		//   2nd press: cut the flush's persistence short (Api.AbandonFlush) = give up at the next deadline check
		//              and exit 4 **after emitting the loss report**
		//   3rd press onwards: stop swallowing it and go back to the .NET default (immediate exit). By this point
		//              the reporting path is given up on.
		// Note: the console's × button, a logoff and Stop-Process do not come through this handler
		//       (Windows cuts the first two off after about 5 seconds, and Stop-Process kills instantly with TerminateProcess).
		var stopSignals = 0;
		FileSystem? mounted = null;
		ConsoleCancelEventHandler onCancel = (_, e) => {
			var name = DescribeStopKey(e.SpecialKey);
			var count = System.Threading.Interlocked.Increment(ref stopSignals);
			if (count >= 3) {
				Logger.Warning(name, " received ", count, " times. Going back to the default behaviour (immediate exit).");
				e.Cancel = false;
				return;
			}
			if (count == 2) {
				Logger.Warning(name, " received twice. Cutting the flush wait short (whatever is unflushed will be lost).");
				api.AbandonFlush();
			}
			if (count == 1) {
				Logger.Information(name, " received. Trying to unmount.");
			}
			e.Cancel = true;
			// Harmless even after disposal (RequestStop does nothing from the second call on).
			mounted?.RequestStop();
		};
		Console.CancelKeyPress += onCancel;

		try {
			using (var fileSystem = new FileSystem(api, mountPoint)) {
				mounted = fileSystem;
				fileSystem.Run();
			}
			Logger.Information("PGFS unmounted.");
			// Write out whatever write-back has not flushed (a retry with a deadline; if anything is left, the Api
			// enumerates "what is lost" in the Error log). The unmount completes on the driver side, so the filesystem
			// cannot refuse it - **the exit code is the last reporting path left**. Aligned with the same contract as
			// mount.pgfs (Linux).
			api.Dispose();
			if (api.UnflushedAtShutdown > 0) {
				Logger.Error("unmounted with ", api.UnflushedAtShutdown, " unflushed item(s) still left (the contents are in the Error log above).");
				return await Task.FromResult(4);
			}
			return await Task.FromResult(0);
		} catch (Exception ex) {
			Logger.Error("an error occurred while mounted: ", ex);
			// Finish the flush **before removing the subscription** on the exception path too (the disposal of
			// `using var apiLifetime` happens after the finally, so without firing it here the state goes back to
			// "no handler while flushing").
			api.Dispose();
			return 3;
		} finally {
			Console.CancelKeyPress -= onCancel;
		}
	}

	/// <summary>The display name of a stop signal. Ctrl+Break arrives at the same handler (<see cref="ConsoleSpecialKey"/>).</summary>
	private static string DescribeStopKey(ConsoleSpecialKey key) {
		if (key == ConsoleSpecialKey.ControlBreak) { return "Ctrl+Break"; }
		return "Ctrl+C";
	}

	/// <summary>
	/// Returns the reason the mount target cannot be used (null when it can).
	/// <para>
	/// Given a drive letter that is already in use, Dokan falls over with <c>Something's wrong with the Dokan
	/// driver</c>, **a generic exception that says nothing about the cause** (measured). It is caught here
	/// first, and the free candidates are printed as well.
	/// A directory mount assumes "an existing empty directory" (docs/Assign.md, the prerequisites section).
	/// </para>
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static string? DescribeUnusableMountPoint(string mountPoint) {
		var trimmed = mountPoint.TrimEnd('\\', '/');
		if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0])) {
			// **`Directory.Exists` cannot decide this**: a CD-ROM with no medium in it, or a removable drive that is
			// not connected, has no root although the letter is occupied (hit on Q:).
			// "Whether the letter is in use" is read from the DriveInfo listing.
			var letter = char.ToUpperInvariant(trimmed[0]);
			if (!DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == letter)) {
				return null;
			}
			var free = FreeDriveLetters();
			var hint = free.Length switch {
				0 => "(there is no free drive letter)",
				_ => $"the free ones are {string.Join(", ", free)}",
			};
			return $"the mount target {trimmed} is already in use. Specify another drive letter. {hint}";
		}
		if (!Directory.Exists(trimmed)) {
			return $"the mount target directory does not exist: {trimmed} (create an empty directory first)";
		}
		if (Directory.EnumerateFileSystemEntries(trimmed).Any()) {
			// Dokan refuses to mount onto a directory that is not empty. The contents are only hidden, not lost.
			return $"the mount target directory is not empty: {trimmed}";
		}
		return null;
	}

	/// <summary>Enumerates the drive letters that are not in use (D: onwards). For the diagnostic message.</summary>
	private static string[] FreeDriveLetters() {
		var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
		return Enumerable.Range('D', 'Z' - 'D' + 1)
			.Select(c => (char)c)
			.Where(c => !used.Contains(c))
			.Select(c => $"{c}:")
			.ToArray();
	}

	private static void ShowHelp() {
		const string intro = """
			assign.pgfs — mount a PostgreSQL-backed filesystem via DokanNet (Windows)

			Usage: assign.pgfs [options]
			""";
		const string footer = """
			The mount point may be a drive letter ("P:") or a directory path
			("C:\mnt\pgfs").

			On non-Windows platforms use mount.pgfs (Pgfs.Fuse). See docs/Assign.md.
			""";
		Console.Write(HelpText.Build(Tool.Assign, intro, footer));
	}
}
