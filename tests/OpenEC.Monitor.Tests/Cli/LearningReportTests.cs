using System.Text.Json;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Cli;

public class LearningReportTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lr-{Guid.NewGuid():N}")).FullName;

    private string Bringup()
    {
        var path = Path.Combine(_directory, "bringup.pcap");
        BringupCapture.Write(path, cycles: 5);
        return path;
    }

    [Fact]
    public void Analyze_json_carries_a_learning_block()
    {
        var result = new TestApp().Run("analyze", Bringup(), "--json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var learning = json.RootElement.GetProperty("learning");
        Assert.True(learning.GetProperty("sawStartup").GetBoolean());
        Assert.Equal(2, learning.GetProperty("slavesTotal").GetInt32());
        Assert.Equal(2, learning.GetProperty("slavesComplete").GetInt32());
    }

    [Fact]
    public void No_learn_omits_the_learning_block()
    {
        var result = new TestApp().Run("analyze", Bringup(), "--json", "--no-learn");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.TryGetProperty("learning", out _));
    }

    /// <summary>Spec §5: with an ENI supplied the learner cross-checks it and reports disagreements.
    /// Reporting is all it does — mismatches do not affect the exit code, which stays a statement
    /// about bus health (WKC, emergencies, SoE, slave error flags) alone.</summary>
    [Fact]
    public void A_mismatched_eni_surfaces_in_the_learning_block()
    {
        var eni = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        var result = new TestApp().Run("analyze", Bringup(), "--json", "--eni", eni);

        using var json = JsonDocument.Parse(result.Output);
        var mismatches = json.RootElement.GetProperty("learning").GetProperty("mismatches");
        Assert.True(mismatches.GetArrayLength() > 0);
    }

    /// <summary>Regression for two bugs the review found in the same output: the learner
    /// republishes many times as the bus picture fills in, so the same finding was being raised
    /// once per revision instead of once ever; and the Identity finding — the single most
    /// decision-relevant one — was falling outside Render's Take(20) because of that duplication.</summary>
    [Fact]
    public void The_mismatch_list_has_no_duplicates_and_still_contains_identity()
    {
        var eni = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        var result = new TestApp().Run("analyze", Bringup(), "--json", "--eni", eni);

        using var json = JsonDocument.Parse(result.Output);
        var mismatches = json.RootElement.GetProperty("learning").GetProperty("mismatches")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(mismatches.Length, mismatches.Distinct().Count());
        Assert.Contains(mismatches, m => m.StartsWith("Identity", StringComparison.Ordinal));
    }

    /// <summary>AnalysisReport.Describe had no case for ConfigMismatch/ConfigurationLearned — the two
    /// event kinds this plan added — so both fell through to the record's own ToString() and dumped
    /// raw C# into the events array, a machine-readable surface. Pins both that no raw dump reaches
    /// it and that a mismatch renders with its kind and both sides of the disagreement.</summary>
    [Fact]
    public void Events_describe_config_mismatches_instead_of_dumping_the_record()
    {
        var eni = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");

        var result = new TestApp().Run("analyze", Bringup(), "--json", "--eni", eni);

        using var json = JsonDocument.Parse(result.Output);
        var events = json.RootElement.GetProperty("events")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.DoesNotContain(events, e => e.StartsWith("ConfigMismatch {", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.StartsWith("ConfigurationLearned {", StringComparison.Ordinal));
        Assert.Contains(events, e =>
            e.Contains("Identity", StringComparison.Ordinal)
            && e.Contains("ENI says", StringComparison.Ordinal)
            && e.Contains("bus shows", StringComparison.Ordinal));
    }

    /// <summary>Spectre returns a non-zero exit for an unknown option too, so a failing `live` run
    /// cannot tell a wired flag from a typo. The help output can.</summary>
    [Fact]
    public void Live_registers_the_learn_out_option()
    {
        var result = new TestApp().Run("live", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--learn-out", result.Output);
    }

    [Fact]
    public void Live_writes_no_eni_when_the_capture_never_starts()
    {
        var output = Path.Combine(_directory, "bus.eni.xml");

        var result = new TestApp().Run("live", "--interface", "nonexistent0",
            "--duration", "1", "--learn-out", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
