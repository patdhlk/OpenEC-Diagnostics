using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Synthesis;

public class PcapStreamWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Frame(byte payloadByte) => new EtherCatFrameBuilder()
        .AddDatagram(EtherCatCommand.Lrw, 1, 0x01000000, new byte[] { payloadByte, 2, 3, 4 }, 0)
        .Build();

    [Fact]
    public async Task Streamed_frames_read_back_identically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-stream-{Guid.NewGuid():N}.pcap");
        var frame1 = Frame(1);
        var frame2 = Frame(9);

        try
        {
            using (var writer = new PcapStreamWriter(path))
            {
                writer.Write(T0, frame1);
                writer.Write(T0.AddMilliseconds(1), frame2);
                Assert.Equal(2, writer.FramesWritten);
            }

            await using var source = new PcapFileSource(path);
            var frames = new List<RawFrame>();
            await foreach (var f in source.CaptureAsync()) frames.Add(f);

            Assert.Equal(2, frames.Count);
            Assert.Equal(frame1, frames[0].Data.ToArray());
            Assert.Equal(frame2, frames[1].Data.ToArray());
            Assert.Equal(T0, frames[0].Timestamp);
            Assert.Equal(T0.AddMilliseconds(1), frames[1].Timestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task File_is_valid_even_when_closed_after_a_single_frame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-stream-{Guid.NewGuid():N}.pcap");
        var frame = Frame(7);

        try
        {
            using (var writer = new PcapStreamWriter(path))
            {
                writer.Write(T0, frame);
            }

            await using var source = new PcapFileSource(path);
            var frames = new List<RawFrame>();
            await foreach (var f in source.CaptureAsync()) frames.Add(f);

            var single = Assert.Single(frames);
            Assert.Equal(frame, single.Data.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Batch_writer_still_produces_identical_output()
    {
        // PcapFileWriter is refactored to delegate to PcapStreamWriter — this pins the batch API.
        var path = Path.Combine(Path.GetTempPath(), $"openec-batch-{Guid.NewGuid():N}.pcap");
        var frame = Frame(5);

        try
        {
            PcapFileWriter.Write(path, new[] { (T0, frame) });

            await using var source = new PcapFileSource(path);
            var frames = new List<RawFrame>();
            await foreach (var f in source.CaptureAsync()) frames.Add(f);

            var single = Assert.Single(frames);
            Assert.Equal(frame, single.Data.ToArray());
            Assert.Equal(T0, single.Timestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
