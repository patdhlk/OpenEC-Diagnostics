using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Capture;

public class MultiplePassesTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"passes-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void Only_file_sources_advertise_multiple_passes()
    {
        var path = BringupCapture.Write(Path.Combine(_directory, "b.pcap"), cycles: 3);

        Assert.True(new PcapFileSource(path).SupportsMultiplePasses);
        // LiveCaptureSource and RecordingCaptureSource rely on the interface's default (false)
        // rather than declaring the member themselves, and C# only exposes a default interface
        // member through the interface type - hence the casts.
        Assert.False(((ICaptureSource)new LiveCaptureSource("nonexistent0")).SupportsMultiplePasses);
        // Re-enumerating a recording decorator would re-record, so it must stay single-pass.
        Assert.False(((ICaptureSource)new RecordingCaptureSource(new PcapFileSource(path),
            Path.Combine(_directory, "rec.pcap"))).SupportsMultiplePasses);
    }

    /// <summary>Pins what the discovery pass actually buys: ordering-independence. A real master
    /// configures FMMUs and PDOs before process data starts, so on a normally-ordered capture a
    /// single pass already maps everything and the discovery pass is unobservable. Here the cyclic
    /// frames come FIRST, before the configuration that explains them — so a single pass has nothing
    /// to decode them with and maps nothing, while two passes map all sixteen.</summary>
    [Fact]
    public async Task Two_pass_maps_process_data_a_single_pass_would_miss()
    {
        var frames = BringupCapture.Frames(cycles: 10).ToList();
        var cyclicCount = 20;   // the last 10 cycles, two frames each
        var reordered = frames.TakeLast(cyclicCount)
            .Concat(frames.Take(frames.Count - cyclicCount))
            .ToList();

        var path = Path.Combine(_directory, "reordered.pcap");
        PcapFileWriter.Write(path, reordered);

        // File source: discovery pass runs, so pass 2 decodes the leading cyclic frames.
        await using var twoPass = EtherCatMonitor.OpenFile(path);
        await twoPass.RunAsync();

        // Live-shaped source over the same frames: single pass, nothing to decode them with yet.
        await using var singlePass = EtherCatMonitor.FromSource(new ReplaySource(reordered));
        await singlePass.RunAsync();

        Assert.Equal(16, twoPass.ProcessImage.Current.Count);
        Assert.Empty(singlePass.ProcessImage.Current);
    }

    [Fact]
    public async Task Two_pass_does_not_double_count_frames()
    {
        var path = BringupCapture.Write(Path.Combine(_directory, "count.pcap"), cycles: 10);
        var expected = BringupCapture.Frames(cycles: 10).Count;

        await using var monitor = EtherCatMonitor.OpenFile(path);
        await monitor.RunAsync();

        Assert.Equal(expected, monitor.Statistics.TotalFrames);
    }

    /// <summary>The discovery pass used to touch Statistics not at all, so cancelling partway through
    /// a large offline capture left FramesSeen at zero — next to a populated device tree and a
    /// messages panel full of learning events. Three statements on one screen contradicting each
    /// other, and the only one the user could check was the false one.
    ///
    /// The counters are reset when the decode pass begins, which is what keeps the completed-run
    /// invariant above (exactly one traversal) true while the discovery pass still counts.</summary>
    [Fact]
    public async Task A_run_cancelled_inside_the_discovery_pass_still_reports_the_frames_it_saw()
    {
        var frames = BringupCapture.Frames(cycles: 10).ToList();
        using var cts = new CancellationTokenSource();
        // Past the ring scan and the station-address assignment that ends it. A real bringup reads
        // identity and topology by auto-increment first and names the slaves last, so before that
        // point the learner has facts but nothing to hang them on and publishes no configuration —
        // cancelling earlier would be asserting on a picture no observer could have yet.
        const int seen = 60;
        var source = new CancellingMultiPassSource(frames, cts, cancelAfter: seen);
        Assert.True(frames.Count > seen); // otherwise the run would finish rather than be cancelled

        await using var monitor = EtherCatMonitor.FromSource(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(seen, monitor.Statistics.TotalFrames);
        // And the learner really was running during those frames — the pass is not merely counting.
        Assert.NotNull(monitor.Learned);
    }

    /// <summary>A multi-pass source that cancels the caller's own token partway through its FIRST
    /// enumeration, so the run ends inside the discovery pass and the decode pass never begins.</summary>
    private sealed class CancellingMultiPassSource(
        IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> frames,
        CancellationTokenSource cts, int cancelAfter) : ICaptureSource
    {
        public bool SupportsMultiplePasses => true;

        public async IAsyncEnumerable<RawFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var emitted = 0;
            foreach (var (timestamp, frame) in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return new RawFrame(timestamp, frame);
                if (++emitted == cancelAfter) cts.Cancel();
            }
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Replays frames without advertising multiple passes, so the monitor takes the
    /// single-pass live route over a fixed frame list.</summary>
    private sealed class ReplaySource(IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> frames)
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
}
