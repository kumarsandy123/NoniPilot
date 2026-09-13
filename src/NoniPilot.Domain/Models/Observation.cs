namespace NoniPilot.Domain.Models;

/// <summary>A snapshot of desktop state captured by the perception layer before/after an action.</summary>
public sealed class Observation
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ActiveWindowTitle { get; init; }
    public string? ActiveProcessName { get; init; }
    public int MonitorCount { get; init; } = 1;

    /// <summary>Serialized UI Automation / OCR / vision detections, shape left to the vision engine (Phase 2+).</summary>
    public string DetectedElementsJson { get; init; } = "[]";
}
