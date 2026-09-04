# Learning Mode — Core Learner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reconstruct an `EniConfiguration` from passively observed EtherCAT startup traffic and export it as real ENI XML, delivered end-to-end through a new `openec learn` command.

**Architecture:** Pure per-datagram decoders emit immutable facts; a single stateful `LearnedBus` accumulates them and resolves auto-increment addressing; `EniSynthesizer` chains FMMU → SyncManager → PDO assignment → ESI schema to produce global process-image bit offsets; `EniXmlWriter` serialises the result. Nothing in this plan touches `BusObserver`, `ProcessImage`, or `WkcTracker` — integration is Plan 2.

**Tech Stack:** .NET 8, C# latest, xunit, `Dahlke.EtherCAT.Esi` 0.10.0, Spectre.Console.Cli.

**Spec:** `docs/superpowers/specs/2026-08-18-learning-mode-design.md`

## Global Constraints

- Target framework `net8.0`, `Nullable` enabled, `ImplicitUsings` enabled (`Directory.Build.props`).
- 100% passive. No code in this plan may transmit, inject, or otherwise write to a network interface.
- All new SDK types live in namespace `OpenEC.Monitor.Learning`, except the capture generator which lives in `OpenEC.Monitor.Synthesis`.
- Decoders are pure static functions over one `EtherCatDatagram`. All mutable state lives in `LearnedBus`.
- Tests are xunit, one test class per production type, in the mirrored path under `tests/OpenEC.Monitor.Tests/`.
- Run the full suite with `dotnet test`. Run one class with `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~<ClassName>"`.
- ESI `EsiPdoDirection` is from the **slave's** perspective: `Transmit` = slave sends = master **inputs**; `Receive` = slave receives = master **outputs**. The package documents this as the classic EtherCAT confusion — get it wrong and every variable lands on the wrong side.
- Auto-increment addresses count down from zero (`0x0000`, `0xFFFF`, `0xFFFE`, …), so ring position is the two's complement of the address.

---

### Task 1: Learned facts and slave references

**Files:**
- Create: `src/OpenEC.Monitor/Learning/LearnedFacts.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/SlaveRefTests.cs`

