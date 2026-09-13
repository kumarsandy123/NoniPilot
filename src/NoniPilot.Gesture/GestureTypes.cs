namespace NoniPilot.Gesture;

/// <summary>The semantic gestures NoniPilot maps hand landmarks to (roadmap section 8.8/12).</summary>
public enum RecognizedGesture
{
    None,

    /// <summary>Index fingertip tracked - moves the cursor.</summary>
    Point,

    /// <summary>Thumb+index pinch just closed - click, or the start of a drag.</summary>
    PinchStart,

    /// <summary>A pinch that was held and is now released - ends a drag.</summary>
    PinchEnd,

    /// <summary>All fingers extended and spread - pause/release gesture control.</summary>
    OpenPalm,

    /// <summary>A closed fist - the gesture-channel emergency stop.</summary>
    Fist,

    ThumbsUp,
    ThumbsDown,
}

/// <summary>One classified frame: the gesture recognized (if any), the tracked point in [0,1] ROI-normalized coordinates, and confidence.</summary>
public sealed record GestureFrame(
    RecognizedGesture Gesture,
    float NormalizedX,
    float NormalizedY,
    float PinchDistance,
    float Confidence);

/// <summary>21 MediaPipe-scheme hand landmarks (wrist=0 ... pinky_tip=20), normalized to the detector's input crop.</summary>
public sealed record HandLandmark(float X, float Y, float Z);

public sealed class HandDetectionResult
{
    public bool HandDetected { get; init; }
    public IReadOnlyList<HandLandmark> Landmarks { get; init; } = Array.Empty<HandLandmark>();
    public float Confidence { get; init; }
}

/// <summary>Runs the hand-landmark model on one cropped frame region.</summary>
public interface IHandLandmarkDetector
{
    HandDetectionResult Detect(OpenCvSharp.Mat croppedRoiBgr);
}

/// <summary>
/// The full gesture pipeline: camera capture -> crop -> landmark detection -> gesture
/// classification, surfaced as a stream of GestureFrame events (section 8.8).
/// </summary>
public interface IGestureEngine : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<GestureFrame>? GestureDetected;

    /// <summary>Fires for every captured frame regardless of whether a hand was found - drives the camera preview.</summary>
    event EventHandler<OpenCvSharp.Mat>? FramePreview;

    void Start();

    void Stop();
}
