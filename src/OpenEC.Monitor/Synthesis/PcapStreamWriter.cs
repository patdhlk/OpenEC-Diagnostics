namespace OpenEC.Monitor.Synthesis;

/// <summary>Incrementally writes a classic little-endian pcap file (LINKTYPE_ETHERNET,
/// microsecond timestamps). The format needs no trailer, so the file is valid after
/// every <see cref="Write"/> — suitable for recording a live capture as it happens.</summary>
public sealed class PcapStreamWriter : IDisposable
{
    private readonly BinaryWriter _writer;

    public PcapStreamWriter(string path)
    {
        _writer = new BinaryWriter(File.Create(path));
        _writer.Write(0xA1B2C3D4u);
        _writer.Write((ushort)2); _writer.Write((ushort)4);
        _writer.Write(0); _writer.Write(0u);
        _writer.Write(65535u);
        _writer.Write(1u); // LINKTYPE_ETHERNET
    }

    public long FramesWritten { get; private set; }

    public void Write(DateTimeOffset timestamp, ReadOnlySpan<byte> frame)
    {
        var micros = timestamp.ToUnixTimeMilliseconds() * 1000 + timestamp.Microsecond;
        _writer.Write((uint)(micros / 1_000_000));
        _writer.Write((uint)(micros % 1_000_000));
        _writer.Write((uint)frame.Length);
        _writer.Write((uint)frame.Length);
        _writer.Write(frame);
        FramesWritten++;
    }

    public void Dispose() => _writer.Dispose();
}
