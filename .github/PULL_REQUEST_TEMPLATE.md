<!--
Thanks for contributing. Please read CONTRIBUTING.md if you have not yet —
especially the passive-only constraint and the BusObserver snapshot contract.
-->

## What this changes

<!-- What the change does, and why the obvious alternative was wrong. -->

## Related issue

<!-- Closes #123 — or say why this needed no issue (small fix, docs). -->

## How it was verified

<!--
Which tests cover it, and whether you ran it against real hardware. If only
against synthetic captures, say so — that is useful information, not a
weakness.
-->

- [ ] `dotnet build` is warning-free
- [ ] `dotnet test` is green
- [ ] `dotnet format --verify-no-changes` reports nothing
- [ ] New behaviour has a test that fails without the change

## Checklist

- [ ] Nothing in this change transmits on the EtherCAT segment
- [ ] UI/reporting code reads `BusObserver` only via
      `SnapshotSlaves()` / `SnapshotEvents()` / `Statistics` /
      `ProcessImage.Current`
- [ ] User-visible numbers and timestamps use `CultureInfo.InvariantCulture`
- [ ] Commit messages follow Conventional Commits with a real body
- [ ] Any new capture fixture is safe to publish and could not reasonably
      have been synthesised
