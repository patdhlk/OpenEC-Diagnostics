using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>How many slaves are on the ring, counted the way the protocol counts them.
///
/// Every slave increments a broadcast datagram's ADP as it forwards the frame, so the ADP on the
/// returning copy is exactly the number of slaves that saw it. This is how a master sizes the ring,
/// and a passive observer can read the same number off the same datagram without asking anyone.
///
/// It is needed because auto-increment ADPs come back offset by this count, and the whole INIT scan
/// — DL status, SII identity, error counters — is auto-increment and only readable on the return
/// leg. Without the ring length those facts cannot be attributed to a position at all.
///
/// Not thread-safe: owners hold their own instance and drive it from the same loop that drives
/// their other trackers.</summary>
public sealed class RingLengthTracker
{
    /// <summary>The most recent count, or null until a broadcast has returned. Deliberately the
    /// latest rather than the maximum: a slave leaving the ring shortens it, and a stale longer
    /// count would then mis-address every scan fact that follows.</summary>
    public ushort? Length { get; private set; }

    public void Observe(EtherCatDatagram d, FrameDirection direction)
    {
        if (direction != FrameDirection.Returning) return;
        if (d.Command is not (EtherCatCommand.Brd or EtherCatCommand.Bwr or EtherCatCommand.Brw))
            return;
        // A zero ADP on a returning broadcast means no slave incremented it — an empty or
        // unresponsive ring. That is not a count of zero to be believed, it is an absence.
        if (d.Adp == 0) return;
        Length = d.Adp;
    }

    /// <summary>Restates a reference the way the master sent it, or null when the ring length is
    /// not known yet and a returning auto-increment ADP therefore cannot be interpreted. Callers
    /// drop what they cannot place rather than attributing it to a guess.</summary>
    public SlaveRef? Normalize(SlaveRef reference)
    {
        if (!reference.IsAutoIncrement || !reference.IsReturning)
            return reference.Normalized(ringLength: 0);   // nothing to undo; only clears the flag
        return Length is { } length ? reference.Normalized(length) : null;
    }
}
