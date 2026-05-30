namespace Pgfs.Lib;

using System.Reflection;

/// <summary>
/// Reads the running program's name / version / copyright from the entry assembly's attributes.
/// The values originate from <c>src/Directory.Build.props</c> (Version / Product / Copyright). Used for the startup banner.
/// </summary>
public static class AppInfo
{
	/// <summary>Tool name (e.g. <c>mkfs.pgfs</c>). The entry assembly's simple name.</summary>
	public static string Name {
		get {
			var asm = Assembly.GetEntryAssembly();
			if (asm == null) {
				return "pgfs";
			}
			return asm.GetName().Name ?? "pgfs";
		}
	}

	/// <summary>Version (e.g. <c>0.1.0</c>). Drops the <c>+githash</c> suffix of InformationalVersion.</summary>
	public static string Version {
		get {
			var asm = Assembly.GetEntryAssembly();
			if (asm == null) {
				return "0.0.0";
			}
			var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			if (string.IsNullOrEmpty(info)) {
				return asm.GetName().Version?.ToString() ?? "0.0.0";
			}
			var plus = info.IndexOf('+');
			if (plus < 0) {
				return info;
			}
			return info[..plus];
		}
	}

	/// <summary>Copyright notice, or null if unset.</summary>
	public static string? Copyright {
		get {
			var copyright = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
			if (string.IsNullOrEmpty(copyright)) {
				return null;
			}
			return copyright;
		}
	}

	/// <summary>A one-line banner of the form "<c>name version  Copyright ...</c>". Omits the copyright when unset.</summary>
	public static string Banner {
		get {
			var copyright = Copyright;
			if (copyright == null) {
				return $"{Name} {Version}";
			}
			return $"{Name} {Version}  {copyright}";
		}
	}
}
