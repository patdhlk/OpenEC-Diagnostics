# Learning Mode — Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the learner built in Plan 1 into live and offline monitoring — progressive rebind, two-pass offline decode, ENI cross-checking, a learned-config cache — and surface it in the CLI and Inspector.

**Architecture:** `EtherCatMonitor` parses each frame once and hands it to both `BusObserver` and `BusLearner`. When no ENI is supplied the learner's configuration drives the observer through a new `ApplyConfiguration` entry point; when an ENI is supplied the ENI drives and the learner cross-checks, raising `ConfigMismatch`. Offline captures get a cheap discovery pass first, so 100% of process data is mapped. Learned configurations persist as ENI XML keyed by a bus fingerprint.

**Tech Stack:** .NET 8, C# latest, xunit, Avalonia 11.3.2, `Dahlke.EtherCAT.Esi` 0.10.0, `Dahlke.EtherCAT.Diagnostics` 0.10.0, Spectre.Console.Cli, SharpPcap 6.3.1.

**Spec:** `docs/superpowers/specs/2026-08-18-learning-mode-design.md` — this plan implements §5 (control flow), §7 (surfaces) and the two §9a deferrals. Plan 1 (`2026-08-18-learning-mode-core.md`) built everything under `src/OpenEC.Monitor/Learning/` that this plan consumes.

## Global Constraints

- Target framework `net8.0`, `Nullable` enabled, `ImplicitUsings` enabled (`Directory.Build.props`).
- **100% passive.** No code in this plan may transmit, inject, or write to a network interface. The ADS tier (Task 7) reads master-side diagnostics over ADS; that is a separate, already-existing optional module and does not touch the EtherCAT segment.
- **`OpenEC.Monitor` must not gain a dependency on `Dahlke.EtherCAT.Diagnostics`.** The dependency direction is `OpenEC.Monitor.Ads → OpenEC.Monitor`, and Task 7 must preserve it.
- `BusObserver`'s existing lock discipline holds: every write to `Bus`/`EventLog`/`ProcessImage` goes through a method that takes `_lock`, and concurrent readers use the `Snapshot*` accessors.
- Statistics and the event log are observations of the wire, not derivations of the configuration. A rebind must never reset them.
- **Do not modify `src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs` or `tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs`.** Both carry uncommitted work belonging to the repository owner. Every Inspector change in this plan is deliberately routed around them.
- **Never use partial namespace qualifiers in test code.** `OpenEC.Monitor.Tests` contains sibling
  namespaces named `Eni`, `Observation`, `Synthesis`, `Learning`, `Capture`, `Cli` and `Protocol`,
  each of which shadows the SDK namespace of the same name from inside the test assembly. So
  `Eni.EniConfiguration` resolves to `OpenEC.Monitor.Tests.Eni.EniConfiguration` and fails to
  compile. Always add a `using OpenEC.Monitor.<Namespace>;` and use the bare type name.
- Run one test class: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~<ClassName>"` (or `tests/OpenEC.Inspector.Tests` for Inspector classes).
- Run the full suite with `dotnet test`. Baseline at the start of this plan: **294 passing** (211 Monitor + 83 Inspector), 0 failures, 0 warnings.

## Domain facts this plan depends on

Researched rather than assumed; the cross-check is unusable if these are wrong.

- **Master-synthesised ENI variables have no wire representation.** TwinCAT's "Add WC state bit(s)" adds `WcState` and `InputToggle`, both `BIT`, computed by the master ([Data Area](https://infosys.beckhoff.com/content/1033/tf55xx_tc3_mc3/19396190219.html)). The InfoData group — `State`, `AdsAddr`, AoE NetId, Channels, DC shift times, `ObjectId` — is likewise master-side ([General Behavior](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1357974411.html)). A learned configuration cannot contain any of them, so their absence is never a mismatch.
- **`TxPdoState`, `DcInputShift` and `DcOutputShift` are genuine PDO entries** on many drives (they appear in the MDP742 data area with their own enable flags). They must **not** be excluded from the diff — excluding them would hide a real remapping, which is the one failure that makes a cross-check worthless.
- **`EtherCatScannedSlave`** (from `Dahlke.EtherCAT.Diagnostics`) carries `PhysicalAddress`, `VendorId`, `ProductCode`, `RevisionNumber`, `SerialNumber` — the identity tier §6 promises. `AdsEnrichment.PollAsync` already returns these as `AdsBusSnapshot.ScannedSlaves`.
- **`IFilePicker.PickSaveFileAsync(title, defaultName, extension)` already exists** in the Inspector, implemented over Avalonia's `IStorageProvider.SaveFilePickerAsync` with `FilePickerSaveOptions`, and `FakeFilePicker(saveResult:)` is already the test double. Task 9 reuses both; no new dialog plumbing.

---

### Task 1: Swappable configuration in ProcessImage and WkcTracker

**Files:**
- Modify: `src/OpenEC.Monitor/Observation/ProcessImage.cs`
- Modify: `src/OpenEC.Monitor/Observation/WkcTracker.cs`
- Test: `tests/OpenEC.Monitor.Tests/Observation/RebindTests.cs` (create)

