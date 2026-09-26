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
/// <para>
/// <b>Exit codes</b> (v0.2.2): 0 = success / 1 = <c>--check</c> found something to clean up (tombstones included) /
/// 2 = an argument error. <c>--check</c> prints the same report as the dry run, for cron / monitoring to "tell me when
/// things pile up".
/// </para>
/// </summary>
public static class PruneCommand
{
	public static int Run(string[] opts) {
		var apply = false;
		var force = false;
		var check = false;
		var forget = new System.Collections.Generic.List<string>();
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
			if (opts[i] == "--check") {
				check = true;
				continue;
			}
			if (opts[i] == "--forget-tombstone") {
				if (i + 1 >= opts.Length) {
					Console.Error.WriteLine("--forget-tombstone: give a mount_id (or all).");
					return 2;
				}
				forget.Add(opts[i + 1]);
				i++;
				continue;
			}
			if (opts[i] == "--mounts-older-than" && i + 1 < opts.Length) {
				if (!long.TryParse(opts[i + 1], out grace)) {
					Console.Error.WriteLine($"--mounts-older-than: cannot be read as a number of seconds: {opts[i + 1]}");
					return 2;
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
			return 2;
		}
		// --check only looks. When it comes together with the instruction to delete, which one is meant is unclear, so nothing is done.
		if (check && apply) {
			Console.Error.WriteLine("--check and --apply cannot be given together (--check to only look, --apply to delete).");
			return 2;
		}

		// **Removing gravestones by name is not mixed with the ordinary cleanup** (v0.2.2). It is a separate action
		// that looks only at gravestones and removes only gravestones.
		if (forget.Count > 0 && (check || force)) {
			Console.Error.WriteLine("--forget-tombstone cannot be given together with --check / --force (it only removes gravestones; use --apply to remove them).");
			return 2;
		}

		var (json, _, conn, schema, prefix) = CliUtil.Resolve(rest.ToArray());
		var admin = new PruneAdmin(conn, schema, prefix);
		var report = admin.Scan(grace);
		if (forget.Count > 0) {
			return ForgetTombstones(admin, report, forget, apply, json);
		}
		PruneResult? result = null;
		if (apply) { result = admin.Apply(report, force); }

		if (json) {
			Console.WriteLine(JsonSerializer.Serialize(ToJson(report, result, apply), JsonOpts));
		}
		if (!json) {
			PrintText(report, result, apply, force);
		}
		// **Tombstones also count as "found".** They are not deleted, but they are something an operator should
		// review and clear away (the record of an unflushed loss).
		if (check && (!report.IsEmpty || report.Tombstones.Count > 0)) {
			return 1;
		}
		return 0;
	}

	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	/// <summary>
	/// <c>--forget-tombstone &lt;mount_id&gt;</c> (repeatable) / <c>--forget-tombstone all</c>. **The default is a dry run**
	/// (it only shows the gravestones it would remove); <c>--apply</c> removes them. **If even one of the named ids is
	/// not a gravestone, nothing is removed and the exit code is 1** - a typo is a sign that "which ones to remove" has
	/// gone wrong, so the correct ones are not removed on their own. Rows that are not gravestones (live / ordinary old
	/// rows) cannot be removed this way.
	/// </summary>
	private static int ForgetTombstones(PruneAdmin admin, PruneReport report, System.Collections.Generic.List<string> forget, bool apply, bool json) {
		var targets = new System.Collections.Generic.List<PruneMount>();
		var unknown = new System.Collections.Generic.List<string>();
		var all = forget.Contains("all");
		foreach (var tomb in report.Tombstones) {
			if (all || forget.Contains(tomb.MountId)) { targets.Add(tomb); }
		}
		foreach (var id in forget) {
			if (id == "all") { continue; }
			if (targets.Exists(t => t.MountId == id)) { continue; }
			unknown.Add(id);
		}
		var deleted = 0;
		var refused = !report.MountsTablePresent || unknown.Count > 0;
		if (apply && !refused) {
			foreach (var tomb in targets) { deleted += admin.ForgetTombstone(tomb.MountId); }
		}

		if (json) {
			Console.WriteLine(JsonSerializer.Serialize(new {
				dry_run = !apply,
				mounts_table_present = report.MountsTablePresent,
				forget_tombstones = targets.ConvertAll(t => new { mount_id = t.MountId, host = t.Host, mountpoint = t.Mountpoint, unflushed_loss = t.UnflushedLoss }),
				not_tombstone = unknown,
				deleted,
			}, JsonOpts));
		}
		if (!json) {
			PrintForget(report, targets, unknown, apply, refused, deleted);
		}
		if (refused) { return 1; }
		return 0;
	}

	private static void PrintForget(PruneReport report, System.Collections.Generic.List<PruneMount> targets, System.Collections.Generic.List<string> unknown, bool apply, bool refused, int deleted) {
		if (!apply) {
			Console.WriteLine("prune --forget-tombstone (dry run - nothing has been deleted. Use --apply to delete)");
		}
		if (apply) {
			Console.WriteLine("prune --forget-tombstone --apply");
		}
		Console.WriteLine();
		if (!report.MountsTablePresent) {
			Console.WriteLine("* {prefix}mounts cannot be read, so the gravestones cannot be found.");
			return;
		}
		Console.WriteLine($"gravestones to delete: {targets.Count}");
		foreach (var t in targets) {
			Console.WriteLine($"  {t.MountId}  {t.Host} {t.Mountpoint} - {t.UnflushedLoss} unflushed item(s)");
		}
		foreach (var id in unknown) {
			// **Rows that are not gravestones are not removed this way** (live rows and ordinary old rows cannot be removed by name).
			Console.WriteLine($"  not a gravestone (or not found): {id}");
		}
		if (refused) {
			Console.WriteLine();
			Console.WriteLine("An id that is not a gravestone was given, so nothing is deleted. Check the ids in the loss lines of `pgfsctl status`.");
			return;
		}
		if (apply) {
			Console.WriteLine();
			Console.WriteLine($"deleted: {deleted} gravestone row(s)");
		}
	}

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
		Console.WriteLine($"  stale rows to delete: {report.StaleMounts.Count} (rows that are not alive = same host: the pid is gone / other hosts: heartbeat at least {report.MountsGraceSeconds}s ago; no loss)");
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
