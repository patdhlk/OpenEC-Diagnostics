using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Observation;

/// <summary>The stateful heart of the SDK: consumes decoded frames and maintains
/// bus model, statistics, process image and the event log.
/// All writes to <see cref="Bus"/>/<see cref="EventLog"/> must go through
/// <see cref="Process"/>, <see cref="SetResolvedDeviceName"/> or <see cref="ApplyConfiguration"/> -
/// the only three entry points that take the internal lock; concurrent readers must use the Snapshot*
/// accessors rather than enumerating <see cref="Bus"/>.Slaves or <see cref="EventLog"/>
/// directly, since those collections are mutated in place.</summary>
public sealed class BusObserver
{
    private const int EventLogCap = 10_000;

    private EniConfiguration? _eni;
    private readonly DirectionTracker _direction = new();
    private readonly RingLossTracker _ringLoss = new();
    private readonly CycleEstimator _cycle = new();
    private readonly SlaveStateTracker _states;
    private readonly WkcTracker _wkc;
    private readonly TopologyTracker _topology;
    private readonly HealthTracker _health;
    private readonly ProcessDataActivityTracker _processData;
    private readonly List<MonitorEvent> _eventLog = new();
    private readonly object _lock = new();

    public BusObserver(EniConfiguration? eni = null,
        TimeSpan? staleProcessDataAfter = null)
    {
        _eni = eni;
        Bus = new BusModel();
        if (eni is not null) Bus.Seed(eni);
        _states = new SlaveStateTracker(Bus);
        _topology = new TopologyTracker(Bus);
        if (eni is not null) _topology.Rebind(eni);
        _wkc = new WkcTracker(eni);
        _health = new HealthTracker(Bus, eni);
        _processData = new ProcessDataActivityTracker(Bus, staleProcessDataAfter);
        ProcessImage = new ProcessImage(eni);
    }

    public BusModel Bus { get; }
    public TrafficStatistics Statistics { get; } = new();
    public ProcessImage ProcessImage { get; }
    public IReadOnlyList<MonitorEvent> EventLog => _eventLog;

    public event Action<MonitorEvent>? EventRaised;

    public void Process(DateTimeOffset ts, FrameDecodeResult decoded)
    {
        lock (_lock)
        {
            switch (decoded)
            {
                case FrameDecodeResult.NotEtherCat:
                    Statistics.CountNonEtherCat();
                    return;
                case FrameDecodeResult.Malformed:
                    Statistics.CountMalformed();
                    return;
                case FrameDecodeResult.Success ok:
                    ProcessFrame(ts, ok.Frame);
                    return;
            }
        }
    }

    /// <summary>Counts one frame into <see cref="Statistics"/> and does nothing else — no bus model,
    /// no process image, no events. The offline discovery pass runs only the learner, since there is
    /// no configuration to decode against yet, but a session cancelled inside that pass must still be
    /// able to say how many frames it read: reporting zero beside a populated device tree and a
    /// messages panel full of learning events is three statements contradicting each other.
    /// Under the same lock as <see cref="Process"/>, so the two can never interleave a write.</summary>
    internal void CountFramesOnly(DateTimeOffset ts, FrameDecodeResult decoded)
    {
        lock (_lock)
        {
            switch (decoded)
            {
                case FrameDecodeResult.NotEtherCat: Statistics.CountNonEtherCat(); return;
                case FrameDecodeResult.Malformed: Statistics.CountMalformed(); return;
                case FrameDecodeResult.Success: Statistics.CountFrame(ts); return;
            }
        }
    }

    /// <summary>Drops the counts <see cref="CountFramesOnly"/> accumulated, so the decode pass's own
    /// traversal is the only one the finished statistics describe. See
    /// <see cref="TrafficStatistics.Reset"/>. The event log is deliberately NOT cleared: learning
    /// events raised during discovery are observations that happened.</summary>
    internal void ResetStatistics()
    {
        lock (_lock) Statistics.Reset();
    }

