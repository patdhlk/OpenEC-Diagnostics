using System.Buffers.Binary;
using System.Text;

namespace OpenEC.Monitor.Protocol;

public static class MailboxParser
{
    /// <summary>Attempts to interpret a datagram payload as an EtherCAT mailbox. Null when implausible.</summary>
    public static MailboxMessage? TryParse(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 6) return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var station = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        var channel = (byte)(span[4] & 0x3F);
        var priority = (byte)(span[4] >> 6);
        var typeByte = (byte)(span[5] & 0x0F);
        var counter = (byte)((span[5] >> 4) & 0x07);
        if (length == 0 || length > span.Length - 6) return null;
        if (typeByte is > 5 and not 15) return null;
        var type = (MailboxType)typeByte;
        var body = payload.Slice(6, length);
        return new MailboxMessage(length, station, channel, priority, type, counter, body,
            type == MailboxType.Coe ? TryParseCoe(body) : null,
            type == MailboxType.Foe ? TryParseFoe(body) : null,
            type == MailboxType.Eoe ? TryParseEoe(body) : null,
            type == MailboxType.Soe ? TryParseSoe(body) : null);
    }

    /// <summary>Parses a CoE header plus, for the initiate/expedited SDO services, the fixed-layout
    /// SDO command byte, index and sub-index. Index/SubIndex are only meaningful for those PDUs
    /// (SdoRequest/SdoResponse with an expedited or initiate-segmented command specifier); for
    /// segment PDUs the equivalent bytes carry segment data rather than an index/sub-index, so
    /// this parser does not attempt to interpret segment transfers (header-level fidelity, M1).</summary>
    private static CoeMessage? TryParseCoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 2) return null;
        var header = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var number = (ushort)(header & 0x01FF);
        var serviceByte = (byte)(header >> 12);
        if (serviceByte is < 1 or > 8) return null;
        var service = (CoeService)serviceByte;
        SdoTransfer? sdo = null;
        CoeEmergency? emergency = null;
        if (service is CoeService.SdoRequest or CoeService.SdoResponse && span.Length >= 6)
        {
            var cs = span[2];
            sdo = new SdoTransfer(cs,
                Expedited: (cs & 0x02) != 0,
                SizeIndicated: (cs & 0x01) != 0,
                Index: BinaryPrimitives.ReadUInt16LittleEndian(span[3..]),
                SubIndex: span[5],
                Data: body.Length > 6 ? body[6..] : ReadOnlyMemory<byte>.Empty);
        }
        else if (service == CoeService.Emergency && span.Length >= 5)
        {
            emergency = new CoeEmergency(
                BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
                span[4],
                body.Length > 5 ? body[5..] : ReadOnlyMemory<byte>.Empty);
        }
        return new CoeMessage(number, service, sdo, emergency);
    }

    private static FoeMessage? TryParseFoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 6) return null;
        if (span[0] is < 1 or > 6) return null;
        var opCode = (FoeOpCode)span[0];
        var packet = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
        var data = body.Length > 6 ? body[6..] : ReadOnlyMemory<byte>.Empty;
        string? fileName = null, errorText = null;
        if (opCode is FoeOpCode.ReadRequest or FoeOpCode.WriteRequest)
            fileName = Encoding.ASCII.GetString(data.Span);
        else if (opCode == FoeOpCode.Error)
            errorText = Encoding.ASCII.GetString(data.Span);
        return new FoeMessage(opCode, packet, fileName, errorText, data);
    }

    /// <summary>Parses the fixed 4-byte SoE header (ETG.1000.5 / IEC 61800-7-204): opcode,
    /// incomplete/error bits, drive number, element flags and the IDN/fragments-left field.
    /// Everything after the header is exposed as raw data (header-level fidelity, M1).</summary>
    private static SoeMessage? TryParseSoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 4) return null;
        if ((span[0] & 0x07) is < 1 or > 6) return null;
        var error = (span[0] & 0x10) != 0;
        return new SoeMessage(
            OpCode: (SoeOpCode)(span[0] & 0x07),
            Incomplete: (span[0] & 0x08) != 0,
            Error: error,
            DriveNumber: (byte)(span[0] >> 5),
            Elements: (SoeElements)span[1],
            IdnOrFragmentsLeft: BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
            ErrorCode: error && span.Length >= 6
                ? BinaryPrimitives.ReadUInt16LittleEndian(span[^2..]) : null,
            Data: body.Length > 4 ? body[4..] : ReadOnlyMemory<byte>.Empty);
    }

    private static EoeFragment? TryParseEoe(ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 4) return null;
        var h1 = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var h2 = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        return new EoeFragment(
            FrameType: (byte)(h1 & 0x0F),
            Port: (byte)((h1 >> 4) & 0x0F),
            LastFragment: (h1 & 0x0100) != 0,
            TimeAppended: (h1 & 0x0200) != 0,
            FragmentNumber: (ushort)(h2 & 0x3F),
            OffsetOrBufferSize: (ushort)((h2 >> 6) & 0x3F),
            FrameNumber: (byte)(h2 >> 12));
    }
}
