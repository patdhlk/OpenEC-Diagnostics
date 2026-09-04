using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class EniXmlWriterTests
{
    private static EniConfiguration Learned()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!.Configuration;
    }

    private static EniConfiguration RoundTrip(EniConfiguration source)
    {
        using var stream = new MemoryStream();
        EniXmlWriter.ToXml(source).Save(stream);
        stream.Position = 0;
        return EniConfiguration.Load(stream);
    }

    [Fact]
    public void Slaves_survive_a_round_trip()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        Assert.Equal(source.Slaves.Count, reloaded.Slaves.Count);
        Assert.Equal(source.Slaves[0].PhysAddr, reloaded.Slaves[0].PhysAddr);
        Assert.Equal(source.Slaves[0].AutoIncAddr, reloaded.Slaves[0].AutoIncAddr);
        Assert.Equal(source.Slaves[0].VendorId, reloaded.Slaves[0].VendorId);
        Assert.Equal(source.Slaves[0].ProductCode, reloaded.Slaves[0].ProductCode);
        Assert.Equal(source.Slaves[1].Name, reloaded.Slaves[1].Name);
    }

    [Fact]
    public void Mailbox_windows_survive_a_round_trip()
    {
        var reloaded = RoundTrip(Learned());

        // MailboxOut is <Send>, i.e. SM0 at 0x1000 — the same convention the hand-built round trip
        // below and sample.eni.xml already use. A learner-derived round trip must not encode the
        // opposite one.
        Assert.Equal(0x1000, reloaded.Slaves[0].MailboxOut!.Start);
        Assert.Equal(128, reloaded.Slaves[0].MailboxOut!.Length);
        Assert.Equal(0x1080, reloaded.Slaves[0].MailboxIn!.Start);
    }

    [Fact]
    public void Cyclic_commands_survive_a_round_trip()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        var cmd = Assert.Single(reloaded.CyclicCommands);
        Assert.Equal(source.CyclicCommands[0].Command, cmd.Command);
        Assert.Equal(source.CyclicCommands[0].RawAddress, cmd.RawAddress);
        Assert.Equal(source.CyclicCommands[0].DataLength, cmd.DataLength);
        Assert.Equal(source.CyclicCommands[0].ExpectedWkc, cmd.ExpectedWkc);
        Assert.Equal(source.CyclicCommands[0].InputOffs, cmd.InputOffs);
    }

    [Fact]
    public void Variables_survive_a_round_trip_with_their_offsets()
    {
        var source = Learned();

        var reloaded = RoundTrip(source);

        Assert.Equal(source.Variables.Count, reloaded.Variables.Count);
        Assert.Equal(source.Variables.Select(v => v.BitOffs),
            reloaded.Variables.Select(v => v.BitOffs));
        Assert.Equal(source.Variables.Select(v => v.Name),
            reloaded.Variables.Select(v => v.Name));
        Assert.Equal(source.Variables.Select(v => v.IsInput),
            reloaded.Variables.Select(v => v.IsInput));
    }

    /// <summary>Round-trips a configuration the learner cannot currently produce: outputs as well
    /// as inputs, a physical cyclic command alongside a logical one, a slave with no mailbox beside
    /// one with, and every scalar field set to a distinct value. The learner-derived tests above
    /// are all input-only with a single logical command, so without this the &lt;Outputs&gt; section,
    /// the Adp/Ado branch, and RevisionNo/DataType/BitSize round-trip only vacuously — a writer
    /// that dropped &lt;Outputs&gt; entirely would still pass them.</summary>
    [Fact]
    public void A_configuration_with_outputs_and_physical_commands_survives_a_round_trip()
    {
        var source = new EniConfiguration
        {
            Slaves =
            [
                new EniSlave("Term 1 (EK1100)", 1001, 0x0000, 2, 0x044C2C52, 0x00110000, null, null),
                new EniSlave("Drive 2 (AX5101)", 1002, 0xFFFF, 2, 0x13ED6012, 0x00000001,
                    new MailboxRange(0x1000, 128), new MailboxRange(0x1080, 128)),
            ],
            CyclicCommands =
            [
                new EniCyclicCommand(EtherCatCommand.Lrw, 0x01000000, 4, 6, 0, 8),
                new EniCyclicCommand(EtherCatCommand.Brd, (0x0130u << 16) | 0, 2, 4, null, null),
            ],
            Variables =
            [
                new EniVariable("Drive 2 (AX5101).Inputs.Statusword", "UINT", 16, 16, true),
                new EniVariable("Drive 2 (AX5101).Outputs.Controlword", "UINT", 16, 64, false),
                new EniVariable("Term 1 (EK1100).Outputs.Bit", "BOOL", 1, 0, false),
            ],
            CycleTimeMicroseconds = 1000,
        };

        var reloaded = RoundTrip(source);

        Assert.Equal(0x00110000u, reloaded.Slaves[0].RevisionNo);
        Assert.Null(reloaded.Slaves[0].MailboxOut);
        Assert.Null(reloaded.Slaves[0].MailboxIn);
        Assert.Equal(0x1000, reloaded.Slaves[1].MailboxOut!.Start);
        Assert.Equal(0x1080, reloaded.Slaves[1].MailboxIn!.Start);

        var logical = reloaded.CyclicCommands.Single(c => c.Command == EtherCatCommand.Lrw);
        Assert.Equal(0x01000000u, logical.RawAddress);
        Assert.Equal(0, logical.InputOffs);
        Assert.Equal(8, logical.OutputOffs);

        var physical = reloaded.CyclicCommands.Single(c => c.Command == EtherCatCommand.Brd);
        Assert.Equal((0x0130u << 16) | 0, physical.RawAddress);
        Assert.Null(physical.InputOffs);
        Assert.Null(physical.OutputOffs);

        Assert.Equal(2, reloaded.Variables.Count(v => !v.IsInput));
        var controlword = reloaded.Variables.Single(v => v.Name.EndsWith("Controlword"));
        Assert.False(controlword.IsInput);
        Assert.Equal("UINT", controlword.DataType);
        Assert.Equal(16, controlword.BitSize);
        Assert.Equal(64, controlword.BitOffs);
        Assert.Equal(1000, reloaded.CycleTimeMicroseconds);
    }

    [Fact]
    public void Written_file_is_loadable_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.eni.xml");
        try
        {
            EniXmlWriter.Write(Learned(), path);

            var reloaded = EniConfiguration.Load(path);
            Assert.Equal(2, reloaded.Slaves.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
