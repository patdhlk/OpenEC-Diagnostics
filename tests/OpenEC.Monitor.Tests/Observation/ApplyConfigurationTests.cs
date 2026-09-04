using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class ApplyConfigurationTests
{
    /// <summary>Learns the synthetic bringup, then hands the result to a fresh observer — the shape
    /// Task 3 wires up for a live session that started with no ENI.</summary>
    private static LearnedConfiguration LearnBringup()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!;
    }

    private static void Pump(BusObserver observer, int cycles = 5)
    {
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles))
            observer.Process(timestamp, EtherCatFrameParser.Parse(frame));
    }

    [Fact]
    public void Applying_a_configuration_names_the_slaves()
    {
        var observer = new BusObserver();
        Pump(observer);
        Assert.All(observer.SnapshotSlaves(), s => Assert.Null(s.ConfiguredName));

        observer.ApplyConfiguration(LearnBringup());

        var slave = observer.SnapshotSlaves().Single(s => s.Address == 1001);
        Assert.NotNull(slave.ConfiguredName);
        Assert.Equal(2u, slave.VendorId);
    }

    [Fact]
    public void Applying_a_configuration_maps_process_variables()
    {
        var observer = new BusObserver();
        Pump(observer);
        Assert.Empty(observer.ProcessImage.Current);

        observer.ApplyConfiguration(LearnBringup());
        Pump(observer);

        Assert.Equal(16, observer.ProcessImage.Current.Count);
    }

    /// <summary>Statistics and the event log are observations of the wire, not derivations of the
    /// configuration. A rebind that reset them would discard everything learned about bus health.</summary>
    [Fact]
    public void Applying_a_configuration_preserves_statistics_and_the_event_log()
    {
        var observer = new BusObserver();
        Pump(observer);

        // BringupCapture's only AL-status traffic is the broadcast BRD poll, which updates the
        // aggregate Bus.BusState without raising a MonitorEvent - so the plain pump above leaves
        // the event log empty and the assertions below would pass vacuously. One targeted
        // FPRD read of a station's AL status (exactly what a real master also issues, e.g. to
        // find out which slave is out of sync) gives the test a genuine SlaveStateChanged event
        // to prove survives the rebind, matching how BusObserverTests hand-crafts frames for its
        // own event-raising tests.
        var stateRead = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, new byte[] { 0x02, 0x00 }, 1)
            .Build();
        observer.Process(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(stateRead));

        var frames = observer.Statistics.EtherCatFrames;
        var events = observer.SnapshotEvents().Count;
        Assert.True(frames > 0);
        Assert.True(events > 0);

        observer.ApplyConfiguration(LearnBringup());

        Assert.Equal(frames, observer.Statistics.EtherCatFrames);
        Assert.Equal(events, observer.SnapshotEvents().Count);
    }

    [Fact]
    public void Applied_exposes_the_configuration_in_force()
    {
        var observer = new BusObserver();
        Assert.Null(observer.Applied);

        var learned = LearnBringup();
        observer.ApplyConfiguration(learned);

        Assert.Same(learned, observer.Applied);
    }

    /// <summary>ApplyConfiguration takes the same lock as Process, so a rebind arriving from the
    /// schema-resolution timer while the pump is mid-frame must not corrupt state or throw.</summary>
    [Fact]
    public async Task Applying_a_configuration_concurrently_with_processing_is_safe()
    {
        var observer = new BusObserver();
        var learned = LearnBringup();
        var frames = BringupCapture.Frames(cycles: 40).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var pump = Task.Run(() =>
        {
            for (var round = 0; round < 20 && !cts.IsCancellationRequested; round++)
                foreach (var (timestamp, frame) in frames)
                    observer.Process(timestamp, EtherCatFrameParser.Parse(frame));
        }, cts.Token);

        var rebind = Task.Run(() =>
        {
            for (var round = 0; round < 200 && !cts.IsCancellationRequested; round++)
            {
                observer.ApplyConfiguration(learned);
                Assert.NotNull(observer.SnapshotSlaves());
                Assert.NotNull(observer.SnapshotEvents());
            }
        }, cts.Token);

        await Task.WhenAll(pump, rebind);
        Assert.True(observer.Statistics.EtherCatFrames > 0);
    }

    /// <summary>ApplyConfiguration must rebind the health tracker's configured-device count, or a
    /// live session that started with no ENI would never flag a missing device after learning.</summary>
    [Fact]
    public void Applying_a_configuration_updates_the_health_device_count()
    {
        var observer = new BusObserver();

        var before = observer.SnapshotHealth();
        Assert.Null(before.ConfiguredDevices);        // no config → device count not enforced
        Assert.NotEqual(HealthLevel.Fault, before.Level);

        observer.ApplyConfiguration(LearnBringup());  // learns two configured slaves

        var after = observer.SnapshotHealth();
        Assert.Equal(2, after.ConfiguredDevices);     // rebind picked up the learned count
        Assert.Equal(0, after.FoundDevices);          // both seeded, neither seen on the wire
        Assert.Equal(HealthLevel.Fault, after.Level); // 2 configured vs 0 found → mismatch
    }
}
