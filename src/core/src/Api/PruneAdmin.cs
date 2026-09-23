namespace Pgfs.Core.Api;

using System.Collections.Generic;
using System.Linq;
using Logging;
using Utility;

/// <summary>
/// **Cleans up what an abnormal termination left behind** (docs/handle-context.md, stage C-3). The body of `pgfsctl prune`.
///
/// <para>
/// All three kinds of leftovers are **the same shape of problem** - nobody cleans up after a mount that
/// died abnormally:
/// </para>
/// <list type="number">
///   <item><b>Rows in <c>{prefix}mounts</c></b>: they are only removed on a clean exit, so every
///     <c>kill -9</c> leaves one behind (99 rows accumulated during testing). **The only harm is how
///     status looks.**</item>
///   <item><b>Orphaned data rows</b>: stage C-2 keeps the body alive while a handle is open, so **if the
///     daemon dies in the middle, a data row nobody references is left behind**. It does not appear in the
///     namespace, but it does count towards <c>df</c>.</item>
///   <item><b><c>.fuse_hidden*</c></b>: the trace left when libfuse with <c>hard_remove = 0</c> turned an
///     unlink-while-open into a rename. **A <c>kill -9</c> of the daemon leaves it, content and all,
///     forever, and it shows up in the ls of other mounts too.** Once stage C-2 is in, **nobody creates
///     them any more**, so a one-time cleanup is enough.</item>
/// </list>
///
/// <para>
/// <b>There is one shared danger</b>: **deleting something a currently live mount is still using breaks it**.
/// That is why **the liveness test differs per kind of leftover**:
/// </para>
/// <list type="bullet">
///   <item>A <c>mounts</c> row (**whose only harm is cosmetic**) is removed once its heartbeat is old enough.</item>
///   <item>**The side that deletes data** (orphaned data, <c>.fuse_hidden*</c>) requires that **no live mount
///     exists at all**. Judging by heartbeat age alone and deleting the rows first would make **a live mount
///     that merely lost its heartbeat to a database problem** invisible to the next run, cascading into
///     deleting a body that is still in use.</item>
/// </list>
///
/// <para>
/// <b>The same condition is what keeps pending inodes out of it.</b> A pending inode of metadata write-back
/// **exists only in the memory of a running mount**, so its data row looks unreferenced.
/// Requiring that **no live mount exists at all** avoids that structurally.
/// </para>
/// </summary>
public sealed class PruneAdmin
{
	// **The 90-second heartbeat threshold (StatusAdmin's live display) is not used here.**
	// prune's liveness test is "the pid on the same host, live until the grace period on another host" (review H-3).
	// Cutting at 90 seconds deletes the body of **a live mount that merely lost its heartbeat to a stalled database**.
	// Live for display (status) and live for "is it safe to delete data" (prune) are **different questions**.

	/// <summary>
	/// The default heartbeat age after which a <c>{prefix}mounts</c> row may be removed (one hour).
	/// **It is also the limit up to which a row from another host is treated as live** (<see cref="IsLive"/>).
	/// </summary>
	public const long DefaultMountsGraceSeconds = 3600;

	/// <summary>
	/// **The lower bound** of <c>--mounts-older-than</c> (10 minutes = 20 heartbeats of 30 seconds).
	/// <para>
	/// The grace doubles as **the limit up to which a row from another host is treated as live**
	/// (<see cref="IsLive"/>). Passing a small value, 0 or a negative one would **drop the row of a mount that is
	/// alive on another host as stale, take it out of the live check at the same time, and run the data-deleting
	/// side**. A row removed that way is not brought back by the heartbeat's UPDATE, so it would keep being
	/// invisible and keep being removed by every later prune.
	/// </para>
	/// </summary>
	public const long MinMountsGraceSeconds = 600;

	private readonly string connectionString;
	private readonly string schemaName;
	private readonly string tablePrefix;

	public PruneAdmin(string connectionString, string schemaName, string tablePrefix) {
		this.connectionString = connectionString;
		this.schemaName = schemaName;
		this.tablePrefix = tablePrefix;
	}

	private string QualifiedTable(string name) => $"\"{this.schemaName}\".\"{this.tablePrefix}{name}\"";

	/// <summary>Counts the leftovers (**read-only**).</summary>
	public PruneReport Scan(long mountsGraceSeconds) {
		if (mountsGraceSeconds < MinMountsGraceSeconds) {
			throw new System.ArgumentOutOfRangeException(
				nameof(mountsGraceSeconds),
				mountsGraceSeconds,
				$"The grace must be at least {MinMountsGraceSeconds} seconds (so that a mount alive on another host is not treated as dead)");
		}
		var report = new PruneReport { MountsGraceSeconds = mountsGraceSeconds };
		this.ScanMounts(report);
		this.ScanOrphanData(report);
		this.ScanFuseHidden(report);
		return report;
	}

