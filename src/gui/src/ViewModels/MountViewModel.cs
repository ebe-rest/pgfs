namespace Pgfs.Gui.ViewModels;

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Pgfs.Core.Api;

/// <summary>
/// The wrapper used to display one row of the Mounts list plus the Process detail (Layer 3).
/// It formats the raw values of <see cref="MountInfo"/> and turns the stats/config JSON snapshots into readable text.
/// </summary>
public sealed class MountViewModel
{
	private readonly MountInfo info;

	public MountViewModel(MountInfo info) {
		this.info = info;
	}

	public string MountId => this.info.MountId;
	public string Host => this.info.Host;
	public long Pid => this.info.Pid;
	public string Mode => this.info.Mode;
	public string Mountpoint => this.info.Mountpoint;
	public string Uptime => FormatDuration(this.info.UptimeSeconds);
	public string HeartbeatAge => FormatDuration(this.info.HeartbeatAgeSeconds);
	public string Live => YesNo(this.info.Live);

	/// <summary>The Layer 3 text shown in the Process detail panel on selection (the cache statistics plus the effective config).</summary>
	public string Detail => this.BuildDetail();

	private string BuildDetail() {
		var sb = new StringBuilder();
		sb.Append(this.Host).Append("  pid ").Append(this.Pid).Append("  (").Append(this.Mode).Append(')');
		if (!this.info.Live) {
			sb.Append("  [stale]");
		}
		sb.Append('\n').Append(this.Mountpoint).Append('\n');
		sb.Append("uptime ").Append(this.Uptime).Append("   heartbeat ").Append(this.HeartbeatAge).Append(" ago\n\n");

		var stats = SafeParse(this.info.StatsJson) as JsonObject;
		if (stats == null) {
			sb.Append("(no stats snapshot — press Refresh to ping)\n");
			return sb.ToString();
		}
		AppendCache(sb, "inode cache  ", stats["inode"] as JsonObject, false);
		AppendNegative(sb, stats["inode"] as JsonObject);
		AppendCache(sb, "content cache", stats["content"] as JsonObject, true);
		var notify = stats["notify"] as JsonObject;
		if (notify != null) {
			sb.Append("notify        : listen=").Append(OnOff(NodeBool(notify["control_listen"])))
			  .Append(" data=").Append(OnOff(NodeBool(notify["data_enabled"])))
			  .Append(" connected=").Append(YesNo(NodeBool(notify["connected"]))).Append('\n');
		}

		var cfg = SafeParse(this.info.ConfigJson) as JsonObject;
		if (cfg != null && cfg.Count > 0) {
			sb.Append("\neffective config:\n");
			foreach (var kv in cfg) {
				sb.Append("  ").Append(kv.Key).Append(" = ").Append(ConfigVal(kv.Value)).Append('\n');
			}
		}
		return sb.ToString();
	}

	/// <summary>The negative lookup cache line. Not shown for an older snapshot, or when it is disabled (TTL 0) and has no activity.</summary>
	private static void AppendNegative(StringBuilder sb, JsonObject? inode) {
		if (inode == null) { return; }
		var negHits = inode["negativeHits"];
		if (negHits == null) { return; }
		var entries = NodeLong(inode["negativeEntries"]);
		var hits = NodeLong(negHits);
		if (entries == 0 && hits == 0) { return; }
		sb.Append("negative      : ").Append(entries).Append(" entries / ").Append(hits).Append(" hit\n");
	}

	private static void AppendCache(StringBuilder sb, string label, JsonObject? c, bool isContent) {
		if (c == null) {
			return;
		}
		var hits = NodeLong(c["hits"]);
		var misses = NodeLong(c["misses"]);
		sb.Append(label).Append(" : ");
		if (isContent) {
			sb.Append(NodeLong(c["entries"])).Append(" chunks / ").Append(FormatBytes(NodeLong(c["bytes"])))
			  .Append(" / max ").Append(FormatBytes(NodeLong(c["maxBytes"])));
		} else {
			sb.Append(NodeLong(c["entries"])).Append(" entries / cap ").Append(NodeLong(c["capacity"]));
		}
		sb.Append(" / hit ").Append(HitRatio(hits, misses))
		  .Append(" (").Append(hits).Append(" hit, ").Append(misses).Append(" miss, ")
		  .Append(NodeLong(c["evictions"])).Append(" evict)\n");
	}

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

	private static string ConfigVal(JsonNode? v) {
		if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) {
			return s;
		}
		return v?.ToJsonString() ?? "null";
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

	private static string HitRatio(long hits, long misses) {
		var total = hits + misses;
		if (total <= 0) {
			return "n/a";
		}
		return (100.0 * hits / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";
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
		return $"{v.ToString("0.0", CultureInfo.InvariantCulture)} {units[u]}";
	}
}
