using System.IO;
using System.Text.Json;

namespace NoniPilot.Desktop.Services;

public sealed record BrowserBookmark(string Label, string Url);

/// <summary>Persisted bookmark list for the Browser Automation page - same static-store pattern as AiProviderSettingsStore.</summary>
public static class BrowserBookmarkStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "browser-bookmarks.json");

    public static List<BrowserBookmark> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<BrowserBookmark>>(json) ?? DefaultBookmarks();
            }
        }
        catch
        {
            // A corrupt or unreadable file must never block the page from loading.
        }

        return DefaultBookmarks();
    }

    public static void Save(List<BrowserBookmark> bookmarks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<BrowserBookmark> DefaultBookmarks() =>
    [
        new("Google", "https://www.google.com"),
        new("Gmail", "https://mail.google.com"),
        new("YouTube", "https://www.youtube.com"),
    ];
}
