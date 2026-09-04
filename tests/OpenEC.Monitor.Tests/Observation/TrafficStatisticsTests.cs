using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class TrafficStatisticsTests
{
    [Fact]
    public void Counts_frames_datagrams_and_rates()
    {
        var stats = new TrafficStatistics();
        var t0 = DateTimeOffset.UnixEpoch;
        stats.CountFrame(t0);
        stats.CountDatagram(EtherCatCommand.Lrw);
        stats.CountFrame(t0.AddSeconds(1));
        stats.CountDatagram(EtherCatCommand.Lrw);
        stats.CountNonEtherCat();
        stats.CountMalformed();

        Assert.Equal(4, stats.TotalFrames);
        Assert.Equal(2, stats.EtherCatFrames);
        Assert.Equal(1, stats.NonEtherCatFrames);
        Assert.Equal(1, stats.MalformedFrames);
        Assert.Equal(2, stats.DatagramsByCommand[EtherCatCommand.Lrw]);
        Assert.Equal(2.0, stats.FramesPerSecond!.Value, precision: 3);
    }

    [Fact]
    public void Splits_frame_rates_by_direction_and_idx_pool()
    {
        // Outbound frames split cyclic vs queued on the first datagram's idx pool
        // (TwinCAT: cyclic frames use low idx values, queued frames rotate 0x80..0xFF).
        var stats = new TrafficStatistics();
        var t0 = DateTimeOffset.UnixEpoch;
        stats.CountFrame(t0);
        stats.CountDirection(FrameDirection.Outbound, firstIndex: 0);    // cyclic
        stats.CountFrame(t0.AddMilliseconds(300));
        stats.CountDirection(FrameDirection.Returning, firstIndex: 0);
        stats.CountFrame(t0.AddMilliseconds(600));
        stats.CountDirection(FrameDirection.Outbound, firstIndex: 0x90); // queued
        stats.CountFrame(t0.AddSeconds(1));
        stats.CountDirection(FrameDirection.Returning, firstIndex: 0x90);

        Assert.Equal(2, stats.OutboundFrames);
        Assert.Equal(2, stats.ReturningFrames);
        Assert.Equal(1, stats.OutboundCyclicFrames);
        Assert.Equal(1, stats.OutboundQueuedFrames);
        Assert.Equal(2.0, stats.OutboundFramesPerSecond!.Value, precision: 3);
        Assert.Equal(2.0, stats.ReturningFramesPerSecond!.Value, precision: 3);
        Assert.Equal(1.0, stats.OutboundCyclicFramesPerSecond!.Value, precision: 3);
        Assert.Equal(1.0, stats.OutboundQueuedFramesPerSecond!.Value, precision: 3);
    }

    [Fact]
    public void Detects_index_gaps_as_suspected_loss()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(1);
        stats.ObserveOutboundIndex(2);
        stats.ObserveOutboundIndex(5); // gap of 2
        stats.ObserveOutboundIndex(6);

        Assert.Equal(2, stats.SuspectedLostFrames);
    }

    [Fact]
    public void Index_wraparound_is_not_loss()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(255);
        stats.ObserveOutboundIndex(0);
        Assert.Equal(0, stats.SuspectedLostFrames);
    }

    [Fact]
    public void Fixed_idx0_cyclic_frames_interleaved_with_high_acyclic_pool_are_not_loss()
    {
        // TwinCAT sends cyclic frames with a fixed idx 0 and acyclic frames from a rotating
        // pool. When the rotating idx is high (>=193), the wrap back to the next cyclic
        // idx-0 frame lands in the 2..63 gap window and must not count as loss (observed
        // on a real capture: 2925 phantom lost frames on a healthy bus).
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(0);
        stats.ObserveOutboundIndex(209);
        stats.ObserveOutboundIndex(0);
        stats.ObserveOutboundIndex(210);
        stats.ObserveOutboundIndex(0);

        Assert.Equal(0, stats.SuspectedLostFrames);
    }

    [Fact]
    public void Large_jumps_are_ignored_as_multiplexed_sequences()
    {
        var stats = new TrafficStatistics();
        stats.ObserveOutboundIndex(1);
        stats.ObserveOutboundIndex(128); // different idx pool, not loss
        Assert.Equal(0, stats.SuspectedLostFrames);
    }
}
