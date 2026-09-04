using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

/// <summary>Render color of a status dot; the view maps these onto the palette
/// (Idle→Ink3, Ok→Ok, Oos→Oos, Fail→Fail). One mapping serves the explorer tree,
/// the device editor's state badge, and the status bar (spec-§4 "AL badge colors").</summary>
public enum StatusDot { Idle, Ok, Oos, Fail }

public static class StatusDotMap
{
    public static StatusDot ForSlave(SlaveStatus status) => status switch
    {
        { ErrorFlag: true } => StatusDot.Fail,
        { AlState: SlaveAlState.Op } => StatusDot.Ok,
        { AlState: SlaveAlState.SafeOp or SlaveAlState.PreOp } => StatusDot.Oos,
        _ => StatusDot.Idle,
    };

    public static StatusDot ForSession(SessionState state) => state switch
    {
        SessionState.Running => StatusDot.Ok,
        SessionState.Faulted => StatusDot.Fail,
        _ => StatusDot.Idle,
    };

    public static StatusDot ForHealth(HealthLevel level) => level switch
    {
        HealthLevel.Ok => StatusDot.Ok,
        HealthLevel.Warning => StatusDot.Oos,
        HealthLevel.Fault => StatusDot.Fail,
        _ => StatusDot.Idle,
    };
}
