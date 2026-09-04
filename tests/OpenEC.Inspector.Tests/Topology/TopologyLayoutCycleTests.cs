using OpenEC.Inspector.Topology;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

/// <summary>The layout engine walks the parent graph recursively, so a cycle in it is not a wrong
/// picture — it is an unbounded recursion, and the resulting stack overflow cannot be caught. It
/// aborts the process from inside the 4 Hz refresh with no exception and no message. The topologies
/// here are hand-built rather than reconstructed, because TopologyReconstructor is what normally
/// guarantees a tree: these pin the layout engine's own behaviour if that guarantee ever slips.
///
/// Every test here asserts only that the call returns. Before the guard existed each one killed the
/// whole test process rather than failing, which is exactly why the defect reached a shipped build
/// with a green suite.</summary>
public class TopologyLayoutCycleTests
{
    private static TopologyNode Node(ushort address, ushort? parent, int ringPosition) =>
        new(address, ringPosition, parent, ParentPort: 1, OwnPort: 0,
            new Dictionary<byte, PortState>(), new Dictionary<byte, PortCounters>(),
            TopologyEdgeSource.Inferred);

    /// <summary>BusTopology.MasterNode is internal to the SDK, so the map's root is rebuilt here
    /// to its documented shape: the reserved address, no parent, ring position -1.</summary>
    private static TopologyNode Master() =>
        new(BusTopology.MasterAddress, -1, null, null, OwnPort: 0,
            new Dictionary<byte, PortState>(), new Dictionary<byte, PortCounters>(),
            TopologyEdgeSource.Wire);

    private static BusTopology Topology(params TopologyNode[] slaves) =>
        new([Master(), .. slaves], [], [], PortDataObserved: false);

    /// <summary>The exact shape a device at the master's reserved address produced on real
    /// hardware: a node carrying address 0, so ChildrenOf(0) returns the node itself, forever.</summary>
    [Fact]
    public void A_node_that_is_its_own_parent_does_not_hang_the_layout()
    {
        var layout = TopologyLayoutEngine.Layout(Topology(
            Node(BusTopology.MasterAddress, BusTopology.MasterAddress, 0),
            Node(1001, BusTopology.MasterAddress, 1)));

        Assert.Contains(layout.Boxes, b => b.Address == 1001);
    }

    /// <summary>Two nodes sharing an address, the second parented under the first's own child. The
    /// walk reaches it from the master and then loops between the two forever.</summary>
    [Fact]
    public void A_cycle_reachable_from_the_master_places_each_address_once()
    {
        var layout = TopologyLayoutEngine.Layout(Topology(
            Node(1001, BusTopology.MasterAddress, 0),
            Node(1002, 1001, 1),
            Node(1001, 1002, 2)));

        var addresses = layout.Boxes.Select(b => b.Address).ToList();
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
    }

    /// <summary>A cycle with no edge from the master is unreachable, so nothing in it is drawn —
    /// but the call must still return, and the rest of the bus must still be laid out.</summary>
    [Fact]
    public void A_detached_cycle_does_not_hang_the_layout()
    {
        var layout = TopologyLayoutEngine.Layout(Topology(
            Node(1001, BusTopology.MasterAddress, 0),
            Node(1002, 1003, 1),
            Node(1003, 1002, 2)));

        Assert.Contains(layout.Boxes, b => b.Address == BusTopology.MasterAddress);
        Assert.Contains(layout.Boxes, b => b.Address == 1001);
    }

    /// <summary>The guard must not cost the ordinary case: a plain line still lays out in full.</summary>
    [Fact]
    public void An_acyclic_bus_is_unaffected_by_the_guard()
    {
        var layout = TopologyLayoutEngine.Layout(Topology(
            Node(1001, BusTopology.MasterAddress, 0),
            Node(1002, 1001, 1),
            Node(1003, 1002, 2)));

        Assert.Equal(new ushort[] { BusTopology.MasterAddress, 1001, 1002, 1003 },
            layout.Boxes.Select(b => b.Address).OrderBy(a => a).ToArray());
    }
}
