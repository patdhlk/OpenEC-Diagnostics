using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Learning;

/// <summary>Turns learned facts into an <see cref="EniConfiguration"/> by chaining
/// FMMU → SyncManager → PDO assignment → ESI schema, per design spec §3.
///
/// ESI states what a slave offers; the assignment objects state what the master selected;
/// the SyncManager states where those bytes live in the slave's physical memory; the FMMU
/// maps that window into the master's logical process image. Chaining the four yields a
/// global bit offset per entry — which is exactly what <see cref="EniVariable.BitOffs"/>
/// means, so <see cref="ProcessVariableMap"/> consumes the result unchanged.</summary>
public static class EniSynthesizer
{
    public static EniConfiguration Synthesize(LearnedBus bus,
        IReadOnlyDictionary<ushort, EsiDevice> schemas)
    {
        var slaves = bus.Slaves;
        var inputOrigin = Origin(slaves, FmmuType.Inputs);
        var outputOrigin = Origin(slaves, FmmuType.Outputs);

        var variables = new List<EniVariable>();
        foreach (var slave in slaves)
        {
            schemas.TryGetValue(slave.StationAddress, out var schema);
            var name = slave.DisplayName(schema);
            foreach (var fmmu in slave.Fmmus.Values
                         .Where(f => f.Enabled && f.Type is FmmuType.Inputs or FmmuType.Outputs)
                         .OrderBy(f => f.LogicalStart).ThenBy(f => f.LogicalStartBit))
            {
                var isInput = fmmu.Type == FmmuType.Inputs;
                var origin = isInput ? inputOrigin : outputOrigin;
                var baseBit = (int)((fmmu.LogicalStart - origin) * 8) + fmmu.LogicalStartBit;
                variables.AddRange(VariablesFor(slave, schema, name, fmmu, isInput, baseBit));
            }
        }

        var topology = TopologyReconstructor.Reconstruct(
            slaves.Select(TopologyDevice.FromLearned).ToList());

        return new EniConfiguration
        {
            Slaves = slaves.Select(s => ToEniSlave(s, schemas, topology)).ToList(),
            CyclicCommands = bus.CyclicCommands
                .Select(c => ToCyclicCommand(c, slaves, inputOrigin, outputOrigin))
                .ToList(),
            Variables = variables,
        };
    }

    /// <summary>The lowest logical byte address covered by any FMMU of a direction. Offsets
    /// are expressed relative to this so they match the ENI convention, where BitOffs is
    /// relative to the whole input or output image.</summary>
    private static uint Origin(IReadOnlyList<LearnedSlave> slaves, FmmuType type)
    {
        var starts = slaves.SelectMany(s => s.Fmmus.Values)
            .Where(f => f.Enabled && f.Type == type)
            .Select(f => f.LogicalStart)
            .ToList();
        return starts.Count == 0 ? 0 : starts.Min();
    }

    private static EniSlave ToEniSlave(LearnedSlave slave,
        IReadOnlyDictionary<ushort, EsiDevice> schemas, BusTopology topology)
    {
        schemas.TryGetValue(slave.StationAddress, out var schema);
        return new EniSlave(
            slave.DisplayName(schema),
            slave.StationAddress,
            slave.RingPosition >= 0 ? (ushort)(0 - slave.RingPosition) : (ushort)0,
            slave.VendorId ?? 0,
            slave.ProductCode ?? 0,
            slave.Revision ?? 0,
            // ENI is written from the MASTER's perspective: <Send> is where the master sends, i.e.
            // SM0 (the slave's MBoxOut window), and <Recv> is where it reads, SM1. The repo's own
            // sample.eni.xml encodes this — <Send> is 0x1000 there — and EniConfigurationTests
            // asserts it. Getting it backwards mislabels every mailbox window in the export.
            slave.MailboxRange(0),
            slave.MailboxRange(1),
            PreviousPortOf(slave.StationAddress, topology));
    }

    /// <summary>The learned upstream edge, or null when there is none to declare. Only a
    /// wire-derived edge is exported: an inferred ring-order edge is not a fact about the
    /// topology, and writing it would make the exported ENI assert a wiring nobody observed.</summary>
    private static EniPreviousPort? PreviousPortOf(ushort address, BusTopology topology)
    {
        if (topology.Find(address) is not { EdgeSource: TopologyEdgeSource.Wire } node) return null;
        if (node.ParentAddress is not { } parent || parent == BusTopology.MasterAddress) return null;
        return node.ParentPort is { } port ? new EniPreviousPort(parent, port) : null;
    }

    private static EniCyclicCommand ToCyclicCommand(LearnedCyclicCommand cyclic,
        IReadOnlyList<LearnedSlave> slaves, uint inputOrigin, uint outputOrigin)
    {
        var start = cyclic.RawAddress;
        var end = start + (uint)cyclic.DataLength;
        var intersectsInputs = Intersects(slaves, FmmuType.Inputs, start, end);
        var intersectsOutputs = Intersects(slaves, FmmuType.Outputs, start, end);
        return new EniCyclicCommand(cyclic.Command, cyclic.RawAddress, cyclic.DataLength,
            cyclic.ExpectedWkc,
            intersectsInputs ? (int)(start - inputOrigin) : null,
            intersectsOutputs ? (int)(start - outputOrigin) : null);
    }

