namespace Pgfs.Lib.Utility;

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

public static class String
{
	public static char[] Separators { get; set; } = [',', ';', ':', '|', ' ', '\t', '\n'];

	public enum SplitCharKind
	{
		Char,
		Space,
		Quote,
		QuoteBegin,
		QuoteEnd,
		Escape,
		Separator,
	}

	public static IEnumerable<string> Split(this string? item) {
		return Split<string, StringBuilder>(
			item: item,
			charKind: c => {
				if (char.IsWhiteSpace(c)) { return SplitCharKind.Space; }
				if (c == '"') { return SplitCharKind.Quote; }
				if (c == '\\') { return SplitCharKind.Escape; }
				if (c == ',') { return SplitCharKind.Separator; }
				return SplitCharKind.Char;
			},
			() => new StringBuilder(),
			(b, c) => b.Append(c),
			b => b.ToString()
		);
	}
	public static IEnumerable<TString> Split<TString, TBuilder>(
		this TString? item,
		Func<char, SplitCharKind> charKind,
		Func<TBuilder> initializer,
		Func<TBuilder, char, TBuilder> appender,
		Func<TBuilder, TString> finisher
	) where TString : IEnumerable<char> {
		if (item == null) {
			yield break;
		}

		var current = default(TBuilder);
		var inQuote1 = false;
		var inQuote2 = false; // whether this is the kind of quote closed by the same character
		var escape = false;
		var appending = false;
		var entry = default(TString);
		var ok = false;
		foreach (var c in item) {
			if (escape) {
				append(c);
				escape = false;
				continue;
			}
			switch (charKind(c)) {
			case SplitCharKind.Escape:
				escape = true;
				continue;
			case SplitCharKind.QuoteBegin:
				if (inQuote2) {
					append(c);
					continue;
				}
				if (inQuote1) {
					throw new FormatException("Unexpected quote begin.");
				}
				inQuote1 = true;
				continue;
			case SplitCharKind.QuoteEnd:
				if (inQuote2) {
					append(c);
					continue;
				}
				if (!inQuote1) {
					throw new FormatException("Unexpected quote end.");
				}
				inQuote1 = false;
				continue;
			case SplitCharKind.Quote:
				if (inQuote1) {
					append(c);
					continue;
				}
				if (inQuote2) {
					inQuote2 = false;
					continue;
				}
				inQuote2 = true;
				continue;
			case SplitCharKind.Space:
				if (!inQuote1 && !inQuote2 && !appending) {
					continue;
				}
				append(c);
				continue;
			case SplitCharKind.Separator:
				if (inQuote1 || inQuote2) {
					append(c);
					continue;
				}
				(entry, ok) = push();
				if (!ok) {
					continue;
				}
				yield return entry;
				continue;
			default: // Char
				append(c);
				continue;
			}
		}

		(entry, ok) = push();
		if (!ok) {
			yield break;
		}
		yield return entry;
		yield break;

		void append(char c) {
			if (!appending) {
				current = initializer();
				appending = true;
			}
			current = appender(current!, c);
		}

		(TString, bool) push() {
			if (!appending) {
				return default;
			}
			appending = false;
			return (finisher(current!), true);
		}
	}
	public static string Join(this IEnumerable<string> items, string? separator) {
		return string.Join(separator, items);
	}
	public static string Join(this IEnumerable<string> items, char separator) {
		return string.Join(separator, items);
	}
	public static string ElegantJoin(this IEnumerable items, char[]? separators = null) {
		var strings = items.Cast<string>().Where(e => e != "").ToArray();
		var usedChars = (from j in strings
				from k in j
				select k)
			.ToHashSet();
		var seps =
			from s in (char[])[..separators ?? [], ',', ';', ':', '|', ' ', '\t', '\n']
			where !usedChars.Contains(s)
			select s;
		var sep = seps.First().ToString();
		return new StringBuilder(sep).AppendJoin(sep, strings).Append(sep).ToString();
	}
	public static IEnumerable<string> ElegantSplit(this string? target) {
		if (target == null) {
			yield break;
		}
		var length = target.Length;
		switch (length) {
		case 0:
			yield break;
		case 1:
			yield return target;
			yield break;
		case 2 when target[0] == target[^1]:
			yield break;
		case 2:
			yield return target;
			yield break;
		default:
			var separator = target[0];
			var start = 1;
			for (;;) {
				var end = target[start..].IndexOf(separator);
				if (end < 0) {
					yield return target[start..];
					yield break;
				}
				yield return target[start..end];
				start = end + 1;
			}
		}
	}
	public static bool IsNullOrWhiteSpace([NotNullWhen(false)] this string? value) {
		return string.IsNullOrWhiteSpace(value);
	}

	public static bool IsNotNullAndNotWhiteSpace([NotNullWhen(true)] this string? value) {
		return !string.IsNullOrWhiteSpace(value);
	}
}
