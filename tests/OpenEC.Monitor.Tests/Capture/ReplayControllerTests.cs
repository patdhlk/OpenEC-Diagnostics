using OpenEC.Monitor.Capture;

namespace OpenEC.Monitor.Tests.Capture;

public class ReplayControllerTests
{
    [Fact]
    public void Default_ctor_starts_paused()
    {
        var controller = new ReplayController();
        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Explicit_start_unpaused()
    {
        var controller = new ReplayController(startPaused: false);
        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Speed_setter_clamps_to_valid_range()
    {
        var controller = new ReplayController();

        controller.Speed = 100;
        Assert.Equal(ReplayController.MaxSpeed, controller.Speed);

        controller.Speed = 0.001;
        Assert.Equal(ReplayController.MinSpeed, controller.Speed);

        controller.Speed = 2.5;
        Assert.Equal(2.5, controller.Speed);

        controller.Speed = double.NaN;
        Assert.Equal(1.0, controller.Speed);
    }

    [Fact]
    public void Ctor_speed_is_clamped()
    {
        var controller = new ReplayController(speed: 99);
        Assert.Equal(ReplayController.MaxSpeed, controller.Speed);
    }

    [Fact]
    public void Pause_and_resume_toggle_and_are_idempotent()
    {
        var controller = new ReplayController(startPaused: false);

        controller.Resume();
        Assert.False(controller.IsPaused);

        controller.Pause();
        Assert.True(controller.IsPaused);

        controller.Pause();
        Assert.True(controller.IsPaused);

        controller.Resume();
        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Step_while_running_does_not_pause()
    {
        var c = new ReplayController(startPaused: false);
        c.Step();
        Assert.False(c.IsPaused);
    }

    [Fact]
    public void Step_while_paused_keeps_it_paused()
    {
        var c = new ReplayController(startPaused: true);
        c.Step();
        Assert.True(c.IsPaused);
    }
}
