namespace Pgfs.Lib.Config;

using Npgsql;
using Pgfs.Lib.Logging;

/// <summary>
/// A static descriptor of a setting (non-generic base). It aggregates the CLI flag / TOML key / DB key / persistence
/// target into a single immutable object, lined up as static fields under <see cref="Schema"/>
/// (= a codification of [docs/settings-matrix.md](../../../../docs/settings-matrix.md)).
///
/// <para>
/// <see cref="ConfigLoader"/> needs to treat multiple <c>Field</c>s as a list (matching CLI flags, matching
/// `-o` keys, bulk-SELECTing SaveTo=Db fields, etc.), so a base without a type parameter is provided.
/// The value type's Parse / Format live on the <see cref="Field{T}"/> derived side.
/// </para>
/// </summary>
public abstract record Field
{
	/// <summary>The setting's scope. One of `mount` / `database` / `logging` / `file_system` / `setting`.</summary>
	public required string Scope { get; init; }

	/// <summary>The key name within the scope. e.g. `mount_point` / `fallback_uname`. snake_case.</summary>
	public required string Key { get; init; }

	/// <summary>The CLI flags accepted. List both the short and long forms (e.g. `["-m", "--mount-point"]`).</summary>
	public required string[] CliOptions { get; init; }

	/// <summary>
	/// The key name used when received via fstab's `-o key=val,flag,...`. If null, <see cref="EffectiveDashOName"/>
	/// automatically uses <see cref="Key"/> with `_` replaced by `-`. Matches the fstab convention.
	/// </summary>
	public string? DashOName { get; init; }

	/// <summary>The persistence target. <see cref="SaveTarget.None"/> means it is never saved.</summary>
	public required SaveTarget SaveTo { get; init; }

	/// <summary>Description text (for auto-generated help / TOML comments).</summary>
	public string Comment { get; init; } = "";

	/// <summary>The `scope.key` concatenation. Used in error messages / log identification / the `pgfs_settings.(scope,key)` key.</summary>
	public string FullKey {
		get { return $"{this.Scope}.{this.Key}"; }
	}

	/// <summary>The key the `-o` parser matches against. <see cref="DashOName"/> if specified, otherwise <see cref="Key"/> with `_` replaced by `-`.</summary>
	public string EffectiveDashOName {
		get {
			if (this.DashOName != null) {
				return this.DashOName;
			}
			return this.Key.Replace('_', '-');
		}
	}

	/// <summary>Whether it is a bool type (= a flag that takes no argument). Used by the CLI parser to decide whether to consume a value.</summary>
	public abstract bool IsBool { get; }

	/// <summary>Converts a string (the raw representation coming from CLI / TOML / DB) into the internal value and returns the raw representation; also serves as a validation hook. Throws on failure.</summary>
	internal abstract string NormalizeRaw(string raw);
}

/// <summary>
/// A typed descriptor of a setting. Serialization / deserialization per value type lives on the derived types
/// (<see cref="StringField"/> / <see cref="IntField"/> / ...).
/// </summary>
public abstract record Field<T> : Field
{
	/// <summary>
	/// A factory that returns the default value. OS-dependent (`OperatingSystem.IsWindows() ? ... : ...`) or
	/// runtime-environment-dependent values can be computed here too.
	/// </summary>
	public required System.Func<T> DefaultFn { get; init; }

	/// <summary>Converts a string (the raw representation from CLI / TOML / DB) into T. Throws on failure.</summary>
	public abstract T Parse(string raw);

	/// <summary>Converts T into a string. Used for persistence (TOML / DB).</summary>
	public abstract string Format(T value);

	/// <summary>Base validation-hook implementation: Parse once, then re-Format. On failure Parse throws.</summary>
	internal override string NormalizeRaw(string raw) {
		var parsed = this.Parse(raw);
		return this.Format(parsed);
	}

	/// <summary>
	/// Converts T into a JSON literal representation. Used when <see cref="ConfigStore.Save"/> writes to
	/// <c>pgfs_settings.value</c> (JSONB).
	/// The default implementation JSON-encodes the result of <see cref="Format"/> (= with double quotes).
	/// The numeric / bool derived types override this method so the value becomes a native JSON representation.
	/// </summary>
	internal virtual string FormatJson(T value) {
		return System.Text.Json.JsonSerializer.Serialize(this.Format(value));
	}
}

