# OpenEC-Diagnostics — Milestone 1 Design (SDK + CLI)

**Date:** 2026-08-15
**Status:** Approved
**Scope:** `OpenEC.Monitor` (core SDK), `OpenEC.Monitor.Ads` (optional active module), `OpenEC.CLI`, tests. The `OpenEC.Inspector` GUI (Avalonia) is Milestone 2 and out of scope here.

## 1. Goal

A free, open-source C#/.NET 8 implementation of passive EtherCAT monitoring per `README.md`: decode raw EtherCAT traffic observed through a network TAP (DUALCOMM ETAP-1000) or from `.pcap`/`.pcapng` files, track bus/slave state, decode process data via ENI, and expose it all through a clean SDK plus a headless CLI.

Non-goals for M1: GUI, EtherCAT master functionality (we never transmit on the bus), SoE/AoE mailbox decoding (header-level recognition only), EoE full IP reassembly (fragment headers only).

## 2. Solution layout

```
OpenEC-Diagnostics.sln
├── src/
│   ├── OpenEC.Monitor/       # Core SDK — 100% passive, master-agnostic (net8.0)
│   ├── OpenEC.Monitor.Ads/   # Optional active diagnostics over TwinCAT ADS
│   └── OpenEC.CLI/           # Headless analyzer / live monitor
├── tests/
│   └── OpenEC.Monitor.Tests/ # xunit; synthetic frame fixtures, ENI fixture, generated pcaps
├── docs/
│   ├── tap-setup.md          # ETAP-1000 wiring + capture-interface guide
│   └── superpowers/specs/    # this document
└── README.md
```

## 3. OpenEC.Monitor — core SDK

Four layers; each has one purpose, a small public surface, and is testable in isolation.

### 3.1 Capture sources

```csharp
public readonly record struct RawFrame(DateTimeOffset Timestamp, ReadOnlyMemory<byte> Data);

public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default);
}
```

- `PcapFileSource` — SharpPcap `CaptureFileReaderDevice`; supports pcap and pcapng.
- `LiveCaptureSource` — SharpPcap `LibPcapLiveDevice`; promiscuous mode; BPF filter `ether proto 0x88a4 or (vlan and ether proto 0x88a4)`.
- `CaptureDevices.List()` — enumerates interfaces for the CLI `devices` command.

### 3.2 Frame decoder (pure, stateless)

Hand-rolled parsers over `ReadOnlySpan<byte>` — the decoder is the product; no external dissector. Little-endian throughout (EtherCAT wire order).

- **Ethernet II**: dst/src MAC, optional 802.1Q VLAN tag, EtherType. Only `0x88A4` frames pass; others are counted and skipped.
- **EtherCAT frame header** (2 bytes): 11-bit length, 4-bit protocol type (must be 1 = ESC datagrams).
- **Datagram chain** — each datagram:
  - Header (10 bytes): `cmd` (byte), `idx` (byte), 32-bit address — interpreted as `(ADP, ADO)` for physical commands or logical address for `LRD/LWR/LRW` — 11-bit length, circulating bit, more-datagrams bit (`M`), 16-bit IRQ.
  - Payload (`len` bytes), then 16-bit **WKC**.
  - `EtherCatCommand` enum: `NOP, APRD, APWR, APRW, FPRD, FPWR, FPRW, BRD, BWR, BRW, LRD, LWR, LRW, ARMW, FRMW`.
- **Mailbox decoding** — attempted when a physical-address datagram's ADO falls in the standard SM0/SM1 mailbox range (per-slave offsets from ENI when available; default 0x1000–0x1FFF heuristic otherwise):
  - Mailbox header (6 bytes): length, station address, channel/priority, type (`CoE=3, FoE=4, SoE=5, VoE=15, EoE=2, AoE=1`), counter.
  - **CoE**: CoE header (number/service: emergency, SDO req/res, SDO info, TxPDO/RxPDO); SDO expedited/segmented upload/download with index/subindex; emergency error code/register.
  - **FoE**: opcode (RRQ/WRQ/DATA/ACK/ERR/BUSY), packet number, filename/error text.
  - **EoE**: fragment header (fragment number, complete-size, frame number); no IP reassembly in M1.
