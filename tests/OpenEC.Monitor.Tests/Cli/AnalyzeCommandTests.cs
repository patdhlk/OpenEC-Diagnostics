using System.Text.Json;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Synthesis;
using OpenEC.Monitor.Tests.Learning;

namespace OpenEC.Monitor.Tests.Cli;

public class AnalyzeCommandTests
{
    private static TestApp App() => new();

    private static string BringupPcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-analyze-bringup-{Guid.NewGuid():N}.pcap");
        return BringupCapture.Write(path, cycles: 5);
    }

    private static string SamplePcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-analyze-{Guid.NewGuid():N}.pcap");
        SampleCapture.WriteDemo(path, cycles: 30);
        return path;
    }

    private static string FixtureEni() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

    private static string GarbageFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-garbage-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, new byte[] { 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 });
        return path;
    }

    [Fact]
    public void Analyze_with_eni_reports_bus_errors_via_exit_code_1()
    {
        var path = SamplePcap();
        try
        {
            var result = App().Run("analyze", path, "--eni", FixtureEni());
            Assert.Equal(1, result.ExitCode); // sample contains a WKC error + emergency
            Assert.Contains("WKC", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_json_is_machine_readable()
    {
        var path = SamplePcap();
        try
        {
            var result = App().Run("analyze", path, "--eni", FixtureEni(), "--json");
            Assert.Equal(1, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Output);
            Assert.Equal(1, doc.RootElement.GetProperty("wkcMismatches").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("emergencies").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("soeErrors").GetInt64());
            Assert.Equal(0, doc.RootElement.GetProperty("ringLostFrames").GetInt64());
            Assert.Contains(doc.RootElement.GetProperty("events").EnumerateArray(),
                e => e.GetString()!.Contains("SoE error") && e.GetString()!.Contains("S-0-0017"));
            Assert.True(doc.RootElement.GetProperty("etherCatFrames").GetInt64() > 0);
            Assert.True(doc.RootElement.GetProperty("slaves").GetArrayLength() >= 4);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Analysing a bringup capture is how a bus gets into the cache, so a later mid-run
    /// attach can recognise it. The command supplied no cache at all until this was wired, which made
    /// the README's "cached by fingerprint" claim unreachable by any user: nothing saved, so nothing
    /// could ever be loaded. Asserting on the DEFAULT directory rather than an injected one is the
    /// point — the defect was the production construction site, not the cache.</summary>
    [Fact]
    public void Analyze_caches_a_completely_learned_bus_under_the_default_directory()
    {
        var path = BringupPcap();
        try
        {
            Assert.Equal(0, App().Run("analyze", path).ExitCode);

            // Same frames, learned in-process, so the test can compute the fingerprint the command
            // will have saved under without reparsing the file.
            var learned = LearnedBusCacheTests.LearnBringup().Configuration;
            var cache = new LearnedBusCache(LearnedBusCache.DefaultDirectory);
            Assert.True(cache.TryLoad(LearnedBusCache.Fingerprint(learned), out var cached));
            Assert.Equal(16, cached!.Variables.Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A cache hit has to be distinguishable from a fresh learn without string-matching the
    /// events list. `provenance` cannot carry it: that field reports the LEARNER's view of this capture,
    /// which on a mid-run attach honestly knows nothing — the cache-sourced attribution lives on the
    /// applied configuration. Hence a separate field naming where the configuration in force came from.</summary>
    [Fact]
    public void Analyze_reports_a_cache_hit_as_the_configuration_source()
    {
        // Save the bus first, then analyse a capture that begins after startup: it can never learn a
        // configuration of its own, so anything decoding it came out of the cache.
        new LearnedBusCache(LearnedBusCache.DefaultDirectory).Save(LearnedBusCacheTests.LearnBringup());
        var path = Path.Combine(Path.GetTempPath(), $"openec-analyze-midrun-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, LearnedBusCacheTests.MidRunFrames());
        try
        {
            var result = App().Run("analyze", path, "--json");

            using var doc = JsonDocument.Parse(result.Output);
            var learning = doc.RootElement.GetProperty("learning");
            Assert.Equal("cache", learning.GetProperty("configurationSource").GetString());
            // And the field genuinely adds information: the learner's own view still reports what the
            // wire showed, which here is nothing.
            Assert.False(learning.GetProperty("sawStartup").GetBoolean());
            Assert.Equal(0, learning.GetProperty("slavesComplete").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_reports_a_freshly_learned_bus_as_observed()
    {
        var path = BringupPcap();
        try
        {
            var result = App().Run("analyze", path, "--json");

            using var doc = JsonDocument.Parse(result.Output);
            Assert.Equal("observed",
                doc.RootElement.GetProperty("learning").GetProperty("configurationSource").GetString());
        }
        finally { File.Delete(path); }
    }

    /// <summary>The field is additive: with an ENI supplied the ENI is the authority and nothing is
    /// rebound, so there is no configuration to attribute and the JSON shape stays as it was.</summary>
    [Fact]
    public void Analyze_with_an_eni_omits_the_configuration_source()
    {
        var path = SamplePcap();
        try
        {
            var result = App().Run("analyze", path, "--eni", FixtureEni(), "--json");

            using var doc = JsonDocument.Parse(result.Output);
            Assert.False(doc.RootElement.GetProperty("learning")
                .TryGetProperty("configurationSource", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_missing_file_exits_2()
    {
        Assert.Equal(2, App().Run("analyze", "/nonexistent/nope.pcap").ExitCode);
    }

    [Fact]
    public void Analyze_garbage_pcap_exits_2_instead_of_crashing()
    {
        var path = GarbageFile(".pcap");
        try
        {
            var result = App().Run("analyze", path);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("error", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_valid_pcap_with_garbage_eni_exits_2_instead_of_crashing()
    {
        var pcap = SamplePcap();
        var eni = GarbageFile(".xml");
        try
        {
            var result = App().Run("analyze", pcap, "--eni", eni);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("error", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(pcap);
            File.Delete(eni);
        }
    }

    [Fact]
    public void Analyze_json_with_garbage_eni_emits_json_error_object_and_exits_2()
    {
        var pcap = SamplePcap();
        var eni = GarbageFile(".xml");
        try
        {
            var result = App().Run("analyze", pcap, "--eni", eni, "--json");
            Assert.Equal(2, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Output);
            Assert.True(doc.RootElement.TryGetProperty("error", out var error));
            Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
        }
        finally
        {
            File.Delete(pcap);
            File.Delete(eni);
        }
    }
}
