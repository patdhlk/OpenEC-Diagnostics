using Avalonia;

namespace OpenEC.Inspector;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        // Avalonia 12.1's default Windows GPU backend (ANGLE, after the SkiaSharp 3.119 bump)
        // rasterizes geometry but drops glyph runs on some drivers, so every label renders blank
        // while borders and buttons still draw. Force Skia's software renderer on Windows, which
        // draws text reliably. Win32PlatformOptions is inert on macOS/Linux.
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .LogToTrace();
}
