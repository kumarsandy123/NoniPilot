using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Agent;

/// <summary>
/// Deliberately dumb and local: matches a handful of safety-critical or extremely common
/// phrases without ever calling the LLM, so "Stop" always works even if the network is down or
/// the planner is mid-request (section 4/10 - emergency stop must be immediate), and so a
/// request like "open my desktop" doesn't have to pay for a full CPU-bound model round-trip
/// just to look up a path that's already known statically.
/// </summary>
public sealed class LocalIntentService : IIntentService
{
    private static readonly (string Intent, string[] Phrases)[] ExactFastPathIntents =
    [
        ("stop", ["stop", "stop it", "halt", "cancel", "cancel that", "emergency stop", "abort"]),
        // A softer counterpart to "stop": ends the active conversation and returns to passive
        // wake-word-only listening, without an emergency stop of in-flight computer control.
        // Waking back up is "hey noni"/"jago noni" etc, matched separately by CommandProcessor.
        ("sleep", ["sleep", "go to sleep", "so jao", "so jaao", "sula do", "sona hai", "जाओ सो जाओ", "सो जाओ"]),
        // Observed live: the local model called the wrong tool (gesture_check_visibility
        // instead of face_recognize_person) for this exact question despite an explicit system
        // prompt instruction - the same "weak model unreliably picks the right tool" failure
        // mode already worked around for open/close, solved the same way here.
        ("recognize_me", [
            "do you recognize me", "do you recognize me?", "who am i", "who am i?",
            "is this me", "is that me", "do you know who i am", "kya tum mujhe pehchante ho",
            "mujhe pehchano", "main kaun hoon",
        ]),
    ];

    /// <summary>
    /// Well-known-folder open requests, matched more loosely than the exact-phrase intents
    /// above since real phrasing varies a lot ("open the desktop", "open my desktop folder",
    /// "desktop khol do"). Observed live: a model asked to "open the desktop" instead searched
    /// *inside* the Desktop for something named "desktop" via filesystem_open_best_match, and -
    /// because a folder literally named "Desktop" happened to exist inside the real Desktop -
    /// opened that nested folder instead of the Desktop itself. This fast path bypasses that
    /// failure mode entirely for the whole class of "open my <well-known folder>" requests: no
    /// LLM call, no fuzzy search, just the actual special-folder path.
    /// </summary>
    private static readonly string[] OpenWords = ["open", "show", "khol", "kholo", "kholiye"];

    // Every row shares the same OpenWords set above - declared first so static field
    // initialization order doesn't leave it null when this array is built (C# runs field
    // initializers in textual declaration order within a class).
    private static readonly (string Intent, string[] FolderWords)[] OpenFolderIntents =
    [
        ("open_desktop", ["desktop"]),
        ("open_documents", ["documents", "document", "docs"]),
        ("open_downloads", ["downloads", "download"]),
        ("open_pictures", ["pictures", "picture", "photos", "photo"]),
        ("open_music", ["music", "songs"]),
        ("open_videos", ["videos", "video"]),
    ];

    /// <summary>
    /// Words that mean this command is actually about something more specific than "open the
    /// folder itself" - e.g. creating/searching/renaming something in relation to it - and
    /// must NOT be short-circuited by the folder fast path above, or a real, specific request
    /// would silently get replaced with just opening the parent folder.
    /// </summary>
    private static readonly string[] MoreSpecificThanJustOpening =
    [
        "create", "delete", "remove", "rename", "copy", "move", "search", "find", "inside",
        "named", "call it", "new folder", "file", "in the", "from the", "to the",
    ];

    public string? TryMatchFastPathIntent(string naturalLanguageCommand)
    {
        var normalized = naturalLanguageCommand.Trim().TrimEnd('.', '!').ToLowerInvariant();

        foreach (var (intent, phrases) in ExactFastPathIntents)
        {
            if (phrases.Contains(normalized))
            {
                return intent;
            }
        }

        if (MoreSpecificThanJustOpening.Any(word => normalized.Contains(word)))
        {
            return null;
        }

        // Keep this conservative - short commands only, so a longer sentence that happens to
        // mention "desktop" in passing (e.g. as part of a larger instruction) never misfires.
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)
        {
            return null;
        }

        if (!OpenWords.Any(w => normalized.Contains(w)))
        {
            return null;
        }

        foreach (var (intent, folderWords) in OpenFolderIntents)
        {
            if (folderWords.Any(w => normalized.Contains(w)))
            {
                return intent;
            }
        }

        return null;
    }
}
