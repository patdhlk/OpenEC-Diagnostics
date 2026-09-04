using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Learning;

/// <summary>Everything learned about one slave. Populated only from observed traffic;
/// a null property means "not seen", never a defaulted stand-in.</summary>
public sealed class LearnedSlave
{
    /// <summary>SDO values keyed by object index, then sub-index. Sub-index order matters:
    /// assignment and mapping objects are read back in sub-index order, and sub-index 0
    /// carries the count.</summary>
    private readonly Dictionary<ushort, SortedDictionary<byte, uint>> _sdo = new();

    public required ushort StationAddress { get; init; }
    public int RingPosition { get; set; } = -1;
    public uint? VendorId { get; set; }
    public uint? ProductCode { get; set; }
    public uint? Revision { get; set; }
    public uint? SerialNumber { get; set; }
    public Dictionary<byte, SyncManagerFact> SyncManagers { get; } = new();
    public Dictionary<byte, FmmuFact> Fmmus { get; } = new();
    public Dictionary<uint, byte[]> EepromWords { get; } = new();

    /// <summary>Port state from DL status (0x0110), keyed by port index. An absent key means the
    /// register was never read for that port — the same contract as every other property here.</summary>
    public Dictionary<byte, PortState> Ports { get; } = new();

    /// <summary>Per-port error counters, merged across the block reads that produced them.
    /// Named Counters rather than PortCounters on purpose: a property sharing its element type's
    /// name hides that type in every expression inside this class, so <c>PortCounters.Unknown</c>
    /// would resolve to the property instead of the type. It also matches
    /// <see cref="Topology.TopologyDevice.Counters"/>.</summary>
    public Dictionary<byte, PortCounters> Counters { get; } = new();

    public byte? ProcessingUnitErrors { get; set; }
    public byte? PdiErrors { get; set; }

    /// <summary>The ports that can carry a downstream topology edge, in the ESC's internal
    /// forwarding order. Port 0 is upstream by definition and is excluded. Ordering matters:
    /// it decides which branch the reconstruction walks first, and therefore the map's row
    /// order — see the topology design spec §10, where the order is still to be confirmed
    /// against hardware.</summary>
    public IReadOnlyList<byte> ActiveDownstreamPorts =>
        TopologyReconstructor.ForwardingOrder
            .Where(port => port != 0 && Ports.TryGetValue(port, out var state) && state.IsActive)
            .ToList();

    public void RecordPorts(IReadOnlyDictionary<byte, PortState> ports)
    {
        foreach (var (port, state) in ports) Ports[port] = state;
    }

    public void RecordPortCounters(IReadOnlyDictionary<byte, PortCounters> counters)
    {
        foreach (var (port, value) in counters)
            Counters[port] = Counters.TryGetValue(port, out var existing)
                ? existing.Merge(value)
                : value;
    }

    /// <summary>Folds facts learned before this slave had a name into the slave that now carries
    /// it. Existing values win: anything already attributed to the station address was observed
    /// against a named slave, which is the stronger claim. Only used by <see cref="LearnedBus"/>'s
    /// promotion step, where the INIT scan's findings meet the address assignment that named
    /// them.</summary>
    internal void MergeFrom(LearnedSlave scanned)
    {
        VendorId ??= scanned.VendorId;
        ProductCode ??= scanned.ProductCode;
        Revision ??= scanned.Revision;
        SerialNumber ??= scanned.SerialNumber;
        ProcessingUnitErrors ??= scanned.ProcessingUnitErrors;
        PdiErrors ??= scanned.PdiErrors;
        if (RingPosition < 0) RingPosition = scanned.RingPosition;

        foreach (var (word, value) in scanned.EepromWords) EepromWords.TryAdd(word, value);
        foreach (var (port, state) in scanned.Ports) Ports.TryAdd(port, state);
        foreach (var (port, counters) in scanned.Counters) Counters.TryAdd(port, counters);
        foreach (var (number, sm) in scanned.SyncManagers) SyncManagers.TryAdd(number, sm);
        foreach (var (number, fmmu) in scanned.Fmmus) Fmmus.TryAdd(number, fmmu);
        foreach (var (index, subs) in scanned._sdo)
        {
            if (!_sdo.TryGetValue(index, out var mine))
                _sdo[index] = mine = new SortedDictionary<byte, uint>();
            foreach (var (sub, value) in subs) mine.TryAdd(sub, value);
        }
    }

