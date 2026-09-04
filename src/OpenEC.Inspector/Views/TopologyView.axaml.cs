using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class TopologyView : UserControl
{
    /// <summary>Dashes an inferred edge. Inline rather than a separate file because it exists
    /// solely for this view's wire template.</summary>
    public static readonly IValueConverter DashConverter =
        new FuncValueConverter<bool, AvaloniaList<double>?>(
            inferred => inferred ? [3, 3] : null);

    /// <summary>Fault colour for an edge the ENI and the wire describe differently, the ordinary
    /// line colour otherwise. Resolved against the active theme variant — the same mechanism
    /// <see cref="StatusDotBrushConverter"/> uses — because the palette's brushes live in theme
    /// dictionaries and a variant-less lookup would never find them, silently painting grey.</summary>
    public static readonly IValueConverter WireStrokeConverter =
        new FuncValueConverter<bool, IBrush?>(conflict =>
        {
            var app = Application.Current;
            var key = conflict ? "Fail" : "Line";
            return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush)
                ? brush as IBrush
                : Brushes.Gray;
        });

    public TopologyView()
    {
        InitializeComponent();
        // Selection is handled here rather than with per-box buttons: a Button would bring its own
        // focus and press visuals, and the box's border already carries the device's status colour.
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TopologyViewModel model) return;
        if ((e.Source as Control)?.DataContext is not TopologyBoxViewModel box) return;
        model.SelectedNode = box.Node;
    }
}
