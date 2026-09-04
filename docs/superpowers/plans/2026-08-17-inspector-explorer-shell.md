# Inspector Explorer Shell & House Style Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the Inspector into a device-tree explorer + tabbed device editor + docked messages panel, re-skinned in the Dahlke house style (chrome bars, flat panels, token palette, light/dark).

**Architecture:** Tree selection replaces the `InspectorSection` nav. New view-models (`ExplorerViewModel`, `DeviceEditorViewModel`, `VariableWatchViewModel`) stay plain CommunityToolkit.Mvvm classes polled at 4 Hz via the existing `Tick()`/`IRefreshable` pipeline; the only SDK addition is the pure `ProcessVariableAssignment` matcher in `OpenEC.Monitor.Eni`. Theme = Avalonia `ThemeDictionaries` (Light/Dark token brushes) + global styles over FluentTheme.

**Tech Stack:** .NET 8, Avalonia 11.3.2 (FluentTheme, headless xunit for UI smoke), CommunityToolkit.Mvvm 8.x, xunit.

**Spec:** `docs/superpowers/specs/2026-08-17-inspector-explorer-shell-design.md`

## Global Constraints

- No new package references anywhere. Avalonia stays 11.3.2, FluentTheme stays the base theme.
- `Directory.Build.props` applies: nullable enabled, warnings-as-errors, analyzers — new code must build clean.
- UI reads `BusObserver` **only** via `SnapshotSlaves()` / `SnapshotEvents(int)`; never subscribe to per-frame callbacks (single-writer contract).
- `MainWindowViewModel.StatusText` format is frozen — tests assert substrings including `rec → <file>` and `completed`.
- Passive-only. No capture-path, `BusObserver`, `MonitorSession`, or `ProcessImage` changes; the only SDK change is the new additive `ProcessVariableAssignment`.
- Palette hex values must be copied exactly from the tables in Task 2 (they define the Inspector's house theme).
- House look rules: `CornerRadius="0"` everywhere; shell font `"Segoe UI, Helvetica Neue, Arial"`; monospace `"Consolas, Menlo, monospace"`; accent `#66B2FF` with dark ink `#20262C` for text on accent.
- Commit style (from repo history): `feat(inspector): …`, `feat(monitor): …`, `test: …`, `docs: …`.
- Test command: `dotnet test` from repo root (headless Avalonia tests run under `[AvaloniaFact]`). Run the **full** suite at the end of every task; a task is not done with any test red.
- Plan code marked **(sketch)** compiles against the APIs read on 2026-08-17 but is illustrative — verify signatures against the actual files before committing. Test code is authoritative.

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/OpenEC.Monitor/Eni/ProcessVariableAssignment.cs` | Pure variable→slave matcher |
| Create | `tests/OpenEC.Monitor.Tests/Eni/ProcessVariableAssignmentTests.cs` | Matcher tests |
| Create | `src/OpenEC.Inspector/Theme/Palette.axaml` | Light/Dark token brushes |
| Create | `src/OpenEC.Inspector/Theme/Controls.axaml` | Global styles + style classes |
| Create | `src/OpenEC.Inspector/ViewModels/StatusDot.cs` | `StatusDot` enum + `StatusDotMap` |
| Create | `src/OpenEC.Inspector/ViewModels/VariableWatchViewModel.cs` | Scoped PV watch (per-slave / unmatched) + `VariableRowViewModel` + `VariableValueFormat` |
| Create | `src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs` | Tree nodes + refresh + selection callback |
| Create | `src/OpenEC.Inspector/ViewModels/DeviceEditorViewModel.cs` | Per-slave editor (General/Variables); hosts moved `SlaveDetailViewModel` |
| Create | `src/OpenEC.Inspector/Views/ExplorerView.axaml(.cs)` | TreeView |
| Create | `src/OpenEC.Inspector/Views/DeviceEditorView.axaml(.cs)` | Tab strip + General/Variables panes |
| Create | `src/OpenEC.Inspector/Views/VariableWatchView.axaml(.cs)` | Variables list + CTA |
| Create | `src/OpenEC.Inspector/Views/StatusDotBrushConverter.cs` | `StatusDot` → palette brush |
| Modify | `src/OpenEC.Inspector/App.axaml` | Merge theme, `SystemAccentColor` |
| Modify | `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs` | Append-only diffing + `IsCollapsed` |
| Modify | `src/OpenEC.Inspector/Views/EventsView.axaml(.cs)` | Page → docked panel with header |
| Modify | `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs` | Tree-driven navigation |
| Modify | `src/OpenEC.Inspector/Views/MainWindow.axaml` | New shell (chrome bars, tree, dock) |
| Modify | `src/OpenEC.Inspector/Views/StartView.axaml`, `Views/DashboardView.axaml` | Token restyle |
| Modify | `tests/OpenEC.Inspector.Tests/TestDoubles.cs` | Add `PushCaptureSource` |
| Delete (Task 9) | `TopologyViewModel.cs`, `TopologyView.axaml(.cs)`, `PvWatchViewModel.cs`, `PvWatchView.axaml(.cs)` | Replaced by Explorer/DeviceEditor/VariableWatch |
| Delete (migrated) | `TopologyViewModelTests.cs` (Task 7), `PvWatchViewModelTests.cs` (Task 5) | Guarantees move to new test files |

Ordering rationale: old types (`TopologyViewModel`, `PvWatchViewModel`) stay compiling until Task 9 rewires `MainWindowViewModel` and the views in one atomic step — every task leaves `main` green.

---

### Task 1: SDK matcher — `ProcessVariableAssignment`

**Files:**
- Create: `src/OpenEC.Monitor/Eni/ProcessVariableAssignment.cs`
- Test: `tests/OpenEC.Monitor.Tests/Eni/ProcessVariableAssignmentTests.cs`

**Interfaces:**
- Consumes: `EniConfiguration` (`Slaves`, `Variables`), `EniSlave` (`Name`, `PhysAddr`), `EniVariable` (`Name`).
- Produces: `ProcessVariableAssignment(IReadOnlyDictionary<ushort, IReadOnlyList<EniVariable>> BySlave, IReadOnlyList<EniVariable> Unmatched)` with `static ProcessVariableAssignment Build(EniConfiguration eni)`. **Every** ENI slave appears as a `BySlave` key (empty list when nothing matched). Tasks 6 and 9 consume exactly this shape.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Eni/ProcessVariableAssignmentTests.cs
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Tests.Eni;

public class ProcessVariableAssignmentTests
{
    private static EniSlave Slave(string name, ushort addr) =>
        new(name, addr, 0, 2, 0, 0, null, null);

    private static EniVariable Var(string name) => new(name, "BOOL", 1, 0, true);

    private static EniConfiguration Eni(IReadOnlyList<EniSlave> slaves, IReadOnlyList<EniVariable> vars) =>
        new() { Slaves = slaves, CyclicCommands = [], Variables = vars };

    [Fact]
    public void Assigns_variables_to_slaves_by_name_prefix()
    {
        var eni = Eni(
            [Slave("Term 2 (EL1008)", 1002), Slave("Term 3 (EL2008)", 1003)],
            [Var("Term 2 (EL1008).Channel 1.Input"),
             Var("Term 2 (EL1008).Channel 2.Input"),
             Var("Term 3 (EL2008).Channel 1.Output")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal(2, a.BySlave[1002].Count);
        Assert.Single(a.BySlave[1003]);
        Assert.Empty(a.Unmatched);
    }

    [Fact]
    public void Longest_slave_name_wins_for_nested_names()
    {
        var eni = Eni(
            [Slave("Rack 1", 1001), Slave("Rack 1.Module 2", 1002)],
            [Var("Rack 1.Module 2.Temp"), Var("Rack 1.Status")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal("Rack 1.Module 2.Temp", Assert.Single(a.BySlave[1002]).Name);
        Assert.Equal("Rack 1.Status", Assert.Single(a.BySlave[1001]).Name);
    }

    [Fact]
    public void Variables_matching_no_slave_are_reported_unmatched()
    {
        var eni = Eni([Slave("Term 2 (EL1008)", 1002)], [Var("Ghost.Value")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal("Ghost.Value", Assert.Single(a.Unmatched).Name);
        Assert.Empty(a.BySlave[1002]);
    }

    [Fact]
    public void Duplicate_slave_names_assign_to_the_lowest_address()
    {
        var eni = Eni(
            [Slave("Term (EL1008)", 1005), Slave("Term (EL1008)", 1002)],
            [Var("Term (EL1008).Channel 1.Input")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Single(a.BySlave[1002]);
        Assert.Empty(a.BySlave[1005]);
    }

    [Fact]
    public void Every_eni_slave_gets_a_key_even_without_variables()
    {
        var eni = Eni([Slave("Term 1 (EK1100)", 1001)], []);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Empty(a.BySlave[1001]);
        Assert.Empty(a.Unmatched);
    }

    [Fact]
    public void The_fixture_eni_assigns_all_five_variables()
    {
        var eni = EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Empty(a.Unmatched);
        Assert.Equal(2, a.BySlave[1004].Count); // Drive 4: Statusword + Controlword
        Assert.Equal(2, a.BySlave[1002].Count); // Term 2: Channel 1 + 2
        Assert.Single(a.BySlave[1003]);          // Term 3: Channel 1.Output
        Assert.Empty(a.BySlave[1001]);           // EK1100 coupler: none
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter ProcessVariableAssignmentTests`
Expected: FAIL — `ProcessVariableAssignment` does not exist (compile error).

- [ ] **Step 3: Implement**

Signature (authoritative) plus behavior; body below is a **(sketch)**:

```csharp
// src/OpenEC.Monitor/Eni/ProcessVariableAssignment.cs
namespace OpenEC.Monitor.Eni;

/// <summary>Partitions an ENI's process variables by owning slave, matched by name
/// prefix ("SlaveName." …). Longest slave name wins; identical names resolve to the
/// lowest PhysAddr. Pure and immutable — a heuristic over TwinCAT-style ENI naming,
/// safe because unmatched variables stay reachable through <see cref="Unmatched"/>.</summary>
public sealed record ProcessVariableAssignment(
    IReadOnlyDictionary<ushort, IReadOnlyList<EniVariable>> BySlave,
    IReadOnlyList<EniVariable> Unmatched)
{
    public static ProcessVariableAssignment Build(EniConfiguration eni)
    {
        // (sketch)
        var candidates = eni.Slaves
            .OrderByDescending(s => s.Name.Length).ThenBy(s => s.PhysAddr).ToList();
        var bySlave = new Dictionary<ushort, List<EniVariable>>();
        foreach (var s in eni.Slaves) bySlave.TryAdd(s.PhysAddr, []); // TryAdd: tolerate duplicate PhysAddr
        var unmatched = new List<EniVariable>();
        foreach (var v in eni.Variables)
        {
            var owner = candidates.FirstOrDefault(
                s => v.Name.StartsWith(s.Name + ".", StringComparison.Ordinal));
            if (owner is null) unmatched.Add(v);
            else bySlave[owner.PhysAddr].Add(v);
        }
        return new ProcessVariableAssignment(
            bySlave.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EniVariable>)kv.Value),
            unmatched);
    }
}
```

Duplicate-name tie-break falls out of the ordering: equal names have equal lengths, so `ThenBy(PhysAddr)` puts the lowest address first and `FirstOrDefault` picks it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter ProcessVariableAssignmentTests`
Expected: 6 PASS.

- [ ] **Step 5: Full suite + commit**

```bash
dotnet test
git add src/OpenEC.Monitor/Eni/ProcessVariableAssignment.cs tests/OpenEC.Monitor.Tests/Eni/ProcessVariableAssignmentTests.cs
git commit -m "feat(monitor): ProcessVariableAssignment maps ENI variables to slaves by name prefix"
```

---

### Task 2: Theme resources (palette + global styles)

**Files:**
- Create: `src/OpenEC.Inspector/Theme/Palette.axaml`, `src/OpenEC.Inspector/Theme/Controls.axaml`
- Modify: `src/OpenEC.Inspector/App.axaml`

**Interfaces:**
- Consumes: nothing.
- Produces: `DynamicResource` brush keys used by every later view task: `Bg`, `Panel`, `Panel2`, `Panel3`, `Line`, `Line2`, `Ink`, `Ink2`, `Ink3`, `Chrome`, `Chrome2`, `ChromeLine`, `ChromeInk`, `ChromeInk2`, `Accent`, `AccentSoft`, `Ok`, `Fail`, `Oos`, `Maint`, `Fc`. Style classes: `chrome`, `panel`, `tile`, `label`, `value`, `mono`, `accent` (Button), `panelHeader`.

There is no meaningful unit test for resource dictionaries; verification is: solution builds, all existing headless UI tests still pass, and `dotnet run` shows the restyled window (visual spot-check happens in Task 9/10; here the app must simply still boot styled-but-unrestructured).

- [ ] **Step 1: Write `Theme/Palette.axaml`** (declarative — copy exactly)

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="Bg">#eef0f2</SolidColorBrush>
      <SolidColorBrush x:Key="Panel">#ffffff</SolidColorBrush>
      <SolidColorBrush x:Key="Panel2">#f5f6f7</SolidColorBrush>
      <SolidColorBrush x:Key="Panel3">#eceef0</SolidColorBrush>
      <SolidColorBrush x:Key="Line">#d5d9dc</SolidColorBrush>
      <SolidColorBrush x:Key="Line2">#e9ecee</SolidColorBrush>
      <SolidColorBrush x:Key="Ink">#2e3439</SolidColorBrush>
      <SolidColorBrush x:Key="Ink2">#5f676d</SolidColorBrush>
      <SolidColorBrush x:Key="Ink3">#8d949a</SolidColorBrush>
      <SolidColorBrush x:Key="Chrome">#3f444a</SolidColorBrush>
      <SolidColorBrush x:Key="Chrome2">#4e555c</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeLine">#5a6168</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeInk">#f2f4f6</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeInk2">#a9b1b8</SolidColorBrush>
      <SolidColorBrush x:Key="Accent">#66b2ff</SolidColorBrush>
      <SolidColorBrush x:Key="AccentSoft">#e2eefb</SolidColorBrush>
      <SolidColorBrush x:Key="Ok">#0d8a16</SolidColorBrush>
      <SolidColorBrush x:Key="Fail">#cd4e4e</SolidColorBrush>
      <SolidColorBrush x:Key="Oos">#b78e00</SolidColorBrush>
      <SolidColorBrush x:Key="Maint">#1b8fbf</SolidColorBrush>
      <SolidColorBrush x:Key="Fc">#ee8632</SolidColorBrush>
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="Bg">#242729</SolidColorBrush>
      <SolidColorBrush x:Key="Panel">#2e3236</SolidColorBrush>
      <SolidColorBrush x:Key="Panel2">#33383c</SolidColorBrush>
      <SolidColorBrush x:Key="Panel3">#3a4045</SolidColorBrush>
      <SolidColorBrush x:Key="Line">#464c52</SolidColorBrush>
      <SolidColorBrush x:Key="Line2">#3b4045</SolidColorBrush>
      <SolidColorBrush x:Key="Ink">#e6e9eb</SolidColorBrush>
      <SolidColorBrush x:Key="Ink2">#adb5bb</SolidColorBrush>
      <SolidColorBrush x:Key="Ink3">#7f878d</SolidColorBrush>
      <SolidColorBrush x:Key="Chrome">#1d2023</SolidColorBrush>
      <SolidColorBrush x:Key="Chrome2">#2b3035</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeLine">#383e43</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeInk">#eef1f3</SolidColorBrush>
      <SolidColorBrush x:Key="ChromeInk2">#98a1a8</SolidColorBrush>
      <SolidColorBrush x:Key="Accent">#66b2ff</SolidColorBrush>
      <SolidColorBrush x:Key="AccentSoft">#2c3d50</SolidColorBrush>
      <SolidColorBrush x:Key="Ok">#44b34b</SolidColorBrush>
      <SolidColorBrush x:Key="Fail">#e06e6e</SolidColorBrush>
      <SolidColorBrush x:Key="Oos">#d1a91d</SolidColorBrush>
      <SolidColorBrush x:Key="Maint">#41a8d6</SolidColorBrush>
      <SolidColorBrush x:Key="Fc">#f09a55</SolidColorBrush>
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

- [ ] **Step 2: Write `Theme/Controls.axaml`**

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style Selector="Window">
    <Setter Property="FontFamily" Value="Segoe UI, Helvetica Neue, Arial" />
    <Setter Property="Background" Value="{DynamicResource Bg}" />
  </Style>

  <!-- Sharp corners everywhere: the house style has no rounded corners. -->
  <Style Selector="Button"><Setter Property="CornerRadius" Value="0" /></Style>
  <Style Selector="TextBox"><Setter Property="CornerRadius" Value="0" /></Style>
  <Style Selector="ComboBox"><Setter Property="CornerRadius" Value="0" /></Style>
  <Style Selector="ToggleButton"><Setter Property="CornerRadius" Value="0" /></Style>
  <Style Selector="CheckBox"><Setter Property="CornerRadius" Value="0" /></Style>

  <Style Selector="Button.accent">
    <Setter Property="Background" Value="{DynamicResource Accent}" />
    <Setter Property="Foreground" Value="#20262c" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>

  <Style Selector="Border.chrome">
    <Setter Property="Background" Value="{DynamicResource Chrome}" />
  </Style>
  <Style Selector="Border.chrome TextBlock">
    <Setter Property="Foreground" Value="{DynamicResource ChromeInk}" />
  </Style>

  <Style Selector="Border.panel">
    <Setter Property="Background" Value="{DynamicResource Panel}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Line}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="16" />
  </Style>
  <Style Selector="Border.tile">
    <Setter Property="Background" Value="{DynamicResource Panel}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Line}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="16,12" />
    <Setter Property="Width" Value="200" />
  </Style>
  <Style Selector="Border.panelHeader">
    <Setter Property="Background" Value="{DynamicResource Panel2}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Line}" />
    <Setter Property="BorderThickness" Value="0,1,0,1" />
    <Setter Property="Padding" Value="10,4" />
  </Style>

  <Style Selector="TextBlock.label">
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Foreground" Value="{DynamicResource Ink2}" />
  </Style>
  <Style Selector="TextBlock.value">
    <Setter Property="FontSize" Value="22" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Margin" Value="0,4,0,0" />
  </Style>
  <Style Selector="TextBlock.mono">
    <Setter Property="FontFamily" Value="Consolas, Menlo, monospace" />
    <Setter Property="FontSize" Value="12" />
  </Style>

  <!-- Fluent's TabItem header is oversized for a tool window. -->
  <Style Selector="TabItem">
    <Setter Property="FontSize" Value="13" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="MinHeight" Value="36" />
    <Setter Property="Padding" Value="12,6" />
  </Style>
</Styles>
```

- [ ] **Step 3: Wire into `App.axaml`** (full replacement)

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.App"
             RequestedThemeVariant="Default">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="/Theme/Palette.axaml" />
      </ResourceDictionary.MergedDictionaries>
      <!-- Fluent controls (selection highlights, checkboxes, tab pipe) pick up the house accent. -->
      <Color x:Key="SystemAccentColor">#66B2FF</Color>
    </ResourceDictionary>
  </Application.Resources>
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="/Theme/Controls.axaml" />
  </Application.Styles>
</Application>
```

- [ ] **Step 4: Verify**

Run: `dotnet build && dotnet test`
Expected: build clean, all 168 tests pass (existing views render under the new global styles; headless smoke tests catch axaml load errors).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/Theme/ src/OpenEC.Inspector/App.axaml
git commit -m "feat(inspector): house-style theme tokens and global styles over FluentTheme"
```

---

### Task 3: `StatusDot` + `StatusDotMap`

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/StatusDot.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/StatusDotMapTests.cs`

**Interfaces:**
- Consumes: `SlaveStatus` (`AlState`, `ErrorFlag`), `SlaveAlState { Unknown, Init, PreOp, Boot, SafeOp, Op }`, `SessionState { Idle, Running, Completed, Stopped, Faulted }`.
- Produces: `public enum StatusDot { Idle, Ok, Oos, Fail }`; `public static class StatusDotMap` with `StatusDot ForSlave(SlaveStatus status)` and `StatusDot ForSession(SessionState state)`. Tasks 6, 7, 9 consume these exact names.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Inspector.Tests/ViewModels/StatusDotMapTests.cs
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class StatusDotMapTests
{
    private static SlaveStatus Status(SlaveAlState state, bool error = false) =>
        new() { Address = 1001, AlState = state, ErrorFlag = error };

    [Theory]
    [InlineData(SlaveAlState.Op, StatusDot.Ok)]
    [InlineData(SlaveAlState.SafeOp, StatusDot.Oos)]
    [InlineData(SlaveAlState.PreOp, StatusDot.Oos)]
    [InlineData(SlaveAlState.Init, StatusDot.Idle)]
    [InlineData(SlaveAlState.Boot, StatusDot.Idle)]
    [InlineData(SlaveAlState.Unknown, StatusDot.Idle)]
    public void Al_states_map_to_dots(SlaveAlState state, StatusDot expected) =>
        Assert.Equal(expected, StatusDotMap.ForSlave(Status(state)));

    [Fact]
    public void The_error_flag_overrides_any_state() =>
        Assert.Equal(StatusDot.Fail, StatusDotMap.ForSlave(Status(SlaveAlState.Op, error: true)));

    [Theory]
    [InlineData(SessionState.Running, StatusDot.Ok)]
    [InlineData(SessionState.Faulted, StatusDot.Fail)]
    [InlineData(SessionState.Completed, StatusDot.Idle)]
    [InlineData(SessionState.Stopped, StatusDot.Idle)]
    [InlineData(SessionState.Idle, StatusDot.Idle)]
    public void Session_states_map_to_dots(SessionState state, StatusDot expected) =>
        Assert.Equal(expected, StatusDotMap.ForSession(state));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter StatusDotMapTests`
Expected: FAIL (compile error, `StatusDot` unknown).

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Inspector/ViewModels/StatusDot.cs
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

/// <summary>Render color of a status dot; the view maps these onto the palette
/// (Idle→Ink3, Ok→Ok, Oos→Oos, Fail→Fail). One mapping serves the explorer tree,
/// the device editor's state badge, and the status bar (spec-§4 "AL badge colors").</summary>
public enum StatusDot { Idle, Ok, Oos, Fail }

public static class StatusDotMap
{
    public static StatusDot ForSlave(SlaveStatus status) => status switch
    {
        { ErrorFlag: true } => StatusDot.Fail,
        { AlState: SlaveAlState.Op } => StatusDot.Ok,
        { AlState: SlaveAlState.SafeOp or SlaveAlState.PreOp } => StatusDot.Oos,
        _ => StatusDot.Idle,
    };

    public static StatusDot ForSession(SessionState state) => state switch
    {
        SessionState.Running => StatusDot.Ok,
        SessionState.Faulted => StatusDot.Fail,
        _ => StatusDot.Idle,
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter StatusDotMapTests`
Expected: 12 PASS.

- [ ] **Step 5: Full suite + commit**

```bash
dotnet test
git add src/OpenEC.Inspector/ViewModels/StatusDot.cs tests/OpenEC.Inspector.Tests/ViewModels/StatusDotMapTests.cs
git commit -m "feat(inspector): StatusDot mapping for AL states and session state"
```

---

### Task 4: EventsViewModel — append-only diffing + collapse

**Files:**
- Modify: `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`
- Modify: `tests/OpenEC.Inspector.Tests/TestDoubles.cs` (add `PushCaptureSource`)
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/EventsViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `MonitorSession.Observer.SnapshotEvents(int lastN)` (returns the newest ≤ lastN events, oldest-first), `EventFormatter`, existing `EventRow`/`CategoryFilter`.
- Produces: `EventsViewModel(MonitorSession session, int maxRows = 500)` (the second parameter exists for tests); new `[ObservableProperty] bool IsCollapsed` (collapse guard lives **inside** `Refresh()`; expanding triggers an immediate catch-up refresh). `Rows`, `Categories`, `AutoScroll` unchanged — the view keeps binding to them. Task 9 binds `IsCollapsed` to the panel header toggle.

Behavior to implement in `Refresh()` (replacing Clear+Rebuild):

1. If `IsCollapsed` → return (skip all work; the M3-seeded "collapsed skips work").
2. Snapshot `SnapshotEvents(maxRows)`; keep the existing count+tail short-circuit for "nothing new".
3. If the previous tail reference is found in the new snapshot (scan from the end, reference equality) → **append** only the events after it (respecting category filters), then trim `Rows` from the front while `Rows.Count > maxRows`.
4. If not found (first refresh, or ≥ maxRows new events since last tick) → full rebuild as today.
5. Filter toggle keeps its existing behavior (reset trackers → rebuild).
6. `OnIsCollapsedChanged(false)` → `Refresh()`.

- [ ] **Step 1: Add `PushCaptureSource` to TestDoubles.cs**

```csharp
// append to tests/OpenEC.Inspector.Tests/TestDoubles.cs
using System.Threading.Channels; // add to usings

/// <summary>Frames are pushed by the test and flow to the pump immediately —
/// lets a test grow the event log between two Refresh() calls.</summary>
internal sealed class PushCaptureSource : ICaptureSource
{
    private readonly Channel<RawFrame> _channel = Channel.CreateUnbounded<RawFrame>();

    public void Push(RawFrame frame) => _channel.Writer.TryWrite(frame);
    public void Complete() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Write the failing tests** (append to EventsViewModelTests; keep every existing test)

```csharp
// additions to tests/OpenEC.Inspector.Tests/ViewModels/EventsViewModelTests.cs
using System.Collections.Specialized;
using OpenEC.Inspector.Session;
using OpenEC.Monitor;
using OpenEC.Monitor.Capture;

// helpers inside the class:
private static async Task<List<RawFrame>> DemoFramesAsync()
{
    var frames = new List<RawFrame>();
    await using var source = new PcapFileSource(TestSessions.WriteDemoPcap());
    await foreach (var f in source.CaptureAsync()) frames.Add(f);
    return frames;
}

private static async Task WaitForFramesAsync(MonitorSession session, long count)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (session.FramesSeen < count)
    {
        Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {count} frames");
        await Task.Delay(10);
    }
}

[Fact]
public async Task New_events_are_appended_without_rebuilding_existing_rows()
{
    var frames = await DemoFramesAsync();
    var source = new PushCaptureSource();
    await using var session = new MonitorSession(
        EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
    session.Start();

    // First 61 frames cover the state change (cycle 16) and WKC mismatch (cycle 25).
    foreach (var f in frames.Take(61)) source.Push(f);
    await WaitForFramesAsync(session, 61);
    var vm = new EventsViewModel(session);
    vm.Refresh();
    Assert.True(vm.Rows.Count >= 2);
    var firstRow = vm.Rows[0];
    var countBefore = vm.Rows.Count;

    var actions = new List<NotifyCollectionChangedAction>();
    vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

    foreach (var f in frames.Skip(61)) source.Push(f);
    source.Complete();
    await WaitForFramesAsync(session, frames.Count);
    vm.Refresh();

    Assert.True(vm.Rows.Count > countBefore);
    Assert.Same(firstRow, vm.Rows[0]); // existing rows untouched
    Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Add, a));
    Assert.Contains(vm.Rows, r => r.Category == "Emergency");
    Assert.Contains(vm.Rows, r => r.Category == "SoE");
}

[Fact]
public async Task Appending_beyond_the_cap_trims_the_oldest_rows()
{
    var frames = await DemoFramesAsync();
    var source = new PushCaptureSource();
    await using var session = new MonitorSession(
        EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
    session.Start();

    foreach (var f in frames.Take(61)) source.Push(f);
    await WaitForFramesAsync(session, 61);
    var vm = new EventsViewModel(session, maxRows: 3);
    vm.Refresh();
    var before = vm.Rows.Count;
    Assert.InRange(before, 1, 3);

    foreach (var f in frames.Skip(61)) source.Push(f);
    source.Complete();
    await WaitForFramesAsync(session, frames.Count);
    vm.Refresh();

    Assert.True(vm.Rows.Count <= 3);
    Assert.Contains(vm.Rows, r => r.Category == "SoE"); // newest survived the trim
}

[Fact]
public async Task More_new_events_than_the_cap_trigger_a_full_rebuild()
{
    var frames = await DemoFramesAsync();
    var source = new PushCaptureSource();
    await using var session = new MonitorSession(
        EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
    session.Start();

    // maxRows 2: after the second batch, SnapshotEvents(2) no longer contains the
    // old tail (emergency + SoE alone fill the window), so append is impossible.
    foreach (var f in frames.Take(61)) source.Push(f);
    await WaitForFramesAsync(session, 61);
    var vm = new EventsViewModel(session, maxRows: 2);
    vm.Refresh();

    var actions = new List<NotifyCollectionChangedAction>();
    vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

    foreach (var f in frames.Skip(61)) source.Push(f);
    source.Complete();
    await WaitForFramesAsync(session, frames.Count);
    vm.Refresh();

    Assert.Contains(NotifyCollectionChangedAction.Reset, actions); // Rows.Clear() ran
    Assert.True(vm.Rows.Count <= 2);
    Assert.Contains(vm.Rows, r => r.Category == "SoE");
}

[Fact]
public async Task A_collapsed_panel_skips_refresh_and_catches_up_on_expand()
{
    await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
    var vm = new EventsViewModel(session) { IsCollapsed = true };

    vm.Refresh();
    Assert.Empty(vm.Rows);

    vm.IsCollapsed = false; // expanding refreshes immediately

    Assert.True(vm.Rows.Count >= 4);
}
```

Note: the append test's assertions are category-based on purpose — the exact event
count from the demo pcap is an implementation detail of `BusObserver`. If the
61-frame split does not put the state change and WKC mismatch in the first batch,
adjust the split index, not the assertions (`SampleCapture.WriteDemo` cycles: state
change at cycle 16, WKC at 25, emergency at 33, SoE at 41; ~2 frames per cycle plus
one extra frame at each of cycles 16/33/41).

- [ ] **Step 3: Run to verify the new tests fail**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter EventsViewModelTests`
Expected: the four new tests FAIL (no `maxRows` parameter / no `IsCollapsed`; clear+rebuild raises non-Add actions). Existing four tests still pass.

- [ ] **Step 4: Implement the diffing** per the behavior list above. Keep `MaxRows` as an instance field set from the constructor (default 500). The tail-scan and append are the interesting parts — **(sketch)**:

```csharp
public void Refresh()
{
    if (IsCollapsed) return;
    var events = _session.Observer.SnapshotEvents(_maxRows);
    if (events.Count == _lastCount && ReferenceEquals(events.Count > 0 ? events[^1] : null, _lastTail))
        return;
    var tailIndex = IndexOfTail(events, _lastTail);   // -1 when unseen
    _lastCount = events.Count;
    _lastTail = events.Count > 0 ? events[^1] : null;
    if (tailIndex < 0) { Rebuild(events); return; }
    for (var i = tailIndex + 1; i < events.Count; i++) AppendIfEnabled(events[i]);
    while (Rows.Count > _maxRows) Rows.RemoveAt(0);
}
```

`AppendIfEnabled` is the per-event body of today's `Rebuild` loop, factored out and
shared by both paths. `partial void OnIsCollapsedChanged(bool value) { if (!value) Refresh(); }`.

- [ ] **Step 5: Run to verify all pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter EventsViewModelTests`
Expected: 8 PASS.

- [ ] **Step 6: Full suite + commit**

```bash
dotnet test
git add src/OpenEC.Inspector/ViewModels/EventsViewModel.cs tests/OpenEC.Inspector.Tests/
git commit -m "feat(inspector): append-only event diffing and collapsible messages state"
```

---

### Task 5: `VariableWatchViewModel` (scoped PV watch)

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/VariableWatchViewModel.cs`
- Test: create `tests/OpenEC.Inspector.Tests/ViewModels/VariableWatchViewModelTests.cs`; delete `PvWatchViewModelTests.cs` (all five guarantees migrate here)
- Modify: `src/OpenEC.Inspector/ViewModels/PvWatchViewModel.cs` (delegate `FormatValue` to the new shared formatter; nothing else — the class stays alive until Task 9)

**Interfaces:**
- Consumes: `MonitorSession` (`Eni`, `ProcessImage.Current`), `EniSlave`, `EniVariable` (`Name`, `DataType`, `IsInput`), `VariableValue` (Task 1's assignment provides the per-slave lists).
- Produces (Tasks 7 and 9 consume these exact members):

```csharp
public sealed partial class VariableRowViewModel : ObservableObject
{
    public required string FullName { get; init; }   // ProcessImage lookup key
    public required string Name { get; init; }       // display name (prefix-stripped)
    public required string DataType { get; init; }
    public required string Direction { get; init; }  // "IN" / "OUT"
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _updated = "—";
}

public static class VariableValueFormat
{
    public static string Describe(VariableValue v);  // moved body of PvWatchViewModel.FormatValue
}

public sealed partial class VariableWatchViewModel : ObservableObject, IRefreshable
{
    public static VariableWatchViewModel ForSlave(MonitorSession session, Func<Task> requestLoadEni,
        EniSlave? slave, IReadOnlyList<EniVariable> variables);   // slave null: observed-only station
    public static VariableWatchViewModel ForUnmatched(MonitorSession session, Func<Task> requestLoadEni,
        IReadOnlyList<EniVariable> unmatched);
    public bool HasEni { get; }                       // session.Eni is not null (CTA when false)
    public ObservableCollection<VariableRowViewModel> Rows { get; }
    [ObservableProperty] private string _filterText;
    [RelayCommand] private Task LoadEniAsync();        // invokes requestLoadEni
    public void Refresh();
}
```

Behavior: rows are seeded from the **assigned variable list** (not just observed values — a mapped-but-quiet variable shows `—`). Seeding happens inside `Refresh()`, **not** the constructor — Task 7's editor test relies on an un-refreshed watch having empty `Rows`. Rows are sorted by `FullName` ordinal, display name = `FullName` minus `"{slave.Name}."` prefix in slave scope, full name in unmatched scope. `FilterText` narrows by display name, case-insensitive, rebuilding the row set (same pattern as today's `PvWatchViewModel.Refresh`: rebuild rows when the wanted set changes, otherwise update `Value`/`Updated` in place). Values come from `session.ProcessImage.Current.TryGetValue(FullName, …)` formatted by `VariableValueFormat.Describe`; missing → `—`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Inspector.Tests/ViewModels/VariableWatchViewModelTests.cs
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class VariableWatchViewModelTests
{
    private static readonly Func<Task> NoLoad = () => Task.CompletedTask;

    private static (EniSlave Slave, IReadOnlyList<EniVariable> Vars) DriveScope(EniConfiguration eni)
    {
        var slave = eni.Slaves.Single(s => s.PhysAddr == 1004);
        var vars = ProcessVariableAssignment.Build(eni).BySlave[1004];
        return (slave, vars);
    }

    [Fact]
    public async Task Slave_scope_lists_its_variables_with_stripped_names_sorted()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, vars);

        vm.Refresh();

        Assert.True(vm.HasEni);
        Assert.Equal(["Inputs.Statusword", "Outputs.Controlword"],
            vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["IN", "OUT"], vm.Rows.Select(r => r.Direction).ToArray());
        Assert.Equal(vars.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => v.DataType),
            vm.Rows.Select(r => r.DataType));
    }

    [Fact]
    public async Task Values_format_with_hex_bool_and_cia402_description()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni);
        var term2 = eni.Slaves.Single(s => s.PhysAddr == 1002);
        var (drive, driveVars) = DriveScope(eni);

        var driveVm = VariableWatchViewModel.ForSlave(session, NoLoad, drive, driveVars);
        driveVm.Refresh();
        var statusword = driveVm.Rows.Single(r => r.Name.EndsWith("Statusword"));
        Assert.StartsWith("0x0637 (1591)", statusword.Value);
        Assert.Contains(" — ", statusword.Value); // CiA-402 description appended

    var termVm = VariableWatchViewModel.ForSlave(session, NoLoad, term2, assignment.BySlave[1002]);
        termVm.Refresh();
        Assert.Equal("TRUE", termVm.Rows.Single(r => r.Name.Contains("Channel 1")).Value);
        Assert.Equal("FALSE", termVm.Rows.Single(r => r.Name.Contains("Channel 2")).Value);
    }

    [Fact]
    public async Task An_assigned_but_never_observed_variable_shows_a_placeholder()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var ghost = new EniVariable("Drive 4 (AX5101).Ghost", "BOOL", 1, 999, true);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, [.. vars, ghost]);

        vm.Refresh();

        var row = vm.Rows.Single(r => r.Name == "Ghost");
        Assert.Equal("—", row.Value);
        Assert.Equal("—", row.Updated);
    }

    [Fact]
    public async Task Filter_narrows_rows_case_insensitively_and_resets()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, vars);
        vm.Refresh();

        vm.FilterText = "statusword";
        Assert.Equal("Inputs.Statusword", Assert.Single(vm.Rows).Name);

        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Unmatched_scope_shows_full_names()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var ghost = new EniVariable("Ghost.Value", "INT", 16, 0, true);
        var vm = VariableWatchViewModel.ForUnmatched(session, NoLoad, [ghost]);

        vm.Refresh();

        Assert.Equal("Ghost.Value", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public async Task Without_eni_the_watch_reports_no_eni_and_stays_empty()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave: null, variables: []);

        vm.Refresh();

        Assert.False(vm.HasEni);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Load_eni_command_invokes_the_callback()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var invoked = false;
        var vm = VariableWatchViewModel.ForSlave(session,
            () => { invoked = true; return Task.CompletedTask; }, slave: null, variables: []);

        await vm.LoadEniCommand.ExecuteAsync(null);

        Assert.True(invoked);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter VariableWatchViewModelTests`
Expected: FAIL (compile error).

- [ ] **Step 3: Implement** `VariableWatchViewModel.cs` per the Produces block and behavior notes. Move the body of `PvWatchViewModel.FormatValue` into `VariableValueFormat.Describe` verbatim; change `PvWatchViewModel.FormatValue` to `internal static string FormatValue(VariableValue v) => VariableValueFormat.Describe(v);`.

- [ ] **Step 4: Delete `tests/.../PvWatchViewModelTests.cs`** — each of its five tests now has a successor: listing→`Slave_scope_lists…`, formatting→`Values_format…`, filter→`Filter_narrows…`, no-ENI→`Without_eni…`, callback→`Load_eni_command…`.

- [ ] **Step 5: Run the new tests, then the full suite**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter VariableWatchViewModelTests` → 7 PASS.
Run: `dotnet test` → all green.

- [ ] **Step 6: Commit**

```bash
git add -A src/OpenEC.Inspector/ViewModels/ tests/OpenEC.Inspector.Tests/ViewModels/
git commit -m "feat(inspector): scoped VariableWatchViewModel over ProcessVariableAssignment"
```

---

### Task 6: `ExplorerViewModel` + tree nodes

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/ExplorerViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession` (`Observer.SnapshotSlaves()`, `State`, `SourceDescription`), `StatusDotMap` (Task 3), `ProcessVariableAssignment` (Task 1).
- Produces (Task 9 and the Task 9 view consume these exact members):

```csharp
public abstract partial class ExplorerNode : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private StatusDot _dot;
}
public sealed partial class NetworkNode : ExplorerNode
{
    public ObservableCollection<ExplorerNode> Children { get; } = [];
}
public sealed partial class SlaveNode : ExplorerNode
{
    public required ushort Address { get; init; }
}
public sealed partial class ProcessImageNode : ExplorerNode { }

public sealed partial class ExplorerViewModel : ObservableObject
{
    public ExplorerViewModel(MonitorSession session, ProcessVariableAssignment? assignment,
        Action<ExplorerNode?> onSelected);
    public NetworkNode Root { get; }
    public IReadOnlyList<NetworkNode> RootItems { get; }   // == [Root]; TreeView ItemsSource
    [ObservableProperty] private ExplorerNode? _selectedNode;  // setter fires onSelected
    public void Refresh();
}
```

Behavior of `Refresh()`:
- Root: `Label = session.SourceDescription`, `Dot = StatusDotMap.ForSession(session.State)`.
- Slave nodes: upsert into `Root.Children` from `SnapshotSlaves()` ordered by address, updating `Label = $"{DisplayName} ({Address})"` and `Dot = StatusDotMap.ForSlave(status)` **on the existing instances** (Topology's row-reuse pattern — expansion/selection survive ticks). New addresses insert at their ordered position, always before the `ProcessImageNode`.
- `ProcessImageNode` (single cached instance, `Label = "Process Image"`, `Dot = Idle`): present as the **last** child when `assignment is null` (no ENI — the node hosts the Load-ENI CTA) **or** `assignment.Unmatched.Count > 0`; absent otherwise (spec-§4 visibility rule).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Inspector.Tests/ViewModels/ExplorerViewModelTests.cs
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class ExplorerViewModelTests
{
    private static readonly Action<ExplorerNode?> Ignore = _ => { };

    [Fact]
    public async Task Refresh_builds_root_and_ordered_slave_nodes()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        Assert.Equal(session.SourceDescription, vm.Root.Label);
        Assert.Equal(StatusDot.Idle, vm.Root.Dot); // completed file session
        var slaves = vm.Root.Children.OfType<SlaveNode>().ToList();
        Assert.Equal([1001, 1002, 1003, 1004], slaves.Select(s => (int)s.Address).ToArray());
        Assert.Equal("Term 1 (EK1100) (1001)", slaves[0].Label);
    }

    [Fact]
    public async Task The_faulted_drive_gets_a_fail_dot()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        var drive = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);
        Assert.Equal(StatusDot.Fail, drive.Dot); // SafeOp + error flag
    }

    [Fact]
    public async Task Refresh_updates_nodes_in_place()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);
        vm.Refresh();
        var before = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);

        vm.Refresh();

        Assert.Same(before, vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004));
        Assert.Equal(4, vm.Root.Children.OfType<SlaveNode>().Count());
    }

    [Fact]
    public async Task Without_eni_the_process_image_node_is_present_and_last()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new ExplorerViewModel(session, assignment: null, Ignore);

        vm.Refresh();

        Assert.IsType<ProcessImageNode>(vm.Root.Children[^1]);
    }

    [Fact]
    public async Task A_fully_matched_eni_hides_the_process_image_node()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        Assert.DoesNotContain(vm.Root.Children, n => n is ProcessImageNode);
    }

    [Fact]
    public async Task Unmatched_variables_keep_the_process_image_node_visible()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni) with
        {
            Unmatched = [new EniVariable("Ghost.Value", "INT", 16, 0, true)],
        };
        var vm = new ExplorerViewModel(session, assignment, Ignore);

        vm.Refresh();

        Assert.IsType<ProcessImageNode>(vm.Root.Children[^1]);
    }

    [Fact]
    public async Task Selecting_a_node_invokes_the_callback()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        ExplorerNode? seen = null;
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), n => seen = n);
        vm.Refresh();

        var drive = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);
        vm.SelectedNode = drive;

        Assert.Same(drive, seen);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/OpenEC.Inspector.Tests --filter ExplorerViewModelTests` → compile error.

- [ ] **Step 3: Implement** per the Produces block. Insert-position logic **(sketch)**: `Root.Children.Insert(Root.Children.OfType<SlaveNode>().TakeWhile(s => s.Address < status.Address).Count(), node)` — counting only `SlaveNode`s keeps the index correct whether or not the `ProcessImageNode` is attached; enforce the process-image rule at the end of `Refresh()` by adding/removing the cached node instance at the tail. `partial void OnSelectedNodeChanged(ExplorerNode? value) => _onSelected(value);`

- [ ] **Step 4: Run to verify pass** — 7 PASS, then `dotnet test` full suite.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs tests/OpenEC.Inspector.Tests/ViewModels/ExplorerViewModelTests.cs
git commit -m "feat(inspector): explorer tree view-model with status dots and process-image node"
```