    /// <summary>The lowest physical address that can hold process data. Below it is ESC register
    /// space (ETG.1000.4: registers occupy 0x0000-0x0FFF, the process-data RAM begins at 0x1000).
    /// </summary>
    public const ushort ProcessDataAreaStart = 0x1000;

    /// <summary>The FMMUs that actually carry process data.
    ///
    /// An enabled input/output FMMU is not automatically one of them: every TwinCAT slave observed
    /// on real hardware also maps a single byte of ESC REGISTER space — 0x080D, a SyncManager
    /// status byte — into the process image, which is how the master surfaces per-slave state
    /// alongside the data. That FMMU has no SyncManager window behind it and never will, because it
    /// is not pointing at one. Counting it as unplaced process data declared every slave on a
    /// healthy 16-device bus incapable of placing its process data, which is a statement about this
    /// predicate rather than about the bus.</summary>
    public IEnumerable<FmmuFact> ProcessDataFmmus =>
        Fmmus.Values.Where(f => f.Enabled
                                && f.Type is FmmuType.Inputs or FmmuType.Outputs
                                && f.PhysicalStart >= ProcessDataAreaStart);

    /// <summary>A digest that moves whenever anything a completeness assessment reads has moved.
    ///
    /// It exists because <see cref="BusLearner"/> republishes only when its digest changes, and the
    /// published <see cref="LearningCompleteness"/> is computed at that moment. Facts that alter
    /// what is KNOWN without altering the synthesized configuration — an FMMU block arriving after
    /// the last variable was placed, most of all — then never triggered a republish, and the
    /// completeness every surface displayed stayed frozen at whatever it had been. On a real
    /// 16-slave bringup that left 13 of 16 slaves reporting a state they had already grown out of.
    ///
    /// Lives here rather than in the learner because it has to reach the SDO values, and it is a
    /// change detector rather than a second copy of the assessment: it must be at least as
    /// sensitive as <see cref="LearningCompleteness.Assess"/>, never more precise.</summary>
    internal string FactDigest =>
        $"{StationAddress}:{RingPosition}:{VendorId}:{ProductCode}:{Revision}:{SerialNumber}"
        + ":sm=" + string.Join(",", SyncManagers.Values.OrderBy(sm => sm.Number)
            .Select(sm => $"{sm.Number}@{sm.PhysicalStart}+{sm.Length}:{sm.Enabled}"))
        + ":fmmu=" + string.Join(",", Fmmus.Values.OrderBy(f => f.Number)
            .Select(f => $"{f.Number}@{f.PhysicalStart}+{f.Length}:{f.Type}:{f.Enabled}"))
        + ":sdo=" + string.Join(",", _sdo.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}#" + string.Join('.',
                kv.Value.Select(entry => $"{entry.Key}={entry.Value}"))));

    public bool IdentityKnown => VendorId is not null && ProductCode is not null;

    /// <summary>True when this slave's identity came from a master-side ADS poll rather than from
    /// the wire. Provenance reports it as <see cref="FactSource.Ads"/>.</summary>
    public bool IdentityFromAds { get; set; }

    public void RecordSdo(ushort index, byte subIndex, uint value)
    {
        if (!_sdo.TryGetValue(index, out var subs))
            _sdo[index] = subs = new SortedDictionary<byte, uint>();
        subs[subIndex] = value;
    }

    public bool TryGetSdo(ushort index, byte subIndex, out uint value)
    {
        value = 0;
        return _sdo.TryGetValue(index, out var subs) && subs.TryGetValue(subIndex, out value);
    }

    /// <summary>The number of entries the object declares. Sub-index 0 carries it and is
    /// authoritative when present: entries beyond it are stale leftovers from an earlier, longer
    /// configuration, which is how a master shortens a PDO list. When sub-index 0 was never
    /// observed there is no declared count, so every observed entry counts.
    ///
    /// Returning the entry total instead would be wrong twice over: the dictionary holds only
    /// keys >= 1 in that case, so any "count minus one" undercounts by one, and on a sparse
    /// capture (entries 1, 3, 5 with no sub-index 0) a total-based cap drops everything above it.</summary>
    private static int DeclaredCount(SortedDictionary<byte, uint> subs) =>
        subs.TryGetValue(0, out var declared) ? (int)declared : int.MaxValue;

    /// <summary>PDO indices assigned to a SyncManager, from object 0x1C10 + n. Sub-index 0
    /// is the count, so a later count of zero correctly empties a previously filled list.</summary>
    public IReadOnlyList<ushort> AssignedPdos(byte syncManagerNumber)
    {
        var index = (ushort)(0x1C10 + syncManagerNumber);
        if (!_sdo.TryGetValue(index, out var subs)) return [];
        var count = DeclaredCount(subs);
        return subs.Where(kv => kv.Key >= 1 && kv.Key <= count)
            .OrderBy(kv => kv.Key)
            .Select(kv => (ushort)kv.Value)
            .ToList();
    }

    /// <summary>Entries of a mapping object (0x16xx/0x1Axx) in sub-index order.</summary>
    public IReadOnlyList<PdoMappingEntry> Mapping(ushort pdoIndex)
    {
        if (!_sdo.TryGetValue(pdoIndex, out var subs)) return [];
        var count = DeclaredCount(subs);
        return subs.Where(kv => kv.Key >= 1 && kv.Key <= count)
            .OrderBy(kv => kv.Key)
            .Select(kv => PdoMappingEntry.FromRaw(kv.Value))
            .ToList();
    }

    /// <summary>The name used to qualify this slave's process variables. Must be unique per
    /// slave: <c>ProcessImage</c> keys its variable dictionary by name, so two slaves
    /// sharing a name silently lose one of them. The ESI device name is a TYPE name shared by
    /// every identical terminal on the bus, so it cannot stand alone — the station address is
    /// unique by definition and is already known for any slave we can place variables for.</summary>
    public string DisplayName(EsiDevice? schema) =>
        schema?.NameEn is { Length: > 0 } name
            ? $"Slave {StationAddress} ({name})"
            : $"Slave {StationAddress}";

    /// <summary>The SyncManager whose physical window this FMMU maps, or null when none matches.
    /// Only enabled managers with a non-zero length can carry process data, and the match is
    /// ordered by SM number so an ambiguous physical start resolves the same way on every run.
    /// <see cref="EniSynthesizer"/> uses the result to place variables and
    /// <see cref="LearningCompleteness"/> checks it for null, so both must ask the same question —
    /// a second copy of this predicate is how ProcessDataPlaceable starts lying.</summary>
    public SyncManagerFact? SyncManagerFor(FmmuFact fmmu) =>
        SyncManagers.Values
            .Where(sm => sm.Enabled && sm.Length > 0 && sm.PhysicalStart == fmmu.PhysicalStart)
            .OrderBy(sm => sm.Number)
            .FirstOrDefault();

    /// <summary>The mailbox window of a SyncManager, or null when that SM was never configured.</summary>
    public MailboxRange? MailboxRange(byte syncManagerNumber) =>
        SyncManagers.TryGetValue(syncManagerNumber, out var sm) && sm.Length > 0
            ? new MailboxRange(sm.PhysicalStart, sm.Length)
            : null;
}
