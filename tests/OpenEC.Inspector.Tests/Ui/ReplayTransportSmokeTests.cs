using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector.Tests.Ui;

/// <summary>Renders the real shell over a replay session and checks that the transport bar
/// (play/pause button + speed slider) actually instantiates, shows only for a replay, and
/// reflects the view model — the binding paths the VM-level tests cannot exercise.</summary>
public class ReplayTransportSmokeTests
{
    private static MainWindowViewModel CreateViewModel() => new(
        () => [],
        (spec, eni) => new MonitorSession(spec, eni),
        new FakeFilePicker(),
        marshal: action => action(),
        // A replay begins paused and never completes on its own, so keep the start probe short.
        earlyFaultProbe: TimeSpan.FromMilliseconds(150));

    [AvaloniaFact]
    public async Task Replay_transport_renders_and_reflects_play_pause_and_speed()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasSession);
        Assert.True(vm.IsReplay);

        var playPause = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, vm.TogglePlayPauseCommand));
        var slider = window.GetVisualDescendants().OfType<Slider>().Single();

        // Visible and interactive while the paused replay is Running.
        Assert.True(playPause.IsEffectivelyVisible);
        Assert.True(slider.IsEffectivelyVisible);
        Assert.True(playPause.IsEnabled);
        Assert.True(slider.IsEnabled);

        // Slider is bound to the controller's clamp range.
        Assert.Equal(ReplayController.MinSpeed, slider.Minimum);
        Assert.Equal(ReplayController.MaxSpeed, slider.Maximum);

        // Paused → the button offers Play.
        Assert.Contains("Play", (string)playPause.Content!);

        // Toggling flips the label to Pause, then back.
        vm.TogglePlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Pause", (string)playPause.Content!);
        vm.TogglePlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Play", (string)playPause.Content!);

        // Dragging the slider (via its two-way value) forwards to the controller and the readout.
        slider.Value = 4.0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4.0, vm.Session!.Playback!.Speed);
        Assert.Contains("4", (string)window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => (t.Text ?? "").Contains('×')).Text!);

        // Closing the session hides the transport entirely.
        await vm.StopSessionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsReplay);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Slider>(),
            s => s.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Step_button_renders_and_enables_only_while_paused()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        await vm.Start.StartReplayCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var stepButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, vm.StepFrameCommand));

        // Paused replay: the step control is shown and enabled.
        Assert.True(stepButton.IsEffectivelyVisible);
        Assert.True(stepButton.IsEnabled);

        // Playing: stepping is disabled (like a debugger that can't step a running program).
        vm.TogglePlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(stepButton.IsEnabled);

        await vm.StopSessionCommand.ExecuteAsync(null);
    }
}
