using System.Diagnostics;
using OpenEC.Monitor.Capture;

namespace OpenEC.Monitor.Tests.Capture;

public class ReplayCaptureSourceTests
{
    [Fact]
    public async Task Frames_pass_through_in_order_and_completely()
    {
        var controller = new ReplayController(startPaused: false, speed: 10);
        var frames = Frames(5, 10);
        await using var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);

        var received = new List<RawFrame>();
        await foreach (var frame in source.CaptureAsync())
        {
            received.Add(frame);
        }

        Assert.Equal(5, received.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((byte)i, received[i].Data.Span[0]);
        }
    }

    [Fact]
    public async Task A_paused_replay_emits_nothing_until_resumed()
    {
        var controller = new ReplayController(startPaused: true);
        var frames = Frames(3, 10);
        await using var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);

        var received = new List<RawFrame>();
        var receivedLock = new object();
        using var cts = new CancellationTokenSource();

        var pumpTask = Task.Run(async () =>
        {
            await foreach (var frame in source.CaptureAsync(cts.Token))
            {
                lock (receivedLock)
                {
                    received.Add(frame);
                }
            }
        });

        try
        {
            await Task.Delay(150);

            lock (receivedLock)
            {
                Assert.Empty(received);
            }

            controller.Resume();
            await pumpTask;

            lock (receivedLock)
            {
                Assert.Equal(3, received.Count);
            }
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task Higher_speed_replays_far_faster_than_realtime()
    {
        var controller = new ReplayController(startPaused: false, speed: 10);
        var frames = Frames(2, 1000.0);
        await using var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);

        var sw = Stopwatch.StartNew();
        await foreach (var _ in source.CaptureAsync()) { }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task Realtime_speed_cannot_outrun_the_capture()
    {
        var controller = new ReplayController(startPaused: false, speed: 1);
        var frames = Frames(2, 300.0);
        await using var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);

        var sw = Stopwatch.StartNew();
        await foreach (var _ in source.CaptureAsync()) { }
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Cancellation_stops_a_paused_replay_promptly()
    {
        var controller = new ReplayController(startPaused: true);
        var frames = Frames(3, 50);
        await using var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);

        using var cts = new CancellationTokenSource();

        var pumpTask = Task.Run(async () =>
        {
            await foreach (var _ in source.CaptureAsync(cts.Token)) { }
        });

        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask);
    }

    [Fact]
    public async Task A_step_releases_exactly_one_frame_and_stays_paused()
    {
        var controller = new ReplayController(startPaused: true);
        var frames = Frames(5, 50);            // 50ms gaps: free-running would take ~200ms
        var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);
        var emitted = new List<RawFrame>();
        var gate = new object();
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in source.CaptureAsync(cts.Token))
                lock (gate) emitted.Add(f);
        });
        try
        {
            await Task.Delay(100);
            lock (gate) Assert.Empty(emitted);                 // paused: nothing yet
            controller.Step();
            await WaitUntilAsync(() => { lock (gate) return emitted.Count >= 1; });
            await Task.Delay(100);                             // give any erroneous extra frame time to leak
            lock (gate) Assert.Single(emitted);               // exactly one; the 50ms gap was skipped and it re-paused
            controller.Step();
            controller.Step();
            await WaitUntilAsync(() => { lock (gate) return emitted.Count >= 3; });
            await Task.Delay(100);
            lock (gate) Assert.Equal(3, emitted.Count);       // queued steps advance exactly that many
            controller.Resume();
            await pump;                                        // free flow to EOF
            lock (gate) Assert.Equal(5, emitted.Count);
        }
        finally { cts.Cancel(); try { await pump; } catch { } }
    }

    [Fact]
    public async Task A_step_skips_the_inter_frame_delay()
    {
        var controller = new ReplayController(startPaused: true);
        var frames = Frames(2, 5000);
        var source = new ReplayCaptureSource(new ListCaptureSource(frames), controller);
        var emitted = new List<RawFrame>();
        var gate = new object();
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in source.CaptureAsync(cts.Token))
                lock (gate) emitted.Add(f);
        });
        try
        {
            await Task.Delay(50);
            lock (gate) Assert.Empty(emitted);
            var sw = Stopwatch.StartNew();
            controller.Step();
            await WaitUntilAsync(() => { lock (gate) return emitted.Count >= 1; });
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
            lock (gate) Assert.Single(emitted);
        }
        finally { cts.Cancel(); try { await pump; } catch { } }
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
    }


    static RawFrame[] Frames(int count, double gapMs)
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(i => new RawFrame(t0.AddMilliseconds(i * gapMs), new byte[] { (byte)i }))
            .ToArray();
    }
}

file sealed class ListCaptureSource(IReadOnlyList<RawFrame> frames) : ICaptureSource
{
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var f in frames) { ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return f; }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
