namespace Pgfs.Lib.Models;

using Collections;

/**
 * Does not hold the full path.
 */
public class Inode : Base
{
	public long parent_id { get; set; }
	public string name { get; set; } = "";
	public string uname { get; set; } = "";
	public string gname { get; set; } = "";
	public int st_mode { get; set; }
	public int st_nlink { get; set; }
	public long st_size { get; set; }
	public DateTime st_mtime { get; set; }
	public DateTime st_ctime { get; set; }
	public string? link_target { get; set; }
	public bool is_junction { get; set; }
	public long? data_id { get; set; }
	public string xattrs { get; set; } = "";

	public long ParentId { get { return this.parent_id; } set { this.parent_id = value; } }
	public string Name { get { return this.name; } set { this.name = value; } }
	public string UserName { get { return this.uname; } set { this.uname = value; } }
	public string GroupName { get { return this.gname; } set { this.gname = value; } }
	public int Mode { get { return this.st_mode; } set { this.st_mode = value; } }
	public int NLink { get { return this.st_nlink; } set { this.st_nlink = value; } }
	public long Size { get { return this.st_size; } set { this.st_size = value; } }
	public DateTime Mtime { get { return this.st_mtime; } set { this.st_mtime = value; } }
	public DateTime Ctime { get { return this.st_ctime; } set { this.st_ctime = value; } }
	public string? LinkTarget { get { return this.link_target; } set { this.link_target = value; } }
	public bool IsJunction { get { return this.is_junction; } set { this.is_junction = value; } }
	public long? DataId { get { return this.data_id; } set { this.data_id = value; } }
	public string Xattrs { get { return this.xattrs; } set { this.xattrs = value; } }

	public bool IsDirectory { get { return Lib.Api.Mode.IsDirectory(this.Mode); } }
	public bool IsFile { get { return Lib.Api.Mode.IsFile(this.Mode); } }

	public Inode? Parent { get; set; }
	public FirstList<Inode> Children { get; set; } = new();
	public Data? Data { get; set; }

	public DateTime CacheTime { get; set; } = DateTime.Now;
	public Inode UpdateCacheTime() {
		if (this.CacheTime < DateTime.Now) {
			this.CacheTime = DateTime.Now;
		}
		return this;
	}
}
