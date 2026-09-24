using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector.Tests.ViewModels;

public class ReplayTransportTests
{
    private static MainWindowViewModel CreateShell() =>
        new(() => [],
            (spec, eni) => new MonitorSession(spec, eni),
            new FakeFilePicker(),
            marshal: action => action(),
            earlyFaultProbe: TimeSpan.FromMilliseconds(150));

    [Fact]
    public async Task Replay_source_spec_gets_a_playback_controller()
    {
        await using var s = new MonitorSession(new SourceSpec.Replay(TestSessions.WriteDemoPcap()));

        Assert.NotNull(s.Playback);
        Assert.True(s.Playback!.IsPaused);
    }

    [Fact]
    public async Task File_and_live_sources_have_no_playback()
    {
        await using var fileSession = new MonitorSession(new SourceSpec.File(TestSessions.WriteDemoPcap()));
        await using var liveSession = new MonitorSession(new SourceSpec.Live("en0"));

        Assert.Null(fileSession.Playback);
        Assert.Null(liveSession.Playback);
    }

    [Fact]
    public async Task Starting_a_replay_shows_a_paused_transport()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        Assert.True(vm.HasSession);
        Assert.True(vm.IsReplay);
        Assert.True(vm.IsPlaybackPaused);
        Assert.True(vm.CanControlPlayback);
        Assert.Contains("Play", vm.PlayPauseLabel);

        await vm.StopSessionCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Toggle_play_pause_drives_the_controller()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        var playback = vm.Session!.Playback!;
        Assert.True(playback.IsPaused);
        Assert.True(vm.IsPlaybackPaused);

        vm.TogglePlayPauseCommand.Execute(null);
        Assert.False(playback.IsPaused);
        Assert.False(vm.IsPlaybackPaused);
        Assert.Contains("Pause", vm.PlayPauseLabel);

        vm.TogglePlayPauseCommand.Execute(null);
        Assert.True(playback.IsPaused);
        Assert.True(vm.IsPlaybackPaused);

        await vm.StopSessionCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Speed_slider_forwards_and_clamps_to_the_controller()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        vm.PlaybackSpeed = 5.0;
        Assert.Equal(5.0, vm.Session!.Playback!.Speed);

        vm.PlaybackSpeed = 100.0;
        Assert.Equal(ReplayController.MaxSpeed, vm.Session!.Playback!.Speed);
        Assert.Contains("×", vm.PlaybackSpeedText);

        await vm.StopSessionCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Analyze_file_session_is_not_a_replay()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartFileCommand.ExecuteAsync(null);

        Assert.False(vm.IsReplay);
        Assert.False(vm.CanControlPlayback);
    }

    [Fact]
    public async Task Closing_a_replay_clears_transport_state()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        await vm.StopSessionCommand.ExecuteAsync(null);

        Assert.False(vm.IsReplay);
        Assert.False(vm.CanControlPlayback);
        Assert.False(vm.HasSession);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
    }

    [Fact]
    public async Task Step_is_available_only_while_a_replay_is_paused()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        Assert.True(vm.IsReplay);
        Assert.True(vm.CanStepFrame);                 // starts paused
        vm.TogglePlayPauseCommand.Execute(null);      // play
        Assert.False(vm.CanStepFrame);                // cannot step while running

        await vm.StopSessionCommand.ExecuteAsync(null);
        Assert.False(vm.CanStepFrame);
    }

    [Fact]
    public async Task Step_frame_advances_the_replay_by_one_frame()
    {
        var vm = CreateShell();
        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Session!.FramesSeen);      // paused: nothing decoded yet
        vm.StepFrameCommand.Execute(null);
        await WaitUntilAsync(() => vm.Session!.FramesSeen >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken); // let any erroneous extra frame leak
        Assert.Equal(1, vm.Session!.FramesSeen);       // exactly one frame decoded, then re-paused

        await vm.StopSessionCommand.ExecuteAsync(null);
    }
}
