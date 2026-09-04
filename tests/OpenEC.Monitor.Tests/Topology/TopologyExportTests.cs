using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class TopologyExportTests
{
    /// <summary>A three-slave line learned from register traffic: address assignments give ring
    /// order, DL-status reads give the ports. 1001 and 1002 forward on port 1; 1003 ends the line.
    /// </summary>
    private static LearnedBus LearnLine()
    {
        var bus = new LearnedBus();
        ushort[] stations = [1001, 1002, 1003];
        for (var position = 0; position < stations.Length; position++)
            bus.Observe(DateTimeOffset.UnixEpoch,
                new EtherCatDatagram(EtherCatCommand.Apwr, 0,
                    (0x0010u << 16) | (ushort)(0 - position), false, false, 0,
                    BitConverter.GetBytes(stations[position]), 1),
                FrameDirection.Outbound);

        // Link+open loop on ports 0 and 1 = 0x0030; port 0 only = 0x0010.
        foreach (var (station, raw) in new (ushort, ushort)[]
                 { (1001, 0x0030), (1002, 0x0030), (1003, 0x0010) })
            bus.Observe(DateTimeOffset.UnixEpoch,
                new EtherCatDatagram(EtherCatCommand.Fprd, 0, (0x0110u << 16) | station,
                    false, false, 0, BitConverter.GetBytes(raw), 1),
                FrameDirection.Returning);
        return bus;
    }

    [Fact]
    public void A_learned_slave_converts_to_a_topology_device()
    {
        var slave = LearnLine().Slaves.Single(s => s.StationAddress == 1001);

        var device = TopologyDevice.FromLearned(slave);

        Assert.Equal((ushort)1001, device.Address);
        Assert.Equal(0, device.RingPosition);
        Assert.True(device.HasPortData);
        Assert.Equal(new byte[] { 1 }, device.ActiveDownstreamPorts);
    }

    [Fact]
    public void The_synthesized_eni_carries_the_learned_topology()
    {
        var eni = EniSynthesizer.Synthesize(LearnLine(), new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());

        Assert.Null(Slave(eni, 1001).PreviousPort);                       // hangs off the master
        Assert.Equal(new EniPreviousPort(1001, 1), Slave(eni, 1002).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 1), Slave(eni, 1003).PreviousPort);
    }

    [Fact]
    public void Previous_port_round_trips_through_the_writer_and_the_parser()
    {
        var original = EniSynthesizer.Synthesize(LearnLine(),
            new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());
        var path = Path.Combine(Path.GetTempPath(), $"openec-topo-{Guid.NewGuid():N}.eni.xml");

        try
        {
            EniXmlWriter.Write(original, path);
            var reloaded = EniConfiguration.Load(path);

            foreach (var slave in original.Slaves)
                Assert.Equal(slave.PreviousPort, Slave(reloaded, slave.PhysAddr).PreviousPort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A bus learned without any DL-status read exports no topology at all, rather than a
    /// line the wire never showed.</summary>
    [Fact]
    public void A_bus_with_no_port_data_exports_no_previous_ports()
    {
        var bus = new LearnedBus();
        bus.Observe(DateTimeOffset.UnixEpoch,
            new EtherCatDatagram(EtherCatCommand.Apwr, 0, 0x0010_0000u, false, false, 0,
                BitConverter.GetBytes((ushort)1001), 1),
            FrameDirection.Outbound);

        var eni = EniSynthesizer.Synthesize(bus, new Dictionary<ushort, Dahlke.EtherCAT.Esi.EsiDevice>());

        Assert.All(eni.Slaves, s => Assert.Null(s.PreviousPort));
    }

    private static EniSlave Slave(EniConfiguration eni, ushort address) =>
        eni.Slaves.Single(s => s.PhysAddr == address);
}
