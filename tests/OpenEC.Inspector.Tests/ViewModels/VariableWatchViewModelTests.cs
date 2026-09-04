using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.Tests.ViewModels;

public class VariableWatchViewModelTests
{
    private static readonly Func<Task> NoLoad = () => Task.CompletedTask;

    private static (EniSlave Slave, IReadOnlyList<EniVariable> Vars) DriveScope(EniConfiguration eni)
    {
        var slave = eni.Slaves.Single(s => s.PhysAddr == 1004);
        var vars = ProcessVariableAssignment.Build(eni).BySlave[1004];
        return (slave, vars);
    }

    [Fact]
    public async Task Slave_scope_lists_its_variables_with_stripped_names_sorted()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, vars);

        vm.Refresh();

        Assert.True(vm.HasVariables);
        Assert.Equal(["Inputs.Statusword", "Outputs.Controlword"],
            vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["IN", "OUT"], vm.Rows.Select(r => r.Direction).ToArray());
        Assert.Equal(vars.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => v.DataType),
            vm.Rows.Select(r => r.DataType));
    }

    [Fact]
    public async Task Values_format_with_hex_bool_and_cia402_description()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var assignment = ProcessVariableAssignment.Build(eni);
        var term2 = eni.Slaves.Single(s => s.PhysAddr == 1002);
        var (drive, driveVars) = DriveScope(eni);

        var driveVm = VariableWatchViewModel.ForSlave(session, NoLoad, drive, driveVars);
        driveVm.Refresh();
        var statusword = driveVm.Rows.Single(r => r.Name.EndsWith("Statusword"));
        Assert.StartsWith("0x0637 (1591)", statusword.Value);
        Assert.Contains(" — ", statusword.Value); // CiA-402 description appended

        var termVm = VariableWatchViewModel.ForSlave(session, NoLoad, term2, assignment.BySlave[1002]);
        termVm.Refresh();
        Assert.Equal("TRUE", termVm.Rows.Single(r => r.Name.Contains("Channel 1")).Value);
        Assert.Equal("FALSE", termVm.Rows.Single(r => r.Name.Contains("Channel 2")).Value);
    }

    [Fact]
    public async Task An_assigned_but_never_observed_variable_shows_a_placeholder()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var ghost = new EniVariable("Drive 4 (AX5101).Ghost", "BOOL", 1, 999, true);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, [.. vars, ghost]);

        vm.Refresh();

        var row = vm.Rows.Single(r => r.Name == "Ghost");
        Assert.Equal("—", row.Value);
        Assert.Equal("—", row.Updated);
    }

    [Fact]
    public async Task Filter_narrows_rows_case_insensitively_and_resets()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var (slave, vars) = DriveScope(eni);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, vars);
        vm.Refresh();

        vm.FilterText = "statusword";
        Assert.Equal("Inputs.Statusword", Assert.Single(vm.Rows).Name);

        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Unmatched_scope_shows_full_names()
    {
        var eni = TestSessions.LoadFixtureEni();
        await using var session = await TestSessions.RunFileSessionAsync(eni);
        var ghost = new EniVariable("Ghost.Value", "INT", 16, 0, true);
        var vm = VariableWatchViewModel.ForUnmatched(session, NoLoad, [ghost]);

        vm.Refresh();

        Assert.Equal("Ghost.Value", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public async Task Without_eni_the_watch_reports_no_eni_and_stays_empty()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave: null, variables: []);

        vm.Refresh();

        Assert.False(vm.HasVariables);
        Assert.Empty(vm.Rows);
    }

    /// <summary>Pins the premise behind the empty-state copy: a session that learned variables
    /// with no ENI at all must NOT show the "no process image" panel. The panel's own visibility
    /// binding is `!HasVariables`, so this is the same fact stated from the view's perspective.</summary>
    [Fact]
    public async Task Learned_variables_with_no_eni_hide_the_empty_state_panel()
    {
        await using var session = await TestSessions.BringupAsync();
        Assert.Null(session.Eni);
        var learned = session.Observer.Applied!.Configuration;
        var assignment = ProcessVariableAssignment.Build(learned);
        var slave = learned.Slaves.Single(s => s.PhysAddr == 1001);
        var vm = VariableWatchViewModel.ForSlave(session, NoLoad, slave, assignment.BySlave[1001]);

        vm.Refresh();

        Assert.True(vm.HasVariables);
        Assert.NotEmpty(vm.Rows);
    }

    [Fact]
    public async Task Load_eni_command_invokes_the_callback()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var invoked = false;
        var vm = VariableWatchViewModel.ForSlave(session,
            () => { invoked = true; return Task.CompletedTask; }, slave: null, variables: []);

        await vm.LoadEniCommand.ExecuteAsync(null);

        Assert.True(invoked);
    }
}
