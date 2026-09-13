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
    private const int IndexMcp = 5;
    private const int IndexTip = 8;
    private const int MiddleMcp = 9;
    private const int MiddleTip = 12;
    private const int RingMcp = 13;
    private const int RingTip = 16;
    private const int PinkyMcp = 17;
    private const int PinkyTip = 20;

    // A finger counts as "extended" when its tip sits meaningfully farther from the wrist than
    // its own base knuckle (MCP) does - a standard, per-finger alternative to the whole-hand
    // average ratio used for Fist/OpenPalm, needed to tell specific finger-combination poses
    // (peace sign, three-finger) apart from each other.
    private const float FingerExtendedMultiplier = 1.15f;

    // Swipe: accumulated normalized wrist-X travel counts as a deliberate sideways sweep once it
    // passes this threshold within SwipeMaxWindow of consistent direction; a cooldown afterward
    // stops the same physical sweep from re-triggering as it settles.
    private const float SwipeDistanceThreshold = 0.35f;
    private static readonly TimeSpan SwipeMaxWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SwipeCooldown = TimeSpan.FromMilliseconds(900);

    // Zoom: this pipeline has no real depth data, so frame-to-frame change in hand-span (a
    // stand-in for camera distance) stands in for "moved toward/away from the camera". Gated by
    // the tracked point barely moving so an ordinary drag (point moves, span roughly constant)
    // is never misread as a zoom.
    private const float ZoomSpanDeltaThreshold = 0.05f;
    private const float ZoomMaxPosDelta = 0.04f;

    private readonly GestureCalibration _calibration;
    private bool _wasPinching;

    private float? _lastSwipeWristX;
    private DateTime _swipeWindowStartUtc;
    private float _swipeAccum;
    private DateTime _swipeCooldownUntilUtc = DateTime.MinValue;

    private float _lastPinchSpan;
    private HandLandmark? _lastPinchPos;

    // CloseWindow/OpenExplorer are one-shot: without this, holding the pose for the ~1-2 seconds
    // it takes a person to notice it worked would fire Alt+F4 (or launch Explorer) dozens of
    // times. Tracks which one-shot pose most recently fired so it only fires again after the
    // hand visibly leaves that pose in between.
    private RecognizedGesture _lastOneShotPose = RecognizedGesture.None;

    public GestureClassifier(GestureCalibration? calibration = null)
    {
        _calibration = calibration ?? new GestureCalibration();
    }

    /// <summary>Resets pinch-hold and swipe-tracking state - call when tracking is (re)started or the hand is lost.</summary>
    public void Reset()
    {
        _wasPinching = false;
        ResetSwipeTracking();
        _swipeCooldownUntilUtc = DateTime.MinValue;
        _lastPinchSpan = 0f;
        _lastPinchPos = null;
        _lastOneShotPose = RecognizedGesture.None;
    }

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

        var indexExtended = IsExtended(indexTip, landmarks[IndexMcp], wrist);
        var middleExtended = IsExtended(middleTip, landmarks[MiddleMcp], wrist);
        var ringExtended = IsExtended(ringTip, landmarks[RingMcp], wrist);
        var pinkyExtended = IsExtended(pinkyTip, landmarks[PinkyMcp], wrist);

        var isPeaceSign = indexExtended && middleExtended && !ringExtended && !pinkyExtended;
        var isThreeFingerPose = indexExtended && middleExtended && ringExtended && !pinkyExtended;

        RecognizedGesture gesture;

        if (isFist)
        {
            gesture = RecognizedGesture.Fist;
            _wasPinching = false;
            ResetSwipeTracking();
            _lastOneShotPose = RecognizedGesture.None;
        }
        else if (isThumbUp)
        {
            gesture = RecognizedGesture.ThumbsUp;
            ResetSwipeTracking();
        }
        else if (isThumbDown)
        {
            gesture = RecognizedGesture.ThumbsDown;
            ResetSwipeTracking();
        }
        else if (isPeaceSign)
        {
            gesture = FireOnce(RecognizedGesture.CloseWindow);
        }
        else if (isThreeFingerPose)
        {
            gesture = FireOnce(RecognizedGesture.OpenExplorer);
        }
        else if (isOpenPalm)
        {
            _wasPinching = false;
            _lastOneShotPose = RecognizedGesture.None;
            gesture = ClassifySwipe(wrist) ?? RecognizedGesture.OpenPalm;
        }
        else if (isPinching && !_wasPinching)
        {
            gesture = RecognizedGesture.PinchStart;
            _wasPinching = true;
            _lastPinchSpan = handSpan;
            _lastPinchPos = indexTip;
            ResetSwipeTracking();
            _lastOneShotPose = RecognizedGesture.None;
        }
        else if (!isPinching && _wasPinching)
        {
            gesture = RecognizedGesture.PinchEnd;
            _wasPinching = false;
        }
        else if (isPinching)
        {
            // Held pinch, already reported as PinchStart on a prior frame - normally a
            // continued Point so the caller keeps updating drag position, unless the hand is
            // being pulled toward/away from the camera instead (see ClassifyHeldPinch).
            gesture = ClassifyHeldPinch(handSpan, indexTip);
        }
        else
        {
            _lastOneShotPose = RecognizedGesture.None;
            gesture = ClassifySwipe(wrist) ?? RecognizedGesture.Point;
        }

        return new GestureFrame(gesture, indexTip.X, indexTip.Y, pinchDistance, Confidence: 1f);
    }

    /// <summary>True when a fingertip sits meaningfully farther from the wrist than its own base knuckle - the finger is extended, not curled.</summary>
    private static bool IsExtended(HandLandmark tip, HandLandmark mcp, HandLandmark wrist) =>
        Distance(tip, wrist) > Distance(mcp, wrist) * FingerExtendedMultiplier;

    /// <summary>
    /// Reports a static pose gesture only on the frame it's newly held (not every frame it stays
    /// held) - see the _lastOneShotPose field doc comment for why that matters here.
    /// </summary>
    private RecognizedGesture FireOnce(RecognizedGesture pose)
    {
        if (_lastOneShotPose == pose)
        {
            return RecognizedGesture.None;
        }

        _lastOneShotPose = pose;
        ResetSwipeTracking();
        return pose;
    }

    private void ResetSwipeTracking()
    {
        _lastSwipeWristX = null;
        _swipeAccum = 0f;
    }

    /// <summary>
    /// Accumulates the wrist's horizontal travel while the hand stays in an OpenPalm/neutral
    /// pose; a fast, sustained sideways sweep is reported as one SwipeLeft/SwipeRight, then a
    /// cooldown blocks re-triggering on the tail of the same physical sweep. Returns null when
    /// no swipe just completed, so the caller falls back to its normal gesture for the frame.
    /// </summary>
    private RecognizedGesture? ClassifySwipe(HandLandmark wrist)
    {
        var now = DateTime.UtcNow;

        if (now < _swipeCooldownUntilUtc)
        {
            _lastSwipeWristX = wrist.X;
            return null;
        }

        if (_lastSwipeWristX is not { } lastX)
        {
            _lastSwipeWristX = wrist.X;
            _swipeWindowStartUtc = now;
            _swipeAccum = 0f;
            return null;
        }

        var dx = wrist.X - lastX;
        _lastSwipeWristX = wrist.X;

        if (_swipeAccum == 0f || MathF.Sign(dx) == MathF.Sign(_swipeAccum))
        {
            _swipeAccum += dx;
        }
        else
        {
            // Direction reversed - a genuine swipe doesn't backtrack, restart the window.
            _swipeAccum = dx;
            _swipeWindowStartUtc = now;
        }

        if (now - _swipeWindowStartUtc > SwipeMaxWindow)
        {
            // Too slow to be a deliberate swipe (drift) - restart from here instead of firing.
            _swipeAccum = dx;
            _swipeWindowStartUtc = now;
        }

        if (MathF.Abs(_swipeAccum) < SwipeDistanceThreshold)
        {
            return null;
        }

        var swiped = _swipeAccum > 0 ? RecognizedGesture.SwipeRight : RecognizedGesture.SwipeLeft;
        _swipeAccum = 0f;
        _swipeCooldownUntilUtc = now + SwipeCooldown;
        return swiped;
    }

    /// <summary>
    /// Held-pinch continuation: an ordinary drag keeps the tracked point moving while hand size
    /// barely changes; pulling the pinched hand toward/away from the camera instead changes
    /// hand-span with the tracked point roughly stationary - reported as ZoomIn/ZoomOut so a
    /// pinch can drive OS zoom instead of always dragging.
    /// </summary>
    private RecognizedGesture ClassifyHeldPinch(float handSpan, HandLandmark indexTip)
    {
        var spanDeltaRatio = _lastPinchSpan > 0 ? (handSpan - _lastPinchSpan) / _lastPinchSpan : 0f;
        var framePosDelta = _lastPinchPos is { } lastPos ? Distance(indexTip, lastPos) : 0f;

        _lastPinchSpan = handSpan;
        _lastPinchPos = indexTip;

        if (MathF.Abs(spanDeltaRatio) > ZoomSpanDeltaThreshold && framePosDelta < ZoomMaxPosDelta)
        {
            return spanDeltaRatio > 0 ? RecognizedGesture.ZoomIn : RecognizedGesture.ZoomOut;
        }

        return RecognizedGesture.Point;
    }

    private static float Distance(HandLandmark a, HandLandmark b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
