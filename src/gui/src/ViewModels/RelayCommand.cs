namespace Pgfs.Gui.ViewModels;

using System;
using System.Windows.Input;

/// <summary>A minimal ICommand that accepts either a synchronous or an asynchronous body. It blocks re-entry while running.</summary>
public sealed class RelayCommand : ICommand
{
	private readonly Func<System.Threading.Tasks.Task> execute;
	private readonly Func<bool>? canExecute;
	private bool running;

	public RelayCommand(Func<System.Threading.Tasks.Task> execute, Func<bool>? canExecute = null) {
		this.execute = execute;
		this.canExecute = canExecute;
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) {
		if (this.running) {
			return false;
		}
		return this.canExecute == null || this.canExecute();
	}

	public async void Execute(object? parameter) {
		this.running = true;
		this.RaiseCanExecuteChanged();
		try {
			await this.execute();
		} finally {
			this.running = false;
			this.RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged() {
		this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}
