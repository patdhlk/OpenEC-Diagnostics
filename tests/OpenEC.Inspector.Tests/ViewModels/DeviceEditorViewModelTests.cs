using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class DeviceEditorViewModelTests
{
    private static readonly Func<Task> NoLoad = () => Task.CompletedTask;

    private static async Task<(MonitorSession Session, DeviceEditorViewModel Editor)> DriveEditorAsync()
    {
        var eni = TestSessions.LoadFixtureEni();
        var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni);
        var slave = eni.Slaves.Single(s => s.PhysAddr == 1004);
        var watch = VariableWatchViewModel.ForSlave(session, NoLoad, slave, assignment.BySlave[1004]);
        return (session, new DeviceEditorViewModel(session, 1004, watch));
    }

    [Fact]
    public async Task Refresh_builds_the_general_tab_from_status_and_events()
    {
        var (session, editor) = await DriveEditorAsync();
        await using var _ = session;

        editor.Refresh();

        Assert.NotNull(editor.Detail);
        Assert.Equal("Drive 4 (AX5101)", editor.Detail!.Title);
        Assert.NotEmpty(editor.Detail.StateHistory);
        Assert.Equal(2, editor.Detail.MailboxActivity.Count); // one CoE emergency + one SoE error
        Assert.Equal(StatusDot.Fail, editor.StateDot);
        Assert.Equal("SafeOp", editor.StateLabel);
        Assert.NotEqual("—", editor.LastSeen);
    }

    [Fact]
    public async Task The_variables_tab_refreshes_only_while_selected()
    {
        var (session, editor) = await DriveEditorAsync();
        await using var _ = session;

        editor.SelectedTabIndex = 0;
        editor.Refresh();
        Assert.Empty(editor.Variables.Rows);   // not scanned yet

        editor.SelectedTabIndex = 1;
        editor.Refresh();
        Assert.Equal(2, editor.Variables.Rows.Count);
    }
}
