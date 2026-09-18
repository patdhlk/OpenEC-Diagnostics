using System.Globalization;
using Avalonia.Data.Converters;
using OpenEC.Monitor;

namespace OpenEC.Inspector.Views;

public sealed class SegmentLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SegmentPipeline s ? (s.Port < 0 ? "Segment" : $"ESL port {s.Port}") : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
