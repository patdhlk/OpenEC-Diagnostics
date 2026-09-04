using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Tests.Topology;

public class DlStatusDecoderTests
{
    private static EtherCatDatagram Read(ushort adp, ushort ado, byte[] payload, ushort wkc = 1) =>
        new(EtherCatCommand.Fprd, 0, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    /// <summary>A mid-line terminal: link on ports 0 and 1, both loops open, both partners
    /// powered; ports 2 and 3 are Unused because their loops are closed. Bits 4,5 (link 0,1) +
    /// bits 9,11 (signal 0,1) + bits 12,14 (loops closed 2,3) = 0x5A30.</summary>
    [Fact]
    public void Mid_line_terminal_reports_two_active_ports()
    {
        var d = Read(1001, 0x0110, [0x30, 0x5A]);

        var fact = RegisterDecoders.TryDlStatus(d, FrameDirection.Returning);

        Assert.NotNull(fact);
        Assert.Equal(PortLinkState.Active, fact!.Ports[0].State);
        Assert.Equal(PortLinkState.Active, fact.Ports[1].State);
        Assert.Equal(PortLinkState.Unused, fact.Ports[2].State);
        Assert.Equal(PortLinkState.Unused, fact.Ports[3].State);
    }

    /// <summary>Link present on port 1 but its loop is closed: the cable is in and frames are
    /// not passing. Bit 5 (link 1) + bit 10 (loop closed 1) = 0x0420.</summary>
    [Fact]
    public void Link_with_a_closed_loop_is_blocked()
    {
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x20, 0x04]), FrameDirection.Returning);

        Assert.Equal(PortLinkState.Blocked, fact!.Ports[1].State);
        Assert.False(fact.Ports[1].IsActive);
    }

    /// <summary>Loop open with no link: frames leave into nothing. Bit 8 clear means port 0's
    /// loop is open; no link bit is set. Raw 0x0000 gives every port an open loop.</summary>
    [Fact]
    public void Open_loop_without_a_link_is_dangling()
    {
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x00, 0x00]), FrameDirection.Returning);

        Assert.Equal(PortLinkState.Dangling, fact!.Ports[0].State);
    }

    [Fact]
    public void Each_port_reads_its_own_bit_triple()
    {
        // Every link bit set (0x00F0), every loop closed (0x5500), no signal.
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0xF0, 0x55]), FrameDirection.Returning);

        for (byte port = 0; port < 4; port++)
        {
            Assert.True(fact!.Ports[port].HasLink);
            Assert.True(fact.Ports[port].LoopClosed);
            Assert.False(fact.Ports[port].SignalDetected);
        }
    }

    [Fact]
    public void Signal_detected_is_recorded_per_port_without_changing_state()
    {
        // Link + open loop + signal on port 2: bit 6 (0x0040) + bit 13 (0x2000).
        var fact = RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x40, 0x20]), FrameDirection.Returning);

        Assert.True(fact!.Ports[2].SignalDetected);
        Assert.Equal(PortLinkState.Active, fact.Ports[2].State);
    }

    [Fact]
    public void Outbound_reads_and_other_registers_and_zero_wkc_are_ignored()
    {
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30, 0x0A]), FrameDirection.Outbound));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0120, [0x30, 0x0A]), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30, 0x0A], wkc: 0), FrameDirection.Returning));
        Assert.Null(RegisterDecoders.TryDlStatus(
            Read(1001, 0x0110, [0x30]), FrameDirection.Returning));
    }
}
