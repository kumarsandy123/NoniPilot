using System.Windows;
using System.Windows.Controls;
using NoniPilot.Desktop.Controls;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop.Pages;

/// <summary>
/// Pure new UI over IAuditService.QueryAsync - confirmed nowhere else in the codebase calls it,
/// even though every action has always been recorded to the SQLite audit log.
/// </summary>
public partial class TaskHistoryPage : UserControl, INavigablePage
{
    private readonly AppServices _services;

    private sealed record EventRow(string TimeLabel, string Actor, string Action, string? Target, string Outcome, StatusPillState OutcomeState);

    public TaskHistoryPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    public void OnNavigatedTo() => _ = RefreshAsync(null);

    public void OnNavigatedFrom() { }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(null);

    private void Filter_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshAsync(string.IsNullOrWhiteSpace(TaskIdFilterBox.Text) ? null : TaskIdFilterBox.Text.Trim());

    private async Task RefreshAsync(string? taskId)
    {
        var events = await _services.Audit.QueryAsync(taskId, limit: 200);
        EventsList.ItemsSource = events.Select(ToRow).ToList();
    }

    private static EventRow ToRow(AuditEvent e) => new(
        e.Timestamp.ToLocalTime().ToString("g"),
        e.Actor,
        e.Action,
        e.Target,
        e.Outcome.ToString(),
        e.Outcome switch
        {
            AuditOutcome.Success => StatusPillState.Connected,
            AuditOutcome.Failure => StatusPillState.Disconnected,
            AuditOutcome.Denied => StatusPillState.Warning,
            _ => StatusPillState.Info,
        });
}