	private void ScanMounts(PruneReport report) {
		var sql = $@"SELECT mount_id, host, pid, mountpoint,
				EXTRACT(EPOCH FROM ((now() AT TIME ZONE 'UTC') - heartbeat_at))::bigint AS hb_age,
				COALESCE((stats->>'unflushedLoss')::int, 0) AS loss
			FROM {this.QualifiedTable("mounts")} ORDER BY host, mountpoint";
		List<dynamic> rows;
		try {
			rows = Pg.Query<dynamic>(this.connectionString, sql).ToList();
		} catch (System.Exception ex) {
			Logger.Warning("prune: cannot read the mounts registry (the table may not exist yet): ", ex.Message);
			report.MountsTablePresent = false;
			return;
		}
		foreach (var row in rows) {
			var entry = new PruneMount {
				MountId = (string)row.mount_id,
				Host = (string)row.host,
				Pid = (long)row.pid,
				Mountpoint = (string)row.mountpoint,
				HeartbeatAgeSeconds = (long)row.hb_age,
				UnflushedLoss = (int)row.loss,
			};
			// The liveness test **depends on the host** (see IsLive below).
			if (this.IsLive(entry, report.MountsGraceSeconds)) { report.LiveMounts.Add(entry); }
			// **Tombstones (B-2) are never removed.** They drive the warning on the next mount, so an operator
			// reviews them and deletes them by hand.
			if (entry.UnflushedLoss > 0) {
				report.Tombstones.Add(entry);
				continue;
			}
			if (entry.HeartbeatAgeSeconds < report.MountsGraceSeconds) { continue; }
			report.StaleMounts.Add(entry);
		}
	}

	/// <summary>
	/// **Whether the mount behind this row is alive.** This decides whether the data-deleting side may run.
	/// <para>
	/// <b>On the same host the answer is whether the pid is alive; the heartbeat is not consulted.</b>
	/// There are two directions to get wrong, and **neither one alone is enough**:
	/// </para>
	/// <list type="bullet">
	///   <item><b>Demotion</b>: a row killed with <c>kill -9</c> keeps a fresh heartbeat, so **for the
	///     90 seconds right after it died it still looks live**. Cleanup of the data side stalls for that
	///     window, which means **it cannot clean up exactly when there is most to clean up** (measured).</item>
	///   <item><b>Promotion</b>: **a live mount that merely lost its heartbeat to a database problem or a
	///     stall** is treated as dead when only the heartbeat is consulted. Running against it **deletes a
	///     body that is still open**. The two used to be ANDed together, so **promotion had no effect and
	///     the span from 90 seconds to the grace period was a blind zone that was neither live nor stale**
	///     (review H-3).</item>
	/// </list>
	/// <para>
	/// <b>A row from another host offers no pid to check</b>, so it is **treated as live until the grace
	/// period (<c>--mounts-older-than</c>, 3600 seconds by default) has passed**. Cutting here at the
	/// 90-second heartbeat would **delete the body of a mount on another host that merely hit a temporary
	/// database stall**. A row past the grace period is also a <c>StaleMounts</c> deletion candidate in the
	/// same run, so by then the basis for the question is gone anyway.
	/// </para>
	/// </summary>
	private bool IsLive(PruneMount entry, long mountsGraceSeconds) {
		// **Compare with the same API the registration side used.** `{prefix}mounts.host` is written by `Api`
		// using `Dns.GetHostName()`, and **on Linux that is the FQDN while `Environment.MachineName` is the
		// short name**, so comparing against the latter **decides "another host" for what is in fact the same
		// host, errs on the safe side, and stalls the data-side cleanup forever**
		// (measured on Linux: `host.example.internal` versus `host`).
		// **Comparing only the first label** is not done, because it **would treat same-named hosts in
		// different domains as identical**.
		if (!string.Equals(entry.Host, System.Net.Dns.GetHostName(), System.StringComparison.OrdinalIgnoreCase)) {
			return entry.HeartbeatAgeSeconds < mountsGraceSeconds;
		}
		return this.PidLooksAlive(entry);
	}

