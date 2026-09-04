using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Learning;

/// <summary>Real traffic, not synthesis. `twincat-ring-scan.pcap` is a short slice of a TwinCAT
/// master restarting a 16-slave bus, captured through a passive network tap. It exists because the
/// synthetic bringup agreed with the decoders about something neither of them had checked: that a
/// returning auto-increment datagram carries the ADP the master sent. It does not — every slave
/// increments it on the way past — and the whole scan was therefore discarded on hardware while the
/// entire test suite stayed green. The capture is anonymized: the master MAC's device-specific bytes
/// are rewritten (its direction bit preserved) and it carries no device serial numbers; the vendor
/// and product codes are the generic terminal identities the bus reports.
///
/// The slice deliberately starts mid-capture, inside the scan, so it also pins that the ring length
/// is recoverable from the scan's own broadcast rather than from traffic that happened earlier.
///
/// The window covers, in the order a real master emits them:
///   Bwr  0x0010  broadcast-clear the station addresses  (returning ADP = 16 = the ring length)
///   Aprd 0x0110  DL status, by auto-increment
///   Apwr 0x0502 / Aprd 0x0508  SII identity, by auto-increment
///   Apwr 0x0010  the station addresses, assigned last, from what the scan found</summary>
public class RealRingScanTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "twincat-ring-scan.pcap");

    private static async Task<EtherCatMonitor> RunAsync()
    {
        var monitor = EtherCatMonitor.OpenFile(Fixture);
        await monitor.RunAsync();
        return monitor;
    }

    [Fact]
    public async Task The_scan_is_recognised_as_a_bus_startup()
    {
        await using var monitor = await RunAsync();

        Assert.True(monitor.Learned!.Completeness.SawStartup);
        Assert.Equal(16, monitor.Learned.Configuration.Slaves.Count);
    }

    /// <summary>Identity is read out of SII during the scan, by auto-increment, on the return leg —
    /// the exact combination that used to be dropped. Before the ADP fix this was zero for all 16.
    /// </summary>
    [Fact]
    public async Task Every_slave_gets_its_identity_from_the_scan()
    {
        await using var monitor = await RunAsync();

        Assert.All(monitor.Learned!.Completeness.Slaves, s => Assert.True(s.IdentityKnown,
            $"slave {s.StationAddress} learned no identity from the ring scan"));
    }

    /// <summary>Identity read before the station addresses existed still ends up on the right
    /// slave. These are the real vendor and product codes off this bus, so a mis-attribution by one
    /// ring position — the failure mode an ADP offset produces — shows up here as a wrong pairing
    /// rather than as absent data.</summary>
    [Fact]
    public async Task Identity_lands_on_the_slave_it_was_read_from()
    {
        await using var monitor = await RunAsync();

        var byAddress = monitor.Learned!.Configuration.Slaves.ToDictionary(s => s.PhysAddr);

        Assert.Equal(0x0021u, byAddress[1001].VendorId);
        Assert.Equal(0x07500354u, byAddress[1001].ProductCode);
        Assert.Equal(0x0002u, byAddress[1002].VendorId);
        Assert.Equal(0x044C2C52u, byAddress[1002].ProductCode);
        Assert.Equal(0x0002u, byAddress[1003].VendorId);
        Assert.Equal(0x17B63052u, byAddress[1003].ProductCode);
        // 1005-1016 are twelve identical terminals; 1004 is not one of them, which is what makes
        // the boundary between them worth asserting.
        Assert.NotEqual(byAddress[1005].ProductCode, byAddress[1004].ProductCode);
        Assert.All(Enumerable.Range(1005, 12),
            address => Assert.Equal(0x00004000u, byAddress[(ushort)address].ProductCode));
    }

    /// <summary>The map is reconstructed from observed port state rather than falling back to ring
    /// order. This bus is not a straight line: 1003 and 1004 both hang off 1002, on ports 1 and 2.
    /// A reconstruction that never saw the DL-status reads draws it as a flat chain of sixteen and
    /// says so in a notice — which is what the Inspector showed before this fix.</summary>
    [Fact]
    public async Task The_real_branch_in_the_bus_is_reconstructed_from_the_wire()
    {
        await using var monitor = await RunAsync();
        var topology = monitor.Observer.SnapshotTopology();

        Assert.True(topology.PortDataObserved);
        Assert.Empty(topology.Unplaced);
        Assert.Equal(BusTopology.MasterAddress, topology.Find(1001)!.ParentAddress);
        Assert.Equal((ushort)1001, topology.Find(1002)!.ParentAddress);

        // The branch: two children on one device, on different ports.
        Assert.Equal((ushort)1002, topology.Find(1003)!.ParentAddress);
        Assert.Equal((byte)1, topology.Find(1003)!.ParentPort);
        Assert.Equal((ushort)1002, topology.Find(1004)!.ParentAddress);
        Assert.Equal((byte)2, topology.Find(1004)!.ParentPort);

        // And the long line hanging off the branch.
        Assert.Equal((ushort)1004, topology.Find(1005)!.ParentAddress);
        Assert.Equal((ushort)1015, topology.Find(1016)!.ParentAddress);

        Assert.All(topology.Nodes.Where(n => !n.IsMaster),
            n => Assert.Equal(TopologyEdgeSource.Wire, n.EdgeSource));
    }

    /// <summary>Whatever else it does, the reconstruction hands its consumers a tree — the
    /// invariant TopologyLayoutEngine recurses on, checked here against real traffic rather than
    /// against a fixture written by the same hand as the code.</summary>
    [Fact]
    public async Task The_reconstructed_parent_graph_is_a_tree()
    {
        await using var monitor = await RunAsync();
        var topology = monitor.Observer.SnapshotTopology();

        var slaves = topology.Nodes.Where(n => !n.IsMaster).Select(n => n.Address).ToList();
        Assert.Equal(slaves.Count, slaves.Distinct().Count());
        Assert.DoesNotContain(BusTopology.MasterAddress, slaves);

        foreach (var node in topology.Nodes.Where(n => !n.IsMaster))
        {
            var seen = new HashSet<ushort>();
            var current = node;
            while (current is { IsMaster: false })
            {
                Assert.True(seen.Add(current.Address),
                    $"cycle in the parent graph, reached again at {current.Address}");
                current = topology.Find(current.ParentAddress!.Value);
            }
            Assert.NotNull(current);
        }
    }

    /// <summary>Every TwinCAT slave here also maps one byte of ESC REGISTER space (0x080D, a
    /// SyncManager status byte) into the process image through an enabled input FMMU. It has no
    /// SyncManager window behind it because it is not pointing at one, and requiring every enabled
    /// FMMU to resolve declared all sixteen slaves of a healthy bus unable to place their process
    /// data.</summary>
    [Fact]
    public async Task A_register_mapped_fmmu_does_not_make_a_slave_report_unplaceable_process_data()
    {
        await using var monitor = await RunAsync();

        Assert.All(monitor.Learned!.Completeness.Slaves, s => Assert.True(s.ProcessDataPlaceable,
            $"slave {s.StationAddress} reported its process data unplaceable"));
    }

    /// <summary>The published assessment has to describe the end of the capture, not the last
    /// moment the synthesized configuration happened to change — and it has to manage that without
    /// turning one bringup into hundreds of revisions.
    ///
    /// Both halves are asserted together because each is the other's failure mode: gating the
    /// assessment on the configuration digest froze 13 of these 16 slaves at a state they had
    /// already grown out of, and gating it on nothing at all produced 415 revisions for this same
    /// capture, every one of them announcing a configuration nobody had altered.</summary>
    [Fact]
    public async Task The_published_completeness_is_current_without_churning_revisions()
    {
        var learner = new BusLearner();
        var revisions = 0;
        learner.ConfigurationLearned += _ => revisions++;

        await using (var source = new PcapFileSource(Fixture))
            await foreach (var raw in source.CaptureAsync(default))
                learner.Observe(raw.Timestamp, EtherCatFrameParser.Parse(raw.Data));

        // 15 of the 16 have FMMUs written during this window; 1002 is configured outside it.
        Assert.Equal(15, learner.Current!.Completeness.Slaves.Count(s => s.FmmusKnown));
        Assert.All(learner.Current.Completeness.Slaves, s => Assert.True(s.ProcessDataPlaceable));
        Assert.InRange(revisions, 1, 40);
    }
}
