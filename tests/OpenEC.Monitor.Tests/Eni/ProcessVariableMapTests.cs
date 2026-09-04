using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Eni;

public class ProcessVariableMapTests
{
    private static EniConfiguration LoadFixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static EtherCatDatagram Lrw(byte[] payload) => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, payload, 6);

    [Fact]
    public void Resolves_input_variables_from_lrw_payload()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        // byte0: channel1=1, channel2=0; bytes2-3: statusword 0x0637 (Operation enabled)
        var resolved = map.ResolveInputs(Lrw(new byte[] { 0x01, 0x00, 0x37, 0x06 }));

        Assert.Equal(3, resolved.Count);
        Assert.Equal(true, resolved.Single(r => r.Variable.Name.Contains("Channel 1")).Value);
        Assert.Equal(false, resolved.Single(r => r.Variable.Name.Contains("Channel 2")).Value);
        Assert.Equal((ushort)0x0637, resolved.Single(r => r.Variable.Name.EndsWith("Statusword")).Value);
    }

    [Fact]
    public void Resolves_output_variables_from_lrw_payload()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        var resolved = map.ResolveOutputs(Lrw(new byte[] { 0x01, 0x00, 0x0F, 0x00 }));

        Assert.Equal(2, resolved.Count);
        Assert.Equal(true, resolved.Single(r => r.Variable.Name.Contains("Channel 1")).Value);
        Assert.Equal((ushort)0x000F, resolved.Single(r => r.Variable.Name.EndsWith("Controlword")).Value);
    }

    [Fact]
    public void Unmatched_datagram_resolves_to_empty()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        var other = new EtherCatDatagram(EtherCatCommand.Lrw, 1, 0x02000000, false, false, 0,
            new byte[] { 1, 2, 3, 4 }, 6);
        Assert.Empty(map.ResolveInputs(other));
    }

    [Fact]
    public void Short_payload_is_ignored()
    {
        var map = ProcessVariableMap.Build(LoadFixture());
        Assert.Empty(map.ResolveInputs(Lrw(new byte[] { 0x01 })));
    }

    [Theory]
    [InlineData("BOOL", 1, 0, true)]
    [InlineData("USINT", 8, 16, (byte)0x37)]
    [InlineData("INT", 16, 16, (short)0x0637)]
    [InlineData("UDINT", 32, 0, 0x06370001u)]
    public void Decodes_primitive_types(string type, int bitSize, int bitOffset, object expected)
    {
        var payload = new byte[] { 0x01, 0x00, 0x37, 0x06 };
        Assert.Equal(expected, ProcessValueDecoder.Decode(type, bitSize, payload, bitOffset));
    }

    [Fact]
    public void Decodes_real_and_falls_back_to_hex()
    {
        var real = BitConverter.GetBytes(1.5f);
        Assert.Equal(1.5f, ProcessValueDecoder.Decode("REAL", 32, real, 0));
        Assert.Equal("0102", ProcessValueDecoder.Decode("SOMESTRUCT", 16, new byte[] { 0x01, 0x02 }, 0));
    }
}
