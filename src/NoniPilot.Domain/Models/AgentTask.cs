namespace NoniPilot.Domain.Models;

/// <summary>
/// One user-issued command and everything that happened while NoniPilot carried it out.
/// Named AgentTask (not Task) to avoid colliding with System.Threading.Tasks.Task.
/// </summary>
public sealed class AgentTask
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public string? Intent { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public RiskLevel RiskLevel { get; set; } = RiskLevel.L0Safe;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public List<TaskStep> Steps { get; init; } = new();
}

public sealed class TaskStep
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required int Sequence { get; init; }

    /// <summary>Which service handled this step, e.g. "FileSystem", "ComputerControl", "Application".</summary>
    public required string Tool { get; init; }

    /// <summary>The specific action requested on that tool, e.g. "MoveFile", "Click".</summary>
    public required string Action { get; init; }

    public string ParametersJson { get; init; } = "{}";
    public TaskStepStatus Status { get; set; } = TaskStepStatus.Pending;
    public RiskLevel RiskLevel { get; set; } = RiskLevel.L0Safe;

    /// <summary>0.0-1.0 confidence the planner/verifier assigned to this step's target or outcome.</summary>
    public double? Confidence { get; set; }

    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
