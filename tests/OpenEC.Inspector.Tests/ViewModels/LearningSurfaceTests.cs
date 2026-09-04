using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;

namespace OpenEC.Inspector.Tests.ViewModels;

public class LearningSurfaceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ls-{Guid.NewGuid():N}")).FullName;

    /// <summary>Asserts the COMPLETE sentence exactly. The previous version asserted only
    /// `Contains("learned", OrdinalIgnoreCase)`, and both branches of `DeviceEditorViewModel.Describe`
    /// contain "learned" — verified by mutation: with the complete branch made unreachable and the
    /// degraded one shortened to "Partially learned — missing everything.", that assertion still
    /// passed. It could not tell the two answers apart, which is the only thing it exists to do.</summary>
    [Fact]
    public async Task Device_editor_reports_a_fully_learned_slave_as_fully_learned()
    {
        await using var session = await TestSessions.BringupAsync();
        var editor = new DeviceEditorViewModel(session, 1001,
            VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []));

        editor.Refresh();

        Assert.Equal("Fully learned from observed traffic.", editor.Completeness);
    }

    /// <summary>The sentence the whole completeness apparatus exists to produce, and it had no test at
    /// all — `grep -rn "Partially\|Restarting" tests/` returned nothing. It is the project's governing
    /// principle in one string: name what is missing rather than present a partial configuration as a
    /// complete one, and say what would recover it. A slave with one real gap must name that gap and
    /// no other, so the message cannot decay into a generic "something is missing".</summary>
    [Theory]
    [MemberData(nameof(Gaps))]
    public void A_slave_with_one_gap_names_that_gap_and_no_other(string gap)
    {
        var described = DeviceEditorViewModel.Describe(WithOneGap(1001, gap));

        Assert.Equal(
            $"Partially learned — missing {gap}. "
            + "Restarting the master with the capture running would recover it.",
            described);
        foreach (var other in AllGaps.Where(g => g != gap))
            Assert.DoesNotContain(other, described, StringComparison.Ordinal);
    }

    /// <summary>Every gap label <c>DeviceEditorViewModel.Describe</c> can emit. Kept as one list so the
    /// theory both drives each case and checks that the others stay unmentioned.</summary>
    private static readonly string[] AllGaps =
        ["identity", "sync managers", "FMMUs", "PDO mapping", "process-data placement"];

    public static TheoryData<string> Gaps()
    {
        var data = new TheoryData<string>();
        foreach (var gap in AllGaps) data.Add(gap);
        return data;
    }

    /// <summary>A completeness record whose only shortfall for <paramref name="address"/> is
    /// <paramref name="gap"/>. Built by hand rather than provoked from synthetic traffic: no capture
    /// can be steered into leaving exactly one flag false, and which flags a given capture produces is
    /// LearningCompletenessTests' subject, not this one's.</summary>
    private static SlaveCompleteness WithOneGap(ushort address, string gap) =>
        new(address,
            IdentityKnown: gap != "identity",
            SyncManagersKnown: gap != "sync managers",
            FmmusKnown: gap != "FMMUs",
            PdoMappingKnown: gap != "PDO mapping",
            NamesFromEsi: true,
            ProcessDataPlaceable: gap != "process-data placement");

    /// <summary>The coherence gap the Save fix opened. With an ENI loaded, `Observer.Applied` is null
    /// for the whole session by design, so the strip — which used to read it — stayed blank for exactly
    /// the sessions whose Save button now exports a reconstruction: offering an artifact while hiding
    /// its quality. Sourcing the strip from the learned configuration makes the two agree, and the
    /// sentence describes what Save would write rather than anything about the loaded ENI.</summary>
    [Fact]
    public async Task Device_editor_reports_completeness_even_with_an_eni_loaded()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker(), withEni: true);
        var session = vm.Session!;
        Assert.NotNull(session.Eni);
        Assert.Null(session.Observer.Applied);   // by design, and why the strip used to stay blank

        var editor = new DeviceEditorViewModel(session, 1001,
            VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []));
        editor.Refresh();

        Assert.Equal("Fully learned from observed traffic.", editor.Completeness);
        // The two statements the screen makes about the reconstruction now agree.
        Assert.True(vm.SaveLearnedEniCommand.CanExecute(null));
    }

    [Fact]
    public async Task Device_editor_says_nothing_when_no_configuration_was_learned()
    {
        await using var session = await TestSessions.EmptyAsync();
        var editor = new DeviceEditorViewModel(session, 1001,
            VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, null, []));

        editor.Refresh();

        Assert.Equal("", editor.Completeness);
    }

    /// <summary>Spec §7's headline Inspector claim: the Variables tab works with no ENI at all.
    /// The learned configuration is an `EniConfiguration` like any other, so it goes through the
    /// same `ProcessVariableAssignment.Build` the ENI path uses — the tab cannot tell the
    /// difference, which is the whole point.</summary>
    [Fact]
    public async Task Variables_populate_from_learning_with_no_eni_loaded()
    {
        await using var session = await TestSessions.BringupAsync();
        Assert.Null(session.Eni);

        var learned = session.Observer.Applied!.Configuration;
        var assignment = ProcessVariableAssignment.Build(learned);
        var slave = learned.Slaves.Single(s => s.PhysAddr == 1001);
        var watch = VariableWatchViewModel.ForSlave(session, () => Task.CompletedTask, slave,
            assignment.BySlave[1001]);
        var editor = new DeviceEditorViewModel(session, 1001, watch);

        editor.SelectedTabIndex = 1;   // the Variables tab; it refreshes only while selected
        editor.Refresh();

        Assert.NotEmpty(editor.Variables.Rows);
    }

    /// <summary>Goes through <see cref="MainWindowViewModel"/> the way the UI does, unlike
    /// <see cref="Variables_populate_from_learning_with_no_eni_loaded"/> above, which builds the
    /// watch by hand and so never noticed that <c>MainWindowViewModel</c> never rebuilds its
    /// assignment from a learned configuration. Both statements on the device-editor screen —
    /// completeness on the General tab and rows on the Variables tab — must agree.</summary>
    [Fact]
    public async Task Selecting_a_learned_slave_through_the_shell_populates_its_variables()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());
        vm.Tick(); // picks up the learned revision published while the session was starting

        var node = vm.Explorer!.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1001);
        vm.Explorer.SelectedNode = node;

        var editor = Assert.IsType<DeviceEditorViewModel>(vm.CurrentPage);
        editor.SelectedTabIndex = 1; // the Variables tab; it refreshes only while selected
        editor.Refresh();

        Assert.NotEmpty(editor.Variables.Rows);
        Assert.NotEqual("", editor.Completeness);
    }

    [Fact]
    public async Task Saving_the_learned_eni_writes_a_loadable_file()
    {
        var output = Path.Combine(_directory, "bus.eni.xml");
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker(saveResult: output));

        await vm.SaveLearnedEniCommand.ExecuteAsync(null);

        Assert.Equal(2, EniConfiguration.Load(output).Slaves.Count);
    }

    /// <summary>With an ENI loaded the ENI stays the authority, so `OnConfigurationLearned` returns
    /// before `ApplyConfiguration` by design and `Observer.Applied` is null for the whole session
    /// (pinned by LearningIntegrationTests). The command guarded on `Applied`, so the button — enabled
    /// via HasSession alone, with no CanExecute — did nothing at all: no dialog, no file, no message,
    /// while `Session.Learned` held a perfectly good reconstruction the whole time.</summary>
    [Fact]
    public async Task Saving_the_learned_eni_works_with_an_eni_loaded()
    {
        var output = Path.Combine(_directory, "with-eni.eni.xml");
        var vm = await TestSessions.ShellWithBringupAsync(
            new FakeFilePicker(saveResult: output), withEni: true);
        Assert.NotNull(vm.Session!.Eni);
        Assert.Null(vm.Session.Observer.Applied);   // by design, and why the old guard never fired
        Assert.NotNull(vm.Session.Learned);

        await vm.SaveLearnedEniCommand.ExecuteAsync(null);

        // The two slaves the bringup revealed, not the four the loaded ENI declares: what gets saved
        // is the reconstruction from the wire, which is the only thing "learned ENI" can mean.
        Assert.Equal(2, EniConfiguration.Load(output).Slaves.Count);
    }

    /// <summary>The silence had a second half: with genuinely nothing learned the button was still
    /// enabled and still did nothing. It is now disabled, and the hint the view shows says why —
    /// rather than leaving the user to conclude the export is broken.</summary>
    [Fact]
    public async Task With_nothing_learned_the_save_command_is_disabled_and_says_why()
    {
        var vm = await TestSessions.ShellWithNothingLearnedAsync(new FakeFilePicker(saveResult: "x"));
        vm.Tick();

        Assert.Null(vm.Session!.Learned);
        Assert.False(vm.SaveLearnedEniCommand.CanExecute(null));
        Assert.Contains("Nothing has been learned", vm.SaveLearnedEniHint);
    }

    [Fact]
    public async Task Once_something_is_learned_the_save_command_becomes_available()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());
        vm.Tick();

        Assert.True(vm.SaveLearnedEniCommand.CanExecute(null));
        Assert.DoesNotContain("Nothing has been learned", vm.SaveLearnedEniHint);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_surfaces_no_error()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker(saveResult: null));

        await vm.SaveLearnedEniCommand.ExecuteAsync(null);

        // A cancelled dialog is a silent no-op, not an error surfaced to the user. The old name
        // promised a filesystem check this does not make — nothing here writes to a directory the
        // test could inspect, so asserting on an empty one would have passed trivially.
        Assert.Null(vm.FaultMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
