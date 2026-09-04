using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

public sealed record ResolvedVariable(EniVariable Variable, object Value);

/// <summary>Maps observed cyclic datagrams onto ENI process-image variables.</summary>
public sealed class ProcessVariableMap
{
    private sealed record Entry(EniVariable Variable, int PayloadBitOffset);
    private sealed record Mapping(int DataLength, List<Entry> Entries);

    private readonly Dictionary<(EtherCatCommand, uint), Mapping> _inputs = new();
    private readonly Dictionary<(EtherCatCommand, uint), Mapping> _outputs = new();

    public static ProcessVariableMap Build(EniConfiguration eni)
    {
        var map = new ProcessVariableMap();
        foreach (var cmd in eni.CyclicCommands)
        {
            var key = (cmd.Command, cmd.RawAddress);
            if (cmd.InputOffs is int inOffs)
                map._inputs[key] = new Mapping(cmd.DataLength, Collect(eni, isInput: true, inOffs, cmd.DataLength));
            if (cmd.OutputOffs is int outOffs)
                map._outputs[key] = new Mapping(cmd.DataLength, Collect(eni, isInput: false, outOffs, cmd.DataLength));
        }
        return map;
    }

    private static List<Entry> Collect(EniConfiguration eni, bool isInput, int imageByteOffset, int dataLength)
    {
        var startBit = imageByteOffset * 8;
        var endBit = startBit + dataLength * 8;
        return eni.Variables
            .Where(v => v.IsInput == isInput && v.BitOffs >= startBit && v.BitOffs + v.BitSize <= endBit)
            .Select(v => new Entry(v, v.BitOffs - startBit))
            .ToList();
    }

    public IReadOnlyList<ResolvedVariable> ResolveInputs(EtherCatDatagram d) => Resolve(_inputs, d);

    public IReadOnlyList<ResolvedVariable> ResolveOutputs(EtherCatDatagram d) => Resolve(_outputs, d);

    private static IReadOnlyList<ResolvedVariable> Resolve(
        Dictionary<(EtherCatCommand, uint), Mapping> side, EtherCatDatagram d)
    {
        if (!side.TryGetValue((d.Command, d.RawAddress), out var mapping))
            return Array.Empty<ResolvedVariable>();
        var payload = d.Payload.Span;
        if (payload.Length < mapping.DataLength)
            return Array.Empty<ResolvedVariable>();
        var result = new List<ResolvedVariable>(mapping.Entries.Count);
        foreach (var e in mapping.Entries)
        {
            if (e.PayloadBitOffset + e.Variable.BitSize > payload.Length * 8) continue;
            result.Add(new ResolvedVariable(e.Variable,
                ProcessValueDecoder.Decode(e.Variable.DataType, e.Variable.BitSize, payload, e.PayloadBitOffset)));
        }
        return result;
    }
}
