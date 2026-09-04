using System.ComponentModel;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class FramesCommand : AsyncCommand<FramesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("pcap/pcapng file")]
        public string File { get; init; } = "";

        [CommandOption("--cmd")]
        [Description("Only datagrams with this command (e.g. LRW, BRD, FPRD)")]
        public string? Command { get; init; }

        [CommandOption("--adp")]
        [Description("Only physical datagrams with this station address")]
        public ushort? Adp { get; init; }

        [CommandOption("--count")]
        [Description("Stop after this many datagram lines")]
        public int Count { get; init; } = int.MaxValue;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] file not found: {settings.File}");
            return 2;
        }
        EtherCatCommand? filter = null;
        if (settings.Command is not null)
        {
            if (!Enum.TryParse<EtherCatCommand>(settings.Command, ignoreCase: true, out var parsed))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] unknown command {settings.Command}");
                return 2;
            }
            filter = parsed;
        }

        var direction = new DirectionTracker();
        var printed = 0;
        var frameNo = 0;
        try
        {
            await using var source = new PcapFileSource(settings.File);
            await foreach (var raw in source.CaptureAsync(cancellationToken))
            {
                frameNo++;
                if (EtherCatFrameParser.Parse(raw.Data) is not FrameDecodeResult.Success ok) continue;
                var dir = direction.Classify(ok.Frame) == FrameDirection.Outbound ? "->" : "<-";
                foreach (var d in ok.Frame.Datagrams)
                {
                    if (filter is not null && d.Command != filter) continue;
                    if (settings.Adp is not null && (d.IsLogical || d.Adp != settings.Adp)) continue;
                    var addr = d.IsLogical
                        ? $"log 0x{d.LogicalAddress:X8}"
                        : $"adp {d.Adp} ado 0x{d.Ado:X4}";
                    AnsiConsole.MarkupLineInterpolated(
                        $"#{frameNo,5} {raw.Timestamp:HH:mm:ss.ffffff} {dir} {d.Command,-5} idx {d.Index,3} {addr} len {d.Payload.Length,4} wkc {d.WorkingCounter}");
                    if (++printed >= settings.Count) return 0;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            // CLI boundary: a corrupt pcap surfaces as a SharpPcap exception from CaptureAsync;
            // map it to exit 2 (usage/IO failure) instead of the default unhandled-exception exit 255.
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
