using Dahlke.EtherCAT.Diagnostics;
using OpenEC.Monitor.Ads;

namespace OpenEC.Monitor.Tests.Ads;

public class AdsEnrichmentTests
{
    private sealed class StubClient : IEtherCatClient
    {
        public EtherCatMasterState? MasterState { get; init; }
        public IReadOnlyList<EtherCatSlaveInfo>? Configured { get; init; }
        public IReadOnlyList<EtherCatScannedSlave>? Scanned { get; init; }
        public FrameStatistics? Frames { get; init; }
        public Func<ushort, SlaveErrorCounters?>? Counters { get; init; }

        public Task<EtherCatMasterState?> GetMasterStateAsync(string masterAmsNetId, CancellationToken ct) =>
            Task.FromResult(MasterState);
        public Task<IReadOnlyList<EtherCatSlaveInfo>?> GetConfiguredSlavesAsync(string masterAmsNetId, CancellationToken ct) =>
            Task.FromResult(Configured);
        public Task<IReadOnlyList<EtherCatScannedSlave>?> GetScannedSlavesAsync(string masterAmsNetId, CancellationToken ct) =>
            Task.FromResult(Scanned);
        public Task<FrameStatistics?> GetFrameStatisticsAsync(string masterAmsNetId, CancellationToken ct) =>
            Task.FromResult(Frames);
        public Task<SlaveErrorCounters?> GetSlaveErrorCountersAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct) =>
            Task.FromResult(Counters?.Invoke(physicalAddress));

        // Remaining IEtherCatClient members are not exercised by AdsEnrichment.
        public Task<IReadOnlyList<EtherCatMasterInfo>> GetMastersAsync(string amsNetId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EtherCatMasterInfo>>(Array.Empty<EtherCatMasterInfo>());
        public Task<EtherCatSlaveDetail?> GetSlaveDetailAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> ResetSlaveErrorCountersAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SyncUnitInfo>> GetSyncUnitsAsync(string masterAmsNetId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SyncUnitInfo>>(Array.Empty<SyncUnitInfo>());
        public Task<CoeReadResult> ReadCoeObjectAsync(string masterAmsNetId, ushort physicalAddress, ushort index, byte subIndex, int timeoutMs, int maxBytes, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<CoeWriteResult> WriteCoeObjectAsync(string masterAmsNetId, ushort physicalAddress, ushort index, byte subIndex, ReadOnlyMemory<byte> data, int timeoutMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Cia402StatusResult> ReadCia402StatusAsync(string masterAmsNetId, ushort physicalAddress, int timeoutMs, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Builds a snapshot with one scanned slave whose identity read answered and one
    /// whose per-slave identity read (IG 0x11) did not — the four identity fields on
    /// <see cref="EtherCatScannedSlave"/> are "null together, never individually" for the
    /// unanswered case. Other snapshot members are not what <see cref="AdsBusSnapshot.ScannedIdentities"/>
    /// is about, so they take whatever empty/default values compile.</summary>
    private static AdsBusSnapshot SnapshotWith(ushort answered, ushort unanswered) =>
        new(
            new EtherCatMasterState { CurrentState = "OP", RequestedState = "OP", SlaveCount = 2 },
            new List<EtherCatSlaveInfo>(),
            new List<EtherCatScannedSlave>
            {
                new()
                {
                    PhysicalAddress = answered, VendorId = 2u, ProductCode = 0x03F03052u,
                    RevisionNumber = 0x00120000u, SerialNumber = 0u,
                },
                new()
                {
                    PhysicalAddress = unanswered, VendorId = null, ProductCode = null,
                    RevisionNumber = null, SerialNumber = null,
                },
            },
            null,
            new Dictionary<ushort, SlaveErrorCounters>());

    /// <summary>A scanned slave whose identity read did not answer must not reach the learner at all.
    /// Zeroing it instead would have the learner report vendor 0 as an ADS-sourced fact.</summary>
    [Fact]
    public void Scanned_identities_drop_slaves_whose_identity_read_did_not_answer()
    {
        var snapshot = SnapshotWith(answered: /* station */ 1001, unanswered: 1002);

        var identities = snapshot.ScannedIdentities();

        Assert.Equal((ushort)1001, Assert.Single(identities).Address);
    }

    [Fact]
    public async Task Unreachable_master_yields_null()
    {
        var enrichment = new AdsEnrichment(new StubClient { MasterState = null });
        Assert.Null(await enrichment.PollAsync("10.0.0.1.1.1", CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_collects_slaves_and_counters()
    {
        var stub = new StubClient
        {
            MasterState = new EtherCatMasterState { CurrentState = "OP", RequestedState = "OP", SlaveCount = 2 },
            Configured = new List<EtherCatSlaveInfo>
            {
                new()
                {
                    PhysicalAddress = 1001, AutoIncrementAddress = 0, Name = "EK1100", Type = "EK1100",
                    CurrentState = "OP", RequestedState = "OP", IsPresent = true, HasError = false, IsDisabled = false,
                },
                new()
                {
                    PhysicalAddress = 1002, AutoIncrementAddress = 1, Name = "EL1008", Type = "EL1008",
                    CurrentState = "OP", RequestedState = "OP", IsPresent = true, HasError = false, IsDisabled = false,
                },
            },
            Scanned = new List<EtherCatScannedSlave>(),
            Counters = a => a == 1002
                ? new SlaveErrorCounters { PhysicalAddress = 1002, Ports = Array.Empty<PortErrorCounters>(), AbnormalStateChanges = 0 }
                : null,
        };
        var enrichment = new AdsEnrichment(stub);

        var snapshot = await enrichment.PollAsync("10.0.0.1.1.1", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.ConfiguredSlaves.Count);
        Assert.Single(snapshot.ErrorCounters);
        Assert.True(snapshot.ErrorCounters.ContainsKey(1002));
    }
}
