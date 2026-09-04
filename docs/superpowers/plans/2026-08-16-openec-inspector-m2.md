# OpenEC.Inspector (M2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `OpenEC.Inspector`, the Avalonia desktop GUI over the M1 `OpenEC.Monitor` SDK: live-NIC or pcap sessions with Dashboard, Topology, Events, and PV-Watch views.

**Architecture:** A `MonitorSession` lifecycle wrapper around the SDK's `EtherCatMonitor` facade (which already owns the capture source, `BusObserver`, and pump) drives everything; plain CommunityToolkit.Mvvm view-models poll `BusObserver` snapshots at 4 Hz via a single `DispatcherTimer` — the UI never subscribes to per-frame callbacks. Views are thin AXAML over the view-models.

**Tech Stack:** .NET 8, Avalonia 11.3.x (Fluent theme), CommunityToolkit.Mvvm 8.4.0, xUnit 2.9.3 + Avalonia.Headless.XUnit.

**Spec:** `docs/superpowers/specs/2026-08-16-openec-inspector-m2-design.md`

**Deliberate refinements vs. the spec** (same observable behavior, noted for reviewers):
1. Spec §4 sketches `MonitorSession` owning `ICaptureSource` + parse pump directly. The SDK's `EtherCatMonitor` facade (`src/OpenEC.Monitor/EtherCatMonitor.cs`) already implements exactly that pump, including malformed-frame counting. `MonitorSession` wraps it instead of duplicating it (DRY).
2. Spec §6 lists "WKC involvement" in the slave detail pane. The SDK attributes WKC mismatches to *datagram* addresses (`WkcMismatchDetected.Address` is a `uint` logical/physical datagram address), not to slaves, so per-slave WKC attribution is not derivable. It is dropped from the detail pane; the bus-level WKC counter lives on the Dashboard.
3. Spec §7's "ENI parse failure: error dialog" is rendered as an inline error message on the start screen (same information, no modal machinery).

## Global Constraints

- `TargetFramework` comes from the repo root `Directory.Build.props` (net8.0, nullable, implicit usings, latest lang) — do NOT set `<TargetFramework>` in new csproj files.
- All `Avalonia.*` package versions MUST be identical. Pin `11.3.2`; if NuGet restore reports NU1102 (version unavailable), run `dotnet package search Avalonia --take 3`, pick the newest stable 11.x, and use that same version for every `Avalonia.*` reference.
- `CommunityToolkit.Mvvm` = `8.4.0`. Test stack mirrors `tests/OpenEC.Monitor.Tests`: `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4, `coverlet.collector` 6.0.4.
- `OpenEC.Inspector` references `OpenEC.Monitor` ONLY. Never reference `OpenEC.Monitor.Ads` (passive-only v1).
- UI code reads `BusObserver` ONLY via `SnapshotSlaves()` / `SnapshotEvents()` / `Statistics` / `ProcessImage.Current` — never iterate `Observer.EventLog` or `Bus.Slaves` from view-models (single-writer contract).
- One `ICaptureSource` per session — `LiveCaptureSource` is not re-entrant; a new session always builds a fresh source (the `EtherCatMonitor.OpenLive/OpenFile` factories guarantee this).
- All user-visible number/time formatting uses `CultureInfo.InvariantCulture` (deterministic tests on any machine).
- Commit after every task. Commits are SSH-signed via 1Password — if `git commit` fails with `1Password: failed to fill whole buffer`, ask the user to unlock 1Password and retry; do not disable signing.
- Test fixtures: generate pcaps at test runtime with `OpenEC.Monitor.Synthesis.SampleCapture.WriteDemo(path)` (no binary fixtures); the ENI fixture is linked from `tests/OpenEC.Monitor.Tests/Fixtures/sample.eni.xml`.

## Demo-capture ground truth (used by assertions throughout)

`SampleCapture.WriteDemo(path)` (50 cycles, 1 ms cadence) produces **103 EtherCAT frames**. Combined with the `sample.eni.xml` fixture (4 slaves: 1001 "Term 1 (EK1100)", 1002 "Term 2 (EL1008)", 1003 "Term 3 (EL2008)", 1004 "Drive 4 (AX5101)"; LRW cnt=6, BRD cnt=4), a completed session yields:

- `Statistics`: `TotalFrames == 103`, `EtherCatFrames == 103`, `MalformedFrames == 0`, `WkcMismatches == 1`, `EstimatedCycleTime == 1.00 ms`.
- Slave 1004: `AlState == SafeOp`, `ErrorFlag == true` (from the FPRD 0x0130 read returning `0x14`).
- Events include: `SlaveStateChanged` (1004 → SafeOp, error), `WkcMismatchDetected` (Expected 6, Actual 5), `EmergencyReceived` (station 1004), `SoeErrorReceived` (station 1004, code 0x7009).
- `ProcessImage.Current` has exactly 5 variables:
  `Term 2 (EL1008).Channel 1.Input` = `true`, `Term 2 (EL1008).Channel 2.Input` = `false`, `Drive 4 (AX5101).Inputs.Statusword` = `(ushort)0x0637` (with non-null `Cia402Description`), `Term 3 (EL2008).Channel 1.Output` = `true`, `Drive 4 (AX5101).Outputs.Controlword` = `(ushort)0x000F`.

## File Structure

```
src/OpenEC.Inspector/
├── OpenEC.Inspector.csproj        # Avalonia app, refs OpenEC.Monitor
├── Program.cs                     # AppBuilder entry point
├── App.axaml / App.axaml.cs       # FluentTheme; wires MainWindowViewModel
├── Session/
│   ├── SourceSpec.cs              # Live(interface) | File(path)
│   └── MonitorSession.cs          # lifecycle wrapper over EtherCatMonitor
├── ViewModels/
│   ├── IRefreshable.cs            # void Refresh()
│   ├── DashboardViewModel.cs      # stat tiles from TrafficStatistics
│   ├── EventFormatter.cs          # MonitorEvent → category + description
│   ├── EventsViewModel.cs         # filtered event rows (+ CategoryFilter)
│   ├── TopologyViewModel.cs       # slave rows + SlaveDetailViewModel
│   ├── PvWatchViewModel.cs        # process-variable rows
│   ├── StartViewModel.cs          # source picking, ENI load, early-fault probe
│   ├── IFilePicker.cs             # testable file-dialog seam
│   └── MainWindowViewModel.cs     # shell: nav, tick, status, fault banner
└── Views/
    ├── MainWindow.axaml(.cs)      # shell layout + 4 Hz DispatcherTimer
    ├── StartView.axaml(.cs)
    ├── DashboardView.axaml(.cs)
    ├── TopologyView.axaml(.cs)
    ├── EventsView.axaml(.cs)      # auto-scroll code-behind
    ├── PvWatchView.axaml(.cs)
    └── StorageFilePicker.cs       # IFilePicker via Avalonia StorageProvider

tests/OpenEC.Inspector.Tests/
├── OpenEC.Inspector.Tests.csproj  # links sample.eni.xml fixture
├── TestAppBuilder.cs              # [AvaloniaTestApplication] headless setup
├── TestSessions.cs                # demo-pcap/session helpers
├── TestDoubles.cs                 # FakeFilePicker, test capture sources
├── Session/MonitorSessionTests.cs
├── Session/MonitorSessionEniTests.cs
├── ViewModels/DashboardViewModelTests.cs
├── ViewModels/EventsViewModelTests.cs
├── ViewModels/TopologyViewModelTests.cs
├── ViewModels/PvWatchViewModelTests.cs
├── ViewModels/StartViewModelTests.cs
├── ViewModels/MainWindowViewModelTests.cs
└── Ui/ShellSmokeTests.cs          # [AvaloniaFact] headless smoke
```

---

### Task 1: Project scaffolding

**Files:**
- Create: `src/OpenEC.Inspector/OpenEC.Inspector.csproj`
- Create: `src/OpenEC.Inspector/Program.cs`
- Create: `src/OpenEC.Inspector/App.axaml`, `src/OpenEC.Inspector/App.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/MainWindow.axaml`, `src/OpenEC.Inspector/Views/MainWindow.axaml.cs` (placeholder; replaced in Task 11)
- Create: `tests/OpenEC.Inspector.Tests/OpenEC.Inspector.Tests.csproj`
- Create: `tests/OpenEC.Inspector.Tests/TestAppBuilder.cs`
- Create: `tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs` (first smoke test; extended in Task 11)
- Modify: `OpenEC-Diagnostics.sln` (via `dotnet sln add`)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: buildable `OpenEC.Inspector` app project + `OpenEC.Inspector.Tests` with working `[AvaloniaFact]` infrastructure. Later tasks add files to these projects without touching the csproj files again (globbing covers new sources; the AXAML glob comes from the Avalonia NuGet).

- [ ] **Step 1: Create the app project file**

`src/OpenEC.Inspector/OpenEC.Inspector.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.2" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.2" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.2" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.2" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.2" Condition="'$(Configuration)' == 'Debug'" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenEC.Monitor\OpenEC.Monitor.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="OpenEC.Inspector.Tests" />
  </ItemGroup>

</Project>
```

(`OutputType` stays `WinExe` on all platforms — that is the Avalonia convention for "no console window"; macOS/Linux run it fine.)

- [ ] **Step 2: Create the entry point**

`src/OpenEC.Inspector/Program.cs`:

```csharp
using Avalonia;

namespace OpenEC.Inspector;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
```

- [ ] **Step 3: Create the App**

`src/OpenEC.Inspector/App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.App"
             RequestedThemeVariant="Default">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`src/OpenEC.Inspector/App.axaml.cs` (session wiring lands in Task 11; for now just show the window):

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 4: Create the placeholder MainWindow**

`src/OpenEC.Inspector/Views/MainWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="OpenEC.Inspector.Views.MainWindow"
        Title="OpenEC Inspector"
        Width="1100" Height="720">
  <TextBlock Text="OpenEC Inspector" VerticalAlignment="Center" HorizontalAlignment="Center" />
</Window>
```

`src/OpenEC.Inspector/Views/MainWindow.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace OpenEC.Inspector.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 5: Create the test project**

`tests/OpenEC.Inspector.Tests/OpenEC.Inspector.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia.Headless.XUnit" Version="11.3.2" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\OpenEC.Inspector\OpenEC.Inspector.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="..\OpenEC.Monitor.Tests\Fixtures\sample.eni.xml"
             Link="Fixtures\sample.eni.xml"
             CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Create the headless test bootstrap**

`tests/OpenEC.Inspector.Tests/TestAppBuilder.cs`:

```csharp
using Avalonia;
using Avalonia.Headless;
using OpenEC.Inspector;
using OpenEC.Inspector.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace OpenEC.Inspector.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
```

