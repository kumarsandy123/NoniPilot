using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NoniPilot.Agent.Providers;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint. Groq
/// (https://api.groq.com/openai/v1) and a local Ollama server
/// (http://localhost:11434/v1) both implement this same wire shape, so one HTTP client
/// covers both the "free cloud" and "fully local" options - only base URL, API key and
/// model name differ.
/// </summary>
public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    public string Name { get; }

    public OpenAiCompatibleChatProvider(string name, string baseUrl, string model, string? apiKey = null, TimeSpan? timeout = null)
    {
        Name = name;
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var wireMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
        };

        foreach (var m in messages)
        {
            wireMessages.Add(ToWireMessage(m));
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = wireMessages,
            ["tool_choice"] = "auto",
        };

        if (tools.Count > 0)
        {
            var wireTools = new JsonArray();
            foreach (var t in tools)
            {
                wireTools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.ParametersSchema.GetRawText()),
                    },
                });
            }
            body["tools"] = wireTools;
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderUnavailableException(Name, "could not reach the server (no connectivity?)", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderUnavailableException(Name, "request timed out", ex);
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new ChatProviderUnavailableException(Name, "rate limit / free quota exhausted");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ChatProviderUnavailableException(Name, "authentication rejected - check the API key");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ChatProviderUnavailableException(Name, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        var choice = json?["choices"]?[0]?["message"] ?? throw new ChatProviderUnavailableException(Name, "response had no choices[0].message");

        var text = choice["content"]?.GetValue<string?>();
        var toolCalls = new List<ChatToolCall>();

        if (choice["tool_calls"] is JsonArray toolCallsArray)
        {
            foreach (var tc in toolCallsArray)
            {
                var id = tc!["id"]!.GetValue<string>();
                var fnName = tc["function"]!["name"]!.GetValue<string>();
                var argsString = tc["function"]!["arguments"]!.GetValue<string>();
                var argsElement = string.IsNullOrWhiteSpace(argsString)
                    ? JsonDocument.Parse("{}").RootElement
                    : JsonDocument.Parse(argsString).RootElement;
                toolCalls.Add(new ChatToolCall(id, fnName, argsElement));
            }
        }

        // Some models (observed live: qwen2.5 via Ollama) sometimes emit a tool call as literal
        // Hermes-style text - "<tool_call>{\"name\":...,\"arguments\":{...}}</tool_call>" -
        // inside the plain content field instead of the structured tool_calls field Ollama's
        // OpenAI-compatibility layer is supposed to translate it into. Left unhandled, that text
        // was just displayed/spoken verbatim as if it were the model's answer, and the intended
        // action never actually ran - a real, user-visible "it says it did something but didn't"
        // bug. Only checked when the structured field came back empty, so a model that already
        // used it correctly is never second-guessed.
        if (toolCalls.Count == 0 && text is not null)
        {
            text = ExtractInlineToolCalls(text, toolCalls);
        }

        return new ChatCompletionResult
        {
            ToolCalls = toolCalls,
            FinalText = text,
        };
    }

    private static readonly Regex InlineToolCallPattern = new(
        @"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <returns>The original text with any recognized inline tool-call blocks removed, leaving
    /// only genuine surrounding prose (if any) - the blocks themselves are appended to
    /// <paramref name="toolCalls"/> instead of being left in for the user to see/hear.</returns>
    private static string ExtractInlineToolCalls(string text, List<ChatToolCall> toolCalls)
    {
        var matches = InlineToolCallPattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        foreach (Match match in matches)
        {
            try
            {
                var root = JsonDocument.Parse(match.Groups[1].Value).RootElement;
                var name = root.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var arguments = root.TryGetProperty("arguments", out var argsProp)
                    ? (argsProp.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(argsProp.GetString()!).RootElement
                        : JsonDocument.Parse(argsProp.GetRawText()).RootElement)
                    : JsonDocument.Parse("{}").RootElement;

                toolCalls.Add(new ChatToolCall(Guid.NewGuid().ToString("N"), name, arguments));
            }
            catch (JsonException)
            {
                // A malformed inline block shouldn't crash the whole response - skip it and
                // let whatever real prose remains still get shown/spoken.
            }
        }

        return toolCalls.Count == 0 ? text : InlineToolCallPattern.Replace(text, string.Empty).Trim();
    }

    private static JsonObject ToWireMessage(ChatMessage message)
    {
        var role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => "user",
        };

        var obj = new JsonObject { ["role"] = role };

        if (message.Role == ChatRole.Tool)
        {
            obj["tool_call_id"] = message.ToolCallId;
            obj["content"] = message.Text ?? string.Empty;
            return obj;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            obj["content"] = message.Text is null ? null : JsonValue.Create(message.Text);
            var wireCalls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                wireCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.GetRawText(),
                    },
                });
            }
            obj["tool_calls"] = wireCalls;
            return obj;
        }

        obj["content"] = message.Text ?? string.Empty;
        return obj;
    }
}
