namespace Pgfs.Assign;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Lib.Logging;
using Lib.Utility;

/// <summary>
/// Resolver that converts between user name &lt;-&gt; SID/uid on Windows.
/// A Windows-specific implementation using <see cref="NTAccount"/> and <see cref="SecurityIdentifier"/>.
///
/// Same role as the Linux-side <see cref="Pgfs.Mount.UserResolver"/>, but a SID does not fit in a 32-bit
/// integer, so to produce the uid (uint32) Dokan needs, this provisionally uses the SID's RID (the
/// trailing 32 bits). For now names are the source of truth and SIDs are cached per request.
///
/// pgfs_inode's uname/gname are OS-independent (Linux names). Well-known mappings like root &lt;-&gt; Administrator(s),
/// nobody/nogroup &lt;-&gt; ANONYMOUS LOGON and other &lt;-&gt; Everyone are handled bidirectionally by the Map* helpers below.
///
/// The roles are clearly separated:
/// <list type="bullet">
/// <item><c>DefaultUname</c> / <c>DefaultGname</c> = **the running process's user / primary group** (used as the owner of new inodes).</item>
/// <item><c>fallbackUserSid</c> / <c>fallbackGroupSid</c> etc. = the SID resolved from **mount.fallback_uname / fallback_gname**
///       (returned when name -&gt; SID or SID -&gt; name translation fails).</item>
/// </list>
/// Unless the owner of new files is "the running process itself" and the fallback for unknown names is the
/// anonymous principal, files created by others would appear to be "yours" — a security incident.
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
		// Normalize the fallback names (to Linux names). When resolving a SID, map to the Windows name first
		// (docs/permission-interop.md).
		this.fallbackUname = NameNormalizer.Normalize(fallbackUname);
		this.fallbackGname = NameNormalizer.Normalize(fallbackGname);
		// Resolve a SID from the fallback name. If that fails, fall back hard to AnonymousSid.
		this.fallbackUserSid = TryTranslateNameToSid(MapPgfsUserToWin(this.fallbackUname))
			?? FallbackUserSidWithWarning(this.fallbackUname);
		this.fallbackGroupSid = TryTranslateNameToSid(MapPgfsGroupToWin(this.fallbackGname))
			?? FallbackGroupSidWithWarning(this.fallbackGname);

		// Owner of new inodes: the running process's user / primary group.
		// Normalize + map Windows -> Linux to produce the stored (Linux) name.
		var current = WindowsIdentity.GetCurrent();
		var name = current.Name;
		if (string.IsNullOrEmpty(name)) {
			name = Environment.UserName;
		}
		this.processUname = MapWinUserToPgfs(NameNormalizer.Normalize(name));

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
		var pgfs = NameNormalizer.Normalize(uname);
		return this.unameToSid.GetOrAdd(pgfs, n => {
			return TryTranslateNameToSid(MapPgfsUserToWin(n)) ?? this.fallbackUserSid;
		});
	}

	public SecurityIdentifier GroupSidOf(string gname) {
		var pgfs = NameNormalizer.Normalize(gname);
		return this.gnameToSid.GetOrAdd(pgfs, n => {
			return TryTranslateNameToSid(MapPgfsGroupToWin(n)) ?? this.fallbackGroupSid;
		});
	}

	/// <summary>Gets a user name from a SID. Normalizes + maps Windows -> Linux to the stored (Linux) name. Falls back to the fallback uname if unresolved.</summary>
	public string UnameOf(SecurityIdentifier sid) {
		var key = sid.Value;
		return this.sidToUname.GetOrAdd(key, _ => {
			var raw = TryTranslateSidToName(sid);
			if (raw == null) {
				return this.fallbackUname;
			}
			return MapWinUserToPgfs(NameNormalizer.Normalize(raw));
		});
	}

	public string GnameOf(SecurityIdentifier sid) {
		var key = sid.Value;
		return this.sidToGname.GetOrAdd(key, _ => {
			var raw = TryTranslateSidToName(sid);
			if (raw == null) {
				return this.fallbackGname;
			}
			return MapWinGroupToPgfs(NameNormalizer.Normalize(raw));
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
			// Return the raw `DOMAIN\name`. Normalization (domain strip + lower-case + half-width) and Linux-name mapping happen at the caller.
			return account.Value;
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
	/// Whether a SID is a group (Group / Alias / WellKnownGroup), via the SID_NAME_USE returned by
	/// <c>LookupAccountSid</c>. Used to dispatch when a group is set as the owner (docs/permission-interop.md).
	/// Treated as a user if resolution fails.
	/// </summary>
	public bool IsGroupSid(SecurityIdentifier sid) {
		var bytes = new byte[sid.BinaryLength];
		sid.GetBinaryForm(bytes, 0);
		var name = new StringBuilder(256);
		var domain = new StringBuilder(256);
		var cchName = (uint)name.Capacity;
		var cchDomain = (uint)domain.Capacity;
		if (!LookupAccountSid(null, bytes, name, ref cchName, domain, ref cchDomain, out var use)) {
			return false;
		}
		// SID_NAME_USE: 1=User 2=Group 3=Domain 4=Alias 5=WellKnownGroup ... Group-like are 2 / 4 / 5.
		return use == 2 || use == 4 || use == 5;
	}

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool LookupAccountSid(
		string? systemName,
		byte[] sid,
		StringBuilder name,
		ref uint cchName,
		StringBuilder referencedDomainName,
		ref uint cchReferencedDomainName,
		out int peUse);

	// ------------------------------------------------------------------
	// Well-known principal mapping Linux <-> Windows (docs/permission-interop.md)
	// ------------------------------------------------------------------
	//
	// The DB stores Linux names, so both the storing direction (Windows -> Pgfs) and the rendering/resolving
	// direction (Pgfs -> Windows) are needed. The input is assumed to be already normalized (lower-case,
	// domain-stripped, half-width). nobody / nogroup correspond to Windows ANONYMOUS LOGON (S-1-5-7); there is
	// no dedicated anonymous group SID, so it is shared.

	private static string MapWinUserToPgfs(string n) {
		if (n == "administrator") { return "root"; }
		if (n == "anonymous logon") { return "nobody"; }
		return n;
	}

	private static string MapWinGroupToPgfs(string n) {
		if (n == "administrators") { return "root"; }
		if (n == "everyone") { return "other"; }
		if (n == "anonymous logon") { return "nogroup"; }
		return n;
	}

	private static string MapPgfsUserToWin(string n) {
		if (n == "root") { return "Administrator"; }
		if (n == "nobody") { return "NT AUTHORITY\\ANONYMOUS LOGON"; }
		return n;
	}

	private static string MapPgfsGroupToWin(string n) {
		if (n == "root") { return "Administrators"; }
		if (n == "nogroup") { return "NT AUTHORITY\\ANONYMOUS LOGON"; }
		if (n == "other") { return "Everyone"; }
		return n;
	}
}
