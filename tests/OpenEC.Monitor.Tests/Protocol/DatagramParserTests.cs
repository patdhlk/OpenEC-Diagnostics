using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class DatagramParserTests
{
    private static byte[] Datagram(byte cmd, byte idx, uint address, byte[] payload,
        ushort wkc, bool more = false, ushort irq = 0)
    {
        var bytes = new byte[12 + payload.Length];
        bytes[0] = cmd;
        bytes[1] = idx;
        BitConverter.GetBytes(address).CopyTo(bytes, 2);
        var lenField = (ushort)(payload.Length & 0x07FF);
        if (more) lenField |= 0x8000;
        BitConverter.GetBytes(lenField).CopyTo(bytes, 6);
        BitConverter.GetBytes(irq).CopyTo(bytes, 8);
        payload.CopyTo(bytes, 10);
        BitConverter.GetBytes(wkc).CopyTo(bytes, 10 + payload.Length);
        return bytes;
    }

    [Fact]
    public void Parses_single_physical_datagram()
    {
        // FPRD ADP=1001 ADO=0x0130, 2-byte payload, WKC=1
        var raw = Datagram(4, 0x21, (0x0130u << 16) | 1001, new byte[] { 0x08, 0x00 }, 1);

        var result = DatagramParser.ParseChain(raw);

        var d = Assert.Single(result);
        Assert.Equal(EtherCatCommand.Fprd, d.Command);
        Assert.Equal(0x21, d.Index);
        Assert.Equal(1001, d.Adp);
        Assert.Equal(0x0130, d.Ado);
        Assert.False(d.IsLogical);
        Assert.Equal(new byte[] { 0x08, 0x00 }, d.Payload.ToArray());
        Assert.Equal(1, d.WorkingCounter);
        Assert.False(d.MoreFollows);
    }

    [Fact]
    public void Parses_chain_of_two_datagrams()
    {
        var first = Datagram(12, 1, 0x01000000, new byte[] { 1, 2, 3, 4 }, 6, more: true);
        var second = Datagram(7, 2, 0x01300000, new byte[] { 0, 0 }, 4);
        var raw = first.Concat(second).ToArray();

        var result = DatagramParser.ParseChain(raw);

        Assert.Equal(2, result.Count);
        Assert.Equal(EtherCatCommand.Lrw, result[0].Command);
        Assert.True(result[0].IsLogical);
        Assert.Equal(0x01000000u, result[0].LogicalAddress);
        Assert.True(result[0].MoreFollows);
        Assert.Equal(EtherCatCommand.Brd, result[1].Command);
        Assert.Equal(0x0130, result[1].Ado);
    }

    [Fact]
    public void Truncated_header_throws()
    {
        var raw = new byte[] { 4, 0, 0, 0, 0 };
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }

    [Fact]
    public void Truncated_payload_throws()
    {
        var raw = Datagram(4, 0, 0, new byte[] { 1, 2, 3, 4 }, 0)[..14];
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }

    [Fact]
    public void Unknown_command_throws()
    {
        var raw = Datagram(99, 0, 0, Array.Empty<byte>(), 0);
        Assert.Throws<MalformedFrameException>(() => DatagramParser.ParseChain(raw));
    }
}
