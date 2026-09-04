using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class StartViewModelTests
{
    private static StartViewModel Create(
        Action<MonitorSession>? onStarted = null,
        IFilePicker? picker = null,
        Func<SourceSpec, OpenEC.Monitor.Eni.EniConfiguration?, MonitorSession>? factory = null) =>
        new(
            () => [("en11", "ETAP tap"), ("en0", null)],
            factory ?? ((spec, eni) => new MonitorSession(spec, eni)),
            picker ?? new FakeFilePicker(),
            onStarted ?? (_ => { }),
            earlyFaultProbe: TimeSpan.FromSeconds(2));

    [Fact]
    public void Devices_are_listed_on_construction()
    {
        var vm = Create();

        Assert.Equal(["en11", "en0"], vm.Devices);
    }

    [Fact]
    public async Task Start_live_without_a_selected_device_reports_an_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);

        await vm.StartLiveCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Start_file_with_a_missing_path_reports_an_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = "/nonexistent/nope.pcap";

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Start_file_hands_a_completed_demo_session_to_the_shell()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.NotNull(started);
        Assert.Equal(SessionState.Completed, started!.State);
        Assert.Null(vm.ErrorMessage);
        await started.DisposeAsync();
    }

    [Fact]
    public async Task A_garbage_file_faults_early_and_stays_on_the_start_screen()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        var garbage = Path.Combine(Path.GetTempPath(), $"openec-garbage-{Guid.NewGuid():N}.pcap");
        await File.WriteAllTextAsync(garbage, "this is not a capture file", TestContext.Current.CancellationToken);
        vm.PcapPath = garbage;

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task An_invalid_eni_blocks_the_start_with_an_inline_error()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();
        var badEni = Path.Combine(Path.GetTempPath(), $"openec-bad-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(badEni, "<not-an-eni>", TestContext.Current.CancellationToken);
        vm.EniPath = badEni;

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.Contains("ENI", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_valid_eni_is_loaded_into_the_session()
    {
        MonitorSession? started = null;
        var vm = Create(s => started = s);
        vm.PcapPath = TestSessions.WriteDemoPcap();
        vm.EniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.NotNull(started!.Eni);
        await started.DisposeAsync();
    }

    [Fact]
    public async Task Browse_commands_fill_the_paths_from_the_picker()
    {
        var vm = Create(picker: new FakeFilePicker("/tmp/picked.pcap"));

        await vm.BrowsePcapCommand.ExecuteAsync(null);

        Assert.Equal("/tmp/picked.pcap", vm.PcapPath);
    }

    [Fact]
    public void Permission_faults_get_the_tap_setup_hint()
    {
        var hinted = StartViewModel.FormatFault(new IOException("en11: Permission denied (BPF)"));

        Assert.Contains("tap-setup.md", hinted);
        Assert.DoesNotContain("tap-setup.md", StartViewModel.FormatFault(new IOException("other")));
    }

    [Fact]
    public async Task Browse_record_fills_the_record_path_from_the_save_picker()
    {
        var vm = Create(picker: new FakeFilePicker(saveResult: "/tmp/recording.pcap"));

        await vm.BrowseRecordCommand.ExecuteAsync(null);

        Assert.Equal("/tmp/recording.pcap", vm.RecordPath);
    }

    [Fact]
    public async Task Start_live_carries_the_record_path_into_the_source_spec()
    {
        SourceSpec? captured = null;
        var vm = Create(factory: (spec, eni) =>
        {
            captured = spec;
            return new MonitorSession(new SourceSpec.File(TestSessions.WriteDemoPcap()), eni);
        });
        vm.SelectedDevice = "en11";
        vm.RecordPath = "/tmp/live-recording.pcap";

        await vm.StartLiveCommand.ExecuteAsync(null);

        Assert.Equal(new SourceSpec.Live("en11") { RecordPath = "/tmp/live-recording.pcap" }, captured);
    }

    [Fact]
    public async Task Start_file_does_not_attach_the_record_path()
    {
        SourceSpec? captured = null;
        var vm = Create(factory: (spec, eni) =>
        {
            captured = spec;
            return new MonitorSession(spec, eni);
        });
        vm.PcapPath = TestSessions.WriteDemoPcap();
        vm.RecordPath = "/tmp/should-not-be-used.pcap";

        await vm.StartFileCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Null(captured!.RecordPath);
    }
}
