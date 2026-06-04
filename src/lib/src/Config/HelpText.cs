namespace Pgfs.Lib.Config;

using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Builds the <c>--help</c> text from <see cref="Schema.AllFields"/>. Each Program passes only the intro
/// (Usage + a one-line description) and the footer (tool-specific prose: fstab usage, unmount steps, etc.);
/// the option listing is auto-generated from each Field's <see cref="Field.CliOptions"/> /
/// <see cref="Field.Comment"/> / type / default value. <see cref="Field.AppliesTo"/> filters per tool.
///
/// <para>
/// The description uses each Field's <see cref="Field.Comment"/> (English) as-is. The goal is to remove the
/// double-maintenance of hand-written help, so adding a new setting to <see cref="Schema"/> automatically
/// lists it in help too.
/// </para>
/// </summary>
public static class HelpText
{
	/// <summary>The column where the description starts. If the left (option column) is longer, it wraps to the next line.</summary>
	private const int DescColumn = 34;

	/// <summary>The maximum length of a default value to display. Defaults longer than this (e.g. search_path) are verbose, so omitted.</summary>
	private const int MaxDefaultLen = 40;

	/// <summary>The display order and headings of scopes. A scope not listed here is not shown.</summary>
	private static readonly (string Scope, string Title)[] ScopeOrder = [
		("root", "Common options"),
		("setting", "Configuration file"),
		("database", "Database"),
		("mount", "Mount"),
		("file_system", "Filesystem"),
		("statfs", "Free space (df)"),
		("logging", "Logging"),
		("audit", "Audit"),
	];

	/// <summary>
	/// Returns the full help text for the given tool. Concatenates <paramref name="intro"/> → the per-scope
	/// option listing → <paramref name="footer"/>.
	/// </summary>
	public static string Build(Tool tool, string intro, string footer) {
		var sb = new StringBuilder();
		sb.AppendLine(intro.TrimEnd());

		foreach (var (scope, title) in ScopeOrder) {
			var fields = Schema.AllFields
				.Where(f => f.Scope == scope && f.AppliesTo.HasFlag(tool))
				.ToList();
			if (fields.Count == 0) {
				continue;
			}
			sb.AppendLine();
			sb.AppendLine($"{title}:");
			foreach (var f in fields) {
				sb.AppendLine(RenderField(f));
			}
		}

		var foot = footer.Trim();
		if (foot.Length == 0) {
			return sb.ToString();
		}
		sb.AppendLine();
		sb.AppendLine(foot);
		return sb.ToString();
	}

	/// <summary>Formats one Field into one option line. If the left side overflows, the description wraps to the next line.</summary>
	private static string RenderField(Field f) {
		var left = new StringBuilder("  ").Append(string.Join(", ", f.CliOptions));
		var placeholder = Placeholder(f);
		if (placeholder != null) {
			left.Append(' ').Append(placeholder);
		}
		var leftStr = left.ToString();

		var desc = f.Comment;
		var def = f.HelpDefaultRaw();
		if (def != null && def.Length <= MaxDefaultLen) {
			desc = $"{desc} (default: {def})";
		}
		if (desc.Length == 0) {
			return leftStr;
		}
		if (leftStr.Length <= DescColumn - 2) {
			return leftStr.PadRight(DescColumn) + desc;
		}
		return leftStr + "\n" + new string(' ', DescColumn) + desc;
	}

	/// <summary>The value placeholder. Uses <see cref="Field.ArgName"/> if set, otherwise derives it from the type.</summary>
	private static string? Placeholder(Field f) {
		if (f.ArgName != null) {
			return f.ArgName;
		}
		return f switch {
			BoolField => null,
			IntField or LongField => "<n>",
			ConnectionField => "<connstr>",
			LogLevelField => "<level>",
			LoggingOutputField => "<spec>",
			StringListField => "<list>",
			_ => "<value>",
		};
	}
}
