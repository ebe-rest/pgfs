namespace Pgfs.Fuse;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Core.Logging;
using Core.Utility;

/// <summary>
/// Calls the Linux/macOS <c>libc</c> getpwnam_r/getpwuid_r/getgrnam_r/getgrgid_r through P/Invoke to convert
/// between a user name and a uid, and between a group name and a gid. The results are cached in memory.
///
/// **Always use the <c>_r</c> versions.** glibc states plainly that the non-<c>_r</c> versions
/// (<c>getpwnam</c> and friends) are MT-Unsafe (the returned pointer points into **a static buffer shared
/// by the process**). This class is called from FUSE's multi-threaded paths, and
/// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> **does not serialize the factory**, so when the
/// first resolutions of two different keys run at the same time it **can cache one user's information
/// under another**. The <c>_r</c> versions use the caller's buffer, so that race cannot happen in principle.
///
/// This class is **specific to Linux/macOS**. Do not call it on Windows.
/// The Windows side is to get a separate implementation that carries the same responsibility through
/// <c>WindowsIdentity</c> / <c>NTAccount</c> (handled on the Pgfs.Assign side).
///
/// A name this host does not have (a uname / gname in the DB) is shown as **the kernel's overflowuid / overflowgid** (the value
/// "an id without a mapping is shown as"; <c>/proc/sys/kernel/overflowuid</c>, usually 65534 = nobody). There is no setting for it (mount.fallback_* was removed in v0.2.1).
/// A uid / gid without a name (a container's uid, for example) is written to the DB as <c>file_system.unknown_name</c> (default <c>(unknown)</c>).
/// </summary>
internal sealed class UserResolver
{
	/// <summary>The value used when <c>/proc/sys/kernel/overflow*</c> cannot be read (65534, the same as the kernel default).</summary>
	private const uint DefaultOverflowId = 65534;

	private readonly uint overflowUid;
	private readonly uint overflowGid;
	private readonly string unknownName;

	// Our own name (mount.self_uname / self_gname) -> the process's own uid / gid. Even a name this host does not have is shown as our own file.
	private readonly string selfUname;
	private readonly string selfGname;
	private readonly uint selfUid;
	private readonly uint selfGid;

	private readonly ConcurrentDictionary<string, uint> unameToUid = new();
	private readonly ConcurrentDictionary<string, uint> gnameToGid = new();
	private readonly ConcurrentDictionary<uint, string> uidToUname = new();
	private readonly ConcurrentDictionary<uint, string> gidToGname = new();

	/// <param name="unknownName">The name written to the DB for a uid / gid without a name (<c>file_system.unknown_name</c>).</param>
	/// <param name="selfUname">Our own name (not used when empty). This name is shown as <paramref name="selfUid"/>.</param>
	/// <param name="selfGname">Our own group name (not used when empty). This name is shown as <paramref name="selfGid"/>.</param>
	public UserResolver(string unknownName, string selfUname = "", string selfGname = "", uint selfUid = 0, uint selfGid = 0) {
		this.unknownName = unknownName;
		this.selfUname = NameNormalizer.Normalize(selfUname ?? "");
		this.selfGname = NameNormalizer.Normalize(selfGname ?? "");
		this.selfUid = selfUid;
		this.selfGid = selfGid;
		this.overflowUid = ReadOverflowId("/proc/sys/kernel/overflowuid");
		this.overflowGid = ReadOverflowId("/proc/sys/kernel/overflowgid");
	}

	private static uint ReadOverflowId(string path) {
		try {
			if (uint.TryParse(File.ReadAllText(path).Trim(), out var id)) {
				return id;
			}
		} catch (Exception ex) {
			Logger.Warning("UserResolver: cannot read ", path, " (", ex.Message, "), using ", DefaultOverflowId);
		}
		return DefaultOverflowId;
	}

	private static uint? ResolveUidNow(string uname) {
		if (!TryPasswd((ref passwd pw, nint buf, nuint len, out nint result) => getpwnam_r(uname, ref pw, buf, len, out result), p => p.pw_uid, $"getpwnam_r('{uname}')", out var uid)) {
			return null;
		}
		return uid;
	}

	private static uint? ResolveGidNow(string gname) {
		if (!TryGroup((ref group gr, nint buf, nuint len, out nint result) => getgrnam_r(gname, ref gr, buf, len, out result), g => g.gr_gid, $"getgrnam_r('{gname}')", out var gid)) {
			return null;
		}
		return gid;
	}

	/// <summary>uname -&gt; uid. The input name is normalized first. overflowuid for a name this host does not have.</summary>
	public uint UidOf(string uname) {
		var key = NameNormalizer.Normalize(uname);
		if (this.selfUname.Length > 0 && key == this.selfUname) {
			return this.selfUid;
		}
		return this.unameToUid.GetOrAdd(key, n => ResolveUidNow(n) ?? this.overflowUid);
	}

	/// <summary>gname -&gt; gid. The input name is normalized first. overflowgid for a name this host does not have.</summary>
	public uint GidOf(string gname) {
		var key = NameNormalizer.Normalize(gname);
		if (this.selfGname.Length > 0 && key == this.selfGname) {
			return this.selfGid;
		}
		return this.gnameToGid.GetOrAdd(key, n => ResolveGidNow(n) ?? this.overflowGid);
	}

