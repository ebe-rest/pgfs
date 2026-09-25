namespace Pgfs.Core.Config;

/// <summary>
/// The aggregate of settings referenced when mounting. A POCO. The values are filled by <see cref="ConfigLoader"/>
/// integrating CLI / TOML / DB / Default.
///
/// <para>
/// Properties are mutable (<c>{ get; set; }</c>) so that items which can change while mounted (e.g. volume_label, the kind
/// that may be rewritten via OS APIs in the future) can be handled. The rewrite + persistence pair is centralized in the thin
/// <c>Api.SetXxx(...)</c> wrappers, which call <see cref="ConfigStore.Save"/> inside.
/// </para>
/// </summary>
public sealed class MountConfig
{
	public string MountPoint { get; set; } = "";
	/// <summary>The maximum number of bytes in one FUSE WRITE request (0 = the libfuse default). Only effective on Linux (FUSE).</summary>
	public int MaxWrite { get; set; }
	public int CacheMaxEntries { get; set; }
	public long CacheDataMaxBytes { get; set; }
	/// <summary>The TTL (in milliseconds) of the negative lookup (ENOENT) cache. 0 disables it.</summary>
	public int NegativeCacheTtlMs { get; set; }
	/// <summary>Whether to enable the write-back cache (the default false is the traditional write-through).</summary>
	public bool WriteBack { get; set; }
	/// <summary>The cap on dirty bytes. A write that exceeds it waits until the flush is done (back-pressure).</summary>
	public long WriteBackMaxBytes { get; set; }
	/// <summary>How long dirty data may be left alone (in milliseconds). 0 disables the time trigger.</summary>
	public int WriteBackIntervalMs { get; set; }
	/// <summary>Whether metadata (create/mkdir/symlink and the attribute changes to them) is written back as well. <see cref="WriteBack"/> is a prerequisite.</summary>
	public bool WriteBackMetadata { get; set; }
	/// <summary>
	/// How a create with <c>O_EXCL</c> is treated (<c>write_through</c> / <c>defer</c>).
	/// <c>defer</c> loses cross-client exclusion. <see cref="WriteBackMetadata"/> is a prerequisite.
	/// </summary>
	public string WriteBackMetadataExclusiveCreate { get; set; } = "write_through";
	/// <summary>The cap on the number of pending inodes. A create that exceeds it waits until the flush is done (back-pressure).</summary>
	public int WriteBackMaxInodes { get; set; }
	/// <summary>The blocking limit for back-pressure / the flush deadline at unmount (in milliseconds). 0 = do not wait.</summary>
	public int WriteBackFlushTimeoutMs { get; set; }
	/// <summary>The name this client presents for itself (a supplement). Empty = the OS name (<see cref="Schema.Mount.SelfUname"/>).</summary>
	public string SelfUname { get; set; } = "";
	/// <summary>The group version of the self-presented name (a supplement). Empty = the usual rules.</summary>
	public string SelfGname { get; set; } = "";
	public bool Foreground { get; set; }

	/// <summary>
	/// The FUSE flags received via `-o key=val,flag,...` (`allow_other`, `default_permissions`, `ro`, `rw`,
	/// `nonempty`, `auto_unmount`, etc.). <see cref="ConfigLoader"/> accumulates them in `ParseDashOOptions`, and
	/// mount.pgfs wires them into <c>Pgfs.Fuse.MountOptions.Options</c>. Not placed in Schema and not managed by a Field
	/// (because it is an arbitrary key set).
	/// </summary>
	public System.Collections.Generic.List<string> FuseFlags { get; set; } = new();
}
