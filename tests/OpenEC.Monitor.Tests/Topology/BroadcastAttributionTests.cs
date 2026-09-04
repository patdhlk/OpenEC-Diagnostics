using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

/// <summary>A broadcast addresses every slave at once. Its ADP field counts responders rather than
/// naming one, and a broadcast read returns the bitwise OR of what every slave held — so the
/// per-slave facts decoded from it belong to no slave in particular. Taking the ADP verbatim
/// attributed them to address zero, which is BusTopology.MasterAddress: the resulting device node
/// carried the master's own address and so became its own parent, and the recursive walk over that
/// graph overflowed the stack. These pin the attribution itself, upstream of that.</summary>
public class BroadcastAttributionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>A returning broadcast read of DL status (0x0110), as a master polling for topology
    /// changes emits it. ADP is zero, as it is on the wire before any slave increments it.</summary>
    private static EtherCatDatagram BroadcastDlStatus(ushort raw) =>
        new(EtherCatCommand.Brd, 0, 0x0110u << 16, false, false, 0,
            BitConverter.GetBytes(raw), 1);

    /// <summary>A returning broadcast read of the error-counter block at 0x0300.</summary>
    private static EtherCatDatagram BroadcastErrorCounters() =>
        new(EtherCatCommand.Brd, 0, 0x0300u << 16, false, false, 0,
            new byte[16], 1);

    private static EtherCatDatagram AddressedDlStatus(ushort station, ushort raw) =>
        new(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station, false, false, 0,
            BitConverter.GetBytes(raw), 1);

    [Fact]
    public void A_broadcast_dl_status_read_never_creates_a_device_at_the_reserved_address()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);

        tracker.Observe(T0, BroadcastDlStatus(0x0030), FrameDirection.Returning).ToList();

        var topology = tracker.Current;
        Assert.DoesNotContain(topology.Nodes.Where(n => !n.IsMaster),
            n => n.Address == BusTopology.MasterAddress);
        // Unplaced is where a device at the reserved address surfaces once one exists, so an empty
        // Unplaced is what distinguishes "the broadcast was never attributed to a slave" from
        // "a phantom slave was created and then filtered out downstream".
        Assert.Empty(topology.Unplaced);
        Assert.False(topology.PortDataObserved,
            "a broadcast read describes no single slave's ports");
    }

    /// <summary>The guard is about who the fact belongs to, not about dropping port data: an
    /// addressed read of the same register still lands on its slave.</summary>
    [Fact]
    public void An_addressed_dl_status_read_is_still_attributed_to_its_slave()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        var tracker = new TopologyTracker(model);

        tracker.Observe(T0, AddressedDlStatus(1001, 0x0030), FrameDirection.Returning).ToList();

        Assert.True(tracker.Current.PortDataObserved);
        Assert.NotEmpty(tracker.Current.Find(1001)!.Ports);
    }

    /// <summary>The learner resolves slave references through the same rule and must drop the same
    /// facts, or a learned configuration grows a phantom slave at address zero — which then reaches
    /// the map through the very ENI the learner publishes.</summary>
    [Fact]
    public void The_learner_does_not_learn_a_slave_from_a_broadcast_dl_status_read()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, BroadcastDlStatus(0x0030), FrameDirection.Returning);

        Assert.DoesNotContain(bus.Slaves, s => s.StationAddress == BusTopology.MasterAddress);
    }

    /// <summary>The error-counter block is the other per-slave register read a master broadcasts.
    /// The learner is where a broadcast of it is observable: the topology tracker keeps counters in
    /// a map it never derives its device list from, so that path cannot show the difference.</summary>
    [Fact]
    public void The_learner_does_not_learn_a_slave_from_a_broadcast_error_counter_read()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, BroadcastErrorCounters(), FrameDirection.Returning);

        Assert.DoesNotContain(bus.Slaves, s => s.StationAddress == BusTopology.MasterAddress);
    }
}
