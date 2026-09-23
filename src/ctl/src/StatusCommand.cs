namespace Pgfs.Ctl;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Api;

/// <summary>
/// The CLI front end of the <c>status</c> subcommand. Takes Layer 1 (the cluster activity listing) and
/// Layer 2 (the filesystem statistics) from <see cref="StatusAdmin"/> (Core) and prints them as text
/// (in sections) or with <c>--json</c>.
/// The design of record is docs/design/runtime-control-plane.md, the status section.
/// </summary>
public static class StatusCommand
{
	public static int Run(string[] opts) {
		var (json, _, conn, schema, prefix) = CliUtil.Resolve(opts);
		var admin = new StatusAdmin(conn, schema, prefix);
		var mounts = admin.ListMounts();
		var fs = admin.GetFsStats();
		if (json) {
			Console.WriteLine(JsonSerializer.Serialize(ToJson(mounts, fs), JsonOpts));
			return 0;
		}
		PrintText(mounts, fs);
		return 0;
	}

	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	/// <summary>
	/// Lists in red the mounts that exited leaving a loss behind (the gravestones, B-2). **They never go away
	/// on their own**, so the operator deletes them once they have been seen. With the default way of starting
	/// up neither the log nor the exit code reaches anyone, so this is the main place it gets noticed.
	/// </summary>
	private static void PrintLossRows(MountsStatus mounts) {
		var lost = new List<MountInfo>();
		foreach (var m in mounts.Mounts) {
			if (m.UnflushedLoss > 0) { lost.Add(m); }
		}
		if (lost.Count == 0) { return; }
		Console.WriteLine();
		Console.WriteLine(Red($"  !! write-back UNFLUSHED LOSS ({lost.Count} mount(s))"));
		foreach (var m in lost) {
			Console.WriteLine($"     {m.Host} {m.Mountpoint}: {m.UnflushedLoss} item(s) were lost (endedAt {Pgfs.Core.Api.Api.FormatUtcStamp(m.EndedAt)} / mount_id {m.MountId})");
		}
		Console.WriteLine("     delete them once they have been seen: DELETE FROM <schema>.<prefix>mounts WHERE (stats->>'unflushedLoss')::int > 0");
	}

	private static void PrintText(MountsStatus mounts, FsStats fs) {
		var liveCount = 0;
		foreach (var m in mounts.Mounts) {
			if (m.Live) {
				liveCount++;
			}
		}
		Console.WriteLine($"Mounts ({liveCount} live / {mounts.Mounts.Count} total):");
		if (!mounts.TablePresent) {
			Console.WriteLine("  ({prefix}mounts table not present — re-run mkfs to enable the registry)");
		} else if (mounts.Mounts.Count == 0) {
			Console.WriteLine("  (no mounts registered)");
		} else {
			Console.WriteLine($"  {"HOST",-16} {"PID",-7} {"MODE",-6} {"MOUNTPOINT",-20} {"UPTIME",-9} {"HB-AGE",-8} LIVE");
			foreach (var m in mounts.Mounts) {
				var live = YesNo(m.Live);
				// A gravestone (a row left by an exit with a loss) cannot be told apart by live/hb-age, so it is stated explicitly.
				if (m.UnflushedLoss > 0) { live = "ENDED"; }
				Console.WriteLine($"  {m.Host,-16} {m.Pid,-7} {m.Mode,-6} {m.Mountpoint,-20} {FormatDuration(m.UptimeSeconds),-9} {FormatDuration(m.HeartbeatAgeSeconds),-8} {live}");
			}
			PrintLossRows(mounts);
		}
		PrintLayer3(mounts);
		Console.WriteLine();
		Console.WriteLine("Filesystem:");
		Console.WriteLine($"  schema/prefix : {fs.Schema} / {fs.Prefix}");
		Console.WriteLine($"  version       : {fs.Version}  (label: {fs.VolumeLabel})");
		Console.WriteLine($"  inodes        : {Count(fs.InodeCount)}");
		Console.WriteLine($"  files         : {Count(fs.FileCount)}");
		Console.WriteLine($"  chunks        : {Count(fs.ChunkCount)}");
		Console.WriteLine($"  used          : {FormatBytes(fs.UsedBytes)} ({fs.UsedBytes} bytes)");
		Console.WriteLine($"  cluster_size  : {fs.ClusterSize}");
		Console.WriteLine($"  max_file_size : {FormatBytes(fs.MaxFileSize)} ({fs.MaxFileSize} bytes)");
		Console.WriteLine($"  audit         : {OnOff(fs.AuditEnabled)}");
		var citus = fs.Citus switch {
			true  => $"yes ({fs.CitusNodeCount} nodes)",
			false => "no",
		};
		Console.WriteLine($"  citus         : {citus}");
	}

