using System.Buffers.Binary;

namespace OpenEC.Monitor.Protocol;

public static class EtherCatFrameParser
{
    public const ushort EtherCatEtherType = 0x88A4;

    public static FrameDecodeResult Parse(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length < 14)
            return new FrameDecodeResult.Malformed("frame shorter than Ethernet header");
        var dst = MacAddress.FromBytes(span[..6]);
        var src = MacAddress.FromBytes(span[6..12]);
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(span[12..]);
        ushort? vlanId = null;
        var offset = 14;
        if (etherType == 0x8100)
        {
            if (span.Length < 18)
                return new FrameDecodeResult.Malformed("VLAN tag truncated");
            vlanId = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(span[14..]) & 0x0FFF);
            etherType = BinaryPrimitives.ReadUInt16BigEndian(span[16..]);
            offset = 18;
        }
        if (etherType != EtherCatEtherType)
            return new FrameDecodeResult.NotEtherCat(etherType);
        if (span.Length < offset + 2)
            return new FrameDecodeResult.Malformed("EtherCAT frame header truncated");
        var header = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
        var length = header & 0x07FF;
        var protocolType = header >> 12;
        if (protocolType != 1)
            return new FrameDecodeResult.Malformed($"unsupported EtherCAT protocol type {protocolType}");
        if (span.Length < offset + 2 + length)
            return new FrameDecodeResult.Malformed("EtherCAT datagram area truncated");
        try
        {
            var datagrams = DatagramParser.ParseChain(frame.Slice(offset + 2, length));
            return new FrameDecodeResult.Success(new EtherCatFrame(dst, src, vlanId, datagrams));
        }
        catch (MalformedFrameException ex)
        {
            return new FrameDecodeResult.Malformed(ex.Message);
        }
    }
}