---

### Task 7: `DeviceEditorViewModel`

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/DeviceEditorViewModel.cs` (also receives the **moved** `SlaveDetailViewModel` record, verbatim from `TopologyViewModel.cs`)
- Modify: `src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs` (remove `SlaveDetailViewModel` from it; the rest stays until Task 9)
- Test: create `tests/OpenEC.Inspector.Tests/ViewModels/DeviceEditorViewModelTests.cs`; delete `TopologyViewModelTests.cs` (guarantees migrated: slave listing/ordering/in-place-update → Task 6 explorer tests; detail-from-events and state/error → this task; clear-selection → Task 9 shell test)

**Interfaces:**
- Consumes: `SlaveDetailViewModel.Build(SlaveStatus?, IReadOnlyList<MonitorEvent>)` (moved, unchanged), `StatusDotMap.ForSlave`, `VariableWatchViewModel` (Task 5), `MonitorSession.Observer` snapshots.
- Produces (Task 9 consumes):

```csharp
public sealed partial class DeviceEditorViewModel : ObservableObject, IRefreshable
{
    public DeviceEditorViewModel(MonitorSession session, ushort address,
        VariableWatchViewModel variables);
    public ushort Address { get; }
    public VariableWatchViewModel Variables { get; }
    [ObservableProperty] private SlaveDetailViewModel? _detail;
    [ObservableProperty] private StatusDot _stateDot;
    [ObservableProperty] private string _stateLabel = "Unknown";
    [ObservableProperty] private string _lastSeen = "—";
    [ObservableProperty] private int _selectedTabIndex;   // 0 = General, 1 = Variables
    public void Refresh();
}
```

Behavior of `Refresh()`: look up this address in `SnapshotSlaves()`; set `StateDot`/`StateLabel` (`AlState.ToString()`)/`LastSeen` (`HH:mm:ss.fff` invariant, `—` when null); rebuild `Detail` via the moved `SlaveDetailViewModel.Build` with this address's events (reuse `TopologyViewModel`'s `AddressOf` switch — move that private helper here too). When `SelectedTabIndex == 1`, also `Variables.Refresh()` (the General tab's detail rebuild is cheap and unconditional; the variables scan only runs while visible).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Inspector.Tests/ViewModels/DeviceEditorViewModelTests.cs
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class DeviceEditorViewModelTests
{
    private static readonly Func<Task> NoLoad = () => Task.CompletedTask;

    private static async Task<(MonitorSession Session, DeviceEditorViewModel Editor)> DriveEditorAsync()
    {
        var eni = TestSessions.LoadFixtureEni();
        var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni);
        var slave = eni.Slaves.Single(s => s.PhysAddr == 1004);
        var watch = VariableWatchViewModel.ForSlave(session, NoLoad, slave, assignment.BySlave[1004]);
        return (session, new DeviceEditorViewModel(session, 1004, watch));
    }

    [Fact]
    public async Task Refresh_builds_the_general_tab_from_status_and_events()
    {
        var (session, editor) = await DriveEditorAsync();
        await using var _ = session;

        editor.Refresh();

        Assert.NotNull(editor.Detail);
        Assert.Equal("Drive 4 (AX5101)", editor.Detail!.Title);
        Assert.NotEmpty(editor.Detail.StateHistory);
        Assert.Equal(2, editor.Detail.MailboxActivity.Count); // one CoE emergency + one SoE error
        Assert.Equal(StatusDot.Fail, editor.StateDot);
        Assert.Equal("SafeOp", editor.StateLabel);
        Assert.NotEqual("—", editor.LastSeen);
    }

    [Fact]
    public async Task The_variables_tab_refreshes_only_while_selected()
    {
        var (session, editor) = await DriveEditorAsync();
        await using var _ = session;

        editor.SelectedTabIndex = 0;
        editor.Refresh();
        Assert.Empty(editor.Variables.Rows);   // not scanned yet

        editor.SelectedTabIndex = 1;
        editor.Refresh();
        Assert.Equal(2, editor.Variables.Rows.Count);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `DeviceEditorViewModelTests` → compile error.

- [ ] **Step 3: Implement**; move `SlaveDetailViewModel` and `AddressOf` out of `TopologyViewModel.cs` into the new file unchanged (namespace stays `OpenEC.Inspector.ViewModels`, so `TopologyViewModel` keeps compiling).

- [ ] **Step 4: Delete `TopologyViewModelTests.cs`** (successors listed above) and run the full suite.

Run: `dotnet test` → green.

- [ ] **Step 5: Commit**

```bash
git add -A src/OpenEC.Inspector/ViewModels/ tests/OpenEC.Inspector.Tests/ViewModels/
git commit -m "feat(inspector): tabbed DeviceEditorViewModel hosting slave detail and variables"
```

---

### Task 8: New views (explorer, editor, variables, messages panel, converter)

**Files:**
- Create: `src/OpenEC.Inspector/Views/StatusDotBrushConverter.cs`
- Create: `src/OpenEC.Inspector/Views/ExplorerView.axaml` + `.axaml.cs` (empty code-behind: `InitializeComponent` only)
- Create: `src/OpenEC.Inspector/Views/DeviceEditorView.axaml` + `.axaml.cs` (empty)
- Create: `src/OpenEC.Inspector/Views/VariableWatchView.axaml` + `.axaml.cs` (empty)
- Modify: `src/OpenEC.Inspector/Views/EventsView.axaml` (page → docked panel content) — its existing `.axaml.cs` auto-scroll logic is kept **unchanged**

These are UserControls not yet referenced by the shell — the old shell keeps running; verification is compile + full suite. Task 9 wires and smoke-tests them.

- [ ] **Step 1: `StatusDotBrushConverter.cs`**

```csharp
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

