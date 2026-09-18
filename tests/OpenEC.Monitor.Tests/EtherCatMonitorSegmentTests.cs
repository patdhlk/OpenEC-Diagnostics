using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests;

public class EtherCatMonitorSegmentTests
{
    private static byte[] Outbound() => new EtherCatFrameBuilder()
        .AddPhysical(EtherCatCommand.Fprd, 1, 1001, 0x0130, new byte[] { 0x00, 0x00 }, 0).Build();

    // A returning FPRD to AL-status 0x0130 (WKC 1) carrying the state byte: 0x08 = Op, 0x14 = SafeOp+error.
    private static byte[] Returning(byte status) => new EtherCatFrameBuilder().AsReturning()
        .AddPhysical(EtherCatCommand.Fprd, 1, 1001, 0x0130, new byte[] { status, 0x00 }, 1).Build();

    [Fact]
    public async Task Demultiplexes_colliding_addresses_into_independent_segments()
    {
        // Two CU2508 downlink segments, each with a slave at the SAME station address 1001 but in a
        // different AL state, multiplexed onto one uplink and told apart only by the ESL port tag.
        var t = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var frames = new List<(DateTimeOffset, byte[])>
        {
            (t, EslWrapper.Prefix(Outbound(), port: 0)),
            (t, EslWrapper.Prefix(Outbound(), port: 1)),
            (t.AddMicroseconds(50), EslWrapper.Prefix(Returning(0x08), port: 0)),
            (t.AddMicroseconds(50), EslWrapper.Prefix(Returning(0x14), port: 1)),
        };
        var path = Path.Combine(Path.GetTempPath(), $"openec-esl-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, frames);
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path,
                new EtherCatMonitorOptions { Learning = LearningMode.Off });
            await monitor.RunAsync();

            var segments = monitor.Segments.OrderBy(s => s.Port).ToList();
            Assert.Equal(2, segments.Count);
            Assert.Equal(new[] { 0, 1 }, segments.Select(s => s.Port).ToArray());

            var port0 = segments[0].Observer.SnapshotSlaves().Single(s => s.Address == 1001);
            var port1 = segments[1].Observer.SnapshotSlaves().Single(s => s.Address == 1001);

            // Had the two segments shared one observer, these would clobber each other.
            Assert.Equal(SlaveAlState.Op, port0.AlState);
            Assert.False(port0.ErrorFlag);
            Assert.Equal(SlaveAlState.SafeOp, port1.AlState);
            Assert.True(port1.ErrorFlag);

            // ESL traffic never leaks into the plain (-1) facade segment.
            Assert.Empty(monitor.Observer.SnapshotSlaves());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Plain_capture_yields_a_single_unlabelled_segment()
    {
        var t = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var frames = new List<(DateTimeOffset, byte[])>
        {
            (t, Outbound()),
            (t.AddMicroseconds(50), Returning(0x08)),
        };
        var path = Path.Combine(Path.GetTempPath(), $"openec-plain-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, frames);
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path,
                new EtherCatMonitorOptions { Learning = LearningMode.Off });
            await monitor.RunAsync();

            var segment = Assert.Single(monitor.Segments);
            Assert.Equal(-1, segment.Port);
            // The single-segment facade points at exactly this segment.
            Assert.Same(segment.Observer, monitor.Observer);
            Assert.Equal(SlaveAlState.Op, monitor.Observer.SnapshotSlaves().Single().AlState);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
