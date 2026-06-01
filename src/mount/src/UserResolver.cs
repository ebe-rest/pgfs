namespace Pgfs.Mount;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lib.Logging;
using Lib.Utility;

/// <summary>
/// P/Invokes the Linux/macOS <c>libc</c> getpwnam/getpwuid/getgrnam/getgrgid to convert between
/// user name &lt;-&gt; uid and group name &lt;-&gt; gid. Results are cached in memory.
///
/// This class is **Linux/macOS-specific**. Do not call it on Windows.
/// The Windows side has a separate implementation handling the same role via
/// <c>WindowsIdentity</c> / <c>NTAccount</c> (in Pgfs.Assign).
///
/// Unknown user names / uids are recorded in the cache as the "fallback (defaultUid/Gid)".
/// The fallback names (mount.fallback_uname / fallback_gname) are resolved and cached at startup.
/// If a fallback name itself cannot be resolved, the NFS convention `65534` (nobody/nogroup) is
/// hardcoded and a warning is emitted.
/// </summary>
internal sealed class UserResolver
{
	/// <summary>The final hardcoded uid/gid used when a fallback name itself cannot be resolved (the conventional NFS `nobody` value).</summary>
	private const uint HardcodedNobodyUid = 65534;
	private const uint HardcodedNogroupGid = 65534;

	private readonly uint defaultUid;
	private readonly uint defaultGid;
	private readonly string defaultUname;
	private readonly string defaultGname;

	private readonly ConcurrentDictionary<string, uint> unameToUid = new();
	private readonly ConcurrentDictionary<string, uint> gnameToGid = new();
	private readonly ConcurrentDictionary<uint, string> uidToUname = new();
	private readonly ConcurrentDictionary<uint, string> gidToGname = new();

	public UserResolver(string fallbackUname, string fallbackGname) {
		// Name normalization (docs/permission-interop.md). The fallback names are normalized too, for both storage and resolution.
		this.defaultUname = NameNormalizer.Normalize(fallbackUname);
		this.defaultGname = NameNormalizer.Normalize(fallbackGname);
		// Resolve uid / gid from the fallback names once at startup and pin them, so getpwnam is not called on every request.
		this.defaultUid = ResolveUidNow(this.defaultUname) ?? FallbackUidWithWarning(this.defaultUname);
		this.defaultGid = ResolveGidNow(this.defaultGname) ?? FallbackGidWithWarning(this.defaultGname);
	}

	private static uint FallbackUidWithWarning(string uname) {
		Logger.Warning("UserResolver: fallback uname '", uname, "' cannot be resolved by getpwnam, using hardcoded uid ", HardcodedNobodyUid);
		return HardcodedNobodyUid;
	}

	private static uint FallbackGidWithWarning(string gname) {
		Logger.Warning("UserResolver: fallback gname '", gname, "' cannot be resolved by getgrnam, using hardcoded gid ", HardcodedNogroupGid);
		return HardcodedNogroupGid;
	}

	private static uint? ResolveUidNow(string uname) {
		var ptr = getpwnam(uname);
		if (ptr == nint.Zero) {
			return null;
		}
		var pw = Marshal.PtrToStructure<passwd>(ptr);
		return pw.pw_uid;
	}

	private static uint? ResolveGidNow(string gname) {
		var ptr = getgrnam(gname);
		if (ptr == nint.Zero) {
			return null;
		}
		var gr = Marshal.PtrToStructure<group>(ptr);
		return gr.gr_gid;
	}

	/// <summary>uname -&gt; uid. The input name is normalized first. Falls back to defaultUid (the fallback uname's uid) if unresolved.</summary>
	public uint UidOf(string uname) {
		var key = NameNormalizer.Normalize(uname);
		return this.unameToUid.GetOrAdd(key, n => ResolveUidNow(n) ?? this.defaultUid);
	}

	/// <summary>gname -&gt; gid. The input name is normalized first. Falls back to defaultGid (the fallback gname's gid) if unresolved.</summary>
	public uint GidOf(string gname) {
		var key = NameNormalizer.Normalize(gname);
		return this.gnameToGid.GetOrAdd(key, n => ResolveGidNow(n) ?? this.defaultGid);
	}

	/// <summary>uid -&gt; uname. The OS-derived name is normalized before returning. Falls back to defaultUname if unresolved.</summary>
	public string UnameOf(uint uid) {
		return this.uidToUname.GetOrAdd(uid, id => {
			var ptr = getpwuid(id);
			if (ptr == nint.Zero) {
				return this.defaultUname;
			}
			var pw = Marshal.PtrToStructure<passwd>(ptr);
			var s = Marshal.PtrToStringUTF8(pw.pw_name) ?? this.defaultUname;
			return NameNormalizer.Normalize(s);
		});
	}

	/// <summary>gid -&gt; gname. The OS-derived name is normalized before returning. Falls back to defaultGname if unresolved.</summary>
	public string GnameOf(uint gid) {
		return this.gidToGname.GetOrAdd(gid, id => {
			var ptr = getgrgid(id);
			if (ptr == nint.Zero) {
				return this.defaultGname;
			}
			var gr = Marshal.PtrToStructure<group>(ptr);
			var s = Marshal.PtrToStringUTF8(gr.gr_name) ?? this.defaultGname;
			return NameNormalizer.Normalize(s);
		});
	}

	// ------------------------------------------------------------------
	// libc P/Invoke section (Linux-specific)
	// ------------------------------------------------------------------
	//
	// On both glibc and musl the `passwd` struct layout agrees:
	//   char *pw_name;
	//   char *pw_passwd;
	//   uid_t pw_uid;
	//   gid_t pw_gid;
	//   char *pw_gecos;
	//   char *pw_dir;
	//   char *pw_shell;
	// macOS adds pw_change / pw_class / pw_expire, but we only need pw_name and pw_uid, so reading the
	// first 16 bytes is enough.
	//
	// The group struct is similar:
	//   char *gr_name;
	//   char *gr_passwd;
	//   gid_t gr_gid;
	//   char **gr_mem;
	// and we only use the leading members.

#pragma warning disable CS8981 // Use lowercase type names to match the C convention.
	[StructLayout(LayoutKind.Sequential)]
	private struct passwd
	{
		public nint pw_name;
		public nint pw_passwd;
		public uint pw_uid;
		public uint pw_gid;
		public nint pw_gecos;
		public nint pw_dir;
		public nint pw_shell;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct group
	{
		public nint gr_name;
		public nint gr_passwd;
		public uint gr_gid;
		public nint gr_mem;
	}

	[DllImport("libc", CharSet = CharSet.Ansi, SetLastError = false)]
	private static extern nint getpwnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport("libc", CharSet = CharSet.Ansi, SetLastError = false)]
	private static extern nint getpwuid(uint uid);

	[DllImport("libc", CharSet = CharSet.Ansi, SetLastError = false)]
	private static extern nint getgrnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport("libc", CharSet = CharSet.Ansi, SetLastError = false)]
	private static extern nint getgrgid(uint gid);
#pragma warning restore CS8981
}
