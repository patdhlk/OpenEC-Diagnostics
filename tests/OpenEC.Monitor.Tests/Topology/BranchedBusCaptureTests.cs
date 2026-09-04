using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class BranchedBusCaptureTests
{
    private static LearnedBus Learn(IEnumerable<(DateTimeOffset Timestamp, byte[] Frame)> frames)
    {
        var bus = new LearnedBus();
        var direction = new DirectionTracker();
        foreach (var (timestamp, frame) in frames)
        {
            if (EtherCatFrameParser.Parse(frame) is not FrameDecodeResult.Success ok) continue;
            var dir = direction.Classify(ok.Frame);
            foreach (var datagram in ok.Frame.Datagrams)
                bus.Observe(timestamp, datagram, dir);
        }
        return bus;
    }

    [Fact]
    public void The_branched_capture_learns_four_slaves_in_ring_order()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        Assert.Equal([1001, 1002, 1003, 1004], bus.Slaves.Select(s => s.StationAddress));
    }

    [Fact]
    public void The_branched_capture_reconstructs_the_expected_tree()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        var topology = TopologyReconstructor.Reconstruct(
            bus.Slaves.Select(TopologyDevice.FromLearned).ToList());

        Assert.True(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1002)!.ParentPort);
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
    }

    [Fact]
    public void The_branched_capture_carries_error_counters()
    {
        var bus = Learn(BranchedBusCapture.Frames(cycles: 3));

        var junction = bus.Slaves.Single(s => s.StationAddress == 1001);
        Assert.True(junction.Counters[0].AnyKnown);
    }

    [Fact]
    public void The_branched_capture_writes_a_readable_pcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-branched-{Guid.NewGuid():N}.pcap");
        try
        {
            BranchedBusCapture.Write(path, cycles: 3);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_observer_exposes_the_branched_topology_from_the_capture()
    {
        var observer = new BusObserver();
        foreach (var (timestamp, frame) in BranchedBusCapture.Frames(cycles: 3))
            observer.Process(timestamp, EtherCatFrameParser.Parse(frame));

        var topology = observer.SnapshotTopology();

        Assert.True(topology.PortDataObserved);
        Assert.Equal((ushort)1001, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);
    }
}
