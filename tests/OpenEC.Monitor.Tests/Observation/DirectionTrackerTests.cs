using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class DirectionTrackerTests
{
    private static EtherCatFrame Parse(byte[] raw) =>
        ((FrameDecodeResult.Success)EtherCatFrameParser.Parse(raw)).Frame;

    private static byte[] Cycle(byte idx, bool returning)
    {
        var b = new EtherCatFrameBuilder();
        if (returning) b.AsReturning();
        return b.AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0, 0, 0, 0 },
            (ushort)(returning ? 6 : 0)).Build();
    }

    [Fact]
    public void Mac_bit_classifies_once_both_values_seen()
    {
        var tracker = new DirectionTracker();
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(1, returning: false))));
        Assert.Equal(FrameDirection.Returning, tracker.Classify(Parse(Cycle(1, returning: true))));
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(2, returning: false))));
        Assert.Equal(FrameDirection.Returning, tracker.Classify(Parse(Cycle(2, returning: true))));
    }

    [Fact]
    public void Pairing_fallback_when_mac_bit_never_varies()
    {
        var tracker = new DirectionTracker();
        // All frames outbound-bit-clear (e.g. a tap that strips the bit): pair duplicates.
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(1, returning: false))));
        var second = tracker.Classify(Parse(Cycle(1, returning: false)));
        Assert.Equal(FrameDirection.Returning, second);
        Assert.Equal(FrameDirection.Outbound, tracker.Classify(Parse(Cycle(2, returning: false))));
    }

    [Fact]
    public void Pairing_fallback_backlog_bounded_at_1024()
    {
        var tracker = new DirectionTracker();
        // Run ~5000 matched outbound/returning pairs through pairing-fallback mode
        // with constant MAC bit (all outbound-bit-clear).
        for (byte idx = 0; idx < 20; idx++)
        {
            // First sighting: outbound
            var outbound = tracker.Classify(Parse(Cycle(idx, returning: false)));
            Assert.Equal(FrameDirection.Outbound, outbound);
            // Second sighting (same key): returning
            var returning = tracker.Classify(Parse(Cycle(idx, returning: false)));
            Assert.Equal(FrameDirection.Returning, returning);
        }
        // Run many more cycles with index wraparound (256 indices × ~20 cycles each)
        for (int cycle = 0; cycle < 250; cycle++)
        {
            for (byte idx = 0; idx < 255; idx++)
            {
                var outbound = tracker.Classify(Parse(Cycle(idx, returning: false)));
                Assert.Equal(FrameDirection.Outbound, outbound);
                var returning = tracker.Classify(Parse(Cycle(idx, returning: false)));
                Assert.Equal(FrameDirection.Returning, returning);
            }
        }
        // Backlog should never exceed 1024
        Assert.True(tracker.PendingBacklog <= 1024, $"PendingBacklog {tracker.PendingBacklog} exceeds 1024");
    }
}
