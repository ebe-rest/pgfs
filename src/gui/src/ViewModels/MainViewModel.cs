namespace Pgfs.Gui.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Pgfs.Core.Api;
using Pgfs.Core.Config;

/// <summary>
/// The main ViewModel of the read-only dashboard. It calls Core's <see cref="StatusAdmin"/> /
/// <see cref="ConfigAdmin"/> in process directly and shows Mounts (L1) / Filesystem (L2) /
/// Process detail (L3) / Config. It refreshes on a timer poll, plus a ping NOTIFY on Refresh.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
	private const int PollSeconds = 3;

	private string connection = "";
	private string schema = "public";
	private string prefix = "pgfs_";
	private string statusMessage = "not connected";
	private bool isConnected;
	private FsStats? fs;
	private MountViewModel? selectedMount;

	private StatusAdmin? statusAdmin;
	private ConfigAdmin? configAdmin;
	private DispatcherTimer? timer;

	public MainViewModel(string[] args) {
		this.ConnectCommand = new RelayCommand(this.ConnectAsync);
		this.RefreshCommand = new RelayCommand(this.RefreshAsync, () => this.isConnected);
		this.Prefill(args);
	}

	public ObservableCollection<MountViewModel> Mounts { get; } = new();
	public ObservableCollection<ConfigItem> ConfigItems { get; } = new();

	public RelayCommand ConnectCommand { get; }
	public RelayCommand RefreshCommand { get; }

	public string Connection { get => this.connection; set => this.SetField(ref this.connection, value); }
	public string Schema { get => this.schema; set => this.SetField(ref this.schema, value); }
	public string Prefix { get => this.prefix; set => this.SetField(ref this.prefix, value); }
	public string StatusMessage { get => this.statusMessage; set => this.SetField(ref this.statusMessage, value); }
	public FsStats? Fs { get => this.fs; private set => this.SetField(ref this.fs, value); }
	public MountViewModel? SelectedMount { get => this.selectedMount; set => this.SetField(ref this.selectedMount, value); }

	public bool IsConnected {
		get => this.isConnected;
		private set {
			if (this.SetField(ref this.isConnected, value)) {
				this.RefreshCommand.RaiseCanExecuteChanged();
			}
		}
	}

	/// <summary>Fills the connection bar's initial values with the same <see cref="ConfigLoader"/> mount.pgfs uses (CLI arguments + toml), best-effort.</summary>
	private void Prefill(string[] args) {
		try {
			var loader = new ConfigLoader(args, Pgfs.Core.Config.Schema.AllFields, null);
			var db = loader.BuildDatabaseConfig();
			this.Connection = db.Connection.ConnectionString;
			this.Schema = db.SchemaName;
			this.Prefix = db.GetPrefix();
		} catch {
			// On a failure it stays at the defaults (the user types into the connection bar).
		}
	}

	private async Task ConnectAsync() {
		try {
			this.statusAdmin = new StatusAdmin(this.connection, this.schema, this.prefix);
			this.configAdmin = new ConfigAdmin(this.connection, this.schema, this.prefix);
			await this.ReloadStatusAsync();
			await this.ReloadConfigAsync();
			this.IsConnected = true;
			this.StartTimer();
			this.StatusMessage = "connected";
		} catch (Exception ex) {
			this.IsConnected = false;
			this.StatusMessage = "connect failed: " + ex.Message;
		}
	}

	private async Task RefreshAsync() {
		this.FirePing();
		await Task.Delay(700); // Wait a little for the running mounts to receive the ping and write their snapshots back.
		await this.ReloadStatusAsync();
		await this.ReloadConfigAsync();
	}

	/// <summary>Fires a control ping NOTIFY to prompt every mount to refresh its Layer 3 snapshot at once (the same idea as ConfigAdmin.FireSet).</summary>
	private void FirePing() {
		try {
			using var ch = new NotifyChannel(this.connection, this.schema, this.prefix);
			ch.Publish(new NotifyMessage { Control = "ping" });
		} catch {
			// best effort: even if it does not arrive, the snapshot is refreshed by the next heartbeat (30s).
		}
	}

	private async Task ReloadStatusAsync() {
		var sa = this.statusAdmin;
		if (sa == null) {
			return;
		}
		try {
			var (mounts, stats) = await Task.Run(() => (sa.ListMounts(), sa.GetFsStats()));
			this.UpdateMounts(mounts);
			this.Fs = stats;
			var note = mounts.TablePresent switch {
				true  => "",
				false => " (mounts table absent — re-run mkfs)",
			};
			this.StatusMessage = $"updated: {mounts.Mounts.Count} mount(s){note}";
		} catch (Exception ex) {
			this.StatusMessage = "status error: " + ex.Message;
		}
	}

	private async Task ReloadConfigAsync() {
		var ca = this.configAdmin;
		if (ca == null) {
			return;
		}
		try {
			var items = await Task.Run(() => ca.List(null));
			this.ConfigItems.Clear();
			foreach (var it in items) {
				this.ConfigItems.Add(it);
			}
		} catch (Exception ex) {
			this.StatusMessage = "config error: " + ex.Message;
		}
	}

	private void UpdateMounts(MountsStatus status) {
		var prevId = this.SelectedMount?.MountId;
		this.Mounts.Clear();
		foreach (var m in status.Mounts) {
			this.Mounts.Add(new MountViewModel(m));
		}
		this.SelectedMount = this.Mounts.FirstOrDefault(m => m.MountId == prevId) ?? this.Mounts.FirstOrDefault();
	}

	private void StartTimer() {
		if (this.timer != null) {
			return;
		}
		this.timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PollSeconds) };
		this.timer.Tick += this.OnTick;
		this.timer.Start();
	}

	private async void OnTick(object? sender, EventArgs e) {
		await this.ReloadStatusAsync();
	}
}
