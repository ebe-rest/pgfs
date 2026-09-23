namespace Pgfs.Core.Config;

using System.Collections.Generic;
using Npgsql;

/// <summary>
/// Converts **a libpq connection URI**
/// (<c>postgresql://[user[:password]@][host][:port][,host[:port]...][/dbname][?param=value&amp;...]</c>) into an
/// <see cref="NpgsqlConnectionStringBuilder"/>.
/// <para>
/// <b>Npgsql does not interpret the URI form</b> (<c>NpgsqlConnectionStringBuilder(string)</c> only takes the kv
/// form, and a URI fails with <c>Format of the initialization string does not conform to specification</c>).
/// <see cref="ConnectionField"/> used to pass it straight through, so **writing a URI in the first column of fstab or
/// in <c>-c</c> kept the mount from starting at all** (the documentation and the fstab examples assumed the URI form).
/// </para>
/// <para>
/// What is accepted is libpq's <c>postgresql://</c> / <c>postgres://</c>. The user, password, host, port and dbname are
/// percent-decoded. Several hosts (<c>h1:5432,h2:5433</c>) are handed to Npgsql's Host notation as they are. IPv6 is
/// written in square brackets as <c>[::1]:5432</c>. The query takes libpq's parameter names (the table below) or
/// Npgsql's kv names, and **a name that is neither is an error rather than being dropped silently** (so that a
/// misspelt <c>sslmod=require</c> does not look as if it took effect).
/// </para>
/// </summary>
public static class PostgresUri
{
	/// <summary>libpq's query parameter names -> Npgsql's kv names.</summary>
	private static readonly Dictionary<string, string> LibpqToNpgsql = new(System.StringComparer.OrdinalIgnoreCase) {
		["host"] = "Host",
		["hostaddr"] = "Host",
		["port"] = "Port",
		["dbname"] = "Database",
		["user"] = "Username",
		["password"] = "Password",
		["sslmode"] = "SSL Mode",
		["sslcert"] = "SSL Certificate",
		["sslkey"] = "SSL Key",
		["sslrootcert"] = "Root Certificate",
		["connect_timeout"] = "Timeout",
		["application_name"] = "Application Name",
		["options"] = "Options",
		["target_session_attrs"] = "Target Session Attributes",
		["keepalives_idle"] = "Keepalive",
	};

	/// <summary>Whether <paramref name="raw"/> starts with <c>postgresql://</c> / <c>postgres://</c>.</summary>
	public static bool IsUri(string raw) {
		var s = raw.TrimStart();
		if (s.StartsWith("postgresql://", System.StringComparison.OrdinalIgnoreCase)) { return true; }
		return s.StartsWith("postgres://", System.StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Interprets the URI and returns a builder. Throws <see cref="System.FormatException"/> when it cannot.</summary>
	public static NpgsqlConnectionStringBuilder Parse(string raw) {
		var s = raw.Trim();
		var body = s[(s.IndexOf("://", System.StringComparison.Ordinal) + 3)..];
		var builder = new NpgsqlConnectionStringBuilder();

		// Cut the query off first (a `?` can appear in a password, but it must be percent-encoded there).
		var query = "";
		var q = body.IndexOf('?');
		if (q >= 0) {
			query = body[(q + 1)..];
			body = body[..q];
		}
		// The userinfo runs up to **the last** `@` (no `@` appears on the host side).
		var at = body.LastIndexOf('@');
		if (at >= 0) {
			ApplyUserInfo(builder, body[..at]);
			body = body[(at + 1)..];
		}
		// Everything after the first `/` is the database name.
		var slash = body.IndexOf('/');
		if (slash >= 0) {
			var db = Decode(body[(slash + 1)..]);
			if (db.Length > 0) { builder.Database = db; }
			body = body[..slash];
		}
		ApplyHostSpec(builder, body);
		ApplyQuery(builder, query);
		return builder;
	}

	private static void ApplyUserInfo(NpgsqlConnectionStringBuilder builder, string userInfo) {
		var colon = userInfo.IndexOf(':');
		if (colon < 0) {
			if (userInfo.Length > 0) { builder.Username = Decode(userInfo); }
			return;
		}
		var user = Decode(userInfo[..colon]);
		if (user.Length > 0) { builder.Username = user; }
		builder.Password = Decode(userInfo[(colon + 1)..]);
	}

	/// <summary>
	/// Sets <c>host[:port]</c> (several are allowed, separated by commas). **With a single one, the port goes into
	/// Port**; with several, they are handed to Npgsql's Host notation (<c>h1:5432,h2:5433</c>) as they are. When it
	/// is empty nothing is set (the default destination).
	/// </summary>
	private static void ApplyHostSpec(NpgsqlConnectionStringBuilder builder, string hostSpec) {
		var spec = Decode(hostSpec);
		if (spec.Length == 0) { return; }
		if (spec.Contains(',')) {
			builder.Host = spec;
			return;
		}
		var (host, port) = SplitHostPort(spec);
		if (host.Length > 0) { builder.Host = host; }
		if (port == null) { return; }
		if (!int.TryParse(port, out var portNumber)) {
			throw new System.FormatException($"The port of the connection URI is not a number: '{port}'");
		}
		builder.Port = portNumber;
	}

	/// <summary>
	/// Splits <c>host:port</c>. IPv6 is written in square brackets as <c>[::1]:5432</c> (the brackets are removed for Host).
	/// </summary>
	private static (string Host, string? Port) SplitHostPort(string spec) {
		if (spec.StartsWith('[')) {
			var close = spec.IndexOf(']');
			if (close < 0) { throw new System.FormatException($"The IPv6 address of the connection URI has no ']': '{spec}'"); }
			var v6 = spec[1..close];
			var rest = spec[(close + 1)..];
			if (rest.StartsWith(':')) { return (v6, rest[1..]); }
			return (v6, null);
		}
		var colon = spec.LastIndexOf(':');
		if (colon < 0) { return (spec, null); }
		return (spec[..colon], spec[(colon + 1)..]);
	}

	private static void ApplyQuery(NpgsqlConnectionStringBuilder builder, string query) {
		if (query.Length == 0) { return; }
		foreach (var pair in query.Split('&', System.StringSplitOptions.RemoveEmptyEntries)) {
			var eq = pair.IndexOf('=');
			if (eq <= 0) { throw new System.FormatException($"A parameter of the connection URI is not in the key=value form: '{pair}'"); }
			var key = Decode(pair[..eq]);
			var value = Decode(pair[(eq + 1)..]);
			SetParameter(builder, key, value);
		}
	}

	/// <summary>
	/// Sets one parameter. A libpq name is read as the Npgsql name, and the value of <c>sslmode</c>
	/// (<c>verify-full</c> and so on) has its hyphens removed to match Npgsql's enum names.
	/// **When it is neither a libpq name nor an Npgsql name it is an error** (dropping it silently would leave a setting
	/// that looks as if it took effect but did not).
	/// </summary>
	private static void SetParameter(NpgsqlConnectionStringBuilder builder, string key, string value) {
		var name = key;
		if (LibpqToNpgsql.TryGetValue(key, out var mapped)) { name = mapped; }
		if (string.Equals(name, "SSL Mode", System.StringComparison.OrdinalIgnoreCase)) { value = value.Replace("-", ""); }
		try {
			builder[name] = value;
		} catch (System.ArgumentException ex) {
			throw new System.FormatException($"The connection URI parameter '{key}' cannot be interpreted: {ex.Message}", ex);
		}
	}

	private static string Decode(string s) {
		return System.Uri.UnescapeDataString(s);
	}
}
