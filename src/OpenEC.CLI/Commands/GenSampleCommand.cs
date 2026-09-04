using System.ComponentModel;
using OpenEC.Monitor.Synthesis;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class GenSampleCommand : Command<GenSampleCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<output>")]
        [Description("Output pcap path")]
        public string Output { get; init; } = "";

        [CommandOption("--cycles")]
        [Description("Number of bus cycles to generate (default 50)")]
        public int Cycles { get; init; } = 50;

        [CommandOption("--bringup")]
        [Description("Emit a full INIT-to-OP startup sequence instead of cyclic-only demo traffic, so `openec learn` has something to reconstruct")]
        public bool Bringup { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.Bringup)
                BringupCapture.Write(settings.Output, settings.Cycles);
            else
                SampleCapture.WriteDemo(settings.Output, settings.Cycles);
            AnsiConsole.MarkupLineInterpolated(
                $"Wrote [green]{settings.Output}[/] ({settings.Cycles} cycles{(settings.Bringup ? ", with bus startup" : "")})");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
