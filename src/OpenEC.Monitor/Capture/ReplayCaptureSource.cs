using System.Runtime.CompilerServices;

namespace OpenEC.Monitor.Capture;

/// <summary>Decorates a capture source, pacing frames to their original timestamps scaled
/// by the controller's speed, suspending delivery while paused, and releasing one frame per step.</summary>
public sealed class ReplayCaptureSource(ICaptureSource inner, ReplayController controller) : ICaptureSource
{
    // A paced replay is a single live-like pass; prevent two-pass discovery from re-running it.
    public bool SupportsMultiplePasses => false;

    // Pulling the first frame from inner happens before the gate, so a bad file faults immediately.
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset? prev = null;
        await foreach (var frame in inner.CaptureAsync(ct))
        {
            await controller.WaitToEmitAsync(prev is { } p ? frame.Timestamp - p : TimeSpan.Zero, ct);
            prev = frame.Timestamp;
            yield return frame;
        }
    }


    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
