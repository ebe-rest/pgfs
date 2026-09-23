namespace Pgfs.Core.Api;

using System.Collections.Generic;
using System.Linq;
using Pgfs.Core.Config;
using Pgfs.Core.Logging;
using Pgfs.Core.Utility;

/// <summary>
/// The body of the <c>status</c> subcommand (lives in Core so it can be reused). Shared by pgfsctl
/// (the CLI) and the GUI. It returns **Layer 1 (which mounts are running in the cluster) and
/// Layer 2 (filesystem statistics)** read-only from the database
/// (the design is in docs/runtime-control-plane.md).
///
/// <para>
/// Layer 3 (per-process cache statistics and effective settings) works the same way: the mount writes
/// a snapshot into <c>{prefix}mounts.stats</c> on every heartbeat, and this class only reads it.
/// </para>
/// </summary>
public sealed class StatusAdmin
{
	/// <summary>A mount counts as "live" when its heartbeat is younger than this (three times the 30s heartbeat = up to two missed beats are tolerated).</summary>
	private const long LiveThresholdSeconds = 90;

	private readonly string connectionString;
	private readonly string schemaName;
	private readonly string tablePrefix;
	private readonly ConfigStore store;

	public StatusAdmin(string connectionString, string schemaName, string tablePrefix) {
		this.connectionString = connectionString;
		this.schemaName = schemaName;
		this.tablePrefix = tablePrefix;
		this.store = new ConfigStore(connectionString, schemaName, tablePrefix);
	}

	/// <summary>
	/// Reads <c>{prefix}mounts</c> and returns every row annotated with uptime, heartbeat age and
	/// whether it is live (Layer 1). When the table is absent (a filesystem that has not been re-mkfs'd)
	/// the result is <see cref="MountsStatus.TablePresent"/>=false with an empty list.
	/// Ages are computed from the database's <c>now()</c> rather than the client clock, to avoid clock skew.
	/// </summary>
	public MountsStatus ListMounts() {
		var sql = $@"SELECT mount_id, host, pid, mountpoint, mode,
				EXTRACT(EPOCH FROM ((now() AT TIME ZONE 'UTC') - started_at))::bigint   AS uptime,
				EXTRACT(EPOCH FROM ((now() AT TIME ZONE 'UTC') - heartbeat_at))::bigint AS hb_age,
				config::text AS config, stats::text AS stats,
				COALESCE((stats->>'unflushedLoss')::int, 0) AS loss, stats->>'endedAt' AS ended_at
			FROM {this.QualifiedTable("mounts")}
			ORDER BY host, started_at";
		List<dynamic> rows;
		try {
			rows = Pg.Query<dynamic>(this.connectionString, sql).ToList();
		} catch (System.Exception ex) {
			Logger.Warning("status: cannot read the mounts registry (the table may not exist yet): ", ex.Message);
			return new MountsStatus { TablePresent = false, Mounts = new List<MountInfo>() };
		}
		var list = new List<MountInfo>();
		foreach (var row in rows) {
			var hbAge = (long)row.hb_age;
			list.Add(new MountInfo {
				MountId = (string)row.mount_id,
				Host = (string)row.host,
				Pid = (long)row.pid,
				Mountpoint = (string)row.mountpoint,
				Mode = (string)row.mode,
				UptimeSeconds = (long)row.uptime,
				HeartbeatAgeSeconds = hbAge,
				Live = hbAge < LiveThresholdSeconds,
				ConfigJson = JsonOrEmpty((object?)row.config),
				StatsJson = JsonOrEmpty((object?)row.stats),
				UnflushedLoss = (int)row.loss,
				EndedAt = (string?)row.ended_at,
			});
		}
		return new MountsStatus { TablePresent = true, Mounts = list };
	}

	/// <summary>
	/// Warns **on stderr** when a past unmount left unflushed data behind (a tombstone) (B-2).
	/// <para>
	/// <c>mount.pgfs</c> daemonizes and the child discards stdout/stderr, so nothing logged by the child
	/// reaches anybody unless <c>--log-output</c> was given. **Only the parent, before the fork, still has
	/// a terminal**, which is why this is a static method meant to be called from there. A failure here
	/// never blocks startup.
	/// </para>
	/// </summary>
	public static void WarnPastLossToConsole(string connectionString, string schemaName, string tablePrefix) {
		try {
			var admin = new StatusAdmin(connectionString, schemaName, tablePrefix);
			var mounts = admin.ListMounts();
			if (!mounts.TablePresent) { return; }
			var lost = new List<MountInfo>();
			foreach (var m in mounts.Mounts) {
				if (m.UnflushedLoss > 0) { lost.Add(m); }
			}
			if (lost.Count == 0) { return; }
			System.Console.Error.WriteLine($"pgfs: warning - {lost.Count} record(s) of a past unmount that could not write out its unflushed data (the lost content cannot be recovered)");
			foreach (var m in lost) {
				System.Console.Error.WriteLine($"pgfs:   lost {m.UnflushedLoss}: {m.Host} {m.Mountpoint} (endedAt {Api.FormatUtcStamp(m.EndedAt)} / mount_id {m.MountId})");
			}
			System.Console.Error.WriteLine("pgfs:   see pgfsctl status for details. Once reviewed, delete them with: DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0");
		} catch (System.Exception ex) {
			Logger.Debug("could not query past losses (ignored): ", ex.Message);
		}
	}