	/// <summary>
	/// Layer 3 (the details of the running processes). Prints each mount's last heartbeat snapshot (the cache
	/// statistics plus the effective settings) as a section. With the table missing, or nothing registered, it prints nothing.
	/// </summary>
	private static void PrintLayer3(MountsStatus mounts) {
		if (!mounts.TablePresent || mounts.Mounts.Count == 0) {
			return;
		}
		Console.WriteLine();
		Console.WriteLine("Process detail (Layer 3 — last heartbeat snapshot):");
		foreach (var m in mounts.Mounts) {
			// The heartbeat has stopped = **every number below is as of that moment**.
			// On a failure that cannot write to the database even the error state does not reach here, so the staleness is made to stand out in red.
			var stale = "";
			if (!m.Live) { stale = Red($" [stale: heartbeat {FormatDuration(m.HeartbeatAgeSeconds)} ago]"); }
			Console.WriteLine($"  {m.Host} pid {m.Pid} ({m.Mode}){stale}:");
			PrintCacheStats(m.StatsJson, m.HeartbeatAgeSeconds);
			PrintEffectiveConfig(m.ConfigJson);
		}
	}

	private static void PrintCacheStats(string statsJson, long hbAgeSeconds) {
		var node = SafeParse(statsJson);
		if (node is not JsonObject obj) {
			Console.WriteLine("    (no stats snapshot)");
			return;
		}
		var inode = obj["inode"];
		if (inode != null) {
			var h = NodeLong(inode["hits"]);
			var mi = NodeLong(inode["misses"]);
			Console.WriteLine($"    inode cache  : {NodeLong(inode["entries"])} entries / cap {NodeLong(inode["capacity"])} / hit {HitRatio(h, mi)} ({h} hit, {mi} miss, {NodeLong(inode["evictions"])} evict)");
			PrintNegativeStats(inode);
		}
		var content = obj["content"];
		if (content != null) {
			var h = NodeLong(content["hits"]);
			var mi = NodeLong(content["misses"]);
			Console.WriteLine($"    content cache: {NodeLong(content["entries"])} chunks / {FormatBytes(NodeLong(content["bytes"]))} / max {FormatBytes(NodeLong(content["maxBytes"]))} / hit {HitRatio(h, mi)} ({h} hit, {mi} miss, {NodeLong(content["evictions"])} evict)");
		}
		PrintWriteBackStats(obj["writeBack"], hbAgeSeconds);
		PrintMetadataWriteBackStats(obj["writeBackMetadata"]);
		var handles = obj["handles"];
		if (handles != null) {
			// `open` / `peak` are the values of **FUSE's handle table** (Dokan does not use the table, so they are 0 on Windows).
			// `inodes` is **the number of open bodies** and **works on both operating systems** (docs/Pgfsctl.md).
			// **The two measure different things** - the point is to be able to tell whether it is only the handles
			// leaking or only the body count, so they are not folded into one.
			// **An older snapshot has no `inodes`** (a row written by a mount from before stage C-1).
			// `NodeLong` returns -1 for a missing value, so printing it as-is **looks like the value -1**.
			// When it is missing, the item is not printed at all.
			var inodes = "";
			if (handles["inodes"] != null) { inodes = $" / {NodeLong(handles["inodes"])} inodes"; }
			Console.WriteLine($"    handles      : {NodeLong(handles["open"])} open / peak {NodeLong(handles["peak"])}{inodes}");
		}
		var notify = obj["notify"];
		if (notify != null) {
			Console.WriteLine($"    notify       : listen={OnOff(NodeBool(notify["control_listen"]))} data={OnOff(NodeBool(notify["data_enabled"]))} connected={YesNo(NodeBool(notify["connected"]))}");
		}
	}

	/// <summary>The statistics of the negative lookup cache. Not printed for an older snapshot, or when it is disabled (TTL 0).</summary>
	private static void PrintNegativeStats(JsonNode inode) {
		var negHits = inode["negativeHits"];
		if (negHits == null) { return; }
		var entries = NodeLong(inode["negativeEntries"]);
		var hits = NodeLong(negHits);
		if (entries == 0 && hits == 0) { return; }
		Console.WriteLine($"    negative     : {entries} entries / {hits} hit");
	}

