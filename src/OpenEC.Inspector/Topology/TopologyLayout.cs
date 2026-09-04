using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Topology;

public enum TopologyBoxKind
{
    Master,

    /// <summary>An ordinary in-line device: one upstream port, one downstream.</summary>
    Device,

    /// <summary>More than one active downstream port — a branch opens here.</summary>
    Junction,

    /// <summary>No active downstream port: the end of a line.</summary>
    LineEnd,
}

public enum PortSide { Left, Right, Bottom }

public readonly record struct TopologyPoint(double X, double Y);

/// <param name="HasError">True when a known counter on this port is non-zero. An unread counter
/// leaves this false without implying health — <see cref="PortCounters.AnyKnown"/> is what
/// separates "clean" from "unknown".</param>
public sealed record TopologyPortMark(byte Port, PortSide Side, PortLinkState State, bool HasError,
    double X, double Y, double Width, double Height);

/// <param name="EdgeInferred">True when this device's parent is a ring-order guess rather than an
/// observed or declared edge.</param>
/// <param name="HasConflict">True when the ENI declared a different parent or port for this device
/// than the wire showed. Spec §7: the wire's version is drawn, and the disagreement is marked.</param>
public sealed record TopologyBox(ushort Address, int Row, double X, double Y,
    double Width, double Height, TopologyBoxKind Kind, bool IsWide, bool EdgeInferred,
    bool HasConflict, IReadOnlyList<TopologyPortMark> Ports);

public sealed record TopologyWire(ushort FromAddress, ushort ToAddress, bool IsInferred,
    bool HasConflict, IReadOnlyList<TopologyPoint> Points);

public sealed record TopologyLayout(
    IReadOnlyList<TopologyBox> Boxes,
    IReadOnlyList<TopologyWire> Wires,
    IReadOnlyList<ushort> Unplaced,
    bool PortDataObserved,
    double Width,
    double Height)
{
    public static readonly TopologyLayout Empty =
        new([], [], [], PortDataObserved: false, Width: 0, Height: 0);
}
