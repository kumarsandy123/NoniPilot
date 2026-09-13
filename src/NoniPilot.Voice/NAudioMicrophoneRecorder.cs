using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NoniPilot.Voice;

/// <summary>
/// Push-to-talk / continuous-mode mic capture (section 8.1) via NAudio's WASAPI capture - the
/// modern Windows audio API. The original implementation used the legacy winmm-based
/// WaveInEvent, which failed with "UnspecifiedError calling waveInOpen" on real hardware
/// during development (that error crashed the whole app before MainWindow's try/catch was
/// added - see docs/architecture/decisions.md). WASAPI is broadly more compatible across
/// modern audio driver stacks, so it's the default now; whatever sample rate/channel count
/// the capture device natively uses is written into the WAV header as-is (both STT backends
/// read the header rather than assuming 16kHz mono), so no resampling is needed here.
/// </summary>
public sealed class NAudioMicrophoneRecorder : IMicrophoneRecorder, IDisposable
{
    // A conservative RMS threshold on a 0..1 normalized sample scale, originally set well above
    // an assumed room-noise floor. Lowered from 0.02 after live measurement (2026-09-13, logged
    // via WakePhraseRecognizer.AppendLog): real spoken commands on this user's actual hardware
    // frequently peaked at only 0.007-0.02 RMS, below the old threshold entirely, so genuine
    // speech was being thrown out before STT ever ran. Whisper (the STT engine as of the same
    // date) is far less prone to hallucinating text from near-silent/noise audio than the old
    // Windows dictation engine was, so a lower threshold here is a smaller risk than it used to
    // be if it occasionally lets a quiet non-speech sound through.
    private const double AmplitudeThreshold = 0.012;

    /// <summary>
    /// Below this much *cumulative* loud time, a recording is treated as having captured no
    /// real speech at all - a brief blip clears the instantaneous threshold above but isn't a
    /// spoken word. Added after a live, repeatable bug: NoniPilot's own voice trailing off
    /// through the speakers (acoustic bleed into the mic, especially without headphones) was
    /// occasionally loud enough, for long enough, to pass the old "was it ever loud" gate and
    /// get transcribed into a short, generic hallucinated phrase ("Thank you. Thank you.") that
    /// NoniPilot then treated as a real new command from the user. Real short commands ("stop",
    /// "haan"/yes) still comfortably clear this - it's tuned to reject blips, not brief words.
    /// </summary>
    private const int MinimumLoudMilliseconds = 220;

    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;

    private readonly object _amplitudeLock = new();
    private bool _everLoud;
    private DateTime _lastLoudUtc;
    private double _totalLoudMilliseconds;
    private double _peakRms;
    private int _chunkCount;

