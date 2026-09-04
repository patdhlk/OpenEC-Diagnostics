# OpenEC-Diagnostics — Inspector Explorer Shell & House Style Design

**Date:** 2026-08-17
**Status:** Approved
**Scope:** `OpenEC.Inspector` (shell restructure + full re-skin), one new pure type in `OpenEC.Monitor.Eni`, and the matching tests. No capture-path, `BusObserver`, or `MonitorSession` changes.

## 1. Goal

Turn the Inspector's plain FluentTheme window (nav buttons + page switch) into an
engineering-tool explorer shell in the Dahlke house style: a device tree as primary
navigation, a tabbed per-device editor, an always-visible docked messages panel, and
chrome top/status bars — the layout family of EC-Inspector, in the colors and
flat-panel look of the Inspector's house theme.

Non-goals (ledgered for later):

- Value charts / sparklines (EC-Inspector's Chart pane) — stays on the M3 list.
- A Short Info docked box under the tree — the General tab carries the same facts.
- A manual light/dark toggle — the theme follows the OS (`RequestedThemeVariant="Default"`).
- Binding-log smoke assertion (M3 seed) — separate concern, not pulled in.
- Per-frame browsing, multiple sessions, ADS enrichment — unchanged M2 non-goals.
- `BusObserver` 10k event-log cap behavior (M3 seed) — unchanged.

Two M3 seed items **are** pulled in because this design forces them:
**events-view append-only diffing** (the messages panel now renders continuously)
and **AL badge colors** (the tree's status dots and the editor's state badge).

## 2. Shell layout

With a session active (the Start screen stays full-window as today):

```
┌──────────────────────────────────────────────────────────────┐
│ chrome top bar: "OpenEC Inspector" · source description ·    │  ← Chrome bg, 2px
│                                    [Stop session]            │    Accent bottom border
├───────────────┬──────────────────────────────────────────────┤
│ Explorer tree │  Editor area                                 │
│ (Panel bg)    │  network root  → Dashboard tiles             │
│  ● network    │  slave node    → Device editor (tabs)        │
│   ● slave     │  process image → unmatched-variables watch   │
│   ● slave     │                                              │
│   ◦ Process   ├──────────────────────────────────────────────┤
│     Image     │  Messages panel (docked, collapsible)        │
├───────────────┴──────────────────────────────────────────────┤
│ chrome status bar: state dot · StatusText (incl. rec →)      │  ← 2px Accent top border
└──────────────────────────────────────────────────────────────┘
```

Removed: the 150px `StackPanel` of nav buttons, the `InspectorSection` enum and
`SelectSectionCommand`, and the two-pane `TopologyView`. Tree selection is the
navigation. The fault banner overlay stays as-is on top of the editor area.

## 3. Theme resources

New `Theme/` folder in `OpenEC.Inspector`, merged into `App.axaml` after `FluentTheme`:

- **`Theme/Palette.axaml`** — a `ResourceDictionary` with `ThemeDictionaries` for
  `Light` and `Dark`, defining the house theme's tokens as `Color` +
  `SolidColorBrush` resources. Brush keys mirror the CSS names:

  | Key | Light | Dark |
  |---|---|---|
  | `Bg` | `#eef0f2` | `#242729` |
  | `Panel` / `Panel2` / `Panel3` | `#ffffff` / `#f5f6f7` / `#eceef0` | `#2e3236` / `#33383c` / `#3a4045` |
  | `Line` / `Line2` | `#d5d9dc` / `#e9ecee` | `#464c52` / `#3b4045` |
  | `Ink` / `Ink2` / `Ink3` | `#2e3439` / `#5f676d` / `#8d949a` | `#e6e9eb` / `#adb5bb` / `#7f878d` |
  | `Chrome` / `Chrome2` / `ChromeLine` | `#3f444a` / `#4e555c` / `#5a6168` | `#1d2023` / `#2b3035` / `#383e43` |
  | `ChromeInk` / `ChromeInk2` | `#f2f4f6` / `#a9b1b8` | `#eef1f3` / `#98a1a8` |
  | `Accent` / `AccentSoft` | `#66b2ff` / `#e2eefb` | `#66b2ff` / `#2c3d50` |
  | `Ok` / `Fail` / `Oos` / `Maint` / `Fc` | `#0d8a16` / `#cd4e4e` / `#b78e00` / `#1b8fbf` / `#ee8632` | `#44b34b` / `#e06e6e` / `#d1a91d` / `#41a8d6` / `#f09a55` |

  `Maint`/`Fc` may be unused initially; the full set is ported so future work never
  invents off-palette colors.

- **`Theme/Controls.axaml`** — global styles enforcing the house look over Fluent:
  `CornerRadius="0"` on Button/TextBox/TabItem/ToggleButton/etc. (sharp corners
  everywhere), window `FontFamily="Segoe UI, Helvetica Neue, Arial"` (falls through
  to Helvetica Neue on macOS), monospace values via `Consolas, Menlo, monospace`,
  and shared style classes: `chrome` (bars), `panel`/`tile` (flat `Panel` bg +
  1px `Line` border), `label` (`Ink2`, small), `value` (large, semibold, tabular
  numerals), `mono`, `dot` (status ellipse).

Dashboard tiles lose `CornerRadius="6"` and the grey-alpha `#11888888`/`#33888888`
colors in favor of `Panel`/`Line`/`Ink2` tokens. All remaining hardcoded colors in
views (fault banner, warning glyph) move to tokens (`Fail`, `Oos`).

## 4. Explorer tree

An Avalonia `TreeView` bound to a small node model (interface level; internals are
the plan's call):

```csharp
public enum StatusDot { Idle, Ok, Oos, Fail }   // Idle renders Ink3

public abstract partial class ExplorerNode : ObservableObject
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private StatusDot _dot;
}
public sealed partial class NetworkNode : ExplorerNode      // children = slaves + process image
public sealed partial class SlaveNode : ExplorerNode        // ushort Address
public sealed partial class ProcessImageNode : ExplorerNode // [ObservableProperty] bool IsVisible
```

- **Network root** — label = `Session.SourceDescription`; dot: Running → `Ok`,
  Faulted → `Fail`, Completed/Stopped/Idle → `Idle`. Selecting shows the Dashboard.
- **Slave nodes** — ordered by address, label `"{DisplayName} ({Address})"`. Dot
  implements the AL badge colors: error flag → `Fail` (overrides state), OP → `Ok`,
  SAFEOP/PREOP → `Oos`, INIT/BOOT/unseen → `Idle`. The same mapping renders the
  state badge in the editor's General tab — one converter, two uses.
- **Process Image node** — visibility rule: no ENI loaded → visible (selecting shows
  the Load-ENI call-to-action); ENI loaded and unmatched variables exist → visible,
  shows only the unmatched variables; ENI loaded and everything matched → hidden.

The 4 Hz tick updates labels/dots on existing node instances (the row-reuse pattern
`TopologyViewModel.Refresh` uses today) — no tree rebuild, so expansion and
selection survive ticks. New slaves appearing mid-session insert in address order.

Selection drives the editor: `NetworkNode` → `DashboardViewModel`, `SlaveNode` →
`DeviceEditorViewModel`, `ProcessImageNode` → the unmatched-variables watch (the
reshaped PV-watch VM). Stopping a session clears tree and editor back to Start.

## 5. Device editor

House-style tab strip (accent underline on the active tab), two tabs:

- **General** — today's Topology detail promoted to a full pane: identity block
  (vendor / product / revision hex, physical address, AL state badge with dot
  colors, error flag, last seen), state history list, mailbox activity list
  (emergencies / SoE errors).
- **Variables** — this slave's process variables: filter box + rows of
  (name **without** the slave prefix, DataType, monospace value, updated time).
  No ENI loaded → the same Load-ENI CTA, wired to the existing
  `RestartWithEniAsync` flow (which restarts the session with the ENI).

View-model reshaping: `TopologyViewModel` and `PvWatchViewModel` become

- `ExplorerViewModel` — owns the node collection + refresh (absorbs
  `TopologyViewModel.Refresh`'s snapshot logic and `MailboxProtocolsByAddress`).
- `DeviceEditorViewModel` — per selected slave; General-tab content is the existing
  `SlaveDetailViewModel.Build` output; Variables tab filters a shared variables store.
- `VariableWatchViewModel` — one shared store over `Session.Observer`'s decoded
  `ProcessImage` values, partitioned by `ProcessVariableAssignment` (§7); serves
  both the per-device tab (by address) and the Process Image node (unmatched).

`IRefreshable` stays the tick contract for whichever of these is visible.

## 6. Messages panel (docked)

`EventsView` moves from a page to a bottom-docked panel (~180 px) under the editor,
always present while a session runs:

- Header bar (Panel2 bg): "Messages" title, category filter checkboxes, auto-scroll
  toggle, and a collapse chevron. Collapsed = header only.
- Because the panel renders continuously, `EventsViewModel.Refresh` changes from
  Clear+Rebuild to **append-only diffing**: locate the previously seen tail event
  (reference scan from the end of the new snapshot), append only events after it,
  trim the front to the 500-row cap. Tail not found (≥500 new events in one tick)
  → full rebuild fallback. Filter toggle → full rebuild (user-initiated, rare).
  The existing auto-scroll guard behavior is preserved.
- The main window ticks the events VM every tick (not only when "visible" — it
  always is, unless collapsed; collapsed skips the refresh).

## 7. SDK: variable→slave assignment

One new pure type in `OpenEC.Monitor.Eni` — no observer or capture changes:

```csharp
/// <summary>Partitions an ENI's process variables by owning slave, matched by
/// name prefix ("SlaveName." …), longest slave name winning. Pure and immutable.</summary>
public sealed record ProcessVariableAssignment(
    IReadOnlyDictionary<ushort, IReadOnlyList<EniVariable>> BySlave,   // key: PhysAddr
    IReadOnlyList<EniVariable> Unmatched)
{
    public static ProcessVariableAssignment Build(EniConfiguration eni);
}
```

Matching rule: `variable.Name` starts with `slave.Name + "."` (ordinal). When slave
names nest (one is a prefix of another), the longest matching name wins. Covers
TwinCAT-exported names like `SubDevice_1014 [EL3162].Channel 1.Value`. Variables
matching nothing land in `Unmatched`. Both inputs and outputs are assigned; the
per-device tab may show an IN/OUT marker from `EniVariable.IsInput`.

This is a heuristic on ENI naming — acceptable because unmatched variables remain
reachable via the Process Image node, so nothing is ever hidden by a failed match.

## 8. Start screen & status bar

- **StartView** — re-skinned: a `Panel` card centered on the `Bg` background, accent
  primary button for Start, house-styled source pickers and the record-to-file row.
  Behavior unchanged.
- **Top bar** — chrome: app title, `SourceDescription`, Stop-session button
  (chrome-outline style). Only shown with a session; Start screen fills the window.
- **Status bar** — chrome: a session-state dot (same `StatusDot` mapping as the
  network root) + the existing `StatusText` string unchanged (including the
  `rec → file.pcap` suffix).

## 9. Testing

Headless VM tests as in M2 (no rendering assertions beyond what exists today):

- **SDK:** `ProcessVariableAssignment` — prefix match, longest-name-wins nesting,
  unmatched fallback, empty ENI, duplicate slave names.
- **Events diffing:** append preserves existing row instances; front-trim at cap;
  tail-not-found triggers rebuild; filter toggle rebuilds; collapsed panel skips work.
- **Explorer:** nodes update in place across snapshots (instance identity), address
  ordering on insert, dot mapping per AL state/error flag, Process Image node
  visibility rules (no ENI / unmatched / fully matched).
- **Editor:** per-device variable filtering and prefix stripping; General tab facts
  match `SlaveDetailViewModel.Build`; Load-ENI CTA path unchanged.
- **Shell:** migration of existing section-nav tests to tree selection (selection →
  CurrentPage mapping, stop-session teardown, fault banner unaffected).

Existing M2 tests that assert `InspectorSection` behavior are rewritten, not deleted
— every navigation guarantee they encoded must survive under tree selection.

## 10. Milestone bookkeeping

Pulled from the M3 seed list into this work: events append-only diffing, AL badge
colors. Explicitly left in M3: charts, binding-log smoke assertion, SDK
`TrafficStatistics` snapshot accessor, `BusObserver` event-log cap policy, datagram
totals / mailbox badges beyond error traffic.
