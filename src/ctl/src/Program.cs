namespace Pgfs.Ctl;

using Core.Logging;

/// <summary>
/// The pgfsctl entry point - the admin tool that gathers PGFS's runtime control plane (config / status).
///
/// <para>
/// It does not depend on FUSE or Dokan and references Core alone, so it runs on both Linux and Windows. It
/// is a subcommand tool (<c>switch(args[0])</c>) dispatching to <c>config</c> / <c>status</c>. The design of
/// record is docs/design/runtime-control-plane.md, the pgfsctl section.
/// </para>
/// </summary>
public static class Program
{
	public static int Main(string[] args) {
		// It is a CLI tool, so stdout (and --json in particular) must not be polluted by info/trace SQL dumps.
		// **The warnings Core emits are pinned to stderr.** The logger's default output is stderr in Release but
		// **stdout in a Debug build** (the #if DEBUG in ILogger.DefaultOutput), so leaving it to the default mixes a
		// warning line in front of `--json` in Debug and makes it unreadable as JSON (a test actually hit this).
		Logger.MinLevel = Level.Warning;
		Logger.Output = Console.Error.WriteLine;
		try {
			if (args.Length == 0) {
				ShowUsage();
				return 1;
			}
			var sub = args[0];
			var rest = args[1..];
			switch (sub) {
				case "config":
					return ConfigCommand.Run(rest);
				case "status":
					return StatusCommand.Run(rest);
				case "prune":
					return PruneCommand.Run(rest);
				case "-h":
				case "-?":
				case "--help":
					ShowUsage();
					return 0;
				case "--version":
					Console.WriteLine(Pgfs.Core.AppInfo.Banner);
					return 0;
			}
			Console.Error.WriteLine($"unknown subcommand '{sub}'");
			ShowUsage();
			return 1;
		} catch (Exception ex) {
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 1;
		}
	}

	private static void ShowUsage() {
		Console.Error.WriteLine("""
			pgfsctl — PGFS runtime control plane (config / status)

			Usage:
			  pgfsctl config list [--json] [connection opts]
			  pgfsctl config get <scope.key> [--json] [connection opts]
			  pgfsctl config set <scope.key> <value> [connection opts]
			  pgfsctl status [--json] [connection opts]
			  pgfsctl prune [--apply] [--force] [--mounts-older-than <sec>] [--json] [conn opts]

			Connection opts are resolved like mount.pgfs:
			  -c/--connection <connstr>   -s/--schema <name>   -x/--prefix <prefix>
			  -f/--setting-file <toml>    --setting-path <dirs>
			(also read from pgfs.toml via the search path).

			'config set' applies live to running mounts via NOTIFY where the field is
			live-reloadable; DB-backed fields persist to {prefix}settings. See
			docs/design/runtime-control-plane.md for the (SaveTo, Reload) matrix.

			'status' shows the cluster mount registry ({prefix}mounts) and DB-derived
			filesystem stats (inode/file/chunk counts, used bytes, audit, citus).

			'prune' cleans up what an abnormal exit left behind: stale {prefix}mounts
			rows, orphan data rows, and the .fuse_hidden* leftovers of libfuse. It is a
			DRY RUN by default; pass --apply to delete. Rows that record unflushed loss
			(tombstones) are never deleted. The data-destroying part is skipped while any
			mount is live (--force overrides; stop every mount first).
			""");
	}
}
