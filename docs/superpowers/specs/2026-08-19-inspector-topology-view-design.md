# OpenEC-Diagnostics — Topology View Design (port-level network map)

**Date:** 2026-08-19
**Status:** Approved
**Scope:** New `Topology/` namespace in `OpenEC.Monitor`; two new decoders in
`RegisterDecoders`; a `Ports` fact collection on `LearnedSlave`; `PreviousPort` on `EniSlave`
plus its parse in `EniConfiguration` and its emit in `EniXmlWriter`; DL-status reads in
`BringupCapture`; a tabbed explorer pane and a new `TopologyViewModel`/`TopologyView` in
`OpenEC.Inspector`; a resizable explorer column in `MainWindow.axaml`. No changes to the
frame decoder, the capture sources, the ADS module, the process-image or PDO-mapping paths,
or `MainWindowViewModel.OnNodeSelected`.

This milestone claims the work the learning-mode design deferred in its §10 ("DL status
`0x0110` for port topology; per-port error counters `0x0300`–`0x0310`").

## 1. Goal

Draw the bus as engineers actually picture it: a branched physical map showing which port of
which device the next line hangs off, with per-port link and error state on every box, and
selecting a box opening that device's editor.

The reference is the Acontis EC-Inspector Topology View — a master box heading a line of
devices, junction devices opening further lines, orthogonal wires between them, and small
per-port bars on each box.

The governing principle carries over from learning mode: **the wire is the authority, the ENI
is a declaration.** Where both describe the topology, the wire wins and the difference is a
finding, not an error. Where neither does, the map says so rather than drawing a plausible
guess.

Non-goals for this milestone:

- **Flat View.** EC-Inspector's third explorer tab. Not in scope; the tab strip is built to
  take a third tab later.
- **The `ESC Register`, `Extended Diagnosis` and `CoE Object-Dictionary` editor tabs.** This
  spec makes the per-port counters available, which is most of what an `Extended Diagnosis`
  tab would need, but the tab itself is a separate surface.
- **The `Short Info` panel** and the Diagnosis/Learning/Step/Trigger toolbar.
- **Active bus participation of any kind.** Unchanged project guarantee: no reads we initiate,
  no frame injection. Every fact here is observed or read from an ENI.
- **Distributed-clock topology** (`0x0910`/`0x092C`), still unmodelled.
- **A CLI topology surface.** The exported ENI gains `<PreviousPort>`, so topology is already
  visible to other tooling; a `analyze --json` topology section is deferred.

## 2. Facts and their sources

| Fact | Source on the wire | Phase |
| --- | --- | --- |
| Per-port physical link, loop state, signal detected | Returning `FPRD`/`BRD` of **0x0110** (DL status), 2 bytes | INIT onward, and cyclically on masters that watch for topology change |
| Per-port invalid-frame / CRC and RX error counters | Returning read of **0x0300 + 2n** (invalid frame) and **0x0301 + 2n** (RX error), n = port | any |
| Per-port forwarded RX error counter | Returning read of **0x0308 + n** | any |
| Per-port lost-link counter | Returning read of **0x0310 + n** | any |
| Processing-unit error counter | Returning read of **0x030C** | any |
| PDI error counter | Returning read of **0x030D** | any |
| Ring order | Already learned: `APWR` to **0x0010**, auto-increment ADP (learning-mode design §2) | INIT |
| Parent device and parent/child port | ENI `<Slave><PreviousPort>`: phys address + port | offline |

The register set above is corroborated by EC-Inspector's own Extended Diagnosis labels, which
name exactly these offsets per port.

**DL status (0x0110), 16 bits:**

| Bits | Meaning |
| --- | --- |
| 0 | PDI operational |
| 1 | PDI watchdog status |
| 2 | Enhanced link detection |
| 4, 5, 6, 7 | Physical link on port 0, 1, 2, 3 |
| 8, 10, 12, 14 | Loop closed on port 0, 1, 2, 3 (0 = open, 1 = closed) |
| 9, 11, 13, 15 | Signal detected on port 0, 1, 2, 3 |

The four port states the map renders derive from that triple:

| Physical link | Loop | Rendered state |
| --- | --- | --- |
| no | closed | **Unused** — nothing plugged in. No bar drawn. |
| yes | open | **Active** — link up and forwarding |
| yes | closed | **Blocked** — cable present, frames not passing. A fault worth seeing. |
| no | open | **Dangling** — loop open with no link. Frames leave into nothing: a pulled cable on a port the master has not closed. |

The two states that matter diagnostically are the mixed ones. An ESC auto-closes a port that
has no link, so **Blocked** and **Dangling** are both the link and the loop bit disagreeing —
which is exactly the condition a topology map should make impossible to miss.

Signal-detected (bits 9/11/13/15) is recorded per port but does not drive the rendered state.
It distinguishes a partner that is powered from one that is not, and is surfaced in the box
tooltip rather than the bar.

Decoders follow the shape of the existing five in `RegisterDecoders`: pure, keyed on ADO plus
`FrameDirection`, reads taken only on the returning path and only with `WorkingCounter != 0`,
since a zero working counter means no slave answered and the payload is meaningless.

## 3. Reconstruction

Two independent producers of the same model, resolved by provenance.

**From the wire.** Ring order is already known. Each device's *active* port set comes from DL
status. Walk devices in ring order carrying a stack of devices with unused downstream ports:

1. Push the master, the root, at ring position −1.
2. For each device in ring order: pop the stack until its top has an unused active downstream
   port; attach the device to that port, entering on its own port 0; mark that parent port
   consumed; push the device.
3. Repeat. A device with no active downstream port is popped on the next iteration, which is
   what ends a line and returns the walk to the nearest ancestor with a port left.

A junction is therefore a consequence of a device's port count — it is simply a device the
walk revisits — never a device type we have to recognise.

Downstream ports are consumed in the ESC's internal forwarding order, **0 → 3 → 1 → 2**, so a
device with ports 1 and 2 both active is entered at 0, branches out of 1 first, and continues
out of 2 when that subtree returns. See §10 — this ordering must be confirmed against hardware
before the branch order can be trusted.

Because the walk is bounded by ring order, every device is placed at most once and a cycle is
structurally impossible.

**From the ENI.** `<PreviousPort>` states the parent's physical address and the port directly,
so the edge needs no reconstruction. `EniSlave` gains a nullable `PreviousPort` record; a null
means the ENI did not declare one, never a defaulted parent.

**Resolution.** The wire's topology wins wherever it exists. The ENI supplies edges for
devices the wire never described. An edge both describe differently is drawn as the wire has
it, marked on the map, and reported to the messages panel through the existing
`ConfigurationDiff` path — the same treatment every other ENI-versus-wire disagreement gets.

## 4. Components

New, in `src/OpenEC.Monitor/Topology/`:

- **`PortState`** — one port's link, loop and signal booleans plus its rendered state.
- **`PortCounters`** — that port's four error counters, every one nullable: absent means never read.
  Separate from `PortState` because the two come from different registers and arrive at different
  times; a combined record would have to be rebuilt on every counter read.
- **`BusTopology`** — the resolved tree. Nodes carry the device reference, parent, parent
  port, own port, port states, and a `FactSource` for the edge. Also carries the *unplaced*
  set (§7).
- **`TopologyReconstructor`** — pure. Takes ring-ordered devices with their port states plus
  optional ENI edges, returns a `BusTopology`. No I/O, no Avalonia, no mutation of its input.

Changed:

- **`RegisterDecoders`** — gains `TryDlStatus` and `TryPortCounters`, plus the register
  constants.
- **`LearnedSlave`** — gains `Ports`, a dictionary keyed by port index. An absent key means
  the port was never observed, consistent with the type's existing contract that a null
  property means *not seen*, never a defaulted stand-in.
- **`EniModels` / `EniConfiguration`** — `EniSlave.PreviousPort` and its parse.
- **`EniXmlWriter`** — emits `<PreviousPort>` for learned topology. Because the writer also
  produces the learned-bus cache, topology survives a cache round-trip and appears in the
  exported ENI without extra work.
- **`BringupCapture`** — emits DL-status reads so a branched bus exists as a synthetic
  fixture, the same way it already makes learning testable without hardware.

New, in `src/OpenEC.Inspector/`:

- **`TopologyLayout`** and **`TopologyLayoutEngine`** (§5) — pure geometry.
- **`TopologyViewModel`** — implements `IRefreshable`. Holds box and wire view models, the
  zoom factor, and the unplaced set.
- **`TopologyBoxViewModel`** — holds a reference to the *same* `ExplorerNode` instance the
  tree uses, so selection is by identity.
- **`TopologyView.axaml`** — the `ItemsControl` over a `Canvas`.

## 5. Layout engine

A pure function from `BusTopology` to an immutable `TopologyLayout`: device boxes with
position, size and kind; orthogonal wire polylines; per-box port marks carrying side and
state. It references no Avalonia type, so it tests as plain data.

- **Row-based.** The master's line runs left to right on row 0. Each branch opens a new row
  beneath its parent's row. Wires route Manhattan-style, which is what produces the
  right-then-down-then-left path a return edge takes in the reference image.
- **Deterministic.** The same topology yields byte-identical geometry. This is both what makes
  it testable and what stops the map jittering as facts arrive mid-session.
- **Two box widths.** Structurally significant devices — line heads, line ends, and anything
  opening a branch — are wide with a horizontal label; the rest are narrow with a rotated
  label. This is a single predicate in the engine. It is an approximation of the reference
  image's rule, which is not documented; see §10.
- **Port sides are fixed by index.** Port 0 renders on the box's left edge (upstream, toward
  the master), port 1 on the right (the line continuing), ports 2 and 3 beneath. That is what
  puts the bars under the branch-opening devices in the reference image.
