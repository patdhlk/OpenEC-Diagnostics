using System.Threading.Channels;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;

namespace OpenEC.Monitor;

/// <summary>One EtherCAT segment's full pipeline: a <see cref="BusObserver"/>, its optional
/// <see cref="BusLearner"/>, and the cache/diff wiring that binds them. A plain capture has exactly
/// one of these; a CU2508 uplink carries one per populated downlink port, told apart by the ESL
/// port tag. Each pipeline is independent — its own bus model, topology, process image and learned
/// cache — which is what lets two segments reuse the same station addresses without colliding.</summary>
public sealed class SegmentPipeline
{
    private readonly EtherCatMonitorOptions _options;
    private readonly BusLearner? _learner;

    /// <summary>Set only once a cached configuration has actually been APPLIED — not merely looked
    /// up. Latching on the attempt would burn the single lookup on the first published revision,
    /// which on a mid-run attach knows only one slave and can never match a saved multi-slave bus.</summary>
    private bool _cacheApplied;

    /// <summary>Mismatches already raised, keyed on everything but the timestamp. The learner
    /// republishes many times as the picture of the bus fills in, and <see cref="ConfigurationDiff"/>
    /// recomputes the same finding on every one of those revisions once it stabilises — without this,
    /// nine real findings become dozens of identical events and bury the ones that matter.</summary>
    private readonly HashSet<(ConfigMismatchKind Kind, ushort? Address, string Declared, string Observed)>
        _raisedMismatches = new();

    internal SegmentPipeline(int port, EtherCatMonitorOptions options, ChannelWriter<MonitorEvent> events)
    {
        Port = port;
        _options = options;
        Observer = new BusObserver(options.Eni, options.StaleProcessDataAfter);
        Observer.EventRaised += e => events.TryWrite(e);

        if (options.Learning != LearningMode.Off)
        {
            _learner = new BusLearner(options.EsiDirectory);
            _learner.ConfigurationLearned += OnConfigurationLearned;
        }
    }

    /// <summary>ESL downlink port this segment's traffic entered/left by, or -1 for a plain capture
    /// with no ESL wrapper. Consumers treat a negative port as "single, unlabelled segment".</summary>
    public int Port { get; }

    public BusObserver Observer { get; }

    /// <summary>The configuration this segment's learner has derived, or null when learning is off
    /// or nothing has been learned yet.</summary>
    public LearnedConfiguration? Learned => _learner?.Current;

    /// <summary>True once this segment has seen at least one frame. A CU2508 capture leaves the
    /// plain (-1) pipeline empty, so the monitor uses this to hide it from <c>Segments</c>.</summary>
    public bool HasTraffic => Observer.Statistics.TotalFrames > 0;

    internal bool HasLearner => _learner is not null;

    internal void Process(DateTimeOffset ts, Protocol.FrameDecodeResult decoded) =>
        Observer.Process(ts, decoded);

    internal void CountFramesOnly(DateTimeOffset ts, Protocol.FrameDecodeResult decoded) =>
        Observer.CountFramesOnly(ts, decoded);

    internal void Observe(DateTimeOffset ts, Protocol.FrameDecodeResult decoded) =>
        _learner?.Observe(ts, decoded);

    internal void ResetStatistics() => Observer.ResetStatistics();

    internal Task ResolveSchemasAsync(CancellationToken ct) =>
        _learner?.ResolveSchemasAsync(ct) ?? Task.CompletedTask;

    internal void ApplyAdsIdentity(
        IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> scanned) =>
        _learner?.ApplyAdsIdentity(scanned);

    /// <summary>Every learned revision lands here. With an ENI supplied the ENI is the authority and
    /// this only reports disagreements; with no ENI the learned configuration is all we have, so it
    /// rebinds the observer.</summary>
    private void OnConfigurationLearned(LearnedConfiguration learned)
    {
        if (_options.Eni is { } declared)
        {
            foreach (var mismatch in ConfigurationDiff.Compare(
                         declared, learned.Configuration, DateTimeOffset.UtcNow))
            {
                // SlaveMissing and ProcessImage both mean "the ENI declares something the bus did not
                // show". Until learning is complete that is indistinguishable from "not discovered
                // yet", and reporting it anyway is how a half-learned bus accuses a healthy machine.
                // Identity and SlaveUnexpected need no such gate: they describe a slave already seen,
                // and are true the moment they are observed.
                if (!learned.Completeness.IsComplete
                    && mismatch.Kind is ConfigMismatchKind.SlaveMissing
                                     or ConfigMismatchKind.ProcessImage)
                    continue;

                if (_raisedMismatches.Add((mismatch.Kind, mismatch.Address, mismatch.Declared, mismatch.Observed)))
                    Observer.Raise(mismatch);
            }
            return;
        }

        // Retry the lookup on every revision until one hits: the bus picture arrives a slave at a
        // time, so an early revision's fingerprint legitimately misses a bus that a later one matches.
        // Bounded in practice — revisions stop once the picture stabilises, and each retry is a probe.
        if (!_cacheApplied && !learned.Completeness.IsComplete && _options.LearnedCache is { } cache)
        {
            var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);
            if (cache.TryLoad(fingerprint, out var cached)
                || cache.TryLoad(LearnedBusCache.FallbackFingerprint(learned.Configuration), out cached))
            {
                _cacheApplied = true;
                // Completeness deliberately still describes what THIS capture revealed, not the
                // cached file. The cache gives a usable configuration; it does not make the capture
                // more complete, and saying otherwise would be the dishonesty completeness prevents.
                //
                // Provenance, by contrast, MUST be replaced. Every fact now in force was read out of
                // a cache file, not off this capture's wire; carrying the learner's own provenance
                // would report a cached identity as `Inferred` and a cached PDO mapping as
                // `EsiDefault` — naming sources that produced none of it. FactSource.Cache is in the
                // enum for exactly this moment.
                Observer.ApplyConfiguration(learned with
                {
                    Configuration = cached!,
                    Provenance = cached!.Slaves.ToDictionary(
                        s => s.PhysAddr,
                        _ => new FactProvenance(FactSource.Cache, FactSource.Cache, FactSource.Cache)),
                });
                Observer.Raise(new MonitorEvent.ConfigurationLearned(DateTimeOffset.UtcNow,
                    learned.Revision, $"cache hit — {learned.Completeness.Summary}"));
                return;
            }
        }

        // A cache hit stands until this capture learns something at least as good. On a mid-run
        // attach the learner's own picture has no FMMUs and no PDO mapping, so letting a later
        // revision overwrite it would discard the only usable configuration available.
        if (_cacheApplied && !learned.Completeness.IsComplete) return;

        Observer.ApplyConfiguration(learned);
        Observer.Raise(new MonitorEvent.ConfigurationLearned(
            DateTimeOffset.UtcNow, learned.Revision, learned.Completeness.Summary));
        if (learned.Completeness.IsComplete) _options.LearnedCache?.Save(learned);
    }
}
