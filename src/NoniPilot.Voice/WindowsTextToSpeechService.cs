using System.Speech.Synthesis;

namespace NoniPilot.Voice;

/// <summary>
/// Speaks responses via the built-in Windows SAPI synthesizer - free, offline, zero setup.
///
/// Creates a FRESH SpeechSynthesizer per call rather than reusing one long-lived instance.
/// This was changed after a real, reproducible bug in continuous voice mode: the first
/// response in a session played back correctly, but every response after that was silent -
/// no exception, no error - even though an isolated test of repeated SpeakAsync calls on
/// their own (outside the full app, with no microphone capture running alongside) worked
/// fine every time. That points at a resource/session conflict between WASAPI microphone
/// capture (NAudioMicrophoneRecorder, running moments earlier in the same voice-mode loop)
/// and the synthesizer's playback session on this machine's audio stack, not a logic bug in
/// the speak call itself. Recreating the synthesizer per call forces a clean COM/audio
/// session each time, which is the standard practical workaround for this class of SAPI
/// reliability issue in apps that also do audio capture.
/// </summary>
public sealed class WindowsTextToSpeechService : ITextToSpeechService, IDisposable
{
    private volatile SpeechSynthesizer? _currentSynthesizer;

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        var synthesizer = new SpeechSynthesizer();
        synthesizer.SetOutputToDefaultAudioDevice();
        _currentSynthesizer = synthesizer;

        var tcs = new TaskCompletionSource();

        void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            synthesizer.SpeakCompleted -= OnSpeakCompleted;
            tcs.TrySetResult();
        }

        synthesizer.SpeakCompleted += OnSpeakCompleted;

        var registration = cancellationToken.Register(() =>
        {
            // Cancelling counts as "done speaking" for the caller's purposes (e.g. the voice
            // conversation loop waiting to listen again) - it should proceed immediately, not
            // wait for a completion event that a cancelled utterance will never raise.
            synthesizer.SpeakAsyncCancelAll();
            tcs.TrySetResult();
        });

        _ = tcs.Task.ContinueWith(_ =>
        {
            registration.Dispose();
            if (ReferenceEquals(_currentSynthesizer, synthesizer))
            {
                _currentSynthesizer = null;
            }
            synthesizer.Dispose();
        }, TaskScheduler.Default);

        synthesizer.SpeakAsync(text);
        return tcs.Task;
    }

    /// <summary>Immediately silences whatever NoniPilot is currently saying - wired to STOP/Stop Talking.</summary>
    public void StopSpeaking() => _currentSynthesizer?.SpeakAsyncCancelAll();

    public void Dispose() => _currentSynthesizer?.Dispose();
}