    private static bool Intersects(IReadOnlyList<LearnedSlave> slaves, FmmuType type,
        uint start, uint end) =>
        slaves.SelectMany(s => s.Fmmus.Values)
            .Where(f => f.Enabled && f.Type == type)
            .Any(f => f.LogicalStart < end && f.LogicalStart + f.Length > start);

    private static IEnumerable<EniVariable> VariablesFor(LearnedSlave slave, EsiDevice? schema,
        string slaveName, FmmuFact fmmu, bool isInput, int baseBit)
    {
        // The match lives on LearnedSlave so LearningCompleteness asks the identical question:
        // an arbitrary dictionary-order pick would resolve 0x1C10 + the WRONG SM number and could
        // yield a real-but-wrong PDO assignment. When nothing matches, this FMMU's variables cannot
        // be placed at all — LearningCompleteness reports the slave as incomplete so the gap is
        // visible, since this method cannot report a miss itself.
        var syncManager = slave.SyncManagerFor(fmmu);
        if (syncManager is null) yield break;

        var bit = baseBit;
        foreach (var pdoIndex in AssignedPdos(slave, schema, syncManager.Number, isInput))
        {
            var pdo = Pdo(schema, pdoIndex);
            foreach (var entry in MappingFor(slave, schema, pdoIndex))
            {
                if (!entry.IsPadding)
                {
                    var esiEntry = pdo?.Entries.FirstOrDefault(e =>
                        e.Index == entry.Index && (e.SubIndex ?? 0) == entry.SubIndex);
                    yield return new EniVariable(
                        $"{slaveName}.{EntryName(esiEntry, pdo, entry)}",
                        esiEntry?.DataType ?? DefaultDataType(entry.BitLength),
                        entry.BitLength, bit, isInput);
                }
                bit += entry.BitLength;
            }
        }
    }

    /// <summary>Assignment observed on the wire wins; otherwise fall back to the PDOs ESI
    /// declares for this SyncManager, then — only when unambiguous — to any PDO of the right
    /// direction. Internal rather than private so <see cref="LearningCompleteness"/> can report
    /// what the synthesizer would actually resolve instead of what ESI merely declares.</summary>
    internal static IReadOnlyList<ushort> AssignedPdos(LearnedSlave slave, EsiDevice? schema,
        byte syncManagerNumber, bool isInput)
    {
        var observed = slave.AssignedPdos(syncManagerNumber);
        if (observed.Count > 0) return observed;
        IReadOnlyList<EsiPdo> pdos = schema?.ProcessData?.Pdos ?? [];
        var direction = isInput ? EsiPdoDirection.Transmit : EsiPdoDirection.Receive;
        var forSm = pdos.Where(p => p.Direction == direction && p.SyncManager == syncManagerNumber)
            .Select(p => p.Index).ToList();
        if (forSm.Count > 0) return forSm;

        // Last resort: ESI PDOs of the right direction with no declared SM. Safe only when this
        // slave has a single SyncManager of that direction — otherwise the same PDO would be
        // assigned to several, producing duplicate variables at different offsets under one name.
        var candidateSms = slave.SyncManagers.Values.Count(sm =>
            sm.Enabled && sm.Length > 0 && slave.Fmmus.Values.Any(f =>
                f.Enabled && f.PhysicalStart == sm.PhysicalStart
                && (f.Type == FmmuType.Inputs) == isInput));
        return candidateSms == 1
            ? pdos.Where(p => p.Direction == direction).Select(p => p.Index).ToList()
            : [];
    }

    private static EsiPdo? Pdo(EsiDevice? schema, ushort pdoIndex) =>
        schema?.ProcessData?.Pdos.FirstOrDefault(p => p.Index == pdoIndex);

    /// <summary>Mapping observed on the wire wins; otherwise use the ESI default mapping. Internal
    /// for the same reason as <see cref="AssignedPdos"/>: a resolved PDO with no resolvable entries
    /// still places no variable, so completeness has to run the whole chain, not just its first
    /// link, or it reports mapping as known for a slave that produced nothing.</summary>
    internal static IReadOnlyList<PdoMappingEntry> MappingFor(LearnedSlave slave, EsiDevice? schema,
        ushort pdoIndex)
    {
        var observed = slave.Mapping(pdoIndex);
        if (observed.Count > 0) return observed;
        return Pdo(schema, pdoIndex)?.Entries
            .Select(e => new PdoMappingEntry(e.Index, e.SubIndex ?? 0, (byte)e.BitLength))
            .ToList() ?? [];
    }

    private static string EntryName(EsiPdoEntry? esiEntry, EsiPdo? pdo, PdoMappingEntry entry)
    {
        if (esiEntry?.Name is { Length: > 0 } entryName)
            return pdo?.Name is { Length: > 0 } pdoName
                ? $"{pdoName}.{entryName}"
                : entryName;
        return $"0x{entry.Index:X4}:{entry.SubIndex:X2}";
    }

    /// <summary>Width-derived type when ESI states none. Signedness is unknowable from the
    /// wire, so the unsigned form is used and provenance marks the variable as inferred.</summary>
    private static string DefaultDataType(byte bitLength) => bitLength switch
    {
        1 => "BOOL",
        8 => "USINT",
        16 => "UINT",
        32 => "UDINT",
        64 => "ULINT",
        _ => $"BIT{bitLength}",
    };
}
