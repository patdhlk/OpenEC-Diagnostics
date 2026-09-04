# OpenEC-Diagnostics — Milestone 2 Design (OpenEC.Inspector GUI)

**Date:** 2026-08-16
**Status:** Approved
**Scope:** `OpenEC.Inspector` (Avalonia desktop app) and `OpenEC.Inspector.Tests`. Builds strictly on the M1 `OpenEC.Monitor` SDK. Passive-only: no ADS enrichment in this milestone.

## 1. Goal

A cross-platform desktop inspector for passive EtherCAT monitoring: pick a live capture NIC (network TAP) or open a `.pcap`/`.pcapng` file, then watch bus topology, traffic health, events, and decoded process variables through a GUI. The GUI is a thin, testable layer over the M1 SDK — all protocol knowledge stays in `OpenEC.Monitor`.

Non-goals for M2 v1 (ledgered for later):

- ADS enrichment (resolved device names, IG 0x0C frame counters) — the CLI keeps that role for now; the GUI adds it in a follow-up milestone with connection-config UI.
- Pcap replay pacing — a file session pumps to completion at full speed and the views show the final state (the analyzer use case).
- Frame-level browsing (Wireshark-style per-frame lists), charts/sparklines, saving captures, multiple concurrent sessions.
- The M1-deferred SDK items (AL-control via BWR/APWR, configurable buffer limits, `LiveCaptureSource` re-entrancy, EoE reassembly, SoE emergency payload decode) stay deferred; the GUI works within today's SDK contracts.

## 2. Solution layout

```
OpenEC-Diagnostics.sln
├── src/
│   ├── OpenEC.Monitor/          # (M1) core SDK — unchanged
│   ├── OpenEC.Monitor.Ads/      # (M1) unchanged, NOT referenced by Inspector
│   ├── OpenEC.CLI/              # (M1) unchanged
│   └── OpenEC.Inspector/        # NEW — Avalonia desktop app (net8.0)
│       ├── Program.cs, App.axaml(.cs)
│       ├── Session/             # MonitorSession engine + source spec + state
│       ├── ViewModels/          # CommunityToolkit.Mvvm, plain testable classes
│       └── Views/               # AXAML views + MainWindow shell
└── tests/
    ├── OpenEC.Monitor.Tests/    # (M1) unchanged
    └── OpenEC.Inspector.Tests/  # NEW — xunit: session, VM, headless smoke
```

Dependencies: Avalonia 11.x (Fluent theme, light/dark follows OS), `CommunityToolkit.Mvvm` 8.x, project reference to `OpenEC.Monitor` only. `Directory.Build.props` conventions (nullable, warnings-as-errors, analyzers) apply as in M1.

## 3. Architecture: snapshot polling

The decided data-flow architecture (over ReactiveUI event-push and over a separate Core project):

- **Single writer, snapshot readers.** `BusObserver` is single-writer under one lock; concurrent readers must use `SnapshotSlaves()` / `SnapshotEvents()`. The GUI honors that contract the same way the CLI live dashboard does: the capture pump is the only writer; the UI *polls* snapshots on a timer and never subscribes to per-frame callbacks. At ~500 fps bus traffic, event-push into a UI thread is the wrong shape; sampled state at 4 Hz is.
- **CommunityToolkit.Mvvm**, not ReactiveUI: source-generated `[ObservableProperty]`/`[RelayCommand]`, no Rx dependency, view-models stay plain classes unit-testable without a UI thread.
- **No separate Core project.** Session and view-models live in `OpenEC.Inspector`; the test project references it directly.

## 4. Session engine

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
- Pump task: `await foreach (var frame in source.CaptureAsync(ct))` → `EtherCatFrameParser.Parse(frame.Data)` → `Observer.Process(frame.Timestamp, decoded)`. `MalformedFrameException` increments `MalformedFrames` and continues; the pump never dies on bad input.
- Terminal states: file EOF → `Completed`; user stop → `Stopped`; capture/IO exception → `Faulted` with the exception retained. State transitions are exposed as an event for the shell status bar (coarse, low-frequency — not per-frame).
- ENI, when provided, is passed to the `BusObserver` constructor (seeds topology and enables `ProcessImage` decoding).