/// <summary>StatusDot → palette brush. Resolved at convert time against the active theme
/// variant; a live theme switch repaints a dot on its next value change (4 Hz tick), which
/// is an accepted approximation.</summary>
public sealed class StatusDotBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusDot.Ok => "Ok",
            StatusDot.Oos => "Oos",
            StatusDot.Fail => "Fail",
            _ => "Ink3",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush)
            ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: `ExplorerView.axaml`** — DataContext is `ExplorerViewModel`.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:OpenEC.Inspector.ViewModels"
             xmlns:views="using:OpenEC.Inspector.Views"
             x:Class="OpenEC.Inspector.Views.ExplorerView">
  <UserControl.Resources>
    <views:StatusDotBrushConverter x:Key="DotBrush" />
  </UserControl.Resources>
  <UserControl.Styles>
    <Style Selector="TreeViewItem">
      <Setter Property="IsExpanded" Value="True" />
    </Style>
  </UserControl.Styles>
  <Border Background="{DynamicResource Panel}"
          BorderBrush="{DynamicResource Line}" BorderThickness="0,0,1,0">
    <TreeView ItemsSource="{Binding RootItems}"
              SelectedItem="{Binding SelectedNode, Mode=TwoWay}">
      <TreeView.DataTemplates>
        <TreeDataTemplate DataType="vm:NetworkNode" ItemsSource="{Binding Children}">
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Ellipse Width="9" Height="9" VerticalAlignment="Center"
                     Fill="{Binding Dot, Converter={StaticResource DotBrush}}" />
            <TextBlock Text="{Binding Label}" FontWeight="SemiBold" />
          </StackPanel>
        </TreeDataTemplate>
        <DataTemplate DataType="vm:SlaveNode">
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Ellipse Width="9" Height="9" VerticalAlignment="Center"
                     Fill="{Binding Dot, Converter={StaticResource DotBrush}}" />
            <TextBlock Text="{Binding Label}" />
          </StackPanel>
        </DataTemplate>
        <DataTemplate DataType="vm:ProcessImageNode">
          <TextBlock Text="{Binding Label}" Foreground="{DynamicResource Ink2}" />
        </DataTemplate>
      </TreeView.DataTemplates>
    </TreeView>
  </Border>
