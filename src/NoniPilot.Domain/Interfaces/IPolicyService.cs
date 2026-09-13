using NoniPilot.Domain.Models;

namespace NoniPilot.Domain.Interfaces;

public sealed record PolicyDecision(
    bool Allowed,
    RiskLevel RiskLevel,
    ApprovalMode ApprovalMode,
    string Reason);

/// <summary>
/// Evaluates every action against the risk model (section 9) and any administrator
/// PermissionPolicy overrides, BEFORE it is allowed to execute. This is the one gate
/// every execution engine (ComputerControl, FileSystem, Applications, Browser) must
/// call through - the AI planner is never trusted to enforce this itself.
/// </summary>
public interface IPolicyService
{
    /// <param name="tool">e.g. "FileSystem", "ComputerControl", "Application".</param>
    /// <param name="action">e.g. "Delete", "Click", "Launch".</param>
    /// <param name="parameters">Raw action parameters, used for path/app-restriction matching.</param>
    PolicyDecision Evaluate(string tool, string action, IReadOnlyDictionary<string, object?> parameters);
}

/// <summary>
/// Owned by the presentation layer (NoniPilot.Desktop) and injected into the agent loop,
/// so a PolicyDecision that requires confirmation can actually ask a human before proceeding.
/// </summary>
public interface IUserConfirmationService
{
    Task<bool> ConfirmAsync(
        string tool,
        string action,
        string humanReadableSummary,
        RiskLevel riskLevel,
        CancellationToken cancellationToken = default);
}
