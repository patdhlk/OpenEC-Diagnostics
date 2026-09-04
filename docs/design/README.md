# Design record

This directory is the design record of OpenEC-Diagnostics. Each milestone was
specified before it was built, and the design was kept current as the work
proceeded, so these documents describe both the intended design and where the
implementation deliberately departed from it. Each one consolidates the
milestone's specification, the refinements made while building it, and the
decisions taken along the way.

They were written as working documents for AI-assisted development, which is
why they read a little unusually in places. Ignore that framing; the
engineering content is the point.

**These are historical records, not living documentation.** They describe the
design at the moment each milestone was built. Where a document and the code
disagree, the code is right. For current behaviour, read
[the README](../../README.md) and [docs/tap-setup.md](../tap-setup.md).

## Contents

| Document | Subject |
| --- | --- |
| [`monitor-and-cli.md`](monitor-and-cli.md) | The `OpenEC.Monitor` core SDK, the optional `OpenEC.Monitor.Ads` active module, and the `openec` CLI |
| [`inspector.md`](inspector.md) | The Avalonia `OpenEC.Inspector` GUI — session engine, views, and the explorer-shell restructure |
| [`learning-mode.md`](learning-mode.md) | ENI-independent bus discovery — reconstructing a runtime ENI equivalent from passive traffic |
| [`topology-view.md`](topology-view.md) | The port-level physical topology map and the SDK fact layer that feeds it |

## Cross-cutting principles

These invariants hold across every milestone. They explain the parts of the
architecture whose reasons are not visible in the code.

- **Passive by construction.** The tool never transmits on the bus. It decodes
  traffic observed through a network TAP or read from a `.pcap`/`.pcapng` file.
  The only active path is `OpenEC.Monitor.Ads`, which talks to a TwinCAT master
  over ADS; it lives in its own project and the core SDK carries zero TwinCAT
  dependencies.

- **The GUI stays passive.** `OpenEC.Inspector` references `OpenEC.Monitor`
  only — never `OpenEC.Monitor.Ads` — so the desktop app cannot initiate bus or
  master traffic.

- **Single writer, snapshot readers.** `BusObserver` is single-writer under one
  lock; the capture pump is the only writer. Concurrent readers use snapshot
  accessors. UIs (the CLI live dashboard and the Inspector) poll snapshots on a
  timer and never subscribe to per-frame callbacks — at ~500 fps bus traffic,
  sampled state is the right shape, event-push into a UI thread is not.

- **The wire is the authority; the ENI is a declaration.** Vendor ESI files
  declare what a device offers; the master's startup traffic reveals what it
  actually selected and where. Learning mode chains the two ("ESI is the schema,
  the wire is the binding"). Where the wire and a supplied ENI disagree, the
  wire is used, and the difference is reported as a finding — never silently
  treated as an error.

- **Never claim a fact that was not observed.** A value that was never seen is
  reported as unknown, not as a plausible default. Completeness is surfaced
  explicitly (a coverage line in the CLI, a status strip in the Inspector, a
  `learning` block in `analyze --json`).
