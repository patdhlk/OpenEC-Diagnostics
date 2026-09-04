using System.Reflection;
using OpenEC.CLI.Commands;
using OpenEC.Monitor.Capture;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI;

public static class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(Configure);
        return app.Run(args);
    }

    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("openec");
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        config.SetApplicationVersion(informational?.Split('+')[0] ?? "0.0.0");

        // A missing native pcap library reaches an unguarded command (e.g. `devices`) as a thrown
        // exception; render it as one clean line instead of a stack trace, matching the other commands.
        config.SetExceptionHandler((ex, _) =>
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return ex is PcapNativeLibraryMissingException ? 2 : -1;
        });
        config.AddCommand<DevicesCommand>("devices")
            .WithDescription("List capture interfaces");
        config.AddCommand<GenSampleCommand>("gen-sample")
            .WithDescription("Generate a synthetic EtherCAT sample capture");
        config.AddCommand<LearnCommand>("learn")
            .WithDescription("Reconstruct a bus configuration from a startup capture");
        config.AddCommand<FramesCommand>("frames")
            .WithDescription("Dump decoded frames/datagrams from a capture file");
        config.AddCommand<AnalyzeCommand>("analyze")
            .WithDescription("Analyze a capture file and report bus health");
        config.AddCommand<LiveCommand>("live")
            .WithDescription("Monitor a live interface (TAP monitor port)");
    }
}
