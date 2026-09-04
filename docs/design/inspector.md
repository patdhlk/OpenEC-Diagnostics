# OpenEC-Diagnostics — Inspector Design

**Date:** 2026-08-16 (M2 foundation), 2026-08-17 (explorer shell restructure)

This document describes the design of OpenEC.Inspector at the time Milestone 2 (M2 foundation) and the subsequent explorer-shell restructure were built. Where this document and the code disagree, the code is authoritative.

## 1. Goal

A cross-platform desktop inspector for passive EtherCAT monitoring: pick a live capture NIC (network TAP) or open a `.pcap`/`.pcapng` file, then watch bus topology, traffic health, events, and decoded process variables through a GUI. The GUI is a thin, testable layer over the M1 SDK — all protocol knowledge stays in `OpenEC.Monitor`.

The user interface presents an engineering-tool explorer shell: a device tree as primary navigation, a tabbed per-device editor, an always-visible docked messages panel, and chrome top/status bars. The visual design follows the Dahlke house style: flat panels, token-based palette supporting light and dark themes, and sharp corners throughout.

## 2. Non-goals

Deferred to later milestones:

- **ADS enrichment** (resolved device names, IG 0x0C frame counters) — the CLI keeps that role; the GUI adds it in a follow-up milestone with connection-config UI.
- **Pcap replay pacing** — a file session pumps to completion at full speed and the views show the final state (the analyzer use case).
- **Frame-level browsing** (Wireshark-style per-frame lists), charts/sparklines, saving captures, multiple concurrent sessions.
- The M1-deferred SDK items (AL-control via BWR/APWR, configurable buffer limits, `LiveCaptureSource` re-entrancy, EoE reassembly, SoE emergency payload decode) stay deferred; the GUI works within the existing SDK contracts.

## 3. Solution layout

```
OpenEC-Diagnostics.sln
├── src/
│   ├── OpenEC.Monitor/          # (M1) core SDK — unchanged
│   ├── OpenEC.Monitor.Ads/      # (M1) unchanged, NOT referenced by Inspector
│   ├── OpenEC.CLI/              # (M1) unchanged
│   └── OpenEC.Inspector/        # Avalonia desktop app (net8.0)
│       ├── Program.cs, App.axaml(.cs)
│       ├── Session/             # MonitorSession engine + source spec + state
│       ├── ViewModels/          # CommunityToolkit.Mvvm, plain testable classes
│       ├── Views/               # AXAML views + MainWindow shell
│       └── Theme/               # Palette + global styles
└── tests/
    ├── OpenEC.Monitor.Tests/    # (M1) unchanged
    └── OpenEC.Inspector.Tests/  # xunit: session, VM, headless smoke
```

**Dependencies:** Avalonia 11.x (Fluent theme, light/dark follows OS), `CommunityToolkit.Mvvm` 8.x, project reference to `OpenEC.Monitor` only. `Directory.Build.props` conventions (nullable, warnings-as-errors, analyzers) apply as in M1.

## 4. Architecture: snapshot polling

The data-flow architecture chosen over ReactiveUI event-push and over a separate Core project:

- **Single writer, snapshot readers.** `BusObserver` is single-writer under one lock; concurrent readers must use `SnapshotSlaves()` / `SnapshotEvents()`. The GUI honors that contract the same way the CLI live dashboard does: the capture pump is the only writer; the UI *polls* snapshots on a timer and never subscribes to per-frame callbacks. At ~500 fps bus traffic, event-push into a UI thread is the wrong shape; sampled state at 4 Hz is.
- **CommunityToolkit.Mvvm**, not ReactiveUI: source-generated `[ObservableProperty]`/`[RelayCommand]`, no Rx dependency, view-models stay plain classes unit-testable without a UI thread.
- **No separate Core project.** Session and view-models live in `OpenEC.Inspector`; the test project references it directly.

## 5. Session engine

`MonitorSession` is the only concurrency-bearing unit in the app.

