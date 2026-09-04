using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Passive ring-loss detection: an outbound frame whose (idx, command, address)
/// key is re-sent while the previous send is still unanswered counts as lost on the ring —
/// the passive equivalent of TwinCAT's "Lost Frames" counter. Auto-increment/broadcast
/// commands mutate ADP on the return path, so physical keys use only the ADO half.
/// Caveat: a returning frame that the capture itself dropped is indistinguishable from a
/// real ring loss, so this can overcount when the TAP/BPF loses frames. Eviction of stale
/// keys can cause missed (never false) detections.</summary>
public sealed class RingLossTracker
{
    private readonly HashSet<(byte, EtherCatCommand, uint)> _pending = new();
    private readonly Queue<(byte, EtherCatCommand, uint)> _order = new();

    /// <summary>Returns the number of newly detected ring losses for this frame.</summary>
    public int Observe(EtherCatFrame frame, FrameDirection direction)
    {
        if (frame.Datagrams.Count == 0) return 0;
        var d = frame.Datagrams[0];
        var key = (d.Index, d.Command, d.IsLogical ? d.RawAddress : d.Ado);
        if (direction == FrameDirection.Returning)
        {
            _pending.Remove(key);
            return 0;
        }
        var lost = _pending.Remove(key) ? 1 : 0;
        _pending.Add(key);
        _order.Enqueue(key);
        while (_order.Count > 1024)
            _pending.Remove(_order.Dequeue());
        return lost;
    }
}