(Headless uses its own lifetime, so `App.OnFrameworkInitializationCompleted`'s desktop-lifetime branch is a no-op in tests — tests create windows directly.)

- [ ] **Step 7: Write the first smoke test**

`tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs`:

```csharp
using Avalonia.Headless.XUnit;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector.Tests.Ui;

public class ShellSmokeTests
{
    [AvaloniaFact]
    public void Main_window_constructs_and_shows()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("OpenEC Inspector", window.Title);
    }
}
```

- [ ] **Step 8: Add both projects to the solution**

Run:
```bash
cd ec-brain
dotnet sln add src/OpenEC.Inspector/OpenEC.Inspector.csproj tests/OpenEC.Inspector.Tests/OpenEC.Inspector.Tests.csproj
```

- [ ] **Step 9: Build and run the smoke test**

Run: `dotnet test tests/OpenEC.Inspector.Tests`
Expected: build succeeds; 1 test PASSES. (If restore fails on the Avalonia version, apply the Global Constraints version rule.)

- [ ] **Step 10: Verify the existing suite still passes**

Run: `dotnet test OpenEC-Diagnostics.sln`
Expected: all projects build; 91 existing tests + 1 new test pass.

- [ ] **Step 11: Commit**

```bash
git add src/OpenEC.Inspector tests/OpenEC.Inspector.Tests OpenEC-Diagnostics.sln
git commit -m "feat(inspector): scaffold Avalonia app and headless test project"
```

---

### Task 2: SourceSpec + MonitorSession (file happy path)

**Files:**
- Create: `src/OpenEC.Inspector/Session/SourceSpec.cs`
- Create: `src/OpenEC.Inspector/Session/MonitorSession.cs`
- Create: `tests/OpenEC.Inspector.Tests/TestSessions.cs`
- Test: `tests/OpenEC.Inspector.Tests/Session/MonitorSessionTests.cs`

**Interfaces:**
- Consumes (SDK): `EtherCatMonitor.OpenLive(string, EtherCatMonitorOptions)` / `.OpenFile(string, EtherCatMonitorOptions)` / `.FromSource(ICaptureSource, EtherCatMonitorOptions)`; `EtherCatMonitor.RunAsync(CancellationToken)`, `.Observer`, `.Statistics`, `.ProcessImage`, `.DisposeAsync()`; `EtherCatMonitorOptions { Eni }`; `SampleCapture.WriteDemo(string path)`.
- Produces (used by every later task):
  - `SourceSpec` — `abstract record` with `SourceSpec.Live(string InterfaceName)`, `SourceSpec.File(string Path)`, and `string Description { get; }`.
  - `enum SessionState { Idle, Running, Completed, Stopped, Faulted }`
  - `MonitorSession : IAsyncDisposable` — ctors `(SourceSpec, EniConfiguration? eni = null)` and `(EtherCatMonitor, string sourceDescription, EniConfiguration? eni = null)`; members `BusObserver Observer`, `TrafficStatistics Statistics`, `ProcessImage ProcessImage`, `EniConfiguration? Eni`, `SourceSpec? Source`, `string SourceDescription`, `SessionState State`, `Exception? Fault`, `Task Completion`, `long FramesSeen`, `long MalformedFrames`, `event Action<SessionState>? StateChanged`, `void Start()`, `Task StopAsync()`.
  - `TestSessions.WriteDemoPcap()`, `TestSessions.LoadFixtureEni()`, `TestSessions.RunFileSessionAsync(EniConfiguration?)` test helpers.

- [ ] **Step 1: Write the test helpers**

`tests/OpenEC.Inspector.Tests/TestSessions.cs`:

```csharp
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Inspector.Tests;

internal static class TestSessions
{
    public static string WriteDemoPcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-inspector-{Guid.NewGuid():N}.pcap");
        return SampleCapture.WriteDemo(path);
    }

    public static EniConfiguration LoadFixtureEni() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    public static async Task<MonitorSession> RunFileSessionAsync(EniConfiguration? eni = null)
    {
        var session = new MonitorSession(new SourceSpec.File(WriteDemoPcap()), eni);
        session.Start();
        await session.Completion;
        return session;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/OpenEC.Inspector.Tests/Session/MonitorSessionTests.cs`:

```csharp
using OpenEC.Inspector.Session;

namespace OpenEC.Inspector.Tests.Session;

public class MonitorSessionTests
{
    [Fact]
    public async Task File_session_pumps_to_completion()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Equal(SessionState.Completed, session.State);
        Assert.Null(session.Fault);
        Assert.Equal(103, session.FramesSeen);
        Assert.Equal(0, session.MalformedFrames);
        Assert.NotEmpty(session.Observer.SnapshotSlaves());
    }

    [Fact]
    public async Task Source_description_is_the_file_name()
    {
        var path = TestSessions.WriteDemoPcap();
        await using var session = new MonitorSession(new SourceSpec.File(path));

        Assert.Equal(Path.GetFileName(path), session.SourceDescription);
        Assert.Equal(new SourceSpec.File(path), session.Source);
    }

    [Fact]
    public void Live_source_description_is_the_interface_name() =>
        Assert.Equal("en11", new SourceSpec.Live("en11").Description);

    [Fact]
    public async Task Start_twice_throws()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public async Task State_changes_fire_the_event()
    {
        var states = new List<SessionState>();
        var path = TestSessions.WriteDemoPcap();
        await using var session = new MonitorSession(new SourceSpec.File(path));
        session.StateChanged += states.Add;

        session.Start();
        await session.Completion;

        Assert.Equal([SessionState.Running, SessionState.Completed], states);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MonitorSessionTests"`
Expected: FAIL to compile — `SourceSpec`, `MonitorSession` do not exist yet.

- [ ] **Step 4: Implement SourceSpec**

`src/OpenEC.Inspector/Session/SourceSpec.cs`:

```csharp
namespace OpenEC.Inspector.Session;

/// <summary>Where a session captures from. One spec → one fresh ICaptureSource.</summary>
public abstract record SourceSpec
{
    public sealed record Live(string InterfaceName) : SourceSpec;
    public sealed record File(string Path) : SourceSpec;

    public string Description => this switch
    {
        Live l => l.InterfaceName,
        File f => System.IO.Path.GetFileName(f.Path),
        _ => ToString()!,
    };
}
```

- [ ] **Step 5: Implement MonitorSession**

`src/OpenEC.Inspector/Session/MonitorSession.cs`:

```csharp
using OpenEC.Monitor;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Session;

public enum SessionState { Idle, Running, Completed, Stopped, Faulted }

/// <summary>Lifecycle wrapper around EtherCatMonitor: one capture source, one pump task,
/// one state machine. UI code must read the observer via snapshots only.</summary>
public sealed class MonitorSession : IAsyncDisposable
{
    private readonly EtherCatMonitor _monitor;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pump = Task.CompletedTask;

    public MonitorSession(SourceSpec source, EniConfiguration? eni = null)
        : this(CreateMonitor(source, eni), source.Description, eni) =>
        Source = source;

    /// <summary>Composition/test seam mirroring EtherCatMonitor.FromSource.</summary>
    public MonitorSession(EtherCatMonitor monitor, string sourceDescription, EniConfiguration? eni = null)
    {
        _monitor = monitor;
        SourceDescription = sourceDescription;
        Eni = eni;
    }

    private static EtherCatMonitor CreateMonitor(SourceSpec source, EniConfiguration? eni) => source switch
    {
        SourceSpec.Live l => EtherCatMonitor.OpenLive(l.InterfaceName, new EtherCatMonitorOptions { Eni = eni }),
        SourceSpec.File f => EtherCatMonitor.OpenFile(f.Path, new EtherCatMonitorOptions { Eni = eni }),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public BusObserver Observer => _monitor.Observer;
    public TrafficStatistics Statistics => _monitor.Statistics;
    public ProcessImage ProcessImage => _monitor.ProcessImage;
    public EniConfiguration? Eni { get; }
    public SourceSpec? Source { get; }
    public string SourceDescription { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public Exception? Fault { get; private set; }
    public Task Completion => _done.Task;
    public long FramesSeen => Statistics.TotalFrames;
    public long MalformedFrames => Statistics.MalformedFrames;

    /// <summary>Raised from the pump thread; UI subscribers must marshal to their own thread.</summary>
    public event Action<SessionState>? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (State != SessionState.Idle)
                throw new InvalidOperationException($"Session already started (state: {State}).");
            Transition(SessionState.Running);
        }
        _pump = Task.Run(async () =>
        {
            try
            {
                await _monitor.RunAsync(_cts.Token);
                CompleteWith(_cts.IsCancellationRequested ? SessionState.Stopped : SessionState.Completed);
            }
            catch (OperationCanceledException)
            {
                CompleteWith(SessionState.Stopped);
            }
            catch (Exception ex)
            {
                Fault = ex;
                CompleteWith(SessionState.Faulted);
            }
        });
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (State == SessionState.Idle)
            {
                Transition(SessionState.Stopped);
                _done.TrySetResult();
                return;
            }
        }
        _cts.Cancel();
        await Completion.ConfigureAwait(false);
    }

    private void CompleteWith(SessionState terminal)
    {
        lock (_gate)
        {
            if (State == SessionState.Running) Transition(terminal);
        }
        _done.TrySetResult();
    }

    private void Transition(SessionState next)
    {
        State = next;
        StateChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _pump.ConfigureAwait(false); } catch { /* terminal state already captured */ }
        await _monitor.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        lock (_gate)
        {
            if (State is SessionState.Idle or SessionState.Running) Transition(SessionState.Stopped);
        }
        _done.TrySetResult();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MonitorSessionTests"`
Expected: 5 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Inspector/Session tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): MonitorSession lifecycle over EtherCatMonitor"
```

---

### Task 3: MonitorSession stop, fault, and dispose paths

**Files:**
- Modify: `src/OpenEC.Inspector/Session/MonitorSession.cs` (only if a test exposes a gap — the Task 2 implementation is expected to already cover these paths)
- Create: `tests/OpenEC.Inspector.Tests/TestDoubles.cs`
- Test: `tests/OpenEC.Inspector.Tests/Session/MonitorSessionTests.cs` (extend)

**Interfaces:**
- Consumes: Task 2's `MonitorSession` second ctor; SDK `EtherCatMonitor.FromSource`, `ICaptureSource`, `RawFrame`.
- Produces (used by Tasks 9–10 tests): `FakeFilePicker(string? result)`, `BlockingCaptureSource`, `TriggeredFaultSource` in `OpenEC.Inspector.Tests.TestDoubles`.

- [ ] **Step 1: Write the test doubles**

`tests/OpenEC.Inspector.Tests/TestDoubles.cs` (`FakeFilePicker` references `IFilePicker`, which arrives in Task 9 — to keep this task compiling on its own, add only the capture sources now and note that `FakeFilePicker` is added in Task 9):

```csharp
using System.Runtime.CompilerServices;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector.Tests;

