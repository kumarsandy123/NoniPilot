namespace NoniPilot.Domain.Interfaces;

public sealed record RunningApplication(int ProcessId, string ProcessName, string? MainWindowTitle);

/// <summary>Application lifecycle control (section 8.6): launch, focus, close, enumerate.</summary>
public interface IApplicationService
{
    Task<int> LaunchAsync(string appPathOrName, string? arguments = null, CancellationToken cancellationToken = default);

    Task<bool> FocusAsync(string processNameOrWindowTitle, CancellationToken cancellationToken = default);

    Task<bool> CloseAsync(int processId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunningApplication>> ListRunningAsync(CancellationToken cancellationToken = default);
}
