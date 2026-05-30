namespace Pgfs.Lib.Models;

public static class SettingLoggingCycle
{
	public enum Enum
	{
		None = 0,
		Hourly = 1,
		Daily = 1 + 1,
		Monthly = 1 + 2,
	}
}
