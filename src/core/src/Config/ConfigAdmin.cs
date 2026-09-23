namespace Pgfs.Core.Config;

using System.Collections.Generic;
using System.Linq;
using Pgfs.Core.Api;
using Pgfs.Core.Utility;

/// <summary>
/// The body of <c>config get</c> / <c>config list</c> / <c>config set</c> (lives in Core so it can be
/// reused). Shared by pgfsctl (the CLI) and the GUI. Field validation
/// (<see cref="Field.NormalizeRaw"/>) and JSONB formatting are internal to Core, so the logic lives
/// here and the CLI stays a thin front end
/// (the design is in docs/runtime-control-plane.md).
///
/// <para>
/// **The set matrix** (dispatched on the Field's <c>(SaveTo, Reload)</c>):
/// <list type="bullet">
///   <item>Db + Live -> write to pgfs_settings plus a <c>set</c> NOTIFY (persistent and live)</item>
///   <item>File + Live -> only a <c>set</c> NOTIFY (ephemeral; no database row is created)</item>
///   <item>Db + NextMount -> write to pgfs_settings (takes effect on the next mount)</item>
///   <item>File + NextMount -> rejected (a remote toml cannot be touched; the user is told to edit their own toml)</item>
///   <item>Format / None -> rejected</item>
/// </list>
/// The live <c>set</c> NOTIFY is received by the <see cref="Api"/> of every running mount, which applies
/// it immediately through <c>ApplyLiveSet</c>. There is no ack, so the reported count is the number of
/// rows registered in <c>{prefix}mounts</c>, offered as a "most recent" figure rather than a delivery count.
/// </para>
/// </summary>
public sealed class ConfigAdmin
{
	private readonly string connectionString;
	private readonly string schemaName;
	private readonly string tablePrefix;
	private readonly ConfigStore store;

	public ConfigAdmin(string connectionString, string schemaName, string tablePrefix) {
		this.connectionString = connectionString;
		this.schemaName = schemaName;
		this.tablePrefix = tablePrefix;
		this.store = new ConfigStore(connectionString, schemaName, tablePrefix);
	}

	/// <summary>
	/// Returns every Field ordered by <c>scope.key</c>, with its effective value, its origin, where it is
	/// persisted and its reload policy.
	/// <paramref name="loader"/> is expected to be a Loader built **without** a <see cref="ConfigStore"/>
	/// (CLI and TOML only). Origin precedence is CLI/TOML &gt; DB &gt; default.
	/// </summary>
	public IReadOnlyList<ConfigItem> List(ConfigLoader? loader) {
		var dbRaw = this.LoadDbRaw();
		var items = new List<ConfigItem>();
		foreach (var field in Schema.AllFields.OrderBy(f => f.FullKey)) {
			items.Add(BuildItem(field, loader, dbRaw));
		}
		return items;
	}

	/// <summary>Returns the effective value and metadata of a single <c>scope.key</c>. Null for an unknown key.</summary>
	public ConfigItem? Get(string fullKey, ConfigLoader? loader) {
		var field = FindField(fullKey);
		if (field == null) {
			return null;
		}
		return BuildItem(field, loader, this.LoadDbRaw());
	}

	/// <summary>
	/// Sets the value of <c>scope.key</c>: validate, then persist, fire a NOTIFY or reject according to the
	/// (SaveTo, Reload) matrix.
	/// </summary>
	public SetResult Set(string fullKey, string rawValue) {
		var field = FindField(fullKey);
		if (field == null) {
			return Fail($"unknown key '{fullKey}'");
		}
		string normalized;
		try {
			normalized = field.NormalizeRaw(rawValue);
		} catch (System.Exception ex) {
			return Fail($"invalid value for '{fullKey}': {ex.Message}");
		}
		if (field.Reload == ReloadPolicy.Format) {
			return Fail($"'{fullKey}' is format-time only (immutable after mkfs); cannot be changed at runtime");
		}
		if (field.SaveTo == SaveTarget.None) {
			return Fail($"'{fullKey}' is not a persistable/settable runtime field");
		}
		if (field.SaveTo == SaveTarget.File && field.Reload == ReloadPolicy.NextMount) {
			return Fail($"'{fullKey}' is file-backed and not live-reloadable; edit pgfs.toml on each client (applies on next mount). pgfsctl cannot reach a remote pgfs.toml.");
		}
		var mounts = this.RegisteredMountCount();
		var persisted = false;
		if (field.SaveTo == SaveTarget.Db) {
			this.store.SaveRaw(field, normalized);
			persisted = true;
		}
		var liveFired = false;
		if (field.Reload == ReloadPolicy.Live) {
			this.FireSet(fullKey, normalized);
			liveFired = true;
		}
		return new SetResult {
			Ok = true,
			Persisted = persisted,
			LiveFired = liveFired,
			Message = BuildSetMessage(mounts, persisted, liveFired),
		};
	}

