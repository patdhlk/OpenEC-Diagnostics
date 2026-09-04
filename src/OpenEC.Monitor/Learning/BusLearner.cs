using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Drives the decoders, holds the accumulator, and republishes a synthesized
/// configuration whenever the derived picture actually changes. Has no reference to
/// <see cref="BusObserver"/>: it consumes decoded frames and emits configurations, which
/// keeps it testable in isolation and lets the offline discovery pass reuse it verbatim.</summary>
public sealed class BusLearner
{
    private readonly LearnedBus _bus = new();
    private readonly DirectionTracker _direction = new();
    private readonly Dictionary<ushort, EsiDevice> _schemas = new();
    private readonly string? _esiDirectory;
    private readonly object _gate = new();
    private string? _lastFingerprint;
    private string? _lastFactsFingerprint;
    private int _revision;

    public BusLearner(string? esiDirectory = null) => _esiDirectory = esiDirectory;

    /// <summary>The most recent configuration, or null before anything has been learned.</summary>
    public LearnedConfiguration? Current { get; private set; }

    public event Action<LearnedConfiguration>? ConfigurationLearned;

    public void Observe(DateTimeOffset timestamp, FrameDecodeResult decoded)
    {
        if (decoded is not FrameDecodeResult.Success ok) return;
        lock (_gate)
        {
            var direction = _direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                _bus.Observe(timestamp, datagram, direction);
            Republish();
        }
    }

    /// <summary>Resolves learned identities against the ESI directory and republishes with
    /// vendor names, datatypes and default PDO mappings folded in. Separate from
    /// <see cref="Observe"/> because ESI lookup is async and the capture pump is not.
    /// The snapshot below copies the identity fields' values, not the <see cref="LearnedSlave"/>
    /// references: those slaves stay live and mutable under the pump while this method's lookups
    /// run unlocked, so holding onto the objects themselves would let a concurrent
    /// <see cref="Observe"/> write tear a value read after the gate is released.</summary>
    public async Task ResolveSchemasAsync(CancellationToken ct = default)
    {
        if (_esiDirectory is null) return;

        List<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> pending;
        lock (_gate)
        {
            pending = _bus.Slaves
                .Where(s => !_schemas.ContainsKey(s.StationAddress)
                            && s.VendorId is not null && s.ProductCode is not null)
                .Select(s => (s.StationAddress, s.VendorId!.Value, s.ProductCode!.Value,
                              s.Revision ?? 0))
                .ToList();
        }
        if (pending.Count == 0) return;

        var resolved = new Dictionary<ushort, EsiDevice>();
        using var enricher = new EsiEnricher(_esiDirectory);
        foreach (var slave in pending)
        {
            ct.ThrowIfCancellationRequested();
            var device = await enricher.ResolveDeviceAsync(
                slave.VendorId, slave.ProductCode, slave.Revision);
            if (device is not null) resolved[slave.Address] = device;
        }

        if (resolved.Count == 0) return;
        lock (_gate)
        {
            foreach (var (address, device) in resolved) _schemas[address] = device;
            Republish(force: true);
        }
    }

    /// <summary>Folds master-side identity from an ADS poll into slaves whose identity the wire
    /// never revealed — the case where TwinCAT's startup checking is disabled, so it never reads
    /// SII and never queries 0x1018 (spec §6).
    ///
    /// A slave whose identity the wire already revealed is skipped outright, so identity observed
    /// on the wire always wins: ADS reports what the master BELIEVES is out there, and where that
    /// disagrees with the bus, the disagreement is the finding — not something to overwrite. A
    /// tuple rather than the ADS type keeps OpenEC.Monitor free of a dependency on the diagnostics
    /// package.</summary>
    public void ApplyAdsIdentity(
        IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> scanned)
    {
        lock (_gate)
        {
            var known = _bus.Slaves.ToDictionary(s => s.StationAddress);
            var changed = false;
            foreach (var entry in scanned)
            {
                if (!known.TryGetValue(entry.Address, out var slave)) continue;
                if (slave.IdentityKnown) continue;
                slave.VendorId = entry.VendorId;
                slave.ProductCode = entry.ProductCode;
                slave.Revision = entry.Revision;
                slave.IdentityFromAds = true;
                changed = true;
            }
            if (changed) Republish(force: true);
        }
    }

