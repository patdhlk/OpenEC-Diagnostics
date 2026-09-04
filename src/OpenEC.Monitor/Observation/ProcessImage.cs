using System.Collections.Concurrent;
using Dahlke.EtherCAT.Cia402;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

public sealed record VariableValue(EniVariable Variable, object Value, DateTimeOffset Timestamp)
{
    /// <summary>Human-readable CiA-402 decode when this variable is a DS402 status- or controlword.</summary>
    public string? Cia402Description => Value is ushort word
        ? Variable.Name.Contains("statusword", StringComparison.OrdinalIgnoreCase)
            ? MotionCia402.DescribeStatusword(word)
            : Variable.Name.Contains("controlword", StringComparison.OrdinalIgnoreCase)
                ? MotionCia402.DescribeControlword(word)
                : null
        : null;
}

/// <summary>Latest decoded value of every mapped process variable.</summary>
public sealed class ProcessImage
{
    private readonly ConcurrentDictionary<string, VariableValue> _current = new();
    private ProcessVariableMap? _map;

    internal ProcessImage(EniConfiguration? eni) => Rebind(eni);

    public IReadOnlyDictionary<string, VariableValue> Current => _current;

    /// <summary>Swaps the variable map when a learned configuration arrives or is refined, carrying
    /// each decoded value onto the new map's variable at the same <c>(BitOffs, BitSize, IsInput)</c>
    /// placement. Values with no counterpart there are dropped.
    ///
    /// Keys cannot simply be kept: a rebind renames variables — a synthetic `0x6000:01` becomes an
    /// ESI-derived `Channel 1.Input 1` — so old keys would linger in the watch under names the new map
    /// can never refresh. But clearing outright was worse. `RunAsync` awaits a final schema resolution
    /// after the capture loop, which forces a republish and so a rebind, and on a live session no
    /// frames follow to repopulate: the same bringup ended with 16 decoded values without an ESI
    /// directory and 0 with one. Supplying vendor files emptied the process image.
    ///
    /// Migrating by placement is right because placement is what the wire determines — the FMMU and
    /// SyncManager chain — while the name is precisely what a rebind changes. Only unambiguous
    /// placements migrate: two values sharing one placement give no basis for choosing between them,
    /// and a wrongly carried value is worse than a missing one the next frame would fill in.</summary>
    internal void Rebind(EniConfiguration? eni)
    {
        var carried = new Dictionary<(int BitOffs, int BitSize, bool IsInput), VariableValue?>();
        foreach (var value in _current.Values)
        {
            var placement = (value.Variable.BitOffs, value.Variable.BitSize, value.Variable.IsInput);
            // Null marks an ambiguous placement, so a second value at the same one disqualifies it
            // rather than overwriting the first.
            carried[placement] = carried.ContainsKey(placement) ? null : value;
        }

        _map = eni is null ? null : ProcessVariableMap.Build(eni);
        _current.Clear();
        if (eni is null) return;
        foreach (var variable in eni.Variables)
            if (carried.TryGetValue((variable.BitOffs, variable.BitSize, variable.IsInput),
                    out var value) && value is not null)
                // The timestamp stays as it was: the rebind observed nothing, so claiming a fresh
                // one would date a stale value to now.
                _current[variable.Name] = value with { Variable = variable };
    }

    internal void UpdateInputs(EtherCatDatagram d, DateTimeOffset ts)
    {
        if (_map is null) return;
        foreach (var r in _map.ResolveInputs(d))
            _current[r.Variable.Name] = new VariableValue(r.Variable, r.Value, ts);
    }

    internal void UpdateOutputs(EtherCatDatagram d, DateTimeOffset ts)
    {
        if (_map is null) return;
        foreach (var r in _map.ResolveOutputs(d))
            _current[r.Variable.Name] = new VariableValue(r.Variable, r.Value, ts);
    }
}
