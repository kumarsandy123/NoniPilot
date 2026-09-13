using System.Text.Json;

namespace NoniPilot.Agent.Providers;

/// <summary>
/// The "AI Providers" settings panel's backing model. Defaults are chosen so a fresh install
/// costs nothing and never silently reaches for a paid provider, and - per explicit user
/// request (2026-09-12) - never depends on anyone else's rate-limited free token either: Local
/// is on and is the only provider enabled by default. Groq remains fully supported and can be
/// switched on in the AI Providers window by anyone who wants its faster/more capable hosted
/// model and is fine with its free-tier quota running out mid-task; it just isn't the default
/// anymore, since "a token that runs out while I'm working" is exactly the failure mode being
/// avoided. The paid Claude provider stays off until explicitly enabled either way, matching
/// the "never upgrade automatically, never add a payment method" rule.
/// </summary>
public sealed class AiProviderSettings
{
    public bool EnableLocal { get; set; } = true;
    public bool EnableGroq { get; set; } = false;
    public bool EnableClaudePaid { get; set; } = false;

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434/v1";
    // Switched from llama3.1 (8B, 4.9GB) to qwen2.5:3b (3.1B, 1.9GB) on 2026-09-13 after the
    // user asked for real speed and measured evidence, not a guess: a same-prompt comparison
    // via the actual OpenAiCompatibleChatProvider class showed qwen2.5:3b answering a plain
    // greeting in ~11s vs llama3.1's ~27s (and llama3.1 wrongly tried to call a tool for it,
    // matching an earlier-documented bug; qwen2.5:3b correctly just answered), and a real
    // tool-calling request (list a folder) in ~6.2s vs ~8.6s, both correctly calling the tool.
    // Also declares "tools" capability and a much larger native context window (32768 vs
    // llama3.1's default-loaded 4096 - see docs/architecture/decisions.md's context-ceiling
    // finding). If tool-calling reliability ever regresses noticeably, llama3.1 is still pulled
    // locally and can be set here as a fallback with no re-download needed.
    public string OllamaModel { get; set; } = "qwen2.5:3b";
    // Groq's exact lineup, and the free tier's per-model tokens-per-minute limit, both shift
    // over time - see docs/architecture/decisions.md for the trail: llama-3.3-70b-versatile
    // and llama-3.1-8b-instant both 404'd (model_not_found), then openai/gpt-oss-120b worked
    // but 413'd (request too large for its free-tier TPM limit) against our ~17-tool request.
    // openai/gpt-oss-20b is the same family, smaller, and fits that limit. If this ever fails
    // again, check console.groq.com/playground for a working name/size and change it in the
    // AI Providers window - no rebuild needed.
    public string GroqModel { get; set; } = "openai/gpt-oss-20b";

    /// <summary>
    /// ISO-639-1 hint for Groq Whisper voice transcription (e.g. "hi", "en", "ur"). Empty
    /// means auto-detect, which is genuinely ambiguous between languages that sound alike but
    /// use different scripts (Hindi/Urdu in particular) - pin this if voice input keeps
    /// transcribing into the wrong script/language.
    /// </summary>
    public string SpokenLanguage { get; set; } = "";
}

public static class AiProviderSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "ai-providers.json");

    public static AiProviderSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AiProviderSettings>(json) ?? new AiProviderSettings();
            }
        }
        catch
        {
            // A corrupt or unreadable settings file must never block startup - fall back to defaults.
        }

        return new AiProviderSettings();
    }

    public static void Save(AiProviderSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Builds the actual provider chain from settings + whichever API keys are present in the environment.</summary>
public static class AiProviderFactory
{
    public static ChatProviderRouter Build(AiProviderSettings settings)
    {
        var providers = new List<IChatProvider>();

        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (settings.EnableGroq && !string.IsNullOrWhiteSpace(groqKey))
        {
            providers.Add(new OpenAiCompatibleChatProvider("Groq", "https://api.groq.com/openai/v1", settings.GroqModel, groqKey));
        }

        var claudeKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (settings.EnableClaudePaid && !string.IsNullOrWhiteSpace(claudeKey))
        {
            providers.Add(new ClaudeChatProvider());
        }

        if (settings.EnableLocal)
        {
            // Local inference on CPU-only hardware is slow, and a multi-step tool exchange
            // means several sequential calls, each of which can individually take minutes -
            // 3 minutes was measured too short for a real tool round-trip on an 8B model with
            // no GPU (see docs/architecture/decisions.md). Local has no per-call cost, so a
            // generous timeout costs only wall-clock time, not money - err on the long side.
            providers.Add(new OpenAiCompatibleChatProvider(
                "Local (Ollama)", settings.OllamaBaseUrl, settings.OllamaModel, apiKey: null, timeout: TimeSpan.FromMinutes(8)));
        }

        return new ChatProviderRouter(providers);
    }
}
