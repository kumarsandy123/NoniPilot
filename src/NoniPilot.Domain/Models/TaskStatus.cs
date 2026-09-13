namespace NoniPilot.Domain.Models;

public enum AgentTaskStatus
{
    Pending,
    Planning,
    Running,
    WaitingForUser,
    Verified,
    Failed,
    Cancelled,
}

public enum TaskStepStatus
{
    Pending,
    Running,
    Verified,
    Failed,
    WaitingForUser,
    Skipped,
}