</UserControl>
```

- [ ] **Step 3: `VariableWatchView.axaml`** — DataContext is `VariableWatchViewModel`. Port `PvWatchView.axaml`'s two states with the new columns and house styling **(sketch — bindings authoritative, layout adjustable)**:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.VariableWatchView">
  <Grid>
    <StackPanel IsVisible="{Binding !HasEni}" VerticalAlignment="Center"
                HorizontalAlignment="Center" Spacing="12">
      <TextBlock Text="No ENI loaded" FontSize="18" FontWeight="SemiBold"
                 HorizontalAlignment="Center" />
      <TextBlock Text="Variables need the process image from an ENI file."
                 TextWrapping="Wrap" MaxWidth="420" HorizontalAlignment="Center" />
      <Button Classes="accent" Content="Load ENI…" HorizontalAlignment="Center"
              Command="{Binding LoadEniCommand}" />
    </StackPanel>

    <DockPanel IsVisible="{Binding HasEni}">
      <TextBox DockPanel.Dock="Top" Watermark="Filter variables…"
               Text="{Binding FilterText}" Margin="0,0,0,8" />
      <ListBox ItemsSource="{Binding Rows}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Grid ColumnDefinitions="*,44,90,240,110">
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
              <TextBlock Grid.Column="1" Classes="label" Text="{Binding Direction}" />
              <TextBlock Grid.Column="2" Classes="label" Text="{Binding DataType}" />
              <TextBlock Grid.Column="3" Classes="mono" Text="{Binding Value}" />
              <TextBlock Grid.Column="4" Classes="label" Text="{Binding Updated}" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </DockPanel>
  </Grid>
</UserControl>
```

