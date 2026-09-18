using System.ComponentModel;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Eni;
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
            var learners = new Dictionary<int, BusLearner>();
            await using var source = new PcapFileSource(settings.Capture);
            await foreach (var frame in source.CaptureAsync(cancellationToken))
            {
                var decoded = EtherCatFrameParser.Parse(frame.Data);
                int port = decoded is FrameDecodeResult.Success { Frame.Esl: { } e } ? e.Port : -1;
                if (!learners.TryGetValue(port, out var learner))
                {
                    learner = new BusLearner(settings.EsiDirectory);
                    learners[port] = learner;
                }
                learner.Observe(frame.Timestamp, decoded);
            }

            foreach (var learner in learners.Values)
                await learner.ResolveSchemasAsync(cancellationToken);

            var learned = learners.Where(kv => kv.Value.Current is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Current!);

            if (learned.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Learned nothing:[/] no EtherCAT slaves observed.");
                return 1;
            }

            if (settings.Output is { } output)
            {
                if (learned.Count == 1 && learned.ContainsKey(-1))
                {
                    EniXmlWriter.Write(learned[-1].Configuration, output);
                    AnsiConsole.MarkupLineInterpolated($"Wrote [green]{output}[/]");
                }
                else
                {
                    foreach (var (port, config) in learned.OrderBy(kv => kv.Key))
                    {
                        var portPath = InsertPortIntoPath(output, port);
                        EniXmlWriter.Write(config.Configuration, portPath);
                        AnsiConsole.MarkupLineInterpolated($"Wrote [green]{portPath}[/]");
                    }
                }
            }

            if (learned.Count == 1 && learned.ContainsKey(-1))
            {
                Report(learned[-1]);
            }
            else
            {
                foreach (var (port, config) in learned.OrderBy(kv => kv.Key))
                {
                    AnsiConsole.MarkupLineInterpolated($"[bold]ESL port {port}[/]");
                    Report(config);
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }

    private static string InsertPortIntoPath(string path, int port)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var portName = $"{fileName}.port{port}{ext}";
        return string.IsNullOrEmpty(dir) ? portName : Path.Combine(dir, portName);
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
