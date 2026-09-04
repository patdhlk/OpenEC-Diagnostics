using System.ComponentModel;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

/// <summary>Reconstructs a bus configuration from a capture that includes bus startup, and
/// optionally writes it out as ENI XML for reuse with --eni.</summary>
public sealed class LearnCommand : AsyncCommand<LearnCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<capture>")]
        [Description("pcap/pcapng file containing bus startup")]
        public string Capture { get; init; } = "";

        [CommandOption("--out")]
        [Description("Write the learned configuration to this ENI XML path")]
        public string? Output { get; init; }

        [CommandOption("--esi-dir")]
        [Description("ESI directory used to resolve device and variable names")]
        public string? EsiDirectory { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var learner = new BusLearner(settings.EsiDirectory);
            await using var source = new PcapFileSource(settings.Capture);
            await foreach (var frame in source.CaptureAsync(cancellationToken))
                learner.Observe(frame.Timestamp, EtherCatFrameParser.Parse(frame.Data));
            await learner.ResolveSchemasAsync(cancellationToken);

            if (learner.Current is not { } learned)
            {
                AnsiConsole.MarkupLine("[yellow]Learned nothing:[/] no EtherCAT slaves observed.");
                return 1;
            }

            // Write before reporting: a failed --out must not leave a success-looking table
            // above the error, which reads as "learned, and saved" when nothing was saved.
            if (settings.Output is { } output)
            {
                EniXmlWriter.Write(learned.Configuration, output);
                AnsiConsole.MarkupLineInterpolated($"Wrote [green]{output}[/]");
            }
            Report(learned);
            return 0;
        }
        catch (Exception ex)
        {
            // CLI boundary: a corrupt pcap (SharpPcap), an unreadable capture (IOException) or an
            // unwritable --out path (XDocument.Save throwing ArgumentException/NotSupportedException)
            // must map to exit 2 (usage/IO failure), not the default unhandled-exception exit 255.
            // Deliberately bare, as in AnalyzeCommand and FramesCommand: a filtered list is a list
            // of the failures we happened to think of, and everything it misses escapes the
            // documented 0/1/2 contract.
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }

    private static void Report(LearnedConfiguration learned)
    {
        var table = new Table().Title("Learned bus").AddColumn("Addr").AddColumn("Name")
            .AddColumn("Identity").AddColumn("Complete");
        foreach (var slave in learned.Configuration.Slaves)
        {
            var completeness = learned.Completeness.Slaves
                .FirstOrDefault(s => s.StationAddress == slave.PhysAddr);
            table.AddRow(
                slave.PhysAddr.ToString(),
                slave.Name.EscapeMarkup(),
                $"0x{slave.VendorId:X4}:0x{slave.ProductCode:X8}",
                completeness?.IsComplete == true ? "[green]yes[/]" : "[yellow]partial[/]");
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine(learned.Completeness.Summary);
        AnsiConsole.WriteLine(
            $"{learned.Configuration.CyclicCommands.Count} cyclic commands, "
            + $"{learned.Configuration.Variables.Count} process variables.");
    }
}
