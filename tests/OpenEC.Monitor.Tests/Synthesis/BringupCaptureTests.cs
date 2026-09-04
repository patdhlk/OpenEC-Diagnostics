using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Synthesis;

public class BringupCaptureTests
{
    /// <summary>Feeds a generated bringup through the parser and learner exactly as the
    /// live pump would, so the fixture is validated against the real decode path.</summary>
    private static LearnedBus Learn()
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
    public void Generated_bringup_yields_two_slaves_in_ring_order()
    {
        var bus = Learn();

        Assert.True(bus.SawStartup);
        Assert.Equal(2, bus.Slaves.Count);
        Assert.Equal(new[] { 1001, 1002 }, bus.Slaves.Select(s => (int)s.StationAddress));
        Assert.Equal(new[] { 0, 1 }, bus.Slaves.Select(s => s.RingPosition));
    }

    [Fact]
    public void Generated_bringup_carries_identity()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(0x00120000u, slave.Revision);
    }

    [Fact]
    public void Generated_bringup_configures_sync_managers_and_fmmus()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(0x1000, slave.SyncManagers[0].PhysicalStart);   // mailbox out
        Assert.Equal(0x1100, slave.SyncManagers[3].PhysicalStart);   // inputs
        Assert.Equal(FmmuType.Inputs, slave.Fmmus[0].Type);
        Assert.Equal(0x00010000u, slave.Fmmus[0].LogicalStart);
    }

    /// <summary>The second slave's logical start is where a position-arithmetic off-by-one would
    /// hide, and Task 8's bit offsets depend on it — so it is asserted here at the source rather
    /// than left to surface as a confusing offset mismatch downstream.</summary>
    [Fact]
    public void Each_slave_maps_into_its_own_logical_byte()
    {
        var slaves = Learn().Slaves;

        Assert.Equal(0x00010000u, slaves[0].Fmmus[0].LogicalStart);
        Assert.Equal(0x00010001u, slaves[1].Fmmus[0].LogicalStart);
        Assert.Equal(FmmuType.Inputs, slaves[1].Fmmus[0].Type);
        Assert.Equal(0x1100, slaves[1].SyncManagers[3].PhysicalStart);
    }

    [Fact]
    public void Generated_bringup_assigns_and_maps_pdos()
    {
        var slave = Learn().Slaves[0];

        Assert.Equal(new ushort[] { 0x1A00 }, slave.AssignedPdos(3));
        var mapping = slave.Mapping(0x1A00);
        Assert.Equal(8, mapping.Count);
        Assert.All(mapping, e => Assert.Equal(1, e.BitLength));
    }

    [Fact]
    public void Generated_bringup_produces_a_cyclic_command_table()
    {
        var cyclic = Assert.Single(Learn().CyclicCommands);

        Assert.Equal(EtherCatCommand.Lrd, cyclic.Command);
        Assert.Equal(0x00010000u, cyclic.RawAddress);
        Assert.Equal(2, cyclic.ExpectedWkc);
    }

    /// <summary>Round-trips the written file back through the real pcap reader. A length check
    /// alone would pass for any non-empty blob, and Task 12 reads this file through
    /// <see cref="PcapFileSource"/>, so parseability is the property that actually matters.</summary>
    [Fact]
    public async Task Written_capture_is_a_readable_pcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bringup-{Guid.NewGuid():N}.pcap");
        try
        {
            BringupCapture.Write(path, cycles: 3);

            await using var source = new PcapFileSource(path);
            var readBack = 0;
            await foreach (var _ in source.CaptureAsync())
                readBack++;

            Assert.Equal(BringupCapture.Frames(cycles: 3).Count, readBack);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The two-slave bringup is a LINE: 1001 forwards on port 1, 1002 ends it. Added so
    /// the topology facts have end-to-end coverage on the fixture every other learning test uses.
    /// </summary>
    [Fact]
    public void The_bringup_capture_carries_dl_status_for_its_line()
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

        var topology = OpenEC.Monitor.Topology.TopologyReconstructor.Reconstruct(
            bus.Slaves.Select(OpenEC.Monitor.Topology.TopologyDevice.FromLearned).ToList());

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
    }
}
