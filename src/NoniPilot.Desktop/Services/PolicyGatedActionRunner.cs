using System.Text.Json;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Generalizes ToolCallingPlannerService.ExecuteToolAsync's evaluate -> confirm -> execute ->
/// audit sequence for page buttons that call a service directly instead of going through the
/// AI planner - every privileged action in this app is required to flow through this same
/// gate, whether the AI or a direct button click triggered it.
/// </summary>
public sealed class PolicyGatedActionRunner
{
    private readonly IPolicyService _policy;
    private readonly IUserConfirmationService _confirmation;
    private readonly IAuditService _audit;

    public PolicyGatedActionRunner(IPolicyService policy, IUserConfirmationService confirmation, IAuditService audit)
    {
        _policy = policy;
        _confirmation = confirmation;
        _audit = audit;
    }

    /// <returns>Whether the action ran, and a human-readable outcome/denial reason suitable
    /// for showing directly in a status label.</returns>
    public async Task<(bool Success, string Message)> RunAsync(
        string tool,
        string action,
        IReadOnlyDictionary<string, object?> parameters,
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        var decision = _policy.Evaluate(tool, action, parameters);

        if (!decision.Allowed)
        {
            await AuditAsync(tool, action, parameters, AuditOutcome.Denied, decision.Reason, cancellationToken);
            return (false, decision.Reason);
        }

        if (decision.ApprovalMode == ApprovalMode.AlwaysConfirm)
        {
            var summary = $"{tool}.{action} with parameters {JsonSerializer.Serialize(parameters)}";
            var confirmed = await _confirmation.ConfirmAsync(tool, action, summary, decision.RiskLevel, cancellationToken);

            if (!confirmed)
            {
                await AuditAsync(tool, action, parameters, AuditOutcome.Denied, "User declined confirmation.", cancellationToken);
                return (false, "Cancelled - you declined the confirmation.");
            }
        }

        try
        {
            await execute(cancellationToken);
            await AuditAsync(tool, action, parameters, AuditOutcome.Success, "Completed.", cancellationToken);
            return (true, "Done.");
        }
        catch (Exception ex)
        {
            await AuditAsync(tool, action, parameters, AuditOutcome.Failure, ex.Message, cancellationToken);
            return (false, ex.Message);
        }
    }

    private Task AuditAsync(
        string tool, string action, IReadOnlyDictionary<string, object?> parameters, AuditOutcome outcome, string reason, CancellationToken ct) =>
        _audit.RecordAsync(new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            Actor = "user",
            Action = $"{tool}.{action}",
            Target = JsonSerializer.Serialize(parameters),
            Outcome = outcome,
            MetadataJson = JsonSerializer.Serialize(new { reason }),
        }, ct);
}