/// <summary>Parks until cancelled, then honors the cancellation. Simulates a quiet live NIC.</summary>
internal sealed class BlockingCaptureSource : ICaptureSource
{
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = ct.Register(() => parked.TrySetResult());
        await parked.Task;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Waits for <see cref="Trigger"/>, then throws. Simulates a mid-session capture fault.</summary>
internal sealed class TriggeredFaultSource : ICaptureSource
{
    public TaskCompletionSource Trigger { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Trigger.Task;
        // The condition is always true; it exists so the compiler keeps the iterator shape
        // without flagging the yield below as unreachable.
        if (Trigger.Task.IsCompleted) throw new IOException("boom");
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/OpenEC.Inspector.Tests/Session/MonitorSessionTests.cs`:

```csharp
    [Fact]
    public async Task Stop_during_a_running_session_yields_stopped()
    {
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");
        session.Start();

        await session.StopAsync();

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Null(session.Fault);
    }

    [Fact]
    public async Task Capture_fault_yields_faulted_with_the_exception()
    {
        var source = new TriggeredFaultSource();
        await using var session = new MonitorSession(EtherCatMonitor.FromSource(source), "test");
        var states = new List<SessionState>();
        session.StateChanged += states.Add;
        session.Start();

        source.Trigger.SetResult();
        await session.Completion;

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.IsType<IOException>(session.Fault);
        Assert.Equal("boom", session.Fault!.Message);
        Assert.Equal([SessionState.Running, SessionState.Faulted], states);
    }

    [Fact]
    public async Task Nonexistent_file_faults_instead_of_throwing()
    {
        await using var session = new MonitorSession(
            new SourceSpec.File("/nonexistent/no-such-capture.pcap"));
        session.Start();

        await session.Completion;

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.NotNull(session.Fault);
    }

    [Fact]
    public async Task Dispose_while_running_cancels_and_stops()
    {
        var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");
        session.Start();

        await session.DisposeAsync();

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.True(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task Stop_before_start_completes_immediately_as_stopped()
    {
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");

        await session.StopAsync();

        Assert.Equal(SessionState.Stopped, session.State);
    }
```

Also add `using OpenEC.Monitor;` to the test file's usings.

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MonitorSessionTests"`
Expected: all 10 PASS — Task 2's implementation already handles these paths. If any fail, fix `MonitorSession` (not the test) until green; the state machine in the spec (§4) is the contract.

- [ ] **Step 4: Commit**

```bash
git add tests/OpenEC.Inspector.Tests
git commit -m "test(inspector): session stop/fault/dispose coverage and capture-source doubles"
```

---

### Task 4: ENI-seeded session end-to-end assertions

**Files:**
- Test: `tests/OpenEC.Inspector.Tests/Session/MonitorSessionEniTests.cs`

**Interfaces:**
- Consumes: Task 2's `MonitorSession` + `TestSessions`; SDK `SlaveAlState`, `MonitorEvent` variants, `ProcessImage.Current`.
- Produces: pinned ground truth (see the "Demo-capture ground truth" section) that Tasks 5–8 view-model tests build on. No production code — this task locks the SDK-facing contract before any VM exists.

- [ ] **Step 1: Write the tests**

`tests/OpenEC.Inspector.Tests/Session/MonitorSessionEniTests.cs`:

```csharp
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.Session;

public class MonitorSessionEniTests
{
    [Fact]
    public async Task Eni_seeds_the_topology_with_all_configured_slaves()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var slaves = session.Observer.SnapshotSlaves();
        Assert.Equal(4, slaves.Count);
        var drive = Assert.Single(slaves, s => s.Address == 1004);
        Assert.Equal("Drive 4 (AX5101)", drive.DisplayName);
        Assert.Equal(SlaveAlState.SafeOp, drive.AlState);
        Assert.True(drive.ErrorFlag);
    }

    [Fact]
    public async Task Eni_session_raises_the_expected_event_kinds()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var events = session.Observer.SnapshotEvents();
        Assert.Contains(events, e => e is MonitorEvent.SlaveStateChanged
        {
            Address: 1004, NewState: SlaveAlState.SafeOp, ErrorFlag: true,
        });
        Assert.Contains(events, e => e is MonitorEvent.WkcMismatchDetected { Expected: 6, Actual: 5 });
        Assert.Contains(events, e => e is MonitorEvent.EmergencyReceived { StationAddress: 1004 });
        Assert.Contains(events, e => e is MonitorEvent.SoeErrorReceived { StationAddress: 1004, ErrorCode: 0x7009 });
        Assert.Equal(1, session.Statistics.WkcMismatches);
    }

    [Fact]
    public async Task Eni_session_decodes_all_five_process_variables()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var pv = session.ProcessImage.Current;
        Assert.Equal(5, pv.Count);
        Assert.Equal(true, pv["Term 2 (EL1008).Channel 1.Input"].Value);
        Assert.Equal(false, pv["Term 2 (EL1008).Channel 2.Input"].Value);
        Assert.Equal((ushort)0x0637, pv["Drive 4 (AX5101).Inputs.Statusword"].Value);
        Assert.Equal(true, pv["Term 3 (EL2008).Channel 1.Output"].Value);
        Assert.Equal((ushort)0x000F, pv["Drive 4 (AX5101).Outputs.Controlword"].Value);
        Assert.NotNull(pv["Drive 4 (AX5101).Inputs.Statusword"].Cia402Description);
    }

    [Fact]
    public async Task Without_eni_the_process_image_stays_empty()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Empty(session.ProcessImage.Current);
        Assert.Null(session.Eni);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MonitorSessionEniTests"`
Expected: 4 tests PASS with no production changes — these assert existing SDK behavior through the session. If an exact value fails (e.g. an event's error code), inspect the actual value, verify it against `SampleCapture.WriteDemo`'s construction (see the comments in `src/OpenEC.Monitor/Synthesis/SampleCapture.cs`), and correct the *expected* value only if the capture genuinely encodes something different — never loosen an assertion just to pass.

- [ ] **Step 3: Commit**

```bash
git add tests/OpenEC.Inspector.Tests
git commit -m "test(inspector): pin ENI-seeded session ground truth for the GUI"
```

---

### Task 5: DashboardViewModel

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/IRefreshable.cs`
- Create: `src/OpenEC.Inspector/ViewModels/DashboardViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/DashboardViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession.Statistics` (Task 2); `TrafficStatistics` members `OutboundCyclicFramesPerSecond`, `OutboundQueuedFramesPerSecond`, `ReturningFramesPerSecond`, `EstimatedCycleTime`, `WkcMismatches`, `SuspectedLostFrames`, `RingLostFrames`, `EtherCatFrames`, `TotalFrames`, `MalformedFrames`.
- Produces (bound by Task 11's `DashboardView`, driven by Task 10's shell): `interface IRefreshable { void Refresh(); }`; `DashboardViewModel(MonitorSession)` with string properties `CyclicTxRate`, `QueuedTxRate`, `RxRate`, `CycleTime`, `WkcMismatches`, `LostFrames`, `RingLostFrames`, `FrameTotals`, `Malformed`.

- [ ] **Step 1: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/DashboardViewModelTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public async Task Refresh_formats_the_demo_session_statistics()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new DashboardViewModel(session);

        vm.Refresh();

        Assert.Equal("1.00 ms", vm.CycleTime);
        Assert.Equal("1", vm.WkcMismatches);
        Assert.Equal("103 EtherCAT / 103 total", vm.FrameTotals);
        Assert.Equal("0", vm.Malformed);
        Assert.Equal("0", vm.RingLostFrames);
        Assert.EndsWith(" /s", vm.CyclicTxRate);
        Assert.EndsWith(" /s", vm.RxRate);
    }

    [Fact]
    public async Task Before_any_refresh_the_tiles_show_placeholders()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new DashboardViewModel(session);

        Assert.Equal("—", vm.CycleTime);
        Assert.Equal("—", vm.CyclicTxRate);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~DashboardViewModelTests"`
Expected: FAIL to compile — `DashboardViewModel` does not exist.

- [ ] **Step 3: Implement IRefreshable and DashboardViewModel**

`src/OpenEC.Inspector/ViewModels/IRefreshable.cs`:

```csharp
namespace OpenEC.Inspector.ViewModels;

/// <summary>A view-model the shell's 4 Hz timer refreshes while its view is active.</summary>
public interface IRefreshable
{
    void Refresh();
}
```

`src/OpenEC.Inspector/ViewModels/DashboardViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject, IRefreshable
{
    private const string Placeholder = "—"; // em dash

    private readonly MonitorSession _session;

    public DashboardViewModel(MonitorSession session) => _session = session;

    [ObservableProperty] private string _cyclicTxRate = Placeholder;
    [ObservableProperty] private string _queuedTxRate = Placeholder;
    [ObservableProperty] private string _rxRate = Placeholder;
    [ObservableProperty] private string _cycleTime = Placeholder;
    [ObservableProperty] private string _wkcMismatches = "0";
    [ObservableProperty] private string _lostFrames = "0";
    [ObservableProperty] private string _ringLostFrames = "0";
    [ObservableProperty] private string _frameTotals = "0";
    [ObservableProperty] private string _malformed = "0";

    public void Refresh()
    {
        var s = _session.Statistics;
        CyclicTxRate = FormatRate(s.OutboundCyclicFramesPerSecond);
        QueuedTxRate = FormatRate(s.OutboundQueuedFramesPerSecond);
        RxRate = FormatRate(s.ReturningFramesPerSecond);
        CycleTime = s.EstimatedCycleTime is { } cycle
            ? cycle.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms"
            : Placeholder;
        WkcMismatches = s.WkcMismatches.ToString("N0", CultureInfo.InvariantCulture);
        LostFrames = s.SuspectedLostFrames.ToString("N0", CultureInfo.InvariantCulture);
        RingLostFrames = s.RingLostFrames.ToString("N0", CultureInfo.InvariantCulture);
        FrameTotals = string.Create(CultureInfo.InvariantCulture,
            $"{s.EtherCatFrames:N0} EtherCAT / {s.TotalFrames:N0} total");
        Malformed = s.MalformedFrames.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatRate(double? rate) =>
        rate is { } r ? r.ToString("N0", CultureInfo.InvariantCulture) + " /s" : Placeholder;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~DashboardViewModelTests"`
Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): dashboard view-model with invariant-formatted stat tiles"
```

---

### Task 6: EventFormatter + EventsViewModel

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/EventFormatter.cs`
- Create: `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/EventsViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession.Observer.SnapshotEvents(int lastN)` (Task 2); `MonitorEvent` variants.
- Produces (bound by Task 11's `EventsView`; `EventFormatter` also consumed by Task 7):
  - `static class EventFormatter` — `string Category(MonitorEvent)` (one of `"State"`, `"State request"`, `"WKC"`, `"Emergency"`, `"SoE"`, `"Other"`), `string Describe(MonitorEvent)`.
  - `sealed record EventRow(string Time, string Category, string Description)`.
  - `sealed partial class CategoryFilter` — `string Name`, `bool IsEnabled` (observable).
  - `EventsViewModel(MonitorSession)` — `IReadOnlyList<CategoryFilter> Categories`, `ObservableCollection<EventRow> Rows`, `bool AutoScroll` (observable), `Refresh()`.

- [ ] **Step 1: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/EventsViewModelTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class EventsViewModelTests
{
    [Fact]
    public void Formatter_categorizes_and_describes_every_event_kind()
    {
        var ts = DateTimeOffset.UnixEpoch;

        var state = new MonitorEvent.SlaveStateChanged(ts, 1004, SlaveAlState.Op, SlaveAlState.SafeOp, true);
        Assert.Equal("State", EventFormatter.Category(state));
        Assert.Equal("Slave 1004: Op → SafeOp (error)", EventFormatter.Describe(state));

        var wkc = new MonitorEvent.WkcMismatchDetected(ts, OpenEC.Monitor.Protocol.EtherCatCommand.Lrw,
            0x01000000, 6, 5);
        Assert.Equal("WKC", EventFormatter.Category(wkc));
        Assert.Equal("Lrw @0x01000000: WKC 5 (expected 6)", EventFormatter.Describe(wkc));

        var emergency = new MonitorEvent.EmergencyReceived(ts, 1004, 0x8130, 0x81);
        Assert.Equal("Emergency", EventFormatter.Category(emergency));
        Assert.Equal("Slave 1004: CoE emergency 0x8130 (register 0x81)", EventFormatter.Describe(emergency));
    }

    [Fact]
    public async Task Refresh_fills_rows_from_the_event_log()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);

        vm.Refresh();

        Assert.True(vm.Rows.Count >= 4);
        Assert.Contains(vm.Rows, r => r.Category == "WKC");
        Assert.Contains(vm.Rows, r => r.Category == "Emergency");
        Assert.Contains(vm.Rows, r => r.Category == "SoE");
    }

    [Fact]
    public async Task Disabling_categories_filters_the_rows()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);
        vm.Refresh();

        foreach (var category in vm.Categories)
            category.IsEnabled = category.Name == "WKC";

        var row = Assert.Single(vm.Rows);
        Assert.Equal("WKC", row.Category);

        foreach (var category in vm.Categories)
            category.IsEnabled = true;
        Assert.True(vm.Rows.Count >= 4);
    }

    [Fact]
    public async Task Unchanged_snapshot_does_not_rebuild_rows()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);
        vm.Refresh();
        var changes = 0;
        vm.Rows.CollectionChanged += (_, _) => changes++;

