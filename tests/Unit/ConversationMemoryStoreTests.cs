using NoniPilot.Agent.Providers;

namespace NoniPilot.Tests.Unit;

/// <summary>
/// Verifies the actual persistence round-trip added for "save a memory in it of every task"
/// (2026-09-13) - directly exercising ConversationMemoryStore rather than relying on flaky live
/// UI automation to prove the save/load logic itself is correct. Backs up/restores the real file
/// around the test since the store deliberately uses a fixed, non-injectable path (same simple
/// static-store convention as AiProviderSettingsStore/GestureCalibrationStore) - this test must
/// never leave the user's actual saved conversation memory altered.
/// </summary>
public class ConversationMemoryStoreTests
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "conversation-memory.json");

    [Fact]
    public void SaveThenLoad_RoundTripsExchangesInOrder()
    {
        var backupPath = FilePath + ".test-backup";
        var hadExisting = File.Exists(FilePath);
        if (hadExisting)
        {
            File.Copy(FilePath, backupPath, overwrite: true);
        }

        try
        {
            var exchanges = new List<RememberedExchange>
            {
                new("open chrome", "Opened Chrome."),
                new("what is 5 plus 7", "12"),
            };

            ConversationMemoryStore.Save(exchanges);
            var loaded = ConversationMemoryStore.Load();

            Assert.Equal(2, loaded.Count);
            Assert.Equal("open chrome", loaded[0].Command);
            Assert.Equal("Opened Chrome.", loaded[0].Answer);
            Assert.Equal("what is 5 plus 7", loaded[1].Command);
            Assert.Equal("12", loaded[1].Answer);
        }
        finally
        {
            if (hadExisting)
            {
                File.Copy(backupPath, FilePath, overwrite: true);
                File.Delete(backupPath);
            }
            else
            {
                File.Delete(FilePath);
            }
        }
    }

    [Fact]
    public void Load_WhenNoFileExists_ReturnsEmptyList()
    {
        var backupPath = FilePath + ".test-backup";
        var hadExisting = File.Exists(FilePath);
        if (hadExisting)
        {
            File.Move(FilePath, backupPath);
        }

        try
        {
            var loaded = ConversationMemoryStore.Load();
            Assert.Empty(loaded);
        }
        finally
        {
            if (hadExisting)
            {
                File.Move(backupPath, FilePath);
            }
        }
    }
}
