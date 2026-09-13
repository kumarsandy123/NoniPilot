namespace NoniPilot.Agent.Providers;

/// <summary>
/// Tries each configured provider in priority order on every request, falling through to
/// the next on connectivity/quota/auth failure. This is the "no internet -> local", "quota
/// exhausted -> local" behavior: it re-evaluates per request, so a mid-task quota exhaustion
/// falls back immediately rather than requiring a restart.
/// </summary>
public sealed class ChatProviderRouter : IChatProvider
{
    private readonly IReadOnlyList<IChatProvider> _providers;

    public ChatProviderRouter(IReadOnlyList<IChatProvider> providers)
    {
        _providers = providers;
    }

    public string Name => _providers.Count == 0 ? "(none configured)" : string.Join(" -> ", _providers.Select(p => p.Name));

    /// <summary>Raised whenever a provider failed and the router is trying the next one - wire this to a status bar.</summary>
    public event Action<string, string>? ProviderFellBack;

    public async Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            throw new ChatProviderUnavailableException(
                "Router",
                "no AI provider is enabled. Enable Local AI (install Ollama) or add a free Groq API key in Settings.");
        }

        Exception? lastError = null;
        var failures = new List<string>();

        foreach (var provider in _providers)
        {
            try
            {
                return await provider.CompleteAsync(systemPrompt, messages, tools, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatProviderUnavailableException ex)
            {
                lastError = ex;
                failures.Add(ex.Message); // ex.Message already starts with "{provider.Name} is unavailable: ..."
                ProviderFellBack?.Invoke(provider.Name, ex.Message);
            }
        }

        // Every provider's actual reason goes in the message itself - not just the transient
        // status-bar event - so it's still visible in the chat transcript afterwards, not lost
        // the moment the next provider's attempt overwrites the status text.
        throw new ChatProviderUnavailableException("Router", string.Join("  |  ", failures), lastError);
    }
}
