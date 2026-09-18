using System.Buffers.Binary;

namespace OpenEC.Monitor.Protocol;

/// <summary>Where the 16-byte ESL block sits relative to the frame it wraps. The CU2508 driver
/// prefixes it (its cookie occupies the destination-MAC slot on the wire); the ET2000 probe
/// postfixes it. Purely informational — both decode identically.</summary>
public enum EslPlacement { Prefix, Postfix }

/// <summary>Metadata recovered from a Beckhoff ESL (EtherCAT Switch Link) wrapper. A CU2508
/// multiplexes up to eight independent EtherCAT segments onto one gigabit uplink, tagging every
/// frame with the physical downlink port it entered or left by; <see cref="Port"/> is that tag and
/// is what lets a passive monitor tell the segments apart. Byte layout per the Beckhoff ESL
/// specification and the Wireshark <c>packet-esl.c</c> dissector.</summary>
public readonly record struct EslMetadata(
    byte Port,
    ulong Timestamp,
    bool TimestampValid,
    bool CrcError,
    bool AlignError,
    bool Extended,
    EslPlacement Placement);

/// <summary>Detects and strips the ESL wrapper, exposing the inner Ethernet frame and the
/// port/timestamp/error metadata the switch stamped on it.</summary>
public static class EslHeader
{
    /// <summary>Fixed 16-byte control block: 6-byte cookie, 2-byte ctrl/state, 8-byte timestamp.</summary>
    public const int Size = 16;

    /// <summary>Port index returned when no port bit is set — malformed, never seen on real hardware.</summary>
    public const byte UnknownPort = 0xFF;

    // Ctrl/State is a little-endian u16 at offset 6. Bit assignments per packet-esl.c.
    private const ushort TimeStampEnaBit = 0x2000;
    private const ushort CrcErrorBit = 0x1000;
    private const ushort AlignErrorBit = 0x0800;
    private const ushort ExtendedBit = 0x0100;

    // Port bitmap → index. Scanned in port order, matching the dissector's flags_to_port(): exactly
    // one bit is set per frame, so the first match is the port. Ports 0–7 live in the low byte,
    // 8/9 in the high byte's top bits, 10/11 in the high byte's low bits.
    private static readonly (byte Port, ushort Mask)[] PortMasks =
    {
        (0, 0x0080), (1, 0x0040), (2, 0x0020), (3, 0x0010),
        (4, 0x0008), (5, 0x0004), (6, 0x0002), (7, 0x0001),
        (8, 0x8000), (9, 0x4000), (10, 0x0400), (11, 0x0200),
    };

    // The cookie occupies bytes 0..5 (the destination-MAC slot). Beckhoff's fixed value is
    // 01-01-05-10-00-00; the fourth octet is 0x10 or 0x11 (accepted by the dissector too).
    private static bool IsCookie(ReadOnlySpan<byte> s) =>
        s.Length >= 6 && s[0] == 0x01 && s[1] == 0x01 && s[2] == 0x05
        && (s[3] == 0x10 || s[3] == 0x11) && s[4] == 0x00 && s[5] == 0x00;

    /// <summary>If <paramref name="frame"/> carries an ESL wrapper (cookie at the front for a
    /// prefix, or in the trailing 16 bytes for a postfix), strips it: <paramref name="meta"/> holds
    /// the decoded control block and <paramref name="inner"/> is the wrapped Ethernet frame ready
    /// for ordinary parsing. Returns false and leaves <paramref name="inner"/> = <paramref name="frame"/>
    /// for a plain frame.</summary>
    public static bool TryPeel(ReadOnlyMemory<byte> frame, out EslMetadata meta, out ReadOnlyMemory<byte> inner)
    {
        var span = frame.Span;
        if (span.Length >= Size && IsCookie(span))
        {
            meta = Decode(span[..Size], EslPlacement.Prefix);
            inner = frame[Size..];
            return true;
        }
        // A postfix wrapper still needs a non-empty inner frame in front of it.
        if (span.Length > Size && IsCookie(span[^Size..]))
        {
            meta = Decode(span[^Size..], EslPlacement.Postfix);
            inner = frame[..^Size];
            return true;
        }
        meta = default;
        inner = frame;
        return false;
    }

    private static EslMetadata Decode(ReadOnlySpan<byte> header, EslPlacement placement)
    {
        var ctrl = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        return new EslMetadata(
            Port: PortOf(ctrl),
            Timestamp: timestamp,
            TimestampValid: (ctrl & TimeStampEnaBit) != 0,
            CrcError: (ctrl & CrcErrorBit) != 0,
            AlignError: (ctrl & AlignErrorBit) != 0,
            Extended: (ctrl & ExtendedBit) != 0,
            Placement: placement);
    }

    private static byte PortOf(ushort ctrl)
    {
        foreach (var (port, mask) in PortMasks)
            if ((ctrl & mask) != 0)
                return port;
        return UnknownPort;
    }
}
