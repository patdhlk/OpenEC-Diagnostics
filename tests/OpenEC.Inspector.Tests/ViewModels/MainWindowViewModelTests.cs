using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor;

namespace OpenEC.Inspector.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel Create(
        Func<SourceSpec, OpenEC.Monitor.Eni.EniConfiguration?, MonitorSession>? factory = null,
        IFilePicker? picker = null) =>
        new(
            () => [],
            factory ?? ((spec, eni) => new MonitorSession(spec, eni)),
            picker ?? new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromSeconds(2));

    private static async Task<MainWindowViewModel> CreateWithDemoSessionAsync(
        string? eniPath = null, IFilePicker? picker = null)
    {
        var vm = Create(picker: picker);
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        vm.Start.EniPath = eniPath;
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public void Boots_to_the_start_screen_with_no_session()
    {
        var vm = Create();

        Assert.Same(vm.Start, vm.CurrentPage);
        Assert.False(vm.HasSession);
        Assert.Equal("No session", vm.StatusText);
    }

    [Fact]
    public async Task Starting_a_file_session_switches_to_the_dashboard()
    {
        var vm = await CreateWithDemoSessionAsync();

        Assert.True(vm.HasSession);
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
        Assert.NotNull(vm.Session);
        Assert.NotNull(vm.Explorer);
        Assert.NotNull(vm.Events);

        vm.Tick();
        Assert.Contains("103", vm.StatusText);
        Assert.Contains("completed", vm.StatusText);
    }

    [Fact]
    public async Task Node_selection_swaps_the_current_page()
    {
        var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        var vm = await CreateWithDemoSessionAsync(eniPath: eniPath);

        var explorer = vm.Explorer!;
        var drive = explorer.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1004);

        explorer.SelectedNode = drive;
        var editor = Assert.IsType<DeviceEditorViewModel>(vm.CurrentPage);
        Assert.Equal(1004, editor.Address);

        explorer.SelectedNode = explorer.Root;
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);

        explorer.SelectedNode = drive;
        Assert.Same(editor, vm.CurrentPage); // editor instances are cached per address
    }

    [Fact]
    public async Task Clearing_the_selection_falls_back_to_the_dashboard()
    {
        var vm = await CreateWithDemoSessionAsync();
        var drive = vm.Explorer!.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1004);
        vm.Explorer.SelectedNode = drive;

        vm.Explorer.SelectedNode = null;

        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
    }

    [Fact]
    public async Task Without_eni_the_process_image_node_shows_the_variable_watch()
    {
        var vm = await CreateWithDemoSessionAsync();

        var node = vm.Explorer!.Root.Children.OfType<ProcessImageNode>().Single();
        vm.Explorer.SelectedNode = node;

        var watch = Assert.IsType<VariableWatchViewModel>(vm.CurrentPage);
        Assert.False(watch.HasVariables);
    }

    [Fact]
    public async Task Load_eni_from_a_device_editor_restarts_and_preserves_the_selection()
    {
        var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        var vm = await CreateWithDemoSessionAsync(picker: new FakeFilePicker(eniPath));

        // Without an ENI only station 1004 is observed on the wire.
        var drive = vm.Explorer!.Root.Children.OfType<SlaveNode>().Single(n => n.Address == 1004);
        vm.Explorer.SelectedNode = drive;
        var editor = (DeviceEditorViewModel)vm.CurrentPage;
        Assert.False(editor.Variables.HasVariables);

        await editor.Variables.LoadEniCommand.ExecuteAsync(null);
        await vm.Session!.Completion;

        var reselected = Assert.IsType<SlaveNode>(vm.Explorer!.SelectedNode);
        Assert.Equal(1004, reselected.Address);
        var reloaded = Assert.IsType<DeviceEditorViewModel>(vm.CurrentPage);
        Assert.True(reloaded.Variables.HasVariables);
        reloaded.SelectedTabIndex = 1;
        reloaded.Refresh();
        Assert.Equal(2, reloaded.Variables.Rows.Count);
    }

    [Fact]
    public async Task A_finished_capture_offers_to_close_rather_than_stop()
    {
        var vm = await CreateWithDemoSessionAsync();
        vm.Tick();

        // A file read to its end has nothing left to stop; the control is only a way out.
        Assert.Equal(SessionState.Completed, vm.Session!.State);
        Assert.Equal("Close session", vm.SessionActionLabel);
    }

    [Fact]
    public async Task A_running_capture_still_offers_to_stop()
    {
        var vm = new MainWindowViewModel(
            () => [],
            (spec, _) => new MonitorSession(
                EtherCatMonitor.FromSource(new BlockingCaptureSource()), spec.Description),
            new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromMilliseconds(10));
        vm.Start.SelectedDevice = "en11";
        await vm.Start.StartLiveCommand.ExecuteAsync(null);
        vm.Tick();

        Assert.Equal(SessionState.Running, vm.Session!.State);
        Assert.Equal("Stop session", vm.SessionActionLabel);

        await vm.StopSessionCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stopping_the_session_clears_explorer_and_events()
    {
        var vm = await CreateWithDemoSessionAsync();

        await vm.StopSessionCommand.ExecuteAsync(null);

        Assert.Null(vm.Explorer);
        Assert.Null(vm.Events);
        Assert.Equal(StatusDot.Idle, vm.SessionDot);
    }

    [Fact]
    public async Task Stopping_the_session_returns_to_a_fresh_start_screen()
    {
        var vm = await CreateWithDemoSessionAsync();
        var firstStart = vm.Start;

        await vm.StopSessionCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Null(vm.Session);
        Assert.NotSame(firstStart, vm.Start);
        Assert.Same(vm.Start, vm.CurrentPage);
        Assert.Equal("No session", vm.StatusText);
    }

    [Fact]
    public async Task A_mid_session_fault_raises_the_banner_via_marshal()
    {
        var source = new TriggeredFaultSource();
        // Short probe: the parked source never completes early, so the default 2 s probe
        // would just add dead wait time to this test.
        var vm = new MainWindowViewModel(
            () => [],
            (_, eni) => new MonitorSession(EtherCatMonitor.FromSource(source), "fake", eni),
            new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromMilliseconds(50));
        vm.Start.PcapPath = TestSessions.WriteDemoPcap(); // path only satisfies validation
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        Assert.True(vm.HasSession);

        source.Trigger.SetResult();
        await vm.Session!.Completion;

        Assert.Equal("boom", vm.FaultMessage);
        vm.DismissFaultCommand.Execute(null);
        Assert.Null(vm.FaultMessage);
    }

    [Fact]
    public async Task A_stale_fault_notification_from_a_detached_session_does_not_write_after_a_new_session_is_attached()
    {
        // Deterministic pinning test for the leaked StateChanged subscription: with a real
        // asynchronous dispatcher, a posted action from an already-detached session can run
        // AFTER a new session has been attached. Use a queueing marshal so nothing runs until
        // we choose to drain it, well after the swap has happened.
        var source = new TriggeredFaultSource();
        var queued = new List<Action>();
        var usedFaultSource = false;
        var vm = new MainWindowViewModel(
            () => [],
            (spec, eni) =>
            {
                if (!usedFaultSource)
                {
                    usedFaultSource = true;
                    return new MonitorSession(EtherCatMonitor.FromSource(source), "fake", eni);
                }
                return new MonitorSession(spec, eni);
            },
            new FakeFilePicker(),
            marshal: action => queued.Add(action),
            earlyFaultProbe: TimeSpan.FromMilliseconds(50));

        vm.Start.PcapPath = TestSessions.WriteDemoPcap(); // path only satisfies validation
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        Assert.True(vm.HasSession);
        var faultedSession = vm.Session!;

        source.Trigger.SetResult();
        await faultedSession.Completion; // the fault-banner action is queued, not run

        await vm.StopSessionCommand.ExecuteAsync(null);

        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        Assert.True(vm.HasSession);

        // Drain everything queued so far (the stale fault action plus whatever Stop/Start
        // queued along the way) only now that a new session is attached.
        foreach (var action in queued.ToList()) action();

        Assert.Null(vm.FaultMessage);
        Assert.True(vm.HasSession);
    }

    [Fact]
    public async Task The_status_line_shows_an_active_recording()
    {
        var recordPath = Path.Combine(Path.GetTempPath(), $"openec-status-rec-{Guid.NewGuid():N}.pcap");
        try
        {
            var vm = Create(factory: (_, eni) => new MonitorSession(
                new SourceSpec.File(TestSessions.WriteDemoPcap()) { RecordPath = recordPath }, eni));
            vm.Start.PcapPath = TestSessions.WriteDemoPcap();
            await vm.Start.StartFileCommand.ExecuteAsync(null);

            vm.Tick();

            Assert.Contains($"rec → {Path.GetFileName(recordPath)}", vm.StatusText);
        }
        finally
        {
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    [Fact]
    public async Task The_status_line_omits_the_recording_suffix_without_a_record_path()
    {
        var vm = await CreateWithDemoSessionAsync();

        vm.Tick();

        Assert.DoesNotContain("rec →", vm.StatusText);
    }

    [Fact]
    public async Task The_status_bar_surfaces_bus_health()
    {
        var vm = await TestSessions.ShellWithBringupAsync(new FakeFilePicker());
        vm.Tick();

        Assert.Equal(StatusDot.Ok, vm.HealthDot);
        Assert.Contains("2/2 devices", vm.HealthText);
        Assert.Contains("DC synced", vm.HealthText);
    }

    [Fact]
    public void With_no_session_the_health_indicator_is_idle_and_blank()
    {
        var vm = Create();

        Assert.Equal(StatusDot.Idle, vm.HealthDot);
        Assert.Equal("", vm.HealthText);
    }

    [Fact]
    public async Task Opening_a_capture_file_from_the_menu_starts_a_session()
    {
        var vm = Create(picker: new FakeFilePicker(TestSessions.WriteBringupPcap()));

        await vm.OpenCaptureFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasSession);
        Assert.NotNull(vm.Session);
    }

    [Fact]
    public async Task Opening_a_capture_file_replaces_an_active_session()
    {
        var vm = Create(picker: new FakeFilePicker(TestSessions.WriteBringupPcap()));
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        var first = vm.Session;

        await vm.OpenCaptureFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasSession);
        Assert.NotSame(first, vm.Session);
    }

    [Fact]
    public async Task Cancelling_the_open_dialog_leaves_the_session_untouched()
    {
        var vm = await CreateWithDemoSessionAsync();
        var session = vm.Session;

        await vm.OpenCaptureFileCommand.ExecuteAsync(null);

        Assert.Same(session, vm.Session);
        Assert.True(vm.HasSession);
    }

    [Fact]
    public async Task Loading_an_eni_mid_session_restarts_with_it_applied()
    {
        var eniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        var vm = await CreateWithDemoSessionAsync(picker: new FakeFilePicker(eniPath));

        Assert.Null(vm.Session!.Eni);

        await vm.RestartWithEniCommand.ExecuteAsync(null);
        await vm.Session!.Completion;

        Assert.True(vm.HasSession);
        Assert.NotNull(vm.Session!.Eni);
    }

    [Fact]
    public async Task Loading_an_eni_without_a_session_is_a_no_op()
    {
        var vm = Create(picker: new FakeFilePicker(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml")));

        await vm.RestartWithEniCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Null(vm.Session);
    }

}
