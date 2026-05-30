namespace Pgfs.Lib.Api;

public static class Mode
{
	public static readonly int S_IFSOCK = Convert.ToInt32("0140000", 8);
	public static readonly int S_IFLNK = Convert.ToInt32("0120000", 8);
	public static readonly int S_IFREG = Convert.ToInt32("0100000", 8);
	public static readonly int S_IFBLK = Convert.ToInt32("0060000", 8);
	public static readonly int S_IFDIR = Convert.ToInt32("0040000", 8);
	public static readonly int S_IFCHR = Convert.ToInt32("0020000", 8);
	public static readonly int S_IFIFO = Convert.ToInt32("0010000", 8);
	public static readonly int S_ISUID = Convert.ToInt32("0004000", 8);
	public static readonly int S_ISGID = Convert.ToInt32("0002000", 8);
	public static readonly int S_ISVTX = Convert.ToInt32("0001000", 8);
	public static readonly int S_IRWXU = Convert.ToInt32("00700", 8);
	public static readonly int S_IRUSR = Convert.ToInt32("00400", 8);
	public static readonly int S_IWUSR = Convert.ToInt32("00200", 8);
	public static readonly int S_IXUSR = Convert.ToInt32("00100", 8);
	public static readonly int S_IRWXG = Convert.ToInt32("00070", 8);
	public static readonly int S_IRGRP = Convert.ToInt32("00040", 8);
	public static readonly int S_IWGRP = Convert.ToInt32("00020", 8);
	public static readonly int S_IXGRP = Convert.ToInt32("00010", 8);
	public static readonly int S_IRWXO = Convert.ToInt32("00007", 8);
	public static readonly int S_IROTH = Convert.ToInt32("00004", 8);
	public static readonly int S_IWOTH = Convert.ToInt32("00002", 8);
	public static readonly int S_IXOTH = Convert.ToInt32("00001", 8);

	public static bool IsDirectory(int mode) => (mode & Mode.S_IFDIR) != 0;
	public static bool IsFile(int mode) => (mode & Mode.S_IFREG) != 0;
}
