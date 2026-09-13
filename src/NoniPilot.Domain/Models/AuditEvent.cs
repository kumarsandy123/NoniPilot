namespace NoniPilot.Domain.Models;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
    RequiresConfirmation,
}

/// <summary>An immutable, tamper-aware record of one action NoniPilot took or refused to take.</summary>
public sealed class AuditEvent
{
    public required string Id { get; init; }
    public string? TaskId { get; init; }

    /// <summary>Who/what caused this: "user", "agent", "administrator-policy".</summary>
    public required string Actor { get; init; }

    public required string Action { get; init; }
    public string? Target { get; init; }
    public required AuditOutcome Outcome { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string MetadataJson { get; init; } = "{}";
}
