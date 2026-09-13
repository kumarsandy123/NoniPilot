using System.Text.Json.Serialization;
using System.Windows;

namespace NoniPilot.Desktop.Models;

/// <summary>
/// A named list of natural-language commands, replayed in order through the existing
/// IPlannerService - deliberately not a low-level keystroke/mouse macro recorder, just a
/// saved sequence of the same commands you could type one at a time. Optionally scheduled to
/// run itself (TaskSchedulerService), instead of only ever being triggered by clicking Run.
/// </summary>
public sealed class AutomationSequence
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<string> Commands { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ScheduleSpec? Schedule { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool? LastRunSuccess { get; set; }

    // Computed display-only properties for AutomationPage's list binding - deliberately not
    // persisted (JsonIgnore), since they're derived fresh from the fields above every time.
    [JsonIgnore]
    public Visibility HasSchedule => Schedule is not null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string ScheduleToggleLabel => Schedule?.Enabled == true ? "Pause" : "Resume";

    [JsonIgnore]
    public string ScheduleSummary
    {
        get
        {
            if (Schedule is null)
            {
                var lastRun = LastRunAt.HasValue
                    ? $"Last run: {LastRunAt.Value.LocalDateTime:g} ({(LastRunSuccess == true ? "success" : "failed")})"
                    : "Not scheduled.";
                return lastRun;
            }

            var parts = new List<string> { Schedule.Describe() };
            parts.Add(Schedule.Enabled ? "Active" : "Paused");

            if (Schedule.Enabled && NextRunAt.HasValue)
            {
                parts.Add($"Next run: {NextRunAt.Value.LocalDateTime:g}");
            }

            if (LastRunAt.HasValue)
            {
                parts.Add($"Last run: {LastRunAt.Value.LocalDateTime:g} ({(LastRunSuccess == true ? "success" : "failed")})");
            }

            return string.Join(" · ", parts);
        }
    }
}