    /// <summary>Refreshes <see cref="Current"/> whenever anything it reports would differ, and
    /// announces a new revision only when the derived CONFIGURATION differs.
    ///
    /// The two move independently. A revision is a statement that the bus is configured differently
    /// than it was, and cyclic traffic must not churn them once the bus is in OP — that is what the
    /// configuration digest is for. But <see cref="LearnedConfiguration.Completeness"/> is a
    /// statement about what LEARNING knows, and facts change that without changing the
    /// configuration at all: an FMMU block arriving after the last variable was placed moves no
    /// synthesized byte. Gating both on one digest left completeness frozen at whatever it happened
    /// to be at the last configuration change; gating neither turned a single 16-slave bringup into
    /// several hundred revisions, each announcing a configuration nobody had altered.</summary>
    private void Republish(bool force = false)
    {
        if (_bus.Slaves.Count == 0) return;
        var configuration = EniSynthesizer.Synthesize(_bus, _schemas);
        var configurationDigest = Fingerprint(configuration);
        var factsDigest = Fingerprint(_bus);
        var configurationChanged = force || configurationDigest != _lastFingerprint;
        if (!configurationChanged && factsDigest == _lastFactsFingerprint) return;
        _lastFingerprint = configurationDigest;
        _lastFactsFingerprint = factsDigest;
        Current = new LearnedConfiguration(configuration,
            LearningCompleteness.Assess(_bus, _schemas),
            _bus.Slaves.ToDictionary(s => s.StationAddress, Provenance),
            configurationChanged ? ++_revision : _revision);
        // Only a changed configuration is news. A refreshed assessment under the same revision is
        // still visible to anything that reads Current, which is how every surface reaches it.
        if (configurationChanged) ConfigurationLearned?.Invoke(Current);
    }

    private FactProvenance Provenance(LearnedSlave slave)
    {
        var identity = slave.EepromWords.Count > 0 ? FactSource.Sii
            : slave.IdentityFromAds ? FactSource.Ads
            : slave.IdentityKnown ? FactSource.CoeIdentity
            : FactSource.Inferred;
        var names = _schemas.ContainsKey(slave.StationAddress)
            ? FactSource.EsiDefault
            : FactSource.Inferred;
        // PDO assignment is learned from a CoE mailbox DOWNLOAD, never from a register write:
        // 0x1C1x lives in the object dictionary, not in the ESC register file. Labelling it
        // RegisterWrite would misstate the source, which is the one thing provenance exists to
        // get right.
        //
        // Three outcomes, not two. `EsiDefault` used to be the sole alternative to `Coe`, so a
        // capture with no ESI directory at all still reported `mapping=EsiDefault` — naming a source
        // that does not exist, next to a `learn` run reporting zero process variables. `EsiDefault`
        // is a real answer only where a schema actually resolved for THIS slave, which is the same
        // dictionary the synthesizer consults; with neither wire nor schema, nothing is known and
        // `Inferred` says so.
        var mapping = slave.SyncManagers.Keys.Any(sm => slave.AssignedPdos(sm).Count > 0)
            ? FactSource.Coe
            : _schemas.ContainsKey(slave.StationAddress)
                ? FactSource.EsiDefault
                : FactSource.Inferred;
        return new FactProvenance(identity, names, mapping);
    }

    /// <summary>A cheap structural digest of everything a consumer would notice changing.
    /// Deliberately excludes working counters and cyclic timing, which vary every frame.
    ///
    /// The mailbox windows are in here because <see cref="BusObserver"/> acts on them: they are how it
    /// decides whether a datagram carries CoE or SoE, and it only ever learns them from a republish.
    /// Leaving them out meant "SM1 just became known" moved nothing in this digest, so the second half
    /// of a slave's mailbox map reached the observer only when some unrelated change happened to carry
    /// it — and until then the emergencies arriving in that window went unreported.</summary>
    /// <summary>Everything a completeness assessment reads, per slave. See
    /// <see cref="LearnedSlave.FactDigest"/> for why the configuration digest alone is not
    /// enough.</summary>
    private static string Fingerprint(LearnedBus bus) =>
        string.Join('|', bus.Slaves.Select(s => s.FactDigest));

    private static string Fingerprint(Eni.EniConfiguration configuration) =>
        string.Join('|',
            configuration.Slaves.Select(s =>
                $"{s.PhysAddr}:{s.VendorId}:{s.ProductCode}:{s.RevisionNo}:{s.Name}"
                + $":{Window(s.MailboxOut)}:{Window(s.MailboxIn)}")
            .Concat(configuration.CyclicCommands.Select(c =>
                $"{c.Command}:{c.RawAddress}:{c.DataLength}:{c.ExpectedWkc}"))
            .Concat(configuration.Variables.Select(v =>
                $"{v.Name}:{v.BitOffs}:{v.BitSize}:{v.IsInput}")));

    /// <summary>"-" rather than an empty string for an unknown window, so a window that becomes known
    /// can never digest to the same text as its absence.</summary>
    private static string Window(Eni.MailboxRange? range) =>
        range is null ? "-" : $"{range.Start}+{range.Length}";
}
