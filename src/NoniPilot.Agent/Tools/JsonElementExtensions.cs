using System.Text.Json;

namespace NoniPilot.Agent.Tools;

internal static class JsonElementExtensions
{
    public static string GetRequiredString(this IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"Required string parameter '{key}' was missing.");

    public static string? GetOptionalString(this IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int GetRequiredInt(this IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) ? value.GetInt32() : throw new ArgumentException($"Required int parameter '{key}' was missing.");

    public static int GetOptionalInt(this IReadOnlyDictionary<string, JsonElement> input, string key, int fallback) =>
        input.TryGetValue(key, out var value) ? value.GetInt32() : fallback;

    public static long? GetOptionalLong(this IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) ? value.GetInt64() : null;

    public static bool GetOptionalBool(this IReadOnlyDictionary<string, JsonElement> input, string key, bool fallback) =>
        input.TryGetValue(key, out var value) ? value.GetBoolean() : fallback;

    /// <summary>Converts a JSON object element (a provider's tool-call arguments) into the keyed lookup ToolSpec.ExecuteAsync expects.</summary>
    public static IReadOnlyDictionary<string, JsonElement> AsDictionary(this JsonElement arguments)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }
        }
        return dict;
    }

    /// <summary>Flattens tool input into the loosely-typed bag IPolicyService.Evaluate expects.</summary>
    public static IReadOnlyDictionary<string, object?> ToPolicyParameters(this IReadOnlyDictionary<string, JsonElement> input)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in input)
        {
            result[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => value.ToString(),
            };
        }

        return result;
    }
}
