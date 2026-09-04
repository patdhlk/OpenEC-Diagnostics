using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class AdsIdentityTests
{
    /// <summary>A bus discovered mid-run: station addresses are visible from FPRD traffic, but the
    /// master never read SII and never queried 0x1018, so identity is unknown.</summary>
    private static BusLearner LearnerWithAnonymousSlave()
    {
        var learner = new BusLearner();
        // A real pair, as BringupCapture emits: the outbound read carries no data and WKC 0, the
        // returning half carries the answer and WKC 1. This also sets both MAC-bit values, so the
        // tracker uses its primary heuristic rather than the pairing fallback — these tests are
        // about the ADS tier and should not depend on how ambiguous frames get disambiguated.
        var request = new EtherCatFrameBuilder()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, new byte[2], 0).Build();
        var answer = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, [0x08, 0x00], 1).Build();
        learner.Observe(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(request));
        learner.Observe(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(answer));
        return learner;
    }

    [Fact]
    public void Ads_identity_fills_a_slave_the_wire_never_identified()
    {
        var learner = LearnerWithAnonymousSlave();
        Assert.Equal(0u, learner.Current!.Configuration.Slaves.Single().VendorId);

        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

        var slave = learner.Current!.Configuration.Slaves.Single();
        Assert.Equal(2u, slave.VendorId);
        Assert.Equal(0x03F03052u, slave.ProductCode);
    }

    [Fact]
    public void Ads_identity_is_marked_in_provenance()
    {
        var learner = LearnerWithAnonymousSlave();

        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

        Assert.Equal(FactSource.Ads, learner.Current!.Provenance[1001].Identity);
    }

    /// <summary>The wire is the authority. ADS reports what the master BELIEVES; if the bus itself
    /// said something different, that difference is exactly what a diagnostic tool must preserve.</summary>
    [Fact]
    public void Ads_identity_does_not_override_identity_learned_from_the_wire()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 3))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        Assert.Equal(0x03F03052u, learner.Current!.Configuration.Slaves[0].ProductCode);

        learner.ApplyAdsIdentity([(1001, 999u, 0xDEADBEEFu, 1u)]);

        var slave = learner.Current!.Configuration.Slaves.Single(s => s.PhysAddr == 1001);
        Assert.Equal(0x03F03052u, slave.ProductCode);
        Assert.Equal(FactSource.Sii, learner.Current!.Provenance[1001].Identity);
    }

    [Fact]
    public void An_ads_poll_for_an_unknown_address_is_ignored()
    {
        var learner = LearnerWithAnonymousSlave();

        learner.ApplyAdsIdentity([(1099, 2u, 0x03F03052u, 0x00120000u)]);

        Assert.Single(learner.Current!.Configuration.Slaves);
        Assert.Equal(0u, learner.Current!.Configuration.Slaves.Single().VendorId);
    }

    /// <summary>The learner is private and had no accessor, so the ADS tier was reachable only from
    /// tests: no production caller, and no API an SDK consumer could reach either — while the README
    /// advertised the tier. This exercises the facade pass-through `LiveCommand`'s poll loop calls,
    /// which is the only thing that makes the claim true.</summary>
    [Fact]
    public async Task The_monitor_facade_folds_an_ads_poll_into_the_learned_configuration()
    {
        var path = WriteAnonymousSlavePcap();
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path);
            await monitor.RunAsync();
            Assert.Equal(0u, monitor.Learned!.Configuration.Slaves.Single().VendorId);

            monitor.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

            Assert.Equal(2u, monitor.Learned!.Configuration.Slaves.Single().VendorId);
            Assert.Equal(FactSource.Ads, monitor.Learned!.Provenance[1001].Identity);
        }
        finally { File.Delete(path); }
    }

    /// <summary>`live --ads` polls unconditionally, including under `--no-learn`-shaped options where
    /// there is no learner at all. The pass-through must absorb that rather than throw.</summary>
    [Fact]
    public async Task The_monitor_facade_ignores_an_ads_poll_when_learning_is_off()
    {
        var path = WriteAnonymousSlavePcap();
        try
        {
            await using var monitor = EtherCatMonitor.OpenFile(path,
                new EtherCatMonitorOptions { Learning = LearningMode.Off });
            await monitor.RunAsync();

            monitor.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);

            Assert.Null(monitor.Learned);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The same two frames <see cref="LearnerWithAnonymousSlave"/> feeds, on disk, so the
    /// facade tests above drive a real capture source rather than a hand-built learner.</summary>
    private static string WriteAnonymousSlavePcap()
    {
        var request = new EtherCatFrameBuilder()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, new byte[2], 0).Build();
        var answer = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1001, 0x0130, [0x08, 0x00], 1).Build();
        var path = Path.Combine(Path.GetTempPath(), $"openec-ads-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path,
        [
            (DateTimeOffset.UnixEpoch, request),
            (DateTimeOffset.UnixEpoch.AddMicroseconds(60), answer),
        ]);
        return path;
    }

    /// <summary>ADS is polled once a second. A poll that tells the learner nothing it did not already
    /// know must not bump the revision, or every subscriber re-runs every second for nothing.</summary>
    [Fact]
    public void An_ads_poll_that_changes_nothing_does_not_publish_a_revision()
    {
        var learner = LearnerWithAnonymousSlave();
        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);
        var revision = learner.Current!.Revision;

        learner.ApplyAdsIdentity([(1001, 2u, 0x03F03052u, 0x00120000u)]);
        learner.ApplyAdsIdentity([(1099, 2u, 0x03F03052u, 0x00120000u)]);

        Assert.Equal(revision, learner.Current!.Revision);
    }
}
