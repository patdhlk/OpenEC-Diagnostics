using System.Buffers.Binary;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>One observed cyclic datagram shape: the master's frame table as seen on the wire.</summary>
public sealed record LearnedCyclicCommand(EtherCatCommand Command, uint RawAddress,
    int DataLength, ushort ExpectedWkc);

/// <summary>Accumulates decoded facts into a picture of the bus. The only stateful piece of
/// the learner; the decoders it drives are all pure. Not thread-safe — callers feed it from
/// a single pump, exactly as <see cref="BusObserver"/> is fed.</summary>
public sealed class LearnedBus
{
    private const uint EepromVendorIdWord = 0x0008;
    private const uint EepromProductCodeWord = 0x000A;
    private const uint EepromRevisionWord = 0x000C;
    private const uint EepromSerialWord = 0x000E;
    private const ushort CoeIdentityObject = 0x1018;

    private readonly Dictionary<ushort, LearnedSlave> _slaves = new();

    /// <summary>Slaves the INIT scan described before the master had named them, keyed by ring
    /// position. The scan reads identity and DL status by auto-increment and only assigns station
    /// addresses afterwards, from what it found — so on a real bringup every one of those facts
    /// arrives before its slave has an address. Dropping them, as this used to, discards the entire
    /// scan and leaves learning permanently stuck at zero however many times the master restarts.
    /// They are held here and promoted by <see cref="Promote"/> when the assignment names them, and
    /// are deliberately absent from <see cref="Slaves"/> until then: a slave with no station address
    /// is not something any surface can honestly show.</summary>
    private readonly Dictionary<int, LearnedSlave> _pendingByRing = new();
    private readonly RingLengthTracker _ringLength = new();
    private readonly Dictionary<ushort, ushort> _autoIncToStation = new();
    private readonly Dictionary<SlaveRef, uint> _pendingSiiAddress = new();
    private readonly Dictionary<(EtherCatCommand, uint), CyclicObservation> _cyclic = new();

    private sealed class CyclicObservation
    {
        public int DataLength;
        public readonly Dictionary<ushort, int> WkcCounts = new();
    }

    /// <summary>True once a station-address assignment has been observed, meaning the capture
    /// includes bus startup and the learned picture can be complete.</summary>
    public bool SawStartup { get; private set; }

    public IReadOnlyList<LearnedSlave> Slaves =>
        _slaves.Values.OrderBy(s => s.RingPosition < 0 ? int.MaxValue : s.RingPosition)
            .ThenBy(s => s.StationAddress)
            .ToList();

    public IReadOnlyList<LearnedCyclicCommand> CyclicCommands =>
        _cyclic.Select(kv => new LearnedCyclicCommand(kv.Key.Item1, kv.Key.Item2,
                kv.Value.DataLength, ModalWkc(kv.Value)))
            .OrderBy(c => c.RawAddress)
            .ToList();

    /// <paramref name="timestamp"/> is accepted for symmetry with BusObserver.Process and is
    /// reserved for time-based facts; no fact learned today needs it.
    public void Observe(DateTimeOffset timestamp, EtherCatDatagram d, FrameDirection direction)
    {
        // Before anything else: every returning auto-increment ADP below is offset by this.
        _ringLength.Observe(d, direction);

        if (d.IsLogical)
        {
            ObserveCyclic(d, direction);
            return;
        }

        if (RegisterDecoders.TryStationAddress(d, direction) is { } assignment)
        {
            SawStartup = true;
            _autoIncToStation[assignment.AutoIncAddress] = assignment.StationAddress;
            Promote(assignment.RingPosition, assignment.StationAddress);
            return;
        }

        if (RegisterDecoders.TrySiiAddress(d, direction) is { IsRead: true } siiAddress)
        {
            // Keyed on the normalized reference so the returning data datagram, whose ADP the ring
            // incremented, still finds the address its own request wrote.
            if (_ringLength.Normalize(siiAddress.Slave) is { } pendingKey)
                _pendingSiiAddress[pendingKey] = siiAddress.WordAddress;
            return;
        }

        if (RegisterDecoders.TrySiiData(d, direction) is { } siiData)
        {
            ObserveSiiData(siiData);
            return;
        }

        if (RegisterDecoders.TryDlStatus(d, direction) is { } dlStatus)
        {
            if (Resolve(dlStatus.Slave) is { } portSlave) portSlave.RecordPorts(dlStatus.Ports);
            return;
        }

        if (RegisterDecoders.TryPortCounters(d, direction) is { } counters)
        {
            if (Resolve(counters.Slave) is { } counterSlave)
            {
                counterSlave.RecordPortCounters(counters.Ports);
                counterSlave.ProcessingUnitErrors =
                    counters.ProcessingUnitErrors ?? counterSlave.ProcessingUnitErrors;
                counterSlave.PdiErrors = counters.PdiErrors ?? counterSlave.PdiErrors;
            }
            return;
        }

        foreach (var sm in RegisterDecoders.TrySyncManagers(d, direction))
            if (Resolve(sm.Slave) is { } slave) slave.SyncManagers[sm.Number] = sm;

        foreach (var fmmu in RegisterDecoders.TryFmmus(d, direction))
            if (Resolve(fmmu.Slave) is { } slave) slave.Fmmus[fmmu.Number] = fmmu;

        var sdo = MailboxDecoders.TrySdoDownload(d, direction)
            ?? MailboxDecoders.TrySdoUploadResponse(d, direction);
        if (sdo is not null) ObserveSdo(sdo);

        // A returning physical read proves the slave answered, which is enough to list it
        // when we attached after startup and never saw an address assignment.
        if (direction == FrameDirection.Returning && d.WorkingCounter > 0
            && d.Command == EtherCatCommand.Fprd && d.Adp != 0)
            GetOrAdd(d.Adp);
    }

