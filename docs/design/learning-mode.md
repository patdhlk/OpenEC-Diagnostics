# OpenEC-Diagnostics — Learning Mode Design

**Date:** 2026-08-18

These design documents describe the architecture at the time each milestone was built. Where a document and the code disagree, the code is authoritative.

## Goal

Reconstruct a runtime equivalent of an ENI file from passively observed traffic, so that OpenEC.Inspector and OpenEC.CLI give named process variables, identified devices and exact WKC checking on a bus for which no ENI.xml was supplied.

**The governing principle: ESI is the schema, the wire is the binding.** Vendor ESI files declare what a device *offers* — PDOs, entries, names, datatypes, sync managers, object dictionary. The master's startup traffic reveals what it actually *selected and where it put it*. Learning mode chains the two.

Target is full ENI equivalence (identity, topology order, cyclic command table, PDO assignment and mapping, named variables), exportable as real ENI XML.

### Non-goals

- Active bus participation of any kind. Learning is 100% passive, consistent with the project's core guarantee. No CoE writes, no SDO Info requests, no frame injection.
- Reading Acontis `.ecd` snapshots (proprietary format).
- Modular/slot device configuration (see Known Limits).
- Distributed-clock, port-topology and error-counter learning. Registers `0x0110`, `0x092C` and `0x0300`+ are observable and named here for later milestones, but are not part of this scope.

## Bringup facts and their sources

Every learned fact traces to specific observed traffic. Phase attribution is per the Beckhoff ESM description ([tc3_io_intro/1446518411](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1446518411.html)):

> **Init→PreOp:** "The master configures the channels of the SyncManagers for the mailbox communication and initializes the synchronization of the distributed clocks."
>
> **PreOp→SafeOp:** "The master uses mailbox communication to set parameters for mapping process data, configuring the channels of the SyncManagers for the process data communication and the channels of the FMMUs."

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

### Register layouts

- **FMMU** (16 bytes at `0x0600 + 16n`): logical start address (4), length (2), logical start bit (1), logical stop bit (1), physical start address (2), physical start bit (1), type (1; 1 = read/inputs, 2 = write/outputs), activate (1).
- **SyncManager** (8 bytes at `0x0800 + 8n`): physical start address (2), length (2), control (1), status (1), activate (1), PDI control (1).

## The offset chain

The FMMU decode is the linchpin. ESI states what a slave offers; `0x1C1x` states which PDOs the master selected; the SyncManager states where those bytes sit in the slave's physical memory; the FMMU maps that physical window into the master's logical process image.

For each slave, for each configured FMMU: find the enabled, non-zero-length SyncManager whose physical start address **equals** the FMMU's physical start. That SM carries the PDOs assigned to it. Equality rather than containment is deliberate: a containment rule would also require adding `(fmmu.PhysicalStart - sm.PhysicalStart) * 8 + fmmu.PhysicalStartBit` to the offset, which the formula below does not carry. When no SyncManager matches, the FMMU's variables cannot be placed at all, and `LearningCompleteness.ProcessDataPlaceable` reports the slave as incomplete rather than the configuration silently coming up short. PDO entries fill the SM data area sequentially in assignment order, so bit offsets accumulate. The resulting global offset is:

```
BitOffs = (FMMU.LogicalStartAddress - imageOriginBytes) * 8
        + FMMU.LogicalStartBit
        + offsetWithinSmBits
```

`imageOriginBytes` is a byte address, `FMMU.LogicalStartBit` and `offsetWithinSmBits` are bit counts, and the result is the bit offset into the input or output image. Input versus output comes from the FMMU type byte.

Two details make this line up with existing code unchanged:

- `EniVariable.BitOffs` is relative to the whole input or output image, and `ProcessVariableMap.Collect` slices on `cmd.InputOffs * 8`. Synthesis therefore picks an image origin: the lowest logical address covered by any input FMMU is the input origin, likewise outputs. Each cyclic command's `InputOffs`/`OutputOffs` is its logical address minus that origin. `LRW` sets both when input and output FMMUs intersect its range.
- ESI padding entries (`Index` 0 with a bit length, `SubIndex` null) are consumed to keep offsets aligned but excluded from the variable list.

`ProcessVariableMap` and `ProcessValueDecoder` require no changes.

### Variable naming

Variable naming follows `{SlaveName}.{PdoName}.{EntryName}`, where `{SlaveName}` is `Slave {StationAddress} ({EsiDeviceName})`, or `Slave {StationAddress}` when ESI resolves nothing. Where ESI cannot supply an entry name, the synthetic form `{SlaveName}.0x{Index:X4}:{SubIndex:X2}` is used and marked as such in provenance.

