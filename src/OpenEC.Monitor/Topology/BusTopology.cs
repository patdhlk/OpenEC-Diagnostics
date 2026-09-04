using OpenEC.Monitor.Learning;

namespace OpenEC.Monitor.Topology;

/// <summary>Where a topology edge came from. Deliberately separate from
/// <see cref="Learning.FactSource"/>, whose members describe identity and mapping provenance and
/// would be a poor fit for an edge.</summary>
public enum TopologyEdgeSource
{
    /// <summary>Derived from DL-status port state observed on the wire.</summary>
    Wire,

    /// <summary>Declared by an ENI &lt;PreviousPort&gt; element.</summary>
    Eni,

    /// <summary>Neither source described this edge; it follows ring order alone.</summary>
    Inferred,
}

/// <summary>Reconstruction input: one device's ring position and port facts. A small record
/// rather than <see cref="Learning.LearnedSlave"/> so the reconstruction stays pure and can be
/// driven from hand-written fixtures.</summary>
public sealed record TopologyDevice(
    ushort Address,
    int RingPosition,
    IReadOnlyDictionary<byte, PortState> Ports,
    IReadOnlyDictionary<byte, PortCounters> Counters)
{
    /// <summary>The ports that can carry a downstream edge, in the ESC's forwarding order.</summary>
    public IReadOnlyList<byte> ActiveDownstreamPorts =>
        TopologyReconstructor.ForwardingOrder
            .Where(port => port != 0 && Ports.TryGetValue(port, out var state) && state.IsActive)
            .ToList();

    public bool HasPortData => Ports.Count > 0;

    /// <summary>Projects a learned slave onto the reconstruction's input. Copies the fact
    /// dictionaries rather than aliasing them: the learned slave stays live and mutable under the
    /// capture pump, and reconstruction must see a stable snapshot.</summary>
    public static TopologyDevice FromLearned(LearnedSlave slave) => new(
        slave.StationAddress,
        slave.RingPosition,
        new Dictionary<byte, PortState>(slave.Ports),
        new Dictionary<byte, PortCounters>(slave.Counters));
}

/// <summary>One placed device. <paramref name="OwnPort"/> is the port the frame enters on, which
/// is 0 for every ESC by definition; it is carried explicitly so the layout engine never has to
/// assume it.</summary>
public sealed record TopologyNode(
    ushort Address,
    int RingPosition,
    ushort? ParentAddress,
    byte? ParentPort,
    byte OwnPort,
    IReadOnlyDictionary<byte, PortState> Ports,
    IReadOnlyDictionary<byte, PortCounters> Counters,
    TopologyEdgeSource EdgeSource)
{
    public bool IsMaster => ParentAddress is null;
}

/// <summary>An edge the ENI and the wire describe differently. Reported, never silently
/// resolved: the wire's version is what gets drawn.</summary>
public sealed record TopologyConflict(ushort Address, string Declared, string Observed);

/// <param name="PortDataObserved">False when no device produced port state, meaning the tree is
/// ring order alone and no port bars may be drawn.</param>
public sealed record BusTopology(
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<ushort> Unplaced,
    IReadOnlyList<TopologyConflict> Conflicts,
    bool PortDataObserved)
{
    /// <summary>The master's stand-in address. Zero is not a valid configured station address, so
    /// it cannot collide with a real device.</summary>
    public const ushort MasterAddress = 0;

    internal static TopologyNode MasterNode { get; } = new(
        MasterAddress, RingPosition: -1, ParentAddress: null, ParentPort: null, OwnPort: 0,
        new Dictionary<byte, PortState>(), new Dictionary<byte, PortCounters>(),
        TopologyEdgeSource.Wire);

    public static readonly BusTopology Empty =
        new([MasterNode], [], [], PortDataObserved: false);

    public TopologyNode? Find(ushort address) => Nodes.FirstOrDefault(n => n.Address == address);

    public IEnumerable<TopologyNode> ChildrenOf(ushort address) =>
        Nodes.Where(n => n.ParentAddress == address);
}
