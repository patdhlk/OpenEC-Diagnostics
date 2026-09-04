using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public async Task Refresh_formats_the_demo_session_statistics()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new DashboardViewModel(session);

        vm.Refresh();

        Assert.Equal("1.00 ms", vm.CycleTime);
        Assert.Equal("1", vm.WkcMismatches);
        Assert.Equal("103 EtherCAT / 103 total", vm.FrameTotals);
        Assert.Equal("0", vm.Malformed);
        Assert.Equal("0", vm.RingLostFrames);
        Assert.EndsWith(" /s", vm.CyclicTxRate);
        Assert.EndsWith(" /s", vm.RxRate);
    }

    [Fact]
    public async Task Before_any_refresh_the_tiles_show_placeholders()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new DashboardViewModel(session);

        Assert.Equal("—", vm.CycleTime);
        Assert.Equal("—", vm.CyclicTxRate);
    }
}
