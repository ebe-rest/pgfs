namespace Pgfs.Lib.Logging;

public static class Logger
{
	public static ILogger Default { get; set; } = new Instance();

	public static Level.Enum MinLevel {
		get => Default.MinLevel;
		set => Default.MinLevel = value;
	}

	public static Action<string> Output {
		get => Default.Output;
		set => Default.Output = value;
	}

	public static Action<string>? WarningStderrSink {
		get => Default.WarningStderrSink;
		set => Default.WarningStderrSink = value;
	}

	public static bool IsEnabled(Level.Enum level) => Default.IsEnabled(level);
	public static bool IsTraceEnabled => Default.IsEnabled(Level.Enum.Trace);
	public static bool IsDebugEnabled => Default.IsEnabled(Level.Enum.Debug);

	/// <summary>
	/// Emits information that should always be followable on the console (stderr) even when the log sink is a file —
	/// process lifecycle such as the startup banner / shutdown. Always writes to stderr in terse form (<c>pgfs: ...</c>),
	/// and when the configured sink is not stderr (file / stdout) also records it there with a timestamp (avoids double
	/// output when the sink is already stderr).
	/// </summary>
	public static void Lifecycle(params object?[] messages) {
		// If the configured sink is not stderr, also keep a copy in the sink for the record (WarningStderrSink != null means the sink is not stderr).
		if (Default.WarningStderrSink != null) {
			Default.Information(messages);
		}
		var sb = new System.Text.StringBuilder();
		foreach (var message in messages) {
			sb.Append(message);
		}
		System.Console.Error.WriteLine($"pgfs: {sb}");
	}

	public static void Log(Level.Enum level, params object[] messages) => Default.Log(level, messages);

	public static void Trace(params object?[] messages) => Default.Trace(messages);
	public static void Debug(params object?[] messages) => Default.Debug(messages);
	public static void Information(params object?[] messages) => Default.Information(messages);
	public static void Warning(params object?[] messages) => Default.Warning(messages);
	public static void Error(params object?[] messages) => Default.Error(messages);
	public static void Critical(params object?[] messages) => Default.Critical(messages);
}
