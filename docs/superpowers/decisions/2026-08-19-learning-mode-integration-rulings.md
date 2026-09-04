# SDD ledger — plan: docs/superpowers/plans/2026-08-18-learning-mode-integration.md

Spec: docs/superpowers/specs/2026-08-18-learning-mode-design.md (read, reachable; §5/§7/§9a are this plan's scope)
Branch: feat/learning-mode-integration (created from main @ a3295ac)
Baseline: `dotnet test` — 294 passing (211 Monitor + 83 Inspector), 0 failures.
Owner WIP preserved and OFF LIMITS: src/OpenEC.Inspector/ViewModels/ExplorerViewModel.cs,
  tests/OpenEC.Inspector.Tests/Ui/ShellSmokeTests.cs

## Pre-flight conflict scan

### Shared files / producer-consumer pairs

| Tasks | Produces → Consumes | Finding |
| --- | --- | --- |
| 1 → 2 | `ProcessImage.Rebind`, `WkcTracker.Rebind` → `ApplyConfiguration` | Clean. |
| 2 → 3, 5, 6, 9 | `BusObserver.ApplyConfiguration`, `.Applied` | Clean. |
| 3, 4, 5, 6 → same file | **`EtherCatMonitor.cs` is edited by four tasks in sequence**: T3 restructures `RunAsync` and adds the learner handler; T4 REPLACES that handler with `OnConfigurationLearned`; T5 inserts the discovery pass into `RunAsync`; T6 extends T4's handler with the cache consult. | **Ordering is load-bearing** — each edit assumes the previous one landed. Out of order the anchors won't match. Verified `_options` is already a field on the class, which T4/T6 both rely on. Flag in each dispatch. |
| 3 → 7 | T3 adds `BusLearner._gate` and locks `Observe`/`ResolveSchemasAsync`; T7's `ApplyAdsIdentity` locks the same gate | **T7 depends on T3's lock existing.** Flag in T7's dispatch. |
| 3, 6 → same file | Both modify `EtherCatMonitorOptions.cs` (T3 adds `Learning`, T6 adds `LearnedCache`) | Clean, sequential. |
| 4 → 8, 10 | `MonitorEvent.ConfigMismatch`, `MonitorEvent.ConfigurationLearned` → `AnalysisReport`, `EventFormatter` | Clean. |
| 6 → 3 | `LearnedBusCache` type → the option T6 adds | Clean; T6 owns both ends. |
| 7 → Ads module | `AdsBusSnapshot.ScannedIdentities()` mapping `EtherCatScannedSlave` | Verified against the package's own XML docs: `PhysicalAddress`, `VendorId`, `ProductCode`, `RevisionNumber` all exist. |
| 9 → existing | `MainWindowViewModel._filePicker` + `[RelayCommand]`; `IFilePicker.PickSaveFileAsync`; `FakeFilePicker(saveResult:)` | All verified present before planning. No new dialog plumbing. |
| 9 → owner WIP | `ExplorerViewModel.cs`, `ShellSmokeTests.cs` | Excluded by Global Constraints; the completeness strip is routed to `DeviceEditorViewModel` instead. |

### Per-task internal agreement

| Task | Tests vs. the code it specifies | Finding |
| --- | --- | --- |
| 1 | `internal ProcessImage(EniConfiguration?)` / `internal UpdateInputs` vs. tests in `OpenEC.Monitor.Tests` | Agrees — `InternalsVisibleTo` is set for that assembly. |
| 2 | `new BusObserver()` vs. `BusObserver(EniConfiguration? eni = null)` | Agrees. |
| 3 | `LiveShapedSource : ICaptureSource` vs. the interface T5 later extends | Agrees — T5's addition is a default interface member, so an existing implementor still compiles. |
| 3 | `With_an_eni_the_learner_runs_but_does_not_rebind` asserting `Applied` is null | Agrees at T3 (nothing subscribes with an ENI) and still holds after T4 (the handler returns early). |
| 4 | Replacing T3's wiring | Agrees — T3's ENI test survives the replacement. |
| 5 | `Two_pass_does_not_double_count_frames` vs. `Statistics.TotalFrames` | Agrees — pass 1 never calls `Observer.Process`, so nothing double-counts. |
| 6 | **`A_mid_run_attach_applies_a_cached_configuration`** | **DEFECT — see Ruling 1.** |
| 7 | Anonymous-slave discovery via a returning `FPRD` to 0x0130 | Agrees — `LearnedBus`'s mid-run fallback requires `Fprd` + non-zero WKC + non-zero ADP, all satisfied. |
| 8 | `TestApp` harness, `JsonDocument` assertions | Agrees. |
| 9 | `VariableWatchViewModel.ForSlave(session, Func<Task>, EniSlave?, IReadOnlyList<EniVariable>)` | Agrees — matches the call in `MainWindowViewModel.GetOrCreateEditor`. |
| 9 | **`Cancelling_the_save_dialog_writes_nothing`** | **Weak — see Ruling 2.** |
| 10 | `EventFormatter` switch cases | Agrees. |

## Rulings

**Ruling 1 (pre-flight, Task 6): the mid-run cache test cannot work as written. Build the capture
explicitly instead of slicing the bringup tail.**
Why: the test takes `BringupCapture.Frames(cycles: 20).TakeLast(20)` as a "cyclic-only" capture. But
that tail contains only `LRD` and a broadcast `BRD` to 0x0130 — no `FPRD`. `LearnedBus`'s mid-run
discovery fallback requires an `FPRD` with a non-zero ADP, so **no slave is ever discovered**,
`Republish` never fires (it needs `Slaves.Count > 0`), `Learned` stays null, and the test NREs on
`monitor.Learned!` before it can reach the cache at all.
Fix: construct the mid-run capture in the test — the cyclic frames plus one returning `FPRD` AL-status
poll per station (1001, 1002), which is what a real master actually emits in OP and is exactly what
makes mid-run discovery possible. Leaves the shared `BringupCapture` fixture untouched, which six
test classes depend on.
Cost if wrong: if the fallback fingerprint still misses, the test fails loudly on the first run and
tells us the fingerprint inputs are wrong — which is itself the finding.

**Ruling 2 (pre-flight, Task 9): strengthen the cancelled-save assertion.**
Why: `Assert.Empty(Directory.GetFiles(_directory))` passes trivially — nothing in that test ever
writes to `_directory`, so it would also pass if the command wrote somewhere else entirely. It
asserts nothing about the code under test.
Fix: assert `vm.FaultMessage` is null instead — a cancelled dialog must be a silent no-op, not an
error surfaced to the user. That is a real property of the command.
Cost if wrong: a slightly weaker guarantee than a filesystem check, but a true one rather than a
vacuous one.

## Progress

Task 1: implemented (commit 25d351c, 4 new tests, 298 total) — review dispatched (sonnet).
  Implementer reported one deviation: it corrected the brief's test assertions to compile
  (dictionary access / boxed-value comparison). Flagged to the reviewer for per-assertion
  equivalent/stronger/weaker assessment — that is exactly where a test gets quietly weakened
  while still compiling.
Task 1: complete (commits 3c0d7e4..25d351c, review clean). 298 tests.
  Deviation assessed EQUIVALENT and in one case a genuine fix: my brief's
  `Assert.Equal(0x42, value.Value)` compares a boxed int against a boxed byte and would have FAILED
  at runtime against a CORRECT implementation. The implementer's `(byte)value.Value` unboxes first.
  My test-code defect, caught by the implementer and confirmed by the reviewer.
Task 1: minor (deferred): one of the two assertion changes (Assert.Contains -> ContainsKey) was a
  style choice, not compiler-forced; harmless, noted so future readers don't assume otherwise.

**Ruling 3 (Task 2, DONE_WITH_CONCERNS — verified myself): the implementer is right; its fix stands.**
My brief's `Applying_a_configuration_preserves_statistics_and_the_event_log` asserted `events > 0`
after pumping `BringupCapture` through a bare `BusObserver`. Verified directly: BringupCapture never
writes AL Control 0x0120 (grep: 0 occurrences — the only `StateChangeRequested` trigger), and its only
0x0130 traffic is a broadcast `BRD`, which `SlaveStateTracker` handles by setting `BusState` and
`yield break`ing without raising anything. Its CoE frames are SDO downloads, not emergencies. So the
fixture genuinely produces ZERO MonitorEvents and my assertion would have failed against correct code.
The implementer added one hand-crafted returning `FPRD` AL-status frame so a real `SlaveStateChanged`
exists, leaving the assertion itself intact — which preserves what the test is for (the event log
survives a rebind) rather than weakening it. Correct call.
Cost if wrong: none; the added frame is realistic traffic a master actually emits.

PROCESS PATTERN (mine, two tasks running): both brief defects so far are me asserting a property of
BringupCapture's output — a decoded value's boxed type, then the presence of events — without
checking it. The countermeasure for the remaining tasks: any assertion about what the fixture
PRODUCES gets verified against the fixture before it ships, not after.
Task 2: complete (commits 25d351c..2887bea, review clean). 303 tests.
  Deviation assessed PRESERVES INTENT: the reviewer re-derived from SlaveStateTracker and
  EtherCatFrameBuilder that the added FPRD frame fires SlaveStateChanged deterministically (fresh
  SlaveStatus defaults AlState 0, decoded 2 differs), so the baseline is events==1 and the assertion
  still fails if ApplyConfiguration wipes the log. The fix repaired the stimulus, not the check.
  Named risk resolved: Bus.Seed on a populated model is ADDITIVE — it only sets ConfiguredName/
  identity via GetOrAdd and never removes entries; runtime state (AlState, ErrorFlag, AlStatusCode,
  LastSeen) is untouched and no slave is deleted.
Task 2: minor (deferred): the concurrency test has no start barrier, so true overlap is
  scheduler-dependent (highly likely at its iteration volume, not guaranteed). Plan-mandated.
Task 2: minor (deferred): Assert.NotNull on the Snapshot* accessors is a low bar — they never return
  null; their real function is to surface an unhandled exception from a locked call. Plan-mandated.

**Ruling 4 (Task 3 deviation, applied pre-emptively to Tasks 4 and 7): partial namespace qualifiers
cannot be used in this test assembly.**
The implementer found my Task 3 test used `Eni.EniConfiguration.Load(...)`, which fails to compile:
`OpenEC.Monitor.Tests` contains a sibling namespace `Eni` that shadows `OpenEC.Monitor.Eni` from
inside the test assembly. Verified — SEVEN such shadowing namespaces exist: Eni, Observation,
Synthesis, Learning, Capture, Cli, Protocol.
Its fix (add the using, drop the qualifier) matches the convention already in EtherCatMonitorTests.cs.
Acting pre-emptively rather than waiting for the failures: my Task 4 test used `Eni.*` AND
`Observation.*`, and my Task 7 test used `Synthesis.*` — three more compile failures queued up. All
fixed in the plan, plus a standing Global Constraint so no later task repeats it.
Cost if wrong: none; it is the convention the assembly already follows.

Task 3: implemented (commit 0ebb731, 3 new tests, 306 total) — review dispatched (sonnet) with the
  lock/await boundary and the resolved.Count==0 churn guard as named checks.

**Ruling 5 (Task 3, two plan-mandated Importants): fix both — the reviewer is right, and both sit in
the exact mechanism this task exists to make safe.**

Finding A — shallow snapshot race. `ResolveSchemasAsync` snapshots the LIST under the gate but its
elements are the same mutable `LearnedSlave` objects living in `_bus`. `VendorId`/`ProductCode`/
`Revision` are plain `{ get; set; }` on that type, and the resolver reads them AFTER releasing the
lock while `Observe` can be writing them from the pump thread. `Nullable<uint>` is a bool+uint struct
and is not written atomically, so a concurrent write can be observed torn — hasValue true with a
stale value — producing a wrong ESI lookup and a wrong device name. `??=` means hasValue only goes
false→true, so `!.Value` is safe from an NRE; the value itself is not safe.
Fix: snapshot the VALUES inside the lock, not the object references — `pending` becomes a list of
`(ushort Address, uint VendorId, uint ProductCode, uint Revision)` built by a `.Select` inside the
`lock`. That closes the race entirely rather than narrowing it.

Finding B — the final resolution pass can overlap the periodic one. `RunAsync` awaits the final
`ResolveSchemasAsync` BEFORE `linked.Cancel()`, so the background resolver may be mid-call at that
moment. Both snapshot the same pending slaves, both resolve them, and both reach
`Republish(force: true)` — two revision bumps for identical content. Same churn defect class the
`resolved.Count == 0` guard exists to prevent, reached by a different path.
Fix: cancel and await the resolver BEFORE the final pass, so the two are serialised.

Also folding in the Minor: a genuine (non-cancellation) resolver fault could propagate out of the
`finally` before `_events.Writer.TryComplete()` runs. Moving `TryComplete()` ahead of the resolver
await removes it at zero cost.

Cost if wrong: Finding A's fix is strictly narrower reads; Finding B's fix only reorders shutdown.
Neither changes what is resolved, only when and from what.

**Ruling 6 (Task 3 coverage gap → deferred to Task 4): the churn assertion needs an event that does
not exist yet.**
The reviewer notes none of Task 3's tests set `EsiDirectory`, so the periodic timer, the enricher
integration and both concurrency paths are untested. The fix adds an end-to-end ESI test (names must
reach the configuration, which only happens if the resolver ran). But the assertion that would
actually pin CHURN — no two published revisions carrying identical content — needs
`MonitorEvent.ConfigurationLearned`, which Task 4 introduces. Carrying it to Task 4's dispatch
rather than inventing a weaker proxy here.
Cost if wrong: the overlap path stays unpinned by a test until Task 4 lands.
Task 3: fix round 1/5 (2 findings + coverage addressed, 0 open; commits 0ebb731..ed33a13)
Task 3: complete (commits 2887bea..ed33a13, review clean). 307 tests.
  Re-review confirmed: value snapshot built inside the lock with no residual LearnedSlave read
  escaping it; shutdown genuinely serialised (Cancel + await precede the final pass, not just the
  helper existing); StopResolverAsync catches ONLY OperationCanceledException; and the new ESI test
  verified non-vacuous by tracing BringupCapture's identity -> the EL1008 fixture -> DisplayName.

**Ruling 7 (Task 3 Minor, carried into Task 5 rather than opening a round): fix the finally ordering
in Task 5, which edits RunAsync anyway.**
Moving `TryComplete()` to the top of `finally` (my own fix instruction) reopened a narrow
exception-path window: if the capture loop throws before the success-path Cancel, the resolver can be
mid-`Republish` when the channel completes, and that event is silently dropped by `TryWrite`.
There is a strictly better ordering that satisfies both constraints — stop the resolver first, and
guarantee TryComplete with a nested finally:
    finally { linked.Cancel(); try { await StopResolverAsync(resolver); }
              finally { _events.Writer.TryComplete(); } }
Task 5 inserts the discovery pass into this same method, so folding it there costs nothing, whereas
opening a fix round now costs two dispatches for a Minor.
Cost if wrong: on an exception path only, one ConfigurationLearned event may be dropped from a
channel that already has lossy DropOldest semantics.

**Ruling 8 (retiring Ruling 6): the churn assertion is not worth a brittle test. The structural fix
is the guarantee.**
Ruling 6 carried forward an assertion that no two published revisions carry identical content, to be
written once `MonitorEvent.ConfigurationLearned` existed. Reconsidered: `Republish` increments the
revision on every publish, so two publishes of identical content produce two DIFFERENT revision
numbers — a duplicate-revision assertion would catch nothing. The only alternatives are count-based
("at most N events"), which is brittle against any legitimate change in how many times learning
refines during startup.
The overlap path is now structurally impossible: Task 3's fix serialises the periodic resolver and
the final pass, so they cannot both reach `Republish(force: true)`. That serialisation is the
guarantee, and a brittle count assertion would be worse than none.
Cost if wrong: the overlap path has no regression test, so a future edit reintroducing the
interleaving would not be caught. Surfacing to the final review to triage.

**Ruling 9 (Task 4, red suite — LOAD-BEARING): pull Task 10's formatter work forward and add the
missing category toggles. My plan gap, and Task 10 as written would NOT have fixed it.**
The implementer's diagnosis is correct and I verified it directly. `EventsViewModel.AppendIfEnabled`
does `Categories.FirstOrDefault(c => c.Name == category)?.IsEnabled ?? true`, so any category with no
toggle is permanently visible. `CategoryNames` is
`["State", "State request", "WKC", "Emergency", "SoE"]`. Task 4 correctly makes the learner raise
ConfigMismatch/ConfigurationLearned, which fall into "Other" — no toggle — so
`EventsViewModelTests.Disabling_categories_filters_the_rows` sees extra always-visible rows and its
`Assert.Single` fails.
Why Task 10 would not have fixed it: Task 10 only teaches `EventFormatter` the new categories
("Config", "Learning"). Those still have no entry in `CategoryNames`, so they would still be
permanently visible and the test would still fail — six tasks later, with a red suite the whole way.
Ruling: pull Task 10's `EventFormatter` change forward into Task 4's fix round, and add "Config",
"Learning" and "Other" to `CategoryNames` so every category the formatter can emit is filterable.
Adding "Other" is a small improvement beyond the strict fix: it makes the filter robust against any
future event type rather than leaving one permanently-visible hole.
I am NOT weakening the failing test — it is a correct test that caught a real gap.
Never proceed past a red suite: it makes every later task's verification ambiguous.
Cost if wrong: Task 10 shrinks to its README work, and "Other" becomes filterable (default on, so
no display change).

Task 4: minor (deferred): a fourth brief defect of the same family — `Assert.Equal(1001, ushort?)`
  hits an xunit overload trap. Implementer's `(ushort?)1001` cast is correct and equivalent.
Task 4: fix round 1/5 (red suite closed; commits 6de2415..9000d1d). Suite green at 320/320,
  matching predicted arithmetic exactly; `Disabling_categories_filters_the_rows` passes UNMODIFIED.
  Task review dispatched over BOTH commits (ed33a13..9000d1d) — the review had not run yet, because
  a red suite makes review meaningless, so per the DONE_WITH_CONCERNS rule the correctness concern
  was addressed first.
  Review framed on the finding that decides whether this feature is trustworthy: TxPdoState must be
  reported as a mismatch while WcState must not, plus whether the leaf-name match is too broad.
Task 4: complete (commits ed33a13..9000d1d, review clean). 320 tests.
  Exclusion boundary verified correct in BOTH directions: WcState/InputToggle/InfoData excluded,
  TxPdoState reported — with a test that fails if that boundary is ever crossed. ENI-authority path
  confirmed structurally incapable of reaching ApplyConfiguration. Protected test untouched.
Task 4: minor (deferred): WcState/InputToggle are matched by LEAF NAME while InfoData is matched by
  PATH SEGMENT. A device genuinely exposing a real PDO entry whose leaf is literally "WcState" would
  be silently excluded — same risk class the brief worries about for TxPdoState, far lower
  probability, and the doc comment justifies InfoData's exactness without noting the asymmetry.
Task 4: minor (deferred): no test asserts DcInputShift/DcOutputShift are NOT excluded (only
  TxPdoState is). Logically guaranteed — the predicate never names them — but untested.
Task 4: minor (deferred): the unexpected-slave scan is O(n*m); harmless at real bus sizes.

**Ruling 10 (Task 5, concern 1): the cast is correct and equivalent. Fifth brief defect, same family.**
Default interface members cannot be accessed through a concrete type in C# — a real language
restriction the implementer verified with a standalone repro. My brief's test read
`new LiveCaptureSource(...).SupportsMultiplePasses` directly. Casting to `ICaptureSource` in the test
is the only way to express the assertion and changes nothing about what it proves.

**Ruling 11 (Task 5, concern 2 — LOAD-BEARING): replace the two-pass test, which pins nothing.**
The implementer reverted the discovery pass and re-ran the tests in isolation. Two of three pass
either way. Root cause, which I should have seen when writing the plan: `BringupCapture` emits ALL
configuration traffic (INIT->SAFEOP) before any cyclic frame, so single-pass learning converges
before the first LRD arrives and maps all 16 variables anyway. On that fixture the discovery pass
makes no observable difference, so `Offline_two_pass_maps_process_data_from_the_first_frame` proves
nothing about two-pass.
Worth noting this also means the FEATURE's value is narrower than spec §5 implies: a real master
configures FMMUs and PDOs before process data starts, so single-pass already maps everything in the
normal case. What two-pass actually buys is ordering-INDEPENDENCE — correctness that does not rely on
an assumption about master behaviour, which matters for a runtime PDO remap, a merged capture, or a
TAP that dropped early frames. That is worth keeping; the overstated test is not.
Fix: a test that reorders the fixture so cyclic frames precede the configuration explaining them, and
compares a live-shaped single-pass run against a file two-pass run over the same frames. Single-pass
must map strictly fewer — 0 variables, since no cyclic frames follow the config — and two-pass all 16.
That pins the actual guarantee.
Cost if wrong: if the reordered capture behaves unexpectedly the test fails loudly on the first run
and tells us something real about the pass logic.
Task 5: fix round 1/5 (2 concerns resolved; commits 60d181b..cfc1535). 323 tests.
Task 5: complete (commits 9000d1d..cfc1535, review clean).
  Replaced test assessed STRONGER: reviewer derived BringupCapture's composition by hand (100 config
  frames + 20 cyclic = 120), confirming TakeLast(20) selects exactly the cyclic tail with no overlap,
  and that PcapStreamWriter preserves enumeration order so the non-monotonic reordering survives the
  round trip. Assert.Empty on the single-pass side is a forced consequence, not an accident.
  ICaptureSource casts assessed EQUIVALENT (CS1061 is real — default interface members are not
  projected onto the implementing type).
