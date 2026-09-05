using Avalonia;
using Avalonia.Headless;
using OpenEC.Inspector;
using OpenEC.Inspector.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Avalonia's headless application is process-global, and the shell tests drive real
// MonitorSessions that share the on-disk learned-bus cache. Those cannot run concurrently,
// and xUnit v3 parallelises test classes by default — so serialise the whole assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpenEC.Inspector.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
