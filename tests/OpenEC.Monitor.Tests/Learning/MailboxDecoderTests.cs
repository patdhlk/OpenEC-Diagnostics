using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class MailboxDecoderTests
{
    /// <summary>Wraps a CoE body in a mailbox header addressed to <paramref name="station"/>.</summary>
    internal static byte[] CoeMailbox(ushort station, byte[] body)
    {
        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes(station).CopyTo(mailbox, 2);
        mailbox[5] = 0x03;                       // type 3 = CoE
        body.CopyTo(mailbox, 6);
        return mailbox;
    }

    /// <summary>An expedited SDO with a 4-byte value. Service 2 = SDO request (download),
    /// 3 = SDO response (upload answer).</summary>
    internal static byte[] ExpeditedSdo(byte service, byte commandSpecifier,
        ushort index, byte subIndex, uint value)
    {
        var body = new byte[10];
        BitConverter.GetBytes((ushort)(service << 12)).CopyTo(body, 0);
        body[2] = commandSpecifier;
        BitConverter.GetBytes(index).CopyTo(body, 3);
        body[5] = subIndex;
        BitConverter.GetBytes(value).CopyTo(body, 6);
        return body;
    }

    private static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, byte[] payload,
        ushort wkc = 1) =>
        new(cmd, 0, (0x1000u << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Pdo_assignment_download_is_decoded()
    {
        // Download 0x1C13:01 = 0x1A00 (assign TxPDO 0x1A00 to SM3). cs 0x23 = expedited, 4 bytes.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x23, 0x1C13, 1, 0x1A00));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        var fact = MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(0x1C13, fact!.Index);
        Assert.Equal(1, fact.SubIndex);
        Assert.Equal(0x1A00u, fact.Value);
    }

    [Fact]
    public void Pdo_mapping_download_is_decoded()
    {
        // 0x1A00:01 = 0x60000110 → object 0x6000 sub 0x01, 16 bits.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x23, 0x1A00, 1, 0x60000110));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        var fact = MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        var entry = PdoMappingEntry.FromRaw(fact!.Value);
        Assert.Equal(0x6000, entry.Index);
        Assert.Equal(1, entry.SubIndex);
        Assert.Equal(16, entry.BitLength);
        Assert.False(entry.IsPadding);
    }

    [Fact]
    public void Padding_mapping_entries_are_recognised()
    {
        var entry = PdoMappingEntry.FromRaw(0x00000004);

        Assert.True(entry.IsPadding);
        Assert.Equal(4, entry.BitLength);
    }

    [Fact]
    public void Sdo_upload_response_is_decoded_from_returning_frames()
    {
        // 0x1018:01 = vendor id 0x00000002. cs 0x43 = expedited upload response, 4 bytes.
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 0x00000002));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        var fact = MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal(0x1018, fact!.Index);
        Assert.Equal(2u, fact.Value);
    }

    [Fact]
    public void Upload_responses_are_not_mistaken_for_downloads()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 2));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Non_coe_mailbox_traffic_is_ignored()
    {
        var mailbox = new byte[10];
        BitConverter.GetBytes((ushort)4).CopyTo(mailbox, 0);
        mailbox[5] = 0x04;                       // type 4 = FoE
        var d = Datagram(EtherCatCommand.Fpwr, 1001, mailbox);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Segmented_transfers_are_ignored()
    {
        // cs 0x00 → initiate download, not expedited, size not indicated.
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x00, 0x1A00, 1, 0));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    /// <summary>Same service as a download, different command specifier — the case that actually
    /// exercises the ccs guard. An upload request is how a master READS an object, so decoding it
    /// as a download would record a value that was never written.</summary>
    [Fact]
    public void An_upload_request_is_not_decoded_as_a_download()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(2, 0x43, 0x1018, 1, 0));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }

    [Fact]
    public void A_download_response_is_not_decoded_as_an_upload_response()
    {
        // Service 3 (SdoResponse) with ccs 1: a download acknowledgement, carrying no value.
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x23, 0x1C13, 1, 0x1A00));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning));
    }

    /// <summary>The bringup fixture emits RETURNING FPWR mailbox frames echoing what the master
    /// wrote, so this guard is what stops a master download being re-recorded as a slave answer.
    /// It is load-bearing on the branch's own primary fixture.</summary>
    [Fact]
    public void Upload_responses_require_a_read_command()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 2));
        var d = Datagram(EtherCatCommand.Fpwr, 1001, payload);

        Assert.Null(MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning));
    }

    [Fact]
    public void Upload_responses_with_a_zero_working_counter_are_ignored()
    {
        var payload = CoeMailbox(1001, ExpeditedSdo(3, 0x43, 0x1018, 1, 2));
        var d = Datagram(EtherCatCommand.Fprd, 1001, payload, wkc: 0);

        Assert.Null(MailboxDecoders.TrySdoUploadResponse(d, FrameDirection.Returning));
    }

    /// <summary>The command specifier declares a 4-byte value but the body stops after the
    /// sub-index. Zero-filling here would fabricate a value that was never on the wire.</summary>
    [Fact]
    public void Truncated_expedited_data_is_rejected_rather_than_zero_filled()
    {
        var body = new byte[6];
        BitConverter.GetBytes((ushort)((ushort)CoeService.SdoRequest << 12)).CopyTo(body, 0);
        body[2] = 0x23;
        BitConverter.GetBytes((ushort)0x1C13).CopyTo(body, 3);
        body[5] = 1;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, CoeMailbox(1001, body));

        Assert.Null(MailboxDecoders.TrySdoDownload(d, FrameDirection.Outbound));
    }
}
