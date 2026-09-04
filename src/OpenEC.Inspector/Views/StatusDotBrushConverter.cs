using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

/// <summary>StatusDot → palette brush. Resolved at convert time against the active theme
/// variant; a live theme switch repaints a dot on its next value change (4 Hz tick), which
/// is an accepted approximation.</summary>
public sealed class StatusDotBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusDot.Ok => "Ok",
            StatusDot.Oos => "Oos",
            StatusDot.Fail => "Fail",
            _ => "Ink3",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush)
            ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
