using System.Threading.Channels;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor;

/// <summary>Facade tying a capture source to one or more <see cref="SegmentPipeline"/>s with an
/// async event stream. A plain capture drives a single segment; a CU2508 uplink is demultiplexed by
/// ESL port, one independent pipeline per populated downlink segment. The single-segment facade
/// (<see cref="Observer"/>, <see cref="Bus"/>, …) targets the plain segment for backward
/// compatibility; multi-segment consumers read <see cref="Segments"/>.</summary>
public sealed class EtherCatMonitor : IAsyncDisposable
{
    private static readonly TimeSpan SchemaResolveInterval = TimeSpan.FromSeconds(2);

    private readonly ICaptureSource _source;
    private readonly EtherCatMonitorOptions _options;
    private readonly Channel<MonitorEvent> _events;

    // Segments keyed by ESL port; -1 is the plain (no-ESL) segment, created eagerly so the
    // single-segment facade and any pre-run access have a target. Guarded by _segmentsLock because
    // the capture pump creates segments as new ports appear while the schema resolver enumerates them.
    private readonly object _segmentsLock = new();
    private readonly Dictionary<int, SegmentPipeline> _segments = new();
    private readonly List<SegmentPipeline> _segmentOrder = new();
    private readonly SegmentPipeline _primary;

    private EtherCatMonitor(ICaptureSource source, EtherCatMonitorOptions options)
    {
        _source = source;
        _options = options;
        _events = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _primary = CreateSegment(-1);
    }

    private SegmentPipeline CreateSegment(int port)
    {
        var pipeline = new SegmentPipeline(port, _options, _events.Writer);
        _segments[port] = pipeline;
        _segmentOrder.Add(pipeline);
        return pipeline;
    }

    private SegmentPipeline SegmentFor(FrameDecodeResult decoded)
    {
        // Only a decoded EtherCAT frame carries an ESL port; malformed and non-EtherCAT frames have
        // no segment identity, so they fall to the plain (-1) segment.
        var port = decoded is FrameDecodeResult.Success { Frame.Esl: { } esl } ? esl.Port : -1;
        lock (_segmentsLock)
            return _segments.TryGetValue(port, out var existing) ? existing : CreateSegment(port);
    }

    private IReadOnlyList<SegmentPipeline> SegmentsSnapshot()
    {
        lock (_segmentsLock)
            return _segmentOrder.ToList();
    }

    public static EtherCatMonitor OpenFile(string path, EtherCatMonitorOptions? options = null) =>
        new(new PcapFileSource(path), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor OpenLive(string interfaceName, EtherCatMonitorOptions? options = null) =>
        new(new LiveCaptureSource(interfaceName), options ?? new EtherCatMonitorOptions());

    public static EtherCatMonitor FromSource(ICaptureSource source, EtherCatMonitorOptions? options = null) =>
        new(source, options ?? new EtherCatMonitorOptions());

    /// <summary>Every segment that has observed at least one frame, in the order its port first
    /// appeared. Before any traffic (or a completely empty capture) this is just the plain segment,
    /// so the list is never empty. A plain capture yields exactly one entry with
    /// <see cref="SegmentPipeline.Port"/> = -1; a CU2508 capture yields one per populated port.</summary>
    public IReadOnlyList<SegmentPipeline> Segments
    {
        get
        {
            var withTraffic = SegmentsSnapshot().Where(s => s.HasTraffic).ToList();
            return withTraffic.Count > 0 ? withTraffic : new List<SegmentPipeline> { _primary };
        }
    }

    /// <summary>The plain (no-ESL) segment's observer. On a CU2508 capture this segment stays empty;
    /// such consumers must enumerate <see cref="Segments"/> instead.</summary>
    public BusObserver Observer => _primary.Observer;

    /// <summary>Delegates to <see cref="BusObserver.Bus"/> (spec §3.5 facade surface).</summary>
    public BusModel Bus => Observer.Bus;

    /// <summary>Delegates to <see cref="BusObserver.Statistics"/> (spec §3.5 facade surface).</summary>
    public TrafficStatistics Statistics => Observer.Statistics;

    /// <summary>Delegates to <see cref="BusObserver.ProcessImage"/> (spec §3.5 facade surface).</summary>
    public ProcessImage ProcessImage => Observer.ProcessImage;

    /// <summary>Delegates to <see cref="BusObserver.SnapshotHealth"/> (spec §3.5 facade surface).</summary>
    public BusHealth SnapshotHealth() => Observer.SnapshotHealth();

    /// <summary>The configuration the plain segment's learner has derived from observed traffic, or
    /// null when learning is off or nothing has been learned yet. Per-segment learned configurations
    /// live on <see cref="SegmentPipeline.Learned"/>.</summary>
    public LearnedConfiguration? Learned => _primary.Learned;

    /// <summary>Folds master-side identity from an ADS poll into slaves whose identity the wire
    /// never revealed — the case where the master's startup checking is disabled, so it never reads
    /// SII and never queries 0x1018 (spec §6). A no-op when learning is off. Applies to the plain
    /// segment: an ADS scan describes one master's bus.
    ///
    /// The tuple shape rather than the ADS type is what keeps this assembly independent of
    /// Dahlke.EtherCAT.Diagnostics; OpenEC.Monitor.Ads maps its own snapshot into it via
    /// <c>AdsBusSnapshot.ScannedIdentities</c>.</summary>
    public void ApplyAdsIdentity(
        IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)> scanned) =>
        _primary.ApplyAdsIdentity(scanned);