	/// <summary>The dirty / flush statistics of write-back. An older snapshot does not have them, so they are nullable.</summary>
	private static void PrintWriteBackStats(JsonNode? writeBack, long hbAgeSeconds) {
		if (writeBack == null) { return; }
		var enabled = NodeBool(writeBack["enabled"]);
		var dirtyBytes = NodeLong(writeBack["dirtyBytes"]);
		var maxBytes = NodeLong(writeBack["maxBytes"]);
		var flushes = NodeLong(writeBack["flushes"]);
		var failures = NodeLong(writeBack["flushFailures"]);
		// **The effective mode is printed alongside.** The first phase of the two-phase flip no longer accepts new
		// dirty data although the setting is still on, and while a drain continues the flush still runs although
		// the setting is off. Printing only `on` / `off` would be a lie in both cases.
		// An older snapshot has no such key, so it is treated as nullable.
		var mode = OnOff(enabled);
		if (writeBack["intakeClosed"] is JsonNode ic && NodeBool(ic)) { mode = OnOff(enabled) + " (intake closed = effectively off)"; }
		if (writeBack["drainPending"] is JsonNode dp && NodeBool(dp)) { mode = OnOff(enabled) + " (drain continuing = something is unflushed)"; }
		Console.WriteLine($"    write-back   : {mode} / dirty {FormatBytes(dirtyBytes)} of {FormatBytes(maxBytes)} ({NodeLong(writeBack["dirtyChunks"])} chunks, {NodeLong(writeBack["dirtyFiles"])} files) / {flushes} flush, {failures} failed");
		PrintWriteBackErrorState(writeBack, hbAgeSeconds);
	}

	/// <summary>
	/// Prints **in red** the error state entered after consecutive flush failures (blocking new writes / creates).
	/// Prints nothing when things are normal, or for an older snapshot.
	/// </summary>
	private static void PrintWriteBackErrorState(JsonNode writeBack, long hbAgeSeconds) {
		if (writeBack["errorState"] is not JsonValue v || !v.TryGetValue<string>(out var reason)) { return; }
		if (string.IsNullOrEmpty(reason)) { return; }
		var since = "";
		if (writeBack["errorSince"] is JsonValue sv && sv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) {
			since = $" (since {s})";
		}
		// **The freshness of this line is printed alongside.** The error state only arrives through the heartbeat,
		// so if the heartbeat is old this red is old too (and conversely, on a failure that cannot write to the
		// database the red never appears and everything looks green).
		Console.WriteLine(Red($"    !! write-back ERROR STATE{since} [information from a heartbeat {FormatDuration(hbAgeSeconds)} ago]: blocking new writes / creates - {reason}"));
	}

	/// <summary>Wraps it in ANSI red on a terminal (no escapes are mixed in when it is redirected).</summary>
	private static string Red(string text) {
		if (Console.IsOutputRedirected) { return text; }
		return "\u001b[0;31m" + text + "\u001b[0m";
	}

	/// <summary>
	/// The pending / conflict / discard statistics of metadata write-back. An older snapshot does not have
	/// them, so they are nullable. When it is disabled and nothing has happened, no line is printed (it is off
	/// by default, so it does not clutter the existing output).
	/// </summary>
	private static void PrintMetadataWriteBackStats(JsonNode? meta) {
		if (meta == null) { return; }
		var enabled = NodeBool(meta["enabled"]);
		var pending = NodeLong(meta["pendingInodes"]);
		var flushes = NodeLong(meta["flushes"]);
		if (!enabled && pending == 0 && flushes == 0) { return; }
		var failures = NodeLong(meta["flushFailures"]);
		// **The effective mode is printed alongside** (B-9). In the first phase of the two-phase flip no new
		// pending entries are accepted although the setting is still on, so printing only `on` would be a lie.
		// An older snapshot has no such key, so it is treated as nullable.
		var mode = OnOff(enabled);
		if (meta["intakeClosed"] is JsonNode ic && NodeBool(ic)) { mode = OnOff(enabled) + " (intake closed = effectively off)"; }
		Console.WriteLine($"    write-back(m): {mode} / pending {pending} of {NodeLong(meta["maxInodes"])} inodes / {flushes} flush, {failures} failed / {NodeLong(meta["conflicts"])} conflict, {NodeLong(meta["cancels"])} cancel, {NodeLong(meta["discards"])} discard");
	}