## 5. Shell & navigation

Single `MainWindow` hosting two top-level states:

1. **Start screen** (no session): live-NIC picker fed by `CaptureDevices.List()`, "Open pcap…" file picker, optional "Load ENI…" picker shown for both source kinds. Start button creates the `MonitorSession`, starts it, and swaps to the shell. Capture-open failures keep you here with the error shown inline (see §7).
2. **Shell** (session exists): slim left sidebar with four view entries — Dashboard, Topology, Events, PV Watch — content pane, and a persistent status bar: source description, session state, frames seen, malformed count. A Stop/New-session action returns to the start screen. One session at a time.

`MainWindowViewModel` runs one `DispatcherTimer` at 4 Hz calling `Refresh(session)` on the **active** view's VM only. Each view VM implements a small `IRefreshable` (`void Refresh(MonitorSession session)`), reads snapshots, and diffs into its observable state (update-in-place keyed by slave address / variable name so selection and scroll survive refreshes).

## 6. Views

- **Topology + slave detail.** Slave list ordered by configured address: AL-state badge (Init/PreOp/SafeOp/Op, color-coded, error flag), configured address, name (ENI-seeded, else "Slave N"), mailbox-protocol indicators from observed traffic. Selecting a row shows a detail pane: identity, AL-state history for that slave, WKC involvement, last mailbox activity (CoE/SoE summary strings the SDK already formats).
- **Dashboard.** Stat tiles mirroring the CLI live dashboard: Tx/Rx direction rates, estimated cycle time, WKC health, ring-loss count (TwinCAT "Lost Frames" equivalent), frame/datagram totals, malformed count. Numbers only in v1 — no charts.
- **Events.** Virtualized list over `SnapshotEvents(lastN)` (bounded tail), newest last with auto-scroll; scrolling up pauses auto-scroll until the user returns to the bottom. Filter chips by event category/severity.
- **PV Watch.** Requires ENI. Table over `ProcessImage.Current`: variable name, formatted value (including the SDK's CiA-402 status-word description where applicable), last-update timestamp, text filter box. Without an ENI: inline empty-state with a "Load ENI" action that re-seeds by starting a new session with the same source spec plus the ENI.

## 7. Error handling

- **Capture open fails** (typical: BPF permissions on macOS): session faults before the shell transition; the start screen shows the message inline plus a hint referencing `docs/tap-setup.md` (ChmodBPF).
- **Mid-session fault** (device vanished, truncated file): state → `Faulted`, dismissible banner with the message, views freeze on last good snapshots. The app never exits or discards observed state on capture errors; a new session can be started from the shell.
- **ENI parse failure**: error dialog; the session can proceed without ENI (PV watch stays in its empty state).
- **Per-frame decode errors**: counted (`MalformedFrames`), shown in the status bar and dashboard, never propagated.

## 8. Testing

TDD throughout (red-green-refactor), three layers, heaviest at the bottom:

1. **Session tests** (bulk of coverage): pcap fixtures generated with the existing `Synthesis` builders (`EtherCatFrameBuilder`, `SampleCapture`, `PcapFileWriter`) drive `MonitorSession` end-to-end. Assert: state machine transitions (Running→Completed, Stop, fault on bad file path), snapshot contents after completion, malformed-frame tolerance, double-start throws, dispose during Running cancels cleanly.
2. **View-model tests**: run a file session to completion, call `Refresh()`, assert observable state — dashboard tile values, topology rows and selection stability across refreshes, event filtering, PV rows and formatting, start-screen validation (no device selected, missing file).
3. **Headless smoke tests** (`Avalonia.Headless.XUnit`): app boots, start screen renders, navigation switches the four views.

Manual acceptance, as in M1: live capture against the DUALCOMM ETAP-1000 on `en11` — verify topology shows station 1001, ~4.4 ms cycle estimate, rates plausible vs. the CLI live dashboard on the same tap.

## 9. Milestones after v1 (ledger)

- ADS enrichment panel (names + IG 0x0C counters) with connection-config UI.
- Pcap replay pacing / timeline scrubbing.
- Frame-level browser; charts for rates/cycle jitter.
- App packaging (.app bundle / dotnet publish profiles).
