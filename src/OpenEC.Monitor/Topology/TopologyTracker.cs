using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Topology;

/// <summary>Accumulates port facts and keeps a current <see cref="BusTopology"/>, emitting an
/// event whenever a port's state actually changes. A sibling of
/// <see cref="SlaveStateTracker"/>: same constructor shape, same Observe signature, driven from
/// the same loop in <see cref="BusObserver"/>.
///
/// It decodes for itself rather than reading the learner, because the observer is fed
/// independently of <see cref="BusLearner"/> and `--no-learn` switches the learner off entirely.
/// Topology is observation, not learning, and must survive that.
///
/// Not thread-safe: <see cref="BusObserver"/> holds its lock across Observe, exactly as for the
/// other trackers.</summary>
public sealed class TopologyTracker(BusModel model)
{
    private readonly Dictionary<ushort, Dictionary<byte, PortState>> _ports = new();
    private readonly Dictionary<ushort, Dictionary<byte, PortCounters>> _counters = new();
    private readonly Dictionary<ushort, int> _ringPositions = new();
    private readonly Dictionary<ushort, ushort> _autoIncToStation = new();

    /// <summary>Port facts read during the INIT scan, before the master had assigned the station
    /// addresses that name them — keyed by ring position and promoted by <see cref="Observe"/> when
    /// the assignment arrives. The scan reads DL status by auto-increment and assigns addresses
    /// afterwards, so on a real bringup this is where every port fact lands first. Dropping them
    /// left the map permanently in ring order on hardware whose master polls topology every
    /// bringup.</summary>
    private readonly Dictionary<int, Dictionary<byte, PortState>> _portsByRing = new();
    private readonly Dictionary<int, Dictionary<byte, PortCounters>> _countersByRing = new();
    private readonly RingLengthTracker _ringLength = new();
    private EniConfiguration? _eni;
    private BusTopology? _current;
    private readonly HashSet<(ushort Address, string Declared, string Observed)> _reported = new();

    public BusTopology Current => _current ??= Rebuild();

    /// <summary>Adopts a configuration's declared edges and its auto-increment addresses. Mirrors
    /// <c>WkcTracker.Rebind</c>: a learned configuration published mid-session replaces the
    /// previous declaration rather than being merged into it.</summary>
    public void Rebind(EniConfiguration? eni)
    {
        _eni = eni;
        _current = null;
        _reported.Clear();
    }

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        // Every returning auto-increment ADP below is offset by this, so it is read first.
        _ringLength.Observe(d, dir);

        if (RegisterDecoders.TryStationAddress(d, dir) is { } assignment)
        {
            _autoIncToStation[assignment.AutoIncAddress] = assignment.StationAddress;
            _ringPositions[assignment.StationAddress] = assignment.RingPosition;
            Promote(assignment.RingPosition, assignment.StationAddress);
            _current = null;
            yield break;
        }

        if (RegisterDecoders.TryDlStatus(d, dir) is { } dlStatus)
        {
            if (Resolve(dlStatus.Slave) is not { } resolved) yield break;
            if (resolved.Station is not { } address)
            {
                // Scan phase: real port state, no name for it yet. Held, not dropped.
                Pending(_portsByRing, resolved.RingPosition, dlStatus.Ports);
                _current = null;
                yield break;
            }
            if (!_ports.TryGetValue(address, out var known))
                _ports[address] = known = new Dictionary<byte, PortState>();

            var changed = false;
            foreach (var (port, state) in dlStatus.Ports)
            {
                var previous = known.GetValueOrDefault(port);
                known[port] = state;
                if (previous is null) { changed = true; continue; }   // first read is not a change
                if (previous.State == state.State) continue;
                changed = true;
                yield return new MonitorEvent.TopologyChanged(ts, address, port,
                    previous.State, state.State);
            }
            if (changed)
            {
                _current = null;
                foreach (var conflict in NewConflicts(ts)) yield return conflict;
            }
            yield break;
        }