**Interfaces:**
- Produces: `ProcessImage.Rebind(EniConfiguration?)` (internal) and `WkcTracker.Rebind(EniConfiguration?)` (public). Task 2 calls both.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Observation/RebindTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class RebindTests
{
    private static EniConfiguration Config(string variableName, int expectedWkc) => new()
    {
        Slaves = [],
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 1, expectedWkc, 0, null)],
        Variables = [new EniVariable(variableName, "USINT", 8, 0, true)],
    };

    private static EtherCatDatagram Logical(ushort wkc) =>
        new(EtherCatCommand.Lrd, 0, 0x00010000, false, false, 0, new byte[] { 0x42 }, wkc);

    [Fact]
    public void Process_image_decodes_nothing_until_it_is_rebound()
    {
        var image = new ProcessImage(null);

        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Empty(image.Current);

        image.Rebind(Config("Slave 1001.Input", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);

        Assert.Equal(0x42, Assert.Contains("Slave 1001.Input", image.Current).Value);
    }

    /// <summary>A rebind can rename variables — synthetic names become ESI-derived ones — so values
    /// decoded under the previous map must not linger under keys the new map can never produce.
    /// Otherwise the Inspector's watch shows phantom variables forever.</summary>
    [Fact]
    public void Rebinding_drops_values_decoded_under_the_previous_map()
    {
        var image = new ProcessImage(Config("Slave 1001.0x6000:01", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Single(image.Current);

        image.Rebind(Config("EL1008.Channel 1.Input", 1));

        Assert.Empty(image.Current);
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Contains("EL1008.Channel 1.Input", image.Current);
    }

    [Fact]
    public void Wkc_tracker_rebind_replaces_the_expected_value()
    {
        var tracker = new WkcTracker(Config("v", expectedWkc: 3));
        Assert.NotNull(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));

        tracker.Rebind(Config("v", expectedWkc: 2));

        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));
    }

    /// <summary>The observed-WKC histogram is evidence from the wire, not a derivation of the
    /// configuration, so a rebind must keep it — otherwise every rebind restarts the 20-frame
    /// learning threshold and no-ENI mismatch detection never converges on a live bus.</summary>
    [Fact]
    public void Wkc_tracker_rebind_keeps_the_observed_histogram()
    {
        var tracker = new WkcTracker();
        for (var i = 0; i < 25; i++)
            tracker.Observe(DateTimeOffset.UnixEpoch, Logical(3), FrameDirection.Returning);

        tracker.Rebind(null);

        Assert.NotNull(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RebindTests"`
Expected: FAIL — `Rebind` does not exist on either type.

- [ ] **Step 3: Make ProcessImage's map swappable**

In `src/OpenEC.Monitor/Observation/ProcessImage.cs`, change the field and constructor and add `Rebind`:

```csharp
    private readonly ConcurrentDictionary<string, VariableValue> _current = new();
    private ProcessVariableMap? _map;

    internal ProcessImage(EniConfiguration? eni) => Rebind(eni);

    public IReadOnlyDictionary<string, VariableValue> Current => _current;

    /// <summary>Swaps the variable map when a learned configuration arrives or is refined.
    /// Values decoded under the previous map are dropped: a rebind can rename variables — a
    /// synthetic `0x6000:01` becomes an ESI-derived `Channel 1.Input` — and keeping the old keys
    /// would leave entries in the watch that the new map can never refresh.</summary>
    internal void Rebind(EniConfiguration? eni)
    {
        _map = eni is null ? null : ProcessVariableMap.Build(eni);
        _current.Clear();
    }
```

The `_map is null` guards in `UpdateInputs`/`UpdateOutputs` stay as they are.

- [ ] **Step 4: Make WkcTracker's expectations swappable**

In `src/OpenEC.Monitor/Observation/WkcTracker.cs`, change the field to non-readonly and replace the constructor body:

```csharp
    private Dictionary<(EtherCatCommand, uint), ushort> _expectedFromEni = new();

    public WkcTracker(EniConfiguration? eni = null) => Rebind(eni);

    /// <summary>Replaces the ENI-derived expectations. Built into a local and assigned as one
    /// reference so a concurrent reader never sees a half-populated table.
    ///
    /// The observed-mode histogram in <c>_observed</c> is deliberately NOT cleared: it is evidence
    /// gathered from the wire, independent of which configuration is loaded, and discarding it
    /// would restart the 20-frame learning threshold on every rebind — so a live bus whose
    /// configuration is still being refined would never converge on mismatch detection.</summary>
    public void Rebind(EniConfiguration? eni)
    {
        var expected = new Dictionary<(EtherCatCommand, uint), ushort>();
        foreach (var cmd in eni?.CyclicCommands ?? Enumerable.Empty<EniCyclicCommand>())
            expected[(cmd.Command, cmd.RawAddress)] = (ushort)cmd.ExpectedWkc;
        _expectedFromEni = expected;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RebindTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Observation/ProcessImage.cs src/OpenEC.Monitor/Observation/WkcTracker.cs tests/OpenEC.Monitor.Tests/Observation/RebindTests.cs
git commit -m "feat(observation): make process-image and WKC configuration swappable"
```

---

### Task 2: BusObserver.ApplyConfiguration

**Files:**
- Modify: `src/OpenEC.Monitor/Observation/BusObserver.cs`
- Test: `tests/OpenEC.Monitor.Tests/Observation/ApplyConfigurationTests.cs` (create)

**Interfaces:**
- Consumes: `ProcessImage.Rebind`, `WkcTracker.Rebind` (Task 1); `LearnedConfiguration` (Plan 1).
- Produces: `BusObserver.ApplyConfiguration(LearnedConfiguration)` and `BusObserver.Applied` (`LearnedConfiguration?`). Tasks 3, 5, 9 consume both.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Observation/ApplyConfigurationTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class ApplyConfigurationTests
{
    /// <summary>Learns the synthetic bringup, then hands the result to a fresh observer — the shape
    /// Task 3 wires up for a live session that started with no ENI.</summary>
    private static LearnedConfiguration LearnBringup()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!;
    }

    private static void Pump(BusObserver observer, int cycles = 5)
    {
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles))
            observer.Process(timestamp, EtherCatFrameParser.Parse(frame));
    }

    [Fact]
    public void Applying_a_configuration_names_the_slaves()
    {
        var observer = new BusObserver();
        Pump(observer);
        Assert.All(observer.SnapshotSlaves(), s => Assert.Null(s.ConfiguredName));

        observer.ApplyConfiguration(LearnBringup());

        var slave = observer.SnapshotSlaves().Single(s => s.Address == 1001);
        Assert.NotNull(slave.ConfiguredName);
        Assert.Equal(2u, slave.VendorId);
    }

    [Fact]
    public void Applying_a_configuration_maps_process_variables()
    {
        var observer = new BusObserver();
        Pump(observer);
        Assert.Empty(observer.ProcessImage.Current);

        observer.ApplyConfiguration(LearnBringup());
        Pump(observer);

        Assert.Equal(16, observer.ProcessImage.Current.Count);
    }

    /// <summary>Statistics and the event log are observations of the wire, not derivations of the
    /// configuration. A rebind that reset them would discard everything learned about bus health.</summary>
    [Fact]
    public void Applying_a_configuration_preserves_statistics_and_the_event_log()
    {
        var observer = new BusObserver();
        Pump(observer);
        var frames = observer.Statistics.EtherCatFrames;
        var events = observer.SnapshotEvents().Count;
        Assert.True(frames > 0);
        Assert.True(events > 0);

        observer.ApplyConfiguration(LearnBringup());

        Assert.Equal(frames, observer.Statistics.EtherCatFrames);
        Assert.Equal(events, observer.SnapshotEvents().Count);
    }

    [Fact]
    public void Applied_exposes_the_configuration_in_force()
    {
        var observer = new BusObserver();
        Assert.Null(observer.Applied);

        var learned = LearnBringup();
        observer.ApplyConfiguration(learned);

        Assert.Same(learned, observer.Applied);
    }

    /// <summary>ApplyConfiguration takes the same lock as Process, so a rebind arriving from the
    /// schema-resolution timer while the pump is mid-frame must not corrupt state or throw.</summary>
    [Fact]
    public async Task Applying_a_configuration_concurrently_with_processing_is_safe()
    {
        var observer = new BusObserver();
        var learned = LearnBringup();
        var frames = BringupCapture.Frames(cycles: 40).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var pump = Task.Run(() =>
        {
            for (var round = 0; round < 20 && !cts.IsCancellationRequested; round++)
                foreach (var (timestamp, frame) in frames)
                    observer.Process(timestamp, EtherCatFrameParser.Parse(frame));
        }, cts.Token);

        var rebind = Task.Run(() =>
        {
            for (var round = 0; round < 200 && !cts.IsCancellationRequested; round++)
            {
                observer.ApplyConfiguration(learned);
                Assert.NotNull(observer.SnapshotSlaves());
                Assert.NotNull(observer.SnapshotEvents());
            }
        }, cts.Token);

        await Task.WhenAll(pump, rebind);
        Assert.True(observer.Statistics.EtherCatFrames > 0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~ApplyConfigurationTests"`
Expected: FAIL — `ApplyConfiguration` and `Applied` do not exist.

- [ ] **Step 3: Add the entry point**

In `src/OpenEC.Monitor/Observation/BusObserver.cs`, change `_eni` from readonly and add the method. Place `ApplyConfiguration` immediately after `SetResolvedDeviceName`, so the three lock-taking entry points sit together:

```csharp
    private EniConfiguration? _eni;
```

```csharp
    /// <summary>The configuration most recently applied by <see cref="ApplyConfiguration"/>,
    /// or null when the observer is still running on whatever it was constructed with.</summary>
    public LearnedConfiguration? Applied { get; private set; }

    /// <summary>Rebinds the observer to a learned configuration, under the same lock as
    /// <see cref="Process"/> and <see cref="SetResolvedDeviceName"/> — the third and last writer
    /// to <see cref="Bus"/>, so a rebind can never race a concurrent <see cref="SnapshotSlaves"/>.
    ///
    /// Identity, names, the auto-increment map, the process-variable map, WKC expectations and the
    /// mailbox windows all come from the new configuration. <see cref="Statistics"/> and the event
    /// log are deliberately untouched: they are observations of the wire, not derivations of the
    /// configuration, and resetting them on every refinement would discard the bus-health history
    /// a diagnostic session exists to accumulate.</summary>
    public void ApplyConfiguration(LearnedConfiguration config)
    {
        lock (_lock)
        {
            _eni = config.Configuration;
            Bus.Seed(config.Configuration);
            ProcessImage.Rebind(config.Configuration);
            _wkc.Rebind(config.Configuration);
            Applied = config;
        }
    }
```

`IsMailboxWindow` already reads `_eni?.Slaves`, so learned mailbox ranges replace its `0x1000–0x2000` fallback with no further change.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~ApplyConfigurationTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS. `BusObserverTests` covers the pre-existing lock behaviour and must not regress.

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Observation/BusObserver.cs tests/OpenEC.Monitor.Tests/Observation/ApplyConfigurationTests.cs
git commit -m "feat(observation): BusObserver.ApplyConfiguration rebinds without losing history"
```

---

### Task 3: Monitor wiring — one parse, two consumers

**Files:**
- Modify: `src/OpenEC.Monitor/EtherCatMonitorOptions.cs`
- Modify: `src/OpenEC.Monitor/EtherCatMonitor.cs`
- Modify: `src/OpenEC.Monitor/Learning/BusLearner.cs` (add a lock)
- Test: `tests/OpenEC.Monitor.Tests/LearningIntegrationTests.cs` (create)

**Interfaces:**
- Consumes: `BusObserver.ApplyConfiguration` (Task 2); `BusLearner` (Plan 1).
- Produces: `LearningMode` enum, `EtherCatMonitorOptions.Learning`, `EtherCatMonitor.Learned` (`LearnedConfiguration?`). Tasks 5, 7, 8 consume these.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/LearningIntegrationTests.cs`:

```csharp
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests;

public class LearningIntegrationTests
{
    /// <summary>Replays the synthetic bringup frame by frame through a live-shaped source, so the
    /// monitor cannot take the offline two-pass route.</summary>
    private sealed class LiveShapedSource(IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> frames)
        : ICaptureSource
    {
        public async IAsyncEnumerable<RawFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var (timestamp, frame) in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return new RawFrame(timestamp, frame);
            }
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task With_no_eni_the_learned_configuration_drives_the_process_image()
    {
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source);

        await monitor.RunAsync();

        Assert.NotNull(monitor.Learned);
        Assert.Equal(16, monitor.Learned!.Configuration.Variables.Count);
        // Learning converges during startup, so the cyclic frames that follow are mapped.
        Assert.NotEmpty(monitor.ProcessImage.Current);
    }

    [Fact]
    public async Task Learning_off_leaves_the_monitor_exactly_as_it_was()
    {
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Learning = LearningMode.Off });

        await monitor.RunAsync();

        Assert.Null(monitor.Learned);
        Assert.Empty(monitor.ProcessImage.Current);
        Assert.True(monitor.Statistics.EtherCatFrames > 0);
    }

    /// <summary>With an ENI supplied the ENI drives: the learner still runs (Task 4 cross-checks
    /// with it) but must not rebind the observer out from under the declared configuration.</summary>
    [Fact]
    public async Task With_an_eni_the_learner_runs_but_does_not_rebind()
    {
        var eni = Eni.EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Eni = eni });

        await monitor.RunAsync();

        Assert.NotNull(monitor.Learned);
        Assert.Null(monitor.Observer.Applied);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningIntegrationTests"`
Expected: FAIL — `LearningMode` and `monitor.Learned` do not exist.

- [ ] **Step 3: Add the option**

In `src/OpenEC.Monitor/EtherCatMonitorOptions.cs`:

```csharp
/// <summary>Whether the passive learner runs alongside the observer. `Auto` is the default: with
/// no ENI it supplies the configuration, and with an ENI it cross-checks against it.</summary>
public enum LearningMode { Auto, Off }
```

and on the options class:

```csharp
    public LearningMode Learning { get; set; } = LearningMode.Auto;
```

- [ ] **Step 4: Make BusLearner safe for the resolution timer**

`BusLearner.Observe` runs on the pump thread while `ResolveSchemasAsync` reads `_bus.Slaves` from a timer — and `LearnedBus` is documented as not thread-safe. Add a gate to `src/OpenEC.Monitor/Learning/BusLearner.cs`:

```csharp
    private readonly object _gate = new();
```

Wrap `Observe`'s body:

```csharp
    public void Observe(DateTimeOffset timestamp, FrameDecodeResult decoded)
    {
        if (decoded is not FrameDecodeResult.Success ok) return;
        lock (_gate)
        {
            var direction = _direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                _bus.Observe(timestamp, datagram, direction);
            Republish();
        }
    }
```

And in `ResolveSchemasAsync`, take the gate for the two synchronous halves while leaving the `await` outside it — holding a lock across an await would serialise the pump against ESI file I/O:

```csharp
    public async Task ResolveSchemasAsync(CancellationToken ct = default)
    {
        if (_esiDirectory is null) return;

        List<LearnedSlave> pending;
        lock (_gate)
        {
            pending = _bus.Slaves
                .Where(s => !_schemas.ContainsKey(s.StationAddress)
                            && s.VendorId is not null && s.ProductCode is not null)
                .ToList();
        }
        if (pending.Count == 0) return;

        var resolved = new Dictionary<ushort, EsiDevice>();
        using var enricher = new EsiEnricher(_esiDirectory);
        foreach (var slave in pending)
        {
            ct.ThrowIfCancellationRequested();
            var device = await enricher.ResolveDeviceAsync(
                slave.VendorId!.Value, slave.ProductCode!.Value, slave.Revision ?? 0);
            if (device is not null) resolved[slave.StationAddress] = device;
        }

        if (resolved.Count == 0) return;
        lock (_gate)
        {
            foreach (var (address, device) in resolved) _schemas[address] = device;
            Republish(force: true);
        }
    }
```

Note the `resolved.Count == 0` early return keeps the no-new-schemas case from republishing, preserving the revision-churn guarantee.

- [ ] **Step 5: Wire the learner into the monitor**

In `src/OpenEC.Monitor/EtherCatMonitor.cs`, add the field and constructor wiring:

```csharp
    private static readonly TimeSpan SchemaResolveInterval = TimeSpan.FromSeconds(2);

    private readonly BusLearner? _learner;
```

In the constructor, after `Observer.EventRaised += …`:

```csharp
        if (options.Learning != LearningMode.Off)
        {
            _learner = new BusLearner(options.EsiDirectory);
            // With an ENI supplied the ENI is the authority and the learner only cross-checks, so
            // it must not rebind the observer. With no ENI, its configuration is all we have.
            if (options.Eni is null)
                _learner.ConfigurationLearned += Observer.ApplyConfiguration;
        }
```

Add the accessor:

```csharp
    /// <summary>The configuration the learner has derived from observed traffic, or null when
    /// learning is off or nothing has been learned yet.</summary>
    public LearnedConfiguration? Learned => _learner?.Current;
```

Replace the body of `RunAsync`'s capture loop so each frame is parsed once and handed to both consumers, and run schema resolution on a timer alongside the pump:

```csharp
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resolver = _learner is null
            ? Task.CompletedTask
            : ResolveSchemasPeriodicallyAsync(linked.Token);
        try
        {
            await EnrichNamesAsync();
            await foreach (var raw in _source.CaptureAsync(ct))
            {
                var decoded = EtherCatFrameParser.Parse(raw.Data);
                Observer.Process(raw.Timestamp, decoded);
                _learner?.Observe(raw.Timestamp, decoded);
            }
            // A final resolution pass after the capture ends: an offline file or a stopped live
            // session may have learned identities in its last frames.
            if (_learner is not null) await _learner.ResolveSchemasAsync(ct);
        }
        finally
        {
            linked.Cancel();
            try { await resolver; } catch (OperationCanceledException) { /* expected on stop */ }
            _events.Writer.TryComplete();
        }
    }

    /// <summary>ESI lookup is async and the capture pump is not, so resolution runs on its own
    /// cadence. `ResolveSchemasAsync` returns immediately once every identity is either resolved or
    /// unresolvable, so a converged session costs nothing per tick.</summary>
    private async Task ResolveSchemasPeriodicallyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SchemaResolveInterval, ct);
                await _learner!.ResolveSchemasAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
```

Keep the existing `try`/`finally` shape's other contents (the `_events.Writer.TryComplete()` was already in the `finally`).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningIntegrationTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. `EtherCatMonitorTests` and the CLI tests exercise `RunAsync` heavily — a regression there means the loop restructuring changed behaviour.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Monitor/EtherCatMonitorOptions.cs src/OpenEC.Monitor/EtherCatMonitor.cs src/OpenEC.Monitor/Learning/BusLearner.cs tests/OpenEC.Monitor.Tests/LearningIntegrationTests.cs
git commit -m "feat(monitor): one parse, two consumers — learner runs alongside the observer"
```

---

### Task 4: ENI cross-check

**Files:**
- Modify: `src/OpenEC.Monitor/Observation/MonitorEvents.cs`
- Create: `src/OpenEC.Monitor/Learning/ConfigurationDiff.cs`
- Modify: `src/OpenEC.Monitor/EtherCatMonitor.cs` (raise the event)
- Test: `tests/OpenEC.Monitor.Tests/Learning/ConfigurationDiffTests.cs` (create)

**Interfaces:**
- Produces: `MonitorEvent.ConfigMismatch`, `ConfigMismatchKind`, `ConfigurationDiff.Compare(EniConfiguration declared, EniConfiguration learned) → IReadOnlyList<ConfigMismatch>`. Tasks 8 and 10 consume both.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/ConfigurationDiffTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class ConfigurationDiffTests
{
    private static EniConfiguration Config(
        IReadOnlyList<EniSlave>? slaves = null, IReadOnlyList<EniVariable>? variables = null) => new()
    {
        Slaves = slaves ?? [new EniSlave("Term 1 (EL1008)", 1001, 0x0000, 2, 0x03F03052, 0x00120000, null, null)],
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 1, 1, 0, null)],
        Variables = variables ?? [new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true)],
    };

    [Fact]
    public void Identical_configurations_produce_no_mismatches()
    {
        Assert.Empty(ConfigurationDiff.Compare(Config(), Config()));
    }

    [Fact]
    public void A_different_product_code_at_the_same_address_is_an_identity_mismatch()
    {
        var learned = Config([
            new EniSlave("Term 1 (EL2008)", 1001, 0x0000, 2, 0x07D83052, 0x00110000, null, null)]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.Identity, mismatch.Kind);
        Assert.Equal(1001, mismatch.Address);
    }

    [Fact]
    public void A_slave_the_bus_never_showed_is_reported_missing()
    {
        var learned = Config(slaves: []);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.SlaveMissing, mismatch.Kind);
    }

    [Fact]
    public void A_slave_the_eni_never_declared_is_reported_unexpected()
    {
        var declared = Config(slaves: []);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(declared, Config()));

        Assert.Equal(ConfigMismatchKind.SlaveUnexpected, mismatch.Kind);
    }

    /// <summary>TwinCAT's "Add WC state bit(s)" injects WcState and InputToggle into the ENI's
    /// process image; the master computes them and they never appear on the wire. A learned
    /// configuration therefore cannot contain them, and reporting their absence would raise a
    /// mismatch on every real TwinCAT configuration.</summary>
    [Fact]
    public void Master_synthesised_variables_are_not_mismatches()
    {
        var declared = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true),
            new EniVariable("Term 1 (EL1008).WcState", "BOOL", 1, 8, true),
            new EniVariable("Term 1 (EL1008).InputToggle", "BOOL", 1, 9, true),
            new EniVariable("Term 1 (EL1008).InfoData.State", "UINT", 16, 16, true),
            new EniVariable("Term 1 (EL1008).InfoData.AdsAddr", "UDINT", 32, 32, true),
        ]);

        Assert.Empty(ConfigurationDiff.Compare(declared, Config()));
    }

    /// <summary>TxPdoState is a genuine PDO entry on many drives, not a master-computed bit.
    /// Excluding it would hide a real remapping — the failure that makes a cross-check worthless.</summary>
    [Fact]
    public void A_missing_real_pdo_entry_is_still_a_mismatch()
    {
        var declared = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true),
            new EniVariable("Drive 2 (AX5101).Inputs.TxPdoState", "BOOL", 1, 8, true),
        ]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(declared, Config()));

        Assert.Equal(ConfigMismatchKind.ProcessImage, mismatch.Kind);
        Assert.Contains("TxPdoState", mismatch.Declared);
    }

    [Fact]
    public void A_variable_at_a_different_offset_is_a_process_image_mismatch()
    {
        var learned = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 8, true)]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.ProcessImage, mismatch.Kind);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~ConfigurationDiffTests"`
Expected: FAIL — `ConfigurationDiff` does not exist.

- [ ] **Step 3: Add the event**

In `src/OpenEC.Monitor/Observation/MonitorEvents.cs`, add inside the `MonitorEvent` record:

```csharp
    /// <summary>The declared ENI and the passively learned configuration disagree.</summary>
    public sealed record ConfigMismatch(DateTimeOffset Timestamp, ConfigMismatchKind Kind,
        ushort? Address, string Declared, string Observed) : MonitorEvent(Timestamp);

    /// <summary>A learned configuration was published. Spec §7 puts this on the event stream so a
    /// session's log shows when the picture of the bus changed, and by how much.</summary>
    public sealed record ConfigurationLearned(DateTimeOffset Timestamp, int Revision, string Summary)
        : MonitorEvent(Timestamp);
```

and at namespace level in the same file:

```csharp
public enum ConfigMismatchKind { SlaveMissing, SlaveUnexpected, Identity, ProcessImage }
```

- [ ] **Step 4: Write the diff**

`src/OpenEC.Monitor/Learning/ConfigurationDiff.cs`:

```csharp
using System.Globalization;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;

namespace OpenEC.Monitor.Learning;

/// <summary>Compares a declared ENI against what the bus actually showed. This is the diagnostic
/// the commercial tools do not advertise: "your ENI no longer matches the machine."</summary>
public static class ConfigurationDiff
{
    /// <summary>Variables a master synthesises into the ENI's process image that have no wire
    /// representation, so a learned configuration can never contain them.
    ///
    /// TwinCAT's "Add WC state bit(s)" adds `WcState` and `InputToggle`, both computed by the
    /// master, and the whole `InfoData` group (State, AdsAddr, AoE NetId, Channels, DC shift times,
    /// ObjectId) is master-side bookkeeping. Matching `InfoData` as a path segment rather than by
    /// leaf name is deliberate: it is exact, and it cannot accidentally swallow a real variable.
    ///
    /// Deliberately NOT excluded: `TxPdoState`, `DcInputShift` and `DcOutputShift`. Those are
    /// genuine PDO entries on many drives, and excluding them would hide a real remapping — the
    /// one failure that would make this whole comparison worthless.</summary>
    private static bool IsMasterSynthesised(EniVariable variable)
    {
        if (variable.Name.Contains(".InfoData.", StringComparison.Ordinal)) return true;
        var leaf = variable.Name.AsSpan()[(variable.Name.LastIndexOf('.') + 1)..];
        return leaf.Equals("WcState", StringComparison.Ordinal)
            || leaf.Equals("InputToggle", StringComparison.Ordinal);
    }

    public static IReadOnlyList<MonitorEvent.ConfigMismatch> Compare(
        EniConfiguration declared, EniConfiguration learned) =>
        Compare(declared, learned, DateTimeOffset.UnixEpoch);

    public static IReadOnlyList<MonitorEvent.ConfigMismatch> Compare(
        EniConfiguration declared, EniConfiguration learned, DateTimeOffset timestamp)
    {
        var mismatches = new List<MonitorEvent.ConfigMismatch>();
        var learnedSlaves = learned.Slaves.ToDictionary(s => s.PhysAddr);

        foreach (var slave in declared.Slaves)
        {
            if (!learnedSlaves.TryGetValue(slave.PhysAddr, out var observed))
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.SlaveMissing, slave.PhysAddr,
                    Identity(slave), "not seen on the bus"));
                continue;
            }
            // Identity is only comparable when the wire actually revealed it; a zero means "not
            // observed" (startup checking disabled), which is a completeness gap, not a mismatch.
            if (observed.VendorId != 0 && observed.ProductCode != 0
                && (observed.VendorId != slave.VendorId || observed.ProductCode != slave.ProductCode))
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.Identity, slave.PhysAddr,
                    Identity(slave), Identity(observed)));
            }
        }

        foreach (var slave in learned.Slaves.Where(s => declared.Slaves.All(d => d.PhysAddr != s.PhysAddr)))
        {
            mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                ConfigMismatchKind.SlaveUnexpected, slave.PhysAddr,
                "not in the ENI", Identity(slave)));
        }

        var learnedVariables = learned.Variables
            .ToDictionary(v => (v.Name, v.IsInput), v => v.BitOffs);
        foreach (var variable in declared.Variables.Where(v => !IsMasterSynthesised(v)))
        {
            if (!learnedVariables.TryGetValue((variable.Name, variable.IsInput), out var offset))
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.ProcessImage, null,
                    $"{variable.Name} @bit {variable.BitOffs}", "not in the learned image"));
            }
            else if (offset != variable.BitOffs)
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.ProcessImage, null,
                    $"{variable.Name} @bit {variable.BitOffs}", $"@bit {offset}"));
            }
        }

        return mismatches;
    }

    private static string Identity(EniSlave slave) => string.Create(CultureInfo.InvariantCulture,
        $"{slave.Name} (0x{slave.VendorId:X4}:0x{slave.ProductCode:X8})");
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~ConfigurationDiffTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Raise the event from the monitor**

In `src/OpenEC.Monitor/EtherCatMonitor.cs`'s constructor, replace the learner wiring added in Task 3 with:

```csharp
        if (options.Learning != LearningMode.Off)
        {
            _learner = new BusLearner(options.EsiDirectory);
            _learner.ConfigurationLearned += OnConfigurationLearned;
        }
```

and add the handler as a private method:

```csharp
    /// <summary>Every learned revision lands here. With an ENI supplied the ENI is the authority and
    /// this only reports disagreements; with no ENI the learned configuration is all we have, so it
    /// rebinds the observer.</summary>
    private void OnConfigurationLearned(LearnedConfiguration learned)
    {
        if (_options.Eni is { } declared)
        {
            foreach (var mismatch in ConfigurationDiff.Compare(
                         declared, learned.Configuration, DateTimeOffset.UtcNow))
                Observer.Raise(mismatch);
            return;
        }

        Observer.ApplyConfiguration(learned);
        Observer.Raise(new MonitorEvent.ConfigurationLearned(
            DateTimeOffset.UtcNow, learned.Revision, learned.Completeness.Summary));
    }
```

`BusObserver.Raise` is currently private. Change it to `internal` and give it a doc line explaining why:

```csharp
    /// <summary>Internal so the monitor can surface events it derives from the learner —
    /// <see cref="MonitorEvent.ConfigMismatch"/> — through the same log and stream as observed
    /// events, without a second event path for callers to subscribe to.</summary>
    internal void Raise(MonitorEvent evt)
```

- [ ] **Step 7: Add an integration test for the raised event**

Append to `tests/OpenEC.Monitor.Tests/LearningIntegrationTests.cs`. **Use unqualified type names with
`using` directives, never partial namespace qualifiers** — `OpenEC.Monitor.Tests` contains sibling
namespaces `Eni`, `Observation`, `Synthesis`, `Learning`, `Capture`, `Cli` and `Protocol` that shadow
the SDK's namespaces of the same names, so `Eni.EniConfiguration` resolves to the wrong thing. Task 3
added `using OpenEC.Monitor.Eni;` to this file already; add `using OpenEC.Monitor.Observation;` too:

```csharp
    [Fact]
    public async Task A_mismatched_eni_raises_config_mismatch_events()
    {
        // sample.eni.xml declares four slaves at 1001-1004; the bringup fixture has two at 1001-1002.
        var eni = EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Eni = eni });

        await monitor.RunAsync();

        var mismatches = monitor.Observer.SnapshotEvents()
            .OfType<MonitorEvent.ConfigMismatch>().ToList();
        Assert.NotEmpty(mismatches);
        Assert.Contains(mismatches, m => m.Kind == ConfigMismatchKind.SlaveMissing);
    }
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/OpenEC.Monitor/Observation/MonitorEvents.cs src/OpenEC.Monitor/Observation/BusObserver.cs src/OpenEC.Monitor/Learning/ConfigurationDiff.cs src/OpenEC.Monitor/EtherCatMonitor.cs tests/OpenEC.Monitor.Tests/Learning/ConfigurationDiffTests.cs tests/OpenEC.Monitor.Tests/LearningIntegrationTests.cs
git commit -m "feat(learning): cross-check a declared ENI against what the bus showed"
```

---

### Task 5: Two-pass offline decode

**Files:**
- Modify: `src/OpenEC.Monitor/Capture/ICaptureSource.cs`
- Modify: `src/OpenEC.Monitor/Capture/PcapFileSource.cs`
- Modify: `src/OpenEC.Monitor/EtherCatMonitor.cs`
- Test: `tests/OpenEC.Monitor.Tests/Capture/MultiplePassesTests.cs` (create)

**Interfaces:**
- Produces: `ICaptureSource.SupportsMultiplePasses` (default interface member, `false`), overridden `true` in `PcapFileSource`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Capture/MultiplePassesTests.cs`:

