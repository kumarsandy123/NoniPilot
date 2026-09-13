namespace NoniPilot.Desktop;

public enum ChatEntryKind
{
    User,
    Assistant,
    SystemStep,
    Error,
}

/// <summary>One row in the chat transcript - the "complete chatting history" view, not just the latest result.</summary>
public sealed class ChatEntry
{
    public required ChatEntryKind Kind { get; init; }
    public required string Text { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string TimeLabel => Timestamp.ToString("HH:mm");
}