/// <summary>Handles a string as-is. No encoding or other processing.</summary>
public sealed record StringField : Field<string>
{
	public override bool IsBool { get { return false; } }
	public override string Parse(string raw) { return raw; }
	public override string Format(string value) { return value; }
}

/// <summary>A decimal integer (32-bit). Parsed / formatted culture-independently (InvariantCulture).</summary>
public sealed record IntField : Field<int>
{
	public override bool IsBool { get { return false; } }
	public override int Parse(string raw) {
		return int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
	}
	public override string Format(int value) {
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}
	internal override string FormatJson(int value) {
		return this.Format(value);
	}
}

/// <summary>A decimal integer (64-bit). Parsed / formatted culture-independently (InvariantCulture).</summary>
public sealed record LongField : Field<long>
{
	public override bool IsBool { get { return false; } }
	public override long Parse(string raw) {
		return long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
	}
	public override string Format(long value) {
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}
	internal override string FormatJson(long value) {
		return this.Format(value);
	}
}

/// <summary>
/// A list of strings. The persisted representation is comma-separated.
/// e.g. pass `setting.search_path` on the CLI as `--setting-path /etc/pgfs,/home/me/.config/pgfs`,
/// and write it in TOML as `search_path = ["/etc/pgfs", "/home/me/.config/pgfs"]` (the Loader normalizes a
/// TomlArray to comma-separated).
/// </summary>
public sealed record StringListField : Field<System.Collections.Generic.List<string>>
{
	public override bool IsBool { get { return false; } }
	public override System.Collections.Generic.List<string> Parse(string raw) {
		if (string.IsNullOrEmpty(raw)) {
			return new System.Collections.Generic.List<string>();
		}
		var parts = raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
		return new System.Collections.Generic.List<string>(parts);
	}
	public override string Format(System.Collections.Generic.List<string> value) {
		return string.Join(",", value);
	}
}

/// <summary>
/// For a PostgreSQL connection string (<see cref="NpgsqlConnectionStringBuilder"/>). Both the kv form (`Host=...;Port=...`)
/// and the URL form (`postgresql://user@host/db`) are passed to the <see cref="NpgsqlConnectionStringBuilder"/> ctor
/// (Npgsql 9.x interprets both). Format adopts the kv canonical form returned by
/// <see cref="NpgsqlConnectionStringBuilder.ConnectionString"/>, so a value entered as a URL round-trips asymmetrically
/// and is written out as kv (when persisting to TOML / DB it is unified to the kv form).
/// </summary>
public sealed record ConnectionField : Field<NpgsqlConnectionStringBuilder>
{
	public override bool IsBool { get { return false; } }
	public override NpgsqlConnectionStringBuilder Parse(string raw) {
		return new NpgsqlConnectionStringBuilder(raw);
	}
	public override string Format(NpgsqlConnectionStringBuilder value) {
		return value.ConnectionString;
	}
}

/// <summary>
/// For <see cref="Pgfs.Lib.Logging.Level.Enum"/>. <see cref="Pgfs.Lib.Logging.Level.Parse(string)"/> already does
/// case-insensitive + first-character matching, so we delegate to it.
/// </summary>
public sealed record LogLevelField : Field<Pgfs.Lib.Logging.Level.Enum>
{
	public override bool IsBool { get { return false; } }
	public override Pgfs.Lib.Logging.Level.Enum Parse(string raw) {
		return Pgfs.Lib.Logging.Level.Parse(raw);
	}
	public override string Format(Pgfs.Lib.Logging.Level.Enum value) {
		return value.String();
	}
}

