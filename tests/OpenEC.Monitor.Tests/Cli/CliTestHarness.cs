using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OpenEC.Monitor.Tests.Cli;

// Spectre.Console.Testing 0.57.2 (the version referenced by this project) does not ship
// CommandAppTester (that type belonged to the pre-merge Spectre.Console.Cli.Testing
// package). This tiny local harness reproduces the same shape (Run -> ExitCode/Output)
// by swapping the static AnsiConsole.Console for a TestConsole around CommandApp.Run.
internal sealed record CommandResult(int ExitCode, string Output);

internal sealed class TestApp
{
    // AnsiConsole.Console is a static/global property, so swapping it must be serialized:
    // xUnit runs different test classes (and thus CliCommandTests/AnalyzeCommandTests) in
    // parallel by default, and two concurrent Run() calls would otherwise race on the swap
    // and corrupt each other's captured output.
    private static readonly object Gate = new();

    private readonly CommandApp _app = new();

    public TestApp() => _app.Configure(OpenEC.CLI.Program.Configure);

    public CommandResult Run(params string[] args)
    {
        lock (Gate)
        {
            var console = new TestConsole();
            var original = AnsiConsole.Console;
            AnsiConsole.Console = console;
            // Spectre.Console.Cli's own help/validation-error rendering (e.g. `--help`, or a
            // ValidationResult failure) does not read the swapped AnsiConsole.Console: it writes
            // through Settings.Console, which falls back to a process-wide `Lazy<IAnsiConsole>`
            // the FIRST time anything renders through it — and that Lazy then keeps returning that
            // one console forever, so every help render after the first, in the whole test process,
            // would otherwise land in a stale, already-disposed-of TestConsole instead of this run's.
            // Configuring Settings.Console explicitly, every run, sidesteps that fallback entirely.
            _app.Configure(c => c.ConfigureConsole(console));
            try
            {
                var exitCode = _app.Run(args);
                return new CommandResult(exitCode, console.Output);
            }
            finally
            {
                AnsiConsole.Console = original;
            }
        }
    }
}