    public void StartRecording()
    {
        _buffer = new MemoryStream();
        _capture = new WasapiCapture();
        _writer = new WaveFileWriter(_buffer, _capture.WaveFormat);
        _everLoud = false;
        _lastLoudUtc = DateTime.UtcNow;
        _totalLoudMilliseconds = 0;
        _peakRms = 0;
        _chunkCount = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public byte[] StopRecording()
    {
        if (_capture is null || _buffer is null || _writer is null)
        {
            return Array.Empty<byte>();
        }

        // WasapiCapture.StopRecording() is asynchronous - its capture thread can still be
        // mid-callback when the call returns. Wait for RecordingStopped so a last in-flight
        // DataAvailable callback can't write to the buffer after we've already read it below.
        using var stoppedSignal = new ManualResetEventSlim(false);
        _capture.RecordingStopped += (_, _) => stoppedSignal.Set();
        _capture.StopRecording();
        stoppedSignal.Wait(TimeSpan.FromSeconds(2));

        _capture.DataAvailable -= OnDataAvailable;

        // Flush patches the RIFF header's length fields in place without closing the stream -
        // extract the bytes before disposing the writer, which does close the stream.
        _writer.Flush();
        var rawBytes = _buffer.ToArray();

        _writer.Dispose();
        _capture.Dispose();

        _writer = null;
        _capture = null;
        _buffer = null;

        bool hadRealSpeech;
        double totalLoudMs;
        double peakRms;
        int chunkCount;
        lock (_amplitudeLock)
        {
            hadRealSpeech = _totalLoudMilliseconds >= MinimumLoudMilliseconds;
            totalLoudMs = _totalLoudMilliseconds;
            peakRms = _peakRms;
            chunkCount = _chunkCount;
        }

        // Temporary diagnostics for the "wake word works, follow-up command in voice mode
        // never gets picked up" investigation (2026-09-13) - shows whether the mic is hearing
        // near-silence (peakRms close to 0 - a real capture/device problem) or genuine speech
        // that's just falling short of the loud-duration gate (peakRms clears AmplitudeThreshold
        // but totalLoudMs stays low - a tuning problem instead).
        WakePhraseRecognizer.AppendLog(
            $"[recorder] bytes={rawBytes.Length} chunks={chunkCount} peakRms={peakRms:F4} totalLoudMs={totalLoudMs:F0} threshold={MinimumLoudMilliseconds} passed={hadRealSpeech}");

        if (!hadRealSpeech)
        {
            // Not enough real speech to be worth transcribing at all - most commonly NoniPilot's
            // own voice bleeding acoustically into the mic right after speaking, not a genuine
            // utterance. Returning empty here (rather than sending faint/echo audio to STT)
            // means CommandProcessor never gets a hallucinated phrase to act on in the first
            // place, instead of trying to filter one out after the fact.
            return Array.Empty<byte>();
        }

        // WASAPI captures in the device's native format (commonly 48kHz stereo IEEE float),
        // not a fixed format - and that variability is exactly what made Windows Speech
        // Recognition hang indefinitely on "Transcribing..." during development (it does not
        // handle arbitrary capture formats well). Standardizing to 16kHz mono 16-bit PCM here
        // - the format both STT backends were actually designed around - fixes that, and
        // shrinks the upload for Groq Whisper as a side benefit.
        return ResampleTo16kMonoPcm(rawBytes);
    }

    private static byte[] ResampleTo16kMonoPcm(byte[] rawWavBytes)
    {
        var targetFormat = new WaveFormat(16000, 16, 1);

        using var sourceStream = new MemoryStream(rawWavBytes);
        using var reader = new WaveFileReader(sourceStream);
        using var resampler = new MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };
        using var outputStream = new MemoryStream();

        using (var writer = new WaveFileWriter(outputStream, targetFormat))
        {
            var buffer = new byte[targetFormat.AverageBytesPerSecond];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, bytesRead);
            }
        }

        return outputStream.ToArray();
    }

    public async Task<byte[]> RecordUntilSilenceAsync(
        TimeSpan maxDuration,
        TimeSpan silenceDuration,
        CancellationToken cancellationToken = default)
    {
        StartRecording();
        var start = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            if (DateTime.UtcNow - start > maxDuration)
            {
                break;
            }

            lock (_amplitudeLock)
            {
                if (_everLoud && DateTime.UtcNow - _lastLoudUtc > silenceDuration)
                {
                    break;
                }
            }
        }

        return StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        TrackAmplitude(e.Buffer, e.BytesRecorded);
    }

    /// <summary>
    /// Computes RMS amplitude for this chunk to drive silence detection. Handles the two
    /// formats WASAPI capture devices commonly report (IEEE float and 16-bit PCM); any other
    /// format is skipped, which just means silence detection won't trigger and the
    /// maxDuration cap becomes the only stop condition for that recording.
    /// </summary>
    private void TrackAmplitude(byte[] buffer, int bytesRecorded)
    {
        var format = _capture?.WaveFormat;
        if (format is null)
        {
            return;
        }

        double sumSquares = 0;
        var sampleCount = 0;

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (var i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                var sample = BitConverter.ToSingle(buffer, i);
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= bytesRecorded; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else
        {
            return;
        }

        if (sampleCount == 0)
        {
            return;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);

        lock (_amplitudeLock)
        {
            _chunkCount++;
            if (rms > _peakRms)
            {
                _peakRms = rms;
            }
        }

        if (rms <= AmplitudeThreshold)
        {
            return;
        }

        // sampleCount counts individual interleaved samples across all channels, not frames -
        // divide out the channel count so stereo audio doesn't double-count elapsed time.
        var frameCount = sampleCount / Math.Max(1, format.Channels);
        var chunkMilliseconds = frameCount * 1000.0 / format.SampleRate;

        lock (_amplitudeLock)
        {
            _everLoud = true;
            _lastLoudUtc = DateTime.UtcNow;
            _totalLoudMilliseconds += chunkMilliseconds;
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _capture?.Dispose();
        _buffer?.Dispose();
    }
}