/// <summary>
/// For <see cref="Pgfs.Lib.Models.SettingLoggingOutput"/>. String representations:
/// <list type="bullet">
///   <item><c>"stdout"</c> / <c>"stderr"</c> / <c>"none"</c> → a single <see cref="Pgfs.Lib.Models.SettingLoggingKind.Enum"/></item>
///   <item><c>"&lt;cycle&gt;:&lt;directory&gt;/&lt;pattern&gt;"</c> (e.g. <c>"daily:/var/log/pgfs/pgfs-*.log"</c>) → the File form</item>
///   <item>falls back to <see cref="Pgfs.Lib.Models.SettingLoggingKind.Enum.Stderr"/> if it cannot be interpreted</item>
/// </list>
/// Format converts back to the above forms. Composing multiple Kinds (Stdout | File, etc.) is out of range for the
/// string representation; currently Format checks File first → Stdout → Stderr → None and writes out only one (= round-trip loss).
/// </summary>
public sealed record LoggingOutputField : Field<Pgfs.Lib.Models.SettingLoggingOutput>
{
	public override bool IsBool { get { return false; } }
	public override Pgfs.Lib.Models.SettingLoggingOutput Parse(string raw) {
		var v = raw.Trim();
		if (v.Length == 0 || v.Equals("stderr", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Lib.Models.SettingLoggingOutput { Kind = Pgfs.Lib.Models.SettingLoggingKind.Enum.Stderr };
		}
		if (v.Equals("stdout", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Lib.Models.SettingLoggingOutput { Kind = Pgfs.Lib.Models.SettingLoggingKind.Enum.Stdout };
		}
		if (v.Equals("none", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Lib.Models.SettingLoggingOutput { Kind = Pgfs.Lib.Models.SettingLoggingKind.Enum.None };
		}
		var colon = v.IndexOf(':');
		if (colon > 0) {
			var cyclePart = v[..colon].Trim();
			var rest = v[(colon + 1)..].Trim();
			var slash = rest.LastIndexOf('/');
			if (slash > 0) {
				var cycle = cyclePart.ToLowerInvariant() switch {
					"none" => Pgfs.Lib.Models.SettingLoggingCycle.Enum.None,
					"hourly" => Pgfs.Lib.Models.SettingLoggingCycle.Enum.Hourly,
					"daily" => Pgfs.Lib.Models.SettingLoggingCycle.Enum.Daily,
					"monthly" => Pgfs.Lib.Models.SettingLoggingCycle.Enum.Monthly,
					_ => Pgfs.Lib.Models.SettingLoggingCycle.Enum.Daily,
				};
				return new Pgfs.Lib.Models.SettingLoggingOutput {
					Kind = Pgfs.Lib.Models.SettingLoggingKind.Enum.File,
					Cycle = cycle,
					Directory = rest[..slash],
					FileNamePattern = rest[(slash + 1)..],
				};
			}
		}
		return new Pgfs.Lib.Models.SettingLoggingOutput { Kind = Pgfs.Lib.Models.SettingLoggingKind.Enum.Stderr };
	}
	public override string Format(Pgfs.Lib.Models.SettingLoggingOutput value) {
		if ((value.Kind & Pgfs.Lib.Models.SettingLoggingKind.Enum.File) != 0) {
			var cycle = value.Cycle switch {
				Pgfs.Lib.Models.SettingLoggingCycle.Enum.None => "none",
				Pgfs.Lib.Models.SettingLoggingCycle.Enum.Hourly => "hourly",
				Pgfs.Lib.Models.SettingLoggingCycle.Enum.Daily => "daily",
				Pgfs.Lib.Models.SettingLoggingCycle.Enum.Monthly => "monthly",
				_ => "daily",
			};
			return $"{cycle}:{value.Directory}/{value.FileNamePattern}";
		}
		if ((value.Kind & Pgfs.Lib.Models.SettingLoggingKind.Enum.Stdout) != 0) {
			return "stdout";
		}
		if ((value.Kind & Pgfs.Lib.Models.SettingLoggingKind.Enum.Stderr) != 0) {
			return "stderr";
		}
		return "none";
	}
}

/// <summary>
/// bool. Accepts `true` / `false` / `1` / `0` / `yes` / `no` / `on` / `off` (case-insensitive).
/// Treating an empty value (= the flag was passed on its own) as true on the CLI flag-receiving side is
/// the responsibility of <see cref="ConfigLoader"/>.
/// </summary>
public sealed record BoolField : Field<bool>
{
	public override bool IsBool { get { return true; } }
	public override bool Parse(string raw) {
		var t = raw.Trim().ToLowerInvariant();
		switch (t) {
			case "true":
			case "1":
			case "yes":
			case "on":
				return true;
			case "false":
			case "0":
			case "no":
			case "off":
			case "":
				return false;
		}
		throw new System.FormatException($"cannot be interpreted as bool: '{raw}'");
	}
	public override string Format(bool value) {
		if (value) {
			return "true";
		}
		return "false";
	}
	internal override string FormatJson(bool value) {
		return this.Format(value);
	}
}
