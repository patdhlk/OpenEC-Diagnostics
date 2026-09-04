# OpenEC-Diagnostics Milestone 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the `OpenEC.Monitor` passive EtherCAT monitoring SDK, the optional `OpenEC.Monitor.Ads` active-diagnostics module, and the `OpenEC.CLI` tool, fully tested.

**Architecture:** Layered SDK — capture sources (SharpPcap) → pure wire-format decoders (Ethernet/EtherCAT/mailbox) → ENI configuration mapping → stateful bus observation (direction pairing, AL states, WKC, process image) → `EtherCatMonitor` facade. CLI sits on the facade; ADS module is a thin adapter over `Dahlke.EtherCAT.Diagnostics`.

**Tech Stack:** .NET 8 / C# 12, SharpPcap, Dahlke.EtherCAT.Esi 0.10.0, Dahlke.EtherCAT.Cia402 0.10.0, Dahlke.TwinCAT.Ads 0.10.0, Dahlke.EtherCAT.Diagnostics 0.10.0, Spectre.Console.Cli, xunit.

**Spec:** `docs/superpowers/specs/2026-08-15-openec-diagnostics-m1-design.md`

## Global Constraints

- Target framework `net8.0` for all projects (set once in `Directory.Build.props`; remove `TargetFramework` from generated csprojs).
- `Nullable` and `ImplicitUsings` enabled solution-wide.
- Core `OpenEC.Monitor` must have **zero** TwinCAT/ADS dependencies. Only `OpenEC.Monitor.Ads` may reference `Dahlke.TwinCAT.Ads` / `Dahlke.EtherCAT.Diagnostics`.
- Pin `Dahlke.*` packages to `0.10.0`; other packages use latest stable.
- All multi-byte EtherCAT wire values are **little-endian**; EtherTypes in the Ethernet header are **big-endian**.
- Decoders never throw for malformed traffic past the parser boundary — the frame parser converts `MalformedFrameException` into `FrameDecodeResult.Malformed`.
- TDD: every task writes its failing test first (a compile error counts as the red step when the type is new). Run tests, then commit. Never `git push`.
- Test commands run from the repo root: `dotnet test --filter "FullyQualifiedName~<TestClass>"`.

---

### Task 1: Solution scaffold

**Files:**
- Create: `Directory.Build.props`, `.gitignore`, `OpenEC-Diagnostics.sln`
- Create: `src/OpenEC.Monitor/OpenEC.Monitor.csproj`, `src/OpenEC.Monitor.Ads/OpenEC.Monitor.Ads.csproj`, `src/OpenEC.CLI/OpenEC.CLI.csproj`, `tests/OpenEC.Monitor.Tests/OpenEC.Monitor.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: the solution every later task builds into. Project references: Tests → Monitor + Monitor.Ads + CLI; CLI → Monitor + Monitor.Ads; Monitor.Ads → Monitor.

- [ ] **Step 1: Scaffold**

```bash
cd ec-brain
dotnet new gitignore
cat > Directory.Build.props <<'XML'
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
XML
dotnet new sln -n OpenEC-Diagnostics
dotnet new classlib -n OpenEC.Monitor -o src/OpenEC.Monitor
dotnet new classlib -n OpenEC.Monitor.Ads -o src/OpenEC.Monitor.Ads
dotnet new console  -n OpenEC.CLI -o src/OpenEC.CLI
dotnet new xunit    -n OpenEC.Monitor.Tests -o tests/OpenEC.Monitor.Tests
rm src/OpenEC.Monitor/Class1.cs src/OpenEC.Monitor.Ads/Class1.cs tests/OpenEC.Monitor.Tests/UnitTest1.cs
dotnet sln add src/OpenEC.Monitor src/OpenEC.Monitor.Ads src/OpenEC.CLI tests/OpenEC.Monitor.Tests
```

Then edit each generated csproj and **delete its `<TargetFramework>` line** so `Directory.Build.props` governs (the templates emit `net10.0` under SDK 10).

- [ ] **Step 2: References and packages**

```bash
dotnet add src/OpenEC.Monitor package SharpPcap
dotnet add src/OpenEC.Monitor package Dahlke.EtherCAT.Esi --version 0.10.0
dotnet add src/OpenEC.Monitor package Dahlke.EtherCAT.Cia402 --version 0.10.0
dotnet add src/OpenEC.Monitor package Microsoft.Extensions.Logging.Abstractions
dotnet add src/OpenEC.Monitor.Ads reference src/OpenEC.Monitor
dotnet add src/OpenEC.Monitor.Ads package Dahlke.EtherCAT.Diagnostics --version 0.10.0
dotnet add src/OpenEC.CLI reference src/OpenEC.Monitor
dotnet add src/OpenEC.CLI reference src/OpenEC.Monitor.Ads
dotnet add src/OpenEC.CLI package Spectre.Console.Cli
dotnet add src/OpenEC.CLI package Spectre.Console
dotnet add tests/OpenEC.Monitor.Tests reference src/OpenEC.Monitor
dotnet add tests/OpenEC.Monitor.Tests reference src/OpenEC.Monitor.Ads
dotnet add tests/OpenEC.Monitor.Tests reference src/OpenEC.CLI
dotnet add tests/OpenEC.Monitor.Tests package Spectre.Console.Testing
```

- [ ] **Step 3: Verify build and empty test run**

Run: `dotnet build && dotnet test`
Expected: build succeeds, 0 tests, exit code 0.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution (Monitor, Monitor.Ads, CLI, Tests)"
```

---

### Task 2: Datagram chain parser

**Files:**
- Create: `src/OpenEC.Monitor/Protocol/EtherCatCommand.cs`, `src/OpenEC.Monitor/Protocol/EtherCatDatagram.cs`, `src/OpenEC.Monitor/Protocol/MalformedFrameException.cs`, `src/OpenEC.Monitor/Protocol/DatagramParser.cs`
- Test: `tests/OpenEC.Monitor.Tests/Protocol/DatagramParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum EtherCatCommand : byte { Nop=0, Aprd=1, Apwr=2, Aprw=3, Fprd=4, Fpwr=5, Fprw=6, Brd=7, Bwr=8, Brw=9, Lrd=10, Lwr=11, Lrw=12, Armw=13, Frmw=14 }`
  - `sealed record EtherCatDatagram(EtherCatCommand Command, byte Index, uint RawAddress, bool Circulating, bool MoreFollows, ushort Irq, ReadOnlyMemory<byte> Payload, ushort WorkingCounter)` with computed `ushort Adp`, `ushort Ado`, `bool IsLogical`, `uint LogicalAddress`
  - `static IReadOnlyList<EtherCatDatagram> DatagramParser.ParseChain(ReadOnlyMemory<byte> data)` — throws `MalformedFrameException` on truncation/unknown command.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Protocol/DatagramParserTests.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class DatagramParserTests
{
    private static byte[] Datagram(byte cmd, byte idx, uint address, byte[] payload,
        ushort wkc, bool more = false, ushort irq = 0)
    {
        var bytes = new byte[12 + payload.Length];
        bytes[0] = cmd;
        bytes[1] = idx;
        BitConverter.GetBytes(address).CopyTo(bytes, 2);
        var lenField = (ushort)(payload.Length & 0x07FF);
        if (more) lenField |= 0x8000;
        BitConverter.GetBytes(lenField).CopyTo(bytes, 6);
        BitConverter.GetBytes(irq).CopyTo(bytes, 8);
        payload.CopyTo(bytes, 10);
        BitConverter.GetBytes(wkc).CopyTo(bytes, 10 + payload.Length);
        return bytes;
    }

    [Fact]
    public void Parses_single_physical_datagram()
    {
        // FPRD ADP=1001 ADO=0x0130, 2-byte payload, WKC=1
        var raw = Datagram(4, 0x21, (0x0130u << 16) | 1001, new byte[] { 0x08, 0x00 }, 1);

        var result = DatagramParser.ParseChain(raw);

        var d = Assert.Single(result);
        Assert.Equal(EtherCatCommand.Fprd, d.Command);
        Assert.Equal(0x21, d.Index);
        Assert.Equal(1001, d.Adp);
        Assert.Equal(0x0130, d.Ado);
        Assert.False(d.IsLogical);
        Assert.Equal(new byte[] { 0x08, 0x00 }, d.Payload.ToArray());
        Assert.Equal(1, d.WorkingCounter);
        Assert.False(d.MoreFollows);
    }

    [Fact]
    public void Parses_chain_of_two_datagrams()
    {
        var first = Datagram(12, 1, 0x01000000, new byte[] { 1, 2, 3, 4 }, 6, more: true);
        var second = Datagram(7, 2, 0x01300000, new byte[] { 0, 0 }, 4);
        var raw = first.Concat(second).ToArray();

        var result = DatagramParser.ParseChain(raw);

        Assert.Equal(2, result.Count);
        Assert.Equal(EtherCatCommand.Lrw, result[0].Command);
        Assert.True(result[0].IsLogical);
        Assert.Equal(0x01000000u, result[0].LogicalAddress);
        Assert.True(result[0].MoreFollows);
        Assert.Equal(EtherCatCommand.Brd, result[1].Command);
        Assert.Equal(0x0130, result[1].Ado);
    }

    [Fact]
    public void Truncated_header_throws()
    {
        var raw = new byte[] { 4, 0, 0, 0, 0 };
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }

    [Fact]
    public void Truncated_payload_throws()
    {
        var raw = Datagram(4, 0, 0, new byte[] { 1, 2, 3, 4 }, 0)[..14];
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }

    [Fact]
    public void Unknown_command_throws()
    {
        var raw = Datagram(99, 0, 0, Array.Empty<byte>(), 0);
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DatagramParserTests"`
Expected: FAIL — compile error, `OpenEC.Monitor.Protocol` types do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Protocol/EtherCatCommand.cs
namespace OpenEC.Monitor.Protocol;

/// <summary>EtherCAT datagram command per ETG.1000.4.</summary>
public enum EtherCatCommand : byte
{
    Nop = 0, Aprd = 1, Apwr = 2, Aprw = 3, Fprd = 4, Fpwr = 5, Fprw = 6,
    Brd = 7, Bwr = 8, Brw = 9, Lrd = 10, Lwr = 11, Lrw = 12, Armw = 13, Frmw = 14,
}
```

```csharp
// src/OpenEC.Monitor/Protocol/MalformedFrameException.cs
namespace OpenEC.Monitor.Protocol;

public sealed class MalformedFrameException(string message) : Exception(message);
```

```csharp
// src/OpenEC.Monitor/Protocol/EtherCatDatagram.cs
namespace OpenEC.Monitor.Protocol;

public sealed record EtherCatDatagram(
    EtherCatCommand Command,
    byte Index,
    uint RawAddress,
    bool Circulating,
    bool MoreFollows,
    ushort Irq,
    ReadOnlyMemory<byte> Payload,
    ushort WorkingCounter)
{
    /// <summary>Position/fixed station address (low 16 bits) for physical commands.</summary>
    public ushort Adp => (ushort)(RawAddress & 0xFFFF);

    /// <summary>Register offset (high 16 bits) for physical commands.</summary>
    public ushort Ado => (ushort)(RawAddress >> 16);

    public bool IsLogical => Command is EtherCatCommand.Lrd or EtherCatCommand.Lwr or EtherCatCommand.Lrw;

    public uint LogicalAddress => RawAddress;
}
```

```csharp
// src/OpenEC.Monitor/Protocol/DatagramParser.cs
using System.Buffers.Binary;

namespace OpenEC.Monitor.Protocol;

public static class DatagramParser
{
    /// <summary>Parses the datagram area of an EtherCAT frame (after the 2-byte frame header).</summary>
    public static IReadOnlyList<EtherCatDatagram> ParseChain(ReadOnlyMemory<byte> data)
    {
        var result = new List<EtherCatDatagram>();
        var span = data.Span;
        var offset = 0;
        while (true)
        {
            if (data.Length - offset < 12)
                throw new MalformedFrameException($"datagram header truncated at offset {offset}");
            var cmdByte = span[offset];
            if (cmdByte > 14)
                throw new MalformedFrameException($"unknown datagram command 0x{cmdByte:X2}");
            var idx = span[offset + 1];
            var address = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 2)..]);
            var lenField = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 6)..]);
            var len = lenField & 0x07FF;
            var circulating = (lenField & 0x4000) != 0;
            var more = (lenField & 0x8000) != 0;
            var irq = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 8)..]);
            if (data.Length - offset < 12 + len)
                throw new MalformedFrameException($"datagram payload truncated at offset {offset}");
            var payload = data.Slice(offset + 10, len);
            var wkc = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 10 + len)..]);
            result.Add(new EtherCatDatagram((EtherCatCommand)cmdByte, idx, address,
                circulating, more, irq, payload, wkc));
            offset += 12 + len;
            if (!more) break;
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DatagramParserTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Protocol tests/OpenEC.Monitor.Tests/Protocol
git commit -m "feat: EtherCAT datagram chain parser"
```

---

### Task 3: Ethernet/EtherCAT frame parser

**Files:**
- Create: `src/OpenEC.Monitor/Protocol/MacAddress.cs`, `src/OpenEC.Monitor/Protocol/EtherCatFrame.cs`, `src/OpenEC.Monitor/Protocol/FrameDecodeResult.cs`, `src/OpenEC.Monitor/Protocol/EtherCatFrameParser.cs`
- Test: `tests/OpenEC.Monitor.Tests/Protocol/EtherCatFrameParserTests.cs`

**Interfaces:**
- Consumes: `DatagramParser.ParseChain`, `EtherCatDatagram`, `MalformedFrameException` (Task 2).
- Produces:
  - `readonly record struct MacAddress(ulong Value)` with `static MacAddress FromBytes(ReadOnlySpan<byte> b)` (6 bytes), `bool IsLocallyAdministered`, `ToString()` → `"aa:bb:cc:dd:ee:ff"`
  - `sealed record EtherCatFrame(MacAddress Destination, MacAddress Source, ushort? VlanId, IReadOnlyList<EtherCatDatagram> Datagrams)`
  - `abstract record FrameDecodeResult` with nested `Success(EtherCatFrame Frame)`, `NotEtherCat(ushort EtherType)`, `Malformed(string Reason)`
  - `static FrameDecodeResult EtherCatFrameParser.Parse(ReadOnlyMemory<byte> frame)`; `const ushort EtherCatFrameParser.EtherCatEtherType = 0x88A4`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Protocol/EtherCatFrameParserTests.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class EtherCatFrameParserTests
{
    private static byte[] Frame(byte[] datagramArea, byte srcFirstOctet = 0x00, bool vlan = false)
    {
        var header = (ushort)(datagramArea.Length | (1 << 12));
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });          // dst
        bytes.AddRange(new byte[] { srcFirstOctet, 0x01, 0x05, 0x10, 0x00, 0x01 }); // src
        if (vlan) bytes.AddRange(new byte[] { 0x81, 0x00, 0x00, 0x2A });            // VLAN 42
        bytes.AddRange(new byte[] { 0x88, 0xA4 });
        bytes.Add((byte)(header & 0xFF));
        bytes.Add((byte)(header >> 8));
        bytes.AddRange(datagramArea);
        return bytes.ToArray();
    }

    private static byte[] NopDatagram()
    {
        var d = new byte[12];
        d[0] = 0; // NOP, len 0, wkc 0
        return d;
    }

    [Fact]
    public void Parses_plain_ethercat_frame()
    {
        var result = EtherCatFrameParser.Parse(Frame(NopDatagram()));

        var ok = Assert.IsType<FrameDecodeResult.Success>(result);
        Assert.Null(ok.Frame.VlanId);
        Assert.Single(ok.Frame.Datagrams);
        Assert.False(ok.Frame.Source.IsLocallyAdministered);
        Assert.Equal("00:01:05:10:00:01", ok.Frame.Source.ToString());
    }

    [Fact]
    public void Parses_vlan_tagged_frame_and_locally_administered_source()
    {
        var result = EtherCatFrameParser.Parse(Frame(NopDatagram(), srcFirstOctet: 0x02, vlan: true));

        var ok = Assert.IsType<FrameDecodeResult.Success>(result);
        Assert.Equal((ushort)42, ok.Frame.VlanId);
        Assert.True(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Non_ethercat_ethertype_is_reported()
    {
        var raw = Frame(NopDatagram());
        raw[12] = 0x08; raw[13] = 0x00; // IPv4
        var result = EtherCatFrameParser.Parse(raw);
        var not = Assert.IsType<FrameDecodeResult.NotEtherCat>(result);
        Assert.Equal((ushort)0x0800, not.EtherType);
    }

    [Fact]
    public void Truncated_frame_is_malformed_not_thrown()
    {
        var raw = Frame(NopDatagram())[..16];
        Assert.IsType<FrameDecodeResult.Malformed>(EtherCatFrameParser.Parse(raw));
    }

    [Fact]
    public void Bad_datagram_area_is_malformed_not_thrown()
    {
        var bad = new byte[12];
        bad[0] = 99; // unknown command
        Assert.IsType<FrameDecodeResult.Malformed>(EtherCatFrameParser.Parse(Frame(bad)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EtherCatFrameParserTests"`
Expected: FAIL — compile error, new types missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Protocol/MacAddress.cs
namespace OpenEC.Monitor.Protocol;

public readonly record struct MacAddress(ulong Value)
{
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        ulong v = 0;
        for (var i = 0; i < 6; i++) v = (v << 8) | bytes[i];
        return new MacAddress(v);
    }

    /// <summary>Bit 0x02 of the first octet — set by EtherCAT slaves on frames returning to the master.</summary>
    public bool IsLocallyAdministered => ((Value >> 40) & 0x02) != 0;

    public override string ToString() => string.Join(":",
        Enumerable.Range(0, 6).Select(i => ((Value >> (8 * (5 - i))) & 0xFF).ToString("x2")));
}
```

```csharp
// src/OpenEC.Monitor/Protocol/EtherCatFrame.cs
namespace OpenEC.Monitor.Protocol;

public sealed record EtherCatFrame(
    MacAddress Destination,
    MacAddress Source,
    ushort? VlanId,
    IReadOnlyList<EtherCatDatagram> Datagrams);
```

```csharp
// src/OpenEC.Monitor/Protocol/FrameDecodeResult.cs
namespace OpenEC.Monitor.Protocol;

public abstract record FrameDecodeResult
{
    public sealed record Success(EtherCatFrame Frame) : FrameDecodeResult;
    public sealed record NotEtherCat(ushort EtherType) : FrameDecodeResult;
    public sealed record Malformed(string Reason) : FrameDecodeResult;
}
```

```csharp
// src/OpenEC.Monitor/Protocol/EtherCatFrameParser.cs
using System.Buffers.Binary;

namespace OpenEC.Monitor.Protocol;

public static class EtherCatFrameParser
{
    public const ushort EtherCatEtherType = 0x88A4;