	/// <summary>Aggregates the filesystem statistics (Layer 2) from the database. A number that could not be aggregated comes back as -1 (= unknown).</summary>
	public FsStats GetFsStats() {
		var settings = this.LoadSettings();
		var citus = SettingBool(settings, Schema.Database.Citus);
		return new FsStats {
			Schema = this.schemaName,
			Prefix = this.tablePrefix,
			Version = Setting(settings, Schema.FileSystem.Version),
			VolumeLabel = Setting(settings, Schema.FileSystem.VolumeLabel),
			InodeCount = this.ScalarLong($"SELECT count(*) FROM {this.QualifiedTable("inode")}"),
			FileCount = this.ScalarLong($"SELECT count(*) FROM {this.QualifiedTable("data")}"),
			ChunkCount = this.ScalarLong($"SELECT count(*) FROM {this.QualifiedTable("data_chunk")}"),
			UsedBytes = this.ScalarLong($"SELECT COALESCE(SUM(length(payload)),0)::bigint FROM {this.QualifiedTable("data_chunk")}"),
			ClusterSize = SettingLong(settings, Schema.FileSystem.ClusterSize),
			MaxFileSize = SettingLong(settings, Schema.FileSystem.MaxFileSize),
			AuditEnabled = SettingBool(settings, Schema.Audit.Enabled),
			Citus = citus,
			CitusNodeCount = citus switch {
				true  => (int)this.ScalarLong("SELECT count(*) FROM pg_dist_node"),
				false => -1,
			},
		};
	}

	// ------------------------------------------------------------------
	// Internals
	// ------------------------------------------------------------------

	private long ScalarLong(string sql) {
		try {
			return Pg.Query<long>(this.connectionString, sql).FirstOrDefault();
		} catch (System.Exception ex) {
			if (Logger.IsTraceEnabled) { Logger.Trace("status: aggregate query failed (", sql, "): ", ex.Message); }
			return -1;
		}
	}

	private Dictionary<string, string> LoadSettings() {
		var dict = new Dictionary<string, string>();
		try {
			foreach (var kv in this.store.LoadAll(Schema.AllFields)) {
				dict[kv.Key] = kv.Value;
			}
		} catch {
		}
		return dict;
	}

	/// <summary>Takes a JSONB column as a string. NULL is treated as an empty object.</summary>
	private static string JsonOrEmpty(object? column) {
		if (column == null) { return "{}"; }
		return (string)column;
	}

	private static string Setting(IReadOnlyDictionary<string, string> settings, Field field) {
		if (settings.TryGetValue(field.FullKey, out var v)) {
			return v;
		}
		return field.FormatDefaultRaw();
	}

	private static long SettingLong(IReadOnlyDictionary<string, string> settings, Field field) {
		if (long.TryParse(Setting(settings, field), out var n)) {
			return n;
		}
		return -1;
	}

	private static bool SettingBool(IReadOnlyDictionary<string, string> settings, Field field) {
		return Setting(settings, field) == "true";
	}

	private string QualifiedTable(string baseName) {
		return $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tablePrefix + baseName)}";
	}
}

/// <summary>One running mount's status row (Layer 1).</summary>
public sealed record MountInfo
{
	public required string MountId { get; init; }
	public required string Host { get; init; }
	public required long Pid { get; init; }
	public required string Mountpoint { get; init; }
	/// <summary><c>"fuse"</c> / <c>"dokan"</c>.</summary>
	public required string Mode { get; init; }
	public required long UptimeSeconds { get; init; }
	public required long HeartbeatAgeSeconds { get; init; }
	/// <summary>The heartbeat is younger than the threshold (= the process is probably still alive).</summary>
	public required bool Live { get; init; }
	/// <summary>The raw JSON of <c>{prefix}mounts.config</c> (the effective-settings snapshot, Layer 3). <c>"{}"</c> when not available.</summary>
	public required string ConfigJson { get; init; }
	/// <summary>The raw JSON of <c>{prefix}mounts.stats</c> (the cache-statistics snapshot, Layer 3). <c>"{}"</c> when not available.</summary>
	public required string StatsJson { get; init; }
	/// <summary>
	/// When this row is **a tombstone left behind by a mount that ended with data still unflushed**,
	/// the number of entries that were lost (B-2). 0 = an ordinary row.
	/// A row that ended cleanly is DELETEd, so anything above 0 is exactly "an unmount that could not
	/// write everything out in time".
	/// </summary>
	public int UnflushedLoss { get; init; }
	/// <summary>When the row became a tombstone (ISO 8601). Null when it is not a tombstone.</summary>
	public string? EndedAt { get; init; }
}

/// <summary>The result of <see cref="StatusAdmin.ListMounts"/>.</summary>
public sealed record MountsStatus
{
	/// <summary>Whether the <c>{prefix}mounts</c> table existed (false = an existing filesystem that has not been re-mkfs'd).</summary>
	public required bool TablePresent { get; init; }
	public required IReadOnlyList<MountInfo> Mounts { get; init; }
}

/// <summary>Filesystem statistics (Layer 2). A value of -1 means the aggregate failed (= unknown).</summary>
public sealed record FsStats
{
	public required string Schema { get; init; }
	public required string Prefix { get; init; }
	public required string Version { get; init; }
	public required string VolumeLabel { get; init; }
	public required long InodeCount { get; init; }
	public required long FileCount { get; init; }
	public required long ChunkCount { get; init; }
	public required long UsedBytes { get; init; }
	public required long ClusterSize { get; init; }
	public required long MaxFileSize { get; init; }
	public required bool AuditEnabled { get; init; }
	public required bool Citus { get; init; }
	/// <summary>The number of rows in Citus's pg_dist_node. -1 when this is not a Citus filesystem or the value could not be read.</summary>
	public required int CitusNodeCount { get; init; }
}
