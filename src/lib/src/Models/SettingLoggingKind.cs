namespace Pgfs.Lib.Models;

public static class SettingLoggingKind
{
	[Flags]
	public enum Enum
	{
		None = 0,
		Stdout = 1,
		Stderr = 1 << 1,
		File = 1 << 2,
		DB = 1 << 3,
	}
}