	/// <summary>How many rows are currently registered in <c>{prefix}mounts</c>. -1 when the table is absent (= unknown). A "most recent" figure that includes stale rows.</summary>
	public int RegisteredMountCount() {
		try {
			var n = Pg.Query<long>(this.connectionString, $"SELECT count(*) FROM {this.QualifiedMounts()}").First();
			return (int)n;
		} catch {
			return -1;
		}
	}

	// ------------------------------------------------------------------
	// Internals
	// ------------------------------------------------------------------

	private void FireSet(string fullKey, string normalized) {
		using var ch = new NotifyChannel(this.connectionString, this.schemaName, this.tablePrefix);
		ch.Publish(new NotifyMessage { Control = "set", Key = fullKey, Value = normalized });
	}

	private static ConfigItem BuildItem(Field field, ConfigLoader? loader, IReadOnlyDictionary<string, string> dbRaw) {
		var fromLoader = loader?.GetMergedRaw(field);
		if (fromLoader != null) {
			return MakeItem(field, fromLoader, "config");
		}
		if (dbRaw.TryGetValue(field.FullKey, out var dbVal)) {
			return MakeItem(field, dbVal, "db");
		}
		return MakeItem(field, field.FormatDefaultRaw(), "default");
	}

	private static ConfigItem MakeItem(Field field, string raw, string source) {
		return new ConfigItem {
			FullKey = field.FullKey,
			Value = Mask(field.FullKey, raw),
			Source = source,
			SaveTo = field.SaveTo,
			Reload = field.Reload,
			Comment = field.Comment,
		};
	}

	private Dictionary<string, string> LoadDbRaw() {
		var dict = new Dictionary<string, string>();
		try {
			foreach (var kv in this.store.LoadAll(Schema.AllFields)) {
				dict[kv.Key] = kv.Value;
			}
		} catch {
		}
		return dict;
	}

	private string QualifiedMounts() {
		return $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tablePrefix + "mounts")}";
	}

	private static Field? FindField(string fullKey) {
		foreach (var f in Schema.AllFields) {
			if (f.FullKey == fullKey) {
				return f;
			}
		}
		return null;
	}

	private static SetResult Fail(string message) {
		return new SetResult { Ok = false, Message = message };
	}

	private static string BuildSetMessage(int mounts, bool persisted, bool liveFired) {
		var who = (mounts < 0) switch {
			true  => "an unknown number of",
			false => mounts.ToString(),
		};
		if (persisted && liveFired) {
			return $"persisted to pgfs_settings; set fired to {who} running mount(s) for live apply";
		}
		if (persisted) {
			return $"persisted to pgfs_settings; applies on next mount ({who} running)";
		}
		return $"set fired to {who} running mount(s) for live apply (ephemeral; edit pgfs.toml on each client to persist across remounts)";
	}

	private static string Mask(string fullKey, string value) {
		// Hides both the kv form and the URL form (it used to cover only the kv form, so a URL's password came out in plain text).
		return ConnectionStringMask.MaskIfConnection(fullKey, value);
	}
}

/// <summary>A snapshot of one setting's effective value (the result of <see cref="ConfigAdmin.List"/> / <see cref="ConfigAdmin.Get"/>).</summary>
public sealed record ConfigItem
{
	/// <summary><c>scope.key</c>.</summary>
	public required string FullKey { get; init; }
	/// <summary>The raw representation of the effective value (a connection string has its Password masked).</summary>
	public required string Value { get; init; }
	/// <summary>The origin: <c>"config"</c> (CLI/TOML), <c>"db"</c> (pgfs_settings) or <c>"default"</c>.</summary>
	public required string Source { get; init; }
	public required SaveTarget SaveTo { get; init; }
	public required ReloadPolicy Reload { get; init; }
	public required string Comment { get; init; }
}

/// <summary>The result of <see cref="ConfigAdmin.Set"/>.</summary>
public sealed record SetResult
{
	/// <summary>Whether the set took effect (false = rejected, failed validation, or the File+NextMount case where nothing can be done).</summary>
	public required bool Ok { get; init; }
	/// <summary>A message for a human (what succeeded, or why it was rejected).</summary>
	public required string Message { get; init; }
	/// <summary>Whether the value was persisted to pgfs_settings.</summary>
	public bool Persisted { get; init; }
	/// <summary>Whether the live <c>set</c> NOTIFY was fired.</summary>
	public bool LiveFired { get; init; }
}
