using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class EventFormatterTests
{
    private static MonitorEvent.ConfigMismatch Mismatch(ConfigMismatchKind kind) =>
        new(DateTimeOffset.UnixEpoch, kind, 1001, "Term 1 (EL1008)", "Term 1 (EL2008)");

    [Fact]
    public void Config_mismatches_get_their_own_category()
    {
        Assert.Equal("Config", EventFormatter.Category(Mismatch(ConfigMismatchKind.Identity)));
    }

    [Fact]
    public void An_identity_mismatch_names_both_sides_and_the_slave()
    {
        var text = EventFormatter.Describe(Mismatch(ConfigMismatchKind.Identity));

        Assert.Contains("1001", text);
        Assert.Contains("Term 1 (EL1008)", text);
        Assert.Contains("Term 1 (EL2008)", text);
    }

    /// <summary>Process-image mismatches carry no address, so the description must not claim one.</summary>
    [Fact]
    public void A_process_image_mismatch_without_an_address_reads_cleanly()
    {
        var mismatch = new MonitorEvent.ConfigMismatch(DateTimeOffset.UnixEpoch,
            ConfigMismatchKind.ProcessImage, null, "x @bit 0", "@bit 8");

        var text = EventFormatter.Describe(mismatch);

        Assert.DoesNotContain("Slave ,", text);
        Assert.DoesNotContain("Slave :", text);
        Assert.Contains("x @bit 0", text);
    }

    [Fact]
    public void A_learned_configuration_reports_its_revision_and_summary()
    {
        var learned = new MonitorEvent.ConfigurationLearned(
            DateTimeOffset.UnixEpoch, 7, "learned 2/2 slaves");

        Assert.Equal("Learning", EventFormatter.Category(learned));
        var text = EventFormatter.Describe(learned);
        Assert.Contains("7", text);
        Assert.Contains("learned 2/2 slaves", text);
    }

    [Fact]
    public void A_bus_health_change_reports_level_devices_and_dc()
    {
        var health = new MonitorEvent.BusHealthChanged(DateTimeOffset.UnixEpoch,
            new BusHealth(SlaveAlState.Op, BusStateUniform: true, FoundDevices: 1, ConfiguredDevices: 2,
                DcSync: DcSyncState.OutOfSync, MaxDcDeviationNs: 40_000));

        Assert.Equal("Health", EventFormatter.Category(health));
        var text = EventFormatter.Describe(health);
        Assert.Contains("fault", text);        // 1 ≠ 2 configured and DC out of sync
        Assert.Contains("1/2 devices", text);
        Assert.Contains("DC out of sync", text);
    }
}