        if (RegisterDecoders.TryPortCounters(d, dir) is { } counters)
        {
            if (Resolve(counters.Slave) is not { } resolved) yield break;
            if (resolved.Station is not { } address)
            {
                Pending(_countersByRing, resolved.RingPosition, counters.Ports);
                yield break;
            }
            if (!_counters.TryGetValue(address, out var known))
                _counters[address] = known = new Dictionary<byte, PortCounters>();
            foreach (var (port, value) in counters.Ports)
                known[port] = known.TryGetValue(port, out var existing) ? existing.Merge(value) : value;
            _current = null;   // counters ride along on the nodes, so the snapshot is stale
            foreach (var conflict in NewConflicts(ts)) yield return conflict;
        }
    }

    /// <summary>New disagreements between the ENI and the wire, each reported once. A standing
    /// disagreement re-derived on every poll must not re-enter the message stream, or a healthy
    /// bus with one wiring difference would bury every other event.</summary>
    private IEnumerable<MonitorEvent> NewConflicts(DateTimeOffset ts)
    {
        foreach (var conflict in Current.Conflicts)
        {
            var key = (conflict.Address, conflict.Declared, conflict.Observed);
            if (!_reported.Add(key)) continue;
            yield return new MonitorEvent.ConfigMismatch(ts, ConfigMismatchKind.Topology,
                conflict.Address, conflict.Declared, conflict.Observed);
        }
    }

    /// <summary>Auto-increment addressing has no station address until the assignment that maps
    /// the two has been seen. Until then the fact cannot name its slave and is dropped rather
    /// than attributed to a guess — the same rule <see cref="LearnedBus"/> applies.</summary>
    /// <summary>Where a fact belongs: a station address once one is known, otherwise the ring
    /// position it was read at. Null when the reference names nobody — a broadcast, or a returning
    /// auto-increment ADP seen before any broadcast sized the ring.</summary>
    private (ushort? Station, int RingPosition)? Resolve(SlaveRef slave)
    {
        if (slave.IsBroadcast) return null;   // names every slave at once, so it names none of them
        if (_ringLength.Normalize(slave) is not { } reference) return null;
        if (!reference.IsAutoIncrement) return (reference.Address, -1);
        if (_autoIncToStation.TryGetValue(reference.Address, out var station))
            return (station, reference.RingPosition);
        if (model.TryMapAutoInc(reference.Address, out var seeded)) return (seeded, reference.RingPosition);
        return (null, reference.RingPosition);
    }

    private static void Pending<T>(Dictionary<int, Dictionary<byte, T>> store, int ringPosition,
        IReadOnlyDictionary<byte, T> facts)
    {
        if (!store.TryGetValue(ringPosition, out var known))
            store[ringPosition] = known = new Dictionary<byte, T>();
        foreach (var (port, value) in facts) known[port] = value;
    }

    /// <summary>Attaches what the scan read at a ring position to the address just assigned it.</summary>
    private void Promote(int ringPosition, ushort stationAddress)
    {
        if (_portsByRing.Remove(ringPosition, out var scannedPorts))
        {
            if (!_ports.TryGetValue(stationAddress, out var known))
                _ports[stationAddress] = known = new Dictionary<byte, PortState>();
            foreach (var (port, state) in scannedPorts) known.TryAdd(port, state);
        }
        if (_countersByRing.Remove(ringPosition, out var scannedCounters))
        {
            if (!_counters.TryGetValue(stationAddress, out var known))
                _counters[stationAddress] = known = new Dictionary<byte, PortCounters>();
            foreach (var (port, value) in scannedCounters) known.TryAdd(port, value);
        }
    }

    private BusTopology Rebuild()
    {
        var addresses = model.Slaves.Select(s => s.Address)
            .Union(_ports.Keys)
            .Union(_ringPositions.Keys)
            .ToList();

        var devices = addresses.Select(address => new TopologyDevice(
                address,
                RingPositionOf(address),
                _ports.TryGetValue(address, out var ports)
                    ? new Dictionary<byte, PortState>(ports)
                    : new Dictionary<byte, PortState>(),
                _counters.TryGetValue(address, out var counters)
                    ? new Dictionary<byte, PortCounters>(counters)
                    : new Dictionary<byte, PortCounters>()))
            .ToList();

        return TopologyReconstructor.Reconstruct(devices, _eni);
    }

    /// <summary>Ring position from the observed address assignment, falling back to the ENI's
    /// declared auto-increment address. Both encode the position the same way: auto-increment
    /// addresses count down from zero.</summary>
    private int RingPositionOf(ushort address)
    {
        if (_ringPositions.TryGetValue(address, out var observed)) return observed;
        var declared = _eni?.Slaves.FirstOrDefault(s => s.PhysAddr == address);
        return declared is null ? -1 : (ushort)(0 - declared.AutoIncAddr);
    }
}
