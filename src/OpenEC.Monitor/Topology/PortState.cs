namespace OpenEC.Monitor.Topology;

/// <summary>What one ESC port is doing, derived from its DL status bit triple. The two mixed
/// states are the diagnostically interesting ones: an ESC auto-closes a port with no link, so
/// a link disagreeing with its loop bit is a fault worth surfacing.</summary>
public enum PortLinkState
{
    /// <summary>No link and the loop is closed — nothing plugged in.</summary>
    Unused,

    /// <summary>Link up and the loop is open — frames pass.</summary>
    Active,

    /// <summary>Link up but the loop is closed — cable present, frames not passing.</summary>
    Blocked,

    /// <summary>Loop open with no link — frames leave into nothing.</summary>
    Dangling,
}

/// <summary>One port as DL status (0x0110) describes it. <paramref name="SignalDetected"/> is
/// recorded but does not affect <see cref="State"/>: it distinguishes a powered partner from an
/// unpowered one, which belongs in a tooltip rather than in the port's rendered state.</summary>
public sealed record PortState(byte Port, bool HasLink, bool LoopClosed, bool SignalDetected)
{
    public PortLinkState State => (HasLink, LoopClosed) switch
    {
        (true, false) => PortLinkState.Active,
        (true, true) => PortLinkState.Blocked,
        (false, false) => PortLinkState.Dangling,
        (false, true) => PortLinkState.Unused,
    };

    /// <summary>True when frames actually traverse this port, which is the only condition under
    /// which it can carry a topology edge.</summary>
    public bool IsActive => State == PortLinkState.Active;
}
