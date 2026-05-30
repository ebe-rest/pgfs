namespace Pgfs.Lib.Models;

/// <summary>
/// A Dapper base class that factors out the audit columns (id / created_at / created_by / updated_at / updated_by).
/// <see cref="Inode"/> / <see cref="Data"/> / <see cref="Chunk"/> inherit it to represent one row of each table.
/// </summary>
public abstract class Base
{
	public long id { get; set; }
	public DateTime created_at { get; set; }
	public string created_by { get; set; } = "";
	public DateTime updated_at { get; set; }
	public string updated_by { get; set; } = "";

	public long Id { get { return this.id; } set { this.id = value; } }
	public DateTime CreatedAt { get { return this.created_at; } set { this.created_at = value; } }
	public string CreatedBy { get { return this.created_by; } set { this.created_by = value; } }
	public DateTime UpdatedAt { get { return this.updated_at; } set { this.updated_at = value; } }
	public string UpdatedBy { get { return this.updated_by; } set { this.updated_by = value; } }
}