        vm.Refresh();

        Assert.Equal(0, changes);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~EventsViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement EventFormatter**

`src/OpenEC.Inspector/ViewModels/EventFormatter.cs`:

```csharp
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public static class EventFormatter
{
    public static string Category(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged => "State",
        MonitorEvent.StateChangeRequested => "State request",
        MonitorEvent.WkcMismatchDetected => "WKC",
        MonitorEvent.EmergencyReceived => "Emergency",
        MonitorEvent.SoeErrorReceived => "SoE",
        _ => "Other",
    };

    public static string Describe(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s =>
            $"Slave {s.Address}: {s.OldState} → {s.NewState}{(s.ErrorFlag ? " (error)" : "")}",
        MonitorEvent.StateChangeRequested r => $"Slave {r.Address}: requested {r.RequestedState}",
        MonitorEvent.WkcMismatchDetected w =>
            $"{w.Command} @0x{w.Address:X8}: WKC {w.Actual} (expected {w.Expected})",
        MonitorEvent.EmergencyReceived em =>
            $"Slave {em.StationAddress}: CoE emergency 0x{em.ErrorCode:X4} (register 0x{em.ErrorRegister:X2})",
        MonitorEvent.SoeErrorReceived so =>
            $"Slave {so.StationAddress}: SoE error 0x{so.ErrorCode:X4} on {so.IdnLabel} ({so.OpCode})",
        _ => e.ToString()!,
    };
}
```

(Interpolated numbers here are integers/hex — culture-safe without explicit `CultureInfo`.)

- [ ] **Step 4: Implement EventsViewModel**

`src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed record EventRow(string Time, string Category, string Description);

public sealed partial class CategoryFilter : ObservableObject
{
    private readonly Action _onChanged;

    public CategoryFilter(string name, Action onChanged)
    {
        Name = name;
        _onChanged = onChanged;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isEnabled = true;

    partial void OnIsEnabledChanged(bool value) => _onChanged();
}

public sealed partial class EventsViewModel : ObservableObject, IRefreshable
{
    private const int MaxRows = 500;

    private static readonly string[] CategoryNames =
        ["State", "State request", "WKC", "Emergency", "SoE"];

    private readonly MonitorSession _session;
    private (int Count, DateTimeOffset? Last) _lastKey = (-1, null);

    public EventsViewModel(MonitorSession session)
    {
        _session = session;
        Categories = CategoryNames.Select(n => new CategoryFilter(n, OnFilterChanged)).ToList();
    }

    public IReadOnlyList<CategoryFilter> Categories { get; }
    public ObservableCollection<EventRow> Rows { get; } = [];

    [ObservableProperty] private bool _autoScroll = true;

    public void Refresh()
    {
        var events = _session.Observer.SnapshotEvents(MaxRows);
        var key = (events.Count, events.Count > 0 ? events[^1].Timestamp : (DateTimeOffset?)null);
        if (key == _lastKey) return;
        _lastKey = key;
        Rebuild(events);
    }

    private void OnFilterChanged()
    {
        _lastKey = (-1, null);
        Refresh();
    }

    private void Rebuild(IReadOnlyList<MonitorEvent> events)
    {
        Rows.Clear();
        foreach (var e in events)
        {
            var category = EventFormatter.Category(e);
            var enabled = Categories.FirstOrDefault(c => c.Name == category)?.IsEnabled ?? true;
            if (!enabled) continue;
            Rows.Add(new EventRow(
                e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                category,
                EventFormatter.Describe(e)));
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~EventsViewModelTests"`
Expected: 4 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): event log view-model with category filters"
```

---

### Task 7: TopologyViewModel + slave detail

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/TopologyViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession.Observer.SnapshotSlaves()` / `.SnapshotEvents()` (Task 2); `EventFormatter.Describe` (Task 6); `SlaveStatus.DisplayName`.
- Produces (bound by Task 11's `TopologyView`):
  - `sealed partial class SlaveRowViewModel` — `ushort Address` (init), observable `string Name`, `string State`, `bool HasError`, `string MailboxProtocols`, `string LastSeen`.
  - `sealed record SlaveDetailViewModel(string Title, string Identity, IReadOnlyList<string> StateHistory, IReadOnlyList<string> MailboxActivity)`.
  - `TopologyViewModel(MonitorSession)` — `ObservableCollection<SlaveRowViewModel> Slaves`, observable `SlaveRowViewModel? SelectedSlave`, observable `SlaveDetailViewModel? Detail`, `Refresh()`.

- [ ] **Step 1: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/TopologyViewModelTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class TopologyViewModelTests
{
    [Fact]
    public async Task Refresh_lists_all_eni_slaves_ordered_by_address()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new TopologyViewModel(session);

        vm.Refresh();

        Assert.Equal(4, vm.Slaves.Count);
        Assert.Equal([1001, 1002, 1003, 1004], vm.Slaves.Select(s => (int)s.Address).ToArray());
        Assert.Equal("Term 1 (EK1100)", vm.Slaves[0].Name);
    }

    [Fact]
    public async Task The_faulted_drive_shows_state_error_and_mailbox_protocols()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new TopologyViewModel(session);

        vm.Refresh();

        var drive = vm.Slaves.Single(s => s.Address == 1004);
        Assert.Equal("SafeOp", drive.State);
        Assert.True(drive.HasError);
        Assert.Contains("CoE", drive.MailboxProtocols);
        Assert.Contains("SoE", drive.MailboxProtocols);
    }

    [Fact]
    public async Task Selection_survives_refresh_and_rows_are_updated_in_place()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new TopologyViewModel(session);
        vm.Refresh();
        var selected = vm.Slaves.Single(s => s.Address == 1004);
        vm.SelectedSlave = selected;

        vm.Refresh();

        Assert.Same(selected, vm.SelectedSlave);
        Assert.Same(selected, vm.Slaves.Single(s => s.Address == 1004));
        Assert.Equal(4, vm.Slaves.Count);
    }

    [Fact]
    public async Task Selecting_a_slave_builds_its_detail_from_events()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new TopologyViewModel(session);
        vm.Refresh();

        vm.SelectedSlave = vm.Slaves.Single(s => s.Address == 1004);

        Assert.NotNull(vm.Detail);
        Assert.Equal("Drive 4 (AX5101)", vm.Detail!.Title);
        Assert.NotEmpty(vm.Detail.StateHistory);
        Assert.Equal(2, vm.Detail.MailboxActivity.Count); // one CoE emergency + one SoE error
    }

    [Fact]
    public async Task Clearing_the_selection_clears_the_detail()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new TopologyViewModel(session);
        vm.Refresh();
        vm.SelectedSlave = vm.Slaves[0];

        vm.SelectedSlave = null;

        Assert.Null(vm.Detail);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement TopologyViewModel**

`src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class SlaveRowViewModel : ObservableObject
{
    public required ushort Address { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _state = "Unknown";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _mailboxProtocols = "";
    [ObservableProperty] private string _lastSeen = "—";
}

public sealed record SlaveDetailViewModel(
    string Title,
    string Identity,
    IReadOnlyList<string> StateHistory,
    IReadOnlyList<string> MailboxActivity)
{
    public static SlaveDetailViewModel Build(SlaveStatus? status, IReadOnlyList<MonitorEvent> events)
    {
        var identity = status is { VendorId: { } vendor, ProductCode: { } product }
            ? string.Create(CultureInfo.InvariantCulture,
                $"Vendor 0x{vendor:X8} · Product 0x{product:X8} · Rev 0x{status.Revision ?? 0:X8}")
            : "Identity not observed";
        return new SlaveDetailViewModel(
            status?.DisplayName ?? "Unknown slave",
            identity,
            events.OfType<MonitorEvent.SlaveStateChanged>().Select(EventFormatter.Describe).ToList(),
            events.Where(e => e is MonitorEvent.EmergencyReceived or MonitorEvent.SoeErrorReceived)
                .Select(EventFormatter.Describe).ToList());
    }
}

public sealed partial class TopologyViewModel : ObservableObject, IRefreshable
{
    private readonly MonitorSession _session;

    public TopologyViewModel(MonitorSession session) => _session = session;

    public ObservableCollection<SlaveRowViewModel> Slaves { get; } = [];

    [ObservableProperty] private SlaveRowViewModel? _selectedSlave;
    [ObservableProperty] private SlaveDetailViewModel? _detail;

    partial void OnSelectedSlaveChanged(SlaveRowViewModel? value) => UpdateDetail();

    public void Refresh()
    {
        var snapshot = _session.Observer.SnapshotSlaves().OrderBy(s => s.Address).ToList();
        var mailbox = MailboxProtocolsByAddress();

        foreach (var status in snapshot)
        {
            var row = Slaves.FirstOrDefault(r => r.Address == status.Address);
            if (row is null)
            {
                row = new SlaveRowViewModel { Address = status.Address };
                Slaves.Insert(Slaves.TakeWhile(r => r.Address < status.Address).Count(), row);
            }
            row.Name = status.DisplayName;
            row.State = status.AlState.ToString();
            row.HasError = status.ErrorFlag;
            row.MailboxProtocols = mailbox.TryGetValue(status.Address, out var protocols)
                ? string.Join(" ", protocols.Order())
                : "";
            row.LastSeen = status.LastSeen?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";
        }
        UpdateDetail();
    }

    private Dictionary<ushort, HashSet<string>> MailboxProtocolsByAddress()
    {
        var result = new Dictionary<ushort, HashSet<string>>();
        foreach (var e in _session.Observer.SnapshotEvents())
        {
            var (address, protocol) = e switch
            {
                MonitorEvent.EmergencyReceived em => ((ushort?)em.StationAddress, "CoE"),
                MonitorEvent.SoeErrorReceived so => ((ushort?)so.StationAddress, "SoE"),
                _ => (null, ""),
            };
            if (address is { } a)
            {
                if (!result.TryGetValue(a, out var set)) result[a] = set = [];
                set.Add(protocol);
            }
        }
        return result;
    }

    private void UpdateDetail()
    {
        if (SelectedSlave is null)
        {
            Detail = null;
            return;
        }
        var address = SelectedSlave.Address;
        var status = _session.Observer.SnapshotSlaves().FirstOrDefault(s => s.Address == address);
        var events = _session.Observer.SnapshotEvents()
            .Where(e => AddressOf(e) == address).ToList();
        Detail = SlaveDetailViewModel.Build(status, events);
    }

    private static ushort? AddressOf(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s => s.Address,
        MonitorEvent.StateChangeRequested r => r.Address,
        MonitorEvent.EmergencyReceived em => em.StationAddress,
        MonitorEvent.SoeErrorReceived so => so.StationAddress,
        _ => null,
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewModelTests"`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): topology view-model with in-place rows and slave detail"
```

---

### Task 8: PvWatchViewModel

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/PvWatchViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/PvWatchViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession.ProcessImage.Current`, `MonitorSession.Eni` (Task 2); `VariableValue` (`Variable.Name`, `Value`, `Timestamp`, `Cia402Description`).
- Produces (bound by Task 11's `PvWatchView`; the load-ENI callback is provided by Task 10's shell):
  - `sealed partial class PvRowViewModel` — `string Name` (init), observable `string Value`, `string Updated`.
  - `PvWatchViewModel(MonitorSession session, Func<Task> requestLoadEni)` — `bool HasEni`, `ObservableCollection<PvRowViewModel> Rows`, observable `string FilterText`, `LoadEniCommand` (async), `Refresh()`, `internal static string FormatValue(VariableValue)`.

