using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace NoniPilot.Voice;

/// <summary>
/// Speech-to-text via Groq's free-tier Whisper endpoint - the same OpenAI-compatible
/// `/audio/transcriptions` shape OpenAI's Whisper API uses, so this is stable, well-documented
/// wire format, not a Groq-specific one.
/// </summary>
public sealed class GroqWhisperSpeechToTextService : ISpeechToTextService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string? _language;

    public string Name => "Groq Whisper";

    /// <param name="language">
    /// Optional ISO-639-1 hint (e.g. "hi" for Hindi, "en" for English). Whisper auto-detects
    /// the spoken language when this is null, which is genuinely ambiguous between languages
    /// that sound alike but use different scripts - Hindi and Urdu in particular, which are
    /// spoken almost identically but auto-detection can guess either script. Pinning this
    /// removes that guesswork entirely.
    /// </param>
    public GroqWhisperSpeechToTextService(string apiKey, string model = "whisper-large-v3-turbo", string? language = null)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.groq.com/openai/v1/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
        _language = language;
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        if (wavAudio.Length == 0)
        {
            return null;
        }

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "audio.wav");
        content.Add(new StringContent(_model), "model");
        if (!string.IsNullOrWhiteSpace(_language))
        {
            content.Add(new StringContent(_language), "language");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("audio/transcriptions", content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        return json?["text"]?.GetValue<string>();
    }
}