- Decoder outputs immutable record types: `EtherCatFrame` → `IReadOnlyList<EtherCatDatagram>` → optional `MailboxMessage`.
- Malformed input never throws for the session: parse failures yield a `DecodeError` result carried alongside good frames (counted, inspectable).

### 3.3 ENI parser

`EniConfiguration.Load(Stream | path)` parses `EtherCATConfig` XML:

- Per slave: name, physical address (`Info/PhysAddr`), auto-inc address, vendor ID, product code, revision; mailbox sync-manager offsets/sizes.
- Cyclic section: command table (cmd, addresses, expected WKC (`Cnt`), data offsets) per cyclic frame.
- ProcessData / PDO entries: index, subindex, bit length, name, data type, and position in the process image.
- Output model: `EniConfiguration` with `Slaves`, `CyclicCommands`, and `VariableMap` — an interval map from logical address ranges to `ProcessVariable(Name, DataType, BitOffset, BitLength, Slave)`.
- Namespace-tolerant parsing (ENI exports differ between masters); unknown elements ignored; missing optional sections leave those features off rather than failing.

### 3.4 Bus observer (stateful engine)

`BusObserver` consumes decoded frames and maintains the live model:

- **Direction pairing.** The ETAP-1000 monitor port aggregates both directions, so each cyclic frame is seen twice (outbound and processed). Primary heuristic: slaves set the locally-administered bit (0x02 in the first source-MAC octet) on the return path. Fallback: pair by (`idx`, `cmd`, address) within a cycle window and treat the copy with incremented WKC as the return. Unpaired frames are counted, not dropped.
- **Slave state tracking.** AL Control writes (ADO 0x0120) and AL Status reads (ADO 0x0130, incl. `BRD` sums) → per-slave `Init/PreOp/SafeOp/Op/Boot` + error flag; state-change events with timestamps.
- **WKC analysis.** Expected WKC per cyclic command from ENI (`Cnt`) when loaded, else learned as the mode of observed values; mismatches raise `WkcError` events with the datagram context.
- **Cycle metrics.** Cycle time estimation (median inter-arrival of the recurring cyclic command set), frame/datagram rates, lost-frame detection via `idx` sequence gaps.
- **Process image.** With ENI loaded, `LRD/LWR/LRW` payloads are windowed onto `VariableMap` → typed current values (`bool`, integer widths, `REAL/LREAL`, bit-packed). DS402 statuswords/controlwords decoded via `Dahlke.EtherCAT.Cia402` when a variable is identified as one (by PDO index 0x6040/0x6041 or ENI type hints).
- **ESI enrichment.** Optional `IEsiCatalog` (`Dahlke.EtherCAT.Esi`, `AddEsiCatalog` or direct construction with an ESI directory) resolves vendor/product/revision → readable device names; ENI identity is the lookup key.
- **Mailbox/event log.** CoE emergencies, SDO aborts, FoE errors surfaced as a bounded in-memory event log + event stream.

### 3.5 Facade

```csharp
var monitor = EtherCatMonitor.OpenFile("capture.pcapng", options);   // or .OpenLive("en7", options)
// options: EniConfiguration? Eni, string? EsiDirectory, ILoggerFactory?, buffer limits
await foreach (var evt in monitor.Events.WithCancellation(ct)) { ... } // state changes, WKC errors, emergencies
monitor.Bus;          // slaves: address, name (ESI/ENI), AL state, last seen
monitor.Statistics;   // frame/datagram/cycle metrics, decode errors
monitor.ProcessImage; // named variables with typed current values (when ENI loaded)
```

Frame-dump consumers (the CLI `frames` command) read a capture source and the frame parser directly rather than going through the facade — the facade carries no per-frame stream.

## 4. OpenEC.Monitor.Ads — optional active module

