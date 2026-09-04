using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyResolutionTests
{
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, active, !active, active);
        }
        return new TopologyDevice(address, ringPosition, ports, new Dictionary<byte, PortCounters>());
    }

    private static TopologyDevice Blind(ushort address, int ringPosition) =>
        new(address, ringPosition, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());

    private static EniConfiguration Eni(params (ushort Address, ushort? Parent, byte Port)[] slaves) =>
        new()
        {
            Slaves = slaves.Select(s => new EniSlave($"Slave {s.Address}", s.Address, 0, 0, 0, 0,
                null, null, s.Parent is { } parent ? new EniPreviousPort(parent, s.Port) : null)).ToList(),
            CyclicCommands = [],
            Variables = [],
        };

    [Fact]
    public void The_wire_wins_where_both_describe_the_same_edge()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 2)));   // ENI claims port 2; the wire says port 1

        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal(TopologyEdgeSource.Wire, topology.Find(1002)!.EdgeSource);
    }

    [Fact]
    public void A_disagreement_is_recorded_as_a_conflict()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 2)));

        var conflict = Assert.Single(topology.Conflicts);
        Assert.Equal((ushort)1002, conflict.Address);
        Assert.Contains("1001 port 2", conflict.Declared);
        Assert.Contains("1001 port 1", conflict.Observed);
    }

    [Fact]
    public void Agreement_produces_no_conflict()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)],
            Eni((1001, null, 0), (1002, 1001, 1)));

        Assert.Empty(topology.Conflicts);
    }

    /// <summary>The wire placed nothing, so every edge comes from the ENI — a real branched tree
    /// rather than the ring-order line the wire-only path would produce.</summary>
    [Fact]
    public void Eni_edges_place_devices_the_wire_never_described()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1), Blind(1003, 2), Blind(1004, 3)],
            Eni((1001, null, 0), (1002, 1001, 1), (1003, 1002, 1), (1004, 1002, 2)));

        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Eni, n.EdgeSource));
        Assert.False(topology.PortDataObserved);   // no port bars may be drawn
    }

    [Fact]
    public void An_eni_declaring_no_parent_for_the_first_slave_attaches_it_to_the_master()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0)], Eni((1001, null, 0)));

        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
    }

    /// <summary>An ENI edge naming a parent that is not on the bus cannot be honoured. The device
    /// falls back to ring order rather than being dropped.</summary>
    [Fact]
    public void An_eni_edge_to_an_absent_parent_falls_back_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1)],
            Eni((1001, null, 0), (1002, 1099, 1)));

        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal(TopologyEdgeSource.Inferred, topology.Find(1002)!.EdgeSource);
    }

    /// <summary>Nodes are compared element-wise rather than comparing the two BusTopology records:
    /// a record's list member compares by reference, so the topologies themselves would never be
    /// equal. The nodes DO compare correctly here, because both calls pass the same device
    /// instances and therefore share their port dictionaries.</summary>
    [Fact]
    public void A_null_eni_behaves_exactly_like_the_single_argument_overload()
    {
        var devices = new[] { Device(1001, 0, 1), Device(1002, 1) };

        var implicitly_null = TopologyReconstructor.Reconstruct(devices);
        var explicitly_null = TopologyReconstructor.Reconstruct(devices, eni: null);

        Assert.Equal(implicitly_null.Nodes, explicitly_null.Nodes);
        Assert.Equal(implicitly_null.Unplaced, explicitly_null.Unplaced);
        Assert.Equal(implicitly_null.PortDataObserved, explicitly_null.PortDataObserved);
    }
}