    public static FrameDecodeResult Parse(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length < 14)
            return new FrameDecodeResult.Malformed("frame shorter than Ethernet header");
        var dst = MacAddress.FromBytes(span[..6]);
        var src = MacAddress.FromBytes(span[6..12]);
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(span[12..]);
        ushort? vlanId = null;
        var offset = 14;
        if (etherType == 0x8100)
        {
            if (span.Length < 18)
                return new FrameDecodeResult.Malformed("VLAN tag truncated");
            vlanId = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(span[14..]) & 0x0FFF);
            etherType = BinaryPrimitives.ReadUInt16BigEndian(span[16..]);
            offset = 18;
        }
        if (etherType != EtherCatEtherType)
            return new FrameDecodeResult.NotEtherCat(etherType);
        if (span.Length < offset + 2)
            return new FrameDecodeResult.Malformed("EtherCAT frame header truncated");
        var header = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
        var length = header & 0x07FF;
        var protocolType = header >> 12;
        if (protocolType != 1)
            return new FrameDecodeResult.Malformed($"unsupported EtherCAT protocol type {protocolType}");
        if (span.Length < offset + 2 + length)
            return new FrameDecodeResult.Malformed("EtherCAT datagram area truncated");
        try
        {
            var datagrams = DatagramParser.ParseChain(frame.Slice(offset + 2, length));
            return new FrameDecodeResult.Success(new EtherCatFrame(dst, src, vlanId, datagrams));
        }
        catch (MalformedFrameException ex)
        {
            return new FrameDecodeResult.Malformed(ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EtherCatFrameParserTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Protocol tests/OpenEC.Monitor.Tests/Protocol
git commit -m "feat: Ethernet II / VLAN / EtherCAT frame parser with tolerant error model"
```

---

### Task 4: Synthetic frame builder + round-trip tests

**Files:**
- Create: `src/OpenEC.Monitor/Synthesis/EtherCatFrameBuilder.cs`
- Test: `tests/OpenEC.Monitor.Tests/Protocol/FrameBuilderRoundTripTests.cs`

**Interfaces:**
- Consumes: `EtherCatCommand`, `EtherCatFrameParser`, `FrameDecodeResult` (Tasks 2–3).
- Produces (used by every later task that fabricates traffic, and by the CLI `gen-sample` command):
  - `sealed class EtherCatFrameBuilder` with:
    - `EtherCatFrameBuilder AsReturning()` — sets the 0x02 bit on the source MAC
    - `EtherCatFrameBuilder AddDatagram(EtherCatCommand cmd, byte idx, uint address, byte[] payload, ushort wkc, ushort irq = 0)`
    - `EtherCatFrameBuilder AddPhysical(EtherCatCommand cmd, byte idx, ushort adp, ushort ado, byte[] payload, ushort wkc)`
    - `byte[] Build()` — Ethernet II + EtherCAT header, more-flag set on all but the last datagram

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Protocol/FrameBuilderRoundTripTests.cs
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Protocol;

public class FrameBuilderRoundTripTests
{
    [Fact]
    public void Built_frame_round_trips_through_parser()
    {
        var raw = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, 7, 0x01000000, new byte[] { 1, 2, 3, 4 }, 6)
            .AddPhysical(EtherCatCommand.Brd, 8, 0, 0x0130, new byte[] { 0x08, 0x00 }, 4)
            .Build();

        var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw));
        Assert.Equal(2, ok.Frame.Datagrams.Count);
        Assert.True(ok.Frame.Datagrams[0].MoreFollows);
        Assert.False(ok.Frame.Datagrams[1].MoreFollows);
        Assert.Equal(0x01000000u, ok.Frame.Datagrams[0].LogicalAddress);
        Assert.Equal(6, ok.Frame.Datagrams[0].WorkingCounter);
        Assert.Equal(0x0130, ok.Frame.Datagrams[1].Ado);
        Assert.False(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Returning_frame_has_locally_administered_source()
    {
        var raw = new EtherCatFrameBuilder()
            .AsReturning()
            .AddDatagram(EtherCatCommand.Lrd, 1, 0, Array.Empty<byte>(), 1)
            .Build();

        var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw));
        Assert.True(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Build_without_datagrams_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new EtherCatFrameBuilder().Build());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FrameBuilderRoundTripTests"`
Expected: FAIL — `OpenEC.Monitor.Synthesis` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Synthesis/EtherCatFrameBuilder.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Composes valid EtherCAT wire images — for tests, demos, and generated sample captures.</summary>
public sealed class EtherCatFrameBuilder
{
    private sealed record PendingDatagram(EtherCatCommand Command, byte Index, uint Address,
        byte[] Payload, ushort Wkc, ushort Irq);

    private readonly List<PendingDatagram> _datagrams = new();
    private readonly byte[] _dst = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private readonly byte[] _src = { 0x00, 0x01, 0x05, 0x10, 0x00, 0x01 };

    public EtherCatFrameBuilder AsReturning()
    {
        _src[0] |= 0x02;
        return this;
    }

    public EtherCatFrameBuilder AddDatagram(EtherCatCommand cmd, byte idx, uint address,
        byte[] payload, ushort wkc, ushort irq = 0)
    {
        _datagrams.Add(new PendingDatagram(cmd, idx, address, payload, wkc, irq));
        return this;
    }

    public EtherCatFrameBuilder AddPhysical(EtherCatCommand cmd, byte idx, ushort adp, ushort ado,
        byte[] payload, ushort wkc)
        => AddDatagram(cmd, idx, ((uint)ado << 16) | adp, payload, wkc);

    public byte[] Build()
    {
        if (_datagrams.Count == 0)
            throw new InvalidOperationException("at least one datagram required");
        var area = new List<byte>();
        for (var i = 0; i < _datagrams.Count; i++)
        {
            var d = _datagrams[i];
            var lenField = (ushort)(d.Payload.Length & 0x07FF);
            if (i < _datagrams.Count - 1) lenField |= 0x8000;
            area.Add((byte)d.Command);
            area.Add(d.Index);
            area.AddRange(BitConverter.GetBytes(d.Address));
            area.AddRange(BitConverter.GetBytes(lenField));
            area.AddRange(BitConverter.GetBytes(d.Irq));
            area.AddRange(d.Payload);
            area.AddRange(BitConverter.GetBytes(d.Wkc));
        }
        var frame = new List<byte>();
        frame.AddRange(_dst);
        frame.AddRange(_src);
        frame.Add(0x88); frame.Add(0xA4);
        var header = (ushort)(area.Count | (1 << 12));
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)(header >> 8));
        frame.AddRange(area);
        return frame.ToArray();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FrameBuilderRoundTripTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Synthesis tests/OpenEC.Monitor.Tests/Protocol
git commit -m "feat: synthetic EtherCAT frame builder with parser round-trip tests"
```

---

### Task 5: Mailbox decoding (CoE / FoE / EoE)

**Files:**
- Create: `src/OpenEC.Monitor/Protocol/Mailbox.cs` (all mailbox record types + enums), `src/OpenEC.Monitor/Protocol/MailboxParser.cs`
- Test: `tests/OpenEC.Monitor.Tests/Protocol/MailboxParserTests.cs`

**Interfaces:**
- Consumes: nothing new (operates on a datagram payload `ReadOnlyMemory<byte>`).
- Produces:
  - `enum MailboxType : byte { Error=0, Aoe=1, Eoe=2, Coe=3, Foe=4, Soe=5, Voe=15 }`
  - `enum CoeService : byte { Emergency=1, SdoRequest=2, SdoResponse=3, TxPdo=4, RxPdo=5, TxPdoRemoteRequest=6, RxPdoRemoteRequest=7, SdoInfo=8 }`
  - `enum FoeOpCode : byte { ReadRequest=1, WriteRequest=2, Data=3, Ack=4, Error=5, Busy=6 }`
  - `sealed record SdoTransfer(byte CommandSpecifier, bool Expedited, bool SizeIndicated, ushort Index, byte SubIndex, ReadOnlyMemory<byte> Data)`
  - `sealed record CoeEmergency(ushort ErrorCode, byte ErrorRegister, ReadOnlyMemory<byte> Data)`
  - `sealed record CoeMessage(ushort Number, CoeService Service, SdoTransfer? Sdo, CoeEmergency? Emergency)`
  - `sealed record FoeMessage(FoeOpCode OpCode, uint PacketNumber, string? FileName, string? ErrorText, ReadOnlyMemory<byte> Data)`
  - `sealed record EoeFragment(byte FrameType, byte Port, bool LastFragment, bool TimeAppended, ushort FragmentNumber, ushort OffsetOrBufferSize, byte FrameNumber)`
  - `sealed record MailboxMessage(ushort Length, ushort StationAddress, byte Channel, byte Priority, MailboxType Type, byte Counter, ReadOnlyMemory<byte> Body, CoeMessage? Coe, FoeMessage? Foe, EoeFragment? Eoe)`
  - `static MailboxMessage? MailboxParser.TryParse(ReadOnlyMemory<byte> payload)` — null when the payload is not a plausible mailbox.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Protocol/MailboxParserTests.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class MailboxParserTests
{
    private static byte[] Mailbox(byte type, byte[] body, ushort station = 1004, byte counter = 1)
    {
        var bytes = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(bytes, 0);
        BitConverter.GetBytes(station).CopyTo(bytes, 2);
        bytes[4] = 0x00;                          // channel 0, priority 0
        bytes[5] = (byte)((counter << 4) | type);
        body.CopyTo(bytes, 6);
        return bytes;
    }

    [Fact]
    public void Parses_coe_expedited_sdo_download_request()
    {
        // CoE header: service SdoRequest (2), number 0. SDO: cs 0x23 (expedited download,
        // size indicated), index 0x1C12, sub 0, data 01 00 00 00.
        var body = new byte[] { 0x00, 0x20, 0x23, 0x12, 0x1C, 0x00, 0x01, 0x00, 0x00, 0x00 };
        var msg = MailboxParser.TryParse(Mailbox(3, body));

        Assert.NotNull(msg);
        Assert.Equal(MailboxType.Coe, msg!.Type);
        Assert.Equal((ushort)1004, msg.StationAddress);
        Assert.NotNull(msg.Coe);
        Assert.Equal(CoeService.SdoRequest, msg.Coe!.Service);
        Assert.NotNull(msg.Coe.Sdo);
        Assert.Equal(0x23, msg.Coe.Sdo!.CommandSpecifier);
        Assert.True(msg.Coe.Sdo.Expedited);
        Assert.True(msg.Coe.Sdo.SizeIndicated);
        Assert.Equal((ushort)0x1C12, msg.Coe.Sdo.Index);
        Assert.Equal(0, msg.Coe.Sdo.SubIndex);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, msg.Coe.Sdo.Data.ToArray());
    }

    [Fact]
    public void Parses_coe_emergency()
    {
        // CoE header: service Emergency (1). Error code 0x8130 (heartbeat), register 0x81.
        var body = new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var msg = MailboxParser.TryParse(Mailbox(3, body));

        Assert.NotNull(msg?.Coe?.Emergency);
        var emcy = msg!.Coe!.Emergency!;
        Assert.Equal((ushort)0x8130, emcy.ErrorCode);
        Assert.Equal(0x81, emcy.ErrorRegister);
        Assert.Equal(5, emcy.Data.Length);
    }

    [Fact]
    public void Parses_foe_write_request_with_filename()
    {
        var body = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00 }
            .Concat("firmware.bin"u8.ToArray()).ToArray();
        var msg = MailboxParser.TryParse(Mailbox(4, body));

        Assert.NotNull(msg?.Foe);
        Assert.Equal(FoeOpCode.WriteRequest, msg!.Foe!.OpCode);
        Assert.Equal("firmware.bin", msg.Foe.FileName);
    }

    [Fact]
    public void Parses_eoe_fragment_header()
    {
        // h1: type 0, port 1, lastFragment set -> 0x0110. h2: fragment 3, offset 2, frameNo 5.
        var h2 = (ushort)(3 | (2 << 6) | (5 << 12));
        var body = new byte[] { 0x10, 0x01, (byte)(h2 & 0xFF), (byte)(h2 >> 8), 0xDE, 0xAD };
        var msg = MailboxParser.TryParse(Mailbox(2, body));

        Assert.NotNull(msg?.Eoe);
        var eoe = msg!.Eoe!;
        Assert.Equal(0, eoe.FrameType);
        Assert.Equal(1, eoe.Port);
        Assert.True(eoe.LastFragment);
        Assert.Equal((ushort)3, eoe.FragmentNumber);
        Assert.Equal((ushort)2, eoe.OffsetOrBufferSize);
        Assert.Equal(5, eoe.FrameNumber);
    }

    [Fact]
    public void Rejects_implausible_payloads()
    {
        Assert.Null(MailboxParser.TryParse(new byte[] { 1, 2, 3 }));                       // too short
        Assert.Null(MailboxParser.TryParse(Mailbox(9, new byte[] { 1, 2 })));              // bad type
        var lied = Mailbox(3, new byte[] { 0x00, 0x20 });
        BitConverter.GetBytes((ushort)500).CopyTo(lied, 0);                                // length > body
        Assert.Null(MailboxParser.TryParse(lied));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MailboxParserTests"`
Expected: FAIL — mailbox types do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Protocol/Mailbox.cs
namespace OpenEC.Monitor.Protocol;

public enum MailboxType : byte { Error = 0, Aoe = 1, Eoe = 2, Coe = 3, Foe = 4, Soe = 5, Voe = 15 }

public enum CoeService : byte
{
    Emergency = 1, SdoRequest = 2, SdoResponse = 3, TxPdo = 4, RxPdo = 5,
    TxPdoRemoteRequest = 6, RxPdoRemoteRequest = 7, SdoInfo = 8,
}

public enum FoeOpCode : byte { ReadRequest = 1, WriteRequest = 2, Data = 3, Ack = 4, Error = 5, Busy = 6 }

public sealed record SdoTransfer(byte CommandSpecifier, bool Expedited, bool SizeIndicated,
    ushort Index, byte SubIndex, ReadOnlyMemory<byte> Data);

public sealed record CoeEmergency(ushort ErrorCode, byte ErrorRegister, ReadOnlyMemory<byte> Data);

public sealed record CoeMessage(ushort Number, CoeService Service, SdoTransfer? Sdo, CoeEmergency? Emergency);

public sealed record FoeMessage(FoeOpCode OpCode, uint PacketNumber, string? FileName,
    string? ErrorText, ReadOnlyMemory<byte> Data);

public sealed record EoeFragment(byte FrameType, byte Port, bool LastFragment, bool TimeAppended,
    ushort FragmentNumber, ushort OffsetOrBufferSize, byte FrameNumber);

public sealed record MailboxMessage(ushort Length, ushort StationAddress, byte Channel, byte Priority,
    MailboxType Type, byte Counter, ReadOnlyMemory<byte> Body,
    CoeMessage? Coe, FoeMessage? Foe, EoeFragment? Eoe);
```

```csharp
// src/OpenEC.Monitor/Protocol/MailboxParser.cs
using System.Buffers.Binary;
using System.Text;

namespace OpenEC.Monitor.Protocol;

public static class MailboxParser
{
    /// <summary>Attempts to interpret a datagram payload as an EtherCAT mailbox. Null when implausible.</summary>
    public static MailboxMessage? TryParse(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 6) return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var station = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        var channel = (byte)(span[4] & 0x3F);
        var priority = (byte)(span[4] >> 6);
        var typeByte = (byte)(span[5] & 0x0F);
        var counter = (byte)((span[5] >> 4) & 0x07);
        if (length == 0 || length > span.Length - 6) return null;
        if (typeByte is > 5 and not 15) return null;
        var type = (MailboxType)typeByte;
        var body = payload.Slice(6, length);
        return new MailboxMessage(length, station, channel, priority, type, counter, body,
            type == MailboxType.Coe ? TryParseCoe(body) : null,
            type == MailboxType.Foe ? TryParseFoe(body) : null,
            type == MailboxType.Eoe ? TryParseEoe(body) : null);
    }

    private static CoeMessage? TryParseCoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 2) return null;
        var header = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var number = (ushort)(header & 0x01FF);
        var serviceByte = (byte)(header >> 12);
        if (serviceByte is < 1 or > 8) return null;
        var service = (CoeService)serviceByte;
        SdoTransfer? sdo = null;
        CoeEmergency? emergency = null;
        if (service is CoeService.SdoRequest or CoeService.SdoResponse && span.Length >= 6)
        {
            var cs = span[2];
            sdo = new SdoTransfer(cs,
                Expedited: (cs & 0x02) != 0,
                SizeIndicated: (cs & 0x01) != 0,
                Index: BinaryPrimitives.ReadUInt16LittleEndian(span[3..]),
                SubIndex: span[5],
                Data: body.Length > 6 ? body[6..] : ReadOnlyMemory<byte>.Empty);
        }
        else if (service == CoeService.Emergency && span.Length >= 5)
        {
            emergency = new CoeEmergency(
                BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
                span[4],
                body.Length > 5 ? body[5..] : ReadOnlyMemory<byte>.Empty);
        }
        return new CoeMessage(number, service, sdo, emergency);
    }

    private static FoeMessage? TryParseFoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 6) return null;
        if (span[0] is < 1 or > 6) return null;
        var opCode = (FoeOpCode)span[0];
        var packet = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
        var data = body.Length > 6 ? body[6..] : ReadOnlyMemory<byte>.Empty;
        string? fileName = null, errorText = null;
        if (opCode is FoeOpCode.ReadRequest or FoeOpCode.WriteRequest)
            fileName = Encoding.ASCII.GetString(data.Span);
        else if (opCode == FoeOpCode.Error)
            errorText = Encoding.ASCII.GetString(data.Span);
        return new FoeMessage(opCode, packet, fileName, errorText, data);
    }

    private static EoeFragment? TryParseEoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 4) return null;
        var h1 = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var h2 = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        return new EoeFragment(
            FrameType: (byte)(h1 & 0x0F),
            Port: (byte)((h1 >> 4) & 0x0F),
            LastFragment: (h1 & 0x0100) != 0,
            TimeAppended: (h1 & 0x0200) != 0,
            FragmentNumber: (ushort)(h2 & 0x3F),
            OffsetOrBufferSize: (ushort)((h2 >> 6) & 0x3F),
            FrameNumber: (byte)(h2 >> 12));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MailboxParserTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Protocol tests/OpenEC.Monitor.Tests/Protocol
git commit -m "feat: mailbox decoding for CoE (SDO, emergency), FoE and EoE headers"
```

---

### Task 6: ENI parser

**Files:**
- Create: `src/OpenEC.Monitor/Eni/EniModels.cs`, `src/OpenEC.Monitor/Eni/EniXmlValues.cs`, `src/OpenEC.Monitor/Eni/EniConfiguration.cs`
- Create: `tests/OpenEC.Monitor.Tests/Fixtures/sample.eni.xml` (mark as `<Content>` with `CopyToOutputDirectory=PreserveNewest` in the test csproj)
- Test: `tests/OpenEC.Monitor.Tests/Eni/EniConfigurationTests.cs`

**Interfaces:**
- Consumes: `EtherCatCommand` (Task 2).
- Produces:
  - `sealed record MailboxRange(ushort Start, ushort Length)` with `bool Contains(ushort ado)`
  - `sealed record EniSlave(string Name, ushort PhysAddr, ushort AutoIncAddr, uint VendorId, uint ProductCode, uint RevisionNo, MailboxRange? MailboxOut, MailboxRange? MailboxIn)`
  - `sealed record EniCyclicCommand(EtherCatCommand Command, uint RawAddress, int DataLength, int ExpectedWkc, int? InputOffs, int? OutputOffs)`
  - `sealed record EniVariable(string Name, string DataType, int BitSize, int BitOffs, bool IsInput)`
  - `sealed class EniConfiguration` with `IReadOnlyList<EniSlave> Slaves`, `IReadOnlyList<EniCyclicCommand> CyclicCommands`, `IReadOnlyList<EniVariable> Variables`, `int? CycleTimeMicroseconds`, `static EniConfiguration Load(string path)`, `static EniConfiguration Load(Stream stream)`
  - `static class EniXmlValues` with `long? ParseNumber(string? text)` handling decimal and `#x` hex literals.

- [ ] **Step 1: Create the fixture**

