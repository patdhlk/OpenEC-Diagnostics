using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class RebindTests
{
    private static EniConfiguration Config(string variableName, int expectedWkc) => new()
    {
        Slaves = [],
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 1, expectedWkc, 0, null)],
        Variables = [new EniVariable(variableName, "USINT", 8, 0, true)],
    };

    private static EtherCatDatagram Logical(ushort wkc) =>
        new(EtherCatCommand.Lrd, 0, 0x00010000, false, false, 0, new byte[] { 0x42 }, wkc);

    [Fact]
    public void Process_image_decodes_nothing_until_it_is_rebound()
    {
        var image = new ProcessImage(null);

        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Empty(image.Current);

        image.Rebind(Config("Slave 1001.Input", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);

        Assert.True(image.Current.TryGetValue("Slave 1001.Input", out var value));
        Assert.Equal(0x42, (byte)value.Value);
    }

    /// <summary>A rebind that only RENAMES a variable must keep its value. This is the whole of what
    /// an ESI resolution does to a learned configuration — a synthetic `0x6000:01` becomes
    /// `Channel 1.Input` at the same offset — and it is forced once more after the capture loop ends,
    /// when no further frames can repopulate anything. Clearing here meant a live session with an
    /// `--esi-dir` finished with an empty process image while the same session without one finished
    /// with sixteen values. Placement is what the wire determines; the name is what a rebind changes,
    /// so placement is what the value follows.</summary>
    [Fact]
    public void Rebinding_carries_a_value_onto_a_renamed_variable_at_the_same_placement()
    {
        var image = new ProcessImage(Config("Slave 1001.0x6000:01", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Single(image.Current);

        image.Rebind(Config("EL1008.Channel 1.Input", 1));

        var carried = Assert.Single(image.Current);
        Assert.Equal("EL1008.Channel 1.Input", carried.Key);
        Assert.Equal(0x42, (byte)carried.Value.Value);
        // The variable itself is the new map's, so a later refresh writes to the same key.
        Assert.Equal("EL1008.Channel 1.Input", carried.Value.Variable.Name);
        // The timestamp is the one the value was decoded at: the rebind observed nothing.
        Assert.Equal(DateTimeOffset.UnixEpoch, carried.Value.Timestamp);
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Single(image.Current);
    }

    /// <summary>The other half of the same rule, and the reason keys cannot simply be kept: a value
    /// whose placement the new map does not contain has no variable left to refresh it, so it must go
    /// rather than linger in the watch as a phantom the map can never touch again.</summary>
    [Fact]
    public void Rebinding_drops_a_value_whose_placement_the_new_map_does_not_contain()
    {
        var image = new ProcessImage(Config("Slave 1001.0x6000:01", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);
        Assert.Single(image.Current);

        // Same name, different placement: the variable moved, so the old value is not about it.
        image.Rebind(new EniConfiguration
        {
            Slaves = [],
            CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 1, 1, 0, null)],
            Variables = [new EniVariable("Slave 1001.0x6000:01", "USINT", 8, 64, true)],
        });

        Assert.Empty(image.Current);
    }

    /// <summary>Rebinding to nothing — learning switched off, or a configuration withdrawn — leaves no
    /// map to refresh anything, so nothing may survive.</summary>
    [Fact]
    public void Rebinding_to_no_configuration_drops_everything()
    {
        var image = new ProcessImage(Config("Slave 1001.0x6000:01", 1));
        image.UpdateInputs(Logical(1), DateTimeOffset.UnixEpoch);

        image.Rebind(null);

        Assert.Empty(image.Current);
    }

    [Fact]
    public void Wkc_tracker_rebind_replaces_the_expected_value()
    {
        var tracker = new WkcTracker(Config("v", expectedWkc: 3));
        Assert.NotNull(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));

        tracker.Rebind(Config("v", expectedWkc: 2));

        Assert.Null(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));
    }

    /// <summary>The observed-WKC histogram is evidence from the wire, not a derivation of the
    /// configuration, so a rebind must keep it — otherwise every rebind restarts the 20-frame
    /// learning threshold and no-ENI mismatch detection never converges on a live bus.</summary>
    [Fact]
    public void Wkc_tracker_rebind_keeps_the_observed_histogram()
    {
        var tracker = new WkcTracker();
        for (var i = 0; i < 25; i++)
            tracker.Observe(DateTimeOffset.UnixEpoch, Logical(3), FrameDirection.Returning);

        tracker.Rebind(null);

        Assert.NotNull(tracker.Observe(DateTimeOffset.UnixEpoch, Logical(2), FrameDirection.Returning));
    }
}
