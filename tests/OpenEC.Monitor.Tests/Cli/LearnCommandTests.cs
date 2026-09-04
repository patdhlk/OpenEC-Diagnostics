using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Cli;

public class LearnCommandTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"learn-{Guid.NewGuid():N}")).FullName;

    private string Capture()
    {
        var path = Path.Combine(_directory, "bringup.pcap");
        BringupCapture.Write(path, cycles: 5);
        return path;
    }

    [Fact]
    public void Learns_a_bringup_and_reports_coverage()
    {
        var result = new TestApp().Run("learn", Capture());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2/2", result.Output);
    }

    [Fact]
    public void Writes_a_loadable_eni_file()
    {
        var outputPath = Path.Combine(_directory, "bus.eni.xml");

        var result = new TestApp().Run("learn", Capture(), "--out", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, EniConfiguration.Load(outputPath).Slaves.Count);
    }

    [Fact]
    public void A_capture_with_no_ethercat_traffic_exits_one()
    {
        var path = Path.Combine(_directory, "empty.pcap");
        PcapFileWriter.Write(path, []);

        var result = new TestApp().Run("learn", path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nothing", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_file_exits_two()
    {
        var result = new TestApp().Run("learn",
            Path.Combine(_directory, "does-not-exist.pcap"));

        Assert.Equal(2, result.ExitCode);
    }

    /// <summary>The workflow the README documents, end to end through the CLI only. Without
    /// --bringup there is no way to exercise `learn` at all short of real hardware, since the plain
    /// demo capture carries no startup sequence to reconstruct.</summary>
    [Fact]
    public void Gen_sample_bringup_produces_a_capture_learn_can_reconstruct()
    {
        var capture = Path.Combine(_directory, "bringup.pcap");
        var output = Path.Combine(_directory, "bus.eni.xml");

        var generated = new TestApp().Run("gen-sample", capture, "--bringup", "--cycles", "5");
        var learned = new TestApp().Run("learn", capture, "--out", output);

        Assert.Equal(0, generated.ExitCode);
        Assert.Equal(0, learned.ExitCode);
        Assert.Equal(2, EniConfiguration.Load(output).Slaves.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
