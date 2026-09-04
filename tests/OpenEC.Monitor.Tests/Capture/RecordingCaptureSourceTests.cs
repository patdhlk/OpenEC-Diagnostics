using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Capture;

public class RecordingCaptureSourceTests
{
    [Fact]
    public async Task Frames_pass_through_unchanged_and_land_in_the_recording()
    {
        var demoPath = Path.Combine(Path.GetTempPath(), $"openec-rec-demo-{Guid.NewGuid():N}.pcap");
        var recordPath = Path.Combine(Path.GetTempPath(), $"openec-rec-out-{Guid.NewGuid():N}.pcap");
        SampleCapture.WriteDemo(demoPath);

        try
        {
            var passed = new List<RawFrame>();
            await using (var source = new RecordingCaptureSource(new PcapFileSource(demoPath), recordPath))
            {
                await foreach (var frame in source.CaptureAsync()) passed.Add(frame);
            }

            Assert.Equal(103, passed.Count);

            var recorded = new List<RawFrame>();
            await using (var replay = new PcapFileSource(recordPath))
            {
                await foreach (var frame in replay.CaptureAsync()) recorded.Add(frame);
            }

            Assert.Equal(103, recorded.Count);
            Assert.Equal(passed[0].Data.ToArray(), recorded[0].Data.ToArray());
            Assert.Equal(passed[^1].Data.ToArray(), recorded[^1].Data.ToArray());
            Assert.Equal(passed[0].Timestamp, recorded[0].Timestamp);
        }
        finally
        {
            File.Delete(demoPath);
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    [Fact]
    public async Task Unwritable_record_path_surfaces_on_enumeration()
    {
        var demoPath = Path.Combine(Path.GetTempPath(), $"openec-rec-demo-{Guid.NewGuid():N}.pcap");
        SampleCapture.WriteDemo(demoPath);

        try
        {
            await using var source = new RecordingCaptureSource(
                new PcapFileSource(demoPath), "/nonexistent-dir/recording.pcap");

            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await foreach (var _ in source.CaptureAsync()) { }
            });
        }
        finally
        {
            File.Delete(demoPath);
        }
    }
}
