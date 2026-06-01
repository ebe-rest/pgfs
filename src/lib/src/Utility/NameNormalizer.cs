namespace Pgfs.Lib.Utility;

using System.Text;

/// <summary>
/// Normalization of owner / group / principal names. See [docs/permission-interop.md](../../../../docs/permission-interop.md).
///
/// PGFS is a name-based ACL store: the "same person" is represented by a matching text name across Linux and Windows.
/// To absorb the naming differences between the two OSes (Windows is case-insensitive and domain-qualified, Linux is
/// case-sensitive), the same normalization is applied to <b>stored names, comparisons, and the caller's own name</b>.
/// This overrides Linux's native case-sensitivity at the pgfs layer so that <c>alice</c> = <c>Alice</c> = <c>Ａlice</c>
/// are treated as the same on both OSes.
///
/// Order of normalization:
/// <list type="number">
/// <item>full-width ASCII (U+FF01–FF5E) → half-width (U+0021–007E), full-width space (U+3000) → space</item>
/// <item>strip domain: <c>DOMAIN\name</c> (NetBIOS) and <c>name@domain</c> (UPN) → <c>name</c></item>
/// <item>lower-case (invariant)</item>
/// </list>
///
/// Aliasing of well-known names across OSes (root↔Administrator etc.) is not this class's responsibility; it is
/// Windows-specific and handled by the mapping in <see cref="Pgfs.Assign.WindowsUserResolver"/> (Linux names are
/// canonical for storage).
/// </summary>
public static class NameNormalizer
{
	/// <summary>Normalize a name. null / empty returns an empty string.</summary>
	public static string Normalize(string? name) {
		if (string.IsNullOrEmpty(name)) { return string.Empty; }
		var half = ToHalfWidthAscii(name);
		var stripped = StripDomain(half);
		return stripped.ToLowerInvariant();
	}

	/// <summary>Fold full-width ASCII / full-width space to half-width. Other characters pass through.</summary>
	private static string ToHalfWidthAscii(string s) {
		var sb = new StringBuilder(s.Length);
		foreach (var c in s) {
			if (c >= '！' && c <= '～') {
				sb.Append((char)(c - 0xFEE0));
				continue;
			}
			if (c == '　') {
				sb.Append(' ');
				continue;
			}
			sb.Append(c);
		}
		return sb.ToString();
	}

	/// <summary>Drop the prefix of <c>DOMAIN\name</c> and the suffix of <c>name@domain</c>.</summary>
	private static string StripDomain(string name) {
		var s = name;
		var bs = s.LastIndexOf('\\');
		if (bs >= 0) { s = s[(bs + 1)..]; }
		var at = s.IndexOf('@');
		if (at >= 0) { s = s[..at]; }
		return s;
	}
}