```csharp
public abstract record SourceSpec
{
    public sealed record Live(string InterfaceName) : SourceSpec;
    public sealed record File(string Path) : SourceSpec;
}

public enum SessionState { Idle, Running, Completed, Stopped, Faulted }

public sealed class MonitorSession : IAsyncDisposable
{
    public MonitorSession(SourceSpec source, EniConfiguration? eni = null);

    public BusObserver Observer { get; }        // snapshot reads only from UI
    public SessionState State { get; }
    public Exception? Fault { get; }
    public string SourceDescription { get; }    // "en11" / "capture.pcap"
    public long FramesSeen { get; }
    public long MalformedFrames { get; }

    public void Start();                        // idempotent-hostile: throws if not Idle
    public Task StopAsync();                    // cancel pump, await drain
}
```

- Owns a **fresh** `ICaptureSource` per session (`LiveCaptureSource` is not re-entrant) — `Live` → `LiveCaptureSource`, `File` → `PcapFileSource`.
- Wraps the SDK's `EtherCatMonitor` facade (which already owns the capture source, `BusObserver`, and pump) rather than duplicating the pump logic.
- Terminal states: file EOF → `Completed`; user stop → `Stopped`; capture/IO exception → `Faulted` with the exception retained. State transitions are exposed as an event for the shell status bar (coarse, low-frequency — not per-frame).
- `MalformedFrameException` increments `MalformedFrames` and continues; the pump never dies on bad input.
- ENI, when provided, is passed to the `BusObserver` constructor (seeds topology and enables `ProcessImage` decoding).

## 6. Shell layout

With a session active (the Start screen stays full-window when no session exists):

```
┌──────────────────────────────────────────────────────────────┐
│ chrome top bar: "OpenEC Inspector" · source description ·    │  ← Chrome bg, 2px
│                                    [Stop session]            │    Accent bottom border
├───────────────┬──────────────────────────────────────────────┤
│ Explorer tree │  Editor area                                 │
│ (Panel bg)    │  network root  → Dashboard tiles             │
│  ● network    │  slave node    → Device editor (tabs)        │
│   ● slave     │  process image → unmatched-variables watch   │
│   ● slave     │                                              │
│   ◦ Process   ├──────────────────────────────────────────────┤
│     Image     │  Messages panel (docked, collapsible)        │
├───────────────┴──────────────────────────────────────────────┤
│ chrome status bar: state dot · StatusText (incl. rec →)      │  ← 2px Accent top border
└──────────────────────────────────────────────────────────────┘
```

`MainWindowViewModel` runs one `DispatcherTimer` at 4 Hz calling `Refresh()` on the **active** view's VM only. Each view VM implements a small `IRefreshable` (`void Refresh()`), reads snapshots, and diffs into its observable state (update-in-place keyed by slave address / variable name so selection and scroll survive refreshes).

The messages panel (formerly Events view) renders continuously and uses append-only diffing rather than clear+rebuild to preserve existing row instances.

## 7. Theme resources

Two `ResourceDictionary` files in `Theme/`, merged into `App.axaml` after `FluentTheme`:

**`Theme/Palette.axaml`** — defines the house theme's tokens as `Color` + `SolidColorBrush` resources with `ThemeDictionaries` for `Light` and `Dark`. Brush keys mirror the CSS names:

| Key | Light | Dark |
|---|---|---|
| `Bg` | `#eef0f2` | `#242729` |
| `Panel` / `Panel2` / `Panel3` | `#ffffff` / `#f5f6f7` / `#eceef0` | `#2e3236` / `#33383c` / `#3a4045` |
| `Line` / `Line2` | `#d5d9dc` / `#e9ecee` | `#464c52` / `#3b4045` |
| `Ink` / `Ink2` / `Ink3` | `#2e3439` / `#5f676d` / `#8d949a` | `#e6e9eb` / `#adb5bb` / `#7f878d` |
| `Chrome` / `Chrome2` / `ChromeLine` | `#3f444a` / `#4e555c` / `#5a6168` | `#1d2023` / `#2b3035` / `#383e43` |
| `ChromeInk` / `ChromeInk2` | `#f2f4f6` / `#a9b1b8` | `#eef1f3` / `#98a1a8` |
| `Accent` / `AccentSoft` | `#66b2ff` / `#e2eefb` | `#66b2ff` / `#2c3d50` |
| `Ok` / `Fail` / `Oos` / `Maint` / `Fc` | `#0d8a16` / `#cd4e4e` / `#b78e00` / `#1b8fbf` / `#ee8632` | `#44b34b` / `#e06e6e` / `#d1a91d` / `#41a8d6` / `#f09a55` |

