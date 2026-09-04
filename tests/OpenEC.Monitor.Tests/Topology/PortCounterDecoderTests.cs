using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class PortCounterDecoderTests
{
    private static EtherCatDatagram Read(ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | 1001, false, false, 0, payload, wkc);

    /// <summary>The whole 0x0300-0x030D block in one read, as a master polls it:
    /// 8 bytes of per-port invalid-frame/RX-error pairs, 4 forwarded-RX-error bytes,
    /// then the processing-unit and PDI counters.</summary>
    [Fact]
    public void The_full_block_decodes_every_port_and_both_device_counters()
    {
        byte[] payload =
        [
            114,
            0,
            114,
            0,
            0,
            0,
            0,
            0,   // 0x0300-0x0307: ports 0 and 1 each 114 invalid frames
            7,
            0,
            0,
            0,                    // 0x0308-0x030B: forwarded RX error, port 0 = 7
            3,                             // 0x030C: processing unit errors
            9,                             // 0x030D: PDI errors
        ];

        var fact = RegisterDecoders.TryPortCounters(Read(0x0300, payload), FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal((byte)114, fact!.Ports[0].InvalidFrame);
        Assert.Equal((byte)0, fact.Ports[0].RxError);
        Assert.Equal((byte)114, fact.Ports[1].InvalidFrame);
        Assert.Equal((byte)7, fact.Ports[0].ForwardedRxError);
        Assert.Equal((byte)3, fact.ProcessingUnitErrors);
        Assert.Equal((byte)9, fact.PdiErrors);
    }

    /// <summary>Counters never read stay null. A short read of only 0x0300-0x0301 says nothing
    /// about lost link, and reporting zero there would invent a fact.</summary>
    [Fact]
    public void Registers_outside_the_read_stay_null()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0300, [5, 6]), FrameDirection.Returning);

        Assert.Equal((byte)5, fact!.Ports[0].InvalidFrame);
        Assert.Equal((byte)6, fact.Ports[0].RxError);
        Assert.Null(fact.Ports[0].LostLink);
        Assert.Null(fact.Ports[0].ForwardedRxError);
        Assert.Null(fact.ProcessingUnitErrors);
        Assert.False(fact.Ports.ContainsKey(1));
    }

    /// <summary>The lost-link block at 0x0310 is a separate read on most masters.</summary>
    [Fact]
    public void The_lost_link_block_decodes_on_its_own()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0310, [1, 2, 0, 0]), FrameDirection.Returning);

        Assert.Equal((byte)1, fact!.Ports[0].LostLink);
        Assert.Equal((byte)2, fact.Ports[1].LostLink);
        Assert.Null(fact.Ports[0].InvalidFrame);
    }

    /// <summary>A read that starts mid-block is attributed to the right ports.</summary>
    [Fact]
    public void A_read_starting_at_port_two_is_not_attributed_to_port_zero()
    {
        var fact = RegisterDecoders.TryPortCounters(
            Read(0x0304, [42, 0, 0, 0]), FrameDirection.Returning);

        Assert.Equal((byte)42, fact!.Ports[2].InvalidFrame);
        Assert.False(fact.Ports.ContainsKey(0));
    }

    [Fact]
    public void Outbound_reads_other_registers_and_zero_wkc_are_ignored()
    {
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, [1, 2]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0400, [1, 2]), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, [1, 2], wkc: 0), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryPortCounters(
            Read(0x0300, []), FrameDirection.Returning));
    }

    [Fact]
    public void Merging_keeps_the_newer_value_and_never_erases_a_known_one()
    {
        var first = new PortCounters(InvalidFrame: 5, RxError: null, ForwardedRxError: null, LostLink: 2);
        var second = new PortCounters(InvalidFrame: 6, RxError: 1, ForwardedRxError: null, LostLink: null);

        var merged = first.Merge(second);

        Assert.Equal((byte)6, merged.InvalidFrame);   // newer wins
        Assert.Equal((byte)1, merged.RxError);        // newly learned
        Assert.Equal((byte)2, merged.LostLink);       // not erased by an absent value
        Assert.Null(merged.ForwardedRxError);
    }
}
