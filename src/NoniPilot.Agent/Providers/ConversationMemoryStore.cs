using System.Text.Json;

namespace NoniPilot.Agent.Providers;

/// <summary>
/// A single remembered exchange (one user command + NoniPilot's final answer to it) - the same
/// shape ToolCallingPlannerService already keeps in memory for the running conversation
/// (CommitToHistory), just also written to disk so it survives an app restart. Never includes
/// intermediate tool-call traces, only the semantic Q&amp;A pair - same reasoning as
/// CommitToHistory's own doc comment: those live in the audit log/Task History, not here.
/// </summary>
public sealed record RememberedExchange(string Command, string Answer);

/// <summary>
/// Persists the tail of the conversation across app restarts - added after a live request
/// ("save a memory in it of every task") for continuity: previously every relaunch started with
/// a completely blank conversation, so a reference to something discussed a few minutes earlier
/// was lost the moment the app closed. Capped at the same size the in-memory history already
/// uses (MaxRememberedTasks in ToolCallingPlannerService) so this never grows the prompt sent to
/// the model beyond what already worked before persistence existed.
/// </summary>
public static class ConversationMemoryStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoniPilot", "conversation-memory.json");

    public static List<RememberedExchange> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<RememberedExchange>>(json) ?? new List<RememberedExchange>();
            }
        }
        catch
        {
            // A corrupt or unreadable memory file must never block startup - just start fresh.
        }

        return new List<RememberedExchange>();
    }

    public static void Save(IReadOnlyList<RememberedExchange> exchanges)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(exchanges));
        }
        catch
        {
            // Persistence is a nice-to-have, not a correctness requirement - never let a disk
            // error break the actual conversation that's happening right now.
        }
    }
}