	/// <summary>
	/// **For a row on the same host, whether the pid still belongs to its owner.**
	/// A pid can be reused, so **the process name is checked for being one of ours** as well.
	/// </summary>
	private bool PidLooksAlive(PruneMount entry) {
		try {
			var process = System.Diagnostics.Process.GetProcessById((int)entry.Pid);
			var name = process.ProcessName;
			if (name.StartsWith("assign.pgfs", System.StringComparison.OrdinalIgnoreCase)) { return true; }
			if (name.StartsWith("mount.pgfs", System.StringComparison.OrdinalIgnoreCase)) { return true; }
			// The pid is alive but belongs to a different process = it was reused. The owner of this row is dead.
			Logger.Debug("prune: pid ", entry.Pid, " is a different process (", name, "), so the row is treated as dead");
			return false;
		} catch (System.ArgumentException) {
			// No such pid = dead.
			return false;
		} catch (System.Exception ex) {
			// When it cannot be decided, err **on the safe side** (assume it is alive).
			Logger.Warning("prune: cannot determine whether pid ", entry.Pid, " is alive: ", ex.Message);
			return true;
		}
	}

	/// <summary>
	/// Collects **data rows that no inode references**.
	/// <para>
	/// **Two queries are issued and the difference is taken on the client.** `data` and `inode` have
	/// **different distribution keys**, so on Citus an anti-join turns into a repartition join and is
	/// rejected (measured: even a join between inodes produced
	/// <c>the query contains a join that requires repartitioning</c>). prune is an administrative command,
	/// so two round trips plus a local set operation are good enough.
	/// </para>
	/// </summary>
	private void ScanOrphanData(PruneReport report) {
		var dataIds = Pg.Query<long>(this.connectionString, $"SELECT id FROM {this.QualifiedTable("data")}").ToList();
		if (dataIds.Count == 0) { return; }
		var used = new HashSet<long>(Pg.Query<long>(
			this.connectionString,
			$"SELECT DISTINCT data_id FROM {this.QualifiedTable("inode")} WHERE data_id IS NOT NULL"));
		var orphans = dataIds.Where(id => !used.Contains(id)).ToList();
		if (orphans.Count == 0) { return; }
		report.OrphanDataIds = orphans;
		report.OrphanDataBytes = Pg.Query<long?>(
			this.connectionString,
			$"SELECT COALESCE(sum(total_size), 0) FROM {this.QualifiedTable("data")} WHERE id = ANY(@ids)",
			new { ids = orphans }).FirstOrDefault() ?? 0;
	}

	/// <summary>
	/// Collects the traces libfuse leaves when it turns an unlink-while-open into a rename (<c>.fuse_hidden*</c>).
	/// <para>
	/// <b>The SQL only does a coarse pre-filter; the accept/reject decision is funnelled through
	/// <see cref="Api.IsLibfuseHidden"/>.</b>
	/// Deleting on a prefix match alone would take **an ordinary file the user created themselves, such as
	/// <c>.fuse_hidden_notes.txt</c>**, content and all. **The listing side
	/// (<see cref="Api.IsLibfuseHidden"/>) tests strictly and does not hide such a file**, so it is plainly
	/// visible in <c>ls</c> = **something the user can see would disappear**.
	/// The cause was having the test in two places, so **the deleting side now uses the same function as the
	/// listing side**.
	/// </para>
	/// <para>
	/// <c>_</c> is a LIKE wildcard, so an <c>ESCAPE</c> is added too. Without it the pattern would also match
	/// <c>.fuseAhidden...</c> (the strict test below would reject it, but there is no point pulling it out of
	/// the database).
	/// </para>
	/// </summary>
	private void ScanFuseHidden(PruneReport report) {
		var sql = $"SELECT id, parent_id, name, st_size FROM {this.QualifiedTable("inode")} WHERE name LIKE @pattern ESCAPE '\\' ORDER BY id";
		var rows = Pg.Query<dynamic>(this.connectionString, sql, new { pattern = @".fuse\_hidden%" }).ToList();
		foreach (var row in rows) {
			// **Anything that does not match libfuse's format (prefix plus 16 hex digits) is a user file.** Leave it alone.
			if (!Api.IsLibfuseHidden((string)row.name)) { continue; }
			report.FuseHidden.Add(new PruneInode {
				Id = (long)row.id,
				ParentId = (long)row.parent_id,
				Name = (string)row.name,
				Size = (long)row.st_size,
			});
		}
	}