- **Recomputed only on topology change.** Port colours, counters and AL state update by
  mutating observable properties on existing box view models — the row-reuse discipline
  `ExplorerViewModel.Refresh` already uses to keep selection stable across ticks. A bus that
  stays put gets one layout pass per session.

Zoom is a `ScaleTransform` bound to the zoom selector; pan is the enclosing `ScrollViewer`.
Neither affects layout.

## 6. Inspector surfaces

**The explorer pane becomes tabbed.** `ExplorerView` hosts a `TabControl` with
`TabStripPlacement="Bottom"` and two tabs: **Classic View** (today's `TreeView`, unchanged)
and **Topology View**. Tab styling comes from the existing house theme in
`Theme/Controls.axaml`, so light/dark tracking is inherited rather than reimplemented.

**Selection stays single and shared.** `ExplorerViewModel` remains the sole owner of
`SelectedNode`. Both tabs bind to it, and a box holds the same `SlaveNode` instance its tree
row holds, so clicking a box selects by identity. Consequences:

- `MainWindowViewModel.OnNodeSelected` is unchanged. The Device Editor opens from a box
  exactly as it opens from a row.
- Switching tabs preserves the selection, and the selected box is highlighted in the map.
- The `M1` box maps to the root `NetworkNode`, so clicking the master shows the Dashboard,
  identical to clicking the root row.

**Junction nodes split by what was observed:**

- A junction that is a real addressable device (EK1122, CU1128 and similar) is an ordinary
  `SlaveNode` drawn with junction styling. It is fully selectable and has a General tab like
  any other device.
- A branch point the port states reveal but whose identity was never observed becomes a
  synthesized, non-selectable node labelled as inferred. It is never given a fabricated
  station address. The reference image's `RE1`/`RE2`/`RE3` boxes are this kind.

  **Deferred, and why.** No fact source in §2 produces such a node: reconstruction places only
  devices the bus reported, and a real junction (EK1122, CU1128) is itself an addressable slave
  that arrives as an ordinary device. The first milestone therefore implements the junction
  *styling* and marks an inferred *edge*, but adds no pseudo-node type that nothing constructs.
  This becomes real work the moment a fact source reveals branch points without identities.

**The pane becomes resizable.** `MainWindow.axaml` goes from `ColumnDefinitions="280,*"` to
`"280,Auto,*"` with a `GridSplitter`. The pane keeps a remembered width per tab — narrow for
the tree, wide for the map — so selecting Topology View gives the map room without a drag.

**Topology change is an event.** A link appearing or dropping mid-session recomputes the
geometry and emits a messages-panel entry ("topology change: link lost on 1013 port 1"),
alongside the state changes, WKC faults, CoE emergencies and SoE errors already streaming
there. For a passive diagnostic tool this is the feature's sharpest edge: the map shows where
the cable went, the log says when.

## 7. Degradation

A passive observer routinely sees less than everything. Each shortfall has one defined,
honest rendering.

- **No port data at all** — the master never polls `0x0110` and no ENI was loaded. The tab
  still opens and draws the devices as a single line in ring order, with an explicit note that
  topology was not observed. Ring order is genuinely known; the ports are not, so **no port
  bars are drawn** rather than grey ones implying "no link".
- **Partial port data** — devices whose parent cannot be determined go to an *unplaced* strip
  below the map, labelled as such. They are never guessed into the tree.
- **Contradictory port data** — a stack underflow during reconstruction (more line ends than
  branches opened) means the port states disagree with each other. The devices placed so far
  stand; the remainder goes to the unplaced strip. No exception, no partially painted map.
- **Counters never read** — the port bar shows link state alone. A counter that was never
  observed is null and renders as unknown, never as `0`.
- **ENI disagrees with the wire** — §3's resolution rule: the wire is drawn, the edge is
  marked, the disagreement is reported.
- **Topology recomputation** — selection survives, because boxes reference the same
  `SlaveNode` instances the tree does.

## 8. Testing

Every risky part is a pure function, so nearly all of it tests without a UI.

- **Decoders** — bit-layout cases for `TryDlStatus` per port, and for each counter offset in
  `TryPortCounters`, plus the direction and `WorkingCounter` guards. Mirrors
  `RegisterDecoderTests`. This is where a wrong constant hides, so each port gets an explicit
  case rather than a loop.
- **Reconstruction** — table-driven from port-state fixtures: straight line; single branch;
  nested branches; the reference image's three-row shape including its return edge;
  contradictory data landing in the unplaced strip; and a bus with no port data at all
  degrading to ring order.
- **Layout** — geometry assertions: no overlapping boxes, wire endpoints landing on the ports
  they claim, and identical output across two runs on the same input.
- **ENI** — `<PreviousPort>` parsed from a new branched fixture, and a round trip (learned
  topology → `EniXmlWriter` → re-parse → same topology) which is the learned-bus cache
  contract.
- **Synthesis** — `BringupCapture` extended to emit DL-status and counter reads for its existing
  two-slave *line*, plus a separate `BranchedBusCapture` for the branched shape. Two fixtures
  rather than one because `BringupCapture`'s two-slave shape is asserted by a dozen existing tests,
  and widening it would change what they mean. Together they give CLI and Inspector tests both
  topologies with no hardware attached.
- **UI** — one headless `AvaloniaFact`: the Topology View tab renders; clicking a box selects
  the same node clicking its tree row selects; switching tabs preserves selection. In the vein
  of the existing `ShellSmokeTests`.
- **Hardware** — the §10 assumptions confirmed against a real TwinCAT capture on the ETAP-1000.

## 9. Delivery order

The implementation plan for this spec is
`docs/superpowers/plans/2026-08-19-inspector-topology-view.md`.


The two layers are independently valuable, and the plan should stage them in this order:

1. **The fact layer** (§2, §3) — decoders, reconstruction, ENI `PreviousPort`, the
   `EniXmlWriter` emit, and the `BringupCapture` fixture. Shippable on its own: the exported
   ENI and the learned-bus cache gain topology before any UI exists, and every §8 test except
   the headless UI one is writable here.
2. **The view** (§5, §6) — layout engine, tabbed pane, resizable column, topology-change
   events.

Stage 1 has no dependency on stage 2, and stage 2 needs nothing from stage 1 that §3 does not
already define, so the layout engine can be built against hand-written `BusTopology` fixtures.

## 10. Assumptions to verify

Recorded explicitly so they are not mistaken for settled facts. Each is a single constant or
predicate in one place. As of the Milestone 4 release none has been confirmed against
hardware — verification remains pending on a real TwinCAT capture over an ETAP-1000 segment
(§8), so the constants below stand as documented assumptions, pinned only by the decoder tests.

- **The `0x0110` bit layout** in §2 is per ETG.1000.4 and is not corroborated by an indexed
  vendor page (Beckhoff InfoSys does not document the ESC register set). The decoder tests pin
  it; a real capture confirms it.
- **The ESC internal port forwarding order `0 → 3 → 1 → 2`** determines the order branches are
  visited, and therefore the row order of the map. A capture of a bus with a known junction
  confirms it.
- **The port letter mapping** (`A`/`B`/`C`/`D` ↔ port index 0–3) used to read ENI
  `<PreviousPort>` is one documented translation table. Until confirmed it is marked
  unverified in the code, not asserted.
- **The reference image's wide/narrow box rule** is undocumented; §5 states our approximation.

## 11. Known limits

- **We see only what the master reads.** Port state and counters reach the map only if the
  master polls those registers. TwinCAT reads DL status because it needs it for
  topology-change detection, but a master that reads it once at startup gives a static map,
  and one that never polls the error counters leaves them permanently unknown. This is
  inherent to passive monitoring, not a defect, and §7 defines what the map shows instead.
- **A master using `AutoInc only – No Fixed Address`** has no station-address anchor, so
  devices are keyed by ring position (learning-mode design §9). Topology reconstruction is
  unaffected — it needs ring order, which that mode still provides — but ENI edge matching by
  physical address cannot resolve.
- **Non-addressable junction hardware** appears only as an inferred node, since nothing on the
  wire identifies it.
- **The map reflects what the master did, not what the ENI says.** As with learning, a
  legitimate difference is the cross-check's output, not an error.
