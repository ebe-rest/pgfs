namespace Pgfs.Core.Config;

using Npgsql;
using Pgfs.Core.Logging;

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

	/// <summary>The key name within the scope. e.g. `mount_point` / `volume_label`. snake_case.</summary>
	public required string Key { get; init; }

	/// <summary>The CLI flags accepted. List both the short and long forms (e.g. `["-m", "--mount-point"]`).</summary>
	public required string[] CliOptions { get; init; }

	/// <summary>
	/// bool-only: negated CLI aliases that set this value to <c>false</c> (e.g. <c>--deny-plperlu</c>). Bare-only (takes no value).
	/// Empty by default. <see cref="CliOptions"/> is the positive (true) side; this is the negative (false) side.
	/// </summary>
	public string[] NegatedCliOptions { get; init; } = [];

	/// <summary>
	/// bool-only: whether the positive CLI alias may optionally take a following bool literal (<c>true</c>/<c>false</c>/...) as its value.
	/// Default <c>false</c> (= existing flags stay bare=true and do not consume the next token).
	/// When <c>true</c>, <c>--flag true</c> / <c>--flag false</c> are accepted (if the next token is not a bool literal, it is bare=true).
	/// </summary>
	public bool AcceptsInlineBool { get; init; } = false;

	/// <summary>
	/// The key name used when received via fstab's `-o key=val,flag,...`. If null, <see cref="EffectiveDashOName"/>
	/// automatically uses <see cref="Key"/> with `_` replaced by `-`. Matches the fstab convention.
	/// </summary>
	public string? DashOName { get; init; }

	/// <summary>The persistence target. <see cref="SaveTarget.None"/> means it is never saved.</summary>
	public required SaveTarget SaveTo { get; init; }

	/// <summary>How this item is treated by a configuration reload while running (Live / NextMount / Format). The default is the conservative NextMount.</summary>
	public ReloadPolicy Reload { get; init; } = ReloadPolicy.NextMount;

	/// <summary>The description (for generating help and for TOML comments).</summary>
	public string Comment { get; init; } = "";

	/// <summary>
	/// The set of tools whose <c>--help</c> this setting appears in. Default is <see cref="Tool.All"/> (shown in every tool).
	/// A filter for <see cref="HelpText"/> to split mkfs-only (<c>--clean</c> / <c>--citus</c> etc.) vs mount/assign-only
	/// (<c>--mount-point</c> etc.). It does not affect CLI parsing (see <see cref="Tool"/>).
	/// </summary>
	public Tool AppliesTo { get; init; } = Tool.All;

	/// <summary>
	/// The value placeholder in <c>--help</c> (e.g. <c>"&lt;path&gt;"</c> / <c>"&lt;bytes&gt;"</c>). If null,
	/// <see cref="HelpText"/> derives one from the type (Int/Long → <c>&lt;n&gt;</c>, Connection → <c>&lt;connstr&gt;</c>, etc.).
	/// </summary>
	public string? ArgName { get; init; }

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

	/// <summary>
	/// The display string of the default value shown in <c>--help</c>. Null means not shown (a connection string to keep
	/// secret, or a default not worth displaying). Stringified via the value type's <see cref="Field{T}.Format"/>.
	/// </summary>
	internal abstract string? HelpDefaultRaw();

	/// <summary>Returns the default value in its raw representation (unmasked). Used for the <c>config list</c> / <c>config get</c> display.</summary>
	public abstract string FormatDefaultRaw();

	/// <summary>Parses a raw representation and turns it into a JSON literal. Used when <see cref="ConfigStore.SaveRaw"/> writes to pgfs_settings.</summary>
	internal abstract string FormatJsonFromRaw(string raw);
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

	/// <summary>Default implementation: stringify <see cref="DefaultFn"/> via <see cref="Format"/>. Null when empty (not shown).</summary>
	internal override string? HelpDefaultRaw() {
		var s = this.Format(this.DefaultFn());
		if (string.IsNullOrEmpty(s)) {
			return null;
		}
		return s;
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

	/// <summary>The raw representation (unmasked) of <see cref="DefaultFn"/> put through <see cref="Format"/>.</summary>
	public override string FormatDefaultRaw() {
		return this.Format(this.DefaultFn());
	}

	/// <summary>Puts a raw representation through <see cref="Parse"/> then <see cref="FormatJson"/> to make a JSONB literal.</summary>
	internal override string FormatJsonFromRaw(string raw) {
		return this.FormatJson(this.Parse(raw));
	}
}

