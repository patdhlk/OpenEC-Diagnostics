using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Views;

/// <summary>Colours a port bar by its link state. Unused ports are never drawn, so they have no
/// colour here — an unused port is the absence of a bar, not a grey one. Resolved against the
/// active theme variant, the same way <see cref="StatusDotBrushConverter"/> does it.</summary>
public sealed class PortMarkBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PortLinkState.Active => "Ok",
            PortLinkState.Blocked => "PortBlocked",
            PortLinkState.Dangling => "PortDangling",
            _ => "Line",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush)
            ? brush : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
