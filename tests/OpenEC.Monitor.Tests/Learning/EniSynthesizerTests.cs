using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class EniSynthesizerTests
{
    private static LearnedBus LearnBringup()
    {
        var bus = new LearnedBus();
        var direction = new DirectionTracker();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
        {
            if (EtherCatFrameParser.Parse(frame) is not FrameDecodeResult.Success ok) continue;
            var dir = direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                bus.Observe(timestamp, datagram, dir);
        }
        return bus;
    }

    [Fact]
    public void Slaves_are_emitted_in_ring_order_with_identity()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal(2, eni.Slaves.Count);
        Assert.Equal(1001, eni.Slaves[0].PhysAddr);
        Assert.Equal(0x0000, eni.Slaves[0].AutoIncAddr);
        Assert.Equal(0xFFFF, eni.Slaves[1].AutoIncAddr);
        Assert.Equal(2u, eni.Slaves[0].VendorId);
        Assert.Equal(0x03F03052u, eni.Slaves[0].ProductCode);
    }

    /// <summary>ENI is written from the MASTER's perspective, so MailboxOut (&lt;Send&gt;) is SM0 —
    /// the slave's MBoxOut window at 0x1000, which the master writes. sample.eni.xml encodes the
    /// same convention and EniConfigurationTests asserts it; the two must not disagree.</summary>
    [Fact]
    public void Mailbox_windows_come_from_the_mailbox_sync_managers()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal(0x1000, eni.Slaves[0].MailboxOut!.Start);
        Assert.Equal(0x1080, eni.Slaves[0].MailboxIn!.Start);
        Assert.Equal(128, eni.Slaves[0].MailboxIn!.Length);
    }

    [Fact]
    public void Cyclic_commands_carry_length_and_expected_working_counter()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        var cmd = Assert.Single(eni.CyclicCommands);
        Assert.Equal(EtherCatCommand.Lrd, cmd.Command);
        Assert.Equal(2, cmd.ExpectedWkc);
        Assert.Equal(0, cmd.InputOffs);
        Assert.Null(cmd.OutputOffs);
    }

    [Fact]
    public void Variables_are_placed_at_wire_correct_bit_offsets()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        // Two slaves, eight 1-bit inputs each, mapped to consecutive logical bytes.
        Assert.Equal(16, eni.Variables.Count);
        Assert.All(eni.Variables, v => Assert.True(v.IsInput));
        Assert.Equal(Enumerable.Range(0, 16).ToArray(), eni.Variables.Select(v => v.BitOffs));
        Assert.All(eni.Variables, v => Assert.Equal(1, v.BitSize));
    }

    [Fact]
    public void Synthetic_names_are_used_when_no_esi_schema_is_available()
    {
        var eni = EniSynthesizer.Synthesize(LearnBringup(), new Dictionary<ushort, EsiDevice>());

        Assert.Equal("Slave 1001.0x6000:01", eni.Variables[0].Name);
        Assert.Equal("BOOL", eni.Variables[0].DataType);
    }

    /// <summary>ProcessImage keys its variable dictionary by name, so colliding names silently
    /// discard a slave's entire contribution. The bringup fixture is two IDENTICAL terminals, so
    /// an ESI device-type name alone is not enough to tell their variables apart.</summary>
    [Fact]
    public async Task Variable_names_are_unique_across_identical_slaves()
    {
        var bus = LearnBringup();
        using var enricher = new EsiEnricher(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        var device = await enricher.ResolveDeviceAsync(2, 0x03F03052, 0x00120000, "EL1008");
        Assert.NotNull(device);
        var schemas = new Dictionary<ushort, EsiDevice> { [1001] = device, [1002] = device };

        var eni = EniSynthesizer.Synthesize(bus, schemas);

        Assert.Equal(16, eni.Variables.Count);
        Assert.Equal(16, eni.Variables.Select(v => v.Name).Distinct().Count());
    }

    [Fact]
    public void Padding_entries_advance_the_offset_without_becoming_variables()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00010000, length: 2, entries:
        [
            0x60000110u,   // 0x6000:01, 16 bits
            0x00000004u,   // padding, 4 bits
            0x60020104u,   // 0x6002:01, 4 bits
        ]);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>());

        Assert.Equal(2, eni.Variables.Count);
        Assert.Equal(0, eni.Variables[0].BitOffs);
        Assert.Equal(20, eni.Variables[1].BitOffs);
    }

    [Fact]
    public void Output_fmmus_produce_output_variables_on_their_own_origin()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00020000, length: 1,
            entries: [0x70000108u], fmmuType: 2);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>());

        var variable = Assert.Single(eni.Variables);
        Assert.False(variable.IsInput);
        Assert.Equal(0, variable.BitOffs);
    }

    /// <summary>A SyncManager the slave never activated cannot carry process data, so an FMMU
    /// pointing at its window places nothing — rather than resolving to the wrong assignment
    /// object and emitting plausible but wrong variables.</summary>
    [Fact]
    public void Disabled_sync_managers_are_not_matched()
    {
        var bus = new LearnedBus();
        Assign(bus, station: 1001, logicalStart: 0x00010000, length: 1,
            entries: [0x60000108u], smEnabled: false);

        Assert.Empty(EniSynthesizer.Synthesize(bus, new Dictionary<ushort, EsiDevice>()).Variables);
    }

    /// <summary>Drives a minimal bringup for one slave straight through LearnedBus.</summary>
    private static void Assign(LearnedBus bus, ushort station, uint logicalStart, ushort length,
        uint[] entries, byte fmmuType = 1, bool smEnabled = true)
    {
        var t = DateTimeOffset.UnixEpoch;
        void Physical(EtherCatCommand cmd, ushort adp, ushort ado, byte[] payload,
            FrameDirection dir = FrameDirection.Outbound) =>
            bus.Observe(t, new EtherCatDatagram(cmd, 0, ((uint)ado << 16) | adp, false, false, 0,
                payload, 1), dir);

        Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, BitConverter.GetBytes(station));

        // SM 3 carries inputs and SM 2 carries outputs, as on real Beckhoff devices. The
        // register offset and the assignment object below are both derived from this one number
        // so they cannot disagree: the synthesizer finds the SM by physical address and then
        // looks up 0x1C10 + that SM's number, so a mismatch yields silently zero variables.
        var smNumber = fmmuType == 1 ? (byte)3 : (byte)2;

        var smBlock = new byte[8];
        BitConverter.GetBytes((ushort)0x1100).CopyTo(smBlock, 0);
        BitConverter.GetBytes(length).CopyTo(smBlock, 2);
        smBlock[6] = smEnabled ? (byte)0x01 : (byte)0x00;
        Physical(EtherCatCommand.Fpwr, station, (ushort)(0x0800 + 8 * smNumber), smBlock);

        var fmmu = new byte[16];
        BitConverter.GetBytes(logicalStart).CopyTo(fmmu, 0);
        BitConverter.GetBytes(length).CopyTo(fmmu, 4);
        fmmu[7] = 7;
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[11] = fmmuType;
        fmmu[12] = 1;
        Physical(EtherCatCommand.Fpwr, station, 0x0600, fmmu);

        var pdoIndex = fmmuType == 1 ? (ushort)0x1A00 : (ushort)0x1600;
        var assignObject = (ushort)(0x1C10 + smNumber);
        void Sdo(ushort index, byte sub, uint value) =>
            Physical(EtherCatCommand.Fpwr, station, 0x1000,
                MailboxDecoderTests.CoeMailbox(station,
                    MailboxDecoderTests.ExpeditedSdo(2, 0x23, index, sub, value)));

        for (byte i = 0; i < entries.Length; i++) Sdo(pdoIndex, (byte)(i + 1), entries[i]);
        Sdo(pdoIndex, 0, (uint)entries.Length);
        Sdo(assignObject, 1, pdoIndex);
        Sdo(assignObject, 0, 1);
    }
}
