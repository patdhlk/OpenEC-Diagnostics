# Security Policy

## Supported versions

Only the latest release receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest release | ✅ |
| older releases | ❌ |

## Reporting a vulnerability

**Please do not open a public issue.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/patdhlk/OpenEC-Diagnostics/security/advisories/new),
or by email to <patrick.dahlke@pichu.io>.

Please include what the issue is, how to reproduce it, and — if it involves a
malformed input — the smallest capture or `ENI.xml` that triggers it.

You can expect an acknowledgement within a week. Since this is a personal
project rather than a funded one, a fix timeline depends on severity; I will
tell you what to expect rather than leave you guessing. If you plan to
disclose publicly, please give me 90 days, and tell me if you need it sooner.

## What is in scope

The realistic attack surface is **untrusted input parsing**. The tool exists to
be pointed at files and traffic that someone else produced:

- **`.pcap` / `.pcapng` files** — malformed captures reaching the frame
  decoder and the pcap reader.
- **`ENI.xml` files** — malformed or hostile configuration XML.
- **ESI files** — device description XML.
- **Live capture from the wire** — a device on the observed segment emitting
  deliberately malformed EtherCAT frames.
- **The learned-bus cache** — files under `%APPDATA%\openec\learned` /
  `~/Library/Application Support/openec/learned` / `~/.config/openec/learned`
  (or `OPENEC_CACHE_DIR`), which are read back and trusted on later runs.

Memory-safety-adjacent bugs (unhandled exceptions that crash a long-running
monitoring session, unbounded allocation from a length field, infinite loops
on a crafted frame) are all in scope, and the graph-traversal and
length-prefix paths are the parts worth looking at hardest.

## What is not in scope

- **Anything requiring an active writer on the EtherCAT segment.** This tool
  is strictly passive: it never transmits. An attacker who can put frames on
  an EtherCAT segment already controls the machine, and that is outside what
  a passive observer can defend against.
- **The packet-capture permissions themselves.** Live capture needs
  OS-level privileges (BPF on macOS, `CAP_NET_RAW` on Linux, Npcap on
  Windows). How your OS grants those is your OS's concern; see
  [docs/tap-setup.md](docs/tap-setup.md) for the least-privilege setup.
- **Vulnerabilities in dependencies.** Report those upstream. Tell me anyway
  if OpenEC-Diagnostics is affected, so the pin can be bumped.

## A note on what this tool sees

A capture from a live EtherCAT segment contains the full process image of the
machine — every commanded position, every sensor reading, every device serial
number. Treat captures, and the learned-bus cache derived from them, with
the same care as the machine's own data. This matters most when attaching a
capture to a bug report; see [CONTRIBUTING.md](CONTRIBUTING.md#reporting-a-decoding-bug).
