namespace NoniPilot.Domain.Interfaces;

/// <summary>
/// A small set of commands that must work instantly and deterministically, without waiting
/// on an LLM round-trip - most importantly "Stop" (section 4/10). Checked before every
/// command is handed to IPlannerService.
/// </summary>
public interface IIntentService
{
    /// <returns>The matched fast-path intent name (e.g. "stop", "cancel"), or null if this
    /// command isn't a fast-path match and should go to the full planner instead.</returns>
    string? TryMatchFastPathIntent(string naturalLanguageCommand);
}
