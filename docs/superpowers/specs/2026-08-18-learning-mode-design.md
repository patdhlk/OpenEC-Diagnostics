# OpenEC-Diagnostics — Learning Mode Design (ENI-independent bus discovery)

**Date:** 2026-08-18
**Status:** Approved
**Scope:** New `Learning/` namespace in `OpenEC.Monitor`; one new entry point on `BusObserver`;
swappable config in `ProcessImage` and `WkcTracker`; one default interface member on
`ICaptureSource` plus an override in `PcapFileSource`; new CLI verb and flags; Inspector
completeness surface and ENI export. No changes to the frame decoder, the capture sources'
enumeration behaviour, or the ADS module.

## 1. Goal

Reconstruct a runtime equivalent of an ENI file from passively observed traffic, so that
`OpenEC.Inspector` and `OpenEC.CLI` give named process variables, identified devices and
exact WKC checking on a bus for which no `ENI.xml` was supplied.

The governing principle: **ESI is the schema, the wire is the binding.** Vendor ESI files
declare what a device *offers* — PDOs, entries, names, datatypes, sync managers, object
dictionary. The master's startup traffic reveals what it actually *selected and where it put
it*. Learning mode chains the two.

Target is full ENI equivalence (identity, topology order, cyclic command table, PDO
assignment and mapping, named variables), exportable as real ENI XML.

Non-goals:

- Active bus participation of any kind. Learning is 100% passive, consistent with the
  project's core guarantee. No CoE writes, no SDO Info requests, no frame injection.
- Reading Acontis `.ecd` snapshots (proprietary format).
- Modular/slot device configuration (see §9).
- Distributed-clock, port-topology and error-counter learning. Registers `0x0110`,
  `0x092C` and `0x0300`+ are observable and named here for later milestones, but are not
  part of this scope.

## 2. Bringup facts and their sources

Every learned fact traces to specific observed traffic. Phase attribution is per the
Beckhoff ESM description
([tc3_io_intro/1446518411](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1446518411.html)):

> **Init→PreOp:** "The master configures the channels of the SyncManagers for the mailbox
> communication and initializes the synchronization of the distributed clocks."
>
> **PreOp→SafeOp:** "The master uses mailbox communication to set parameters for mapping
> process data, configuring the channels of the SyncManagers for the process data
> communication and the channels of the FMMUs."

| Fact | Source on the wire | Phase |
| --- | --- | --- |
| Slave count, ring position, station address | `APWR` to **0x0010**; auto-increment ADP gives ring position, payload gives configured address | INIT |
| Identity (vendor, product, revision, serial) | SII reads: `FPWR` **0x0502** control, **0x0504** address; data at **0x0508**. EEPROM words 0x08/0x0A/0x0C/0x0E. Fallback: CoE SDO read of **0x1018** | INIT / PreOP |
| Mailbox SyncManagers (SM0/SM1) | `FPWR` to **0x0800 + 8n** | INIT→PreOP |
| Process-data SyncManagers (SM2/SM3) | `FPWR` to **0x0800 + 8n** | PreOP→SafeOP |
| FMMU configuration | `FPWR` to **0x0600 + 16n** | PreOP→SafeOP |
| PDO assignment | CoE SDO download to **0x1C12** (SM2/RxPDO), **0x1C13** (SM3/TxPDO) | PreOP→SafeOP |
| PDO mapping overrides | CoE SDO download to **0x16xx** / **0x1Axx**; each subindex is `index<<16 \| subindex<<8 \| bitlen` | PreOP→SafeOP |
| Entry names, datatypes, defaults | **ESI** via `EsiDevice.ProcessData`, keyed by learned identity | offline |
| Cyclic command table | Observed `LRD`/`LWR`/`LRW` logical address, data length, modal WKC | SafeOP / OP |

Register layouts used by the decoders:

- **FMMU** (16 bytes at `0x0600 + 16n`): logical start address (4), length (2), logical
  start bit (1), logical stop bit (1), physical start address (2), physical start bit (1),
  type (1; 1 = read/inputs, 2 = write/outputs), activate (1).
- **SyncManager** (8 bytes at `0x0800 + 8n`): physical start address (2), length (2),
  control (1), status (1), activate (1), PDI control (1).

## 3. The offset chain

The FMMU decode is the linchpin. ESI states what a slave offers; `0x1C1x` states which PDOs
the master selected; the SyncManager states where those bytes sit in the slave's physical
memory; the FMMU maps that physical window into the master's logical process image.

