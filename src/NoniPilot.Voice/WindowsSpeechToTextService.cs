using System.Speech.Recognition;

namespace NoniPilot.Voice;

/// <summary>
/// Fully offline speech-to-text via the built-in Windows SAPI dictation engine - free, no
/// network, always available, but noticeably less accurate than Groq's Whisper. The free
/// fallback when no GROQ_API_KEY is set, same "hybrid, local as the guaranteed floor"
/// pattern as the chat provider chain.
/// </summary>
public sealed class WindowsSpeechToTextService : ISpeechToTextService
{
    public string Name => "Windows Speech (offline)";

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        if (wavAudio.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run(() =>
        {
            using var engine = new SpeechRecognitionEngine();
            engine.LoadGrammar(new DictationGrammar());

            using var stream = new MemoryStream(wavAudio);
            engine.SetInputToWaveStream(stream);

            var result = engine.Recognize(TimeSpan.FromSeconds(15));
            return result?.Text;
        }, cancellationToken);
    }
}
