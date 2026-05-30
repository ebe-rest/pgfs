namespace Pgfs.Lib.Logging;

using System.Text;

public class Instance(Level.Enum logLevel, Action<string> output) : ILogger
{
	public Instance(Action<string> output) : this(ILogger.DefaultLogLevel, output) { }
	public Instance(Level.Enum logLevel) : this(logLevel, ILogger.DefaultOutput) { }
	public Instance() : this(ILogger.DefaultLogLevel, ILogger.DefaultOutput) { }

	public Level.Enum MinLevel { get; set; } = logLevel;
	public Action<string> Output { get; set; } = output;
	public Action<string>? WarningStderrSink { get; set; }

	public void Log(Level.Enum logLevel, params object?[] messages) {
		if (logLevel < this.MinLevel) {
			return;
		}

		var body = new StringBuilder();
		foreach (var message in messages) {
			body.Append(message);
		}
		var bodyStr = body.ToString();

		var sb = new StringBuilder();
		sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff"));
		sb.Append(" [");
		sb.Append(logLevel);
		sb.Append("](");
		sb.Append(Thread.CurrentThread.ManagedThreadId);
		sb.Append(") ");
		sb.Append(bodyStr);
		this.Output(sb.ToString());

		// Warning and above is also emitted to stderr in terse form (only when the configured sink is not stderr).
		var echo = this.WarningStderrSink;
		if (logLevel >= Level.Warning && echo != null) {
			echo($"pgfs: [{logLevel}] {bodyStr}");
		}
	}

	public void Trace(params object?[] messages) => this.Log(Level.Trace, messages);
	public void Debug(params object?[] messages) => this.Log(Level.Debug, messages);
	public void Information(params object?[] messages) => this.Log(Level.Information, messages);
	public void Warning(params object?[] messages) => this.Log(Level.Warning, messages);
	public void Error(params object?[] messages) => this.Log(Level.Error, messages);
	public void Critical(params object?[] messages) => this.Log(Level.Critical, messages);
}
