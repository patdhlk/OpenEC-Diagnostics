using System.Buffers.Binary;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Wraps a plain Ethernet frame in a Beckhoff ESL header — as a CU2508 (prefix) or an
/// ET2000 probe (postfix) would — for tests and generated sample captures. The exact inverse of
/// <see cref="Protocol.EslHeader"/>.</summary>
public static class EslWrapper
{
    // Port index → one-hot ctrl bit, mirroring EslHeader.PortMasks.
    private static readonly ushort[] PortMasks =
    {
        0x0080, 0x0040, 0x0020, 0x0010, 0x0008, 0x0004, 0x0002, 0x0001,
        0x8000, 0x4000, 0x0400, 0x0200,
    };

    private const ushort TimeStampEnaBit = 0x2000;
    private const ushort CrcErrorBit = 0x1000;
    private const ushort AlignErrorBit = 0x0800;

    /// <summary>Prepends the 16-byte ESL block (CU2508 layout): its cookie lands in the
    /// destination-MAC slot on the wire.</summary>
    public static byte[] Prefix(byte[] innerFrame, byte port, ulong timestamp = 0,
        bool timestampValid = false, bool crcError = false, bool alignError = false, byte cookie4 = 0x10)
    {
        var header = BuildHeader(port, timestamp, timestampValid, crcError, alignError, cookie4);
        var result = new byte[header.Length + innerFrame.Length];
        header.CopyTo(result, 0);
        innerFrame.CopyTo(result, header.Length);
        return result;
    }

    /// <summary>Appends the 16-byte ESL block (ET2000 layout) after the wrapped frame.</summary>
    public static byte[] Postfix(byte[] innerFrame, byte port, ulong timestamp = 0,
        bool timestampValid = false, bool crcError = false, bool alignError = false, byte cookie4 = 0x10)
    {
        var header = BuildHeader(port, timestamp, timestampValid, crcError, alignError, cookie4);
        var result = new byte[innerFrame.Length + header.Length];
        innerFrame.CopyTo(result, 0);
        header.CopyTo(result, innerFrame.Length);
        return result;
    }

    private static byte[] BuildHeader(byte port, ulong timestamp, bool timestampValid,
        bool crcError, bool alignError, byte cookie4)
    {
        if (port >= PortMasks.Length)
            throw new ArgumentOutOfRangeException(nameof(port), port, "ESL port must be 0..11");
        var ctrl = PortMasks[port];
        if (timestampValid) ctrl |= TimeStampEnaBit;
        if (crcError) ctrl |= CrcErrorBit;
        if (alignError) ctrl |= AlignErrorBit;

        var header = new byte[16];
        header[0] = 0x01;
        header[1] = 0x01;
        header[2] = 0x05;
        header[3] = cookie4;
        header[4] = 0x00;
        header[5] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), ctrl);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), timestamp);
        return header;
    }
}
