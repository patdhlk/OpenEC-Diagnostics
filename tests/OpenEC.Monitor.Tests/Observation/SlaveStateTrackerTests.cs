using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class SlaveStateTrackerTests
{
    private static EtherCatDatagram Physical(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc) =>
        new(cmd, 1, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Fprd_al_status_updates_slave_and_raises_event()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1),
            FrameDirection.Returning).ToList();

        var evt = Assert.IsType<MonitorEvent.SlaveStateChanged>(Assert.Single(events));
        Assert.Equal((ushort)1004, evt.Address);
        Assert.Equal(SlaveAlState.SafeOp, evt.NewState);
        Assert.True(evt.ErrorFlag);
        Assert.True(model.TryGet(1004, out var slave));
        Assert.Equal(SlaveAlState.SafeOp, slave!.AlState);
        Assert.True(slave.ErrorFlag);
        Assert.Equal(t, slave.LastSeen);
    }

    [Fact]
    public void Unchanged_state_raises_no_event()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var d = Physical(EtherCatCommand.Fprd, 1002, 0x0130, new byte[] { 0x08, 0x00 }, 1);

        Assert.Single(tracker.Observe(DateTimeOffset.UnixEpoch, d, FrameDirection.Returning));
        Assert.Empty(tracker.Observe(DateTimeOffset.UnixEpoch.AddSeconds(1), d, FrameDirection.Returning));
    }

    [Fact]
    public void Zero_wkc_is_ignored()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var events = tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Fprd, 1002, 0x0130, new byte[] { 0x08, 0x00 }, 0),
            FrameDirection.Returning);
        Assert.Empty(events);
        Assert.False(model.TryGet(1002, out _));
    }

    [Fact]
    public void Brd_updates_bus_state()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Brd, 0, 0x0130, new byte[] { 0x08, 0x00 }, 4),
            FrameDirection.Returning).ToList();

        Assert.Equal(SlaveAlState.Op, model.BusState);
        Assert.True(model.BusStateUniform);

        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Brd, 0, 0x0130, new byte[] { 0x0C, 0x00 }, 4),
            FrameDirection.Returning).ToList(); // Op | SafeOp mixed
        Assert.False(model.BusStateUniform);
    }

    [Fact]
    public void Al_control_write_raises_state_change_requested()
    {
        var model = new BusModel();
        var tracker = new SlaveStateTracker(model);
        var events = tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Fpwr, 1004, 0x0120, new byte[] { 0x04, 0x00 }, 0),
            FrameDirection.Outbound).ToList();

        var evt = Assert.IsType<MonitorEvent.StateChangeRequested>(Assert.Single(events));
        Assert.Equal(SlaveAlState.SafeOp, evt.RequestedState);
        Assert.Equal((ushort)1004, evt.Address);
    }

    [Fact]
    public void Aprd_maps_through_eni_auto_increment_addresses()
    {
        var eni = EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var model = new BusModel();
        model.Seed(eni);
        var tracker = new SlaveStateTracker(model);

        // AutoIncAddr 65535 is 'Term 2 (EL1008)' -> PhysAddr 1002
        tracker.Observe(DateTimeOffset.UnixEpoch,
            Physical(EtherCatCommand.Aprd, 65535, 0x0130, new byte[] { 0x02, 0x00 }, 1),
            FrameDirection.Returning).ToList();

        Assert.True(model.TryGet(1002, out var slave));
        Assert.Equal(SlaveAlState.PreOp, slave!.AlState);
        Assert.Equal("Term 2 (EL1008)", slave.ConfiguredName);
    }

    [Fact]
    public void Seed_populates_slaves_from_eni()
    {
        var eni = EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var model = new BusModel();
        model.Seed(eni);

        Assert.Equal(4, model.Slaves.Count);
        Assert.True(model.TryGet(1004, out var drive));
        Assert.Equal(0x13ed6012u, drive!.ProductCode);
    }
}
