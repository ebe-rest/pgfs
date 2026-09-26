namespace Pgfs.Core.Logging;

using System.Globalization;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// **A sink that sends one line at a time to syslog** (v0.2.2). It is for the daemonized child of <c>mount.pgfs</c>:
/// once it has cut off stdout / stderr, the Error / Warning lines and the start / exit lines can still be read with
/// <c>journalctl -t pgfs</c>.
///
/// <para>
/// **It does not call libc's <c>syslog(3)</c>; it sends an RFC 3164 datagram straight to the local syslog socket**
/// (<c>/dev/log</c>, <c>/var/run/syslog</c> on macOS). <c>syslog(3)</c> is a variadic function and is not guaranteed
/// to be safe to call through P/Invoke. Both journald and rsyslog accept this format. The facility is daemon.
/// </para>
/// <para>
/// **It never throws when sending fails** - a logging failure must not stop the mount. Where there is no socket
/// (Windows / containers and so on) <see cref="TryOpen"/> returns null and the caller wires nothing.
/// </para>
/// </summary>
public sealed class SyslogSink
{
	private const int FacilityDaemon = 3;

	// The limit for one datagram. journald accepts longer lines, but it is cut to rsyslog's default (8 KiB).
	private const int MaxBytes = 8192;

	private static readonly string[] SocketPaths = ["/dev/log", "/var/run/syslog"];

	private readonly Socket socket;
	private readonly UnixDomainSocketEndPoint endPoint;
	private readonly string ident;
	private readonly int pid;
	private readonly object gate = new();

	private SyslogSink(Socket socket, UnixDomainSocketEndPoint endPoint, string ident) {
		this.socket = socket;
		this.endPoint = endPoint;
		this.ident = ident;
		this.pid = Environment.ProcessId;
	}

	/// <summary>
	/// Looks for the local syslog socket and opens it. Null when there is none.
	/// </summary>
	public static SyslogSink? TryOpen(string ident) {
		foreach (var path in SocketPaths) {
			if (!File.Exists(path)) { continue; }
			try {
				var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
				return new SyslogSink(socket, new UnixDomainSocketEndPoint(path), ident);
			} catch (Exception) {
				// Try the next candidate.
			}
		}
		return null;
	}

	/// <summary>
	/// Sends one entry. Shaped to be wired to <see cref="Logger.SyslogSink"/> (<c>Action&lt;Level.Enum, string&gt;</c>).
	/// </summary>
	public void Write(Level.Enum level, string message) {
		var bytes = Encoding.UTF8.GetBytes(this.Format(level, message));
		var length = Math.Min(bytes.Length, MaxBytes);
		try {
			lock (this.gate) {
				this.socket.SendTo(bytes.AsSpan(0, length), SocketFlags.None, this.endPoint);
			}
		} catch (Exception) {
			// Even when syslog is down, the mount is not stopped for the sake of logging.
		}
	}

	/// <summary>
	/// The log level -> the syslog severity. The start / exit lines (<see cref="Logger.Lifecycle"/>) come as Information and become notice.
	/// </summary>
	public static int Severity(Level.Enum level) {
		switch (level) {
			case Level.Enum.Critical:
				return 2;
			case Level.Enum.Error:
				return 3;
			case Level.Enum.Warning:
				return 4;
			default:
				return 5;
		}
	}

	private string Format(Level.Enum level, string message) {
		var priority = FacilityDaemon * 8 + Severity(level);
		var now = DateTime.Now;
		// The RFC 3164 date pads the day to two characters with a space, as in "Sep  5".
		var day = now.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);
		var stamp = $"{now.ToString("MMM", CultureInfo.InvariantCulture)} {day} {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
		return $"<{priority}>{stamp} {this.ident}[{this.pid}]: {message}";
	}
}
