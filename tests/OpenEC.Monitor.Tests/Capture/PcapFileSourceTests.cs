using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Capture;

public class PcapFileSourceTests
{
    [Fact]
    public async Task Written_pcap_reads_back_with_timestamps_and_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-{Guid.NewGuid():N}.pcap");
        var t0 = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var frame1 = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, 1, 0x01000000, new byte[] { 1, 2, 3, 4 }, 0).Build();
        var frame2 = new EtherCatFrameBuilder().AsReturning()
            .AddDatagram(EtherCatCommand.Lrw, 1, 0x01000000, new byte[] { 5, 6, 7, 8 }, 6).Build();
        PcapFileWriter.Write(path, new[] { (t0, frame1), (t0.AddMilliseconds(1), frame2) });

        try
        {
            await using var source = new PcapFileSource(path);
            var frames = new List<RawFrame>();
            await foreach (var f in source.CaptureAsync()) frames.Add(f);

            Assert.Equal(2, frames.Count);
            Assert.Equal(frame1, frames[0].Data.ToArray());
            Assert.Equal(frame2, frames[1].Data.ToArray());
            Assert.Equal(t0, frames[0].Timestamp);
            var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(frames[1].Data));
            Assert.True(ok.Frame.Source.IsLocallyAdministered);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Live_source_with_unknown_interface_throws()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            var enumerator = new LiveCaptureSource("openec-does-not-exist-0").CaptureAsync().GetAsyncEnumerator();
            enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void Device_listing_does_not_throw()
    {
        var devices = CaptureDevices.List();
        Assert.NotNull(devices);
    }
}