```csharp
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Capture;

public class MultiplePassesTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"passes-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void Only_file_sources_advertise_multiple_passes()
    {
        var path = BringupCapture.Write(Path.Combine(_directory, "b.pcap"), cycles: 3);

        Assert.True(new PcapFileSource(path).SupportsMultiplePasses);
        Assert.False(new LiveCaptureSource("nonexistent0").SupportsMultiplePasses);
        // Re-enumerating a recording decorator would re-record, so it must stay single-pass.
        Assert.False(new RecordingCaptureSource(new PcapFileSource(path),
            Path.Combine(_directory, "rec.pcap")).SupportsMultiplePasses);
    }

    /// <summary>The point of the discovery pass: on a file, the cyclic frames that arrive BEFORE
    /// learning converges are still decoded, because pass 2 starts with the finished configuration.
    /// A single-pass run over the same file maps strictly fewer of them.</summary>
    [Fact]
    public async Task Offline_two_pass_maps_process_data_from_the_first_frame()
    {
        var path = BringupCapture.Write(Path.Combine(_directory, "two-pass.pcap"), cycles: 20);

        await using var monitor = EtherCatMonitor.OpenFile(path);
        await monitor.RunAsync();

        Assert.Equal(16, monitor.ProcessImage.Current.Count);
        Assert.NotNull(monitor.Observer.Applied);
        // Pass 2 decodes every cyclic frame in the file under the final configuration.
        Assert.Equal(16, monitor.Learned!.Configuration.Variables.Count);
    }

    [Fact]
    public async Task Two_pass_does_not_double_count_frames()
    {
        var path = BringupCapture.Write(Path.Combine(_directory, "count.pcap"), cycles: 10);
        var expected = BringupCapture.Frames(cycles: 10).Count;

        await using var monitor = EtherCatMonitor.OpenFile(path);
        await monitor.RunAsync();

        Assert.Equal(expected, monitor.Statistics.TotalFrames);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~MultiplePassesTests"`
