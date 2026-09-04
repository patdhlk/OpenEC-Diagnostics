# OpenEC-Diagnostics 🚀

[![CI](https://github.com/patdhlk/OpenEC-Diagnostics/actions/workflows/ci.yml/badge.svg)](https://github.com/patdhlk/OpenEC-Diagnostics/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Contributions Welcome](https://img.shields.io/badge/contributions-welcome-brightgreen.svg?style=flat)](CONTRIBUTING.md)

**OpenEC-Diagnostics** is a free, open-source C# implementation of EtherCAT monitoring and diagnostic tools. Inspired by proprietary industry standards like Acontis `EC-Monitor` and `EC-Inspector`, this project provides both a powerful developer SDK and a ready-to-use graphical application to passively analyze, troubleshoot, and monitor EtherCAT networks.

---

## 📖 About The Project

EtherCAT networks often require deep traffic analysis for troubleshooting, state tracking, and process data verification. Historically, developers have had to rely on expensive, proprietary tools to decode this traffic. 

This repository provides an open-source alternative built entirely in modern C# / .NET, divided into two main components:

1. **`OpenEC.Monitor` (The SDK)**: A managed C# library/SDK that decodes raw EtherCAT frames. It acts as a passive observer via a Network TAP (Test Access Point) or by analyzing `.pcap` files, parsing Process Data (PDOs), SubDevice states, and error counters, exposing them through a clean C# API.
2. **`OpenEC.Inspector` (The Application)**: A user-friendly diagnostic application (built on top of `OpenEC.Monitor`) that allows engineers to visualize network topology, inspect real-time traffic, and troubleshoot issues independently of the Master controller.

> **Passive by design.** OpenEC-Diagnostics observes and never transmits. This
> is the constraint the whole architecture rests on, not a current limitation —
> it is what makes the tool safe to attach to a running machine.

## ⬇️ Download / Install

Prebuilt CLI packages are available for Windows, macOS, and Linux. The Inspector GUI can be downloaded from the releases page.

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/patdhlk/OpenEC-Diagnostics/main/install.sh | sh
```

**Windows:**

```powershell
irm https://raw.githubusercontent.com/patdhlk/OpenEC-Diagnostics/main/install.ps1 | iex
```

For manual downloads and the Inspector GUI, visit the [releases page](https://github.com/patdhlk/OpenEC-Diagnostics/releases/latest). Archive names follow the pattern `openec-<version>-<os>-<arch>.tar.gz` (Linux/macOS) or `openec-<version>-<os>-<arch>.zip` (Windows).

**Runtime prerequisites:** Linux requires `libpcap0.8` and `libssl3`; Windows requires [Npcap](https://npcap.com/) even for offline pcap analysis; macOS ships libpcap. Building from source requires the .NET 8 SDK.

## ✨ Key Features

* **100% Passive Monitoring**: Listen to network traffic via a standard Network TAP without interfering with the Master or SubDevices. Zero risk of disrupting the active machine process.
* **Hardware & Master Agnostic**: Works alongside *any* EtherCAT Master (TwinCAT, Acontis, IgH, SOEM, etc.) and *any* vendor's SubDevices.
* **Deep Frame Decoding**: Accurately decodes EtherCAT Datagrams, Working Counters (WKC), Mailbox protocols (CoE, FoE, EoE, SoE), and Process Data.
* **ENI Configuration Parsing**: Import `ENI.xml` (EtherCAT Network Information) files to automatically map raw byte payloads to human-readable process variables.
* **Offline Analysis**: Built-in support for reading and analyzing Wireshark/`pcap` / `pcapng` packet captures offline.
* **Cross-Platform**: Built on .NET 8, capable of running on Windows, Linux, and macOS.

## 🏗️ Architecture

```text
OpenEC-Diagnostics/
├── src/
│   ├── OpenEC.Monitor/          # The core SDK (frame decoding, pcap/ENI/ESI parsing, learning)
│   ├── OpenEC.Monitor.Ads/      # Optional read-only TwinCAT master enrichment over ADS
│   ├── OpenEC.CLI/              # `openec` — command-line interface for headless monitoring
│   └── OpenEC.Inspector/        # The GUI application (Avalonia, cross-platform)
├── tests/
│   ├── OpenEC.Monitor.Tests/    # SDK and CLI tests, plus capture fixtures
│   └── OpenEC.Inspector.Tests/  # Headless Avalonia UI and view-model tests
├── docs/
│   ├── tap-setup.md             # TAP wiring, capture permissions, ENI export
│   └── superpowers/             # Design specs, implementation plans, decision logs
└── README.md
```

`OpenEC.Inspector` deliberately references `OpenEC.Monitor` only — never
`OpenEC.Monitor.Ads` — so the GUI stays purely passive.

## 🚀 Getting Started

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
All dependencies resolve from nuget.org; nothing else is needed to build.

```bash
git clone https://github.com/patdhlk/OpenEC-Diagnostics.git
cd OpenEC-Diagnostics
dotnet build
dotnet test
```

The suite runs in seconds. It needs no hardware, no network,
and no capture permissions — every case either uses a checked-in capture or
synthesises frames at test time.

The `openec` CLI (`src/OpenEC.CLI`) covers device listing, synthetic sample
generation, offline capture analysis, and live monitoring. During
development, invoke it via `dotnet run`:

```bash
# Generate a synthetic EtherCAT capture to poke at
dotnet run --project src/OpenEC.CLI -- gen-sample demo.pcap

# Analyze it (--eni maps raw addresses to human-readable slave/variable names)
dotnet run --project src/OpenEC.CLI -- analyze demo.pcap --eni <your ENI.xml>

# Generate a synthetic capture that includes bus startup, then reconstruct its configuration
dotnet run --project src/OpenEC.CLI -- gen-sample bringup.pcap --bringup
dotnet run --project src/OpenEC.CLI -- learn bringup.pcap --out bus.eni.xml

# Learning runs by default. `analyze --json` reports what it worked out, and with
# an ENI supplied it also lists where the bus disagrees with the file.
dotnet run --project src/OpenEC.CLI -- analyze bringup.pcap --eni bus.eni.xml --json
dotnet run --project src/OpenEC.CLI -- analyze bringup.pcap --no-learn   # opt out

# Dump individual decoded datagrams
dotnet run --project src/OpenEC.CLI -- frames demo.pcap --count 10

# List capture-capable interfaces on this machine
dotnet run --project src/OpenEC.CLI -- devices

# Monitor a live interface wired to a TAP's monitor port for 10 seconds
dotnet run --project src/OpenEC.CLI -- live --interface <if> --duration 10

# Same, but write whatever the session learned about the bus to an ENI file
dotnet run --project src/OpenEC.CLI -- live --interface <if> --duration 10 \
    --learn-out bus.eni.xml

# With a TwinCAT target reachable over ADS, the master's own bus scan fills in the
# identity of any slave whose identity the wire never revealed (startup checking off)
dotnet run --project src/OpenEC.CLI -- live --interface <if> --ads <AMS NetId>
```

`analyze` and `live` cache every fully learned bus under the per-user application-data
directory (`%APPDATA%\openec\learned`, `~/Library/Application Support/openec/learned`,
`~/.config/openec/learned`), keyed by a fingerprint of the bus. That is what lets a later
mid-run attach to an already-running machine decode process data. Set `OPENEC_CACHE_DIR`
to relocate it, or pass `--no-learn` to switch learning and its persistence off entirely.

Live capture (`devices` / `live`) needs OS-level packet-capture
permissions, and connecting to real hardware needs a Network TAP spliced
into the EtherCAT segment. See **[docs/tap-setup.md](docs/tap-setup.md)**
for the full wiring diagram, macOS/Linux/Windows permission setup, a
verification walkthrough, and how to export `ENI.xml` from TwinCAT.

## 🔍 OpenEC.Inspector (GUI)

A cross-platform Avalonia desktop app over the same SDK:

```bash
dotnet run --project src/OpenEC.Inspector
```

Pick a live capture interface (the TAP monitor port, e.g. `en11`) or open a
`.pcap`/`.pcapng` file, optionally load an ENI — live sessions can also be
recorded to a `.pcap` file as they run. Once a session starts, an explorer
shell takes over: a device tree on the left shows the master and each slave
with a live status dot, and selecting a node opens a tabbed editor —
**General** for AL state, identity, and mailbox activity, **Variables** for
the decoded process-variable watch. The watch does not need an ENI: if the
capture caught the master bringing the bus up, the process image is learned
from that traffic instead, and the General tab says per slave how much of it
is known. **Save learned ENI…** writes that reconstruction out as a real ENI
file. A docked messages panel along the bottom streams state changes, WKC
faults, CoE emergencies, SoE errors, and any disagreement between a loaded
ENI and what the bus actually shows. The whole app follows a light/dark house
theme that tracks the OS setting.

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

Live capture needs the same BPF permissions as the CLI — see `docs/tap-setup.md`.

## 📌 Status

- **Milestone 1**: `OpenEC.Monitor` SDK + `OpenEC.CLI` —
  passive frame decoding, ENI/ESI-aware naming, offline `.pcap`/`.pcapng`
  analysis, and live TAP monitoring from the command line.
- **Milestone 2**: `OpenEC.Inspector`, an Avalonia-based graphical
  application built around an explorer shell — a device tree with live status dots for
  the master and each slave, a tabbed device editor (General for AL state/identity/
  mailbox activity, Variables for the decoded process-variable watch), and a docked
  messages panel streaming state changes, WKC faults, CoE emergencies, and SoE errors —
  supporting live network TAP capture and offline `.pcap`/`.pcapng` analysis, fully
  passive and cross-platform.
- **Milestone 3**: learning mode — ENI-independent bus discovery, integrated.
  Identity, topology order, PDO mapping and the cyclic command table are reconstructed from
  observed startup traffic and export as real ENI XML (`openec learn`, `live --learn-out`).
  Learning is on by default: offline captures get a discovery pass first, so process data is
  mapped from the first frame regardless of where in the file the configuration appeared, while
  live sessions rebind progressively as the picture firms up. With an ENI loaded the learner
  cross-checks it and reports where the bus disagrees. A bus whose startup was seen once is
  cached by fingerprint, so attaching mid-run to a running machine still decodes process data.
  Where the master's startup checking is disabled and the wire never reveals identity, an
  optional ADS poll fills it in — and every fact carries provenance saying which of those it
  came from. The Inspector shows per-slave coverage and can save the reconstruction.
- **Milestone 4**: the Topology View — port-level physical network map in the Inspector,
  fed by DL-status and ESC error-counter facts learned passively from the wire, with
  topology changes streamed to the messages panel.
- **Next**: pcap replay with pacing control, frame-level packet browser, DC and port-topology
  diagnostics, and standalone app packaging (Windows MSI, macOS app bundle, Linux Flatpak).

## 📚 Documentation

- **[docs/tap-setup.md](docs/tap-setup.md)** — TAP wiring diagram, packet-capture
  permissions on macOS/Linux/Windows, a verification walkthrough, and how to
  export `ENI.xml` from TwinCAT.
- **[docs/superpowers/](docs/superpowers/README.md)** — the design record: a
  spec and an implementation plan per milestone, plus decision logs. They
  explain the parts of the architecture whose reasons are not visible in the
  code. Worth reading before a substantial change.
- **[CHANGELOG.md](CHANGELOG.md)** — what landed in each milestone.

## 🤝 Contributing

Contributions are welcome — decoding bugs backed by a capture are the most
valuable thing this project can receive. Please read
**[CONTRIBUTING.md](CONTRIBUTING.md)** first: it covers the build, the
conventions the codebase follows (the `BusObserver` snapshot contract, the
passive-only constraint, commit style), and what makes a good bug report.

For anything beyond a small fix, open an issue before writing code. EtherCAT
behaviour is subtle and agreeing on an approach first is much cheaper.

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). To report
a security vulnerability, see **[SECURITY.md](SECURITY.md)** — please do not
open a public issue.

## ⚖️ License

Licensed under the **[Apache License 2.0](LICENSE)**.

Third-party dependencies and their licenses are listed in
**[NOTICE](NOTICE)**. All are permissive: the `Dahlke.EtherCAT.*` SDK packages
are Apache-2.0, most others are MIT, and `PacketDotNet` (pulled in transitively
by SharpPcap) is MPL-2.0 — a file-level copyleft that imposes no obligation on
this project, which uses the published package unmodified.

## 📋 A note on captures

A capture from a live EtherCAT segment contains the machine's full process
image — every commanded position, every sensor reading, every device serial
number — and the master's MAC address identifies real equipment. Treat
captures, and the learned-bus cache derived from them, with the same care as
the machine's own data. This matters most when attaching one to a bug report.

## ™️ Trademarks

EtherCAT® is a registered trademark and patented technology, licensed by
Beckhoff Automation GmbH, Germany. TwinCAT® is a registered trademark of
Beckhoff Automation GmbH. EC-Monitor and EC-Inspector are products of acontis
technologies GmbH.

This project is an independent implementation and is not endorsed by,
sponsored by, or affiliated with Beckhoff Automation GmbH, the EtherCAT
Technology Group, or acontis technologies GmbH. Those names are used only to
describe interoperability and prior art.
