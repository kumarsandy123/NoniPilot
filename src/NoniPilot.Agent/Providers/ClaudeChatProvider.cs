using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace NoniPilot.Agent.Providers;

/// <summary>The optional, opt-in, paid provider (section "Cost Protection": disabled by default, never auto-enabled).</summary>
public sealed class ClaudeChatProvider : IChatProvider
{
    private const string ClaudeModel = "claude-opus-5";
    private readonly AnthropicClient _client;

    public string Name => "Claude";

    public ClaudeChatProvider(AnthropicClient? client = null)
    {
        _client = client ?? new AnthropicClient();
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        Message response;
        try
        {
            response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = ClaudeModel,
                MaxTokens = 8000,
                System = systemPrompt,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.High },
                Tools = tools.Select(t => (ToolUnion)ToAnthropicTool(t)).ToList(),
                Messages = ToAnthropicMessages(messages),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ChatProviderUnavailableException(Name, ex.Message, ex);
        }

        if (response.StopReason == "refusal")
        {
            return new ChatCompletionResult
            {
                Refused = true,
                RefusalReason = $"{response.StopDetails?.Category}: {response.StopDetails?.Explanation}",
            };
        }

        string? text = null;
        var toolCalls = new List<ChatToolCall>();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                text = t.Text;
            }
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                toolCalls.Add(new ChatToolCall(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
            }
        }

        return new ChatCompletionResult { ToolCalls = toolCalls, FinalText = text };
    }

    private static List<MessageParam> ToAnthropicMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<MessageParam>();
        var i = 0;

        while (i < messages.Count)
        {
            var m = messages[i];

            if (m.Role == ChatRole.User)
            {
                result.Add(new MessageParam { Role = Role.User, Content = m.Text ?? string.Empty });
                i++;
                continue;
            }

            if (m.Role == ChatRole.Assistant)
            {
                List<ContentBlockParam> assistantContent = new();
                if (!string.IsNullOrEmpty(m.Text))
                {
                    assistantContent.Add(new TextBlockParam { Text = m.Text });
                }

                if (m.ToolCalls is { Count: > 0 })
                {
                    foreach (var call in m.ToolCalls)
                    {
                        assistantContent.Add(new ToolUseBlockParam
                        {
                            ID = call.Id,
                            Name = call.Name,
                            Input = JsonElementToDictionary(call.Arguments),
                        });
                    }
                }

                result.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                i++;
                continue;
            }

            // ChatRole.Tool: Claude requires every tool_result for one turn in a single user
            // message, unlike the OpenAI-compatible shape which allows one message per result.
            List<ContentBlockParam> toolResults = new();
            while (i < messages.Count && messages[i].Role == ChatRole.Tool)
            {
                toolResults.Add(new ToolResultBlockParam { ToolUseID = messages[i].ToolCallId!, Content = messages[i].Text ?? string.Empty });
                i++;
            }
            result.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        return result;
    }

    private static Tool ToAnthropicTool(ChatToolDefinition def)
    {
        var root = def.ParametersSchema;
        var properties = new Dictionary<string, JsonElement>();
        List<string> required = new();

        if (root.TryGetProperty("properties", out var propsElement))
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                properties[prop.Name] = prop.Value;
            }
        }

        if (root.TryGetProperty("required", out var reqElement) && reqElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqElement.EnumerateArray())
            {
                required.Add(r.GetString()!);
            }
        }

        return new Tool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Dictionary<string, JsonElement> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }
        }
        return dict;
    }
}