Expected: FAIL — `SupportsMultiplePasses` does not exist.

- [ ] **Step 3: Add the capability flag**

In `src/OpenEC.Monitor/Capture/ICaptureSource.cs`:

```csharp
public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default);

    /// <summary>True when <see cref="CaptureAsync"/> can be enumerated more than once and yields
    /// the same frames each time. Only then can learning run a cheap discovery pass before the
    /// decode pass, so that process data arriving before the configuration converged is still
    /// mapped. False for live interfaces, and false for the recording decorator — re-enumerating
    /// that would write the capture twice.</summary>
    bool SupportsMultiplePasses => false;
}
```

In `src/OpenEC.Monitor/Capture/PcapFileSource.cs`, add to the class:

```csharp
    /// <summary>Each call to <see cref="CaptureAsync"/> opens its own reader, so the file can be
    /// replayed as often as the caller likes.</summary>
    public bool SupportsMultiplePasses => true;
```

- [ ] **Step 4: Add the discovery pass**

In `src/OpenEC.Monitor/EtherCatMonitor.cs`, insert the pass before the main loop inside `RunAsync`'s `try`, immediately after `await EnrichNamesAsync();`:

```csharp
            // Discovery pass. Only the learner runs, so no process-image work happens against a
            // configuration that does not exist yet; pass 2 then decodes the whole file under the
            // finished configuration. Skipped when an ENI was supplied — that is already the
            // authority — and impossible on a live source, which cannot be replayed.
            var discovered = false;
            if (_learner is not null && _options.Eni is null && _source.SupportsMultiplePasses)
            {
                await foreach (var raw in _source.CaptureAsync(ct))
                    _learner.Observe(raw.Timestamp, EtherCatFrameParser.Parse(raw.Data));
                await _learner.ResolveSchemasAsync(ct);
                if (_learner.Current is { } learned) Observer.ApplyConfiguration(learned);
                discovered = true;
            }
```

Then in the main loop, skip the learner when the discovery pass already ran — otherwise every frame is observed twice and the statistics the learner derives are doubled:

```csharp
            await foreach (var raw in _source.CaptureAsync(ct))
            {
                var decoded = EtherCatFrameParser.Parse(raw.Data);
                Observer.Process(raw.Timestamp, decoded);
                if (!discovered) _learner?.Observe(raw.Timestamp, decoded);
            }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~MultiplePassesTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS. `AnalyzeCommandTests` runs over files and now takes the two-pass route, so a regression there means the pass changed observable output.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Monitor/Capture/ICaptureSource.cs src/OpenEC.Monitor/Capture/PcapFileSource.cs src/OpenEC.Monitor/EtherCatMonitor.cs tests/OpenEC.Monitor.Tests/Capture/MultiplePassesTests.cs
git commit -m "feat(capture): discovery pass maps offline process data from the first frame"
```

---

### Task 6: Learned-configuration cache

**Files:**
- Create: `src/OpenEC.Monitor/Learning/LearnedBusCache.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/LearnedBusCacheTests.cs` (create)