    private void ObserveCyclic(EtherCatDatagram d, FrameDirection direction)
    {
        if (direction != FrameDirection.Returning) return;
        var key = (d.Command, d.RawAddress);
        if (!_cyclic.TryGetValue(key, out var observation))
            _cyclic[key] = observation = new CyclicObservation();
        observation.DataLength = Math.Max(observation.DataLength, d.Payload.Length);
        observation.WkcCounts[d.WorkingCounter] =
            observation.WkcCounts.GetValueOrDefault(d.WorkingCounter) + 1;
    }

    private void ObserveSiiData(SiiDataFact fact)
    {
        if (_ringLength.Normalize(fact.Slave) is not { } key) return;
        if (!_pendingSiiAddress.Remove(key, out var wordAddress)) return;
        if (Resolve(fact.Slave) is not { } slave) return;
        for (var i = 0; i + 2 <= fact.Data.Length; i += 2)
            slave.EepromWords[wordAddress + (uint)(i / 2)] = [fact.Data[i], fact.Data[i + 1]];
        slave.VendorId ??= ReadEepromDword(slave, EepromVendorIdWord);
        slave.ProductCode ??= ReadEepromDword(slave, EepromProductCodeWord);
        slave.Revision ??= ReadEepromDword(slave, EepromRevisionWord);
        slave.SerialNumber ??= ReadEepromDword(slave, EepromSerialWord);
    }

    private void ObserveSdo(SdoValueFact fact)
    {
        if (Resolve(fact.Slave) is not { } slave) return;
        slave.RecordSdo(fact.Index, fact.SubIndex, fact.Value);
        if (fact.Index != CoeIdentityObject) return;
        switch (fact.SubIndex)
        {
            case 1: slave.VendorId ??= fact.Value; break;
            case 2: slave.ProductCode ??= fact.Value; break;
            case 3: slave.Revision ??= fact.Value; break;
            case 4: slave.SerialNumber ??= fact.Value; break;
        }
    }

    private static uint? ReadEepromDword(LearnedSlave slave, uint wordAddress)
    {
        if (!slave.EepromWords.TryGetValue(wordAddress, out var low)) return null;
        if (!slave.EepromWords.TryGetValue(wordAddress + 1, out var high)) return null;
        Span<byte> dword = [low[0], low[1], high[0], high[1]];
        return BinaryPrimitives.ReadUInt32LittleEndian(dword);
    }

    private static ushort ModalWkc(CyclicObservation observation) =>
        observation.WkcCounts.Count == 0 ? (ushort)0
            : observation.WkcCounts.MaxBy(kv => kv.Value).Key;

    /// <summary>Maps a reference to a slave, translating auto-increment addressing through
    /// the assignment map. Returns null when the reference cannot yet be resolved — traffic
    /// seen before the address assignment is dropped rather than attributed to a guess.</summary>
    private LearnedSlave? Resolve(SlaveRef re)
    {
        if (re.IsBroadcast) return null;   // names every slave at once, so it names none of them
        if (_ringLength.Normalize(re) is not { } reference) return null;
        if (!reference.IsAutoIncrement) return GetOrAdd(reference.Address);
        return _autoIncToStation.TryGetValue(reference.Address, out var station)
            ? GetOrAdd(station)
            : PendingAt(reference.RingPosition);
    }

    /// <summary>The still-unnamed slave at a ring position, created on first sight. See
    /// <see cref="_pendingByRing"/>.</summary>
    private LearnedSlave PendingAt(int ringPosition)
    {
        if (!_pendingByRing.TryGetValue(ringPosition, out var slave))
            _pendingByRing[ringPosition] = slave = new LearnedSlave
            {
                StationAddress = 0,
                RingPosition = ringPosition,
            };
        return slave;
    }

    /// <summary>Attaches everything the scan learned at a ring position to the station address the
    /// master has just assigned it.</summary>
    private void Promote(int ringPosition, ushort stationAddress)
    {
        // Merged into the named slave rather than re-keyed: StationAddress stays init-only, so a
        // slave never exists with an address it was not created for.
        if (_pendingByRing.Remove(ringPosition, out var scanned))
            GetOrAdd(stationAddress).MergeFrom(scanned);
        GetOrAdd(stationAddress).RingPosition = ringPosition;
    }

    private LearnedSlave GetOrAdd(ushort stationAddress)
    {
        if (!_slaves.TryGetValue(stationAddress, out var slave))
            _slaves[stationAddress] = slave = new LearnedSlave { StationAddress = stationAddress };
        return slave;
    }
}
