namespace Pgfs.Assign;

using Lib;
using Lib.Logging;
using LTRData.Extensions.Native.Memory;

public static class Logger
{
	public static Instance Default { get; set; } = new();

	public static bool DebugEnabled => Logger.Default.DebugEnabled;

	public static Level.Enum MinLevel {
		get => Logger.Default.MinLevel;
		set => Logger.Default.MinLevel = value;
	}

	public static Action<string> Output {
		get => Logger.Default.Output;
		set => Logger.Default.Output = value;
	}

	public static void Log(Level.Enum logLevel, params object?[] messages) => Logger.Default.Log(logLevel, messages!);

	/// <summary>Process lifecycle markers (always stderr). Backed by <see cref="Lib.Logging.Logger.Lifecycle"/>.</summary>
	public static void Lifecycle(params object?[] messages) => Lib.Logging.Logger.Lifecycle(messages);

	public static void Trace(params object?[] messages) => Logger.Log(Level.Trace, messages);
	public static void Debug(params object?[] messages) => Logger.Log(Level.Debug, messages);
	public static void Information(params object?[] messages) => Logger.Log(Level.Information, messages);
	public static void Warning(params object?[] messages) => Logger.Log(Level.Warning, messages);
	public static void Error(params object?[] messages) => Logger.Log(Level.Error, messages);
	public static void Critical(params object?[] messages) => Logger.Log(Level.Critical, messages);

	public static void Debug(string message, params object[] args) => Logger.Log(Level.Debug, message, args);
	public static void Info(string message, params object[] args) => Logger.Log(Level.Information, [message, ..args]);
	public static void Warn(string message, params object[] args) => Logger.Log(Level.Warning, [message, ..args]);
	public static void Error(string message, params object[] args) => Logger.Log(Level.Error, [message, ..args]);
	public static void Fatal(string message, params object[] args) => Logger.Log(Level.Critical, [message, ..args]);

	public sealed class Instance(ILogger parent) : ILogger, DokanNet.Logging.ILogger
	{
		public Instance() : this(Lib.Logging.Logger.Default) { }

		public bool DebugEnabled => parent.MinLevel <= Level.Debug;

		public Level.Enum MinLevel {
			get => parent.MinLevel;
			set => parent.MinLevel = value;
		}

		public Action<string> Output {
			get => parent.Output;
			set => parent.Output = value;
		}

		public Action<string>? WarningStderrSink {
			get => parent.WarningStderrSink;
			set => parent.WarningStderrSink = value;
		}

		public void Log(Level.Enum logLevel, params object?[] messages) {
			for (var i = 0; i < messages.Length; i++) {
				switch (messages[i]) {
				case NativeMemory<char> mem1:
					messages[i] = mem1.Span.ToString();
					continue;
				case ReadOnlyNativeMemory<char> mem2:
					messages[i] = mem2.Span.ToString();
					continue;
				default:
					continue;
				}
			}
			parent.Log(logLevel, messages);
		}

		public void Trace(params object?[] messages) => parent.Log(Level.Trace, messages);
		public void Debug(params object?[] messages) => parent.Log(Level.Debug, messages);
		public void Information(params object?[] messages) => parent.Log(Level.Information, messages);
		public void Warning(params object?[] messages) => parent.Log(Level.Warning, messages);
		public void Error(params object?[] messages) => parent.Log(Level.Error, messages);
		public void Critical(params object?[] messages) => parent.Log(Level.Critical, messages);

		public void Debug(string message, params object[] args) => parent.Log(Level.Debug, [message, ..args]);
		public void Info(string message, params object[] args) => parent.Log(Level.Information, [message, ..args]);
		public void Warn(string message, params object[] args) => parent.Log(Level.Warning, [message, ..args]);
		public void Error(string message, params object[] args) => parent.Log(Level.Error, [message, ..args]);
		public void Fatal(string message, params object[] args) => parent.Log(Level.Critical, [message, ..args]);
	}
}
