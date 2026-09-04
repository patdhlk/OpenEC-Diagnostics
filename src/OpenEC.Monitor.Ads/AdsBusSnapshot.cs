using Dahlke.EtherCAT.Diagnostics;
using OpenEC.Monitor.Learning;

namespace OpenEC.Monitor.Ads;

/// <summary>One poll of master-side diagnostics — data a passive TAP cannot see.</summary>
public sealed record AdsBusSnapshot(
    EtherCatMasterState MasterState,
    IReadOnlyList<EtherCatSlaveInfo> ConfiguredSlaves,
    IReadOnlyList<EtherCatScannedSlave> ScannedSlaves,
    FrameStatistics? FrameStatistics,
    IReadOnlyDictionary<ushort, SlaveErrorCounters> ErrorCounters)
{
    /// <summary>The scanned identities in the shape <c>BusLearner.ApplyAdsIdentity</c> takes.
    /// Mapping here rather than in the learner keeps the dependency direction intact:
    /// OpenEC.Monitor.Ads knows about OpenEC.Monitor, never the reverse.
    ///
    /// Slaves whose per-slave identity read (IG 0x11) did not answer are DROPPED, never zeroed.
    /// The package reports those four fields as absent rather than zeroed precisely so a caller
    /// cannot confuse "did not answer" with "vendor 0"; passing a zero through would have the
    /// learner stamp <see cref="FactSource.Ads"/> on an identity nobody ever read, which is the one
    /// thing provenance exists to prevent.</summary>
    public IReadOnlyList<(ushort Address, uint VendorId, uint ProductCode, uint Revision)>
        ScannedIdentities() =>
        ScannedSlaves
            .Where(s => s.VendorId is not null && s.ProductCode is not null
                && s.RevisionNumber is not null)
            .Select(s => (Address: s.PhysicalAddress, VendorId: s.VendorId!.Value,
                ProductCode: s.ProductCode!.Value, Revision: s.RevisionNumber!.Value))
            .ToList();
}
