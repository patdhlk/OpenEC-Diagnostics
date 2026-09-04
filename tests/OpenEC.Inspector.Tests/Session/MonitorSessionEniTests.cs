using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.Session;

public class MonitorSessionEniTests
{
    [Fact]
    public async Task Eni_seeds_the_topology_with_all_configured_slaves()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var slaves = session.Observer.SnapshotSlaves();
        Assert.Equal(4, slaves.Count);
        var drive = Assert.Single(slaves, s => s.Address == 1004);
        Assert.Equal("Drive 4 (AX5101)", drive.DisplayName);
        Assert.Equal(SlaveAlState.SafeOp, drive.AlState);
        Assert.True(drive.ErrorFlag);
    }

    [Fact]
    public async Task Eni_session_raises_the_expected_event_kinds()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var events = session.Observer.SnapshotEvents();
        Assert.Contains(events, e => e is MonitorEvent.SlaveStateChanged
        {
            Address: 1004, NewState: SlaveAlState.SafeOp, ErrorFlag: true,
        });
        Assert.Contains(events, e => e is MonitorEvent.WkcMismatchDetected { Expected: 6, Actual: 5 });
        Assert.Contains(events, e => e is MonitorEvent.EmergencyReceived { StationAddress: 1004 });
        Assert.Contains(events, e => e is MonitorEvent.SoeErrorReceived { StationAddress: 1004, ErrorCode: 0x7009 });
        Assert.Equal(1, session.Statistics.WkcMismatches);
    }

    [Fact]
    public async Task Eni_session_decodes_all_five_process_variables()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());

        var pv = session.ProcessImage.Current;
        Assert.Equal(5, pv.Count);
        Assert.Equal(true, pv["Term 2 (EL1008).Channel 1.Input"].Value);
        Assert.Equal(false, pv["Term 2 (EL1008).Channel 2.Input"].Value);
        Assert.Equal((ushort)0x0637, pv["Drive 4 (AX5101).Inputs.Statusword"].Value);
        Assert.Equal(true, pv["Term 3 (EL2008).Channel 1.Output"].Value);
        Assert.Equal((ushort)0x000F, pv["Drive 4 (AX5101).Outputs.Controlword"].Value);
        Assert.NotNull(pv["Drive 4 (AX5101).Inputs.Statusword"].Cia402Description);
    }

    [Fact]
    public async Task Without_eni_the_process_image_stays_empty()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Empty(session.ProcessImage.Current);
        Assert.Null(session.Eni);
    }
}
