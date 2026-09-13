namespace NoniPilot.Domain.Interfaces;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// Direct mouse/keyboard/window control. Every method here is a privileged, policy-gated
/// action (section 8.4) - callers must go through IPolicyService first, this service does
/// not check policy itself.
/// </summary>
public interface IComputerControlService
{
    Task MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default);

    Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clickCount = 1, CancellationToken cancellationToken = default);

    Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken = default);

    /// <summary>
    /// Low-level press/release, distinct from ClickAsync/DragAsync: needed for continuous,
    /// real-time-driven manipulation (gesture pinch-and-drag) where the caller controls
    /// cursor position across many frames while the button stays held.
    /// </summary>
    void PressMouseButton(MouseButton button = MouseButton.Left);

    void ReleaseMouseButton(MouseButton button = MouseButton.Left);

    Task ScrollAsync(int deltaWheelClicks, CancellationToken cancellationToken = default);

    Task TypeTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Sends a key combination, e.g. "^c" (Ctrl+C), "%{F4}" (Alt+F4) - SendKeys-style syntax.</summary>
    Task SendKeysAsync(string keys, CancellationToken cancellationToken = default);

    Task<bool> FocusWindowAsync(string windowTitleContains, CancellationToken cancellationToken = default);

    /// <summary>
    /// Global kill switch (section 10/21.4): immediately stops any in-flight mouse/keyboard
    /// automation and releases held input state. Must work even if the agent loop is stuck.
    /// </summary>
    void EmergencyStop();
}