    /// <summary>Thread-safe snapshot of the current per-slave bus status, safe to enumerate
    /// while <see cref="Process"/> is concurrently mutating <see cref="Bus"/> on another thread.</summary>
    public IReadOnlyList<SlaveStatus> SnapshotSlaves()
    {
        lock (_lock) return Bus.Slaves.ToList();
    }

    /// <summary>Sets the resolved device name for a slave under the same lock as
    /// <see cref="Process"/>, so ESI-directory name enrichment (which can run concurrently
    /// with the capture pump, e.g. while EnrichNamesAsync awaits I/O) never mutates
    /// <see cref="Bus"/> outside of it - a second, previously unguarded writer alongside
    /// <see cref="Process"/> that could otherwise still race a concurrent SnapshotSlaves().</summary>
    public void SetResolvedDeviceName(ushort address, string name)
    {
        lock (_lock) Bus.GetOrAdd(address).ResolvedDeviceName = name;
    }

    /// <summary>The configuration most recently applied by <see cref="ApplyConfiguration"/>,
    /// or null when the observer is still running on whatever it was constructed with.</summary>
    public LearnedConfiguration? Applied { get; private set; }

    /// <summary>Rebinds the observer to a learned configuration, under the same lock as
    /// <see cref="Process"/> and <see cref="SetResolvedDeviceName"/> — the third and last writer
    /// to <see cref="Bus"/>, so a rebind can never race a concurrent <see cref="SnapshotSlaves"/>.
    ///
    /// Identity, names, the auto-increment map, the process-variable map, WKC expectations and the
    /// mailbox windows all come from the new configuration. <see cref="Statistics"/> and the event
    /// log are deliberately untouched: they are observations of the wire, not derivations of the
    /// configuration, and resetting them on every refinement would discard the bus-health history
    /// a diagnostic session exists to accumulate.</summary>
    public void ApplyConfiguration(LearnedConfiguration config)
    {
        lock (_lock)
        {
            _eni = config.Configuration;
            Bus.Seed(config.Configuration);
            ProcessImage.Rebind(config.Configuration);
            _wkc.Rebind(config.Configuration);
            _topology.Rebind(config.Configuration);
            _health.Rebind(config.Configuration);
            Applied = config;
        }
    }

    /// <summary>Thread-safe snapshot of the event log, safe to enumerate while
    /// <see cref="Process"/> is concurrently appending to it on another thread.
    /// When <paramref name="lastN"/> is greater than zero, only the most recent
    /// <paramref name="lastN"/> events are returned.</summary>
    public IReadOnlyList<MonitorEvent> SnapshotEvents(int lastN = 0)
    {
        lock (_lock)
            return lastN > 0 ? _eventLog.TakeLast(lastN).ToList() : _eventLog.ToList();
    }

    /// <summary>Thread-safe snapshot of the current topology. <see cref="BusTopology"/> and every
    /// type it holds are immutable records, so the returned value stays valid while
    /// <see cref="Process"/> continues on another thread.</summary>
    public BusTopology SnapshotTopology()
    {
        lock (_lock)
            return _topology.Current;
    }

    /// <summary>Thread-safe snapshot of the current bus health.</summary>
    public BusHealth SnapshotHealth()
    {
        lock (_lock)
            return _health.Compute() with { StaleSlaves = _processData.StaleSlaves() };
    }

    /// <summary>Thread-safe snapshot of what every located input window has been doing. Answers
    /// whether a slave's process data is moving at all, which no AL state, working counter or error
    /// counter can.</summary>
    public IReadOnlyList<ProcessDataActivity> SnapshotProcessData()
    {
        lock (_lock)
            return _processData.Snapshot();
    }

