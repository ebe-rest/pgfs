namespace Pgfs.Lib.Logging;

using Pgfs.Lib.Models;

/// <summary>
/// Converts a <see cref="SettingLoggingOutput"/> (the logging.output setting) into the
/// <c>Action&lt;string&gt;</c> sink used for <see cref="Logger.Output"/>. Each Program.cs wires it at startup with
/// <c>Logger.Output = LogSink.Create(config.Logging.Output)</c>.
///
/// <para>
/// Priority is File &gt; Stdout &gt; Stderr &gt; None (same order as <see cref="LoggingOutputField.Format"/>).
/// The default (unset) is <see cref="SettingLoggingKind.Enum.Stderr"/>, so it goes to stderr as before.
/// </para>
/// </summary>
public static class LogSink
{
	/// <summary>
	/// Wires both <see cref="Logger.Output"/> and <see cref="Logger.WarningStderrSink"/> at once. Each Program.cs calls
	/// <c>LogSink.Configure(config.Logging.Output)</c>. The stderr echo of Warning-and-above is disabled when the effective
	/// sink is already stderr (no File/Stdout, only Stderr), because it would otherwise be double output.
	/// </summary>
	public static void Configure(SettingLoggingOutput output) {
		Logger.Output = Create(output);
		Logger.WarningStderrSink = ResolveWarningEcho(output);
	}

	private static Action<string>? ResolveWarningEcho(SettingLoggingOutput output) {
		var kind = output.Kind;
		var sinkIsStderr = (kind & SettingLoggingKind.Enum.File) == 0
			&& (kind & SettingLoggingKind.Enum.Stdout) == 0
			&& (kind & SettingLoggingKind.Enum.Stderr) != 0;
		if (sinkIsStderr) {
			return null;
		}
		return Console.Error.WriteLine;
	}

	public static Action<string> Create(SettingLoggingOutput output) {
		var kind = output.Kind;
		if ((kind & SettingLoggingKind.Enum.File) != 0) {
			var sink = new RotatingFileSink(output.Directory, output.FileNamePattern, output.Cycle);
			return sink.Write;
		}
		if ((kind & SettingLoggingKind.Enum.Stdout) != 0) {
			return Console.Out.WriteLine;
		}
		if ((kind & SettingLoggingKind.Enum.Stderr) != 0) {
			return Console.Error.WriteLine;
		}
		return static _ => { };
	}
}