	/// <summary>uid -&gt; uname. The OS-derived name is normalized before returning. unknown_name for a uid without a name.</summary>
	public string UnameOf(uint uid) {
		return this.uidToUname.GetOrAdd(uid, id => {
			// Copy the strings **while the buffer is still alive** (finish it inside pick).
			if (!TryPasswd((ref passwd pw, nint buf, nuint len, out nint result) => getpwuid_r(id, ref pw, buf, len, out result), p => Marshal.PtrToStringUTF8(p.pw_name), $"getpwuid_r({id})", out var name)) {
				return this.unknownName;
			}
			return NameNormalizer.Normalize(name ?? this.unknownName);
		});
	}

	/// <summary>gid -&gt; gname. The OS-derived name is normalized before returning. unknown_name for a gid without a name.</summary>
	public string GnameOf(uint gid) {
		return this.gidToGname.GetOrAdd(gid, id => {
			if (!TryGroup((ref group gr, nint buf, nuint len, out nint result) => getgrgid_r(id, ref gr, buf, len, out result), g => Marshal.PtrToStringUTF8(g.gr_name), $"getgrgid_r({id})", out var name)) {
				return this.unknownName;
			}
			return NameNormalizer.Normalize(name ?? this.unknownName);
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

	[DllImport("libc", SetLastError = false)]
	private static extern int getpwnam_r([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref passwd pwd, nint buf, nuint buflen, out nint result);

	[DllImport("libc", SetLastError = false)]
	private static extern int getpwuid_r(uint uid, ref passwd pwd, nint buf, nuint buflen, out nint result);

	[DllImport("libc", SetLastError = false)]
	private static extern int getgrnam_r([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref group grp, nint buf, nuint buflen, out nint result);

	[DllImport("libc", SetLastError = false)]
	private static extern int getgrgid_r(uint gid, ref group grp, nint buf, nuint buflen, out nint result);
#pragma warning restore CS8981

	// ------------------------------------------------------------------
	// Helpers for calling the _r versions (allocating the buffer + growing it on ERANGE)
	// ------------------------------------------------------------------

	/// <summary>The buffer length allocated on the first attempt. Matched to the typical value of `sysconf(_SC_GETPW_R_SIZE_MAX)`.</summary>
	private const int InitialBufferBytes = 1024;
	/// <summary>
	/// The upper bound for growing the buffer. If it is still ERANGE after growing this far, it gives up and
	/// falls back (a broken NSS implementation, for example. **Growing it without limit would take the whole
	/// mount down with an OOM**, hence the bound).
	/// </summary>
	private const int MaxBufferBytes = 256 * 1024;
	/// <summary>`ERANGE` (the buffer is too small). The Linux value.</summary>
	private const int Erange = 34;

	private delegate int PasswdLookup(ref passwd pwd, nint buf, nuint buflen, out nint result);
	private delegate int GroupLookup(ref group grp, nint buf, nuint buflen, out nint result);

	/// <summary>
	/// Calls <c>getpw*_r</c> and takes the values it needs out with <paramref name="pick"/> **while the buffer
	/// is still alive**.
	/// Returns false when it is not found (<c>result == NULL</c>) or on an error. ERANGE doubles the buffer and retries.
	/// </summary>
	private static bool TryPasswd<T>(PasswdLookup call, System.Func<passwd, T> pick, string what, out T value) {
		value = default!;
		var len = InitialBufferBytes;
		while (true) {
			var buf = Marshal.AllocHGlobal(len);
			try {
				var pw = default(passwd);
				var rc = call(ref pw, buf, (nuint)len, out var result);
				if (rc == Erange && len < MaxBufferBytes) {
					len *= 2;
					continue;
				}
				if (rc != 0) {
					Logger.Warning("UserResolver: ", what, " returned errno ", rc, " (using the fallback)");
					return false;
				}
				// result == NULL with rc == 0 means "there is no such user / group" = a normal negative answer.
				if (result == nint.Zero) { return false; }
				value = pick(pw);
				return true;
			} finally {
				Marshal.FreeHGlobal(buf);
			}
		}
	}

	/// <summary>The group version of <see cref="TryPasswd"/>.</summary>
	private static bool TryGroup<T>(GroupLookup call, System.Func<group, T> pick, string what, out T value) {
		value = default!;
		var len = InitialBufferBytes;
		while (true) {
			var buf = Marshal.AllocHGlobal(len);
			try {
				var gr = default(group);
				var rc = call(ref gr, buf, (nuint)len, out var result);
				if (rc == Erange && len < MaxBufferBytes) {
					len *= 2;
					continue;
				}
				if (rc != 0) {
					Logger.Warning("UserResolver: ", what, " returned errno ", rc, " (using the fallback)");
					return false;
				}
				if (result == nint.Zero) { return false; }
				value = pick(gr);
				return true;
			} finally {
				Marshal.FreeHGlobal(buf);
			}
		}
	}
}
