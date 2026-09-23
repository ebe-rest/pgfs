# GUI - the operations dashboard (Avalonia)

> **Route**: [docs/README.md](../README.md) › [runtime-control-plane.md](runtime-control-plane.md) › **this document**
>
> **What this document is the source of truth for**: the design, implementation status and change record of
> `src/gui/` (`Pgfs.Gui`, output `pgfsgui`). The technology choice, the screen layout and how Core is called
> (in process, directly) belong here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [control-plane.md](control-plane.md) | The design of `StatusAdmin` / `ConfigAdmin` themselves, which the GUI reads |
> | [../Pgfsctl.md](../Pgfsctl.md) | The specification of the CLI (`pgfsctl config` / `status`). It uses the same Core API as the GUI |
> | [runtime-control-plane.md](runtime-control-plane.md) | The overall shape of the operations work and how its parts relate (the hub) |
> | [settings-matrix.md](settings-matrix.md) | The settings items the GUI's Config screen lists |

A thin operations front end that reads `config` / `status`. In keeping with the idea of **aggregating in the
database and spanning the cluster**, it shows every mount on one screen.

## Design

### The settled design

Agreed: the technology is **Avalonia (cross-platform desktop)**, and the MVP is **a read-only dashboard first**.

**Decision 1 - the technology is Avalonia**: one binary behaving identically on all three operating systems
(Linux/Win/mac) fits the project's goal of "the same behaviour" best. It was chosen over the alternatives
(WinForms/WPF = Windows only, MAUI = weak on Linux desktop, Web = spans the cluster but is a different animal,
TUI = not graphical) as the native desktop option.

**Decision 2 - call Core in process, do not shell out**: like pgfsctl, the GUI **references Core alone** (no
FUSE / Dokan dependency). It calls [StatusAdmin](../../src/core/src/Api/StatusAdmin.cs) (ListMounts/GetFsStats)
and [ConfigAdmin](../../src/core/src/Config/ConfigAdmin.cs) (list/get/set) directly (richer types, no
subprocess). `pgfsctl --json` remains for external and scripted consumers. The connection is resolved by
reusing [ConfigLoader](../../src/core/src/Config/ConfigLoader.cs) (CLI + toml) plus the UI's connection bar.

**Decision 3 - minimal dependencies plus plain MVVM**: `Avalonia` + `Avalonia.Desktop` +
`Avalonia.Themes.Fluent` only. MVVM is plain `INotifyPropertyChanged` (the project's policy of not adding
dependencies loosely; ReactiveUI and the like are not brought in). `net10.0`, in the new `src/gui/`
(`Pgfs.Gui`, output `pgfsgui`). The name is a single word, as an admin tool (the same exception to the
`{role}.pgfs` convention that pgfsctl takes).

**Decision 4 - the refresh model is polling plus a ping refresh**: a timer polls `ListMounts`/`GetFsStats`
every few seconds. The "Refresh" button **fires a ping control NOTIFY so the Layer 3 snapshot is updated at
once** and then reads (the same trick as status.sh).

**The screen (the operations dashboard)**: (1) the connection bar (connection/schema/prefix, prefilled from
toml/CLI, plus Connect) / (2) Mounts (the Layer 1 grid) / (3) Filesystem (the Layer 2 panel) / (4) Process
detail (selecting a row shows Layer 3 = the inode/content cache statistics with hit ratios + notify + the
effective config) / (5) Config (a list of (3)'s settings with their source/SaveTo/Reload; set comes later).

**What testing looks like here (unlike the earlier work)**: it builds on Windows. It runs on all three
operating systems since it is Avalonia, but **the GUI cannot go into the docker e2e** (it needs a display).
Verification is **manual inspection plus a smoke test that points the ViewModel's data fetching at a real
database** (the same style as the offline smoke of ConfigAdmin/StatusAdmin).

**The implementation sub-steps**:
- **(a)**: the Avalonia project skeleton (a new `src/gui/`, a Core reference, the NuGet restore, an empty window, the whole solution green).
- **(b)** (the MVP): the read-only dashboard - Layer 1 (Mounts) + Layer 2 (FS) + Layer 3 (Process detail) + the Config list view. Timer polling plus the ping refresh. **The MVP ends here.**
- **(c)**: config set (applied live) from the Config screen. Persistence and guidance are varied along the (SaveTo, Reload) matrix.
- **(d)**: the finish (a connection dialog, error display, how stale rows are shown).

**Open points (at implementation time)**: (1) the project's output name (`pgfsgui` for now; changeable on
request). (2) confirming the display prerequisites for running on Linux (X11/Wayland) during manual
verification. (3) the shape of the editing UI for the config list (settled in (c)).

## Implementation status (as-built)

- **(a) complete**: `src/gui/` created (`Pgfs.Gui`, output `pgfsgui`, referencing Core). The Avalonia
  boilerplate (Program / App.axaml / MainWindow.axaml plus their .cs, FluentTheme, an empty window).
  **Avalonia 12.0.5 was adopted** - the original 11.2.3 pulled in `Tmds.DBus.Protocol` 0.20.0 transitively,
  which raised NU1903 (HIGH, GHSA-xrw6-gwf8-vvr9); since a new project carries no legacy, it went to the
  current 12.0.5 line instead. **The gui and the whole solution build green with no vulnerability warnings.**
  The Release single-file publish settings (the same shape as Ctl) are not added yet, because the GUI publish
  is settled in (d). It is started by hand (`dotnet run --project src/gui/Gui.csproj`; not part of the docker
  e2e, since it needs a display). The empty window has been inspected.
- **(b) complete (builds green; the visual check is manual)**: the read-only dashboard (the MVP). Plain MVVM
  (`ObservableObject`/`RelayCommand`, no added dependencies, reflection bindings). `MainViewModel` calls Core
  in process directly - `StatusAdmin.ListMounts/GetFsStats` (a 3s timer poll) plus `ConfigAdmin.List` (on
  Connect/Refresh). Refresh publishes `NotifyChannel.Publish(Control="ping")` (the same shape as
  ConfigAdmin.FireSet) and re-reads 700ms later so the Layer 3 snapshot is updated at once. The screen is the
  connection bar / Mounts (the L1 grid, selectable) / Filesystem (the L2 panel) / Process detail (L3 = the
  selected mount's inode/content cache statistics with hit ratios + notify + the effective config;
  `MountViewModel` parses the stats/config JSON) / Config (a list of `ConfigItem` with key/value/src/reload).
  The database calls go through `Task.Run` so the UI does not block. **The data layer is already green in the
  e2e through status.sh, so what is specific to the GUI is the visual check of the bindings and the layout.**
  `AvaloniaUseCompiledBindingsByDefault=false` (the MVP; moving to compiled bindings is a candidate for (d)).

---


## Change record

The chronological record is appended here (the design and the as-built status are owned by the two chapters above).

- [runtime-control-plane.md](runtime-control-plane.md) had grown to 1,802 lines, so it was split by feature
  and this document was carved out of it. The content is as it was before the split.
