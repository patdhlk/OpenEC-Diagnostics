using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Protocol;

public class FrameBuilderRoundTripTests
{
    [Fact]
    public void Built_frame_round_trips_through_parser()
    {
        var raw = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, 7, 0x01000000, new byte[] { 1, 2, 3, 4 }, 6)
            .AddPhysical(EtherCatCommand.Brd, 8, 0, 0x0130, new byte[] { 0x08, 0x00 }, 4)
            .Build();

        var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw));
        Assert.Equal(2, ok.Frame.Datagrams.Count);
        Assert.True(ok.Frame.Datagrams[0].MoreFollows);
        Assert.False(ok.Frame.Datagrams[1].MoreFollows);
        Assert.Equal(0x01000000u, ok.Frame.Datagrams[0].LogicalAddress);
        Assert.Equal(6, ok.Frame.Datagrams[0].WorkingCounter);
        Assert.Equal(0x0130, ok.Frame.Datagrams[1].Ado);
        Assert.False(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Returning_frame_has_locally_administered_source()
    {
        var raw = new EtherCatFrameBuilder()
            .AsReturning()
            .AddDatagram(EtherCatCommand.Lrd, 1, 0, Array.Empty<byte>(), 1)
            .Build();

        var ok = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw));
        Assert.True(ok.Frame.Source.IsLocallyAdministered);
    }

    [Fact]
    public void Build_without_datagrams_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new EtherCatFrameBuilder().Build());
    }

    [Fact]
    public void AddDatagram_with_oversized_payload_throws()
    {
        var oversizedPayload = new byte[2048];
        Assert.Throws<ArgumentException>(() =>
            new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrw, 0, 0, oversizedPayload, 0));
    }

    [Fact]
    public void Build_with_excessive_datagram_area_throws()
    {
        // Build enough datagrams to exceed 0x07FF (2047) bytes total.
        // Each datagram: 1 (cmd) + 1 (idx) + 4 (addr) + 2 (len) + 2 (irq) + payload + 2 (wkc) = 12 + payload
        // To exceed 2047 bytes, we need roughly 170+ datagrams with ~10 byte payloads.
        // Simpler: add datagrams with 64-byte payloads; 170 * 76 = 12920 >> 2047
        var builder = new EtherCatFrameBuilder();
        for (int i = 0; i < 170; i++)
        {
            builder.AddDatagram(EtherCatCommand.Lrw, (byte)i, 0, new byte[64], 0);
        }
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}
