using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

public sealed class TrafficStatistics
{
    private readonly Dictionary<EtherCatCommand, long> _byCommand = new();
    private byte _lastOutboundIdx;
    private bool _hasOutboundIdx;

    public long TotalFrames { get; private set; }
    public long EtherCatFrames { get; private set; }
    public long NonEtherCatFrames { get; private set; }
    public long MalformedFrames { get; private set; }
    public long SuspectedLostFrames { get; private set; }

    /// <summary>Outbound frames whose returning echo was never observed before the same
    /// key was re-sent — comparable to TwinCAT's "Lost Frames". See <see cref="RingLossTracker"/>
    /// for the capture-drop caveat.</summary>
    public long RingLostFrames { get; internal set; }

    public long WkcMismatches { get; internal set; }
    public DateTimeOffset? FirstTimestamp { get; private set; }
    public DateTimeOffset? LastTimestamp { get; private set; }
    public TimeSpan? EstimatedCycleTime { get; internal set; }
    public IReadOnlyDictionary<EtherCatCommand, long> DatagramsByCommand => _byCommand;

    public long OutboundFrames { get; private set; }
    public long ReturningFrames { get; private set; }

    /// <summary>Outbound frames whose first datagram idx is below 0x80. TwinCAT sends
    /// cyclic process-data frames from a fixed low idx pool and queued (acyclic/mailbox)
    /// frames from a rotating 0x80–0xFF pool, so this split mirrors the System Manager's
    /// "Cyclic + Queued" counters; for other masters it is an idx-pool heuristic.</summary>
    public long OutboundCyclicFrames { get; private set; }
    public long OutboundQueuedFrames { get; private set; }

    public double? FramesPerSecond => Rate(EtherCatFrames);
    public double? OutboundFramesPerSecond => Rate(OutboundFrames);
    public double? ReturningFramesPerSecond => Rate(ReturningFrames);
    public double? OutboundCyclicFramesPerSecond => Rate(OutboundCyclicFrames);
    public double? OutboundQueuedFramesPerSecond => Rate(OutboundQueuedFrames);

    private double? Rate(long count)
    {
        if (FirstTimestamp is null || LastTimestamp is null) return null;
        var seconds = (LastTimestamp.Value - FirstTimestamp.Value).TotalSeconds;
        return seconds <= 0 ? null : count / seconds;
    }

    internal void CountFrame(DateTimeOffset ts)
    {
        TotalFrames++;
        EtherCatFrames++;
        FirstTimestamp ??= ts;
        LastTimestamp = ts;
    }

    internal void CountDirection(FrameDirection direction, byte? firstIndex)
    {
        if (direction == FrameDirection.Returning) { ReturningFrames++; return; }
        OutboundFrames++;
        if (firstIndex is { } idx)
        {
            if (idx < 0x80) OutboundCyclicFrames++;
            else OutboundQueuedFrames++;
        }
    }

    /// <summary>Clears every counter and every derived timestamp. Used at exactly one point: when the
    /// offline discovery pass hands over to the decode pass. The discovery pass counts frames so a run
    /// cancelled inside it does not report zero, and the decode pass then traverses the same capture
    /// again — so without this a completed run would describe two traversals of a one-traversal file.</summary>
    internal void Reset()
    {
        TotalFrames = 0;
        EtherCatFrames = 0;
        NonEtherCatFrames = 0;
        MalformedFrames = 0;
        SuspectedLostFrames = 0;
        RingLostFrames = 0;
        WkcMismatches = 0;
        FirstTimestamp = null;
        LastTimestamp = null;
        EstimatedCycleTime = null;
        OutboundFrames = 0;
        ReturningFrames = 0;
        OutboundCyclicFrames = 0;
        OutboundQueuedFrames = 0;
        _byCommand.Clear();
        _lastOutboundIdx = 0;
        _hasOutboundIdx = false;
    }

    internal void CountNonEtherCat() { TotalFrames++; NonEtherCatFrames++; }

    internal void CountMalformed() { TotalFrames++; MalformedFrames++; }

    internal void CountDatagram(EtherCatCommand cmd) =>
        _byCommand[cmd] = _byCommand.GetValueOrDefault(cmd) + 1;

    /// <summary>Heuristic frame-loss detection over the master's outbound idx sequence.
    /// Gaps of 2–63 count as loss; larger jumps are treated as a different idx pool.
    /// Transitions into or out of idx 0 are ignored: masters like TwinCAT use a fixed
    /// idx 0 for cyclic frames alongside a rotating acyclic pool, and the wrap from a
    /// high acyclic idx back to 0 would otherwise register as a phantom gap. The cost is
    /// a blind spot for genuine losses adjacent to 0 in a purely rotating pool.</summary>
    internal void ObserveOutboundIndex(byte idx)
    {
        if (_hasOutboundIdx && idx != 0 && _lastOutboundIdx != 0)
        {
            var delta = (byte)(idx - _lastOutboundIdx);
            if (delta is > 1 and < 64) SuspectedLostFrames += delta - 1;
        }
        _lastOutboundIdx = idx;
        _hasOutboundIdx = true;
    }
}