	private static void PrintEffectiveConfig(string configJson) {
		if (SafeParse(configJson) is not JsonObject obj || obj.Count == 0) {
			return;
		}
		Console.WriteLine("    config       :");
		foreach (var kv in obj) {
			Console.WriteLine($"      {kv.Key} = {ConfigVal(kv.Value)}");
		}
	}

	/// <summary>Turns a raw JSON string into a JsonNode. Empty or malformed gives null (= treated as "no snapshot yet").</summary>
	private static JsonNode? SafeParse(string? raw) {
		if (string.IsNullOrWhiteSpace(raw)) {
			return null;
		}
		try {
			return JsonNode.Parse(raw);
		} catch {
			return null;
		}
	}

	private static long NodeLong(JsonNode? n) {
		if (n is JsonValue v && v.TryGetValue<long>(out var l)) {
			return l;
		}
		return -1;
	}

	private static bool NodeBool(JsonNode? n) {
		return n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
	}

	/// <summary>A string value is printed without quotes; a number or a bool is printed in JSON notation as-is.</summary>
	private static string ConfigVal(JsonNode? v) {
		if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) {
			return s;
		}
		return v?.ToJsonString() ?? "null";
	}

	private static string HitRatio(long hits, long misses) {
		var total = hits + misses;
		if (total <= 0) {
			return "n/a";
		}
		return (100.0 * hits / total).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
	}

	private static string OnOff(bool b) {
		return b switch {
			true  => "on",
			false => "off",
		};
	}

	private static string YesNo(bool b) {
		return b switch {
			true  => "yes",
			false => "no",
		};
	}

	private static Dictionary<string, object?> ToJson(MountsStatus mounts, FsStats fs) {
		var rows = new List<Dictionary<string, object?>>();
		foreach (var m in mounts.Mounts) {
			rows.Add(new Dictionary<string, object?> {
				["mount_id"] = m.MountId,
				["host"] = m.Host,
				["pid"] = m.Pid,
				["mountpoint"] = m.Mountpoint,
				["mode"] = m.Mode,
				["uptime_seconds"] = m.UptimeSeconds,
				["heartbeat_age_seconds"] = m.HeartbeatAgeSeconds,
				["live"] = m.Live,
				["stats"] = SafeParse(m.StatsJson),
				["config"] = SafeParse(m.ConfigJson),
			});
		}
		return new Dictionary<string, object?> {
			["mounts"] = new Dictionary<string, object?> {
				["table_present"] = mounts.TablePresent,
				["rows"] = rows,
			},
			["fs"] = new Dictionary<string, object?> {
				["schema"] = fs.Schema,
				["prefix"] = fs.Prefix,
				["version"] = fs.Version,
				["volume_label"] = fs.VolumeLabel,
				["inode_count"] = fs.InodeCount,
				["file_count"] = fs.FileCount,
				["chunk_count"] = fs.ChunkCount,
				["used_bytes"] = fs.UsedBytes,
				["cluster_size"] = fs.ClusterSize,
				["max_file_size"] = fs.MaxFileSize,
				["audit_enabled"] = fs.AuditEnabled,
				["citus"] = fs.Citus,
				["citus_node_count"] = fs.CitusNodeCount,
			},
		};
	}

	/// <summary>A failed aggregation (-1) is printed as "?".</summary>
	private static string Count(long n) {
		if (n < 0) {
			return "?";
		}
		return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private static string FormatDuration(long seconds) {
		if (seconds < 0) {
			return "?";
		}
		if (seconds < 60) {
			return $"{seconds}s";
		}
		if (seconds < 3600) {
			return $"{seconds / 60}m{seconds % 60}s";
		}
		if (seconds < 86400) {
			return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
		}
		return $"{seconds / 86400}d{(seconds % 86400) / 3600}h";
	}

	private static string FormatBytes(long bytes) {
		if (bytes < 0) {
			return "?";
		}
		string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
		double v = bytes;
		var u = 0;
		while (v >= 1024 && u < units.Length - 1) {
			v /= 1024;
			u++;
		}
		if (u == 0) {
			return $"{bytes} B";
		}
		return $"{v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} {units[u]}";
	}
}
