namespace Pgfs.Core.Utility;

using System.Runtime.InteropServices;

/// <summary>
/// Maps service names to port numbers.
/// </summary>
public static class ServiceResolver
{
	/// <summary>The result of splitting one line of `/etc/services`.</summary>
	/// <param name="Name">The service name (column 1).</param>
	/// <param name="Port">The port number.</param>
	/// <param name="Protocol">The protocol (<c>tcp</c> / <c>udp</c> and so on).</param>
	/// <param name="Rest">Column 3 onwards (the aliases and the end-of-line comment. **The `#` has not been dropped yet**).</param>
	private readonly record struct ServiceEntry(string Name, int Port, string Protocol, string[] Rest);

	/// <summary>
	/// Splits `/etc/services` line by line and returns the pieces. **Comments, blank lines and malformed lines are skipped.**
	/// <para>
	/// **The forward lookup (name -> port) and the reverse lookup (port -> name) each carried the same
	/// preprocessing**, so it was made into one. The only difference was the spelling of `StartsWith('#')`
	/// (char) versus `StartsWith("#")` (string).
	/// **The caller decides how to treat the aliases** - because only the forward lookup looks at column 3 onwards.
	/// </para>
	/// <para>
	/// **The exceptions of `File.ReadLines` come out during the enumeration**, so the caller's `try` may stay wrapped around the `foreach`.
	/// </para>
	/// </summary>
	private static IEnumerable<ServiceEntry> EnumerateServices(string servicesPath) {
		foreach (var line in File.ReadLines(servicesPath)) {
			var trimmedLine = line.Trim();
			if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#')) {
				continue;
			}
			// Parse: service_name  port/protocol  [aliases...]
			var parts = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) {
				continue;
			}
			var portProto = parts[1].Split('/');
			if (portProto.Length < 2) {
				continue;
			}
			if (!int.TryParse(portProto[0], out var portNumber)) {
				continue;
			}
			yield return new ServiceEntry(parts[0], portNumber, portProto[1], parts[2..]);
		}
	}
	// A cache of the services that get used often
	private static readonly Dictionary<string, int> knownServices = new(StringComparer.OrdinalIgnoreCase) {
		["ftp"] = 21,
		["ssh"] = 22,
		["telnet"] = 23,
		["smtp"] = 25,
		["dns"] = 53,
		["http"] = 80,
		["pop3"] = 110,
		["nntp"] = 119,
		["ntp"] = 123,
		["imap"] = 143,
		["snmp"] = 161,
		["ldap"] = 389,
		["https"] = 443,
		["smtps"] = 465,
		["ldaps"] = 636,
		["imaps"] = 993,
		["pop3s"] = 995,
		["mssql"] = 1433,
		["oracle"] = 1521,
		["mysql"] = 3306,
		["rdp"] = 3389,
		["postgresql"] = 5432,
		["redis"] = 6379,
		["http-alt"] = 8080,
		["elasticsearch"] = 9200,
	};

	/// <summary>
	/// Gets the port number for a service name.
	/// </summary>
	/// <param name="serviceName">The service name (e.g. "http", "postgresql").</param>
	/// <param name="protocol">The protocol ("tcp" or "udp"; default: null).</param>
	/// <returns>The port number, or -1 if not found.</returns>
	public static int GetPortByServiceName(string serviceName, string? protocol = null) {
		if (string.IsNullOrWhiteSpace(serviceName)) {
			return -1;
		}

		// Return numeric values as-is.
		if (int.TryParse(serviceName, out var port)) {
			return port;
		}

		// Check the well-known services.
		if (knownServices.TryGetValue(serviceName, out var wellKnownPort)) {
			return wellKnownPort;
		}

		// Look it up in the OS services file.
		return GetPortFromServicesFile(serviceName, protocol);
	}

	/// <summary>
	/// Looks up a port number in the OS services file.
	/// </summary>
	private static int GetPortFromServicesFile(string serviceName, string? protocol) {
		var servicesPath = GetServicesFilePath();
		if (!File.Exists(servicesPath)) {
			return GetPortByServiceNameNative(serviceName, protocol);
		}

		try {
			foreach (var entry in EnumerateServices(servicesPath)) {
				var proto = entry.Protocol;

				// The service name matches
				if (entry.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase)) {
					// No protocol was specified, or it matches
					if (protocol == null || proto.Equals(protocol, StringComparison.OrdinalIgnoreCase)) {
						return entry.Port;
					}
				}

				// Check the aliases (**stop as soon as the end-of-line comment is reached**)
				foreach (var alias in entry.Rest) {
					if (alias.StartsWith('#')) {
						break;
					}

					if (alias.Equals(serviceName, StringComparison.OrdinalIgnoreCase)) {
						if (protocol == null || proto.Equals(protocol, StringComparison.OrdinalIgnoreCase)) {
							return entry.Port;
						}
					}
				}
			}
		} catch (Exception) {
			// Ignore file read errors.
			return GetPortByServiceNameNative(serviceName, protocol);
		}

		return GetPortByServiceNameNative(serviceName, protocol);
	}

	/// <summary>
	/// Gets the service name for a port number (reverse lookup).
	/// </summary>
	public static string? GetServiceNameByPort(int port, string? protocol = null) {
		// Search the well-known services.
		foreach (var kvp in knownServices.Where(kvp => kvp.Value == port)) {
			return kvp.Key;
		}

		// Search the services file.
		return GetServiceNameFromServicesFile(port, protocol);
	}

	private static string? GetServiceNameFromServicesFile(int port, string? protocol) {
		var servicesPath = GetServicesFilePath();

		if (!File.Exists(servicesPath)) {
			return null;
		}

		try {
			foreach (var entry in EnumerateServices(servicesPath)) {
				if (entry.Port == port) {
					if (protocol == null || entry.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase)) {
						return entry.Name;
					}
				}
			}
		} catch (Exception) {
			// Ignore file read errors.
		}

		return null;
	}

