namespace Pgfs.Ctl;

using System.Text.Json;
using Core.Api;

/// <summary>
/// The CLI front end of the <c>prune</c> subcommand. **It cleans up what an abnormal exit left behind**
/// (docs/design/handle-context.md, stage C-3). The body is <see cref="PruneAdmin"/> (Core).
///
/// <para>
/// <b>The default is a dry run</b> - it only counts and shows, and deletes nothing. `--apply` carries it out.
/// **A cleanup command whose victims cannot be eyeballed first is dangerous**, so this default does not change.
/// </para>
/// </summary>
public static class PruneCommand
{
	public static int Run(string[] opts) {
		var apply = false;
		var force = false;
		var grace = PruneAdmin.DefaultMountsGraceSeconds;
		var rest = new System.Collections.Generic.List<string>();
		for (var i = 0; i < opts.Length; i++) {
			if (opts[i] == "--apply") {
				apply = true;
				continue;
			}
			if (opts[i] == "--force") {
				force = true;
				continue;
			}
			if (opts[i] == "--mounts-older-than" && i + 1 < opts.Length) {
				if (!long.TryParse(opts[i + 1], out grace)) {
					Console.Error.WriteLine($"--mounts-older-than: cannot be read as a number of seconds: {opts[i + 1]}");
					return 1;
				}
				i++;
				continue;
			}
			rest.Add(opts[i]);
		}
		// **The grace has a lower bound.** It doubles as the limit up to which a row from another host is treated
		// as live, so making it small treats mounts alive on other hosts as dead and removes the data side
		// (PruneAdmin.MinMountsGraceSeconds).
		if (grace < PruneAdmin.MinMountsGraceSeconds) {
			Console.Error.WriteLine($"--mounts-older-than: give at least {PruneAdmin.MinMountsGraceSeconds} seconds (given {grace}).");
			Console.Error.WriteLine("  A small value treats mounts alive on other hosts as dead and removes the bodies they are using.");
			return 1;
		}

		var (json, _, conn, schema, prefix) = CliUtil.Resolve(rest.ToArray());
		var admin = new PruneAdmin(conn, schema, prefix);
		var report = admin.Scan(grace);
		PruneResult? result = null;
		if (apply) { result = admin.Apply(report, force); }

		if (json) {
			Console.WriteLine(JsonSerializer.Serialize(ToJson(report, result, apply), JsonOpts));
			return 0;
		}
		PrintText(report, result, apply, force);
		return 0;
	}

	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	private static void PrintText(PruneReport report, PruneResult? result, bool apply, bool force) {
		if (!apply) {
			Console.WriteLine("prune (dry run - nothing has been deleted. Use --apply to carry it out)");
		}
		if (apply) {
			Console.WriteLine("prune --apply");
		}
		Console.WriteLine();

		Console.WriteLine($"Mount registry ({report.LiveMounts.Count} live):");
		if (!report.MountsTablePresent) {
			Console.WriteLine("  ({prefix}mounts table not present)");
		}
		foreach (var m in report.LiveMounts) {
			Console.WriteLine($"  live : {m.Host} {m.Mountpoint} (pid {m.Pid}, heartbeat {m.HeartbeatAgeSeconds}s ago)");
		}
		Console.WriteLine($"  stale rows to delete: {report.StaleMounts.Count} (heartbeat at least {report.MountsGraceSeconds}s ago / no loss)");
		foreach (var m in report.Tombstones) {
			// **The gravestones are not deleted.** They are kept to tell the operator how much could not be written back (B-2).
			Console.WriteLine($"  gravestones (kept): {m.Host} {m.Mountpoint} - {m.UnflushedLoss} unflushed");
		}
		Console.WriteLine();

		Console.WriteLine("Orphan data (bodies no inode references any more):");
		Console.WriteLine($"  {report.OrphanDataIds.Count} row(s) / {report.OrphanDataBytes} byte(s)");
		Console.WriteLine();

		Console.WriteLine("libfuse leftovers (.fuse_hidden*):");
		Console.WriteLine($"  {report.FuseHidden.Count}");
		foreach (var f in report.FuseHidden) {
			Console.WriteLine($"    {f.Name} (inode {f.Id}, {f.Size} byte(s))");
		}
		Console.WriteLine();

		if (!report.MountsTablePresent) {
			// **The registry cannot be read = whether any mount is alive cannot be decided.** Even --force leaves the data side alone.
			Console.WriteLine("Note: {prefix}mounts cannot be read, so whether any mount is alive cannot be decided.");
			Console.WriteLine("  The side that deletes data (orphan data / .fuse_hidden) is left alone even with --force.");
			Console.WriteLine("  On an existing filesystem, run the migration (docs/ddl/pgfs_mounts.sql) first (see the CHANGELOG).");
		}
		if (report.LiveMounts.Count > 0 && !force) {
			// **The side that deletes data stays away while any live mount is around.** It would otherwise catch
			// "bodies a running mount is keeping alive only while they are open", and the bodies of pending inodes
			// that are not in the database yet (docs/design/handle-context.md, the Core design of stage C).
			Console.WriteLine("Note: there are live mounts, so the side that deletes data (orphan data / .fuse_hidden) is left alone.");
			Console.WriteLine("  Stop every mount and run it again (or --force if you really must).");
		}
		if (result == null) {
			if (report.IsEmpty) { Console.WriteLine("There is nothing to clean up."); }
			return;
		}
		Console.WriteLine($"deleted: mounts {result.MountsDeleted} row(s) / .fuse_hidden {result.FuseHiddenDeleted} / orphan data {result.OrphanDataDeleted} row(s)");
		if (result.SkippedBecauseLive) {
			Console.WriteLine("(the side that deletes data was skipped for the reason above)");
		}
		if (result.SkippedBecauseUnknown) {
			Console.WriteLine("(whether any mount is alive could not be decided, so the side that deletes data was skipped)");
		}
	}

	private static object ToJson(PruneReport report, PruneResult? result, bool apply) {
		return new {
			dry_run = !apply,
			mounts = new {
				table_present = report.MountsTablePresent,
				live = report.LiveMounts.Count,
				stale = report.StaleMounts.Count,
				tombstones = report.Tombstones.Count,
				grace_seconds = report.MountsGraceSeconds,
			},
			orphan_data = new { rows = report.OrphanDataIds.Count, bytes = report.OrphanDataBytes },
			fuse_hidden = report.FuseHidden.Count,
			applied = result switch {
				{ } r => new {
					mounts_deleted = r.MountsDeleted,
					fuse_hidden_deleted = r.FuseHiddenDeleted,
					orphan_data_deleted = r.OrphanDataDeleted,
					skipped_because_live = r.SkippedBecauseLive,
					skipped_because_unknown = r.SkippedBecauseUnknown,
				},
				_ => null,
			},
		};
	}
}
