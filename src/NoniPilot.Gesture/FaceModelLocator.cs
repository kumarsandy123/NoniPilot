using System.Net.Http;

namespace NoniPilot.Gesture;

/// <summary>
/// Locates (and auto-downloads, same "no manual setup" UX as WhisperLocalSpeechToTextService's
/// ggml model download) the standard OpenCV Haar-cascade face detector - a small (~900KB),
/// well-known file distributed as part of the official OpenCV project itself, not a
/// NoniPilot-specific model. The trained per-user face-recognition model lives alongside it but
/// is never auto-downloaded (it doesn't exist until <c>FaceRecognitionEngine.EnrollAsync</c>
/// creates it from the user's own camera).
/// </summary>
public static class FaceModelLocator
{
    private const string CascadeUrl =
        "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml";

    public static string ModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "models");

    public static string CascadePath => Path.Combine(ModelsDirectory, "haarcascade_frontalface_default.xml");

    public static string TrainedFaceModelPath => Path.Combine(ModelsDirectory, "face-model.yml");

    public static async Task<string> EnsureCascadeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDirectory);

        if (File.Exists(CascadePath))
        {
            return CascadePath;
        }

        var tempPath = CascadePath + ".downloading";
        using var http = new HttpClient();
        using (var response = await http.GetAsync(CascadeUrl, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(tempPath);
            await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, CascadePath, overwrite: true);
        return CascadePath;
    }
}
