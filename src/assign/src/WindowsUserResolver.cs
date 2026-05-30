namespace Pgfs.Assign;

using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Principal;
using Lib.Logging;

/// <summary>
/// Resolver that converts between user name &lt;-&gt; SID/uid on Windows.
/// A Windows-specific implementation using <see cref="NTAccount"/> and <see cref="SecurityIdentifier"/>.
///
/// Same role as the Linux-side <see cref="Pgfs.Mount.UserResolver"/>, but a SID does not fit in a 32-bit
/// integer, so to produce the uid (uint32) Dokan needs, this provisionally uses the SID's RID (the
/// trailing 32 bits). For now names are the source of truth and SIDs are cached per request.
///
/// pgfs_inode's uname/gname are OS-independent. Mappings like Linux root -&gt; Windows Administrator are
/// handled by <c>IsWritable</c> in [src/assign/src/FileSystemUtils.cs].
///
/// The roles are clearly separated:
/// <list type="bullet">
/// <item><c>DefaultUname</c> / <c>DefaultGname</c> = **the running process's user / primary group** (used as the owner of new inodes).</item>
/// <item><c>fallbackUserSid</c> / <c>fallbackGroupSid</c> etc. = the SID resolved from **mount.fallback_uname / fallback_gname**
///       (returned when name -&gt; SID or SID -&gt; name translation fails).</item>
/// </list>
/// Unless the owner of new files is "the running process itself" and the fallback for unknown names is
/// "Guest-equivalent", files created by others would appear to be "yours" — a security incident.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserResolver
{
	// The running process's id and name (used as the owner of new inodes).
	private readonly string processUname;
	private readonly string processGname;

	// Fallback for when name -> SID or SID -> name resolution fails (derived from mount.fallback_*).
	private readonly string fallbackUname;
	private readonly string fallbackGname;
	private readonly SecurityIdentifier fallbackUserSid;
	private readonly SecurityIdentifier fallbackGroupSid;

	private readonly ConcurrentDictionary<string, SecurityIdentifier> unameToSid = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, SecurityIdentifier> gnameToSid = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, string> sidToUname = new();
	private readonly ConcurrentDictionary<string, string> sidToGname = new();

	public WindowsUserResolver(string fallbackUname, string fallbackGname) {
		this.fallbackUname = fallbackUname;
		this.fallbackGname = fallbackGname;
		// Resolve a SID from the fallback name. If that fails, fall back hard to AnonymousSid.
		this.fallbackUserSid = TryTranslateNameToSid(fallbackUname)
			?? FallbackUserSidWithWarning(fallbackUname);
		this.fallbackGroupSid = TryTranslateNameToSid(fallbackGname)
			?? FallbackGroupSidWithWarning(fallbackGname);

		// Owner of new inodes: the running process's user / primary group.
		var current = WindowsIdentity.GetCurrent();
		var name = current.Name;
		if (string.IsNullOrEmpty(name)) {
			name = Environment.UserName;
		}
		this.processUname = StripDomain(name);

		SecurityIdentifier? firstGroup = null;
		var groups = current.Groups;
		if (groups != null) {
			foreach (var g in groups) {
				firstGroup = g as SecurityIdentifier;
				if (firstGroup != null) {
					break;
				}
			}
		}
		var processGroupSid = firstGroup ?? this.fallbackGroupSid;
		this.processGname = this.GnameOf(processGroupSid);
	}

	public string DefaultUname => this.processUname;
	public string DefaultGname => this.processGname;

	/// <summary>
	/// Gets a SID from a name. Falls back to the fallback (the SID of mount.fallback_uname) if unresolved.
	/// </summary>
	public SecurityIdentifier UserSidOf(string uname) {
		return this.unameToSid.GetOrAdd(uname, n => {
			return TryTranslateNameToSid(n) ?? this.fallbackUserSid;
		});
	}

	public SecurityIdentifier GroupSidOf(string gname) {
		return this.gnameToSid.GetOrAdd(gname, n => {
			return TryTranslateNameToSid(n) ?? this.fallbackGroupSid;
		});
	}

	/// <summary>Gets a user name from a SID. Falls back to the fallback uname if unresolved.</summary>
	public string UnameOf(SecurityIdentifier sid) {
		var key = sid.Value;
		return this.sidToUname.GetOrAdd(key, _ => {
			return TryTranslateSidToName(sid) ?? this.fallbackUname;
		});
	}

	public string GnameOf(SecurityIdentifier sid) {
		var key = sid.Value;
		return this.sidToGname.GetOrAdd(key, _ => {
			return TryTranslateSidToName(sid) ?? this.fallbackGname;
		});
	}

	private static SecurityIdentifier? TryTranslateNameToSid(string name) {
		try {
			var account = new NTAccount(name);
			return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
		} catch {
			return null;
		}
	}

	private static string? TryTranslateSidToName(SecurityIdentifier sid) {
		try {
			var account = (NTAccount)sid.Translate(typeof(NTAccount));
			return StripDomain(account.Value);
		} catch {
			return null;
		}
	}

	private static SecurityIdentifier FallbackUserSidWithWarning(string uname) {
		Logger.Warning("WindowsUserResolver: fallback uname '", uname, "' cannot be translated to a SID, using AnonymousSid");
		return new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
	}

	private static SecurityIdentifier FallbackGroupSidWithWarning(string gname) {
		Logger.Warning("WindowsUserResolver: fallback gname '", gname, "' cannot be translated to a SID, using AnonymousSid");
		return new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
	}

	/// <summary>
	/// Extracts just `name` from `DOMAIN\name` form, to make it easier to pass to downstream handling such
	/// as `Administrators`.
	/// </summary>
	// Windows-shareable concept: stripping a domain prefix from a name is a similar idea on both OSes, but
	// the separator differs (Linux uses `:`, Windows uses `\`), so each OS keeps its own.
	private static string StripDomain(string name) {
		var idx = name.IndexOf('\\');
		if (idx < 0) {
			return name;
		}
		return name[(idx + 1) ..];
	}
}