- [ ] **Step 4: `DeviceEditorView.axaml`** — DataContext is `DeviceEditorViewModel` **(sketch)**:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="using:OpenEC.Inspector.Views"
             x:Class="OpenEC.Inspector.Views.DeviceEditorView">
  <UserControl.Resources>
    <views:StatusDotBrushConverter x:Key="DotBrush" />
  </UserControl.Resources>
  <TabControl SelectedIndex="{Binding SelectedTabIndex}">
    <TabItem Header="General">
      <ScrollViewer>
        <StackPanel Spacing="12" Margin="4,12,4,4" DataContext="{Binding}">
          <Border Classes="panel">
            <StackPanel Spacing="8">
              <StackPanel Orientation="Horizontal" Spacing="8">
                <Ellipse Width="10" Height="10" VerticalAlignment="Center"
                         Fill="{Binding StateDot, Converter={StaticResource DotBrush}}" />
                <TextBlock Text="{Binding StateLabel}" FontWeight="SemiBold" />
                <TextBlock Classes="label" Text="{Binding LastSeen, StringFormat='last seen {0}'}" />
              </StackPanel>
              <TextBlock Text="{Binding Detail.Title}" FontWeight="SemiBold" FontSize="16" />
              <TextBlock Classes="label" Text="{Binding Detail.Identity}" TextWrapping="Wrap" />
              <TextBlock Classes="label" Text="{Binding Address, StringFormat='Physical address {0}'}" />
            </StackPanel>
          </Border>
          <Border Classes="panel">
            <StackPanel Spacing="8">
              <TextBlock Text="State history" FontWeight="SemiBold" />
              <ItemsControl ItemsSource="{Binding Detail.StateHistory}" />
            </StackPanel>
          </Border>
          <Border Classes="panel">
            <StackPanel Spacing="8">
              <TextBlock Text="Mailbox activity" FontWeight="SemiBold" />
              <ItemsControl ItemsSource="{Binding Detail.MailboxActivity}" />
            </StackPanel>
          </Border>
        </StackPanel>
      </ScrollViewer>
    </TabItem>
    <TabItem Header="Variables">
      <views:VariableWatchView DataContext="{Binding Variables}" Margin="4,12,4,4" />
    </TabItem>
  </TabControl>
