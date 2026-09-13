using System.IO;
using System.Text.Json;
using NoniPilot.Desktop.Models;

namespace NoniPilot.Desktop.Services;

/// <summary>Persisted list of saved AutomationSequences - same static-store pattern as AiProviderSettingsStore.</summary>
public static class AutomationSequenceStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "automation-sequences.json");

    public static List<AutomationSequence> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<AutomationSequence>>(json) ?? new List<AutomationSequence>();
            }
        }
        catch
        {
            // A corrupt or unreadable file must never block the page from loading.
        }

        return new List<AutomationSequence>();
    }

    public static void Save(List<AutomationSequence> sequences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(sequences, new JsonSerializerOptions { WriteIndented = true }));
    }
}