```xml
<?xml version="1.0" encoding="utf-8"?>
<EtherCATConfig>
  <Config>
    <Master>
      <Info><Name>EtherCAT Master</Name></Info>
    </Master>
    <Slave>
      <Info><Name>Term 1 (EK1100)</Name><PhysAddr>1001</PhysAddr><AutoIncAddr>0</AutoIncAddr><VendorId>2</VendorId><ProductCode>#x044c2c52</ProductCode><RevisionNo>#x00110000</RevisionNo></Info>
    </Slave>
    <Slave>
      <Info><Name>Term 2 (EL1008)</Name><PhysAddr>1002</PhysAddr><AutoIncAddr>65535</AutoIncAddr><VendorId>2</VendorId><ProductCode>#x03f03052</ProductCode><RevisionNo>#x00120000</RevisionNo></Info>
    </Slave>
    <Slave>
      <Info><Name>Term 3 (EL2008)</Name><PhysAddr>1003</PhysAddr><AutoIncAddr>65534</AutoIncAddr><VendorId>2</VendorId><ProductCode>#x07d83052</ProductCode><RevisionNo>#x00110000</RevisionNo></Info>
    </Slave>
    <Slave>
      <Info><Name>Drive 4 (AX5101)</Name><PhysAddr>1004</PhysAddr><AutoIncAddr>65533</AutoIncAddr><VendorId>2</VendorId><ProductCode>#x13ed6012</ProductCode><RevisionNo>#x00000001</RevisionNo></Info>
      <Mailbox>
        <Send><Start>4096</Start><Length>128</Length></Send>
        <Recv><Start>4224</Start><Length>128</Length></Recv>
      </Mailbox>
    </Slave>
    <Cyclic>
      <CycleTime>1000</CycleTime>
      <Frame>
        <Cmd><State>OP</State><Cmd>12</Cmd><Addr>16777216</Addr><DataLength>4</DataLength><Cnt>6</Cnt><InputOffs>0</InputOffs><OutputOffs>0</OutputOffs></Cmd>
        <Cmd><State>OP</State><Cmd>7</Cmd><Adp>0</Adp><Ado>#x0130</Ado><DataLength>2</DataLength><Cnt>4</Cnt></Cmd>
      </Frame>
    </Cyclic>
    <ProcessImage>
      <Inputs>
        <ByteSize>4</ByteSize>
        <Variable><Name>Term 2 (EL1008).Channel 1.Input</Name><DataType>BOOL</DataType><BitSize>1</BitSize><BitOffs>0</BitOffs></Variable>
        <Variable><Name>Term 2 (EL1008).Channel 2.Input</Name><DataType>BOOL</DataType><BitSize>1</BitSize><BitOffs>1</BitOffs></Variable>
        <Variable><Name>Drive 4 (AX5101).Inputs.Statusword</Name><DataType>UINT</DataType><BitSize>16</BitSize><BitOffs>16</BitOffs></Variable>
      </Inputs>
      <Outputs>
        <ByteSize>4</ByteSize>
        <Variable><Name>Term 3 (EL2008).Channel 1.Output</Name><DataType>BOOL</DataType><BitSize>1</BitSize><BitOffs>0</BitOffs></Variable>
        <Variable><Name>Drive 4 (AX5101).Outputs.Controlword</Name><DataType>UINT</DataType><BitSize>16</BitSize><BitOffs>16</BitOffs></Variable>
      </Outputs>
    </ProcessImage>
  </Config>
</EtherCATConfig>
```

Add to `tests/OpenEC.Monitor.Tests/OpenEC.Monitor.Tests.csproj`:

```xml
<ItemGroup>
  <Content Include="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Eni/EniConfigurationTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Eni;

public class EniConfigurationTests
{
    private static EniConfiguration LoadFixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    [Fact]
    public void Parses_slaves_with_identity_and_mailbox()
    {
        var eni = LoadFixture();

        Assert.Equal(4, eni.Slaves.Count);
        var drive = eni.Slaves.Single(s => s.PhysAddr == 1004);
        Assert.Equal("Drive 4 (AX5101)", drive.Name);
        Assert.Equal(2u, drive.VendorId);
        Assert.Equal(0x13ed6012u, drive.ProductCode);
        Assert.NotNull(drive.MailboxOut);
        Assert.Equal((ushort)4096, drive.MailboxOut!.Start);
        Assert.True(drive.MailboxOut.Contains(4100));
        Assert.False(drive.MailboxOut.Contains(5000));
        var coupler = eni.Slaves.Single(s => s.PhysAddr == 1001);
        Assert.Null(coupler.MailboxOut);
        Assert.Equal(0x044c2c52u, coupler.ProductCode); // '#x' hex literal parsed
    }

    [Fact]
    public void Parses_cyclic_commands_with_expected_wkc()
    {
        var eni = LoadFixture();

        Assert.Equal(2, eni.CyclicCommands.Count);
        var lrw = eni.CyclicCommands[0];
        Assert.Equal(EtherCatCommand.Lrw, lrw.Command);
        Assert.Equal(0x01000000u, lrw.RawAddress);
        Assert.Equal(4, lrw.DataLength);
        Assert.Equal(6, lrw.ExpectedWkc);
        Assert.Equal(0, lrw.InputOffs);
        var brd = eni.CyclicCommands[1];
        Assert.Equal(EtherCatCommand.Brd, brd.Command);
        Assert.Equal((uint)(0x0130 << 16), brd.RawAddress);
        Assert.Equal(4, brd.ExpectedWkc);
        Assert.Null(brd.InputOffs);
        Assert.Equal(1000, eni.CycleTimeMicroseconds);
    }

    [Fact]
    public void Parses_process_image_variables()
    {
        var eni = LoadFixture();

        Assert.Equal(5, eni.Variables.Count);
        var sw = eni.Variables.Single(v => v.Name.EndsWith("Statusword"));
        Assert.True(sw.IsInput);
        Assert.Equal("UINT", sw.DataType);
        Assert.Equal(16, sw.BitSize);
        Assert.Equal(16, sw.BitOffs);
        var cw = eni.Variables.Single(v => v.Name.EndsWith("Controlword"));
        Assert.False(cw.IsInput);
    }

    [Fact]
    public void Tolerates_missing_sections()
    {
        using var stream = new MemoryStream(
            "<EtherCATConfig><Config><Slave><Info><Name>S</Name><PhysAddr>1001</PhysAddr></Info></Slave></Config></EtherCATConfig>"u8.ToArray());
        var eni = EniConfiguration.Load(stream);

        Assert.Single(eni.Slaves);
        Assert.Empty(eni.CyclicCommands);
        Assert.Empty(eni.Variables);
        Assert.Null(eni.CycleTimeMicroseconds);
    }

    [Theory]
    [InlineData("#x0130", 0x0130)]
    [InlineData("1000", 1000)]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    public void Parses_eni_number_literals(string? text, int? expected)
    {
        Assert.Equal(expected, (int?)EniXmlValues.ParseNumber(text));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EniConfigurationTests"`
Expected: FAIL — `OpenEC.Monitor.Eni` does not exist.

- [ ] **Step 4: Implement**

```csharp
// src/OpenEC.Monitor/Eni/EniModels.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

public sealed record MailboxRange(ushort Start, ushort Length)
{
    public bool Contains(ushort ado) => ado >= Start && ado < Start + Length;
}

public sealed record EniSlave(string Name, ushort PhysAddr, ushort AutoIncAddr,
    uint VendorId, uint ProductCode, uint RevisionNo,
    MailboxRange? MailboxOut, MailboxRange? MailboxIn);

/// <summary>One command of the master's cyclic frame table. RawAddress matches
/// <see cref="EtherCatDatagram.RawAddress"/> (logical address, or ado&lt;&lt;16|adp).</summary>
public sealed record EniCyclicCommand(EtherCatCommand Command, uint RawAddress,
    int DataLength, int ExpectedWkc, int? InputOffs, int? OutputOffs);

/// <summary>A process-image variable. BitOffs is relative to the whole input or output image.</summary>
public sealed record EniVariable(string Name, string DataType, int BitSize, int BitOffs, bool IsInput);
```

```csharp
// src/OpenEC.Monitor/Eni/EniXmlValues.cs
using System.Globalization;

namespace OpenEC.Monitor.Eni;

public static class EniXmlValues
{
    /// <summary>Parses an ENI numeric literal: decimal, or hex prefixed with '#x'.</summary>
    public static long? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex : null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
            ? dec : null;
    }
}
```

```csharp
// src/OpenEC.Monitor/Eni/EniConfiguration.cs
using System.Xml.Linq;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

/// <summary>Parsed EtherCAT Network Information (ENI) file. Namespace-agnostic and
/// tolerant: missing sections leave the corresponding lists empty.</summary>
public sealed class EniConfiguration
{
    public required IReadOnlyList<EniSlave> Slaves { get; init; }
    public required IReadOnlyList<EniCyclicCommand> CyclicCommands { get; init; }
    public required IReadOnlyList<EniVariable> Variables { get; init; }
    public int? CycleTimeMicroseconds { get; init; }

    public static EniConfiguration Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static EniConfiguration Load(Stream stream)
    {
        var doc = XDocument.Load(stream);
        return new EniConfiguration
        {
            Slaves = ParseSlaves(doc),
            CyclicCommands = ParseCyclic(doc),
            Variables = ParseVariables(doc),
            CycleTimeMicroseconds = (int?)EniXmlValues.ParseNumber(
                Local(doc.Root, "Config", "Cyclic", "CycleTime")?.Value),
        };
    }

    private static IEnumerable<XElement> LocalDescendants(XContainer? node, string name) =>
        node?.Descendants().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private static XElement? Local(XContainer? node, params string[] path)
    {
        var current = node as XElement ?? (node as XDocument)?.Root;
        foreach (var name in path)
        {
            current = current?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (current is null) return null;
        }
        return current;
    }

    private static string? Text(XContainer? parent, string name) =>
        (parent as XElement)?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static IReadOnlyList<EniSlave> ParseSlaves(XDocument doc)
    {
        var slaves = new List<EniSlave>();
        foreach (var el in LocalDescendants(doc.Root, "Slave"))
        {
            var info = Local(el, "Info");
            if (info is null) continue;
            var physAddr = (ushort?)EniXmlValues.ParseNumber(Text(info, "PhysAddr"));
            if (physAddr is null) continue;
            slaves.Add(new EniSlave(
                Text(info, "Name") ?? $"Slave {physAddr}",
                physAddr.Value,
                (ushort)(EniXmlValues.ParseNumber(Text(info, "AutoIncAddr")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "VendorId")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "ProductCode")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "RevisionNo")) ?? 0),
                ParseMailboxRange(Local(el, "Mailbox", "Send")),
                ParseMailboxRange(Local(el, "Mailbox", "Recv"))));
        }
        return slaves;
    }

    private static MailboxRange? ParseMailboxRange(XElement? el)
    {
        if (el is null) return null;
        var start = (ushort?)EniXmlValues.ParseNumber(Text(el, "Start"));
        var length = (ushort?)EniXmlValues.ParseNumber(Text(el, "Length"));
        return start is null || length is null ? null : new MailboxRange(start.Value, length.Value);
    }

    private static IReadOnlyList<EniCyclicCommand> ParseCyclic(XDocument doc)
    {
        var commands = new List<EniCyclicCommand>();
        foreach (var cyclic in LocalDescendants(doc.Root, "Cyclic"))
        foreach (var cmd in LocalDescendants(cyclic, "Cmd"))
        {
            var cmdNumber = EniXmlValues.ParseNumber(Text(cmd, "Cmd"));
            if (cmdNumber is null or < 0 or > 14) continue;
            var addr = EniXmlValues.ParseNumber(Text(cmd, "Addr"));
            uint rawAddress;
            if (addr is not null)
            {
                rawAddress = (uint)addr.Value;
            }
            else
            {
                var adp = EniXmlValues.ParseNumber(Text(cmd, "Adp")) ?? 0;
                var ado = EniXmlValues.ParseNumber(Text(cmd, "Ado")) ?? 0;
                rawAddress = ((uint)ado << 16) | (ushort)adp;
            }
            commands.Add(new EniCyclicCommand(
                (EtherCatCommand)cmdNumber.Value,
                rawAddress,
                (int)(EniXmlValues.ParseNumber(Text(cmd, "DataLength")) ?? 0),
                (int)(EniXmlValues.ParseNumber(Text(cmd, "Cnt")) ?? 0),
                (int?)EniXmlValues.ParseNumber(Text(cmd, "InputOffs")),
                (int?)EniXmlValues.ParseNumber(Text(cmd, "OutputOffs"))));
        }
        return commands;
    }

    private static IReadOnlyList<EniVariable> ParseVariables(XDocument doc)
    {
        var variables = new List<EniVariable>();
        foreach (var image in LocalDescendants(doc.Root, "ProcessImage"))
        {
            foreach (var (section, isInput) in new[] { ("Inputs", true), ("Outputs", false) })
            foreach (var v in LocalDescendants(Local(image, section), "Variable"))
            {
                var name = Text(v, "Name");
                var bitOffs = (int?)EniXmlValues.ParseNumber(Text(v, "BitOffs"));
                if (name is null || bitOffs is null) continue;
                variables.Add(new EniVariable(
                    name,
                    Text(v, "DataType") ?? "UNKNOWN",
                    (int)(EniXmlValues.ParseNumber(Text(v, "BitSize")) ?? 0),
                    bitOffs.Value,
                    isInput));
            }
        }
        return variables;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EniConfigurationTests"`
Expected: PASS, 8 tests (4 facts + 4 theory cases).

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Eni tests/OpenEC.Monitor.Tests
git commit -m "feat: tolerant ENI parser (slaves, cyclic commands, process image)"
```

---

### Task 7: Process variable map + value decoder

**Files:**
- Create: `src/OpenEC.Monitor/Eni/ProcessValueDecoder.cs`, `src/OpenEC.Monitor/Eni/ProcessVariableMap.cs`
- Test: `tests/OpenEC.Monitor.Tests/Eni/ProcessVariableMapTests.cs`

**Interfaces:**
- Consumes: `EniConfiguration`, `EniVariable`, `EniCyclicCommand` (Task 6); `EtherCatDatagram` (Task 2).
- Produces:
  - `sealed record ResolvedVariable(EniVariable Variable, object Value)`
  - `static object ProcessValueDecoder.Decode(string dataType, int bitSize, ReadOnlySpan<byte> payload, int bitOffset)` — BOOL/BIT, (U)SINT/BYTE, (U)INT/WORD, (U)DINT/DWORD, (U)LINT/LWORD, REAL, LREAL; anything else or non-byte-aligned multi-byte → lowercase hex string
  - `sealed class ProcessVariableMap` with `static ProcessVariableMap Build(EniConfiguration eni)`, `IReadOnlyList<ResolvedVariable> ResolveInputs(EtherCatDatagram d)`, `IReadOnlyList<ResolvedVariable> ResolveOutputs(EtherCatDatagram d)` — matches datagrams to ENI cyclic commands by `(Command, RawAddress)`; a variable's offset inside the payload is `BitOffs - InputOffs*8` (or `OutputOffs`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Eni/ProcessVariableMapTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Eni;

public class ProcessVariableMapTests
{
    private static EniConfiguration LoadFixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static EtherCatDatagram Lrw(byte[] payload) => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, payload, 6);

    [Fact]
    public void Resolves_input_variables_from_lrw_payload()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        // byte0: channel1=1, channel2=0; bytes2-3: statusword 0x0637 (Operation enabled)
        var resolved = map.ResolveInputs(Lrw(new byte[] { 0x01, 0x00, 0x37, 0x06 }));

        Assert.Equal(3, resolved.Count);
        Assert.Equal(true, resolved.Single(r => r.Variable.Name.Contains("Channel 1")).Value);
        Assert.Equal(false, resolved.Single(r => r.Variable.Name.Contains("Channel 2")).Value);
        Assert.Equal((ushort)0x0637, resolved.Single(r => r.Variable.Name.EndsWith("Statusword")).Value);
    }

    [Fact]
    public void Resolves_output_variables_from_lrw_payload()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        var resolved = map.ResolveOutputs(Lrw(new byte[] { 0x01, 0x00, 0x0F, 0x00 }));

        Assert.Equal(2, resolved.Count);
        Assert.Equal(true, resolved.Single(r => r.Variable.Name.Contains("Channel 1")).Value);
        Assert.Equal((ushort)0x000F, resolved.Single(r => r.Variable.Name.EndsWith("Controlword")).Value);
    }

    [Fact]
    public void Unmatched_datagram_resolves_to_empty()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        var other = new EtherCatDatagram(EtherCatCommand.Lrw, 1, 0x02000000, false, false, 0,
            new byte[] { 1, 2, 3, 4 }, 6);
        Assert.Empty(map.ResolveInputs(other));
    }

    [Fact]
    public void Short_payload_is_ignored()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        Assert.Empty(map.ResolveInputs(Lrw(new byte[] { 0x01 })));
    }

    [Theory]
    [InlineData("BOOL", 1, 0, true)]
    [InlineData("USINT", 8, 8, (byte)0x37)]
    [InlineData("INT", 16, 8, (short)0x0637)]
    [InlineData("UDINT", 32, 0, 0x06370001u)]
    public void Decodes_primitive_types(string type, int bitSize, int bitOffset, object expected)
    {
        var payload = new byte[] { 0x01, 0x00, 0x37, 0x06 };
        Assert.Equal(expected, ProcessValueDecoder.Decode(type, bitSize, payload, bitOffset));
    }

    [Fact]
    public void Decodes_real_and_falls_back_to_hex()
    {
        var real = BitConverter.GetBytes(1.5f);
        Assert.Equal(1.5f, ProcessValueDecoder.Decode("REAL", 32, real, 0));
        Assert.Equal("0102", ProcessValueDecoder.Decode("SOMESTRUCT", 16, new byte[] { 0x01, 0x02 }, 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ProcessVariableMapTests"`
Expected: FAIL — `ProcessVariableMap` / `ProcessValueDecoder` missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Eni/ProcessValueDecoder.cs
using System.Buffers.Binary;

namespace OpenEC.Monitor.Eni;

public static class ProcessValueDecoder
{
    /// <summary>Decodes an IEC 61131 typed value out of a payload at a bit offset.
    /// Multi-byte types must be byte-aligned; otherwise (or for unknown types) the raw
    /// bytes are returned as a lowercase hex string.</summary>
    public static object Decode(string dataType, int bitSize, ReadOnlySpan<byte> payload, int bitOffset)
    {
        var type = dataType.ToUpperInvariant();
        if (type is "BOOL" or "BIT")
            return ((payload[bitOffset / 8] >> (bitOffset % 8)) & 1) == 1;
        if (bitOffset % 8 != 0)
            return Hex(payload, bitOffset, bitSize);
        var b = payload[(bitOffset / 8)..];
        return type switch
        {
            "BYTE" or "USINT" => b[0],
            "SINT" => (sbyte)b[0],
            "UINT" or "WORD" => BinaryPrimitives.ReadUInt16LittleEndian(b),
            "INT" => BinaryPrimitives.ReadInt16LittleEndian(b),
            "UDINT" or "DWORD" => BinaryPrimitives.ReadUInt32LittleEndian(b),
            "DINT" => BinaryPrimitives.ReadInt32LittleEndian(b),
            "ULINT" or "LWORD" => BinaryPrimitives.ReadUInt64LittleEndian(b),
            "LINT" => BinaryPrimitives.ReadInt64LittleEndian(b),
            "REAL" => BinaryPrimitives.ReadSingleLittleEndian(b),
            "LREAL" => BinaryPrimitives.ReadDoubleLittleEndian(b),
            _ => Hex(payload, bitOffset, bitSize),
        };
    }

    private static string Hex(ReadOnlySpan<byte> payload, int bitOffset, int bitSize)
    {
        var byteStart = bitOffset / 8;
        var byteCount = Math.Max(1, (bitSize + 7) / 8);
        var end = Math.Min(payload.Length, byteStart + byteCount);
        return Convert.ToHexString(payload[byteStart..end]).ToLowerInvariant();
    }
}
```

```csharp
// src/OpenEC.Monitor/Eni/ProcessVariableMap.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

public sealed record ResolvedVariable(EniVariable Variable, object Value);

/// <summary>Maps observed cyclic datagrams onto ENI process-image variables.</summary>
public sealed class ProcessVariableMap
{
    private sealed record Entry(EniVariable Variable, int PayloadBitOffset);

    private readonly Dictionary<(EtherCatCommand, uint), List<Entry>> _inputs = new();
    private readonly Dictionary<(EtherCatCommand, uint), List<Entry>> _outputs = new();