**Interfaces:**
- Consumes: `EtherCatDatagram`, `EtherCatCommand` from `OpenEC.Monitor.Protocol`.
- Produces: `SlaveRef`, `FmmuType`, and the fact records `StationAddressFact`, `SiiAddressFact`, `SiiDataFact`, `SyncManagerFact`, `FmmuFact`, `SdoValueFact`, `PdoMappingEntry`. Every later task depends on these exact names.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/SlaveRefTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class SlaveRefTests
{
    private static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, ushort ado) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, ReadOnlyMemory<byte>.Empty, 0);

    [Fact]
    public void Auto_increment_commands_are_flagged()
    {
        var re = SlaveRef.From(Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010));

        Assert.True(re.IsAutoIncrement);
        Assert.Equal(0xFFFF, re.Address);
    }

    [Fact]
    public void Fixed_address_commands_are_not_flagged()
    {
        var re = SlaveRef.From(Datagram(EtherCatCommand.Fpwr, 1001, 0x0600));

        Assert.False(re.IsAutoIncrement);
        Assert.Equal(1001, re.Address);
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0xFFFF, 1)]
    [InlineData(0xFFFE, 2)]
    [InlineData(0xFFFD, 3)]
    public void Ring_position_is_the_twos_complement_of_the_auto_increment_address(
        int autoInc, int expected)
    {
        var re = new SlaveRef((ushort)autoInc, IsAutoIncrement: true);

        Assert.Equal(expected, re.RingPosition);
    }

    [Fact]
    public void Ring_position_is_unknown_for_fixed_addressing()
    {
        Assert.Equal(-1, new SlaveRef(1001, IsAutoIncrement: false).RingPosition);
    }

    /// <summary>StationAddressFact.RingPosition is the property LearnedBus consumes to key
    /// every slave, so it is tested over the same boundary cases as SlaveRef's.</summary>
    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0xFFFF, 1)]
    [InlineData(0xFFFE, 2)]
    [InlineData(0xFFFD, 3)]
    public void Station_address_fact_reports_the_same_ring_position(int autoInc, int expected)
    {
        var fact = new StationAddressFact((ushort)autoInc, StationAddress: 1001);

        Assert.Equal(expected, fact.RingPosition);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~SlaveRefTests"`
Expected: FAIL — `The type or namespace name 'Learning' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/LearnedFacts.cs`:

```csharp
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>How a datagram addressed the slave it targeted. During INIT the master uses
/// auto-increment addressing before configured station addresses exist, so a fact cannot
/// name its slave until <see cref="LearnedBus"/> has seen the assignment that maps the
/// two. Carrying the addressing mode keeps the decoders pure.</summary>
public readonly record struct SlaveRef(ushort Address, bool IsAutoIncrement)
{
    public static SlaveRef From(EtherCatDatagram d) => new(d.Adp,
        d.Command is EtherCatCommand.Aprd or EtherCatCommand.Apwr
            or EtherCatCommand.Aprw or EtherCatCommand.Armw);

    /// <summary>Zero-based ring position, or -1 when this reference uses fixed addressing.
    /// Auto-increment addresses count down from zero, so the position is the two's
    /// complement of the address.</summary>
    public int RingPosition => IsAutoIncrement ? (ushort)(0 - Address) : -1;
}

/// <summary>FMMU direction, per the type byte at offset 11 of an FMMU register block.</summary>
public enum FmmuType : byte { None = 0, Inputs = 1, Outputs = 2 }

/// <summary>The master assigning a configured station address to a ring position (APWR 0x0010).</summary>
public sealed record StationAddressFact(ushort AutoIncAddress, ushort StationAddress)
{
    /// <summary>Delegates to <see cref="SlaveRef.RingPosition"/> rather than repeating the
    /// two's-complement arithmetic. This is the property <see cref="LearnedBus"/> actually
    /// consumes to key every slave, so it must not drift from the tested implementation.</summary>
    public int RingPosition => new SlaveRef(AutoIncAddress, IsAutoIncrement: true).RingPosition;
}

/// <summary>An SII/EEPROM address+command write (register 0x0502). The data arrives separately.</summary>
public sealed record SiiAddressFact(SlaveRef Slave, uint WordAddress, bool IsRead);

/// <summary>SII/EEPROM data returned at register 0x0508, answering the preceding address write.</summary>
public sealed record SiiDataFact(SlaveRef Slave, byte[] Data);

/// <summary>One SyncManager register block (8 bytes at 0x0800 + 8n).</summary>
public sealed record SyncManagerFact(SlaveRef Slave, byte Number, ushort PhysicalStart,
    ushort Length, byte Control, bool Enabled);

/// <summary>One FMMU register block (16 bytes at 0x0600 + 16n).</summary>
public sealed record FmmuFact(SlaveRef Slave, byte Number, uint LogicalStart, ushort Length,
    byte LogicalStartBit, byte LogicalStopBit, ushort PhysicalStart, byte PhysicalStartBit,
    FmmuType Type, bool Enabled);

/// <summary>One CoE SDO value, from a master download or a slave upload response.
/// Only expedited transfers are decoded; segmented transfers are ignored (spec §9).</summary>
public sealed record SdoValueFact(SlaveRef Slave, ushort Index, byte SubIndex, uint Value);

/// <summary>One entry of a PDO mapping object (0x16xx/0x1Axx), decoded from its 32-bit value.</summary>
public sealed record PdoMappingEntry(ushort Index, byte SubIndex, byte BitLength)
{
    public static PdoMappingEntry FromRaw(uint raw) =>
        new((ushort)(raw >> 16), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));

    /// <summary>ESI writes padding as index 0 with a bit length and no sub-index. Padding
    /// advances the offset but is not a variable.</summary>
    public bool IsPadding => Index == 0;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~SlaveRefTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/LearnedFacts.cs tests/OpenEC.Monitor.Tests/Learning/SlaveRefTests.cs
git commit -m "feat(learning): fact records and slave reference for bus discovery"
```

---

### Task 2: Station address and SII register decoders

**Files:**
- Create: `src/OpenEC.Monitor/Learning/RegisterDecoders.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs`

**Interfaces:**
- Consumes: Task 1's facts; `FrameDirection` from `OpenEC.Monitor.Observation`.
- Produces: `RegisterDecoders.TryStationAddress`, `.TrySiiAddress`, `.TrySiiData`, and the register constants `StationAddressRegister`, `SiiControlRegister`, `SiiDataRegister`, `SyncManagerBase`, `FmmuBase`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class RegisterDecoderTests
{
    internal static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc = 1) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Station_address_assignment_is_decoded_with_its_ring_position()
    {
        // APWR to auto-inc 0xFFFF (second slave), register 0x0010, assigning address 1002.
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, new byte[] { 0xEA, 0x03 });

        var fact = RegisterDecoders.TryStationAddress(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(1002, fact!.StationAddress);
        Assert.Equal(1, fact.RingPosition);
    }

    [Fact]
    public void Returning_station_address_writes_are_ignored()
    {
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, new byte[] { 0xEA, 0x03 });

        Assert.Null(RegisterDecoders.TryStationAddress(d, FrameDirection.Returning));
    }

    [Fact]
    public void Writes_to_other_registers_are_ignored()
    {
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0120, new byte[] { 0x02, 0x00 });

        Assert.Null(RegisterDecoders.TryStationAddress(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Sii_read_command_carries_the_word_address()
    {
        // Control 0x0100 (read), word address 0x00000008 (vendor id).
        var payload = new byte[] { 0x00, 0x01, 0x08, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0502, payload);

        var fact = RegisterDecoders.TrySiiAddress(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(8u, fact!.WordAddress);
        Assert.True(fact.IsRead);
        Assert.Equal(1001, fact.Slave.Address);
        Assert.False(fact.Slave.IsAutoIncrement);
    }

    [Fact]
    public void Sii_data_is_decoded_from_returning_reads_only()
    {
        var payload = new byte[] { 0x02, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fprd, 1001, 0x0508, payload);

        Assert.NotNull(RegisterDecoders.TrySiiData(d, FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TrySiiData(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Sii_data_with_zero_working_counter_is_ignored()
    {
        var payload = new byte[] { 0x02, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fprd, 1001, 0x0508, payload, wkc: 0);

        Assert.Null(RegisterDecoders.TrySiiData(d, FrameDirection.Returning));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RegisterDecoderTests"`
Expected: FAIL — `RegisterDecoders` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/RegisterDecoders.cs`:

```csharp
using System.Buffers.Binary;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Pure decoders turning one observed datagram into zero or more learned facts.
/// Register offsets are per ETG.1000.4; phase attribution is documented in the design spec.</summary>
public static class RegisterDecoders
{
    public const ushort StationAddressRegister = 0x0010;
    public const ushort SiiControlRegister = 0x0502;
    public const ushort SiiDataRegister = 0x0508;
    public const ushort SyncManagerBase = 0x0800;
    public const ushort FmmuBase = 0x0600;

    internal static bool IsWrite(EtherCatCommand cmd) =>
        cmd is EtherCatCommand.Fpwr or EtherCatCommand.Apwr or EtherCatCommand.Bwr;

    internal static bool IsRead(EtherCatCommand cmd) =>
        cmd is EtherCatCommand.Fprd or EtherCatCommand.Aprd or EtherCatCommand.Brd;

    /// <summary>APWR to 0x0010 — the master assigning a configured station address to the
    /// slave at an auto-increment position. The single richest datagram on the bus: it
    /// yields ring position and station address together.</summary>
    public static StationAddressFact? TryStationAddress(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound) return null;
        if (d.Command != EtherCatCommand.Apwr) return null;
        if (d.Ado != StationAddressRegister || d.Payload.Length < 2) return null;
        return new StationAddressFact(d.Adp,
            BinaryPrimitives.ReadUInt16LittleEndian(d.Payload.Span));
    }

    /// <summary>Write to 0x0502 — SII control (2 bytes) followed by the SII word address
    /// (4 bytes). Bit 8 of control requests a read.</summary>
    public static SiiAddressFact? TrySiiAddress(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return null;
        if (d.Ado != SiiControlRegister || d.Payload.Length < 6) return null;
        var span = d.Payload.Span;
        var control = BinaryPrimitives.ReadUInt16LittleEndian(span);
        return new SiiAddressFact(SlaveRef.From(d),
            BinaryPrimitives.ReadUInt32LittleEndian(span[2..]), (control & 0x0100) != 0);
    }

    /// <summary>Returning read of 0x0508 — the EEPROM data answering the preceding address
    /// write. A zero working counter means no slave answered, so the payload is meaningless.</summary>
    public static SiiDataFact? TrySiiData(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Ado != SiiDataRegister || d.Payload.Length < 2 || d.WorkingCounter == 0) return null;
        return new SiiDataFact(SlaveRef.From(d), d.Payload.ToArray());
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RegisterDecoderTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/RegisterDecoders.cs tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs
git commit -m "feat(learning): station-address and SII register decoders"
```

---

### Task 3: SyncManager and FMMU decoders

**Files:**
- Modify: `src/OpenEC.Monitor/Learning/RegisterDecoders.cs` (append two methods)
- Modify: `tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs` (append tests)

**Interfaces:**
- Produces: `RegisterDecoders.TrySyncManagers(EtherCatDatagram, FrameDirection) → IReadOnlyList<SyncManagerFact>` and `.TryFmmus(...) → IReadOnlyList<FmmuFact>`. Both return lists because masters write several consecutive register blocks in one datagram.

- [ ] **Step 1: Write the failing test**

Append to `tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs` (inside the class):

```csharp
    [Fact]
    public void Sync_manager_block_is_decoded()
    {
        // SM2: phys start 0x1100, length 6, control 0x64, status 0x00, activate 0x01, pdi 0x00
        var payload = new byte[] { 0x00, 0x11, 0x06, 0x00, 0x64, 0x00, 0x01, 0x00 };
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0810, payload);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        var sm = Assert.Single(facts);
        Assert.Equal(2, sm.Number);
        Assert.Equal(0x1100, sm.PhysicalStart);
        Assert.Equal(6, sm.Length);
        Assert.True(sm.Enabled);
    }

    [Fact]
    public void Consecutive_sync_manager_blocks_in_one_write_are_all_decoded()
    {
        var payload = new byte[16];
        // SM0 at 0x1000 len 128, enabled.
        payload[0] = 0x00; payload[1] = 0x10; payload[2] = 0x80; payload[6] = 0x01;
        // SM1 at 0x1080 len 128, enabled.
        payload[8] = 0x80; payload[9] = 0x10; payload[10] = 0x80; payload[14] = 0x01;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0800, payload);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        Assert.Equal(2, facts.Count);
        Assert.Equal(0, facts[0].Number);
        Assert.Equal(1, facts[1].Number);
        Assert.Equal(0x1080, facts[1].PhysicalStart);
    }

    [Fact]
    public void Fmmu_block_is_decoded()
    {
        var payload = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(payload, 0);  // logical start
        BitConverter.GetBytes((ushort)2).CopyTo(payload, 4);    // length
        payload[6] = 0;                                          // logical start bit
        payload[7] = 7;                                          // logical stop bit
        BitConverter.GetBytes((ushort)0x1100).CopyTo(payload, 8); // physical start
        payload[10] = 0;                                          // physical start bit
        payload[11] = 1;                                          // type: inputs
        payload[12] = 1;                                          // activate
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0600, payload);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        var fmmu = Assert.Single(facts);
        Assert.Equal(0, fmmu.Number);
        Assert.Equal(0x00010000u, fmmu.LogicalStart);
        Assert.Equal(2, fmmu.Length);
        Assert.Equal(0x1100, fmmu.PhysicalStart);
        Assert.Equal(FmmuType.Inputs, fmmu.Type);
        Assert.True(fmmu.Enabled);
    }

    [Fact]
    public void Fmmu_number_follows_the_register_offset()
    {
        var payload = new byte[16];
        payload[11] = 2;
        payload[12] = 1;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0610, payload);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        Assert.Equal(1, Assert.Single(facts).Number);
    }

    [Fact]
    public void Unaligned_register_offsets_decode_nothing()
    {
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0604, new byte[16]);

        Assert.Empty(RegisterDecoders.TryFmmus(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Partial_trailing_block_is_dropped()
    {
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0800, new byte[12]);

        Assert.Single(RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Sync_manager_blocks_past_the_last_real_block_are_not_fabricated()
    {
        // Starts at SM 15 (0x0800 + 15*8) with room for two blocks; only SM 15 exists.
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0878, new byte[16]);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        Assert.Equal(15, Assert.Single(facts).Number);
    }

    [Fact]
    public void Fmmu_blocks_past_the_last_real_block_are_not_fabricated()
    {
        // Starts at FMMU 15 (0x0600 + 15*16) with room for two blocks; only FMMU 15 exists.
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x06F0, new byte[32]);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        Assert.Equal(15, Assert.Single(facts).Number);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RegisterDecoderTests"`
Expected: FAIL — `TrySyncManagers` / `TryFmmus` do not exist.

- [ ] **Step 3: Write the implementation**

Append to `src/OpenEC.Monitor/Learning/RegisterDecoders.cs` (inside the class):

```csharp
    /// <summary>ETG.1000.4 defines 16 SyncManagers and 16 FMMUs per slave, numbered 0-15.</summary>
    private const int MaxRegisterBlocks = 16;

    /// <summary>Writes to 0x0800 + 8n. Layout per block: physical start (2), length (2),
    /// control (1), status (1), activate (1), PDI control (1). Bit 0 of activate enables.
    /// Masters configure several SyncManagers in one datagram, so this returns a list.
    /// The loop is bounded by block number as well as payload length: a write starting near
    /// the top of the window with a long payload must not fabricate a block 16 that no slave
    /// has, since a bogus SyncManager can later match a real FMMU by physical address.</summary>
    public static IReadOnlyList<SyncManagerFact> TrySyncManagers(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return [];
        if (d.Ado < SyncManagerBase || d.Ado >= SyncManagerBase + 8 * MaxRegisterBlocks) return [];
        var offset = d.Ado - SyncManagerBase;
        if (offset % 8 != 0) return [];
        var first = offset / 8;
        var span = d.Payload.Span;
        var facts = new List<SyncManagerFact>();
        for (var i = 0; i + 8 <= span.Length && first + i / 8 < MaxRegisterBlocks; i += 8)
        {
            var b = span.Slice(i, 8);
            facts.Add(new SyncManagerFact(SlaveRef.From(d), (byte)(first + i / 8),
                BinaryPrimitives.ReadUInt16LittleEndian(b),
                BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
                b[4], (b[6] & 0x01) != 0));
        }
        return facts;
    }

    /// <summary>Writes to 0x0600 + 16n. Layout per block: logical start address (4),
    /// length (2), logical start bit (1), logical stop bit (1), physical start address (2),
    /// physical start bit (1), type (1), activate (1), then 3 reserved bytes.
    /// Bounded by block number as well as payload length, for the same reason as
    /// <see cref="TrySyncManagers"/>.</summary>
    public static IReadOnlyList<FmmuFact> TryFmmus(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return [];
        if (d.Ado < FmmuBase || d.Ado >= FmmuBase + 16 * MaxRegisterBlocks) return [];
        var offset = d.Ado - FmmuBase;
        if (offset % 16 != 0) return [];
        var first = offset / 16;
        var span = d.Payload.Span;
        var facts = new List<FmmuFact>();
        for (var i = 0; i + 16 <= span.Length && first + i / 16 < MaxRegisterBlocks; i += 16)
        {
            var b = span.Slice(i, 16);
            facts.Add(new FmmuFact(SlaveRef.From(d), (byte)(first + i / 16),
                BinaryPrimitives.ReadUInt32LittleEndian(b),
                BinaryPrimitives.ReadUInt16LittleEndian(b[4..]),
                b[6], b[7],
                BinaryPrimitives.ReadUInt16LittleEndian(b[8..]),
                b[10], (FmmuType)b[11], (b[12] & 0x01) != 0));
        }
        return facts;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~RegisterDecoderTests"`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/RegisterDecoders.cs tests/OpenEC.Monitor.Tests/Learning/RegisterDecoderTests.cs
git commit -m "feat(learning): SyncManager and FMMU register decoders"
```

---

### Task 4: CoE SDO decoders

**Files:**
- Create: `src/OpenEC.Monitor/Learning/MailboxDecoders.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/MailboxDecoderTests.cs`

**Interfaces:**
- Consumes: `MailboxParser.TryParse`, `CoeService`, `SdoTransfer` from `OpenEC.Monitor.Protocol`; `SdoValueFact` from Task 1.
- Produces: `MailboxDecoders.TrySdoDownload(EtherCatDatagram, FrameDirection) → SdoValueFact?` and `.TrySdoUploadResponse(...) → SdoValueFact?`. Both cover expedited transfers only.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/MailboxDecoderTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class MailboxDecoderTests
{
    /// <summary>Wraps a CoE body in a mailbox header addressed to <paramref name="station"/>.</summary>
    internal static byte[] CoeMailbox(ushort station, byte[] body)
    {
        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes(station).CopyTo(mailbox, 2);
        mailbox[5] = 0x03;                       // type 3 = CoE
        body.CopyTo(mailbox, 6);
        return mailbox;
    }

    /// <summary>An expedited SDO with a 4-byte value. Service 2 = SDO request (download),
    /// 3 = SDO response (upload answer).</summary>
    internal static byte[] ExpeditedSdo(byte service, byte commandSpecifier,
        ushort index, byte subIndex, uint value)
    {
        var body = new byte[10];
        BitConverter.GetBytes((ushort)(service << 12)).CopyTo(body, 0);
        body[2] = commandSpecifier;
        BitConverter.GetBytes(index).CopyTo(body, 3);
        body[5] = subIndex;
        BitConverter.GetBytes(value).CopyTo(body, 6);
        return body;
    }

    private static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, byte[] payload,
        ushort wkc = 1) =>
        new(cmd, 0, (0x1000u << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Pdo_assignment_download_is_decoded()
    {
        // Download 0x1C13:01 = 0x1A00 (assign TxPDO 0x1A00 to SM3). cs 0x23 = expedited, 4 bytes.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x23, 0x1C13, 1, 0x1A00));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        var fact = MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(0x1C13, fact!.Index);
        Assert.Equal(1, fact.SubIndex);
        Assert.Equal(0x1A00u, fact.Value);
    }

    [Fact]
    public void Pdo_mapping_download_is_decoded()
    {
        // 0x1A00:01 = 0x60000110 → object 0x6000 sub 0x01, 16 bits.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x23, 0x1A00, 1, 0x60000110));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        var fact = MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        var entry = PdoMappingEntry.FromRaw(fact!.Value);
        Assert.Equal(0x6000, entry.Index);
        Assert.Equal(1, entry.SubIndex);
        Assert.Equal(16, entry.BitLength);
        Assert.False(entry.IsPadding);
    }

    [Fact]
    public void Padding_mapping_entries_are_recognised()
    {
        var entry = PdoMappingEntry.FromRaw(0x00000004);

        Assert.True(entry.IsPadding);
        Assert.Equal(4, entry.BitLength);
    }

    [Fact]
    public void Sdo_upload_response_is_decoded_from_returning_frames()
    {
        // 0x1018:01 = vendor id 0x00000002. cs 0x43 = expedited upload response, 4 bytes.
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 0x00000002));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        var fact = MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal(0x1018, fact!.Index);
        Assert.Equal(2u, fact.Value);
    }

    [Fact]
    public void Upload_responses_are_not_mistaken_for_downloads()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 2));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Non_coe_mailbox_traffic_is_ignored()
    {
        var mailbox = new byte[10];
        BitConverter.GetBytes((ushort)4).CopyTo(mailbox, 0);
        mailbox[5] = 0x04;                       // type 4 = FoE
        var d = Datagram(EtherCatCommand.Fpwr, 1001, mailbox);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Segmented_transfers_are_ignored()
    {
        // cs 0x00 → initiate download, not expedited, size not indicated.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x00, 0x1A00, 1, 0));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    /// <summary>Same service as a download, different command specifier — the case that actually
    /// exercises the ccs guard. An upload request is how a master READS an object, so decoding it
    /// as a download would record a value that was never written.</summary>
    [Fact]
    public void An_upload_request_is_not_decoded_as_a_download()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x43, 0x1018, 1, 0));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void A_download_response_is_not_decoded_as_an_upload_response()
    {
        // Service 3 (SdoResponse) with ccs 1: a download acknowledgement, carrying no value.
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x23, 0x1C13, 1, 0x1A00));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning));
    }

    [Fact]
    public void Upload_responses_with_a_zero_working_counter_are_ignored()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 2));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload, wkc: 0);

        Assert.Null(MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning));
    }

    /// <summary>The command specifier declares a 4-byte value but the body stops after the
    /// sub-index. Zero-filling here would fabricate a value that was never on the wire.</summary>
    [Fact]
    public void Truncated_expedited_data_is_rejected_rather_than_zero_filled()
    {
        var body = new byte[6];
        BitConverter.GetBytes((ushort)((ushort)CoeService.SdoRequest << 12)).CopyTo(body, 0);
        body[2] = 0x23;
        BitConverter.GetBytes((ushort)0x1C13).CopyTo(body, 3);
        body[5] = 1;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, CoeMailbox(1001, body));

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~MailboxDecoderTests"`
Expected: FAIL — `MailboxDecoders` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/MailboxDecoders.cs`:

```csharp
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Pure decoders for the CoE traffic that configures process data: PDO assignment
/// (0x1C1x), PDO mapping (0x16xx/0x1Axx) and the identity object (0x1018).
/// Only expedited SDO transfers are decoded — every value learning mode needs fits in four
/// bytes, and segmented transfers are out of scope per the design spec.</summary>
public static class MailboxDecoders
{
    private const byte DownloadRequest = 1;   // client command specifier: initiate download
    private const byte UploadResponse = 2;    // server command specifier: initiate upload

    /// <summary>A master-to-slave SDO write. Carries PDO assignment and mapping.</summary>
    public static SdoValueFact? TrySdoDownload(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound) return null;
        if (!RegisterDecoders.IsWrite(d.Command)) return null;
        return TryValue(d, CoeService.SdoRequest, DownloadRequest);
    }

    /// <summary>A slave-to-master SDO read answer. Carries identity when the master polls
    /// 0x1018 instead of reading SII.</summary>
    public static SdoValueFact? TrySdoUploadResponse(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning) return null;
        if (!RegisterDecoders.IsRead(d.Command) || d.WorkingCounter == 0) return null;
        return TryValue(d, CoeService.SdoResponse, UploadResponse);
    }

    private static SdoValueFact? TryValue(EtherCatDatagram d, CoeService service, byte specifier)
    {
        var mailbox = MailboxParser.TryParse(d.Payload);
        if (mailbox?.Coe is not { } coe || coe.Service != service) return null;
        if (coe.Sdo is not { Expedited: true, SizeIndicated: true } sdo) return null;
        // Load-bearing on real traffic: an SDO upload REQUEST also carries service SdoRequest
        // (with ccs 2), which is how masters read an object such as 0x1018. Without this check a
        // read request would be recorded as a written value.
        if ((sdo.CommandSpecifier >> 5) != specifier) return null;
        if (TryExpeditedValue(sdo) is not { } value) return null;
        return new SdoValueFact(SlaveRef.From(d), sdo.Index, sdo.SubIndex, value);
    }

    /// <summary>Reads the expedited payload as a little-endian unsigned value. Bits 2-3 of
    /// the command specifier count the UNUSED bytes of the four-byte field. Returns null when
    /// the mailbox body carries fewer bytes than the specifier declares — a truncated capture
    /// must be rejected, not zero-filled into a plausible-looking value, since a fabricated
    /// `0x1C13:00 = 0` would read downstream as "no PDOs assigned".</summary>
    private static uint? TryExpeditedValue(SdoTransfer sdo)
    {
        var span = sdo.Data.Span;
        var used = 4 - ((sdo.CommandSpecifier >> 2) & 0x03);
        if (span.Length < used) return null;
        uint value = 0;
        for (var i = 0; i < used; i++)
            value |= (uint)span[i] << (8 * i);
        return value;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~MailboxDecoderTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/MailboxDecoders.cs tests/OpenEC.Monitor.Tests/Learning/MailboxDecoderTests.cs
git commit -m "feat(learning): CoE SDO decoders for PDO assignment, mapping and identity"
```

---

### Task 5: LearnedSlave and the LearnedBus accumulator

**Files:**
- Create: `src/OpenEC.Monitor/Learning/LearnedSlave.cs`
- Create: `src/OpenEC.Monitor/Learning/LearnedBus.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/LearnedBusTests.cs`

**Interfaces:**
- Consumes: all decoders and facts from Tasks 1–4.
- Produces: `LearnedSlave` (properties `StationAddress`, `RingPosition`, `VendorId`, `ProductCode`, `Revision`, `SerialNumber`, `SyncManagers`, `Fmmus`, `EepromWords`, `IdentityKnown`; methods `RecordSdo`, `TryGetSdo`, `AssignedPdos(byte smNumber)`, `Mapping(ushort pdoIndex)`, `MailboxRange(byte smNumber)`) and `LearnedBus` (`Observe(DateTimeOffset, EtherCatDatagram, FrameDirection)`, `Slaves`, `CyclicCommands`, `SawStartup`). The SDO store itself stays private — no task in this plan enumerates it, only probes and derived views.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/LearnedBusTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class LearnedBusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EtherCatDatagram Physical(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc = 1) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    private static EtherCatDatagram Logical(EtherCatCommand cmd, uint address, int length,
        ushort wkc) =>
        new(cmd, 0, address, false, false, 0, new byte[length], wkc);

    [Fact]
    public void Station_address_assignment_creates_a_slave_at_its_ring_position()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0xFFFF, 0x0010, [0xEA, 0x03]),
            FrameDirection.Outbound);

        Assert.Equal(2, bus.Slaves.Count);
        Assert.Equal(0, bus.Slaves[0].RingPosition);
        Assert.Equal(1001, bus.Slaves[0].StationAddress);
        Assert.Equal(1, bus.Slaves[1].RingPosition);
        Assert.Equal(1002, bus.Slaves[1].StationAddress);
        Assert.True(bus.SawStartup);
    }

    [Fact]
    public void Sii_reads_addressed_by_auto_increment_resolve_to_the_assigned_slave()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        // Read request for EEPROM word 8, then the answer: vendor 2, product 0x03F03052.
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0502,
            [0x00, 0x01, 0x08, 0x00, 0x00, 0x00]), FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Aprd, 0x0000, 0x0508,
            [0x02, 0x00, 0x00, 0x00, 0x52, 0x30, 0xF0, 0x03]), FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
    }

    [Fact]
    public void Identity_falls_back_to_the_coe_identity_object()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        foreach (var (sub, value) in new (byte, uint)[] { (1, 2u), (2, 0x03F03052u), (3, 0x00100000u) })
        {
            var body = MailboxDecoderTests.ExpeditedSdo(3, 0x43, 0x1018, sub, value);
            bus.Observe(T0, Physical(EtherCatCommand.Fprd, 1001,
                    0x1080, MailboxDecoderTests.CoeMailbox(1001, body)),
                FrameDirection.Returning);
        }

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(0x00120000u, slave.Revision);
    }

    [Fact]
    public void Sync_managers_and_fmmus_are_recorded_against_the_slave()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x0810,
            [0x00, 0x11, 0x06, 0x00, 0x64, 0x00, 0x01, 0x00]), FrameDirection.Outbound);

        var fmmu = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(fmmu, 0);
        BitConverter.GetBytes((ushort)6).CopyTo(fmmu, 4);
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[7] = 7; fmmu[11] = 1; fmmu[12] = 1;
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x0600, fmmu),
            FrameDirection.Outbound);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(0x1100, slave.SyncManagers[2].PhysicalStart);
        Assert.Equal(FmmuType.Inputs, slave.Fmmus[0].Type);
    }

    [Fact]
    public void Pdo_assignment_is_read_back_in_subindex_order()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1C13, 0, 0);
        Download(bus, 0x1C13, 2, 0x1A01);
        Download(bus, 0x1C13, 1, 0x1A00);
        Download(bus, 0x1C13, 0, 2);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(new ushort[] { 0x1A00, 0x1A01 }, slave.AssignedPdos(3));
    }

    [Fact]
    public void Assignment_count_of_zero_yields_no_pdos()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        Download(bus, 0x1C12, 1, 0x1600);
        Download(bus, 0x1C12, 0, 0);

        Assert.Empty(Assert.Single(bus.Slaves).AssignedPdos(2));
    }

    [Fact]
    public void Pdo_mapping_entries_are_read_back_in_subindex_order()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1A00, 0, 2);
        Download(bus, 0x1A00, 1, 0x60000110);
        Download(bus, 0x1A00, 2, 0x60010108);

        var mapping = Assert.Single(bus.Slaves).Mapping(0x1A00);
        Assert.Equal(2, mapping.Count);
        Assert.Equal(0x6000, mapping[0].Index);
        Assert.Equal(16, mapping[0].BitLength);
        Assert.Equal(0x6001, mapping[1].Index);
        Assert.Equal(8, mapping[1].BitLength);
    }

    [Fact]
    public void Cyclic_commands_record_length_and_modal_working_counter()
    {
        var bus = new LearnedBus();
        for (var i = 0; i < 10; i++)
            bus.Observe(T0, Logical(EtherCatCommand.Lrd, 0x00010000, 6, 3),
                FrameDirection.Returning);
        bus.Observe(T0, Logical(EtherCatCommand.Lrd, 0x00010000, 6, 2),
            FrameDirection.Returning);

        var cmd = Assert.Single(bus.CyclicCommands);
        Assert.Equal(EtherCatCommand.Lrd, cmd.Command);
        Assert.Equal(0x00010000u, cmd.RawAddress);
        Assert.Equal(6, cmd.DataLength);
        Assert.Equal(3, cmd.ExpectedWkc);
    }

    /// <summary>A capture that never sees an explicit sub-index 0 write still yields every entry
    /// it did observe. Sparse sub-indices are included because a total-based cap would drop
    /// everything above the entry count.</summary>
    [Fact]
    public void Entries_are_all_returned_when_no_declared_count_was_observed()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1C13, 1, 0x1A00);
        Download(bus, 0x1C13, 2, 0x1A01);
        Download(bus, 0x1A00, 1, 0x60000110);
        Download(bus, 0x1A00, 3, 0x60020108);
        Download(bus, 0x1A00, 5, 0x60040104);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(new ushort[] { 0x1A00, 0x1A01 }, slave.AssignedPdos(3));
        Assert.Equal(new[] { 0x6000, 0x6002, 0x6004 },
            slave.Mapping(0x1A00).Select(e => (int)e.Index));
    }

    [Fact]
    public void Attaching_mid_run_without_startup_still_discovers_slaves()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, Physical(EtherCatCommand.Fprd, 1005, 0x0130, [0x08, 0x00]),
            FrameDirection.Returning);

        Assert.Equal(1005, Assert.Single(bus.Slaves).StationAddress);
        Assert.False(bus.SawStartup);
    }

    private static void Download(LearnedBus bus, ushort index, byte sub, uint value)
    {
        var body = MailboxDecoderTests.ExpeditedSdo(2, 0x23, index, sub, value);
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x1000,
            MailboxDecoderTests.CoeMailbox(1001, body)), FrameDirection.Outbound);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedBusTests"`
Expected: FAIL — `LearnedBus` does not exist.

- [ ] **Step 3: Write LearnedSlave**

`src/OpenEC.Monitor/Learning/LearnedSlave.cs`:

```csharp
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Learning;

/// <summary>Everything learned about one slave. Populated only from observed traffic;
/// a null property means "not seen", never a defaulted stand-in.</summary>
public sealed class LearnedSlave
{
    /// <summary>SDO values keyed by object index, then sub-index. Sub-index order matters:
    /// assignment and mapping objects are read back in sub-index order, and sub-index 0
    /// carries the count.</summary>
    private readonly Dictionary<ushort, SortedDictionary<byte, uint>> _sdo = new();

    public required ushort StationAddress { get; init; }
    public int RingPosition { get; set; } = -1;
    public uint? VendorId { get; set; }
    public uint? ProductCode { get; set; }
    public uint? Revision { get; set; }
    public uint? SerialNumber { get; set; }
    public Dictionary<byte, SyncManagerFact> SyncManagers { get; } = new();
    public Dictionary<byte, FmmuFact> Fmmus { get; } = new();
    public Dictionary<uint, byte[]> EepromWords { get; } = new();

    public bool IdentityKnown => VendorId is not null && ProductCode is not null;

    public void RecordSdo(ushort index, byte subIndex, uint value)
    {
        if (!_sdo.TryGetValue(index, out var subs))
            _sdo[index] = subs = new SortedDictionary<byte, uint>();
        subs[subIndex] = value;
    }

    public bool TryGetSdo(ushort index, byte subIndex, out uint value)
    {
        value = 0;
        return _sdo.TryGetValue(index, out var subs) && subs.TryGetValue(subIndex, out value);
    }

    /// <summary>The number of entries the object declares. Sub-index 0 carries it and is
    /// authoritative when present: entries beyond it are stale leftovers from an earlier, longer
    /// configuration, which is how a master shortens a PDO list. When sub-index 0 was never
    /// observed there is no declared count, so every observed entry counts.
    ///
    /// Returning the entry total instead would be wrong twice over: the dictionary holds only
    /// keys >= 1 in that case, so any "count minus one" undercounts by one, and on a sparse
    /// capture (entries 1, 3, 5 with no sub-index 0) a total-based cap drops everything above it.</summary>
    private static int DeclaredCount(SortedDictionary<byte, uint> subs) =>
        subs.TryGetValue(0, out var declared) ? (int)declared : int.MaxValue;

    /// <summary>PDO indices assigned to a SyncManager, from object 0x1C10 + n. Sub-index 0
    /// is the count, so a later count of zero correctly empties a previously filled list.</summary>
    public IReadOnlyList<ushort> AssignedPdos(byte syncManagerNumber)
    {
        var index = (ushort)(0x1C10 + syncManagerNumber);
        if (!_sdo.TryGetValue(index, out var subs)) return [];
        var count = DeclaredCount(subs);
        return subs.Where(kv => kv.Key >= 1 && kv.Key <= count)
            .OrderBy(kv => kv.Key)
            .Select(kv => (ushort)kv.Value)
            .ToList();
    }

    /// <summary>Entries of a mapping object (0x16xx/0x1Axx) in sub-index order.</summary>
    public IReadOnlyList<PdoMappingEntry> Mapping(ushort pdoIndex)
    {
        if (!_sdo.TryGetValue(pdoIndex, out var subs)) return [];
        var count = DeclaredCount(subs);
        return subs.Where(kv => kv.Key >= 1 && kv.Key <= count)
            .OrderBy(kv => kv.Key)
            .Select(kv => PdoMappingEntry.FromRaw(kv.Value))
            .ToList();
    }

    /// <summary>The mailbox window of a SyncManager, or null when that SM was never configured.</summary>
    public MailboxRange? MailboxRange(byte syncManagerNumber) =>
        SyncManagers.TryGetValue(syncManagerNumber, out var sm) && sm.Length > 0
            ? new MailboxRange(sm.PhysicalStart, sm.Length)
            : null;
}
```

- [ ] **Step 4: Write LearnedBus**

`src/OpenEC.Monitor/Learning/LearnedBus.cs`:

```csharp
using System.Buffers.Binary;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>One observed cyclic datagram shape: the master's frame table as seen on the wire.</summary>
public sealed record LearnedCyclicCommand(EtherCatCommand Command, uint RawAddress,
    int DataLength, ushort ExpectedWkc);

/// <summary>Accumulates decoded facts into a picture of the bus. The only stateful piece of
/// the learner; the decoders it drives are all pure. Not thread-safe — callers feed it from
/// a single pump, exactly as <see cref="BusObserver"/> is fed.</summary>
public sealed class LearnedBus
{
    private const uint EepromVendorIdWord = 0x0008;
    private const uint EepromProductCodeWord = 0x000A;
    private const uint EepromRevisionWord = 0x000C;
    private const uint EepromSerialWord = 0x000E;
    private const ushort CoeIdentityObject = 0x1018;

    private readonly Dictionary<ushort, LearnedSlave> _slaves = new();
    private readonly Dictionary<ushort, ushort> _autoIncToStation = new();
    private readonly Dictionary<SlaveRef, uint> _pendingSiiAddress = new();
    private readonly Dictionary<(EtherCatCommand, uint), CyclicObservation> _cyclic = new();

    private sealed class CyclicObservation
    {
        public int DataLength;
        public readonly Dictionary<ushort, int> WkcCounts = new();
    }

    /// <summary>True once a station-address assignment has been observed, meaning the capture
    /// includes bus startup and the learned picture can be complete.</summary>
    public bool SawStartup { get; private set; }

    public IReadOnlyList<LearnedSlave> Slaves =>
        _slaves.Values.OrderBy(s => s.RingPosition < 0 ? int.MaxValue : s.RingPosition)
            .ThenBy(s => s.StationAddress)
            .ToList();

    public IReadOnlyList<LearnedCyclicCommand> CyclicCommands =>
        _cyclic.Select(kv => new LearnedCyclicCommand(kv.Key.Item1, kv.Key.Item2,
                kv.Value.DataLength, ModalWkc(kv.Value)))
            .OrderBy(c => c.RawAddress)
            .ToList();

    public void Observe(DateTimeOffset timestamp, EtherCatDatagram d, FrameDirection direction)
    {
        if (d.IsLogical)
        {
            ObserveCyclic(d, direction);
            return;
        }

        if (RegisterDecoders.TryStationAddress(d, direction) is { } assignment)
        {
            SawStartup = true;
            _autoIncToStation[assignment.AutoIncAddress] = assignment.StationAddress;
            GetOrAdd(assignment.StationAddress).RingPosition = assignment.RingPosition;
            return;
        }

        if (RegisterDecoders.TrySiiAddress(d, direction) is { IsRead: true } siiAddress)
        {
            _pendingSiiAddress[siiAddress.Slave] = siiAddress.WordAddress;
            return;
        }

        if (RegisterDecoders.TrySiiData(d, direction) is { } siiData)
        {
            ObserveSiiData(siiData);
            return;
        }

        foreach (var sm in RegisterDecoders.TrySyncManagers(d, direction))
            if (Resolve(sm.Slave) is { } slave) slave.SyncManagers[sm.Number] = sm;

        foreach (var fmmu in RegisterDecoders.TryFmmus(d, direction))
            if (Resolve(fmmu.Slave) is { } slave) slave.Fmmus[fmmu.Number] = fmmu;

        var sdo = MailboxDecoders.TrySdoDownload(d, direction)
            ?? MailboxDecoders.TrySdoUploadResponse(d, direction);
        if (sdo is not null) ObserveSdo(sdo);

        // A returning physical read proves the slave answered, which is enough to list it
        // when we attached after startup and never saw an address assignment.
        if (direction == FrameDirection.Returning && d.WorkingCounter > 0
            && d.Command == EtherCatCommand.Fprd && d.Adp != 0)
            GetOrAdd(d.Adp);
    }

    private void ObserveCyclic(EtherCatDatagram d, FrameDirection direction)
    {
        if (direction != FrameDirection.Returning) return;
        var key = (d.Command, d.RawAddress);
        if (!_cyclic.TryGetValue(key, out var observation))
            _cyclic[key] = observation = new CyclicObservation();
        observation.DataLength = Math.Max(observation.DataLength, d.Payload.Length);
        observation.WkcCounts[d.WorkingCounter] =
            observation.WkcCounts.GetValueOrDefault(d.WorkingCounter) + 1;
    }

    private void ObserveSiiData(SiiDataFact fact)
    {
        if (!_pendingSiiAddress.Remove(fact.Slave, out var wordAddress)) return;
        if (Resolve(fact.Slave) is not { } slave) return;
        for (var i = 0; i + 2 <= fact.Data.Length; i += 2)
            slave.EepromWords[wordAddress + (uint)(i / 2)] = [fact.Data[i], fact.Data[i + 1]];
        slave.VendorId ??= ReadEepromDword(slave, EepromVendorIdWord);
        slave.ProductCode ??= ReadEepromDword(slave, EepromProductCodeWord);
        slave.Revision ??= ReadEepromDword(slave, EepromRevisionWord);
        slave.SerialNumber ??= ReadEepromDword(slave, EepromSerialWord);
    }

    private void ObserveSdo(SdoValueFact fact)
    {
        if (Resolve(fact.Slave) is not { } slave) return;
        slave.RecordSdo(fact.Index, fact.SubIndex, fact.Value);
        if (fact.Index != CoeIdentityObject) return;
        switch (fact.SubIndex)
        {
            case 1: slave.VendorId ??= fact.Value; break;
            case 2: slave.ProductCode ??= fact.Value; break;
            case 3: slave.Revision ??= fact.Value; break;
            case 4: slave.SerialNumber ??= fact.Value; break;
        }
    }

    private static uint? ReadEepromDword(LearnedSlave slave, uint wordAddress)
    {
        if (!slave.EepromWords.TryGetValue(wordAddress, out var low)) return null;
        if (!slave.EepromWords.TryGetValue(wordAddress + 1, out var high)) return null;
        Span<byte> dword = [low[0], low[1], high[0], high[1]];
        return BinaryPrimitives.ReadUInt32LittleEndian(dword);
    }

    private static ushort ModalWkc(CyclicObservation observation) =>
        observation.WkcCounts.Count == 0 ? (ushort)0
            : observation.WkcCounts.MaxBy(kv => kv.Value).Key;

    /// <summary>Maps a reference to a slave, translating auto-increment addressing through
    /// the assignment map. Returns null when the reference cannot yet be resolved — traffic
    /// seen before the address assignment is dropped rather than attributed to a guess.</summary>
    private LearnedSlave? Resolve(SlaveRef re)
    {
        if (!re.IsAutoIncrement) return GetOrAdd(re.Address);
        return _autoIncToStation.TryGetValue(re.Address, out var station)
            ? GetOrAdd(station)
            : null;
    }

    private LearnedSlave GetOrAdd(ushort stationAddress)
    {
        if (!_slaves.TryGetValue(stationAddress, out var slave))
            _slaves[stationAddress] = slave = new LearnedSlave { StationAddress = stationAddress };
        return slave;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnedBusTests"`
Expected: PASS (9 tests).

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Learning/LearnedSlave.cs src/OpenEC.Monitor/Learning/LearnedBus.cs tests/OpenEC.Monitor.Tests/Learning/LearnedBusTests.cs
git commit -m "feat(learning): LearnedBus accumulator with auto-increment resolution"
```

---

### Task 6: Synthetic bringup capture generator

**Files:**
- Create: `src/OpenEC.Monitor/Synthesis/BringupCapture.cs`
- Test: `tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs`

**Interfaces:**
- Consumes: `EtherCatFrameBuilder`, `PcapFileWriter`.
- Produces: `BringupCapture.Frames(int cycles) → IReadOnlyList<(DateTimeOffset, byte[])>` and `BringupCapture.Write(string path, int cycles = 20) → string`. Every later task's integration test uses this as its fixture.

The generated bus mirrors the `EL1008.xml` ESI fixture identity exactly — vendor 2, product `0x03F03052`, revision `0x00120000` — so Task 10's ESI-resolution test finds a real device. These values are copied from the fixture's own `<Vendor><Id>` and `<Type ProductCode= RevisionNo=>`; do not adjust them without re-reading that file.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs`:

```csharp
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Synthesis;

public class BringupCaptureTests
{
    /// <summary>Feeds a generated bringup through the parser and learner exactly as the
    /// live pump would, so the fixture is validated against the real decode path.</summary>
    private static LearnedBus Learn()
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
        return bus;
    }

    [Fact]
    public void Generated_bringup_yields_two_slaves_in_ring_order()
    {
        var bus = Learn();

        Assert.True(bus.SawStartup);
        Assert.Equal(2, bus.Slaves.Count);
        Assert.Equal(new[] { 1001, 1002 }, bus.Slaves.Select(s => (int)s.StationAddress));
        Assert.Equal(new[] { 0, 1 }, bus.Slaves.Select(s => s.RingPosition));
    }

    [Fact]
    public void Generated_bringup_carries_identity()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(0x00100000u, slave.Revision);
    }

    [Fact]
    public void Generated_bringup_configures_sync_managers_and_fmmus()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(0x1000, slave.SyncManagers[0].PhysicalStart);   // mailbox out
        Assert.Equal(0x1100, slave.SyncManagers[3].PhysicalStart);   // inputs
        Assert.Equal(FmmuType.Inputs, slave.Fmmus[0].Type);
        Assert.Equal(0x00010000u, slave.Fmmus[0].LogicalStart);
    }

    /// <summary>The second slave's logical start is where a position-arithmetic off-by-one would
    /// hide, and Task 8's bit offsets depend on it — so it is asserted here at the source rather
    /// than left to surface as a confusing offset mismatch downstream.</summary>
    [Fact]
    public void Each_slave_maps_into_its_own_logical_byte()
    {
        var slaves = Learn().Slaves;

        Assert.Equal(0x00010000u, slaves[0].Fmmus[0].LogicalStart);
        Assert.Equal(0x00010001u, slaves[1].Fmmus[0].LogicalStart);
        Assert.Equal(FmmuType.Inputs, slaves[1].Fmmus[0].Type);
        Assert.Equal(0x1100, slaves[1].SyncManagers[3].PhysicalStart);
    }

    [Fact]
    public void Generated_bringup_assigns_and_maps_pdos()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(new ushort[] { 0x1A00 }, slave.AssignedPdos(3));
        var mapping = slave.Mapping(0x1A00);
        Assert.Equal(8, mapping.Count);
        Assert.All(mapping, e => Assert.Equal(1, e.BitLength));
    }

    [Fact]
    public void Generated_bringup_produces_a_cyclic_command_table()
    {
        var cyclic = Assert.Single(Learn().CyclicCommands);

        Assert.Equal(EtherCatCommand.Lrd, cyclic.Command);
        Assert.Equal(0x00010000u, cyclic.RawAddress);
        Assert.Equal(2, cyclic.ExpectedWkc);
    }

    /// <summary>Round-trips the written file back through the real pcap reader. A length check
    /// alone would pass for any non-empty blob, and Task 12 reads this file through
    /// <see cref="PcapFileSource"/>, so parseability is the property that actually matters.</summary>
    [Fact]
    public async Task Written_capture_is_a_readable_pcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bringup-{Guid.NewGuid():N}.pcap");
        try
        {
            BringupCapture.Write(path, cycles: 3);

            await using var source = new PcapFileSource(path);
            var readBack = 0;
            await foreach (var _ in source.CaptureAsync())
                readBack++;

            Assert.Equal(BringupCapture.Frames(cycles: 3).Count, readBack);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BringupCaptureTests"`
Expected: FAIL — `BringupCapture` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Synthesis/BringupCapture.cs`:

```csharp
using System.Buffers.Binary;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Generates a synthetic INIT→OP bringup for a two-slave bus, so learning mode is
/// testable without hardware. Bringup happens once on a real bus and is awkward to capture
/// on demand, which makes this the load-bearing test asset for the whole feature.
///
/// The bus is two 8-bit digital input terminals sharing the identity of the EL1008 ESI test
/// fixture (vendor 2, product 0x03F03052, revision 0x00120000). Each contributes one byte of
/// inputs, mapped through FMMU 0 into logical address 0x00010000.</summary>
public static class BringupCapture
{
    private const uint VendorId = 2;
    private const uint ProductCode = 0x03F03052;
    private const uint Revision = 0x00120000;   // must match EL1008.xml's RevisionNo
    private const uint SerialNumber = 0;

    private static readonly ushort[] Stations = [1001, 1002];

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

        // --- INIT: assign configured station addresses by ring position ---
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

        // --- INIT: read identity out of SII, four dwords from word 0x08 ---
        foreach (var station in Stations)
        {
            foreach (var (word, value) in new (uint, uint)[]
                     {
                         (0x0008, VendorId), (0x000A, ProductCode),
                         (0x000C, Revision), (0x000E, SerialNumber),
                     })
            {
                var request = new byte[6];
                BitConverter.GetBytes((ushort)0x0100).CopyTo(request, 0);   // read command
                BitConverter.GetBytes(word).CopyTo(request, 2);
                Emit(new EtherCatFrameBuilder()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 0),
                    new EtherCatFrameBuilder().AsReturning()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 1));
                idx++;

                var answer = BitConverter.GetBytes(value);
                Emit(new EtherCatFrameBuilder()
                        .AddPhysical(EtherCatCommand.Fprd, idx, station, 0x0508, new byte[4], 0),
                    new EtherCatFrameBuilder().AsReturning()
                        .AddPhysical(EtherCatCommand.Fprd, idx, station, 0x0508, answer, 1));
                idx++;
            }
        }

        // --- INIT→PREOP: mailbox SyncManagers (SM0 out, SM1 in) ---
        foreach (var station in Stations)
        {
            var block = new byte[16];
            WriteSyncManager(block.AsSpan(0, 8), start: 0x1000, length: 128, control: 0x26);
            WriteSyncManager(block.AsSpan(8, 8), start: 0x1080, length: 128, control: 0x22);
            EmitWrite(station, 0x0800, block);
        }

        // --- PREOP→SAFEOP: PDO assignment and mapping over CoE ---
        foreach (var station in Stations)
        {
            EmitSdo(station, 0x1C13, 0, 0);
            EmitSdo(station, 0x1A00, 0, 0);
            for (byte bit = 1; bit <= 8; bit++)
                EmitSdo(station, 0x1A00, bit, (uint)(0x60000000 | ((uint)bit << 8) | 0x01));
            EmitSdo(station, 0x1A00, 0, 8);
            EmitSdo(station, 0x1C13, 1, 0x1A00);
            EmitSdo(station, 0x1C13, 0, 1);
        }

        // --- PREOP→SAFEOP: process-data SyncManager and FMMU ---
        for (var position = 0; position < Stations.Length; position++)
        {
            var block = new byte[8];
            WriteSyncManager(block, start: 0x1100, length: 1, control: 0x00);
            EmitWrite(Stations[position], 0x0818, block);   // SM3 = 0x0800 + 3*8

            var fmmu = new byte[16];
            BitConverter.GetBytes(0x00010000u + (uint)position).CopyTo(fmmu, 0);
            BitConverter.GetBytes((ushort)1).CopyTo(fmmu, 4);
            fmmu[6] = 0;                                     // logical start bit
            fmmu[7] = 7;                                     // logical stop bit
            BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
            fmmu[10] = 0;                                    // physical start bit
            fmmu[11] = (byte)1;                              // inputs
            fmmu[12] = 1;                                    // activate
            EmitWrite(Stations[position], 0x0600, fmmu);
        }

        // --- OP: cyclic input read plus the broadcast AL status poll ---
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var inputs = new byte[] { (byte)(cycle & 0xFF), (byte)(~cycle & 0xFF) };
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrd, idx, 0x00010000, new byte[2], 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[2], 0)
                .Build()));
            frames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrd, idx, 0x00010000, inputs, 2)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, [0x08, 0x00], 2)
                .Build()));
            idx += 2;
            t = t.AddMilliseconds(1);
        }

        return frames;

        void EmitWrite(ushort station, ushort register, byte[] payload)
        {
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, register, payload, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, register, payload, 1));
            idx++;
        }

        void EmitSdo(ushort station, ushort index, byte subIndex, uint value)
        {
            var mailbox = CoeDownload(station, index, subIndex, value);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x1000, mailbox, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x1000, mailbox, 1));
            idx++;
        }
    }

    private static void WriteSyncManager(Span<byte> block, ushort start, ushort length, byte control)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block, start);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], length);
        block[4] = control;
        block[6] = 0x01;    // activate
    }

    /// <summary>An expedited, size-indicated CoE SDO download wrapped in a mailbox header.</summary>
    private static byte[] CoeDownload(ushort station, ushort index, byte subIndex, uint value)
    {
        var body = new byte[10];
        BitConverter.GetBytes((ushort)((ushort)CoeService.SdoRequest << 12)).CopyTo(body, 0);
        body[2] = 0x23;     // ccs 1, expedited, size indicated, 4 bytes used
        BitConverter.GetBytes(index).CopyTo(body, 3);
        body[5] = subIndex;
        BitConverter.GetBytes(value).CopyTo(body, 6);

        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes(station).CopyTo(mailbox, 2);
        mailbox[5] = (byte)MailboxType.Coe;
        body.CopyTo(mailbox, 6);
        return mailbox;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BringupCaptureTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Synthesis/BringupCapture.cs tests/OpenEC.Monitor.Tests/Synthesis/BringupCaptureTests.cs
git commit -m "feat(synthesis): synthetic INIT-to-OP bringup capture generator"
```

---

### Task 7: ESI device resolution

**Files:**
- Modify: `src/OpenEC.Monitor/EsiEnricher.cs`
- Test: `tests/OpenEC.Monitor.Tests/EsiEnricherTests.cs` (append)
- Modify: `tests/OpenEC.Monitor.Tests/Fixtures/Esi/EL1008.xml` — the stub fixture carries only
  Vendor/Type/Name, so it must gain a real ESI process-data block before any `ProcessData`
  assertion can mean anything. Per `Dahlke.EtherCAT.Esi`'s documentation, `<Sm>`, `<TxPdo>` and
  `<RxPdo>` are **direct children of `<Device>`** with element-style children (`<Index>`,
  `<SubIndex>`, `<BitLen>`, `<Name>`, `<DataType>`); there is no container element. Declare four
  `<Sm>` children so ordinal 3 is the input SyncManager, and one `<TxPdo Sm="3">` at index
  `#x1a00` with eight 1-bit entries at `#x6000:01..08` — deliberately mirroring what
  `BringupCapture` emits, so ESI names can later be checked flowing into synthesized variables.

**Interfaces:**
- Produces: `EsiEnricher.ResolveDeviceAsync(uint vendorId, uint productCode, uint revision, string? typeHint = null) → Task<EsiDevice?>`, exposing `NameEn`, `ProcessData` (PDOs and SyncManagers) and `ObjectDictionary`. `ResolveNameAsync` is reimplemented in terms of it so there is one lookup path.

- [ ] **Step 1: Write the failing test**

Append to `tests/OpenEC.Monitor.Tests/EsiEnricherTests.cs` (inside the existing class):

```csharp
    [Fact]
    public async Task Resolves_the_full_device_including_process_data()
    {
        using var enricher = new EsiEnricher(FixtureDirectory);

        var device = await enricher.ResolveDeviceAsync(2, 0x03F03052, 0x00120000, "EL1008");

        Assert.NotNull(device);
        Assert.Equal("EL1008 8Ch. Dig. Input 24V, 3ms", device!.NameEn);
        var pdo = Assert.Single(device.ProcessData!.Pdos);
        Assert.Equal(0x1A00, pdo.Index);
        Assert.Equal(EsiPdoDirection.Transmit, pdo.Direction);
        Assert.Equal(8, pdo.Entries.Count);
        Assert.Equal("Input 1", pdo.Entries[0].Name);
        Assert.Equal("BOOL", pdo.Entries[0].DataType);
        Assert.Equal(1, pdo.Entries[0].BitLength);
    }

    [Fact]
    public async Task Unknown_identities_resolve_to_null()
    {
        using var enricher = new EsiEnricher(FixtureDirectory);

        Assert.Null(await enricher.ResolveDeviceAsync(0xDEAD, 0xBEEF, 1));
    }
```

If the existing test class has no `FixtureDirectory` member, add it:

```csharp
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EsiEnricherTests"`
Expected: FAIL — `ResolveDeviceAsync` does not exist.

- [ ] **Step 3: Write the implementation**

In `src/OpenEC.Monitor/EsiEnricher.cs`, replace the `ResolveNameAsync` method with:

```csharp
    /// <summary>Resolves a slave identity to its full ESI device description — name, declared
    /// process data and object dictionary. Learning mode needs the process data, not just the
    /// name: ESI supplies the schema (PDO entry names, datatypes, bit lengths) that the wire's
    /// FMMU and assignment traffic then binds to concrete offsets.</summary>
    public async Task<EsiDevice?> ResolveDeviceAsync(uint vendorId, uint productCode, uint revision,
        string? typeHint = null)
    {
        var result = await _catalog.LookupAsync(new EsiKey(vendorId, productCode, revision),
            typeHint ?? string.Empty);
        return result.Status == EsiStatus.Resolved ? result.Device : null;
    }

    public async Task<string?> ResolveNameAsync(uint vendorId, uint productCode, uint revision,
        string? typeHint = null) =>
        (await ResolveDeviceAsync(vendorId, productCode, revision, typeHint))?.NameEn;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EsiEnricherTests"`
Expected: PASS — the two new tests plus all pre-existing ones.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/EsiEnricher.cs tests/OpenEC.Monitor.Tests/EsiEnricherTests.cs
git commit -m "feat(esi): expose full device resolution for learning mode"
```

---

### Task 8: EniSynthesizer — the offset chain

**Files:**
- Create: `src/OpenEC.Monitor/Learning/EniSynthesizer.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/EniSynthesizerTests.cs`

**Interfaces:**
- Consumes: `LearnedBus`, `LearnedSlave`, `EsiDevice`.
- Produces: `EniSynthesizer.Synthesize(LearnedBus bus, IReadOnlyDictionary<ushort, EsiDevice> schemas) → EniConfiguration`.

This is the heart of the feature. The chain, per spec §3: for each enabled FMMU, find the SyncManager at its physical start; take that SM's assigned PDOs (observed assignment first, ESI default second); walk each PDO's entries (observed mapping first, ESI default second) accumulating bit offsets; emit an `EniVariable` per non-padding entry.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/EniSynthesizerTests.cs`:

```csharp
using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class EniSynthesizerTests
{
    private static LearnedBus LearnBringup()
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
        return bus;
    }

    [Fact]
    public void Slaves_are_emitted_in_ring_order_with_identity()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal(2, eni.Slaves.Count);
        Assert.Equal(1001, eni.Slaves[0].PhysAddr);
        Assert.Equal(0x0000, eni.Slaves[0].AutoIncAddr);
        Assert.Equal(0xFFFF, eni.Slaves[1].AutoIncAddr);
        Assert.Equal(2u, eni.Slaves[0].VendorId);
        Assert.Equal(0x03F03052u, eni.Slaves[0].ProductCode);
    }

    [Fact]
    public void Mailbox_windows_come_from_the_mailbox_sync_managers()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal(0x1080, eni.Slaves[0].MailboxOut!.Start);
        Assert.Equal(0x1000, eni.Slaves[0].MailboxIn!.Start);
        Assert.Equal(128, eni.Slaves[0].MailboxIn!.Length);
    }

    [Fact]
    public void Cyclic_commands_carry_length_and_expected_working_counter()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        var cmd = Assert.Single(eni.CyclicCommands);
        Assert.Equal(EtherCatCommand.Lrd, cmd.Command);
        Assert.Equal(2, cmd.ExpectedWkc);
        Assert.Equal(0, cmd.InputOffs);
        Assert.Null(cmd.OutputOffs);
    }

    [Fact]
    public void Variables_are_placed_at_wire_correct_bit_offsets()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        // Two slaves, eight 1-bit inputs each, mapped to consecutive logical bytes.
        Assert.Equal(16, eni.Variables.Count);
        Assert.All(eni.Variables, v => Assert.True(v.IsInput));
        Assert.Equal(Enumerable.Range(0, 16).ToArray(), eni.Variables.Select(v => v.BitOffs));
        Assert.All(eni.Variables, v => Assert.Equal(1, v.BitSize));
    }

    [Fact]
    public void Synthetic_names_are_used_when_no_esi_schema_is_available()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal("Slave 1001.0x6000:01", eni.Variables[0].Name);
        Assert.Equal("BOOL", eni.Variables[0].DataType);
    }

    [Fact]
    public void Padding_entries_advance_the_offset_without_becoming_variables()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00010000, length: 2, entries:
        [
            0x60000110u,   // 0x6000:01, 16 bits
            0x00000004u,   // padding, 4 bits
            0x60020104u,   // 0x6002:01, 4 bits
        ]);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>());

        Assert.Equal(2, eni.Variables.Count);
        Assert.Equal(0, eni.Variables[0].BitOffs);
        Assert.Equal(20, eni.Variables[1].BitOffs);
    }

    [Fact]
    public void Output_fmmus_produce_output_variables_on_their_own_origin()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00020000, length: 1,
            entries: [0x70000108u], fmmuType: 2);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>());

        var variable = Assert.Single(eni.Variables);
        Assert.False(variable.IsInput);
        Assert.Equal(0, variable.BitOffs);
    }

    /// <summary>A SyncManager the slave never activated cannot carry process data, so an FMMU
    /// pointing at its window places nothing — rather than resolving to the wrong assignment
    /// object and emitting plausible but wrong variables.</summary>
    [Fact]
    public void Disabled_sync_managers_are_not_matched()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00010000, length: 1,
            entries: [0x60000108u], smEnabled: false);

        Assert.Empty(EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>()).Variables);
    }

    /// <summary>Drives a minimal bringup for one slave straight through LearnedBus.</summary>
    private static void Assign(LearnedBus bus, ushort station, uint logicalStart, ushort length,
        uint[] entries, byte fmmuType = 1, bool smEnabled = true)
    {
        var t = DateTimeOffset.UnixEpoch;
        void Physical(EtherCatCommand cmd, ushort adp, ushort ado, byte[] payload,
            FrameDirection dir = FrameDirection.Outbound) =>
            bus.Observe(t, new EtherCatDatagram(cmd, 0, ((uint)ado << 16) | adp, false, false, 0,
                payload, 1), dir);

        Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, BitConverter.GetBytes(station));

        // SM 3 carries inputs and SM 2 carries outputs, as on real Beckhoff devices. The
        // register offset and the assignment object below are both derived from this one number
        // so they cannot disagree: the synthesizer finds the SM by physical address and then
        // looks up 0x1C10 + that SM's number, so a mismatch yields silently zero variables.
        var smNumber = fmmuType == 1 ? (byte)3 : (byte)2;

        var smBlock = new byte[8];
        BitConverter.GetBytes((ushort)0x1100).CopyTo(smBlock, 0);
        BitConverter.GetBytes(length).CopyTo(smBlock, 2);
        smBlock[6] = smEnabled ? (byte)0x01 : (byte)0x00;
        Physical(EtherCatCommand.Fpwr, station, (ushort)(0x0800 + 8 * smNumber), smBlock);

        var fmmu = new byte[16];
        BitConverter.GetBytes(logicalStart).CopyTo(fmmu, 0);
        BitConverter.GetBytes(length).CopyTo(fmmu, 4);
        fmmu[7] = 7;
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[11] = fmmuType;
        fmmu[12] = 1;
        Physical(EtherCatCommand.Fpwr, station, 0x0600, fmmu);

        var pdoIndex = fmmuType == 1 ? (ushort)0x1A00 : (ushort)0x1600;
        var assignObject = (ushort)(0x1C10 + smNumber);
        void Sdo(ushort index, byte sub, uint value) =>
            Physical(EtherCatCommand.Fpwr, station, 0x1000,
                MailboxDecoderTests.CoeMailbox(station,
                    MailboxDecoderTests.ExpeditedSdo(2, 0x23, index, sub, value)));

        for (byte i = 0; i < entries.Length; i++) Sdo(pdoIndex, (byte)(i + 1), entries[i]);
        Sdo(pdoIndex, 0, (uint)entries.Length);
        Sdo(assignObject, 1, pdoIndex);
        Sdo(assignObject, 0, 1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniSynthesizerTests"`
Expected: FAIL — `EniSynthesizer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/EniSynthesizer.cs`:

```csharp
using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Turns learned facts into an <see cref="EniConfiguration"/> by chaining
/// FMMU → SyncManager → PDO assignment → ESI schema, per design spec §3.
///
/// ESI states what a slave offers; the assignment objects state what the master selected;
/// the SyncManager states where those bytes live in the slave's physical memory; the FMMU
/// maps that window into the master's logical process image. Chaining the four yields a
/// global bit offset per entry — which is exactly what <see cref="EniVariable.BitOffs"/>
/// means, so <see cref="ProcessVariableMap"/> consumes the result unchanged.</summary>
public static class EniSynthesizer
{
    public static EniConfiguration Synthesize(LearnedBus bus,
        IReadOnlyDictionary<ushort, EsiDevice> schemas)
    {
        var slaves = bus.Slaves;
        var inputOrigin = Origin(slaves, FmmuType.Inputs);
        var outputOrigin = Origin(slaves, FmmuType.Outputs);

        var variables = new List<EniVariable>();
        foreach (var slave in slaves)
        {
            schemas.TryGetValue(slave.StationAddress, out var schema);
            var name = schema?.NameEn ?? $"Slave {slave.StationAddress}";
            foreach (var fmmu in slave.Fmmus.Values
                         .Where(f => f.Enabled && f.Type is FmmuType.Inputs or FmmuType.Outputs)
                         .OrderBy(f => f.LogicalStart).ThenBy(f => f.LogicalStartBit))
            {
                var isInput = fmmu.Type == FmmuType.Inputs;
                var origin = isInput ? inputOrigin : outputOrigin;
                var baseBit = (int)((fmmu.LogicalStart - origin) * 8) + fmmu.LogicalStartBit;
                variables.AddRange(VariablesFor(slave, schema, name, fmmu, isInput, baseBit));
            }
        }

        return new EniConfiguration
        {
            Slaves = slaves.Select(s => ToEniSlave(s, schemas)).ToList(),
            CyclicCommands = bus.CyclicCommands
                .Select(c => ToCyclicCommand(c, slaves, inputOrigin, outputOrigin))
                .ToList(),
            Variables = variables,
        };
    }

    /// <summary>The lowest logical byte address covered by any FMMU of a direction. Offsets
    /// are expressed relative to this so they match the ENI convention, where BitOffs is
    /// relative to the whole input or output image.</summary>
    private static uint Origin(IReadOnlyList<LearnedSlave> slaves, FmmuType type)
    {
        var starts = slaves.SelectMany(s => s.Fmmus.Values)
            .Where(f => f.Enabled && f.Type == type)
            .Select(f => f.LogicalStart)
            .ToList();
        return starts.Count == 0 ? 0 : starts.Min();
    }

    private static EniSlave ToEniSlave(LearnedSlave slave,
        IReadOnlyDictionary<ushort, EsiDevice> schemas)
    {
        schemas.TryGetValue(slave.StationAddress, out var schema);
        return new EniSlave(
            schema?.NameEn ?? $"Slave {slave.StationAddress}",
            slave.StationAddress,
            slave.RingPosition >= 0 ? (ushort)(0 - slave.RingPosition) : (ushort)0,
            slave.VendorId ?? 0,
            slave.ProductCode ?? 0,
            slave.Revision ?? 0,
            // Mirrors the ENI parser's mapping: <Mailbox><Send> (the slave's send mailbox,
            // SM1) becomes MailboxOut, <Recv> (SM0) becomes MailboxIn.
            slave.MailboxRange(1),
            slave.MailboxRange(0));
    }

    private static EniCyclicCommand ToCyclicCommand(LearnedCyclicCommand cyclic,
        IReadOnlyList<LearnedSlave> slaves, uint inputOrigin, uint outputOrigin)
    {
        var start = cyclic.RawAddress;
        var end = start + (uint)cyclic.DataLength;
        var intersectsInputs = Intersects(slaves, FmmuType.Inputs, start, end);
        var intersectsOutputs = Intersects(slaves, FmmuType.Outputs, start, end);
        return new EniCyclicCommand(cyclic.Command, cyclic.RawAddress, cyclic.DataLength,
            cyclic.ExpectedWkc,
            intersectsInputs ? (int)(start - inputOrigin) : null,
            intersectsOutputs ? (int)(start - outputOrigin) : null);
    }

    private static bool Intersects(IReadOnlyList<LearnedSlave> slaves, FmmuType type,
        uint start, uint end) =>
        slaves.SelectMany(s => s.Fmmus.Values)
            .Where(f => f.Enabled && f.Type == type)
            .Any(f => f.LogicalStart < end && f.LogicalStart + f.Length > start);

    private static IEnumerable<EniVariable> VariablesFor(LearnedSlave slave, EsiDevice? schema,
        string slaveName, FmmuFact fmmu, bool isInput, int baseBit)
    {
        // Match the SyncManager whose physical window this FMMU maps. Only enabled managers with
        // a non-zero length can carry process data, and the match is ordered by SM number so an
        // ambiguous physical start resolves the same way on every run: an arbitrary dictionary-order
        // pick would resolve 0x1C10 + the WRONG SM number and could yield a real-but-wrong PDO
        // assignment. When nothing matches, this FMMU's variables cannot be placed at all —
        // LearningCompleteness reports the slave as incomplete so the gap is visible.
        var syncManager = slave.SyncManagers.Values
            .Where(sm => sm.Enabled && sm.Length > 0 && sm.PhysicalStart == fmmu.PhysicalStart)
            .OrderBy(sm => sm.Number)
            .FirstOrDefault();
        if (syncManager is null) yield break;

        var bit = baseBit;
        foreach (var pdoIndex in AssignedPdos(slave, schema, syncManager.Number, isInput))
        {
            var pdo = schema?.ProcessData?.Pdos.FirstOrDefault(p => p.Index == pdoIndex);
            foreach (var entry in MappingFor(slave, pdo, pdoIndex))
            {
                if (!entry.IsPadding)
                {
                    var esiEntry = pdo?.Entries.FirstOrDefault(e =>
                        e.Index == entry.Index && (e.SubIndex ?? 0) == entry.SubIndex);
                    yield return new EniVariable(
                        $"{slaveName}.{EntryName(esiEntry, pdo, entry)}",
                        esiEntry?.DataType ?? DefaultDataType(entry.BitLength),
                        entry.BitLength, bit, isInput);
                }
                bit += entry.BitLength;
            }
        }
    }

    /// <summary>Assignment observed on the wire wins; otherwise fall back to the PDOs ESI
    /// declares for this SyncManager, then to any PDO of the right direction.</summary>
    private static IReadOnlyList<ushort> AssignedPdos(LearnedSlave slave, EsiDevice? schema,
        byte syncManagerNumber, bool isInput)
    {
        var observed = slave.AssignedPdos(syncManagerNumber);
        if (observed.Count > 0) return observed;
        IReadOnlyList<EsiPdo> pdos = schema?.ProcessData?.Pdos ?? [];
        var direction = isInput ? EsiPdoDirection.Transmit : EsiPdoDirection.Receive;
        var forSm = pdos.Where(p => p.Direction == direction && p.SyncManager == syncManagerNumber)
            .Select(p => p.Index).ToList();
        return forSm.Count > 0
            ? forSm
            : pdos.Where(p => p.Direction == direction).Select(p => p.Index).ToList();
    }

    /// <summary>Mapping observed on the wire wins; otherwise use the ESI default mapping.</summary>
    private static IReadOnlyList<PdoMappingEntry> MappingFor(LearnedSlave slave, EsiPdo? pdo,
        ushort pdoIndex)
    {
        var observed = slave.Mapping(pdoIndex);
        if (observed.Count > 0) return observed;
        return pdo?.Entries
            .Select(e => new PdoMappingEntry(e.Index, e.SubIndex ?? 0, (byte)e.BitLength))
            .ToList() ?? [];
    }

    private static string EntryName(EsiPdoEntry? esiEntry, EsiPdo? pdo, PdoMappingEntry entry)
    {
        if (esiEntry?.Name is { Length: > 0 } entryName)
            return pdo?.Name is { Length: > 0 } pdoName
                ? $"{pdoName}.{entryName}"
                : entryName;
        return $"0x{entry.Index:X4}:{entry.SubIndex:X2}";
    }

    /// <summary>Width-derived type when ESI states none. Signedness is unknowable from the
    /// wire, so the unsigned form is used and provenance marks the variable as inferred.</summary>
    private static string DefaultDataType(byte bitLength) => bitLength switch
    {
        1 => "BOOL",
        8 => "USINT",
        16 => "UINT",
        32 => "UDINT",
        64 => "ULINT",
        _ => $"BIT{bitLength}",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniSynthesizerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/EniSynthesizer.cs tests/OpenEC.Monitor.Tests/Learning/EniSynthesizerTests.cs
git commit -m "feat(learning): synthesize EniConfiguration from the FMMU offset chain"
```

---

### Task 9: Completeness, provenance and LearnedConfiguration

**Files:**
- Create: `src/OpenEC.Monitor/Learning/LearningCompleteness.cs`
- Create: `src/OpenEC.Monitor/Learning/LearnedConfiguration.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/LearningCompletenessTests.cs`

**Interfaces:**
- Produces: `FactSource`, `FactProvenance`, `SlaveCompleteness`, `LearningCompleteness` (with `Assess(LearnedBus, IReadOnlyDictionary<ushort, EsiDevice>)` and `Summary`), and `LearnedConfiguration`.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/LearningCompletenessTests.cs`:

```csharp
using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class LearningCompletenessTests
{
    private static LearnedBus LearnBringup()
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
        return bus;
    }

    [Fact]
    public void A_full_bringup_is_assessed_as_complete()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.True(completeness.SawStartup);
        Assert.All(completeness.Slaves, s =>
        {
            Assert.True(s.IdentityKnown);
            Assert.True(s.SyncManagersKnown);
            Assert.True(s.FmmusKnown);
            Assert.True(s.PdoMappingKnown);
            Assert.True(s.ProcessDataPlaceable);
        });
    }

    [Fact]
    public void A_mid_run_attach_is_assessed_as_incomplete()
    {
        var bus = new LearnedBus();
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0130u << 16) | 1005, false, false, 0,
                new byte[] { 0x08, 0x00 }, 1),
            FrameDirection.Returning);

        var completeness = LearningCompleteness.Assess(bus, new Dictionary<ushort, EsiDevice>());

        Assert.False(completeness.SawStartup);
        Assert.False(Assert.Single(completeness.Slaves).IdentityKnown);
        Assert.False(completeness.IsComplete);
    }

    [Fact]
    public void Summary_reports_how_many_slaves_are_fully_learned()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.Contains("2/2", completeness.Summary);
    }

    /// <summary>An FMMU whose physical window matches no configured SyncManager cannot have its
    /// variables placed. EniSynthesizer drops them silently, so completeness is the only surface
    /// that can say so — otherwise a short configuration reads as a complete one.</summary>
    [Fact]
    public void An_fmmu_with_no_matching_sync_manager_is_not_placeable()
    {
        var bus = new LearnedBus();
        var t = DateTimeOffset.UnixEpoch;
        void Physical(EtherCatCommand cmd, ushort adp, ushort ado, byte[] payload) =>
            bus.Observe(t, new EtherCatDatagram(cmd, 0, ((uint)ado << 16) | adp, false, false, 0,
                payload, 1), FrameDirection.Outbound);

        Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, BitConverter.GetBytes((ushort)1001));

        // An enabled input FMMU pointing at physical 0x1100 — but no SyncManager is ever configured.
        var fmmu = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(fmmu, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(fmmu, 4);
        fmmu[7] = 7;
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[11] = 1;
        fmmu[12] = 1;
        Physical(EtherCatCommand.Fpwr, 1001, 0x0600, fmmu);

        var slave = Assert.Single(
            LearningCompleteness.Assess(bus, new Dictionary<ushort, EsiDevice>()).Slaves);

        Assert.True(slave.FmmusKnown);
        Assert.False(slave.ProcessDataPlaceable);
        Assert.False(slave.IsComplete);
    }

    [Fact]
    public void Names_are_marked_inferred_when_no_esi_schema_resolved()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.All(completeness.Slaves, s => Assert.False(s.NamesFromEsi));
    }

    /// <summary>A device can carry a name without carrying process data — a bus coupler does, and
    /// so does any modular device, whose PDOs live under &lt;Modules&gt; and are out of the ESI
    /// catalogue's scope. The flag must follow the name, not the process data.</summary>
    [Fact]
    public void Names_come_from_esi_even_when_the_device_declares_no_process_data()
    {
        var bus = LearnBringup();
        var coupler = new EsiDevice(
            VendorName: "Beckhoff Automation GmbH",
            NameEn: "EK1100 EtherCAT Coupler",
            NameDe: null, Group: null, Url: null, EBusCurrentMa: null,
            ObjectDictionary: null, ProcessData: null);
        var schemas = new Dictionary<ushort, EsiDevice> { [1001] = coupler };

        var completeness = LearningCompleteness.Assess(bus, schemas);
        var slave = completeness.Slaves.Single(s => s.StationAddress == 1001);

        Assert.True(slave.NamesFromEsi);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningCompletenessTests"`
Expected: FAIL — `LearningCompleteness` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/LearningCompleteness.cs`:

```csharp
using Dahlke.EtherCAT.Esi;

namespace OpenEC.Monitor.Learning;

/// <summary>Where a learned fact came from. Learning never silently claims a fact it
/// inferred, so every surface can state its own confidence.</summary>
public enum FactSource { Sii, CoeIdentity, RegisterWrite, EsiDefault, Cache, Ads, Inferred }

public sealed record FactProvenance(FactSource Identity, FactSource Names, FactSource Mapping);

/// <param name="ProcessDataPlaceable">True when every enabled process-data FMMU resolves to an
/// enabled SyncManager, so its variables can actually be placed. Vacuously true for a slave with
/// no process data at all — a coupler has nothing to place. False means
/// <see cref="EniSynthesizer"/> silently emits no variables for those FMMUs: it matches
/// SyncManagers by physical address and cannot report a miss itself, so this flag is the only
/// place that gap becomes visible.</param>
public sealed record SlaveCompleteness(ushort StationAddress, bool IdentityKnown,
    bool SyncManagersKnown, bool FmmusKnown, bool PdoMappingKnown, bool NamesFromEsi,
    bool ProcessDataPlaceable)
{
    public bool IsComplete =>
        IdentityKnown && SyncManagersKnown && FmmusKnown && PdoMappingKnown && ProcessDataPlaceable;
}

/// <summary>What learning does and does not know, so the Inspector and CLI can say so
/// plainly rather than presenting a partial picture as a complete one.</summary>
public sealed record LearningCompleteness(bool SawStartup, IReadOnlyList<SlaveCompleteness> Slaves)
{
    public bool IsComplete => SawStartup && Slaves.Count > 0 && Slaves.All(s => s.IsComplete);

    public string Summary
    {
        get
        {
            var complete = Slaves.Count(s => s.IsComplete);
            var text = $"learned {complete}/{Slaves.Count} slaves";
            if (SawStartup) return text;
            return $"{text}; no bus startup observed — restart the master to learn PDO mapping";
        }
    }

    public static LearningCompleteness Assess(LearnedBus bus,
        IReadOnlyDictionary<ushort, EsiDevice> schemas) =>
        new(bus.SawStartup, bus.Slaves.Select(slave =>
        {
            var hasMapping = slave.SyncManagers.Keys
                .Any(sm => slave.AssignedPdos(sm)
                    .Any(pdo => slave.Mapping(pdo).Count > 0));
            var schema = schemas.GetValueOrDefault(slave.StationAddress);
            // Mirrors EniSynthesizer's SyncManager match exactly. `All` over an empty set is true,
            // which is the right answer for a coupler: no process data means nothing to place.
            var placeable = slave.Fmmus.Values
                .Where(f => f.Enabled && f.Type is FmmuType.Inputs or FmmuType.Outputs)
                .All(f => slave.SyncManagers.Values.Any(sm =>
                    sm.Enabled && sm.Length > 0 && sm.PhysicalStart == f.PhysicalStart));
            return new SlaveCompleteness(
                slave.StationAddress,
                slave.IdentityKnown,
                slave.SyncManagers.Count > 0,
                slave.Fmmus.Values.Any(f => f.Enabled),
                hasMapping || schema?.ProcessData?.Pdos.Count > 0,
                // Keyed off the NAME, because that is what EniSynthesizer uses to name the slave
                // (`schema?.NameEn ?? "Slave {addr}"`). `ProcessData` is an independently nullable
                // field: a coupler can carry a name with no process data, and a modular device can
                // carry process data with no name — so testing the wrong field would report a
                // synthetic name as ESI-derived, which is the exact dishonesty this type prevents.
                schema?.NameEn is not null,
                placeable);
        }).ToList());
}
```

`src/OpenEC.Monitor/Learning/LearnedConfiguration.cs`:

```csharp
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Learning;

/// <summary>A learned bus configuration. Wraps <see cref="EniConfiguration"/> rather than
/// replacing it, so every existing consumer — ProcessVariableMap, WkcTracker, BusModel —
/// works against learned and declared configurations identically.</summary>
public sealed record LearnedConfiguration(
    EniConfiguration Configuration,
    LearningCompleteness Completeness,
    IReadOnlyDictionary<ushort, FactProvenance> Provenance,
    int Revision);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearningCompletenessTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/LearningCompleteness.cs src/OpenEC.Monitor/Learning/LearnedConfiguration.cs tests/OpenEC.Monitor.Tests/Learning/LearningCompletenessTests.cs
git commit -m "feat(learning): completeness assessment and LearnedConfiguration wrapper"
```

---

### Task 10: BusLearner orchestrator

**Files:**
- Create: `src/OpenEC.Monitor/Learning/BusLearner.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/BusLearnerTests.cs`

**Interfaces:**
- Consumes: `LearnedBus`, `EniSynthesizer`, `LearningCompleteness`, `EsiEnricher`, `FrameDecodeResult`.
- Produces: `BusLearner(string? esiDirectory = null)`, `.Observe(DateTimeOffset, FrameDecodeResult)`, `.ResolveSchemasAsync(CancellationToken)`, `.Current → LearnedConfiguration?`, event `Action<LearnedConfiguration>? ConfigurationLearned`.

ESI lookup is async and the pump is not, so schema resolution runs as an explicit step: the caller observes frames, then awaits `ResolveSchemasAsync` to fold ESI names in. Plan 2 calls it on a timer for live sessions.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/BusLearnerTests.cs`:

```csharp
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class BusLearnerTests
{
    private static BusLearner Learn(string? esiDirectory = null)
    {
        var learner = new BusLearner(esiDirectory);
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner;
    }

    [Fact]
    public void Observing_a_bringup_produces_a_configuration()
    {
        var learner = Learn();

        Assert.NotNull(learner.Current);
        Assert.Equal(2, learner.Current!.Configuration.Slaves.Count);
        Assert.Equal(16, learner.Current.Configuration.Variables.Count);
    }

    [Fact]
    public void Configuration_revision_increments_only_when_the_picture_changes()
    {
        var learner = Learn();
        var revision = learner.Current!.Revision;

        // Replaying identical cyclic traffic must not churn the revision.
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5).TakeLast(4))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));

        Assert.Equal(revision, learner.Current!.Revision);
    }

    [Fact]
    public void Subscribers_are_notified_when_a_revision_lands()
    {
        var learner = new BusLearner();
        var seen = new List<int>();
        learner.ConfigurationLearned += c => seen.Add(c.Revision);

        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));

        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(r => r), seen);
    }

    [Fact]
    public void Malformed_and_non_ethercat_frames_are_ignored()
    {
        var learner = new BusLearner();

        learner.Observe(DateTimeOffset.UnixEpoch, new FrameDecodeResult.NotEtherCat(0x0800));
        learner.Observe(DateTimeOffset.UnixEpoch, new FrameDecodeResult.Malformed("bad"));

        Assert.Null(learner.Current);
    }

    [Fact]
    public async Task Esi_resolution_replaces_synthetic_slave_names()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.DoesNotContain("Slave 1001", learner.Current!.Configuration.Slaves[0].Name);
        Assert.True(learner.Current.Completeness.Slaves[0].NamesFromEsi);
    }

    /// <summary>The headline capability of the whole milestone: with no ENI at all, a resolved ESI
    /// schema turns bare offsets into named, typed process variables. Asserting both sides of the
    /// transformation matters — without this the feature could ship with every variable still
    /// called "Slave 1001.0x6000:01" and every other test in the plan would still pass.</summary>
    [Fact]
    public async Task Esi_resolution_names_process_variables()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));

        var before = learner.Current!.Configuration.Variables[0];
        Assert.Equal("Slave 1001.0x6000:01", before.Name);
        Assert.Equal("BOOL", before.DataType);

        await learner.ResolveSchemasAsync(CancellationToken.None);

        var after = learner.Current!.Configuration.Variables[0];
        Assert.Equal("EL1008 8Ch. Dig. Input 24V, 3ms.Channel 1.Input 1", after.Name);
        Assert.Equal("BOOL", after.DataType);
        Assert.Equal(0, after.BitOffs);
        Assert.Equal(1, after.BitSize);
    }

    /// <summary>Plan 2 drives schema resolution from a timer, so a call that resolves nothing new
    /// must not publish a revision. Otherwise a converged live session emits a fresh, identical
    /// configuration on every tick.</summary>
    [Fact]
    public async Task Repeated_schema_resolution_does_not_churn_revisions()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        await learner.ResolveSchemasAsync(CancellationToken.None);
        var revision = learner.Current!.Revision;
        var published = 0;
        learner.ConfigurationLearned += _ => published++;

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.Equal(revision, learner.Current!.Revision);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Esi_resolution_without_a_directory_is_a_no_op()
    {
        var learner = Learn();
        var before = learner.Current!.Configuration.Slaves[0].Name;

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.Equal(before, learner.Current!.Configuration.Slaves[0].Name);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BusLearnerTests"`
Expected: FAIL — `BusLearner` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/BusLearner.cs`:

```csharp
using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Drives the decoders, holds the accumulator, and republishes a synthesized
/// configuration whenever the derived picture actually changes. Has no reference to
/// <see cref="BusObserver"/>: it consumes decoded frames and emits configurations, which
/// keeps it testable in isolation and lets the offline discovery pass reuse it verbatim.</summary>
public sealed class BusLearner
{
    private readonly LearnedBus _bus = new();
    private readonly DirectionTracker _direction = new();
    private readonly Dictionary<ushort, EsiDevice> _schemas = new();
    private readonly string? _esiDirectory;
    private string? _lastFingerprint;
    private int _revision;

    public BusLearner(string? esiDirectory = null) => _esiDirectory = esiDirectory;

    /// <summary>The most recent configuration, or null before anything has been learned.</summary>
    public LearnedConfiguration? Current { get; private set; }

    public event Action<LearnedConfiguration>? ConfigurationLearned;

    public void Observe(DateTimeOffset timestamp, FrameDecodeResult decoded)
    {
        if (decoded is not FrameDecodeResult.Success ok) return;
        var direction = _direction.Classify(ok.Frame);
        foreach (var datagram in ok.Frame.Datagrams)
            _bus.Observe(timestamp, datagram, direction);
        Republish();
    }

    /// <summary>Resolves learned identities against the ESI directory and republishes with
    /// vendor names, datatypes and default PDO mappings folded in. Separate from
    /// <see cref="Observe"/> because ESI lookup is async and the capture pump is not.</summary>
    public async Task ResolveSchemasAsync(CancellationToken ct = default)
    {
        if (_esiDirectory is null) return;

        // Nothing left to look up is a true no-op: returning here avoids rebuilding the enricher's
        // ServiceProvider and re-running lookups on every call. Plan 2 drives this from a timer.
        var pending = _bus.Slaves
            .Where(s => !_schemas.ContainsKey(s.StationAddress)
                        && s.VendorId is not null && s.ProductCode is not null)
            .ToList();
        if (pending.Count == 0) return;

        var added = false;
        using var enricher = new EsiEnricher(_esiDirectory);
        foreach (var slave in pending)
        {
            ct.ThrowIfCancellationRequested();
            var device = await enricher.ResolveDeviceAsync(
                slave.VendorId!.Value, slave.ProductCode!.Value, slave.Revision ?? 0);
            if (device is null) continue;
            _schemas[slave.StationAddress] = device;
            added = true;
        }

        // Force only when a schema was actually resolved. Resolution can change completeness and
        // provenance (NamesFromEsi, for one) without changing the configuration fingerprint, so it
        // must not be gated on that fingerprint — but an unconditional force would emit a fresh
        // revision on every call once nothing is left to resolve, which is the churn the
        // fingerprint exists to prevent.
        Republish(force: added);
    }

    /// <summary>Republishes only when the derived configuration would differ, so cyclic
    /// traffic does not churn revisions once the bus is in OP.</summary>
    private void Republish(bool force = false)
    {
        if (_bus.Slaves.Count == 0) return;
        var configuration = EniSynthesizer.Synthesize(_bus, _schemas);
        var fingerprint = Fingerprint(configuration);
        if (!force && fingerprint == _lastFingerprint) return;
        _lastFingerprint = fingerprint;
        Current = new LearnedConfiguration(configuration,
            LearningCompleteness.Assess(_bus, _schemas),
            _bus.Slaves.ToDictionary(s => s.StationAddress, Provenance),
            ++_revision);
        ConfigurationLearned?.Invoke(Current);
    }

    private FactProvenance Provenance(LearnedSlave slave)
    {
        var identity = slave.EepromWords.Count > 0 ? FactSource.Sii
            : slave.IdentityKnown ? FactSource.CoeIdentity
            : FactSource.Inferred;
        var names = _schemas.ContainsKey(slave.StationAddress)
            ? FactSource.EsiDefault
            : FactSource.Inferred;
        var mapping = slave.SyncManagers.Keys.Any(sm => slave.AssignedPdos(sm).Count > 0)
            ? FactSource.RegisterWrite
            : FactSource.EsiDefault;
        return new FactProvenance(identity, names, mapping);
    }

    /// <summary>A cheap structural digest of everything a consumer would notice changing.
    /// Deliberately excludes working counters and cyclic timing, which vary every frame.</summary>
    private static string Fingerprint(Eni.EniConfiguration configuration) =>
        string.Join('|',
            configuration.Slaves.Select(s =>
                $"{s.PhysAddr}:{s.VendorId}:{s.ProductCode}:{s.RevisionNo}:{s.Name}")
            .Concat(configuration.CyclicCommands.Select(c =>
                $"{c.Command}:{c.RawAddress}:{c.DataLength}:{c.ExpectedWkc}"))
            .Concat(configuration.Variables.Select(v =>
                $"{v.Name}:{v.BitOffs}:{v.BitSize}:{v.IsInput}")));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~BusLearnerTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/BusLearner.cs tests/OpenEC.Monitor.Tests/Learning/BusLearnerTests.cs
git commit -m "feat(learning): BusLearner orchestrator with revision publishing"
```

---

### Task 11: ENI XML export

**Files:**
- Create: `src/OpenEC.Monitor/Learning/EniXmlWriter.cs`
- Test: `tests/OpenEC.Monitor.Tests/Learning/EniXmlWriterTests.cs`

**Interfaces:**
- Produces: `EniXmlWriter.Write(EniConfiguration, string path)` and `EniXmlWriter.ToXml(EniConfiguration) → XDocument`. The output must round-trip through the existing `EniConfiguration.Load`, which is what makes the export double as the Plan 2 cache format.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Learning/EniXmlWriterTests.cs`:

```csharp
using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class EniXmlWriterTests
{
    private static EniConfiguration Learned()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!.Configuration;
    }

    private static EniConfiguration RoundTrip(EniConfiguration source)
    {
        using var stream = new MemoryStream();
        EniXmlWriter.ToXml(source).Save(stream);
        stream.Position = 0;
        return EniConfiguration.Load(stream);
    }

    [Fact]
    public void Slaves_survive_a_round_trip()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        Assert.Equal(source.Slaves.Count, reloaded.Slaves.Count);
        Assert.Equal(source.Slaves[0].PhysAddr, reloaded.Slaves[0].PhysAddr);
        Assert.Equal(source.Slaves[0].AutoIncAddr, reloaded.Slaves[0].AutoIncAddr);
        Assert.Equal(source.Slaves[0].VendorId, reloaded.Slaves[0].VendorId);
        Assert.Equal(source.Slaves[0].ProductCode, reloaded.Slaves[0].ProductCode);
        Assert.Equal(source.Slaves[1].Name, reloaded.Slaves[1].Name);
    }

    [Fact]
    public void Mailbox_windows_survive_a_round_trip()
    {
        var reloaded = RoundTrip(Learned());

        Assert.Equal(0x1080, reloaded.Slaves[0].MailboxOut!.Start);
        Assert.Equal(128, reloaded.Slaves[0].MailboxOut!.Length);
        Assert.Equal(0x1000, reloaded.Slaves[0].MailboxIn!.Start);
    }

    [Fact]
    public void Cyclic_commands_survive_a_round_trip()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        var cmd = Assert.Single(reloaded.CyclicCommands);
        Assert.Equal(source.CyclicCommands[0].Command, cmd.Command);
        Assert.Equal(source.CyclicCommands[0].RawAddress, cmd.RawAddress);
        Assert.Equal(source.CyclicCommands[0].DataLength, cmd.DataLength);
        Assert.Equal(source.CyclicCommands[0].ExpectedWkc, cmd.ExpectedWkc);
        Assert.Equal(source.CyclicCommands[0].InputOffs, cmd.InputOffs);
    }

    [Fact]
    public void Variables_survive_a_round_trip_with_their_offsets()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        Assert.Equal(source.Variables.Count, reloaded.Variables.Count);
        Assert.Equal(source.Variables.Select(v => v.BitOffs),
            reloaded.Variables.Select(v => v.BitOffs));
        Assert.Equal(source.Variables.Select(v => v.Name),
            reloaded.Variables.Select(v => v.Name));
        Assert.Equal(source.Variables.Select(v => v.IsInput),
            reloaded.Variables.Select(v => v.IsInput));
    }

    /// <summary>Round-trips a configuration the learner cannot currently produce: outputs as well
    /// as inputs, a physical cyclic command alongside a logical one, a slave with no mailbox beside
    /// one with, and every scalar field set to a distinct value. The learner-derived tests above
    /// are all input-only with a single logical command, so without this the &lt;Outputs&gt; section,
    /// the Adp/Ado branch, and RevisionNo/DataType/BitSize round-trip only vacuously — a writer
    /// that dropped &lt;Outputs&gt; entirely would still pass them.</summary>
    [Fact]
    public void A_configuration_with_outputs_and_physical_commands_survives_a_round_trip()
    {
        var source = new EniConfiguration
        {
            Slaves =
            [
                new EniSlave("Term 1 (EK1100)", 1001, 0x0000, 2, 0x044C2C52, 0x00110000, null, null),
                new EniSlave("Drive 2 (AX5101)", 1002, 0xFFFF, 2, 0x13ED6012, 0x00000001,
                    new MailboxRange(0x1000, 128), new MailboxRange(0x1080, 128)),
            ],
            CyclicCommands =
            [
                new EniCyclicCommand(EtherCatCommand.Lrw, 0x01000000, 4, 6, 0, 8),
                new EniCyclicCommand(EtherCatCommand.Brd, (0x0130u << 16) | 0, 2, 4, null, null),
            ],
            Variables =
            [
                new EniVariable("Drive 2 (AX5101).Inputs.Statusword", "UINT", 16, 16, true),
                new EniVariable("Drive 2 (AX5101).Outputs.Controlword", "UINT", 16, 64, false),
                new EniVariable("Term 1 (EK1100).Outputs.Bit", "BOOL", 1, 0, false),
            ],
            CycleTimeMicroseconds = 1000,
        };

        var reloaded = RoundTrip(source);

        Assert.Equal(0x00110000u, reloaded.Slaves[0].RevisionNo);
        Assert.Null(reloaded.Slaves[0].MailboxOut);
        Assert.Null(reloaded.Slaves[0].MailboxIn);
        Assert.Equal(0x1000, reloaded.Slaves[1].MailboxOut!.Start);
        Assert.Equal(0x1080, reloaded.Slaves[1].MailboxIn!.Start);

        var logical = reloaded.CyclicCommands.Single(c => c.Command == EtherCatCommand.Lrw);
        Assert.Equal(0x01000000u, logical.RawAddress);
        Assert.Equal(0, logical.InputOffs);
        Assert.Equal(8, logical.OutputOffs);

        var physical = reloaded.CyclicCommands.Single(c => c.Command == EtherCatCommand.Brd);
        Assert.Equal((0x0130u << 16) | 0, physical.RawAddress);
        Assert.Null(physical.InputOffs);
        Assert.Null(physical.OutputOffs);

        Assert.Equal(2, reloaded.Variables.Count(v => !v.IsInput));
        var controlword = reloaded.Variables.Single(v => v.Name.EndsWith("Controlword"));
        Assert.False(controlword.IsInput);
        Assert.Equal("UINT", controlword.DataType);
        Assert.Equal(16, controlword.BitSize);
        Assert.Equal(64, controlword.BitOffs);
        Assert.Equal(1000, reloaded.CycleTimeMicroseconds);
    }

    [Fact]
    public void Written_file_is_loadable_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.eni.xml");
        try
        {
            EniXmlWriter.Write(Learned(), path);

            var reloaded = EniConfiguration.Load(path);
            Assert.Equal(2, reloaded.Slaves.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniXmlWriterTests"`
Expected: FAIL — `EniXmlWriter` does not exist.

- [ ] **Step 3: Write the implementation**

`src/OpenEC.Monitor/Learning/EniXmlWriter.cs`:

```csharp
using System.Xml.Linq;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Serialises a configuration as ENI XML in the subset
/// <see cref="EniConfiguration.Load"/> reads back. Learned configurations therefore
/// interoperate with the existing --eni flag, and the same writer serves as the cache
/// format in the integration milestone.</summary>
public static class EniXmlWriter
{
    public static void Write(EniConfiguration configuration, string path) =>
        ToXml(configuration).Save(path);

    public static XDocument ToXml(EniConfiguration configuration) =>
        new(new XElement("EtherCATConfig",
            new XComment(" Generated by OpenEC learning mode from observed bus traffic. "),
            new XElement("Config",
                new XElement("Master",
                    new XElement("Info", new XElement("Name", "EtherCAT Master"))),
                configuration.Slaves.Select(Slave),
                Cyclic(configuration),
                ProcessImage(configuration))));

    private static XElement Slave(EniSlave slave)
    {
        var element = new XElement("Slave",
            new XElement("Info",
                new XElement("Name", slave.Name),
                new XElement("PhysAddr", slave.PhysAddr),
                new XElement("AutoIncAddr", slave.AutoIncAddr),
                new XElement("VendorId", slave.VendorId),
                new XElement("ProductCode", slave.ProductCode),
                new XElement("RevisionNo", slave.RevisionNo)));
        var mailbox = new XElement("Mailbox");
        if (slave.MailboxOut is { } send)
            mailbox.Add(new XElement("Send",
                new XElement("Start", send.Start), new XElement("Length", send.Length)));
        if (slave.MailboxIn is { } recv)
            mailbox.Add(new XElement("Recv",
                new XElement("Start", recv.Start), new XElement("Length", recv.Length)));
        if (mailbox.HasElements) element.Add(mailbox);
        return element;
    }

    /// <summary>Emits `&lt;Cyclic&gt;&lt;Frame&gt;&lt;Cmd&gt;…`, matching the real ENI layout in
    /// `tests/OpenEC.Monitor.Tests/Fixtures/sample.eni.xml`. The parser would accept a flatter
    /// shape (it searches descendants), but the export's purpose is an ENI other tooling can
    /// read, and it doubles as the integration milestone's cache format.</summary>
    private static XElement Cyclic(EniConfiguration configuration)
    {
        var cyclic = new XElement("Cyclic");
        if (configuration.CycleTimeMicroseconds is { } cycleTime)
            cyclic.Add(new XElement("CycleTime", cycleTime));
        var frame = new XElement("Frame");
        cyclic.Add(frame);
        foreach (var command in configuration.CyclicCommands)
        {
            var element = new XElement("Cmd",
                new XElement("Cmd", (int)command.Command),
                new XElement("DataLength", command.DataLength),
                new XElement("Cnt", command.ExpectedWkc));
            if (IsLogical(command.Command))
                element.Add(new XElement("Addr", command.RawAddress));
            else
            {
                element.Add(new XElement("Adp", (ushort)(command.RawAddress & 0xFFFF)));
                element.Add(new XElement("Ado", (ushort)(command.RawAddress >> 16)));
            }
            if (command.InputOffs is { } inputOffs)
                element.Add(new XElement("InputOffs", inputOffs));
            if (command.OutputOffs is { } outputOffs)
                element.Add(new XElement("OutputOffs", outputOffs));
            frame.Add(element);
        }
        return cyclic;
    }

    private static XElement ProcessImage(EniConfiguration configuration) =>
        new("ProcessImage",
            new XElement("Inputs", configuration.Variables.Where(v => v.IsInput).Select(Variable)),
            new XElement("Outputs", configuration.Variables.Where(v => !v.IsInput).Select(Variable)));

    private static XElement Variable(EniVariable variable) =>
        new("Variable",
            new XElement("Name", variable.Name),
            new XElement("DataType", variable.DataType),
            new XElement("BitSize", variable.BitSize),
            new XElement("BitOffs", variable.BitOffs));

    private static bool IsLogical(EtherCatCommand command) =>
        command is EtherCatCommand.Lrd or EtherCatCommand.Lwr or EtherCatCommand.Lrw;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~EniXmlWriterTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Learning/EniXmlWriter.cs tests/OpenEC.Monitor.Tests/Learning/EniXmlWriterTests.cs
git commit -m "feat(learning): export learned configuration as ENI XML"
```

---

### Task 12: `openec learn` command

**Files:**
- Create: `src/OpenEC.CLI/Commands/LearnCommand.cs`
- Modify: `src/OpenEC.CLI/Program.cs` (register the command)
- Test: `tests/OpenEC.Monitor.Tests/Cli/LearnCommandTests.cs`

**Interfaces:**
- Consumes: `BusLearner`, `EniXmlWriter`, `PcapFileSource`, `EtherCatFrameParser`; the existing `TestApp` harness for test invocation.
- Produces: the `learn` verb. Exit codes: 0 learned something, 1 nothing learned, 2 I/O error — matching `AnalyzeCommand`'s convention.

- [ ] **Step 1: Write the failing test**

`tests/OpenEC.Monitor.Tests/Cli/LearnCommandTests.cs`:

```csharp
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Cli;

public class LearnCommandTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"learn-{Guid.NewGuid():N}")).FullName;

    private string Capture()
    {
        var path = Path.Combine(_directory, "bringup.pcap");
        BringupCapture.Write(path, cycles: 5);
        return path;
    }

    [Fact]
    public void Learns_a_bringup_and_reports_coverage()
    {
        var result = new TestApp().Run("learn", Capture());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2/2", result.Output);
    }

    [Fact]
    public void Writes_a_loadable_eni_file()
    {
        var outputPath = Path.Combine(_directory, "bus.eni.xml");

        var result = new TestApp().Run("learn", Capture(), "--out", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, EniConfiguration.Load(outputPath).Slaves.Count);
    }

    [Fact]
    public void A_capture_with_no_ethercat_traffic_exits_one()
    {
        var path = Path.Combine(_directory, "empty.pcap");
        PcapFileWriter.Write(path, []);

        var result = new TestApp().Run("learn", path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nothing", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_file_exits_two()
    {
        var result = new TestApp().Run("learn",
            Path.Combine(_directory, "does-not-exist.pcap"));

        Assert.Equal(2, result.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
```

`TestApp` is the existing internal harness in `tests/OpenEC.Monitor.Tests/Cli/CliTestHarness.cs`. It swaps `AnsiConsole.Console` for a `TestConsole` under a lock and returns `CommandResult(int ExitCode, string Output)`. Do not modify it — the other CLI test classes depend on its current shape.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnCommandTests"`
Expected: FAIL — the `learn` command is not registered.

- [ ] **Step 3: Write the command**

`src/OpenEC.CLI/Commands/LearnCommand.cs`:

```csharp
using System.ComponentModel;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

/// <summary>Reconstructs a bus configuration from a capture that includes bus startup, and
/// optionally writes it out as ENI XML for reuse with --eni.</summary>
public sealed class LearnCommand : AsyncCommand<LearnCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<capture>")]
        [Description("pcap/pcapng file containing bus startup")]
        public string Capture { get; init; } = "";

        [CommandOption("--out")]
        [Description("Write the learned configuration to this ENI XML path")]
        public string? Output { get; init; }

        [CommandOption("--esi-dir")]
        [Description("ESI directory used to resolve device and variable names")]
        public string? EsiDirectory { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var learner = new BusLearner(settings.EsiDirectory);
            await using var source = new PcapFileSource(settings.Capture);
            await foreach (var frame in source.CaptureAsync(cancellationToken))
                learner.Observe(frame.Timestamp, EtherCatFrameParser.Parse(frame.Data));
            await learner.ResolveSchemasAsync(cancellationToken);

            if (learner.Current is not { } learned)
            {
                AnsiConsole.MarkupLine("[yellow]Learned nothing:[/] no EtherCAT slaves observed.");
                return 1;
            }

            Report(learned);
            if (settings.Output is { } output)
            {
                EniXmlWriter.Write(learned.Configuration, output);
                AnsiConsole.MarkupLineInterpolated($"Wrote [green]{output}[/]");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or FileNotFoundException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }

    private static void Report(LearnedConfiguration learned)
    {
        var table = new Table().Title("Learned bus").AddColumn("Addr").AddColumn("Name")
            .AddColumn("Identity").AddColumn("Complete");
        foreach (var slave in learned.Configuration.Slaves)
        {
            var completeness = learned.Completeness.Slaves
                .FirstOrDefault(s => s.StationAddress == slave.PhysAddr);
            table.AddRow(
                slave.PhysAddr.ToString(),
                slave.Name.EscapeMarkup(),
                $"0x{slave.VendorId:X4}:0x{slave.ProductCode:X8}",
                completeness?.IsComplete == true ? "[green]yes[/]" : "[yellow]partial[/]");
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine(learned.Completeness.Summary);
        AnsiConsole.WriteLine(
            $"{learned.Configuration.CyclicCommands.Count} cyclic commands, "
            + $"{learned.Configuration.Variables.Count} process variables.");
    }
}
```

- [ ] **Step 4: Register the command**

In `src/OpenEC.CLI/Program.cs`, inside `Configure`, after the `gen-sample` registration:

```csharp
        config.AddCommand<LearnCommand>("learn")
            .WithDescription("Reconstruct a bus configuration from a startup capture");
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OpenEC.Monitor.Tests --filter "FullyQualifiedName~LearnCommandTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test plus the ~60 added by this plan.

- [ ] **Step 7: Update the README**

In `README.md`, add to the CLI examples block:

```bash
# Generate a synthetic capture that includes bus startup, then reconstruct its configuration
dotnet run --project src/OpenEC.CLI -- gen-sample bringup.pcap --bringup
dotnet run --project src/OpenEC.CLI -- learn bringup.pcap --out bus.eni.xml
```

And in the **Status** section, replace the `**Next**` line with:

```markdown
- **Milestone 3** (in progress): learning mode — ENI-independent bus discovery. The core
  learner reconstructs identity, topology order, PDO mapping and the cyclic command table
  from observed startup traffic and exports it as ENI XML (`openec learn`). Integration into
  live sessions and the Inspector follows.
- **Next**: learning-mode integration (live rebind, learned-config cache, ENI cross-check),
  pcap replay with pacing control, frame-level packet browser, and standalone app packaging.
```

- [ ] **Step 8: Commit**

```bash
git add src/OpenEC.CLI/Commands/LearnCommand.cs src/OpenEC.CLI/Program.cs tests/OpenEC.Monitor.Tests/Cli/LearnCommandTests.cs README.md
git commit -m "feat(cli): openec learn reconstructs a bus configuration from a capture"
```

---

## Verification

After Task 12, learning mode's core is complete and independently useful:

```bash
dotnet run --project src/OpenEC.CLI -- gen-sample /tmp/bringup.pcap --bringup
dotnet run --project src/OpenEC.CLI -- learn /tmp/bringup.pcap --out /tmp/bus.eni.xml
dotnet run --project src/OpenEC.CLI -- analyze /tmp/bringup.pcap --eni /tmp/bus.eni.xml
```

The third command proves the loop closes: a configuration learned from the wire is consumed
by the existing analysis path with no special handling.

**Hardware acceptance (open):** capture a real TwinCAT bringup through the ETAP-1000 and run
`openec learn` against it. The synthetic generator covers the decode paths, but only real
hardware validates that TwinCAT emits the traffic in the shape the decoders expect —
particularly SII reads, which the master may skip entirely when startup checking is disabled.

## Spec §6 degradation coverage

Three of the five degradation rows are exercised here; the other two need Plan 2's cache and
live rebind.

| Spec §6 row | Covered by |
| --- | --- |
| Full INIT→OP observed | Tasks 6, 8, 10 — the whole synthetic bringup path |
| Attach at OP, cache miss | `LearnedBusTests.Attaching_mid_run_without_startup_still_discovers_slaves`, `LearningCompletenessTests.A_mid_run_attach_is_assessed_as_incomplete` |
| Startup checking disabled | `LearnedBusTests.Identity_falls_back_to_the_coe_identity_object` |
| Bus never reaches OP | `EniSynthesizerTests.Padding_entries_…` and `…Output_fmmus_…` synthesize with no cyclic traffic at all |
| Attach at OP, cache hit | **Plan 2** — requires `LearnedBusCache` |

## Deferred to Plan 2 (integration)

`BusObserver.ApplyConfiguration`, swappable `ProcessImage`/`WkcTracker` config, the
`ICaptureSource.SupportsMultiplePasses` two-pass flow, the learned-config cache with bus
fingerprinting, `ConfigurationDiff` and `MonitorEvent.ConfigMismatch`, the `analyze --json`
learning block, and the Inspector completeness strip and "Save learned ENI…" command.
