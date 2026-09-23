namespace Pgfs.Gui;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pgfs.Gui.ViewModels;

public sealed class App : Application
{
	public override void Initialize() {
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted() {
		if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
			var args = desktop.Args ?? System.Array.Empty<string>();
			desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(args) };
		}
		base.OnFrameworkInitializationCompleted();
	}
}
