using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>What one slave's input window has been doing: how often it was sampled, and when its
/// contents last actually changed.</summary>
/// <param name="StaleFor">How long the window has held the same bytes, as of the last frame
/// observed. Measured against capture time, never wall-clock, so an offline analysis of a capture
/// from last week reports what the bus did rather than how long ago it did it.</param>
public sealed record ProcessDataActivity(
    ushort Address,
    uint LogicalStart,
    int Length,
    long Samples,
    long Changes,
    TimeSpan StaleFor,
    bool IsStale);

/// <summary>Watches each slave's process-data INPUT window and reports when one stops changing.
///
/// This exists because of a failure the rest of the SDK is structurally unable to see. A device
/// whose application has hung — but whose EtherCAT chip has not — keeps answering every datagram,
/// keeps its working counter correct, keeps every error counter at zero, and walks the AL state
/// machine into OP on request. Every health signal EtherCAT defines reports it as fine. The only
/// evidence left is that the numbers it sends never change again, and that a master-driven state
/// reset does not revive it, because the reset re-initialises the chip and not the application
/// behind it. This tracker is the one place that evidence is collected.
///
/// It decodes the FMMU register writes itself rather than reading the learner, for the same reason
/// <see cref="Topology.TopologyTracker"/> does: the observer is fed independently of
/// <see cref="BusLearner"/>, and `--no-learn` switches the learner off entirely.
///
/// Staleness is deliberately reported, not concluded. Plenty of devices hold an input steady for a
/// long time perfectly legitimately — a digital input nobody has pressed reads the same forever —
/// so this raises an observation and lets a reader judge. It is only reported for a slave in OP,
/// because a slave that is not in OP is not expected to be exchanging process data at all.
///
/// Not thread-safe: <see cref="BusObserver"/> holds its lock across Observe, exactly as for the
/// other trackers.</summary>
public sealed class ProcessDataActivityTracker(BusModel model, TimeSpan? staleAfter)
{
    private sealed class Window
    {
        public uint LogicalStart;
        public int Length;
        public byte[]? Last;
        public long Samples;
        public long Changes;
        public DateTimeOffset LastChange;
        public bool Reported;
    }

    private readonly Dictionary<ushort, Window> _windows = new();
    private readonly RingLengthTracker _ringLength = new();
    private readonly Dictionary<int, (uint LogicalStart, int Length)> _pendingByRing = new();
    private DateTimeOffset _now;

    /// <summary>Null disables the check outright; the windows are still tracked, so
    /// <see cref="Snapshot"/> keeps answering and only the verdict goes away.</summary>
    public TimeSpan? StaleAfter { get; } = staleAfter;

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        _now = ts;

        // An input FMMU tells us which logical bytes belong to which slave. Written once at
        // bring-up, so this costs nothing in the cyclic phase.
        foreach (var fmmu in RegisterDecoders.TryFmmus(d, dir))
        {
            if (fmmu is not { Enabled: true, Type: FmmuType.Inputs }) continue;
            if (fmmu.PhysicalStart < LearnedSlave.ProcessDataAreaStart) continue;   // register mirror
            if (Resolve(fmmu.Slave) is not { } resolved) continue;
            if (resolved.Station is not { } address)
            {
                _pendingByRing[resolved.RingPosition] = (fmmu.LogicalStart, fmmu.Length);
                continue;
            }
            Track(address, fmmu.LogicalStart, fmmu.Length);
        }

        if (RegisterDecoders.TryStationAddress(d, dir) is { } assignment
            && _pendingByRing.Remove(assignment.RingPosition, out var scanned))
            Track(assignment.StationAddress, scanned.LogicalStart, scanned.Length);

        if (dir != FrameDirection.Returning || !d.IsLogical || d.WorkingCounter == 0)
            yield break;

