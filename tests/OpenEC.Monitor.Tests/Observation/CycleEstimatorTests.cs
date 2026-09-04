// tests/OpenEC.Monitor.Tests/Observation/CycleEstimatorTests.cs
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class CycleEstimatorTests
{
    private static EtherCatDatagram Lrw() => new(
        EtherCatCommand.Lrw, 1, 0x01000000, false, false, 0, new byte[4], 0);

    [Fact]
    public void Estimates_cycle_time_from_outbound_lrw_cadence()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 20; i++)
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Outbound);

        Assert.NotNull(estimator.EstimatedCycleTime);
        Assert.Equal(1.0, estimator.EstimatedCycleTime!.Value.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Returning_frames_and_physical_commands_are_ignored()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        var brd = new EtherCatDatagram(EtherCatCommand.Brd, 1, 0x01300000, false, false, 0, new byte[2], 0);
        for (var i = 0; i < 20; i++)
        {
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Returning);
            estimator.Observe(t0.AddMilliseconds(i), brd, FrameDirection.Outbound);
        }
        Assert.Null(estimator.EstimatedCycleTime);
    }

    [Fact]
    public void Too_few_samples_yield_null()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++)
            estimator.Observe(t0.AddMilliseconds(i), Lrw(), FrameDirection.Outbound);
        Assert.Null(estimator.EstimatedCycleTime);
    }

    [Fact]
    public void Median_is_robust_against_one_outlier()
    {
        var estimator = new CycleEstimator();
        var t0 = DateTimeOffset.UnixEpoch;
        var t = t0;
        for (var i = 0; i < 20; i++)
        {
            t = t.AddMilliseconds(i == 10 ? 50 : 1); // one late frame
            estimator.Observe(t, Lrw(), FrameDirection.Outbound);
        }
        Assert.Equal(1.0, estimator.EstimatedCycleTime!.Value.TotalMilliseconds, precision: 3);
    }
}