</UserControl>
```

- [ ] **Step 5: Reshape `EventsView.axaml`** into the docked panel (code-behind untouched):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="OpenEC.Inspector.Views.EventsView">
  <DockPanel>
    <Border DockPanel.Dock="Top" Classes="panelHeader">
      <DockPanel>
        <ToggleButton DockPanel.Dock="Right" IsChecked="{Binding IsCollapsed}"
                      Padding="6,0" FontSize="11" Content="▾">
          <ToggleButton.Styles>
            <Style Selector="ToggleButton:checked">
              <Setter Property="Content" Value="▸" />
            </Style>
          </ToggleButton.Styles>
        </ToggleButton>
        <StackPanel Orientation="Horizontal" Spacing="16">
          <TextBlock Text="Messages" FontWeight="SemiBold" VerticalAlignment="Center" />
          <ItemsControl ItemsSource="{Binding Categories}">
            <ItemsControl.ItemsPanel>
              <ItemsPanelTemplate>
                <StackPanel Orientation="Horizontal" Spacing="12" />
              </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <CheckBox Content="{Binding Name}" IsChecked="{Binding IsEnabled}" FontSize="12" />
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
          <CheckBox Content="Auto-scroll" IsChecked="{Binding AutoScroll}" FontSize="12" />
        </StackPanel>
      </DockPanel>
    </Border>

    <ListBox x:Name="EventList" ItemsSource="{Binding Rows}" Height="180"
             IsVisible="{Binding !IsCollapsed}" Background="{DynamicResource Panel}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <Grid ColumnDefinitions="110,110,*">
            <TextBlock Classes="mono" Text="{Binding Time}" />
            <TextBlock Grid.Column="1" Classes="label" Text="{Binding Category}" />
            <TextBlock Grid.Column="2" Text="{Binding Description}" FontSize="12" TextWrapping="Wrap" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```

