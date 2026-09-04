using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class SlaveRefTests
{
    private static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, ushort ado) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, ReadOnlyMemory<byte>.Empty, 0);

    [Fact]
    public void Auto_increment_commands_are_flagged()
    {
        var re = SlaveRef.From(Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010), FrameDirection.Outbound);

        Assert.True(re.IsAutoIncrement);
        Assert.Equal(0xFFFF, re.Address);
    }

    [Fact]
    public void Fixed_address_commands_are_not_flagged()
    {
        var re = SlaveRef.From(Datagram(EtherCatCommand.Fpwr, 1001, 0x0600), FrameDirection.Outbound);

        Assert.False(re.IsAutoIncrement);
        Assert.Equal(1001, re.Address);
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0xFFFF, 1)]
    [InlineData(0xFFFE, 2)]
    [InlineData(0xFFFD, 3)]
    public void Ring_position_is_the_twos_complement_of_the_auto_increment_address(
        int autoInc, int expected)
    {
        var re = new SlaveRef((ushort)autoInc, IsAutoIncrement: true);

        Assert.Equal(expected, re.RingPosition);
    }

    [Fact]
    public void Ring_position_is_unknown_for_fixed_addressing()
    {
        Assert.Equal(-1, new SlaveRef(1001, IsAutoIncrement: false).RingPosition);
    }

    /// <summary>StationAddressFact.RingPosition is the property LearnedBus consumes to key
    /// every slave, so it is tested over the same boundary cases as SlaveRef's.</summary>
    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0xFFFF, 1)]
    [InlineData(0xFFFE, 2)]
    [InlineData(0xFFFD, 3)]
    public void Station_address_fact_reports_the_same_ring_position(int autoInc, int expected)
    {
        var fact = new StationAddressFact((ushort)autoInc, StationAddress: 1001);

        Assert.Equal(expected, fact.RingPosition);
    }

    /// <summary>The defect this normalization exists for. Every slave increments an auto-increment
    /// datagram's ADP as it forwards the frame, so on a 16-slave ring the returning copy of the
    /// datagram the master sent to ring position 0 comes back reading 16. Read verbatim it names
    /// nobody; normalized it names position 0 again. Values taken from a real TwinCAT ring scan.</summary>
    [Theory]
    [InlineData(16, 16, 0)]
    [InlineData(15, 16, 1)]
    [InlineData(1, 16, 15)]
    [InlineData(2, 2, 0)]
    [InlineData(1, 2, 1)]
    public void A_returning_auto_increment_address_is_normalized_by_the_ring_length(
        int returningAdp, int ringLength, int expectedPosition)
    {
        var raw = SlaveRef.From(
            Datagram(EtherCatCommand.Aprd, (ushort)returningAdp, 0x0110),
            FrameDirection.Returning);

        var normalized = raw.Normalized((ushort)ringLength);

        Assert.Equal(expectedPosition, normalized.RingPosition);
        Assert.False(normalized.IsReturning);
    }

    /// <summary>Only the return leg is offset. An outbound ADP is already the position the master
    /// addressed, and shifting it would break the assignment datagram that names every slave.</summary>
    [Fact]
    public void An_outbound_auto_increment_address_is_left_alone()
    {
        var raw = SlaveRef.From(
            Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010), FrameDirection.Outbound);

        Assert.Equal(raw, raw.Normalized(16));
        Assert.Equal(1, raw.Normalized(16).RingPosition);
    }

    /// <summary>Fixed addressing carries a station address, which no slave increments.</summary>
    [Fact]
    public void A_returning_fixed_address_is_left_alone()
    {
        var raw = SlaveRef.From(
            Datagram(EtherCatCommand.Fprd, 1001, 0x0110), FrameDirection.Returning);

        Assert.Equal((ushort)1001, raw.Normalized(16).Address);
    }
}
