using System.Runtime.CompilerServices;
using SharpPcap;
using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

/// <summary>Reads pcap and pcapng files via SharpPcap.</summary>
public sealed class PcapFileSource(string path) : ICaptureSource
{
    /// <summary>Each call to <see cref="CaptureAsync"/> opens its own reader, so the file can be
    /// replayed as often as the caller likes.</summary>
    public bool SupportsMultiplePasses => true;

    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        using var device = PcapNative.Guard(() =>
        {
            var reader = new CaptureFileReaderDevice(path);
            reader.Open();
            return reader;
        });
        while (!ct.IsCancellationRequested && TryReadNext(device, out var frame))
        {
            yield return frame;
        }
    }

    // The ref struct PacketCapture must not be a local in the async iterator (that needs C# 13's
    // "ref/unsafe in async" feature). Keep it inside this synchronous helper; GetPacket() returns a
    // RawCapture whose Data is an owned byte[], so the RawFrame is safe to hand back.
    private static bool TryReadNext(CaptureFileReaderDevice device, out RawFrame frame)
    {
        if (device.GetNextPacket(out PacketCapture capture) == GetPacketStatus.PacketRead)
        {
            var raw = capture.GetPacket();
            var utc = DateTime.SpecifyKind(raw.Timeval.Date, DateTimeKind.Utc);
            frame = new RawFrame(new DateTimeOffset(utc), raw.Data);
            return true;
        }

        frame = default;
        return false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
