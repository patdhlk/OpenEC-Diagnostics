using System.Buffers.Binary;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Observation;

public class HealthTrackerTests
{
    private static EtherCatDatagram Physical(EtherCatCommand cmd, ushort adp, ushort ado,
        byte[] payload, ushort wkc) =>
        new(cmd, 1, ((uint)ado << 16) | adp, false, false, 0, payload, wkc);

    private static byte[] EncodeDcDiff(int signedNs)
    {
        var magnitude = Math.Abs(signedNs);
        var signBit = signedNs < 0 ? unchecked((int)0x8000_0000) : 0;
        var raw = magnitude | signBit;
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, raw);
        return bytes;
    }

    [Fact]
    public void Dc_never_polled_reports_unknown()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);

        var health = tracker.Compute();

        Assert.Equal(DcSyncState.Unknown, health.DcSync);
        Assert.Null(health.MaxDcDeviationNs);
    }

    [Fact]
    public void Dc_within_tolerance_reports_synced()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 1),
            FrameDirection.Returning).ToList();

        Assert.Single(events);
        var evt = Assert.IsType<MonitorEvent.BusHealthChanged>(events[0]);
        Assert.Equal(DcSyncState.Synced, evt.Health.DcSync);
        Assert.Equal(5000, evt.Health.MaxDcDeviationNs);
    }

    [Fact]
    public void Dc_exceeding_tolerance_reports_out_of_sync()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        // First slave within tolerance
        tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 1),
            FrameDirection.Returning).ToList();

        // Second slave exceeds tolerance (15 µs > 10 µs)
        var events = tracker.Observe(t.AddMilliseconds(1),
            Physical(EtherCatCommand.Fprd, 1002, 0x092C, EncodeDcDiff(15_000), 1),
            FrameDirection.Returning).ToList();

        Assert.Single(events);
        var evt = Assert.IsType<MonitorEvent.BusHealthChanged>(events[0]);
        Assert.Equal(DcSyncState.OutOfSync, evt.Health.DcSync);
        Assert.Equal(HealthLevel.Fault, evt.Health.Level);
        Assert.Equal(15_000, evt.Health.MaxDcDeviationNs);
    }

    [Fact]
    public void Dc_negative_value_is_decoded_correctly()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        // Negative 8 µs (local behind reference)
        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(-8000), 1),
            FrameDirection.Returning).ToList();

        Assert.Single(events);
        Assert.True(model.TryGet(1001, out var slave));
        Assert.Equal(-8000, slave!.DcSystemTimeDiffNs);

        var evt = Assert.IsType<MonitorEvent.BusHealthChanged>(events[0]);
        Assert.Equal(DcSyncState.Synced, evt.Health.DcSync);
        Assert.Equal(8000, evt.Health.MaxDcDeviationNs); // magnitude
    }

    [Fact]
    public void All_devices_found_reports_match()
    {
        var eni = new EniConfiguration
        {
            Slaves = new[]
            {
                new EniSlave("Slave1", 1001, 0, 1, 1, 1, null, null, null),
                new EniSlave("Slave2", 1002, 1, 1, 1, 1, null, null, null)
            },
            CyclicCommands = Array.Empty<EniCyclicCommand>(),
            Variables = Array.Empty<EniVariable>()
        };
        var model = new BusModel();
        model.Seed(eni);
        model.BusStateUniform = true; // uniform state
        var tracker = new HealthTracker(model, eni);
        // Mark both slaves as seen
        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch;
        model.GetOrAdd(1002).LastSeen = DateTimeOffset.UnixEpoch;

        var health = tracker.Compute();

        Assert.Equal(2, health.FoundDevices);
        Assert.Equal(2, health.ConfiguredDevices);
        Assert.True(health.DevicesMatch);
        Assert.Equal(HealthLevel.Ok, health.Level);
    }

    [Fact]
    public void Missing_device_reports_fault()
    {
        var eni = new EniConfiguration
        {
            Slaves = new[]
            {
                new EniSlave("Slave1", 1001, 0, 1, 1, 1, null, null, null),
                new EniSlave("Slave2", 1002, 1, 1, 1, 1, null, null, null)
            },
            CyclicCommands = Array.Empty<EniCyclicCommand>(),
            Variables = Array.Empty<EniVariable>()
        };
        var model = new BusModel();
        model.Seed(eni);
        var tracker = new HealthTracker(model, eni);

        // Only one slave seen
        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch;

        var health = tracker.Compute();

        Assert.Equal(1, health.FoundDevices);
        Assert.Equal(2, health.ConfiguredDevices);
        Assert.False(health.DevicesMatch);
        Assert.Equal(HealthLevel.Fault, health.Level);
    }

    [Fact]
    public void Unexpected_device_reports_fault()
    {
        var eni = new EniConfiguration
        {
            Slaves = new[]
            {
                new EniSlave("Slave1", 1001, 0, 1, 1, 1, null, null, null)
            },
            CyclicCommands = Array.Empty<EniCyclicCommand>(),
            Variables = Array.Empty<EniVariable>()
        };
        var model = new BusModel();
        model.Seed(eni);
        var tracker = new HealthTracker(model, eni);

        // Two slaves seen (one unexpected)
        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch;
        model.GetOrAdd(1002).LastSeen = DateTimeOffset.UnixEpoch;

        var health = tracker.Compute();

        Assert.Equal(2, health.FoundDevices);
        Assert.Equal(1, health.ConfiguredDevices);
        Assert.False(health.DevicesMatch);
        Assert.Equal(HealthLevel.Fault, health.Level);
    }

    [Fact]
    public void No_config_does_not_fault_on_device_count()
    {
        var model = new BusModel();
        model.BusStateUniform = true; // uniform state
        var tracker = new HealthTracker(model); // no ENI

        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch;
        model.GetOrAdd(1002).LastSeen = DateTimeOffset.UnixEpoch;

        var health = tracker.Compute();

        Assert.Equal(2, health.FoundDevices);
        Assert.Null(health.ConfiguredDevices);
        Assert.True(health.DevicesMatch); // null config means match
        Assert.Equal(HealthLevel.Ok, health.Level);
    }

    [Fact]
    public void Steady_state_emits_no_event()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        // First observation emits event
        var first = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 1),
            FrameDirection.Returning).ToList();
        Assert.Single(first);

        // Same value again emits nothing
        var second = tracker.Observe(t.AddMilliseconds(1),
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 1),
            FrameDirection.Returning).ToList();
        Assert.Empty(second);
    }

    [Fact]
    public void Non_uniform_bus_state_reports_warning()
    {
        var model = new BusModel();
        model.BusState = SlaveAlState.Op;
        model.BusStateUniform = false; // mixed states
        var tracker = new HealthTracker(model);

        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch;

        var health = tracker.Compute();

        Assert.Equal(HealthLevel.Warning, health.Level);
    }

    [Fact]
    public void Zero_wkc_is_ignored()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 0),
            FrameDirection.Returning).ToList();

        Assert.Empty(events);
        Assert.False(model.TryGet(1001, out _));
    }

    [Fact]
    public void Outbound_frames_are_ignored()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Fprd, 1001, 0x092C, EncodeDcDiff(5000), 1),
            FrameDirection.Outbound).ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void Logical_commands_are_ignored()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        var lrd = new EtherCatDatagram(EtherCatCommand.Lrd, 1, 0x00010000,
            false, false, 0, new byte[4], 1);
        var events = tracker.Observe(t, lrd, FrameDirection.Returning).ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void Al_status_read_triggers_recompute()
    {
        var model = new BusModel();
        var tracker = new HealthTracker(model);
        var t = DateTimeOffset.UnixEpoch;

        // Set up initial state
        model.BusState = SlaveAlState.PreOp;
        model.BusStateUniform = true;

        // First AL status read emits initial health
        var events = tracker.Observe(t,
            Physical(EtherCatCommand.Brd, 0, 0x0130, [0x02], 1),
            FrameDirection.Returning).ToList();

        Assert.Single(events);
        var evt = Assert.IsType<MonitorEvent.BusHealthChanged>(events[0]);
        Assert.Equal(SlaveAlState.PreOp, evt.Health.BusState);
    }

    [Fact]
    public void Level_prefers_fault_over_warning()
    {
        var eni = new EniConfiguration
        {
            Slaves = new[]
            {
                new EniSlave("Slave1", 1001, 0, 1, 1, 1, null, null, null),
                new EniSlave("Slave2", 1002, 1, 1, 1, 1, null, null, null)
            },
            CyclicCommands = Array.Empty<EniCyclicCommand>(),
            Variables = Array.Empty<EniVariable>()
        };
        var model = new BusModel();
        model.Seed(eni);
        model.BusStateUniform = false; // non-uniform → Warning in isolation
        var tracker = new HealthTracker(model, eni);
        model.GetOrAdd(1001).LastSeen = DateTimeOffset.UnixEpoch; // only one of two found → Fault

        var health = tracker.Compute();

        Assert.False(health.BusStateUniform);
        Assert.False(health.DevicesMatch);
        Assert.Equal(HealthLevel.Fault, health.Level); // Fault wins over Warning
    }
}
