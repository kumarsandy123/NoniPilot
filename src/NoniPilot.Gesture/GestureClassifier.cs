namespace NoniPilot.Gesture;

/// <summary>Calibration/sensitivity settings (section 8.8/11: "Calibration, sensitivity and dwell-time settings").</summary>
public sealed class GestureCalibration
{
    /// <summary>Thumb-tip-to-index-tip distance, as a ratio of hand span, below which a pinch is detected.</summary>
    public float PinchThreshold { get; set; } = 0.35f;

    /// <summary>Average fingertip-to-wrist distance (ratio of hand span) below which a fist is detected.</summary>
    public float FistThreshold { get; set; } = 0.45f;

    /// <summary>Average fingertip-to-wrist distance (ratio of hand span) above which an open palm is detected.</summary>
    public float OpenPalmThreshold { get; set; } = 0.85f;

    public int DwellTimeMs { get; set; } = 400;
}

/// <summary>
/// Pure landmark-geometry classification - no camera, no ML model, fully unit-testable with
/// synthetic landmark data (see tests/Unit/GestureClassifierTests.cs). This is deliberately
/// separated from IHandLandmarkDetector so the one part of the gesture pipeline that doesn't
/// require a live camera to verify is tested as such.
/// </summary>
public sealed class GestureClassifier
{
    // MediaPipe's 21-point hand landmark scheme.
    private const int Wrist = 0;
    private const int ThumbTip = 4;
    private const int IndexTip = 8;
    private const int MiddleMcp = 9;
    private const int MiddleTip = 12;
    private const int RingTip = 16;
    private const int PinkyTip = 20;

    private readonly GestureCalibration _calibration;
    private bool _wasPinching;

    public GestureClassifier(GestureCalibration? calibration = null)
    {
        _calibration = calibration ?? new GestureCalibration();
    }

    /// <summary>Resets pinch-hold state - call when tracking is (re)started or the hand is lost.</summary>
    public void Reset() => _wasPinching = false;

    public GestureFrame Classify(IReadOnlyList<HandLandmark> landmarks)
    {
        if (landmarks.Count < 21)
        {
            return new GestureFrame(RecognizedGesture.None, 0, 0, 0, 0);
        }

        var wrist = landmarks[Wrist];
        var thumbTip = landmarks[ThumbTip];
        var indexTip = landmarks[IndexTip];
        var middleTip = landmarks[MiddleTip];
        var ringTip = landmarks[RingTip];
        var pinkyTip = landmarks[PinkyTip];
        var middleMcp = landmarks[MiddleMcp];

        var handSpan = MathF.Max(Distance(wrist, middleMcp), 0.0001f);
        var pinchDistance = Distance(thumbTip, indexTip);
        var pinchRatio = pinchDistance / handSpan;

        var avgFingertipToWristRatio = (
            Distance(indexTip, wrist) +
            Distance(middleTip, wrist) +
            Distance(ringTip, wrist) +
            Distance(pinkyTip, wrist)) / 4f / handSpan;

        var isFist = avgFingertipToWristRatio < _calibration.FistThreshold;
        var isOpenPalm = avgFingertipToWristRatio > _calibration.OpenPalmThreshold;
        var isPinching = pinchRatio < _calibration.PinchThreshold;

        // Thumb pointing well above (up) or below (down) the wrist, with the other fingers
        // not fanned wide open - a coarse but workable thumbs-up/down heuristic.
        var thumbVerticalOffset = (wrist.Y - thumbTip.Y) / handSpan;
        var isThumbUp = !isFist && !isOpenPalm && thumbVerticalOffset > 0.6f;
        var isThumbDown = !isFist && !isOpenPalm && thumbVerticalOffset < -0.6f;

        RecognizedGesture gesture;

        if (isFist)
        {
            gesture = RecognizedGesture.Fist;
            _wasPinching = false;
        }
        else if (isThumbUp)
        {
            gesture = RecognizedGesture.ThumbsUp;
        }
        else if (isThumbDown)
        {
            gesture = RecognizedGesture.ThumbsDown;
        }
        else if (isOpenPalm)
        {
            gesture = RecognizedGesture.OpenPalm;
            _wasPinching = false;
        }
        else if (isPinching && !_wasPinching)
        {
            gesture = RecognizedGesture.PinchStart;
            _wasPinching = true;
        }
        else if (!isPinching && _wasPinching)
        {
            gesture = RecognizedGesture.PinchEnd;
            _wasPinching = false;
        }
        else if (isPinching)
        {
            // Held pinch, already reported as PinchStart on a prior frame - report as a
            // continued Point so the caller keeps updating drag position.
            gesture = RecognizedGesture.Point;
        }
        else
        {
            gesture = RecognizedGesture.Point;
        }

        return new GestureFrame(gesture, indexTip.X, indexTip.Y, pinchDistance, Confidence: 1f);
    }

    private static float Distance(HandLandmark a, HandLandmark b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
