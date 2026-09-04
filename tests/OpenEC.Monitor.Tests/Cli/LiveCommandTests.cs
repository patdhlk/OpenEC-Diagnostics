namespace OpenEC.Monitor.Tests.Cli;

public class LiveCommandTests
{
    private static TestApp App() => new();

    [Fact]
    public void Live_requires_interface_option()
    {
        Assert.NotEqual(0, App().Run("live").ExitCode);
    }

    [Fact]
    public void Live_with_unknown_interface_exits_2()
    {
        var result = App().Run("live", "--interface", "openec-does-not-exist-0", "--duration", "1");
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Live_on_non_interactive_console_skips_dashboard()
    {
        // Spectre's LiveDisplay is only supported on interactive consoles — driving it with
        // redirected output crashes inside LiveRenderable (observed on real ETAP-1000
        // captures). On a non-interactive console the dashboard must be skipped entirely.
        // Capture needs an openable Ethernet-type interface plus BPF/raw-socket permission;
        // probe a few quiet candidates and skip silently where none work (CI).
        var learnOut = Path.Combine(Path.GetTempPath(), $"openec-live-{Guid.NewGuid():N}.eni.xml");
        CommandResult? result = null;
        foreach (var candidate in new[] { "lo", "en1", "en2", "anpi0" })
        {
            result = App().Run("live", "--interface", candidate, "--duration", "1",
                "--learn-out", learnOut);
            if (result.ExitCode != 2) break;
        }
        if (result is null || result.ExitCode == 2) return; // no usable interface on this machine

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("OpenEC live", result.Output); // dashboard table title
        Assert.Contains("dashboard disabled", result.Output);
        Assert.Contains("Session summary", result.Output);
        // `--learn-out` used to write nothing and say nothing when the session learned nothing —
        // asking for an export and getting silence. A quiet interface carries no EtherCAT traffic,
        // so this is exactly that case: no file, but an explanation.
        Assert.False(File.Exists(learnOut));
        Assert.Contains("nothing learned", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
