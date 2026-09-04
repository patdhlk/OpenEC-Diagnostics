using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Topology;

/// <summary>Turns ring order plus per-device active-port sets into a tree. Pure: no I/O, no
/// mutation of its input, same input yields the same output.</summary>
public static class TopologyReconstructor
{
    /// <summary>The ESC's internal frame forwarding order. A frame enters at port 0 and is
    /// forwarded 0 → 3 → 1 → 2, so a device with two open downstream ports branches out of the
    /// earlier one in this sequence first. This ordering decides the map's row order and is
    /// marked unverified in the design spec §10 — it lives here, in one place, so confirming it
    /// against a real capture is a one-line change.</summary>
    public static readonly byte[] ForwardingOrder = [0, 3, 1, 2];

    /// <summary>Devices in ring order, unknown positions last by address — the same ordering
    /// <see cref="Learning.LearnedBus.Slaves"/> uses, so the map and the device tree agree.</summary>
    private static List<TopologyDevice> InRingOrder(IReadOnlyList<TopologyDevice> devices) =>
        devices
            .OrderBy(d => d.RingPosition < 0 ? int.MaxValue : d.RingPosition)
            .ThenBy(d => d.Address)
            .ToList();

    public static BusTopology Reconstruct(IReadOnlyList<TopologyDevice> devices) =>
        Reconstruct(devices, eni: null);

    /// <summary>The wire is the authority; the ENI fills gaps and its disagreements are reported.
    /// Spec §3.</summary>
    public static BusTopology Reconstruct(IReadOnlyList<TopologyDevice> devices, EniConfiguration? eni)
    {
        // Zero is the master's own stand-in (BusTopology.MasterAddress), so a device carrying it is
        // not addressable as a slave: it is an unconfigured slave that has not been assigned a
        // station address yet, or a broadcast datagram whose ADP named no one in particular. Placing
        // it would give the resulting node the master's address, making it its own parent and turning
        // the parent graph from a tree into a cycle — which every consumer that walks the tree
        // recurses on forever. Reported as unplaced rather than dropped, so a bus that really is
        // sending these still says so instead of quietly showing a shorter map.
        var reserved = devices
            .Where(d => d.Address == BusTopology.MasterAddress)
            .Select(d => d.Address)
            .Distinct()
            .ToList();
        var ordered = InRingOrder(
            devices.Where(d => d.Address != BusTopology.MasterAddress).ToList());
        if (ordered.Count == 0) return BusTopology.Empty with { Unplaced = reserved };

        var declared = eni?.Slaves
            .Where(s => s.PreviousPort is not null)
            .GroupBy(s => s.PhysAddr)
            .ToDictionary(g => g.Key, g => g.Last().PreviousPort!)
            ?? new Dictionary<ushort, EniPreviousPort>();

        if (ordered.Any(d => d.HasPortData))
        {
            var fromWire = FromPorts(ordered);
            return fromWire with
            {
                Conflicts = Conflicts(fromWire, declared),
                Unplaced = [.. fromWire.Unplaced, .. reserved],
            };
        }

        var result = declared.Count > 0 ? FromEni(ordered, declared) : RingOrderOnly(ordered);
        return result with { Unplaced = [.. result.Unplaced, .. reserved] };
    }

    /// <summary>Compares the drawn tree against what the ENI declared. Only devices the wire
    /// actually placed are compared: a device the wire never described has no observed edge to
    /// disagree with, and reporting one would accuse a healthy machine.</summary>
    private static List<TopologyConflict> Conflicts(BusTopology wire,
        IReadOnlyDictionary<ushort, EniPreviousPort> declared)
    {
        var conflicts = new List<TopologyConflict>();
        foreach (var node in wire.Nodes.Where(n => !n.IsMaster
                                                   && n.EdgeSource == TopologyEdgeSource.Wire))
        {
            if (!declared.TryGetValue(node.Address, out var edge)) continue;
            if (edge.PhysAddr == node.ParentAddress && edge.Port == node.ParentPort) continue;
            conflicts.Add(new TopologyConflict(node.Address,
                $"{edge.PhysAddr} port {edge.Port}",
                $"{node.ParentAddress} port {node.ParentPort}"));
        }
        return conflicts;
    }

