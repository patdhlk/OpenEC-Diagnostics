using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

/// <summary>An ENI's declared PreviousPort edges are attacker-free but not error-free: a
/// hand-edited or generator-bugged file can name a device as its own upstream, or name two devices
/// as each other's. Those close a cycle in a graph whose consumers walk it recursively, so the
/// reconstruction must refuse the edge rather than pass the cycle on.</summary>
public class EniDeclaredCycleTests
{
    private static TopologyDevice Blind(ushort address, int ringPosition) =>
        new(address, ringPosition, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());

    private static EniConfiguration Eni(params EniSlave[] slaves) =>
        new() { Slaves = slaves, CyclicCommands = [], Variables = [] };

    private static EniSlave Slave(ushort physAddr, EniPreviousPort? previous) =>
        new($"Slave {physAddr}", physAddr, 0, 2, 0x1111, 0x0001, null, null, previous);

    /// <summary>The edge names the device itself. Honouring it would make the node its own parent.</summary>
    [Fact]
    public void An_edge_naming_the_device_itself_falls_back_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1)],
            Eni(Slave(1001, null), Slave(1002, new EniPreviousPort(1002, 1))));

        var node = topology.Find(1002)!;
        Assert.NotEqual(node.Address, node.ParentAddress);
        Assert.Equal((ushort)1001, node.ParentAddress);
        Assert.Equal(TopologyEdgeSource.Inferred, node.EdgeSource);
    }

    /// <summary>Two devices declared as each other's upstream.</summary>
    [Fact]
    public void A_pair_of_edges_naming_each_other_falls_back_to_ring_order()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1)],
            Eni(Slave(1001, new EniPreviousPort(1002, 1)),
                Slave(1002, new EniPreviousPort(1001, 1))));

        // 1001 is placed first, so its forward reference to 1002 cannot be honoured; 1002 then
        // legitimately hangs off 1001. Either way the two are not each other's parent.
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
    }

    /// <summary>A well-formed ENI still gets its declared edges honoured — the guard must not cost
    /// the feature it protects.</summary>
    [Fact]
    public void A_well_formed_declaration_is_still_honoured()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1), Blind(1003, 2)],
            Eni(Slave(1001, null),
                Slave(1002, new EniPreviousPort(1001, 1)),
                Slave(1003, new EniPreviousPort(1001, 2))));

        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal((ushort)1001, topology.Find(1003)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1003)!.ParentPort);
        Assert.Equal(TopologyEdgeSource.Eni, topology.Find(1003)!.EdgeSource);
    }

    /// <summary>Whatever the file declares, the graph handed to a recursive consumer is a tree.</summary>
    [Fact]
    public void No_declaration_can_make_the_parent_graph_cyclic()
    {
        var topology = TopologyReconstructor.Reconstruct(
            [Blind(1001, 0), Blind(1002, 1), Blind(1003, 2)],
            Eni(Slave(1001, new EniPreviousPort(1003, 1)),
                Slave(1002, new EniPreviousPort(1002, 1)),
                Slave(1003, new EniPreviousPort(1001, 1))));

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
        }
    }
}
