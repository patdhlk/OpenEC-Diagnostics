using System.Threading.Channels;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor;

/// <summary>Facade tying a capture source to a BusObserver with an async event stream.</summary>
public sealed class EtherCatMonitor : IAsyncDisposable
{
    private static readonly TimeSpan SchemaResolveInterval = TimeSpan.FromSeconds(2);

    private readonly ICaptureSource _source;
    private readonly EtherCatMonitorOptions _options;
    private readonly Channel<MonitorEvent> _events;
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

    private EtherCatMonitor(ICaptureSource source, EtherCatMonitorOptions options)
    {
        _source = source;
        _options = options;
        Observer = new BusObserver(options.Eni, options.StaleProcessDataAfter);
        _events = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        Observer.EventRaised += e => _events.Writer.TryWrite(e);

        if (options.Learning != LearningMode.Off)
        {
            _learner = new BusLearner(options.EsiDirectory);
            _learner.ConfigurationLearned += OnConfigurationLearned;
        }
    }

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

    public static EtherCatMonitor OpenFile(string path, EtherCatMonitorOptions? options = null) =>
        new(new PcapFileSource(path), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor OpenLive(string interfaceName, EtherCatMonitorOptions? options = null) =>
        new(new LiveCaptureSource(interfaceName), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor FromSource(ICaptureSource source, EtherCatMonitorOptions? options = null) =>
        new(source, options ?? new EtherCatMonitorOptions());

    public BusObserver Observer { get; }

    /// <summary>Delegates to <see cref="BusObserver.Bus"/> (spec §3.5 facade surface).</summary>
    public BusModel Bus => Observer.Bus;

    /// <summary>Delegates to <see cref="BusObserver.Statistics"/> (spec §3.5 facade surface).</summary>
    public TrafficStatistics Statistics => Observer.Statistics;

    /// <summary>Delegates to <see cref="BusObserver.ProcessImage"/> (spec §3.5 facade surface).</summary>
    public ProcessImage ProcessImage => Observer.ProcessImage;

    /// <summary>Delegates to <see cref="BusObserver.SnapshotHealth"/> (spec §3.5 facade surface).</summary>
    public BusHealth SnapshotHealth() => Observer.SnapshotHealth();

    /// <summary>The configuration the learner has derived from observed traffic, or null when
    /// learning is off or nothing has been learned yet.</summary>
    public LearnedConfiguration? Learned => _learner?.Current;

    /// <summary>Folds master-side identity from an ADS poll into slaves whose identity the wire
    /// never revealed — the case where the master's startup checking is disabled, so it never reads
    /// SII and never queries 0x1018 (spec §6). A no-op when learning is off.
    ///
    /// The learner is private, so without this pass-through the ADS tier is unreachable through the
    /// facade and no consumer could feed it. The tuple shape rather than the ADS type is what keeps
    /// this assembly independent of Dahlke.EtherCAT.Diagnostics; OpenEC.Monitor.Ads maps its own
    /// snapshot into it via <c>AdsBusSnapshot.ScannedIdentities</c>.</summary>
    public void ApplyAdsIdentity(
        IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> scanned) =>
        _learner?.ApplyAdsIdentity(scanned);

    public IAsyncEnumerable<MonitorEvent> Events => _events.Reader.ReadAllAsync();

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resolver = _learner is null
            ? Task.CompletedTask
            : ResolveSchemasPeriodicallyAsync(linked.Token);
        try
        {
            await EnrichNamesAsync();
            // Discovery pass. Only the learner runs, so no process-image work happens against a
            // configuration that does not exist yet; pass 2 then decodes the whole file under the
            // finished configuration. Skipped when an ENI was supplied — that is already the
            // authority — and impossible on a live source, which cannot be replayed.
            var discovered = false;
            if (_learner is not null && _options.Eni is null && _source.SupportsMultiplePasses)
            {
                await foreach (var raw in _source.CaptureAsync(ct))
                {
                    var discovering = EtherCatFrameParser.Parse(raw.Data);
                    // Counted, and only counted. A session cancelled partway through a large offline
                    // capture used to report zero frames beside a populated device tree and a
                    // messages panel full of learning events; the count is the one of those three
                    // the user can check, and it was the false one.
                    Observer.CountFramesOnly(raw.Timestamp, discovering);
                    _learner.Observe(raw.Timestamp, discovering);
                }
                await _learner.ResolveSchemasAsync(ct);
                // Deliberately no ApplyConfiguration here. Republish fires ConfigurationLearned
                // whenever it sets Current — including the forced republish after schema resolution —
                // so OnConfigurationLearned has already applied everything this pass produced. Unlike
                // this line, it also knows whether a cached configuration is in force and must not be
                // replaced by a weaker one; re-applying here bypasses that and stomps a cache hit.
                discovered = true;
            }
            // The decode pass re-traverses the same capture, so the discovery pass's counts have to go
            // before it starts: after a COMPLETED run the statistics must describe exactly one
            // traversal. Only the counters — the bus model, process image and event log are all
            // legitimately carried forward.
            if (discovered) Observer.ResetStatistics();
            await foreach (var raw in _source.CaptureAsync(ct))
            {
                var decoded = EtherCatFrameParser.Parse(raw.Data);
                Observer.Process(raw.Timestamp, decoded);
                if (!discovered) _learner?.Observe(raw.Timestamp, decoded);
            }
            // Stop the periodic resolver BEFORE the final pass. If both ran at once they would each
            // snapshot the same pending slaves, resolve them, and force-republish — two revisions
            // for identical content, which is the churn the fingerprint check exists to prevent.
            linked.Cancel();
            await StopResolverAsync(resolver);
            // An offline file or a stopped live session may have learned identities in its last frames.
            if (_learner is not null) await _learner.ResolveSchemasAsync(ct);
        }
        finally
        {
            linked.Cancel();
            try
            {
                await StopResolverAsync(resolver);
            }
            finally
            {
                _events.Writer.TryComplete();
            }
        }
    }

    /// <summary>ESI lookup is async and the capture pump is not, so resolution runs on its own
    /// cadence. `ResolveSchemasAsync` returns immediately once every identity is either resolved or
    /// unresolvable, so a converged session costs nothing per tick.</summary>
    private async Task ResolveSchemasPeriodicallyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SchemaResolveInterval, ct);
                await _learner!.ResolveSchemasAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Awaits the periodic resolver's exit, treating cancellation as the expected outcome.
    /// Safe to call twice — awaiting an already-completed task returns immediately.</summary>
    private static async Task StopResolverAsync(Task resolver)
    {
        try { await resolver; }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    private async Task EnrichNamesAsync()
    {
        if (_options.EsiDirectory is null || _options.Eni is null) return;
        using var enricher = new EsiEnricher(_options.EsiDirectory, _options.LoggerFactory);
        foreach (var slave in _options.Eni.Slaves)
        {
            var name = await enricher.ResolveNameAsync(slave.VendorId, slave.ProductCode,
                slave.RevisionNo, EsiEnricher.TypeHintFromName(slave.Name));
            if (name is not null)
                Observer.SetResolvedDeviceName(slave.PhysAddr, name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Guard against a consumer awaiting Events indefinitely when the monitor is disposed
        // without ever running (or after RunAsync's own TryComplete already fired) - Complete
        // is idempotent, so this is safe either way.
        _events.Writer.TryComplete();
        await _source.DisposeAsync();
    }
}
