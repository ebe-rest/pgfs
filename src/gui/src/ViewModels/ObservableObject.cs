namespace Pgfs.Gui.ViewModels;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>A plain INotifyPropertyChanged base for MVVM (kept dependency-free).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null) {
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	/// <summary>Raises the change notification only when the value actually changed. True when it did.</summary>
	protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) {
		if (EqualityComparer<T>.Default.Equals(field, value)) {
			return false;
		}
		field = value;
		this.OnPropertyChanged(name);
		return true;
	}
}