	/// <summary>
	/// Performs the cleanup. **Pass the result of <see cref="Scan"/> unchanged** (= only delete what was shown).
	/// <para>
	/// **The data-deleting side is skipped whenever even one live mount exists** (<paramref name="force"/>
	/// overrides this). `force` is meant to be used **after stopping every mount**.
	/// </para>
	/// </summary>
	public PruneResult Apply(PruneReport report, bool force) {
		var result = new PruneResult();
		foreach (var mount in report.StaleMounts) {
			result.MountsDeleted += Pg.Execute(
				this.connectionString,
				$"DELETE FROM {this.QualifiedTable("mounts")} WHERE mount_id = @id",
				new { id = mount.MountId });
		}
		// **When the registry cannot be read, nobody knows who is alive.** An empty `LiveMounts` means "cannot be
		// seen", not "there are none", so **the data-deleting side is left alone even with `force`**. The typical
		// case is a v0.1.0 filesystem mounted by v0.2.0 without migrating `{prefix}mounts`; firing there would
		// **remove the bodies and the `.fuse_hidden*` files that running mounts have open**.
		if (!report.MountsTablePresent) {
			result.SkippedBecauseUnknown = true;
			return result;
		}
		if (report.LiveMounts.Count > 0 && !force) {
			result.SkippedBecauseLive = true;
			return result;
		}
		foreach (var inode in report.FuseHidden) {
			// **The inode row goes first.** Deleting the data first would, if this died halfway, leave a row
			// that has a name but no body, and reads would return NUL bytes - the same shape of bug that
			// docs/data-id-lifecycle.md fixed.
			result.FuseHiddenDeleted += Pg.Execute(
				this.connectionString,
				$"DELETE FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent AND id = @id",
				new { parent = inode.ParentId, id = inode.Id });
		}
		// **The data behind the <c>.fuse_hidden*</c> entries just deleted is orphaned as of this point.**
		// To catch it in the same run, only the data side is scanned again and the result is deleted together
		// with what the first scan found.
		var after = new PruneReport();
		this.ScanOrphanData(after);
		var targets = new HashSet<long>(report.OrphanDataIds);
		foreach (var id in after.OrphanDataIds) { targets.Add(id); }
		foreach (var dataId in targets) {
			Pg.Execute(this.connectionString, $"DELETE FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @id", new { id = dataId });
			result.OrphanDataDeleted += Pg.Execute(this.connectionString, $"DELETE FROM {this.QualifiedTable("data")} WHERE id = @id", new { id = dataId });
		}
		return result;
	}
}

/// <summary>The leftovers `prune` found (a read-only result).</summary>
public sealed class PruneReport
{
	public bool MountsTablePresent { get; set; } = true;
	public long MountsGraceSeconds { get; set; } = PruneAdmin.DefaultMountsGraceSeconds;

	/// <summary>Mounts considered alive. **This is what decides whether the data-deleting side may run.**</summary>
	public List<PruneMount> LiveMounts { get; } = new();

	/// <summary>Rows whose heartbeat is old enough and that carry no loss (deletion candidates).</summary>
	public List<PruneMount> StaleMounts { get; } = new();

	/// <summary>Tombstones that carry a loss (**never deleted here** - an operator reviews them and deletes them).</summary>
	public List<PruneMount> Tombstones { get; } = new();

	public List<long> OrphanDataIds { get; set; } = new();
	public long OrphanDataBytes { get; set; }
	public List<PruneInode> FuseHidden { get; } = new();

	public bool IsEmpty => this.StaleMounts.Count == 0 && this.OrphanDataIds.Count == 0 && this.FuseHidden.Count == 0;
}

/// <summary>One <c>{prefix}mounts</c> row as seen by `prune`.</summary>
public sealed class PruneMount
{
	public string MountId { get; set; } = "";
	public string Host { get; set; } = "";
	public long Pid { get; set; }
	public string Mountpoint { get; set; } = "";
	public long HeartbeatAgeSeconds { get; set; }
	public int UnflushedLoss { get; set; }
}

/// <summary>An inode row as seen by `prune` (<c>.fuse_hidden*</c>).</summary>
public sealed class PruneInode
{
	public long Id { get; set; }
	public long ParentId { get; set; }
	public string Name { get; set; } = "";
	public long Size { get; set; }
}

/// <summary>The result of `prune --apply`.</summary>
public sealed class PruneResult
{
	public int MountsDeleted { get; set; }
	public int FuseHiddenDeleted { get; set; }
	public int OrphanDataDeleted { get; set; }

	/// <summary>A live mount existed, so **the data-deleting side was skipped**.</summary>
	public bool SkippedBecauseLive { get; set; }

	/// <summary>
	/// The registry could not be read, so **whether any mount is alive could not be decided** and the
	/// data-deleting side was skipped (**even with `force`**).
	/// </summary>
	public bool SkippedBecauseUnknown { get; set; }
}
