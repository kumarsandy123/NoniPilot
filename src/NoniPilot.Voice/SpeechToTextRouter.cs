namespace NoniPilot.Voice;

/// <summary>Tries each STT provider in order (Groq first if configured, Windows Speech as the always-available floor).</summary>
public sealed class SpeechToTextRouter : ISpeechToTextService
{
    private readonly IReadOnlyList<ISpeechToTextService> _providers;

    public SpeechToTextRouter(IReadOnlyList<ISpeechToTextService> providers) => _providers = providers;

    public string Name => string.Join(" -> ", _providers.Select(p => p.Name));

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            string? result;
            try
            {
                result = await provider.TranscribeAsync(wavAudio, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }
}

public static class VoiceServiceFactory
{
    /// <param name="enableGroq">
    /// Must mirror AiProviderSettings.EnableGroq - previously this factory added Groq Whisper
    /// whenever GROQ_API_KEY happened to be present in the environment, completely ignoring
    /// whether the user had actually turned Groq on for chat. A real, live bug this caused: the
    /// user disabled Groq (explicit "no external token" decision) but a leftover GROQ_API_KEY
    /// set at the Windows user-environment level from earlier in the same day meant every voice
    /// listen cycle - including the wake-word loop's short cycles - still silently tried a
    /// Groq network call first every single time, adding real latency (and a real dependency on
    /// a rate-limited free tier) to something that was supposed to be fully local. Now gated
    /// behind the same flag as chat, so "Groq off" actually means off everywhere.
    /// </param>
    /// <param name="language">
    /// Optional ISO-639-1 hint (e.g. "hi") passed to Groq Whisper to remove auto-detection
    /// ambiguity between similar-sounding languages (Hindi/Urdu in particular). Only relevant
    /// when enableGroq is true. Null/empty leaves Whisper on auto-detect.
    /// </param>
    public static SpeechToTextRouter BuildSpeechToText(bool enableGroq = false, string? language = null)
    {
        var providers = new List<ISpeechToTextService>();

        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (enableGroq && !string.IsNullOrWhiteSpace(groqKey))
        {
            providers.Add(new GroqWhisperSpeechToTextService(groqKey, language: language));
        }

        // Local Whisper (Whisper.net/whisper.cpp) replaces Windows' built-in SAPI dictation as
        // the default, always-available STT floor - measured live (2026-09-13): SAPI dictation
        // transcribed a clearly-captured real command as "The latino", nowhere close to correct.
        // Whisper is fully offline (no account/token/rate-limit, a one-time ~140MB model
        // download) and dramatically more accurate. WindowsSpeechToTextService stays last in the
        // chain as an extra fallback only if Whisper's model somehow fails to load.
        providers.Add(new WhisperLocalSpeechToTextService(language));
        providers.Add(new WindowsSpeechToTextService());

        return new SpeechToTextRouter(providers);
    }

    public static ITextToSpeechService BuildTextToSpeech() => new WindowsTextToSpeechService();
}