- [ ] **Step 1: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/PvWatchViewModelTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class PvWatchViewModelTests
{
    private static readonly Func<Task> NoLoad = () => Task.CompletedTask;

    [Fact]
    public async Task Refresh_lists_all_variables_sorted_by_name()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new PvWatchViewModel(session, NoLoad);

        vm.Refresh();

        Assert.True(vm.HasEni);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(
            [
                "Drive 4 (AX5101).Inputs.Statusword",
                "Drive 4 (AX5101).Outputs.Controlword",
                "Term 2 (EL1008).Channel 1.Input",
                "Term 2 (EL1008).Channel 2.Input",
                "Term 3 (EL2008).Channel 1.Output",
            ],
            vm.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task Values_format_with_hex_bool_and_cia402_description()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new PvWatchViewModel(session, NoLoad);

        vm.Refresh();

        var statusword = vm.Rows.Single(r => r.Name.EndsWith("Statusword"));
        Assert.StartsWith("0x0637 (1591)", statusword.Value);
        Assert.Contains(" — ", statusword.Value); // CiA-402 description appended
        Assert.Equal("TRUE", vm.Rows.Single(r => r.Name.Contains("Channel 1.Input")).Value);
        Assert.Equal("FALSE", vm.Rows.Single(r => r.Name.Contains("Channel 2.Input")).Value);
    }

    [Fact]
    public async Task Filter_narrows_rows_case_insensitively()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new PvWatchViewModel(session, NoLoad);
        vm.Refresh();

        vm.FilterText = "statusword";

        var row = Assert.Single(vm.Rows);
        Assert.EndsWith("Statusword", row.Name);

        vm.FilterText = "";
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public async Task Without_eni_the_view_reports_no_eni_and_stays_empty()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new PvWatchViewModel(session, NoLoad);

        vm.Refresh();

        Assert.False(vm.HasEni);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Load_eni_command_invokes_the_callback()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var invoked = false;
        var vm = new PvWatchViewModel(session, () => { invoked = true; return Task.CompletedTask; });

        await vm.LoadEniCommand.ExecuteAsync(null);

        Assert.True(invoked);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~PvWatchViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement PvWatchViewModel**

`src/OpenEC.Inspector/ViewModels/PvWatchViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class PvRowViewModel : ObservableObject
{
    public required string Name { get; init; }

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _updated = "";
}

public sealed partial class PvWatchViewModel : ObservableObject, IRefreshable
{
    private readonly MonitorSession _session;
    private readonly Func<Task> _requestLoadEni;

    public PvWatchViewModel(MonitorSession session, Func<Task> requestLoadEni)
    {
        _session = session;
        _requestLoadEni = requestLoadEni;
    }

    public bool HasEni => _session.Eni is not null;
    public ObservableCollection<PvRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";

    partial void OnFilterTextChanged(string value) => Refresh();

    [RelayCommand]
    private Task LoadEniAsync() => _requestLoadEni();

    public void Refresh()
    {
        var wanted = _session.ProcessImage.Current.Values
            .Where(v => v.Variable.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Variable.Name, StringComparer.Ordinal)
            .ToList();

        if (wanted.Count != Rows.Count ||
            !wanted.Select(v => v.Variable.Name).SequenceEqual(Rows.Select(r => r.Name)))
        {
            Rows.Clear();
            foreach (var v in wanted) Rows.Add(new PvRowViewModel { Name = v.Variable.Name });
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            Rows[i].Value = FormatValue(wanted[i]);
            Rows[i].Updated = wanted[i].Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }

    internal static string FormatValue(VariableValue v)
    {
        var text = v.Value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            ushort word => string.Create(CultureInfo.InvariantCulture, $"0x{word:X4} ({word})"),
            _ => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
        };
        return v.Cia402Description is { } description ? $"{text} — {description}" : text;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~PvWatchViewModelTests"`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): process-variable watch view-model"
```

---

### Task 9: StartViewModel + IFilePicker

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/IFilePicker.cs`
- Create: `src/OpenEC.Inspector/ViewModels/StartViewModel.cs`
- Modify: `tests/OpenEC.Inspector.Tests/TestDoubles.cs` (add `FakeFilePicker`)
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/StartViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession`, `SourceSpec`, `SessionState` (Task 2); `EniConfiguration.Load(string)`; test doubles (Task 3).
- Produces (consumed by Task 10's shell and Task 11's `StartView`):
  - `interface IFilePicker { Task<string?> PickFileAsync(string title, params string[] extensions); }`
  - `StartViewModel(Func<IReadOnlyList<(string Name, string? Description)>> listDevices, Func<SourceSpec, EniConfiguration?, MonitorSession> createSession, IFilePicker filePicker, Action<MonitorSession> onStarted, TimeSpan? earlyFaultProbe = null)` — `ObservableCollection<string> Devices`, observable `string? SelectedDevice`, `string? PcapPath`, `string? EniPath`, `string? ErrorMessage`, `bool IsStarting`; commands `RefreshDevicesCommand`, `BrowsePcapCommand`, `BrowseEniCommand`, `StartLiveCommand`, `StartFileCommand`; `internal static string FormatFault(Exception)`.
  - `FakeFilePicker(string? result = null) : IFilePicker` in TestDoubles.

- [ ] **Step 1: Add FakeFilePicker to the test doubles**

Append to `tests/OpenEC.Inspector.Tests/TestDoubles.cs` (add `using OpenEC.Inspector.ViewModels;`):

```csharp
internal sealed class FakeFilePicker(string? result = null) : IFilePicker
{
    public Task<string?> PickFileAsync(string title, params string[] extensions) =>
        Task.FromResult(result);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/StartViewModelTests.cs`:

```csharp
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class StartViewModelTests
{
    private static StartViewModel Create(
        Action<MonitorSession>? onStarted = null,
        IFilePicker? picker = null,
        Func<SourceSpec, OpenEC.Monitor.Eni.EniConfiguration?, MonitorSession>? factory = null) =>
        new(
            () => [("en11", "ETAP tap"), ("en0", null)],
            factory ?? ((spec, eni) => new MonitorSession(spec, eni)),
            picker ?? new FakeFilePicker(),
            onStarted ?? (_ => { }),
            earlyFaultProbe: TimeSpan.FromSeconds(2));

    [Fact]
    public void Devices_are_listed_on_construction()
    {
        var vm = Create();

        Assert.Equal(["en11", "en0"], vm.Devices);
    }

    [Fact]
    public async Task Start_live_without_a_selected_device_reports_an_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);