    public IAsyncEnumerable<MonitorEvent> Events => _events.Reader.ReadAllAsync();

    public async Task RunAsync(CancellationToken ct = default)
    {
        var learning = _options.Learning != LearningMode.Off;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resolver = learning ? ResolveSchemasPeriodicallyAsync(linked.Token) : Task.CompletedTask;
        try
        {
            await EnrichNamesAsync();
            // Discovery pass. Only the learner runs, so no process-image work happens against a
            // configuration that does not exist yet; pass 2 then decodes the whole file under the
            // finished configuration. Skipped when an ENI was supplied — that is already the
            // authority — and impossible on a live source, which cannot be replayed.
            var discovered = false;
            if (learning && _options.Eni is null && _source.SupportsMultiplePasses)
            {
                await foreach (var raw in _source.CaptureAsync(ct))
                {
                    var discovering = EtherCatFrameParser.Parse(raw.Data);
                    var segment = SegmentFor(discovering);
                    // Counted, and only counted. A session cancelled partway through a large offline
                    // capture used to report zero frames beside a populated device tree and a
                    // messages panel full of learning events; the count is the one of those three
                    // the user can check, and it was the false one.
                    segment.CountFramesOnly(raw.Timestamp, discovering);
                    segment.Observe(raw.Timestamp, discovering);
                }
                await ResolveAllSchemasAsync(ct);
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
            if (discovered)
                foreach (var segment in SegmentsSnapshot())
                    segment.ResetStatistics();
            await foreach (var raw in _source.CaptureAsync(ct))
            {
                var decoded = EtherCatFrameParser.Parse(raw.Data);
                var segment = SegmentFor(decoded);
                segment.Process(raw.Timestamp, decoded);
                if (!discovered) segment.Observe(raw.Timestamp, decoded);
            }
            // Stop the periodic resolver BEFORE the final pass. If both ran at once they would each
            // snapshot the same pending slaves, resolve them, and force-republish — two revisions
            // for identical content, which is the churn the fingerprint check exists to prevent.
            linked.Cancel();
            await StopResolverAsync(resolver);
            // An offline file or a stopped live session may have learned identities in its last frames.
            if (learning) await ResolveAllSchemasAsync(ct);
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
                await ResolveAllSchemasAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Resolves every segment's pending schemas. New segments can appear between ticks as
    /// the capture reveals new ports, so this always works from a fresh snapshot.</summary>
    private Task ResolveAllSchemasAsync(CancellationToken ct) =>
        Task.WhenAll(SegmentsSnapshot().Where(s => s.HasLearner).Select(s => s.ResolveSchemasAsync(ct)));

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
