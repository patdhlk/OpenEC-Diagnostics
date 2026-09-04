using OpenEC.Monitor.Capture;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class DevicesCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var table = new Table().AddColumn("Name").AddColumn("Description");
        foreach (var (name, description) in CaptureDevices.List())
            table.AddRow(name.EscapeMarkup(), (description ?? "").EscapeMarkup());
        AnsiConsole.Write(table);
        return 0;
    }
}