        await vm.StartLiveCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Start_file_with_a_missing_path_reports_an_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = "/nonexistent/nope.pcap";

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Start_file_hands_a_completed_demo_session_to_the_shell()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.NotNull(started);
        Assert.Equal(SessionState.Completed, started!.State);
        Assert.Null(vm.ErrorMessage);
        await started.DisposeAsync();
    }

    [Fact]
    public async Task A_garbage_file_faults_early_and_stays_on_the_start_screen()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        var garbage = Path.Combine(Path.GetTempPath(), $"openec-garbage-{Guid.NewGuid():N}.pcap");
        await File.WriteAllTextAsync(garbage, "this is not a capture file");
        vm.PcapPath = garbage;

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task An_invalid_eni_blocks_the_start_with_an_inline_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();
        var badEni = Path.Combine(Path.GetTempPath(), $"openec-bad-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(badEni, "<not-an-eni>");
        vm.EniPath = badEni;

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.Contains("ENI", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_valid_eni_is_loaded_into_the_session()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();
        vm.EniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.NotNull(started!.Eni);
        await started.DisposeAsync();
    }

    [Fact]
    public async Task Browse_commands_fill_the_paths_from_the_picker()
    {
        var vm = Create(picker: new FakeFilePicker("/tmp/picked.pcap"));

        await vm.BrowsePcapCommand.ExecuteAsync(null);

        Assert.Equal("/tmp/picked.pcap", vm.PcapPath);
    }

    [Fact]
    public void Permission_faults_get_the_tap_setup_hint()
    {
        var hinted = StartViewModel.FormatFault(new IOException("en11: Permission denied (BPF)"));

        Assert.Contains("tap-setup.md", hinted);
        Assert.DoesNotContain("tap-setup.md", StartViewModel.FormatFault(new IOException("other")));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~StartViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement IFilePicker and StartViewModel**

`src/OpenEC.Inspector/ViewModels/IFilePicker.cs`:

```csharp
namespace OpenEC.Inspector.ViewModels;

/// <summary>Testable seam over the platform file dialog. Returns null when cancelled.</summary>
public interface IFilePicker
{
    Task<string?> PickFileAsync(string title, params string[] extensions);
}
```

`src/OpenEC.Inspector/ViewModels/StartViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class StartViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<(string Name, string? Description)>> _listDevices;
    private readonly Func<SourceSpec, EniConfiguration?, MonitorSession> _createSession;
    private readonly IFilePicker _filePicker;
    private readonly Action<MonitorSession> _onStarted;
    private readonly TimeSpan _earlyFaultProbe;

    public StartViewModel(
        Func<IReadOnlyList<(string Name, string? Description)>> listDevices,
        Func<SourceSpec, EniConfiguration?, MonitorSession> createSession,
        IFilePicker filePicker,
        Action<MonitorSession> onStarted,
        TimeSpan? earlyFaultProbe = null)
    {
        _listDevices = listDevices;
        _createSession = createSession;
        _filePicker = filePicker;
        _onStarted = onStarted;
        _earlyFaultProbe = earlyFaultProbe ?? TimeSpan.FromMilliseconds(500);
        RefreshDevices();
    }

    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty] private string? _selectedDevice;
    [ObservableProperty] private string? _pcapPath;
    [ObservableProperty] private string? _eniPath;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isStarting;

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        ErrorMessage = null;
        try
        {
            foreach (var (name, _) in _listDevices()) Devices.Add(name);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BrowsePcapAsync() =>
        PcapPath = await _filePicker.PickFileAsync("Open capture", "pcap", "pcapng") ?? PcapPath;

    [RelayCommand]
    private async Task BrowseEniAsync() =>
        EniPath = await _filePicker.PickFileAsync("Load ENI", "xml") ?? EniPath;

    [RelayCommand]
    private Task StartLiveAsync() =>
        SelectedDevice is null
            ? SetError("Select a capture interface first.")
            : StartAsync(new SourceSpec.Live(SelectedDevice));

    [RelayCommand]
    private Task StartFileAsync() =>
        string.IsNullOrWhiteSpace(PcapPath) || !System.IO.File.Exists(PcapPath)
            ? SetError("Choose an existing .pcap/.pcapng file first.")
            : StartAsync(new SourceSpec.File(PcapPath));

    private Task SetError(string message)
    {
        ErrorMessage = message;
        return Task.CompletedTask;
    }

    private async Task StartAsync(SourceSpec spec)
    {
        ErrorMessage = null;
        EniConfiguration? eni = null;
        if (!string.IsNullOrWhiteSpace(EniPath))
        {
            try
            {
                eni = EniConfiguration.Load(EniPath);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"ENI could not be loaded: {ex.Message}";
                return;
            }
        }

        IsStarting = true;
        try
        {
            var session = _createSession(spec, eni);
            session.Start();
            // Early-fault probe (mirrors the CLI's live command): a bad interface or file
            // faults within moments — don't switch to the shell just to show a dead session.
            await Task.WhenAny(session.Completion, Task.Delay(_earlyFaultProbe));
            if (session.State == SessionState.Faulted)
            {
                ErrorMessage = FormatFault(session.Fault!);
                await session.DisposeAsync();
                return;
            }
            _onStarted(session);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsStarting = false;
        }
    }

    internal static string FormatFault(Exception ex) =>
        ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("BPF", StringComparison.OrdinalIgnoreCase)
            ? $"{ex.Message}\nOn macOS, capture needs BPF access — see docs/tap-setup.md (ChmodBPF)."
            : ex.Message;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~StartViewModelTests"`
Expected: 9 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): start-screen view-model with early-fault probe"
```

---

### Task 10: MainWindowViewModel (shell)

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2, 5–9.
- Produces (bound by Task 11's `MainWindow`):
  - `enum InspectorSection { Dashboard, Topology, Events, PvWatch }`
  - `MainWindowViewModel(Func<IReadOnlyList<(string Name, string? Description)>> listDevices, Func<SourceSpec, EniConfiguration?, MonitorSession> createSession, IFilePicker filePicker, Action<Action>? marshal = null, TimeSpan? earlyFaultProbe = null)` — `StartViewModel Start`, `MonitorSession? Session`, per-section VM properties, observable `object CurrentPage`, `bool HasSession`, `InspectorSection SelectedSection`, `string StatusText`, `string? FaultMessage`; commands `SelectSectionCommand`, `StopSessionCommand`, `DismissFaultCommand`; `void Tick()` (called by the view's `DispatcherTimer` every 250 ms).

- [ ] **Step 1: Write the failing tests**

`tests/OpenEC.Inspector.Tests/ViewModels/MainWindowViewModelTests.cs`:

```csharp
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor;

namespace OpenEC.Inspector.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel Create(
        Func<SourceSpec, OpenEC.Monitor.Eni.EniConfiguration?, MonitorSession>? factory = null,
        IFilePicker? picker = null) =>
        new(
            () => [],
            factory ?? ((spec, eni) => new MonitorSession(spec, eni)),
            picker ?? new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromSeconds(2));

    private static async Task<MainWindowViewModel> CreateWithDemoSessionAsync(
        string? eniPath = null, IFilePicker? picker = null)
    {
        var vm = Create(picker: picker);
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        vm.Start.EniPath = eniPath;
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public void Boots_to_the_start_screen_with_no_session()
    {
        var vm = Create();

        Assert.Same(vm.Start, vm.CurrentPage);
        Assert.False(vm.HasSession);
        Assert.Equal("No session", vm.StatusText);
    }

    [Fact]
    public async Task Starting_a_file_session_switches_to_the_dashboard()
    {
        var vm = await CreateWithDemoSessionAsync();

        Assert.True(vm.HasSession);
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
        Assert.NotNull(vm.Session);

        vm.Tick();
        Assert.Contains("103", vm.StatusText);
        Assert.Contains("completed", vm.StatusText);
    }

    [Fact]
    public async Task Section_selection_swaps_the_current_page()
    {
        var vm = await CreateWithDemoSessionAsync();

        vm.SelectSectionCommand.Execute(InspectorSection.Topology);
        Assert.IsType<TopologyViewModel>(vm.CurrentPage);

        vm.SelectSectionCommand.Execute(InspectorSection.Events);
        Assert.IsType<EventsViewModel>(vm.CurrentPage);

        vm.SelectSectionCommand.Execute(InspectorSection.PvWatch);
        Assert.IsType<PvWatchViewModel>(vm.CurrentPage);
    }

    [Fact]
    public async Task Stopping_the_session_returns_to_a_fresh_start_screen()
    {
        var vm = await CreateWithDemoSessionAsync();
        var firstStart = vm.Start;

        await vm.StopSessionCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Null(vm.Session);
        Assert.NotSame(firstStart, vm.Start);
        Assert.Same(vm.Start, vm.CurrentPage);
        Assert.Equal("No session", vm.StatusText);
    }

    [Fact]
    public async Task A_mid_session_fault_raises_the_banner_via_marshal()
    {
        var source = new TriggeredFaultSource();
        // Short probe: the parked source never completes early, so the default 2 s probe
        // would just add dead wait time to this test.
        var vm = new MainWindowViewModel(
            () => [],
            (_, eni) => new MonitorSession(EtherCatMonitor.FromSource(source), "fake", eni),
            new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromMilliseconds(50));
        vm.Start.PcapPath = TestSessions.WriteDemoPcap(); // path only satisfies validation
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        Assert.True(vm.HasSession);

        source.Trigger.SetResult();
        await vm.Session!.Completion;

        Assert.Equal("boom", vm.FaultMessage);
        vm.DismissFaultCommand.Execute(null);
        Assert.Null(vm.FaultMessage);
    }

    [Fact]
    public async Task Load_eni_from_pv_watch_restarts_the_session_with_the_eni()
    {
        var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        var vm = await CreateWithDemoSessionAsync(picker: new FakeFilePicker(eniPath));
        vm.SelectSectionCommand.Execute(InspectorSection.PvWatch);
        var pvWatch = (PvWatchViewModel)vm.CurrentPage;
        Assert.False(pvWatch.HasEni);

        await pvWatch.LoadEniCommand.ExecuteAsync(null);
        await vm.Session!.Completion;

        Assert.Equal(InspectorSection.PvWatch, vm.SelectedSection);
        var reloaded = (PvWatchViewModel)vm.CurrentPage;
        Assert.True(reloaded.HasEni);
        reloaded.Refresh();
        Assert.Equal(5, reloaded.Rows.Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement MainWindowViewModel**

`src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.ViewModels;

public enum InspectorSection { Dashboard, Topology, Events, PvWatch }

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<(string Name, string? Description)>> _listDevices;
    private readonly Func<SourceSpec, EniConfiguration?, MonitorSession> _createSession;
    private readonly IFilePicker _filePicker;
    private readonly Action<Action> _marshal;
    private readonly TimeSpan? _earlyFaultProbe;

    public MainWindowViewModel(
        Func<IReadOnlyList<(string Name, string? Description)>> listDevices,
        Func<SourceSpec, EniConfiguration?, MonitorSession> createSession,
        IFilePicker filePicker,
        Action<Action>? marshal = null,
        TimeSpan? earlyFaultProbe = null)
    {
        _listDevices = listDevices;
        _createSession = createSession;
        _filePicker = filePicker;
        _marshal = marshal ?? (action => action());
        _earlyFaultProbe = earlyFaultProbe;
        Start = NewStartViewModel();
        _currentPage = Start;
    }

    public StartViewModel Start { get; private set; }
    public MonitorSession? Session { get; private set; }
    public DashboardViewModel? Dashboard { get; private set; }
    public TopologyViewModel? Topology { get; private set; }
    public EventsViewModel? Events { get; private set; }
    public PvWatchViewModel? PvWatch { get; private set; }

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private InspectorSection _selectedSection = InspectorSection.Dashboard;
    [ObservableProperty] private string _statusText = "No session";
    [ObservableProperty] private string? _faultMessage;

    private StartViewModel NewStartViewModel() =>
        new(_listDevices, _createSession, _filePicker, OnSessionStarted, _earlyFaultProbe);

    private void OnSessionStarted(MonitorSession session)
    {
        Session = session;
        Dashboard = new DashboardViewModel(session);
        Topology = new TopologyViewModel(session);
        Events = new EventsViewModel(session);
        PvWatch = new PvWatchViewModel(session, RestartWithEniAsync);
        session.StateChanged += state => _marshal(() =>
        {
            if (state == SessionState.Faulted) FaultMessage = session.Fault?.Message;
            UpdateStatus();
        });
        HasSession = true;
        FaultMessage = null;
        SelectedSection = InspectorSection.Dashboard;
        CurrentPage = Dashboard;
        Tick();
    }

    partial void OnSelectedSectionChanged(InspectorSection value)
    {
        if (!HasSession) return;
        CurrentPage = value switch
        {
            InspectorSection.Topology => Topology!,
            InspectorSection.Events => Events!,
            InspectorSection.PvWatch => PvWatch!,
            _ => (object)Dashboard!,
        };
        (CurrentPage as IRefreshable)?.Refresh();
    }

    [RelayCommand]
    private void SelectSection(InspectorSection section) => SelectedSection = section;

    /// <summary>Called by the view's DispatcherTimer every 250 ms (4 Hz).</summary>
    public void Tick()
    {
        if (Session is null) return;
        (CurrentPage as IRefreshable)?.Refresh();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (Session is null)
        {
            StatusText = "No session";
            return;
        }
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"{Session.SourceDescription} · {StateLabel(Session.State)} · " +
            $"{Session.FramesSeen:N0} frames · {Session.MalformedFrames:N0} malformed");
    }

    private static string StateLabel(SessionState state) => state switch
    {
        SessionState.Running => "capturing",
        SessionState.Completed => "completed",
        SessionState.Stopped => "stopped",
        SessionState.Faulted => "faulted",
        _ => "idle",
    };

    [RelayCommand]
    private void DismissFault() => FaultMessage = null;

    [RelayCommand]
    private async Task StopSessionAsync()
    {
        if (Session is null) return;
        var session = DetachSession();
        await session.StopAsync();
        await session.DisposeAsync();
        Start = NewStartViewModel();
        OnPropertyChanged(nameof(Start));
        CurrentPage = Start;
        StatusText = "No session";
    }

    private async Task RestartWithEniAsync()
    {
        if (Session?.Source is not { } spec) return;
        var path = await _filePicker.PickFileAsync("Load ENI", "xml");
        if (path is null) return;
        EniConfiguration eni;
        try
        {
            eni = EniConfiguration.Load(path);
        }
        catch (Exception ex)
        {
            FaultMessage = $"ENI could not be loaded: {ex.Message}";
            return;
        }
        var old = DetachSession();
        await old.StopAsync();
        await old.DisposeAsync();
        var next = _createSession(spec, eni);
        next.Start();
        OnSessionStarted(next);
        SelectedSection = InspectorSection.PvWatch;
    }

    private MonitorSession DetachSession()
    {
        var session = Session!;
        Session = null;
        HasSession = false;
        Dashboard = null;
        Topology = null;
        Events = null;
        PvWatch = null;
        FaultMessage = null;
        return session;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: 6 tests PASS.

- [ ] **Step 5: Run the whole Inspector suite**

Run: `dotnet test tests/OpenEC.Inspector.Tests`
Expected: all tests from Tasks 1–10 PASS (1 smoke + 10 session + 4 ENI + 2 dashboard + 4 events + 5 topology + 5 PV + 9 start + 6 shell = 46).

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): shell view-model with nav, tick, status, and fault banner"
```

---

### Task 11: Views, app wiring, and headless UI tests

**Files:**
- Create: `src/OpenEC.Inspector/Views/StorageFilePicker.cs`
- Create: `src/OpenEC.Inspector/Views/StartView.axaml`, `.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/DashboardView.axaml`, `.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/TopologyView.axaml`, `.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/EventsView.axaml`, `.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/PvWatchView.axaml`, `.axaml.cs`
- Modify: `src/OpenEC.Inspector/Views/MainWindow.axaml`, `.axaml.cs` (replace the Task 1 placeholder)
- Modify: `src/OpenEC.Inspector/App.axaml.cs` (wire the real view-model)
- Test: `tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs` (extend)

**Interfaces:**
- Consumes: all view-models (Tasks 5–10); Avalonia `StorageProvider`, `DispatcherTimer`.
- Produces: the runnable application. Styling notes: translucent grays (`#11888888` fills, `#33888888` borders) so both light and dark theme variants work without naming Fluent resource keys.

- [ ] **Step 1: Extend the headless smoke tests (failing first)**

Replace `tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs` with:

```csharp
using Avalonia.Headless.XUnit;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector.Tests.Ui;

public class ShellSmokeTests
{
    private static MainWindowViewModel CreateViewModel() => new(
        () => [],
        (spec, eni) => new MonitorSession(spec, eni),
        new FakeFilePicker(),
        marshal: action => action(),
        earlyFaultProbe: TimeSpan.FromSeconds(2));

    [AvaloniaFact]
    public void Main_window_boots_to_the_start_screen()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Equal("OpenEC Inspector", window.Title);
        Assert.Same(vm.Start, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_file_session_renders_the_dashboard_and_all_sections()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        vm.Start.EniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        await vm.Start.StartFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasSession);
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
        vm.Tick();

        // Walk every section while the window is live — templates must instantiate without throwing.
        foreach (var section in Enum.GetValues<InspectorSection>())
        {
            vm.SelectSectionCommand.Execute(section);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        await vm.StopSessionCommand.ExecuteAsync(null);
        Assert.Same(vm.Start, vm.CurrentPage);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~ShellSmokeTests"`
Expected: FAIL — `MainWindow` has no bindings yet (first test fails on `vm.Start` vs `CurrentPage` only if DataContext templates are missing; the second fails to find views). Compile errors are also an acceptable "red" here.

- [ ] **Step 3: Implement StorageFilePicker**

`src/OpenEC.Inspector/Views/StorageFilePicker.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public sealed class StorageFilePicker(Window window) : IFilePicker
{
    public async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(title)
                {
                    Patterns = extensions.Select(e => $"*.{e}").ToArray(),
                },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
```

- [ ] **Step 4: Implement the shell MainWindow**

`src/OpenEC.Inspector/Views/MainWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:OpenEC.Inspector.ViewModels"
        xmlns:views="using:OpenEC.Inspector.Views"
        x:Class="OpenEC.Inspector.Views.MainWindow"
        Title="OpenEC Inspector"
        Width="1100" Height="720">

  <Window.DataTemplates>
    <DataTemplate DataType="{x:Type vm:StartViewModel}"><views:StartView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:DashboardViewModel}"><views:DashboardView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:TopologyViewModel}"><views:TopologyView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:EventsViewModel}"><views:EventsView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:PvWatchViewModel}"><views:PvWatchView /></DataTemplate>
  </Window.DataTemplates>

  <DockPanel>
    <Border DockPanel.Dock="Bottom" Padding="10,5" BorderThickness="0,1,0,0" BorderBrush="#33888888">
      <TextBlock Text="{Binding StatusText}" FontSize="12" />
    </Border>

    <StackPanel DockPanel.Dock="Left" Width="150" Spacing="4" Margin="8"
                IsVisible="{Binding HasSession}">
      <Button Content="Dashboard" HorizontalAlignment="Stretch"
              Command="{Binding SelectSectionCommand}"
              CommandParameter="{x:Static vm:InspectorSection.Dashboard}" />
      <Button Content="Topology" HorizontalAlignment="Stretch"
              Command="{Binding SelectSectionCommand}"
              CommandParameter="{x:Static vm:InspectorSection.Topology}" />
      <Button Content="Events" HorizontalAlignment="Stretch"
              Command="{Binding SelectSectionCommand}"
              CommandParameter="{x:Static vm:InspectorSection.Events}" />
      <Button Content="PV Watch" HorizontalAlignment="Stretch"
              Command="{Binding SelectSectionCommand}"
              CommandParameter="{x:Static vm:InspectorSection.PvWatch}" />
      <Separator Margin="0,8" />
      <Button Content="Stop session" HorizontalAlignment="Stretch"
              Command="{Binding StopSessionCommand}" />
    </StackPanel>

    <Grid>
      <ContentControl Content="{Binding CurrentPage}" Margin="8" />
      <Border IsVisible="{Binding FaultMessage, Converter={x:Static ObjectConverters.IsNotNull}}"
              VerticalAlignment="Top" Background="#CCB00020" CornerRadius="4"
              Padding="12,8" Margin="16">
        <DockPanel>
          <Button DockPanel.Dock="Right" Content="Dismiss" Margin="12,0,0,0"
                  Command="{Binding DismissFaultCommand}" />
          <TextBlock Text="{Binding FaultMessage}" Foreground="White"
                     TextWrapping="Wrap" VerticalAlignment="Center" />
        </DockPanel>
      </Border>
    </Grid>
  </DockPanel>
</Window>
```

`src/OpenEC.Inspector/Views/MainWindow.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.Tick());
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
```

- [ ] **Step 5: Implement StartView**

`src/OpenEC.Inspector/Views/StartView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.StartView">
  <UserControl.Styles>
    <Style Selector="Border.card">
      <Setter Property="Background" Value="#11888888" />
      <Setter Property="BorderBrush" Value="#33888888" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="CornerRadius" Value="6" />
      <Setter Property="Padding" Value="16" />
    </Style>
  </UserControl.Styles>

  <ScrollViewer>
    <StackPanel MaxWidth="520" Spacing="16" VerticalAlignment="Center" Margin="0,24">
      <TextBlock Text="OpenEC Inspector" FontSize="24" FontWeight="SemiBold" />

      <Border Classes="card">
        <StackPanel Spacing="8">
          <TextBlock Text="Live capture" FontWeight="SemiBold" />
          <DockPanel>
            <Button DockPanel.Dock="Right" Content="↻" Margin="8,0,0,0"
                    Command="{Binding RefreshDevicesCommand}" />
            <ComboBox ItemsSource="{Binding Devices}" SelectedItem="{Binding SelectedDevice}"
                      HorizontalAlignment="Stretch" PlaceholderText="Capture interface (TAP monitor port)" />
          </DockPanel>
          <Button Content="Start capture" Command="{Binding StartLiveCommand}"
                  IsEnabled="{Binding !IsStarting}" />
        </StackPanel>
      </Border>

      <Border Classes="card">
        <StackPanel Spacing="8">
          <TextBlock Text="Capture file" FontWeight="SemiBold" />
          <DockPanel>
            <Button DockPanel.Dock="Right" Content="Browse…" Margin="8,0,0,0"
                    Command="{Binding BrowsePcapCommand}" />
            <TextBox Text="{Binding PcapPath}" Watermark="path/to/capture.pcap" />
          </DockPanel>
          <Button Content="Analyze file" Command="{Binding StartFileCommand}"
                  IsEnabled="{Binding !IsStarting}" />
        </StackPanel>
      </Border>

      <Border Classes="card">
        <StackPanel Spacing="8">
          <TextBlock Text="ENI (optional)" FontWeight="SemiBold" />
          <DockPanel>
            <Button DockPanel.Dock="Right" Content="Browse…" Margin="8,0,0,0"
                    Command="{Binding BrowseEniCommand}" />
            <TextBox Text="{Binding EniPath}" Watermark="path/to/configuration.xml" />
          </DockPanel>
          <TextBlock Text="Enables the process-variable watch and slave names."
                     FontSize="12" Opacity="0.7" />
        </StackPanel>
      </Border>

      <TextBlock Text="{Binding ErrorMessage}" Foreground="OrangeRed" TextWrapping="Wrap"
                 IsVisible="{Binding ErrorMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

`src/OpenEC.Inspector/Views/StartView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace OpenEC.Inspector.Views;

public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();
}
```

- [ ] **Step 6: Implement DashboardView**

`src/OpenEC.Inspector/Views/DashboardView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.DashboardView">
  <UserControl.Styles>
    <Style Selector="Border.tile">
      <Setter Property="Background" Value="#11888888" />
      <Setter Property="BorderBrush" Value="#33888888" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="CornerRadius" Value="6" />
      <Setter Property="Padding" Value="16,12" />
      <Setter Property="Width" Value="200" />
    </Style>
    <Style Selector="Border.tile TextBlock.label">
      <Setter Property="FontSize" Value="12" />
      <Setter Property="Opacity" Value="0.7" />
    </Style>
    <Style Selector="Border.tile TextBlock.value">
      <Setter Property="FontSize" Value="22" />
      <Setter Property="FontWeight" Value="SemiBold" />
      <Setter Property="Margin" Value="0,4,0,0" />
    </Style>
  </UserControl.Styles>

  <ScrollViewer>
    <WrapPanel>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Cyclic Tx" />
          <TextBlock Classes="value" Text="{Binding CyclicTxRate}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Queued Tx" />
          <TextBlock Classes="value" Text="{Binding QueuedTxRate}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Rx" />
          <TextBlock Classes="value" Text="{Binding RxRate}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Cycle time" />
          <TextBlock Classes="value" Text="{Binding CycleTime}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="WKC mismatches" />
          <TextBlock Classes="value" Text="{Binding WkcMismatches}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Suspected lost (idx)" />
          <TextBlock Classes="value" Text="{Binding LostFrames}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Ring lost frames" />
          <TextBlock Classes="value" Text="{Binding RingLostFrames}" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Frames" />
          <TextBlock Classes="value" Text="{Binding FrameTotals}" FontSize="16" />
        </StackPanel>
      </Border>
      <Border Classes="tile" Margin="0,0,12,12">
        <StackPanel>
          <TextBlock Classes="label" Text="Malformed" />
          <TextBlock Classes="value" Text="{Binding Malformed}" />
        </StackPanel>
      </Border>
    </WrapPanel>
  </ScrollViewer>
</UserControl>
```

`src/OpenEC.Inspector/Views/DashboardView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace OpenEC.Inspector.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}
```

- [ ] **Step 7: Implement TopologyView**

`src/OpenEC.Inspector/Views/TopologyView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.TopologyView">
  <Grid ColumnDefinitions="*,12,320">
    <ListBox ItemsSource="{Binding Slaves}" SelectedItem="{Binding SelectedSlave}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <Grid ColumnDefinitions="60,*,80,30,90,100">
            <TextBlock Text="{Binding Address}" />
            <TextBlock Grid.Column="1" Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
            <TextBlock Grid.Column="2" Text="{Binding State}" />
            <TextBlock Grid.Column="3" Text="⚠" Foreground="OrangeRed"
                       IsVisible="{Binding HasError}" />
            <TextBlock Grid.Column="4" Text="{Binding MailboxProtocols}" Opacity="0.7" />
            <TextBlock Grid.Column="5" Text="{Binding LastSeen}" Opacity="0.7" FontSize="12" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>

    <Border Grid.Column="2" Background="#11888888" BorderBrush="#33888888"
            BorderThickness="1" CornerRadius="6" Padding="16"
            IsVisible="{Binding Detail, Converter={x:Static ObjectConverters.IsNotNull}}">
      <ScrollViewer>
        <StackPanel Spacing="8" DataContext="{Binding Detail}">
          <TextBlock Text="{Binding Title}" FontWeight="SemiBold" FontSize="16" />
          <TextBlock Text="{Binding Identity}" TextWrapping="Wrap" FontSize="12" Opacity="0.8" />
          <TextBlock Text="State history" FontWeight="SemiBold" Margin="0,8,0,0" />
          <ItemsControl ItemsSource="{Binding StateHistory}" />
          <TextBlock Text="Mailbox activity" FontWeight="SemiBold" Margin="0,8,0,0" />
          <ItemsControl ItemsSource="{Binding MailboxActivity}" />
        </StackPanel>
      </ScrollViewer>
    </Border>
  </Grid>
</UserControl>
```

`src/OpenEC.Inspector/Views/TopologyView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace OpenEC.Inspector.Views;

public partial class TopologyView : UserControl
{
    public TopologyView() => InitializeComponent();
}
```

- [ ] **Step 8: Implement EventsView (with auto-scroll code-behind)**

`src/OpenEC.Inspector/Views/EventsView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.EventsView">
  <DockPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="16" Margin="0,0,0,8">
      <ItemsControl ItemsSource="{Binding Categories}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate>
            <StackPanel Orientation="Horizontal" Spacing="12" />
          </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <CheckBox Content="{Binding Name}" IsChecked="{Binding IsEnabled}" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <CheckBox Content="Auto-scroll" IsChecked="{Binding AutoScroll}" />
    </StackPanel>

    <ListBox x:Name="EventList" ItemsSource="{Binding Rows}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <Grid ColumnDefinitions="110,110,*">
            <TextBlock Text="{Binding Time}" FontFamily="Menlo,Consolas,monospace" FontSize="12" />
            <TextBlock Grid.Column="1" Text="{Binding Category}" FontSize="12" Opacity="0.7" />
            <TextBlock Grid.Column="2" Text="{Binding Description}" FontSize="12" TextWrapping="Wrap" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```

`src/OpenEC.Inspector/Views/EventsView.axaml.cs`:

```csharp
using System.Collections.Specialized;
using Avalonia.Controls;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class EventsView : UserControl
{
    private EventsViewModel? _viewModel;

    public EventsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Rows.CollectionChanged -= OnRowsChanged;
            _viewModel = DataContext as EventsViewModel;
            if (_viewModel is not null) _viewModel.Rows.CollectionChanged += OnRowsChanged;
        };
        EventList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || e.Source is not ScrollViewer scroll) return;
        var atBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        if (e.OffsetDelta.Y < 0) _viewModel.AutoScroll = false;
        else if (atBottom) _viewModel.AutoScroll = true;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is { AutoScroll: true, Rows.Count: > 0 })
            EventList.ScrollIntoView(_viewModel.Rows[^1]);
    }
}
```

- [ ] **Step 9: Implement PvWatchView**

`src/OpenEC.Inspector/Views/PvWatchView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.PvWatchView">
  <Grid>
    <StackPanel IsVisible="{Binding !HasEni}" VerticalAlignment="Center"
                HorizontalAlignment="Center" Spacing="12">
      <TextBlock Text="No ENI loaded" FontSize="18" FontWeight="SemiBold"
                 HorizontalAlignment="Center" />
      <TextBlock Text="The process-variable watch needs the process image from an ENI file."
                 TextWrapping="Wrap" MaxWidth="420" HorizontalAlignment="Center" />
      <Button Content="Load ENI…" HorizontalAlignment="Center"
              Command="{Binding LoadEniCommand}" />
    </StackPanel>

    <DockPanel IsVisible="{Binding HasEni}">
      <TextBox DockPanel.Dock="Top" Watermark="Filter variables…"
               Text="{Binding FilterText}" Margin="0,0,0,8" />
      <ListBox ItemsSource="{Binding Rows}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Grid ColumnDefinitions="*,280,110">
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
              <TextBlock Grid.Column="1" Text="{Binding Value}"
                         FontFamily="Menlo,Consolas,monospace" FontSize="12" />
              <TextBlock Grid.Column="2" Text="{Binding Updated}" Opacity="0.7" FontSize="12" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </DockPanel>
  </Grid>
