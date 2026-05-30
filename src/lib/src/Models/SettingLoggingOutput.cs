namespace Pgfs.Lib.Models;

public class SettingLoggingOutput
{
	public SettingLoggingKind.Enum Kind { get; set; } = SettingLoggingKind.Enum.Stderr;
	public string Directory { get; set; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pgfs", "log");
	public string FileNamePattern { get; set; } = "pgfs-*.log";
	public SettingLoggingCycle.Enum Cycle { get; set; } = SettingLoggingCycle.Enum.Daily;
}
