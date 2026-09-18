using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Protocol;

public class EslHeaderTests
{
    private static byte[] InnerFrame(byte srcFirstOctet = 0x00) =>
        (srcFirstOctet == 0x02 ? new EtherCatFrameBuilder().AsReturning() : new EtherCatFrameBuilder())
            .AddPhysical(EtherCatCommand.Brd, 1, 0, 0x0130, new byte[] { 0x08, 0x00 }, 4)
            .Build();

    [Fact]
    public void Prefix_wrapper_is_peeled_and_inner_frame_decodes()
    {
        var inner = InnerFrame();
        var raw = EslWrapper.Prefix(inner, port: 3, timestamp: 0x1122334455667788,
            timestampValid: true);

        var frame = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw)).Frame;

        Assert.NotNull(frame.Esl);
        var esl = frame.Esl!.Value;
        Assert.Equal((byte)3, esl.Port);
        Assert.Equal(EslPlacement.Prefix, esl.Placement);
        Assert.True(esl.TimestampValid);
        Assert.Equal(0x1122334455667788UL, esl.Timestamp);
        Assert.False(esl.CrcError);

        // The wrapped frame decodes exactly as the same frame would unwrapped.
        var plain = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(inner)).Frame;
        Assert.Equal(plain.Source.ToString(), frame.Source.ToString());
        Assert.Equal(plain.Datagrams.Count, frame.Datagrams.Count);
        Assert.Equal(plain.Datagrams[0].Ado, frame.Datagrams[0].Ado);
    }

    [Fact]
    public void Postfix_wrapper_is_peeled_and_inner_frame_decodes()
    {
        var inner = InnerFrame(srcFirstOctet: 0x02);
        var raw = EslWrapper.Postfix(inner, port: 7, timestamp: 42, timestampValid: true);

        var frame = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw)).Frame;

        Assert.NotNull(frame.Esl);
        var esl = frame.Esl!.Value;
        Assert.Equal((byte)7, esl.Port);
        Assert.Equal(EslPlacement.Postfix, esl.Placement);
        Assert.Equal(42UL, esl.Timestamp);
        // Postfix leaves the inner frame's own header at offset 0, so direction still reads.
        Assert.True(frame.Source.IsLocallyAdministered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Port_bitmap_round_trips_for_every_port(byte port)
    {
        var raw = EslWrapper.Prefix(InnerFrame(), port);
        var frame = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw)).Frame;
        Assert.Equal(port, frame.Esl!.Value.Port);
    }

    [Theory]
    [InlineData((byte)0x10)]
    [InlineData((byte)0x11)]
    public void Both_cookie_variants_are_recognised(byte cookie4)
    {
        var raw = EslWrapper.Prefix(InnerFrame(), port: 2, cookie4: cookie4);
        var frame = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw)).Frame;
        Assert.NotNull(frame.Esl);
        Assert.Equal((byte)2, frame.Esl!.Value.Port);
    }

    [Fact]
    public void Error_flags_are_decoded()
    {
        var raw = EslWrapper.Prefix(InnerFrame(), port: 1, crcError: true, alignError: true);
        var esl = Assert.IsType<FrameDecodeResult.Success>(EtherCatFrameParser.Parse(raw)).Frame.Esl!.Value;
        Assert.True(esl.CrcError);
        Assert.True(esl.AlignError);
    }

    [Fact]
    public void Plain_frame_has_no_esl_metadata()
    {
        var frame = Assert.IsType<FrameDecodeResult.Success>(
            EtherCatFrameParser.Parse(InnerFrame())).Frame;
        Assert.Null(frame.Esl);
    }

    [Fact]
    public void TryPeel_returns_false_and_passes_through_a_plain_frame()
    {
        var inner = InnerFrame();
        Assert.False(EslHeader.TryPeel(inner, out _, out var passthrough));
        Assert.Equal(inner.Length, passthrough.Length);
    }

    [Fact]
    public void TryPeel_strips_exactly_the_sixteen_byte_prefix()
    {
        var inner = InnerFrame();
        var raw = EslWrapper.Prefix(inner, port: 5);
        Assert.True(EslHeader.TryPeel(raw, out var meta, out var stripped));
        Assert.Equal((byte)5, meta.Port);
        Assert.Equal(inner.Length, stripped.Length);
        Assert.True(inner.AsSpan().SequenceEqual(stripped.Span));
    }
}
