using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyReconstructorTests
{
    /// <summary>A device whose given ports are active and whose remaining ports are unused.
    /// Port 0 is always included as the upstream link.</summary>
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, HasLink: active, LoopClosed: !active,
                SignalDetected: active);
        }
        return new TopologyDevice(address, ringPosition, ports,
            new Dictionary<byte, PortCounters>());
    }

    /// <summary>A device with no port data at all.</summary>
    private static TopologyDevice Blind(ushort address, int ringPosition) =>
        new(address, ringPosition, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());

    [Fact]
    public void A_straight_line_chains_every_device_to_its_predecessor()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1),
            Device(1002, 1, 1),
            Device(1003, 2),          // line end: only port 0 active
        ]);

        Assert.True(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Wire, n.EdgeSource));
    }

    [Fact]
    public void The_master_is_the_root_and_the_first_device_hangs_off_it()
    {
        var topology = TopologyReconstructor.Reconstruct([Device(1001, 0)]);

        var master = Assert.Single(topology.Nodes, n => n.IsMaster);
        Assert.Null(master.ParentAddress);
        Assert.Equal(-1, master.RingPosition);
        Assert.Equal((byte)0, topology.Find(1001)!.ParentPort);
        Assert.Equal((byte)0, topology.Find(1001)!.OwnPort);
    }

    /// <summary>1001 opens a branch on ports 1 and 2. Forwarding order 0 → 3 → 1 → 2 means the
    /// branch out of port 1 is walked first, so 1002 lands there and 1003 — arriving after 1002's
    /// line has ended — lands on port 2.</summary>
    [Fact]
    public void A_branch_point_places_its_second_subtree_on_its_next_port()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1, 2),
            Device(1002, 1),          // line end, closes the port 1 branch
            Device(1003, 2),          // line end, takes port 2
        ]);

        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal((ushort)1001, topology.Find(1003)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1003)!.ParentPort);
        Assert.Equal(2, topology.ChildrenOf(1001).Count());
    }

    /// <summary>Port 3 precedes ports 1 and 2 in the forwarding order, so a device with 3 and 1
    /// open branches out of 3 first.</summary>
    [Fact]
    public void Port_three_is_walked_before_port_one()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 3, 1),
            Device(1002, 1),
            Device(1003, 2),
        ]);

        Assert.Equal((byte)3, topology.Find(1002)!.ParentPort);
        Assert.Equal((byte)1, topology.Find(1003)!.ParentPort);
    }

    /// <summary>The reference image's shape: a main line, a branch that itself runs several
    /// devices deep, and a further branch off a device inside it.</summary>
    [Fact]
    public void Nested_branches_reconstruct_to_the_expected_parents()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0, 1),          // main line
            Device(1002, 1, 1, 2),       // junction: two branches
            Device(1003, 2, 1),          // first branch, continues
            Device(1004, 3),             // first branch ends
            Device(1005, 4),             // second branch of 1002
        ]);

        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1003, topology.Find(1004)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1005)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1003)!.ParentPort);
        Assert.Equal((byte)2, topology.Find(1005)!.ParentPort);
    }

    /// <summary>More line ends than branches opened: the port states contradict each other. The
    /// devices placed so far stand; the remainder is reported unplaced rather than guessed.</summary>
    [Fact]
    public void Contradictory_port_data_leaves_the_remainder_unplaced()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1001, 0),          // claims to end the line immediately
            Device(1002, 1),          // nowhere left to attach
            Device(1003, 2),
        ]);

        Assert.Equal((ushort)1001, Assert.Single(topology.Nodes, n => !n.IsMaster).Address);
        Assert.Equal(new ushort[] { 1002, 1003 }, topology.Unplaced);
    }

    [Fact]
    public void No_port_data_at_all_degrades_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Blind(1001, 0),
            Blind(1002, 1),
            Blind(1003, 2),
        ]);

        Assert.False(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Inferred, n.EdgeSource));
    }

    [Fact]
    public void Devices_without_a_ring_position_sort_last_by_address()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(1005, -1, 1),
            Device(1001, 0, 1),
            Device(1004, -1),
        ]);

        // Address ordering puts the -1 group as [1004, 1005]; 1004 (lower address) takes 1001's
        // single downstream port and dead-ends, so 1005 has nowhere to attach and is unplaced.
        Assert.Equal([1001, 1004],
            topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address));
        Assert.Equal(new ushort[] { 1005 }, topology.Unplaced);
    }

    [Fact]
    public void An_empty_device_list_yields_a_master_only_topology()
    {
        var topology = TopologyReconstructor.Reconstruct([]);

        Assert.True(Assert.Single(topology.Nodes).IsMaster);
        Assert.False(topology.PortDataObserved);
    }

    [Fact]
    public void Every_device_is_placed_at_most_once_so_a_cycle_is_impossible()
    {
        // Every device claims three open downstream ports: without the ring-order bound this
        // would loop forever or attach a device twice.
        var topology = TopologyReconstructor.Reconstruct(
            Enumerable.Range(0, 20)
                .Select(i => Device((ushort)(1001 + i), i, 1, 2, 3))
                .ToList());

        Assert.Equal(20, topology.Nodes.Count(n => !n.IsMaster));
        Assert.Equal(20, topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address).Distinct().Count());
    }

    /// <summary>Station address zero is BusTopology.MasterAddress. A device carrying it — an
    /// unconfigured slave, or a broadcast datagram whose ADP named no one — must never become a
    /// node, because that node would carry the master's own address and so be its own parent.
    /// Consumers walk the parent graph recursively, so that is a stack overflow, not a wrong
    /// drawing. It is reported as unplaced rather than dropped.</summary>
    [Fact]
    public void A_device_at_the_masters_reserved_address_is_reported_unplaced_never_placed()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(BusTopology.MasterAddress, 0, 1),
            Device(1001, 1, 1),
            Device(1002, 2),
        ]);

        Assert.Single(topology.Nodes, n => n.IsMaster);
        Assert.DoesNotContain(topology.Nodes.Where(n => !n.IsMaster),
            n => n.Address == BusTopology.MasterAddress);
        Assert.Contains(BusTopology.MasterAddress, topology.Unplaced);
        Assert.Equal([1001, 1002], topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address));
    }

    /// <summary>The same guard on the path taken when no device reports port state at all, where
    /// each device is chained to its ring-order predecessor.</summary>
    [Fact]
    public void A_blind_device_at_the_reserved_address_is_reported_unplaced_never_placed()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Blind(BusTopology.MasterAddress, 0),
            Blind(1001, 1),
        ]);

        Assert.DoesNotContain(topology.Nodes.Where(n => !n.IsMaster),
            n => n.Address == BusTopology.MasterAddress);
        Assert.Contains(BusTopology.MasterAddress, topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
    }

    /// <summary>A device whose only appearance is the reserved address leaves a master-only map,
    /// still reporting what it could not place.</summary>
    [Fact]
    public void A_bus_of_nothing_but_the_reserved_address_yields_a_master_only_topology()
    {
        var topology = TopologyReconstructor.Reconstruct([Blind(BusTopology.MasterAddress, 0)]);

        Assert.True(Assert.Single(topology.Nodes).IsMaster);
        Assert.Equal(new[] { BusTopology.MasterAddress }, topology.Unplaced);
    }

    /// <summary>No reconstruction, on any path and from any input, may hand its caller a parent
    /// graph that is not a tree. This is the invariant the layout engines recurse on.</summary>
    [Fact]
    public void Every_reconstructed_node_reaches_the_master_without_revisiting_a_node()
    {
        var topology = TopologyReconstructor.Reconstruct([
            Device(BusTopology.MasterAddress, 0, 1, 2),
            Device(1001, 1, 1, 2),
            Device(1002, 2, 1),
            Blind(1003, 3),
            Device(1004, -1),
        ]);

        // Asserted first, and separately: Find is by address, so a duplicate address — or a device
        // carrying the master's own — makes it ambiguous and would let the walk below terminate at
        // the wrong node and pass without ever seeing the cycle.
        var slaves = topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address).ToList();
        Assert.Equal(slaves.Count, slaves.Distinct().Count());
        Assert.DoesNotContain(BusTopology.MasterAddress, slaves);

        foreach (var node in topology.Nodes.Where(n => !n.IsMaster))
        {
            var seen = new HashSet<ushort>();
            var current = node;
            while (current is { IsMaster: false })
            {
                Assert.True(seen.Add(current.Address),
                    $"cycle in the parent graph, reached again at {current.Address}");
                current = topology.Find(current.ParentAddress!.Value);
            }
            Assert.NotNull(current);   // the walk ended at the master, not at a dangling parent
        }
    }
}
