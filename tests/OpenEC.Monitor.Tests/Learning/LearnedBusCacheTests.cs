using OpenEC.Monitor;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class LearnedBusCacheTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}")).FullName;

    internal static LearnedConfiguration LearnBringup()
    {
        var learner = new BusLearner();
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner.Current!;
    }

    private static EniConfiguration WithSlaves(params EniSlave[] slaves) => new()
    {
        Slaves = slaves,
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 2, 2, 0, null)],
        Variables = [],
    };

    private static EniSlave Slave(ushort address, uint product) =>
        new($"S{address}", address, (ushort)(0 - (address - 1001)), 2, product, 0x00120000, null, null);

    /// <summary>A mid-run attach: cyclic traffic plus the AL-status polls a master actually emits in
    /// OP. The FPRD polls matter — LearnedBus's mid-run discovery needs an FPRD with a non-zero ADP,
    /// so a purely cyclic capture would discover no slaves at all and never publish anything for the
    /// cache to be consulted against. No station-address assignment, no SII, no CoE.</summary>
    internal static List<(DateTimeOffset, byte[])> MidRunFrames(bool withLateSlave = false)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var midRunFrames = new List<(DateTimeOffset, byte[])>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            midRunFrames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrd, (byte)cycle, 0x00010000, new byte[2], 0).Build()));
            midRunFrames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrd, (byte)cycle, 0x00010000,
                    [(byte)cycle, (byte)~cycle], 2).Build()));
            foreach (var station in new ushort[] { 1001, 1002 })
                midRunFrames.Add((t.AddMicroseconds(120), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, (byte)cycle, station, 0x0130,
                        [0x08, 0x00], 1).Build()));
            t = t.AddMilliseconds(1);
        }
        // A third station appearing only now forces one more revision AFTER the cache hit — the
        // exact shape the overwrite guard exists to intercept. Kept opt-in so the other mid-run
        // tests still describe a two-slave bus.
        if (withLateSlave)
            midRunFrames.Add((t.AddMicroseconds(120), new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Fprd, 0x20, 1003, 0x0130, [0x08, 0x00], 1).Build()));
        return midRunFrames;
    }

    [Fact]
    public void A_saved_configuration_loads_back()
    {
        var cache = new LearnedBusCache(_directory);
        var learned = LearnBringup();

        cache.Save(learned);
        var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);

        Assert.True(cache.TryLoad(fingerprint, out var reloaded));
        Assert.Equal(2, reloaded!.Slaves.Count);
        Assert.Equal(16, reloaded.Variables.Count);
    }

    [Fact]
    public void A_miss_reports_false_and_yields_null()
    {
        var cache = new LearnedBusCache(_directory);

        Assert.False(cache.TryLoad("deadbeef", out var reloaded));
        Assert.Null(reloaded);
    }

    [Fact]
    public void Saving_writes_a_metadata_sidecar()
    {
        var cache = new LearnedBusCache(_directory);
        var learned = LearnBringup();

        cache.Save(learned);

        var fingerprint = LearnedBusCache.Fingerprint(learned.Configuration);
        Assert.True(File.Exists(Path.Combine(_directory, $"{fingerprint}.eni.xml")));
        Assert.True(File.Exists(Path.Combine(_directory, $"{fingerprint}.meta.json")));
    }

    /// <summary>The fingerprint deliberately excludes serial numbers, so swapping in an identical
    /// replacement terminal still hits the cache — which is the whole point of caching a bus.</summary>
    [Fact]
    public void The_fingerprint_ignores_names_and_depends_on_identity()
    {
        var a = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var renamed = WithSlaves(
            new EniSlave("renamed", 1001, 0x0000, 2, 0x03F03052, 0x00120000, null, null),
            Slave(1002, 0x03F03052));
        var different = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x07D83052));

        Assert.Equal(LearnedBusCache.Fingerprint(a), LearnedBusCache.Fingerprint(renamed));
        Assert.NotEqual(LearnedBusCache.Fingerprint(a), LearnedBusCache.Fingerprint(different));
    }

    [Fact]
    public void A_different_slave_count_changes_the_fingerprint()
    {
        var two = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var one = WithSlaves(Slave(1001, 0x03F03052));

        Assert.NotEqual(LearnedBusCache.Fingerprint(two), LearnedBusCache.Fingerprint(one));
    }

    /// <summary>On a mid-run attach the wire never revealed identity, so the primary fingerprint
    /// would key on zeroes for every bus. The fallback keys on what IS observable then: how many
    /// slaves answered, at which addresses, and the shape of the cyclic frame table.</summary>
    [Fact]
    public void The_fallback_fingerprint_does_not_depend_on_identity()
    {
        var known = WithSlaves(Slave(1001, 0x03F03052), Slave(1002, 0x03F03052));
        var anonymous = WithSlaves(
            new EniSlave("S1001", 1001, 0x0000, 0, 0, 0, null, null),
            new EniSlave("S1002", 1002, 0xFFFF, 0, 0, 0, null, null));

        Assert.Equal(LearnedBusCache.FallbackFingerprint(known),
            LearnedBusCache.FallbackFingerprint(anonymous));
        Assert.NotEqual(LearnedBusCache.Fingerprint(known), LearnedBusCache.Fingerprint(anonymous));
    }

    [Fact]
    public async Task A_complete_configuration_is_cached_after_a_session()
    {
        var pcap = BringupCapture.Write(Path.Combine(_directory, "run.pcap"), cycles: 5);
        var cache = new LearnedBusCache(_directory);

        await using var monitor = EtherCatMonitor.OpenFile(pcap,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        var fingerprint = LearnedBusCache.Fingerprint(monitor.Learned!.Configuration);
        Assert.True(cache.TryLoad(fingerprint, out _));
    }

    /// <summary>The mid-run attach the cache exists for: a capture that begins after startup reveals
    /// station addresses but no PDO mapping, so the cached configuration from an earlier session is
    /// what makes its variables readable at all.</summary>
    [Fact]
    public async Task A_mid_run_attach_applies_a_cached_configuration()
    {
        var cache = new LearnedBusCache(_directory);
        cache.Save(LearnBringup());
        var midRun = Path.Combine(_directory, "midrun.pcap");
        PcapFileWriter.Write(midRun, MidRunFrames());

        await using var monitor = EtherCatMonitor.OpenFile(midRun,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        // This capture never saw a startup, so the learner alone knows no SyncManagers, no FMMUs and
        // therefore no variables. Sixteen variables in force can only have come from the cache.
        Assert.False(monitor.Learned!.Completeness.SawStartup);
        Assert.Equal(16, monitor.Observer.Applied!.Configuration.Variables.Count);
        // And the payoff: the cyclic frames in this capture now decode through them.
        Assert.NotEmpty(monitor.ProcessImage.Current);
    }

    /// <summary>Provenance exists to say where a fact came from, and on a cache hit the answer is the
    /// cache — not the wire. Before this, `FactSource.Cache` had zero producers anywhere in the tree
    /// and a cached configuration was reported with the learner's own labels: this capture never read
    /// identity, so it claimed `Inferred`, and it learned no PDO mapping, so it claimed `EsiDefault`
    /// with no ESI in sight. Both name a source that produced none of the configuration in force.</summary>
    [Fact]
    public async Task Facts_that_came_from_the_cache_are_attributed_to_the_cache()
    {
        var cache = new LearnedBusCache(_directory);
        cache.Save(LearnBringup());
        var midRun = Path.Combine(_directory, "midrun-provenance.pcap");
        PcapFileWriter.Write(midRun, MidRunFrames());

        await using var monitor = EtherCatMonitor.OpenFile(midRun,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        var applied = monitor.Observer.Applied!;
        Assert.Equal(16, applied.Configuration.Variables.Count); // i.e. the cached one is in force
        Assert.NotEmpty(applied.Provenance);
        Assert.All(applied.Provenance.Values, p =>
        {
            Assert.Equal(FactSource.Cache, p.Identity);
            Assert.Equal(FactSource.Cache, p.Names);
            Assert.Equal(FactSource.Cache, p.Mapping);
        });
        // Completeness still describes what THIS capture revealed. A cache hit supplies a usable
        // configuration; it does not make the capture itself any more complete.
        Assert.False(monitor.Learned!.Completeness.SawStartup);
    }

    /// <summary>A cache hit must not be overwritten by the capture's own weaker picture. The fixture's
    /// late third station supplies a revision after the hit; without the guard, that revision replaces
    /// sixteen cached variables with none.</summary>
    [Fact]
    public async Task A_cached_configuration_is_not_overwritten_by_a_weaker_one()
    {
        var cache = new LearnedBusCache(_directory);
        cache.Save(LearnBringup());
        var midRun = Path.Combine(_directory, "midrun-persist.pcap");
        PcapFileWriter.Write(midRun, MidRunFrames(withLateSlave: true));

        await using var monitor = EtherCatMonitor.OpenFile(midRun,
            new EtherCatMonitorOptions { LearnedCache = cache });
        await monitor.RunAsync();

        // The late third station gives the learner a new structural fingerprint, so it publishes a
        // revision after the hit — one with no FMMUs and no PDO mapping, hence no variables. The
        // guard is the only thing standing between that revision and the sixteen cached variables.
        Assert.Equal(3, monitor.Learned!.Configuration.Slaves.Count);
        Assert.False(monitor.Learned.Completeness.IsComplete);
        Assert.Equal(16, monitor.Observer.Applied!.Configuration.Variables.Count);
    }

    /// <summary>A mid-run attach can only compute the fallback fingerprint, so a save that indexed
    /// solely under the primary key would leave the fallback lookup reading a file nothing writes.</summary>
    [Fact]
    public void Saving_indexes_under_both_fingerprints()
    {
        var cache = new LearnedBusCache(_directory);
        var learned = LearnBringup();

        cache.Save(learned);

        var primary = LearnedBusCache.Fingerprint(learned.Configuration);
        var fallback = LearnedBusCache.FallbackFingerprint(learned.Configuration);
        Assert.NotEqual(primary, fallback);
        Assert.True(cache.TryLoad(primary, out _));
        Assert.True(cache.TryLoad(fallback, out _));
    }

    /// <summary>Caching is on by default at every production construction site, so `--no-learn` —
    /// the documented complete opt-out — has to switch persistence off as well as learning. It does
    /// so by leaving the learner null, which means nothing is ever published for the cache to save.
    /// Asserted here rather than through the CLI because the default directory is shared by every
    /// test in the process, and an absence assertion against shared state is a race, not a test.</summary>
    [Fact]
    public async Task Learning_off_saves_nothing_even_with_a_cache_supplied()
    {
        var pcap = BringupCapture.Write(Path.Combine(_directory, "nolearn.pcap"), cycles: 5);
        var cache = new LearnedBusCache(_directory);

        await using var monitor = EtherCatMonitor.OpenFile(pcap, new EtherCatMonitorOptions
        {
            LearnedCache = cache,
            Learning = LearningMode.Off,
        });
        await monitor.RunAsync();

        Assert.Empty(Directory.GetFiles(_directory, "*.eni.xml"));
    }

    [Fact]
    public async Task Caching_is_off_when_no_cache_is_supplied()
    {
        var pcap = BringupCapture.Write(Path.Combine(_directory, "nocache.pcap"), cycles: 5);

        await using var monitor = EtherCatMonitor.OpenFile(pcap);
        await monitor.RunAsync();

        Assert.False(Directory.Exists(_directory)
            && Directory.GetFiles(_directory, "*.eni.xml").Length > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