- [ ] **Step 6: Verify + commit**

Run: `dotnet build && dotnet test` — old shell still runs; new controls compile.

```bash
git add src/OpenEC.Inspector/Views/
git commit -m "feat(inspector): explorer, device-editor, variable-watch views and messages panel"
```

---

### Task 9: Shell rewire — `MainWindowViewModel` + `MainWindow.axaml` + deletions

**Files:**
- Modify: `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`
- Modify: `src/OpenEC.Inspector/Views/MainWindow.axaml`
- Delete: `src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs`, `src/OpenEC.Inspector/ViewModels/PvWatchViewModel.cs`, `src/OpenEC.Inspector/Views/TopologyView.axaml(.cs)`, `src/OpenEC.Inspector/Views/PvWatchView.axaml(.cs)`
- Test: rewrite the navigation tests in `MainWindowViewModelTests.cs`; migrate `Ui/ShellSmokeTests.cs`

**Interfaces:**
- Consumes: everything produced by Tasks 1, 3–8.
- Produces (views bind to these):

```csharp
public sealed partial class MainWindowViewModel : ObservableObject
{
    // constructor signature UNCHANGED (tests and Program.cs depend on it)
    public StartViewModel Start { get; }
    public MonitorSession? Session { get; }
    public DashboardViewModel? Dashboard { get; }
    public ExplorerViewModel? Explorer { get; }
    public EventsViewModel? Events { get; }
    [ObservableProperty] private object _currentPage;      // Start | Dashboard | DeviceEditor | VariableWatch
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _statusText;        // format frozen
    [ObservableProperty] private string? _faultMessage;
    [ObservableProperty] private StatusDot _sessionDot;
    public void Tick();
    // removed: InspectorSection, SelectedSection, SelectSectionCommand, Topology, PvWatch
}
```

Behavior changes:
- `OnSessionStarted`: build `_assignment = session.Eni is { } eni ? ProcessVariableAssignment.Build(eni) : null`; create `Explorer = new(session, _assignment, OnNodeSelected)`, `Events`, `Dashboard`; clear the per-address editor cache and the process-image page; `Explorer.SelectedNode = Explorer.Root` (drives `CurrentPage = Dashboard` through the callback); then `Tick()`.
- `OnNodeSelected(node)`: `NetworkNode`/`null` → `Dashboard`; `SlaveNode s` → editor from a `Dictionary<ushort, DeviceEditorViewModel>` cache (create with `VariableWatchViewModel.ForSlave(Session, RestartWithEniAsync, eniSlaveOrNull, assignedListOrEmpty)`); `ProcessImageNode` → cached `VariableWatchViewModel.ForUnmatched(Session, RestartWithEniAsync, _assignment?.Unmatched ?? [])`. Refresh the new page.
- `Tick()`: `Explorer?.Refresh()`, `(CurrentPage as IRefreshable)?.Refresh()`, `Events?.Refresh()` (self-guards collapse), `UpdateStatus()`, `SessionDot = Session is null ? StatusDot.Idle : StatusDotMap.ForSession(Session.State)`.
- `RestartWithEniAsync`: before detaching, capture `var selectedAddress = (Explorer?.SelectedNode as SlaveNode)?.Address;` — after `OnSessionStarted(next)`, reselect: `Explorer!.SelectedNode = Explorer.Root.Children.OfType<SlaveNode>().FirstOrDefault(n => n.Address == selectedAddress) ?? (ExplorerNode)Explorer.Root;` (replaces the old `SelectedSection = PvWatch`).
- `DetachSession`: additionally null out `Explorer`, `Events`, `_assignment`, clear caches.

- [ ] **Step 1: Rewrite the failing navigation tests** (replace `Section_selection_swaps_the_current_page` and `Load_eni_from_pv_watch_restarts_the_session_with_the_eni`; add the node tests; keep all other tests byte-identical — boot, stop, fault, stale-fault, status-line):

```csharp
// replacements/additions in tests/OpenEC.Inspector.Tests/ViewModels/MainWindowViewModelTests.cs
[Fact]
public async Task Node_selection_swaps_the_current_page()
{
    var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
    var vm = await CreateWithDemoSessionAsync(eniPath: eniPath);

    var explorer = vm.Explorer!;
    var drive = explorer.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1004);

    explorer.SelectedNode = drive;
    var editor = Assert.IsType<DeviceEditorViewModel>(vm.CurrentPage);
    Assert.Equal(1004, editor.Address);

    explorer.SelectedNode = explorer.Root;
    Assert.IsType<DashboardViewModel>(vm.CurrentPage);

    explorer.SelectedNode = drive;
    Assert.Same(editor, vm.CurrentPage); // editor instances are cached per address
}

[Fact]
public async Task Without_eni_the_process_image_node_shows_the_variable_watch()
{
    var vm = await CreateWithDemoSessionAsync();

    var node = vm.Explorer!.Root.Children.OfType<ProcessImageNode>().Single();
    vm.Explorer.SelectedNode = node;

    var watch = Assert.IsType<VariableWatchViewModel>(vm.CurrentPage);
    Assert.False(watch.HasEni);
}

[Fact]
public async Task Load_eni_from_a_device_editor_restarts_and_preserves_the_selection()
{
    var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
    var vm = await CreateWithDemoSessionAsync(picker: new FakeFilePicker(eniPath));

    // Without an ENI only station 1004 is observed on the wire.
    var drive = vm.Explorer!.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1004);
    vm.Explorer.SelectedNode = drive;
    var editor = (DeviceEditorViewModel)vm.CurrentPage;
    Assert.False(editor.Variables.HasEni);

    await editor.Variables.LoadEniCommand.ExecuteAsync(null);
    await vm.Session!.Completion;

    var reselected = Assert.IsType<SlaveNode>(vm.Explorer!.SelectedNode);
    Assert.Equal(1004, reselected.Address);
    var reloaded = Assert.IsType<DeviceEditorViewModel>(vm.CurrentPage);
    Assert.True(reloaded.Variables.HasEni);
    reloaded.SelectedTabIndex = 1;
    reloaded.Refresh();
    Assert.Equal(2, reloaded.Variables.Rows.Count);
}

[Fact]
public async Task Stopping_the_session_clears_explorer_and_events()
{
    var vm = await CreateWithDemoSessionAsync();

    await vm.StopSessionCommand.ExecuteAsync(null);

    Assert.Null(vm.Explorer);
    Assert.Null(vm.Events);
    Assert.Equal(StatusDot.Idle, vm.SessionDot);
}
```

