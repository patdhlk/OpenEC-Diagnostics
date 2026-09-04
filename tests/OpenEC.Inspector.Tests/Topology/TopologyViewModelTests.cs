using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyViewModelTests
{
    private static async Task<ExplorerViewModel> BranchedExplorerAsync()
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        return explorer;
    }

    [Fact]
    public async Task The_map_has_a_box_per_device_plus_the_master()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Contains(explorer.Topology.Boxes, b => b.Address == BusTopology.MasterAddress);
        foreach (ushort address in new ushort[] { 1001, 1002, 1003, 1004 })
            Assert.Contains(explorer.Topology.Boxes, b => b.Address == address);
    }

    /// <summary>The load-bearing invariant: a box carries the same node instance as its tree row,
    /// so selection is by identity and needs no extra routing.</summary>
    [Fact]
    public async Task A_box_holds_the_same_node_instance_as_its_tree_row()
    {
        var explorer = await BranchedExplorerAsync();

        var row = explorer.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1002);
        var box = explorer.Topology.Boxes.Single(b => b.Address == 1002);

        Assert.Same(row, box.Node);
    }

    [Fact]
    public async Task The_master_box_holds_the_root_node()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Same(explorer.Root,
            explorer.Topology.Boxes.Single(b => b.Address == BusTopology.MasterAddress).Node);
    }

    [Fact]
    public async Task Selecting_a_box_selects_that_node_on_the_explorer()
    {
        var explorer = await BranchedExplorerAsync();
        var box = explorer.Topology.Boxes.Single(b => b.Address == 1003);

        explorer.Topology.SelectedNode = box.Node;

        Assert.Same(box.Node, explorer.SelectedNode);
    }

    [Fact]
    public async Task Selecting_a_tree_row_is_reflected_on_the_map()
    {
        var explorer = await BranchedExplorerAsync();
        var row = explorer.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);

        explorer.SelectedNode = row;

        Assert.Same(row, explorer.Topology.SelectedNode);
    }

    /// <summary>Box instances must survive a tick, or selection and any future animation would be
    /// thrown away every refresh — the same row-reuse rule the tree follows.</summary>
    [Fact]
    public async Task Refreshing_an_unchanged_topology_reuses_the_box_instances()
    {
        var explorer = await BranchedExplorerAsync();
        var before = explorer.Topology.Boxes.ToList();

        explorer.Refresh();

        Assert.Equal(before.Count, explorer.Topology.Boxes.Count);
        Assert.All(before.Zip(explorer.Topology.Boxes), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public async Task A_session_with_no_port_data_shows_a_notice_and_no_port_marks()
    {
        var session = await TestSessions.RunFileSessionAsync();   // demo capture: no DL-status reads
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();

        Assert.NotNull(explorer.Topology.Notice);
        Assert.Contains("not observed", explorer.Topology.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.All(explorer.Topology.Boxes, b => Assert.Empty(b.Ports));
    }

    [Fact]
    public async Task A_session_with_port_data_shows_no_notice()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.Null(explorer.Topology.Notice);
    }

    [Fact]
    public async Task The_canvas_extent_is_published_for_the_scroll_viewer()
    {
        var explorer = await BranchedExplorerAsync();

        Assert.True(explorer.Topology.CanvasWidth > 0);
        Assert.True(explorer.Topology.CanvasHeight > 0);
    }

    [Fact]
    public async Task Classic_view_is_the_default_tab()
    {
        Assert.Equal(0, (await BranchedExplorerAsync()).SelectedViewIndex);
    }

    /// <summary>The geometry fingerprint used to fold only structure, so a port that gains or loses
    /// a link WITHOUT changing the tree — a leaf's spare port, a junction's idle branch — kept its
    /// stale colour: a wrong reading in a diagnostic view. A childless port's state must recolour.</summary>
    [Fact]
    public async Task A_childless_port_changing_link_state_recolours_its_mark()
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();

        var box = explorer.Topology.Boxes.Single(b => b.Address == 1003);   // a line end
        Assert.Equal(PortLinkState.Dangling, box.Ports.Single(p => p.Port == 1).State);

        // Port 1 carries no downstream device, so flipping it Dangling -> Blocked changes neither an
        // edge nor the port count — the exact case the structure-only fingerprint used to miss.
        Feed(session, DlStatusRead(1003, 0x0430));
        explorer.Refresh();

        var after = explorer.Topology.Boxes.Single(b => b.Address == 1003);
        Assert.Same(box, after);   // the rebuild reused the box VM by node identity, so selection survives
        Assert.Equal(PortLinkState.Blocked, after.Ports.Single(p => p.Port == 1).State);
    }

    /// <summary>A counter first going non-zero flips a port mark's error flag while leaving its state
    /// and the tree untouched — the HasError path (ledger T10) the old fingerprint never rebuilt for.</summary>
    [Fact]
    public async Task A_childless_port_gaining_an_error_count_marks_its_mark_in_error()
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();

        var box = explorer.Topology.Boxes.Single(b => b.Address == 1003);
        Assert.False(box.Ports.Single(p => p.Port == 1).HasError);   // counters read, all zero

        Feed(session, CounterRead(1003, 0x0302, [0x01]));   // port 1 invalid-frame counter := 1
        explorer.Refresh();

        Assert.Same(box, explorer.Topology.Boxes.Single(b => b.Address == 1003));
        Assert.True(box.Ports.Single(p => p.Port == 1).HasError);
    }

    private static void Feed(OpenEC.Inspector.Session.MonitorSession session, byte[] frame) =>
        session.Observer.Process(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(frame));

    private static byte[] DlStatusRead(ushort station, ushort raw) =>
        new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, station, 0x0110, BitConverter.GetBytes(raw), 1)
            .Build();

    private static byte[] CounterRead(ushort station, ushort register, byte[] payload) =>
        new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, station, register, payload, 1)
            .Build();
}
