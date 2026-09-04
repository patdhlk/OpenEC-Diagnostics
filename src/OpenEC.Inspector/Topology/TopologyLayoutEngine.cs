using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Topology;

/// <summary>Turns a <see cref="BusTopology"/> into geometry. Pure and deterministic: the same
/// topology yields identical output, which is what both makes it testable and stops the map
/// jittering as facts arrive mid-session.</summary>
public static class TopologyLayoutEngine
{
    private const double BoxHeight = 44;
    private const double WideWidth = 52;
    private const double NarrowWidth = 16;
    private const double GapX = 8;
    private const double RowHeight = 96;
    private const double Margin = 20;
    private const double MarkThickness = 4;
    private const double MarkLength = 14;

    public static TopologyLayout Layout(BusTopology topology)
    {
        if (topology.Nodes.Count == 0) return TopologyLayout.Empty;

        var rows = AssignRows(topology);
        var boxes = new Dictionary<ushort, TopologyBox>();
        var order = new List<ushort>();
        var conflicted = topology.Conflicts.Select(c => c.Address).ToHashSet();

        // Rows are laid out in the order they were opened, so a parent always has a box by the
        // time its branch row needs to indent under it.
        foreach (var row in rows)
        {
            var x = row.Index == 0
                ? Margin
                : boxes[row.ParentAddress].X + WideWidth + GapX * 3;
            var y = Margin + row.Index * RowHeight;

            foreach (var node in row.Nodes)
            {
                var kind = KindOf(topology, node);
                var wide = IsWide(kind, node, isFirstInRow: ReferenceEquals(node, row.Nodes[0]));
                var width = wide ? WideWidth : NarrowWidth;
                var marks = topology.PortDataObserved ? Marks(node, width) : [];
                boxes[node.Address] = new TopologyBox(node.Address, row.Index, x, y,
                    width, BoxHeight, kind, wide, node.EdgeSource == TopologyEdgeSource.Inferred,
                    conflicted.Contains(node.Address), marks);
                order.Add(node.Address);
                x += width + GapX;
            }
        }

        var ordered = order.Select(address => boxes[address]).ToList();
        return new TopologyLayout(
            ordered,
            Wires(topology, boxes, conflicted),
            topology.Unplaced,
            topology.PortDataObserved,
            ordered.Max(b => b.X + b.Width) + Margin,
            ordered.Max(b => b.Y + b.Height) + Margin);
    }

    private sealed record Row(int Index, ushort ParentAddress, List<TopologyNode> Nodes);

