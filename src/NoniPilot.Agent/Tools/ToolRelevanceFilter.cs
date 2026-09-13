namespace NoniPilot.Agent.Tools;

/// <summary>
/// Trims which tool schemas actually get sent to the model for a given command. Every tool's
/// JSON schema (name + description + parameters) is real prompt-token weight on every single
/// request, and on CPU-only local inference that weight is a direct, measurable latency cost -
/// this is a genuine speed lever, not a correctness feature (the model can't call a tool it
/// wasn't offered, so being wrong here means a failed task, not just a slower one).
///
/// Deliberately conservative: each category (FileSystem/Application/ComputerControl) is
/// INCLUDED unless the command clearly, keyword-detectably belongs to a different category and
/// not this one. If a command matches none of the category keyword sets at all, every tool is
/// sent - same behavior as before this filter existed - because failing to offer a tool the
/// model genuinely needs is a much worse outcome than one slightly larger prompt.
/// </summary>
public static class ToolRelevanceFilter
{
    private static readonly string[] FileSystemKeywords =
    [
        "file", "folder", "directory", "desktop", "document", "download", "picture", "photo",
        "video", "music", "create", "delete", "remove", "rename", "move", "copy", "search",
        "find", "open", "pdf", "doc", "xlsx", "image",
    ];

    private static readonly string[] ApplicationKeywords =
    [
        "open", "launch", "start", "close", "quit", "exit", "app", "application", "program",
        "running", "chrome", "notepad", "calculator", "browser", "software", "explorer",
    ];

    private static readonly string[] ComputerControlKeywords =
    [
        "click", "type", "mouse", "scroll", "drag", "keyboard", "press", "key", "window",
        "focus", "cursor", "screen",
    ];

    public static IReadOnlyList<ToolSpec> SelectRelevantTools(string naturalLanguageCommand, IReadOnlyList<ToolSpec> allTools)
    {
        var normalized = naturalLanguageCommand.ToLowerInvariant();

        var matchesFileSystem = FileSystemKeywords.Any(normalized.Contains);
        var matchesApplication = ApplicationKeywords.Any(normalized.Contains);
        var matchesComputerControl = ComputerControlKeywords.Any(normalized.Contains);

        // No category matched at all - don't guess, send everything (the pre-filter behavior).
        if (!matchesFileSystem && !matchesApplication && !matchesComputerControl)
        {
            return allTools;
        }

        return allTools.Where(t => t.PolicyTool switch
        {
            "FileSystem" => matchesFileSystem,
            "Application" => matchesApplication,
            "ComputerControl" => matchesComputerControl,
            _ => true, // anything not in a known category is kept, not silently dropped
        }).ToList();
    }
}