For each slave, for each configured FMMU: find the enabled, non-zero-length SyncManager whose
physical start address **equals** the FMMU's physical start. That SM carries the PDOs assigned to
it. Equality rather than containment is deliberate: a containment rule would also require adding
`(fmmu.PhysicalStart - sm.PhysicalStart) * 8 + fmmu.PhysicalStartBit` to the offset, which the
formula below does not carry. When no SyncManager matches, the FMMU's variables cannot be placed at
all, and `LearningCompleteness.ProcessDataPlaceable` reports the slave as incomplete rather than the
configuration silently coming up short. PDO entries
fill the SM data area sequentially in assignment order, so bit offsets accumulate. The
resulting global offset is

```
BitOffs = (FMMU.LogicalStartAddress - imageOriginBytes) * 8
        + FMMU.LogicalStartBit
        + offsetWithinSmBits
```

`imageOriginBytes` is a byte address, `FMMU.LogicalStartBit` and `offsetWithinSmBits` are bit
counts, and the result is the bit offset into the input or output image. Input versus output
comes from the FMMU type byte.

Two details make this line up with existing code unchanged:

- `EniVariable.BitOffs` is relative to the whole input or output image, and
  `ProcessVariableMap.Collect` slices on `cmd.InputOffs * 8`. Synthesis therefore picks an
  image origin: the lowest logical address covered by any input FMMU is the input origin,
  likewise outputs. Each cyclic command's `InputOffs`/`OutputOffs` is its logical address
  minus that origin. `LRW` sets both when input and output FMMUs intersect its range.
- ESI padding entries (`Index` 0 with a bit length, `SubIndex` null) are consumed to keep
  offsets aligned but excluded from the variable list.

`ProcessVariableMap` and `ProcessValueDecoder` require no changes.

Variable naming follows `{SlaveName}.{PdoName}.{EntryName}`, where `{SlaveName}` is
`Slave {StationAddress} ({EsiDeviceName})`, or `Slave {StationAddress}` when ESI resolves nothing.
Where ESI cannot supply an entry name, the synthetic form
`{SlaveName}.0x{Index:X4}:{SubIndex:X2}` is used and marked as such in provenance.

**The slave component must be unique per slave, not per device type.** `ProcessImage` keys its
variable dictionary by name, so two slaves sharing a name silently discard one of them — and an ESI
device name is a *type* name shared by every identical terminal on the bus, which is the common case
for EtherCAT I/O. TwinCAT's own `{SlaveName}` is the user-assigned *instance* name (`Term 2
(EL1008)`), which a passive observer cannot recover; the station address is unique by definition and
always known, so it stands in.

## 4. Components

```
src/OpenEC.Monitor/Learning/
  RegisterDecoders.cs        StationAddress, Sii, SyncManager, Fmmu — pure fns: datagram → fact?
  MailboxDecoders.cs         PdoAssign (0x1C1x), PdoMapping (0x16xx/0x1Axx), Identity (0x1018)
  LearnedSlave.cs            ring position, station addr, identity, SMs, FMMUs, assigned PDOs
  LearnedBus.cs              accumulator: Observe(ts, datagram, direction)
  BusLearner.cs              orchestrates decoders + ESI resolution; emits revisions
  LearnedConfiguration.cs    EniConfiguration + provenance + completeness
  ConfigurationDiff.cs       learned vs. declared → MonitorEvent.ConfigMismatch
  EniXmlWriter.cs            export as real ENI.xml
  LearnedBusCache.cs         fingerprint → persisted config
```

Each decoder is a pure function over one datagram, independently testable. `LearnedBus` is
the only stateful piece and holds no references outside the namespace. `BusLearner` has no
reference to `BusObserver`; it consumes decoded frames and emits configuration revisions.

`LearnedConfiguration` wraps rather than replaces the existing type, so every downstream
consumer keeps working:

```csharp
public sealed record LearnedConfiguration(
    EniConfiguration Configuration,
    LearningCompleteness Completeness,
    IReadOnlyDictionary<ushort, FactProvenance> Provenance,
    int Revision);

