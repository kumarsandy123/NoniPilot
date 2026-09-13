using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Gesture;

/// <summary>
/// Translates classified gestures into real mouse/keyboard/app actions, using the exact same
/// IComputerControlService/IApplicationService instances as text/voice commands - a fist calls
/// the same EmergencyStop() as the Desktop STOP button (section 8.8/10: "fist emergency stop").
///
/// Scope for this pass (see docs/architecture/decisions.md): Point/Pinch/Fist/OpenPalm/Swipe/
/// Zoom/CloseWindow/OpenExplorer are wired to real actions - Point AND OpenPalm both move the
/// cursor (an open, relaxed hand is the natural resting pose while pointing, so cursor tracking
/// can't depend on a narrower classification), a held Pinch drags (pick up/drop a folder or file
/// exactly like a physical mouse button - a quick pinch with little movement is a click, since
/// the press and release land at the same spot), Swipe moves the focused window to the next/
/// previous connected monitor, pulling a held Pinch toward/away from the camera zooms in/out,
/// and the peace-sign/three-finger poses close the focused window / open a new File Explorer
/// window. ThumbsUp/ThumbsDown are recognized and reported but not yet wired to the confirmation
/// dialog.
/// </summary>
public sealed class GestureActionMapper
{
    private readonly IComputerControlService _computerControl;
    private readonly IApplicationService _applications;
    private readonly Action _onEmergencyStop;

    private bool _isPressed;

    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    public GestureActionMapper(
        IComputerControlService computerControl,
        IApplicationService applications,
        Action onEmergencyStop,
        int screenWidth,
        int screenHeight)
    {
        _computerControl = computerControl;
        _applications = applications;
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
                // Keep tracking the cursor while open - a relaxed open hand is a completely
                // normal "just moving the pointer" pose, not only the narrower Point classification.
                ReleaseIfPressed();
                _ = _computerControl.MoveMouseAsync(screenX, screenY);
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

            case RecognizedGesture.SwipeRight:
                ReleaseIfPressed();
                _ = _computerControl.MoveWindowToAdjacentMonitorAsync(direction: 1);
                break;

            case RecognizedGesture.SwipeLeft:
                ReleaseIfPressed();
                _ = _computerControl.MoveWindowToAdjacentMonitorAsync(direction: -1);
                break;

            case RecognizedGesture.ZoomIn:
                // A zoom mid-pinch means the hand moved toward the camera rather than dragging -
                // release any button PinchStart already pressed so the zoom scroll doesn't also
                // drag whatever was under the cursor.
                ReleaseIfPressed();
                _ = _computerControl.CtrlScrollAsync(deltaWheelClicks: 1);
                break;

            case RecognizedGesture.ZoomOut:
                ReleaseIfPressed();
                _ = _computerControl.CtrlScrollAsync(deltaWheelClicks: -1);
                break;

            case RecognizedGesture.CloseWindow:
                // Alt+F4 closes whichever window currently has focus - reported live
                // (2026-09-13) as closing NoniPilot itself every time, because NoniPilot's own
                // window was the one focused while gesturing at it. Refuse when that's the case
                // instead of ever taking down the app that's supposed to be running the camera.
                ReleaseIfPressed();
                if (!_computerControl.IsSelfForeground())
                {
                    _ = _computerControl.SendKeysAsync("%{F4}");
                }
                break;

            case RecognizedGesture.OpenExplorer:
                ReleaseIfPressed();
                _ = _applications.LaunchAsync("explorer");
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
