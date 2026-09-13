using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace NoniPilot.Voice;

/// <summary>
/// Fully offline, fully local speech-to-text using a downloaded Whisper ggml model
/// (whisper.cpp via Whisper.net) - no account, no API key, no rate limit, never runs out. This
/// replaces Windows' built-in SAPI dictation engine as the default STT for real commands:
/// measured live (2026-09-13), SAPI dictation transcribed a clearly-captured spoken command as
/// "The latino" - nowhere close to what was actually said. Whisper is dramatically more accurate
/// at the cost of a one-time model download (~140MB for the "base" multilingual model, cached
/// under %LOCALAPPDATA%\NoniPilot\models) and somewhat more CPU/RAM per transcription. Keeping
/// the multilingual (not English-only) model since this app's users mix English and
/// Hindi/Hinglish (see the wake-phrase list).
/// </summary>
public sealed class WhisperLocalSpeechToTextService : ISpeechToTextService, IDisposable
{
    private readonly Task<WhisperFactory> _factoryTask;
    private readonly string? _language;

    // whisper.cpp's processor is not safe for concurrent calls against the same factory -
    // this app only ever transcribes one clip at a time anyway (wake-word loop and voice mode
    // are already mutually exclusive via CommandProcessor's mic semaphore), so a simple gate is
    // enough rather than a full request queue.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "Whisper (local, offline)";

    public WhisperLocalSpeechToTextService(string? language = null)
    {
        _language = language;
        _factoryTask = InitializeFactoryAsync();
    }

    private static async Task<WhisperFactory> InitializeFactoryAsync()
    {
        var modelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "ggml-base.bin");

        if (!File.Exists(modelPath))
        {
            var tempPath = modelPath + ".downloading";
            using (var modelStream = await WhisperGgmlDownloader.Default
                       .GetGgmlModelAsync(GgmlType.Base, QuantizationType.NoQuantization)
                       .ConfigureAwait(false))
            using (var fileWriter = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileWriter).ConfigureAwait(false);
            }

            // Download-then-rename so a crash/kill mid-download never leaves a corrupt file at
            // the real path that a later launch would try to load as-is.
            File.Move(tempPath, modelPath, overwrite: true);
        }

        return WhisperFactory.FromPath(modelPath);
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        if (wavAudio.Length == 0)
        {
            return null;
        }

        var factory = await _factoryTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = factory.CreateBuilder();
            builder = string.IsNullOrWhiteSpace(_language) ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
            using var processor = builder.Build();
            using var stream = new MemoryStream(wavAudio);

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            var result = text.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_factoryTask.IsCompletedSuccessfully)
        {
            _factoryTask.Result.Dispose();
        }

        _gate.Dispose();
    }
}
