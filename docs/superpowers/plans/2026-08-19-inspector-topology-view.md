# Topology View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw the EtherCAT bus as a branched, port-level network map in a new Topology View tab of the Inspector's explorer pane, fed by DL-status and error-counter facts learned passively from the wire.

**Architecture:** Two independently shippable stages. Stage 1 adds the fact layer to `OpenEC.Monitor`: two pure register decoders, per-port facts on `LearnedSlave`, a pure `TopologyReconstructor` that turns ring order plus active-port sets into a tree, ENI `<PreviousPort>` on both the read and write paths, and a `TopologyTracker` that lives beside the existing observer trackers and emits topology-change events. Stage 2 adds the view to `OpenEC.Inspector`: a pure layout engine producing geometry, a `TopologyViewModel` sharing the explorer's existing `SelectedNode`, and a tabbed, resizable explorer pane.

**Tech Stack:** .NET 8, C#, Avalonia 11.3.2 (Fluent + house theme), CommunityToolkit.Mvvm, xUnit, Avalonia.Headless.XUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-inspector-topology-view-design.md`

## Global Constraints

- **.NET 8**, C# with nullable reference types enabled (`Directory.Build.props`). Avalonia pinned at **11.3.2** across all Avalonia packages.
- **No new NuGet dependencies.** The dependency set stays Avalonia, CommunityToolkit.Mvvm, Dahlke.EtherCAT.Esi.
- **100% passive.** No frame injection, no writes to the bus, no reads we initiate. Every fact is observed on the wire or read from an ENI file.
- **Never fabricate an unobserved fact.** An absent value is `null` and renders as unknown — never a defaulted stand-in, never a displayed `0`.
- **The wire wins over the ENI.** A disagreement is a reported finding, not an error.
- **Register offsets are per ETG.1000.4**, cited in decoder XML docs as the existing decoders do.
- **Decoders are pure**, keyed on ADO plus `FrameDirection`; reads decode only on `FrameDirection.Returning` and only with `WorkingCounter != 0`.
- **Port sides are fixed:** port 0 left, port 1 right, ports 2 and 3 beneath.
- **ESC internal forwarding order is `0 → 3 → 1 → 2`** — spec §10 marks this unverified; it lives in exactly one constant.
- **The existing 387 tests must stay green.** `dotnet test` after every task.

## Refinements to the spec

Two places where this plan deliberately refines the spec. Both are recorded in the spec itself so the documents agree.

1. **Spec §4 `PortState`** described one type carrying link/loop/signal *and* counters. This plan splits it into `PortState` (DL status) and `PortCounters` (error registers), because the two arrive from different registers at different times and a combined record would have to be rebuilt on every counter update.
2. **Spec §8 `BringupCapture`** asked for a branched bus from the extended `BringupCapture`. `BringupCapture` is a load-bearing fixture whose two-slave shape is asserted by existing tests (`BringupCaptureTests` asserts `bus.Slaves.Count == 2`). This plan instead adds DL-status reads to `BringupCapture` (keeping its two-slave *line*) and adds a separate `BranchedBusCapture` for the branched end-to-end fixture.

---

## File Structure

**Stage 1 — `src/OpenEC.Monitor/`**

| File | Responsibility |
| --- | --- |
| `Topology/PortState.cs` | One port's DL-status-derived link/loop/signal booleans and its rendered `PortLinkState`. |
| `Topology/PortCounters.cs` | One port's four error counters, all nullable. |
| `Topology/BusTopology.cs` | `TopologyNode`, `BusTopology`, `TopologyConflict`, `TopologyEdgeSource`, `TopologyDevice`. |
| `Topology/TopologyReconstructor.cs` | Pure: ring-ordered devices + optional ENI edges → `BusTopology`. |
| `Topology/TopologyTracker.cs` | Stateful observer sibling of `SlaveStateTracker`: accumulates port facts, emits topology-change events, exposes the current `BusTopology`. |
| `Learning/RegisterDecoders.cs` (modify) | `TryDlStatus`, `TryPortCounters` and their register constants. |
| `Learning/LearnedFacts.cs` (modify) | `DlStatusFact`, `PortCountersFact`. |
| `Learning/LearnedSlave.cs` (modify) | `Ports`, `Counters`, `ProcessingUnitErrors`, `PdiErrors`. |
| `Learning/LearnedBus.cs` (modify) | Route the two new facts onto their slave. |
| `Eni/EniModels.cs` (modify) | `EniPreviousPort` record; `EniSlave.PreviousPort`. |
| `Eni/EniConfiguration.cs` (modify) | Parse `<PreviousPort>`. |
| `Learning/EniSynthesizer.cs` (modify) | Carry learned topology onto `EniSlave.PreviousPort`. |
| `Learning/EniXmlWriter.cs` (modify) | Emit `<PreviousPort>`. |
| `Observation/MonitorEvents.cs` (modify) | `MonitorEvent.TopologyChanged`; `ConfigMismatchKind.Topology`. |
| `Observation/BusObserver.cs` (modify) | Own a `TopologyTracker`, expose `SnapshotTopology()`. |
| `Synthesis/BringupCapture.cs` (modify) | DL-status + counter reads for its two-slave line. |
| `Synthesis/BranchedBusCapture.cs` | A four-slave branched bringup fixture. |

**Stage 2 — `src/OpenEC.Inspector/`**

| File | Responsibility |
| --- | --- |
| `Topology/TopologyLayout.cs` | `TopologyBox`, `TopologyWire`, `TopologyPortMark`, `TopologyLayout`, the enums, the metric constants. |
| `Topology/TopologyLayoutEngine.cs` | Pure: `BusTopology` → `TopologyLayout`. |
| `ViewModels/TopologyViewModel.cs` | `TopologyBoxViewModel`, `TopologyWireViewModel`, `TopologyViewModel`. |
| `Views/TopologyView.axaml` (+ `.cs`) | `ItemsControl` over a `Canvas`, inside a `ScrollViewer`. |
| `Views/ExplorerView.axaml` (modify) | `TabControl` with bottom tab strip: Classic View, Topology View. |
| `ViewModels/ExplorerViewModel.cs` (modify) | Own the `TopologyViewModel`; keep sole ownership of `SelectedNode`. |
| `Views/MainWindow.axaml` (modify) | Resizable explorer column. |
| `ViewModels/MainWindowViewModel.cs` (modify) | Remembered pane width per explorer tab. |
| `ViewModels/EventFormatter.cs` (modify) | Category + description for `TopologyChanged`. |
| `ViewModels/EventsViewModel.cs` (modify) | Add `"Topology"` to `CategoryNames`. |

---

# Stage 1 — The fact layer

## Task 1: DL status decoder and port state

**Files:**
- Create: `src/OpenEC.Monitor/Topology/PortState.cs`
- Modify: `src/OpenEC.Monitor/Learning/LearnedFacts.cs` (append `DlStatusFact`)
- Modify: `src/OpenEC.Monitor/Learning/RegisterDecoders.cs` (append constant + `TryDlStatus`)
- Test: `tests/OpenEC.Monitor.Tests/Topology/DlStatusDecoderTests.cs`

**Interfaces:**
- Consumes: `SlaveRef`, `FrameDirection`, `EtherCatDatagram`, `RegisterDecoders.IsRead` (existing).
- Produces: `enum PortLinkState { Unused, Active, Blocked, Dangling }`; `sealed record PortState(byte Port, bool HasLink, bool LoopClosed, bool SignalDetected)` with `PortLinkState State` and `bool IsActive`; `sealed record DlStatusFact(SlaveRef Slave, ushort Raw)` with `IReadOnlyDictionary<byte, PortState> Ports`; `RegisterDecoders.TryDlStatus(EtherCatDatagram, FrameDirection) → DlStatusFact?`; `const ushort RegisterDecoders.DlStatusRegister = 0x0110`.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/DlStatusDecoderTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class DlStatusDecoderTests
{
    private static EtherCatDatagram Read(ushort adp, ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    /// <summary>A mid-line terminal: link on ports 0 and 1, both loops open, both partners
    /// powered. Bits 4,5 (link 0,1) + bits 9,11 (signal 0,1) = 0x0A30.</summary>
    [Fact]
    public void Mid_line_terminal_reports_two_active_ports()
    {
        var d = Read(1001, 0x0110, [0x30, 0x0A]);

        var fact = RegisterDecoders.TryDlStatus(d, FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal(PortLinkState.Active, fact!.Ports[0].State);
        Assert.Equal(PortLinkState.Active, fact.Ports[1].State);
        Assert.Equal(PortLinkState.Unused, fact.Ports[2].State);
        Assert.Equal(PortLinkState.Unused, fact.Ports[3].State);
    }

    /// <summary>Link present on port 1 but its loop is closed: the cable is in and frames are
    /// not passing. Bit 5 (link 1) + bit 10 (loop closed 1) = 0x0420.</summary>
    [Fact]
    public void Link_with_a_closed_loop_is_blocked()
    {
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x20, 0x04]), FrameDirection.Returning);

        Assert.Equal(PortLinkState.Blocked, fact!.Ports[1].State);
        Assert.False(fact.Ports[1].IsActive);
    }

    /// <summary>Loop open with no link: frames leave into nothing. Bit 8 clear means port 0's
    /// loop is open; no link bit is set. Raw 0x0000 gives every port an open loop.</summary>
    [Fact]
    public void Open_loop_without_a_link_is_dangling()
    {
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x00, 0x00]), FrameDirection.Returning);

        Assert.Equal(PortLinkState.Dangling, fact!.Ports[0].State);
    }

    [Fact]
    public void Each_port_reads_its_own_bit_triple()
    {
        // Every link bit set (0x00F0), every loop closed (0xAA00), no signal.
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0xF0, 0xAA]), FrameDirection.Returning);

        for (byte port = 0; port < 4; port++)
        {
            Assert.True(fact!.Ports[port].HasLink);
            Assert.True(fact.Ports[port].LoopClosed);
            Assert.False(fact.Ports[port].SignalDetected);
        }
    }

    [Fact]
    public void Signal_detected_is_recorded_per_port_without_changing_state()
    {
        // Link + open loop + signal on port 2: bit 6 (0x0040) + bit 13 (0x2000).
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x40, 0x20]), FrameDirection.Returning);

        Assert.True(fact!.Ports[2].SignalDetected);
        Assert.Equal(PortLinkState.Active, fact.Ports[2].State);
    }

    [Fact]
    public void Outbound_reads_and_other_registers_and_zero_wkc_are_ignored()
    {
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30, 0x0A]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0120, [0x30, 0x0A]), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30, 0x0A], wkc: 0), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30]), FrameDirection.Returning));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~DlStatusDecoderTests"`
Expected: FAIL — build error, `PortLinkState`/`TryDlStatus` do not exist.

- [ ] **Step 3: Write the port state type**

Create `src/OpenEC.Monitor/Topology/PortState.cs`:

```csharp
namespace OpenEC.Monitor.Topology;

/// <summary>What one ESC port is doing, derived from its DL status bit triple. The two mixed
/// states are the diagnostically interesting ones: an ESC auto-closes a port with no link, so
/// a link disagreeing with its loop bit is a fault worth surfacing.</summary>
public enum PortLinkState
{
    /// <summary>No link and the loop is closed — nothing plugged in.</summary>
    Unused,

    /// <summary>Link up and the loop is open — frames pass.</summary>
    Active,

    /// <summary>Link up but the loop is closed — cable present, frames not passing.</summary>
    Blocked,

    /// <summary>Loop open with no link — frames leave into nothing.</summary>
    Dangling,
}

/// <summary>One port as DL status (0x0110) describes it. <paramref name="SignalDetected"/> is
/// recorded but does not affect <see cref="State"/>: it distinguishes a powered partner from an
/// unpowered one, which belongs in a tooltip rather than in the port's rendered state.</summary>
public sealed record PortState(byte Port, bool HasLink, bool LoopClosed, bool SignalDetected)
{
    public PortLinkState State => (HasLink, LoopClosed) switch
    {
        (true, false) => PortLinkState.Active,
        (true, true) => PortLinkState.Blocked,
        (false, false) => PortLinkState.Dangling,
        (false, true) => PortLinkState.Unused,
    };

    /// <summary>True when frames actually traverse this port, which is the only condition under
    /// which it can carry a topology edge.</summary>
    public bool IsActive => State == PortLinkState.Active;
}
```

- [ ] **Step 4: Write the fact and the decoder**

Append to `src/OpenEC.Monitor/Learning/LearnedFacts.cs`:

```csharp
/// <summary>DL status (register 0x0110) as returned by a slave. One 16-bit word describes all
/// four ports, so the fact exposes them decoded rather than making callers re-shift the raw
/// value. Per ETG.1000.4: bits 4-7 physical link per port 0-3, bits 8/10/12/14 loop closed,
/// bits 9/11/13/15 signal detected.</summary>
public sealed record DlStatusFact(SlaveRef Slave, ushort Raw)
{
    public IReadOnlyDictionary<byte, Topology.PortState> Ports { get; } =
        Enumerable.Range(0, 4).ToDictionary(
            port => (byte)port,
            port => new Topology.PortState(
                (byte)port,
                HasLink: (Raw & (1 << (4 + port))) != 0,
                LoopClosed: (Raw & (1 << (8 + port * 2))) != 0,
                SignalDetected: (Raw & (1 << (9 + port * 2))) != 0));
}
```

Append to `src/OpenEC.Monitor/Learning/RegisterDecoders.cs`, inside the class:

```csharp
    public const ushort DlStatusRegister = 0x0110;

    /// <summary>Returning read of 0x0110 — DL status, the register a master polls to notice a
    /// topology change. A zero working counter means no slave answered, so the payload is
    /// meaningless, exactly as for <see cref="TrySiiData"/>.</summary>
    public static DlStatusFact? TryDlStatus(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Ado != DlStatusRegister || d.Payload.Length < 2 || d.WorkingCounter == 0) return null;
        return new DlStatusFact(SlaveRef.From(d),
            BinaryPrimitives.ReadUInt16LittleEndian(d.Payload.Span));
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~DlStatusDecoderTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS — 387 existing plus 6 new.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Monitor/Topology/PortState.cs \
        src/OpenEC.Monitor/Learning/LearnedFacts.cs \
        src/OpenEC.Monitor/Learning/RegisterDecoders.cs \
        tests/OpenEC.Monitor.Tests/Topology/DlStatusDecoderTests.cs
git commit -m "feat(topology): decode DL status 0x0110 into per-port link state"
```

---

## Task 2: Port error counter decoder

**Files:**
- Create: `src/OpenEC.Monitor/Topology/PortCounters.cs`
- Modify: `src/OpenEC.Monitor/Learning/LearnedFacts.cs` (append `PortCountersFact`)
- Modify: `src/OpenEC.Monitor/Learning/RegisterDecoders.cs` (append constants + `TryPortCounters`)
- Test: `tests/OpenEC.Monitor.Tests/Topology/PortCounterDecoderTests.cs`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: `sealed record PortCounters(byte? InvalidFrame, byte? RxError, byte? ForwardedRxError, byte? LostLink)` with `static readonly PortCounters Unknown` and `PortCounters Merge(PortCounters other)`; `sealed record PortCountersFact(SlaveRef Slave, IReadOnlyDictionary<byte, PortCounters> Ports, byte? ProcessingUnitErrors, byte? PdiErrors)`; `RegisterDecoders.TryPortCounters(EtherCatDatagram, FrameDirection) → PortCountersFact?`.

