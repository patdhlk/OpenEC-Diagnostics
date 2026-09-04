using Dahlke.EtherCAT.Diagnostics;

namespace OpenEC.Monitor.Ads;

public sealed class AdsEnrichment(IEtherCatClient client)
{
    /// <summary>Polls the master once. Null when the master is unreachable; individual
    /// slave-counter failures are skipped so one bad slave cannot break the poll.</summary>
    public async Task<AdsBusSnapshot?> PollAsync(string masterNetId, CancellationToken ct)
    {
        var state = await client.GetMasterStateAsync(masterNetId, ct);
        if (state is null) return null;
        var configured = await client.GetConfiguredSlavesAsync(masterNetId, ct)
            ?? (IReadOnlyList<EtherCatSlaveInfo>)Array.Empty<EtherCatSlaveInfo>();
        var scanned = await client.GetScannedSlavesAsync(masterNetId, ct)
            ?? (IReadOnlyList<EtherCatScannedSlave>)Array.Empty<EtherCatScannedSlave>();
        var frames = await client.GetFrameStatisticsAsync(masterNetId, ct);
        var counters = new Dictionary<ushort, SlaveErrorCounters>();
        foreach (var slave in configured)
        {
            var c = await client.GetSlaveErrorCountersAsync(masterNetId, slave.PhysicalAddress, ct);
            if (c is not null) counters[slave.PhysicalAddress] = c;
        }
        return new AdsBusSnapshot(state, configured, scanned, frames, counters);
    }
}
