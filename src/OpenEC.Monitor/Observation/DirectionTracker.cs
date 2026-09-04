using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Distinguishes outbound from returning frames on an aggregated TAP capture.
/// Primary heuristic: slaves set bit 0x02 of the source MAC's first octet on the return
/// path. Until both bit values have been observed, falls back to pairing duplicate
/// (idx, cmd, address) keys: first sighting is outbound, second is the return.</summary>
public sealed class DirectionTracker
{
    private bool _sawBitSet;
    private bool _sawBitClear;
    private readonly HashSet<(byte, EtherCatCommand, uint)> _pending = new();
    private readonly Queue<(byte, EtherCatCommand, uint)> _pendingOrder = new();

    public FrameDirection Classify(EtherCatFrame frame)
    {
        var bit = frame.Source.IsLocallyAdministered;
        if (bit) _sawBitSet = true; else _sawBitClear = true;
        if (_sawBitSet && _sawBitClear)
            return bit ? FrameDirection.Returning : FrameDirection.Outbound;
        if (frame.Datagrams.Count == 0)
            return FrameDirection.Outbound;
        var d = frame.Datagrams[0];
        var key = (d.Index, d.Command, d.RawAddress);
        if (_pending.Remove(key))
            return FrameDirection.Returning;
        _pending.Add(key);
        _pendingOrder.Enqueue(key);
        while (_pendingOrder.Count > 1024)
            _pending.Remove(_pendingOrder.Dequeue());
        return FrameDirection.Outbound;
    }

    internal int PendingBacklog => _pendingOrder.Count;
}
