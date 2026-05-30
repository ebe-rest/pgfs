namespace Pgfs.Lib.Logging;

using Pgfs.Lib.Models;

/// <summary>
/// A write sink for log files that rotate by date. Used when <see cref="LogSink"/> resolves
/// <c>logging.output = "&lt;cycle&gt;:&lt;dir&gt;/&lt;pattern&gt;"</c> (the File form).
///
/// <para>
/// The file name is formed by replacing <c>*</c> in <c>pattern</c> with a date stamp at the cycle granularity:
/// hourly → <c>yyyyMMddHH</c> / daily → <c>yyyyMMdd</c> / monthly → <c>yyyyMM</c> / none → empty string.
/// e.g. <c>daily:~/pgfs/log/pgfs-*.log</c> → <c>~/pgfs/log/pgfs-20260527.log</c>.
/// <c>~</c> expands to the home directory. If a write crosses a date boundary, the file is reopened.
/// </para>
///
/// <para>
/// It is called from multiple FUSE / Dokan threads, so writes are serialized with a lock. <see cref="StreamWriter.AutoFlush"/>
/// is enabled so the most recent log is not lost on abnormal process termination. If file I/O fails it falls back to
/// stderr, so logging never drags down the filesystem itself.
/// </para>
/// </summary>
internal sealed class RotatingFileSink
{
	private readonly string directory;
	private readonly string pattern;
	private readonly SettingLoggingCycle.Enum cycle;
	private readonly object gate = new();
	private string? currentPath;
	private StreamWriter? writer;

	public RotatingFileSink(string directory, string pattern, SettingLoggingCycle.Enum cycle) {
		this.directory = ExpandHome(directory);
		this.pattern = pattern;
		this.cycle = cycle;
	}

	public void Write(string line) {
		lock (this.gate) {
			try {
				var path = this.ResolvePath(DateTime.Now);
				if (path != this.currentPath) { this.Reopen(path); }
				this.writer!.WriteLine(line);
			} catch (Exception ex) {
				// Do not stop the FS itself when a log file write fails. Escape to stderr.
				Console.Error.WriteLine(line);
				Console.Error.WriteLine($"[pgfs: log file write failed: {ex.Message}]");
			}
		}
	}

	private void Reopen(string path) {
		this.writer?.Flush();
		this.writer?.Dispose();
		Directory.CreateDirectory(this.directory);
		var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
		this.writer = new StreamWriter(stream) { AutoFlush = true };
		this.currentPath = path;
	}

	private string ResolvePath(DateTime now) {
		var stamp = this.cycle switch {
			SettingLoggingCycle.Enum.Hourly => now.ToString("yyyyMMddHH"),
			SettingLoggingCycle.Enum.Daily => now.ToString("yyyyMMdd"),
			SettingLoggingCycle.Enum.Monthly => now.ToString("yyyyMM"),
			_ => "",
		};
		var fileName = this.pattern.Contains('*') ? this.pattern.Replace("*", stamp) : this.pattern;
		return Path.Join(this.directory, fileName);
	}

	private static string ExpandHome(string dir) {
		if (dir == "~") {
			return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		if (dir.StartsWith("~/") || dir.StartsWith("~\\")) {
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Join(home, dir[2..]);
		}
		return dir;
	}
}
