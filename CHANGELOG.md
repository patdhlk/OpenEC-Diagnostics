# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While the version is below `1.0.0`, the public API of `OpenEC.Monitor` may
change in any minor release.

## [Unreleased]

### Fixed

- Inspector UI text is no longer invisible on Windows. The real cause was the
  GPU text path — Avalonia 12.1's default Windows backend (ANGLE, after the
  SkiaSharp 3.119 bump) rasterises geometry but drops glyph runs on some
  drivers, so every label rendered blank while borders and buttons still drew.
  The Inspector now forces Skia's software renderer on Windows. The ineffective
  0.1.1 font-default change is reverted, restoring the native system fonts.
  macOS and Linux are unaffected.

## [0.1.1] - 2026-09-05

### Changed

- Defaulted the Inspector UI to the bundled Inter font as an attempt to fix
  invisible text on Windows. This did not resolve the regression and was
  reverted in 0.1.2, which carries the actual fix.

## [0.1.0] - 2026-09-04

### Added

- First public release: the `OpenEC.Monitor` SDK, the `openec` CLI, and the `OpenEC.Inspector` desktop application, consolidating Milestones 1–4 (detailed below).
- Open-source project files: Apache-2.0 `LICENSE`, `NOTICE`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `SECURITY.md`, issue and pull-request templates,
  Dependabot configuration, and a cross-platform CI workflow building and
  testing on Linux, macOS, and Windows.
- `.editorconfig` capturing the formatting conventions the codebase follows,
  enforced in CI via `dotnet format --verify-no-changes`.

## Milestone 4 — Topology View

### Added

- **Topology View** in `OpenEC.Inspector`: a physical map of the bus drawn from
  passively observed facts — the master, each device in its real position,
  junctions opening branches, and a per-port bar coloured by link state
  (forwarding, closed loop, or open with no partner). Clicking a device selects
  it exactly as clicking its tree row does.
- Per-port link state decoded from DL status (`0x0110`) and the ESC error
  counters (`0x0300`–`0x030D`, `0x0310`–`0x0313`) as the master polls them.
- Bus-tree reconstruction from ring order and port state, exported as ENI
  `<PreviousPort>` topology edges.
- ENI `<PreviousPort>` parsing, with wire-versus-file disagreements reported to
  the messages panel.
- A resizable, width-aware explorer pane with tabbed Classic and Topology views.
- Bus health tracking — master state, found versus configured device counts, and
  distributed-clock sync — surfaced in `analyze`, `live`, and the Inspector.
- Detection of process data that stops changing: a device whose application has
  hung while its EtherCAT chip keeps answering is invisible to every other
  protocol-level measure. Reported as a warning rather than a fault, since a
  legitimately steady input is common.

### Fixed

- A cyclic parent graph no longer crashes the Inspector.
- A broadcast datagram is no longer attributed to a single device.
- The INIT scan is retained rather than discarded during learning.
- Completeness no longer reports a state the bus had already grown out of.
- Port-mark colours refresh on live state change; duplicate-`PhysAddr` ENI files
  are handled rather than trusted.

## Milestone 3 — Learning mode

### Added

- **ENI-independent bus discovery.** Identity, topology order, PDO mapping, and
  the cyclic command table are reconstructed from observed startup traffic and
  exported as real ENI XML (`openec learn`, `live --learn-out`).
- Learning is on by default. Offline captures get a discovery pass first, so
  process data is mapped from the first frame regardless of where in the file
  the configuration appeared; live sessions rebind progressively as the picture
  firms up. `--no-learn` opts out.
- Cross-checking against a loaded ENI, reporting where the bus disagrees.
- A learned-bus cache keyed by a fingerprint of the bus, so attaching mid-run to
  an already-running machine still decodes process data. Located under
  `%APPDATA%\openec\learned`, `~/Library/Application Support/openec/learned`, or
  `~/.config/openec/learned`; relocatable with `OPENEC_CACHE_DIR`.
- Optional ADS enrichment (`live --ads <AMS NetId>`): where a TwinCAT master's
  startup checking is disabled and the wire never reveals a device's identity,
  the master's own bus scan fills it in.
- Provenance on every learned fact, naming which source it came from.
- Per-device coverage display in the Inspector, and **Save learned ENI…**.

## Milestone 2 — OpenEC.Inspector

### Added

- **`OpenEC.Inspector`**, a cross-platform Avalonia desktop application over the
  `OpenEC.Monitor` SDK, supporting live TAP capture and offline
  `.pcap`/`.pcapng` analysis.
- An explorer shell: a device tree with live status dots for the master and each
  device, and a tabbed device editor — **General** for AL state, identity, and
  mailbox activity; **Variables** for the decoded process-variable watch.
- A docked messages panel streaming state changes, WKC faults, CoE emergencies,
  SoE errors, and ENI-versus-bus disagreements.
- Recording of live sessions to a `.pcap` file as they run.
- A light/dark house theme that follows the OS setting.

## Milestone 1 — SDK and CLI

### Added

- **`OpenEC.Monitor`**, a passive EtherCAT decoding SDK: EtherCAT datagrams,
  working counters, mailbox protocols (CoE, FoE, EoE, SoE), and process data.
- **`OpenEC.CLI`** (`openec`) with `analyze`, `frames`, `devices`, `live`,
  `learn`, and `gen-sample`.
- ENI and ESI parsing, mapping raw byte payloads to named process variables.
- Offline `.pcap` / `.pcapng` reading, and live capture from a Network TAP
  monitor port.
- **`OpenEC.Monitor.Ads`**, optional read-only TwinCAT master enrichment
  over ADS.

[0.1.1]: https://github.com/patdhlk/OpenEC-Diagnostics/releases/tag/v0.1.1
[0.1.0]: https://github.com/patdhlk/OpenEC-Diagnostics/releases/tag/v0.1.0
