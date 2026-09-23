namespace Pgfs.Core.Config;

using System.Text.RegularExpressions;
using Npgsql;

/// <summary>
/// **Hides the password** in a connection string (for the log, the header comment of a generated <c>pgfs.toml</c>
/// and <c>pgfsctl config list</c>).
/// <para>
/// There used to be only a regular expression for the kv form (<c>Password=...</c>), so **the password of the URL
/// form (<c>postgresql://user:secret@host/db</c>) came out in plain text**. Writing a URL in the first column of
/// fstab or in <c>-c</c> is an ordinary thing to do, so both forms are hidden.
/// </para>
/// <para>
/// <b>The original notation is kept as far as possible</b> - when the regular expressions can hide it, that result is
/// returned (the order of the kv pairs and the shape of the URL are left alone). Only when the regular expressions
/// find nothing but parsing with <see cref="NpgsqlConnectionStringBuilder"/> yields a Password (a notation not seen
/// before) is it **normalised first and then hidden** (the notation changes, but not leaking comes first).
/// </para>
/// </summary>
public static class ConnectionStringMask
{
	private const string Masked = "***";

	// The kv form: `Password=secret;` (case-insensitive)
	private static readonly Regex KvPassword = new(@"(?i)(password\s*=)[^;]*", RegexOptions.Compiled);

	// The userinfo of the URL form: the `secret` of `postgresql://user:secret@host`
	private static readonly Regex UrlUserInfo = new(@"(?i)^(\s*postgres(?:ql)?://[^:/@]*:)[^@]*@", RegexOptions.Compiled);

	// The query of the URL form: `postgresql://host/db?password=secret&...`
	private static readonly Regex UrlQueryPassword = new(@"(?i)([?&]password=)[^&]*", RegexOptions.Compiled);

	/// <summary>Returns <paramref name="value"/> with its password replaced by <c>***</c>.</summary>
	public static string Mask(string value) {
		var masked = KvPassword.Replace(value, "$1" + Masked);
		masked = UrlUserInfo.Replace(masked, "$1" + Masked + "@");
		masked = UrlQueryPassword.Replace(masked, "$1" + Masked);
		if (!string.Equals(masked, value, System.StringComparison.Ordinal)) { return masked; }
		return MaskByParsing(value);
	}

	/// <summary>
	/// The entry point for displaying a setting's value. It hides **only when the key is a connection string**
	/// (<c>database.connection</c> / <c>database.super_connection</c>).
	/// </summary>
	public static string MaskIfConnection(string fullKey, string value) {
		if (!fullKey.Contains("connection")) { return value; }
		return Mask(value);
	}

	/// <summary>
	/// The last line of defence when the regular expressions found nothing. When parsing yields a Password, it is
	/// **normalised (to the kv form)** and hidden. A string that cannot be parsed is returned as it is (so that
	/// something that is not a connection string is not broken).
	/// </summary>
	private static string MaskByParsing(string value) {
		NpgsqlConnectionStringBuilder builder;
		try {
			builder = ParseAny(value);
		} catch (System.Exception) {
			return value;
		}
		if (string.IsNullOrEmpty(builder.Password)) { return value; }
		builder.Password = Masked;
		return builder.ConnectionString;
	}

	/// <summary>Interprets both the kv form and the URL form with the same rules as <see cref="ConnectionField"/>.</summary>
	private static NpgsqlConnectionStringBuilder ParseAny(string value) {
		if (PostgresUri.IsUri(value)) { return PostgresUri.Parse(value); }
		return new NpgsqlConnectionStringBuilder(value);
	}
}
