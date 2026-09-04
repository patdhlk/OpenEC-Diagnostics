using OpenEC.Monitor.Observation;

namespace OpenEC.Monitor.Tests.Observation;

/// <summary>The fault this check was built for, captured off real hardware.
///
/// `stalled-slave.pcap` is a window from a 16-slave EtherCAT bus in which one device stopped
/// returning updated values, a controller reset did not recover it, and only power-cycling that
/// device did. Slave 1004 is the affected device. Through the whole window it answers every
/// datagram, keeps every ESC error counter at zero and sits in OP alongside everything else — and
/// never once changes a byte of its process data, including straight through a master-driven restart
/// of the entire bus. Its EtherCAT chip was fine; the application behind it had stopped, which is why
/// re-running the state machine changed nothing and only removing power did. The capture is
/// anonymized: the master MAC's device-specific bytes are rewritten (its direction bit preserved) and
/// it carries no device serial numbers.
///
/// Real frames at their captured timestamps, thinned to the FMMU-configuration traffic plus a sparse
/// sample of the cyclic stream. Thinning changes how often each window is sampled and nothing about
/// what it contained, which is what this is asserting on.</summary>
public class RealStalledSlaveTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "stalled-slave.pcap");

    private static async Task<EtherCatMonitor> RunAsync(TimeSpan? staleAfter = null)
    {
        var monitor = EtherCatMonitor.OpenFile(Fixture, new EtherCatMonitorOptions
        {
            StaleProcessDataAfter = staleAfter ?? TimeSpan.FromSeconds(45),
        });
        await monitor.RunAsync();
        return monitor;
    }

    [Fact]
    public async Task The_hung_box_is_the_only_slave_reported_stale()
    {
        await using var monitor = await RunAsync();

        var health = monitor.Observer.SnapshotHealth();
        Assert.Equal([(ushort)1004], health.Stale);
        Assert.Equal(HealthLevel.Warning, health.Level);
    }

    /// <summary>Zero changes, not merely few. Every other slave on this bus moved at least once in
    /// the same window, including the twelve identical sensors that sit idle for long stretches —
    /// which is exactly the distinction a stale check has to get right to be worth having.</summary>
    [Fact]
    public async Task The_hung_box_never_changes_a_byte_while_every_other_slave_does()
    {
        await using var monitor = await RunAsync();
        var activity = monitor.Observer.SnapshotProcessData();

        var hung = Assert.Single(activity, a => a.Address == 1004);
        Assert.Equal(0, hung.Changes);
        Assert.True(hung.Samples > 100, $"only {hung.Samples} samples — the fixture got thinner");
        Assert.True(hung.IsStale);

        Assert.All(activity.Where(a => a.Address != 1004), a =>
        {
            Assert.True(a.Changes > 0, $"slave {a.Address} never changed either");
            Assert.False(a.IsStale, $"slave {a.Address} was reported stale");
        });
    }

    /// <summary>What the threshold actually buys, measured on this bus rather than assumed.
    ///
    /// The twelve identical sensors here update rarely — they sit unchanged for roughly forty
    /// seconds at a stretch — so a threshold below that names them alongside the box that is
    /// genuinely dead, and the report stops distinguishing anything. Above it, only 1004 is left.
    /// The gap between "idle" and "stopped" is a property of the machine, not of the code, which is
    /// why this is an option with a generous default rather than a constant.</summary>
    [Fact]
    public async Task The_threshold_is_what_separates_an_idle_device_from_a_stopped_one()
    {
        await using var tooShort = await RunAsync(TimeSpan.FromSeconds(20));
        await using var longEnough = await RunAsync(TimeSpan.FromSeconds(45));

        // Under the sensors' own idle period, everything that is quiet looks the same.
        var shortStale = tooShort.Observer.SnapshotHealth().Stale;
        Assert.Contains((ushort)1004, shortStale);
        Assert.True(shortStale.Count > 1,
            "at 20s the idle sensors should trip too — if they no longer do, the fixture changed");

        // Above it, the only slave left is the one that never moved at all.
        Assert.Equal([(ushort)1004], longEnough.Observer.SnapshotHealth().Stale);
    }

    /// <summary>Disabling the threshold leaves the observation intact, so the numbers that identify
    /// the box are still readable even with the verdict switched off.</summary>
    [Fact]
    public async Task Disabling_the_check_keeps_the_activity_readable()
    {
        var monitor = EtherCatMonitor.OpenFile(Fixture,
            new EtherCatMonitorOptions { StaleProcessDataAfter = null });
        await monitor.RunAsync();
        await using var _ = monitor;

        Assert.Empty(monitor.Observer.SnapshotEvents().OfType<MonitorEvent.ProcessDataStalled>());
        Assert.Equal(HealthLevel.Ok, monitor.Observer.SnapshotHealth().Level);
        Assert.Equal(0, Assert.Single(monitor.Observer.SnapshotProcessData(),
            a => a.Address == 1004).Changes);
    }
}