Task 5: minor (deferred): the new test's `cyclicCount = 20` is hand-computed rather than derived from
  `cycles`, so a future BringupCapture reshape could silently un-clean the split.
Task 5: minor (deferred): in the two-pass path all ConfigurationLearned events fire as a front-loaded
  burst inside the discovery loop rather than interleaved with decode progress. Byproduct of reusing
  the existing wiring; final state is unaffected.
Task 5: minor (deferred): ReplaySource's trailing `await Task.CompletedTask;` is dead weight.

**Ruling 12 (Task 6, CRITICAL design defect in my plan — three-part fix): the cache never hits on a
mid-run attach, and if it did, the next revision would throw the hit away.**
The implementer traced this empirically with temporary instrumentation. Two compounding defects, both
mine:

(A) The latch is on the wrong event. `_cacheConsulted` is set on the first consult ATTEMPT.
`Republish` fires once per frame, and on a mid-run attach slaves are discovered one at a time, so the
first published revision knows ONE slave. Its fingerprint cannot match the saved two-slave bus, the
lookup misses, the latch burns its single shot, and the complete picture arriving a frame later is
never looked up. The cache therefore cannot hit except in the degenerate case where the very first
revision already matches the saved bus exactly — which a mid-run attach essentially never produces.
The feature is broken in precisely the scenario it exists for.
Fix: latch on a HIT, not on an attempt. Every revision retries while still incomplete and nothing
cached has been applied. Bounded — revisions stop once the bus picture stabilises, and each retry is
a `File.Exists` probe.

