using System.Net.Http;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// A real, live "is it actually reachable" check - the router/settings only expose config
/// flags and key-presence, never a live ping. Local (Ollama) is cheap and free to probe;
/// Groq/Claude are deliberately NOT pinged here to avoid spending real API quota just to
/// render a status dot - their pills stay config+env-var-inferred only.
/// </summary>
public sealed class AiProviderHealthChecker
{
    // Verified live: a bare "localhost" URL can occasionally take noticeably longer than 2s on
    // the very first connection (observed a >2s stall even though a plain curl to the same URL
    // completed in ~0.2s moments earlier - almost certainly .NET trying an IPv6 (::1) route
    // before falling back to IPv4, a well-known localhost-resolution quirk on Windows). 2s was
    // producing false "Unreachable" pills for a genuinely healthy server; this is just a
    // background status check, not something blocking the user, so a few extra seconds of
    // patience costs nothing.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<bool> IsOllamaReachableAsync(string ollamaBaseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // OllamaBaseUrl is the OpenAI-compatible "/v1" endpoint; the native health/tag
            // listing endpoint lives at the host root, not under "/v1".
            var hostRoot = ollamaBaseUrl.TrimEnd('/');
            if (hostRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                hostRoot = hostRoot[..^3];
            }

            using var response = await Http.GetAsync($"{hostRoot}/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
