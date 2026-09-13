using System.Diagnostics;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Browser;

/// <summary>
/// A deliberately scoped implementation: open a URL in the user's default browser, nothing
/// more. Not a Selenium/Playwright-style DOM automation engine - "semantic browser automation"
/// from the original roadmap section 15 is a much larger future project; this is the concrete,
/// real thing that can ship today. Uses the exact same mechanism already proven for
/// protocol/URL activation in WindowsApplicationService.LaunchAsync (Process.Start with
/// UseShellExecute=true correctly hands URLs to whatever the OS has registered to open them).
/// </summary>
public sealed class BrowserService : IBrowserService
{
    public Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(false);
        }

        var normalized = url.Contains("://") ? url : $"https://{url}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(normalized) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