**Register map (ETG.1000.4, corroborated by EC-Inspector's Extended Diagnosis labels):** `0x0300 + 2n` invalid frame/CRC for port n, `0x0301 + 2n` RX error for port n, `0x0308 + n` forwarded RX error, `0x030C` processing unit error, `0x030D` PDI error, `0x0310 + n` lost link. A master typically reads the whole `0x0300`–`0x030D` block in one datagram, so the decoder walks a payload from whatever offset it starts at.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/PortCounterDecoderTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class PortCounterDecoderTests
{
    private static EtherCatDatagram Read(ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | 1001, false, false, 0, payload, wkc);

    /// <summary>The whole 0x0300-0x030D block in one read, as a master polls it:
    /// 8 bytes of per-port invalid-frame/RX-error pairs, 4 forwarded-RX-error bytes,
    /// then the processing-unit and PDI counters.</summary>
    [Fact]
    public void The_full_block_decodes_every_port_and_both_device_counters()
    {
        byte[] payload =
        [
            114, 0, 114, 0, 0, 0, 0, 0,   // 0x0300-0x0307: ports 0 and 1 each 114 invalid frames
            7, 0, 0, 0,                    // 0x0308-0x030B: forwarded RX error, port 0 = 7
            3,                             // 0x030C: processing unit errors
            9,                             // 0x030D: PDI errors
        ];

        var fact = RegisterDecoders.TryPortCounters(Read(0x0300, payload), FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal((byte)114, fact!.Ports[0].InvalidFrame);
        Assert.Equal((byte)0, fact.Ports[0].RxError);
        Assert.Equal((byte)114, fact.Ports[1].InvalidFrame);
        Assert.Equal((byte)7, fact.Ports[0].ForwardedRxError);
        Assert.Equal((byte)3, fact.ProcessingUnitErrors);
        Assert.Equal((byte)9, fact.PdiErrors);
    }

    /// <summary>Counters never read stay null. A short read of only 0x0300-0x0301 says nothing
    /// about lost link, and reporting zero there would invent a fact.</summary>
    [Fact]
    public void Registers_outside_the_read_stay_null()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0300, [5, 6]), FrameDirection.Returning);

        Assert.Equal((byte)5, fact!.Ports[0].InvalidFrame);
        Assert.Equal((byte)6, fact.Ports[0].RxError);
        Assert.Null(fact.Ports[0].LostLink);
        Assert.Null(fact.Ports[0].ForwardedRxError);
        Assert.Null(fact.ProcessingUnitErrors);
        Assert.False(fact.Ports.ContainsKey(1));
    }

    /// <summary>The lost-link block at 0x0310 is a separate read on most masters.</summary>
    [Fact]
    public void The_lost_link_block_decodes_on_its_own()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0310, [1, 2, 0, 0]), FrameDirection.Returning);

        Assert.Equal((byte)1, fact!.Ports[0].LostLink);
        Assert.Equal((byte)2, fact.Ports[1].LostLink);
        Assert.Null(fact.Ports[0].InvalidFrame);
    }

    /// <summary>A read that starts mid-block is attributed to the right ports.</summary>
    [Fact]
    public void A_read_starting_at_port_two_is_not_attributed_to_port_zero()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0304, [42, 0, 0, 0]), FrameDirection.Returning);

        Assert.Equal((byte)42, fact!.Ports[2].InvalidFrame);
        Assert.False(fact.Ports.ContainsKey(0));
    }

    [Fact]
    public void Outbound_reads_other_registers_and_zero_wkc_are_ignored()
    {
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, [1, 2]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0400, [1, 2]), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, [1, 2], wkc: 0), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, []), FrameDirection.Returning));
    }

    [Fact]
    public void Merging_keeps_the_newer_value_and_never_erases_a_known_one()
    {
        var first = new PortCounters(InvalidFrame: 5, RxError: null, ForwardedRxError: null, LostLink: 2);
        var second = new PortCounters(InvalidFrame: 6, RxError: 1, ForwardedRxError: null, LostLink: null);

        var merged = first.Merge(second);

        Assert.Equal((byte)6, merged.InvalidFrame);   // newer wins
        Assert.Equal((byte)1, merged.RxError);        // newly learned
        Assert.Equal((byte)2, merged.LostLink);       // not erased by an absent value
        Assert.Null(merged.ForwardedRxError);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~PortCounterDecoderTests"`
Expected: FAIL — `PortCounters` and `TryPortCounters` do not exist.

- [ ] **Step 3: Write the counters type**

Create `src/OpenEC.Monitor/Topology/PortCounters.cs`:

```csharp
namespace OpenEC.Monitor.Topology;

/// <summary>One port's ESC error counters. Every field is nullable and null means the register
/// was never read: a passive observer sees these only if the master polls them, and rendering an
/// unread counter as 0 would claim a healthy port we know nothing about.</summary>
public sealed record PortCounters(
    byte? InvalidFrame,
    byte? RxError,
    byte? ForwardedRxError,
    byte? LostLink)
{
    public static readonly PortCounters Unknown = new(null, null, null, null);

    public bool AnyKnown =>
        InvalidFrame is not null || RxError is not null
        || ForwardedRxError is not null || LostLink is not null;

    /// <summary>True when any known counter is non-zero. Null counters do not make a port look
    /// healthy — they make it look unknown, which <see cref="AnyKnown"/> distinguishes.</summary>
    public bool AnyError =>
        InvalidFrame > 0 || RxError > 0 || ForwardedRxError > 0 || LostLink > 0;

    /// <summary>Folds a newer partial read over this one. A field the newer read did not cover
    /// keeps its previous value rather than being erased, because masters read these registers
    /// in blocks that do not all cover the same fields.</summary>
    public PortCounters Merge(PortCounters newer) => new(
        newer.InvalidFrame ?? InvalidFrame,
        newer.RxError ?? RxError,
        newer.ForwardedRxError ?? ForwardedRxError,
        newer.LostLink ?? LostLink);
}
```

- [ ] **Step 4: Write the fact and the decoder**

Append to `src/OpenEC.Monitor/Learning/LearnedFacts.cs`:

```csharp
/// <summary>ESC error counters read out of the 0x0300-0x030D and 0x0310-0x0313 blocks. Only the
/// registers the read actually covered are present; the rest stay absent rather than zero.</summary>
public sealed record PortCountersFact(SlaveRef Slave,
    IReadOnlyDictionary<byte, Topology.PortCounters> Ports,
    byte? ProcessingUnitErrors, byte? PdiErrors);
```

Append to `src/OpenEC.Monitor/Learning/RegisterDecoders.cs`, inside the class:

```csharp
    public const ushort ErrorCounterBase = 0x0300;      // 0x0300 + 2n invalid frame, +1 RX error
    public const ushort ForwardedErrorBase = 0x0308;    // 0x0308 + n
    public const ushort ProcessingUnitErrorRegister = 0x030C;
    public const ushort PdiErrorRegister = 0x030D;
    public const ushort LostLinkBase = 0x0310;          // 0x0310 + n

    /// <summary>Returning read of the ESC error-counter registers (ETG.1000.4). Masters read
    /// these in blocks, so the payload is walked byte by byte from whatever offset the datagram
    /// started at and each byte is attributed to the register it actually lands on. A register
    /// the read did not cover is left absent — never defaulted to zero, which would claim a
    /// healthy port on a bus whose master never polls these at all.</summary>
    public static PortCountersFact? TryPortCounters(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Payload.Length == 0 || d.WorkingCounter == 0) return null;
        var start = d.Ado;
        var end = start + d.Payload.Length;
        if (end <= ErrorCounterBase || start > LostLinkBase + 3) return null;

        var span = d.Payload.Span;
        var ports = new Dictionary<byte, PortCounters>();
        byte? processingUnit = null;
        byte? pdi = null;

        PortCounters For(byte port) => ports.TryGetValue(port, out var existing)
            ? existing : PortCounters.Unknown;

        for (var i = 0; i < span.Length; i++)
        {
            var register = start + i;
            var value = span[i];
            switch (register)
            {
                case >= ErrorCounterBase and < ForwardedErrorBase:
                {
                    var offset = register - ErrorCounterBase;
                    var port = (byte)(offset / 2);
                    ports[port] = offset % 2 == 0
                        ? For(port) with { InvalidFrame = value }
                        : For(port) with { RxError = value };
                    break;
                }
                case >= ForwardedErrorBase and < ProcessingUnitErrorRegister:
                {
                    var port = (byte)(register - ForwardedErrorBase);
                    ports[port] = For(port) with { ForwardedRxError = value };
                    break;
                }
                case ProcessingUnitErrorRegister:
                    processingUnit = value;
                    break;
                case PdiErrorRegister:
                    pdi = value;
                    break;
                case >= LostLinkBase and <= LostLinkBase + 3:
                {
                    var port = (byte)(register - LostLinkBase);
                    ports[port] = For(port) with { LostLink = value };
                    break;
                }
            }
        }

        return ports.Count == 0 && processingUnit is null && pdi is null
            ? null
            : new PortCountersFact(SlaveRef.From(d), ports, processingUnit, pdi);
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~PortCounterDecoderTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Monitor/Topology/PortCounters.cs \
        src/OpenEC.Monitor/Learning/LearnedFacts.cs \
        src/OpenEC.Monitor/Learning/RegisterDecoders.cs \
        tests/OpenEC.Monitor.Tests/Topology/PortCounterDecoderTests.cs
git commit -m "feat(topology): decode ESC per-port error counters"
```

---

## Task 3: Port facts accumulate on the learned slave

**Files:**
- Modify: `src/OpenEC.Monitor/Learning/LearnedSlave.cs`
- Modify: `src/OpenEC.Monitor/Learning/LearnedBus.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/LearnedPortFactsTests.cs`

**Interfaces:**
- Consumes: `DlStatusFact`, `PortCountersFact`, `PortState`, `PortCounters` (Tasks 1-2); `LearnedBus.Resolve`, `LearnedBus.GetOrAdd` (existing private/internal helpers).
- Produces: `LearnedSlave.Ports` (`Dictionary<byte, PortState>`), `LearnedSlave.Counters` (`Dictionary<byte, PortCounters>`), `LearnedSlave.ProcessingUnitErrors` (`byte?`), `LearnedSlave.PdiErrors` (`byte?`), `LearnedSlave.ActiveDownstreamPorts` (`IReadOnlyList<byte>`).

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/LearnedPortFactsTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class LearnedPortFactsTests
{
    private static EtherCatDatagram Read(ushort adp, ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    private static LearnedBus BusWithStation(ushort station)
    {
        var bus = new LearnedBus();
        // APWR 0x0010 at auto-inc 0 assigns the station address, anchoring ring position 0.
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0, 0x0010_0000u, false, false, 0,
                BitConverter.GetBytes(station), 1),
            FrameDirection.Outbound);
        return bus;
    }

    [Fact]
    public void Dl_status_reads_land_on_the_addressed_slave()
    {
        var bus = BusWithStation(1001);

        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0110, [0x30, 0x0A]),
            FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(PortLinkState.Active, slave.Ports[0].State);
        Assert.Equal(PortLinkState.Active, slave.Ports[1].State);
        Assert.Equal(new byte[] { 1 }, slave.ActiveDownstreamPorts);
    }

    [Fact]
    public void Active_downstream_ports_follow_the_esc_forwarding_order()
    {
        var bus = BusWithStation(1001);

        // Link + open loop on ports 0, 1, 2 and 3: link bits 0x00F0, all loops open.
        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0110, [0xF0, 0x00]),
            FrameDirection.Returning);

        // Port 0 is upstream, so the downstream ports are the rest in forwarding order 3, 1, 2.
        Assert.Equal(new byte[] { 3, 1, 2 }, Assert.Single(bus.Slaves).ActiveDownstreamPorts);
    }

    [Fact]
    public void Counter_reads_merge_rather_than_replace()
    {
        var bus = BusWithStation(1001);

        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0310, [4, 0, 0, 0]),
            FrameDirection.Returning);
        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0300, [114, 0]),
            FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal((byte)4, slave.Counters[0].LostLink);      // survived the second read
        Assert.Equal((byte)114, slave.Counters[0].InvalidFrame);
    }

    [Fact]
    public void A_slave_with_no_port_read_has_no_port_facts_at_all()
    {
        var slave = Assert.Single(BusWithStation(1001).Slaves);

        Assert.Empty(slave.Ports);
        Assert.Empty(slave.Counters);
        Assert.Null(slave.ProcessingUnitErrors);
        Assert.Empty(slave.ActiveDownstreamPorts);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedPortFactsTests"`
Expected: FAIL — `LearnedSlave.Ports` does not exist.

- [ ] **Step 3: Add the fact collections to `LearnedSlave`**

In `src/OpenEC.Monitor/Learning/LearnedSlave.cs`, add `using OpenEC.Monitor.Topology;` and insert after the existing `Fmmus`/`EepromWords` properties:

```csharp
    /// <summary>Port state from DL status (0x0110), keyed by port index. An absent key means the
    /// register was never read for that port — the same contract as every other property here.</summary>
    public Dictionary<byte, PortState> Ports { get; } = new();

    /// <summary>Per-port error counters, merged across the block reads that produced them.
    /// Named Counters rather than PortCounters on purpose: a property sharing its element type's
    /// name hides that type in every expression inside this class, so <c>PortCounters.Unknown</c>
    /// would resolve to the property instead of the type. It also matches
    /// <see cref="Topology.TopologyDevice.Counters"/>.</summary>
    public Dictionary<byte, PortCounters> Counters { get; } = new();

    public byte? ProcessingUnitErrors { get; set; }
    public byte? PdiErrors { get; set; }

    /// <summary>The ports that can carry a downstream topology edge, in the ESC's internal
    /// forwarding order. Port 0 is upstream by definition and is excluded. Ordering matters:
    /// it decides which branch the reconstruction walks first, and therefore the map's row
    /// order — see the topology design spec §10, where the order is still to be confirmed
    /// against hardware.</summary>
    public IReadOnlyList<byte> ActiveDownstreamPorts =>
        TopologyReconstructor.ForwardingOrder
            .Where(port => port != 0 && Ports.TryGetValue(port, out var state) && state.IsActive)
            .ToList();

    public void RecordPorts(IReadOnlyDictionary<byte, PortState> ports)
    {
        foreach (var (port, state) in ports) Ports[port] = state;
    }

    public void RecordPortCounters(IReadOnlyDictionary<byte, PortCounters> counters)
    {
        foreach (var (port, value) in counters)
            Counters[port] = Counters.TryGetValue(port, out var existing)
                ? existing.Merge(value)
                : value;
    }
```

- [ ] **Step 4: Route the facts in `LearnedBus.Observe`**

In `src/OpenEC.Monitor/Learning/LearnedBus.cs`, insert immediately before the `foreach (var sm in RegisterDecoders.TrySyncManagers(...))` loop:

```csharp
        if (RegisterDecoders.TryDlStatus(d, direction) is { } dlStatus)
        {
            if (Resolve(dlStatus.Slave) is { } portSlave) portSlave.RecordPorts(dlStatus.Ports);
            return;
        }

        if (RegisterDecoders.TryPortCounters(d, direction) is { } counters)
        {
            if (Resolve(counters.Slave) is { } counterSlave)
            {
                counterSlave.RecordPortCounters(counters.Ports);
                counterSlave.ProcessingUnitErrors =
                    counters.ProcessingUnitErrors ?? counterSlave.ProcessingUnitErrors;
                counterSlave.PdiErrors = counters.PdiErrors ?? counterSlave.PdiErrors;
            }
            return;
        }
```

Note the placement: both decoders return early like the SII pair above them, so a DL-status read never falls through to the SyncManager/FMMU decoders. `ForwardingOrder` arrives in Task 4 — until then, add it as a temporary constant on `TopologyReconstructor`; Step 5 will fail to build otherwise.

- [ ] **Step 5: Create the reconstructor stub so this task builds**

Create `src/OpenEC.Monitor/Topology/TopologyReconstructor.cs` with only the constant. Task 4 fills in the rest:

```csharp
namespace OpenEC.Monitor.Topology;

public static class TopologyReconstructor
{
    /// <summary>The ESC's internal frame forwarding order. A frame enters at port 0 and is
    /// forwarded 0 → 3 → 1 → 2, so a device with two open downstream ports branches out of the
    /// earlier one in this sequence first. This ordering decides the map's row order and is
    /// marked unverified in the design spec §10 — it lives here, in one place, so confirming it
    /// against a real capture is a one-line change.</summary>
    public static readonly byte[] ForwardingOrder = [0, 3, 1, 2];
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedPortFactsTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. Watch `BringupCaptureTests` and `LearnedBusTests` in particular — the new early returns sit in the middle of `LearnedBus.Observe`, and a misplaced `return` would starve the SyncManager and FMMU decoders.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Monitor/Learning/LearnedSlave.cs \
        src/OpenEC.Monitor/Learning/LearnedBus.cs \
        src/OpenEC.Monitor/Topology/TopologyReconstructor.cs \
        tests/OpenEC.Monitor.Tests/Topology/LearnedPortFactsTests.cs
git commit -m "feat(topology): accumulate per-port facts on the learned slave"
```

---

## Task 4: Reconstruct the tree from ring order and active ports

**Files:**
- Create: `src/OpenEC.Monitor/Topology/BusTopology.cs`
- Modify: `src/OpenEC.Monitor/Topology/TopologyReconstructor.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/TopologyReconstructorTests.cs`

**Interfaces:**
- Consumes: `PortState`, `PortCounters`, `PortLinkState`, `TopologyReconstructor.ForwardingOrder`.
- Produces:
  - `const ushort BusTopology.MasterAddress = 0`
  - `enum TopologyEdgeSource { Wire, Eni, Inferred }`
  - `sealed record TopologyDevice(ushort Address, int RingPosition, IReadOnlyDictionary<byte, PortState> Ports, IReadOnlyDictionary<byte, PortCounters> Counters)` with `static TopologyDevice FromLearned(LearnedSlave)` added in Task 7 — **not** before
  - `sealed record TopologyNode(ushort Address, int RingPosition, ushort? ParentAddress, byte? ParentPort, byte OwnPort, IReadOnlyDictionary<byte, PortState> Ports, IReadOnlyDictionary<byte, PortCounters> Counters, TopologyEdgeSource EdgeSource)` with `bool IsMaster`
  - `sealed record TopologyConflict(ushort Address, string Declared, string Observed)`
  - `sealed record BusTopology(IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<ushort> Unplaced, IReadOnlyList<TopologyConflict> Conflicts, bool PortDataObserved)` with `static readonly BusTopology Empty`, `IEnumerable<TopologyNode> ChildrenOf(ushort address)`, `TopologyNode? Find(ushort address)`
  - `BusTopology TopologyReconstructor.Reconstruct(IReadOnlyList<TopologyDevice> devices)`

ENI edges arrive in Task 5; this task is the wire path only.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/TopologyReconstructorTests.cs`:

```csharp
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyReconstructorTests
{
    /// <summary>A device whose given ports are active and whose remaining ports are unused.
    /// Port 0 is always included as the upstream link.</summary>
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, HasLink: active, LoopClosed: !active,
                SignalDetected: active);
        }
        return new TopologyDevice(address, ringPosition, ports,
            new Dictionary<byte, PortCounters>());
    }

    /// <summary>A device with no port data at all.</summary>
    private static TopologyDevice Blind(ushort address, int ringPosition) =>
        new(address, ringPosition, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());

    [Fact]
    public void A_straight_line_chains_every_device_to_its_predecessor()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1),
            Device(1002, 1, 1),
            Device(1003, 2),          // line end: only port 0 active
        ]);

        Assert.True(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Wire, n.EdgeSource));
    }

    [Fact]
    public void The_master_is_the_root_and_the_first_device_hangs_off_it()
    {
        var topology = TopologyReconstructor.Reconstruct([Device(1001, 0)]);

        var master = Assert.Single(topology.Nodes, n => n.IsMaster);
        Assert.Null(master.ParentAddress);
        Assert.Equal(-1, master.RingPosition);
        Assert.Equal((byte)0, topology.Find(1001)!.ParentPort);
        Assert.Equal((byte)0, topology.Find(1001)!.OwnPort);
    }

    /// <summary>1001 opens a branch on ports 1 and 2. Forwarding order 0 → 3 → 1 → 2 means the
    /// branch out of port 1 is walked first, so 1002 lands there and 1003 — arriving after 1002's
    /// line has ended — lands on port 2.</summary>
    [Fact]
    public void A_branch_point_places_its_second_subtree_on_its_next_port()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1, 2),
            Device(1002, 1),          // line end, closes the port 1 branch
            Device(1003, 2),          // line end, takes port 2
        ]);

        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal((ushort)1001, topology.Find(1003)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1003)!.ParentPort);
        Assert.Equal(2, topology.ChildrenOf(1001).Count());
    }

    /// <summary>Port 3 precedes ports 1 and 2 in the forwarding order, so a device with 3 and 1
    /// open branches out of 3 first.</summary>
    [Fact]
    public void Port_three_is_walked_before_port_one()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 3, 1),
            Device(1002, 1),
            Device(1003, 2),
        ]);

        Assert.Equal((byte)3, topology.Find(1002)!.ParentPort);
        Assert.Equal((byte)1, topology.Find(1003)!.ParentPort);
    }

    /// <summary>The reference image's shape: a main line, a branch that itself runs several
    /// devices deep, and a further branch off a device inside it.</summary>
    [Fact]
    public void Nested_branches_reconstruct_to_the_expected_parents()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1),          // main line
            Device(1002, 1, 1, 2),       // junction: two branches
            Device(1003, 2, 1),          // first branch, continues
            Device(1004, 3),             // first branch ends
            Device(1005, 4),             // second branch of 1002
        ]);

        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1003, topology.Find(1004)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1005)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1003)!.ParentPort);
        Assert.Equal((byte)2, topology.Find(1005)!.ParentPort);
    }

    /// <summary>More line ends than branches opened: the port states contradict each other. The
    /// devices placed so far stand; the remainder is reported unplaced rather than guessed.</summary>
    [Fact]
    public void Contradictory_port_data_leaves_the_remainder_unplaced()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0),          // claims to end the line immediately
            Device(1002, 1),          // nowhere left to attach
            Device(1003, 2),
        ]);

        Assert.Equal((ushort)1001, Assert.Single(topology.Nodes, n => !n.IsMaster).Address);
        Assert.Equal(new ushort[] { 1002, 1003 }, topology.Unplaced);
    }

    [Fact]
    public void No_port_data_at_all_degrades_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Blind(1001, 0), Blind(1002, 1), Blind(1003, 2),
        ]);

        Assert.False(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Inferred, n.EdgeSource));
    }

    [Fact]
    public void Devices_without_a_ring_position_sort_last_by_address()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1005, -1, 1), Device(1001, 0, 1), Device(1004, -1),
        ]);

        Assert.Equal([1001, 1005, 1004],
            topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address));
    }

    [Fact]
    public void An_empty_device_list_yields_a_master_only_topology()
    {
        var topology = TopologyReconstructor.Reconstruct([]);

        Assert.True(Assert.Single(topology.Nodes).IsMaster);
        Assert.False(topology.PortDataObserved);
    }

    [Fact]
    public void Every_device_is_placed_at_most_once_so_a_cycle_is_impossible()
    {
        // Every device claims three open downstream ports: without the ring-order bound this
        // would loop forever or attach a device twice.
        var topology = TopologyReconstructor.Reconstruct(
            Enumerable.Range(0, 20)
                .Select(i => Device((ushort)(1001 + i), i, 1, 2, 3))
                .ToList());

        Assert.Equal(20, topology.Nodes.Count(n => !n.IsMaster));
        Assert.Equal(20, topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address).Distinct().Count());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyReconstructorTests"`
Expected: FAIL — `TopologyDevice`, `BusTopology` and `Reconstruct` do not exist.

- [ ] **Step 3: Write the model types**

Create `src/OpenEC.Monitor/Topology/BusTopology.cs`:

```csharp
namespace OpenEC.Monitor.Topology;

/// <summary>Where a topology edge came from. Deliberately separate from
/// <see cref="Learning.FactSource"/>, whose members describe identity and mapping provenance and
/// would be a poor fit for an edge.</summary>
public enum TopologyEdgeSource
{
    /// <summary>Derived from DL-status port state observed on the wire.</summary>
    Wire,

    /// <summary>Declared by an ENI &lt;PreviousPort&gt; element.</summary>
    Eni,

    /// <summary>Neither source described this edge; it follows ring order alone.</summary>
    Inferred,
}

/// <summary>Reconstruction input: one device's ring position and port facts. A small record
/// rather than <see cref="Learning.LearnedSlave"/> so the reconstruction stays pure and can be
/// driven from hand-written fixtures.</summary>
public sealed record TopologyDevice(
    ushort Address,
    int RingPosition,
    IReadOnlyDictionary<byte, PortState> Ports,
    IReadOnlyDictionary<byte, PortCounters> Counters)
{
    /// <summary>The ports that can carry a downstream edge, in the ESC's forwarding order.</summary>
    public IReadOnlyList<byte> ActiveDownstreamPorts =>
        TopologyReconstructor.ForwardingOrder
            .Where(port => port != 0 && Ports.TryGetValue(port, out var state) && state.IsActive)
            .ToList();

    public bool HasPortData => Ports.Count > 0;
}

/// <summary>One placed device. <paramref name="OwnPort"/> is the port the frame enters on, which
/// is 0 for every ESC by definition; it is carried explicitly so the layout engine never has to
/// assume it.</summary>
public sealed record TopologyNode(
    ushort Address,
    int RingPosition,
    ushort? ParentAddress,
    byte? ParentPort,
    byte OwnPort,
    IReadOnlyDictionary<byte, PortState> Ports,
    IReadOnlyDictionary<byte, PortCounters> Counters,
    TopologyEdgeSource EdgeSource)
{
    public bool IsMaster => ParentAddress is null;
}

/// <summary>An edge the ENI and the wire describe differently. Reported, never silently
/// resolved: the wire's version is what gets drawn.</summary>
public sealed record TopologyConflict(ushort Address, string Declared, string Observed);

/// <param name="PortDataObserved">False when no device produced port state, meaning the tree is
/// ring order alone and no port bars may be drawn.</param>
public sealed record BusTopology(
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<ushort> Unplaced,
    IReadOnlyList<TopologyConflict> Conflicts,
    bool PortDataObserved)
{
    /// <summary>The master's stand-in address. Zero is not a valid configured station address, so
    /// it cannot collide with a real device.</summary>
    public const ushort MasterAddress = 0;

    public static readonly BusTopology Empty =
        new([MasterNode], [], [], PortDataObserved: false);

    internal static TopologyNode MasterNode { get; } = new(
        MasterAddress, RingPosition: -1, ParentAddress: null, ParentPort: null, OwnPort: 0,
        new Dictionary<byte, PortState>(), new Dictionary<byte, PortCounters>(),
        TopologyEdgeSource.Wire);

    public TopologyNode? Find(ushort address) => Nodes.FirstOrDefault(n => n.Address == address);

    public IEnumerable<TopologyNode> ChildrenOf(ushort address) =>
        Nodes.Where(n => n.ParentAddress == address);
}
```

