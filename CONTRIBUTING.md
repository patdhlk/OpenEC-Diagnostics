# Contributing to OpenEC-Diagnostics

Thanks for wanting to help. This document covers what you need to build the
project, the conventions the codebase already follows, and what a good pull
request looks like here.

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), the same terms that cover the project.

## Getting set up

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Nothing else — all dependencies come from nuget.org, including the
`Dahlke.EtherCAT.*` packages this SDK builds on.

```bash
git clone https://github.com/patdhlk/OpenEC-Diagnostics.git
cd OpenEC-Diagnostics
dotnet build
dotnet test
```

The full suite runs in a couple of seconds. It needs no
hardware, no network, and no elevated permissions — everything either uses a
checked-in capture or synthesises frames at test time.

If you want to work against real hardware, you need a Network TAP spliced into
an EtherCAT segment and OS-level packet-capture permissions. See
[docs/tap-setup.md](docs/tap-setup.md) for wiring, permissions on
macOS/Linux/Windows, and how to export an `ENI.xml` from TwinCAT. You do not
need any of this to contribute to the decoding, learning, or UI layers.

## What the project is, and is not

**OpenEC-Diagnostics is strictly passive.** It observes; it never transmits.
This is the project's central constraint, not a current limitation. The tool is
designed to be safe to attach to a running machine, and a contribution that puts
a frame on the wire — even a well-intentioned one, even behind a flag — breaks
the guarantee the whole design rests on. If you have a use case that seems to
need active access, open an issue and let's talk about it first.

Concretely:

- `OpenEC.Monitor` decodes captured frames. It has no transmit path.
- `OpenEC.Monitor.Ads` reads bus information from a TwinCAT master over ADS.
  That is a read-only side channel to the *master*, not to the EtherCAT
  segment, and it is optional — `OpenEC.Inspector` deliberately does not
  reference it.

## Repository layout

```text
src/OpenEC.Monitor/       Core SDK — frame decoding, pcap/ENI parsing, learning
src/OpenEC.Monitor.Ads/   Optional TwinCAT ADS enrichment (read-only)
src/OpenEC.CLI/           `openec` command-line tool
src/OpenEC.Inspector/     Avalonia desktop application
tests/                    xUnit suites mirroring the two main projects
docs/                     TAP setup guide and per-milestone design docs
```

`docs/design/` holds the consolidated design document for each milestone — the
specification, the deliberate refinements made while building it, and the
decisions behind them. They were written as working documents for AI-assisted
development, so they read a little unusually, but they are the best record of
*why* the architecture is shaped the way it is — worth reading before a
substantial change. See [docs/design/README.md](docs/design/README.md).

## Conventions

These are already followed throughout the codebase; please match them.

**Target framework and language settings** come from the root
`Directory.Build.props` (net8.0, nullable enabled, implicit usings, latest
lang). Do not set `<TargetFramework>` in individual project files.

**Nullable reference types are enabled and warnings are not suppressed.**
Fix the nullability rather than annotating around it.

**Formatting** is enforced by `.editorconfig`. Run `dotnet format` before you
push; CI checks it.

**Culture-invariant formatting.** Every user-visible number and timestamp uses
`CultureInfo.InvariantCulture`, so tests pass on any machine.

**The `BusObserver` snapshot contract.** `BusObserver` has a single writer —
the frame pump. UI and reporting code reads it *only* through
`SnapshotSlaves()`, `SnapshotEvents()`, `Statistics`, and
`ProcessImage.Current`. Never iterate `Observer.EventLog` or `Bus.Slaves`
directly from a view-model or a command; doing so races the pump.

**Test fixtures.** Prefer synthesising frames at test time via
`OpenEC.Monitor.Synthesis.SampleCapture` over adding a binary file. The two
checked-in `.pcap` fixtures exist because they capture real-world behaviour
that is hard to synthesise faithfully; adding a third needs a reason of the
same kind.

## Commit messages

The history follows [Conventional Commits](https://www.conventionalcommits.org/)
with a scope naming the area touched:

```text
feat(topology): decode DL status 0x0110 into per-port link state
fix(learning,topology): a broadcast datagram names no single slave
docs(spec): design the port-level topology view
test(topology): synthesise port traffic and a branched bus fixture
```

Common scopes: `monitor`, `cli`, `inspector`, `learning`, `topology`, `eni`,
`esi`, `observation`, `spec`, `plan`.

**Write a real body.** The subject line says what changed; the body should say
what was actually going on — what the bus does, why the obvious approach was
wrong, what the change deliberately does not do. The inline examples above are
the standard. A commit that explains an EtherCAT behaviour is worth more than
one that describes a diff, because the diff is already there.

## Pull requests

1. **Open an issue first** for anything beyond a small fix. Protocol
   behaviour is subtle and it is much cheaper to agree on an approach before
   the code exists.
2. **One concern per PR.** A decoding fix and a UI change are two PRs.
3. **Add tests.** Every behavioural change needs a test that fails without it.
   If the behaviour depends on real bus traffic that cannot be synthesised,
   say so in the PR and we will work out a fixture together.
4. **`dotnet build` must be warning-free** and `dotnet test` must be green
   across Linux, macOS, and Windows — CI runs all three.
5. **Say how you verified it.** If you tested against real hardware, name the
   master and the devices. If you only have synthetic captures, say that too;
   it is useful information, not a weakness.

## Reporting a decoding bug

Decoding bugs are the most valuable issues this project gets, and the most
frustrating to receive without evidence. A good report has:

- **A capture.** A `.pcap`/`.pcapng` trimmed to the frames that matter. Please
  check that it does not contain anything you cannot publish — process data
  from a production machine can be commercially sensitive, and the master's
  MAC address identifies real equipment.
- **The `ENI.xml`**, if you have one and can share it.
- **What you expected and what you got** — ideally the output of
  `openec analyze <capture> --json`.
- **The master and devices involved**, with versions.

If you cannot share the capture, say so and describe the traffic; a
`openec frames <capture> --count N` excerpt with the payload redacted is often
enough to get started.

## Security

Do not open a public issue for a security vulnerability. See
[SECURITY.md](SECURITY.md).

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md).
