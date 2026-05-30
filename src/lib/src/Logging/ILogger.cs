namespace Pgfs.Lib.Logging;

public interface ILogger
{
#if DEBUG
	protected const Level.Enum DefaultLogLevel = Level.Trace;
	protected static readonly Action<string> DefaultOutput = s => {
		Console.WriteLine(s);
		System.Diagnostics.Debug.WriteLine(s);
	};
#else
	protected const           Level.Enum     DefaultLogLevel = Level.Warning;
	protected static readonly Action<string> DefaultOutput = Console.Error.WriteLine;
#endif

	Level.Enum MinLevel { get; set; }
	Action<string> Output { get; set; }

	/// <summary>
	/// An auxiliary sink that emits Warning-and-above logs to stderr in terse form (<c>pgfs:</c> in place of a
	/// timestamp), separately from <see cref="Output"/> (the configured sink). null disables it. When the configured
	/// sink is already stderr, <see cref="LogSink.Configure"/> sets this to null to avoid double output.
	/// </summary>
	Action<string>? WarningStderrSink { get; set; }

	/// <summary>
	/// Returns whether the given level is enabled. Callers use it on hot paths as
	/// <c>if (Logger.IsTraceEnabled) { Logger.Trace(...); }</c> to avoid allocating the
	/// `params object?[]` array and the boxing it incurs.
	/// </summary>
	bool IsEnabled(Level.Enum logLevel) => logLevel >= this.MinLevel;

	void Log(Level.Enum logLevel, params object?[] messages);

	void Trace(params object?[] messages);
	void Debug(params object?[] messages);
	void Information(params object?[] messages);
	void Warning(params object?[] messages);
	void Error(params object?[] messages);
	void Critical(params object?[] messages);
}
