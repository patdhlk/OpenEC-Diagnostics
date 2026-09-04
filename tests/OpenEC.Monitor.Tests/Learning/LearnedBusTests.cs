using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class LearnedBusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EtherCatDatagram Physical(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc = 1) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    private static EtherCatDatagram Logical(EtherCatCommand cmd, uint address, int length,
        ushort wkc) =>
        new(cmd, 0, address, false, false, 0, new byte[length], wkc);

    [Fact]
    public void Station_address_assignment_creates_a_slave_at_its_ring_position()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0xFFFF, 0x0010, [0xEA, 0x03]),
            FrameDirection.Outbound);

        Assert.Equal(2, bus.Slaves.Count);
        Assert.Equal(0, bus.Slaves[0].RingPosition);
        Assert.Equal(1001, bus.Slaves[0].StationAddress);
        Assert.Equal(1, bus.Slaves[1].RingPosition);
        Assert.Equal(1002, bus.Slaves[1].StationAddress);
        Assert.True(bus.SawStartup);
    }

    /// <summary>A returning broadcast, which is how the ring is counted: every slave increments a
    /// broadcast's ADP, so the ADP that comes back is the slave count. One slave here.</summary>
    private static void CountTheRing(LearnedBus bus, ushort slaves = 1) =>
        bus.Observe(T0, Physical(EtherCatCommand.Brd, slaves, 0x0130, [0x08, 0x00], slaves),
            FrameDirection.Returning);

    [Fact]
    public void Sii_reads_addressed_by_auto_increment_resolve_to_the_assigned_slave()
    {
        var bus = new LearnedBus();
        CountTheRing(bus);
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        // Read request for EEPROM word 8, then the answer: vendor 2, product 0x03F03052. The
        // request goes out to ring position 0 and the answer comes back reading 1, because the one
        // slave on the ring incremented it on the way past.
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0502,
            [0x00, 0x01, 0x08, 0x00, 0x00, 0x00]), FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Aprd, 0x0001, 0x0508,
            [0x02, 0x00, 0x00, 0x00, 0x52, 0x30, 0xF0, 0x03]), FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
    }

    /// <summary>The order a real bringup actually happens in: the master reads identity out of SII
    /// by auto-increment and only then assigns station addresses, from what it found. Every fact
    /// therefore arrives before the slave it belongs to has a name, and must still end up on it —
    /// dropping them is what left learning stuck at 0/16 on hardware through any number of master
    /// restarts.</summary>
    [Fact]
    public void Identity_read_before_the_address_assignment_still_lands_on_the_named_slave()
    {
        var bus = new LearnedBus();
        CountTheRing(bus);

        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0502,
            [0x00, 0x01, 0x08, 0x00, 0x00, 0x00]), FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Aprd, 0x0001, 0x0508,
            [0x02, 0x00, 0x00, 0x00, 0x52, 0x30, 0xF0, 0x03]), FrameDirection.Returning);

        // Nothing is named yet, and nothing may be invented to stand in for a name.
        Assert.Empty(bus.Slaves);

        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal((ushort)1001, slave.StationAddress);
        Assert.Equal(0, slave.RingPosition);
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
    }

    /// <summary>Until a broadcast has sized the ring there is no way to read a returning
    /// auto-increment ADP, so the fact is dropped rather than attributed to whatever position the
    /// raw number happens to look like.</summary>
    [Fact]
    public void A_returning_scan_fact_is_dropped_while_the_ring_length_is_unknown()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0502,
            [0x00, 0x01, 0x08, 0x00, 0x00, 0x00]), FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Aprd, 0x0001, 0x0508,
            [0x02, 0x00, 0x00, 0x00, 0x52, 0x30, 0xF0, 0x03]), FrameDirection.Returning);
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal((ushort)1001, slave.StationAddress);
        Assert.Null(slave.VendorId);
    }

    [Fact]
    public void Identity_falls_back_to_the_coe_identity_object()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        foreach (var (sub, value) in new (byte, uint)[] { (1, 2u), (2, 0x03F03052u), (3, 0x00100000u) })
        {
            var body = MailboxDecoderTests.ExpeditedSdo(3, 0x43, 0x1018, sub, value);
            bus.Observe(T0, Physical(EtherCatCommand.Fprd, 1001,
                    0x1080, MailboxDecoderTests.CoeMailbox(1001, body)),
                FrameDirection.Returning);
        }

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(0x00100000u, slave.Revision);
    }

    [Fact]
    public void Sync_managers_and_fmmus_are_recorded_against_the_slave()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x0810,
            [0x00, 0x11, 0x06, 0x00, 0x64, 0x00, 0x01, 0x00]), FrameDirection.Outbound);

        var fmmu = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(fmmu, 0);
        BitConverter.GetBytes((ushort)6).CopyTo(fmmu, 4);
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[7] = 7; fmmu[11] = 1; fmmu[12] = 1;
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x0600, fmmu),
            FrameDirection.Outbound);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(0x1100, slave.SyncManagers[2].PhysicalStart);
        Assert.Equal(FmmuType.Inputs, slave.Fmmus[0].Type);
    }

    [Fact]
    public void Pdo_assignment_is_read_back_in_subindex_order()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1C13, 0, 0);
        Download(bus, 0x1C13, 2, 0x1A01);
        Download(bus, 0x1C13, 1, 0x1A00);
        Download(bus, 0x1C13, 0, 2);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(new ushort[] { 0x1A00, 0x1A01 }, slave.AssignedPdos(3));
    }

    [Fact]
    public void Assignment_count_of_zero_yields_no_pdos()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);
        Download(bus, 0x1C12, 1, 0x1600);
        Download(bus, 0x1C12, 0, 0);

        Assert.Empty(Assert.Single(bus.Slaves).AssignedPdos(2));
    }

    [Fact]
    public void Pdo_mapping_entries_are_read_back_in_subindex_order()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1A00, 0, 2);
        Download(bus, 0x1A00, 1, 0x60000110);
        Download(bus, 0x1A00, 2, 0x60010108);

        var mapping = Assert.Single(bus.Slaves).Mapping(0x1A00);
        Assert.Equal(2, mapping.Count);
        Assert.Equal(0x6000, mapping[0].Index);
        Assert.Equal(16, mapping[0].BitLength);
        Assert.Equal(0x6001, mapping[1].Index);
        Assert.Equal(8, mapping[1].BitLength);
    }

    [Fact]
    public void Cyclic_commands_record_length_and_modal_working_counter()
    {
        var bus = new LearnedBus();
        for (var i = 0; i < 10; i++)
            bus.Observe(T0, Logical(EtherCatCommand.Lrd, 0x00010000, 6, 3),
                FrameDirection.Returning);
        bus.Observe(T0, Logical(EtherCatCommand.Lrd, 0x00010000, 6, 2),
            FrameDirection.Returning);

        var cmd = Assert.Single(bus.CyclicCommands);
        Assert.Equal(EtherCatCommand.Lrd, cmd.Command);
        Assert.Equal(0x00010000u, cmd.RawAddress);
        Assert.Equal(6, cmd.DataLength);
        Assert.Equal(3, cmd.ExpectedWkc);
    }

    [Fact]
    public void Attaching_mid_run_without_startup_still_discovers_slaves()
    {
        var bus = new LearnedBus();

        bus.Observe(T0, Physical(EtherCatCommand.Fprd, 1005, 0x0130, [0x08, 0x00]),
            FrameDirection.Returning);

        Assert.Equal(1005, Assert.Single(bus.Slaves).StationAddress);
        Assert.False(bus.SawStartup);
    }

    private static void Download(LearnedBus bus, ushort index, byte sub, uint value)
    {
        var body = MailboxDecoderTests.ExpeditedSdo(2, 0x23, index, sub, value);
        bus.Observe(T0, Physical(EtherCatCommand.Fpwr, 1001, 0x1000,
            MailboxDecoderTests.CoeMailbox(1001, body)), FrameDirection.Outbound);
    }

    /// <summary>A capture that never sees an explicit sub-index 0 write still yields every entry
    /// it did observe. Sparse sub-indices are included because a total-based cap would drop
    /// everything above the entry count.</summary>
    [Fact]
    public void Entries_are_all_returned_when_no_declared_count_was_observed()
    {
        var bus = new LearnedBus();
        bus.Observe(T0, Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, [0xE9, 0x03]),
            FrameDirection.Outbound);

        Download(bus, 0x1C13, 1, 0x1A00);
        Download(bus, 0x1C13, 2, 0x1A01);
        Download(bus, 0x1A00, 1, 0x60000110);
        Download(bus, 0x1A00, 3, 0x60020108);
        Download(bus, 0x1A00, 5, 0x60040104);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(new ushort[] { 0x1A00, 0x1A01 }, slave.AssignedPdos(3));
        Assert.Equal(new[] { 0x6000, 0x6002, 0x6004 },
            slave.Mapping(0x1A00).Select(e => (int)e.Index));
    }
}
