namespace OpenEC.Monitor.Synthesis;

/// <summary>Writes classic little-endian pcap files (LINKTYPE_ETHERNET) with microsecond timestamps.</summary>
public static class PcapFileWriter
{
    public static void Write(string path, IEnumerable<(DateTimeOffset Timestamp, byte[] Frame)> frames)
    {
        using var writer = new PcapStreamWriter(path);
        foreach (var (ts, frame) in frames)
            writer.Write(ts, frame);
    }
}
