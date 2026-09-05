# TAP Setup Guide

OpenEC-Diagnostics is a **passive** observer: it never joins the EtherCAT
ring as a Master or SubDevice. To see traffic it needs a copy of the wire
signal, delivered by a Network TAP (Test Access Point) sitting between the
Master and the first SubDevice. This guide covers wiring a DUALCOMM
ETAP-1000, granting your OS the packet-capture permissions `openec` needs,
and verifying the whole chain end-to-end (with an offline fallback if you
don't have hardware in front of you yet).

> Throughout this guide, `openec` refers to the built CLI executable. During
> development, run everything as `dotnet run --project src/OpenEC.CLI --
> <args>`. To get a standalone binary named `openec`, publish with:
> ```bash
> dotnet publish src/OpenEC.CLI -c Release -o publish
> ```
> which produces `publish/openec` (macOS/Linux) or `publish\openec.exe`
> (Windows).

## Recommended TAPs

Any fully-passive Fast-Ethernet (100BASE-TX) copper TAP that aggregates both
directions onto one monitor port works with `openec`. Known-good options:

- **Dualcomm** — Zero-Delay Fast Ethernet Copper TAP, ETAP-1000 (wired below)
- **Profitap** — ProfiShark 100M
- **Profitap** — ProfiShark 1G
- **Beckhoff** — ET2000 Industrial-Ethernet Multi-Channel Probe (EtherCAT-native)

## Runtime prerequisites

Live capture and even offline pcap reading both load a native pcap library:

- **Windows**: requires [Npcap](https://npcap.com/) installed in WinPcap API-compatible mode. This is needed even for offline pcap analysis.
- **Linux**: requires `libpcap0.8` (Debian/Ubuntu: `apt install libpcap0.8`) and `libssl3` (the learned-bus cache hashes with SHA-256 via OpenSSL).
- **macOS**: ships libpcap; no additional installation needed.

These requirements were verified in a bare Debian container.

## 1. Wiring the DUALCOMM ETAP-1000

The ETAP-1000 is a fully-passive copper TAP with two inline ports (A/B) and
one aggregating monitor port. Splice it into the EtherCAT segment between
the Master's NIC and the first SubDevice:

```text
                +-------------------------------+
 Master NIC ----| Port A                Port B  |---- First slave (EK1100)
                |                                |
                |           Monitor port         |
                +---------------|-----------------+
                                 |
                                 v
                          Capture NIC
                     (runs `openec live`)
```

- **Master NIC -> Port A**: the EtherCAT Master's transmit port plugs into
  Port A of the TAP.
- **Port B -> first slave (EK1100)**: Port B continues the ring to the
  first SubDevice (typically an EK1100 coupler).
- **Monitor port -> capture NIC**: the monitor port connects to a *third*
  NIC on the machine running `openec`. This is the interface you pass to
  `--interface`.

The TAP itself is fully passive — it does not require external power and
introduces no additional latency or single point of failure on the ring.
Unplugging the capture NIC (or the whole monitoring PC) has zero effect on
the live EtherCAT segment.

### A note on direction pairing

The monitor port **aggregates both directions of traffic onto one wire**:
the frame the Master sends out, and the same frame after it has looped
through every SubDevice and come back, both arrive on the single capture
NIC. `openec` figures out which is which automatically
(`OpenEC.Monitor.Observation.DirectionTracker`):

1. **Primary heuristic**: EtherCAT SubDevices set bit `0x02` (the
   "locally administered" bit) of the frame's source MAC address on the
   return path. Once both a set and a clear value have been observed on
   the capture, that bit alone classifies every subsequent frame as
   outbound or returning.
2. **Fallback**: until both bit values have been seen (e.g. very early in
   a capture, or with a NIC that doesn't set the bit), `openec` pairs
   frames by matching `(index, command, address)` tuples — the first
   sighting of a given key is treated as outbound, the second as the
   returning echo.

You don't need to configure anything for this — it's automatic — but it
explains why `openec frames` shows a `->` / `<-` column per frame even
though only one physical link is being captured.

## 2. Capture permissions

Reading raw Ethernet frames requires elevated privileges on every
platform. Pick the option below for your OS.

### macOS

macOS gates raw capture behind the BPF (`/dev/bpf*`) devices, which by
default are only readable by root. Check current ownership:

```bash
ls -l /dev/bpf*
```

You'll typically see something like:

```text
crw-------  1 root  wheel   23,  0 Aug 15 10:00 /dev/bpf0
crw-------  1 root  wheel   23,  1 Aug 15 10:00 /dev/bpf1
```

You have two options:

1. **Quick / per-run**: run the CLI with `sudo`:

   ```bash
   sudo dotnet run --project src/OpenEC.CLI -- devices
   sudo openec live --interface en0 --duration 10
   ```

2. **Persistent (recommended for repeated use)**: install Wireshark's
   ChmodBPF launch daemon. Wireshark ships a small `install-ChmodBPF`
   package (also runnable standalone from
   `/Applications/Wireshark.app/Contents/Resources/extras/ChmodBPF.pkg` if
   you already have Wireshark installed, or via `brew install wireshark`
   with the "install ChmodBPF" prompt). It creates an `access_bpf` group
   and a LaunchDaemon that changes `/dev/bpf*` ownership to
   `root:access_bpf` with group-read/write permissions on every boot. Add
   your user to that group once:

   ```bash
   sudo dseditgroup -o edit -a "$(whoami)" -t user access_bpf
   ```

   Log out and back in (group membership is read at login), then confirm:

   ```bash
   ls -l /dev/bpf*
   # crw-rw----  1 root  access_bpf   23, 0 ...
   groups | tr ' ' '\n' | grep access_bpf
   ```

   After this, `openec devices` and `openec live` work without `sudo`.

### Linux

Grant the capture capabilities directly to the published binary instead of
running the whole CLI as root:

```bash
sudo setcap cap_net_raw,cap_net_admin+eip $(command -v openec)
```

(`command -v openec` must resolve to the published executable — point it
at the actual path, e.g. `sudo setcap cap_net_raw,cap_net_admin+eip
./publish/openec`, if it isn't on your `PATH`.) `setcap` needs to be
re-applied after every `dotnet publish` since it rebuilds the binary.
Alternatively, just run via `sudo`:

```bash
sudo dotnet run --project src/OpenEC.CLI -- devices
```

### Windows

Install [Npcap](https://npcap.com/) (the maintained successor to WinPcap;
SharpPcap, the capture library `OpenEC.Monitor` uses, requires it). During
installation, check **"Install Npcap in WinPcap API-compatible Mode"**.
No further permission steps are needed — Npcap handles the driver-level
access — though you may still need to run your terminal as Administrator
depending on your local security policy.

## 3. Verification walkthrough

### With hardware (ETAP-1000 in place)

1. List available interfaces and identify the one wired to the TAP's
   monitor port:

   ```bash
   openec devices
   ```

2. Pick that interface and run a short timed capture:

   ```bash
   openec live --interface <if> --duration 10
   ```

   You should see a live-updating dashboard (frame count, frame rate,
   estimated cycle time, WKC mismatches, bus/slave state) for 10 seconds,
   followed by a session summary line and exit code 0 if no bus errors
   were seen.

### Without hardware (offline)

If you don't have a TAP wired up yet, verify the toolchain against a
synthetic capture instead:

```bash
openec gen-sample demo.pcap
openec analyze demo.pcap --eni <your ENI.xml>
```

`gen-sample` writes a self-contained synthetic EtherCAT capture; `analyze`
decodes it and prints a bus-health report, mapping datagrams to the named
slaves declared in `<your ENI.xml>` (see "Exporting `ENI.xml` from
TwinCAT" below for where to get a real one, or omit `--eni` to see raw
addresses only).

## 4. Exporting `ENI.xml` from TwinCAT

`--eni` lets `openec` resolve raw ADP/ADO addresses and process-data
offsets to the human-readable slave and variable names configured in your
TwinCAT project. To export it:

1. Open your TwinCAT project in TwinCAT XAE (Visual Studio shell).
2. In the **Solution Explorer**, expand **I/O** and select the
   **Device (EtherCAT)** node for your Master.
3. Double-click it to open the device editor, and switch to the
   **EtherCAT** tab.
4. Click **Export Configuration File** and save it — this writes the
   `ENI.xml` describing the full cyclic frame layout, slave list, and
   process image for the configured Master.
5. Pass that file to `--eni` on `analyze` or `live`:

   ```bash
   openec analyze demo.pcap --eni C:\path\to\ENI.xml
   ```

### ESI files (`--esi-dir`)

Vendor ESI (EtherCAT Slave Information) XML files provide device names and
descriptions independent of a specific project's ENI. TwinCAT installs its
bundled ESI library at:

```text
C:\TwinCAT\3.1\Config\Io\EtherCAT
```

Point `--esi-dir` at that folder (or a copy of it on non-Windows capture
machines) to enrich slave names when no ENI is available, or to fill in
details an ENI alone doesn't carry:

```bash
openec analyze demo.pcap --esi-dir C:\TwinCAT\3.1\Config\Io\EtherCAT
```

`--eni` and `--esi-dir` can be combined; both `analyze` and `live` accept
them.
