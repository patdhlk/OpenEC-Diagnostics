namespace OpenEC.Monitor.Tests.Cli;

public class CliCommandTests
{
    private static TestApp App() => new();

    [Fact]
    public void Gen_sample_then_frames_lists_datagrams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-cli-{Guid.NewGuid():N}.pcap");
        try
        {
            var gen = App().Run("gen-sample", path, "--cycles", "10");
            Assert.Equal(0, gen.ExitCode);
            Assert.True(File.Exists(path));

            var frames = App().Run("frames", path, "--count", "5");
            Assert.Equal(0, frames.ExitCode);
            Assert.Contains("LRW", frames.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frames_filter_by_command_excludes_others()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-cli-{Guid.NewGuid():N}.pcap");
        try
        {
            App().Run("gen-sample", path, "--cycles", "10");
            var result = App().Run("frames", path, "--cmd", "BRD", "--count", "5");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("BRD", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LRW", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frames_with_missing_file_exits_2()
    {
        var result = App().Run("frames", "/nonexistent/nope.pcap");
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Frames_with_garbage_file_exits_2_instead_of_crashing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-garbage-{Guid.NewGuid():N}.pcap");
        File.WriteAllBytes(path, new byte[] { 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 });
        try
        {
            var result = App().Run("frames", path);
            Assert.Equal(2, result.ExitCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Frames_with_unknown_cmd_filter_exits_2()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-cli-{Guid.NewGuid():N}.pcap");
        try
        {
            App().Run("gen-sample", path, "--cycles", "10");
            var result = App().Run("frames", path, "--cmd", "NOTACOMMAND");
            Assert.Equal(2, result.ExitCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Devices_lists_interfaces()
    {
        Assert.Equal(0, App().Run("devices").ExitCode);
    }
}
