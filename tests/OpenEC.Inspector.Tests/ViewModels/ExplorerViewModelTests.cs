using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class ExplorerViewModelTests
{
    private static readonly Action<ExplorerNode?> Ignore = _ => { };

    [Fact]
    public async Task Refresh_builds_root_and_ordered_slave_nodes()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        Assert.Equal(session.SourceDescription, vm.Root.Label);
        Assert.Equal(StatusDot.Idle, vm.Root.Dot); // completed file session
        var slaves = vm.Root.Children.OfType<SlaveNode>().ToList();
        Assert.Equal([1001, 1002, 1003, 1004], slaves.Select(s => (int)s.Address).ToArray());
        Assert.Equal("Term 1 (EK1100) (1001)", slaves[0].Label);
    }

    [Fact]
    public async Task The_faulted_drive_gets_a_fail_dot()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        var drive = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);
        Assert.Equal(StatusDot.Fail, drive.Dot); // SafeOp + error flag
    }

    [Fact]
    public async Task Refresh_updates_nodes_in_place()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);
        vm.Refresh();
        var before = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);

        vm.Refresh();

        Assert.Same(before, vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004));
        Assert.Equal(4, vm.Root.Children.OfType<SlaveNode>().Count());
    }

    [Fact]
    public async Task Without_eni_the_process_image_node_is_present_and_last()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new ExplorerViewModel(session, assignment: null, Ignore);

        vm.Refresh();

        Assert.IsType<ProcessImageNode>(vm.Root.Children[^1]);
    }

    [Fact]
    public async Task A_fully_matched_eni_hides_the_process_image_node()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), Ignore);

        vm.Refresh();

        Assert.DoesNotContain(vm.Root.Children, n => n is ProcessImageNode);
    }

    [Fact]
    public async Task Unmatched_variables_keep_the_process_image_node_visible()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni) with
        {
            Unmatched = [new EniVariable("Ghost.Value", "INT", 16, 0, true)],
        };
        var vm = new ExplorerViewModel(session, assignment, Ignore);

        vm.Refresh();

        Assert.IsType<ProcessImageNode>(vm.Root.Children[^1]);
    }

    [Fact]
    public async Task Selecting_a_node_invokes_the_callback()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        ExplorerNode? seen = null;
        var vm = new ExplorerViewModel(session, ProcessVariableAssignment.Build(eni), n => seen = n);
        vm.Refresh();

        var drive = vm.Root.Children.OfType<SlaveNode>().Single(s => s.Address == 1004);
        vm.SelectedNode = drive;

        Assert.Same(drive, seen);
    }
}