**Interfaces:**
- Produces: `LearnedBusCache(string directory)`, `.Fingerprint(EniConfiguration)`, `.FallbackFingerprint(EniConfiguration)`, `.Save(LearnedConfiguration)`, `.TryLoad(string fingerprint, out EniConfiguration?)`, and `LearnedBusCache.DefaultDirectory`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/LearnedBusCacheTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class LearnedBusCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}");

    private static LearnedConfiguration LearnBringup()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!;
    }

    private static EniConfiguration WithSlaves(params EniSlave[] slaves) => new()
    {
        Slaves = slaves,
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 2, 2, 0, null)],
        Variables = [],
    };

    private static EniSlave Slave(ushort address, uint product) =>
        new($"S{address}", address, (ushort)(0 - (address - 1001)), 2, product, 0x00120000, null, null);

    [Fact]
    public void A_saved_configuration_loads_back()
    {
        var cache = new LearnedBusCache(_directory);
        var learned = LearnBringup();

        cache.Save(learned);
        var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);

        Assert.True(cache.TryLoad(fingerprint, out var reloaded));
        Assert.Equal(2, reloaded!.Slaves.Count);
        Assert.Equal(16, reloaded.Variables.Count);
    }

    [Fact]
    public void A_miss_reports_false_and_yields_null()
    {
        var cache = new LearnedBusCache(_directory);

        Assert.False(cache.TryLoad("deadbeef", out var reloaded));
        Assert.Null(reloaded);
    }

    [Fact]
    public void Saving_writes_a_metadata_sidecar()
    {
        var cache = new LearnedBusCache(_directory);
        var learned = LearnBringup();

        cache.Save(learned);

        var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);
        Assert.True(File.Exists(Path.Combine(_directory, $"{fingerprint}.eni.xml")));
        Assert.True(File.Exists(Path.Combine(_directory, $"{fingerprint}.meta.json")));
    }

    /// <summary>The fingerprint deliberately excludes serial numbers, so swapping in an identical
    /// replacement terminal still hits the cache — which is the whole point of caching a bus.</summary>
    [Fact]
    public void The_fingerprint_ignores_names_and_depends_on_identity()
    {
        var a = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var renamed = WithSlaves(
            new EniSlave("renamed", 1001, 0x0000, 2, 0x03F03052, 0x00120000, null, null),
            Slave(1002, 0x03F03052));
        var different = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x07D83052));

        Assert.Equal(LearnedBusCache.Fingerprint(a), LearnedBusCache.Fingerprint(renamed));
        Assert.NotEqual(LearnedBusCache.Fingerprint(a), LearnedBusCache.Fingerprint(different));
    }

    [Fact]
    public void A_different_slave_count_changes_the_fingerprint()
    {
        var two = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var one = WithSlaves(Slave(1001, 0x03F03052));

        Assert.NotEqual(LearnedBusCache.Fingerprint(two), LearnedBusCache.Fingerprint(one));
    }

    /// <summary>On a mid-run attach the wire never revealed identity, so the primary fingerprint
    /// would key on zeroes for every bus. The fallback keys on what IS observable then: how many
    /// slaves answered, at which addresses, and the shape of the cyclic frame table.</summary>
    [Fact]
    public void The_fallback_fingerprint_does_not_depend_on_identity()
    {
        var known = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var anonymous = WithSlaves(
            new EniSlave("S1001", 1001, 0x0000, 0, 0, 0, null, null),
            new EniSlave("S1002", 1002, 0xFFFF, 0, 0, 0, null, null));

        Assert.Equal(LearnedBusCache.FallbackFingerprint(known),
            LearnedBusCache.FallbackFingerprint(anonymous));
        Assert.NotEqual(LearnedBusCache.Fingerprint(known), LearnedBusCache.Fingerprint(anonymous));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedBusCacheTests"`
Expected: FAIL — `LearnedBusCache` does not exist.

- [ ] **Step 3: Write the cache**

`src/OpenEC.Monitor/Learning/LearnedBusCache.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Learning;

/// <summary>Persists learned configurations so a bus whose startup was observed once is recognised
/// on every later mid-run attach. Entries are real ENI XML, which means the cache, the `--out`
/// export and the test fixtures are all the same artifact — one writer, one reader, one format.</summary>
public sealed class LearnedBusCache(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>`%APPDATA%` on Windows, `~/.config` on Linux and macOS — the cross-platform
    /// guarantee holds without a per-OS branch here.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openec", "learned");

    /// <summary>Slave count, then each slave's ring position and identity in ring order, then the
    /// logical address layout. Serial numbers are deliberately excluded so replacing a terminal
    /// with an identical one still hits the cache.</summary>
    public static string Fingerprint(EniConfiguration configuration) => Digest(
        $"v1|{configuration.Slaves.Count}|"
        + string.Join(';', configuration.Slaves
            .OrderBy(s => (ushort)(0 - s.AutoIncAddr)).ThenBy(s => s.PhysAddr)
            .Select(s => string.Create(CultureInfo.InvariantCulture,
                $"{s.VendorId:X}:{s.ProductCode:X}:{s.RevisionNo:X}")))
        + "|" + CyclicShape(configuration));

    /// <summary>Used when identity was never read from the wire — startup checking disabled, or a
    /// capture that began after INIT. Keys only on what is observable in OP: how many slaves
    /// answered, at which station addresses, and the shape of the cyclic frame table. Weaker, so a
    /// hit is not guaranteed and the completeness surface says so.</summary>
    public static string FallbackFingerprint(EniConfiguration configuration) => Digest(
        $"v1-fallback|{configuration.Slaves.Count}|"
        + string.Join(';', configuration.Slaves.Select(s => s.PhysAddr).OrderBy(a => a))
        + "|" + CyclicShape(configuration));

    public void Save(LearnedConfiguration learned)
    {
        Directory.CreateDirectory(directory);
        var fingerprint = Fingerprint(learned.Configuration);
        EniXmlWriter.Write(learned.Configuration, Path.Combine(directory, $"{fingerprint}.eni.xml"));
        File.WriteAllText(Path.Combine(directory, $"{fingerprint}.meta.json"),
            JsonSerializer.Serialize(new
            {
                learned.Revision,
                learned.Completeness.SawStartup,
                learned.Completeness.IsComplete,
                Summary = learned.Completeness.Summary,
                Slaves = learned.Completeness.Slaves,
                Provenance = learned.Provenance.ToDictionary(
                    kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
            }, JsonOptions));
    }

    public bool TryLoad(string fingerprint, out EniConfiguration? configuration)
    {
        configuration = null;
        var path = Path.Combine(directory, $"{fingerprint}.eni.xml");
        if (!File.Exists(path)) return false;
        try
        {
            configuration = EniConfiguration.Load(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            // A corrupt or half-written cache entry must never break a session; treat it as a miss.
            return false;
        }
    }

    private static string CyclicShape(EniConfiguration configuration) =>
        string.Join(';', configuration.CyclicCommands
            .OrderBy(c => c.RawAddress).ThenBy(c => (int)c.Command)
            .Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{(int)c.Command}:{c.RawAddress:X}:{c.DataLength}")));

    private static string Digest(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedBusCacheTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Wire the cache into the monitor**

A cache nothing consults is dead code. Spec §6 requires a cache hit to be applied at frame 1, so
the monitor must both read and write it.

In `src/OpenEC.Monitor/EtherCatMonitorOptions.cs`:

```csharp
    /// <summary>Where learned configurations are cached, so a bus whose startup was observed once is
    /// recognised on later mid-run attaches. Null disables caching — which is what tests want, and
    /// why this is not defaulted: a default would have every test write into the real user profile.
    /// Callers that want caching pass `new LearnedBusCache(LearnedBusCache.DefaultDirectory)`.</summary>
    public LearnedBusCache? LearnedCache { get; set; }
```

In `src/OpenEC.Monitor/EtherCatMonitor.cs`, add the field and extend the handler from Task 4:

```csharp
    private bool _cacheConsulted;
```

```csharp
        // Consult the cache once, and only while what this capture revealed is still incomplete:
        // a cached configuration is a head start for a mid-run attach, not an override for facts
        // observed since. Later revisions from the wire replace it as the picture firms up.
        if (!_cacheConsulted && !learned.Completeness.IsComplete && _options.LearnedCache is { } cache)
        {
            _cacheConsulted = true;
            var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);
            if (cache.TryLoad(fingerprint, out var cached)
                || cache.TryLoad(LearnedBusCache.FallbackFingerprint(learned.Configuration), out cached))
            {
                // Completeness deliberately still describes what THIS capture revealed, not the
                // cached file. The cache gives a usable configuration; it does not make the capture
                // more complete, and saying otherwise would be the dishonesty §4 exists to prevent.
                Observer.ApplyConfiguration(learned with { Configuration = cached! });
                Observer.Raise(new MonitorEvent.ConfigurationLearned(DateTimeOffset.UtcNow,
                    learned.Revision, $"cache hit — {learned.Completeness.Summary}"));
                return;
            }
        }

        Observer.ApplyConfiguration(learned);
        Observer.Raise(new MonitorEvent.ConfigurationLearned(
            DateTimeOffset.UtcNow, learned.Revision, learned.Completeness.Summary));
        // Only a complete configuration is worth caching; a partial one would poison later attaches.
        if (learned.Completeness.IsComplete) _options.LearnedCache?.Save(learned);
```

Insert the cache-consult block immediately after the `if (_options.Eni is { } declared)` early return,
and replace the two trailing lines the Task 4 handler ended with.

- [ ] **Step 6: Write the wiring tests**

Append to `tests/OpenEC.Monitor.Tests/Learning/LearnedBusCacheTests.cs`:

```csharp
    [Fact]
    public async Task A_complete_configuration_is_cached_after_a_session()
    {
        var pcap = BringupCapture.Write(Path.Combine(_directory, "run.pcap"), cycles: 5);
        var cache = new LearnedBusCache(_directory);

        await using var monitor = EtherCatMonitor.OpenFile(pcap,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        var fingerprint = LearnedBusCache.Fingerprint(monitor.Learned!.Configuration);
        Assert.True(cache.TryLoad(fingerprint, out _));
    }

    /// <summary>The mid-run attach the cache exists for: a capture that begins after startup reveals
    /// station addresses but no PDO mapping, so the cached configuration from an earlier session is
    /// what makes its variables readable at all.</summary>
    [Fact]
    public async Task A_mid_run_attach_applies_a_cached_configuration()
    {
        var cache = new LearnedBusCache(_directory);
        cache.Save(LearnBringup());

        // A mid-run attach: cyclic traffic plus the AL-status polls a master actually emits in OP.
        // The FPRD polls matter — LearnedBus's mid-run discovery needs an FPRD with a non-zero ADP,
        // so a purely cyclic capture would discover no slaves at all and never publish anything for
        // the cache to be consulted against. No station-address assignment, no SII, no CoE.
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var midRunFrames = new List<(DateTimeOffset, byte[])>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            midRunFrames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrd, (byte)cycle, 0x00010000, new byte[2], 0).Build()));
            midRunFrames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrd, (byte)cycle, 0x00010000,
                    [(byte)cycle, (byte)~cycle], 2).Build()));
            foreach (var station in new ushort[] { 1001, 1002 })
                midRunFrames.Add((t.AddMicroseconds(120), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, (byte)cycle, station, 0x0130,
                        [0x08, 0x00], 1).Build()));
            t = t.AddMilliseconds(1);
        }
        var midRun = Path.Combine(_directory, "midrun.pcap");
        PcapFileWriter.Write(midRun, midRunFrames);

        await using var monitor = EtherCatMonitor.OpenFile(midRun,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        // Nothing was learned from this capture beyond the cyclic table, so if the process image has
        // named variables they can only have come from the cache.
        Assert.False(monitor.Learned!.Completeness.SawStartup);
        Assert.NotNull(monitor.Observer.Applied);
    }

    [Fact]
    public async Task Caching_is_off_when_no_cache_is_supplied()
    {
        var pcap = BringupCapture.Write(Path.Combine(_directory, "nocache.pcap"), cycles: 5);

        await using var monitor = EtherCatMonitor.OpenFile(pcap);
        await monitor.RunAsync();

        Assert.False(Directory.Exists(_directory)
            && Directory.GetFiles(_directory, "*.eni.xml").Length > 0);
    }