    private void ProcessFrame(DateTimeOffset ts, EtherCatFrame frame)
    {
        Statistics.CountFrame(ts);
        var dir = _direction.Classify(frame);
        if (dir == FrameDirection.Outbound && frame.Datagrams.Count > 0)
            Statistics.ObserveOutboundIndex(frame.Datagrams[0].Index);
        Statistics.CountDirection(dir, frame.Datagrams.Count > 0 ? frame.Datagrams[0].Index : null);
        Statistics.RingLostFrames += _ringLoss.Observe(frame, dir);

        foreach (var d in frame.Datagrams)
        {
            Statistics.CountDatagram(d.Command);
            _cycle.Observe(ts, d, dir);
            _processData.ObserveRingLength(d, dir);

            foreach (var evt in _states.Observe(ts, d, dir))
                Raise(evt);

            foreach (var evt in _topology.Observe(ts, d, dir))
                Raise(evt);

            if (dir == FrameDirection.Returning)
            {
                if (_wkc.Observe(ts, d, dir) is { } mismatch)
                {
                    Statistics.WkcMismatches++;
                    Raise(mismatch);
                }
                if (d.IsLogical) ProcessImage.UpdateInputs(d, ts);
                else if (d.Command == EtherCatCommand.Fprd && d.WorkingCounter == 1)
                    InspectMailbox(ts, d);
            }
            else
            {
                if (d.IsLogical) ProcessImage.UpdateOutputs(d, ts);
                else if (d.Command == EtherCatCommand.Fpwr)
                    InspectMailbox(ts, d);
            }

            foreach (var evt in _health.Observe(ts, d, dir))
                Raise(evt);

            // After the state trackers, so a stall is only ever reported against the AL state the
            // same frame established.
            foreach (var evt in _processData.Observe(ts, d, dir))
                Raise(evt);
        }
        Statistics.EstimatedCycleTime = _cycle.EstimatedCycleTime;
    }

    private void InspectMailbox(DateTimeOffset ts, EtherCatDatagram d)
    {
        if (!IsMailboxWindow(d.Adp, d.Ado)) return;
        var mailbox = MailboxParser.TryParse(d.Payload);
        if (mailbox?.Coe?.Emergency is { } emergency)
            Raise(new MonitorEvent.EmergencyReceived(ts,
                mailbox.StationAddress != 0 ? mailbox.StationAddress : d.Adp,
                emergency.ErrorCode, emergency.ErrorRegister));
        if (mailbox?.Soe is { Error: true, ErrorCode: { } errorCode } soe)
            Raise(new MonitorEvent.SoeErrorReceived(ts,
                mailbox.StationAddress != 0 ? mailbox.StationAddress : d.Adp,
                soe.OpCode, soe.IdnOrFragmentsLeft, errorCode));
    }

    /// <summary>Declared windows replace the generic 0x1000–0x2000 guess only once BOTH are known.
    ///
    /// A supplied ENI always declares the pair, but a LEARNED configuration derives each window
    /// independently from SM0 and SM1 (<see cref="Learning.LearnedSlave.MailboxRange"/>), so it can
    /// carry one without the other. Narrowing on a half-known map used to match only the known window
    /// and drop the fallback, which silently swallowed every CoE emergency and SoE error arriving in
    /// the window learning had not reached yet — making partial knowledge strictly worse than none.
    /// Falling back until the picture is complete is the honest answer: a wider guess may include
    /// traffic that is not mailbox traffic, which <see cref="MailboxParser"/> rejects, whereas
    /// excluding a real mailbox window loses the diagnostic outright.</summary>
    private bool IsMailboxWindow(ushort adp, ushort ado)
    {
        var slave = _eni?.Slaves.FirstOrDefault(s => s.PhysAddr == adp);
        if (slave is { MailboxOut: { } mailboxOut, MailboxIn: { } mailboxIn })
            return mailboxOut.Contains(ado) || mailboxIn.Contains(ado);
        return ado is >= 0x1000 and < 0x2000;
    }

    /// <summary>Internal so the monitor can surface events it derives from the learner —
    /// <see cref="MonitorEvent.ConfigMismatch"/> — through the same log and stream as observed
    /// events, without a second event path for callers to subscribe to.</summary>
    internal void Raise(MonitorEvent evt)
    {
        lock (_lock)
        {
            if (_eventLog.Count < EventLogCap) _eventLog.Add(evt);
        }
        EventRaised?.Invoke(evt);
    }
}