</UserControl>
```

`src/OpenEC.Inspector/Views/PvWatchView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace OpenEC.Inspector.Views;

public partial class PvWatchView : UserControl
{
    public PvWatchView() => InitializeComponent();
}
```

- [ ] **Step 10: Wire the real view-model in App**

Replace `src/OpenEC.Inspector/App.axaml.cs` with:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(
                CaptureDevices.List,
                (spec, eni) => new MonitorSession(spec, eni),
                new StorageFilePicker(window),
                action => Dispatcher.UIThread.Post(action));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 11: Run the smoke tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~ShellSmokeTests"`
Expected: 2 tests PASS.

- [ ] **Step 12: Run the full Inspector suite**

Run: `dotnet test tests/OpenEC.Inspector.Tests`
Expected: all 47 tests PASS.

- [ ] **Step 13: Launch the app once for a human-eye check**

Run: `dotnet run --project src/OpenEC.Inspector` (quit after a look, or leave for the user)
Expected: window opens on the start screen; the device combo lists real interfaces (en11 present on the user's Mac); "Analyze file" with a demo pcap (generate one via the CLI's `gen-sample` command: `dotnet run --project src/OpenEC.CLI -- gen-sample /tmp/demo.pcap`) reaches the dashboard. This step is a sanity look, not a gate — the ETAP-1000 live acceptance happens in Task 12.

