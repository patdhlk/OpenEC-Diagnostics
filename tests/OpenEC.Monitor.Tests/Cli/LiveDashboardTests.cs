using Dahlke.EtherCAT.Diagnostics;
using OpenEC.CLI.Commands;
using OpenEC.Monitor.Ads;
using OpenEC.Monitor.Observation;
using Spectre.Console.Testing;

namespace OpenEC.Monitor.Tests.Cli;

public class LiveDashboardTests
{
    [Fact]
    public void Dashboard_renders_ads_frame_statistics()
    {
        var ads = new AdsBusSnapshot(
            new EtherCatMasterState { CurrentState = "Op", RequestedState = "Op", SlaveCount = 2 },
            ConfiguredSlaves: [],
            ScannedSlaves: [],
            new FrameStatistics
            {
                CyclicSendFrames = 236988,
                QueuedSendFrames = 21544,
                CyclicLostFrames = 0,
                QueuedLostFrames = 1,
                CyclicFramesPerSecond = 227,
                QueuedFramesPerSecond = 16,
                CyclicTxRxErrors = 0,
                QueuedTxRxErrors = 0,
            },
            ErrorCounters: new Dictionary<ushort, SlaveErrorCounters>());

        var console = new TestConsole();
        console.Write(LiveCommand.BuildDashboard(new BusObserver(), ads));

        Assert.Contains("236988 + 21544", console.Output);
        Assert.Contains("227 + 16", console.Output);
        Assert.Contains("0 + 1", console.Output);
    }

    [Fact]
    public void Dashboard_splits_rate_by_direction_for_twincat_comparison()
    {
        // TwinCAT's "Frames / sec" is a send rate; the TAP sees both directions, so the
        // outbound row is the TwinCAT-comparable one, split cyclic + queued like its panel.
        var console = new TestConsole();
        console.Write(LiveCommand.BuildDashboard(new BusObserver(), ads: null));

        Assert.Contains("Out fps (cyclic+queued)", console.Output);
        Assert.Contains("Ret fps", console.Output);
    }
}
