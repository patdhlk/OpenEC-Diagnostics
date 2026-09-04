using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.Ui;

public class ExplorerPaneWidthTests
{
    [Fact]
    public async Task The_pane_starts_at_the_classic_width()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());

        Assert.Equal(280, vm.ExplorerWidth);
    }

    [Fact]
    public async Task Switching_to_the_topology_view_widens_the_pane()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;

        Assert.True(vm.ExplorerWidth > 280);
    }

    [Fact]
    public async Task Switching_back_restores_the_classic_width()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;
        vm.Explorer.SelectedViewIndex = 0;

        Assert.Equal(280, vm.ExplorerWidth);
    }

    [Fact]
    public async Task A_dragged_width_is_remembered_per_view()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());

        vm.Explorer!.SelectedViewIndex = 1;
        vm.ExplorerWidth = 800;                     // as a splitter drag would
        vm.Explorer.SelectedViewIndex = 0;
        vm.Explorer.SelectedViewIndex = 1;

        Assert.Equal(800, vm.ExplorerWidth);
    }
}