- [ ] **Step 14: Commit**

```bash
git add src/OpenEC.Inspector tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): AXAML views, app wiring, and headless UI smoke tests"
```

---

### Task 12: Documentation, full verification, and manual acceptance

**Files:**
- Modify: `README.md` (add an Inspector section next to the existing CLI section)
- Test: none new — full-suite verification.

**Interfaces:**
- Consumes: the finished app.
- Produces: shippable M2.

- [ ] **Step 1: Add the Inspector section to README.md**

Read `README.md` first and match its existing tone/structure. Insert a section after the CLI usage section:

```markdown
## OpenEC.Inspector (GUI)

A cross-platform Avalonia desktop app over the same SDK:

```bash
dotnet run --project src/OpenEC.Inspector
```

Pick a live capture interface (the TAP monitor port, e.g. `en11`) or open a
`.pcap`/`.pcapng` file, optionally load an ENI, and watch:

- **Dashboard** — Tx/Rx rates, cycle time, WKC health, ring loss, frame totals
- **Topology** — slave chain with AL states and a per-slave detail pane
- **Events** — filtered feed of state changes, WKC faults, CoE emergencies, SoE errors
- **PV Watch** — live decoded process variables (needs an ENI)

Live capture needs the same BPF permissions as the CLI — see `docs/tap-setup.md`.
```

(Adjust heading level and placement to match the actual README structure — the block above is content, not exact placement.)

- [ ] **Step 2: Run the entire solution suite**

Run: `dotnet test OpenEC-Diagnostics.sln`
Expected: every project builds; 91 M1 tests + 47 Inspector tests all PASS.

- [ ] **Step 3: Build release once**

Run: `dotnet build OpenEC-Diagnostics.sln -c Release`
Expected: 0 warnings introduced by Inspector projects (investigate any new warning before committing).

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: OpenEC.Inspector usage section"
```

- [ ] **Step 5: Manual acceptance checklist (with the user, hardware required)**

This step needs the ETAP-1000 tap wired per `docs/tap-setup.md` and cannot be automated — hand it to the user as a checklist and record the outcome in the session notes:

1. `dotnet run --project src/OpenEC.Inspector`
2. Start screen lists `en11`; select it, no ENI, Start capture.
3. Dashboard shows ~4.4 ms cycle time and rates comparable to `openec live --interface en11` run side by side (station 1001 network, ~500 fps).
4. Topology shows station 1001 with its AL state; Events stays quiet on a healthy bus.
5. Stop session → start screen returns; restart works (fresh source per session).
6. Unplug the tap NIC mid-session → fault banner appears, views freeze on last state, app stays alive.
7. PV Watch without ENI shows the load-ENI empty state.

---

## Execution notes

- Tasks 2–10 are pure .NET (no display server needed); Task 1 and 11's `[AvaloniaFact]` tests run headless and also need no display.
- Task order is strict up to Task 5; Tasks 5–9 are independent of each other (all depend on Tasks 2–4) and can be parallelized by subagents if desired; Task 10 needs 5–9; Task 11 needs 10; Task 12 needs 11.
- If `SampleCapture`-derived exact values (frame counts, event codes) fail, re-verify against `src/OpenEC.Monitor/Synthesis/SampleCapture.cs` before touching any assertion — the ground-truth section at the top of this plan was derived from that file.
