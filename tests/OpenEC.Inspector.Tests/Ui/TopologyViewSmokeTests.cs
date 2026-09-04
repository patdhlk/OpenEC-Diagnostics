using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector.Tests.Ui;

public class TopologyViewSmokeTests
{
    private static async Task<(Window Window, ExplorerViewModel Explorer)> ShowBranchedAsync(
        int viewIndex)
    {
        var session = await TestSessions.BranchedAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        explorer.SelectedViewIndex = viewIndex;
        var window = new Window
        {
            Content = new ExplorerView { DataContext = explorer },
            Width = 700,
            Height = 600,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, explorer);
    }

    [AvaloniaFact]
    public async Task The_explorer_pane_offers_both_views_by_name()
    {
        var (window, _) = await ShowBranchedAsync(viewIndex: 0);

        var headers = window.GetVisualDescendants().OfType<TabItem>()
            .Select(t => t.Header?.ToString()).ToList();

        Assert.Contains("Classic View", headers);
        Assert.Contains("Topology View", headers);
    }

    [AvaloniaFact]
    public async Task The_topology_tab_renders_a_box_per_device_and_a_wire_per_edge()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);

        // DataContext inherits to every descendant of a box's template, so a raw count of controls
        // carrying a TopologyBoxViewModel over-counts (one box → many controls). Counting DISTINCT
        // box view models faithfully expresses "one box rendered per device".
        var boxCount = window.GetVisualDescendants()
            .Select(v => (v as Control)?.DataContext).OfType<TopologyBoxViewModel>().Distinct().Count();
        var wires = window.GetVisualDescendants().OfType<Polyline>().ToList();

        Assert.Equal(explorer.Topology.Boxes.Count, boxCount);
        Assert.Equal(explorer.Topology.Wires.Count, wires.Count);
    }

    /// <summary>The point of the whole feature: clicking a box drives the same selection a tree
    /// row does.</summary>
    [AvaloniaFact]
    public async Task Clicking_a_box_selects_that_node_on_the_explorer()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);
        var target = explorer.Topology.Boxes.Single(b => b.Address == 1003);
        var control = window.GetVisualDescendants()
            .OfType<Control>()
            .First(c => ReferenceEquals(c.DataContext, target) && c is Border);

        var point = control.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(target.Node, explorer.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Switching_tabs_preserves_the_selection()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);
        var node = explorer.Topology.Boxes.Single(b => b.Address == 1002).Node;
        explorer.SelectedNode = node;

        explorer.SelectedViewIndex = 0;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        explorer.SelectedViewIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(node, explorer.SelectedNode);
        Assert.Same(node, explorer.Topology.SelectedNode);
    }

    /// <summary>The `Fail` brush key must exist for the conflict stroke to resolve. Asserted here
    /// rather than trusted, because a missing key silently paints grey — indistinguishable from a
    /// healthy edge. "Fail" lives in the Palette theme dictionaries, so it is looked up against the
    /// active theme variant — a variant-less lookup cannot resolve a theme-dictionary resource.</summary>
    [AvaloniaFact]
    public async Task The_fault_brush_the_conflict_stroke_needs_is_defined()
    {
        await ShowBranchedAsync(viewIndex: 1);

        var app = Application.Current!;
        Assert.True(app.TryGetResource("Fail", app.ActualThemeVariant, out var fail));
        Assert.NotNull(fail);
    }

    [AvaloniaFact]
    public async Task The_notice_is_shown_only_when_port_data_is_missing()
    {
        var session = await TestSessions.RunFileSessionAsync();
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        explorer.SelectedViewIndex = 1;
        var window = new Window
        {
            Content = new ExplorerView { DataContext = explorer },
            Width = 700,
            Height = 600,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var notice = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains("not observed", StringComparison.OrdinalIgnoreCase)
                                 == true);
        Assert.NotNull(notice);
        Assert.True(notice!.IsVisible);
    }

    /// <summary>Regression: a narrow box is 16px wide but its label (the 4-digit address) is
    /// rotated to run up its 44px-tall axis. A render-transform rotation measures the label against
    /// the box width first, so "1002" was clipped to "10" and only then turned; the layout-transform
    /// rotation measures it at full width. Asserted by the label's own bounds matching the text's
    /// natural width rather than the squeezed box interior.</summary>
    [AvaloniaFact]
    public async Task A_narrow_box_shows_its_whole_address_not_clipped()
    {
        var (window, explorer) = await ShowBranchedAsync(viewIndex: 1);

        // The branched bus has exactly one narrow in-line device; the rest are wide.
        var narrow = explorer.Topology.Boxes.Single(b => !b.IsWide);
        Assert.Equal("1002", narrow.Label);

        var label = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == narrow.Label
                         && t.GetVisualAncestors().OfType<LayoutTransformControl>().Any());

        // Same font, measured unconstrained: what the label needs to show the whole address.
        var probe = new TextBlock { Text = narrow.Label, FontSize = label.FontSize, FontFamily = label.FontFamily };
        probe.Measure(Size.Infinity);

        Assert.True(label.Bounds.Width >= probe.DesiredSize.Width - 0.5,
            $"label width {label.Bounds.Width} is clipped below the natural {probe.DesiredSize.Width}");
    }
}