    public static ProcessVariableMap Build(EniConfiguration eni)
    {
        var map = new ProcessVariableMap();
        foreach (var cmd in eni.CyclicCommands)
        {
            var key = (cmd.Command, cmd.RawAddress);
            if (cmd.InputOffs is int inOffs)
                map._inputs[key] = Collect(eni, isInput: true, inOffs, cmd.DataLength);
            if (cmd.OutputOffs is int outOffs)
                map._outputs[key] = Collect(eni, isInput: false, outOffs, cmd.DataLength);
        }
        return map;
    }

    private static List<Entry> Collect(EniConfiguration eni, bool isInput, int imageByteOffset, int dataLength)
    {
        var startBit = imageByteOffset * 8;
        var endBit = startBit + dataLength * 8;
        return eni.Variables
            .Where(v => v.IsInput == isInput && v.BitOffs >= startBit && v.BitOffs + v.BitSize <= endBit)
            .Select(v => new Entry(v, v.BitOffs - startBit))
            .ToList();
    }

    public IReadOnlyList<ResolvedVariable> ResolveInputs(EtherCatDatagram d) => Resolve(_inputs, d);

    public IReadOnlyList<ResolvedVariable> ResolveOutputs(EtherCatDatagram d) => Resolve(_outputs, d);

    private static IReadOnlyList<ResolvedVariable> Resolve(
        Dictionary<(EtherCatCommand, uint), List<Entry>> side, EtherCatDatagram d)
    {
        if (!side.TryGetValue((d.Command, d.RawAddress), out var entries))
            return Array.Empty<ResolvedVariable>();
        var payload = d.Payload.Span;
        var result = new List<ResolvedVariable>(entries.Count);
        foreach (var e in entries)
        {
            if (e.PayloadBitOffset + e.Variable.BitSize > payload.Length * 8) continue;
            result.Add(new ResolvedVariable(e.Variable,
                ProcessValueDecoder.Decode(e.Variable.DataType, e.Variable.BitSize, payload, e.PayloadBitOffset)));
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ProcessVariableMapTests"`
Expected: PASS, 10 tests (6 facts/cases + 4 theory cases).

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Eni tests/OpenEC.Monitor.Tests/Eni
git commit -m "feat: ENI-driven process variable map and IEC-typed value decoder"
```

---

### Task 8: Capture sources + pcap writer

**Files:**
- Create: `src/OpenEC.Monitor/Capture/RawFrame.cs`, `src/OpenEC.Monitor/Capture/ICaptureSource.cs`, `src/OpenEC.Monitor/Capture/PcapFileSource.cs`, `src/OpenEC.Monitor/Capture/LiveCaptureSource.cs`, `src/OpenEC.Monitor/Capture/CaptureDevices.cs`, `src/OpenEC.Monitor/Synthesis/PcapFileWriter.cs`
- Test: `tests/OpenEC.Monitor.Tests/Capture/PcapFileSourceTests.cs`

**Interfaces:**
- Consumes: `EtherCatFrameBuilder` (Task 4).
- Produces:
  - `readonly record struct RawFrame(DateTimeOffset Timestamp, ReadOnlyMemory<byte> Data)`
  - `interface ICaptureSource : IAsyncDisposable { IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default); }`
  - `sealed class PcapFileSource(string path) : ICaptureSource`
  - `sealed class LiveCaptureSource(string interfaceName) : ICaptureSource` — BPF filter `"ether proto 0x88a4 or (vlan and ether proto 0x88a4)"`, promiscuous, channel-buffered; throws `ArgumentException` when the interface does not exist
  - `static IReadOnlyList<(string Name, string? Description)> CaptureDevices.List()`
  - `static void PcapFileWriter.Write(string path, IEnumerable<(DateTimeOffset Timestamp, byte[] Frame)> frames)` — classic little-endian pcap, LINKTYPE_ETHERNET

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Capture/PcapFileSourceTests.cs
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Capture;

public class PcapFileSourceTests
{
    [Fact]
    public async Task Written_pcap_reads_back_with_timestamps_and_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-{Guid.NewGuid():N}.pcap");
        var t0 = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var frame1 = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, 1, 0x01000000, new byte[] { 1, 2, 3, 4 }, 0).Build();
        var frame2 = new EtherCatFrameBuilder().AsReturning()
            .AddDatagram(EtherCatCommand.Lrw, 1, 0x01000000, new byte[] { 5, 6, 7, 8 }, 6).Build();
        PcapFileWriter.Write(path, new[] { (t0, frame1), (t0.AddMilliseconds(1), frame2) });

        try
        {
            await using var source = new PcapFileSource(path);
            var frames = new List<RawFrame>();
            await foreach (var f in source.CaptureAsync()) frames.Add(f);

            Assert.Equal(2, frames.Count);
            Assert.Equal(frame1, frames[0].Data.ToArray());
            Assert.Equal(frame2, frames[1].Data.ToArray());
            Assert.Equal(t0, frames[0].Timestamp);
            var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(frames[1].Data));
            Assert.True(ok.Frame.Source.IsLocallyAdministered);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Live_source_with_unknown_interface_throws()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = new LiveCaptureSource("openec-does-not-exist-0").CaptureAsync().GetAsyncEnumerator();
            _.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void Device_listing_does_not_throw()
    {
        var devices = CaptureDevices.List();
        Assert.NotNull(devices);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PcapFileSourceTests"`
Expected: FAIL — `OpenEC.Monitor.Capture` missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Capture/RawFrame.cs
namespace OpenEC.Monitor.Capture;

public readonly record struct RawFrame(DateTimeOffset Timestamp, ReadOnlyMemory<byte> Data);
```

```csharp
// src/OpenEC.Monitor/Capture/ICaptureSource.cs
namespace OpenEC.Monitor.Capture;

public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default);
}
```

```csharp
// src/OpenEC.Monitor/Capture/PcapFileSource.cs
using System.Runtime.CompilerServices;
using SharpPcap;
using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

