using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests;

public class EtherCatMonitorTests
{
    private static string WriteScenarioPcap()
    {
        var frames = new List<(DateTimeOffset, byte[])>();
        var t = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        for (byte i = 0; i < 40; i += 2)
        {
            var cycle = i / 2;
            // Cycle 15 carries a WKC error; cycle 10 shows the drive dropping to SafeOp+error.
            ushort wkc = cycle == 15 ? (ushort)5 : (ushort)6;
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrw, i, 0x01000000, new byte[] { 0x01, 0x00, 0x0F, 0x00 }, 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(i + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
                .Build()));
            var returning = new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrw, i, 0x01000000, new byte[] { 0x01, 0x00, 0x37, 0x06 }, wkc)
                .AddPhysical(EtherCatCommand.Brd, (byte)(i + 1), 0, 0x0130, new byte[] { 0x08, 0x00 }, 4);
            frames.Add((t.AddMicroseconds(100), returning.Build()));
            if (cycle == 10)
                frames.Add((t.AddMicroseconds(200), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 100, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1)
                    .Build()));
            t = t.AddMilliseconds(1);
        }
        var path = Path.Combine(Path.GetTempPath(), $"openec-scenario-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, frames);
        return path;
    }

    [Fact]
    public async Task Analyzes_scenario_capture_end_to_end()
    {
        var path = WriteScenarioPcap();
        try
        {
            var eni = EniConfiguration.Load(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
            await using var monitor = EtherCatMonitor.OpenFile(path, new EtherCatMonitorOptions { Eni = eni });

            var events = new List<MonitorEvent>();
            var collector = Task.Run(async () =>
            {
                await foreach (var e in monitor.Events) events.Add(e);
            });
            await monitor.RunAsync();
            await collector;

            Assert.Equal(41, monitor.Observer.Statistics.EtherCatFrames);
            Assert.Equal(1, monitor.Observer.Statistics.WkcMismatches);
            Assert.Contains(events, e => e is MonitorEvent.WkcMismatchDetected);
            Assert.Contains(events, e => e is MonitorEvent.SlaveStateChanged s
                && s.Address == 1004 && s.NewState == SlaveAlState.SafeOp && s.ErrorFlag);
            Assert.Equal(SlaveAlState.Op, monitor.Bus.BusState); // via the EtherCatMonitor facade (spec §3.5)
            Assert.Equal((ushort)0x0637,
                monitor.Observer.ProcessImage.Current["Drive 4 (AX5101).Inputs.Statusword"].Value);
            Assert.NotNull(monitor.Observer.Statistics.EstimatedCycleTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolves_slave_names_from_the_esi_directory()
    {
        var path = WriteScenarioPcap();
        try
        {
            var eni = EniConfiguration.Load(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
            var esiDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi");
            await using var monitor = EtherCatMonitor.OpenFile(path,
                new EtherCatMonitorOptions { Eni = eni, EsiDirectory = esiDirectory });

            await monitor.RunAsync();

            Assert.True(monitor.Observer.Bus.TryGet(1002, out var slave));
            Assert.Equal("EL1008 8Ch. Dig. Input 24V, 3ms", slave!.ResolvedDeviceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_completes_the_events_channel_so_a_reader_cannot_hang()
    {
        var path = WriteScenarioPcap();
        try
        {
            var monitor = EtherCatMonitor.OpenFile(path);
            await monitor.DisposeAsync(); // never ran - RunAsync's own TryComplete never fired

            var drain = Task.Run(async () =>
            {
                var events = new List<MonitorEvent>();
                await foreach (var e in monitor.Events) events.Add(e);
                return events;
            });

            // Guard the assertion itself with a timeout: if DisposeAsync regressed and stopped
            // completing the channel, draining Events would hang forever instead of failing fast.
            var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(drain, finished);
            Assert.Empty(await drain);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Runs_without_eni()
    {
        var path = WriteScenarioPcap();
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path);
            await monitor.RunAsync();
            Assert.Equal(41, monitor.Observer.Statistics.EtherCatFrames);
            Assert.Empty(monitor.Observer.ProcessImage.Current);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Snapshot_health_reports_dc_sync_from_bringup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-health-{Guid.NewGuid():N}.pcap");
        BringupCapture.Write(path, 20);
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path, new EtherCatMonitorOptions
            {
                Learning = LearningMode.Off
            });
            await monitor.RunAsync();

            var health = monitor.SnapshotHealth();

            Assert.Equal(SlaveAlState.Op, health.BusState);
            Assert.True(health.BusStateUniform);
            Assert.Equal(2, health.FoundDevices);
            Assert.Equal(DcSyncState.Synced, health.DcSync);
            Assert.NotNull(health.MaxDcDeviationNs);
            Assert.True(health.MaxDcDeviationNs <= HealthTracker.DcSyncToleranceNs);
            Assert.Equal(HealthLevel.Ok, health.Level);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bus_health_changes_flow_through_the_event_stream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-health-events-{Guid.NewGuid():N}.pcap");
        BringupCapture.Write(path, 20);
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path);

            var events = new List<MonitorEvent>();
            var collector = Task.Run(async () =>
            {
                await foreach (var e in monitor.Events) events.Add(e);
            });
            await monitor.RunAsync();
            await collector;

            Assert.Contains(events, e => e is MonitorEvent.BusHealthChanged);
            Assert.Contains(events, e => e is MonitorEvent.BusHealthChanged h
                && h.Health.DcSync == DcSyncState.Synced);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
