using Avalonia;
using Avalonia.Headless;
using OpenEC.Inspector;
using OpenEC.Inspector.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace OpenEC.Inspector.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
