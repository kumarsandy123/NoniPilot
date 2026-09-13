using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NoniPilot.Desktop.Models;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop.Pages;

/// <summary>One row in the Task Reports list - built from an AuditEvent, not persisted separately.</summary>
public sealed class AutomationRunReportRow
{
    public required string TitleLine { get; init; }
    public required string DetailLine { get; init; }
    public required string OutcomeLabel { get; init; }
    public required Brush OutcomeColor { get; init; }
}

public partial class AutomationPage : UserControl
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ChatEntry> _runLog = new();
    private readonly ObservableCollection<AutomationRunReportRow> _report = new();
    private List<AutomationSequence> _sequences;
    private readonly Dictionary<string, int> _stepEntryIndex = new();

    public AutomationPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _sequences = AutomationSequenceStore.Load();
        SequenceList.ItemsSource = _sequences;
        RunLog.ItemsSource = _runLog;
        ReportList.ItemsSource = _report;

        _services.Scheduler.SequencesChanged += OnSequencesChangedByScheduler;

        _ = LoadReportAsync();
    }

    private void OnSequencesChangedByScheduler() => Dispatcher.Invoke(() =>
    {
        _sequences = AutomationSequenceStore.Load();
        SequenceList.ItemsSource = null;
        SequenceList.ItemsSource = _sequences;
        _ = LoadReportAsync();
    });

    private void ScheduleTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OncePanel is null)
        {
            return; // fires once during InitializeComponent before the other panels exist yet
        }

        var index = ScheduleTypeCombo.SelectedIndex;
        OncePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DailyPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        WeeklyPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool TryParseTimeOfDay(string text, out TimeSpan timeOfDay) =>
        TimeSpan.TryParse(text.Trim(), out timeOfDay) && timeOfDay >= TimeSpan.Zero && timeOfDay < TimeSpan.FromDays(1);

    /// <summary>
    /// Builds a ScheduleSpec from the New Sequence form's controls, or null if "None" is
    /// selected. Returns a human-readable error instead of throwing if the selected recurrence's
    /// required fields are missing/invalid, so Save_Click can show it rather than silently
    /// saving a schedule that will never actually fire.
    /// </summary>
    private (ScheduleSpec? Schedule, string? Error) BuildScheduleFromForm()
    {
        switch (ScheduleTypeCombo.SelectedIndex)
        {
            case 0:
                return (null, null);

            case 1: // Once
                if (OnceDatePicker.SelectedDate is not { } date || !TryParseTimeOfDay(OnceTimeBox.Text, out var onceTime))
                {
                    return (null, "Pick a date and enter a valid time (HH:mm) for the one-time schedule.");
                }

                var onceAt = new DateTimeOffset(date.Date + onceTime, DateTimeOffset.Now.Offset);
                if (onceAt <= DateTimeOffset.Now)
                {
                    return (null, "That one-time date/time is already in the past.");
                }

                return (new ScheduleSpec { Recurrence = ScheduleRecurrence.Once, OnceAt = onceAt }, null);

            case 2: // Daily
                if (!TryParseTimeOfDay(DailyTimeBox.Text, out var dailyTime))
                {
                    return (null, "Enter a valid daily time (HH:mm).");
                }

                return (new ScheduleSpec { Recurrence = ScheduleRecurrence.Daily, TimeOfDay = dailyTime }, null);

            case 3: // Weekly
                if (!TryParseTimeOfDay(WeeklyTimeBox.Text, out var weeklyTime))
                {
                    return (null, "Enter a valid weekly time (HH:mm).");
                }

                var day = (DayOfWeek)WeeklyDayCombo.SelectedIndex;
                return (new ScheduleSpec { Recurrence = ScheduleRecurrence.Weekly, TimeOfDay = weeklyTime, DayOfWeek = day }, null);

            case 4: // Every N minutes
                if (!int.TryParse(IntervalMinutesBox.Text.Trim(), out var minutes) || minutes <= 0)
                {
                    return (null, "Enter a positive whole number of minutes.");
                }

                return (new ScheduleSpec { Recurrence = ScheduleRecurrence.EveryNMinutes, IntervalMinutes = minutes }, null);

            default:
                return (null, null);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var commands = CommandsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (string.IsNullOrWhiteSpace(name) || commands.Count == 0)
        {
            StatusText.Text = "Enter a name and at least one command.";
            return;
        }

        var (schedule, scheduleError) = BuildScheduleFromForm();
        if (scheduleError is not null)
        {
            StatusText.Text = scheduleError;
            return;
        }

        var sequence = new AutomationSequence
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Commands = commands,
            Schedule = schedule,
            NextRunAt = schedule?.ComputeNextRun(DateTimeOffset.Now),
        };

        _sequences.Add(sequence);
        AutomationSequenceStore.Save(_sequences);
        SequenceList.ItemsSource = null;
        SequenceList.ItemsSource = _sequences;
        NameBox.Clear();
        CommandsBox.Clear();
        ScheduleTypeCombo.SelectedIndex = 0;

        StatusText.Text = schedule is null
            ? $"Saved '{name}' with {commands.Count} step(s)."
            : $"Saved '{name}' with {commands.Count} step(s) - {schedule.Describe()}, next run {sequence.NextRunAt:g}.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AutomationSequence sequence)
        {
            return;
        }

        _sequences = _sequences.Where(s => s.Id != sequence.Id).ToList();
        AutomationSequenceStore.Save(_sequences);
        SequenceList.ItemsSource = null;
        SequenceList.ItemsSource = _sequences;
    }

    private void ToggleSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AutomationSequence sequence || sequence.Schedule is null)
        {
            return;
        }

        sequence.Schedule.Enabled = !sequence.Schedule.Enabled;
        sequence.NextRunAt = sequence.Schedule.Enabled ? sequence.Schedule.ComputeNextRun(DateTimeOffset.Now) : null;

        AutomationSequenceStore.Save(_sequences);
        SequenceList.ItemsSource = null;
        SequenceList.ItemsSource = _sequences;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AutomationSequence sequence)
        {
            return;
        }

        _runLog.Clear();
        _stepEntryIndex.Clear();
        StatusText.Text = $"Running '{sequence.Name}'...";

        await _services.AutomationRunner.RunAsync(sequence, actor: "user", onEntry: _runLog.Add, onTaskUpdated: OnTaskUpdated);

        StatusText.Text = $"Finished '{sequence.Name}'.";
        await LoadReportAsync();
    }

    private void OnTaskUpdated(AgentTask task) => Dispatcher.Invoke(() =>
    {
        foreach (var step in task.Steps)
        {
            var text = $"[{step.Status}] {step.Tool}.{step.Action}  (risk: {step.RiskLevel})" +
                (step.ErrorMessage is null ? string.Empty : $"  - {step.ErrorMessage}");
            var entry = new ChatEntry { Kind = ChatEntryKind.SystemStep, Text = text };

            if (_stepEntryIndex.TryGetValue(step.Id, out var index))
            {
                _runLog[index] = entry;
            }
            else
            {
                _runLog.Add(entry);
                _stepEntryIndex[step.Id] = _runLog.Count - 1;
            }
        }
    });

    private async void RefreshReport_Click(object sender, RoutedEventArgs e) => await LoadReportAsync();

    /// <summary>
    /// Every automation run (manual or scheduled) was recorded by AutomationSequenceRunner as an
    /// AuditEvent with Action "AutomationSequence.Run" - this is the "complete reporting of every
    /// task" surface, read back from the same durable audit trail everything else in the app
    /// already flows through, not a second parallel log.
    /// </summary>
    private async Task LoadReportAsync()
    {
        IReadOnlyList<AuditEvent> events;
        try
        {
            events = await _services.Audit.QueryAsync(limit: 500);
        }
        catch
        {
            return;
        }

        var rows = events
            .Where(ev => ev.Action == "AutomationSequence.Run")
            .OrderByDescending(ev => ev.Timestamp)
            .Take(50)
            .Select(ev =>
            {
                var actorLabel = ev.Actor == "scheduler" ? "Scheduled" : "Manual";
                var success = ev.Outcome == AuditOutcome.Success;
                return new AutomationRunReportRow
                {
                    TitleLine = $"{ev.Target} - {actorLabel}",
                    DetailLine = $"{ev.Timestamp.LocalDateTime:g}",
                    OutcomeLabel = success ? "SUCCESS" : "FAILED",
                    OutcomeColor = success ? Brushes.LimeGreen : Brushes.OrangeRed,
                };
            })
            .ToList();

        Dispatcher.Invoke(() =>
        {
            _report.Clear();
            foreach (var row in rows)
            {
                _report.Add(row);
            }

            ReportEmptyText.Visibility = _report.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }
}
