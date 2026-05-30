namespace Pgfs.Lib.Models;

public class Data : Base
{
	public int chunk_size { get; set; }
	public long total_size { get; set; }

	public int ChunkSize { get { return this.chunk_size; } set { this.chunk_size = value; } }
	public long TotalSize { get { return this.total_size; } set { this.total_size = value; } }
}
