using System.Buffers.Binary;

namespace OpenEC.Monitor.Protocol;

public static class DatagramParser
{
    /// <summary>Parses the datagram area of an EtherCAT frame (after the 2-byte frame header).</summary>
    public static IReadOnlyList<EtherCatDatagram> ParseChain(ReadOnlyMemory<byte> data)
    {
        var result = new List<EtherCatDatagram>();
        var span = data.Span;
        var offset = 0;
        while (true)
        {
            if (data.Length - offset < 12)
                throw new MalformedFrameException($"datagram header truncated at offset {offset}");
            var cmdByte = span[offset];
            if (cmdByte > 14)
                throw new MalformedFrameException($"unknown datagram command 0x{cmdByte:X2}");
            var idx = span[offset + 1];
            var address = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 2)..]);
            var lenField = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 6)..]);
            var len = lenField & 0x07FF;
            var circulating = (lenField & 0x4000) != 0;
            var more = (lenField & 0x8000) != 0;
            var irq = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 8)..]);
            if (data.Length - offset < 12 + len)
                throw new MalformedFrameException($"datagram payload truncated at offset {offset}");
            var payload = data.Slice(offset + 10, len);
            var wkc = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 10 + len)..]);
            result.Add(new EtherCatDatagram((EtherCatCommand)cmdByte, idx, address,
                circulating, more, irq, payload, wkc));
            offset += 12 + len;
            if (!more) break;
        }
        return result;
    }
}
