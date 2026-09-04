using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class WkcTrackerTests
{
    private static EniConfiguration Fixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static EtherCatDatagram Lrw(ushort wkc) => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, new byte[4], wkc);

    [Fact]
    public void Eni_expected_wkc_flags_mismatch_immediately()
    {
        var tracker = new WkcTracker(Fixture());
        var t = DateTimeOffset.UnixEpoch;

        Assert.Null(tracker.Observe(t, Lrw(6), FrameDirection.Returning));
        var evt = tracker.Observe(t, Lrw(5), FrameDirection.Returning);

        Assert.NotNull(evt);
        Assert.Equal((ushort)6, evt!.Expected);
        Assert.Equal((ushort)5, evt.Actual);
        Assert.Equal(EtherCatCommand.Lrw, evt.Command);
    }

    [Fact]
    public void Outbound_frames_are_not_checked()
    {
        var tracker = new WkcTracker(Fixture());
        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, Lrw(0), FrameDirection.Outbound));
    }

    [Fact]
    public void Without_eni_expected_wkc_is_learned_from_mode()
    {
        var tracker = new WkcTracker();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 25; i++)
            Assert.Null(tracker.Observe(t.AddMilliseconds(i), Lrw(3), FrameDirection.Returning));

        var evt = tracker.Observe(t.AddMilliseconds(30), Lrw(2), FrameDirection.Returning);
        Assert.NotNull(evt);
        Assert.Equal((ushort)3, evt!.Expected);
        Assert.Equal((ushort)2, evt.Actual);
    }

    [Fact]
    public void Learning_phase_reports_nothing()
    {
        var tracker = new WkcTracker();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 10; i++)
            Assert.Null(tracker.Observe(t.AddMilliseconds(i), Lrw((ushort)(i % 2)), FrameDirection.Returning));
    }

    [Fact]
    public void Physical_reads_outside_cyclic_table_are_not_checked()
    {
        var tracker = new WkcTracker(Fixture());
        var fprd = new EtherCatDatagram(EtherCatCommand.Fprd, 1, (0x0130u << 16) | 1004,
            false, false, 0, new byte[2], 0);
        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, fprd, FrameDirection.Returning));
    }
}
