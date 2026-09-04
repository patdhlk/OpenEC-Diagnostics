using System.ComponentModel;
using System.Text.Json;
using OpenEC.CLI.Reporting;
using OpenEC.Monitor;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class AnalyzeCommand : AsyncCommand<AnalyzeCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("pcap/pcapng file")]
        public string File { get; init; } = "";

        [CommandOption("--eni")]
        [Description("ENI.xml exported from the master configuration")]
        public string? Eni { get; init; }

        [CommandOption("--esi-dir")]
        [Description("Directory of vendor ESI XML files for device naming")]
        public string? EsiDirectory { get; init; }

        [CommandOption("--json")]
        [Description("Emit the report as JSON")]
        public bool Json { get; init; }

        [CommandOption("--no-learn")]
        [Description("Disable passive configuration learning (on by default)")]
        public bool NoLearn { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
            return Error(settings.Json, $"file not found: {settings.File}");
        if (settings.Eni is not null && !File.Exists(settings.Eni))
            return Error(settings.Json, $"ENI not found: {settings.Eni}");

        try
        {
            EniConfiguration? eni = settings.Eni is not null ? EniConfiguration.Load(settings.Eni) : null;

            await using var monitor = EtherCatMonitor.OpenFile(settings.File, new EtherCatMonitorOptions
            {
                Eni = eni,
                EsiDirectory = settings.EsiDirectory,
                Learning = settings.NoLearn ? LearningMode.Off : LearningMode.Auto,
                // Analysing a bringup capture is how a bus gets INTO the cache, so a later mid-run
                // `live` attach can recognise it — which makes `analyze` a writer, not just a
                // reader. `--no-learn` is the complete opt-out: it leaves the learner null, so
                // neither the lookup nor the save can run.
                LearnedCache = LearnedBusCache.Default(),
            });
            await monitor.RunAsync(cancellationToken);

            var report = AnalysisReport.Build(settings.File, monitor);
            if (settings.Json)
            {
                // Write directly to the underlying writer instead of AnsiConsole.WriteLine: Spectre
                // word-wraps Text renderables to the console width, which would inject stray
                // newlines into long JSON string values (e.g. the file path) and corrupt the output.
                var writer = AnsiConsole.Console.Profile.Out.Writer;
                writer.Write(JsonSerializer.Serialize(report, JsonOptions));
                writer.Write('\n');
            }
            else
                Render(report);
            return report.HasBusErrors ? 1 : 0;
        }
        catch (Exception ex)
        {
            // CLI boundary: a corrupt pcap (SharpPcap) or corrupt ENI (XmlException) must map to
            // exit 2 (usage/IO failure), not the default unhandled-exception exit 255.
            return Error(settings.Json, ex.Message);
        }
    }

    private static int Error(bool json, string message)
    {
        if (json)
        {
            var writer = AnsiConsole.Console.Profile.Out.Writer;
            writer.Write(JsonSerializer.Serialize(new { error = message }, JsonOptions));
            writer.Write('\n');
        }
        else
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {message}");
        return 2;
    }

    private static void Render(AnalysisReport report)
    {
        var overview = new Table().Title("Overview").AddColumn("Metric").AddColumn("Value");
        overview.AddRow("File", report.File.EscapeMarkup());
        overview.AddRow("Frames (EtherCAT/total)", $"{report.EtherCatFrames}/{report.TotalFrames}");
        overview.AddRow("Non-EtherCAT / malformed", $"{report.NonEtherCatFrames} / {report.MalformedFrames}");
        overview.AddRow("Frames per second", report.FramesPerSecond?.ToString("F1") ?? "-");
        overview.AddRow("Cycle time (us)", report.CycleTimeMicroseconds?.ToString("F0") ?? "-");
        overview.AddRow("Suspected lost frames", report.SuspectedLostFrames.ToString());
        overview.AddRow("Ring lost frames",
            report.RingLostFrames > 0 ? $"[yellow]{report.RingLostFrames}[/]" : "0");
        overview.AddRow("WKC mismatches",
            report.WkcMismatches > 0 ? $"[red]{report.WkcMismatches}[/]" : "0");
        overview.AddRow("Emergencies",
            report.Emergencies > 0 ? $"[red]{report.Emergencies}[/]" : "0");
        overview.AddRow("SoE errors",
            report.SoeErrors > 0 ? $"[red]{report.SoeErrors}[/]" : "0");
        overview.AddRow("Bus state", report.BusState);
        overview.AddRow("Bus health", HealthFormat.Level(report.Health.Level));
        overview.AddRow("Devices (found/configured)",
            HealthFormat.Devices(report.Health.FoundDevices, report.Health.ConfiguredDevices));
        overview.AddRow("DC sync", HealthFormat.Dc(report.Health.DcSync));
        overview.AddRow("Stale process data",
            HealthFormat.StaleProcessData(report.Health.StaleProcessData));
        AnsiConsole.Write(overview);

        if (report.Learning is { } learning)
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]Learning:[/] {learning.Summary}");
            foreach (var mismatch in learning.Mismatches.Take(20))
                AnsiConsole.MarkupLineInterpolated($"  [yellow]mismatch[/] {mismatch}");
            if (learning.Mismatches.Count > 20)
                AnsiConsole.WriteLine($"  ... {learning.Mismatches.Count - 20} more");
        }

        if (report.Slaves.Count > 0)
        {
            var slaves = new Table().Title("Slaves")
                .AddColumn("Addr").AddColumn("Name").AddColumn("State").AddColumn("Err").AddColumn("AL code");
            foreach (var s in report.Slaves)
                slaves.AddRow(s.Address.ToString(), s.Name.EscapeMarkup(), s.State,
                    s.Error ? "[red]yes[/]" : "no", s.AlStatusCode ?? "-");
            AnsiConsole.Write(slaves);
        }

        if (report.Events.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Events[/] ({report.Events.Count}):");
            foreach (var line in report.Events.Take(50))
                AnsiConsole.WriteLine("  " + line);
            if (report.Events.Count > 50)
                AnsiConsole.WriteLine($"  ... {report.Events.Count - 50} more");
        }
    }
}
