using NoniPilot.Domain.Models;

namespace NoniPilot.Domain.Interfaces;

/// <summary>
/// The Agent core (section 6/14): turns one natural-language command into a running,
/// policy-gated, verified AgentTask. This is the Observe -> Plan -> Act -> Verify -> Recover
/// loop, and it is the only interface the presentation layer talks to for a user command.
/// </summary>
public interface IPlannerService
{
    Task<AgentTask> ExecuteAsync(
        string naturalLanguageCommand,
        Action<AgentTask>? onTaskUpdated = null,
        CancellationToken cancellationToken = default);
}
