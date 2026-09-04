using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Checks returning cyclic datagrams' working counters against the ENI's
/// expected values, or against a learned mode when no ENI is loaded.</summary>
public sealed class WkcTracker
{
    private const int LearnThreshold = 20;
    private const int LearnCap = 1000;

    private Dictionary<(EtherCatCommand, uint), ushort> _expectedFromEni = new();
    private readonly Dictionary<(EtherCatCommand, uint), Dictionary<ushort, int>> _observed = new();

    public WkcTracker(EniConfiguration? eni = null) => Rebind(eni);

    /// <summary>Replaces the ENI-derived expectations. Built into a local and assigned as one
    /// reference so a concurrent reader never sees a half-populated table.
    ///
    /// The observed-mode histogram in <c>_observed</c> is deliberately NOT cleared: it is evidence
    /// gathered from the wire, independent of which configuration is loaded, and discarding it
    /// would restart the 20-frame learning threshold on every rebind — so a live bus whose
    /// configuration is still being refined would never converge on mismatch detection.</summary>
    public void Rebind(EniConfiguration? eni)
    {
        var expected = new Dictionary<(EtherCatCommand, uint), ushort>();
        foreach (var cmd in eni?.CyclicCommands ?? Enumerable.Empty<EniCyclicCommand>())
            expected[(cmd.Command, cmd.RawAddress)] = (ushort)cmd.ExpectedWkc;
        _expectedFromEni = expected;
    }

    public MonitorEvent.WkcMismatchDetected? Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning) return null;
        var key = (d.Command, d.RawAddress);
        var isCyclicShape = d.Command is EtherCatCommand.Brd or EtherCatCommand.Lrd
            or EtherCatCommand.Lwr or EtherCatCommand.Lrw;
        if (!isCyclicShape && !_expectedFromEni.ContainsKey(key)) return null;

        if (_expectedFromEni.TryGetValue(key, out var expected))
            return d.WorkingCounter == expected
                ? null
                : new MonitorEvent.WkcMismatchDetected(ts, d.Command, d.RawAddress, expected, d.WorkingCounter);

        if (!_observed.TryGetValue(key, out var counts))
            _observed[key] = counts = new Dictionary<ushort, int>();
        var total = counts.Values.Sum();
        if (total >= LearnThreshold)
        {
            var mode = counts.MaxBy(kv => kv.Value).Key;
            if (d.WorkingCounter != mode)
                return new MonitorEvent.WkcMismatchDetected(ts, d.Command, d.RawAddress, mode, d.WorkingCounter);
        }
        if (total < LearnCap)
            counts[d.WorkingCounter] = counts.GetValueOrDefault(d.WorkingCounter) + 1;
        return null;
    }
}
