using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class MailboxParserTests
{
    private static byte[] Mailbox(byte type, byte[] body, ushort station = 1004, byte counter = 1)
    {
        var bytes = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(bytes, 0);
        BitConverter.GetBytes(station).CopyTo(bytes, 2);
        bytes[4] = 0x00;                          // channel 0, priority 0
        bytes[5] = (byte)((counter << 4) | type);
        body.CopyTo(bytes, 6);
        return bytes;
    }

    [Fact]
    public void Parses_coe_expedited_sdo_download_request()
    {
        // CoE header: service SdoRequest (2), number 0. SDO: cs 0x23 (expedited download,
        // size indicated), index 0x1C12, sub 0, data 01 00 00 00.
        var body = new byte[] { 0x00, 0x20, 0x23, 0x12, 0x1C, 0x00, 0x01, 0x00, 0x00, 0x00 };
        var msg = MailboxParser.TryParse(Mailbox(3, body));

        Assert.NotNull(msg);
        Assert.Equal(MailboxType.Coe, msg!.Type);
        Assert.Equal((ushort)1004, msg.StationAddress);
        Assert.NotNull(msg.Coe);
        Assert.Equal(CoeService.SdoRequest, msg.Coe!.Service);
        Assert.NotNull(msg.Coe.Sdo);
        Assert.Equal(0x23, msg.Coe.Sdo!.CommandSpecifier);
        Assert.True(msg.Coe.Sdo.Expedited);
        Assert.True(msg.Coe.Sdo.SizeIndicated);
        Assert.Equal((ushort)0x1C12, msg.Coe.Sdo.Index);
        Assert.Equal(0, msg.Coe.Sdo.SubIndex);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, msg.Coe.Sdo.Data.ToArray());
    }

    [Fact]
    public void Parses_coe_emergency()
    {
        // CoE header: service Emergency (1). Error code 0x8130 (heartbeat), register 0x81.
        var body = new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var msg = MailboxParser.TryParse(Mailbox(3, body));

        Assert.NotNull(msg?.Coe?.Emergency);
        var emcy = msg!.Coe!.Emergency!;
        Assert.Equal((ushort)0x8130, emcy.ErrorCode);
        Assert.Equal(0x81, emcy.ErrorRegister);
        Assert.Equal(5, emcy.Data.Length);
    }

    [Fact]
    public void Parses_foe_write_request_with_filename()
    {
        var body = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00 }
            .Concat("firmware.bin"u8.ToArray()).ToArray();
        var msg = MailboxParser.TryParse(Mailbox(4, body));

        Assert.NotNull(msg?.Foe);
        Assert.Equal(FoeOpCode.WriteRequest, msg!.Foe!.OpCode);
        Assert.Equal("firmware.bin", msg.Foe.FileName);
    }

    [Fact]
    public void Parses_eoe_fragment_header()
    {
        // h1: type 0, port 1, lastFragment set -> 0x0110. h2: fragment 3, offset 2, frameNo 5.
        var h2 = (ushort)(3 | (2 << 6) | (5 << 12));
        var body = new byte[] { 0x10, 0x01, (byte)(h2 & 0xFF), (byte)(h2 >> 8), 0xDE, 0xAD };
        var msg = MailboxParser.TryParse(Mailbox(2, body));

        Assert.NotNull(msg?.Eoe);
        var eoe = msg!.Eoe!;
        Assert.Equal(0, eoe.FrameType);
        Assert.Equal(1, eoe.Port);
        Assert.True(eoe.LastFragment);
        Assert.Equal((ushort)3, eoe.FragmentNumber);
        Assert.Equal((ushort)2, eoe.OffsetOrBufferSize);
        Assert.Equal(5, eoe.FrameNumber);
    }

    [Fact]
    public void Parses_soe_read_request()
    {
        // SoE header: opcode ReadRequest (1), drive 2, elements Value (0x40), IDN 17 (S-0-0017).
        var body = new byte[] { 0x41, 0x40, 0x11, 0x00 };
        var msg = MailboxParser.TryParse(Mailbox(5, body));

        Assert.NotNull(msg?.Soe);
        var soe = msg!.Soe!;
        Assert.Equal(SoeOpCode.ReadRequest, soe.OpCode);
        Assert.Equal(2, soe.DriveNumber);
        Assert.Equal(SoeElements.Value, soe.Elements);
        Assert.Equal((ushort)17, soe.IdnOrFragmentsLeft);
        Assert.Equal(0, soe.Data.Length);
    }

    [Fact]
    public void Parses_soe_incomplete_write_fragment()
    {
        // SoE header: opcode WriteRequest (3) with incomplete bit (0x08) set, drive 0,
        // element Value; the IDN field of a follow-up fragment carries fragments left (4).
        var body = new byte[] { 0x0B, 0x40, 0x04, 0x00, 0xDE, 0xAD };
        var msg = MailboxParser.TryParse(Mailbox(5, body));

        Assert.NotNull(msg?.Soe);
        var soe = msg!.Soe!;
        Assert.Equal(SoeOpCode.WriteRequest, soe.OpCode);
        Assert.True(soe.Incomplete);
        Assert.Equal((ushort)4, soe.IdnOrFragmentsLeft);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, soe.Data.ToArray());
    }

    [Fact]
    public void Parses_soe_error_response_with_error_code()
    {
        // SoE header: opcode ReadResponse (2) with error bit (0x10) set, drive 0,
        // element Value, IDN 17; data carries error code 0x7009 (write protected).
        var body = new byte[] { 0x12, 0x40, 0x11, 0x00, 0x09, 0x70 };
        var msg = MailboxParser.TryParse(Mailbox(5, body));

        Assert.NotNull(msg?.Soe);
        var soe = msg!.Soe!;
        Assert.Equal(SoeOpCode.ReadResponse, soe.OpCode);
        Assert.True(soe.Error);
        Assert.Equal((ushort)0x7009, soe.ErrorCode);
    }

    [Theory]
    [InlineData((ushort)17, "S-0-0017")]     // bit 15 clear: standard set 0
    [InlineData((ushort)0x9011, "P-1-0017")] // bit 15 set: product-specific, set 1
    public void Formats_soe_idn_labels(ushort idn, string expected)
    {
        var body = new byte[] { 0x01, 0x40, (byte)(idn & 0xFF), (byte)(idn >> 8) };
        var msg = MailboxParser.TryParse(Mailbox(5, body));

        Assert.Equal(expected, msg?.Soe?.IdnLabel);
    }

    [Fact]
    public void Rejects_implausible_soe_bodies()
    {
        // Reserved opcodes (0, 7) and truncated headers leave Soe null on an otherwise
        // valid mailbox, mirroring the CoE bad-service behavior.
        Assert.Null(MailboxParser.TryParse(Mailbox(5, new byte[] { 0x00, 0x40, 0x11, 0x00 }))?.Soe);
        Assert.Null(MailboxParser.TryParse(Mailbox(5, new byte[] { 0x07, 0x40, 0x11, 0x00 }))?.Soe);
        Assert.Null(MailboxParser.TryParse(Mailbox(5, new byte[] { 0x01, 0x40, 0x11 }))?.Soe);
    }

    [Fact]
    public void Rejects_implausible_payloads()
    {
        Assert.Null(MailboxParser.TryParse(new byte[] { 1, 2, 3 }));                       // too short
        Assert.Null(MailboxParser.TryParse(Mailbox(9, new byte[] { 1, 2 })));              // bad type
        var lied = Mailbox(3, new byte[] { 0x00, 0x20 });
        BitConverter.GetBytes((ushort)500).CopyTo(lied, 0);                                // length > body
        Assert.Null(MailboxParser.TryParse(lied));
    }
}
