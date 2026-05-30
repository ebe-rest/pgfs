namespace Pgfs.Lib.Models;

/// <summary>
/// The model for one chunk that makes up a file body.
/// One row of the table <c>{prefix}data_chunk</c>.
/// The body is stored as bytea (see <see href="../../../../docs/support_for_citus.md"/>).
/// </summary>
public class Chunk : Base
{
	public long data_id { get; set; }
	public int chunk_index { get; set; }
	public byte[] payload { get; set; } = [];

	public long DataId { get { return this.data_id; } set { this.data_id = value; } }
	public int ChunkIndex { get { return this.chunk_index; } set { this.chunk_index = value; } }
	public byte[] Payload { get { return this.payload; } set { this.payload = value; } }

	public Data? Data { get; set; }
}
