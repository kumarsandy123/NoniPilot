using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Gesture;

/// <summary>
/// Translates classified gestures into real mouse actions, using the exact same
/// IComputerControlService instance as text/voice commands - a fist calls the same
/// EmergencyStop() as the Desktop STOP button (section 8.8/10: "fist emergency stop").
///
/// Scope for this pass (see docs/architecture/decisions.md): Point/Pinch/Fist/OpenPalm are
/// wired to real actions. ThumbsUp/ThumbsDown are recognized and reported but not yet wired
/// to the confirmation dialog. Scroll and zoom gestures are not implemented yet.
/// </summary>
public sealed class GestureActionMapper
{
    private readonly IComputerControlService _computerControl;
    private readonly Action _onEmergencyStop;

    private bool _isPressed;

    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    public GestureActionMapper(IComputerControlService computerControl, Action onEmergencyStop, int screenWidth, int screenHeight)
    {
        _computerControl = computerControl;
        _onEmergencyStop = onEmergencyStop;
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
    }

    public void Handle(GestureFrame frame)
    {
        // Mirror X: facing the camera, moving your hand right should move the cursor right.
        var screenX = Math.Clamp((int)((1f - frame.NormalizedX) * ScreenWidth), 0, ScreenWidth - 1);
        var screenY = Math.Clamp((int)(frame.NormalizedY * ScreenHeight), 0, ScreenHeight - 1);

        switch (frame.Gesture)
        {
            case RecognizedGesture.Fist:
                ReleaseIfPressed();
                _onEmergencyStop();
                break;

            case RecognizedGesture.OpenPalm:
                ReleaseIfPressed();
                break;

            case RecognizedGesture.PinchStart:
                _ = _computerControl.MoveMouseAsync(screenX, screenY);
                _computerControl.PressMouseButton();
                _isPressed = true;
                break;

            case RecognizedGesture.PinchEnd:
                ReleaseIfPressed();
                break;

            case RecognizedGesture.Point:
                _ = _computerControl.MoveMouseAsync(screenX, screenY);
                break;

            case RecognizedGesture.ThumbsUp:
            case RecognizedGesture.ThumbsDown:
            case RecognizedGesture.None:
                break;
        }
    }

    private void ReleaseIfPressed()
    {
        if (_isPressed)
        {
            _computerControl.ReleaseMouseButton();
            _isPressed = false;
        }
    }
}
