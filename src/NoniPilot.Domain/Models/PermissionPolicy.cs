namespace NoniPilot.Domain.Models;

public enum ApprovalMode
{
    /// <summary>Runs without asking, subject to risk-level defaults.</summary>
    AutoAllow,

    /// <summary>Always show the confirmation dialog before running.</summary>
    AlwaysConfirm,

    /// <summary>Never allowed, regardless of who asks.</summary>
    Deny,
}

/// <summary>
/// An administrator/user-configured override for how a scope of actions is gated.
/// The built-in RiskLevel defaults (see PolicyService) apply when no PermissionPolicy overrides them.
/// </summary>
public sealed class PermissionPolicy
{
    public required string Id { get; init; }

    /// <summary>What this policy applies to, e.g. "filesystem:delete", "app:launch", "*".</summary>
    public required string Scope { get; init; }

    public RiskLevel RiskLevel { get; init; }
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.AlwaysConfirm;

    /// <summary>Path or application patterns this policy restricts, e.g. "C:\Windows\**".</summary>
    public List<string> PathOrAppRestrictions { get; init; } = new();
}
