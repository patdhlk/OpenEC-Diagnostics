using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

/// <summary>The stale-process-data check: a slave in OP whose input bytes stop changing.
///
/// It is the only signal that catches an application hung behind a healthy EtherCAT chip — the
/// device keeps answering, keeps its working counter right, keeps its error counters at zero and
/// sits in OP, so everything the protocol measures says it is fine and only the data gives it away.
/// These pin both halves of that: that it reports a genuine stall, and that it stays quiet for the
/// cases a naive "hasn't changed" check would shout about.</summary>
public class ProcessDataActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const uint ImageStart = 0x01000000;

    /// <summary>An FMMU register block, laid out as ETG.1000.4 defines it and as a master writes
    /// it: logical start, length, bit range, physical start, type, activate.</summary>
    private static EtherCatDatagram FmmuWrite(ushort station, uint logicalStart, ushort length,
        ushort physicalStart = 0x1100, byte type = 1, byte activate = 1)
    {
        var block = new byte[16];
        BitConverter.GetBytes(logicalStart).CopyTo(block, 0);
        BitConverter.GetBytes(length).CopyTo(block, 4);
        block[6] = 0;
        block[7] = 7;
        BitConverter.GetBytes(physicalStart).CopyTo(block, 8);
        block[10] = 0;
        block[11] = type;        // 1 = inputs
        block[12] = activate;
        return new EtherCatDatagram(EtherCatCommand.Fpwr, 0, (0x0600u << 16) | station,
            false, false, 0, block, 0);
    }

    /// <summary>One returning cyclic read of the whole process image.</summary>
    private static EtherCatDatagram Cyclic(byte[] image) =>
        new(EtherCatCommand.Lrd, 0, ImageStart, false, false, 0, image, 1);

    private static BusModel BusWith(ushort station, SlaveAlState state)
    {
        var model = new BusModel();
        model.GetOrAdd(station).AlState = state;
        model.BusState = state;
        model.BusStateUniform = true;
        return model;
    }

    /// <summary>Feeds `seconds` of unchanging image at 10 ms and returns everything raised.</summary>
    private static List<MonitorEvent> Hold(ProcessDataActivityTracker tracker, byte[] image,
        double seconds, double from = 0)
    {
        var events = new List<MonitorEvent>();
        for (var ms = from * 1000; ms < (from + seconds) * 1000; ms += 10)
            events.AddRange(tracker.Observe(T0.AddMilliseconds(ms), Cyclic(image),
                FrameDirection.Returning));
        return events;
    }

    private static ProcessDataActivityTracker TrackerFor(BusModel model, ushort station,
        TimeSpan? staleAfter, ushort length = 4)
    {
        var tracker = new ProcessDataActivityTracker(model, staleAfter);
        tracker.Observe(T0, FmmuWrite(station, ImageStart, length), FrameDirection.Outbound).ToList();
        return tracker;
    }

    [Fact]
    public void A_slave_in_op_whose_inputs_stop_changing_is_reported()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, TimeSpan.FromSeconds(60));

        var events = Hold(tracker, [1, 2, 3, 4], seconds: 61);

        var stalled = Assert.Single(events.OfType<MonitorEvent.ProcessDataStalled>());
        Assert.Equal((ushort)1001, stalled.Address);
        Assert.True(stalled.StaleFor >= TimeSpan.FromSeconds(60));
    }

    /// <summary>Reported once, not once per cycle. At a 10 ms cycle the difference is one message
    /// versus six thousand a minute.</summary>
    [Fact]
    public void A_standing_stall_is_reported_once_rather_than_every_cycle()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, TimeSpan.FromSeconds(10));

        var events = Hold(tracker, [1, 2, 3, 4], seconds: 60);

        Assert.Single(events.OfType<MonitorEvent.ProcessDataStalled>());
    }

    [Fact]
    public void Nothing_is_reported_before_the_threshold()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, TimeSpan.FromSeconds(60));

        var events = Hold(tracker, [1, 2, 3, 4], seconds: 59);

        Assert.Empty(events.OfType<MonitorEvent.ProcessDataStalled>());
        Assert.False(Assert.Single(tracker.Snapshot()).IsStale);
    }

    /// <summary>A slave that is not in OP is not expected to be exchanging process data, so its
    /// unchanging image says nothing about it.</summary>
    [Theory]
    [InlineData(SlaveAlState.PreOp)]
    [InlineData(SlaveAlState.SafeOp)]
    [InlineData(SlaveAlState.Init)]
    public void A_slave_that_is_not_in_op_is_never_reported(SlaveAlState state)
    {
        var tracker = TrackerFor(BusWith(1001, state), 1001, TimeSpan.FromSeconds(10));

        Assert.Empty(Hold(tracker, [1, 2, 3, 4], seconds: 60)
            .OfType<MonitorEvent.ProcessDataStalled>());
    }

    /// <summary>Per-slave AL state comes from addressed reads many masters stop doing once the bus
    /// is running, so a mid-run attach has none. A uniform bus-wide OP has to stand in, or the check
    /// goes silent on exactly the session a diagnostic tool is for.</summary>
    [Fact]
    public void A_uniform_bus_wide_op_stands_in_for_unknown_per_slave_state()
    {
        var model = new BusModel { BusState = SlaveAlState.Op, BusStateUniform = true };
        model.GetOrAdd(1001);   // seen on the wire, but no addressed AL-status read ever arrived
        Assert.Equal(SlaveAlState.Unknown, model.Slaves.Single().AlState);
        var tracker = TrackerFor(model, 1001, TimeSpan.FromSeconds(10));

        Assert.Single(Hold(tracker, [1, 2, 3, 4], seconds: 30)
            .OfType<MonitorEvent.ProcessDataStalled>());
    }

    [Fact]
    public void A_non_uniform_bus_does_not_stand_in_for_unknown_per_slave_state()
    {
        var model = new BusModel { BusState = SlaveAlState.Op, BusStateUniform = false };
        model.GetOrAdd(1001);
        var tracker = TrackerFor(model, 1001, TimeSpan.FromSeconds(10));

        Assert.Empty(Hold(tracker, [1, 2, 3, 4], seconds: 30)
            .OfType<MonitorEvent.ProcessDataStalled>());
    }

    [Fact]
    public void Data_moving_again_resumes_and_can_stall_a_second_time()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, TimeSpan.FromSeconds(10));

        var events = Hold(tracker, [1, 2, 3, 4], seconds: 20);
        events.AddRange(Hold(tracker, [9, 9, 9, 9], seconds: 20, from: 20));
        events.AddRange(Hold(tracker, [9, 9, 9, 9], seconds: 20, from: 40));

        Assert.Equal(2, events.OfType<MonitorEvent.ProcessDataStalled>().Count());
        Assert.Equal((ushort)1001, Assert.Single(events.OfType<MonitorEvent.ProcessDataResumed>()).Address);
        Assert.True(tracker.Snapshot().Single().IsStale);   // stalled again at the end
    }

    /// <summary>Every TwinCAT slave also maps a byte of ESC register space into the image through an
    /// enabled input FMMU. It is not process data, it is a status mirror, and watching it for change
    /// would report a stall for a device whose real data is moving perfectly well.</summary>
    [Fact]
    public void A_register_mapped_fmmu_is_not_watched()
    {
        var tracker = new ProcessDataActivityTracker(BusWith(1001, SlaveAlState.Op),
            TimeSpan.FromSeconds(10));
        tracker.Observe(T0, FmmuWrite(1001, ImageStart, 1, physicalStart: 0x080D),
            FrameDirection.Outbound).ToList();

        Assert.Empty(Hold(tracker, [1, 2, 3, 4], seconds: 30)
            .OfType<MonitorEvent.ProcessDataStalled>());
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void An_output_fmmu_is_not_watched()
    {
        var tracker = new ProcessDataActivityTracker(BusWith(1001, SlaveAlState.Op),
            TimeSpan.FromSeconds(10));
        tracker.Observe(T0, FmmuWrite(1001, ImageStart, 4, type: 2), FrameDirection.Outbound).ToList();

        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>Disabling the verdict must not disable the observation: the numbers stay available
    /// so a reader can still see that nothing is moving.</summary>
    [Fact]
    public void A_null_threshold_reports_nothing_but_still_counts()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, staleAfter: null);

        var events = Hold(tracker, [1, 2, 3, 4], seconds: 120);

        Assert.Empty(events.OfType<MonitorEvent.ProcessDataStalled>());
        var activity = Assert.Single(tracker.Snapshot());
        Assert.Equal(0, activity.Changes);
        Assert.True(activity.StaleFor >= TimeSpan.FromSeconds(119));
        Assert.False(activity.IsStale);
    }

    /// <summary>Two slaves sharing one image: only the one that stopped is named.</summary>
    [Fact]
    public void Only_the_slave_whose_window_stopped_is_reported()
    {
        var model = BusWith(1001, SlaveAlState.Op);
        model.GetOrAdd(1002).AlState = SlaveAlState.Op;
        var tracker = new ProcessDataActivityTracker(model, TimeSpan.FromSeconds(10));
        tracker.Observe(T0, FmmuWrite(1001, ImageStart, 2), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, FmmuWrite(1002, ImageStart + 2, 2), FrameDirection.Outbound).ToList();

        var events = new List<MonitorEvent>();
        for (var ms = 0; ms < 30_000; ms += 10)
        {
            // 1001's two bytes count up; 1002's never move.
            var image = new byte[] { (byte)(ms / 10 & 0xFF), (byte)(ms / 2550 & 0xFF), 7, 7 };
            events.AddRange(tracker.Observe(T0.AddMilliseconds(ms), Cyclic(image),
                FrameDirection.Returning));
        }

        Assert.Equal((ushort)1002,
            Assert.Single(events.OfType<MonitorEvent.ProcessDataStalled>()).Address);
        Assert.Equal([(ushort)1002], tracker.StaleSlaves());
    }

    /// <summary>A re-bringup can move a slave's window. Bytes measured against the old placement
    /// describe someone else now, so the history goes rather than being carried across.</summary>
    [Fact]
    public void A_window_moving_resets_what_was_known_about_it()
    {
        var tracker = TrackerFor(BusWith(1001, SlaveAlState.Op), 1001, TimeSpan.FromSeconds(10));
        Hold(tracker, [1, 2, 3, 4], seconds: 30);
        Assert.True(tracker.Snapshot().Single().IsStale);

        tracker.Observe(T0.AddSeconds(30), FmmuWrite(1001, ImageStart + 16, 4),
            FrameDirection.Outbound).ToList();

        var activity = Assert.Single(tracker.Snapshot());
        Assert.False(activity.IsStale);
        Assert.Equal(0, activity.Samples);
        Assert.Equal(ImageStart + 16u, activity.LogicalStart);
    }
}
