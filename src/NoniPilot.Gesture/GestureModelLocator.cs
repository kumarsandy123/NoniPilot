namespace NoniPilot.Gesture;

/// <summary>
/// Finds the hand-landmark ONNX model on disk. The model file (several MB) is deliberately
/// NOT checked into the repo - it lives in the user's local app-data folder, same convention
/// as the SQLite audit log and AI-provider settings.
/// </summary>
public static class GestureModelLocator
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "models");

    public static string DefaultModelPath => Path.Combine(DefaultDirectory, "hand_landmark.onnx");

    /// <returns>The model path if a file exists there, otherwise null.</returns>
    public static string? FindModelPath() => File.Exists(DefaultModelPath) ? DefaultModelPath : null;
}