**The slave component must be unique per slave, not per device type.** `ProcessImage` keys its variable dictionary by name, so two slaves sharing a name silently discard one of them — and an ESI device name is a *type* name shared by every identical terminal on the bus, which is the common case for EtherCAT I/O. TwinCAT's own `{SlaveName}` is the user-assigned *instance* name (`Term 2 (EL1008)`), which a passive observer cannot recover; the station address is unique by definition and always known, so it stands in.

## Components

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

src/OpenEC.Monitor/Synthesis/
  BringupCapture.cs          synthetic INIT→OP generator for testing
```

Each decoder is a pure function over one datagram, independently testable. `LearnedBus` is the only stateful piece and holds no references outside the namespace. `BusLearner` has no reference to `BusObserver`; it consumes decoded frames and emits configuration revisions.

`LearnedConfiguration` wraps rather than replaces the existing type, so every downstream consumer keeps working:

```csharp
public sealed record LearnedConfiguration(
    EniConfiguration Configuration,
    LearningCompleteness Completeness,
    IReadOnlyDictionary<ushort, FactProvenance> Provenance,
    int Revision);

public enum FactSource { Sii, CoeIdentity, Coe, RegisterWrite, EsiDefault, Cache, Ads, Inferred }
```

`Coe` and `CoeIdentity` are distinct on purpose: identity may come from the `0x1018` object specifically, while PDO assignment and mapping come from ordinary CoE downloads. Labelling the latter `RegisterWrite` — as an earlier draft did — misreports the source of the fact in the one type whose job is reporting sources accurately.

`LearningCompleteness` carries per-slave `IdentityKnown`, `SyncManagersKnown`, `FmmusKnown`, `PdoMappingKnown`, and a bus-level `SawStartup`. Nothing silently claims a fact it inferred: the Inspector renders completeness as a status strip, the CLI as a coverage line, and `analyze --json` as a `learning` block.

## Control flow

### One parse, two consumers

`EtherCatMonitor.RunAsync` parses each frame once and hands the `FrameDecodeResult` to both `Observer.Process` and `BusLearner.Observe`. No capture-level tee.

### Offline two-pass

`PcapFileSource.CaptureAsync` opens a fresh reader per call and is therefore re-enumerable. `ICaptureSource` gains a default interface member:

```csharp
bool SupportsMultiplePasses => false;
```

`PcapFileSource` returns `true`. `LiveCaptureSource` and `RecordingCaptureSource` inherit `false` — correct for the latter, since re-enumerating it would re-record. When the flag is set, pass 1 runs `BusLearner` alone (no process-image work), and pass 2 runs the observer with the finished configuration. Result: 100% of process data mapped in offline analysis.

### Live progressive rebind

`BusObserver` gains one entry point, taking the same `_lock` as `Process` and `SetResolvedDeviceName`:

```csharp
public void ApplyConfiguration(LearnedConfiguration config)
```

It reseeds `BusModel` (identity, names, auto-increment map), swaps `ProcessImage`'s variable map and replaces `WkcTracker`'s ENI expectations, while preserving accumulated statistics and the event log. `ProcessImage._map` and `WkcTracker._expectedFromEni` become swappable rather than readonly. The learner emits a revision only when the derived configuration actually changes, so rebinds are rare and need no debounce.

Learned mailbox windows replace `BusObserver.IsMailboxWindow`'s `0x1000–0x2000` guess once SM0/SM1 are known at the IP transition.

### Cross-check

Learning is always on. With no ENI, the learned configuration drives everything. With an ENI loaded, the ENI drives and the learner still runs; `ConfigurationDiff.Compare(declared, learned)` runs on each revision and raises `MonitorEvent.ConfigMismatch` for disagreements — wrong device at a position, unexpected identity, PDO remapped at runtime, FMMU layout that does not match. ENI-only variables are excluded by construction (see Known Limits).

**Master-synthesised ENI variables** have no wire representation. TwinCAT's "Add WC state bit(s)" injects `WcState` and `InputToggle`, both `BIT`, computed by the master ([Data Area](https://infosys.beckhoff.com/content/1033/tf55xx_tc3_mc3/19396190219.html)). The InfoData group — `State`, `AdsAddr`, AoE NetId, Channels, DC shift times, `ObjectId` — is likewise master-side ([General Behavior](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1357974411.html)). A learned configuration cannot contain any of them, so their absence is never a mismatch. `TxPdoState`, `DcInputShift` and `DcOutputShift` are genuine PDO entries on many drives and must **not** be excluded — excluding them would hide a real remapping.

### Cache

Learned configurations persist to `<appdata>/openec/learned/<fingerprint>.eni.xml` as real ENI XML with a `.meta.json` sidecar for completeness and provenance, where `<appdata>` is `Environment.SpecialFolder.ApplicationData` — `%APPDATA%` on Windows, `~/Library/Application Support` on macOS, `~/.config` on Linux — keeping the cross-platform guarantee. The cache, the export feature and the test fixture format are the same artifact, and `EniConfiguration.Load` already reads it back.

The fingerprint is `slave count + ordered (vendor, product, revision) + logical address layout` — deliberately excluding serial numbers, so replacing an identical terminal still hits the cache. On a mid-run attach where identity was never read (see Degradation), a weaker fallback fingerprint of `slave count + station addresses observed in FPRD + cyclic command shape` is used; a hit is not guaranteed and the completeness strip says so. The cache indexes under both the primary and fallback fingerprints (skipping the second write when they coincide); two different buses sharing slave count, station addresses and cyclic shape collide on the fallback key, last write wins.

A cache hit applies immediately; the learner keeps running and refines or overrides it only when the learned configuration becomes complete — i.e., it actually observed a startup. A cached mid-run configuration must not be clobbered by the learner's poorer live picture.

## Degradation

Learning never hard-fails and never requires an observed startup. It reports what it knows.

| Situation | Behaviour |
| --- | --- |
| Full INIT→OP observed | Complete configuration; named variables; ENI export available |
| Attach at OP, cache hit | Cached configuration applied at frame 1; refined only when learner observes a complete startup |
| Attach at OP, cache miss | Station addresses, AL states, cyclic command table and byte ranges; no names; completeness strip states that a master restart would recover PDO mapping |
| Startup checking disabled | Identity falls back to CoE `0x1018`, then ADS tier (when wired), then unknown |
| Bus never reaches OP | No cyclic command table; everything learned up to the reached state is kept |

## Surfaces

### SDK

`EtherCatMonitorOptions.Learning = LearningMode.Auto | Off` (default `Auto`). `EtherCatMonitor.Learned` exposes the current `LearnedConfiguration?`. The event stream gains `ConfigurationLearned` and `ConfigMismatch`.

### CLI

```bash
openec learn capture.pcap --out bus.eni.xml
openec analyze capture.pcap                     # learned coverage; --eni now optional
openec live --interface en11 --learn-out bus.eni.xml
openec analyze capture.pcap --no-learn
```

`analyze --json` gains a `learning` block (completeness, per-slave provenance, mismatches), so CI can gate on "the bus no longer matches the committed ENI".

### Inspector

The Variables tab works with no ENI. A completeness strip in the device editor states what is known and what a master restart would recover. A "Save learned ENI…" session command. `ConfigMismatch` lands in the existing docked messages panel; no new UI surface.

All learning-related events (`ConfigurationLearned`, `ConfigMismatch`) are filterable under "Config" and "Learning" categories. The category toggles cover every emittable event category, so future events cannot become permanently visible.

## Testing

The critical asset is a **synthetic bringup generator** (`BringupCapture` in the `Synthesis` namespace): extend `SampleCapture` and `EtherCatFrameBuilder` to emit a full INIT→OP sequence — station-address assignment, SII identity reads, mailbox SM config at IP, process-data SM plus FMMU plus CoE PDO assign at PS, then cyclic traffic. Bringup happens once on a real bus and is awkward to capture on demand, so the whole feature must be testable without hardware.

- **Unit** — each register and mailbox decoder against hand-built datagrams.
- **Golden** — synthetic bringup for the existing `EL1008.xml` ESI fixture; learned configuration must equal a hand-written expected `EniConfiguration`.
- **Round-trip** — learned → ENI XML → `EniConfiguration.Load` → structurally equal.
- **Degradation** — one test per degradation-table row, plus a mixed `LRW` / `LRD`+`LWR` bus.
- **Cross-check** — a mismatched ENI raises `ConfigMismatch`; a `WcState`-only difference raises none.
- **Concurrency** — `ApplyConfiguration` racing `Process`, asserted via the existing snapshot accessors.
- **Hardware acceptance** — a real ETAP-1000 capture of a TwinCAT bringup. Tests cannot substitute for this; it stays open until run on the bench.

## Known limits

Sourced from the vendor documentation and the ESI catalogue's own stated scope.

- **Modular devices.** `Dahlke.EtherCAT.Esi` documents `EsiProcessData` as excluding modular devices: an EJ-series or slot-based device declares per-slot PDOs under `<Modules>` rather than under `<Device>`. Such slaves learn structure and byte ranges but not named variables.
- **PDOs without a declared sync manager.** Most PDOs declare no `Sm` attribute (per the catalogue's own note, 37,541 of 58,128 in Beckhoff's published set), so SM assignment comes from the wire, not ESI.
- **Startup checking is optional and per slave.** "Check Vendor IDs", "Check product codes", "Check revision number" and "Check serial number" are individually configurable ([tc3_io_intro/1357974411](https://infosys.beckhoff.com/content/1033/tc3_io_intro/1357974411.html)). Unchecked means the master never reads identity from the wire.
- **Addressing mode.** `AutoInc only – No Fixed Address` means no `APWR` to `0x0010` to anchor on; `No AutoInc – Use 2. Address` means the master reads a fixed address from the slave instead of assigning one. The learner keys slaves by ring position when the station-address anchor is absent.
- **`Add WC state bit(s)`** injects master-synthesised `WcState` input variables into the process image. They appear in the ENI and have no wire representation. The diff engine treats ENI-only variables as expected, never as a mismatch.
- **`Final State`** may be INIT, PREOP or SAFEOP, so a bus may never produce cyclic traffic.
- **`Use LRD/LWR instead of LRW`** is per slave, so both datagram shapes coexist on one bus.
- **PDI-side identity reads** are invisible to a passive observer.
- Learning reflects what the master *did*, not what the ENI *says*. Learned and declared configurations can legitimately differ; that difference is the cross-check's output, not an error.

### Observed but unmodelled

Named here so a later milestone does not have to rediscover them: watchdog registers `0x0400` (multiplier), `0x0410` (PDI), `0x0420` (SM); DL status `0x0110` for port topology; DC registers `0x0910`/`0x092C`; per-port error counters `0x0300`–`0x0310`.

The TwinCAT Online tab's CSV export — name, physical address, auto-increment address, vendor ID, product code, revision, serial, state, and CRC counters per port — is precisely this feature's fact set, and is a ready-made precedent should a tabular export be wanted.

## Design decisions and refinements

These decisions emerged during implementation and refinement; they capture the rationale for choices that depart from or sharpen the initial design.

### Cache latching on hit, not on attempt

The cache consult is latched on a successful **hit**, not on the first consult **attempt**. On a mid-run attach, slaves are discovered one at a time, so the first published revision knows only one slave. Its fingerprint cannot match the saved multi-slave bus, the lookup misses, and the complete picture arriving a frame later would never be looked up if the latch burned its shot on the first miss. Latching on a hit means every revision retries while still incomplete and nothing cached has been applied. This is bounded: revisions stop once the bus picture stabilises, and each retry is a `File.Exists` probe.

### Cached configuration protected from being overwritten

After applying a cached configuration, learned revisions only re-apply when the learner's own configuration becomes complete (i.e., it actually observed a startup). A mid-run attach produces a learner picture that is strictly worse than the cache (no SyncManagers, no FMMUs, zero variables). Allowing every revision to overwrite would clobber a complete cached configuration with the capture's own incomplete picture. Once the wire shows a startup, the wire genuinely is the better source and takes over.

### Periodic resolver and final pass serialised

The periodic schema-resolution timer and the final resolution pass at shutdown are serialised: the monitor cancels and awaits the resolver **before** the final pass. Both snapshot the same pending slaves and both reach `Republish(force: true)`, so without serialisation they would produce two revision bumps for identical content.

### Value snapshot inside the lock

`ResolveSchemasAsync` snapshots slave identity **values** (vendor, product, revision) inside the gate, not just the `LearnedSlave` object references. `Nullable<uint>` is a bool+uint struct and is not written atomically, so a concurrent write from the pump thread can be observed torn (hasValue true with a stale value), producing a wrong ESI lookup. Snapshotting the values inside the lock closes the race entirely.

### Category toggles cover every emittable event

The Inspector's event-category filter toggles must cover every category `EventFormatter` can emit. Prior to this milestone, categories "Config", "Learning" and "Other" had no toggle, so events falling into those categories were permanently visible — new categories broke the filter. Adding all three makes the filter robust against any future event type.

### Cross-check compares process images by placement, not by name

`ConfigurationDiff` compares process images by placement (BitOffs, BitSize, IsInput), with names kept only for the message. Learned names are synthesised as `Slave {addr} ({esi})…` while an ENI carries the master's own instance labels like `Term 2 (EL1008)…`. They can never match on name, so cross-checking any TwinCAT ENI against a learned bus would report every declared variable as "not in the learned image" if name were the key. Placement is the contract for process data.

### SlaveMissing and ProcessImage checks gated on completeness

`ConfigurationDiff` reports `SlaveMissing` and `ProcessImage` mismatches only when `Completeness.IsComplete` is true. Mismatches computed against a half-learned bus (e.g., only one of two slaves discovered) are never retracted, so they would report false claims like "Term 2 not seen on the bus" when Term 2 is present but not yet learned. `Identity` and `SlaveUnexpected` are ungated: they describe a slave already seen and are true on sight.

### Cross-check deduplicates on (Kind, Address, Declared, Observed)

Mismatches are deduplicated on `(Kind, Address, Declared, Observed)` rather than emitted raw. Without deduplication, comparing a half-learned configuration against an ENI produces one mismatch per declared variable per revision — 86 total for 9 distinct findings in the test fixture — and the `Take(20)` display cutoff drops the flagship "Identity" finding entirely.

### Two-pass offline pins ordering-independence

The offline two-pass mode's value is ordering **independence**, not just completeness. A real master configures FMMUs and PDOs before process data starts, so single-pass already maps everything in the normal case. What two-pass actually guarantees is correctness that does not rely on an assumption about master behaviour — which matters for a runtime PDO remap, a merged capture, or a TAP that dropped early frames.

### FMMU/SyncManager block decoders bounded by register count

`TrySyncManagers` and `TryFmmus` are bounded by both payload length **and** block number. ETG.1000.4 defines 16 SyncManagers and 16 FMMUs per slave (numbered 0-15), so a write starting at block 15 with a 32-byte payload must not fabricate a block 16 that no slave has. A bogus SyncManager can later match a real FMMU by physical address, producing phantom variable placements.

### Mailbox completeness checks on both SM0 and SM1

Mailbox ranges require **both** SM0 and SM1 to be known before being applied. A half-learned mailbox map (SM0 known, SM1 unknown) suppresses CoE emergency detection, so partial knowledge is strictly worse than none in an error-reporting path. The fingerprint does not digest mailbox ranges, so "SM1 became known" never publishes a revision on its own — the completeness check must explicitly gate on both.

### ADS identity tier drops unanswered reads rather than zeroing them

`AdsBusSnapshot.ScannedIdentities()` drops entries whose identity did not answer (identity fields are `uint?` and null together when the per-slave identity read did not respond) rather than zeroing them. `PhysicalAddress` is always present; the identity is the thing in doubt. Zeroing a failed read and stamping it `FactSource.Ads` would report a confident master-side identity for a slave nobody ever identified, violating provenance honesty.

### ADS enrichment guards against churn

`ApplyAdsIdentity` republishes only when something changed. The API is designed for a 1 Hz ADS poll, so republishing unconditionally would bump the revision and re-fire `ConfigurationLearned` on every poll after the bus is identified. A slave whose ADS identity synthesises to the same `EniSlave` values (vendor/product/revision) but flips provenance from `Inferred` to `Ads` still triggers a publish with `force: true`, because provenance is not in the fingerprint but still matters.

### Cache indexes under both fingerprints

The cache writes under both the primary fingerprint (slave count + ordered vendor/product/revision + logical layout) **and** the fallback fingerprint (slave count + station addresses + cyclic shape), skipping the second write when they coincide. This makes mid-run attaches able to hit the cache, since a mid-run capture never observes identity and can only compute the fallback key. Caveat: two different buses sharing the fallback fingerprint collide, last write wins — inherent to a weaker fingerprint.

### Discovery pass must not bypass the cache policy

The offline two-pass mode's discovery-pass tail must **not** call `Observer.ApplyConfiguration(learned)` directly. That bypasses the cache-hit handler and stomps a genuine cache hit back to the raw learned configuration. `Republish` fires the `ConfigurationLearned` event whenever it sets `Current`, including the forced republish after schema resolution, so the handler has already applied everything the pass produced — and unlike a direct call, it knows whether a cached configuration is in force and must not be overwritten.

### Cache directory pre-created and cross-platform

The cache directory is created at option-construction time if `LearnedCache` is set, preventing `DirectoryNotFoundException` on first save. The cache path is `<appdata>/openec/learned/`, where `<appdata>` is `Environment.SpecialFolder.ApplicationData` — `%APPDATA%` on Windows, `~/Library/Application Support` on macOS, `~/.config` on Linux.

## Related design documents

- [./monitor-and-cli.md](./monitor-and-cli.md) — M1: OpenEC.Monitor SDK + OpenEC.Monitor.Ads + OpenEC.CLI
- [./inspector.md](./inspector.md) — M2: Avalonia Inspector GUI + explorer shell
- [./topology-view.md](./topology-view.md) — Port-level topology map
- [./README.md](./README.md) — Design index

Project root: [../../README.md](../../README.md), [../tap-setup.md](../tap-setup.md)
