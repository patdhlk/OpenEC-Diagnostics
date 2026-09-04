using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class StatusDotMapTests
{
    private static SlaveStatus Status(SlaveAlState state, bool error = false) =>
        new() { Address = 1001, AlState = state, ErrorFlag = error };

    [Theory]
    [InlineData(SlaveAlState.Op, StatusDot.Ok)]
    [InlineData(SlaveAlState.SafeOp, StatusDot.Oos)]
    [InlineData(SlaveAlState.PreOp, StatusDot.Oos)]
    [InlineData(SlaveAlState.Init, StatusDot.Idle)]
    [InlineData(SlaveAlState.Boot, StatusDot.Idle)]
    [InlineData(SlaveAlState.Unknown, StatusDot.Idle)]
    public void Al_states_map_to_dots(SlaveAlState state, StatusDot expected) =>
        Assert.Equal(expected, StatusDotMap.ForSlave(Status(state)));

    [Fact]
    public void The_error_flag_overrides_any_state() =>
        Assert.Equal(StatusDot.Fail, StatusDotMap.ForSlave(Status(SlaveAlState.Op, error: true)));

    [Theory]
    [InlineData(SessionState.Running, StatusDot.Ok)]
    [InlineData(SessionState.Faulted, StatusDot.Fail)]
    [InlineData(SessionState.Completed, StatusDot.Idle)]
    [InlineData(SessionState.Stopped, StatusDot.Idle)]
    [InlineData(SessionState.Idle, StatusDot.Idle)]
    public void Session_states_map_to_dots(SessionState state, StatusDot expected) =>
        Assert.Equal(expected, StatusDotMap.ForSession(state));

    [Theory]
    [InlineData(HealthLevel.Ok, StatusDot.Ok)]
    [InlineData(HealthLevel.Warning, StatusDot.Oos)]
    [InlineData(HealthLevel.Fault, StatusDot.Fail)]
    public void Health_levels_map_to_dots(HealthLevel level, StatusDot expected) =>
        Assert.Equal(expected, StatusDotMap.ForHealth(level));
}
