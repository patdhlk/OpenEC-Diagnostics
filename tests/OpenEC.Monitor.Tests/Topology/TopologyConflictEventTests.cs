using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyConflictEventTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static EniConfiguration EniClaimingPortTwo() => new()
    {
        Slaves =
        [
            new EniSlave("Slave 1001", 1001, 0, 0, 0, 0, null, null),
            new EniSlave("Slave 1002", 1002, 0xFFFF, 0, 0, 0, null, null,
                new EniPreviousPort(1001, 2)),
        ],
        CyclicCommands = [],
        Variables = [],
    };

    [Fact]
    public void A_wire_versus_eni_disagreement_is_raised_once()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Rebind(EniClaimingPortTwo());

        var events = new List<MonitorEvent>();
        foreach (var (position, station, raw) in new (int, ushort, ushort)[]
                 { (0, 1001, 0x0030), (1, 1002, 0x0010) })
        {
            events.AddRange(tracker.Observe(T0,
                new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                    (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                    BitConverter.GetBytes(station), 1),
                FrameDirection.Outbound));
            events.AddRange(tracker.Observe(T0,
                new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                    false, false, 0, BitConverter.GetBytes(raw), 1),
                FrameDirection.Returning));
        }

        var mismatch = Assert.Single(events.OfType<MonitorEvent.ConfigMismatch>());
        Assert.Equal(ConfigMismatchKind.Topology, mismatch.Kind);
        Assert.Equal((ushort)1002, mismatch.Address);
        Assert.Equal("1001 port 2", mismatch.Declared);
        Assert.Equal("1001 port 1", mismatch.Observed);
    }

    [Fact]
    public void The_same_conflict_is_not_raised_again_on_a_later_identical_read()
    {
        var model = new BusModel();
        model.GetOrAdd(1001);
        model.GetOrAdd(1002);
        var tracker = new TopologyTracker(model);
        tracker.Rebind(EniClaimingPortTwo());

        void Assign(int position, ushort station) => tracker.Observe(T0,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                BitConverter.GetBytes(station), 1), FrameDirection.Outbound).ToList();

        IEnumerable<MonitorEvent> DlStatus(ushort station, ushort raw) => tracker.Observe(T0,
            new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                false, false, 0, BitConverter.GetBytes(raw), 1), FrameDirection.Returning);

        Assign(0, 1001);
        Assign(1, 1002);
        DlStatus(1001, 0x0030).ToList();
        DlStatus(1002, 0x0010).ToList();

        // A repeated, identical poll must not re-report a standing disagreement.
        var again = DlStatus(1001, 0x0030).Concat(DlStatus(1002, 0x0010)).ToList();

        Assert.Empty(again.OfType<MonitorEvent.ConfigMismatch>());
    }
}