public enum FactSource { Sii, CoeIdentity, Coe, RegisterWrite, EsiDefault, Cache, Ads, Inferred }
```

`Coe` and `CoeIdentity` are distinct on purpose: identity may come from the `0x1018` object
specifically, while PDO assignment and mapping come from ordinary CoE downloads. Labelling the
latter `RegisterWrite` — as an earlier draft did — misreports the source of the fact in the one
type whose job is reporting sources accurately.

`LearningCompleteness` carries per-slave `IdentityKnown`, `SyncManagersKnown`,
`FmmusKnown`, `PdoMappingKnown`, and a bus-level `SawStartup`. Nothing silently claims a
fact it inferred: the Inspector renders completeness as a status strip, the CLI as a
coverage line, and `analyze --json` as a `learning` block.

## 5. Control flow

**One parse, two consumers.** `EtherCatMonitor.RunAsync` parses each frame once and hands
the `FrameDecodeResult` to both `Observer.Process` and `BusLearner.Observe`. No capture-level
tee.

**Offline two-pass.** `PcapFileSource.CaptureAsync` opens a fresh reader per call and is
therefore re-enumerable. `ICaptureSource` gains a default interface member:

```csharp
bool SupportsMultiplePasses => false;
```

`PcapFileSource` returns `true`. `LiveCaptureSource` and `RecordingCaptureSource` inherit
`false` — correct for the latter, since re-enumerating it would re-record. When the flag is
set, pass 1 runs `BusLearner` alone (no process-image work), and pass 2 runs the observer
with the finished configuration. Result: 100% of process data mapped in offline analysis.

**Live progressive rebind.** `BusObserver` gains one entry point, taking the same `_lock` as
`Process` and `SetResolvedDeviceName`:

```csharp
public void ApplyConfiguration(LearnedConfiguration config)
```

It reseeds `BusModel` (identity, names, auto-increment map), swaps `ProcessImage`'s variable
map and replaces `WkcTracker`'s ENI expectations, while preserving accumulated statistics
and the event log. `ProcessImage._map` and `WkcTracker._expectedFromEni` become swappable
rather than readonly. The learner emits a revision only when the derived configuration
actually changes, so rebinds are rare and need no debounce.

Learned mailbox windows replace `BusObserver.IsMailboxWindow`'s `0x1000–0x2000` guess once
SM0/SM1 are known at the IP transition.

**Cross-check.** Learning is always on. With no ENI, the learned configuration drives
everything. With an ENI loaded, the ENI drives and the learner still runs;
`ConfigurationDiff.Compare(declared, learned)` runs on each revision and raises
`MonitorEvent.ConfigMismatch` for disagreements — wrong device at a position, unexpected
identity, PDO remapped at runtime, FMMU layout that does not match. ENI-only variables are
excluded by construction (§9).

**Cache.** Learned configurations persist to `<appdata>/openec/learned/<fingerprint>.eni.xml`
as real ENI XML with a `.meta.json` sidecar for completeness and provenance, where
`<appdata>` is `Environment.SpecialFolder.ApplicationData` — `%APPDATA%` on Windows,
`~/Library/Application Support` on macOS, `~/.config` on Linux — keeping the cross-platform
guarantee. (Corrected during the integration milestone: this paragraph originally said
`~/.config` for macOS too. The code was never affected, since it has always called the API, but
the README repeated the error and would have sent macOS users to a directory the tool never
writes. Verified by running `GetFolderPath(SpecialFolder.ApplicationData)` on macOS 15.) The cache, the
export feature and the test fixture format are the same artifact, and `EniConfiguration.Load`
already reads it back.

The fingerprint is `slave count + ordered (vendor, product, revision) + logical address
layout` — deliberately excluding serial numbers, so replacing an identical terminal still
hits the cache. On a mid-run attach where identity was never read (§9), a weaker fallback
fingerprint of `slave count + station addresses observed in FPRD + cyclic command shape` is
used; a hit is not guaranteed and the completeness strip says so. A cache hit applies
immediately; the learner keeps running and refines or overrides it.

## 6. Degradation

Learning never hard-fails and never requires an observed startup. It reports what it knows.

| Situation | Behaviour |
| --- | --- |
| Full INIT→OP observed | Complete configuration; named variables; ENI export available |
| Attach at OP, cache hit | Cached configuration applied at frame 1; refined as traffic allows |
| Attach at OP, cache miss | Station addresses, AL states, cyclic command table and byte ranges; no names; completeness strip states that a master restart would recover PDO mapping |
| Startup checking disabled | Identity falls back to CoE `0x1018`, then unknown. The ADS tier is **deferred to the integration milestone** — `FactSource.Ads` is defined but nothing produces it, since ADS polling belongs with the live-session wiring |
| Bus never reaches OP | No cyclic command table; everything learned up to the reached state is kept |

## 7. Surfaces

**SDK.** `EtherCatMonitorOptions.Learning = LearningMode.Auto | Off` (default `Auto`).
`EtherCatMonitor.Learned` exposes the current `LearnedConfiguration?`. The event stream gains
`ConfigurationLearned` and `ConfigMismatch`.

**CLI.**

```bash
openec learn capture.pcap --out bus.eni.xml
openec analyze capture.pcap                     # learned coverage; --eni now optional
openec live --interface en11 --learn-out bus.eni.xml
openec analyze capture.pcap --no-learn
```

`analyze --json` gains a `learning` block (completeness, per-slave provenance, mismatches),
so CI can gate on "the bus no longer matches the committed ENI".

**Inspector.** The Variables tab works with no ENI. A completeness strip in the device editor
states what is known and what a master restart would recover. A "Save learned ENI…" session
command. `ConfigMismatch` lands in the existing docked messages panel; no new UI surface.

## 8. Testing

The critical asset is a **synthetic bringup generator**: extend `SampleCapture` and
`EtherCatFrameBuilder` to emit a full INIT→OP sequence — station-address assignment, SII
identity reads, mailbox SM config at IP, process-data SM plus FMMU plus CoE PDO assign at PS,
then cyclic traffic. Bringup happens once on a real bus and is awkward to capture on demand,
so the whole feature must be testable without hardware.

- **Unit** — each register and mailbox decoder against hand-built datagrams.
- **Golden** — synthetic bringup for the existing `EL1008.xml` ESI fixture; learned
  configuration must equal a hand-written expected `EniConfiguration`.
- **Round-trip** — learned → ENI XML → `EniConfiguration.Load` → structurally equal.
- **Degradation** — one test per row of §6, plus a mixed `LRW` / `LRD`+`LWR` bus.
- **Cross-check** — a mismatched ENI raises `ConfigMismatch`; a `WcState`-only difference
  raises none.
- **Concurrency** — `ApplyConfiguration` racing `Process`, asserted via the existing
  snapshot accessors.
- **Hardware acceptance** — a real ETAP-1000 capture of a TwinCAT bringup. Tests cannot
  substitute for this; it stays open until run on the bench, alongside the outstanding M2
  hardware items.

## 9. Known limits

Sourced from the vendor documentation and the ESI catalogue's own stated scope.

- **Modular devices.** `Dahlke.EtherCAT.Esi` documents `EsiProcessData` as excluding modular
  devices: an EJ-series or slot-based device declares per-slot PDOs under `<Modules>` rather
  than under `<Device>`. Such slaves learn structure and byte ranges but not named variables.
- **PDOs without a declared sync manager.** Most PDOs declare no `Sm` attribute (per the
  catalogue's own note, 37,541 of 58,128 in Beckhoff's published set), so SM assignment comes
  from the wire, not ESI.
- **Startup checking is optional and per slave.** "Check Vendor IDs", "Check product codes",
  "Check revision number" and "Check serial number" are individually configurable
  ([tc3_io_intro/1357974411](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1357974411.html)).
  Unchecked means the master never reads identity from the wire.
- **Addressing mode.** `AutoInc only – No Fixed Address` means no `APWR` to `0x0010` to anchor
  on; `No AutoInc – Use 2. Address` means the master reads a fixed address from the slave
  instead of assigning one. The learner keys slaves by ring position when the station-address
  anchor is absent.
- **`Add WC state bit(s)`** injects master-synthesised `WcState` input variables into the
  process image. They appear in the ENI and have no wire representation. The diff engine
  treats ENI-only variables as expected, never as a mismatch.
- **`Final State`** may be INIT, PREOP or SAFEOP, so a bus may never produce cyclic traffic.
- **`Use LRD/LWR instead of LRW`** is per slave, so both datagram shapes coexist on one bus.
- **PDI-side identity reads** are invisible to a passive observer.
- Learning reflects what the master *did*, not what the ENI *says*. Learned and declared
  configurations can legitimately differ; that difference is the cross-check's output, not an
  error.

## 9a. Deferred to the integration milestone

Beyond §5's control-flow items, two things this spec describes are explicitly not in the core
milestone and are listed here so they are not mistaken for gaps:

- **The ADS identity tier** in §6. `FactSource.Ads` exists as a label; no code produces it. ADS
  polling is a live-session concern and belongs with the integration wiring.
- **Per-variable provenance.** `FactProvenance` is recorded per slave, so a slave whose schema
  resolved is marked ESI-named even for individual entries that fell back to the synthetic
  `0x{Index:X4}:{SubIndex:X2}` form — which is what happens for a runtime-remapped PDO, the case
  §9 cares about. Per-entry granularity needs a richer provenance model.

## 10. Observed but unmodelled

Named here so a later milestone does not have to rediscover them: watchdog registers
`0x0400` (multiplier), `0x0410` (PDI), `0x0420` (SM); DL status `0x0110` for port topology;
DC registers `0x0910`/`0x092C`; per-port error counters `0x0300`–`0x0310`.

The TwinCAT Online tab's CSV export — name, physical address, auto-increment address, vendor
ID, product code, revision, serial, state, and CRC counters per port — is precisely this
feature's fact set, and is a ready-made precedent should a tabular export be wanted.
