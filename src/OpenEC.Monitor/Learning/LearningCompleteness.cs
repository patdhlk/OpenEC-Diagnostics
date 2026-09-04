using Dahlke.EtherCAT.Esi;

namespace OpenEC.Monitor.Learning;

/// <summary>Where a learned fact came from. Learning never silently claims a fact it
/// inferred, so every surface can state its own confidence.
///
/// <c>CoeIdentity</c> is specifically the identity objects (0x1018) read over CoE; <c>Coe</c> is
/// any other fact learned from a CoE mailbox transfer, PDO assignment and mapping above all.
/// Those are downloads, not register writes, so <c>RegisterWrite</c> would misstate the source —
/// which defeats the only purpose this enum has.</summary>
public enum FactSource { Sii, CoeIdentity, Coe, RegisterWrite, EsiDefault, Cache, Ads, Inferred }

public sealed record FactProvenance(FactSource Identity, FactSource Names, FactSource Mapping);

/// <param name="ProcessDataPlaceable">True when every enabled process-data FMMU resolves to an
/// enabled SyncManager, so its variables can actually be placed. Vacuously true for a slave with
/// no process data at all — a coupler has nothing to place. False means
/// <see cref="EniSynthesizer"/> silently emits no variables for those FMMUs: it matches
/// SyncManagers by physical address and cannot report a miss itself, so this flag is the only
/// place that gap becomes visible.</param>
public sealed record SlaveCompleteness(ushort StationAddress, bool IdentityKnown,
    bool SyncManagersKnown, bool FmmusKnown, bool PdoMappingKnown, bool NamesFromEsi,
    bool ProcessDataPlaceable)
{
    public bool IsComplete =>
        IdentityKnown && SyncManagersKnown && FmmusKnown && PdoMappingKnown && ProcessDataPlaceable;
}

/// <summary>What learning does and does not know, so the Inspector and CLI can say so
/// plainly rather than presenting a partial picture as a complete one.</summary>
public sealed record LearningCompleteness(bool SawStartup, IReadOnlyList<SlaveCompleteness> Slaves)
{
    public bool IsComplete => SawStartup && Slaves.Count > 0 && Slaves.All(s => s.IsComplete);

    public string Summary
    {
        get
        {
            var complete = Slaves.Count(s => s.IsComplete);
            var text = $"learned {complete}/{Slaves.Count} slaves";
            if (SawStartup) return text;
            return $"{text}; no bus startup observed — restart the master to learn PDO mapping";
        }
    }

    public static LearningCompleteness Assess(LearnedBus bus,
        IReadOnlyDictionary<ushort, EsiDevice> schemas) =>
        new(bus.SawStartup, bus.Slaves.Select(slave =>
        {
            var hasMapping = slave.SyncManagers.Keys
                .Any(sm => slave.AssignedPdos(sm)
                    .Any(pdo => slave.Mapping(pdo).Count > 0));
            var schema = schemas.GetValueOrDefault(slave.StationAddress);
            // Register-mapped FMMUs are excluded by LearnedSlave.ProcessDataFmmus — see there for
            // why an enabled input FMMU is not necessarily process data.
            var processData = slave.ProcessDataFmmus.ToList();
            // Shares EniSynthesizer's SyncManager match rather than restating it. `All` over an
            // empty set is true, which is the right answer for a coupler: no process data means
            // nothing to place.
            var placeable = processData.All(f => slave.SyncManagerFor(f) is not null);
            // Ask what EniSynthesizer would actually resolve, not what ESI merely declares. A
            // schema can carry PDOs the synthesizer places nothing from — no matching SyncManager,
            // or an ambiguous direction-only fallback it now refuses — and reporting those as known
            // mapping claims a fact that produced no variable at all.
            var esiMapping = processData.Any(f =>
                slave.SyncManagerFor(f) is { } sm
                && EniSynthesizer.AssignedPdos(slave, schema, sm.Number, f.Type == FmmuType.Inputs)
                    .Any(pdo => EniSynthesizer.MappingFor(slave, schema, pdo).Count > 0));
            return new SlaveCompleteness(
                slave.StationAddress,
                slave.IdentityKnown,
                slave.SyncManagers.Count > 0,
                slave.Fmmus.Values.Any(f => f.Enabled),
                hasMapping || esiMapping,
                // Keyed off the NAME, because that is the only part of the ESI schema that reaches
                // the slave's display name (see LearnedSlave.DisplayName). `ProcessData` is an
                // independently nullable field: a coupler can carry a name with no process data,
                // and a modular device can carry process data with no name — so testing the wrong
                // field would report a synthetic name as ESI-derived, which is the exact dishonesty
                // this type prevents.
                schema?.NameEn is not null,
                placeable);
        }).ToList());
}