- [ ] **Step 4: Write the reconstruction**

Replace `src/OpenEC.Monitor/Topology/TopologyReconstructor.cs` with the constant plus the walk:

```csharp
namespace OpenEC.Monitor.Topology;

/// <summary>Turns ring order plus per-device active-port sets into a tree. Pure: no I/O, no
/// mutation of its input, same input yields the same output.</summary>
public static class TopologyReconstructor
{
    /// <summary>The ESC's internal frame forwarding order. A frame enters at port 0 and is
    /// forwarded 0 → 3 → 1 → 2, so a device with two open downstream ports branches out of the
    /// earlier one in this sequence first. This ordering decides the map's row order and is
    /// marked unverified in the design spec §10 — it lives here, in one place, so confirming it
    /// against a real capture is a one-line change.</summary>
    public static readonly byte[] ForwardingOrder = [0, 3, 1, 2];

    /// <summary>Devices in ring order, unknown positions last by address — the same ordering
    /// <see cref="Learning.LearnedBus.Slaves"/> uses, so the map and the device tree agree.</summary>
    private static List<TopologyDevice> InRingOrder(IReadOnlyList<TopologyDevice> devices) =>
        devices
            .OrderBy(d => d.RingPosition < 0 ? int.MaxValue : d.RingPosition)
            .ThenBy(d => d.Address)
            .ToList();

    public static BusTopology Reconstruct(IReadOnlyList<TopologyDevice> devices)
    {
        var ordered = InRingOrder(devices);
        if (ordered.Count == 0) return BusTopology.Empty;
        return ordered.Any(d => d.HasPortData) ? FromPorts(ordered) : RingOrderOnly(ordered);
    }

    /// <summary>The stack walk. Each device is placed exactly once, in ring order, so the result
    /// cannot contain a cycle however contradictory the port data is.</summary>
    private static BusTopology FromPorts(List<TopologyDevice> ordered)
    {
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var unplaced = new List<ushort>();

        // The master contributes one downstream cable, modelled as its port 0.
        var stack = new List<(ushort Address, Queue<byte> Remaining)>
        {
            (BusTopology.MasterAddress, new Queue<byte>([(byte)0])),
        };

        foreach (var device in ordered)
        {
            while (stack.Count > 0 && stack[^1].Remaining.Count == 0)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
            {
                // More line ends than branches opened: the port states disagree with each other.
                unplaced.Add(device.Address);
                continue;
            }

            var (parentAddress, remaining) = stack[^1];
            var parentPort = remaining.Dequeue();
            nodes.Add(new TopologyNode(device.Address, device.RingPosition, parentAddress,
                parentPort, OwnPort: 0, device.Ports, device.Counters,
                device.HasPortData ? TopologyEdgeSource.Wire : TopologyEdgeSource.Inferred));
            stack.Add((device.Address, new Queue<byte>(device.ActiveDownstreamPorts)));
        }

        return new BusTopology(nodes, unplaced, [], PortDataObserved: true);
    }

    /// <summary>No device produced port state. Ring order is still real, so the devices are
    /// chained as one line and every edge is labelled inferred. Callers must not draw port bars
    /// for this topology — see <see cref="BusTopology.PortDataObserved"/>.</summary>
    private static BusTopology RingOrderOnly(List<TopologyDevice> ordered)
    {
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var parent = BusTopology.MasterAddress;
        foreach (var device in ordered)
        {
            nodes.Add(new TopologyNode(device.Address, device.RingPosition, parent,
                ParentPort: parent == BusTopology.MasterAddress ? (byte)0 : (byte)1, OwnPort: 0,
                device.Ports, device.Counters, TopologyEdgeSource.Inferred));
            parent = device.Address;
        }
        return new BusTopology(nodes, [], [], PortDataObserved: false);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyReconstructorTests"`
Expected: PASS, 10 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Monitor/Topology/BusTopology.cs \
        src/OpenEC.Monitor/Topology/TopologyReconstructor.cs \
        tests/OpenEC.Monitor.Tests/Topology/TopologyReconstructorTests.cs
git commit -m "feat(topology): reconstruct the bus tree from ring order and port state"
```

---

## Task 5: Parse ENI `<PreviousPort>`

**Files:**
- Modify: `src/OpenEC.Monitor/Eni/EniModels.cs`
- Modify: `src/OpenEC.Monitor/Eni/EniConfiguration.cs`
- Create: `tests/OpenEC.Monitor.Tests/Fixtures/branched.eni.xml`
- Modify: `tests/OpenEC.Monitor.Tests/OpenEC.Monitor.Tests.csproj` only if fixtures are listed individually — check first; `sample.eni.xml` is already copied, so a wildcard is likely already in place
- Test: `tests/OpenEC.Monitor.Tests/Topology/EniPreviousPortTests.cs`

**Interfaces:**
- Consumes: `EniConfiguration.Load`, `EniXmlValues.ParseNumber`, `Text`, `Local` (existing private helpers).
- Produces: `sealed record EniPreviousPort(ushort PhysAddr, byte Port)` with `static byte? ParsePort(string?)`; `EniSlave.PreviousPort` as a new **trailing** constructor parameter with default `null`.

**Port letter mapping (spec §10 — unverified):** ENI writes the port as a letter. The mapping used is `A → 0`, `B → 1`, `C → 2`, `D → 3`, and a numeric value is taken as the port index directly. This lives in exactly one method so confirming it against hardware is a one-line change.

- [ ] **Step 1: Write the fixture**

Create `tests/OpenEC.Monitor.Tests/Fixtures/branched.eni.xml` — a four-slave bus where 1002 is a junction carrying branches on ports B and C:

```xml
<?xml version="1.0" encoding="utf-8"?>
<EtherCATConfig>
  <Config>
    <Master><Info><Name>EtherCAT Master</Name></Info></Master>
    <Slave>
      <Info><Name>Term 1 (EK1100)</Name><PhysAddr>1001</PhysAddr><AutoIncAddr>0</AutoIncAddr>
        <VendorId>2</VendorId><ProductCode>72100946</ProductCode><RevisionNo>1179648</RevisionNo></Info>
    </Slave>
    <Slave>
      <Info><Name>Term 2 (EK1122)</Name><PhysAddr>1002</PhysAddr><AutoIncAddr>65535</AutoIncAddr>
        <VendorId>2</VendorId><ProductCode>73502802</ProductCode><RevisionNo>1179648</RevisionNo></Info>
      <PreviousPort><PhysAddr>1001</PhysAddr><Port>B</Port></PreviousPort>
    </Slave>
    <Slave>
      <Info><Name>Term 3 (EL1008)</Name><PhysAddr>1003</PhysAddr><AutoIncAddr>65534</AutoIncAddr>
        <VendorId>2</VendorId><ProductCode>66093138</ProductCode><RevisionNo>1179648</RevisionNo></Info>
      <PreviousPort><PhysAddr>1002</PhysAddr><Port>B</Port></PreviousPort>
    </Slave>
    <Slave>
      <Info><Name>Term 4 (EL2008)</Name><PhysAddr>1004</PhysAddr><AutoIncAddr>65533</AutoIncAddr>
        <VendorId>2</VendorId><ProductCode>131608658</ProductCode><RevisionNo>1179648</RevisionNo></Info>
      <PreviousPort><PhysAddr>1002</PhysAddr><Port>C</Port></PreviousPort>
    </Slave>
  </Config>
</EtherCATConfig>
```

- [ ] **Step 2: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/EniPreviousPortTests.cs`:

```csharp
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Tests.Topology;

public class EniPreviousPortTests
{
    private static EniConfiguration Load(string fixture) =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

    [Fact]
    public void Previous_port_edges_are_parsed_with_their_port_letters()
    {
        var eni = Load("branched.eni.xml");

        Assert.Equal(new EniPreviousPort(1001, 1), Slave(eni, 1002).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 1), Slave(eni, 1003).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 2), Slave(eni, 1004).PreviousPort);
    }

    /// <summary>The first slave has no PreviousPort — it hangs off the master. A null must stay
    /// null rather than defaulting to a parent the file never declared.</summary>
    [Fact]
    public void A_slave_without_a_previous_port_element_has_none()
    {
        Assert.Null(Slave(Load("branched.eni.xml"), 1001).PreviousPort);
    }

    /// <summary>The existing sample fixture declares no topology at all, and must keep loading.</summary>
    [Fact]
    public void An_eni_with_no_topology_still_loads()
    {
        var eni = Load("sample.eni.xml");

        Assert.NotEmpty(eni.Slaves);
        Assert.All(eni.Slaves, s => Assert.Null(s.PreviousPort));
    }

    [Theory]
    [InlineData("A", 0)]
    [InlineData("B", 1)]
    [InlineData("C", 2)]
    [InlineData("D", 3)]
    [InlineData("a", 0)]
    [InlineData("2", 2)]
    public void Port_letters_and_numbers_both_map_to_a_port_index(string text, byte expected)
    {
        Assert.Equal(expected, EniPreviousPort.ParsePort(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("9")]
    [InlineData(null)]
    public void An_unrecognised_port_is_null_rather_than_a_guess(string? text)
    {
        Assert.Null(EniPreviousPort.ParsePort(text));
    }

    private static EniSlave Slave(EniConfiguration eni, ushort address) =>
        eni.Slaves.Single(s => s.PhysAddr == address);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniPreviousPortTests"`
Expected: FAIL — `EniPreviousPort` does not exist.

- [ ] **Step 4: Add the model**

In `src/OpenEC.Monitor/Eni/EniModels.cs`, add above `EniSlave`:

```csharp
/// <summary>An ENI-declared topology edge: the device upstream of this one, and the upstream
/// device's port it hangs off. ENI writes the port as a letter.</summary>
public sealed record EniPreviousPort(ushort PhysAddr, byte Port)
{
    /// <summary>Maps an ENI port designation to a port index. The letter mapping
    /// (A=0, B=1, C=2, D=3) is marked unverified in the topology design spec §10 and lives only
    /// here. An unrecognised value yields null rather than a defaulted port 0, which would place
    /// a branch on the upstream port and silently corrupt the tree.</summary>
    public static byte? ParsePort(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "A" => 0,
        "B" => 1,
        "C" => 2,
        "D" => 3,
        "0" => 0,
        "1" => 1,
        "2" => 2,
        "3" => 3,
        _ => null,
    };
}
```

Then extend `EniSlave` — the new parameter is **trailing with a default**, so no existing call site changes:

```csharp
public sealed record EniSlave(string Name, ushort PhysAddr, ushort AutoIncAddr,
    uint VendorId, uint ProductCode, uint RevisionNo,
    MailboxRange? MailboxOut, MailboxRange? MailboxIn,
    EniPreviousPort? PreviousPort = null);
```

- [ ] **Step 5: Parse it**

In `src/OpenEC.Monitor/Eni/EniConfiguration.cs`, add the parse helper beside `ParseMailboxRange`:

```csharp
    /// <summary>&lt;PreviousPort&gt; declares the upstream device and its port. Both halves must
    /// parse: a declared parent with an unreadable port is not a usable edge, and inventing
    /// port 0 for it would place a branch on the upstream port.</summary>
    private static EniPreviousPort? ParsePreviousPort(XElement? el)
    {
        if (el is null) return null;
        var physAddr = (ushort?)EniXmlValues.ParseNumber(Text(el, "PhysAddr"));
        var port = EniPreviousPort.ParsePort(Text(el, "Port"));
        return physAddr is null || port is null ? null : new EniPreviousPort(physAddr.Value, port.Value);
    }
```

and add the trailing argument to the `slaves.Add(new EniSlave(...))` call, after the two mailbox ranges:

```csharp
                ParsePreviousPort(Local(el, "PreviousPort"))));
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniPreviousPortTests"`
Expected: PASS, 13 tests (3 facts + 10 theory cases).

- [ ] **Step 7: Confirm the fixture is copied to the output**

Run: `ls tests/OpenEC.Monitor.Tests/bin/Debug/net8.0/Fixtures/`
Expected: `branched.eni.xml` is present. If it is not, add a `<Content Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />` item to the test project.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/OpenEC.Monitor/Eni/EniModels.cs \
        src/OpenEC.Monitor/Eni/EniConfiguration.cs \
        tests/OpenEC.Monitor.Tests/Fixtures/branched.eni.xml \
        tests/OpenEC.Monitor.Tests/Topology/EniPreviousPortTests.cs
git commit -m "feat(eni): parse PreviousPort topology edges"
```

---

## Task 6: Resolve ENI edges against the wire

**Files:**
- Modify: `src/OpenEC.Monitor/Topology/TopologyReconstructor.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/TopologyResolutionTests.cs`

**Interfaces:**
- Consumes: `BusTopology`, `TopologyNode`, `TopologyDevice`, `TopologyConflict`, `EniPreviousPort`, `EniConfiguration`.
- Produces: `TopologyReconstructor.Reconstruct(IReadOnlyList<TopologyDevice> devices, EniConfiguration? eni)` — a second overload; the single-argument form from Task 4 keeps working and forwards with `eni: null`.

**Resolution rule (spec §3):** the wire's edge wins wherever it exists. An ENI edge supplies the parent for a device the wire never placed. An edge both describe differently is drawn as the wire has it and recorded in `BusTopology.Conflicts`.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/TopologyResolutionTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyResolutionTests
{
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, active, !active, active);
        }
        return new TopologyDevice(address, ringPosition, ports, new Dictionary<byte, PortCounters>());
    }

    private static TopologyDevice Blind(ushort address, int ringPosition) =>
        new(address, ringPosition, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());

    private static EniConfiguration Eni(params (ushort Address, ushort? Parent, byte Port)[] slaves) =>
        new()
        {
            Slaves = slaves.Select(s => new EniSlave($"Slave {s.Address}", s.Address, 0, 0, 0, 0,
                null, null, s.Parent is { } parent ? new EniPreviousPort(parent, s.Port) : null)).ToList(),
            CyclicCommands = [],
            Variables = [],
        };

    [Fact]
    public void The_wire_wins_where_both_describe_the_same_edge()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 2)));   // ENI claims port 2; the wire says port 1

        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal(TopologyEdgeSource.Wire, topology.Find(1002)!.EdgeSource);
    }

    [Fact]
    public void A_disagreement_is_recorded_as_a_conflict()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 2)));

        var conflict = Assert.Single(topology.Conflicts);
        Assert.Equal((ushort)1002, conflict.Address);
        Assert.Contains("1001 port 2", conflict.Declared);
        Assert.Contains("1001 port 1", conflict.Observed);
    }

    [Fact]
    public void Agreement_produces_no_conflict()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 1)));

        Assert.Empty(topology.Conflicts);
    }

    /// <summary>The wire placed nothing, so every edge comes from the ENI — a real branched tree
    /// rather than the ring-order line the wire-only path would produce.</summary>
    [Fact]
    public void Eni_edges_place_devices_the_wire_never_described()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1), Blind(1003, 2), Blind(1004, 3)],
            Eni((1001, null, 0), (1002, 1001, 1), (1003, 1002, 1), (1004, 1002, 2)));

        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Eni, n.EdgeSource));
        Assert.False(topology.PortDataObserved);   // no port bars may be drawn
    }

    [Fact]
    public void An_eni_declaring_no_parent_for_the_first_slave_attaches_it_to_the_master()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0)], Eni((1001, null, 0)));

        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
    }

    /// <summary>An ENI edge naming a parent that is not on the bus cannot be honoured. The device
    /// falls back to ring order rather than being dropped.</summary>
    [Fact]
    public void An_eni_edge_to_an_absent_parent_falls_back_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1)],
            Eni((1001, null, 0), (1002, 1099, 1)));

        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal(TopologyEdgeSource.Inferred, topology.Find(1002)!.EdgeSource);
    }

    /// <summary>Nodes are compared element-wise rather than comparing the two BusTopology records:
    /// a record's list member compares by reference, so the topologies themselves would never be
    /// equal. The nodes DO compare correctly here, because both calls pass the same device
    /// instances and therefore share their port dictionaries.</summary>
    [Fact]
    public void A_null_eni_behaves_exactly_like_the_single_argument_overload()
    {
        var devices = new[] { Device(1001, 0, 1), Device(1002, 1) };

        var implicitly_null = TopologyReconstructor.Reconstruct(devices);
        var explicitly_null = TopologyReconstructor.Reconstruct(devices, eni: null);

        Assert.Equal(implicitly_null.Nodes, explicitly_null.Nodes);
        Assert.Equal(implicitly_null.Unplaced, explicitly_null.Unplaced);
        Assert.Equal(implicitly_null.PortDataObserved, explicitly_null.PortDataObserved);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyResolutionTests"`
Expected: FAIL — no two-argument `Reconstruct` overload.

- [ ] **Step 3: Add the resolution**

In `src/OpenEC.Monitor/Topology/TopologyReconstructor.cs`, add `using OpenEC.Monitor.Eni;`, keep the existing one-argument method as a forwarder, and add the overload:

```csharp
    public static BusTopology Reconstruct(IReadOnlyList<TopologyDevice> devices) =>
        Reconstruct(devices, eni: null);

    /// <summary>The wire is the authority; the ENI fills gaps and its disagreements are reported.
    /// Spec §3.</summary>
    public static BusTopology Reconstruct(IReadOnlyList<TopologyDevice> devices, EniConfiguration? eni)
    {
        var ordered = InRingOrder(devices);
        if (ordered.Count == 0) return BusTopology.Empty;

        var declared = eni?.Slaves
            .Where(s => s.PreviousPort is not null)
            .ToDictionary(s => s.PhysAddr, s => s.PreviousPort!)
            ?? new Dictionary<ushort, EniPreviousPort>();

        if (ordered.Any(d => d.HasPortData))
        {
            var fromWire = FromPorts(ordered);
            return fromWire with { Conflicts = Conflicts(fromWire, declared) };
        }

        return declared.Count > 0 ? FromEni(ordered, declared) : RingOrderOnly(ordered);
    }

    /// <summary>Compares the drawn tree against what the ENI declared. Only devices the wire
    /// actually placed are compared: a device the wire never described has no observed edge to
    /// disagree with, and reporting one would accuse a healthy machine.</summary>
    private static List<TopologyConflict> Conflicts(BusTopology wire,
        IReadOnlyDictionary<ushort, EniPreviousPort> declared)
    {
        var conflicts = new List<TopologyConflict>();
        foreach (var node in wire.Nodes.Where(n => !n.IsMaster
                                                   && n.EdgeSource == TopologyEdgeSource.Wire))
        {
            if (!declared.TryGetValue(node.Address, out var edge)) continue;
            if (edge.PhysAddr == node.ParentAddress && edge.Port == node.ParentPort) continue;
            conflicts.Add(new TopologyConflict(node.Address,
                $"{edge.PhysAddr} port {edge.Port}",
                $"{node.ParentAddress} port {node.ParentPort}"));
        }
        return conflicts;
    }

    /// <summary>Every edge from the ENI. An edge naming a parent that is not on the bus cannot be
    /// honoured, so that device falls back to its ring-order predecessor and is labelled inferred
    /// rather than being dropped from the map.</summary>
    private static BusTopology FromEni(List<TopologyDevice> ordered,
        IReadOnlyDictionary<ushort, EniPreviousPort> declared)
    {
        var present = ordered.Select(d => d.Address).ToHashSet();
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var previous = BusTopology.MasterAddress;

        foreach (var device in ordered)
        {
            var edge = declared.GetValueOrDefault(device.Address);
            var usable = edge is not null && present.Contains(edge.PhysAddr);
            nodes.Add(new TopologyNode(device.Address, device.RingPosition,
                usable ? edge!.PhysAddr : previous,
                usable ? edge!.Port : previous == BusTopology.MasterAddress ? (byte)0 : (byte)1,
                OwnPort: 0, device.Ports, device.Counters,
                usable || edge is null && previous == BusTopology.MasterAddress
                    ? TopologyEdgeSource.Eni
                    : TopologyEdgeSource.Inferred));
            previous = device.Address;
        }

        return new BusTopology(nodes, [], [], PortDataObserved: false);
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyResolutionTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS, including Task 4's tests unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Topology/TopologyReconstructor.cs \
        tests/OpenEC.Monitor.Tests/Topology/TopologyResolutionTests.cs
git commit -m "feat(topology): resolve ENI edges against the wire, reporting disagreements"
```

