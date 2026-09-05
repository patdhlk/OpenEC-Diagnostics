using Avalonia;
using Avalonia.Media;

namespace OpenEC.Inspector;

internal static class AppFonts
{
    /// <summary>
    /// The bundled Inter font, referenced through the collection <c>WithInterFont</c> registers.
    /// </summary>
    public const string DefaultFamily = "fonts:Inter#Inter";

    /// <summary>
    /// Registers the bundled Inter font and pins it as the application-wide default.
    /// </summary>
    /// <remarks>
    /// <see cref="AppBuilderExtension.WithInterFont"/> only *registers* the collection; it does not
    /// make it the default family. Without an explicit default every surface falls back to the OS
    /// font, which is broken on Windows 11: "Segoe UI" resolves to the variable "Segoe UI Variable
    /// Text" that Avalonia 12 fails to shape, so all text renders invisibly. Pinning a font we ship
    /// makes rendering deterministic across platforms and covers popups, tooltips and menus that do
    /// not inherit a Window-level FontFamily.
    /// </remarks>
    public static AppBuilder WithAppFonts(this AppBuilder builder) => builder
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = DefaultFamily });
}