    /// <summary>Every edge from the ENI. An edge naming a parent that is not on the bus cannot be
    /// honoured, so that device falls back to its ring-order predecessor and is labelled inferred
    /// rather than being dropped from the map.
    ///
    /// An edge is honoured only when its parent has already been placed. A device's previous port
    /// names the device upstream of it, which in ring order is always one already seen, so this
    /// costs nothing for a well-formed ENI — and it is what keeps a malformed one from describing a
    /// device as its own parent, or two devices as each other's, either of which would close a cycle
    /// in a graph whose consumers assume a tree and walk it recursively.</summary>
    private static BusTopology FromEni(List<TopologyDevice> ordered,
        IReadOnlyDictionary<ushort, EniPreviousPort> declared)
    {
        var placed = new HashSet<ushort> { BusTopology.MasterAddress };
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var previous = BusTopology.MasterAddress;

        foreach (var device in ordered)
        {
            var edge = declared.GetValueOrDefault(device.Address);
            var usable = edge is not null && placed.Contains(edge.PhysAddr);
            nodes.Add(new TopologyNode(device.Address, device.RingPosition,
                usable ? edge!.PhysAddr : previous,
                usable ? edge!.Port : previous == BusTopology.MasterAddress ? (byte)0 : (byte)1,
                OwnPort: 0, device.Ports, device.Counters,
                usable || edge is null && previous == BusTopology.MasterAddress
                    ? TopologyEdgeSource.Eni
                    : TopologyEdgeSource.Inferred));
            placed.Add(device.Address);
            previous = device.Address;
        }

        return new BusTopology(nodes, [], [], PortDataObserved: false);
    }

    /// <summary>The stack walk. Each device is placed exactly once, in ring order, so the result
    /// cannot contain a cycle however contradictory the port data is.</summary>
    private static BusTopology FromPorts(List<TopologyDevice> ordered)
    {
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var unplaced = new List<ushort>();

        // The master contributes one downstream cable, modelled as its port 0.
        var stack = new List<(ushort Address, Queue<byte> Remaining)>
        {
            (BusTopology.MasterAddress, new Queue<byte>([(byte)0])),
        };

        foreach (var device in ordered)
        {
            while (stack.Count > 0 && stack[^1].Remaining.Count == 0)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
            {
                // More line ends than branches opened: the port states disagree with each other.
                unplaced.Add(device.Address);
                continue;
            }

            var (parentAddress, remaining) = stack[^1];
            var parentPort = remaining.Dequeue();
            nodes.Add(new TopologyNode(device.Address, device.RingPosition, parentAddress,
                parentPort, OwnPort: 0, device.Ports, device.Counters,
                device.HasPortData ? TopologyEdgeSource.Wire : TopologyEdgeSource.Inferred));
            stack.Add((device.Address, new Queue<byte>(device.ActiveDownstreamPorts)));
        }

        return new BusTopology(nodes, unplaced, [], PortDataObserved: true);
    }

    /// <summary>No device produced port state. Ring order is still real, so the devices are
    /// chained as one line and every edge is labelled inferred. Callers must not draw port bars
    /// for this topology — see <see cref="BusTopology.PortDataObserved"/>.</summary>
    private static BusTopology RingOrderOnly(List<TopologyDevice> ordered)
    {
        var nodes = new List<TopologyNode> { BusTopology.MasterNode };
        var parent = BusTopology.MasterAddress;
        foreach (var device in ordered)
        {
            nodes.Add(new TopologyNode(device.Address, device.RingPosition, parent,
                ParentPort: parent == BusTopology.MasterAddress ? (byte)0 : (byte)1, OwnPort: 0,
                device.Ports, device.Counters, TopologyEdgeSource.Inferred));
            parent = device.Address;
        }
        return new BusTopology(nodes, [], [], PortDataObserved: false);
    }
}