/// <summary>Reads pcap and pcapng files via SharpPcap.</summary>
public sealed class PcapFileSource(string path) : ICaptureSource
{
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        using var device = new CaptureFileReaderDevice(path);
        device.Open();
        while (!ct.IsCancellationRequested
               && device.GetNextPacket(out PacketCapture capture) == GetPacketStatus.PacketRead)
        {
            var raw = capture.GetPacket();
            var utc = DateTime.SpecifyKind(raw.Timeval.Date, DateTimeKind.Utc);
            yield return new RawFrame(new DateTimeOffset(utc), raw.Data);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

```csharp
// src/OpenEC.Monitor/Capture/LiveCaptureSource.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpPcap;
using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

/// <summary>Captures EtherCAT frames from a live interface (e.g. the TAP monitor port NIC).</summary>
public sealed class LiveCaptureSource(string interfaceName) : ICaptureSource
{
    public const string BpfFilter = "ether proto 0x88a4 or (vlan and ether proto 0x88a4)";

    private LibPcapLiveDevice? _device;

    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _device = LibPcapLiveDeviceList.Instance
            .FirstOrDefault(d => d.Name == interfaceName)
            ?? throw new ArgumentException($"capture interface '{interfaceName}' not found", nameof(interfaceName));
        var channel = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _device.OnPacketArrival += (_, e) =>
        {
            var raw = e.GetPacket();
            var utc = DateTime.SpecifyKind(raw.Timeval.Date, DateTimeKind.Utc);
            channel.Writer.TryWrite(new RawFrame(new DateTimeOffset(utc), raw.Data));
        };
        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = 250,
            Snaplen = 65536,
            Immediate = true,
        });
        _device.Filter = BpfFilter;
        _device.StartCapture();
        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            yield return frame;
    }

    public ValueTask DisposeAsync()
    {
        if (_device is { } device)
        {
            if (device.Started) device.StopCapture();
            device.Dispose();
            _device = null;
        }
        return ValueTask.CompletedTask;
    }
}
```

```csharp
// src/OpenEC.Monitor/Capture/CaptureDevices.cs
using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

public static class CaptureDevices
{
    public static IReadOnlyList<(string Name, string? Description)> List() =>
        LibPcapLiveDeviceList.Instance.Select(d => (d.Name, (string?)d.Description)).ToList();
}
```

```csharp
// src/OpenEC.Monitor/Synthesis/PcapFileWriter.cs
namespace OpenEC.Monitor.Synthesis;

/// <summary>Writes classic little-endian pcap files (LINKTYPE_ETHERNET) with microsecond timestamps.</summary>
public static class PcapFileWriter
{
    public static void Write(string path, IEnumerable<(DateTimeOffset Timestamp, byte[] Frame)> frames)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0xA1B2C3D4u);
        w.Write((ushort)2); w.Write((ushort)4);
        w.Write(0); w.Write(0u);
        w.Write(65535u);
        w.Write(1u); // LINKTYPE_ETHERNET
        foreach (var (ts, frame) in frames)
        {
            var micros = ts.ToUnixTimeMilliseconds() * 1000 + ts.Microsecond;
            w.Write((uint)(micros / 1_000_000));
            w.Write((uint)(micros % 1_000_000));
            w.Write((uint)frame.Length);
            w.Write((uint)frame.Length);
            w.Write(frame);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PcapFileSourceTests"`
Expected: PASS, 3 tests. (macOS note: file reading needs no BPF permission; only live capture does.)

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Capture src/OpenEC.Monitor/Synthesis tests/OpenEC.Monitor.Tests/Capture
git commit -m "feat: pcap file/live capture sources and pcap writer"
```

---

### Task 9: Direction tracker + traffic statistics

**Files:**
- Create: `src/OpenEC.Monitor/Observation/FrameDirection.cs`, `src/OpenEC.Monitor/Observation/DirectionTracker.cs`, `src/OpenEC.Monitor/Observation/TrafficStatistics.cs`
- Test: `tests/OpenEC.Monitor.Tests/Observation/DirectionTrackerTests.cs`, `tests/OpenEC.Monitor.Tests/Observation/TrafficStatisticsTests.cs`

**Interfaces:**
- Consumes: `EtherCatFrame`, `EtherCatCommand` (Tasks 2–3).
- Produces:
  - `enum FrameDirection { Outbound, Returning }`
  - `sealed class DirectionTracker` with `FrameDirection Classify(EtherCatFrame frame)` — source-MAC 0x02 bit once both bit values have been seen; duplicate-pairing fallback on `(Index, Command, RawAddress)` of the first datagram until then
  - `sealed class TrafficStatistics` with `long TotalFrames/EtherCatFrames/NonEtherCatFrames/MalformedFrames/SuspectedLostFrames`, `long WkcMismatches { get; internal set; }`, `DateTimeOffset? FirstTimestamp/LastTimestamp`, `TimeSpan? EstimatedCycleTime { get; internal set; }`, `IReadOnlyDictionary<EtherCatCommand, long> DatagramsByCommand`, `double? FramesPerSecond`, internal mutators `CountFrame(DateTimeOffset)`, `CountNonEtherCat()`, `CountMalformed()`, `CountDatagram(EtherCatCommand)`, `ObserveOutboundIndex(byte idx)`
  - Make internals testable: add `[assembly: InternalsVisibleTo("OpenEC.Monitor.Tests")]` via `<InternalsVisibleTo Include="OpenEC.Monitor.Tests" />` ItemGroup in `OpenEC.Monitor.csproj`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Observation/DirectionTrackerTests.cs
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class DirectionTrackerTests
{
    private static EtherCatFrame Parse(byte[] raw) =>
        ((FrameDecodeResult.Success)EtherCatFrameParser.Parse(raw)).Frame;

    private static byte[] Cycle(byte idx, bool returning)
    {
        var b = new EtherCatFrameBuilder();
        if (returning) b.AsReturning();
        return b.AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0, 0, 0, 0 },
            (ushort)(returning ? 6 : 0)).Build();
    }

    [Fact]
    public void Mac_bit_classifies_once_both_values_seen()
    {
        var tracker = new DirectionTracker();
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(1, returning: false))));
        Assert.Equal(FrameDirection.Returning, tracker.Classify(Parse(Cycle(1, returning: true))));
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(2, returning: false))));
        Assert.Equal(FrameDirection.Returning, tracker.Classify(Parse(Cycle(2, returning: true))));
    }

    [Fact]
    public void Pairing_fallback_when_mac_bit_never_varies()
    {
        var tracker = new DirectionTracker();
        // All frames outbound-bit-clear (e.g. a tap that strips the bit): pair duplicates.
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(1, returning: false))));
        var second = tracker.Classify(Parse(Cycle(1, returning: false)));
        Assert.Equal(FrameDirection.Returning, second);
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(2, returning: false))));
    }
}
```

```csharp
// tests/OpenEC.Monitor.Tests/Observation/TrafficStatisticsTests.cs
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class TrafficStatisticsTests
{
    [Fact]
    public void Counts_frames_datagrams_and_rates()
    {
        var stats = new TrafficStatistics();
        var t0 = DateTimeOffset.UnixEpoch;
        stats.CountFrame(t0);
        stats.CountDatagram(EtherCatCommand.Lrw);
        stats.CountFrame(t0.AddSeconds(1));
        stats.CountDatagram(EtherCatCommand.Lrw);
        stats.CountNonEtherCat();
        stats.CountMalformed();

        Assert.Equal(4, stats.TotalFrames);
        Assert.Equal(2, stats.EtherCatFrames);
        Assert.Equal(1, stats.NonEtherCatFrames);
        Assert.Equal(1, stats.MalformedFrames);
        Assert.Equal(2, stats.DatagramsByCommand[EtherCatCommand.Lrw]);
        Assert.Equal(2.0, stats.FramesPerSecond!.Value, precision: 3);
    }

    [Fact]
    public void Detects_index_gaps_as_suspected_loss()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(1);
        stats.ObserveOutboundIndex(2);
        stats.ObserveOutboundIndex(5); // gap of 2
        stats.ObserveOutboundIndex(6);

        Assert.Equal(2, stats.SuspectedLostFrames);
    }

    [Fact]
    public void Index_wraparound_is_not_loss()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(255);
        stats.ObserveOutboundIndex(0);
        Assert.Equal(0, stats.SuspectedLostFrames);
    }

    [Fact]
    public void Large_jumps_are_ignored_as_multiplexed_sequences()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(1);
        stats.ObserveOutboundIndex(128); // different idx pool, not loss
        Assert.Equal(0, stats.SuspectedLostFrames);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DirectionTrackerTests|FullyQualifiedName~TrafficStatisticsTests"`
Expected: FAIL — `OpenEC.Monitor.Observation` missing.

- [ ] **Step 3: Implement**

Add to `src/OpenEC.Monitor/OpenEC.Monitor.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="OpenEC.Monitor.Tests" />
</ItemGroup>
```

```csharp
// src/OpenEC.Monitor/Observation/FrameDirection.cs
namespace OpenEC.Monitor.Observation;

public enum FrameDirection { Outbound, Returning }
```

```csharp
// src/OpenEC.Monitor/Observation/DirectionTracker.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Distinguishes outbound from returning frames on an aggregated TAP capture.
/// Primary heuristic: slaves set bit 0x02 of the source MAC's first octet on the return
/// path. Until both bit values have been observed, falls back to pairing duplicate
/// (idx, cmd, address) keys: first sighting is outbound, second is the return.</summary>
public sealed class DirectionTracker
{
    private bool _sawBitSet;
    private bool _sawBitClear;
    private readonly HashSet<(byte, EtherCatCommand, uint)> _pending = new();
    private readonly Queue<(byte, EtherCatCommand, uint)> _pendingOrder = new();

    public FrameDirection Classify(EtherCatFrame frame)
    {
        var bit = frame.Source.IsLocallyAdministered;
        if (bit) _sawBitSet = true; else _sawBitClear = true;
        if (_sawBitSet && _sawBitClear)
            return bit ? FrameDirection.Returning : FrameDirection.Outbound;
        if (frame.Datagrams.Count == 0)
            return FrameDirection.Outbound;
        var d = frame.Datagrams[0];
        var key = (d.Index, d.Command, d.RawAddress);
        if (_pending.Remove(key))
            return FrameDirection.Returning;
        _pending.Add(key);
        _pendingOrder.Enqueue(key);
        while (_pending.Count > 1024)
            _pending.Remove(_pendingOrder.Dequeue());
        return FrameDirection.Outbound;
    }
}
```

```csharp
// src/OpenEC.Monitor/Observation/TrafficStatistics.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

public sealed class TrafficStatistics
{
    private readonly Dictionary<EtherCatCommand, long> _byCommand = new();
    private byte _lastOutboundIdx;
    private bool _hasOutboundIdx;

    public long TotalFrames { get; private set; }
    public long EtherCatFrames { get; private set; }
    public long NonEtherCatFrames { get; private set; }
    public long MalformedFrames { get; private set; }
    public long SuspectedLostFrames { get; private set; }
    public long WkcMismatches { get; internal set; }
    public DateTimeOffset? FirstTimestamp { get; private set; }
    public DateTimeOffset? LastTimestamp { get; private set; }
    public TimeSpan? EstimatedCycleTime { get; internal set; }
    public IReadOnlyDictionary<EtherCatCommand, long> DatagramsByCommand => _byCommand;

    public double? FramesPerSecond
    {
        get
        {
            if (FirstTimestamp is null || LastTimestamp is null) return null;
            var seconds = (LastTimestamp.Value - FirstTimestamp.Value).TotalSeconds;
            return seconds <= 0 ? null : EtherCatFrames / seconds;
        }
    }

    internal void CountFrame(DateTimeOffset ts)
    {
        TotalFrames++;
        EtherCatFrames++;
        FirstTimestamp ??= ts;
        LastTimestamp = ts;
    }

    internal void CountNonEtherCat() { TotalFrames++; NonEtherCatFrames++; }

    internal void CountMalformed() { TotalFrames++; MalformedFrames++; }

    internal void CountDatagram(EtherCatCommand cmd) =>
        _byCommand[cmd] = _byCommand.GetValueOrDefault(cmd) + 1;

    /// <summary>Heuristic frame-loss detection over the master's outbound idx sequence.
    /// Gaps of 2–63 count as loss; larger jumps are treated as a different idx pool.</summary>
    internal void ObserveOutboundIndex(byte idx)
    {
        if (_hasOutboundIdx)
        {
            var delta = (byte)(idx - _lastOutboundIdx);
            if (delta is > 1 and < 64) SuspectedLostFrames += delta - 1;
        }
        _lastOutboundIdx = idx;
        _hasOutboundIdx = true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DirectionTrackerTests|FullyQualifiedName~TrafficStatisticsTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor tests/OpenEC.Monitor.Tests/Observation
git commit -m "feat: TAP direction classification and traffic statistics"
```

---

### Task 10: Cycle estimator

**Files:**
- Create: `src/OpenEC.Monitor/Observation/CycleEstimator.cs`
- Test: `tests/OpenEC.Monitor.Tests/Observation/CycleEstimatorTests.cs`

**Interfaces:**
- Consumes: `EtherCatDatagram`, `FrameDirection` (Tasks 2, 9).
- Produces: `sealed class CycleEstimator` with `void Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)` (only outbound logical datagrams are considered) and `TimeSpan? EstimatedCycleTime` (median of consecutive deltas of the most frequent `(Command, RawAddress)` key; null below 8 samples; sliding window of 128 timestamps).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Observation/CycleEstimatorTests.cs
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class CycleEstimatorTests
{
    private static EtherCatDatagram Lrw() => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, new byte[4], 0);

    [Fact]
    public void Estimates_cycle_time_from_outbound_lrw_cadence()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 20; i++)
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Outbound);

        Assert.NotNull(estimator.EstimatedCycleTime);
        Assert.Equal(1.0, estimator.EstimatedCycleTime!.Value.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Returning_frames_and_physical_commands_are_ignored()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        var brd = new EtherCatDatagram(EtherCatCommand.Brd, 1, 0x01300000, false, false, 0, new byte[2], 0);
        for (var i = 0; i < 20; i++)
        {
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Returning);
            estimator.Observe(t0.AddMilliseconds(i), brd, FrameDirection.Outbound);
        }
        Assert.Null(estimator.EstimatedCycleTime);
    }

    [Fact]
    public void Too_few_samples_yield_null()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++)
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Outbound);
        Assert.Null(estimator.EstimatedCycleTime);
    }

    [Fact]
    public void Median_is_robust_against_one_outlier()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        var t = t0;
        for (var i = 0; i < 20; i++)
        {
            t = t.AddMilliseconds(i == 10 ? 50 : 1); // one late frame
            estimator.Observe(t, Lrw(), FrameDirection.Outbound);
        }
        Assert.Equal(1.0, estimator.EstimatedCycleTime!.Value.TotalMilliseconds, precision: 3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CycleEstimatorTests"`
Expected: FAIL — `CycleEstimator` missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Observation/CycleEstimator.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Estimates the bus cycle time from the cadence of the most frequent
/// outbound logical (cyclic) datagram.</summary>
public sealed class CycleEstimator
{
    private const int WindowSize = 128;
    private const int MinSamples = 8;

    private readonly Dictionary<(EtherCatCommand, uint), Queue<DateTimeOffset>> _timestamps = new();
    private readonly Dictionary<(EtherCatCommand, uint), long> _counts = new();

    public void Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !d.IsLogical) return;
        var key = (d.Command, d.RawAddress);
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
        if (!_timestamps.TryGetValue(key, out var queue))
            _timestamps[key] = queue = new Queue<DateTimeOffset>();
        queue.Enqueue(ts);
        while (queue.Count > WindowSize) queue.Dequeue();
    }

    public TimeSpan? EstimatedCycleTime
    {
        get
        {
            if (_counts.Count == 0) return null;
            var top = _counts.MaxBy(kv => kv.Value).Key;
            var samples = _timestamps[top].ToArray();
            if (samples.Length < MinSamples) return null;
            var deltas = new List<double>(samples.Length - 1);
            for (var i = 1; i < samples.Length; i++)
                deltas.Add((samples[i] - samples[i - 1]).TotalMilliseconds);
            deltas.Sort();
            return TimeSpan.FromMilliseconds(deltas[deltas.Count / 2]);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CycleEstimatorTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Observation tests/OpenEC.Monitor.Tests/Observation
git commit -m "feat: cycle-time estimation from cyclic datagram cadence"
```

---

### Task 11: Slave state tracking

**Files:**
- Create: `src/OpenEC.Monitor/Observation/SlaveAlState.cs`, `src/OpenEC.Monitor/Observation/SlaveStatus.cs`, `src/OpenEC.Monitor/Observation/BusModel.cs`, `src/OpenEC.Monitor/Observation/MonitorEvents.cs`, `src/OpenEC.Monitor/Observation/SlaveStateTracker.cs`
- Test: `tests/OpenEC.Monitor.Tests/Observation/SlaveStateTrackerTests.cs`

**Interfaces:**
- Consumes: `EtherCatDatagram`, `EtherCatCommand` (Task 2), `FrameDirection` (Task 9), `EniConfiguration`/`EniSlave` (Task 6).
- Produces:
  - `enum SlaveAlState : byte { Unknown=0, Init=1, PreOp=2, Boot=3, SafeOp=4, Op=8 }`
  - `sealed class SlaveStatus { ushort Address; string? ConfiguredName; string? ResolvedDeviceName; uint? VendorId/ProductCode/Revision; SlaveAlState AlState; bool ErrorFlag; ushort? AlStatusCode; DateTimeOffset? LastSeen; }` (all settable)
  - `sealed class BusModel` with `IReadOnlyCollection<SlaveStatus> Slaves`, `SlaveAlState BusState`, `bool BusStateUniform`, `SlaveStatus GetOrAdd(ushort address)`, `bool TryGet(ushort address, out SlaveStatus)`, `void Seed(EniConfiguration eni)`, `bool TryMapAutoInc(ushort autoIncAdp, out ushort configuredAddress)`
  - `abstract record MonitorEvent(DateTimeOffset Timestamp)` with `sealed record SlaveStateChanged(DateTimeOffset Timestamp, ushort Address, SlaveAlState OldState, SlaveAlState NewState, bool ErrorFlag) : MonitorEvent(Timestamp)` and `sealed record StateChangeRequested(DateTimeOffset Timestamp, ushort Address, SlaveAlState RequestedState) : MonitorEvent(Timestamp)` (later tasks append more event records to `MonitorEvents.cs`)
  - `sealed class SlaveStateTracker(BusModel model)` with `IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)`

AL registers: AL Control is ADO 0x0120 (master FPWR, low nibble = requested state), AL Status is ADO 0x0130 (low nibble state, bit 0x10 error), AL Status Code is ADO 0x0134. A BRD of 0x0130 carries the OR of all slaves' registers: it updates `BusState`, with `BusStateUniform = exactly one bit set in the low nibble`. `Aprd` reads are mapped through `TryMapAutoInc` (ENI `AutoIncAddr` equals the wire ADP) and skipped when unmapped. Per-slave updates require `WorkingCounter > 0`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Observation/SlaveStateTrackerTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class SlaveStateTrackerTests
{
    private static EtherCatDatagram Physical(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc) =>
        new(cmd, 1, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Fprd_al_status_updates_slave_and_raises_event()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1),
            FrameDirection.Returning).ToList();

        var evt = Assert.IsType<MonitorEvent.SlaveStateChanged>(Assert.Single(events));
        Assert.Equal((ushort)1004, evt.Address);
        Assert.Equal(SlaveAlState.SafeOp, evt.NewState);
        Assert.True(evt.ErrorFlag);
        Assert.True(model.TryGet(1004, out var slave));
        Assert.Equal(SlaveAlState.SafeOp, slave!.AlState);
        Assert.True(slave.ErrorFlag);
        Assert.Equal(t, slave.LastSeen);
    }

    [Fact]
    public void Unchanged_state_raises_no_event()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var d = Physical(EtherCatCommand.Fprd, 1002, 0x0130, new byte[] { 0x08, 0x00 }, 1);

        Assert.Single(tracker.Observe(DateTimeOffset.UnixEpoch, d, FrameDirection.Returning));
        Assert.Empty(tracker.Observe(DateTimeOffset.UnixEpoch.AddSeconds(1), d, FrameDirection.Returning));
    }

    [Fact]
    public void Zero_wkc_is_ignored()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var events = tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Fprd, 1002, 0x0130, new byte[] { 0x08, 0x00 }, 0),
            FrameDirection.Returning);
        Assert.Empty(events);
        Assert.False(model.TryGet(1002, out _));
    }

    [Fact]
    public void Brd_updates_bus_state()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Brd, 0, 0x0130, new byte[] { 0x08, 0x00 }, 4),
            FrameDirection.Returning).ToList();

        Assert.Equal(SlaveAlState.Op, model.BusState);
        Assert.True(model.BusStateUniform);

        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Brd, 0, 0x0130, new byte[] { 0x0C, 0x00 }, 4),
            FrameDirection.Returning).ToList(); // Op | SafeOp mixed
        Assert.False(model.BusStateUniform);
    }

    [Fact]
    public void Al_control_write_raises_state_change_requested()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var events = tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Fpwr, 1004, 0x0120, new byte[] { 0x04, 0x00 }, 0),
            FrameDirection.Outbound).ToList();

        var evt = Assert.IsType<MonitorEvent.StateChangeRequested>(Assert.Single(events));
        Assert.Equal(SlaveAlState.SafeOp, evt.RequestedState);
        Assert.Equal((ushort)1004, evt.Address);
    }

    [Fact]
    public void Aprd_maps_through_eni_auto_increment_addresses()
    {
        var eni = EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var model = new BusModel();
        model.Seed(eni);
        var tracker = new SlaveStateTracker(model);

        // AutoIncAddr 65535 is 'Term 2 (EL1008)' -> PhysAddr 1002
        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Aprd, 65535, 0x0130, new byte[] { 0x02, 0x00 }, 1),
            FrameDirection.Returning).ToList();

        Assert.True(model.TryGet(1002, out var slave));
        Assert.Equal(SlaveAlState.PreOp, slave!.AlState);
        Assert.Equal("Term 2 (EL1008)", slave.ConfiguredName);
    }

    [Fact]
    public void Seed_populates_slaves_from_eni()
    {
        var eni = EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var model = new BusModel();
        model.Seed(eni);

        Assert.Equal(4, model.Slaves.Count);
        Assert.True(model.TryGet(1004, out var drive));
        Assert.Equal(0x13ed6012u, drive!.ProductCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SlaveStateTrackerTests"`
Expected: FAIL — new types missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Observation/SlaveAlState.cs
namespace OpenEC.Monitor.Observation;

public enum SlaveAlState : byte { Unknown = 0, Init = 1, PreOp = 2, Boot = 3, SafeOp = 4, Op = 8 }
```

```csharp
// src/OpenEC.Monitor/Observation/SlaveStatus.cs
namespace OpenEC.Monitor.Observation;

public sealed class SlaveStatus
{
    public required ushort Address { get; init; }
    public string? ConfiguredName { get; set; }
    public string? ResolvedDeviceName { get; set; }
    public uint? VendorId { get; set; }
    public uint? ProductCode { get; set; }
    public uint? Revision { get; set; }
    public SlaveAlState AlState { get; set; }
    public bool ErrorFlag { get; set; }
    public ushort? AlStatusCode { get; set; }
    public DateTimeOffset? LastSeen { get; set; }

    public string DisplayName => ConfiguredName ?? ResolvedDeviceName ?? $"Slave {Address}";
}
```

```csharp
// src/OpenEC.Monitor/Observation/BusModel.cs
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Observation;

public sealed class BusModel
{
    private readonly Dictionary<ushort, SlaveStatus> _slaves = new();
    private readonly Dictionary<ushort, ushort> _autoIncToPhys = new();

    public IReadOnlyCollection<SlaveStatus> Slaves => _slaves.Values;
    public SlaveAlState BusState { get; internal set; }
    public bool BusStateUniform { get; internal set; }

    public SlaveStatus GetOrAdd(ushort address)
    {
        if (!_slaves.TryGetValue(address, out var slave))
            _slaves[address] = slave = new SlaveStatus { Address = address };
        return slave;
    }

    public bool TryGet(ushort address, out SlaveStatus? slave) =>
        _slaves.TryGetValue(address, out slave);

    public bool TryMapAutoInc(ushort autoIncAdp, out ushort configuredAddress) =>
        _autoIncToPhys.TryGetValue(autoIncAdp, out configuredAddress);

    public void Seed(EniConfiguration eni)
    {
        foreach (var s in eni.Slaves)
        {
            var slave = GetOrAdd(s.PhysAddr);
            slave.ConfiguredName = s.Name;
            slave.VendorId = s.VendorId;
            slave.ProductCode = s.ProductCode;
            slave.Revision = s.RevisionNo;
            _autoIncToPhys[s.AutoIncAddr] = s.PhysAddr;
        }
    }
}
```

```csharp
// src/OpenEC.Monitor/Observation/MonitorEvents.cs
namespace OpenEC.Monitor.Observation;

public abstract record MonitorEvent(DateTimeOffset Timestamp)
{
    public sealed record SlaveStateChanged(DateTimeOffset Timestamp, ushort Address,
        SlaveAlState OldState, SlaveAlState NewState, bool ErrorFlag) : MonitorEvent(Timestamp);

    public sealed record StateChangeRequested(DateTimeOffset Timestamp, ushort Address,
        SlaveAlState RequestedState) : MonitorEvent(Timestamp);
}
```

```csharp
// src/OpenEC.Monitor/Observation/SlaveStateTracker.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Derives per-slave AL states from observed register traffic
/// (AL Control 0x0120, AL Status 0x0130, AL Status Code 0x0134).</summary>
public sealed class SlaveStateTracker(BusModel model)
{
    private const ushort AlControl = 0x0120;
    private const ushort AlStatus = 0x0130;
    private const ushort AlStatusCode = 0x0134;

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (d.IsLogical) yield break;

        if (dir == FrameDirection.Outbound
            && d.Command is EtherCatCommand.Fpwr
            && d.Ado == AlControl
            && d.Payload.Length >= 1)
        {
            yield return new MonitorEvent.StateChangeRequested(ts, d.Adp,
                ToState((byte)(d.Payload.Span[0] & 0x0F)));
            yield break;
        }

        if (dir != FrameDirection.Returning || d.WorkingCounter == 0 || d.Payload.Length < 1)
            yield break;

        if (d.Command == EtherCatCommand.Brd && d.Ado == AlStatus)
        {
            var raw = d.Payload.Span[0];
            model.BusState = ToState((byte)(raw & 0x0F));
            model.BusStateUniform = System.Numerics.BitOperations.PopCount((uint)(raw & 0x0F)) == 1;
            yield break;
        }

        if (d.Command is not (EtherCatCommand.Fprd or EtherCatCommand.Aprd)) yield break;
        var address = d.Adp;
        if (d.Command == EtherCatCommand.Aprd && !model.TryMapAutoInc(d.Adp, out address))
            yield break;

        if (d.Ado == AlStatus)
        {
            var raw = d.Payload.Span[0];
            var newState = ToState((byte)(raw & 0x0F));
            var error = (raw & 0x10) != 0;
            var slave = model.GetOrAdd(address);
            slave.LastSeen = ts;
            if (slave.AlState != newState || slave.ErrorFlag != error)
            {
                var old = slave.AlState;
                slave.AlState = newState;
                slave.ErrorFlag = error;
                yield return new MonitorEvent.SlaveStateChanged(ts, address, old, newState, error);
            }
        }
        else if (d.Ado == AlStatusCode && d.Payload.Length >= 2)
        {
            var slave = model.GetOrAdd(address);
            slave.LastSeen = ts;
            slave.AlStatusCode = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(d.Payload.Span);
        }
        else if (d.Command == EtherCatCommand.Fprd)
        {
            model.GetOrAdd(address).LastSeen = ts;
        }
    }

    private static SlaveAlState ToState(byte nibble) =>
        nibble is 1 or 2 or 3 or 4 or 8 ? (SlaveAlState)nibble : SlaveAlState.Unknown;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SlaveStateTrackerTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Observation tests/OpenEC.Monitor.Tests/Observation
git commit -m "feat: slave AL state tracking from register traffic"
```

---

### Task 12: WKC tracker

**Files:**
- Create: `src/OpenEC.Monitor/Observation/WkcTracker.cs`
- Modify: `src/OpenEC.Monitor/Observation/MonitorEvents.cs` (add `WkcMismatchDetected`)
- Test: `tests/OpenEC.Monitor.Tests/Observation/WkcTrackerTests.cs`

**Interfaces:**
- Consumes: `EtherCatDatagram` (Task 2), `EniConfiguration`/`EniCyclicCommand` (Task 6), `FrameDirection`, `MonitorEvent` (Tasks 9, 11).
- Produces:
  - `sealed record MonitorEvent.WkcMismatchDetected(DateTimeOffset Timestamp, EtherCatCommand Command, uint Address, ushort Expected, ushort Actual) : MonitorEvent(Timestamp)` — added inside the existing `MonitorEvent` record
  - `sealed class WkcTracker(EniConfiguration? eni = null)` with `MonitorEvent.WkcMismatchDetected? Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)` — only returning `Brd/Lrd/Lwr/Lrw` datagrams (or any key present in the ENI cyclic table) are checked; expected WKC comes from ENI `Cnt` when the `(Command, RawAddress)` key matches, otherwise it is learned as the mode of the first 20+ observations per key.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Observation/WkcTrackerTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class WkcTrackerTests
{
    private static EniConfiguration Fixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static EtherCatDatagram Lrw(ushort wkc) => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, new byte[4], wkc);

    [Fact]
    public void Eni_expected_wkc_flags_mismatch_immediately()
    {
        var tracker = new WkcTracker(Fixture());
        var t = DateTimeOffset.UnixEpoch;

        Assert.Null(tracker.Observe(t, Lrw(6), FrameDirection.Returning));
        var evt = tracker.Observe(t, Lrw(5), FrameDirection.Returning);

        Assert.NotNull(evt);
        Assert.Equal((ushort)6, evt!.Expected);
        Assert.Equal((ushort)5, evt.Actual);
        Assert.Equal(EtherCatCommand.Lrw, evt.Command);
    }

    [Fact]
    public void Outbound_frames_are_not_checked()
    {
        var tracker = new WkcTracker(Fixture());
        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, Lrw(0), FrameDirection.Outbound));
    }

    [Fact]
    public void Without_eni_expected_wkc_is_learned_from_mode()
    {
        var tracker = new WkcTracker();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 25; i++)
            Assert.Null(tracker.Observe(t.AddMilliseconds(i), Lrw(3), FrameDirection.Returning));

        var evt = tracker.Observe(t.AddMilliseconds(30), Lrw(2), FrameDirection.Returning);
        Assert.NotNull(evt);
        Assert.Equal((ushort)3, evt!.Expected);
        Assert.Equal((ushort)2, evt.Actual);
    }

    [Fact]
    public void Learning_phase_reports_nothing()
    {
        var tracker = new WkcTracker();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 10; i++)
            Assert.Null(tracker.Observe(t.AddMilliseconds(i), Lrw((ushort)(i % 2)), FrameDirection.Returning));
    }

    [Fact]
    public void Physical_reads_outside_cyclic_table_are_not_checked()
    {
        var tracker = new WkcTracker(Fixture());
        var fprd = new EtherCatDatagram(EtherCatCommand.Fprd, 1, (0x0130u << 16) | 1004,
            false, false, 0, new byte[2], 0);
        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, fprd, FrameDirection.Returning));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WkcTrackerTests"`
Expected: FAIL — `WkcTracker` missing.

- [ ] **Step 3: Implement**

Add inside the `MonitorEvent` record in `MonitorEvents.cs`:

```csharp
    public sealed record WkcMismatchDetected(DateTimeOffset Timestamp, Protocol.EtherCatCommand Command,
        uint Address, ushort Expected, ushort Actual) : MonitorEvent(Timestamp);
```

```csharp
// src/OpenEC.Monitor/Observation/WkcTracker.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Checks returning cyclic datagrams' working counters against the ENI's
/// expected values, or against a learned mode when no ENI is loaded.</summary>
public sealed class WkcTracker
{
    private const int LearnThreshold = 20;
    private const int LearnCap = 1000;

    private readonly Dictionary<(EtherCatCommand, uint), ushort> _expectedFromEni = new();
    private readonly Dictionary<(EtherCatCommand, uint), Dictionary<ushort, int>> _observed = new();

    public WkcTracker(EniConfiguration? eni = null)
    {
        foreach (var cmd in eni?.CyclicCommands ?? Enumerable.Empty<EniCyclicCommand>())
            _expectedFromEni[(cmd.Command, cmd.RawAddress)] = (ushort)cmd.ExpectedWkc;
    }

    public MonitorEvent.WkcMismatchDetected? Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning) return null;
        var key = (d.Command, d.RawAddress);
        var isCyclicShape = d.Command is EtherCatCommand.Brd or EtherCatCommand.Lrd
            or EtherCatCommand.Lwr or EtherCatCommand.Lrw;
        if (!isCyclicShape && !_expectedFromEni.ContainsKey(key)) return null;

        if (_expectedFromEni.TryGetValue(key, out var expected))
            return d.WorkingCounter == expected
                ? null
                : new MonitorEvent.WkcMismatchDetected(ts, d.Command, d.RawAddress, expected, d.WorkingCounter);

        if (!_observed.TryGetValue(key, out var counts))
            _observed[key] = counts = new Dictionary<ushort, int>();
        var total = counts.Values.Sum();
        if (total >= LearnThreshold)
        {
            var mode = counts.MaxBy(kv => kv.Value).Key;
            if (d.WorkingCounter != mode)
                return new MonitorEvent.WkcMismatchDetected(ts, d.Command, d.RawAddress, mode, d.WorkingCounter);
        }
        if (total < LearnCap)
            counts[d.WorkingCounter] = counts.GetValueOrDefault(d.WorkingCounter) + 1;
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~WkcTrackerTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor/Observation tests/OpenEC.Monitor.Tests/Observation
git commit -m "feat: WKC verification against ENI or learned expectations"
```

---

### Task 13: BusObserver composition + process image