```

Add `using OpenEC.Monitor;`, `using OpenEC.Monitor.Capture;` and `using OpenEC.Monitor.Synthesis;`
to the test file's usings (the last for `EtherCatFrameBuilder` and `PcapFileWriter`).

If `A_mid_run_attach_applies_a_cached_configuration` cannot reach a cache hit because the fallback
fingerprint of a cyclic-only capture differs from the saved bus's, **report that rather than
weakening the assertion** — it would mean the fallback fingerprint's inputs are wrong, which is a
real finding about §5's cache design and my ruling on it.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedBusCacheTests"`
Expected: PASS (9 tests).

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Monitor/Learning/LearnedBusCache.cs src/OpenEC.Monitor/EtherCatMonitorOptions.cs src/OpenEC.Monitor/EtherCatMonitor.cs tests/OpenEC.Monitor.Tests/Learning/LearnedBusCacheTests.cs
git commit -m "feat(learning): cache learned configurations and apply them on mid-run attach"
```

---

### Task 7: ADS identity tier

**Files:**
- Modify: `src/OpenEC.Monitor/Learning/BusLearner.cs`
- Modify: `src/OpenEC.Monitor.Ads/AdsBusSnapshot.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/AdsIdentityTests.cs` (create)

**Interfaces:**
- Produces: `BusLearner.ApplyAdsIdentity(IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)>)` and `AdsBusSnapshot.ScannedIdentities()` returning that same tuple list.

**Why a tuple and not the ADS type:** `OpenEC.Monitor` must not gain a dependency on `Dahlke.EtherCAT.Diagnostics` — the dependency runs `OpenEC.Monitor.Ads → OpenEC.Monitor`, never the reverse. The ADS module does the mapping.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/AdsIdentityTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class AdsIdentityTests
{
    /// <summary>A bus discovered mid-run: station addresses are visible from FPRD traffic, but the
    /// master never read SII and never queried 0x1018, so identity is unknown.</summary>
    private static BusLearner LearnerWithAnonymousSlave()
    {
        var learner = new BusLearner();
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, [0x08, 0x00], 1)
            .Build();
        learner.Observe(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(frame));
        return learner;
    }

    [Fact]
    public void Ads_identity_fills_a_slave_the_wire_never_identified()
    {
        var learner = LearnerWithAnonymousSlave();
        Assert.Equal(0u, learner.Current!.Configuration.Slaves.Single().VendorId);

        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

        var slave = learner.Current!.Configuration.Slaves.Single();
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
    }

    [Fact]
    public void Ads_identity_is_marked_in_provenance()
    {
        var learner = LearnerWithAnonymousSlave();

        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

        Assert.Equal(FactSource.Ads, learner.Current!.Provenance[1001].Identity);
    }

    /// <summary>The wire is the authority. ADS reports what the master BELIEVES; if the bus itself
    /// said something different, that difference is exactly what a diagnostic tool must preserve.</summary>
    [Fact]
    public void Ads_identity_does_not_override_identity_learned_from_the_wire()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 3))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        Assert.Equal(0x03F03052u, learner.Current!.Configuration.Slaves[0].ProductCode);

        learner.ApplyAdsIdentity([(1001, 999u, 0xDEADBEEFu, 1u)]);

        var slave = learner.Current!.Configuration.Slaves.Single(s => s.PhysAddr == 1001);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(FactSource.Sii, learner.Current!.Provenance[1001].Identity);
    }

    [Fact]
    public void An_ads_poll_for_an_unknown_address_is_ignored()
    {
        var learner = LearnerWithAnonymousSlave();

        learner.ApplyAdsIdentity([(1099, 2u, 0x03F03052u, 0x00120000u)]);

        Assert.Single(learner.Current!.Configuration.Slaves);
        Assert.Equal(0u, learner.Current!.Configuration.Slaves.Single().VendorId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~AdsIdentityTests"`
Expected: FAIL — `ApplyAdsIdentity` does not exist.

- [ ] **Step 3: Add the tier to the learner**

In `src/OpenEC.Monitor/Learning/LearnedSlave.cs`, add a flag so provenance can tell the tiers apart:

```csharp
    /// <summary>True when this slave's identity came from a master-side ADS poll rather than from
    /// the wire. Provenance reports it as <see cref="FactSource.Ads"/>.</summary>
    public bool IdentityFromAds { get; set; }
```

In `src/OpenEC.Monitor/Learning/BusLearner.cs`:

```csharp
    /// <summary>Folds master-side identity from an ADS poll into slaves whose identity the wire
    /// never revealed — the case where TwinCAT's startup checking is disabled, so it never reads
    /// SII and never queries 0x1018 (spec §6).
    ///
    /// Uses `??=`, so identity observed on the wire always wins: ADS reports what the master
    /// BELIEVES is out there, and where that disagrees with the bus, the disagreement is the
    /// finding — not something to overwrite. A tuple rather than the ADS type keeps
    /// OpenEC.Monitor free of a dependency on the diagnostics package.</summary>
    public void ApplyAdsIdentity(
        IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> scanned)
    {
        lock (_gate)
        {
            var known = _bus.Slaves.ToDictionary(s => s.StationAddress);
            foreach (var entry in scanned)
            {
                if (!known.TryGetValue(entry.Address, out var slave)) continue;
                if (slave.IdentityKnown) continue;
                slave.VendorId = entry.VendorId;
                slave.ProductCode = entry.ProductCode;
                slave.Revision = entry.Revision;
                slave.IdentityFromAds = true;
            }
            Republish(force: true);
        }
    }
```

In `BusLearner.Provenance`, put the ADS tier ahead of the inferred fallback:

```csharp
        var identity = slave.EepromWords.Count > 0 ? FactSource.Sii
            : slave.IdentityFromAds ? FactSource.Ads
            : slave.IdentityKnown ? FactSource.CoeIdentity
            : FactSource.Inferred;
```

- [ ] **Step 4: Add the ADS-side mapping**

In `src/OpenEC.Monitor.Ads/AdsBusSnapshot.cs`, add to the record:

```csharp
    /// <summary>The scanned identities in the shape <c>BusLearner.ApplyAdsIdentity</c> takes.
    /// Mapping here rather than in the learner keeps the dependency direction intact:
    /// OpenEC.Monitor.Ads knows about OpenEC.Monitor, never the reverse.</summary>
    public IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)>
        ScannedIdentities() =>
        ScannedSlaves
            .Select(s => (s.PhysicalAddress, s.VendorId, s.ProductCode, s.RevisionNumber))
            .ToList();
```

If the property names on `EtherCatScannedSlave` differ from `PhysicalAddress`/`VendorId`/`ProductCode`/`RevisionNumber`, or their types are not `ushort`/`uint`, **stop and report NEEDS_CONTEXT with the actual signature** rather than casting blindly — a wrong cast here silently mislabels every identity.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~AdsIdentityTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Learning/BusLearner.cs src/OpenEC.Monitor/Learning/LearnedSlave.cs src/OpenEC.Monitor.Ads/AdsBusSnapshot.cs tests/OpenEC.Monitor.Tests/Learning/AdsIdentityTests.cs
git commit -m "feat(learning): ADS identity tier for buses whose wire never revealed it"
```

---

### Task 8: CLI surfaces

**Files:**
- Modify: `src/OpenEC.CLI/Reporting/AnalysisReport.cs`
- Modify: `src/OpenEC.CLI/Commands/AnalyzeCommand.cs`
- Modify: `src/OpenEC.CLI/Commands/LiveCommand.cs`
- Test: `tests/OpenEC.Monitor.Tests/Cli/LearningReportTests.cs` (create)

**Interfaces:**
- Consumes: `EtherCatMonitor.Learned` (Task 3), `MonitorEvent.ConfigMismatch` (Task 4), `EniXmlWriter` (Plan 1).
- Produces: `LearningReport` record and `AnalysisReport.Learning`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Cli/LearningReportTests.cs`:

```csharp
using System.Text.Json;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Cli;

public class LearningReportTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lr-{Guid.NewGuid():N}")).FullName;

    private string Bringup()
    {
        var path = Path.Combine(_directory, "bringup.pcap");
        BringupCapture.Write(path, cycles: 5);
        return path;
    }

    [Fact]
    public void Analyze_json_carries_a_learning_block()
    {
        var result = new TestApp().Run("analyze", Bringup(), "--json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var learning = json.RootElement.GetProperty("learning");
        Assert.True(learning.GetProperty("sawStartup").GetBoolean());
        Assert.Equal(2, learning.GetProperty("slavesTotal").GetInt32());
        Assert.Equal(2, learning.GetProperty("slavesComplete").GetInt32());
    }

    [Fact]
    public void No_learn_omits_the_learning_block()
    {
        var result = new TestApp().Run("analyze", Bringup(), "--json", "--no-learn");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.TryGetProperty("learning", out _));
    }

    /// <summary>The CI gate the spec calls for: "the bus no longer matches the committed ENI".</summary>
    [Fact]
    public void A_mismatched_eni_surfaces_in_the_learning_block()
    {
        var eni = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        var result = new TestApp().Run("analyze", Bringup(), "--json", "--eni", eni);

        using var json = JsonDocument.Parse(result.Output);
        var mismatches = json.RootElement.GetProperty("learning").GetProperty("mismatches");
        Assert.True(mismatches.GetArrayLength() > 0);
    }

    [Fact]
    public void Live_learn_out_writes_a_loadable_eni()
    {
        // `live` needs an interface; the capture fails immediately without one, which is enough to
        // prove the flag is wired and that a failed session writes nothing.
        var output = Path.Combine(_directory, "bus.eni.xml");

        var result = new TestApp().Run("live", "--interface", "nonexistent0",
            "--duration", "1", "--learn-out", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningReportTests"`