(`Maint`/`Fc` may be unused initially; the full set is ported so future work never invents off-palette colors.)

**`Theme/Controls.axaml`** — global styles enforcing the house look over Fluent: `CornerRadius="0"` on Button/TextBox/TabItem/ToggleButton/etc. (sharp corners everywhere), window `FontFamily="Segoe UI, Helvetica Neue, Arial"` (falls through to Helvetica Neue on macOS), monospace values via `Consolas, Menlo, monospace`, and shared style classes: `chrome` (bars), `panel`/`tile` (flat `Panel` bg + 1px `Line` border), `label` (`Ink2`, small), `value` (large, semibold, tabular numerals), `mono`, `accent` (Button), `panelHeader`.

## 8. Explorer tree

An Avalonia `TreeView` bound to a small node model:

```csharp
public enum StatusDot { Idle, Ok, Oos, Fail }   // Idle renders Ink3

public abstract partial class ExplorerNode : ObservableObject
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private StatusDot _dot;
}
public sealed partial class NetworkNode : ExplorerNode      // children = slaves + process image
public sealed partial class SlaveNode : ExplorerNode        // ushort Address
public sealed partial class ProcessImageNode : ExplorerNode // [ObservableProperty] bool IsVisible
```

- **Network root** — label = `Session.SourceDescription`; dot: Running → `Ok`, Faulted → `Fail`, Completed/Stopped/Idle → `Idle`. Selecting shows the Dashboard.
- **Slave nodes** — ordered by address, label `"{DisplayName} ({Address})"`. Dot implements the AL badge colors: error flag → `Fail` (overrides state), OP → `Ok`, SAFEOP/PREOP → `Oos`, INIT/BOOT/unseen → `Idle`. The same mapping renders the state badge in the editor's General tab — one converter, two uses.
- **Process Image node** — visibility rule: no ENI loaded → visible (selecting shows the Load-ENI call-to-action); ENI loaded and unmatched variables exist → visible, shows only the unmatched variables; ENI loaded and everything matched → hidden.

The 4 Hz tick updates labels/dots on existing node instances (the row-reuse pattern `TopologyViewModel.Refresh` used originally) — no tree rebuild, so expansion and selection survive ticks. New slaves appearing mid-session insert in address order.

Selection drives the editor: `NetworkNode` → `DashboardViewModel`, `SlaveNode` → `DeviceEditorViewModel`, `ProcessImageNode` → the unmatched-variables watch (the reshaped PV-watch VM).

## 9. Device editor

House-style tab strip (accent underline on the active tab), two tabs:

- **General** — the original Topology detail promoted to a full pane: identity block (vendor / product / revision hex, physical address, AL state badge with dot colors, error flag, last seen), state history list, mailbox activity list (emergencies / SoE errors).
- **Variables** — this slave's process variables: filter box + rows of (name **without** the slave prefix, DataType, monospace value, updated time). No ENI loaded → the same Load-ENI CTA, wired to the existing `RestartWithEniAsync` flow (which restarts the session with the ENI).

View-model reshaping: `TopologyViewModel` and `PvWatchViewModel` became

