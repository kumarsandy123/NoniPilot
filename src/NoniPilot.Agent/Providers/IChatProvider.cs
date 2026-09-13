using System.Text.Json;

namespace NoniPilot.Agent.Providers;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>One turn in a conversation, in a shape every provider (Claude, Groq, Ollama) can be translated to/from.</summary>
public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Text { get; init; }

    /// <summary>Present on an Assistant message that requested tool calls.</summary>
    public List<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>Present on a Tool message: which ToolCall.Id this result answers.</summary>
    public string? ToolCallId { get; init; }
}

public sealed record ChatToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ChatToolDefinition(string Name, string Description, JsonElement ParametersSchema);

public sealed class ChatCompletionResult
{
    public bool HasToolCalls => ToolCalls.Count > 0;
    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = Array.Empty<ChatToolCall>();
    public string? FinalText { get; init; }
    public bool Refused { get; init; }
    public string? RefusalReason { get; init; }
}

/// <summary>
/// Thrown by a provider when it could not complete the request for a reason the router
/// should treat as "try the next provider" rather than "fail the whole task": no
/// connectivity, rate limit/quota exhausted, or auth failure.
/// </summary>
public sealed class ChatProviderUnavailableException(string providerName, string reason, Exception? inner = null)
    : Exception($"{providerName} is unavailable: {reason}", inner)
{
    public string ProviderName { get; } = providerName;
}

/// <summary>
/// One reasoning backend NoniPilot's agent loop can run on. Implementations translate the
/// generic ChatMessage/ChatToolDefinition shape to/from their own wire format - Claude's
/// Messages API, or the OpenAI-compatible chat/completions shape Groq and Ollama both speak.
/// </summary>
public interface IChatProvider
{
    /// <summary>Shown in settings/logs, e.g. "Claude", "Groq", "Local (Ollama)".</summary>
    string Name { get; }

    Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default);
}
