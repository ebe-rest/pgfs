namespace Pgfs.Lib.Config;

/// <summary>
/// Settings for the filesystem itself. Many items are frozen at mkfs time (= rewriting them later is not reflected in the
/// DB-side true value, e.g. <c>pgfs_data.chunk_size</c>).
///
/// <para>
/// <see cref="Version"/> / <see cref="ClusterSize"/> are not referenced by the current code paths (they are only saved at
/// mkfs time). They are kept as placeholders for future compatibility checks or for <c>statfs.f_bsize</c> display.
/// The values are filled by <see cref="ConfigLoader"/> integrating CLI / TOML / DB / Default.
/// </para>
/// </summary>
public sealed class FileSystemConfig
{
	public string Version { get; set; } = "";
	public string VolumeLabel { get; set; } = "";
	public long ClusterSize { get; set; }
	public long DefaultChunkSize { get; set; }
	public long MaxFileSize { get; set; }
}
