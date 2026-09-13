using System.Windows;
using System.Windows.Controls;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Desktop.Pages;

public partial class SystemToolsPage : UserControl
{
    private readonly AppServices _services;

    public SystemToolsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync() => ProcessList.ItemsSource = await _services.Applications.ListRunningAsync();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void CloseProcess_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RunningApplication app)
        {
            return;
        }

        _ = RunAsync("Application", "Close", new Dictionary<string, object?> { ["processId"] = app.ProcessId },
            ct => _services.Applications.CloseAsync(app.ProcessId, ct), reloadAfter: true);
    }

    private void AltTab_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("ComputerControl", "SendKeys", new Dictionary<string, object?> { ["keys"] = "%{TAB}" },
            ct => _services.ComputerControl.SendKeysAsync("%{TAB}", ct));

    private void TaskManager_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("ComputerControl", "SendKeys", new Dictionary<string, object?> { ["keys"] = "^+{ESC}" },
            ct => _services.ComputerControl.SendKeysAsync("^+{ESC}", ct));

    private void ShowDesktop_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("ComputerControl", "SendKeys", new Dictionary<string, object?> { ["keys"] = "^{ESC}d" },
            ct => _services.ComputerControl.SendKeysAsync("^{ESC}d", ct));

    private void Lock_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("System", "Lock", new Dictionary<string, object?>(), ct =>
        {
            SystemPowerActions.Lock();
            return Task.CompletedTask;
        });

    private void Shutdown_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("System", "Shutdown", new Dictionary<string, object?> { ["delaySeconds"] = 30 }, ct =>
        {
            SystemPowerActions.Shutdown(30);
            return Task.CompletedTask;
        });

    private void Restart_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("System", "Restart", new Dictionary<string, object?> { ["delaySeconds"] = 30 }, ct =>
        {
            SystemPowerActions.Restart(30);
            return Task.CompletedTask;
        });

    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemPowerActions.CancelPending();
            StatusText.Text = "Cancelled any pending shutdown/restart.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Nothing to cancel, or failed: {ex.Message}";
        }
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
