using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Protocol;

public class EtherCatFrameParserTests
{
    private static byte[] Frame(byte[] datagramArea, byte srcFirstOctet = 0x00, bool vlan = false)
    {
        var header = (ushort)(datagramArea.Length | (1 << 12));
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });          // dst
        bytes.AddRange(new byte[] { srcFirstOctet, 0x01, 0x05, 0x10, 0x00, 0x01 }); // src
        if (vlan) bytes.AddRange(new byte[] { 0x81, 0x00, 0x00, 0x2A });            // VLAN 42
        bytes.AddRange(new byte[] { 0x88, 0xA4 });
        bytes.Add((byte)(header & 0xFF));
        bytes.Add((byte)(header >> 8));
        bytes.AddRange(datagramArea);
        return bytes.ToArray();
    }

    private static byte[] NopDatagram()
    {
        var d = new byte[12];
        d[0] = 0; // NOP, len 0, wkc 0
        return d;
    }

    [Fact]
    public void Parses_plain_ethercat_frame()
    {
        var result = EtherCatFrameParser.Parse(Frame(NopDatagram()));

        var ok = Assert.IsType<FrameDecodeResult.Success>(result);
        Assert.Null(ok.Frame.VlanId);
        Assert.Single(ok.Frame.Datagrams);
        Assert.False(ok.Frame.Source.IsLocallyAdministered);
        Assert.Equal("00:01:05:10:00:01", ok.Frame.Source.ToString());
    }

    [Fact]
    public void Parses_vlan_tagged_frame_and_locally_administered_source()
    {
        var result = EtherCatFrameParser.Parse(Frame(NopDatagram(), srcFirstOctet: 0x02, vlan: true));

        var ok = Assert.IsType<FrameDecodeResult.Success>(result);
        Assert.Equal((ushort)42, ok.Frame.VlanId);
        Assert.True(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Non_ethercat_ethertype_is_reported()
    {
        var raw = Frame(NopDatagram());
        raw[12] = 0x08; raw[13] = 0x00; // IPv4
        var result = EtherCatFrameParser.Parse(raw);
        var not = Assert.IsType<FrameDecodeResult.NotEtherCat>(result);
        Assert.Equal((ushort)0x0800, not.EtherType);
    }

    [Fact]
    public void Truncated_frame_is_malformed_not_thrown()
    {
        var raw = Frame(NopDatagram())[..16];
        Assert.IsType<FrameDecodeResult.Malformed>(EtherCatFrameParser.Parse(raw));
    }

    [Fact]
    public void Bad_datagram_area_is_malformed_not_thrown()
    {
        var bad = new byte[12];
        bad[0] = 99; // unknown command
        Assert.IsType<FrameDecodeResult.Malformed>(EtherCatFrameParser.Parse(Frame(bad)));
    }
}
