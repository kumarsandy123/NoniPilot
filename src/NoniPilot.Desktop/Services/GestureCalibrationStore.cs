using System.IO;
using System.Text.Json;
using NoniPilot.Gesture;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Persists gesture calibration across sessions - previously this reset every time the
/// gesture UI opened (each launch built a fresh GestureCalibration straight from the sliders'
/// default values). Same static-store pattern as AiProviderSettingsStore.
/// </summary>
public static class GestureCalibrationStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "gesture-calibration.json");

    public static GestureCalibration Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<GestureCalibration>(json) ?? new GestureCalibration();
            }
        }
        catch
        {
            // A corrupt or unreadable settings file must never block startup - fall back to defaults.
        }

        return new GestureCalibration();
    }

    public static void Save(GestureCalibration calibration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(calibration, new JsonSerializerOptions { WriteIndented = true }));
    }
}
