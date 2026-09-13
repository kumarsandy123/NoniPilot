using NoniPilot.Domain.Models;

namespace NoniPilot.Domain.Interfaces;

/// <summary>
/// Interfaces for capabilities scoped to later phases (see roadmap section 15). Declared now,
/// per Phase 0's "define interfaces and domain models before implementing adapters", so the
/// Agent core can be written against a stable contract before Voice/Vision/Gesture/Browser
/// exist. No implementation lives behind these yet.
/// </summary>

/// <summary>
/// Reserved for a future always-on / wake-word listening mode (section 8.1). Push-to-talk
/// voice input is implemented now via the concrete, request/response-shaped interfaces in
/// NoniPilot.Voice (IMicrophoneRecorder/ISpeechToTextService) instead - an always-listening
/// event stream didn't fit push-to-talk well, so this event-based shape is kept for later
/// rather than forced onto the current implementation.
/// </summary>
public interface ICommandInputService
{
    event EventHandler<string>? CommandReceived;

    void StartListening();

    void StopListening();
}

/// <summary>Phase 2: captures and interprets current desktop state (screen capture, UI Automation tree, OCR, vision fallback).</summary>
public interface IObservationService
{
    Task<Observation> CaptureAsync(string taskId, CancellationToken cancellationToken = default);
}

/// <summary>Phase 4: webcam hand tracking mapped to semantic commands (pointer, pinch, drag, scroll, zoom, stop).</summary>
public interface IGestureService
{
    event EventHandler<string>? GestureRecognized;

    void StartTracking();

    void StopTracking();
}

/// <summary>Phase 3: semantic browser automation (open/navigate/fill forms/download).</summary>
public interface IBrowserService
{
    Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Phase 5: loads capability adapters (plugins) under the same policy control as built-in services.</summary>
public interface IPluginService
{
    Task<IReadOnlyList<Plugin>> ListInstalledAsync(CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default);
}