        var start = d.LogicalAddress;
        var end = start + (uint)d.Payload.Length;
        foreach (var (address, window) in _windows)
        {
            if (window.Length == 0) continue;
            if (window.LogicalStart < start || window.LogicalStart + (uint)window.Length > end)
                continue;

            var slice = d.Payload.Span.Slice((int)(window.LogicalStart - start), window.Length);
            window.Samples++;
            if (window.Last is null)
            {
                window.Last = slice.ToArray();
                window.LastChange = ts;
                continue;
            }
            if (slice.SequenceEqual(window.Last))
            {
                if (Verdict(window, address) is { } stalled) yield return stalled;
                continue;
            }

            slice.CopyTo(window.Last);
            window.Changes++;
            window.LastChange = ts;
            if (window.Reported)
            {
                window.Reported = false;
                yield return new MonitorEvent.ProcessDataResumed(ts, address);
            }
        }
    }

    /// <summary>Reported once per stall, and only for a slave the bus says is in OP. Re-arms when
    /// the data moves again, so a device that stalls, recovers and stalls again says so twice.</summary>
    private MonitorEvent? Verdict(Window window, ushort address)
    {
        if (StaleAfter is not { } threshold || window.Reported) return null;
        var stale = _now - window.LastChange;
        if (stale < threshold) return null;
        if (!IsOperational(address)) return null;
        window.Reported = true;
        return new MonitorEvent.ProcessDataStalled(_now, address, stale);
    }

    /// <summary>Whether the bus says this slave is in OP.
    ///
    /// Per-slave AL state comes from addressed reads of 0x0130, which masters do at bring-up and
    /// often never again — once running, many poll the ring with a single BROADCAST read instead, so
    /// a session that attached mid-run has no per-slave state at all. Falling back to a uniform
    /// bus-wide OP is the same statement about every slave on the ring, and without it this check
    /// would simply go quiet on exactly the mid-run attach a diagnostic tool exists for.</summary>
    private bool IsOperational(ushort address)
    {
        if (model.TryGet(address, out var slave) && slave is { AlState: not SlaveAlState.Unknown })
            return slave.AlState == SlaveAlState.Op;
        return model is { BusState: SlaveAlState.Op, BusStateUniform: true };
    }

    private void Track(ushort address, uint logicalStart, int length)
    {
        if (!_windows.TryGetValue(address, out var window))
            _windows[address] = window = new Window();
        // A re-bringup can move a slave's window. Anything held about the old one describes bytes
        // that no longer belong to this slave, so it goes.
        if (window.LogicalStart == logicalStart && window.Length == length) return;
        window.LogicalStart = logicalStart;
        window.Length = length;
        window.Last = null;
        window.Samples = 0;
        window.Changes = 0;
        window.Reported = false;
    }

    private (ushort? Station, int RingPosition)? Resolve(SlaveRef slave)
    {
        if (slave.IsBroadcast) return null;
        if (_ringLength.Normalize(slave) is not { } reference) return null;
        if (!reference.IsAutoIncrement) return (reference.Address, -1);
        return model.TryMapAutoInc(reference.Address, out var seeded)
            ? (seeded, reference.RingPosition)
            : (null, reference.RingPosition);
    }

    public void ObserveRingLength(EtherCatDatagram d, FrameDirection dir) =>
        _ringLength.Observe(d, dir);

    /// <summary>Every slave whose input window has been located, whether or not it is stale.</summary>
    public IReadOnlyList<ProcessDataActivity> Snapshot() =>
        _windows.Select(kv => new ProcessDataActivity(
                kv.Key, kv.Value.LogicalStart, kv.Value.Length, kv.Value.Samples, kv.Value.Changes,
                kv.Value.Last is null ? TimeSpan.Zero : _now - kv.Value.LastChange,
                kv.Value.Reported))
            .OrderBy(a => a.Address)
            .ToList();

    /// <summary>The slaves currently reported stale, for <see cref="BusHealth"/>.</summary>
    public IReadOnlyList<ushort> StaleSlaves() =>
        _windows.Where(kv => kv.Value.Reported).Select(kv => kv.Key).Order().ToList();
}