/// <summary>Handles a string as-is. No encoding or other processing.</summary>
public sealed record StringField : Field<string>
{
	public override bool IsBool { get { return false; } }
	public override string Parse(string raw) { return raw; }
	public override string Format(string value) { return value; }
}

/// <summary>
/// A string with the permitted values enumerated. **<see cref="Parse"/> turns anything outside them into an
/// exception**, so the validation happens in one place no matter which entrance it came through - CLI,
/// setting file or DB (checking it on the applying side would make it possible to create a setting that
/// "goes into the DB but falls over at mount time"). The comparison is case-insensitive.
/// </summary>
public sealed record EnumField : Field<string>
{
	/// <summary>The permitted values. Write them in lower case (the parse ignores case).</summary>
	public required string[] Allowed { get; init; }

	public override bool IsBool { get { return false; } }

	public override string Parse(string raw) {
		foreach (var candidate in this.Allowed) {
			if (string.Equals(raw, candidate, System.StringComparison.OrdinalIgnoreCase)) { return candidate; }
		}
		throw new System.FormatException($"{this.Scope}.{this.Key}: '{raw}' cannot be used (usable values: {string.Join(" / ", this.Allowed)})");
	}

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
/// and the URL form (`postgresql://user@host/db`) are accepted. **The URL form is converted to kv by
/// <see cref="PostgresUri"/>** - Npgsql does not interpret URLs, so handing it straight to the ctor as before failed with
/// `Format of the initialization string does not conform to specification` and **a mount written with a URL did not start**.
/// Format adopts the kv canonical form returned by <see cref="NpgsqlConnectionStringBuilder.ConnectionString"/>, so a value
/// entered as a URL round-trips asymmetrically and is written out as kv (when persisting to TOML / DB it is unified to the
/// kv form).
/// </summary>
public sealed record ConnectionField : Field<NpgsqlConnectionStringBuilder>
{
	public override bool IsBool { get { return false; } }
	public override NpgsqlConnectionStringBuilder Parse(string raw) {
		if (PostgresUri.IsUri(raw)) { return PostgresUri.Parse(raw); }
		return new NpgsqlConnectionStringBuilder(raw);
	}
	public override string Format(NpgsqlConnectionStringBuilder value) {
		return value.ConnectionString;
	}
	/// <summary>The connection string default contains a password, so it is not shown in --help.</summary>
	internal override string? HelpDefaultRaw() {
		return null;
	}
}

/// <summary>
/// For <see cref="Pgfs.Core.Logging.Level.Enum"/>. <see cref="Pgfs.Core.Logging.Level.Parse(string)"/>
/// already handles case-insensitivity and matching on the first character alone, so this delegates to it.
/// </summary>
public sealed record LogLevelField : Field<Pgfs.Core.Logging.Level.Enum>
{
	public override bool IsBool { get { return false; } }
	public override Pgfs.Core.Logging.Level.Enum Parse(string raw) {
		return Pgfs.Core.Logging.Level.Parse(raw);
	}
	public override string Format(Pgfs.Core.Logging.Level.Enum value) {
		return value.String();
	}
}

/// <summary>
/// For <see cref="Pgfs.Core.Models.SettingLoggingOutput"/>. The string representations:
/// <list type="bullet">
///   <item><c>"stdout"</c> / <c>"stderr"</c> / <c>"none"</c> -> a single <see cref="Pgfs.Core.Models.SettingLoggingKind.Enum"/></item>
///   <item><c>"&lt;cycle&gt;:&lt;directory&gt;/&lt;pattern&gt;"</c> (for example <c>"daily:/var/log/pgfs/pgfs-*.log"</c>) -> the File form</item>
///   <item>falls back to <see cref="Pgfs.Core.Models.SettingLoggingKind.Enum.Stderr"/> when it cannot be interpreted</item>
/// </list>
/// Format converts back to the above forms. Composing multiple Kinds (Stdout | File, etc.) is out of range for the
/// string representation; currently Format checks File first → Stdout → Stderr → None and writes out only one (= round-trip loss).
/// </summary>
public sealed record LoggingOutputField : Field<Pgfs.Core.Models.SettingLoggingOutput>
{
	public override bool IsBool { get { return false; } }
	public override Pgfs.Core.Models.SettingLoggingOutput Parse(string raw) {
		var v = raw.Trim();
		if (v.Length == 0 || v.Equals("stderr", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Core.Models.SettingLoggingOutput { Kind = Pgfs.Core.Models.SettingLoggingKind.Enum.Stderr };
		}
		if (v.Equals("stdout", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Core.Models.SettingLoggingOutput { Kind = Pgfs.Core.Models.SettingLoggingKind.Enum.Stdout };
		}
		if (v.Equals("none", System.StringComparison.OrdinalIgnoreCase)) {
			return new Pgfs.Core.Models.SettingLoggingOutput { Kind = Pgfs.Core.Models.SettingLoggingKind.Enum.None };
		}
		var colon = v.IndexOf(':');
		if (colon > 0) {
			var cyclePart = v[..colon].Trim();
			var rest = v[(colon + 1)..].Trim();
			var slash = rest.LastIndexOf('/');
			if (slash > 0) {
				var cycle = cyclePart.ToLowerInvariant() switch {
					"none" => Pgfs.Core.Models.SettingLoggingCycle.Enum.None,
					"hourly" => Pgfs.Core.Models.SettingLoggingCycle.Enum.Hourly,
					"daily" => Pgfs.Core.Models.SettingLoggingCycle.Enum.Daily,
					"monthly" => Pgfs.Core.Models.SettingLoggingCycle.Enum.Monthly,
					_ => Pgfs.Core.Models.SettingLoggingCycle.Enum.Daily,
				};
				return new Pgfs.Core.Models.SettingLoggingOutput {
					Kind = Pgfs.Core.Models.SettingLoggingKind.Enum.File,
					Cycle = cycle,
					Directory = rest[..slash],
					FileNamePattern = rest[(slash + 1)..],
				};
			}
		}
		return new Pgfs.Core.Models.SettingLoggingOutput { Kind = Pgfs.Core.Models.SettingLoggingKind.Enum.Stderr };
	}
	public override string Format(Pgfs.Core.Models.SettingLoggingOutput value) {
		if ((value.Kind & Pgfs.Core.Models.SettingLoggingKind.Enum.File) != 0) {
			var cycle = value.Cycle switch {
				Pgfs.Core.Models.SettingLoggingCycle.Enum.None => "none",
				Pgfs.Core.Models.SettingLoggingCycle.Enum.Hourly => "hourly",
				Pgfs.Core.Models.SettingLoggingCycle.Enum.Daily => "daily",
				Pgfs.Core.Models.SettingLoggingCycle.Enum.Monthly => "monthly",
				_ => "daily",
			};
			return $"{cycle}:{value.Directory}/{value.FileNamePattern}";
		}
		if ((value.Kind & Pgfs.Core.Models.SettingLoggingKind.Enum.Stdout) != 0) {
			return "stdout";
		}
		if ((value.Kind & Pgfs.Core.Models.SettingLoggingKind.Enum.Stderr) != 0) {
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
	/// <summary>A flag's default (false unless set) is self-evident, so it is not shown in --help.</summary>
	internal override string? HelpDefaultRaw() {
		return null;
	}
}
