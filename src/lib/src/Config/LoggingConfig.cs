namespace Pgfs.Lib.Config;

using Pgfs.Lib.Logging;
using Pgfs.Lib.Models;

/// <summary>
/// Log output settings. The values are filled by <see cref="ConfigLoader"/> integrating CLI / TOML / Default
/// (the persistence target is <see cref="SaveTarget.File"/>; not saved to the DB).
///
/// <para>
/// The settings only take effect once the consumer assigns them, e.g. <c>Logger.MinLevel = config.Logging.MinLevel</c>.
/// </para>
/// </summary>
public sealed class LoggingConfig
{
	public Level.Enum MinLevel { get; set; }
	public SettingLoggingOutput Output { get; set; } = new();
}