---

## Task 7: Topology survives the ENI export and the cache

**Files:**
- Modify: `src/OpenEC.Monitor/Topology/BusTopology.cs` (add `TopologyDevice.FromLearned`)
- Modify: `src/OpenEC.Monitor/Learning/EniSynthesizer.cs`
- Modify: `src/OpenEC.Monitor/Learning/EniXmlWriter.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/TopologyExportTests.cs`

**Interfaces:**
- Consumes: `LearnedSlave.Ports`, `LearnedSlave.Counters`, `LearnedSlave.ActiveDownstreamPorts`, `TopologyReconstructor.Reconstruct`, `EniPreviousPort`.
- Produces: `static TopologyDevice TopologyDevice.FromLearned(LearnedSlave)`; `EniSynthesizer` populates `EniSlave.PreviousPort` from the reconstructed topology; `EniXmlWriter` emits `<PreviousPort>`.

Because `EniXmlWriter` is also the learned-bus cache format, this one change makes topology survive a cache round-trip and appear in `Save learned ENI…` output.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/TopologyExportTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyExportTests
{
    /// <summary>A three-slave line learned from register traffic: address assignments give ring
    /// order, DL-status reads give the ports. 1001 and 1002 forward on port 1; 1003 ends the line.
    /// </summary>
    private static LearnedBus LearnLine()
    {
        var bus = new LearnedBus();
        ushort[] stations = [1001, 1002, 1003];
        for (var position = 0; position < stations.Length; position++)
            bus.Observe(DateTimeOffset.UnixEpoch,
                new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                    (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                    BitConverter.GetBytes(stations[position]), 1),
                FrameDirection.Outbound);

        // Link+open loop on ports 0 and 1 = 0x0030; port 0 only = 0x0010.
        foreach (var (station, raw) in new (ushort, ushort)[]
                 { (1001, 0x0030), (1002, 0x0030), (1003, 0x0010) })
            bus.Observe(DateTimeOffset.UnixEpoch,
                new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                    false, false, 0, BitConverter.GetBytes(raw), 1),
                FrameDirection.Returning);
        return bus;
    }

    [Fact]
    public void A_learned_slave_converts_to_a_topology_device()
    {
        var slave = LearnLine().Slaves.Single(s => s.StationAddress == 1001);

        var device = TopologyDevice.FromLearned(slave);

        Assert.Equal((ushort)1001, device.Address);
        Assert.Equal(0, device.RingPosition);
        Assert.True(device.HasPortData);
        Assert.Equal(new byte[] { 1 }, device.ActiveDownstreamPorts);
    }

    [Fact]
    public void The_synthesized_eni_carries_the_learned_topology()
    {
        var eni = EniSynthesizer.Synthesize(LearnLine(), new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());

        Assert.Null(Slave(eni, 1001).PreviousPort);                       // hangs off the master
        Assert.Equal(new EniPreviousPort(1001, 1), Slave(eni, 1002).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 1), Slave(eni, 1003).PreviousPort);
    }

    [Fact]
    public void Previous_port_round_trips_through_the_writer_and_the_parser()
    {
        var original = EniSynthesizer.Synthesize(LearnLine(),
            new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());
        var path = Path.Combine(Path.GetTempPath(), $"openec-topo-{Guid.NewGuid():N}.eni.xml");

        try
        {
            EniXmlWriter.Write(original, path);
            var reloaded = EniConfiguration.Load(path);

            foreach (var slave in original.Slaves)
                Assert.Equal(slave.PreviousPort, Slave(reloaded, slave.PhysAddr).PreviousPort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A bus learned without any DL-status read exports no topology at all, rather than a
    /// line the wire never showed.</summary>
    [Fact]
    public void A_bus_with_no_port_data_exports_no_previous_ports()
    {
        var bus = new LearnedBus();
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0, 0x0010_0000u, false, false, 0,
                BitConverter.GetBytes((ushort)1001), 1),
            FrameDirection.Outbound);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());

        Assert.All(eni.Slaves, s => Assert.Null(s.PreviousPort));
    }

    private static EniSlave Slave(EniConfiguration eni, ushort address) =>
        eni.Slaves.Single(s => s.PhysAddr == address);
}
```

**Before running:** confirm `EniSynthesizer.Synthesize`'s exact signature with
`grep -n "public static EniConfiguration Synthesize" src/OpenEC.Monitor/Learning/EniSynthesizer.cs`
and adjust the two call sites in this test to match it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyExportTests"`
Expected: FAIL — `TopologyDevice.FromLearned` does not exist.

- [ ] **Step 3: Add the conversion**

In `src/OpenEC.Monitor/Topology/BusTopology.cs`, add `using OpenEC.Monitor.Learning;` and add to `TopologyDevice`:

```csharp
    /// <summary>Projects a learned slave onto the reconstruction's input. Copies the fact
    /// dictionaries rather than aliasing them: the learned slave stays live and mutable under the
    /// capture pump, and reconstruction must see a stable snapshot.</summary>
    public static TopologyDevice FromLearned(LearnedSlave slave) => new(
        slave.StationAddress,
        slave.RingPosition,
        new Dictionary<byte, PortState>(slave.Ports),
        new Dictionary<byte, PortCounters>(slave.Counters));
```

- [ ] **Step 4: Carry topology into the synthesized ENI**

In `src/OpenEC.Monitor/Learning/EniSynthesizer.cs`, add `using OpenEC.Monitor.Topology;`, then reconstruct once and pass each slave's edge into `ToEniSlave`. Reconstruct before the `Slaves = ...` projection:

```csharp
        var topology = TopologyReconstructor.Reconstruct(
            slaves.Select(TopologyDevice.FromLearned).ToList());
```

Change the projection to pass it through:

```csharp
            Slaves = slaves.Select(s => ToEniSlave(s, schemas, topology)).ToList(),
```

and extend `ToEniSlave`:

```csharp
    private static EniSlave ToEniSlave(LearnedSlave slave,
        IReadOnlyDictionary<ushort, EsiDevice> schemas, BusTopology topology)
    {
        schemas.TryGetValue(slave.StationAddress, out var schema);
        return new EniSlave(
            slave.DisplayName(schema),
            slave.StationAddress,
            slave.RingPosition >= 0 ? (ushort)(0 - slave.RingPosition) : (ushort)0,
            slave.VendorId ?? 0,
            slave.ProductCode ?? 0,
            slave.Revision ?? 0,
            // ENI is written from the MASTER's perspective: <Send> is where the master sends, i.e.
            // SM0 (the slave's MBoxOut window), and <Recv> is where it reads, SM1. The repo's own
            // sample.eni.xml encodes this — <Send> is 0x1000 there — and EniConfigurationTests
            // asserts it. Getting it backwards mislabels every mailbox window in the export.
            slave.MailboxRange(0),
            slave.MailboxRange(1),
            PreviousPortOf(slave.StationAddress, topology));
    }

    /// <summary>The learned upstream edge, or null when there is none to declare. Only a
    /// wire-derived edge is exported: an inferred ring-order edge is not a fact about the
    /// topology, and writing it would make the exported ENI assert a wiring nobody observed.</summary>
    private static EniPreviousPort? PreviousPortOf(ushort address, BusTopology topology)
    {
        if (topology.Find(address) is not { EdgeSource: TopologyEdgeSource.Wire } node) return null;
        if (node.ParentAddress is not { } parent || parent == BusTopology.MasterAddress) return null;
        return node.ParentPort is { } port ? new EniPreviousPort(parent, port) : null;
    }
```

- [ ] **Step 5: Emit it**

In `src/OpenEC.Monitor/Learning/EniXmlWriter.cs`, inside `Slave(EniSlave slave)`, after the mailbox block is added:

```csharp
        // Written as a numeric port rather than a letter: the letter mapping is unverified
        // (topology design spec §10), and the parser accepts both, so the export stays readable
        // by our own loader without asserting a mapping we have not confirmed.
        if (slave.PreviousPort is { } previous)
            element.Add(new XElement("PreviousPort",
                new XElement("PhysAddr", previous.PhysAddr),
                new XElement("Port", previous.Port)));
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyExportTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. `EniXmlWriterTests`, `EniSynthesizerTests` and `LearnedBusCacheTests` exercise this path — a break there means the new element or the extra `ToEniSlave` argument disturbed an existing assertion.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Monitor/Topology/BusTopology.cs \
        src/OpenEC.Monitor/Learning/EniSynthesizer.cs \
        src/OpenEC.Monitor/Learning/EniXmlWriter.cs \
        tests/OpenEC.Monitor.Tests/Topology/TopologyExportTests.cs
git commit -m "feat(topology): export learned topology as ENI PreviousPort"
```

---

## Task 8: Synthetic fixtures with port traffic

**Files:**
- Modify: `src/OpenEC.Monitor/Synthesis/BringupCapture.cs`
- Create: `src/OpenEC.Monitor/Synthesis/BranchedBusCapture.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/BranchedBusCaptureTests.cs`
- Modify: `tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs` (one added fact)

**Interfaces:**
- Consumes: `EtherCatFrameBuilder` (`AddPhysical(cmd, idx, adp, ado, payload, wkc)`, `AddDatagram`, `AsReturning`, `Build`), `PcapFileWriter.Write`, `LearnedBus`, `TopologyReconstructor`.
- Produces: `BranchedBusCapture.Frames(int cycles = 20)` and `BranchedBusCapture.Write(string path, int cycles = 20)`, matching `BringupCapture`'s shape. `BringupCapture` gains DL-status and counter reads for its existing two stations.

