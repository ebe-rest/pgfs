namespace Pgfs.Core.Config;

/// <summary>
/// How a live reload treats this setting while the process is running. Declared once per
/// <see cref="Field"/> (docs/runtime-control-plane.md).
/// </summary>
public enum ReloadPolicy
{
	/// <summary>Can be re-applied immediately while running (logging.level/output, cache limits, retry, statfs mode, audit, ...).</summary>
	Live,
	/// <summary>Takes effect on the next mount (connection string, mount point, FUSE flags, fallback names, ...). The default.</summary>
	NextMount,
	/// <summary>Frozen at mkfs time and immutable afterwards (file_system.* = version / cluster_size / chunk_size / max_file_size, ...).</summary>
	Format,
}