**Files:**
- Create: `src/OpenEC.Monitor/Observation/ProcessImage.cs`, `src/OpenEC.Monitor/Observation/BusObserver.cs`
- Modify: `src/OpenEC.Monitor/Observation/MonitorEvents.cs` (add `EmergencyReceived`)
- Test: `tests/OpenEC.Monitor.Tests/Observation/BusObserverTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–12.
- Produces:
  - `sealed record MonitorEvent.EmergencyReceived(DateTimeOffset Timestamp, ushort StationAddress, ushort ErrorCode, byte ErrorRegister) : MonitorEvent(Timestamp)`
  - `sealed record VariableValue(EniVariable Variable, object Value, DateTimeOffset Timestamp)` with `string? Cia402Description` — `MotionCia402.DescribeStatusword`/`DescribeControlword` when the variable name contains "statusword"/"controlword" (ordinal-ignore-case) and the value is a `ushort`
  - `sealed class ProcessImage` with `IReadOnlyDictionary<string, VariableValue> Current`, internal `UpdateInputs(EtherCatDatagram, DateTimeOffset)` / `UpdateOutputs(...)` (no-ops without an ENI map)
  - `sealed class BusObserver(EniConfiguration? eni = null)` with `BusModel Bus`, `TrafficStatistics Statistics`, `ProcessImage ProcessImage`, `IReadOnlyList<MonitorEvent> EventLog` (bounded at 10 000), `event Action<MonitorEvent>? EventRaised`, `void Process(DateTimeOffset ts, FrameDecodeResult decoded)`

`Process` wiring: malformed/non-EtherCAT update statistics only. For a decoded frame: classify direction; count frame + datagrams; outbound → `ObserveOutboundIndex` (first datagram only), cycle estimator, process-image outputs, AL-control events, mailbox parse of `Fpwr` writes into a slave's mailbox window; returning → WKC tracker (increments `Statistics.WkcMismatches`), slave state tracker, process-image inputs, mailbox parse of `Fprd` reads with `WorkingCounter == 1`. Mailbox window: the slave's ENI `MailboxOut`/`MailboxIn` range when known, else the 0x1000–0x1FFF heuristic. Every event goes to `EventLog` and `EventRaised`. `Statistics.EstimatedCycleTime` is refreshed from the estimator after each frame.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Observation/BusObserverTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class BusObserverTests
{
    private static EniConfiguration Fixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static void Feed(BusObserver observer, DateTimeOffset ts, byte[] raw) =>
        observer.Process(ts, EtherCatFrameParser.Parse(raw));

    private static (byte[] Outbound, byte[] Returning) CyclePair(byte idx,
        byte[] outputs, byte[] inputs, ushort lrwWkc = 6, byte brdState = 0x08)
    {
        var outbound = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, outputs, 0)
            .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
            .Build();
        var returning = new EtherCatFrameBuilder().AsReturning()
            .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, inputs, lrwWkc)
            .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { brdState, 0 }, 4)
            .Build();
        return (outbound, returning);
    }

    [Fact]
    public void Full_cycles_update_statistics_process_image_and_bus_state()
    {
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        for (byte i = 0; i < 20; i += 2)
        {
            var (outbound, returning) = CyclePair(i,
                outputs: new byte[] { 0x01, 0x00, 0x0F, 0x00 },
                inputs: new byte[] { 0x01, 0x00, 0x37, 0x06 });
            Feed(observer, t.AddMilliseconds(i), outbound);
            Feed(observer, t.AddMilliseconds(i + 0.1), returning);
        }

        Assert.Equal(20, observer.Statistics.EtherCatFrames);
        Assert.Equal(0, observer.Statistics.WkcMismatches);
        Assert.Equal(SlaveAlState.Op, observer.Bus.BusState);

        var sw = observer.ProcessImage.Current["Drive 4 (AX5101).Inputs.Statusword"];
        Assert.Equal((ushort)0x0637, sw.Value);
        Assert.NotNull(sw.Cia402Description); // "Operation enabled ..."
        var cw = observer.ProcessImage.Current["Drive 4 (AX5101).Outputs.Controlword"];
        Assert.Equal((ushort)0x000F, cw.Value);
        Assert.NotNull(observer.Statistics.EstimatedCycleTime);
    }

    [Fact]
    public void Wkc_mismatch_is_counted_and_logged()
    {
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        var good = CyclePair(0, new byte[4], new byte[4]);
        var bad = CyclePair(2, new byte[4], new byte[4], lrwWkc: 5);
        Feed(observer, t, good.Outbound);
        Feed(observer, t.AddMilliseconds(0.1), good.Returning);
        Feed(observer, t.AddMilliseconds(1), bad.Outbound);
        Feed(observer, t.AddMilliseconds(1.1), bad.Returning);

        Assert.Equal(1, observer.Statistics.WkcMismatches);
        Assert.Contains(observer.EventLog, e => e is MonitorEvent.WkcMismatchDetected m && m.Actual == 5);
    }

    [Fact]
    public void Coe_emergency_in_mailbox_read_raises_event()
    {
        var observer = new BusObserver(Fixture());
        var events = new List<MonitorEvent>();
        observer.EventRaised += events.Add;

        // Establish direction context first (one outbound frame with clear MAC bit).
        var (outbound, _) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, DateTimeOffset.UnixEpoch, outbound);

        // Slave 1004's ENI mailbox 'Recv' (slave->master, read by the master via FPRD)
        // starts at 4224 (0x1080): returning FPRD with WKC 1 carries the mailbox content.
        var body = new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 };
        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes((ushort)1004).CopyTo(mailbox, 2);
        mailbox[5] = 0x13; // counter 1, type CoE
        body.CopyTo(mailbox, 6);
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 9, 1004, 4224, mailbox, 1)
            .Build();
        Feed(observer, DateTimeOffset.UnixEpoch.AddMilliseconds(1), frame);

        var emcy = Assert.IsType<MonitorEvent.EmergencyReceived>(
            Assert.Single(events, e => e is MonitorEvent.EmergencyReceived));
        Assert.Equal((ushort)0x8130, emcy.ErrorCode);
        Assert.Equal((ushort)1004, emcy.StationAddress);
    }

    [Fact]
    public void Malformed_and_foreign_frames_only_touch_statistics()
    {
        var observer = new BusObserver();
        observer.Process(DateTimeOffset.UnixEpoch, new FrameDecodeResult.Malformed("x"));
        observer.Process(DateTimeOffset.UnixEpoch, new FrameDecodeResult.NotEtherCat(0x0800));

        Assert.Equal(1, observer.Statistics.MalformedFrames);
        Assert.Equal(1, observer.Statistics.NonEtherCatFrames);
        Assert.Empty(observer.EventLog);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BusObserverTests"`
Expected: FAIL — `BusObserver` / `ProcessImage` missing.

- [ ] **Step 3: Implement**

Add inside the `MonitorEvent` record in `MonitorEvents.cs`:

```csharp
    public sealed record EmergencyReceived(DateTimeOffset Timestamp, ushort StationAddress,
        ushort ErrorCode, byte ErrorRegister) : MonitorEvent(Timestamp);
```

```csharp
// src/OpenEC.Monitor/Observation/ProcessImage.cs
using Dahlke.EtherCAT.Cia402;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

public sealed record VariableValue(EniVariable Variable, object Value, DateTimeOffset Timestamp)
{
    /// <summary>Human-readable CiA-402 decode when this variable is a DS402 status- or controlword.</summary>
    public string? Cia402Description => Value is ushort word
        ? Variable.Name.Contains("statusword", StringComparison.OrdinalIgnoreCase)
            ? MotionCia402.DescribeStatusword(word)
            : Variable.Name.Contains("controlword", StringComparison.OrdinalIgnoreCase)
                ? MotionCia402.DescribeControlword(word)
                : null
        : null;
}

/// <summary>Latest decoded value of every mapped process variable.</summary>
public sealed class ProcessImage
{
    private readonly ProcessVariableMap? _map;
    private readonly Dictionary<string, VariableValue> _current = new();

    internal ProcessImage(EniConfiguration? eni) =>
        _map = eni is null ? null : ProcessVariableMap.Build(eni);

    public IReadOnlyDictionary<string, VariableValue> Current => _current;

    internal void UpdateInputs(EtherCatDatagram d, DateTimeOffset ts)
    {
        if (_map is null) return;
        foreach (var r in _map.ResolveInputs(d))
            _current[r.Variable.Name] = new VariableValue(r.Variable, r.Value, ts);
    }

    internal void UpdateOutputs(EtherCatDatagram d, DateTimeOffset ts)
    {
        if (_map is null) return;
        foreach (var r in _map.ResolveOutputs(d))
            _current[r.Variable.Name] = new VariableValue(r.Variable, r.Value, ts);
    }
}
```

```csharp
// src/OpenEC.Monitor/Observation/BusObserver.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>The stateful heart of the SDK: consumes decoded frames and maintains
/// bus model, statistics, process image and the event log.</summary>
public sealed class BusObserver
{
    private const int EventLogCap = 10_000;

    private readonly EniConfiguration? _eni;
    private readonly DirectionTracker _direction = new();
    private readonly CycleEstimator _cycle = new();
    private readonly SlaveStateTracker _states;
    private readonly WkcTracker _wkc;
    private readonly List<MonitorEvent> _eventLog = new();

    public BusObserver(EniConfiguration? eni = null)
    {
        _eni = eni;
        Bus = new BusModel();
        if (eni is not null) Bus.Seed(eni);
        _states = new SlaveStateTracker(Bus);
        _wkc = new WkcTracker(eni);
        ProcessImage = new ProcessImage(eni);
    }

    public BusModel Bus { get; }
    public TrafficStatistics Statistics { get; } = new();
    public ProcessImage ProcessImage { get; }
    public IReadOnlyList<MonitorEvent> EventLog => _eventLog;

    public event Action<MonitorEvent>? EventRaised;

    public void Process(DateTimeOffset ts, FrameDecodeResult decoded)
    {
        switch (decoded)
        {
            case FrameDecodeResult.NotEtherCat:
                Statistics.CountNonEtherCat();
                return;
            case FrameDecodeResult.Malformed:
                Statistics.CountMalformed();
                return;
            case FrameDecodeResult.Success ok:
                ProcessFrame(ts, ok.Frame);
                return;
        }
    }

    private void ProcessFrame(DateTimeOffset ts, EtherCatFrame frame)
    {
        Statistics.CountFrame(ts);
        var dir = _direction.Classify(frame);
        if (dir == FrameDirection.Outbound && frame.Datagrams.Count > 0)
            Statistics.ObserveOutboundIndex(frame.Datagrams[0].Index);

        foreach (var d in frame.Datagrams)
        {
            Statistics.CountDatagram(d.Command);
            _cycle.Observe(ts, d, dir);

            foreach (var evt in _states.Observe(ts, d, dir))
                Raise(evt);

            if (dir == FrameDirection.Returning)
            {
                if (_wkc.Observe(ts, d, dir) is { } mismatch)
                {
                    Statistics.WkcMismatches++;
                    Raise(mismatch);
                }
                if (d.IsLogical) ProcessImage.UpdateInputs(d, ts);
                else if (d.Command == EtherCatCommand.Fprd && d.WorkingCounter == 1)
                    InspectMailbox(ts, d);
            }
            else
            {
                if (d.IsLogical) ProcessImage.UpdateOutputs(d, ts);
                else if (d.Command == EtherCatCommand.Fpwr)
                    InspectMailbox(ts, d);
            }
        }
        Statistics.EstimatedCycleTime = _cycle.EstimatedCycleTime;
    }

    private void InspectMailbox(DateTimeOffset ts, EtherCatDatagram d)
    {
        if (!IsMailboxWindow(d.Adp, d.Ado)) return;
        var mailbox = MailboxParser.TryParse(d.Payload);
        if (mailbox?.Coe?.Emergency is { } emergency)
            Raise(new MonitorEvent.EmergencyReceived(ts,
                mailbox.StationAddress != 0 ? mailbox.StationAddress : d.Adp,
                emergency.ErrorCode, emergency.ErrorRegister));
    }

    private bool IsMailboxWindow(ushort adp, ushort ado)
    {
        var slave = _eni?.Slaves.FirstOrDefault(s => s.PhysAddr == adp);
        if (slave is { MailboxOut: not null } or { MailboxIn: not null })
            return (slave.MailboxOut?.Contains(ado) ?? false)
                || (slave.MailboxIn?.Contains(ado) ?? false);
        return ado is >= 0x1000 and < 0x2000;
    }

    private void Raise(MonitorEvent evt)
    {
        if (_eventLog.Count < EventLogCap) _eventLog.Add(evt);
        EventRaised?.Invoke(evt);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BusObserverTests"`
Expected: PASS, 4 tests. Note: the fixture ENI's drive mailbox `Recv` element (4224) maps slave→master; the test uses ENI ranges through `IsMailboxWindow`.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — all tests from Tasks 2–13.

- [ ] **Step 6: Commit**

```bash
git add src/OpenEC.Monitor/Observation tests/OpenEC.Monitor.Tests/Observation
git commit -m "feat: BusObserver composition with process image, mailbox events and CiA-402 decoding"
```

---

### Task 14: EtherCatMonitor facade + ESI enrichment + end-to-end test

**Files:**
- Create: `src/OpenEC.Monitor/EtherCatMonitorOptions.cs`, `src/OpenEC.Monitor/EsiEnricher.cs`, `src/OpenEC.Monitor/EtherCatMonitor.cs`
- Test: `tests/OpenEC.Monitor.Tests/EtherCatMonitorTests.cs`, `tests/OpenEC.Monitor.Tests/EsiEnricherTests.cs`

**Interfaces:**
- Consumes: everything prior.
- Produces:
  - `sealed class EtherCatMonitorOptions { EniConfiguration? Eni; string? EsiDirectory; ILoggerFactory? LoggerFactory; }`
  - `sealed class EsiEnricher(string directory, ILoggerFactory? loggerFactory = null)` with `Task<string?> ResolveNameAsync(uint vendorId, uint productCode, uint revision, string? typeHint = null)` and `static string? TypeHintFromName(string? name)` (extracts `EL1008` from `"Term 2 (EL1008)"`)
  - `sealed class EtherCatMonitor : IAsyncDisposable` with `static EtherCatMonitor OpenFile(string path, EtherCatMonitorOptions? options = null)`, `static EtherCatMonitor OpenLive(string interfaceName, ...)`, `static EtherCatMonitor FromSource(ICaptureSource source, ...)` (test seam), `BusObserver Observer`, `IAsyncEnumerable<MonitorEvent> Events` (bounded channel, 4096, DropOldest, completed when `RunAsync` ends), `Task RunAsync(CancellationToken ct = default)`