Thin adapter over `IEtherCatClient` (`Dahlke.EtherCAT.Diagnostics`, pooled connections via `Dahlke.TwinCAT.Ads`):

- `AdsEnrichment.AttachAsync(monitor, amsNetId)` — polls master state, configured-vs-scanned slaves, per-port CRC/error counters (invisible to a tap), sync-unit faults; merges into the bus model as a distinct data source (`Passive` vs `Ads` provenance on each fact).
- Core SDK has **zero** TwinCAT dependencies; this project is the only place `Dahlke.TwinCAT.*` appears.
- Unreachable master → warnings + null-data tolerance (matching the Dahlke library's convention), never a crash of the passive session.

## 5. OpenEC.CLI

Spectre.Console.Cli commands (single tool `openec`):

- `openec devices` — capture interfaces with descriptions.
- `openec analyze <file> [--eni <ENI.xml>] [--esi-dir <dir>] [--json]` — offline report: frame/datagram stats per command, slave inventory + state timeline, WKC error list, cycle metrics, mailbox/emergency log. `--json` for machine-readable output.
- `openec frames <file> [--cmd <name>] [--adp <addr>] [--count n]` — per-frame/datagram dump (tshark-style); filter by command and station address.
- `openec gen-sample <output.pcap> [--cycles n]` — generate a synthetic demo capture so the tooling can be exercised without hardware.
- `openec live --interface <if> [--eni ...] [--esi-dir ...] [--ads <netid>] [--duration s]` — live dashboard (Spectre live table): cycle time, rates, per-slave state + WKC error counters; `--ads` attaches the active module. Ctrl-C ends with a summary report.

Exit codes: 0 clean, 1 errors observed on the bus (WKC/emergency), 2 usage/IO failure — so the CLI is scriptable in CI/acceptance rigs.

## 6. Testing (TDD, xunit)

- **Frame decoder**: hand-crafted byte fixtures for every command type, VLAN tagging, multi-datagram chains, truncated/malformed frames (error results, no throws), mailbox CoE/FoE/EoE cases including SDO segmented + emergency.
- **Fixture builder**: a small `EtherCatFrameBuilder` test utility that composes valid wire images (also used to generate pcap files programmatically for end-to-end tests).
- **ENI parser**: representative `ENI.xml` fixture (2–3 slaves incl. an EL-terminal-style PDO layout and a DS402 axis) + tolerance tests (missing sections, foreign namespaces).
- **Bus observer**: direction pairing (both heuristics), state transitions, WKC mismatch detection, cycle estimation, process-image decode incl. CiA-402 statusword.
- **CLI**: smoke tests running `analyze`/`frames` against generated pcaps, asserting on report content and exit codes.
- **Ads module**: adapter tested against a stubbed `IEtherCatClient`; no live master needed.

## 7. Dependencies

| Project | Packages |
|---|---|
| OpenEC.Monitor | SharpPcap, Dahlke.EtherCAT.Esi, Dahlke.EtherCAT.Cia402, Microsoft.Extensions.Logging.Abstractions |
| OpenEC.Monitor.Ads | Dahlke.TwinCAT.Ads, Dahlke.EtherCAT.Diagnostics |
| OpenEC.CLI | Spectre.Console.Cli |
| Tests | xunit, coverlet |

## 8. Hardware notes (DUALCOMM ETAP-1000)

Wiring: Master NIC → Port A, Port B → first slave; Monitor port → capture NIC on the analysis machine. The monitor port aggregates both directions onto one interface (hence the direction-pairing logic in §3.4). Documented for users in `docs/tap-setup.md`, including macOS/Linux capture-permission notes (BPF device access) and verifying with `openec devices` + `openec live`.

## 9. Milestones

- **M1 (this spec):** SDK + ADS module + CLI + tests.
- **M2:** `OpenEC.Inspector` — Avalonia desktop app on top of the SDK (topology view, live traffic, process-variable watch).
- **M3+:** EoE reassembly, SoE decoding, long-run recording/ring buffers, alarms/export integrations.
