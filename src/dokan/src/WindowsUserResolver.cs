namespace Pgfs.Dokan;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Core.Logging;
using Core.Utility;

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
/// <item>When a name cannot be resolved to a SID on this host = it is shown as the well-known <c>ANONYMOUS LOGON</c> SID (no setting; mount.fallback_* was removed in v0.2.1).</item>
/// <item>When a SID cannot be resolved to a name = <c>file_system.unknown_name</c> (default <c>(unknown)</c>) is written to the DB.</item>
/// <item><c>mount.self_uname</c> / <c>self_gname</c> = the name given to **the process's own SID** (a supplement). Not used for any other SID.</item>
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

	// The SID shown when a name cannot be resolved to a SID (the well-known ANONYMOUS LOGON), and the name written to the DB when a SID cannot be resolved to a name.
	private static readonly SecurityIdentifier UnresolvedSid = new(WellKnownSidType.AnonymousSid, null);
	private readonly string unknownName;

	// Our own name (a supplement). Applied only to the process's own SID / its primary group's SID. Not used when empty.
	private readonly SecurityIdentifier? processSid;
	private readonly SecurityIdentifier? processGroupSid;
	private readonly string selfUname;
	private readonly string selfGname;

	private readonly ConcurrentDictionary<string, SecurityIdentifier> unameToSid = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, SecurityIdentifier> gnameToSid = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, string> sidToUname = new();
	private readonly ConcurrentDictionary<string, string> sidToGname = new();

	/// <param name="unknownName">The name written to the DB when a SID cannot be resolved to a name (<c>file_system.unknown_name</c>).</param>
	/// <param name="selfUname">Our own name (<c>mount.self_uname</c>). The OS name when empty.</param>
	/// <param name="selfGname">Our own group name (<c>mount.self_gname</c>). The usual rule when empty.</param>
	public WindowsUserResolver(string unknownName, string selfUname, string selfGname) {
		this.unknownName = unknownName;
		this.selfUname = NameNormalizer.Normalize(selfUname ?? "");
		this.selfGname = NameNormalizer.Normalize(selfGname ?? "");

		// Owner of new inodes: the running process's user / primary group.
		// Normalize + map Windows -> Linux to produce the stored (Linux) name.
		var current = WindowsIdentity.GetCurrent();
		this.processSid = current.User;
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
		this.processGroupSid = firstGroup;
		this.processGname = this.GnameOf(firstGroup ?? UnresolvedSid);
		if (this.selfUname.Length > 0) {
			this.processUname = this.selfUname;
		}
		if (this.selfGname.Length > 0) {
			this.processGname = this.selfGname;
		}
	}

	public string DefaultUname => this.processUname;
	public string DefaultGname => this.processGname;

	/// <summary>
	/// The name written to the DB when the creator's name is not known (<c>file_system.unknown_name</c>).
	/// **A new inode's owner also falls back to this when it could not be decided from the requester** -
	/// passing it off as the running process's user would create the accident of "a file someone else created
	/// looks like mine".
	/// </summary>
	public string UnknownName => this.unknownName;

	/// <summary>Whether our own group name (self_gname) should be applied to the owner (= whether it was created as ourselves).</summary>
	public bool HasSelfGname => this.selfGname.Length > 0;

	/// <summary>
	/// Gets the SID from a name. Our own name (self_uname) gives the process's SID. ANONYMOUS LOGON when it cannot be resolved.
	/// </summary>
	public SecurityIdentifier UserSidOf(string uname) {
		var pgfs = NameNormalizer.Normalize(uname);
		return this.unameToSid.GetOrAdd(pgfs, n => {
			if (this.selfUname.Length > 0 && n == this.selfUname && this.processSid != null) {
				return this.processSid;
			}
			return TryTranslateNameToSid(MapPgfsUserToWin(n)) ?? UnresolvedSid;
		});
	}

	public SecurityIdentifier GroupSidOf(string gname) {
		var pgfs = NameNormalizer.Normalize(gname);
		return this.gnameToSid.GetOrAdd(pgfs, n => {
			if (this.selfGname.Length > 0 && n == this.selfGname && this.processGroupSid != null) {
				return this.processGroupSid;
			}
			return TryTranslateNameToSid(MapPgfsGroupToWin(n)) ?? UnresolvedSid;
		});
	}

	/// <summary>Gets a user name from a SID. Normalizes + maps Windows -> Linux to the stored (Linux) name. **For ourselves (the process's SID), self_uname when set.** unknown_name if unresolved.</summary>
	public string UnameOf(SecurityIdentifier sid) {
		if (this.selfUname.Length > 0 && this.processSid != null && sid == this.processSid) {
			return this.selfUname;
		}
		var key = sid.Value;
		return this.sidToUname.GetOrAdd(key, _ => {
			var raw = TryTranslateSidToName(sid);
			if (raw == null) {
				return this.unknownName;
			}
			return MapWinUserToPgfs(NameNormalizer.Normalize(raw));
		});
	}

	public string GnameOf(SecurityIdentifier sid) {
		var key = sid.Value;
		return this.sidToGname.GetOrAdd(key, _ => {
			var raw = TryTranslateSidToName(sid);
			if (raw == null) {
				return this.unknownName;
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