`RunAsync` first performs ESI enrichment when both `EsiDirectory` and `Eni` are set (resolves each ENI slave's identity, stores into `SlaveStatus.ResolvedDeviceName`), then pumps: `capture → EtherCatFrameParser.Parse → Observer.Process`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/EsiEnricherTests.cs
namespace OpenEC.Monitor.Tests;

public class EsiEnricherTests
{
    [Fact]
    public void Extracts_type_hint_from_twincat_names()
    {
        Assert.Equal("EL1008", EsiEnricher.TypeHintFromName("Term 2 (EL1008)"));
        Assert.Equal("AX5101", EsiEnricher.TypeHintFromName("Drive 4 (AX5101)"));
        Assert.Null(EsiEnricher.TypeHintFromName("NoParens"));
        Assert.Null(EsiEnricher.TypeHintFromName(null));
    }

    [Fact]
    public async Task Empty_esi_directory_resolves_to_null_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"esi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var enricher = new EsiEnricher(dir);
            Assert.Null(await enricher.ResolveNameAsync(2, 0x03f03052, 0x00120000, "EL1008"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

```csharp
// tests/OpenEC.Monitor.Tests/EtherCatMonitorTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests;

public class EtherCatMonitorTests
{
    private static string WriteScenarioPcap()
    {
        var frames = new List<(DateTimeOffset, byte[])>();
        var t = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        for (byte i = 0; i < 40; i += 2)
        {
            var cycle = i / 2;
            // Cycle 15 carries a WKC error; cycle 10 shows the drive dropping to SafeOp+error.
            ushort wkc = cycle == 15 ? (ushort)5 : (ushort)6;
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrw, i, 0x01000000, new byte[] { 0x01, 0x00, 0x0F, 0x00 }, 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(i + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
                .Build()));
            var returning = new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrw, i, 0x01000000, new byte[] { 0x01, 0x00, 0x37, 0x06 }, wkc)
                .AddPhysical(EtherCatCommand.Brd, (byte)(i + 1), 0, 0x0130, new byte[] { 0x08, 0x00 }, 4);
            frames.Add((t.AddMicroseconds(100), returning.Build()));
            if (cycle == 10)
                frames.Add((t.AddMicroseconds(200), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 100, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1)
                    .Build()));
            t = t.AddMilliseconds(1);
        }
        var path = Path.Combine(Path.GetTempPath(), $"openec-scenario-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, frames);
        return path;
    }

    [Fact]
    public async Task Analyzes_scenario_capture_end_to_end()
    {
        var path = WriteScenarioPcap();
        try
        {
            var eni = EniConfiguration.Load(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
            await using var monitor = EtherCatMonitor.OpenFile(path, new EtherCatMonitorOptions { Eni = eni });

            var events = new List<MonitorEvent>();
            var collector = Task.Run(async () =>
            {
                await foreach (var e in monitor.Events) events.Add(e);
            });
            await monitor.RunAsync();
            await collector;

            Assert.Equal(41, monitor.Observer.Statistics.EtherCatFrames);
            Assert.Equal(1, monitor.Observer.Statistics.WkcMismatches);
            Assert.Contains(events, e => e is MonitorEvent.WkcMismatchDetected);
            Assert.Contains(events, e => e is MonitorEvent.SlaveStateChanged s
                && s.Address == 1004 && s.NewState == SlaveAlState.SafeOp && s.ErrorFlag);
            Assert.Equal(SlaveAlState.Op, monitor.Observer.Bus.BusState);
            Assert.Equal((ushort)0x0637,
                monitor.Observer.ProcessImage.Current["Drive 4 (AX5101).Inputs.Statusword"].Value);
            Assert.NotNull(monitor.Observer.Statistics.EstimatedCycleTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Runs_without_eni()
    {
        var path = WriteScenarioPcap();
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path);
            await monitor.RunAsync();
            Assert.Equal(41, monitor.Observer.Statistics.EtherCatFrames);
            Assert.Empty(monitor.Observer.ProcessImage.Current);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EtherCatMonitorTests|FullyQualifiedName~EsiEnricherTests"`
Expected: FAIL — facade types missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/EtherCatMonitorOptions.cs
using Microsoft.Extensions.Logging;
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor;

public sealed class EtherCatMonitorOptions
{
    public EniConfiguration? Eni { get; set; }
    public string? EsiDirectory { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
}
```

```csharp
// src/OpenEC.Monitor/EsiEnricher.cs
using System.Text.RegularExpressions;
using Dahlke.EtherCAT.Esi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OpenEC.Monitor;

/// <summary>Resolves slave identities to vendor device names via an ESI XML directory
/// (e.g. C:/TwinCAT/3.1/Config/Io/EtherCAT). Unresolvable identities yield null.</summary>
public sealed partial class EsiEnricher
{
    private readonly EsiCatalog _catalog;

    public EsiEnricher(string directory, ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger<EsiCatalog>()
            ?? NullLogger<EsiCatalog>.Instance;
        _catalog = new EsiCatalog(
            Options.Create(new EsiOptions { Directory = directory }),
            logger,
            TimeProvider.System);
    }

    public async Task<string?> ResolveNameAsync(uint vendorId, uint productCode, uint revision,
        string? typeHint = null)
    {
        var result = await _catalog.LookupAsync(new EsiKey(vendorId, productCode, revision), typeHint);
        return result.Status == EsiStatus.Resolved ? result.Device!.NameEn : null;
    }

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex ParentheticalRegex();

    public static string? TypeHintFromName(string? name)
    {
        if (name is null) return null;
        var match = ParentheticalRegex().Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }
}
```

Note for the implementer: if `EsiOptions`'s directory property has a different name than `Directory`, check `Dahlke.EtherCAT.Esi`'s `EsiOptions` XML docs (the config key in its README is `"Esi": { "Directory": ... }`) and adjust — the test suite covers the empty-directory path either way.

```csharp
// src/OpenEC.Monitor/EtherCatMonitor.cs
using System.Threading.Channels;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor;

/// <summary>Facade tying a capture source to a BusObserver with an async event stream.</summary>
public sealed class EtherCatMonitor : IAsyncDisposable
{
    private readonly ICaptureSource _source;
    private readonly EtherCatMonitorOptions _options;
    private readonly Channel<MonitorEvent> _events;

    private EtherCatMonitor(ICaptureSource source, EtherCatMonitorOptions options)
    {
        _source = source;
        _options = options;
        Observer = new BusObserver(options.Eni);
        _events = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        Observer.EventRaised += e => _events.Writer.TryWrite(e);
    }

    public static EtherCatMonitor OpenFile(string path, EtherCatMonitorOptions? options = null) =>
        new(new PcapFileSource(path), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor OpenLive(string interfaceName, EtherCatMonitorOptions? options = null) =>
        new(new LiveCaptureSource(interfaceName), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor FromSource(ICaptureSource source, EtherCatMonitorOptions? options = null) =>
        new(source, options ?? new EtherCatMonitorOptions());

    public BusObserver Observer { get; }

    public IAsyncEnumerable<MonitorEvent> Events => _events.Reader.ReadAllAsync();

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await EnrichNamesAsync();
            await foreach (var raw in _source.CaptureAsync(ct))
                Observer.Process(raw.Timestamp, EtherCatFrameParser.Parse(raw.Data));
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private async Task EnrichNamesAsync()
    {
        if (_options.EsiDirectory is null || _options.Eni is null) return;
        var enricher = new EsiEnricher(_options.EsiDirectory, _options.LoggerFactory);
        foreach (var slave in _options.Eni.Slaves)
        {
            var name = await enricher.ResolveNameAsync(slave.VendorId, slave.ProductCode,
                slave.RevisionNo, EsiEnricher.TypeHintFromName(slave.Name));
            if (name is not null)
                Observer.Bus.GetOrAdd(slave.PhysAddr).ResolvedDeviceName = name;
        }
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EtherCatMonitorTests|FullyQualifiedName~EsiEnricherTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor tests/OpenEC.Monitor.Tests
git commit -m "feat: EtherCatMonitor facade with ESI name enrichment and event stream"
```

---

### Task 15: OpenEC.Monitor.Ads — AdsEnrichment

**Files:**
- Create: `src/OpenEC.Monitor.Ads/AdsBusSnapshot.cs`, `src/OpenEC.Monitor.Ads/AdsEnrichment.cs`, `src/OpenEC.Monitor.Ads/AdsClientFactory.cs`
- Test: `tests/OpenEC.Monitor.Tests/Ads/AdsEnrichmentTests.cs`

**Interfaces:**
- Consumes: `IEtherCatClient`, `EtherCatMasterState`, `EtherCatSlaveInfo`, `EtherCatScannedSlave`, `FrameStatistics`, `SlaveErrorCounters` from `Dahlke.EtherCAT.Diagnostics`; `AdsConnectionPoolBuilder` + `AddEtherCatDiagnostics` for the factory.
- Produces:
  - `sealed record AdsBusSnapshot(EtherCatMasterState MasterState, IReadOnlyList<EtherCatSlaveInfo> ConfiguredSlaves, IReadOnlyList<EtherCatScannedSlave> ScannedSlaves, FrameStatistics? FrameStatistics, IReadOnlyDictionary<ushort, SlaveErrorCounters> ErrorCounters)`
  - `sealed class AdsEnrichment(IEtherCatClient client)` with `Task<AdsBusSnapshot?> PollAsync(string masterNetId, CancellationToken ct)` — null when the master state read returns null (unreachable master); per-slave counter failures are skipped, not fatal
  - `static class AdsClientFactory` with `Task<(IEtherCatClient Client, IAsyncDisposable Handle)> ConnectAsync(string amsNetId, CancellationToken ct)` — pool without generic host, `AddEtherCatDiagnostics(startMonitor: false)`, client from `handle.Services`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Ads/AdsEnrichmentTests.cs
using Dahlke.EtherCAT.Diagnostics;
using OpenEC.Monitor.Ads;

namespace OpenEC.Monitor.Tests.Ads;

public class AdsEnrichmentTests
{
    private sealed class StubClient : IEtherCatClient
    {
        public EtherCatMasterState? MasterState { get; init; }
        public IReadOnlyList<EtherCatSlaveInfo>? Configured { get; init; }
        public IReadOnlyList<EtherCatScannedSlave>? Scanned { get; init; }
        public FrameStatistics? Frames { get; init; }
        public Func<ushort, SlaveErrorCounters?>? Counters { get; init; }

        public Task<EtherCatMasterState?> GetMasterStateAsync(string m, CancellationToken ct) =>
            Task.FromResult(MasterState);
        public Task<IReadOnlyList<EtherCatSlaveInfo>?> GetConfiguredSlavesAsync(string m, CancellationToken ct) =>
            Task.FromResult(Configured);
        public Task<IReadOnlyList<EtherCatScannedSlave>?> GetScannedSlavesAsync(string m, CancellationToken ct) =>
            Task.FromResult(Scanned);
        public Task<FrameStatistics?> GetFrameStatisticsAsync(string m, CancellationToken ct) =>
            Task.FromResult(Frames);
        public Task<SlaveErrorCounters?> GetSlaveErrorCountersAsync(string m, ushort a, CancellationToken ct) =>
            Task.FromResult(Counters?.Invoke(a));
        // Remaining IEtherCatClient members: implement by returning defaults
        // (Task.FromResult(default)) or throwing NotSupportedException — they are
        // not exercised by AdsEnrichment. Match the interface exactly as the
        // compiler demands; adjust nullability to the interface's signatures.
    }

    [Fact]
    public async Task Unreachable_master_yields_null()
    {
        var enrichment = new AdsEnrichment(new StubClient { MasterState = null });
        Assert.Null(await enrichment.PollAsync("10.0.0.1.1.1", CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_collects_slaves_and_counters()
    {
        var stub = new StubClient
        {
            MasterState = new EtherCatMasterState { CurrentState = "OP", SlaveCount = 2 },
            Configured = new List<EtherCatSlaveInfo>
            {
                new() { PhysicalAddress = 1001, Name = "EK1100" },
                new() { PhysicalAddress = 1002, Name = "EL1008" },
            },
            Scanned = new List<EtherCatScannedSlave>(),
            Counters = a => a == 1002 ? new SlaveErrorCounters { PhysicalAddress = 1002 } : null,
        };
        var enrichment = new AdsEnrichment(stub);

        var snapshot = await enrichment.PollAsync("10.0.0.1.1.1", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.ConfiguredSlaves.Count);
        Assert.Single(snapshot.ErrorCounters);
        Assert.True(snapshot.ErrorCounters.ContainsKey(1002));
    }
}
```

**Note for the implementer:** the stub must implement every member of `IEtherCatClient` (`GetMastersAsync`, `GetSlaveDetailAsync`, `GetSyncUnitsAsync`, `ReadCoeObjectAsync`, `WriteCoeObjectAsync`, `ReadCia402StatusAsync`, `ResetSlaveErrorCountersAsync`) — let the compiler list them and fill each with a default-returning body. If `EtherCatMasterState`/`EtherCatSlaveInfo`/`SlaveErrorCounters` are positional records rather than settable DTOs, construct them positionally with the same values instead of object initializers.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AdsEnrichmentTests"`
Expected: FAIL — `OpenEC.Monitor.Ads` types missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor.Ads/AdsBusSnapshot.cs
using Dahlke.EtherCAT.Diagnostics;

namespace OpenEC.Monitor.Ads;

/// <summary>One poll of master-side diagnostics — data a passive TAP cannot see.</summary>
public sealed record AdsBusSnapshot(
    EtherCatMasterState MasterState,
    IReadOnlyList<EtherCatSlaveInfo> ConfiguredSlaves,
    IReadOnlyList<EtherCatScannedSlave> ScannedSlaves,
    FrameStatistics? FrameStatistics,
    IReadOnlyDictionary<ushort, SlaveErrorCounters> ErrorCounters);
```

```csharp
// src/OpenEC.Monitor.Ads/AdsEnrichment.cs
using Dahlke.EtherCAT.Diagnostics;

namespace OpenEC.Monitor.Ads;

public sealed class AdsEnrichment(IEtherCatClient client)
{
    /// <summary>Polls the master once. Null when the master is unreachable; individual
    /// slave-counter failures are skipped so one bad slave cannot break the poll.</summary>
    public async Task<AdsBusSnapshot?> PollAsync(string masterNetId, CancellationToken ct)
    {
        var state = await client.GetMasterStateAsync(masterNetId, ct);
        if (state is null) return null;
        var configured = await client.GetConfiguredSlavesAsync(masterNetId, ct)
            ?? (IReadOnlyList<EtherCatSlaveInfo>)Array.Empty<EtherCatSlaveInfo>();
        var scanned = await client.GetScannedSlavesAsync(masterNetId, ct)
            ?? (IReadOnlyList<EtherCatScannedSlave>)Array.Empty<EtherCatScannedSlave>();
        var frames = await client.GetFrameStatisticsAsync(masterNetId, ct);
        var counters = new Dictionary<ushort, SlaveErrorCounters>();
        foreach (var slave in configured)
        {
            var c = await client.GetSlaveErrorCountersAsync(masterNetId, slave.PhysicalAddress, ct);
            if (c is not null) counters[slave.PhysicalAddress] = c;
        }
        return new AdsBusSnapshot(state, configured, scanned, frames, counters);
    }
}
```

```csharp
// src/OpenEC.Monitor.Ads/AdsClientFactory.cs
using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;

namespace OpenEC.Monitor.Ads;

/// <summary>Builds a started ADS connection pool (no generic host) and resolves the
/// EtherCAT diagnostics client from it. Dispose the returned handle to tear down.</summary>
public static class AdsClientFactory
{
    public static async Task<(IEtherCatClient Client, IAsyncDisposable Handle)> ConnectAsync(
        string amsNetId, CancellationToken ct)
    {
        var handle = await AdsConnectionPoolBuilder.Create()
            .AddTarget("target", o => o.AmsNetId = amsNetId)
            .ConfigureServices(s => s.AddEtherCatDiagnostics(startMonitor: false))
            .BuildAndStartAsync(ct);
        return (handle.Services.GetRequiredService<IEtherCatClient>(), handle);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AdsEnrichmentTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.Monitor.Ads tests/OpenEC.Monitor.Tests/Ads
git commit -m "feat: optional ADS enrichment module over Dahlke.EtherCAT.Diagnostics"
```

---

### Task 16: CLI — devices, gen-sample, frames

**Files:**
- Create: `src/OpenEC.Monitor/Synthesis/SampleCapture.cs`
- Modify: `src/OpenEC.CLI/Program.cs`
- Create: `src/OpenEC.CLI/Commands/DevicesCommand.cs`, `src/OpenEC.CLI/Commands/GenSampleCommand.cs`, `src/OpenEC.CLI/Commands/FramesCommand.cs`
- Test: `tests/OpenEC.Monitor.Tests/Cli/CliCommandTests.cs`

**Interfaces:**
- Consumes: `CaptureDevices`, `PcapFileSource`, `EtherCatFrameParser`, `EtherCatFrameBuilder`, `PcapFileWriter`.
- Produces:
  - `static string SampleCapture.WriteDemo(string path, int cycles = 50)` — writes a synthetic capture: cyclic LRW+BRD pairs (WKC 6/state Op), one WKC-error cycle at `cycles/2`, one drive SafeOp+error `FPRD` at `cycles/3`, one CoE emergency mailbox read at `2*cycles/3`; returns the path. The frame content mirrors the Task 14 scenario builder with station address 1004, logical address 0x01000000, 4-byte images.
  - CLI command tree (`Program.cs` uses `CommandApp`, exposed as `public static class Program { public static int Main(string[] args); public static void Configure(IConfigurator config); }` so tests can reuse `Configure`):
    - `openec devices` — table of capture interfaces, exit 0
    - `openec gen-sample <output.pcap> [--cycles n]` — exit 0
    - `openec frames <file> [--cmd LRW] [--adp 1004] [--count 20]` — one line per datagram (`#frame time dir cmd idx addr len wkc`), exit 0; exit 2 when the file is missing

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Cli/CliCommandTests.cs
using Spectre.Console.Testing;

namespace OpenEC.Monitor.Tests.Cli;

public class CliCommandTests
{
    private static CommandAppTester App()
    {
        var app = new CommandAppTester();
        app.Configure(OpenEC.CLI.Program.Configure);
        return app;
    }

    [Fact]
    public void Gen_sample_then_frames_lists_datagrams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-cli-{Guid.NewGuid():N}.pcap");
        try
        {
            var gen = App().Run("gen-sample", path, "--cycles", "10");
            Assert.Equal(0, gen.ExitCode);
            Assert.True(File.Exists(path));

            var frames = App().Run("frames", path, "--count", "5");
            Assert.Equal(0, frames.ExitCode);
            Assert.Contains("LRW", frames.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frames_filter_by_command_excludes_others()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-cli-{Guid.NewGuid():N}.pcap");
        try
        {
            App().Run("gen-sample", path, "--cycles", "10");
            var result = App().Run("frames", path, "--cmd", "BRD", "--count", "5");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("BRD", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LRW", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frames_with_missing_file_exits_2()
    {
        var result = App().Run("frames", "/nonexistent/nope.pcap");
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Devices_lists_interfaces()
    {
        Assert.Equal(0, App().Run("devices").ExitCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CliCommandTests"`
Expected: FAIL — `Program.Configure` / commands missing.

- [ ] **Step 3: Implement**

```csharp
// src/OpenEC.Monitor/Synthesis/SampleCapture.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Generates a demo capture so the tooling can be exercised without hardware.</summary>
public static class SampleCapture
{
    public static string WriteDemo(string path, int cycles = 50)
    {
        var frames = new List<(DateTimeOffset Timestamp, byte[] Frame)>();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var idx = (byte)((cycle * 2) % 256);
            ushort wkc = cycle == cycles / 2 ? (ushort)5 : (ushort)6;
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0x01, 0x00, 0x0F, 0x00 }, 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
                .Build()));
            frames.Add((t.AddMicroseconds(120), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0x01, 0x00, 0x37, 0x06 }, wkc)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0x08, 0x00 }, 4)
                .Build()));
            if (cycle == cycles / 3)
                frames.Add((t.AddMicroseconds(200), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 200, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1)
                    .Build()));
            if (cycle == 2 * cycles / 3)
            {
                var body = new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 };
                var mailbox = new byte[6 + body.Length];
                BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
                BitConverter.GetBytes((ushort)1004).CopyTo(mailbox, 2);
                mailbox[5] = 0x13;
                body.CopyTo(mailbox, 6);
                frames.Add((t.AddMicroseconds(250), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 201, 1004, 0x1080, mailbox, 1)
                    .Build()));
            }
            t = t.AddMilliseconds(1);
        }
        PcapFileWriter.Write(path, frames);
        return path;
    }
}
```

```csharp
// src/OpenEC.CLI/Program.cs
using OpenEC.CLI.Commands;
using Spectre.Console.Cli;

namespace OpenEC.CLI;

public static class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(Configure);
        return app.Run(args);
    }

    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("openec");
        config.AddCommand<DevicesCommand>("devices")
            .WithDescription("List capture interfaces");
        config.AddCommand<GenSampleCommand>("gen-sample")
            .WithDescription("Generate a synthetic EtherCAT sample capture");
        config.AddCommand<FramesCommand>("frames")
            .WithDescription("Dump decoded frames/datagrams from a capture file");
    }
}
```

```csharp
// src/OpenEC.CLI/Commands/DevicesCommand.cs
using OpenEC.Monitor.Capture;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class DevicesCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var table = new Table().AddColumn("Name").AddColumn("Description");
        foreach (var (name, description) in CaptureDevices.List())
            table.AddRow(name.EscapeMarkup(), (description ?? "").EscapeMarkup());
        AnsiConsole.Write(table);
        return 0;
    }
}
```

```csharp
// src/OpenEC.CLI/Commands/GenSampleCommand.cs
using System.ComponentModel;
using OpenEC.Monitor.Synthesis;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class GenSampleCommand : Command<GenSampleCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<output>")]
        [Description("Output pcap path")]
        public string Output { get; init; } = "";

        [CommandOption("--cycles")]
        [Description("Number of bus cycles to generate (default 50)")]
        public int Cycles { get; init; } = 50;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            SampleCapture.WriteDemo(settings.Output, settings.Cycles);
            AnsiConsole.MarkupLineInterpolated($"Wrote [green]{settings.Output}[/] ({settings.Cycles} cycles)");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
```

```csharp
// src/OpenEC.CLI/Commands/FramesCommand.cs
using System.ComponentModel;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class FramesCommand : AsyncCommand<FramesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("pcap/pcapng file")]
        public string File { get; init; } = "";

        [CommandOption("--cmd")]
        [Description("Only datagrams with this command (e.g. LRW, BRD, FPRD)")]
        public string? Command { get; init; }

        [CommandOption("--adp")]
        [Description("Only physical datagrams with this station address")]
        public ushort? Adp { get; init; }

        [CommandOption("--count")]
        [Description("Stop after this many datagram lines")]
        public int Count { get; init; } = int.MaxValue;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] file not found: {settings.File}");
            return 2;
        }
        EtherCatCommand? filter = null;
        if (settings.Command is not null)
        {
            if (!Enum.TryParse<EtherCatCommand>(settings.Command, ignoreCase: true, out var parsed))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] unknown command {settings.Command}");
                return 2;
            }
            filter = parsed;
        }

        var direction = new DirectionTracker();
        var printed = 0;
        var frameNo = 0;
        await using var source = new PcapFileSource(settings.File);
        await foreach (var raw in source.CaptureAsync())
        {
            frameNo++;
            if (EtherCatFrameParser.Parse(raw.Data) is not FrameDecodeResult.Success ok) continue;
            var dir = direction.Classify(ok.Frame) == FrameDirection.Outbound ? "->" : "<-";
            foreach (var d in ok.Frame.Datagrams)
            {
                if (filter is not null && d.Command != filter) continue;
                if (settings.Adp is not null && (d.IsLogical || d.Adp != settings.Adp)) continue;
                var addr = d.IsLogical
                    ? $"log 0x{d.LogicalAddress:X8}"
                    : $"adp {d.Adp} ado 0x{d.Ado:X4}";
                AnsiConsole.MarkupLineInterpolated(
                    $"#{frameNo,5} {raw.Timestamp:HH:mm:ss.ffffff} {dir} {d.Command,-5} idx {d.Index,3} {addr} len {d.Payload.Length,4} wkc {d.WorkingCounter}");
                if (++printed >= settings.Count) return 0;
            }
        }
        return 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CliCommandTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.CLI src/OpenEC.Monitor/Synthesis tests/OpenEC.Monitor.Tests/Cli
git commit -m "feat: CLI with devices, gen-sample and frames commands"
```

---

### Task 17: CLI — analyze

**Files:**
- Create: `src/OpenEC.CLI/Reporting/AnalysisReport.cs`, `src/OpenEC.CLI/Commands/AnalyzeCommand.cs`
- Modify: `src/OpenEC.CLI/Program.cs` (register `analyze`)
- Test: `tests/OpenEC.Monitor.Tests/Cli/AnalyzeCommandTests.cs`

**Interfaces:**
- Consumes: `EtherCatMonitor`, `EniConfiguration`, `MonitorEvent`, `SampleCapture`.
- Produces:
  - `sealed record SlaveReport(ushort Address, string Name, string State, bool Error, string? AlStatusCode)`
  - `sealed record AnalysisReport(string File, long TotalFrames, long EtherCatFrames, long NonEtherCatFrames, long MalformedFrames, double? FramesPerSecond, double? CycleTimeMicroseconds, long SuspectedLostFrames, long WkcMismatches, long Emergencies, string BusState, IReadOnlyList<SlaveReport> Slaves, IReadOnlyList<string> Events)` with `static AnalysisReport Build(string file, EtherCatMonitor monitor)` and `bool HasBusErrors => WkcMismatches > 0 || Emergencies > 0 || Slaves.Any(s => s.Error)`
  - `openec analyze <file> [--eni <ENI.xml>] [--esi-dir <dir>] [--json]` — renders overview/slaves/events tables (or serialized JSON with `--json`); exit 0 clean, 1 when `HasBusErrors`, 2 on IO/usage errors

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Cli/AnalyzeCommandTests.cs
using System.Text.Json;
using OpenEC.Monitor.Synthesis;
using Spectre.Console.Testing;

namespace OpenEC.Monitor.Tests.Cli;

public class AnalyzeCommandTests
{
    private static CommandAppTester App()
    {
        var app = new CommandAppTester();
        app.Configure(OpenEC.CLI.Program.Configure);
        return app;
    }

    private static string SamplePcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-analyze-{Guid.NewGuid():N}.pcap");
        SampleCapture.WriteDemo(path, cycles: 30);
        return path;
    }

    private static string FixtureEni() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

    [Fact]
    public void Analyze_with_eni_reports_bus_errors_via_exit_code_1()
    {
        var path = SamplePcap();
        try
        {
            var result = App().Run("analyze", path, "--eni", FixtureEni());
            Assert.Equal(1, result.ExitCode); // sample contains a WKC error + emergency
            Assert.Contains("WKC", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_json_is_machine_readable()
    {
        var path = SamplePcap();
        try
        {
            var result = App().Run("analyze", path, "--eni", FixtureEni(), "--json");
            Assert.Equal(1, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Output);
            Assert.Equal(1, doc.RootElement.GetProperty("wkcMismatches").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("emergencies").GetInt64());
            Assert.True(doc.RootElement.GetProperty("etherCatFrames").GetInt64() > 0);
            Assert.True(doc.RootElement.GetProperty("slaves").GetArrayLength() >= 4);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_missing_file_exits_2()
    {
        Assert.Equal(2, App().Run("analyze", "/nonexistent/nope.pcap").ExitCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzeCommandTests"`
Expected: FAIL — `analyze` not registered.

- [ ] **Step 3: Implement**

Register in `Program.Configure`:

```csharp
        config.AddCommand<AnalyzeCommand>("analyze")
            .WithDescription("Analyze a capture file and report bus health");
```

```csharp
// src/OpenEC.CLI/Reporting/AnalysisReport.cs
using OpenEC.Monitor;
using OpenEC.Monitor.Observation;

namespace OpenEC.CLI.Reporting;

public sealed record SlaveReport(ushort Address, string Name, string State, bool Error, string? AlStatusCode);

public sealed record AnalysisReport(
    string File,
    long TotalFrames,
    long EtherCatFrames,
    long NonEtherCatFrames,
    long MalformedFrames,
    double? FramesPerSecond,
    double? CycleTimeMicroseconds,
    long SuspectedLostFrames,
    long WkcMismatches,
    long Emergencies,
    string BusState,
    IReadOnlyList<SlaveReport> Slaves,
    IReadOnlyList<string> Events)
{
    public bool HasBusErrors => WkcMismatches > 0 || Emergencies > 0 || Slaves.Any(s => s.Error);

    public static AnalysisReport Build(string file, EtherCatMonitor monitor)
    {
        var stats = monitor.Observer.Statistics;
        var log = monitor.Observer.EventLog;
        return new AnalysisReport(
            file,
            stats.TotalFrames,
            stats.EtherCatFrames,
            stats.NonEtherCatFrames,
            stats.MalformedFrames,
            stats.FramesPerSecond,
            stats.EstimatedCycleTime?.TotalMicroseconds,
            stats.SuspectedLostFrames,
            stats.WkcMismatches,
            log.Count(e => e is MonitorEvent.EmergencyReceived),
            monitor.Observer.Bus.BusState.ToString(),
            monitor.Observer.Bus.Slaves
                .OrderBy(s => s.Address)
                .Select(s => new SlaveReport(s.Address, s.DisplayName, s.AlState.ToString(),
                    s.ErrorFlag, s.AlStatusCode?.ToString("X4")))
                .ToList(),
            log.Select(Describe).ToList());
    }

    private static string Describe(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s =>
            $"{s.Timestamp:HH:mm:ss.fff} slave {s.Address}: {s.OldState} -> {s.NewState}{(s.ErrorFlag ? " [ERROR]" : "")}",
        MonitorEvent.StateChangeRequested r =>
            $"{r.Timestamp:HH:mm:ss.fff} master requested {r.RequestedState} for slave {r.Address}",
        MonitorEvent.WkcMismatchDetected w =>
            $"{w.Timestamp:HH:mm:ss.fff} WKC mismatch on {w.Command} 0x{w.Address:X8}: expected {w.Expected}, got {w.Actual}",
        MonitorEvent.EmergencyReceived m =>
            $"{m.Timestamp:HH:mm:ss.fff} EMERGENCY from slave {m.StationAddress}: code 0x{m.ErrorCode:X4} register 0x{m.ErrorRegister:X2}",
        _ => e.ToString() ?? "",
    };
}
```

```csharp
// src/OpenEC.CLI/Commands/AnalyzeCommand.cs
using System.ComponentModel;
using System.Text.Json;
using OpenEC.CLI.Reporting;
using OpenEC.Monitor;
using OpenEC.Monitor.Eni;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class AnalyzeCommand : AsyncCommand<AnalyzeCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("pcap/pcapng file")]
        public string File { get; init; } = "";

        [CommandOption("--eni")]
        [Description("ENI.xml exported from the master configuration")]
        public string? Eni { get; init; }

        [CommandOption("--esi-dir")]
        [Description("Directory of vendor ESI XML files for device naming")]
        public string? EsiDirectory { get; init; }

        [CommandOption("--json")]
        [Description("Emit the report as JSON")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] file not found: {settings.File}");
            return 2;
        }
        EniConfiguration? eni = null;
        if (settings.Eni is not null)
        {
            if (!File.Exists(settings.Eni))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] ENI not found: {settings.Eni}");
                return 2;
            }
            eni = EniConfiguration.Load(settings.Eni);
        }

        await using var monitor = EtherCatMonitor.OpenFile(settings.File, new EtherCatMonitorOptions
        {
            Eni = eni,
            EsiDirectory = settings.EsiDirectory,
        });
        await monitor.RunAsync();

        var report = AnalysisReport.Build(settings.File, monitor);
        if (settings.Json)
            AnsiConsole.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        else
            Render(report);
        return report.HasBusErrors ? 1 : 0;
    }

    private static void Render(AnalysisReport report)
    {
        var overview = new Table().Title("Overview").AddColumn("Metric").AddColumn("Value");
        overview.AddRow("File", report.File.EscapeMarkup());
        overview.AddRow("Frames (EtherCAT/total)", $"{report.EtherCatFrames}/{report.TotalFrames}");
        overview.AddRow("Non-EtherCAT / malformed", $"{report.NonEtherCatFrames} / {report.MalformedFrames}");
        overview.AddRow("Frames per second", report.FramesPerSecond?.ToString("F1") ?? "-");
        overview.AddRow("Cycle time (us)", report.CycleTimeMicroseconds?.ToString("F0") ?? "-");
        overview.AddRow("Suspected lost frames", report.SuspectedLostFrames.ToString());
        overview.AddRow("WKC mismatches",
            report.WkcMismatches > 0 ? $"[red]{report.WkcMismatches}[/]" : "0");
        overview.AddRow("Emergencies",
            report.Emergencies > 0 ? $"[red]{report.Emergencies}[/]" : "0");
        overview.AddRow("Bus state", report.BusState);
        AnsiConsole.Write(overview);

        if (report.Slaves.Count > 0)
        {
            var slaves = new Table().Title("Slaves")
                .AddColumn("Addr").AddColumn("Name").AddColumn("State").AddColumn("Err").AddColumn("AL code");
            foreach (var s in report.Slaves)
                slaves.AddRow(s.Address.ToString(), s.Name.EscapeMarkup(), s.State,
                    s.Error ? "[red]yes[/]" : "no", s.AlStatusCode ?? "-");
            AnsiConsole.Write(slaves);
        }

        if (report.Events.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Events[/] ({report.Events.Count}):");
            foreach (var line in report.Events.Take(50))
                AnsiConsole.WriteLine("  " + line);
            if (report.Events.Count > 50)
                AnsiConsole.WriteLine($"  ... {report.Events.Count - 50} more");
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzeCommandTests"`
Expected: PASS, 3 tests. (The sample's WKC-error cycle produces exactly one mismatch because the ENI pins the LRW's expected WKC to 6; the emergency mailbox read at ADO 0x1080 falls in the default window for slaves without ENI mailbox ranges — the fixture drive at 1004 declares 4096/4224, and 0x1080 = 4224 is inside `MailboxIn`.)

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.CLI tests/OpenEC.Monitor.Tests/Cli
git commit -m "feat: analyze command with table and JSON reports and CI-friendly exit codes"
```

---

### Task 18: CLI — live dashboard

**Files:**
- Create: `src/OpenEC.CLI/Commands/LiveCommand.cs`
- Modify: `src/OpenEC.CLI/Program.cs` (register `live`)
- Test: `tests/OpenEC.Monitor.Tests/Cli/LiveCommandTests.cs`

**Interfaces:**
- Consumes: `EtherCatMonitor.OpenLive`, `AnalysisReport`, `AdsEnrichment`, `AdsClientFactory`.
- Produces: `openec live --interface <if> [--eni ...] [--esi-dir ...] [--ads <netid>] [--duration <seconds>]` — refreshes a Spectre `Live` table 4×/second with overview + slave states + last events; optional 1 Hz ADS polling adds master state and per-port CRC counters panel; stops on Ctrl-C or after `--duration`; prints the final `AnalysisReport` tables and uses its exit-code rule.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OpenEC.Monitor.Tests/Cli/LiveCommandTests.cs
using Spectre.Console.Testing;

namespace OpenEC.Monitor.Tests.Cli;

public class LiveCommandTests
{
    private static CommandAppTester App()
    {
        var app = new CommandAppTester();
        app.Configure(OpenEC.CLI.Program.Configure);
        return app;
    }

    [Fact]
    public void Live_requires_interface_option()
    {
        Assert.NotEqual(0, App().Run("live").ExitCode);
    }

    [Fact]
    public void Live_with_unknown_interface_exits_2()
    {
        var result = App().Run("live", "--interface", "openec-does-not-exist-0", "--duration", "1");
        Assert.Equal(2, result.ExitCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LiveCommandTests"`
Expected: FAIL — `live` not registered.

- [ ] **Step 3: Implement**

Register in `Program.Configure`:

```csharp
        config.AddCommand<LiveCommand>("live")
            .WithDescription("Monitor a live interface (TAP monitor port)");
```

```csharp
// src/OpenEC.CLI/Commands/LiveCommand.cs
using System.ComponentModel;
using OpenEC.CLI.Reporting;
using OpenEC.Monitor;
using OpenEC.Monitor.Ads;
using OpenEC.Monitor.Eni;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class LiveCommand : AsyncCommand<LiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--interface <name>")]
        [Description("Capture interface connected to the TAP monitor port")]
        public string? Interface { get; init; }

        [CommandOption("--eni")]
        public string? Eni { get; init; }

        [CommandOption("--esi-dir")]
        public string? EsiDirectory { get; init; }

        [CommandOption("--ads")]
        [Description("AMS NetId of a TwinCAT target for active enrichment")]
        public string? AdsNetId { get; init; }

        [CommandOption("--duration")]
        [Description("Stop after this many seconds (default: until Ctrl-C)")]
        public int? DurationSeconds { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Interface)
                ? ValidationResult.Error("--interface is required")
                : ValidationResult.Success();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        EniConfiguration? eni = null;
        if (settings.Eni is not null)
        {
            if (!File.Exists(settings.Eni))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] ENI not found: {settings.Eni}");
                return 2;
            }
            eni = EniConfiguration.Load(settings.Eni);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (settings.DurationSeconds is { } seconds)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        EtherCatMonitor monitor;
        try
        {
            monitor = EtherCatMonitor.OpenLive(settings.Interface!, new EtherCatMonitorOptions
            {
                Eni = eni,
                EsiDirectory = settings.EsiDirectory,
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }

        await using (monitor)
        {
            AdsBusSnapshot? adsSnapshot = null;
            IAsyncDisposable? adsHandle = null;
            Task? adsLoop = null;
            var pump = Task.Run(async () =>
            {
                try { await monitor.RunAsync(cts.Token); }
                catch (OperationCanceledException) { }
            });
            // The live source can also fail on first use (bad interface): surface that as exit 2.
            var early = await Task.WhenAny(pump, Task.Delay(500, CancellationToken.None));
            if (early == pump && pump.Exception is not null)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]error:[/] {pump.Exception.GetBaseException().Message}");
                return 2;
            }

            if (settings.AdsNetId is not null)
            {
                try
                {
                    var (client, handle) = await AdsClientFactory.ConnectAsync(settings.AdsNetId, cts.Token);
                    adsHandle = handle;
                    var enrichment = new AdsEnrichment(client);
                    adsLoop = Task.Run(async () =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            adsSnapshot = await enrichment.PollAsync(settings.AdsNetId, cts.Token);
                            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ContinueWith(_ => { });
                        }
                    });
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]ads disabled:[/] {ex.Message}");
                }
            }

            await AnsiConsole.Live(new Table()).StartAsync(async ctx =>
            {
                while (!cts.Token.IsCancellationRequested && !pump.IsCompleted)
                {
                    ctx.UpdateTarget(BuildDashboard(monitor, adsSnapshot));
                    try { await Task.Delay(250, cts.Token); }
                    catch (OperationCanceledException) { }
                }
            });
            cts.Cancel();
            await pump;
            if (adsLoop is not null) await adsLoop;
            if (adsHandle is not null) await adsHandle.DisposeAsync();

            var report = AnalysisReport.Build(settings.Interface!, monitor);
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine($"Session summary: {report.EtherCatFrames} frames, "
                + $"{report.WkcMismatches} WKC mismatches, {report.Emergencies} emergencies.");
            return report.HasBusErrors ? 1 : 0;
        }
    }

    private static Table BuildDashboard(EtherCatMonitor monitor, AdsBusSnapshot? ads)
    {
        var stats = monitor.Observer.Statistics;
        var table = new Table().Title("OpenEC live")
            .AddColumn("Metric").AddColumn("Value");
        table.AddRow("Frames", stats.EtherCatFrames.ToString());
        table.AddRow("Rate (fps)", stats.FramesPerSecond?.ToString("F0") ?? "-");
        table.AddRow("Cycle (us)", stats.EstimatedCycleTime?.TotalMicroseconds.ToString("F0") ?? "-");
        table.AddRow("WKC mismatches", stats.WkcMismatches.ToString());
        table.AddRow("Bus state", monitor.Observer.Bus.BusState.ToString());
        foreach (var s in monitor.Observer.Bus.Slaves.OrderBy(s => s.Address).Take(32))
            table.AddRow($"slave {s.Address}",
                $"{s.DisplayName.EscapeMarkup()} {s.AlState}{(s.ErrorFlag ? " [red]ERR[/]" : "")}");
        if (ads is not null)
        {
            table.AddRow("[bold]ADS master[/]",
                $"{ads.MasterState.CurrentState} ({ads.ConfiguredSlaves.Count} slaves)");
            foreach (var (addr, counters) in ads.ErrorCounters.OrderBy(kv => kv.Key).Take(16))
            {
                var crc = string.Join(" ", counters.Ports.Select(p => $"p{p.Port}:{p.CrcErrors}"));
                table.AddRow($"crc {addr}", crc.EscapeMarkup());
            }
        }
        foreach (var evt in monitor.Observer.EventLog.TakeLast(8))
            table.AddRow("event", evt.ToString().EscapeMarkup());
        return table;
    }
}
```

**Note for the implementer:** `SlaveErrorCounters.Ports` is the per-port list (`PortErrorCounters` with `Port`, `CrcErrors`, `ForwardedCrcErrors`, `LostLinkCount`); adjust member access if the compiler disagrees. The `--interface` failure path relies on `LiveCaptureSource` throwing `ArgumentException` for unknown interfaces (Task 8); on machines without libpcap permissions the open call throws instead — both surface as exit 2.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LiveCommandTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEC.CLI tests/OpenEC.Monitor.Tests/Cli
git commit -m "feat: live dashboard command with optional ADS enrichment"
```

---

### Task 19: Documentation + final verification

**Files:**
- Create: `docs/tap-setup.md`
- Modify: `README.md` (add Getting Started + status section)

**Interfaces:**
- Consumes: the finished CLI.
- Produces: user-facing docs; a verified, green milestone.

- [ ] **Step 1: Write `docs/tap-setup.md`**

Content requirements (write it out fully):
- Wiring diagram (ASCII) for the DUALCOMM ETAP-1000: `Master NIC -> Port A`, `Port B -> first slave (EK1100)`, `Monitor port -> capture NIC`; note that the monitor port aggregates both directions and that OpenEC pairs directions automatically (source-MAC 0x02 bit with idx-pairing fallback).
- macOS capture permissions: `ls -l /dev/bpf*`; either run the CLI with `sudo` or install Wireshark's ChmodBPF launch daemon so `/dev/bpf*` is group-readable (`access_bpf` group).
- Linux: `sudo setcap cap_net_raw,cap_net_admin+eip $(command -v openec)` or run via sudo; Windows: install Npcap.
- Verification walkthrough: `openec devices` → pick interface → `openec live --interface <if> --duration 10`; offline alternative `openec gen-sample demo.pcap && openec analyze demo.pcap --eni <your ENI.xml>`.
- How to export ENI.xml from TwinCAT (TwinCAT project → I/O → Device (EtherCAT) → EtherCAT tab → Export Configuration File) and where ESI files live (`C:\TwinCAT\3.1\Config\Io\EtherCAT`).

- [ ] **Step 2: Extend `README.md`**

Add after the Architecture section: a "🚀 Getting Started" section with `dotnet build`, `dotnet test`, the `gen-sample`/`analyze`/`frames`/`live` walkthrough (copy the exact commands from Step 1's verification walkthrough), a pointer to `docs/tap-setup.md`, and a "Status" note: Milestone 1 = SDK + CLI (this release); Milestone 2 = Avalonia-based `OpenEC.Inspector`.

- [ ] **Step 3: Full verification**

```bash
dotnet build
dotnet test
dotnet run --project src/OpenEC.CLI -- gen-sample /tmp/openec-demo.pcap
dotnet run --project src/OpenEC.CLI -- analyze /tmp/openec-demo.pcap --eni tests/OpenEC.Monitor.Tests/Fixtures/sample.eni.xml
dotnet run --project src/OpenEC.CLI -- frames /tmp/openec-demo.pcap --count 10
dotnet run --project src/OpenEC.CLI -- devices
```

Expected: build + all tests green; `analyze` prints the report and exits 1 (the demo capture intentionally contains a WKC error and an emergency); `frames` prints 10 datagram lines; `devices` lists the Mac's interfaces.

- [ ] **Step 4: Commit**

```bash
git add docs/tap-setup.md README.md
git commit -m "docs: TAP setup guide and getting-started walkthrough"
```

---

## Post-plan checklist (for the session lead)

- Run `superpowers:requesting-code-review` after the final task.
- Milestone 2 (`OpenEC.Inspector`, Avalonia) starts with a fresh brainstorm + spec.
- Live-hardware validation (ETAP-1000 + real bus) is a user-run step: `openec live --interface <capture NIC> --eni <exported ENI.xml> --esi-dir <ESI folder>`.
