// tests/OpenEC.Monitor.Tests/Eni/EniConfigurationTests.cs
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Eni;

public class EniConfigurationTests
{
    private static EniConfiguration LoadFixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    [Fact]
    public void Parses_slaves_with_identity_and_mailbox()
    {
        var eni = LoadFixture();

        Assert.Equal(4, eni.Slaves.Count);
        var drive = eni.Slaves.Single(s => s.PhysAddr == 1004);
        Assert.Equal("Drive 4 (AX5101)", drive.Name);
        Assert.Equal(2u, drive.VendorId);
        Assert.Equal(0x13ed6012u, drive.ProductCode);
        Assert.NotNull(drive.MailboxOut);
        Assert.Equal((ushort)4096, drive.MailboxOut!.Start);
        Assert.True(drive.MailboxOut.Contains(4100));
        Assert.False(drive.MailboxOut.Contains(5000));
        var coupler = eni.Slaves.Single(s => s.PhysAddr == 1001);
        Assert.Null(coupler.MailboxOut);
        Assert.Equal(0x044c2c52u, coupler.ProductCode); // '#x' hex literal parsed
    }

    [Fact]
    public void Parses_cyclic_commands_with_expected_wkc()
    {
        var eni = LoadFixture();

        Assert.Equal(2, eni.CyclicCommands.Count);
        var lrw = eni.CyclicCommands[0];
        Assert.Equal(EtherCatCommand.Lrw, lrw.Command);
        Assert.Equal(0x01000000u, lrw.RawAddress);
        Assert.Equal(4, lrw.DataLength);
        Assert.Equal(6, lrw.ExpectedWkc);
        Assert.Equal(0, lrw.InputOffs);
        var brd = eni.CyclicCommands[1];
        Assert.Equal(EtherCatCommand.Brd, brd.Command);
        Assert.Equal((uint)(0x0130 << 16), brd.RawAddress);
        Assert.Equal(4, brd.ExpectedWkc);
        Assert.Null(brd.InputOffs);
        Assert.Equal(1000, eni.CycleTimeMicroseconds);
    }

    [Fact]
    public void Parses_process_image_variables()
    {
        var eni = LoadFixture();

        Assert.Equal(5, eni.Variables.Count);
        var sw = eni.Variables.Single(v => v.Name.EndsWith("Statusword"));
        Assert.True(sw.IsInput);
        Assert.Equal("UINT", sw.DataType);
        Assert.Equal(16, sw.BitSize);
        Assert.Equal(16, sw.BitOffs);
        var cw = eni.Variables.Single(v => v.Name.EndsWith("Controlword"));
        Assert.False(cw.IsInput);
    }

    [Fact]
    public void Tolerates_missing_sections()
    {
        using var stream = new MemoryStream(
            "<EtherCATConfig><Config><Slave><Info><Name>S</Name><PhysAddr>1001</PhysAddr></Info></Slave></Config></EtherCATConfig>"u8.ToArray());
        var eni = EniConfiguration.Load(stream);

        Assert.Single(eni.Slaves);
        Assert.Empty(eni.CyclicCommands);
        Assert.Empty(eni.Variables);
        Assert.Null(eni.CycleTimeMicroseconds);
    }

    [Theory]
    [InlineData("#x0130", 0x0130)]
    [InlineData("1000", 1000)]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    public void Parses_eni_number_literals(string? text, int? expected)
    {
        Assert.Equal(expected, (int?)EniXmlValues.ParseNumber(text));
    }
}
