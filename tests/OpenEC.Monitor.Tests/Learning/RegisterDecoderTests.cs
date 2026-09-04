using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class RegisterDecoderTests
{
    internal static EtherCatDatagram Datagram(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc = 1) =>
        new(cmd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    [Fact]
    public void Station_address_assignment_is_decoded_with_its_ring_position()
    {
        // APWR to auto-inc 0xFFFF (second slave), register 0x0010, assigning address 1002.
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, new byte[] { 0xEA, 0x03 });

        var fact = RegisterDecoders.TryStationAddress(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(1002, fact!.StationAddress);
        Assert.Equal(1, fact.RingPosition);
    }

    [Fact]
    public void Returning_station_address_writes_are_ignored()
    {
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, new byte[] { 0xEA, 0x03 });

        Assert.Null(RegisterDecoders.TryStationAddress(d, FrameDirection.Returning));
    }

    [Fact]
    public void Writes_to_other_registers_are_ignored()
    {
        var d = Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0120, new byte[] { 0x02, 0x00 });

        Assert.Null(RegisterDecoders.TryStationAddress(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Truncated_payloads_are_rejected_rather_than_throwing()
    {
        Assert.Null(RegisterDecoders.TryStationAddress(
            Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, []), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TryStationAddress(
            Datagram(EtherCatCommand.Apwr, 0xFFFF, 0x0010, [0xEA]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TrySiiAddress(
            Datagram(EtherCatCommand.Fpwr, 1001, 0x0502, new byte[5]), FrameDirection.Outbound));
    }

    [Fact]
    public void Sii_decoders_ignore_other_registers()
    {
        Assert.Null(RegisterDecoders.TrySiiAddress(
            Datagram(EtherCatCommand.Fpwr, 1001, 0x0500, new byte[6]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TrySiiData(
            Datagram(EtherCatCommand.Fprd, 1001, 0x0510, new byte[4]), FrameDirection.Returning));
    }

    [Fact]
    public void Sii_read_command_carries_the_word_address()
    {
        // Control 0x0100 (read), word address 0x00000008 (vendor id).
        var payload = new byte[] { 0x00, 0x01, 0x08, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0502, payload);

        var fact = RegisterDecoders.TrySiiAddress(d, FrameDirection.Outbound);

        Assert.NotNull(fact);
        Assert.Equal(8u, fact!.WordAddress);
        Assert.True(fact.IsRead);
        Assert.Equal(1001, fact.Slave.Address);
        Assert.False(fact.Slave.IsAutoIncrement);
    }

    [Fact]
    public void Sii_data_is_decoded_from_returning_reads_only()
    {
        var payload = new byte[] { 0x02, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fprd, 1001, 0x0508, payload);

        Assert.NotNull(RegisterDecoders.TrySiiData(d, FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TrySiiData(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Sii_data_with_zero_working_counter_is_ignored()
    {
        var payload = new byte[] { 0x02, 0x00, 0x00, 0x00 };
        var d = Datagram(EtherCatCommand.Fprd, 1001, 0x0508, payload, wkc: 0);

        Assert.Null(RegisterDecoders.TrySiiData(d, FrameDirection.Returning));
    }

    [Fact]
    public void Sync_manager_block_is_decoded()
    {
        // SM2: phys start 0x1100, length 6, control 0x64, status 0x00, activate 0x01, pdi 0x00
        var payload = new byte[] { 0x00, 0x11, 0x06, 0x00, 0x64, 0x00, 0x01, 0x00 };
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0810, payload);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        var sm = Assert.Single(facts);
        Assert.Equal(2, sm.Number);
        Assert.Equal(0x1100, sm.PhysicalStart);
        Assert.Equal(6, sm.Length);
        Assert.True(sm.Enabled);
    }

    [Fact]
    public void Consecutive_sync_manager_blocks_in_one_write_are_all_decoded()
    {
        var payload = new byte[16];
        // SM0 at 0x1000 len 128, enabled.
        payload[0] = 0x00; payload[1] = 0x10; payload[2] = 0x80; payload[6] = 0x01;
        // SM1 at 0x1080 len 128, enabled.
        payload[8] = 0x80; payload[9] = 0x10; payload[10] = 0x80; payload[14] = 0x01;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0800, payload);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        Assert.Equal(2, facts.Count);
        Assert.Equal(0, facts[0].Number);
        Assert.Equal(1, facts[1].Number);
        Assert.Equal(0x1080, facts[1].PhysicalStart);
    }

    [Fact]
    public void Fmmu_block_is_decoded()
    {
        var payload = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(payload, 0);  // logical start
        BitConverter.GetBytes((ushort)2).CopyTo(payload, 4);    // length
        payload[6] = 0;                                          // logical start bit
        payload[7] = 7;                                          // logical stop bit
        BitConverter.GetBytes((ushort)0x1100).CopyTo(payload, 8); // physical start
        payload[10] = 0;                                          // physical start bit
        payload[11] = 1;                                          // type: inputs
        payload[12] = 1;                                          // activate
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0600, payload);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        var fmmu = Assert.Single(facts);
        Assert.Equal(0, fmmu.Number);
        Assert.Equal(0x00010000u, fmmu.LogicalStart);
        Assert.Equal(2, fmmu.Length);
        Assert.Equal(0x1100, fmmu.PhysicalStart);
        Assert.Equal(FmmuType.Inputs, fmmu.Type);
        Assert.True(fmmu.Enabled);
    }

    [Fact]
    public void Fmmu_number_follows_the_register_offset()
    {
        var payload = new byte[16];
        payload[11] = 2;
        payload[12] = 1;
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0610, payload);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        Assert.Equal(1, Assert.Single(facts).Number);
    }

    [Fact]
    public void Unaligned_register_offsets_decode_nothing()
    {
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0604, new byte[16]);

        Assert.Empty(RegisterDecoders.TryFmmus(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Partial_trailing_block_is_dropped()
    {
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0800, new byte[12]);

        Assert.Single(RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound));
    }

    [Fact]
    public void Sync_manager_blocks_past_the_last_real_block_are_not_fabricated()
    {
        // Starts at SM 15 (0x0800 + 15*8) with room for two blocks; only SM 15 exists.
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x0878, new byte[16]);

        var facts = RegisterDecoders.TrySyncManagers(d, FrameDirection.Outbound);

        Assert.Equal(15, Assert.Single(facts).Number);
    }

    [Fact]
    public void Fmmu_blocks_past_the_last_real_block_are_not_fabricated()
    {
        // Starts at FMMU 15 (0x0600 + 15*16) with room for two blocks; only FMMU 15 exists.
        var d = Datagram(EtherCatCommand.Fpwr, 1001, 0x06F0, new byte[32]);

        var facts = RegisterDecoders.TryFmmus(d, FrameDirection.Outbound);

        Assert.Equal(15, Assert.Single(facts).Number);
    }
}
