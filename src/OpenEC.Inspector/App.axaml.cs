using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Inspector.Views;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(
                CaptureDevices.List,
                (spec, eni) => new MonitorSession(spec, eni),
                new StorageFilePicker(window),
                action => Dispatcher.UIThread.Post(action));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
