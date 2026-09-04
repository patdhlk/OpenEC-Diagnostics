using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace OpenEC.Inspector.Views;

/// <summary>Two-way bridge between a pixel width on a view model and a Grid column's
/// <see cref="GridLength"/>. Needed because a GridSplitter writes the column's GridLength back,
/// and the view model must stay a plain double for the width to be testable without a window.
/// </summary>
public sealed class GridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width ? new GridLength(width, GridUnitType.Pixel) : GridLength.Auto;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GridLength { IsAbsolute: true } length ? length.Value : 280d;
}
