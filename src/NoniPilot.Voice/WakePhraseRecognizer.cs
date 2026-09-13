using System.Speech.Recognition;

namespace NoniPilot.Voice;

/// <summary>
/// Detects a small fixed set of wake phrases from short audio clips using a constrained SAPI
/// grammar - NOT free dictation. Measured live (2026-09-13): asking the Windows offline
/// dictation engine (<see cref="WindowsSpeechToTextService"/>, used by the wake-word loop for
/// general transcription) to recognize "Hey Noni" via <see cref="DictationGrammar"/> returned
/// "Denoting" at 0.4% confidence - dictation simply has no language-model path to an invented
/// name like "Noni" and never once produced "noni" in its top 10 alternates. Loading the exact
/// same audio into a <see cref="Grammar"/> built from a <see cref="Choices"/> list containing
/// only the wake phrases recognized it as "hey noni" at 93.5% confidence - a closed-set grammar
/// only has to do acoustic matching against a handful of known options, not open transcription.
/// This is why the wake-word loop needs its own recognizer instead of reusing whatever the main
/// conversation STT provider is (Groq or Windows dictation) - accuracy on "is this one of N known
/// phrases" and accuracy on "transcribe arbitrary speech" are different problems.
/// </summary>
public sealed class WakePhraseRecognizer : IDisposable
{
    // Deliberately low: initial rollout used 0.5 and the user reported real speech still wasn't
    // triggering it at all, even though the loop was confirmed alive and cycling ("Listening for
    // 'Hey Noni'..." visible in the UI). A real accent/room/mic almost certainly scores lower
    // than the clean synthesized-TTS clip this was tuned against (measured 93.5% there) - a
    // closed-set grammar already only ever "hears" one of the exact phrases below (confirmed:
    // unrelated speech returns no result at all, not a low-confidence guess), so this floor only
    // needs to guard against a forced match on pure silence/noise, not against false positives
    // from real speech.
    private const float MinimumConfidence = 0.15f;

    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "wake-word-debug.log");

    private readonly SpeechRecognitionEngine _engine;

    public WakePhraseRecognizer(IReadOnlyList<string> phrases)
    {
        var grammar = new Grammar(new GrammarBuilder(new Choices(phrases.ToArray())));
        _engine = new SpeechRecognitionEngine();
        _engine.LoadGrammar(grammar);
    }

    /// <summary>
    /// Returns the matched phrase (exactly as it appears in the phrase list passed to the
    /// constructor) if the audio matched one of the wake phrases with reasonable confidence,
    /// otherwise null. Runs synchronously on a background thread since
    /// <see cref="SpeechRecognitionEngine"/> is not natively async.
    ///
    /// Every attempt is appended to a small debug log (recognized text/confidence/pass-fail) at
    /// %LOCALAPPDATA%\NoniPilot\wake-word-debug.log - temporary diagnostic instrumentation added
    /// after a first attempt at this recognizer (tuned only against synthesized TTS audio) still
    /// didn't respond to real spoken "Hey Noni" for the user, so the next real attempt leaves
    /// hard evidence of what the recognizer actually heard instead of another guess.
    /// </summary>
    public Task<string?> TryMatchAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        if (wavAudio.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(wavAudio);
                _engine.SetInputToWaveStream(stream);
                var result = _engine.Recognize(TimeSpan.FromSeconds(8));
                var passed = result is not null && result.Confidence >= MinimumConfidence;

                AppendLog(result is null
                    ? "no result (grammar found no acoustic match)"
                    : $"text=[{result.Text}] confidence={result.Confidence:F3} passedFloor={passed}");

                return passed ? result!.Text : null;
            }
            catch (Exception ex)
            {
                AppendLog($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Exposed so <c>CommandProcessor</c>'s wake-word loop can log what happened upstream of
    /// this recognizer too (e.g. the mic's own silence/loudness gate rejecting a recording
    /// before any audio ever reaches here) into the same debug log, for one complete picture of
    /// a failed attempt.
    /// </summary>
    public static void AppendLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic logging must never be able to break wake-word detection itself.
        }
    }

    public void Dispose() => _engine.Dispose();
}
