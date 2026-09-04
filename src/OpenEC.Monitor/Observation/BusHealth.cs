namespace OpenEC.Monitor.Observation;

public enum DcSyncState
{
    Unknown,
    Synced,
    OutOfSync
}

public enum HealthLevel
{
    Ok,
    Warning,
    Fault
}

public record BusHealth(
    SlaveAlState BusState,
    bool BusStateUniform,
    int FoundDevices,
    int? ConfiguredDevices,
    DcSyncState DcSync,
    int? MaxDcDeviationNs,
    // Slaves in OP whose process-data inputs have not changed for longer than the
    // configured threshold. Optional and defaulted so every existing construction still compiles;
    // an empty list and a null both mean "nothing reported".
    IReadOnlyList<ushort>? StaleSlaves = null)
{
    public IReadOnlyList<ushort> Stale => StaleSlaves ?? [];

    public bool DevicesMatch =>
        ConfiguredDevices is null || FoundDevices == ConfiguredDevices.Value;

    public HealthLevel Level
    {
        get
        {
            if (ConfiguredDevices is not null && FoundDevices != ConfiguredDevices.Value)
                return HealthLevel.Fault;
            if (DcSync == DcSyncState.OutOfSync)
                return HealthLevel.Fault;
            if (!BusStateUniform)
                return HealthLevel.Warning;
            // Warning, not Fault: a device can legitimately hold an input steady, so this reports
            // something worth looking at rather than asserting a defect.
            if (Stale.Count > 0)
                return HealthLevel.Warning;
            return HealthLevel.Ok;
        }
    }
}
