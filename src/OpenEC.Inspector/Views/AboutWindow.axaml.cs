using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenEC.Inspector.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var informational = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = $"Version {informational?.Split('+')[0] ?? "0.0.0"}";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
