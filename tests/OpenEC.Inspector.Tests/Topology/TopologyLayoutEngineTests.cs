using OpenEC.Inspector.Topology;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyLayoutEngineTests
{
    private static TopologyDevice Device(ushort address, int ringPosition, params byte[] activePorts)
    {
        var ports = new Dictionary<byte, PortState>();
        for (byte port = 0; port < 4; port++)
        {
            var active = port == 0 || activePorts.Contains(port);
            ports[port] = new PortState(port, active, !active, active);
        }
        return new TopologyDevice(address, ringPosition, ports, new Dictionary<byte, PortCounters>());
    }

    private static TopologyLayout LayoutOf(params TopologyDevice[] devices) =>
        TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(devices));

    private static TopologyBox Box(TopologyLayout layout, ushort address) =>
        layout.Boxes.Single(b => b.Address == address);

    [Fact]
    public void A_line_lays_out_left_to_right_on_one_row()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1, 1), Device(1003, 2));

        Assert.All(layout.Boxes, b => Assert.Equal(0, b.Row));
        Assert.True(Box(layout, 1001).X < Box(layout, 1002).X);
        Assert.True(Box(layout, 1002).X < Box(layout, 1003).X);
        Assert.Equal(Box(layout, 1001).Y, Box(layout, 1002).Y);
    }

    [Fact]
    public void The_master_is_the_leftmost_box()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1));

        var master = Box(layout, BusTopology.MasterAddress);
        Assert.Equal(TopologyBoxKind.Master, master.Kind);
        Assert.All(layout.Boxes.Where(b => b.Address != BusTopology.MasterAddress),
            b => Assert.True(b.X > master.X));
    }

    [Fact]
    public void A_second_child_opens_a_new_row_beneath()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.Equal(0, Box(layout, 1002).Row);      // first child continues the parent's row
        Assert.Equal(1, Box(layout, 1003).Row);      // second child opens a new one
        Assert.True(Box(layout, 1003).Y > Box(layout, 1002).Y);
    }

    /// <summary>Nested branches are laid out depth first, so a branch's own sub-rows follow it
    /// rather than being interleaved with a later sibling's.</summary>
    [Fact]
    public void Nested_branches_get_successive_rows_depth_first()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1),
            Device(1002, 1, 1, 2),
            Device(1003, 2, 1),
            Device(1004, 3),
            Device(1005, 4));

        Assert.Equal(0, Box(layout, 1003).Row);      // continues 1002's row
        Assert.Equal(0, Box(layout, 1004).Row);
        Assert.Equal(1, Box(layout, 1005).Row);      // 1002's second branch
    }

    [Fact]
    public void No_two_boxes_overlap()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1, 2, 3), Device(1002, 1, 1), Device(1003, 2),
            Device(1004, 3), Device(1005, 4, 1), Device(1006, 5));

        foreach (var a in layout.Boxes)
            foreach (var b in layout.Boxes.Where(x => !ReferenceEquals(x, a)))
            {
                var overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width
                            && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Assert.False(overlaps, $"{a.Address} overlaps {b.Address}");
            }
    }

    [Fact]
    public void Structurally_significant_devices_are_wide_and_the_rest_are_narrow()
    {
        var layout = LayoutOf(
            Device(1001, 0, 1, 2),   // junction
            Device(1002, 1, 1),      // plain mid-line
            Device(1003, 2),         // line end
            Device(1004, 3));        // line end of the second branch

        Assert.True(Box(layout, 1001).IsWide);
        Assert.False(Box(layout, 1002).IsWide);
        Assert.True(Box(layout, 1003).IsWide);
        Assert.True(Box(layout, 1004).IsWide);
        Assert.True(Box(layout, 1001).Width > Box(layout, 1002).Width);
    }

    [Fact]
    public void A_device_with_two_downstream_ports_is_a_junction()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.Equal(TopologyBoxKind.Junction, Box(layout, 1001).Kind);
        Assert.Equal(TopologyBoxKind.LineEnd, Box(layout, 1002).Kind);
    }

    [Fact]
    public void Port_marks_sit_on_the_side_their_index_dictates()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        var box = Box(layout, 1001);
        Assert.Equal(PortSide.Left, box.Ports.Single(p => p.Port == 0).Side);
        Assert.Equal(PortSide.Right, box.Ports.Single(p => p.Port == 1).Side);
        Assert.Equal(PortSide.Bottom, box.Ports.Single(p => p.Port == 2).Side);
    }

    [Fact]
    public void Unused_ports_get_no_mark()
    {
        var layout = LayoutOf(Device(1001, 0, 1), Device(1002, 1));

        // Ports 2 and 3 have no link and a closed loop.
        Assert.DoesNotContain(Box(layout, 1001).Ports, p => p.Port is 2 or 3);
    }

    [Fact]
    public void A_topology_with_no_port_data_draws_no_port_marks_at_all()
    {
        var blind = new TopologyDevice(1001, 0, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());
        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct([blind]));

        Assert.False(layout.PortDataObserved);
        Assert.All(layout.Boxes, b => Assert.Empty(b.Ports));
    }

    [Fact]
    public void Every_wire_ends_on_the_ports_it_claims()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        foreach (var wire in layout.Wires.Where(w => w.FromAddress != BusTopology.MasterAddress))
        {
            var from = Box(layout, wire.FromAddress);
            var to = Box(layout, wire.ToAddress);
            var first = wire.Points[0];
            var last = wire.Points[^1];

            // The wire leaves inside the parent's bounds and arrives at the child's left edge.
            Assert.InRange(first.X, from.X, from.X + from.Width);
            Assert.InRange(first.Y, from.Y, from.Y + from.Height);
            Assert.Equal(to.X, last.X, precision: 3);
        }
    }

    [Fact]
    public void A_same_row_wire_is_a_straight_segment_and_a_branch_wire_is_not()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        var sameRow = layout.Wires.Single(w => w.FromAddress == 1001 && w.ToAddress == 1002);
        var branch = layout.Wires.Single(w => w.FromAddress == 1001 && w.ToAddress == 1003);

        Assert.Equal(2, sameRow.Points.Count);
        Assert.True(branch.Points.Count > 2);
        Assert.All(branch.Points.Zip(branch.Points.Skip(1)),
            pair => Assert.True(pair.First.X == pair.Second.X || pair.First.Y == pair.Second.Y,
                "branch wires must route orthogonally"));
    }

    [Fact]
    public void An_inferred_edge_is_flagged_on_both_the_box_and_the_wire()
    {
        var blind = new TopologyDevice(1002, 1, new Dictionary<byte, PortState>(),
            new Dictionary<byte, PortCounters>());
        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(
            [new TopologyDevice(1001, 0, new Dictionary<byte, PortState>(),
                new Dictionary<byte, PortCounters>()),
                blind]));

        Assert.True(Box(layout, 1002).EdgeInferred);
        Assert.True(layout.Wires.Single(w => w.ToAddress == 1002).IsInferred);
    }

    [Fact]
    public void The_canvas_extent_covers_every_box()
    {
        var layout = LayoutOf(Device(1001, 0, 1, 2), Device(1002, 1), Device(1003, 2));

        Assert.All(layout.Boxes, b => Assert.True(b.X + b.Width <= layout.Width));
        Assert.All(layout.Boxes, b => Assert.True(b.Y + b.Height <= layout.Height));
    }

    /// <summary>Compared as a fingerprint, not with Assert.Equal on the records: a record whose
    /// members are lists compares those members by REFERENCE, so two separately built layouts are
    /// never equal however identical their contents.</summary>
    [Fact]
    public void Layout_is_deterministic()
    {
        TopologyDevice[] Devices() => [Device(1001, 0, 1, 2), Device(1002, 1, 1), Device(1003, 2)];

        static string Fingerprint(TopologyLayout layout) => string.Join(';',
            layout.Boxes.Select(b =>
                $"{b.Address}:{b.Row}:{b.X}:{b.Y}:{b.Width}:{b.Height}:{b.Kind}:{b.IsWide}:{b.HasConflict}:"
                + string.Join(',', b.Ports.Select(p => $"{p.Port}{p.Side}{p.State}{p.X}{p.Y}")))
            .Concat(layout.Wires.Select(w =>
                $"{w.FromAddress}>{w.ToAddress}:{w.IsInferred}:{w.HasConflict}:"
                + string.Join(',', w.Points.Select(pt => $"{pt.X}/{pt.Y}")))));

        Assert.Equal(
            Fingerprint(TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(Devices()))),
            Fingerprint(TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(Devices()))));
    }

    /// <summary>Spec §7: an edge the ENI and the wire describe differently is drawn as the wire
    /// has it AND marked, so the map itself shows where the file and the machine disagree.</summary>
    [Fact]
    public void A_conflicting_edge_is_marked_on_its_box_and_its_wire()
    {
        var eni = new OpenEC.Monitor.Eni.EniConfiguration
        {
            Slaves =
            [
                new OpenEC.Monitor.Eni.EniSlave("Slave 1001", 1001, 0, 0, 0, 0, null, null),
                new OpenEC.Monitor.Eni.EniSlave("Slave 1002", 1002, 0xFFFF, 0, 0, 0, null, null,
                    new OpenEC.Monitor.Eni.EniPreviousPort(1001, 2)),   // the wire will say port 1
            ],
            CyclicCommands = [],
            Variables = [],
        };

        var layout = TopologyLayoutEngine.Layout(TopologyReconstructor.Reconstruct(
            [Device(1001, 0, 1), Device(1002, 1)], eni));

        Assert.True(Box(layout, 1002).HasConflict);
        Assert.True(layout.Wires.Single(w => w.ToAddress == 1002).HasConflict);
        Assert.False(Box(layout, 1001).HasConflict);
    }

    [Fact]
    public void Unplaced_devices_are_carried_through_rather_than_drawn()
    {
        var layout = LayoutOf(Device(1001, 0), Device(1002, 1));

        Assert.Equal(new ushort[] { 1002 }, layout.Unplaced);
        Assert.DoesNotContain(layout.Boxes, b => b.Address == 1002);
    }
}
