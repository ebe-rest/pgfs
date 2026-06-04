namespace Pgfs.Lib.Config;

/// <summary>
/// Identifier of a PGFS executable (= tool). Used by <see cref="Field.AppliesTo"/> to express
/// "which tool's <c>--help</c> this setting appears in".
///
/// <para>
/// The current use is **help-generation filtering only**. CLI parsing (<see cref="ConfigLoader"/>) still
/// accepts all of <see cref="Schema.AllFields"/> as before (e.g. mount silently swallowing and ignoring
/// <c>--citus</c> is unchanged). The sole responsibility is to hide a tool-specific option from another
/// tool's help.
/// </para>
/// </summary>
[System.Flags]
public enum Tool
{
	None = 0,
	Mkfs = 1,
	Mount = 2,
	Assign = 4,
	All = Mkfs | Mount | Assign,
}
