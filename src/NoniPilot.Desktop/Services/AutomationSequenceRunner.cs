using System.Text.Json;
using NoniPilot.Desktop.Models;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Runs an AutomationSequence's commands in order through the shared planner - the one place
/// this happens, used by both AutomationPage's manual "Run" button and TaskSchedulerService's
/// background runs, so a scheduled run is reported and gated exactly like a manual one (not a
/// second, slightly-different code path). Guards every run with AppServices.PlannerGate: a
/// scheduled sequence firing while the user is actively chatting (or two overlapping scheduled
/// runs) would otherwise both mutate ToolCallingPlannerService's shared conversation history
/// concurrently - a real risk that didn't meaningfully exist before real time-based scheduling
/// did, since a manual Run click was inherently user-paced.
/// </summary>
public sealed class AutomationSequenceRunner
{
    private readonly AppServices _services;

    public AutomationSequenceRunner(AppServices services) => _services = services;

    public async Task<bool> RunAsync(
        AutomationSequence sequence,
        string actor,
        Action<ChatEntry>? onEntry,
        Action<AgentTask>? onTaskUpdated,
        CancellationToken cancellationToken = default)
    {
        var overallSuccess = true;
        var stepSummaries = new List<string>();

        await _services.PlannerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var command in sequence.Commands)
            {
                onEntry?.Invoke(new ChatEntry { Kind = ChatEntryKind.User, Text = command });

                try
                {
                    var task = await _services.Planner.ExecuteAsync(command, onTaskUpdated, cancellationToken).ConfigureAwait(false);
                    var resultText = task.Result ?? task.ErrorMessage ?? "(no result)";
                    var kind = task.Result is not null ? ChatEntryKind.Assistant : ChatEntryKind.Error;

                    onEntry?.Invoke(new ChatEntry { Kind = kind, Text = resultText });
                    stepSummaries.Add(resultText);

                    if (task.Result is null)
                    {
                        overallSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    onEntry?.Invoke(new ChatEntry { Kind = ChatEntryKind.Error, Text = $"Unexpected error: {ex.Message}" });
                    stepSummaries.Add($"Error: {ex.Message}");
                    overallSuccess = false;
                    break;
                }
            }
        }
        finally
        {
            _services.PlannerGate.Release();
        }

        await _services.Audit.RecordAsync(new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            Actor = actor,
            Action = "AutomationSequence.Run",
            Target = sequence.Name,
            Outcome = overallSuccess ? AuditOutcome.Success : AuditOutcome.Failure,
            MetadataJson = JsonSerializer.Serialize(new { stepCount = sequence.Commands.Count, steps = stepSummaries }),
        }, cancellationToken).ConfigureAwait(false);

        return overallSuccess;
    }
}