**Shape of the branched fixture:** four slaves. 1001 is a junction with ports 1 and 2 active; 1002 forwards on port 1 to 1003, which ends that line; 1004 hangs off 1001's port 2. That gives a main line, a branch, and a second branch off the same junction — enough to exercise every path in the reconstruction end to end.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/BranchedBusCaptureTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class BranchedBusCaptureTests
{
    private static LearnedBus Learn(IEnumerable<(DateTimeOffset Timestamp, byte[] Frame)> frames)
    {
        var bus = new LearnedBus();
        var direction = new DirectionTracker();
        foreach (var (timestamp, frame) in frames)
        {
            if (EtherCatFrameParser.Parse(frame) is not FrameDecodeResult.Success ok) continue;
            var dir = direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                bus.Observe(timestamp, datagram, dir);
        }
        return bus;
    }

    [Fact]
    public void The_branched_capture_learns_four_slaves_in_ring_order()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        Assert.Equal([1001, 1002, 1003, 1004], bus.Slaves.Select(s => s.StationAddress));
    }

    [Fact]
    public void The_branched_capture_reconstructs_the_expected_tree()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        var topology = TopologyReconstructor.Reconstruct(
            bus.Slaves.Select(TopologyDevice.FromLearned).ToList());

        Assert.True(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
    }

    [Fact]
    public void The_branched_capture_carries_error_counters()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        var junction = bus.Slaves.Single(s => s.StationAddress == 1001);
        Assert.True(junction.Counters[0].AnyKnown);
    }

    [Fact]
    public void The_branched_capture_writes_a_readable_pcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-branched-{Guid.NewGuid():N}.pcap");
        try
        {
            BranchedBusCapture.Write(path, cycles: 3);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

Append to `tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs`:

```csharp
    /// <summary>The two-slave bringup is a LINE: 1001 forwards on port 1, 1002 ends it. Added so
    /// the topology facts have end-to-end coverage on the fixture every other learning test uses.
    /// </summary>
    [Fact]
    public void The_bringup_capture_carries_dl_status_for_its_line()
    {
        var bus = new LearnedBus();
        var direction = new DirectionTracker();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
        {
            if (EtherCatFrameParser.Parse(frame) is not FrameDecodeResult.Success ok) continue;
            var dir = direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                bus.Observe(timestamp, datagram, dir);
        }

        var topology = OpenEC.Monitor.Topology.TopologyReconstructor.Reconstruct(
            bus.Slaves.Select(OpenEC.Monitor.Topology.TopologyDevice.FromLearned).ToList());

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
    }
```

Reconcile the `using` directives and the frame-decoding helper with whatever that file already has — it decodes frames in its first test, so reuse that shape rather than duplicating it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BranchedBusCaptureTests|FullyQualifiedName~BringupCaptureTests"`
Expected: FAIL — `BranchedBusCapture` does not exist, and the bringup capture has no DL-status traffic.

- [ ] **Step 3: Add port reads to `BringupCapture`**

In `src/OpenEC.Monitor/Synthesis/BringupCapture.cs`, add a shared emit helper next to the existing `EmitWrite` and call it after the SII identity block. The DL-status values: 1001 has link and an open loop on ports 0 and 1 (`0x0030`), 1002 on port 0 only (`0x0010`).

```csharp
        // --- INIT: DL status and error counters, as a master polls them for topology ---
        foreach (var (station, dlStatus) in new (ushort Station, ushort DlStatus)[]
                 { (Stations[0], 0x0030), (Stations[1], 0x0010) })
        {
            EmitRead(station, 0x0110, BitConverter.GetBytes(dlStatus));
            EmitRead(station, 0x0300, new byte[14]);   // 0x0300-0x030D, all counters clear
            EmitRead(station, 0x0310, new byte[4]);    // lost link per port
        }
```

with the helper (mirroring `EmitWrite`'s outbound/returning pairing, but a read carries no data outbound and the answer returning):

```csharp
    private static void EmitRead(ushort station, ushort register, byte[] answer) =>
        Emit(new EtherCatFrameBuilder()
                .AddPhysical(EtherCatCommand.Fprd, idx, station, register,
                    new byte[answer.Length], 0),
            new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Fprd, idx++, station, register, answer, 1));
```

`Emit` and `idx` are locals inside `Frames`, so `EmitRead` must be a local function declared alongside `EmitWrite`/`EmitSdo`. Match how those are declared in the file rather than making it a static method.

- [ ] **Step 4: Write the branched fixture**

Create `src/OpenEC.Monitor/Synthesis/BranchedBusCapture.cs`:

```csharp
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>A synthetic bringup for a BRANCHED four-slave bus, so the topology reconstruction is
/// testable end to end without hardware. Deliberately separate from
/// <see cref="BringupCapture"/>: that fixture's two-slave line is asserted by a dozen existing
/// tests, and widening it would change what they mean.
///
/// The shape, which exercises every path in the reconstruction:
/// <code>
///   master ── 1001 ── 1002 ── 1003        1001 is a junction: ports 1 and 2 both active
///               └──── 1004                1004 hangs off its port 2
/// </code>
/// Identity is the EL1008 ESI test fixture's, as in <see cref="BringupCapture"/>.</summary>
public static class BranchedBusCapture
{
    private const uint VendorId = 2;
    private const uint ProductCode = 0x03F03052;
    private const uint Revision = 0x00120000;

    private static readonly ushort[] Stations = [1001, 1002, 1003, 1004];

    /// <summary>DL status per station: link plus open loop on each active port.
    /// 1001: ports 0, 1, 2 → bits 4,5,6 = 0x0070. 1002: ports 0, 1 → 0x0030.
    /// 1003 and 1004 end their lines: port 0 only → 0x0010.</summary>
    private static readonly ushort[] DlStatus = [0x0070, 0x0030, 0x0010, 0x0010];

    public static string Write(string path, int cycles = 20)
    {
        PcapFileWriter.Write(path, Frames(cycles));
        return path;
    }

    public static IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> Frames(int cycles = 20)
    {
        var frames = new List<(DateTimeOffset, byte[])>();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        byte idx = 0;

        void Emit(EtherCatFrameBuilder outbound, EtherCatFrameBuilder returning)
        {
            frames.Add((t, outbound.Build()));
            frames.Add((t.AddMicroseconds(60), returning.Build()));
            t = t.AddMicroseconds(250);
        }

        void EmitRead(ushort station, ushort register, byte[] answer)
        {
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, register,
                        new byte[answer.Length], 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, register, answer, 1));
            idx++;
        }

        // --- INIT: assign station addresses by ring position ---
        for (var position = 0; position < Stations.Length; position++)
        {
            var autoInc = (ushort)(0 - position);
            var payload = BitConverter.GetBytes(Stations[position]);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Apwr, idx, autoInc, 0x0010, payload, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Apwr, idx, autoInc, 0x0010, payload, 1));
            idx++;
        }

        // --- INIT: identity out of SII ---
        foreach (var station in Stations)
        {
            foreach (var (word, value) in new (uint, uint)[]
                     { (0x0008, VendorId), (0x000A, ProductCode), (0x000C, Revision), (0x000E, 0) })
            {
                var request = new byte[6];
                BitConverter.GetBytes((ushort)0x0100).CopyTo(request, 0);
                BitConverter.GetBytes(word).CopyTo(request, 2);
                Emit(new EtherCatFrameBuilder()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 0),
                    new EtherCatFrameBuilder().AsReturning()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 1));
                idx++;
                EmitRead(station, 0x0508, BitConverter.GetBytes(value));
            }
        }

        // --- INIT: DL status and error counters ---
        for (var position = 0; position < Stations.Length; position++)
        {
            EmitRead(Stations[position], 0x0110, BitConverter.GetBytes(DlStatus[position]));
            EmitRead(Stations[position], 0x0300, new byte[14]);
            EmitRead(Stations[position], 0x0310, new byte[4]);
        }

        // --- OP: the broadcast AL status poll, so the capture has cyclic traffic ---
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            frames.Add((t, new EtherCatFrameBuilder()
                .AddPhysical(EtherCatCommand.Brd, idx, 0, 0x0130, new byte[2], 0)
                .Build()));
            frames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Brd, idx, 0, 0x0130, [0x08, 0x00], 4)
                .Build()));
            t = t.AddMicroseconds(250);
            idx++;
        }

        return frames;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BranchedBusCaptureTests|FullyQualifiedName~BringupCaptureTests"`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS. `MultiplePassesTests` compares `BringupCapture.Frames(...).Count` against frames read back from a written file, so both sides move together and the added reads are safe — but confirm that test still passes rather than assuming it.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Monitor/Synthesis/BringupCapture.cs \
        src/OpenEC.Monitor/Synthesis/BranchedBusCapture.cs \
        tests/OpenEC.Monitor.Tests/Topology/BranchedBusCaptureTests.cs \
        tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs
git commit -m "test(topology): synthesise port traffic and a branched bus fixture"
```

---

## Task 9: Track topology on the observer and raise change events

**Files:**
- Create: `src/OpenEC.Monitor/Topology/TopologyTracker.cs`
- Modify: `src/OpenEC.Monitor/Observation/MonitorEvents.cs`
- Modify: `src/OpenEC.Monitor/Observation/BusObserver.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/TopologyTrackerTests.cs`

**Interfaces:**
- Consumes: `BusModel` (`Slaves`, `GetOrAdd`, `TryMapAutoInc`), `RegisterDecoders.TryDlStatus` / `TryPortCounters` / `TryStationAddress`, `TopologyReconstructor.Reconstruct`, `EniConfiguration`.
- Produces:
  - `sealed record MonitorEvent.TopologyChanged(DateTimeOffset Timestamp, ushort Address, byte Port, PortLinkState OldState, PortLinkState NewState) : MonitorEvent`
  - `ConfigMismatchKind.Topology`
  - `sealed class TopologyTracker(BusModel model)` with `IEnumerable<MonitorEvent> Observe(DateTimeOffset, EtherCatDatagram, FrameDirection)`, `void Rebind(EniConfiguration?)`, `BusTopology Current { get; }`
  - `BusObserver.SnapshotTopology() → BusTopology`

**Why the tracker decodes for itself rather than reading the learner:** `BusObserver` is fed independently of `BusLearner`, and `--no-learn` switches the learner off entirely. Topology is *observation*, not learning, so it must survive that. This mirrors `SlaveStateTracker`, which decodes AL status itself rather than asking anyone.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Monitor.Tests/Topology/TopologyTrackerTests.cs`:

```csharp
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static EtherCatDatagram Assign(int ringPosition, ushort station) =>
        new(EtherCatCommand.Apwr, 0, (0x0010u << 16) | (ushort)(0 - ringPosition), false, false, 0,
            BitConverter.GetBytes(station), 1);

    private static EtherCatDatagram DlStatus(ushort station, ushort raw) =>
        new(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station, false, false, 0,
            BitConverter.GetBytes(raw), 1);

    private static TopologyTracker TrackerWithLine(BusModel model)
    {
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, Assign(1, 1002), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, DlStatus(1001, 0x0030), FrameDirection.Returning).ToList();
        tracker.Observe(T0, DlStatus(1002, 0x0010), FrameDirection.Returning).ToList();
        return tracker;
    }

    [Fact]
    public void The_tracker_reconstructs_from_traffic_alone()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);

        var topology = TrackerWithLine(model).Current;

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
    }

    [Fact]
    public void The_first_port_read_is_not_reported_as_a_change()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();

        var events = tracker.Observe(T0, DlStatus(1001, 0x0030), FrameDirection.Returning).ToList();

        Assert.Empty(events);   // learning a port's state for the first time is not a change
    }

    [Fact]
    public void A_link_dropping_raises_one_event_naming_the_port()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        // 1001 loses its downstream link: port 1 goes from Active to Dangling (loop still open).
        var events = tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0010),
            FrameDirection.Returning).ToList();

        var changed = Assert.Single(events.OfType<MonitorEvent.TopologyChanged>());
        Assert.Equal((ushort)1001, changed.Address);
        Assert.Equal((byte)1, changed.Port);
        Assert.Equal(PortLinkState.Active, changed.OldState);
        Assert.Equal(PortLinkState.Dangling, changed.NewState);
    }

    [Fact]
    public void An_unchanged_read_raises_nothing()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        Assert.Empty(tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0030),
            FrameDirection.Returning));
    }

    [Fact]
    public void A_change_rebuilds_the_topology()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0010), FrameDirection.Returning).ToList();

        // 1001 no longer forwards, so 1002 has nowhere to attach.
        Assert.Equal(new ushort[] { 1002 }, tracker.Current.Unplaced);
    }

    [Fact]
    public void Auto_increment_addressed_reads_resolve_to_the_station_address()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();

        // APRD to auto-inc 0, which the assignment above mapped to station 1001.
        tracker.Observe(T0, new EtherCatDatagram(EtherCatCommand.Aprd, 0, 0x0110_0000u,
            false, false, 0, BitConverter.GetBytes((ushort)0x0030), 1), FrameDirection.Returning)
            .ToList();

        Assert.NotNull(tracker.Current.Find(1001));
        Assert.True(tracker.Current.PortDataObserved);
    }

    [Fact]
    public void With_no_port_traffic_the_tracker_reports_ring_order_only()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, Assign(1, 1002), FrameDirection.Outbound).ToList();

        Assert.False(tracker.Current.PortDataObserved);
        Assert.Equal((ushort)1001, tracker.Current.Find(1002)!.ParentAddress);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyTrackerTests"`
Expected: FAIL — `TopologyTracker` does not exist.

- [ ] **Step 3: Add the event and the mismatch kind**

In `src/OpenEC.Monitor/Observation/MonitorEvents.cs`, add inside `MonitorEvent`:

```csharp
    /// <summary>A port's link state changed mid-session — a cable pulled or plugged, or a loop
    /// opening or closing. The map shows where; this says when.</summary>
    public sealed record TopologyChanged(DateTimeOffset Timestamp, ushort Address, byte Port,
        Topology.PortLinkState OldState, Topology.PortLinkState NewState) : MonitorEvent(Timestamp);
```

and extend the enum at the bottom of the file:

```csharp
public enum ConfigMismatchKind { SlaveMissing, SlaveUnexpected, Identity, ProcessImage, Topology }
```

- [ ] **Step 4: Write the tracker**

Create `src/OpenEC.Monitor/Topology/TopologyTracker.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Topology;

/// <summary>Accumulates port facts and keeps a current <see cref="BusTopology"/>, emitting an
/// event whenever a port's state actually changes. A sibling of
/// <see cref="SlaveStateTracker"/>: same constructor shape, same Observe signature, driven from
/// the same loop in <see cref="BusObserver"/>.
///
/// It decodes for itself rather than reading the learner, because the observer is fed
/// independently of <see cref="BusLearner"/> and `--no-learn` switches the learner off entirely.
/// Topology is observation, not learning, and must survive that.
///
/// Not thread-safe: <see cref="BusObserver"/> holds its lock across Observe, exactly as for the
/// other trackers.</summary>
public sealed class TopologyTracker(BusModel model)
{
    private readonly Dictionary<ushort, Dictionary<byte, PortState>> _ports = new();
    private readonly Dictionary<ushort, Dictionary<byte, PortCounters>> _counters = new();
    private readonly Dictionary<ushort, int> _ringPositions = new();
    private readonly Dictionary<ushort, ushort> _autoIncToStation = new();
    private EniConfiguration? _eni;
    private BusTopology? _current;

    public BusTopology Current => _current ??= Rebuild();

    /// <summary>Adopts a configuration's declared edges and its auto-increment addresses. Mirrors
    /// <c>WkcTracker.Rebind</c>: a learned configuration published mid-session replaces the
    /// previous declaration rather than being merged into it.</summary>
    public void Rebind(EniConfiguration? eni)
    {
        _eni = eni;
        _current = null;
    }

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (RegisterDecoders.TryStationAddress(d, dir) is { } assignment)
        {
            _autoIncToStation[assignment.AutoIncAddress] = assignment.StationAddress;
            _ringPositions[assignment.StationAddress] = assignment.RingPosition;
            _current = null;
            yield break;
        }

        if (RegisterDecoders.TryDlStatus(d, dir) is { } dlStatus)
        {
            if (Resolve(dlStatus.Slave) is not { } address) yield break;
            if (!_ports.TryGetValue(address, out var known))
                _ports[address] = known = new Dictionary<byte, PortState>();

            var changed = false;
            foreach (var (port, state) in dlStatus.Ports)
            {
                var previous = known.GetValueOrDefault(port);
                known[port] = state;
                if (previous is null) { changed = true; continue; }   // first read is not a change
                if (previous.State == state.State) continue;
                changed = true;
                yield return new MonitorEvent.TopologyChanged(ts, address, port,
                    previous.State, state.State);
            }
            if (changed) _current = null;
            yield break;
        }

        if (RegisterDecoders.TryPortCounters(d, dir) is { } counters)
        {
            if (Resolve(counters.Slave) is not { } address) yield break;
            if (!_counters.TryGetValue(address, out var known))
                _counters[address] = known = new Dictionary<byte, PortCounters>();
            foreach (var (port, value) in counters.Ports)
                known[port] = known.TryGetValue(port, out var existing) ? existing.Merge(value) : value;
            _current = null;   // counters ride along on the nodes, so the snapshot is stale
        }
    }

    /// <summary>Auto-increment addressing has no station address until the assignment that maps
    /// the two has been seen. Until then the fact cannot name its slave and is dropped rather
    /// than attributed to a guess — the same rule <see cref="LearnedBus"/> applies.</summary>
    private ushort? Resolve(SlaveRef slave)
    {
        if (!slave.IsAutoIncrement) return slave.Address;
        if (_autoIncToStation.TryGetValue(slave.Address, out var station)) return station;
        return model.TryMapAutoInc(slave.Address, out var seeded) ? seeded : null;
    }

    private BusTopology Rebuild()
    {
        var addresses = model.Slaves.Select(s => s.Address)
            .Union(_ports.Keys)
            .Union(_ringPositions.Keys)
            .ToList();

        var devices = addresses.Select(address => new TopologyDevice(
                address,
                RingPositionOf(address),
                _ports.TryGetValue(address, out var ports)
                    ? new Dictionary<byte, PortState>(ports)
                    : new Dictionary<byte, PortState>(),
                _counters.TryGetValue(address, out var counters)
                    ? new Dictionary<byte, PortCounters>(counters)
                    : new Dictionary<byte, PortCounters>()))
            .ToList();

        return TopologyReconstructor.Reconstruct(devices, _eni);
    }

    /// <summary>Ring position from the observed address assignment, falling back to the ENI's
    /// declared auto-increment address. Both encode the position the same way: auto-increment
    /// addresses count down from zero.</summary>
    private int RingPositionOf(ushort address)
    {
        if (_ringPositions.TryGetValue(address, out var observed)) return observed;
        var declared = _eni?.Slaves.FirstOrDefault(s => s.PhysAddr == address);
        return declared is null ? -1 : (ushort)(0 - declared.AutoIncAddr);
    }
}
```

- [ ] **Step 5: Wire it into `BusObserver`**

In `src/OpenEC.Monitor/Observation/BusObserver.cs`:

Add `using OpenEC.Monitor.Topology;`, a field beside `_states`:

```csharp
    private readonly TopologyTracker _topology;
```

construct it beside `_states` in the constructor:

```csharp
        _topology = new TopologyTracker(Bus);
        if (eni is not null) _topology.Rebind(eni);
```

raise its events in `ProcessFrame`, immediately after the `_states` loop:

```csharp
            foreach (var evt in _topology.Observe(ts, d, dir))
                Raise(evt);
```

rebind it in `ApplyConfiguration`, beside `_wkc.Rebind`:

```csharp
            _topology.Rebind(config.Configuration);
```

and add the snapshot accessor beside `SnapshotEvents`:

```csharp
    /// <summary>Thread-safe snapshot of the current topology. <see cref="BusTopology"/> and every
    /// type it holds are immutable records, so the returned value stays valid while
    /// <see cref="Process"/> continues on another thread.</summary>
    public BusTopology SnapshotTopology()
    {
        lock (_lock)
            return _topology.Current;
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~TopologyTrackerTests"`
Expected: PASS, 7 tests.

- [ ] **Step 7: Write the end-to-end observer test**

Append to `tests/OpenEC.Monitor.Tests/Topology/BranchedBusCaptureTests.cs`:

```csharp
    [Fact]
    public void The_observer_exposes_the_branched_topology_from_the_capture()
    {
        var observer = new BusObserver();
        foreach (var (timestamp, frame) in BranchedBusCapture.Frames(cycles: 3))
            observer.Process(timestamp, EtherCatFrameParser.Parse(frame));

        var topology = observer.SnapshotTopology();

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
    }
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS. A new event type flows into the shared event log, so watch `EventsViewModel` tests: the event's category is `"Other"` until Task 14, which is expected at this point but must not throw.

- [ ] **Step 9: Commit**

```bash
git add src/OpenEC.Monitor/Topology/TopologyTracker.cs \
        src/OpenEC.Monitor/Observation/MonitorEvents.cs \
        src/OpenEC.Monitor/Observation/BusObserver.cs \
        tests/OpenEC.Monitor.Tests/Topology/TopologyTrackerTests.cs \
        tests/OpenEC.Monitor.Tests/Topology/BranchedBusCaptureTests.cs
git commit -m "feat(topology): track topology on the observer and raise change events"
```

**Stage 1 is complete here.** The SDK learns topology, exports it, caches it, and reports changes — with no Inspector change at all. A good place to stop and review before starting the view.

---

# Stage 2 — The view

## Task 10: The layout engine

**Files:**
- Create: `src/OpenEC.Inspector/Topology/TopologyLayout.cs`
- Create: `src/OpenEC.Inspector/Topology/TopologyLayoutEngine.cs`
- Test: `tests/OpenEC.Inspector.Tests/Topology/TopologyLayoutEngineTests.cs`

**Interfaces:**
- Consumes: `BusTopology`, `TopologyNode`, `PortState`, `PortLinkState`, `TopologyEdgeSource`.
- Produces:
  - `enum TopologyBoxKind { Master, Device, Junction, LineEnd }`
  - `enum PortSide { Left, Right, Bottom }`
  - `sealed record TopologyPortMark(byte Port, PortSide Side, PortLinkState State, bool HasError, double X, double Y, double Width, double Height)`
  - `sealed record TopologyBox(ushort Address, int Row, double X, double Y, double Width, double Height, TopologyBoxKind Kind, bool IsWide, bool EdgeInferred, bool HasConflict, IReadOnlyList<TopologyPortMark> Ports)`
  - `sealed record TopologyWire(ushort FromAddress, ushort ToAddress, bool IsInferred, bool HasConflict, IReadOnlyList<TopologyPoint> Points)`, `readonly record struct TopologyPoint(double X, double Y)`
  - `sealed record TopologyLayout(IReadOnlyList<TopologyBox> Boxes, IReadOnlyList<TopologyWire> Wires, IReadOnlyList<ushort> Unplaced, bool PortDataObserved, double Width, double Height)` with `static readonly TopologyLayout Empty`
  - `static TopologyLayout TopologyLayoutEngine.Layout(BusTopology)`

**Deliberate deferral, recorded here because it differs from spec §6.** The spec describes a *synthesized, non-selectable* node for a branch point whose identity was never observed. No code path in stage 1 produces one: reconstruction places only devices the bus reported, and a real junction (EK1122, CU1128) is itself an addressable slave that arrives as an ordinary device. So this plan implements the junction *styling* (`TopologyBoxKind.Junction`, chosen by active downstream port count) and marks an inferred *edge* with a dashed wire, but adds no pseudo-node type that nothing constructs. If a future fact source reveals branch points without identities, the pseudo-node becomes a real requirement then.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Inspector.Tests/Topology/TopologyLayoutEngineTests.cs`:

```csharp
using OpenEC.Inspector.Topology;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyLayoutEngineTests
{
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, active, !active, active);
        }
        return new TopologyDevice(address, ringPosition, ports, new Dictionary<byte, PortCounters>());
    }

    private static TopologyLayout LayoutOf(params TopologyDevice[] devices) =>
        TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(devices));

    private static TopologyBox Box(TopologyLayout layout, ushort address) =>
        layout.Boxes.Single(b => b.Address == address);

    [Fact]
    public void A_line_lays_out_left_to_right_on_one_row()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1, 1), Device(1003, 2));

        Assert.All(layout.Boxes, b => Assert.Equal(0, b.Row));
        Assert.True(Box(layout, 1001).X < Box(layout, 1002).X);
        Assert.True(Box(layout, 1002).X < Box(layout, 1003).X);
        Assert.Equal(Box(layout, 1001).Y, Box(layout, 1002).Y);
    }

    [Fact]
    public void The_master_is_the_leftmost_box()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1));

        var master = Box(layout, BusTopology.MasterAddress);
        Assert.Equal(TopologyBoxKind.Master, master.Kind);
        Assert.All(layout.Boxes.Where(b => b.Address != BusTopology.MasterAddress),
            b => Assert.True(b.X > master.X));
    }

    [Fact]
    public void A_second_child_opens_a_new_row_beneath()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.Equal(0, Box(layout, 1002).Row);      // first child continues the parent's row
        Assert.Equal(1, Box(layout, 1003).Row);      // second child opens a new one
        Assert.True(Box(layout, 1003).Y > Box(layout, 1002).Y);
    }

    /// <summary>Nested branches are laid out depth first, so a branch's own sub-rows follow it
    /// rather than being interleaved with a later sibling's.</summary>
    [Fact]
    public void Nested_branches_get_successive_rows_depth_first()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1),
            Device(1002, 1, 1, 2),
            Device(1003, 2, 1),
            Device(1004, 3),
            Device(1005, 4));

        Assert.Equal(0, Box(layout, 1003).Row);      // continues 1002's row
        Assert.Equal(0, Box(layout, 1004).Row);
        Assert.Equal(1, Box(layout, 1005).Row);      // 1002's second branch
    }

    [Fact]
    public void No_two_boxes_overlap()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1, 2, 3), Device(1002, 1, 1), Device(1003, 2),
            Device(1004, 3), Device(1005, 4, 1), Device(1006, 5));

        foreach (var a in layout.Boxes)
        foreach (var b in layout.Boxes.Where(x => !ReferenceEquals(x, a)))
        {
            var overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width
                        && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
            Assert.False(overlaps, $"{a.Address} overlaps {b.Address}");
        }
    }

    [Fact]
    public void Structurally_significant_devices_are_wide_and_the_rest_are_narrow()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1, 2),   // junction
            Device(1002, 1, 1),      // plain mid-line
            Device(1003, 2),         // line end
            Device(1004, 3));        // line end of the second branch

        Assert.True(Box(layout, 1001).IsWide);
        Assert.False(Box(layout, 1002).IsWide);
        Assert.True(Box(layout, 1003).IsWide);
        Assert.True(Box(layout, 1004).IsWide);
        Assert.True(Box(layout, 1001).Width > Box(layout, 1002).Width);
    }

    [Fact]
    public void A_device_with_two_downstream_ports_is_a_junction()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.Equal(TopologyBoxKind.Junction, Box(layout, 1001).Kind);
        Assert.Equal(TopologyBoxKind.LineEnd, Box(layout, 1002).Kind);
    }

    [Fact]
    public void Port_marks_sit_on_the_side_their_index_dictates()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        var box = Box(layout, 1001);
        Assert.Equal(PortSide.Left, box.Ports.Single(p => p.Port == 0).Side);
        Assert.Equal(PortSide.Right, box.Ports.Single(p => p.Port == 1).Side);
        Assert.Equal(PortSide.Bottom, box.Ports.Single(p => p.Port == 2).Side);
    }

    [Fact]
    public void Unused_ports_get_no_mark()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1));

        // Ports 2 and 3 have no link and a closed loop.
        Assert.DoesNotContain(Box(layout, 1001).Ports, p => p.Port is 2 or 3);
    }

    [Fact]
    public void A_topology_with_no_port_data_draws_no_port_marks_at_all()
    {
        var blind = new TopologyDevice(1001, 0, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());
        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct([blind]));

        Assert.False(layout.PortDataObserved);
        Assert.All(layout.Boxes, b => Assert.Empty(b.Ports));
    }

    [Fact]
    public void Every_wire_ends_on_the_ports_it_claims()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        foreach (var wire in layout.Wires.Where(w => w.FromAddress != BusTopology.MasterAddress))
        {
            var from = Box(layout, wire.FromAddress);
            var to = Box(layout, wire.ToAddress);
            var first = wire.Points[0];
            var last = wire.Points[^1];

            // The wire leaves inside the parent's bounds and arrives at the child's left edge.
            Assert.InRange(first.X, from.X, from.X + from.Width);
            Assert.InRange(first.Y, from.Y, from.Y + from.Height);
            Assert.Equal(to.X, last.X, precision: 3);
        }
    }

    [Fact]
    public void A_same_row_wire_is_a_straight_segment_and_a_branch_wire_is_not()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        var sameRow = layout.Wires.Single(w => w.FromAddress == 1001 && w.ToAddress == 1002);
        var branch = layout.Wires.Single(w => w.FromAddress == 1001 && w.ToAddress == 1003);

        Assert.Equal(2, sameRow.Points.Count);
        Assert.True(branch.Points.Count > 2);
        Assert.All(branch.Points.Zip(branch.Points.Skip(1)),
            pair => Assert.True(pair.First.X == pair.Second.X || pair.First.Y == pair.Second.Y,
                "branch wires must route orthogonally"));
    }

    [Fact]
    public void An_inferred_edge_is_flagged_on_both_the_box_and_the_wire()
    {
        var blind = new TopologyDevice(1002, 1, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());
        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(
            [new TopologyDevice(1001, 0, new Dictionary<byte, PortState>(),
                new Dictionary<byte, PortCounters>()), blind]));

        Assert.True(Box(layout, 1002).EdgeInferred);
        Assert.True(layout.Wires.Single(w => w.ToAddress == 1002).IsInferred);
    }

    [Fact]
    public void The_canvas_extent_covers_every_box()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.All(layout.Boxes, b => Assert.True(b.X + b.Width <= layout.Width));
        Assert.All(layout.Boxes, b => Assert.True(b.Y + b.Height <= layout.Height));
    }

    /// <summary>Compared as a fingerprint, not with Assert.Equal on the records: a record whose
    /// members are lists compares those members by REFERENCE, so two separately built layouts are
    /// never equal however identical their contents.</summary>
    [Fact]
    public void Layout_is_deterministic()
    {
        TopologyDevice[] Devices() => [Device(1001, 0, 1, 2), Device(1002, 1, 1), Device(1003, 2)];

        static string Fingerprint(TopologyLayout layout) => string.Join(';',
            layout.Boxes.Select(b =>
                $"{b.Address}:{b.Row}:{b.X}:{b.Y}:{b.Width}:{b.Height}:{b.Kind}:{b.IsWide}:{b.HasConflict}:"
                + string.Join(',', b.Ports.Select(p => $"{p.Port}{p.Side}{p.State}{p.X}{p.Y}")))
            .Concat(layout.Wires.Select(w =>
                $"{w.FromAddress}>{w.ToAddress}:{w.IsInferred}:{w.HasConflict}:"
                + string.Join(',', w.Points.Select(pt => $"{pt.X}/{pt.Y}")))));

        Assert.Equal(
            Fingerprint(TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(Devices()))),
            Fingerprint(TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(Devices()))));
    }

    /// <summary>Spec §7: an edge the ENI and the wire describe differently is drawn as the wire
    /// has it AND marked, so the map itself shows where the file and the machine disagree.</summary>
    [Fact]
    public void A_conflicting_edge_is_marked_on_its_box_and_its_wire()
    {
        var eni = new OpenEC.Monitor.Eni.EniConfiguration
        {
            Slaves =
            [
                new OpenEC.Monitor.Eni.EniSlave("Slave 1001", 1001, 0, 0, 0, 0, null, null),
                new OpenEC.Monitor.Eni.EniSlave("Slave 1002", 1002, 0xFFFF, 0, 0, 0, null, null,
                    new OpenEC.Monitor.Eni.EniPreviousPort(1001, 2)),   // the wire will say port 1
            ],
            CyclicCommands = [],
            Variables = [],
        };

        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)], eni));

        Assert.True(Box(layout, 1002).HasConflict);
        Assert.True(layout.Wires.Single(w => w.ToAddress == 1002).HasConflict);
        Assert.False(Box(layout, 1001).HasConflict);
    }

    [Fact]
    public void Unplaced_devices_are_carried_through_rather_than_drawn()
    {
        var layout = LayoutOf(Device(1001, 0), Device(1002, 1));

        Assert.Equal(new ushort[] { 1002 }, layout.Unplaced);
        Assert.DoesNotContain(layout.Boxes, b => b.Address == 1002);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyLayoutEngineTests"`
Expected: FAIL — `TopologyLayout` does not exist.

- [ ] **Step 3: Write the geometry types**

Create `src/OpenEC.Inspector/Topology/TopologyLayout.cs`:

```csharp
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Topology;

public enum TopologyBoxKind
{
    Master,

    /// <summary>An ordinary in-line device: one upstream port, one downstream.</summary>
    Device,

    /// <summary>More than one active downstream port — a branch opens here.</summary>
    Junction,

    /// <summary>No active downstream port: the end of a line.</summary>
    LineEnd,
}

public enum PortSide { Left, Right, Bottom }

public readonly record struct TopologyPoint(double X, double Y);

/// <param name="HasError">True when a known counter on this port is non-zero. An unread counter
/// leaves this false without implying health — <see cref="PortCounters.AnyKnown"/> is what
/// separates "clean" from "unknown".</param>
public sealed record TopologyPortMark(byte Port, PortSide Side, PortLinkState State, bool HasError,
    double X, double Y, double Width, double Height);

/// <param name="EdgeInferred">True when this device's parent is a ring-order guess rather than an
/// observed or declared edge.</param>
/// <param name="HasConflict">True when the ENI declared a different parent or port for this device
/// than the wire showed. Spec §7: the wire's version is drawn, and the disagreement is marked.</param>
public sealed record TopologyBox(ushort Address, int Row, double X, double Y,
    double Width, double Height, TopologyBoxKind Kind, bool IsWide, bool EdgeInferred,
    bool HasConflict, IReadOnlyList<TopologyPortMark> Ports);

public sealed record TopologyWire(ushort FromAddress, ushort ToAddress, bool IsInferred,
    bool HasConflict, IReadOnlyList<TopologyPoint> Points);

public sealed record TopologyLayout(
    IReadOnlyList<TopologyBox> Boxes,
    IReadOnlyList<TopologyWire> Wires,
    IReadOnlyList<ushort> Unplaced,
    bool PortDataObserved,
    double Width,
    double Height)
{
    public static readonly TopologyLayout Empty =
        new([], [], [], PortDataObserved: false, Width: 0, Height: 0);
}
```

- [ ] **Step 4: Write the engine**

Create `src/OpenEC.Inspector/Topology/TopologyLayoutEngine.cs`:

```csharp
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Topology;

/// <summary>Turns a <see cref="BusTopology"/> into geometry. Pure and deterministic: the same
/// topology yields identical output, which is what both makes it testable and stops the map
/// jittering as facts arrive mid-session.</summary>
public static class TopologyLayoutEngine
{
    private const double BoxHeight = 44;
    private const double WideWidth = 52;
    private const double NarrowWidth = 16;
    private const double GapX = 8;
    private const double RowHeight = 96;
    private const double Margin = 20;
    private const double MarkThickness = 4;
    private const double MarkLength = 14;

    public static TopologyLayout Layout(BusTopology topology)
    {
        if (topology.Nodes.Count == 0) return TopologyLayout.Empty;

        var rows = AssignRows(topology);
        var boxes = new Dictionary<ushort, TopologyBox>();
        var order = new List<ushort>();
        var conflicted = topology.Conflicts.Select(c => c.Address).ToHashSet();

        // Rows are laid out in the order they were opened, so a parent always has a box by the
        // time its branch row needs to indent under it.
        foreach (var row in rows)
        {
            var x = row.Index == 0
                ? Margin
                : boxes[row.ParentAddress].X + WideWidth + GapX * 3;
            var y = Margin + row.Index * RowHeight;

            foreach (var node in row.Nodes)
            {
                var kind = KindOf(topology, node);
                var wide = IsWide(kind, node, isFirstInRow: ReferenceEquals(node, row.Nodes[0]));
                var width = wide ? WideWidth : NarrowWidth;
                var marks = topology.PortDataObserved ? Marks(node, width) : [];
                boxes[node.Address] = new TopologyBox(node.Address, row.Index, x, y,
                    width, BoxHeight, kind, wide, node.EdgeSource == TopologyEdgeSource.Inferred,
                    conflicted.Contains(node.Address), marks);
                order.Add(node.Address);
                x += width + GapX;
            }
        }

        var ordered = order.Select(address => boxes[address]).ToList();
        return new TopologyLayout(
            ordered,
            Wires(topology, boxes, conflicted),
            topology.Unplaced,
            topology.PortDataObserved,
            ordered.Max(b => b.X + b.Width) + Margin,
            ordered.Max(b => b.Y + b.Height) + Margin);
    }

    private sealed record Row(int Index, ushort ParentAddress, List<TopologyNode> Nodes);

    /// <summary>Depth-first row assignment. A node's FIRST child continues its row; every later
    /// child opens a new row directly beneath, so a branch's own sub-rows follow it rather than
    /// being interleaved with a later sibling's.</summary>
    private static List<Row> AssignRows(BusTopology topology)
    {
        var master = topology.Nodes.First(n => n.IsMaster);
        var rows = new List<Row> { new(0, master.Address, [master]) };
        Walk(master, rows[0]);
        return rows;

        void Walk(TopologyNode node, Row row)
        {
            var children = topology.ChildrenOf(node.Address)
                .OrderBy(c => c.ParentPort ?? byte.MaxValue)
                .ThenBy(c => c.RingPosition < 0 ? int.MaxValue : c.RingPosition)
                .ToList();

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var target = row;
                if (i > 0)
                {
                    target = new Row(rows.Count, node.Address, []);
                    rows.Add(target);
                }
                target.Nodes.Add(child);
                Walk(child, target);
            }
        }
    }

    private static TopologyBoxKind KindOf(BusTopology topology, TopologyNode node)
    {
        if (node.IsMaster) return TopologyBoxKind.Master;
        var downstream = topology.ChildrenOf(node.Address).Count();
        return downstream switch
        {
            0 => TopologyBoxKind.LineEnd,
            1 => TopologyBoxKind.Device,
            _ => TopologyBoxKind.Junction,
        };
    }

    /// <summary>Structurally significant devices are wide with a horizontal label; the rest are
    /// narrow with a rotated one. This approximates the reference tool's undocumented rule — see
    /// the design spec §10 — and is deliberately a single predicate so it is cheap to change once
    /// it has been seen rendered.</summary>
    private static bool IsWide(TopologyBoxKind kind, TopologyNode node, bool isFirstInRow) =>
        kind is TopologyBoxKind.Master or TopologyBoxKind.Junction or TopologyBoxKind.LineEnd
        || isFirstInRow
        || node.EdgeSource == TopologyEdgeSource.Inferred;

    /// <summary>Port marks. Port 0 sits on the left edge (upstream, toward the master), port 1 on
    /// the right (the line continuing), ports 2 and 3 beneath. Unused ports get no mark at all —
    /// an absent bar reads as "nothing here", which is exactly what Unused means.</summary>
    private static List<TopologyPortMark> Marks(TopologyNode node, double width)
    {
        var marks = new List<TopologyPortMark>();
        foreach (var (port, state) in node.Ports.OrderBy(kv => kv.Key))
        {
            if (state.State == PortLinkState.Unused) continue;
            var counters = node.Counters.GetValueOrDefault(port);
            var hasError = counters?.AnyError == true;
            marks.Add(port switch
            {
                0 => new TopologyPortMark(port, PortSide.Left, state.State, hasError,
                    -MarkThickness, (BoxHeight - MarkLength) / 2, MarkThickness, MarkLength),
                1 => new TopologyPortMark(port, PortSide.Right, state.State, hasError,
                    width, (BoxHeight - MarkLength) / 2, MarkThickness, MarkLength),
                _ => new TopologyPortMark(port, PortSide.Bottom, state.State, hasError,
                    port == 2 ? width / 2 - MarkLength - 2 : width / 2 + 2, BoxHeight,
                    MarkLength, MarkThickness),
            });
        }
        return marks;
    }

    /// <summary>One wire per edge. Same row: a straight segment from the parent's right edge to
    /// the child's left edge. Different row: orthogonal — down out of the parent, across at the
    /// child's centreline, into the child's left edge.</summary>
    private static List<TopologyWire> Wires(BusTopology topology,
        IReadOnlyDictionary<ushort, TopologyBox> boxes, IReadOnlySet<ushort> conflicted)
    {
        var wires = new List<TopologyWire>();
        foreach (var node in topology.Nodes.Where(n => !n.IsMaster))
        {
            if (node.ParentAddress is not { } parentAddress) continue;
            if (!boxes.TryGetValue(parentAddress, out var from)) continue;
            if (!boxes.TryGetValue(node.Address, out var to)) continue;

            var inferred = node.EdgeSource == TopologyEdgeSource.Inferred;
            var conflict = conflicted.Contains(node.Address);
            var toMid = to.Y + to.Height / 2;

            if (from.Row == to.Row)
            {
                wires.Add(new TopologyWire(parentAddress, node.Address, inferred, conflict,
                [
                    new TopologyPoint(from.X + from.Width, from.Y + from.Height / 2),
                    new TopologyPoint(to.X, toMid),
                ]));
                continue;
            }

            var exitX = from.X + from.Width / 2;
            wires.Add(new TopologyWire(parentAddress, node.Address, inferred, conflict,
            [
                new TopologyPoint(exitX, from.Y + from.Height),
                new TopologyPoint(exitX, toMid),
                new TopologyPoint(to.X, toMid),
            ]));
        }
        return wires;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyLayoutEngineTests"`
Expected: PASS, 17 tests. If `No_two_boxes_overlap` fails for a branch row, the indent in `Layout` is pushing a child row's first box under its parent's box — widen the indent constant rather than special-casing the test.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Inspector/Topology/TopologyLayout.cs \
        src/OpenEC.Inspector/Topology/TopologyLayoutEngine.cs \
        tests/OpenEC.Inspector.Tests/Topology/TopologyLayoutEngineTests.cs
git commit -m "feat(inspector): lay out the topology map as pure geometry"
```

---

## Task 11: The topology view model, sharing the explorer's selection

**Files:**
- Create: `src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs`
- Modify: `src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/Topology/TopologyViewModelTests.cs`

**Interfaces:**
- Consumes: `MonitorSession.Observer.SnapshotTopology()`, `TopologyLayoutEngine.Layout`, `ExplorerNode`/`SlaveNode`/`NetworkNode` (existing), `StatusDotMap.ForSlave` (existing), `IRefreshable`.
- Produces:
  - `sealed partial class TopologyBoxViewModel : ObservableObject` — `ExplorerNode Node { get; }`, `ushort Address { get; }`, and observable `Label`, `X`, `Y`, `Width`, `Height`, `Kind`, `IsWide`, `EdgeInferred`, `HasConflict`, `Dot`, `Ports`, `Tooltip`
  - `sealed partial class TopologyWireViewModel : ObservableObject` — `Points` (`IList<Avalonia.Point>`), `IsInferred`, `HasConflict`
  - `sealed partial class TopologyViewModel : ObservableObject, IRefreshable` — `ObservableCollection<TopologyBoxViewModel> Boxes`, `ObservableCollection<TopologyWireViewModel> Wires`, `double Zoom`, `double CanvasWidth`, `double CanvasHeight`, `string? Notice`, `bool HasUnplaced`, `IReadOnlyList<string> Unplaced`, `ExplorerNode? SelectedNode` (two-way to the explorer's), `void Refresh()`
- `ExplorerViewModel` gains `public TopologyViewModel Topology { get; }` and `[ObservableProperty] private int _selectedViewIndex;` (0 = Classic View, 1 = Topology View).

**The selection rule that makes this work:** `TopologyBoxViewModel.Node` holds the *same* `ExplorerNode` instance the tree row holds. `ExplorerViewModel` stays the sole owner of `SelectedNode`, so no new routing is needed and `MainWindowViewModel.OnNodeSelected` is untouched.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Inspector.Tests/Topology/TopologyViewModelTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyViewModelTests
{
    private static async Task<ExplorerViewModel> BranchedExplorerAsync()
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        return explorer;
    }

    [Fact]
    public async Task The_map_has_a_box_per_device_plus_the_master()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Contains(explorer.Topology.Boxes, b => b.Address == BusTopology.MasterAddress);
        foreach (ushort address in new ushort[] { 1001, 1002, 1003, 1004 })
            Assert.Contains(explorer.Topology.Boxes, b => b.Address == address);
    }

    /// <summary>The load-bearing invariant: a box carries the same node instance as its tree row,
    /// so selection is by identity and needs no extra routing.</summary>
    [Fact]
    public async Task A_box_holds_the_same_node_instance_as_its_tree_row()
    {
        var explorer = await BranchedExplorerAsync();

        var row = explorer.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1002);
        var box = explorer.Topology.Boxes.Single(b => b.Address == 1002);

        Assert.Same(row, box.Node);
    }

    [Fact]
    public async Task The_master_box_holds_the_root_node()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Same(explorer.Root,
            explorer.Topology.Boxes.Single(b => b.Address == BusTopology.MasterAddress).Node);
    }

    [Fact]
    public async Task Selecting_a_box_selects_that_node_on_the_explorer()
    {
        var explorer = await BranchedExplorerAsync();
        var box = explorer.Topology.Boxes.Single(b => b.Address == 1003);

        explorer.Topology.SelectedNode = box.Node;

        Assert.Same(box.Node, explorer.SelectedNode);
    }

    [Fact]
    public async Task Selecting_a_tree_row_is_reflected_on_the_map()
    {
        var explorer = await BranchedExplorerAsync();
        var row = explorer.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);

        explorer.SelectedNode = row;

        Assert.Same(row, explorer.Topology.SelectedNode);
    }

    /// <summary>Box instances must survive a tick, or selection and any future animation would be
    /// thrown away every refresh — the same row-reuse rule the tree follows.</summary>
    [Fact]
    public async Task Refreshing_an_unchanged_topology_reuses_the_box_instances()
    {
        var explorer = await BranchedExplorerAsync();
        var before = explorer.Topology.Boxes.ToList();

        explorer.Refresh();

        Assert.Equal(before.Count, explorer.Topology.Boxes.Count);
        Assert.All(before.Zip(explorer.Topology.Boxes), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public async Task A_session_with_no_port_data_shows_a_notice_and_no_port_marks()
    {
        var session = await TestSessions.RunFileSessionAsync();   // demo capture: no DL-status reads
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();

        Assert.NotNull(explorer.Topology.Notice);
        Assert.Contains("not observed", explorer.Topology.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.All(explorer.Topology.Boxes, b => Assert.Empty(b.Ports));
    }

    [Fact]
    public async Task A_session_with_port_data_shows_no_notice()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Null(explorer.Topology.Notice);
    }

    [Fact]
    public async Task The_canvas_extent_is_published_for_the_scroll_viewer()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.True(explorer.Topology.CanvasWidth > 0);
        Assert.True(explorer.Topology.CanvasHeight > 0);
    }

    [Fact]
    public async Task Classic_view_is_the_default_tab()
    {
        Assert.Equal(0, (await BranchedExplorerAsync()).SelectedViewIndex);
    }
}
```

Add the branched session helper to `tests/OpenEC.Inspector.Tests/TestSessions.cs`:

```csharp
    /// <summary>A completed session over the branched synthetic bus, so the observer has a real
    /// port-level topology to draw.</summary>
    public static async Task<MonitorSession> BranchedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-branched-{Guid.NewGuid():N}.pcap");
        BranchedBusCapture.Write(path, cycles: 5);
        var session = new MonitorSession(new SourceSpec.File(path));
        session.Start();
        await session.Completion;
        return session;
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewModelTests"`
Expected: FAIL — `ExplorerViewModel.Topology` does not exist.

- [ ] **Step 3: Confirm the two Avalonia shape property types before writing against them**

Run:

```bash
dotnet build src/OpenEC.Inspector 2>/dev/null >/dev/null
python3 - <<'EOF'
import subprocess
dll = "~/.nuget/packages/avalonia/11.3.2/ref/net6.0/Avalonia.Controls.dll"
out = subprocess.run(["strings", dll], capture_output=True, text=True).stdout
print([l for l in out.splitlines() if "StrokeDashArray" in l][:5])
EOF
```

`Polyline.Points` is an `IList<Point>` — there is **no** `Avalonia.Points` type, so declare the view
model property as `IList<Point>`. Confirm `Shape.StrokeDashArray`'s type the same way before writing
`DashConverter` in Task 12; if it is not `AvaloniaList<double>`, change that converter's return type
to match. A collection the shape cannot accept fails silently at runtime rather than at build time,
which is why this is checked rather than assumed.

- [ ] **Step 4: Write the view models**

Create `src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.Topology;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.ViewModels;

/// <summary>One box on the map. <see cref="Node"/> is the SAME instance the device tree holds, so
/// selecting a box and selecting its tree row are the same act.</summary>
public sealed partial class TopologyBoxViewModel : ObservableObject
{
    public TopologyBoxViewModel(ushort address, ExplorerNode node)
    {
        Address = address;
        Node = node;
    }

    public ushort Address { get; }
    public ExplorerNode Node { get; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private TopologyBoxKind _kind;
    [ObservableProperty] private bool _isWide;
    [ObservableProperty] private bool _edgeInferred;
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private StatusDot _dot;
    [ObservableProperty] private string _tooltip = "";
    [ObservableProperty] private IReadOnlyList<TopologyPortMark> _ports = [];
}

public sealed partial class TopologyWireViewModel : ObservableObject
{
    /// <summary>Typed as the interface <c>Polyline.Points</c> exposes, not a concrete collection:
    /// there is no `Avalonia.Points` type, and binding a list the shape cannot accept fails
    /// silently at runtime rather than at build time.</summary>
    [ObservableProperty] private IList<Point> _points = new List<Point>();
    [ObservableProperty] private bool _isInferred;
    [ObservableProperty] private bool _hasConflict;
}

/// <summary>The Topology View's model. Geometry is recomputed only when the topology's shape
/// changes; a tick that changes only AL state or counters mutates the existing box view models,
/// which is what keeps selection and instance identity stable (spec §5).</summary>
public sealed partial class TopologyViewModel : ObservableObject, IRefreshable
{
    private const string NoPortDataNotice =
        "Port topology not observed — devices are shown in ring order. "
        + "The master on this bus never read DL status (0x0110).";

    private readonly MonitorSession _session;
    private readonly Func<ushort, ExplorerNode?> _resolveNode;
    private readonly Action<ExplorerNode?> _select;
    private string? _shape;   // fingerprint of the last laid-out topology

    public TopologyViewModel(MonitorSession session, Func<ushort, ExplorerNode?> resolveNode,
        Action<ExplorerNode?> select)
    {
        _session = session;
        _resolveNode = resolveNode;
        _select = select;
    }

    public ObservableCollection<TopologyBoxViewModel> Boxes { get; } = [];
    public ObservableCollection<TopologyWireViewModel> Wires { get; } = [];

    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;
    [ObservableProperty] private string? _notice;

    /// <summary>A separate bool rather than binding IsVisible to <c>Unplaced.Count</c>: Avalonia
    /// does not convert an int to a bool, so the count binding would silently never show the
    /// panel — and the unplaced strip is exactly the surface that must not fail quietly.</summary>
    [ObservableProperty] private bool _hasUnplaced;
    [ObservableProperty] private IReadOnlyList<string> _unplaced = [];
    [ObservableProperty] private ExplorerNode? _selectedNode;

    partial void OnSelectedNodeChanged(ExplorerNode? value) => _select(value);

    /// <summary>Set by the explorer when the selection changed elsewhere. Distinct from the
    /// property setter so echoing a selection back does not re-enter the callback.</summary>
    internal void SyncSelection(ExplorerNode? node)
    {
        if (ReferenceEquals(SelectedNode, node)) return;
        SetProperty(ref _selectedNode, node, nameof(SelectedNode));
    }

    public void Refresh()
    {
        var topology = _session.Observer.SnapshotTopology();
        var layout = TopologyLayoutEngine.Layout(topology);

        // The fingerprint covers everything the geometry depends on. When it is unchanged the
        // boxes are updated in place, so instances — and therefore selection — survive.
        var shape = string.Join('|', layout.Boxes.Select(b =>
            $"{b.Address}:{b.Row}:{b.X}:{b.Y}:{b.Width}:{b.Kind}:{b.Ports.Count}:{b.HasConflict}"));
        if (shape != _shape)
        {
            _shape = shape;
            Rebuild(layout);
        }

        foreach (var box in Boxes) UpdateLive(box);
        Notice = layout.PortDataObserved ? null : NoPortDataNotice;
        Unplaced = layout.Unplaced.Select(a => $"Slave {a}").ToList();
        HasUnplaced = Unplaced.Count > 0;
        CanvasWidth = layout.Width;
        CanvasHeight = layout.Height;
    }

    private void Rebuild(TopologyLayout layout)
    {
        var existing = Boxes.ToDictionary(b => b.Address);
        Boxes.Clear();
        foreach (var geometry in layout.Boxes)
        {
            if (_resolveNode(geometry.Address) is not { } node) continue;
            var box = existing.TryGetValue(geometry.Address, out var reused) && ReferenceEquals(reused.Node, node)
                ? reused
                : new TopologyBoxViewModel(geometry.Address, node);
            box.X = geometry.X;
            box.Y = geometry.Y;
            box.Width = geometry.Width;
            box.Height = geometry.Height;
            box.Kind = geometry.Kind;
            box.IsWide = geometry.IsWide;
            box.EdgeInferred = geometry.EdgeInferred;
            box.HasConflict = geometry.HasConflict;
            box.Ports = geometry.Ports;
            Boxes.Add(box);
        }

        Wires.Clear();
        foreach (var wire in layout.Wires)
            Wires.Add(new TopologyWireViewModel
            {
                Points = wire.Points.Select(p => new Point(p.X, p.Y)).ToList(),
                IsInferred = wire.IsInferred,
                HasConflict = wire.HasConflict,
            });
    }

    /// <summary>Per-tick state: label, status dot and tooltip. No geometry is touched here.</summary>
    private void UpdateLive(TopologyBoxViewModel box)
    {
        if (box.Address == BusTopology.MasterAddress)
        {
            box.Label = "M1";
            box.Dot = StatusDotMap.ForSession(_session.State);
            box.Tooltip = _session.SourceDescription;
            return;
        }

        var status = _session.Observer.SnapshotSlaves().FirstOrDefault(s => s.Address == box.Address);
        box.Label = box.Address.ToString();
        box.Dot = status is null ? StatusDot.Idle : StatusDotMap.ForSlave(status);
        box.Tooltip = Tooltip(box, status?.DisplayName);
    }

    private static string Tooltip(TopologyBoxViewModel box, string? name)
    {
        var lines = new List<string> { name ?? $"Slave {box.Address}" };
        foreach (var port in box.Ports)
        {
            var errors = port.HasError ? " · errors" : "";
            lines.Add($"Port {port.Port}: {port.State}{errors}");
        }
        if (box.EdgeInferred) lines.Add("Connection inferred from ring order, not observed");
        if (box.HasConflict) lines.Add("The loaded ENI declares a different connection for this device");
        return string.Join('\n', lines);
    }
}
```

- [ ] **Step 5: Own it from the explorer**

In `src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs`:

Construct it after `Root`/`RootItems`, in the constructor:

```csharp
        Topology = new TopologyViewModel(session, ResolveNode, node => SelectedNode = node);
```

Add the members beside `Root`:

```csharp
    public TopologyViewModel Topology { get; }

    /// <summary>Which explorer view is showing: 0 = Classic View, 1 = Topology View.</summary>
    [ObservableProperty] private int _selectedViewIndex;

    /// <summary>Maps a topology address to the node the tree already holds — the master's
    /// stand-in address resolves to the root. Returning the tree's instance rather than a new node
    /// is what makes selection identity-based across both views.</summary>
    private ExplorerNode? ResolveNode(ushort address) =>
        address == OpenEC.Monitor.Topology.BusTopology.MasterAddress
            ? Root
            : Root.Children.OfType<SlaveNode>().FirstOrDefault(s => s.Address == address);
```

Echo selection into the map by extending the existing partial method:

```csharp
    partial void OnSelectedNodeChanged(ExplorerNode? value)
    {
        Topology.SyncSelection(value);
        _onSelected(value);
    }
```

And refresh the map at the end of the existing `Refresh()`, after the slave rows and the process-image node are updated — the map resolves nodes from the tree, so the tree must be current first:

```csharp
        Topology.Refresh();
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewModelTests"`
Expected: PASS, 10 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. `ExplorerViewModel` now refreshes the map on every tick, so watch the existing explorer tests for cost or ordering surprises.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels/TopologyViewModel.cs \
        src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs \
        tests/OpenEC.Inspector.Tests/TestSessions.cs \
        tests/OpenEC.Inspector.Tests/Topology/TopologyViewModelTests.cs
git commit -m "feat(inspector): add the topology view model sharing explorer selection"
```

---

## Task 12: The map view and the tabbed explorer pane

**Files:**
- Create: `src/OpenEC.Inspector/Views/TopologyView.axaml` and `TopologyView.axaml.cs`
- Create: `src/OpenEC.Inspector/Views/PortMarkBrushConverter.cs`
- Modify: `src/OpenEC.Inspector/Views/ExplorerView.axaml`
- Modify: `src/OpenEC.Inspector/Theme/Palette.axaml` (two brushes)
- Test: `tests/OpenEC.Inspector.Tests/Ui/TopologyViewSmokeTests.cs`

**Interfaces:**
- Consumes: `TopologyViewModel`, `TopologyBoxViewModel`, `TopologyWireViewModel`, `TopologyPortMark`, `PortLinkState`, `StatusDotBrushConverter` (existing pattern to copy).
- Produces: `PortMarkBrushConverter : IValueConverter` mapping `PortLinkState` → brush; `TopologyView` bound to `TopologyViewModel`; `ExplorerView` hosting a bottom-strip `TabControl` bound to `SelectedViewIndex`.

**Existing patterns to follow:** `StatusDotBrushConverter` for the converter shape, `DeviceEditorView.axaml` for `TabControl SelectedIndex="{Binding ...}"`, and `Theme/Controls.axaml`'s `TabItem` style, which already trims Fluent's oversized headers.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Inspector.Tests/Ui/TopologyViewSmokeTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector.Tests.Ui;

public class TopologyViewSmokeTests
{
    private static async Task<(Window Window, ExplorerViewModel Explorer)> ShowBranchedAsync(
        int viewIndex)
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        explorer.SelectedViewIndex = viewIndex;
        var window = new Window
        {
            Content = new ExplorerView { DataContext = explorer }, Width = 700, Height = 600,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, explorer);
    }

    [AvaloniaFact]
    public async Task The_explorer_pane_offers_both_views_by_name()
    {
        var (window, _) = await ShowBranchedAsync(viewIndex: 0);

        var headers = window.GetVisualDescendants().OfType<TabItem>()
            .Select(t => t.Header?.ToString()).ToList();

        Assert.Contains("Classic View", headers);
        Assert.Contains("Topology View", headers);
    }

    [AvaloniaFact]
    public async Task The_topology_tab_renders_a_box_per_device_and_a_wire_per_edge()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);

        var boxes = window.GetVisualDescendants()
            .Where(v => v is Control { DataContext: TopologyBoxViewModel }).ToList();
        var wires = window.GetVisualDescendants().OfType<Polyline>().ToList();

        Assert.Equal(explorer.Topology.Boxes.Count, boxes.Count);
        Assert.Equal(explorer.Topology.Wires.Count, wires.Count);
    }

    /// <summary>The point of the whole feature: clicking a box drives the same selection a tree
    /// row does.</summary>
    [AvaloniaFact]
    public async Task Clicking_a_box_selects_that_node_on_the_explorer()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);
        var target = explorer.Topology.Boxes.Single(b => b.Address == 1003);
        var control = window.GetVisualDescendants()
            .OfType<Control>()
            .First(c => ReferenceEquals(c.DataContext, target) && c is Border);

        var point = control.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(target.Node, explorer.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Switching_tabs_preserves_the_selection()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);
        var node = explorer.Topology.Boxes.Single(b => b.Address == 1002).Node;
        explorer.SelectedNode = node;

        explorer.SelectedViewIndex = 0;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        explorer.SelectedViewIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(node, explorer.SelectedNode);
        Assert.Same(node, explorer.Topology.SelectedNode);
    }

    /// <summary>The `Fail` brush key must exist for the conflict stroke to resolve. Asserted here
    /// rather than trusted, because a missing key silently paints grey — indistinguishable from a
    /// healthy edge.</summary>
    [AvaloniaFact]
    public async Task The_fault_brush_the_conflict_stroke_needs_is_defined()
    {
        await ShowBranchedAsync(viewIndex: 1);

        Assert.True(Application.Current!.TryFindResource("Fail", out var fail));
        Assert.NotNull(fail);
    }

    [AvaloniaFact]
    public async Task The_notice_is_shown_only_when_port_data_is_missing()
    {
        var session = await TestSessions.RunFileSessionAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        explorer.SelectedViewIndex = 1;
        var window = new Window
        {
            Content = new ExplorerView { DataContext = explorer }, Width = 700, Height = 600,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var notice = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains("not observed", StringComparison.OrdinalIgnoreCase)
                                 == true);
        Assert.NotNull(notice);
        Assert.True(notice!.IsVisible);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewSmokeTests"`
Expected: FAIL — there is no Topology View tab.

- [ ] **Step 3: Add the palette brushes**

In `src/OpenEC.Inspector/Theme/Palette.axaml`, add beside the existing status colours, in **both** the light and dark resource dictionaries so the map tracks the OS theme like the rest of the app:

```xml
    <SolidColorBrush x:Key="PortBlocked" Color="#D0342C" />
    <SolidColorBrush x:Key="PortDangling" Color="#E8A33D" />
```

Reuse the existing `Ok` brush for `Active` and `Line` for the wires rather than adding near-duplicates — check the existing keys with
`grep -n 'x:Key' src/OpenEC.Inspector/Theme/Palette.axaml` and match the names actually present.

- [ ] **Step 4: Write the converter**

Create `src/OpenEC.Inspector/Views/PortMarkBrushConverter.cs`, mirroring `StatusDotBrushConverter`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Views;

/// <summary>Colours a port bar by its link state. Unused ports are never drawn, so they have no
/// colour here — an unused port is the absence of a bar, not a grey one.</summary>
public sealed class PortMarkBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PortLinkState.Active => "Ok",
            PortLinkState.Blocked => "PortBlocked",
            PortLinkState.Dangling => "PortDangling",
            _ => "Line",
        };
        return Application.Current!.TryFindResource(key, out var brush)
            ? brush!
            : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

Check `StatusDotBrushConverter` first: if it resolves brushes a different way (e.g. through `ThemeVariant`), copy that mechanism instead of introducing a second one.

- [ ] **Step 5: Write the map view**

Create `src/OpenEC.Inspector/Views/TopologyView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:OpenEC.Inspector.ViewModels"
             xmlns:views="using:OpenEC.Inspector.Views"
             x:Class="OpenEC.Inspector.Views.TopologyView">
  <UserControl.Resources>
    <views:StatusDotBrushConverter x:Key="DotBrush" />
    <views:PortMarkBrushConverter x:Key="PortBrush" />
  </UserControl.Resources>

  <DockPanel>
    <!-- Zoom selector and the honest-degradation notice share the bottom strip. -->
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Spacing="12"
                Margin="8,4" VerticalAlignment="Center">
      <!-- SelectedItem alone drives this: adding SelectedIndex as well would give the control two
           competing sources of selection. Zoom defaults to 1.0, which selects that item. -->
      <ComboBox Width="88" SelectedItem="{Binding Zoom, Mode=TwoWay}">
        <x:Double>0.5</x:Double>
        <x:Double>1.0</x:Double>
        <x:Double>1.5</x:Double>
        <x:Double>2.0</x:Double>
      </ComboBox>
      <TextBlock Classes="label" VerticalAlignment="Center"
                 Text="{Binding Notice}" TextWrapping="Wrap"
                 IsVisible="{Binding Notice, Converter={x:Static ObjectConverters.IsNotNull}}" />
    </StackPanel>

    <!-- Devices the port data could not place. Listed, never guessed onto the map. -->
    <Border DockPanel.Dock="Bottom" Classes="panel" Margin="8,0,8,4"
            IsVisible="{Binding HasUnplaced}">
      <StackPanel Spacing="4">
        <TextBlock Text="Not placed" FontWeight="SemiBold" />
        <ItemsControl ItemsSource="{Binding Unplaced}" />
      </StackPanel>
    </Border>

    <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
      <Canvas Width="{Binding CanvasWidth}" Height="{Binding CanvasHeight}"
              Background="Transparent">
        <Canvas.RenderTransform>
          <ScaleTransform ScaleX="{Binding Zoom}" ScaleY="{Binding Zoom}" />
        </Canvas.RenderTransform>

        <!-- Wires first so boxes paint over their endpoints. -->
        <ItemsControl ItemsSource="{Binding Wires}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><Canvas /></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate DataType="vm:TopologyWireViewModel">
              <!-- A conflicting edge is drawn where the WIRE says it is, in the fault colour,
                   so the map itself shows where the ENI and the machine disagree (spec §7). -->
              <Polyline Points="{Binding Points}" StrokeThickness="1.5"
                        Stroke="{Binding HasConflict,
                                 Converter={x:Static views:TopologyView.WireStrokeConverter}}"
                        StrokeDashArray="{Binding IsInferred,
                                          Converter={x:Static views:TopologyView.DashConverter}}" />
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>

        <ItemsControl ItemsSource="{Binding Boxes}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><Canvas /></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate DataType="vm:TopologyBoxViewModel">
              <Border Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}"
                      Width="{Binding Width}" Height="{Binding Height}"
                      Background="{DynamicResource Panel}"
                      BorderBrush="{Binding Dot, Converter={StaticResource DotBrush}}"
                      BorderThickness="2" CornerRadius="2"
                      ToolTip.Tip="{Binding Tooltip}">
                <Grid>
                  <TextBlock Text="{Binding Label}" FontSize="11"
                             HorizontalAlignment="Center" VerticalAlignment="Center"
                             IsVisible="{Binding IsWide}" />
                  <TextBlock Text="{Binding Label}" FontSize="9"
                             HorizontalAlignment="Center" VerticalAlignment="Center"
                             IsVisible="{Binding !IsWide}">
                    <TextBlock.RenderTransform>
                      <RotateTransform Angle="-90" />
                    </TextBlock.RenderTransform>
                  </TextBlock>

                  <ItemsControl ItemsSource="{Binding Ports}">
                    <ItemsControl.ItemsPanel>
                      <ItemsPanelTemplate><Canvas /></ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                      <DataTemplate>
                        <Rectangle Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}"
                                   Width="{Binding Width}" Height="{Binding Height}"
                                   Fill="{Binding State, Converter={StaticResource PortBrush}}" />
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
                </Grid>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </Canvas>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```

Create `src/OpenEC.Inspector/Views/TopologyView.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class TopologyView : UserControl
{
    /// <summary>Dashes an inferred edge. Inline rather than a separate file because it exists
    /// solely for this view's wire template.</summary>
    public static readonly IValueConverter DashConverter =
        new FuncValueConverter<bool, AvaloniaList<double>?>(
            inferred => inferred ? [3, 3] : null);

    /// <summary>Fault colour for an edge the ENI and the wire describe differently, the ordinary
    /// line colour otherwise. Resolved through the app's resources so it tracks the OS theme.</summary>
    public static readonly IValueConverter WireStrokeConverter =
        new FuncValueConverter<bool, IBrush?>(conflict =>
            Application.Current!.TryFindResource(conflict ? "Fail" : "Line", out var brush)
                ? brush as IBrush
                : Brushes.Gray);

    public TopologyView()
    {
        InitializeComponent();
        // Selection is handled here rather than with per-box buttons: a Button would bring its own
        // focus and press visuals, and the box's border already carries the device's status colour.
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TopologyViewModel model) return;
        if ((e.Source as Control)?.DataContext is not TopologyBoxViewModel box) return;
        model.SelectedNode = box.Node;
    }
}
```

- [ ] **Step 6: Make the explorer pane tabbed**

In `src/OpenEC.Inspector/Views/ExplorerView.axaml`, wrap the existing `TreeView` in a `TabControl`. The `Border`, the `UserControl.Resources` and the `TreeViewItem` style all stay exactly as they are — only the content nesting changes:

```xml
  <Border Background="{DynamicResource Panel}"
          BorderBrush="{DynamicResource Line}" BorderThickness="0,0,1,0">
    <TabControl TabStripPlacement="Bottom" SelectedIndex="{Binding SelectedViewIndex}">
      <TabItem Header="Classic View">
        <TreeView ItemsSource="{Binding RootItems}"
                  SelectedItem="{Binding SelectedNode, Mode=TwoWay}">
          <!-- the existing TreeView.DataTemplates block moves in here unchanged -->
        </TreeView>
      </TabItem>
      <TabItem Header="Topology View">
        <views:TopologyView DataContext="{Binding Topology}" />
      </TabItem>
    </TabControl>
  </Border>
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyViewSmokeTests"`
Expected: PASS, 6 tests. If `Clicking_a_box...` cannot find the `Border`, print what the descendants actually are before adjusting the test's control predicate — the visual tree under an `ItemsControl` with a `Canvas` panel wraps items in `ContentPresenter`s.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS. `ShellSmokeTests.Clicking_any_explorer_row_selects_that_node` walks the visual tree for `TreeViewItem`s and now has a `TabControl` above them — confirm it still finds them.

- [ ] **Step 9: Commit**

```bash
git add src/OpenEC.Inspector/Views/TopologyView.axaml \
        src/OpenEC.Inspector/Views/TopologyView.axaml.cs \
        src/OpenEC.Inspector/Views/PortMarkBrushConverter.cs \
        src/OpenEC.Inspector/Views/ExplorerView.axaml \
        src/OpenEC.Inspector/Theme/Palette.axaml \
        tests/OpenEC.Inspector.Tests/Ui/TopologyViewSmokeTests.cs
git commit -m "feat(inspector): render the topology map in a tabbed explorer pane"
```

---

## Task 13: A resizable explorer pane that remembers its width per view

**Files:**
- Modify: `src/OpenEC.Inspector/Views/MainWindow.axaml`
- Modify: `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`
- Test: `tests/OpenEC.Inspector.Tests/Ui/ExplorerPaneWidthTests.cs`

**Interfaces:**
- Consumes: `ExplorerViewModel.SelectedViewIndex`.
- Produces: `MainWindowViewModel.ExplorerWidth` (`double`, observable) and `MainWindowViewModel.OnExplorerViewChanged(int viewIndex)`; `MainWindow.axaml` column definitions `"Auto,Auto,*"` with the first column bound through a `GridLength`.

**Widths:** `ClassicPaneWidth = 280` (today's fixed value) and `TopologyPaneWidth = 620`. Switching views moves to the other view's remembered width; dragging the splitter updates whichever view is showing.

- [ ] **Step 1: Write the failing test**

Create `tests/OpenEC.Inspector.Tests/Ui/ExplorerPaneWidthTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.Ui;

public class ExplorerPaneWidthTests
{
    [Fact]
    public async Task The_pane_starts_at_the_classic_width()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new StubFilePicker());

        Assert.Equal(280, vm.ExplorerWidth);
    }

    [Fact]
    public async Task Switching_to_the_topology_view_widens_the_pane()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new StubFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;

        Assert.True(vm.ExplorerWidth > 280);
    }

    [Fact]
    public async Task Switching_back_restores_the_classic_width()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new StubFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;
        vm.Explorer.SelectedViewIndex = 0;

        Assert.Equal(280, vm.ExplorerWidth);
    }

    [Fact]
    public async Task A_dragged_width_is_remembered_per_view()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new StubFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;
        vm.ExplorerWidth = 800;                     // as a splitter drag would
        vm.Explorer.SelectedViewIndex = 0;
        vm.Explorer.SelectedViewIndex = 1;

        Assert.Equal(800, vm.ExplorerWidth);
    }
}
```

Reuse whatever stub file picker the existing Inspector tests already use — find it with
`grep -rn "IFilePicker" tests/OpenEC.Inspector.Tests` and use that type rather than adding another.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~ExplorerPaneWidthTests"`
Expected: FAIL — `MainWindowViewModel.ExplorerWidth` does not exist.

- [ ] **Step 3: Track the width**

In `src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs`, add the constants and state:

```csharp
    private const double ClassicPaneWidth = 280;
    private const double TopologyPaneWidth = 620;

    /// <summary>Remembered pane width per explorer view. The tree wants a narrow pane and the map
    /// wants a wide one, so a single width would make one of the two views useless on every switch.
    /// </summary>
    private readonly double[] _paneWidths = [ClassicPaneWidth, TopologyPaneWidth];
    private int _explorerView;

    [ObservableProperty] private double _explorerWidth = ClassicPaneWidth;

    partial void OnExplorerWidthChanged(double value) => _paneWidths[_explorerView] = value;

    private void OnExplorerViewChanged(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= _paneWidths.Length) return;
        _explorerView = viewIndex;
        ExplorerWidth = _paneWidths[viewIndex];
    }
```

Subscribe when the explorer is created, inside `OnSessionStarted` right after `Explorer = new ExplorerViewModel(...)`:

```csharp
        Explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.SelectedViewIndex))
                OnExplorerViewChanged(Explorer!.SelectedViewIndex);
        };
```

- [ ] **Step 4: Make the column resizable**

In `src/OpenEC.Inspector/Views/MainWindow.axaml`, replace `ColumnDefinitions="280,*"` and add the splitter:

```xml
      <Grid IsVisible="{Binding HasSession}">
        <Grid.ColumnDefinitions>
          <!-- MinWidth stops a drag from collapsing the pane to nothing, which would leave no
               handle to drag back. -->
          <ColumnDefinition Width="{Binding ExplorerWidth, Mode=TwoWay}" MinWidth="180" />
          <ColumnDefinition Width="Auto" />
          <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <views:ExplorerView DataContext="{Binding Explorer}" />
        <GridSplitter Grid.Column="1" Width="4" ResizeDirection="Columns"
                      Background="{DynamicResource Line}" />
        <Grid Grid.Column="2" RowDefinitions="*,Auto">
          <!-- Ambient DataContext here is still MainWindowViewModel: DataContext overrides on
               ExplorerView/EventsView are scoped to their own subtrees, not this sibling Grid. -->
          <ContentControl Content="{Binding CurrentPage}" Margin="12"
                          IsVisible="{Binding HasSession}" />
          <views:EventsView Grid.Row="1" DataContext="{Binding Events}" />
        </Grid>
      </Grid>
```

`ColumnDefinition.Width` is a `GridLength`, not a `double`. Bind it with a converter rather than changing the view model's type — a `double` property is what the tests and the splitter drag both want. Add to `MainWindow.axaml`'s resources:

```xml
    <views:GridLengthConverter x:Key="GridLength" />
```

use `Width="{Binding ExplorerWidth, Mode=TwoWay, Converter={StaticResource GridLength}}"`, and create `src/OpenEC.Inspector/Views/GridLengthConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace OpenEC.Inspector.Views;

/// <summary>Two-way bridge between a pixel width on a view model and a Grid column's
/// <see cref="GridLength"/>. Needed because a GridSplitter writes the column's GridLength back,
/// and the view model must stay a plain double for the width to be testable without a window.
/// </summary>
public sealed class GridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width ? new GridLength(width, GridUnitType.Pixel) : GridLength.Auto;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GridLength { IsAbsolute: true } length ? length.Value : 280d;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~ExplorerPaneWidthTests"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenEC.Inspector/Views/MainWindow.axaml \
        src/OpenEC.Inspector/Views/GridLengthConverter.cs \
        src/OpenEC.Inspector/ViewModels/MainWindowViewModel.cs \
        tests/OpenEC.Inspector.Tests/Ui/ExplorerPaneWidthTests.cs
git commit -m "feat(inspector): make the explorer pane resizable and width-aware per view"
```

---

## Task 14: Topology events and conflicts reach the messages panel

**Files:**
- Modify: `src/OpenEC.Inspector/ViewModels/EventFormatter.cs`
- Modify: `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`
- Modify: `src/OpenEC.Monitor/Topology/TopologyTracker.cs`
- Test: `tests/OpenEC.Inspector.Tests/Topology/TopologyEventTests.cs`
- Test: `tests/OpenEC.Monitor.Tests/Topology/TopologyConflictEventTests.cs`

**Interfaces:**
- Consumes: `MonitorEvent.TopologyChanged`, `ConfigMismatchKind.Topology`, `BusTopology.Conflicts`.
- Produces: `EventFormatter.Category` returns `"Topology"` for `TopologyChanged`; `EventFormatter.Describe` renders it; `EventsViewModel.CategoryNames` includes `"Topology"`; `TopologyTracker.Observe` yields one `MonitorEvent.ConfigMismatch` per newly appearing conflict.

**Why both halves are one task:** `EventsViewModel` filters by a hardcoded `CategoryNames` array. An event whose category is not in that array is filterable only under `"Other"` — or invisible, depending on the filter state. Adding a category to `EventFormatter` without adding it to `CategoryNames` is precisely the sort of half-wiring this repo has been bitten by, so the two move together and one test asserts the pairing.

- [ ] **Step 1: Write the failing tests**

Create `tests/OpenEC.Inspector.Tests/Topology/TopologyEventTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyEventTests
{
    private static readonly MonitorEvent.TopologyChanged LinkLost =
        new(DateTimeOffset.UnixEpoch, Address: 1013, Port: 1,
            PortLinkState.Active, PortLinkState.Dangling);

    [Fact]
    public void A_topology_change_is_its_own_category()
    {
        Assert.Equal("Topology", EventFormatter.Category(LinkLost));
    }

    /// <summary>The category must exist in the filter list, or the event is only reachable as
    /// "Other" — the exact half-wiring this pairing exists to prevent.</summary>
    [Fact]
    public async Task The_messages_panel_offers_a_topology_filter()
    {
        var events = new EventsViewModel(await TestSessions.BranchedAsync());

        Assert.Contains(events.Categories, c => c.Name == "Topology");
    }

    [Fact]
    public void A_link_loss_names_the_device_the_port_and_both_states()
    {
        var text = EventFormatter.Describe(LinkLost);

        Assert.Contains("1013", text);
        Assert.Contains("port 1", text);
        Assert.Contains("Active", text);
        Assert.Contains("Dangling", text);
    }

    [Fact]
    public void A_topology_config_mismatch_reads_as_a_disagreement()
    {
        var text = EventFormatter.Describe(new MonitorEvent.ConfigMismatch(
            DateTimeOffset.UnixEpoch, ConfigMismatchKind.Topology, 1002,
            Declared: "1001 port 2", Observed: "1001 port 1"));

        Assert.Contains("Topology", text);
        Assert.Contains("1001 port 2", text);
        Assert.Contains("1001 port 1", text);
    }

    [Fact]
    public async Task Every_formatter_category_is_a_filterable_category()
    {
        var events = new EventsViewModel(await TestSessions.BranchedAsync());
        MonitorEvent[] samples =
        [
            LinkLost,
            new MonitorEvent.ConfigMismatch(DateTimeOffset.UnixEpoch,
                ConfigMismatchKind.Topology, 1002, "a", "b"),
        ];

        foreach (var sample in samples)
            Assert.Contains(events.Categories, c => c.Name == EventFormatter.Category(sample));
    }
}
```

Check `EventsViewModel`'s public surface first — the collection of filters may not be named `Categories`. Run
`grep -n "public " src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`
and use the real member name in the three tests above.

Create `tests/OpenEC.Monitor.Tests/Topology/TopologyConflictEventTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyConflictEventTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static EniConfiguration EniClaimingPortTwo() => new()
    {
        Slaves =
        [
            new EniSlave("Slave 1001", 1001, 0, 0, 0, 0, null, null),
            new EniSlave("Slave 1002", 1002, 0xFFFF, 0, 0, 0, null, null,
                new EniPreviousPort(1001, 2)),
        ],
        CyclicCommands = [],
        Variables = [],
    };

    [Fact]
    public void A_wire_versus_eni_disagreement_is_raised_once()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Rebind(EniClaimingPortTwo());

        var events = new List<MonitorEvent>();
        foreach (var (position, station, raw) in new (int, ushort, ushort)[]
                 { (0, 1001, 0x0030), (1, 1002, 0x0010) })
        {
            events.AddRange(tracker.Observe(T0,
                new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                    (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                    BitConverter.GetBytes(station), 1),
                FrameDirection.Outbound));
            events.AddRange(tracker.Observe(T0,
                new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                    false, false, 0, BitConverter.GetBytes(raw), 1),
                FrameDirection.Returning));
        }

        var mismatch = Assert.Single(events.OfType<MonitorEvent.ConfigMismatch>());
        Assert.Equal(ConfigMismatchKind.Topology, mismatch.Kind);
        Assert.Equal((ushort)1002, mismatch.Address);
        Assert.Equal("1001 port 2", mismatch.Declared);
        Assert.Equal("1001 port 1", mismatch.Observed);
    }

    [Fact]
    public void The_same_conflict_is_not_raised_again_on_a_later_identical_read()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Rebind(EniClaimingPortTwo());

        void Assign(int position, ushort station) => tracker.Observe(T0,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                BitConverter.GetBytes(station), 1), FrameDirection.Outbound).ToList();

        IEnumerable<MonitorEvent> DlStatus(ushort station, ushort raw) => tracker.Observe(T0,
            new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                false, false, 0, BitConverter.GetBytes(raw), 1), FrameDirection.Returning);

        Assign(0, 1001);
        Assign(1, 1002);
        DlStatus(1001, 0x0030).ToList();
        DlStatus(1002, 0x0010).ToList();

        // A repeated, identical poll must not re-report a standing disagreement.
        var again = DlStatus(1001, 0x0030).Concat(DlStatus(1002, 0x0010)).ToList();

        Assert.Empty(again.OfType<MonitorEvent.ConfigMismatch>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TopologyEventTests|FullyQualifiedName~TopologyConflictEventTests"`
Expected: FAIL — the category is `"Other"` and no conflict is ever raised.

- [ ] **Step 3: Format the event**

In `src/OpenEC.Inspector/ViewModels/EventFormatter.cs`, add to `Category`:

```csharp
        MonitorEvent.TopologyChanged => "Topology",
```

and to `Describe`:

```csharp
        MonitorEvent.TopologyChanged t =>
            $"Slave {t.Address} port {t.Port}: {t.OldState} → {t.NewState}",
```

Both go **before** the `_ =>` fallback arm.

- [ ] **Step 4: Add the filter category**

In `src/OpenEC.Inspector/ViewModels/EventsViewModel.cs`, extend the array — `"Other"` stays last so it keeps reading as the catch-all:

```csharp
    private static readonly string[] CategoryNames =
        ["State", "State request", "WKC", "Emergency", "SoE", "Config", "Learning", "Topology", "Other"];
```

- [ ] **Step 5: Raise the conflicts**

In `src/OpenEC.Monitor/Topology/TopologyTracker.cs`, remember which conflicts have been reported and yield the new ones. Add the field:

```csharp
    private readonly HashSet<(ushort Address, string Declared, string Observed)> _reported = new();
```

Add a method that turns newly appearing conflicts into events:

```csharp
    /// <summary>New disagreements between the ENI and the wire, each reported once. A standing
    /// disagreement re-derived on every poll must not re-enter the message stream, or a healthy
    /// bus with one wiring difference would bury every other event.</summary>
    private IEnumerable<MonitorEvent> NewConflicts(DateTimeOffset ts)
    {
        foreach (var conflict in Current.Conflicts)
        {
            var key = (conflict.Address, conflict.Declared, conflict.Observed);
            if (!_reported.Add(key)) continue;
            yield return new MonitorEvent.ConfigMismatch(ts, ConfigMismatchKind.Topology,
                conflict.Address, conflict.Declared, conflict.Observed);
        }
    }
```

Then yield them wherever the topology was invalidated. In the DL-status branch, replace the trailing `if (changed) _current = null;` with:

```csharp
            if (changed)
            {
                _current = null;
                foreach (var conflict in NewConflicts(ts)) yield return conflict;
            }
```

and in the counters branch, after `_current = null;`:

```csharp
            foreach (var conflict in NewConflicts(ts)) yield return conflict;
```

Note that `NewConflicts` reads `Current`, which rebuilds the topology — so it must run *after* `_current` is cleared, not before.

Also clear the reported set in `Rebind`, since a newly published configuration is a new declaration to compare against:

```csharp
    public void Rebind(EniConfiguration? eni)
    {
        _eni = eni;
        _current = null;
        _reported.Clear();
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~TopologyEventTests|FullyQualifiedName~TopologyConflictEventTests"`
Expected: PASS, 7 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.Inspector/ViewModels/EventFormatter.cs \
        src/OpenEC.Inspector/ViewModels/EventsViewModel.cs \
        src/OpenEC.Monitor/Topology/TopologyTracker.cs \
        tests/OpenEC.Inspector.Tests/Topology/TopologyEventTests.cs \
        tests/OpenEC.Monitor.Tests/Topology/TopologyConflictEventTests.cs
git commit -m "feat(topology): stream topology changes and wiring disagreements to the log"
```

---

## Task 15: Document the feature and verify against hardware

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-19-inspector-topology-view-design.md` (record what verification found)
- Test: `tests/OpenEC.Inspector.Tests/Topology/TopologyDegradationTests.cs`

**Interfaces:** none new. This task closes the loop on spec §7 (degradation) and spec §10 (the unverified assumptions).

- [ ] **Step 1: Write the degradation tests**

Create `tests/OpenEC.Inspector.Tests/Topology/TopologyDegradationTests.cs`:

```csharp
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.Topology;

/// <summary>Spec §7: every shortfall in what the wire revealed has one defined rendering. These
/// are the states a real passive session lands in most often, so they get their own coverage
/// rather than being implied by the happy path.</summary>
public class TopologyDegradationTests
{
    private static async Task<TopologyViewModel> TopologyFor(Func<Task<Session.MonitorSession>> source)
    {
        var explorer = new ExplorerViewModel(await source(), assignment: null, _ => { });
        explorer.Refresh();
        return explorer.Topology;
    }

    [Fact]
    public async Task An_empty_capture_shows_only_the_master_and_no_crash()
    {
        var topology = await TopologyFor(TestSessions.EmptyAsync);

        Assert.Single(topology.Boxes);
        Assert.Empty(topology.Wires);
        Assert.Empty(topology.Unplaced);
    }

    [Fact]
    public async Task A_bus_with_no_port_reads_still_draws_every_device_in_ring_order()
    {
        var topology = await TopologyFor(TestSessions.BringupAsync);

        // BringupCapture now carries DL status, so this is the port-data path; the assertion is
        // that the devices are all present and connected regardless of which path produced them.
        Assert.Equal(3, topology.Boxes.Count);   // master + two slaves
        Assert.Equal(2, topology.Wires.Count);
    }

    [Fact]
    public async Task The_branched_bus_has_no_unplaced_devices()
    {
        var topology = await TopologyFor(TestSessions.BranchedAsync);

        Assert.Empty(topology.Unplaced);
        Assert.False(topology.HasUnplaced);
    }

    [Fact]
    public async Task Zoom_defaults_to_one_hundred_percent()
    {
        Assert.Equal(1.0, (await TopologyFor(TestSessions.BranchedAsync)).Zoom);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/OpenEC.Inspector.Tests --filter "FullyQualifiedName~TopologyDegradationTests"`
Expected: PASS, 4 tests. If `A_bus_with_no_port_reads...` finds a different box count, print `topology.Boxes.Select(b => b.Address)` and correct the expectation — `BringupCapture` is a two-slave line, so three boxes is the intent.

- [ ] **Step 3: Document the feature in the README**

In `README.md`, extend the `## 🔍 OpenEC.Inspector (GUI)` section's description of the explorer shell. Add after the sentence describing the device tree and tabbed editor:

```markdown
The explorer pane offers two views, switched from the tabs along its bottom edge.
**Classic View** is the device tree. **Topology View** draws the bus as a physical map —
the master, each device in its real position, junctions opening branches, and a bar per
port coloured by that port's link state: green forwarding, red for a link whose loop is
closed (cable in, frames not passing), amber for an open port with no partner. Clicking a
box selects the device exactly as clicking its tree row does, so the editor on the right
follows either view. Port state is read from DL status (`0x0110`) and the ESC error
counters (`0x0300`–`0x030D`, `0x0310`–`0x0313`) as the master polls them; on a bus whose
master never reads them the map falls back to ring order and says so, rather than drawing
ports it never saw. A loaded ENI's `<PreviousPort>` fills in edges the wire never showed,
and any disagreement between the two is reported in the messages panel.
```

Also add to the `## 📌 Status` list, following the existing milestone entries' style:

```markdown
- **Milestone 4**: the Topology View — port-level physical network map in the Inspector,
  fed by DL-status and ESC error-counter facts learned passively from the wire, with
  topology changes streamed to the messages panel.
```

- [ ] **Step 4: Run the full suite and the app**

Run: `dotnet test`
Expected: PASS.

Run: `dotnet run --project src/OpenEC.Inspector`
Then: generate a branched capture to open — `dotnet run --project src/OpenEC.CLI -- gen-sample /tmp/branched.pcap --bringup` gives the two-slave line; for the branched shape add a CLI path or open a capture written by `BranchedBusCapture.Write` from a scratch test. Confirm by eye: the tabs read Classic View / Topology View, the map draws, the pane widens on switching, clicking a box opens that device's editor, and the port bars appear.

- [ ] **Step 5: Verify the §10 assumptions against hardware**

With the ETAP-1000 in the segment and TwinCAT bringing the bus up, capture a session including a device with three active ports, then:

1. **DL status bit layout** — confirm the ports the map draws as active match the cables actually plugged in. A systematic mismatch means the link/loop bit positions are transposed.
2. **Forwarding order `0 → 3 → 1 → 2`** — confirm the branch order the map shows matches the physical wiring. If the rows are swapped, change `TopologyReconstructor.ForwardingOrder`, which is the only place it appears.
3. **Port letter mapping** — export the ENI from TwinCAT, load it alongside the same capture, and confirm no topology `ConfigMismatch` appears. A conflict on every branched device means `EniPreviousPort.ParsePort`'s letter mapping is wrong.
4. **Wide/narrow box rule** — compare against EC-Inspector on the same bus and adjust `TopologyLayoutEngine.IsWide` if the reference draws a different set wide.

- [ ] **Step 6: Record the findings in the spec**

Update §10 of `docs/superpowers/specs/2026-08-19-inspector-topology-view-design.md`: for each assumption, replace "to verify" with what the capture showed, and change any constant that turned out wrong along with its decoder test. If hardware is not available yet, leave §10 as it stands — the assumptions are honestly labelled, which is the point of the section.

- [ ] **Step 7: Commit**

```bash
git add README.md \
        docs/superpowers/specs/2026-08-19-inspector-topology-view-design.md \
        tests/OpenEC.Inspector.Tests/Topology/TopologyDegradationTests.cs
git commit -m "docs(topology): document the topology view and its verification status"
```

---

## Done

Both stages complete: the SDK learns, exports and caches port-level topology and reports its
changes; the Inspector draws it in a tabbed, resizable explorer pane where selecting a box is
the same act as selecting a tree row.
