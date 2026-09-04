using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static EtherCatDatagram Assign(int ringPosition, ushort station) =>
        new(EtherCatCommand.Apwr, 0, (0x0010u << 16) | (ushort)(0 - ringPosition), false, false, 0,
            BitConverter.GetBytes(station), 1);

    private static EtherCatDatagram DlStatus(ushort station, ushort raw) =>
        new(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station, false, false, 0,
            BitConverter.GetBytes(raw), 1);

    private static TopologyTracker TrackerWithLine(BusModel model)
    {
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, Assign(1, 1002), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, DlStatus(1001, 0x0030), FrameDirection.Returning).ToList();
        tracker.Observe(T0, DlStatus(1002, 0x0010), FrameDirection.Returning).ToList();
        return tracker;
    }

    [Fact]
    public void The_tracker_reconstructs_from_traffic_alone()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);

        var topology = TrackerWithLine(model).Current;

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
    }

    [Fact]
    public void The_first_port_read_is_not_reported_as_a_change()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();

        var events = tracker.Observe(T0, DlStatus(1001, 0x0030), FrameDirection.Returning).ToList();

        Assert.Empty(events);   // learning a port's state for the first time is not a change
    }

    [Fact]
    public void A_link_dropping_raises_one_event_naming_the_port()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        // 1001 loses its downstream link: port 1 goes from Active to Dangling (loop still open).
        var events = tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0010),
            FrameDirection.Returning).ToList();

        var changed = Assert.Single(events.OfType<MonitorEvent.TopologyChanged>());
        Assert.Equal((ushort)1001, changed.Address);
        Assert.Equal((byte)1, changed.Port);
        Assert.Equal(PortLinkState.Active, changed.OldState);
        Assert.Equal(PortLinkState.Dangling, changed.NewState);
    }

    [Fact]
    public void An_unchanged_read_raises_nothing()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        Assert.Empty(tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0030),
            FrameDirection.Returning));
    }

    [Fact]
    public void A_change_rebuilds_the_topology()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = TrackerWithLine(model);

        tracker.Observe(T0.AddSeconds(1), DlStatus(1001, 0x0010), FrameDirection.Returning).ToList();

        // 1001 no longer forwards, so 1002 has nowhere to attach.
        Assert.Equal(new ushort[] { 1002 }, tracker.Current.Unplaced);
    }

    [Fact]
    public void Auto_increment_addressed_reads_resolve_to_the_station_address()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);
        CountTheRing(tracker);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();

        // The master sent this to ring position 0; the one slave on the ring incremented its ADP
        // on the way past, so it comes back reading 1. The assignment above maps position 0 to
        // station 1001.
        tracker.Observe(T0, ReturningDlStatus(returningAdp: 1, raw: 0x0030),
            FrameDirection.Returning).ToList();

        Assert.NotNull(tracker.Current.Find(1001));
        Assert.True(tracker.Current.PortDataObserved);
    }

    /// <summary>A returning broadcast, which is how the ring is counted: every slave increments a
    /// broadcast's ADP, so the ADP that comes back is the slave count.</summary>
    private static void CountTheRing(TopologyTracker tracker, ushort slaves = 1) =>
        tracker.Observe(T0, new EtherCatDatagram(EtherCatCommand.Brd, 0,
                (0x0130u << 16) | slaves, false, false, 0, new byte[] { 0x08, 0x00 }, slaves),
            FrameDirection.Returning).ToList();

    private static EtherCatDatagram ReturningDlStatus(ushort returningAdp, ushort raw) =>
        new(EtherCatCommand.Aprd, 0, (0x0110u << 16) | returningAdp, false, false, 0,
            BitConverter.GetBytes(raw), 1);

    /// <summary>The real bringup order: the master reads DL status by auto-increment during the
    /// scan and assigns station addresses afterwards. The port state must survive the wait and land
    /// on the slave once it is named — otherwise the map stays in ring order forever on any bus
    /// whose master polls topology at startup, which is all of them.</summary>
    [Fact]
    public void Port_state_read_before_the_address_assignment_still_lands_on_the_named_slave()
    {
        var model = new BusModel();
        var tracker = new TopologyTracker(model);
        CountTheRing(tracker, slaves: 2);

        tracker.Observe(T0, ReturningDlStatus(returningAdp: 2, raw: 0x0030),
            FrameDirection.Returning).ToList();
        tracker.Observe(T0, ReturningDlStatus(returningAdp: 1, raw: 0x0010),
            FrameDirection.Returning).ToList();

        // Nothing is named yet, so nothing is drawn.
        Assert.False(tracker.Current.PortDataObserved);

        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, Assign(1, 1002), FrameDirection.Outbound).ToList();

        Assert.True(tracker.Current.PortDataObserved);
        Assert.NotEmpty(tracker.Current.Find(1001)!.Ports);
        Assert.NotEmpty(tracker.Current.Find(1002)!.Ports);
        Assert.Equal((ushort)1001, tracker.Current.Find(1002)!.ParentAddress);
    }

    [Fact]
    public void With_no_port_traffic_the_tracker_reports_ring_order_only()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Observe(T0, Assign(0, 1001), FrameDirection.Outbound).ToList();
        tracker.Observe(T0, Assign(1, 1002), FrameDirection.Outbound).ToList();

        Assert.False(tracker.Current.PortDataObserved);
        Assert.Equal((ushort)1001, tracker.Current.Find(1002)!.ParentAddress);
    }
}
