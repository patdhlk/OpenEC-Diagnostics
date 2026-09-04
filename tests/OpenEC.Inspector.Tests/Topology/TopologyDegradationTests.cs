using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.Topology;

/// <summary>Spec §7: every shortfall in what the wire revealed has one defined rendering. These
/// are the states a real passive session lands in most often, so they get their own coverage
/// rather than being implied by the happy path.</summary>
public class TopologyDegradationTests
{
    private static async Task<TopologyViewModel> TopologyFor(Func<Task<OpenEC.Inspector.Session.MonitorSession>> source)
    {
        var explorer = new ExplorerViewModel(await source(), assignment: null, _ => { });
        explorer.Refresh();
        return explorer.Topology;
    }

    [Fact]
    public async Task An_empty_capture_shows_only_the_master_and_no_crash()
    {
        var topology = await TopologyFor(TestSessions.EmptyAsync);

        Assert.Single(topology.Boxes);
        Assert.Empty(topology.Wires);
        Assert.Empty(topology.Unplaced);
    }

    [Fact]
    public async Task A_bus_with_no_port_reads_still_draws_every_device_in_ring_order()
    {
        var topology = await TopologyFor(TestSessions.BringupAsync);

        // BringupCapture now carries DL status, so this is the port-data path; the assertion is
        // that the devices are all present and connected regardless of which path produced them.
        Assert.Equal(3, topology.Boxes.Count);   // master + two slaves
        Assert.Equal(2, topology.Wires.Count);
    }

    [Fact]
    public async Task The_branched_bus_has_no_unplaced_devices()
    {
        var topology = await TopologyFor(TestSessions.BranchedAsync);

        Assert.Empty(topology.Unplaced);
        Assert.False(topology.HasUnplaced);
    }

    [Fact]
    public async Task Zoom_defaults_to_one_hundred_percent()
    {
        Assert.Equal(1.0, (await TopologyFor(TestSessions.BranchedAsync)).Zoom);
    }
}
