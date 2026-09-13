using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Desktop.Pages;

public partial class ApplicationsPage : UserControl
{
    private readonly AppServices _services;

    public ApplicationsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var apps = await _services.Applications.ListRunningAsync();
        ProcessList.ItemsSource = apps;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void LaunchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Launch();
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e) => Launch();

    private void Launch()
    {
        var name = LaunchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _ = RunAsync("Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = name },
            ct => _services.Applications.LaunchAsync(name, null, ct), reloadAfter: true);
    }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RunningApplication app)
        {
            return;
        }

        _ = RunAsync("Application", "Focus", new Dictionary<string, object?> { ["processNameOrWindowTitle"] = app.ProcessName },
            ct => _services.Applications.FocusAsync(app.ProcessName, ct));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RunningApplication app)
        {
            return;
        }

        _ = RunAsync("Application", "Close", new Dictionary<string, object?> { ["processId"] = app.ProcessId },
            ct => _services.Applications.CloseAsync(app.ProcessId, ct), reloadAfter: true);
    }

    private async Task RunAsync(string tool, string action, IReadOnlyDictionary<string, object?> parameters, Func<CancellationToken, Task> execute, bool reloadAfter = false)
    {
        var (success, message) = await _services.ActionRunner.RunAsync(tool, action, parameters, execute);
        StatusText.Text = message;

        if (success && reloadAfter)
        {
            await RefreshAsync();
        }
    }
}
