namespace NoniPilot.Desktop.Models;

public enum ScheduleRecurrence
{
    Once,
    Daily,
    Weekly,
    EveryNMinutes,
}

/// <summary>
/// When an AutomationSequence should run itself, without the user manually clicking Run - added
/// per explicit request ("schedule any task... system related or web related"). Deliberately a
/// handful of simple recurrence shapes (not a full cron expression parser) - covers "at 9pm
/// tonight", "every day at 8am", "every Monday at 6pm", and "every 30 minutes" (e.g. checking a
/// website periodically), which is what real task-scheduling requests actually look like.
/// </summary>
public sealed class ScheduleSpec
{
    public required ScheduleRecurrence Recurrence { get; init; }
    public bool Enabled { get; set; } = true;

    /// <summary>Once only: the exact date/time to run.</summary>
    public DateTimeOffset? OnceAt { get; init; }

    /// <summary>Daily/Weekly only: local time-of-day to run.</summary>
    public TimeSpan? TimeOfDay { get; init; }

    /// <summary>Weekly only.</summary>
    public DayOfWeek? DayOfWeek { get; init; }

    /// <summary>EveryNMinutes only.</summary>
    public int? IntervalMinutes { get; init; }

    /// <returns>
    /// The next time this should run strictly after <paramref name="from"/>, or null if this
    /// schedule will never run again (a Once schedule whose time has already passed, or a
    /// malformed spec missing a required field for its recurrence type).
    /// </returns>
    public DateTimeOffset? ComputeNextRun(DateTimeOffset from)
    {
        switch (Recurrence)
        {
            case ScheduleRecurrence.Once:
                return OnceAt.HasValue && OnceAt.Value > from ? OnceAt : null;

            case ScheduleRecurrence.Daily:
            {
                if (!TimeOfDay.HasValue)
                {
                    return null;
                }

                var candidate = new DateTimeOffset(from.Date, from.Offset) + TimeOfDay.Value;
                if (candidate <= from)
                {
                    candidate = candidate.AddDays(1);
                }

                return candidate;
            }

            case ScheduleRecurrence.Weekly:
            {
                if (!TimeOfDay.HasValue || !DayOfWeek.HasValue)
                {
                    return null;
                }

                var candidate = new DateTimeOffset(from.Date, from.Offset) + TimeOfDay.Value;
                var daysUntil = ((int)DayOfWeek.Value - (int)from.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(daysUntil);
                if (candidate <= from)
                {
                    candidate = candidate.AddDays(7);
                }

                return candidate;
            }

            case ScheduleRecurrence.EveryNMinutes:
                return IntervalMinutes is > 0 ? from.AddMinutes(IntervalMinutes.Value) : null;

            default:
                return null;
        }
    }

    public string Describe()
    {
        return Recurrence switch
        {
            ScheduleRecurrence.Once => OnceAt.HasValue ? $"Once at {OnceAt.Value.LocalDateTime:g}" : "Once (no time set)",
            ScheduleRecurrence.Daily => TimeOfDay.HasValue ? $"Daily at {FormatTime(TimeOfDay.Value)}" : "Daily (no time set)",
            ScheduleRecurrence.Weekly => TimeOfDay.HasValue && DayOfWeek.HasValue
                ? $"Every {DayOfWeek.Value} at {FormatTime(TimeOfDay.Value)}"
                : "Weekly (incomplete)",
            ScheduleRecurrence.EveryNMinutes => IntervalMinutes.HasValue ? $"Every {IntervalMinutes.Value} min" : "Interval (no minutes set)",
            _ => "Unknown schedule",
        };
    }

    private static string FormatTime(TimeSpan timeOfDay) => DateTime.Today.Add(timeOfDay).ToString("h:mm tt");
}
