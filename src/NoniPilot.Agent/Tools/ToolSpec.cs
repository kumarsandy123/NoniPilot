using System.Text.Json;
using NoniPilot.Agent.Providers;

namespace NoniPilot.Agent.Tools;

/// <summary>
/// One entry in the tool catalog the agent loop is given: a provider-agnostic tool
/// definition (works for Claude, Groq, or Ollama - see NoniPilot.Agent.Providers), the
/// policy scope it maps to, and the delegate that performs the action against a real
/// NoniPilot service. Keeping this as data (rather than a big switch per tool) is what
/// makes "map every tool 1:1 onto our own service interfaces" tractable.
/// </summary>
public sealed class ToolSpec
{
    public required string ClaudeName { get; init; }
    public required string PolicyTool { get; init; }
    public required string PolicyAction { get; init; }
    public required ChatToolDefinition Definition { get; init; }
    public required Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<object?>> ExecuteAsync { get; init; }

    public static JsonElement Schema(object shape) => JsonSerializer.SerializeToElement(shape);
}