    /// <summary>Depth-first row assignment. A node's FIRST child continues its row; every later
    /// child opens a new row directly beneath, so a branch's own sub-rows follow it rather than
    /// being interleaved with a later sibling's.</summary>
    private static List<Row> AssignRows(BusTopology topology)
    {
        var master = topology.Nodes.First(n => n.IsMaster);
        var rows = new List<Row> { new(0, master.Address, [master]) };
        // A node is placed at most once. TopologyReconstructor only ever emits a tree, so on
        // well-formed input this never fires — but a cycle here is not a wrong picture, it is an
        // unbounded recursion that overflows the stack, and a stack overflow cannot be caught: it
        // takes the whole process down from the 4 Hz refresh, with no exception and no chance for
        // the UI to report anything. The map is drawn on a snapshot of a live bus, so it costs one
        // hash set to make that failure mode structurally unreachable.
        var placed = new HashSet<ushort> { master.Address };
        Walk(master, rows[0]);
        return rows;

        void Walk(TopologyNode node, Row row)
        {
            var children = topology.ChildrenOf(node.Address)
                .Where(c => !placed.Contains(c.Address))
                .OrderBy(c => c.ParentPort ?? byte.MaxValue)
                .ThenBy(c => c.RingPosition < 0 ? int.MaxValue : c.RingPosition)
                .ToList();

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var target = row;
                if (i > 0)
                {
                    target = new Row(rows.Count, node.Address, []);
                    rows.Add(target);
                }
                placed.Add(child.Address);
                target.Nodes.Add(child);
                Walk(child, target);
            }
        }
    }

    private static TopologyBoxKind KindOf(BusTopology topology, TopologyNode node)
    {
        if (node.IsMaster) return TopologyBoxKind.Master;
        var downstream = topology.ChildrenOf(node.Address).Count();
        return downstream switch
        {
            0 => TopologyBoxKind.LineEnd,
            1 => TopologyBoxKind.Device,
            _ => TopologyBoxKind.Junction,
        };
    }

    /// <summary>Structurally significant devices are wide with a horizontal label; the rest are
    /// narrow with a rotated one. This approximates the reference tool's undocumented rule — see
    /// the design spec §10 — and is deliberately a single predicate so it is cheap to change once
    /// it has been seen rendered.</summary>
    private static bool IsWide(TopologyBoxKind kind, TopologyNode node, bool isFirstInRow) =>
        kind is TopologyBoxKind.Master or TopologyBoxKind.Junction or TopologyBoxKind.LineEnd
        || isFirstInRow
        || node.EdgeSource == TopologyEdgeSource.Inferred;

    /// <summary>Port marks. Port 0 sits on the left edge (upstream, toward the master), port 1 on
    /// the right (the line continuing), ports 2 and 3 beneath. Unused ports get no mark at all —
    /// an absent bar reads as "nothing here", which is exactly what Unused means.</summary>
    private static List<TopologyPortMark> Marks(TopologyNode node, double width)
    {
        var marks = new List<TopologyPortMark>();
        foreach (var (port, state) in node.Ports.OrderBy(kv => kv.Key))
        {
            if (state.State == PortLinkState.Unused) continue;
            var counters = node.Counters.GetValueOrDefault(port);
            var hasError = counters?.AnyError == true;
            marks.Add(port switch
            {
                0 => new TopologyPortMark(port, PortSide.Left, state.State, hasError,
                    -MarkThickness, (BoxHeight - MarkLength) / 2, MarkThickness, MarkLength),
                1 => new TopologyPortMark(port, PortSide.Right, state.State, hasError,
                    width, (BoxHeight - MarkLength) / 2, MarkThickness, MarkLength),
                _ => new TopologyPortMark(port, PortSide.Bottom, state.State, hasError,
                    port == 2 ? width / 2 - MarkLength - 2 : width / 2 + 2, BoxHeight,
                    MarkLength, MarkThickness),
            });
        }
        return marks;
    }

    /// <summary>One wire per edge. Same row: a straight segment from the parent's right edge to
    /// the child's left edge. Different row: orthogonal — down out of the parent, across at the
    /// child's centreline, into the child's left edge.</summary>
    private static List<TopologyWire> Wires(BusTopology topology,
        IReadOnlyDictionary<ushort, TopologyBox> boxes, IReadOnlySet<ushort> conflicted)
    {
        var wires = new List<TopologyWire>();
        foreach (var node in topology.Nodes.Where(n => !n.IsMaster))
        {
            if (node.ParentAddress is not { } parentAddress) continue;
            if (!boxes.TryGetValue(parentAddress, out var from)) continue;
            if (!boxes.TryGetValue(node.Address, out var to)) continue;

            var inferred = node.EdgeSource == TopologyEdgeSource.Inferred;
            var conflict = conflicted.Contains(node.Address);
            var toMid = to.Y + to.Height / 2;

            if (from.Row == to.Row)
            {
                wires.Add(new TopologyWire(parentAddress, node.Address, inferred, conflict,
                [
                    new TopologyPoint(from.X + from.Width, from.Y + from.Height / 2),
                    new TopologyPoint(to.X, toMid),
                ]));
                continue;
            }

            var exitX = from.X + from.Width / 2;
            wires.Add(new TopologyWire(parentAddress, node.Address, inferred, conflict,
            [
                new TopologyPoint(exitX, from.Y + from.Height),
                new TopologyPoint(exitX, toMid),
                new TopologyPoint(to.X, toMid),
            ]));
        }
        return wires;
    }
}
