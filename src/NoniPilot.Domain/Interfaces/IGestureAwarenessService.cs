namespace NoniPilot.Domain.Interfaces;

/// <summary>Real, current state of the gesture-tracking camera - not a vision/scene-description
/// capability (NoniPilot.Vision is still an unimplemented stub), just an honest report of what
/// the existing hand-tracking pipeline actually knows right now.</summary>
public sealed record GestureAwarenessStatus(bool CameraActive, bool HandDetected, string? CurrentGesture);

/// <summary>
/// Lets the AI answer "can you see me?" / "kya tum mujhe dekh rahe ho?" truthfully instead of
/// guessing or hallucinating - it can only ever report on real hand-tracking data (is the camera
/// even on, is a hand currently detected, what gesture was just recognized), never a general
/// description of the room or what the user is "doing" beyond that.
/// </summary>
public interface IGestureAwarenessService
{
    GestureAwarenessStatus GetStatus();
}
