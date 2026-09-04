using System;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.Tick());
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnShowAbout(object? sender, EventArgs e) =>
        _ = new AboutWindow().ShowDialog(this);

    private void OnOpenProjectUrl(object? sender, EventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("https://github.com/patdhlk/OpenEC-Diagnostics"));

    private void OnReportIssue(object? sender, EventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("https://github.com/patdhlk/OpenEC-Diagnostics/issues/new"));
}
