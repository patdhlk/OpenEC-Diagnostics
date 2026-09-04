using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;

namespace OpenEC.Inspector.Tests.Ui;

public class ShellSmokeTests
{
    private static MainWindowViewModel CreateViewModel() => new(
        () => [],
        (spec, eni) => new MonitorSession(spec, eni),
        new FakeFilePicker(),
        marshal: action => action(),
        earlyFaultProbe: TimeSpan.FromSeconds(2));

    [AvaloniaFact]
    public void Main_window_boots_to_the_start_screen()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Equal("OpenEC Inspector", window.Title);
        Assert.Same(vm.Start, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_file_session_renders_every_node_page_and_the_messages_panel()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Start.PcapPath = TestSessions.WriteDemoPcap();
        vm.Start.EniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        await vm.Start.StartFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasSession);
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
        vm.Tick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The shell's Explorer/Events panels must actually rebind when a session starts,
        // not just when CurrentPage happens to change (property-path bindings need PropertyChanged).
        var explorerView = window.GetVisualDescendants().OfType<ExplorerView>().Single();
        var eventsView = window.GetVisualDescendants().OfType<EventsView>().Single();
        Assert.Same(vm.Explorer, explorerView.DataContext);
        Assert.Same(vm.Events, eventsView.DataContext);

        // Walk every tree node while the window is live — templates must instantiate without throwing.
        foreach (var node in vm.Explorer!.Root.Children.Append<ExplorerNode>(vm.Explorer.Root).ToList())
        {
            vm.Explorer.SelectedNode = node;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // Editor tabs and the messages panel collapse must also instantiate.
        vm.Explorer.SelectedNode = vm.Explorer.Root.Children.OfType<SlaveNode>().First();
        ((DeviceEditorViewModel)vm.CurrentPage).SelectedTabIndex = 1;
        vm.Tick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        vm.Events!.IsCollapsed = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        await vm.StopSessionCommand.ExecuteAsync(null);
        Assert.Same(vm.Start, vm.CurrentPage);
    }

    // Clicking a row is not the same path as assigning SelectedNode from the view model: Avalonia
    // hunts for the clicked node's container starting at the TreeView, so it probes the *root* item
    // source with a node that lives below the root. Every row has to survive that probe.
    [AvaloniaFact]
    public async Task Clicking_any_explorer_row_selects_that_node()
    {
        await using var session = await TestSessions.RunFileSessionAsync(); // no ENI: process image shows up too
        var explorer = new ExplorerViewModel(session, assignment: null, _ => { });
        explorer.Refresh();
        var window = new Window
        {
            Content = new ExplorerView { DataContext = explorer },
            Width = 400,
            Height = 600,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.Contains(rows, r => r.DataContext is SlaveNode);
        Assert.Contains(rows, r => r.DataContext is ProcessImageNode);

        foreach (var row in rows)
        {
            var point = row.TranslatePoint(new Point(60, 10), window)!.Value; // header strip, past the expander
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Same(row.DataContext, explorer.SelectedNode);
        }
    }

    [AvaloniaFact]
    public void The_native_menu_offers_the_application_entries()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        window.Show();

        // The macOS default app menu (About/Quit) is supplied by the platform; these are the
        // application-specific menus attached on top of it.
        var menu = NativeMenu.GetMenu(window);
        Assert.NotNull(menu);
        var headers = menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
        Assert.Contains("File", headers);
        Assert.Contains("Help", headers);

        var file = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "File").Menu!;
        var fileHeaders = file.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
        Assert.Contains("Open Capture File…", fileHeaders);
        Assert.Contains("Save Learned ENI…", fileHeaders);
        Assert.Contains("Close Session", fileHeaders);
    }

    [AvaloniaFact]
    public void The_close_session_entry_is_disabled_without_a_session()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        window.Show();

        var file = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>()
            .Single(i => i.Header == "File").Menu!;
        var close = file.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Close Session");
        Assert.False(close.IsEnabled);
    }

    [AvaloniaFact]
    public void The_about_dialog_reports_the_application()
    {
        var about = new AboutWindow();

        Assert.Equal("About OpenEC Inspector", about.Title);
    }

    [AvaloniaFact]
    public void The_application_is_named_for_the_macos_menu_bar()
    {
        Assert.Equal("OpenEC Inspector", Application.Current!.Name);
    }

    [AvaloniaFact]
    public void The_load_eni_entry_is_disabled_without_a_session()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        window.Show();

        var file = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>()
            .Single(i => i.Header == "File").Menu!;
        var loadEni = file.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Load ENI…");
        Assert.False(loadEni.IsEnabled);
    }
}