- `ExplorerViewModel` — owns the node collection + refresh (absorbs `TopologyViewModel.Refresh`'s snapshot logic and `MailboxProtocolsByAddress`).
- `DeviceEditorViewModel` — per selected slave; General-tab content is the existing `SlaveDetailViewModel.Build` output; Variables tab filters a shared variables store.
- `VariableWatchViewModel` — one shared store over `Session.Observer`'s decoded `ProcessImage` values, partitioned by `ProcessVariableAssignment`; serves both the per-device tab (by address) and the Process Image node (unmatched).

`IRefreshable` stays the tick contract for whichever of these is visible.

## 10. Messages panel (docked)

The Events view moved from a page to a bottom-docked panel (~180 px) under the editor, always present while a session runs:

- Header bar (Panel2 bg): "Messages" title, category filter checkboxes, auto-scroll toggle, and a collapse chevron. Collapsed = header only.
- Because the panel renders continuously, `EventsViewModel.Refresh` changed from Clear+Rebuild to **append-only diffing**: locate the previously seen tail event (reference scan from the end of the new snapshot), append only events after it, trim the front to the 500-row cap. Tail not found (≥500 new events in one tick) → full rebuild fallback. Filter toggle → full rebuild (user-initiated, rare). The existing auto-scroll guard behavior is preserved.
- The main window ticks the events VM every tick (not only when "visible" — it always is, unless collapsed; collapsed skips the refresh).

## 11. Dashboard

Stat tiles mirroring the CLI live dashboard: Tx/Rx direction rates, estimated cycle time, WKC health, ring-loss count (TwinCAT "Lost Frames" equivalent), frame/datagram totals, malformed count. Numbers only in v1 — no charts.

Tiles styled with house tokens: `Panel` background, `Line` border, `Ink2` labels, large semibold values with tabular numerals.

## 12. SDK addition: variable→slave assignment

One new pure type in `OpenEC.Monitor.Eni` — no observer or capture changes:

```csharp
/// <summary>Partitions an ENI's process variables by owning slave, matched by
/// name prefix ("SlaveName." …), longest slave name winning. Pure and immutable.</summary>
public sealed record ProcessVariableAssignment(
    IReadOnlyDictionary<ushort, IReadOnlyList<EniVariable>> BySlave,   // key: PhysAddr
    IReadOnlyList<EniVariable> Unmatched)
{
    public static ProcessVariableAssignment Build(EniConfiguration eni);
}
```

Matching rule: `variable.Name` starts with `slave.Name + "."` (ordinal). When slave names nest (one is a prefix of another), the longest matching name wins. Covers TwinCAT-exported names like `SubDevice_1014 [EL3162].Channel 1.Value`. Variables matching nothing land in `Unmatched`. Both inputs and outputs are assigned; the per-device tab may show an IN/OUT marker from `EniVariable.IsInput`.

This is a heuristic on ENI naming — acceptable because unmatched variables remain reachable via the Process Image node, so nothing is ever hidden by a failed match.

## 13. Start screen & chrome bars

- **StartView** — a `Panel` card centered on the `Bg` background, accent primary button for Start, house-styled source pickers and the optional ENI row. Behavior unchanged; early-fault errors shown inline with a hint referencing `../../tap-setup.md` (ChmodBPF on macOS).
- **Top bar** — chrome: app title, `SourceDescription`, Stop-session button (chrome-outline style). Only shown with a session; Start screen fills the window.
- **Status bar** — chrome: a session-state dot (same `StatusDot` mapping as the network root) + the existing `StatusText` string unchanged (including the `rec → file.pcap` suffix).

## 14. Error handling

- **Capture open fails** (typical: BPF permissions on macOS): session faults before the shell transition; the start screen shows the message inline plus a hint referencing `../../tap-setup.md` (ChmodBPF).
- **Mid-session fault** (device vanished, truncated file): state → `Faulted`, dismissible banner with the message, views freeze on last good snapshots. The app never exits or discards observed state on capture errors; a new session can be started from the shell.
- **ENI parse failure**: error message inline on start screen; the session can proceed without ENI (PV watch stays in its empty state).
- **Per-frame decode errors**: counted (`MalformedFrames`), shown in the status bar and dashboard, never propagated.

## 15. Testing

TDD throughout (red-green-refactor), three layers, heaviest at the bottom:

1. **Session tests** (bulk of coverage): pcap fixtures generated at test runtime with the existing `Synthesis` builders (`EtherCatFrameBuilder`, `SampleCapture`, `PcapFileWriter`) drive `MonitorSession` end-to-end. Assert: state machine transitions (Running→Completed, Stop, fault on bad file path), snapshot contents after completion, malformed-frame tolerance, double-start throws, dispose during Running cancels cleanly.
2. **View-model tests**: run a file session to completion, call `Refresh()`, assert observable state — dashboard tile values, explorer node rows and selection stability across refreshes, event append-only diffing and filtering, PV rows and formatting with longest-prefix matching, start-screen validation (no device selected, missing file).
3. **Headless smoke tests** (`Avalonia.Headless.XUnit`): app boots, start screen renders, navigation walks every tree node and editor tab while the window is live — templates must instantiate without throwing.

Manual acceptance: live capture against the DUALCOMM ETAP-1000 on `en11` — verify topology shows station 1001, ~4.4 ms cycle estimate, rates plausible vs. the CLI live dashboard on the same tap.

## 16. Design decisions and refinements

This section captures key decisions and refinements made during implementation:

### MonitorSession wraps EtherCatMonitor facade

The SDK's `EtherCatMonitor` facade (`src/OpenEC.Monitor/EtherCatMonitor.cs`) already implements the capture source ownership and parse pump, including malformed-frame counting. `MonitorSession` wraps it instead of duplicating that logic (DRY). The observable behavior is identical to the spec's sketch.

### Per-slave WKC attribution dropped from detail pane

The spec listed "WKC involvement" in the slave detail pane. The SDK attributes WKC mismatches to *datagram* addresses (`WkcMismatchDetected.Address` is a `uint` logical/physical datagram address), not to slaves, so per-slave WKC attribution is not derivable. It is dropped from the detail pane; the bus-level WKC counter lives on the Dashboard.

### ENI parse failure inline, not modal

The spec's "ENI parse failure: error dialog" is rendered as an inline error message on the start screen — same information, no modal machinery.

### ProcessVariableAssignment duplicate-name tie-break

When multiple slaves share the same name, the variable is assigned to the slave with the lowest `PhysAddr`. This falls out of the implementation's ordering: equal names have equal lengths, so `ThenBy(PhysAddr)` puts the lowest address first and `FirstOrDefault` picks it.

### Events append-only diffing driven by panel visibility

The messages panel renders continuously (unlike the old page-switch navigation), so its `Refresh()` changed from Clear+Rebuild to append-only. When the previously seen tail event is found in the new snapshot (reference equality scan from the end), only events after it are appended, then the front is trimmed to the 500-row cap. This preserves existing row instances so selection and auto-scroll behavior survive ticks. The tail-not-found fallback (≥500 new events in one tick) performs a full rebuild.

### Explorer shell replaces InspectorSection enum

The original M2 design used an `InspectorSection` enum and a left sidebar of nav buttons to switch pages. The explorer shell restructure removed that enum entirely — tree selection is the navigation. The `MainWindowViewModel.CurrentPage` property now holds a `Dashboard` / `DeviceEditor` / `VariableWatch` view-model directly, driven by the `ExplorerViewModel.SelectedNode` callback.

### Variable watch scoped by ProcessVariableAssignment

The original PV watch showed all process variables in one flat list. The explorer shell partitions them: per-device Variable tabs show only that slave's assigned variables (with the slave-name prefix stripped), and the Process Image node shows only unmatched variables. This is driven by `ProcessVariableAssignment.Build(eni)` at session start. Rows are seeded from the assigned list (not just observed values), so a mapped-but-quiet variable shows `—`.

### RestartWithEniAsync preserves slave selection

When the user triggers Load ENI from a device editor's Variables tab, the session restarts with the ENI and the shell attempts to re-select the same slave address in the new tree. If that address no longer exists (not in the ENI or not observed on the wire), the network root is selected instead (showing the Dashboard).

### StatusDot mapping serves tree, badge, and status bar

A single `StatusDot` enum (`Idle`, `Ok`, `Oos`, `Fail`) with two mapping functions (`StatusDotMap.ForSlave`, `StatusDotMap.ForSession`) drives the explorer tree dots, the device editor's AL state badge, and the status bar session dot. The palette mapping (Idle→Ink3, Ok→Ok, Oos→Oos, Fail→Fail) is handled by a single `StatusDotBrushConverter` value converter used in every binding site.

### 4 Hz tick unchanged; panel collapse skips work

The 4 Hz `DispatcherTimer` from M2 remained unchanged. The messages panel's `IsCollapsed` guard lives inside `EventsViewModel.Refresh()` — when collapsed, the method returns immediately without scanning events. Expanding triggers an immediate catch-up refresh via `OnIsCollapsedChanged`.

## 17. Related documentation

For overall architecture and other milestones, see:

- [`./monitor-and-cli.md`](./monitor-and-cli.md) — M1: OpenEC.Monitor SDK + OpenEC.Monitor.Ads + OpenEC.CLI
- [`./learning-mode.md`](./learning-mode.md) — ENI-independent bus discovery
- [`./topology-view.md`](./topology-view.md) — port-level topology map
- [`./README.md`](./README.md) — design index
- [`../tap-setup.md`](../tap-setup.md) — capture interface configuration (ChmodBPF, etc.)

For context on specific SDK types, consult the M1 monitor-and-cli design.