Expected: FAIL — no `learning` property, and `--no-learn`/`--learn-out` are unknown options.

- [ ] **Step 3: Add the report block**

In `src/OpenEC.CLI/Reporting/AnalysisReport.cs`, add the record and a field on `AnalysisReport`:

```csharp
public sealed record LearningReport(
    bool SawStartup,
    int SlavesComplete,
    int SlavesTotal,
    string Summary,
    IReadOnlyList<string> Mismatches,
    IReadOnlyDictionary<string, string> Provenance);
```

Add `LearningReport? Learning` as the last positional parameter of `AnalysisReport`, and in `Build`:

```csharp
    public static AnalysisReport Build(string file, EtherCatMonitor monitor)
    {
        // … existing locals …
        var learning = monitor.Learned is { } learned
            ? new LearningReport(
                learned.Completeness.SawStartup,
                learned.Completeness.Slaves.Count(s => s.IsComplete),
                learned.Completeness.Slaves.Count,
                learned.Completeness.Summary,
                log.OfType<MonitorEvent.ConfigMismatch>()
                    .Select(m => $"{m.Kind}: declared {m.Declared}, observed {m.Observed}")
                    .ToList(),
                learned.Provenance.ToDictionary(
                    kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                    kv => $"identity={kv.Value.Identity}, names={kv.Value.Names}, mapping={kv.Value.Mapping}"))
            : null;
        return new AnalysisReport(/* … existing arguments … */, learning);
    }
```

Add `using System.Globalization;` and `using OpenEC.Monitor.Learning;` to the file. `System.Text.Json` omits `null` for a nullable record property only if configured to — so also set `DefaultIgnoreCondition` in `AnalyzeCommand`'s `JsonOptions`:

```csharp
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
```

- [ ] **Step 4: Add `--no-learn` to analyze**

In `src/OpenEC.CLI/Commands/AnalyzeCommand.cs`'s `Settings`:

```csharp
        [CommandOption("--no-learn")]
        [Description("Disable passive configuration learning (on by default)")]
        public bool NoLearn { get; init; }
```

and in the options passed to `OpenFile`:

```csharp
                Learning = settings.NoLearn ? LearningMode.Off : LearningMode.Auto,
```

Also add the learned coverage to the human-readable `Render`, right after the overview table:

```csharp
        if (report.Learning is { } learning)
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]Learning:[/] {learning.Summary}");
            foreach (var mismatch in learning.Mismatches.Take(20))
                AnsiConsole.MarkupLineInterpolated($"  [yellow]mismatch[/] {mismatch}");
            if (learning.Mismatches.Count > 20)
                AnsiConsole.WriteLine($"  ... {learning.Mismatches.Count - 20} more");
        }
```

- [ ] **Step 5: Add `--learn-out` to live**

In `src/OpenEC.CLI/Commands/LiveCommand.cs`'s `Settings`:

```csharp
        [CommandOption("--learn-out")]
        [Description("Write the learned bus configuration to this ENI XML path when the session ends")]
        public string? LearnOut { get; init; }
```

After the session ends and before the summary is printed — inside the same success path that builds `AnalysisReport` — add:

```csharp
                if (settings.LearnOut is { } learnOut && monitor.Learned is { } learned)
                {
                    EniXmlWriter.Write(learned.Configuration, learnOut);
                    AnsiConsole.MarkupLineInterpolated($"Wrote learned ENI → [green]{learnOut}[/]");
                }
```

Add `using OpenEC.Monitor.Learning;` to the file. Placing the write on the success path means a failed capture writes nothing, which is what the test asserts.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningReportTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. `AnalyzeCommandTests` asserts on existing JSON — adding a property must not break it.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.CLI/Reporting/AnalysisReport.cs src/OpenEC.CLI/Commands/AnalyzeCommand.cs src/OpenEC.CLI/Commands/LiveCommand.cs tests/OpenEC.Monitor.Tests/Cli/LearningReportTests.cs
git commit -m "feat(cli): learning coverage in analyze, --no-learn, live --learn-out"
```

---

### Task 9: Inspector — completeness strip and Save learned ENI

**Files:**
- Modify: `src/OpenEC.Inspector/ViewModels/DeviceEditorViewModel.cs`
- Modify: `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`
- Modify: `src/OpenEC.Inspector/Views/DeviceEditorView.axaml`
- Modify: `src/OpenEC.Inspector/Views/MainWindow.axaml`
- Test: `tests/OpenEC.Inspector.Tests/ViewModels/LearningSurfaceTests.cs` (create)

**Do not touch `ExplorerViewModel.cs` or `ShellSmokeTests.cs`** — both carry uncommitted work belonging to the repository owner. The completeness strip belongs on the device editor anyway, which is where a per-slave fact should surface.

**Interfaces:**
- Consumes: `BusObserver.Applied` (Task 2), `SlaveCompleteness` (Plan 1), `IFilePicker.PickSaveFileAsync` (existing), `EniXmlWriter.Write` (Plan 1).
- Produces: `DeviceEditorViewModel.Completeness` (string) and `MainWindowViewModel.SaveLearnedEniCommand`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Inspector.Tests/ViewModels/LearningSurfaceTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class LearningSurfaceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ls-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Device_editor_reports_learned_completeness_for_its_slave()
    {
        await using var session = await TestSessions.BringupAsync();
        var editor = new DeviceEditorViewModel(session, 1001,
            VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []));

        editor.Refresh();

        Assert.Contains("learned", editor.Completeness, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_editor_says_nothing_when_no_configuration_was_learned()
    {
        await using var session = await TestSessions.EmptyAsync();
        var editor = new DeviceEditorViewModel(session, 1001,
            VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []));

        editor.Refresh();

        Assert.Equal("", editor.Completeness);
    }

    /// <summary>Spec §7's headline Inspector claim: the Variables tab works with no ENI at all.
    /// The session below loads no ENI, so any variable in the watch came from learning.</summary>
    [Fact]
    public async Task Variables_populate_with_no_eni_loaded()
    {
        await using var session = await TestSessions.BringupAsync();
        Assert.Null(session.Eni);

        var watch = VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []);
        watch.Refresh();

        Assert.NotEmpty(session.Observer.ProcessImage.Current);
    }

    [Fact]
    public async Task Saving_the_learned_eni_writes_a_loadable_file()
    {
        var output = Path.Combine(_directory, "bus.eni.xml");
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker(saveResult: output));

        await vm.SaveLearnedEniCommand.ExecuteAsync(null);

        Assert.Equal(2, EniConfiguration.Load(output).Slaves.Count);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker(saveResult: null));

        await vm.SaveLearnedEniCommand.ExecuteAsync(null);

        // A cancelled dialog is a silent no-op, not an error surfaced to the user. Asserting on an
        // empty temp directory would pass trivially — nothing in this test writes there either way.
        Assert.Null(vm.FaultMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
```

Add these three helpers to `tests/OpenEC.Inspector.Tests/TestSessions.cs`. The shell helper mirrors
`MainWindowViewModelTests.CreateWithDemoSessionAsync`, which is the established way this suite starts
a session:

```csharp
    public static string WriteBringupPcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-bringup-{Guid.NewGuid():N}.pcap");
        return BringupCapture.Write(path, cycles: 5);
    }

    /// <summary>A completed session over a synthetic INIT→OP bringup, so the learner has published a
    /// full configuration and the observer has had it applied.</summary>
    public static async Task<MonitorSession> BringupAsync()
    {
        var session = new MonitorSession(new SourceSpec.File(WriteBringupPcap()));
        session.Start();
        await session.Completion;
        return session;
    }

    /// <summary>A completed session over a capture with no EtherCAT frames at all, so the learner
    /// never publishes and `Observer.Applied` stays null.</summary>
    public static async Task<MonitorSession> EmptyAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-empty-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, []);
        var session = new MonitorSession(new SourceSpec.File(path));
        session.Start();
        await session.Completion;
        return session;
    }

    /// <summary>A MainWindowViewModel with a completed bringup session, for exercising the
    /// session-level commands. Marshals inline so command execution is synchronous in tests.</summary>
    public static async Task<MainWindowViewModel> ShellWithBringupAsync(IFilePicker picker)
    {
        var vm = new MainWindowViewModel(
            () => [],
            (spec, eni) => new MonitorSession(spec, eni),
            picker,
            marshal: action => action());
        vm.Start.PcapPath = WriteBringupPcap();
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        return vm;
    }
```

`TestSessions.cs` needs `using OpenEC.Inspector.ViewModels;` added for `MainWindowViewModel` and
`IFilePicker`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~LearningSurfaceTests"`
Expected: FAIL — `Completeness` and `SaveLearnedEniCommand` do not exist.

- [ ] **Step 3: Add the completeness strip**

In `src/OpenEC.Inspector/ViewModels/DeviceEditorViewModel.cs`, add the property and populate it in `Refresh`:

```csharp
    [ObservableProperty] private string _completeness = "";
```

```csharp
        // Empty string rather than a placeholder: with no learned configuration there is nothing
        // honest to say, and the view collapses the strip when it is empty.
        Completeness = _session.Observer.Applied?.Completeness.Slaves
            .FirstOrDefault(s => s.StationAddress == Address) is { } slaveCompleteness
            ? Describe(slaveCompleteness)
            : "";
```

and the formatter:

```csharp
    /// <summary>States what is known and what a master restart would recover, rather than
    /// presenting a partial configuration as a complete one.</summary>
    private static string Describe(SlaveCompleteness c)
    {
        if (c.IsComplete) return "Fully learned from observed traffic.";
        var missing = new List<string>();
        if (!c.IdentityKnown) missing.Add("identity");
        if (!c.SyncManagersKnown) missing.Add("sync managers");
        if (!c.FmmusKnown) missing.Add("FMMUs");
        if (!c.PdoMappingKnown) missing.Add("PDO mapping");
        if (!c.ProcessDataPlaceable) missing.Add("process-data placement");
        return $"Partially learned — missing {string.Join(", ", missing)}. "
             + "Restarting the master with the capture running would recover it.";
    }
```

Add `using OpenEC.Monitor.Learning;` to the file.

- [ ] **Step 4: Add the save command**

In `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`:

```csharp
    [RelayCommand]
    private async Task SaveLearnedEniAsync()
    {
        if (Session?.Observer.Applied is not { } learned) return;
        var path = await _filePicker.PickSaveFileAsync("Save learned ENI", "bus.eni.xml", "xml");
        if (path is null) return;
        try
        {
            EniXmlWriter.Write(learned.Configuration, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
        {
            FaultMessage = $"Learned ENI could not be saved: {ex.Message}";
        }
    }
```

Add `using OpenEC.Monitor.Learning;`.

- [ ] **Step 5: Bind both in the views**

In `src/OpenEC.Inspector/Views/DeviceEditorView.axaml`, add a strip below the state badge row, styled with the existing house-theme resources (match the surrounding elements' `Classes`/brush keys — read the file and follow what is there):

Insert it as the last child of the first `Border Classes="panel"`'s `StackPanel`, directly after the
`Physical address` line, so it reads as part of the identity block:

```xml
              <TextBlock Classes="label" Text="{Binding Completeness}" TextWrapping="Wrap"
                         IsVisible="{Binding Completeness,
                                     Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
```

`Classes="label"` is the house-theme muted style already used by the two lines above it, and
`StringConverters` lives in the default Avalonia namespace, so no new xmlns is needed.

In `src/OpenEC.Inspector/Views/MainWindow.axaml`, add a button to the chrome top bar beside the existing "Stop session" button:

The chrome bar's `DockPanel` already docks "Stop session" to the right. Add the save button
immediately before it, so the two sit together in declaration order:

```xml
        <Button DockPanel.Dock="Right" Content="Save learned ENI…" Margin="0,0,8,0"
                Command="{Binding SaveLearnedEniCommand}" />
```

The enclosing `Border` already carries `IsVisible="{Binding HasSession}"`, so the button needs no
visibility binding of its own.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~LearningSurfaceTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. Note the Inspector suite includes `ShellSmokeTests`, which you must not modify — if it fails, the view changes broke a binding and the fix belongs in the view, not the test.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels/DeviceEditorViewModel.cs src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs src/OpenEC.Inspector/Views/DeviceEditorView.axaml src/OpenEC.Inspector/Views/MainWindow.axaml tests/OpenEC.Inspector.Tests/TestSessions.cs tests/OpenEC.Inspector.Tests/ViewModels/LearningSurfaceTests.cs
git commit -m "feat(inspector): completeness strip and Save learned ENI command"
```

---

### Task 10: Inspector — config mismatches in the messages panel

> **Amended during execution (Ruling 9).** The `EventFormatter` cases and the
> `EventFormatterTests` class were pulled forward into Task 4's fix round, because Task 4's new
> events fell into the untoggled "Other" category and turned the suite red — and this task as
> originally written would not have fixed that, since teaching the formatter alone still leaves
> `Config`/`Learning` absent from `EventsViewModel.CategoryNames`. Task 4 therefore also added
> those toggles plus `Other`. **What remains here is the README update in Step 6.** Verify the
> formatter cases and `EventFormatterTests` are present from Task 4 rather than re-adding them.

**Files:**
- Modify: `README.md`
- Already done in Task 4: `src/OpenEC.Inspector/ViewModels/EventFormatter.cs`,
  `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`,
  `tests/OpenEC.Inspector.Tests/ViewModels/EventFormatterTests.cs`

**Interfaces:**
- Consumes: `MonitorEvent.ConfigMismatch`, `ConfigMismatchKind` (Task 4).

The docked messages panel renders whatever `EventsViewModel` produces through `EventFormatter`, so teaching the formatter about the new event is the whole change — no new UI surface, exactly as spec §7 says.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Inspector.Tests/ViewModels/EventFormatterTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class EventFormatterTests
{
    private static MonitorEvent.ConfigMismatch Mismatch(ConfigMismatchKind kind) =>
        new(DateTimeOffset.UnixEpoch, kind, 1001, "Term 1 (EL1008)", "Term 1 (EL2008)");

    [Fact]
    public void Config_mismatches_get_their_own_category()
    {
        Assert.Equal("Config", EventFormatter.Category(Mismatch(ConfigMismatchKind.Identity)));
    }

    [Fact]
    public void An_identity_mismatch_names_both_sides_and_the_slave()
    {
        var text = EventFormatter.Describe(Mismatch(ConfigMismatchKind.Identity));

        Assert.Contains("1001", text);
        Assert.Contains("Term 1 (EL1008)", text);
        Assert.Contains("Term 1 (EL2008)", text);
    }

    [Fact]
    public void A_learned_configuration_reports_its_revision_and_summary()
    {
        var learned = new MonitorEvent.ConfigurationLearned(
            DateTimeOffset.UnixEpoch, 7, "learned 2/2 slaves");

        Assert.Equal("Learning", EventFormatter.Category(learned));
        var text = EventFormatter.Describe(learned);
        Assert.Contains("7", text);
        Assert.Contains("learned 2/2 slaves", text);
    }

    /// <summary>Process-image mismatches carry no address, so the description must not claim one.</summary>
    [Fact]
    public void A_process_image_mismatch_without_an_address_reads_cleanly()
    {
        var mismatch = new MonitorEvent.ConfigMismatch(DateTimeOffset.UnixEpoch,
            ConfigMismatchKind.ProcessImage, null, "x @bit 0", "@bit 8");

        var text = EventFormatter.Describe(mismatch);

        Assert.DoesNotContain("Slave ,", text);
        Assert.DoesNotContain("Slave :", text);
        Assert.Contains("x @bit 0", text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~EventFormatterTests"`
Expected: FAIL — `Category` returns "Other" and `Describe` falls through to `ToString()`.

- [ ] **Step 3: Teach the formatter**

In `src/OpenEC.Inspector/ViewModels/EventFormatter.cs`, add a case to each switch. Place them before the `_ =>` fallback:

```csharp
        MonitorEvent.ConfigMismatch => "Config",
        MonitorEvent.ConfigurationLearned => "Learning",
```

```csharp
        MonitorEvent.ConfigMismatch c =>
            c.Address is { } address
                ? $"Slave {address}: {c.Kind} — ENI says {c.Declared}, bus shows {c.Observed}"
                : $"{c.Kind} — ENI says {c.Declared}, bus shows {c.Observed}",
        MonitorEvent.ConfigurationLearned l =>
            $"Configuration revision {l.Revision}: {l.Summary}",
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~EventFormatterTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus the ~40 added by this plan.

- [ ] **Step 6: Update the README**

In `README.md`, replace the Status section's Milestone 3 bullet and the `**Next**` line with:

```markdown
- **Milestone 3** (this milestone): learning mode — ENI-independent bus discovery, integrated.
  Identity, topology order, PDO mapping and the cyclic command table are reconstructed from
  observed startup traffic; offline captures get a discovery pass so every process variable is
  mapped from the first frame; live sessions rebind progressively as the picture firms up. With an
  ENI loaded the learner cross-checks it and reports where the bus disagrees. Learned
  configurations export as real ENI XML (`openec learn`) and cache by bus fingerprint.
- **Next**: pcap replay with pacing control, frame-level packet browser, DC and port-topology
  diagnostics, and standalone app packaging (Windows MSI, macOS app bundle, Linux Flatpak).
```

And add to the CLI examples block:

```bash
# Cross-check a committed ENI against what the bus actually shows (exit 1 on mismatch)
dotnet run --project src/OpenEC.CLI -- analyze bringup.pcap --eni bus.eni.xml --json
```

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels/EventFormatter.cs tests/OpenEC.Inspector.Tests/ViewModels/EventFormatterTests.cs README.md
git commit -m "feat(inspector): surface config mismatches in the messages panel"
```

---

## Verification

After Task 10, learning mode is integrated end to end:

```bash
# Offline: two-pass decode, every variable mapped, no ENI needed
dotnet run --project src/OpenEC.CLI -- gen-sample /tmp/bringup.pcap --bringup
dotnet run --project src/OpenEC.CLI -- analyze /tmp/bringup.pcap --json | jq .learning

# Cross-check a stale ENI against the bus
dotnet run --project src/OpenEC.CLI -- learn /tmp/bringup.pcap --out /tmp/bus.eni.xml
dotnet run --project src/OpenEC.CLI -- analyze /tmp/bringup.pcap --eni /tmp/bus.eni.xml --json | jq .learning.mismatches

# Inspector: open /tmp/bringup.pcap with no ENI — the Variables tab populates,
# the device editor shows the completeness strip, "Save learned ENI…" writes the config
dotnet run --project src/OpenEC.Inspector
```

The second command should report zero mismatches against its own learned ENI — a learned configuration must agree with itself. Feeding `tests/OpenEC.Monitor.Tests/Fixtures/sample.eni.xml` instead should report several.

**Hardware acceptance (still open, carried from Plan 1):** a real TwinCAT bringup captured through the ETAP-1000, run through `analyze --eni` against the machine's actual ENI. That is the only test that proves the cross-check's exclusion list is right on a real configuration — a false mismatch on `WcState` or an InfoData variable would show up immediately.

## Carried forward from Plan 1's review

These were accepted with rulings during Plan 1 and belong to this milestone or later:

- **`LearnedSlave`'s mutable dictionaries.** `SyncManagers`, `Fmmus` and `EepromWords` are exposed as raw `Dictionary`, so a caller can mutate learner state around `LearnedBus`. `LearnedSlave` is public SDK surface, so narrowing to `IReadOnlyDictionary` plus `internal Record*` methods is a breaking change that gets more expensive after release. Task 7 already touches this file — do it there if it is cheap, otherwise it is the first thing in the next milestone.
- **`BringupCapture` holds too much constant.** `LogicalStartBit` is 0 in every test in the repo, no slave has two FMMUs, no SyncManager carries two PDOs, and no mixed `LRW`/`LRD`+`LWR` bus exists — the last of which spec §8 explicitly requires. Parameterising the generator (`outputs`, `mixedLrw`, `nonZeroStartBit`) would let the existing test classes exercise several shapes rather than one.
- **`EniSynthesizer`'s tier-3 PDO fallback is untested** because the only ESI fixture declares `Sm="3"`. Testing it needs a fixture with no `Sm` attribute.
- **Negative cyclic offsets.** `(int)(start - inputOrigin)` can underflow on a bus where a direction's sub-range sits below its origin. Self-consistent today, but not valid ENI for an external consumer. The mixed-`LRW` fixture above is what would surface it.
- **A second `DirectionTracker`.** The learner and the observer each keep an independent copy of an order-sensitive heuristic; they agree only while both see identical frame sequences. Spec §5's "one parse, two consumers" suggests hoisting classification above both.
