namespace NoniPilot.Voice;

/// <summary>Records microphone audio into WAV bytes (native device format - see NAudioMicrophoneRecorder).</summary>
public interface IMicrophoneRecorder
{
    void StartRecording();

    /// <returns>The recorded audio as WAV bytes, or an empty array if nothing was recorded.</returns>
    byte[] StopRecording();

    /// <summary>
    /// Records one conversational turn: starts immediately, and stops itself once it has
    /// heard speech followed by <paramref name="silenceDuration"/> of quiet, or
    /// <paramref name="maxDuration"/> elapses (whichever first) - the "listen, then
    /// auto-stop" behavior continuous voice mode needs, as opposed to StartRecording/
    /// StopRecording's manual start/stop.
    /// </summary>
    Task<byte[]> RecordUntilSilenceAsync(
        TimeSpan maxDuration,
        TimeSpan silenceDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>Turns recorded audio into text. Implementations: Groq Whisper (cloud, free-tier) and Windows Speech (offline fallback).</summary>
public interface ISpeechToTextService
{
    string Name { get; }

    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken = default);
}

/// <summary>Speaks NoniPilot's responses back to the user (section 8.1: "Text-to-speech responses").</summary>
public interface ITextToSpeechService
{
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Immediately silences whatever is currently being spoken - wired to STOP/Stop Talking.</summary>
    void StopSpeaking();
}
