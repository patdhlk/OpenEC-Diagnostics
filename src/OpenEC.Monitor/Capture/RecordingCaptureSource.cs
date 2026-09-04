using System.Runtime.CompilerServices;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Capture;

/// <summary>Decorates a capture source, teeing every frame into a pcap recording.
/// The writer opens on first enumeration; a write failure propagates to the consumer
/// (the pump), so a recording session faults rather than silently dropping data.</summary>
public sealed class RecordingCaptureSource(ICaptureSource inner, string recordPath) : ICaptureSource
{
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var writer = new PcapStreamWriter(recordPath);
        await foreach (var frame in inner.CaptureAsync(ct))
        {
            writer.Write(frame.Timestamp, frame.Data.Span);
            yield return frame;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