(B) Even a successful hit gets clobbered. After applying a cached configuration, the handler falls
through to `Observer.ApplyConfiguration(learned)` on every later revision — and on a mid-run attach
the learner's own picture is strictly WORSE than the cache (no SyncManagers, no FMMUs, so zero
variables). My spec said "the learner keeps running and refines or overrides it", which is simply
wrong in this direction.
Fix: once a cached configuration is applied, do not replace it with a less-complete learned one.
Re-apply only when the learner's own configuration becomes complete — i.e. it actually observed a
startup, at which point the wire genuinely is the better source.

(C) My test could not detect either defect: it asserted `SawStartup` is false and `Applied` is
non-null, both true with or without a hit. Strengthened to assert the cached configuration's 16
variables are in force and that the cyclic frames actually decode through them — which is the
user-visible payoff and fails without a hit.
Cost if wrong: if retrying per revision proves noisy on a large bus, the retry can be bounded by a
frame count; the alternative (today's behaviour) is a feature that never works.

Task 6: minor (deferred): sixth brief defect of the family — my test's `_directory` field never
  created the directory, so two wiring tests threw DirectoryNotFoundException. Implementer applied
  the `Directory.CreateDirectory(...).FullName` pattern already used by sibling test files.

**Ruling 13 (Task 6 fix round 2, two further defects — verified myself): the fallback fingerprint was
never wired end-to-end, and the discovery pass bypasses the cache policy.**
The implementer applied A and B correctly, the tests still failed 16→0, and it reported BLOCKED
rather than patching further. Both remaining defects confirmed by direct inspection:

(C) `LearnedBusCache.Save` writes only `{Fingerprint}.eni.xml` + `.meta.json`. Nothing ever writes a
fallback-keyed entry, so `TryLoad(FallbackFingerprint(...))` reads a file that cannot exist — the
fallback path is structurally dead for any bus in any session. And it is the ONLY key a mid-run attach
can compute: such a capture never observes identity, so its primary fingerprint is derived from
zeroes and can never match a saved bus. So the two defects interlock — the one key that case can
produce is the one key Save never wrote.
Fix: `Save` indexes under both keys (extracted into a `WriteEntry(learned, key)` helper), skipping the
second write when they coincide. Documented caveat: two different buses sharing slave count, station
addresses and cyclic shape collide on the fallback key, last write wins. Inherent to a weaker
fingerprint, and spec §5 already says a fallback hit is not guaranteed.

(D) `EtherCatMonitor.RunAsync`'s discovery-pass tail (line 131) does
`if (_learner.Current is { } learned) Observer.ApplyConfiguration(learned);` — bypassing
`OnConfigurationLearned`, and therefore `_cacheApplied`, so it stomps a genuine cache hit back to the
raw learned configuration. It is also redundant: `Republish` fires the event whenever it sets
`Current`, including the forced republish after schema resolution, so the handler has already applied
everything the pass produced — and unlike this line, it knows whether a cached configuration is in
force and must not be overwritten.
Fix: delete the line, with a comment saying why it must not come back.

Cost if wrong: (C) duplicates a few KB per cached bus; (D) if some revision could set `Current`
without firing the event, the observer would miss it — inspected `Republish` and no such path exists.
Task 6: fix round 1/5 (A+B applied, tests still red — implementer reported BLOCKED with a trace)
Task 6: fix round 2/5 (C+D found and all four fixed; commits 8c0a763..8ebb084). 334 tests.
  Four interlocking defects in one feature, all mine, none detectable by the tests I wrote for it.
  Separate fail-then-pass evidence per defect, including a deliberate regression check proving D was
  load-bearing rather than merely redundant. Review dispatched over both commits.

**Ruling 14 (Task 6, Important — fix it): Defect B's guard is untested, and that is where I should
spend a round.**
The reviewer traced the mid-run capture's data flow: only TWO revisions fire for the whole RunAsync,
with the cache hit at the last one, so there is never a revision AFTER the hit for the overwrite guard
to intercept. Both mid-run tests pass on fixes A+C+D alone — the implementer's own log shows applying
B produced no observable change — and `A_cached_configuration_is_not_overwritten_by_a_weaker_one`,
despite its name, cannot distinguish the guard's presence from its absence.
Why this is worth a round rather than a deferral: B protects the feature's core value (a cache hit
must not be replaced by the capture's own weaker picture), it sits in a feature that just produced
four defects, and an untested guard is exactly what a future refactor deletes silently. Three of the
four fixes have real fail-then-pass evidence; this one has none.
Fix: give `MidRunFrames` a `withLateSlave` option appending an FPRD poll for a third station after
the two known ones. That produces a post-hit revision whose fingerprint differs while completeness
stays false — precisely the branch B guards. Assert the applied configuration still carries the
cached 16 variables; without the guard it drops to the learner's 3-slave, 0-variable picture.
Cost if wrong: one extra round on a task already at two.

Task 6: ⚠️ resolved — reviewer noted the "fingerprint excludes serial numbers" constraint is vacuous
  because `EniSlave` carries no serial field at all. Correct, and intentional: the ENI model never
  had one, so there is nothing to leak. The spec's actual intent — an identical replacement terminal
  still hits the cache — holds trivially.
Task 6: minor (deferred): `Saving_indexes_under_both_fingerprints` asserts TryLoad for the fallback
  key but not that the fallback key's .meta.json exists (WriteEntry writes both together).
Task 6: minor (deferred): `Caching_is_off_when_no_cache_is_supplied`'s `Directory.Exists` clause is
  now redundant since the field initializer pre-creates the directory.

Task 6: complete (8c0a763, 8ebb084, 0eb9ce0 — 334 tests, 0 warnings)
  Review approved with one Important finding; fix round 3 closed it. Evidence: with the guard at
  `EtherCatMonitor.cs:78` commented out, the strengthened test fails `Expected: 16, Actual: 0` —
  the cached configuration is replaced by the learner's variable-less picture. Guard restored
  byte-for-byte; verified myself that the round's diff touches only the test file and `git diff`
  against the task base shows no `src/` change.
  Scoped re-review skipped deliberately: the round's whole risk surface was "was the temporary
  diagnostic reverted", which is a two-command check I ran directly rather than paying a subagent for.

**Ruling 15 (Task 7, pre-flight — BLOCKING had it shipped): my brief's `ScannedIdentities()` both
fails to compile and, repaired the obvious way, would make provenance lie.**
The brief maps `ScannedSlaves.Select(s => (s.PhysicalAddress, s.VendorId, s.ProductCode,
s.RevisionNumber))` into `IReadOnlyList<(ushort, uint, uint, uint)>`. Checked the package XML docs for
Dahlke.EtherCAT.Diagnostics 0.10.0: all four identity fields are `uint?` and, per the type's own doc,
"null together, never individually" when that slave's per-slave identity read (IG 0x11) did not answer
— reported "absent rather than zeroed" specifically so a caller cannot confuse the two. So the mapping
is a CS0266.
The compile error is not the danger. The repair an implementer reaches for is `s.VendorId!.Value`,
which turns a failed read into vendor 0 / product 0, hands it to `ApplyAdsIdentity`, and gets it
stamped `FactSource.Ads` — the tool would then report a confident master-side identity for a slave
nobody ever identified. Provenance honesty is the property the whole spec is built on; this would
have put a fabricated fact behind it.
Ruling: `ScannedIdentities()` drops entries whose identity did not answer rather than zeroing them,
and Task 7 gains a test pinning that. `PhysicalAddress` is `ushort` and always present (confirmed via
`GetSlaveDetailAsync(string, System.UInt16, …)`), so the address is never the thing in doubt.
Cost if wrong: none — dropping an unanswered identity leaves the slave exactly as the wire left it,
which is what the tier is for.

**Ruling 16 (standing, extends the namespace constraint): `Ads` joins the shadowed-sibling list.**
`OpenEC.Monitor.Tests.Ads` exists (`tests/OpenEC.Monitor.Tests/Ads/AdsEnrichmentTests.cs`) and shadows
`OpenEC.Monitor.Ads` from inside the test assembly. Task 7 is the first task to write test code
against the Ads project, so it is the first that can trip on it. The full shadowed set is now
Eni, Observation, Synthesis, Learning, Capture, Cli, Protocol and Ads.

Task 7: implemented (1d56f2b — 339 tests, 0 warnings). Review dispatched.
  Implementer discovered `EtherCatScannedSlave` is not a positional record: five `required` init-only
  members (`PhysicalAddress` plus the four `uint?` identity fields), so the all-null unanswered case
  IS representable and Ruling 15's test needed no compromise. Verified myself that
  `src/OpenEC.Monitor/OpenEC.Monitor.csproj` still has no `Dahlke.EtherCAT.Diagnostics` reference.
  Deviation flagged by the implementer and referred to the reviewer, not yet ruled on: my
  `LearnerWithAnonymousSlave()` helper observed one `AsReturning()` frame, which does NOT register the
  station. Confirmed against `DirectionTracker.cs`: with one frame seen, `_sawBitClear` is false so the
  MAC-bit branch cannot fire, and the pairing fallback classifies a first sighting as `Outbound`.
  Their repair observes the same frame twice. It works, but models traffic no TAP can produce and
  leans on the fallback heuristic rather than the primary one. Asked the reviewer whether that couples
  four ADS tests to a heuristic they do not care about.

**Ruling 18 (Task 8, pre-flight): omit `learning` with a scoped attribute, not the global switch.**
My brief had `DefaultIgnoreCondition = WhenWritingNull` set on `AnalyzeCommand`'s `JsonOptions` so the
absent learning block disappears under `--no-learn`. Checked `AnalysisReport`: it also carries
`double? FramesPerSecond` and `double? CycleTimeMicroseconds`, so that switch would drop those two keys
from the published JSON whenever they are null. Grepped the suite — nothing asserts them, so the
change would have gone in green while silently altering the output contract of two fields this task
has no business touching. Replaced with `[property: JsonIgnore(Condition = WhenWritingNull)]` on the
one parameter, and told the implementer why not to reach for the global.
Cost if wrong: none; the attribute is strictly narrower than what it replaces.

**Ruling 19 (Task 8, pre-flight): a test of mine pinned nothing — same class as Ruling 11.**
`Live_learn_out_writes_a_loadable_eni` asserted `ExitCode != 0` and that no file appeared, with a
comment claiming this "proves the flag is wired." It does not: Spectre returns non-zero for an unknown
option too (`LiveCommandTests:10` relies on exactly that for a missing required option), so the test
passes identically whether `--learn-out` exists or is a typo. Its name also promised a loadable ENI it
never checks. Split into two honest tests: one asserting `live --help` lists `--learn-out` (I ran the
built CLI — help prints an OPTIONS block and exits 0, so this is real evidence of registration), and
one named for what it checks, that a capture which never starts writes no file.

Countermeasure holding: Task 8's pre-flight verified the two fixture claims that would otherwise have
been assumptions — that `sample.eni.xml` genuinely disagrees with `BringupCapture` (it declares four
slaves and a different product code for 1001), and that a 5-cycle bringup yields both slaves complete
(already pinned by `LearnCommandTests` asserting "2/2"). Both held, so no brief change was needed —
which is the point: checking is cheap whether or not it finds something.

**Ruling 17 (Task 7, review finding — change the fixture).** The reviewer independently reached the
same conclusion I had and sharpened it: `AdsIdentityTests` is the only place in the repo whose station
registration rides entirely on `DirectionTracker`'s fallback pairing branch, because nothing else in
that capture ever sets `_sawBitClear`. Elsewhere — `BringupCapture`, and `MidRunFrames` via its cyclic
LRD pair — both MAC-bit values are seen before any physical read, so the primary heuristic is already
in play. It also noticed the fixture gives its "outbound" observation WKC 1, which no real outbound
datagram carries. Ruling: replace with a real outbound(WKC 0)/returning(WKC 1) pair. Not aesthetics —
it decouples four ADS tests from a disambiguation heuristic they have no stake in.
Cost if wrong: none; if the pair fails to register the station the implementer reports BLOCKED rather
than reverting, so the failure is visible.

**Ruling 20 (Task 7, review finding — guard the republish, and keep `force`).** `ApplyAdsIdentity`
republished unconditionally, so a no-op poll bumped the revision and re-fired `ConfigurationLearned`.
The API is designed for a 1 Hz ADS poll (`LiveCommand.cs:118-119`), so every poll after the bus is
identified would churn every subscriber. Dormant today — nothing wires it yet — but a public API
built for polling should not carry it.
The reviewer offered two repairs: drop to plain `Republish()`, or add a changed-guard. I verified its
premise for the first — `Fingerprint` really does digest `{PhysAddr}:{VendorId}:{ProductCode}:
{RevisionNo}:{Name}`, so an identity fill does change it — and then found the hole it leaves:
provenance is NOT in the fingerprint. A slave whose ADS identity synthesises to the same `EniSlave`
values (`ToEniSlave` maps `VendorId ?? 0`, so an all-zero identity does it) would flip `IdentityFromAds`
and move `Provenance` from `Inferred` to `Ads` with the fingerprint unmoved, and a non-forced republish
would swallow that. Ruling: changed-guard with `force: true` retained inside it — publishes exactly
when something changed, including changes the fingerprint cannot see. Pinned by a new test asserting
the revision is unmoved across a repeat poll and an unknown-address poll.
Cost if wrong: an extra publish on a path that already published — harmless — versus the silent
provenance loss the alternative risks.

Task 7 fix round 1 dispatched (resumed the implementer).

Task 7: complete (1d56f2b, 5b33011 — 340 tests, 0 warnings)
  Fix round 1 closed all three findings. Evidence for the churn guard: the new no-churn test against
  the unguarded code failed `Expected: 2, Actual: 4` — the two no-op polls each bumped the revision,
  which is precisely the 1 Hz churn the guard removes. The realistic frame pair registered station
  1001 through the primary MAC-bit heuristic, so no BLOCKED case arose.
  Scoped re-review skipped deliberately: I read the whole 33-line diff myself and it matches Rulings
  17 and 20 verbatim, with no collateral change. Paying a reviewer to re-read what I had just read in
  full would buy nothing; the budget is better spent on the whole-branch final review.

Task 8: dispatched (base 5b33011). Brief pre-flighted — see Rulings 18 and 19.

**Ruling 21 (Task 9, pre-flight): a third test of mine asserted the wrong thing.**
`Variables_populate_with_no_eni_loaded` built a `VariableWatchViewModel` with an EMPTY variable list
(`ForSlave(session, …, slave: null, variables: [])`), called `Refresh()`, and then asserted on
`session.Observer.ProcessImage.Current` — so the watch it named itself after contributed nothing, and
the two lines constructing it were dead. It would have passed while proving nothing about the
Variables tab, which is spec §7's headline Inspector claim.
Rewrote it around the pattern this suite already uses (`DeviceEditorViewModelTests.DriveEditorAsync`):
build `ProcessVariableAssignment.Build(learnedConfiguration)`, take `BySlave[1001]`, hand it to
`ForSlave` with the learned slave, set `SelectedTabIndex = 1` — the Variables tab refreshes only while
selected, which the brief now says explicitly — and assert `editor.Variables.Rows` is non-empty. That
substitutes the learned configuration for the loaded ENI at the exact seam the ENI path uses, which is
the claim worth pinning.
Also corrected "PASS (4 tests)" to 5 — the brief listed five facts.
Cost if wrong: `BySlave[1001]` throwing would mean learned variable names do not match the learned
slave name (the Ruling 22 collision from Plan 1, resurfacing). The brief tells the implementer to
report BLOCKED with the actual keys rather than relax the assertion, so that failure stays visible.

One pre-flight suspicion of mine was WRONG and worth recording: I thought
`session.Observer.ProcessImage` would not compile, since `MonitorSession` exposes `ProcessImage`
directly. `BusObserver.ProcessImage` exists too (`BusObserver.cs:39`), so both spellings are fine. I
checked before "fixing" it — had I edited on suspicion I would have introduced churn for nothing.

Pending at finish: `docs/superpowers/plans/2026-08-18-learning-mode-integration.md` carries my
uncommitted execution amendments (the namespace Global Constraint, Ruling 9's scope move). Commit it
with the ledger at the end rather than mid-flight, to avoid racing an implementer's `git` calls.
Working tree also holds the repository owner's uncommitted `ExplorerViewModel.cs` and
`ShellSmokeTests.cs` — leave both alone throughout, including at finish.

Task 8: implemented (7e05cec — 345 tests, 0 warnings). Review dispatched.
  Verified myself before review: Ruling 18's scoped `[property: JsonIgnore(WhenWritingNull)]` is on
  `AnalysisReport` and no `DefaultIgnoreCondition` was added to `AnalyzeCommand`; Ruling 19's two
  split tests both exist by their honest names.
  Deviation referred to the reviewer: the implementer also changed
  `tests/OpenEC.Monitor.Tests/Cli/CliTestHarness.cs`, the harness EVERY CLI test runs through, adding
  `_app.Configure(c => c.ConfigureConsole(console))` per run. Their root cause — Spectre.Console.Cli's
  help/validation rendering goes through `Settings.Console`, which falls back to a process-wide `Lazy`
  that latches onto the first console forever — is consistent with what I observed earlier: running
  `live --help` against the built CLI printed fine as a first render. I checked the version detail they
  cited and it is precise: `Spectre.Console.Cli` is 0.55.0 while `Spectre.Console.Testing` is 0.57.2,
  so the harness's existing 0.57.2 comment refers to a different package.
  The risk I asked the reviewer to weigh is not the fix but its blast radius: routing Spectre's
  internal rendering into the captured stream can only ADD previously-lost text, and several existing
  tests parse `result.Output` as JSON. All 345 pass and the implementer re-ran four times, so if there
  is a hazard it is latent rather than active.

**Ruling 22 (Task 8, review finding + two I found under it — the ENI cross-check reports untruths).**
The reviewer found the mismatch list carries 86 entries for 9 distinct findings, and that
`AnalyzeCommand.Render()`'s `Take(20)` drops the Identity finding — the flagship "your ENI no longer
matches this machine" result — for the very fixture Task 8's own test uses. I reproduced it against
the built CLI: 86 total, 9 distinct, `Identity` absent from the first 20.
Verifying that turned up two further defects the reviewer did not reach:
  (B) Mismatches computed against a half-learned bus are never retracted. The distinct list asserts
      `SlaveMissing: Term 2 (EL1008) … not seen on the bus`, but Term 2 is station 1002 and the final
      learned configuration contains 1001 AND 1002 (confirmed: slavesTotal 2, provenance keys 1001,
      1002). The claim is simply false — an artefact of a revision that had only discovered one slave.
  (C) `ConfigurationDiff` keys its process-image lookup on variable NAME
      (`ConfigurationDiff.cs:67-68`). Learned names are synthesised `Slave {addr} ({esi})…`; an ENI
      carries the master's own labels `Term 2 (EL1008)…`. They can never match, so cross-checking any
      TwinCAT ENI against a learned bus reports EVERY declared variable as "not in the learned image".
      Two of the five in the fixture are present at exactly the declared offset.
Why the tests never caught C: `ConfigurationDiffTests` builds both sides from the same `Config()`
helper, so the same name assumption sits in the code and in the test. This is the second instance in
this project of the pattern I recorded during Plan 1 — a defect specified by the plan and then locked
in by a test transcribed from it, which no task-scoped review can see, because inside the task the
code matches its spec. The countermeasure that works is exactly what happened here: run the real
binary against real fixtures and read the output as a user would.
Ruling: fix all three now rather than deferring to the final review. Dedupe on
(Kind, Address, Declared, Observed); gate `SlaveMissing`/`ProcessImage` on `Completeness.IsComplete`
while leaving `Identity`/`SlaveUnexpected` ungated (they describe a slave already seen and are true on
sight); and compare process images by placement (BitOffs, BitSize, IsInput) with names kept for the
message. Derived the expected outcome by hand so the implementer has something to check against
rather than a target to fit: five declared variables should yield three true mismatches, not five
false ones.
Timing matters: Task 9 puts these events in front of a user in the Inspector's messages panel. Fixing
the projection alone would have left Task 9 building a UI on false data.
Cost if wrong: the placement comparison is the risky part — two variables with identical layout but
different meaning would now compare equal. That is the correct answer for process data, where layout
is the contract, and the brief tells the implementer to report rather than force a fix if the fixture
disagrees with my hand-derivation.

Task 8 fix round 1: complete (caf2bea — 348 tests, 0 warnings). All three defects confirmed real by
  the implementer reverting each individually; I re-verified against the built CLI rather than the
  report: 86 total/9 distinct → 6 total/6 distinct, `Term 2` no longer falsely reported missing,
  `Identity` present in both the JSON and the human-readable path. Matches my hand-derivation exactly.

**Ruling 23 (Task 8, found while verifying round 1): the CLI dumps raw C# records as events.**
`AnalysisReport.Describe` has no case for `ConfigMismatch` or `ConfigurationLearned`, so both fall to
`e.ToString()`. Measured on the built binary: with `--eni`, 6 of 11 events are raw record dumps;
without an ENI, 11 of 11 are. It hits the console output and the `events` array in `--json`, which is
a machine-readable surface people parse.
This is the exact counterpart of Ruling 9, which fixed the Inspector's `EventFormatter` for these same
two events — the CLI has its own describer and was missed. Worth recording as a pattern: this project
has two independent event formatters, so any new `MonitorEvent` needs both, and neither one's tests
notice the other's gap.
Dispatched as round 2 rather than folded into Task 10's README work, because it is a code defect on a
user-facing surface, not documentation.
Cost if wrong: none; the change adds two cases to a switch whose default is the thing being replaced.

Method note worth keeping: rounds 1 and 2 here were both found by running the shipped binary against
real fixtures and reading the output as a user would, not by reading diffs. Every defect in this task
was invisible to a green test suite — the tests asserted non-emptiness and shape, never truthfulness
or legibility.

Task 8: complete (7e05cec, caf2bea, aaca6c8 — 349 tests, 0 warnings)
  Verified against the built binary, not the report: mismatches 6 total/6 distinct; raw record dumps
  0 of 11 events in BOTH the --eni and no-ENI runs (were 6/11 and 11/11); all six config events render
  with a slave address where they have one and bus-wide phrasing where they do not.
  A JSON parse failure I hit while checking this was my own malformed shell loop, not a regression —
  the direct run produced valid JSON. Recording it because "verify the tool, not just the claim" cuts
  both ways: my probe was the broken thing that time.

Deferred to the whole-branch review (recorded so it is not lost): config-mismatch events are stamped
  `DateTimeOffset.UtcNow` at raise time, while wire events carry capture-relative timestamps. In the
  same events list that reads as `01:09:25.755 slave 1001: Identity …` next to
  `00:00:00.012 WKC mismatch …`. Defensible — the comparison genuinely happened now, not during the
  capture — but for an offline tool analysing a week-old pcap, half the list in wall-clock and half in
  capture time cannot be ordered by a reader. Worth a decision, not worth a third fix round on a task
  that has had two.

Task 9: implemented (16a0375 — 354 tests, 0 warnings). Fix round 1 dispatched before review.
  Verified the owner's `ExplorerViewModel.cs` and `ShellSmokeTests.cs` are untouched: their working-tree
  diffs contain no learning-related edits at all, and neither is staged.
  Implementer self-reported dispatching one Explore subagent against the brief's no-subagents rule,
  caught it, discarded the output and redid the verification directly. No file changes traced to it.
  Flagged so it does not recur; not treated as a defect in the result.

**Ruling 24 (Task 9, deviation accepted, consequences fixed): `HasEni` was made to lie, and the UI
copy it drives now contradicts the feature.**
The implementer changed `VariableWatchViewModel.HasEni` from `_session.Eni is not null` to
`… || _variables.Count > 0`, because `Refresh()` was clearing `Rows` for any learning-only session.
The behaviour is right and the signal is the correct one — I checked the alternative they rejected,
and gating on `Observer.Applied` would indeed have broken two existing `MainWindowViewModelTests`.
Two consequences are not acceptable as they stand:
  (a) A property named `HasEni` now returns true when there is no ENI. In a project whose whole thesis
      is refusing to present a partial picture as a complete one, leaving a member that misstates what
      it knows is the wrong artefact to ship. Renaming is safe — all five test call sites are outside
      the owner's `ShellSmokeTests`.
  (b) Worse: the empty state it drives reads "No ENI loaded / Variables need the process image from an
      ENI file." That is exactly the claim spec §7 exists to disprove — the Variables tab works with no
      ENI because learning supplies an equivalent configuration. The panel sends a user hunting for a
      file they may not need, and says nothing about the one action that would actually help a mid-run
      attach: restart the master with the capture running.
Ruling: keep the behaviour, rename the member, and rewrite the copy to name both routes to a process
image. Dispatched as a fix round BEFORE the task review rather than after, because the diagnosis was
already certain — sending it through review first would have bought a round-trip and no information.
Cost if wrong: a rename touching five call sites, all covered by the suite.

Task 9: fix round 1 complete (c88230d — 355 tests). Verified myself: no `HasEni` reference remains
  anywhere in src or tests, the empty state now reads "No process image yet" and names both routes to
  one, and the owner's two files are still untouched. Task review dispatched over both commits.

Task 10: complete (5817777 — 355 tests, 0 warnings). Done by me rather than dispatched.

**Ruling 25 (Task 10): I did this one myself, and corrected a false claim of my own that had already
shipped.**
Two reasons not to dispatch it. First, Task 10's remaining scope after Ruling 9 is documentation, and
its correctness depends entirely on facts about what shipped — facts I had just verified against the
running binary rather than against a diff. Second, the specific defects it had to fix were mine.
What the README claimed and no longer does: that the Variables tab "needs an ENI" (Task 9 made that
false), and a Milestone 3 bullet describing integration as still to come.
The correction that matters more: while checking the plan's proposed README line "exit 1 on mismatch",
I found `HasBusErrors` covers WKC, emergencies, SoE and slave error flags — not config mismatches. So
a bus that has drifted from its committed ENI but is otherwise healthy exits 0. I was about to rule
that this was a gap and implement an exit-code gate — then grepped the spec, which says nothing about
CI gates, exit codes or failing a build. It asks cross-check to RAISE and REPORT disagreements
(§ lines 194-197, 242), which is exactly what shipped.
The false attribution was mine: a doc comment I wrote in Task 8's brief, "The CI gate the spec calls
for", shipped into `LearningReportTests.cs` and would have led the next reader to believe exit codes
gate on mismatches. Corrected in place to state what the spec actually asks and what the exit code
actually means. No behaviour changed — adding an unrequested exit-code gate would have been scope
creep dressed up as spec compliance.
Cost if wrong: none; the change is a comment and documentation. The judgement worth keeping is that
checking my own earlier claim against the source cost one grep and prevented a user-visible behaviour
change nobody asked for.

**Ruling 26 (Task 9, review Critical — my brief's defect, and my test's): the Inspector never wires a
learned configuration into the Variables tab.**
`MainWindowViewModel.OnSessionStarted:58` builds `_assignment` from `session.Eni` alone and never
rebuilds it from `Observer.Applied`; `GetOrCreateEditor:93-105` then hands every editor an empty
variable list and caches it. Verified directly. So in the headline case for this whole plan — no ENI,
capture contains the bringup — a user clicks a fully-learned slave and sees, on one screen:
  General tab:   "Fully learned from observed traffic."
  Variables tab: "No process image yet… capture the master's startup so the bus can be learned instead"
The second statement is false and the two contradict each other. That is the exact failure this
feature exists to prevent, shipped in the feature itself.
Why no test caught it: MINE was the wrong test. `Variables_populate_from_learning_with_no_eni_loaded`
constructs the watch by hand from `Observer.Applied.Configuration`, bypassing `MainWindowViewModel`.
It proved the view model CAN render learned variables, never that the app DOES. This is the fourth
instance in Plan 2 of my recurring defect class, and the most consequential — the earlier three were
tests that asserted too little; this one asserted the wrong subject entirely. The countermeasure to
add: when a task's value is "a user can see X", the test must reach X through the same object graph
the user does, not through a hand-built stand-in.
The implementer had flagged this in their report's concerns as a known out-of-scope gap. They were
right and I should have acted on it then instead of accepting the scope line — the brief's "Produces"
list genuinely did not ask for the wiring, which is my omission, not theirs.
Ruling: fix in `MainWindowViewModel` only — rebuild the assignment when the learned revision changes,
keyed on revision so it tracks refinement rather than latching on the first partial picture, with a
loaded ENI always winning. Do NOT rebuild `Explorer`: it is the owner's file and rebuilding would drop
the user's tree selection. Accepted limitation, to be noted in the report: the process-image node's
visibility stays decided by the startup assignment.
Cost if wrong: clearing the editor cache resets the open editor's tab and filter during the first
moments of a session. Cheap against a screen that contradicts itself.
Checked before ruling: `ShellSmokeTests` references none of the APIs involved, so nothing in the fix
requires touching either protected file.

Task 9: complete (16a0375, c88230d, 41b9ba1 — 356 tests, 0 warnings)
  Round 2 closed the Critical. Pre-fix evidence: `Selecting_a_learned_slave_through_the_shell_
  populates_its_variables` failed `Assert.NotEmpty() Failure: Collection was empty`. Verified myself
  that the replacement test walks the user's real path — shell → `Explorer.SelectedNode` →
  `CurrentPage` → editor — and asserts rows AND the completeness string are non-empty at the same
  moment, which pins that the two statements on that screen agree rather than merely that one works.
  `RefreshAssignmentIfLearned()` runs first in `Tick()`.
  Implementer's judgement on the `HasVariables` edge case (item 1): keep the formula, document the
  exception, on the grounds that a scope with a confirmed zero variables should show an empty list
  rather than a "go find a configuration" prompt. I checked their supporting claim rather than taking
  it — `ExplorerViewModel.cs:81` does hide the process-image node when an ENI is loaded and nothing is
  unmatched, so the inaccurate case is unreachable from the live UI. Sound call, accepted.
  Accepted limitation on record: `Explorer` is not rebuilt when learning refines the assignment, so the
  process-image node's visibility still reflects the session-start picture. Rebuilding it would mean
  touching the owner's file and dropping the user's tree selection.

All 10 tasks complete. Dispatching the whole-branch final review on the most capable model.

**FINDING held for the final fix wave (found by me while the whole-branch review runs): the learned-bus
cache is unreachable from every shipped surface.**
`EtherCatMonitorOptions.LearnedCache` defaults to null, and NOTHING in `src/` ever sets it. Verified by
enumerating every `new EtherCatMonitorOptions` construction in the tree — there are exactly three, and
all three leave it null:
  `AnalyzeCommand.cs:53` (Eni, EsiDirectory, Learning)
  `LiveCommand.cs:79`    (Eni, EsiDirectory)
  `MonitorSession.cs:42` (Eni)
The only other mentions of `LearnedBusCache` in `src/` are its own file and the doc comment on the
option telling callers how to opt in.
Consequence: the cache cannot hit for any user of the CLI or the Inspector, because nothing ever saves
and nothing ever loads. The spec's own degradation table (§ line 222) lists "Attach at OP, cache hit →
Cached configuration applied at frame 1" as an expected scenario; as shipped that row is unreachable.
Task 6 spent four defect-fix rounds making the mid-run attach work, and it works — in the SDK, and in
its tests, and nowhere a user can get at it.
This is precisely the cross-task seam a per-task review cannot see: Task 6's brief was the cache,
Tasks 8 and 9's briefs were the surfaces, and no brief owned the join. My omission in planning.
Deliberately NOT dispatching a fix yet. The skill's sequence after all tasks complete is one final
review, then ONE fix wave, then one scoped re-review. The review is in flight; firing a fix now would
invalidate the diff it is reading. Batch this with whatever it returns.
Open question for that fix, to decide then rather than now: enabling the cache by default means the
tool writes to the user's `~/.config/openec/learned/` unprompted. The spec prescribes exactly that
path and treats persistence as the tool's behaviour rather than an opt-in, so default-on matches the
spec — but it is a filesystem side effect on the user's machine and deserves a deliberate decision
rather than a silent default.

## Whole-branch final review — returned, fix wave dispatched

Baseline it verified independently: 0 warnings, 356 tests, both protected files untouched across all
22 commits, no `Dahlke.EtherCAT.Diagnostics` in `OpenEC.Monitor`, no transmit path added. Rulings 22
and 23 confirmed still holding on the shipped binary.
It found concurrency and lifetime sound after a hard look — including that `EtherCatMonitor`'s
unguarded `_raisedMismatches`/`_cacheApplied` are in fact serialised because every path into
`OnConfigurationLearned` arrives holding `BusLearner._gate`, and that lock ordering is only ever
`_gate → BusObserver._lock`, never nested.

Findings, and my adjudication:

C1 — ACCEPTED, and worse than I had it. I found the cache unreachable; the review found the ADS tier
  equally unreachable AND that `_learner` is private with no accessor, so the tier cannot be used even
  from the SDK. Plus `FactSource.Cache` has zero producers, so cache-sourced facts would be attributed
  to `Inferred`/`EsiDefault` on a hit. I verified spec §9a before ruling on scope: it says the ADS tier
  is "deferred to the integration milestone" — this milestone — so wiring belongs here rather than
  descoping the README claim. Fallback offered to the implementer if 1c proves large: drop the README
  sentence rather than ship an unreachable claim. Never both.
C2 — ACCEPTED, and it is a defect inside my own Ruling 22 fix. `learnedByShape` keeps
  `g.First().BitOffs` and prints it as "observed @bit N". The reviewer moved a variable to bit 500 and
  got "observed @bit 0", where bit 0 is a different entry on a different slave. I read the code: the
  comment there even acknowledges the ambiguity and waves it through. On any bus with two entries of
  the same size and direction — nearly all of them — every genuine PDO-remap finding names a wrong
  offset. My fix removed five false claims and introduced a subtler one in the same surface.
I1, I2, I3, I4, I5 — ACCEPTED, all demonstrated by the reviewer with harnesses or the built CLI, not
  argued. I3 is the one I would most regret shipping: a half-learned mailbox map SUPPRESSES CoE
  emergency detection, so partial knowledge is strictly worse than none in an error-reporting path
  this tool exists to provide. Its second half is a genuine cross-task seam — the fingerprint does not
  digest mailbox ranges, so "SM1 became known" never publishes on its own.
M1 — ACCEPTED in part. The item worth acting on: the degraded completeness string, the sentence the
  whole completeness apparatus exists to produce, has NO test at all. Also `Contains("learned")`
  passes against an implementation that always says "Partially learned".
M3, M4, M5, M6 — deferred. M6 is outside this branch's diff (pre-existing `LearnCommand` behaviour).
  M3 is the mixed time bases I had already deferred; still a decision worth making, not a blocker.

Pattern the review named, and I agree with: the branch's structure and concurrency hold up; what has
repeatedly failed is surfaces claiming more than the code knows. Five of seven findings are that.

## Final fix wave — complete, all seven findings fixed, none wrong

Commits 7ff5119, 029215b, 5633e91, be3eb20, 98ba705, 63e9e67, 37d87e1, cff4fa2, 2c6987c.
383 tests (was 356), 0 warnings. Every finding reproduced before being fixed; reproductions recorded
in `final-fix-wave-report.md`. Selected evidence: the moved-variable case now reports "not in the
learned image" instead of naming a different slave's bit 0; a 4-case mailbox theory failed on exactly
`(SM0 known, SM1 unknown)` and the fingerprint stayed at revision 1 across an SM1 write; the cancelled
discovery pass went from `Expected: 20, Actual: 0`; the ESI process-image table reproduced 16/0/16
exactly and now migrates values by placement.
Implementer's own choices, both sound: `CanExecute` over a message for the Save control, with the
tooltip on an always-enabled wrapper because a disabled Avalonia control takes no pointer input; and
reset-at-decode-pass-start for the frame counters, with both halves pinned.

**Ruling 27: the spec has a factual error about the cache path, and the implementer found it.**
Spec § line 204 says `Environment.SpecialFolder.ApplicationData` is "`~/.config` on Linux and macOS".
On macOS it is `~/Library/Application Support`. The doc comment on the option repeated the spec's
error. Not a code defect — the code always used the API, so behaviour was always correct — but the
README would have told macOS users to look in a directory the tool never writes. Corrected in the
README to name all three real paths. Recording it against the spec rather than the code: the spec is
the authority this plan argues from, and this is the first time in two plans that the authority itself
was wrong on a checkable fact.

**Ruling 28 (residual from the wave, dispatched as one short round): the fix for finding 5 introduced a
new incoherence, and the implementer flagged it rather than hiding it.**
`DeviceEditorViewModel.cs:66` sources the completeness strip from `Observer.Applied`, which is null by
design whenever an ENI is loaded. Finding 5's fix now offers "Save learned ENI…" for exactly those
sessions. Net effect: the tool offers to export a reconstruction while showing nothing about how
complete it is — offering an artifact and hiding its quality, which is the failure class this entire
review was about.
Verified before ruling rather than assuming: `MonitorSession.Learned` exists (`:60`), and on a cache
hit `Applied.Completeness` equals `Learned.Completeness` because the capture's own completeness is
deliberately preserved over the cached file's. With no ENI they are the same object. So sourcing the
strip from `Learned` only adds availability and cannot change any existing case.
Also in the same round: `FactSource.Cache` lands correctly on `Observer.Applied.Provenance`, but
`analyze --json` reports `Learned.Provenance`, so a cache hit is structurally invisible in the JSON —
discoverable only by string-matching the events list. Ruled: do NOT switch `provenance` to the applied
view, because the learner's own view is honestly what that field reports and swapping it would discard
what this capture proved. Add an additive field naming the source of the configuration in force.
Cost if wrong: the strip appearing for ENI-loaded sessions is new information in a place that had none;
its wording already describes the reconstruction rather than the ENI, so it cannot misread as a claim
about the file.

Deferred deliberately, for the owner: `live --ads` end to end (needs a TwinCAT target) and hardware
acceptance on the ETAP-1000. Both need equipment no agent here has.

Ruling 28: complete (d2e53bb, 75bb1d8 — 387 tests, 0 warnings).
  The implementer found a case I had missed while re-verifying my own premise: `EtherCatMonitor:110`
  suppresses non-complete revisions published after a cache hit, so `Learned.Completeness` can advance
  past `Applied.Completeness`. That makes `Learned` the fresher assessment as well as the one Save
  actually writes — so the change is strictly better, not merely equivalent as I had argued.
  `configurationSource` is derived from `Observer.Applied.Provenance` rather than a new flag, on the
  grounds that the hit path stamps `FactSource.Cache` on every fact it puts in force, so an all-cache
  provenance IS the cache's signature. That also gives finding 1b's value its first consumer. Both
  values shown to discriminate under mutation.

**Ruling 29: the implementer's closing claim about the spec was wrong, and checking it took one grep.**
Their report said "The design document doesn't itself name the path, so there was nothing to edit
there; the wrong claim lived only in `DefaultDirectory`'s doc comment." That is false. Spec lines
203-204 read: "`<appdata>` is `Environment.SpecialFolder.ApplicationData` — `%APPDATA%` on Windows,
`~/.config` on Linux and macOS". The spec names the path and is wrong about macOS.
Rather than settle it between two contradicting claims, I ran the API: a throwaway net8.0 probe calling
`GetFolderPath(SpecialFolder.ApplicationData)` on this machine printed
`~/Library/Application Support`. So the code and README are right, the spec is wrong, and
their report was wrong about where the error lived.
Corrected spec lines 203-204 in place (dec28bb), naming all three real paths and recording that the
code was never affected because it always called the API. Left a note in the spec itself about the
correction, since the spec is the authority this plan and its successor argue from and a silent edit
would leave the next reader unable to tell which version they had.
Worth keeping as the session's clearest instance of the rule: a subagent's report is a claim. This one
was right about the defect, right about the fix, and wrong about the document — and only the third
mattered for what I did next.
