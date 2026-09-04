using Dahlke.EtherCAT.Esi;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class LearningCompletenessTests
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
    public void A_full_bringup_is_assessed_as_complete()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.True(completeness.SawStartup);
        Assert.All(completeness.Slaves, s =>
        {
            Assert.True(s.IdentityKnown);
            Assert.True(s.SyncManagersKnown);
            Assert.True(s.FmmusKnown);
            Assert.True(s.PdoMappingKnown);
            Assert.True(s.ProcessDataPlaceable);
        });
    }

    [Fact]
    public void A_mid_run_attach_is_assessed_as_incomplete()
    {
        var bus = new LearnedBus();
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0130u << 16) | 1005, false, false, 0,
                new byte[] { 0x08, 0x00 }, 1),
            FrameDirection.Returning);

        var completeness = LearningCompleteness.Assess(bus, new Dictionary<ushort, EsiDevice>());

        Assert.False(completeness.SawStartup);
        Assert.False(Assert.Single(completeness.Slaves).IdentityKnown);
        Assert.False(completeness.IsComplete);
    }

    [Fact]
    public void Summary_reports_how_many_slaves_are_fully_learned()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.Contains("2/2", completeness.Summary);
    }

    /// <summary>An FMMU whose physical window matches no configured SyncManager cannot have its
    /// variables placed. EniSynthesizer drops them silently, so completeness is the only surface
    /// that can say so — otherwise a short configuration reads as a complete one.</summary>
    [Fact]
    public void An_fmmu_with_no_matching_sync_manager_is_not_placeable()
    {
        var bus = new LearnedBus();
        var t = DateTimeOffset.UnixEpoch;
        void Physical(EtherCatCommand cmd, ushort adp, ushort ado, byte[] payload) =>
            bus.Observe(t, new EtherCatDatagram(cmd, 0, ((uint)ado << 16) | adp, false, false, 0,
                payload, 1), FrameDirection.Outbound);

        Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, BitConverter.GetBytes((ushort)1001));

        // An enabled input FMMU pointing at physical 0x1100 — but no SyncManager is ever configured.
        var fmmu = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(fmmu, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(fmmu, 4);
        fmmu[7] = 7;
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[11] = 1;
        fmmu[12] = 1;
        Physical(EtherCatCommand.Fpwr, 1001, 0x0600, fmmu);

        var slave = Assert.Single(
            LearningCompleteness.Assess(bus, new Dictionary<ushort, EsiDevice>()).Slaves);

        Assert.True(slave.FmmusKnown);
        Assert.False(slave.ProcessDataPlaceable);
        Assert.False(slave.IsComplete);
    }

    [Fact]
    public void Names_are_marked_inferred_when_no_esi_schema_resolved()
    {
        var completeness = LearningCompleteness.Assess(LearnBringup(),
            new Dictionary<ushort, EsiDevice>());

        Assert.All(completeness.Slaves, s => Assert.False(s.NamesFromEsi));
    }

    /// <summary>A device can carry a name without carrying process data — a bus coupler does, and
    /// so does any modular device, whose PDOs live under &lt;Modules&gt; and are out of the ESI
    /// catalogue's scope. The flag must follow the name, not the process data.</summary>
    [Fact]
    public void Names_come_from_esi_even_when_the_device_declares_no_process_data()
    {
        var bus = LearnBringup();
        var coupler = new EsiDevice(
            VendorName: "Beckhoff Automation GmbH",
            NameEn: "EK1100 EtherCAT Coupler",
            NameDe: null, Group: null, Url: null, EBusCurrentMa: null,
            ObjectDictionary: null, ProcessData: null);
        var schemas = new Dictionary<ushort, EsiDevice> { [1001] = coupler };

        var completeness = LearningCompleteness.Assess(bus, schemas);
        var slave = completeness.Slaves.Single(s => s.StationAddress == 1001);

        Assert.True(slave.NamesFromEsi);
    }

    /// <summary>Assignment observed, mapping downloads never seen, no ESI: the synthesizer resolves
    /// a SyncManager and an assigned PDO but has no entries to place, so it emits nothing. Reporting
    /// mapping as known here would claim a fact that produced no variable at all.</summary>
    [Fact]
    public void Assignment_without_mapping_is_not_reported_as_known()
    {
        var bus = new LearnedBus();
        var t = DateTimeOffset.UnixEpoch;
        void Physical(EtherCatCommand cmd, ushort adp, ushort ado, byte[] payload) =>
            bus.Observe(t, new EtherCatDatagram(cmd, 0, ((uint)ado << 16) | adp, false, false, 0,
                payload, 1), FrameDirection.Outbound);

        Physical(EtherCatCommand.Apwr, 0x0000, 0x0010, BitConverter.GetBytes((ushort)1001));

        var sm = new byte[8];
        BitConverter.GetBytes((ushort)0x1100).CopyTo(sm, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(sm, 2);
        sm[6] = 0x01;
        Physical(EtherCatCommand.Fpwr, 1001, (ushort)(0x0800 + 8 * 3), sm);

        var fmmu = new byte[16];
        BitConverter.GetBytes(0x00010000u).CopyTo(fmmu, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(fmmu, 4);
        fmmu[7] = 7;
        BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
        fmmu[11] = 1;
        fmmu[12] = 1;
        Physical(EtherCatCommand.Fpwr, 1001, 0x0600, fmmu);

        // Assignment only — the 0x1A00 mapping downloads are deliberately absent.
        foreach (var (sub, value) in new (byte, uint)[] { (1, 0x1A00u), (0, 1u) })
            Physical(EtherCatCommand.Fpwr, 1001, 0x1000,
                MailboxDecoderTests.CoeMailbox(1001,
                    MailboxDecoderTests.ExpeditedSdo(2, 0x23, 0x1C13, sub, value)));

        var schemas = new Dictionary<ushort, EsiDevice>();
        var slave = Assert.Single(LearningCompleteness.Assess(bus, schemas).Slaves);

        Assert.True(slave.ProcessDataPlaceable);
        Assert.False(slave.PdoMappingKnown);
        Assert.Empty(EniSynthesizer.Synthesize(bus, schemas).Variables);
    }
}
