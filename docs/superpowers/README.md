# Design specs, plans, and decisions

This directory is the design record of OpenEC-Diagnostics. Each milestone was
specified before it was built, and the spec and plan were kept as the work
proceeded, so these documents describe both the intended design and where the
implementation deliberately departed from it.

They were written as working documents for AI-assisted development, which is
why they read a little unusually — plans are checkbox task lists addressed to
an implementer, and some carry instructions meant for a coding agent rather
than a human reader. Ignore that framing; the engineering content is the point.

**These are historical records, not living documentation.** They describe the
design at the moment each milestone was built. Where a document and the code
disagree, the code is right. For current behaviour, read
[the README](../../README.md) and [docs/tap-setup.md](../tap-setup.md).

## Contents

### `specs/`

What each milestone should do and why, including the EtherCAT behaviour the
design has to accommodate.

| Spec | Subject |
| --- | --- |
| `2026-08-15-openec-diagnostics-m1-design.md` | The `OpenEC.Monitor` SDK and `openec` CLI |
| `2026-08-16-openec-inspector-m2-design.md` | The Avalonia Inspector application |
| `2026-08-17-inspector-explorer-shell-design.md` | The explorer shell — device tree and tabbed editor |
| `2026-08-18-learning-mode-design.md` | ENI-independent bus discovery |
| `2026-08-19-inspector-topology-view-design.md` | The port-level physical topology map |

### `plans/`

Task-by-task implementation plans derived from the specs. Each opens with the
architecture, the tech-stack pins, the global constraints, and — most usefully
for a reader — a list of deliberate refinements against its spec, with the
reasoning for each.

### `decisions/`

Standalone decision logs for questions settled during implementation.
`2026-08-19-learning-mode-integration-rulings.md` records the rulings made
while integrating learning mode into the CLI and Inspector.

## Why keep them

The specs and plans explain the parts of the architecture whose reasons are not
visible in the code: why UI code reads `BusObserver` only through snapshots,
why `OpenEC.Inspector` does not reference `OpenEC.Monitor.Ads`, why a stalled
device is reported as a warning rather than a fault. Anyone making a
substantial change is better off reading the relevant spec first.
