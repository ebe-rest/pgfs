namespace Pgfs.Gui;

using Avalonia;

/// <summary>
/// The entry point of pgfsgui (the GUI). It starts under Avalonia's classic desktop lifetime.
/// The design of record is docs/design/runtime-control-plane.md, the GUI section.
/// </summary>
internal sealed class Program
{
	// Do nothing before Avalonia is initialized (the template's standing warning; the SynchronizationContext and friends are not set up yet).
	[System.STAThread]
	public static void Main(string[] args) {
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	// Avalonia's designer and tooling call this by reflection, so keep it public and under this name.
	public static AppBuilder BuildAvaloniaApp() {
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.LogToTrace();
	}
}
