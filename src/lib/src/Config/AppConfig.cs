namespace Pgfs.Lib.Config;

/// <summary>
/// Application-behavior settings (scope <c>app</c>).
///
/// <para>
/// <see cref="Schema.App.Plperlu"/> is the top-level gate for whether plperlu (untrusted Perl) may be used.
/// It decides whether mkfs may use plperlu for the real-measurement statfs function / tablespace auto-mkdir.
/// Default <c>true</c>. Persisted to <see cref="SaveTarget.Db"/>. The plperlu x statfs behavior matrix is in
/// [docs/settings-and-plperlu.md](../../../../docs/settings-and-plperlu.md).
/// </para>
/// </summary>
public sealed class AppConfig
{
	public bool Plperlu { get; set; } = true;
}
