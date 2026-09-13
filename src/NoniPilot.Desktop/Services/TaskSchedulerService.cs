using System.Windows.Threading;
using NoniPilot.Desktop.Models;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Polls saved AutomationSequences for ones whose schedule is due and runs them, entirely
/// locally - no cloud scheduler, nothing leaves this PC. Checks every 30s (schedules are never
/// specified more precisely than a minute, so this is plenty timely without being wasteful).
/// Re-reads AutomationSequenceStore on every tick rather than caching in memory, so a sequence
/// someone just saved/edited/deleted on the Automation page is always picked up on the very next
/// tick, not only after a restart.
/// </summary>
public sealed class TaskSchedulerService : IDisposable
{
    private readonly AutomationSequenceRunner _runner;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _running;

    /// <summary>Fired after a scheduled run (or batch of them) completes, so any open Automation
    /// page can refresh its "next run"/"last run" display without polling itself.</summary>
    public event Action? SequencesChanged;

    public TaskSchedulerService(AutomationSequenceRunner runner)
    {
        _runner = runner;
        _timer.Tick += async (_, _) => await CheckDueSequencesAsync();
        _timer.Start();
    }

    private async Task CheckDueSequencesAsync()
    {
        // A tick firing again before the previous batch finished (e.g. a scheduled sequence
        // itself takes longer than 30s) must not start a second overlapping pass.
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            var sequences = AutomationSequenceStore.Load();
            var now = DateTimeOffset.Now;
            var due = sequences.Where(s => s.Schedule?.Enabled == true && s.NextRunAt.HasValue && s.NextRunAt.Value <= now).ToList();

            if (due.Count == 0)
            {
                return;
            }

            foreach (var sequence in due)
            {
                var success = await _runner.RunAsync(sequence, actor: "scheduler", onEntry: null, onTaskUpdated: null).ConfigureAwait(false);
                sequence.LastRunAt = DateTimeOffset.Now;
                sequence.LastRunSuccess = success;
                sequence.NextRunAt = sequence.Schedule!.ComputeNextRun(DateTimeOffset.Now);
            }

            AutomationSequenceStore.Save(sequences);
            SequencesChanged?.Invoke();
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