Also update `Starting_a_file_session_switches_to_the_dashboard` with two extra asserts: `Assert.NotNull(vm.Explorer); Assert.NotNull(vm.Events);`.

- [ ] **Step 2: Run to verify failure** — filter `MainWindowViewModelTests` → compile errors (`Explorer` missing) and the old two tests reference removed members: delete them as part of Step 1.

- [ ] **Step 3: Implement `MainWindowViewModel`** per the behavior list. Delete `TopologyViewModel.cs` and `PvWatchViewModel.cs` in the same step (nothing references them once the VM is rewired — `SlaveDetailViewModel` already moved in Task 7, `FormatValue` already lives in `VariableValueFormat`).

- [ ] **Step 4: Rewrite `MainWindow.axaml`** — the shell **(sketch — binding paths authoritative)**:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:OpenEC.Inspector.ViewModels"
        xmlns:views="using:OpenEC.Inspector.Views"
        x:Class="OpenEC.Inspector.Views.MainWindow"
        Title="OpenEC Inspector"
        Width="1280" Height="800">
  <Window.Resources>
    <views:StatusDotBrushConverter x:Key="DotBrush" />
  </Window.Resources>

  <Window.DataTemplates>
    <DataTemplate DataType="{x:Type vm:StartViewModel}"><views:StartView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:DashboardViewModel}"><views:DashboardView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:DeviceEditorViewModel}"><views:DeviceEditorView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:VariableWatchViewModel}"><views:VariableWatchView /></DataTemplate>
  </Window.DataTemplates>

  <DockPanel>
    <!-- chrome status bar -->
    <Border DockPanel.Dock="Bottom" Classes="chrome" Padding="12,6"
            BorderBrush="{DynamicResource Accent}" BorderThickness="0,2,0,0">
      <StackPanel Orientation="Horizontal" Spacing="8">
        <Ellipse Width="9" Height="9" VerticalAlignment="Center"
                 Fill="{Binding SessionDot, Converter={StaticResource DotBrush}}" />
        <TextBlock Text="{Binding StatusText}" FontSize="12" />
      </StackPanel>
    </Border>

    <!-- chrome top bar (session only) -->
    <Border DockPanel.Dock="Top" Classes="chrome" Padding="16,10"
            BorderBrush="{DynamicResource Accent}" BorderThickness="0,0,0,2"
            IsVisible="{Binding HasSession}">
      <DockPanel>
        <Button DockPanel.Dock="Right" Content="Stop session"
                Command="{Binding StopSessionCommand}" />
        <StackPanel Orientation="Horizontal" Spacing="14" VerticalAlignment="Center">
          <TextBlock Text="OpenEC Inspector" FontSize="15" FontWeight="Bold" />
          <TextBlock Text="{Binding Session.SourceDescription}"
                     Foreground="{DynamicResource ChromeInk2}" VerticalAlignment="Center" />
        </StackPanel>
      </DockPanel>
    </Border>

    <Grid>
      <!-- start screen fills the window when no session -->
      <ContentControl Content="{Binding CurrentPage}" IsVisible="{Binding !HasSession}" />

      <Grid ColumnDefinitions="280,*" IsVisible="{Binding HasSession}">
        <views:ExplorerView DataContext="{Binding Explorer}" />
        <Grid Grid.Column="1" RowDefinitions="*,Auto">
          <ContentControl Content="{Binding CurrentPage}" Margin="12"
                          IsVisible="{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).HasSession}" />
          <views:EventsView Grid.Row="1" DataContext="{Binding Events}" />
        </Grid>
      </Grid>

      <!-- fault banner overlay: unchanged behavior, house Fail color -->
      <Border IsVisible="{Binding FaultMessage, Converter={x:Static ObjectConverters.IsNotNull}}"
              VerticalAlignment="Top" Background="{DynamicResource Fail}"
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

Note: with `HasSession == false` both ContentControls bind `CurrentPage` but only the outer one is visible; the inner `IsVisible` guard prevents the Start view from instantiating twice. If the `$parent[Window]` cast binding proves awkward, an acceptable alternative is a second VM property (`SessionPage`) — but prefer the single `CurrentPage`.

- [ ] **Step 5: Migrate `Ui/ShellSmokeTests.cs`** — replace the section walk:

```csharp
[AvaloniaFact]
public async Task A_file_session_renders_every_node_page_and_the_messages_panel()
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
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    // Walk every tree node while the window is live — templates must instantiate without throwing.
    foreach (var node in vm.Explorer!.Root.Children.Append<ExplorerNode>(vm.Explorer.Root).ToList())
    {
        vm.Explorer.SelectedNode = node;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // Editor tabs and the messages panel collapse must also instantiate.
    vm.Explorer.SelectedNode = vm.Explorer.Root.Children.OfType<SlaveNode>().First();
    ((DeviceEditorViewModel)vm.CurrentPage).SelectedTabIndex = 1;
    vm.Tick();
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    vm.Events!.IsCollapsed = true;
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    await vm.StopSessionCommand.ExecuteAsync(null);
    Assert.Same(vm.Start, vm.CurrentPage);
}
```

- [ ] **Step 6: Run everything**

Run: `dotnet test`
Expected: all green — no test may still mention `InspectorSection`, `TopologyViewModel`, or `PvWatchViewModel` (grep the test tree to confirm: `grep -rn "InspectorSection\|TopologyViewModel\|PvWatchViewModel" tests/ src/` → no hits).

- [ ] **Step 7: Manual smoke** — `dotnet run --project src/OpenEC.Inspector`, open the demo pcap (generate one via `SampleCapture` if needed) with the sample ENI: verify tree + dots, editor tabs, messages panel collapse, status bar dot, fault banner styling, and both OS theme variants.

- [ ] **Step 8: Commit**

```bash
git add -A src/OpenEC.Inspector tests/OpenEC.Inspector.Tests
git commit -m "feat(inspector): explorer shell with device tree, tabbed editor and docked messages panel"
```

---

### Task 10: Restyle Start screen + Dashboard, README, final sweep

**Files:**
- Modify: `src/OpenEC.Inspector/Views/StartView.axaml`, `src/OpenEC.Inspector/Views/DashboardView.axaml`, `README.md`

- [ ] **Step 1: StartView** — swap the `card` style to house tokens and promote the primary actions:
  - `Border.card`: `Background={DynamicResource Panel}`, `BorderBrush={DynamicResource Line}`, remove `CornerRadius` (global styles keep everything square, but delete the local `6` explicitly).
  - `Start capture` and `Analyze file` buttons get `Classes="accent"`.
  - Error text `Foreground="OrangeRed"` → `Foreground="{DynamicResource Fail}"`.
  - Hint text `Opacity=0.7` → `Classes="label"`.

- [ ] **Step 2: DashboardView** — delete the local tile styles that Task 2's `Border.tile`/`TextBlock.label`/`TextBlock.value` now provide (keep the local `Style Selector="Border.tile"` block deleted rather than empty); the only local styling that remains is per-tile layout (`Margin="0,0,12,12"`). Colors `#11888888`/`#33888888` and `CornerRadius="6"` must not survive anywhere: `grep -rn "888888\|CornerRadius=\"6\"\|OrangeRed" src/OpenEC.Inspector` → no hits.

- [ ] **Step 3: README** — in the `## 🔍 OpenEC.Inspector (GUI)` section, update the feature description to the explorer layout (device tree with status dots, tabbed device editor with General/Variables, docked messages panel, light/dark house theme). Two to four sentences; no screenshots.

- [ ] **Step 4: Full suite + visual pass**

Run: `dotnet test` and `dotnet run --project src/OpenEC.Inspector` (both theme variants).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Inspector/Views/StartView.axaml src/OpenEC.Inspector/Views/DashboardView.axaml README.md
git commit -m "feat(inspector): house-style start screen and dashboard tiles; README layout update"
```

---

## Coverage vs. spec (self-check)

- Spec §2 shell layout / removals → Tasks 8, 9. §3 theme table + hardcoded-color sweep → Tasks 2, 9 (fault banner), 10 (grep gate). §4 tree, dots, process-image rule, in-place refresh → Tasks 3, 6. §5 editor tabs, VM reshaping → Tasks 5, 7. §6 messages panel + append-only diffing + collapse → Tasks 4, 8. §7 SDK matcher → Task 1. §8 start screen/top bar/status bar → Tasks 9, 10. §9 testing → each task carries its migrations; deleted test files are enumerated with successors. §10 ledger → no task touches charts, binding-log smoke, `TrafficStatistics`, or the event-log cap.
