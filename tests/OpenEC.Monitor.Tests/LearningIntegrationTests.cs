using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests;

public class LearningIntegrationTests
{
    /// <summary>Replays the synthetic bringup frame by frame through a live-shaped source, so the
    /// monitor cannot take the offline two-pass route.</summary>
    private sealed class LiveShapedSource(IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> frames)
        : ICaptureSource
    {
        public async IAsyncEnumerable<RawFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var (timestamp, frame) in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return new RawFrame(timestamp, frame);
            }
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task With_no_eni_the_learned_configuration_drives_the_process_image()
    {
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source);

        await monitor.RunAsync();

        Assert.NotNull(monitor.Learned);
        Assert.Equal(16, monitor.Learned!.Configuration.Variables.Count);
        // Learning converges during startup, so the cyclic frames that follow are mapped.
        Assert.NotEmpty(monitor.ProcessImage.Current);
    }

    [Fact]
    public async Task Learning_off_leaves_the_monitor_exactly_as_it_was()
    {
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Learning = LearningMode.Off });

        await monitor.RunAsync();

        Assert.Null(monitor.Learned);
        Assert.Empty(monitor.ProcessImage.Current);
        Assert.True(monitor.Statistics.EtherCatFrames > 0);
    }

    /// <summary>With an ENI supplied the ENI drives: the learner still runs (Task 4 cross-checks
    /// with it) but must not rebind the observer out from under the declared configuration.</summary>
    [Fact]
    public async Task With_an_eni_the_learner_runs_but_does_not_rebind()
    {
        var eni = EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Eni = eni });

        await monitor.RunAsync();

        Assert.NotNull(monitor.Learned);
        Assert.Null(monitor.Observer.Applied);
    }

    /// <summary>The reviewer's table, as one test. RunAsync awaits a final ResolveSchemasAsync after
    /// the capture loop; that forces a republish, which rebinds, and ProcessImage.Rebind used to clear
    /// every decoded value. On a live-shaped source no frames follow, so nothing repopulated: the same
    /// 30-cycle bringup gave 16 values live-shaped without ESI, 0 live-shaped WITH ESI, and 16 through
    /// the offline two-pass route. Supplying vendor files made the process image empty.</summary>
    [Fact]
    public async Task A_live_session_with_an_esi_directory_still_ends_with_a_process_image()
    {
        var esiDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi");

        await using var withoutEsi = EtherCatMonitor.FromSource(
            new LiveShapedSource(BringupCapture.Frames(cycles: 30)));
        await withoutEsi.RunAsync();

        await using var withEsi = EtherCatMonitor.FromSource(
            new LiveShapedSource(BringupCapture.Frames(cycles: 30)),
            new EtherCatMonitorOptions { EsiDirectory = esiDirectory });
        await withEsi.RunAsync();

        Assert.Equal(16, withoutEsi.ProcessImage.Current.Count);
        Assert.Equal(16, withEsi.ProcessImage.Current.Count);
        // And the point of supplying ESI at all: the values carried over onto the RENAMED variables,
        // so they are readable under the ESI-derived names rather than the synthetic ones.
        Assert.Contains(withEsi.ProcessImage.Current.Keys, k => k.Contains("Channel 1.Input 1"));
    }

    /// <summary>Exercises the periodic resolver and the final pass end to end. With no ESI resolution
    /// the slave would be named "Slave 1001", so an ESI-derived name is proof the resolver ran and
    /// its results were integrated.</summary>
    [Fact]
    public async Task Esi_resolution_runs_alongside_the_pump_and_names_the_slaves()
    {
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source, new EtherCatMonitorOptions
        {
            EsiDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"),
        });

        await monitor.RunAsync();

        var slave = monitor.Learned!.Configuration.Slaves.Single(s => s.PhysAddr == 1001);
        Assert.Contains("EL1008", slave.Name);
    }

    [Fact]
    public async Task A_mismatched_eni_raises_config_mismatch_events()
    {
        // sample.eni.xml declares four slaves at 1001-1004; the bringup fixture has two at 1001-1002.
        var eni = EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));
        var source = new LiveShapedSource(BringupCapture.Frames(cycles: 30));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Eni = eni });

        await monitor.RunAsync();

        var mismatches = monitor.Observer.SnapshotEvents()
            .OfType<MonitorEvent.ConfigMismatch>().ToList();
        Assert.NotEmpty(mismatches);
        Assert.Contains(mismatches, m => m.Kind == ConfigMismatchKind.SlaveMissing);
    }

    /// <summary>Defect B's gate, exercised through the whole pipeline: a mid-run attach never sees
    /// the station-address assignment that flips SawStartup, so completeness can never be reached —
    /// and reporting "declared slave 1006 not seen on the bus" under those conditions would be a
    /// guess dressed as a finding. Identity needs no such gate: station 1005 is already on the wire,
    /// answering SII reads, and its identity disagreement is true the moment it is observed.</summary>
    [Fact]
    public async Task A_mid_run_attach_reports_identity_but_never_a_missing_slave()
    {
        var declared = new EniConfiguration
        {
            Slaves =
            [
                new EniSlave("Term (EL9999)", 1005, 0, 2, 0x11112222, 0x00010000, null, null),
                new EniSlave("Term (never observed)", 1006, 0, 2, 0x11112222, 0x00010000, null, null),
            ],
            CyclicCommands = [],
            Variables = [],
        };
        var source = new LiveShapedSource(MidRunIdentityOnlyFrames(station: 1005,
            vendorId: 2, productCode: 0x99999999));
        await using var monitor = EtherCatMonitor.FromSource(source,
            new EtherCatMonitorOptions { Eni = declared });

        await monitor.RunAsync();

        Assert.False(monitor.Learned!.Completeness.SawStartup);
        var mismatches = monitor.Observer.SnapshotEvents()
            .OfType<MonitorEvent.ConfigMismatch>().ToList();
        Assert.Contains(mismatches, m => m.Kind == ConfigMismatchKind.Identity && m.Address == (ushort?)1005);
        Assert.DoesNotContain(mismatches, m => m.Kind == ConfigMismatchKind.SlaveMissing);
    }

    /// <summary>Mirrors <see cref="BringupCapture"/>'s SII identity readback for a single station,
    /// but with no preceding station-address assignment — exactly what a slave that was already
    /// configured before this capture started looks like on the wire.</summary>
    private static IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> MidRunIdentityOnlyFrames(
        ushort station, uint vendorId, uint productCode)
    {
        var frames = new List<(DateTimeOffset, byte[])>();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        byte idx = 0;

        void Emit(EtherCatFrameBuilder outbound, EtherCatFrameBuilder returning)
        {
            frames.Add((t, outbound.Build()));
            frames.Add((t.AddMicroseconds(60), returning.Build()));
            t = t.AddMicroseconds(250);
        }

        foreach (var (word, value) in new (uint, uint)[]
                 {
                     (0x0008, vendorId), (0x000A, productCode), (0x000C, 0u), (0x000E, 0u),
                 })
        {
            var request = new byte[6];
            BitConverter.GetBytes((ushort)0x0100).CopyTo(request, 0);   // read command
            BitConverter.GetBytes(word).CopyTo(request, 2);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 1));
            idx++;

            var answer = BitConverter.GetBytes(value);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, 0x0508, new byte[4], 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, 0x0508, answer, 1));
            idx++;
        }

        return frames;
    }
}
