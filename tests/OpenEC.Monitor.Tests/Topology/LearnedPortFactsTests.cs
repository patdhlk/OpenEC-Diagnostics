using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class LearnedPortFactsTests
{
    private static EtherCatDatagram Read(ushort adp, ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    private static LearnedBus BusWithStation(ushort station)
    {
        var bus = new LearnedBus();
        // APWR 0x0010 at auto-inc 0 assigns the station address, anchoring ring position 0.
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0, 0x0010_0000u, false, false, 0,
                BitConverter.GetBytes(station), 1),
            FrameDirection.Outbound);
        return bus;
    }

    [Fact]
    public void Dl_status_reads_land_on_the_addressed_slave()
    {
        var bus = BusWithStation(1001);

        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0110, [0x30, 0x0A]),
            FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal(PortLinkState.Active, slave.Ports[0].State);
        Assert.Equal(PortLinkState.Active, slave.Ports[1].State);
        Assert.Equal(new byte[] { 1 }, slave.ActiveDownstreamPorts);
    }

    [Fact]
    public void Active_downstream_ports_follow_the_esc_forwarding_order()
    {
        var bus = BusWithStation(1001);

        // Link + open loop on ports 0, 1, 2 and 3: link bits 0x00F0, all loops open.
        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0110, [0xF0, 0x00]),
            FrameDirection.Returning);

        // Port 0 is upstream, so the downstream ports are the rest in forwarding order 3, 1, 2.
        Assert.Equal(new byte[] { 3, 1, 2 }, Assert.Single(bus.Slaves).ActiveDownstreamPorts);
    }

    [Fact]
    public void Counter_reads_merge_rather_than_replace()
    {
        var bus = BusWithStation(1001);

        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0310, [4, 0, 0, 0]),
            FrameDirection.Returning);
        bus.Observe(DateTimeOffset.UnixEpoch, Read(1001, 0x0300, [114, 0]),
            FrameDirection.Returning);

        var slave = Assert.Single(bus.Slaves);
        Assert.Equal((byte)4, slave.Counters[0].LostLink);      // survived the second read
        Assert.Equal((byte)114, slave.Counters[0].InvalidFrame);
    }

    [Fact]
    public void A_slave_with_no_port_read_has_no_port_facts_at_all()
    {
        var slave = Assert.Single(BusWithStation(1001).Slaves);

        Assert.Empty(slave.Ports);
        Assert.Empty(slave.Counters);
        Assert.Null(slave.ProcessingUnitErrors);
        Assert.Empty(slave.ActiveDownstreamPorts);
    }
}