#pragma warning disable CA2101
#pragma warning disable SYSLIB1054
#pragma warning disable CS8981 // The type name contains only lowercase ASCII characters. Such names may be reserved for the language.
#if WINDOWS
	[StructLayout(LayoutKind.Sequential)]
	private struct /*ReSharper disable once InconsistentNaming*/servent
	{
		public nint s_name; // Service name.
		public nint s_aliases; // List of aliases.
		public short s_port; // Port number (network byte order).
		public nint s_proto; // Protocol name.
	}
#else
	[StructLayout(LayoutKind.Sequential)]
	private struct /*ReSharper disable once InconsistentNaming*/servent {
		public nint   s_name;    // Service name.
		public nint   s_aliases; // List of aliases.
		public ushort s_port;    // Port number (network byte order).
		public nint   s_proto;   // Protocol name.
	}
#endif

#if WINDOWS
	[DllImport("ws2_32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern nint /*ReSharper disable once IdentifierTypo*/ getservbyname(string name, string? proto);
#else
	[DllImport("libc", CharSet = CharSet.Ansi)]
	private static extern nint /*ReSharper disable once IdentifierTypo*/ getservbyname(string name, string? proto);
#endif

#if WINDOWS
	[DllImport("ws2_32.dll")]
	private static extern short /*ReSharper disable once IdentifierTypo*/ ntohs( /*ReSharper disable once IdentifierTypo*/ short netshort);
#else
	[DllImport("libc")]
	private static extern ushort /*ReSharper disable once IdentifierTypo*/ ntohs(ushort /*ReSharper disable once IdentifierTypo*/netshort);
#endif

#if WINDOWS
	/// <summary>
	/// Gets a port from a service name using the native API.
	/// </summary>
	public static int GetPortByServiceNameNative(string serviceName, string? protocol = null) {
		if (string.IsNullOrWhiteSpace(serviceName)) {
			return -1;
		}

		// Return numeric values as-is.
		if (int.TryParse(serviceName, out var port)) {
			return port;
		}

		try {
			var result = getservbyname(serviceName, protocol);

			if (result == nint.Zero) {
				return -1;
			}

			var entry = Marshal.PtrToStructure<servent>(result);

			// Convert from network byte order to host byte order.
			return ntohs(entry.s_port);
		} catch {
			return -1;
		}
	}
#else
	/// <summary>
	/// Gets a port from a service name using the native API.
	/// </summary>
	public static int GetPortByServiceNameNative(string serviceName, string? protocol = null) {
		if (string.IsNullOrWhiteSpace(serviceName)) {
			return -1;
		}

		// Return numeric values as-is.
		if (int.TryParse(serviceName, out var port)) {
			return port;
		}

		try {
			var result = ServiceResolver.getservbyname(serviceName, protocol);

			if (result == nint.Zero) {
				return -1;
			}

			var entry = Marshal.PtrToStructure<servent>(result);

			// Convert from network byte order to host byte order.
			return ServiceResolver.ntohs(entry.s_port);
		} catch {
			return -1;
		}
	}
#endif

#if WINDOWS
	/// <summary>
	/// Gets the per-OS services file path.
	/// </summary>
	private static string GetServicesFilePath() {
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "services");
	}
#else
	/// <summary>
	/// Gets the per-OS services file path.
	/// </summary>
	private static string GetServicesFilePath() {
		return "/etc/services";
	}
#endif

#pragma warning restore CS8981 // The type name contains only lowercase ASCII characters. Such names may be reserved for the language.
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101
}
