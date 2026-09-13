using NoniPilot.Gesture;

namespace NoniPilot.Tests.Unit;

/// <summary>
/// Synthetic-landmark tests for the one part of the gesture pipeline that doesn't require a
/// live camera/hand to verify: the geometry-based classification logic. Coordinates below are
/// hand-constructed (not captured from a real hand) to exercise each branch deliberately.
/// </summary>
public class GestureClassifierTests
{
    private static HandLandmark L(float x, float y) => new(x, y, 0f);

    /// <summary>Wrist at (0.5, 0.9), middle-MCP at (0.5, 0.6) -> hand span 0.3, matching every scenario below.</summary>
    private static List<HandLandmark> BuildHand(float thumbX, float thumbY, float fingertipY)
    {
        var landmarks = new List<HandLandmark>();
        for (var i = 0; i < 21; i++)
        {
            landmarks.Add(L(0.5f, 0.9f)); // placeholder, overwritten below for the indices that matter
        }

        landmarks[0] = L(0.5f, 0.9f);   // wrist
        landmarks[4] = L(thumbX, thumbY); // thumb tip
        landmarks[8] = L(0.5f, fingertipY);  // index tip
        landmarks[9] = L(0.5f, 0.6f);   // middle MCP (hand-span reference)
        landmarks[12] = L(0.5f, fingertipY); // middle tip
        landmarks[16] = L(0.5f, fingertipY); // ring tip
        landmarks[20] = L(0.5f, fingertipY); // pinky tip

        return landmarks;
    }

    [Fact]
    public void Classify_FingertipsNearWrist_DetectsFist()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.5f, thumbY: 0.85f, fingertipY: 0.85f); // fingertips barely off the wrist

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.Fist, result.Gesture);
    }

    [Fact]
    public void Classify_FingertipsFarFromWrist_DetectsOpenPalm()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.3f, thumbY: 0.75f, fingertipY: 0.1f); // fingertips near the top of frame

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.OpenPalm, result.Gesture);
    }

    [Fact]
    public void Classify_ThumbAndIndexTouching_DetectsPinchStart()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.5f, thumbY: 0.751f, fingertipY: 0.75f); // thumb tip ~= index tip

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.PinchStart, result.Gesture);
    }

    [Fact]
    public void Classify_PinchThenRelease_ReportsPinchEndOnce()
    {
        var classifier = new GestureClassifier();
        var pinching = BuildHand(thumbX: 0.5f, thumbY: 0.751f, fingertipY: 0.75f);
        var released = BuildHand(thumbX: 0.3f, thumbY: 0.75f, fingertipY: 0.75f); // thumb moved away

        var first = classifier.Classify(pinching);
        var second = classifier.Classify(released);
        var third = classifier.Classify(released);

        Assert.Equal(RecognizedGesture.PinchStart, first.Gesture);
        Assert.Equal(RecognizedGesture.PinchEnd, second.Gesture);
        Assert.Equal(RecognizedGesture.Point, third.Gesture); // not re-reported every frame
    }

    [Fact]
    public void Classify_ThumbHighAboveCurledFingers_DetectsThumbsUp()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.3f, thumbY: 0.3f, fingertipY: 0.75f); // thumb well above wrist, fingers curled

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.ThumbsUp, result.Gesture);
    }

    [Fact]
    public void Classify_ThumbBelowCurledFingers_DetectsThumbsDown()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.3f, thumbY: 1.3f, fingertipY: 0.75f); // thumb well below wrist, fingers curled

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.ThumbsDown, result.Gesture);
    }

    [Fact]
    public void Classify_NeutralHandOpenNoPinch_DetectsPoint()
    {
        var classifier = new GestureClassifier();
        var landmarks = BuildHand(thumbX: 0.3f, thumbY: 0.75f, fingertipY: 0.75f); // thumb apart from index, moderate extension

        var result = classifier.Classify(landmarks);

        Assert.Equal(RecognizedGesture.Point, result.Gesture);
    }

    [Fact]
    public void Classify_FewerThan21Landmarks_ReturnsNone()
    {
        var classifier = new GestureClassifier();

        var result = classifier.Classify(new List<HandLandmark> { L(0, 0) });

        Assert.Equal(RecognizedGesture.None, result.Gesture);
    }

    /// <summary>Open-palm hand centered at wristX, with hand span fixed at 0.3 regardless of wristX so
    /// isOpenPalm stays true as the hand sweeps sideways across calls.</summary>
    private static List<HandLandmark> BuildOpenPalmHand(float wristX)
    {
        const float wristY = 0.9f;
        var landmarks = new List<HandLandmark>();
        for (var i = 0; i < 21; i++)
        {
            landmarks.Add(L(wristX, wristY));
        }

        landmarks[0] = L(wristX, wristY);              // wrist
        landmarks[9] = L(wristX, wristY - 0.3f);        // middle MCP -> hand span 0.3
        landmarks[4] = L(wristX - 0.2f, wristY - 0.1f); // thumb tip, far from index tip
        landmarks[8] = L(wristX, wristY - 0.8f);        // index tip
        landmarks[12] = L(wristX, wristY - 0.8f);
        landmarks[16] = L(wristX, wristY - 0.8f);
        landmarks[20] = L(wristX, wristY - 0.8f);

        return landmarks;
    }

    [Fact]
    public void Classify_FastSidewaysSweepRight_DetectsSwipeRight()
    {
        var classifier = new GestureClassifier();
        var sawSwipe = false;

        for (var x = 0.1f; x <= 0.9f; x += 0.1f)
        {
            if (classifier.Classify(BuildOpenPalmHand(x)).Gesture == RecognizedGesture.SwipeRight)
            {
                sawSwipe = true;
            }
        }

        Assert.True(sawSwipe);
    }

    [Fact]
    public void Classify_FastSidewaysSweepLeft_DetectsSwipeLeft()
    {
        var classifier = new GestureClassifier();
        var sawSwipe = false;

        for (var x = 0.9f; x >= 0.1f; x -= 0.1f)
        {
            if (classifier.Classify(BuildOpenPalmHand(x)).Gesture == RecognizedGesture.SwipeLeft)
            {
                sawSwipe = true;
            }
        }

        Assert.True(sawSwipe);
    }

    /// <summary>A pinching hand whose hand span (proxy for camera distance) is the given value, with
    /// the fingertip-to-wrist ratio held constant at 0.5 across calls so the frame never crosses into
    /// Fist/OpenPalm territory purely from the span changing.</summary>
    private static List<HandLandmark> BuildPinchHand(float handSpan)
    {
        const float wristX = 0.5f, wristY = 0.9f;
        var fingertipY = wristY - 0.5f * handSpan;

        var landmarks = new List<HandLandmark>();
        for (var i = 0; i < 21; i++)
        {
            landmarks.Add(L(wristX, wristY));
        }

        landmarks[0] = L(wristX, wristY);               // wrist
        landmarks[9] = L(wristX, wristY - handSpan);     // middle MCP -> defines hand span
        landmarks[4] = L(wristX, fingertipY + 0.001f);   // thumb tip, ~= index tip -> pinching
        landmarks[8] = L(wristX, fingertipY);            // index tip
        landmarks[12] = L(wristX, fingertipY);
        landmarks[16] = L(wristX, fingertipY);
        landmarks[20] = L(wristX, fingertipY);

        return landmarks;
    }

    [Fact]
    public void Classify_HeldPinchHandMovesCloserToCamera_DetectsZoomIn()
    {
        var classifier = new GestureClassifier();

        var start = classifier.Classify(BuildPinchHand(0.30f));
        var zoom = classifier.Classify(BuildPinchHand(0.33f)); // span grew 10%, tracked point barely moved

        Assert.Equal(RecognizedGesture.PinchStart, start.Gesture);
        Assert.Equal(RecognizedGesture.ZoomIn, zoom.Gesture);
    }

    [Fact]
    public void Classify_HeldPinchHandMovesAwayFromCamera_DetectsZoomOut()
    {
        var classifier = new GestureClassifier();

        var start = classifier.Classify(BuildPinchHand(0.30f));
        var zoom = classifier.Classify(BuildPinchHand(0.27f)); // span shrank 10%, tracked point barely moved

        Assert.Equal(RecognizedGesture.PinchStart, start.Gesture);
        Assert.Equal(RecognizedGesture.ZoomOut, zoom.Gesture);
    }

    /// <summary>A hand whose four fingers are independently extended or curled (thumb kept well
    /// away from every fingertip so it's never mistaken for a pinch), with every MCP joint at the
    /// same reference distance from the wrist so only the requested tip positions vary.</summary>
    private static List<HandLandmark> BuildFingerPose(bool indexExtended, bool middleExtended, bool ringExtended, bool pinkyExtended)
    {
        const float wristX = 0.5f, wristY = 0.9f;
        const float mcpY = 0.6f;
        const float extendedTipY = 0.3f;
        const float curledTipY = 0.85f;

        var landmarks = new List<HandLandmark>();
        for (var i = 0; i < 21; i++)
        {
            landmarks.Add(L(wristX, wristY));
        }

        landmarks[0] = L(wristX, wristY);   // wrist
        landmarks[4] = L(0.2f, 0.8f);       // thumb tip - far from every fingertip below, never pinching
        landmarks[5] = L(wristX, mcpY);     // index MCP
        landmarks[9] = L(wristX, mcpY);     // middle MCP (also the hand-span reference)
        landmarks[13] = L(wristX, mcpY);    // ring MCP
        landmarks[17] = L(wristX, mcpY);    // pinky MCP

        landmarks[8] = L(wristX, indexExtended ? extendedTipY : curledTipY);
        landmarks[12] = L(wristX, middleExtended ? extendedTipY : curledTipY);
        landmarks[16] = L(wristX, ringExtended ? extendedTipY : curledTipY);
        landmarks[20] = L(wristX, pinkyExtended ? extendedTipY : curledTipY);

        return landmarks;
    }

    [Fact]
    public void Classify_PeaceSign_DetectsCloseWindowOnceThenStopsRefiring()
    {
        var classifier = new GestureClassifier();
        var peaceSign = BuildFingerPose(indexExtended: true, middleExtended: true, ringExtended: false, pinkyExtended: false);

        var first = classifier.Classify(peaceSign);
        var second = classifier.Classify(peaceSign);

        Assert.Equal(RecognizedGesture.CloseWindow, first.Gesture);
        Assert.Equal(RecognizedGesture.None, second.Gesture); // still held - must not re-fire every frame
    }

    [Fact]
    public void Classify_ThreeFingerPose_DetectsOpenExplorerOnceThenStopsRefiring()
    {
        var classifier = new GestureClassifier();
        var threeFingers = BuildFingerPose(indexExtended: true, middleExtended: true, ringExtended: true, pinkyExtended: false);

        var first = classifier.Classify(threeFingers);
        var second = classifier.Classify(threeFingers);

        Assert.Equal(RecognizedGesture.OpenExplorer, first.Gesture);
        Assert.Equal(RecognizedGesture.None, second.Gesture);
    }

    [Fact]
    public void Classify_PeaceSignReleasedThenRepeated_FiresCloseWindowAgain()
    {
        var classifier = new GestureClassifier();
        var peaceSign = BuildFingerPose(indexExtended: true, middleExtended: true, ringExtended: false, pinkyExtended: false);
        var openPalm = BuildFingerPose(indexExtended: true, middleExtended: true, ringExtended: true, pinkyExtended: true);

        classifier.Classify(peaceSign);
        classifier.Classify(openPalm); // hand releases the pose in between
        var third = classifier.Classify(peaceSign);

        Assert.Equal(RecognizedGesture.CloseWindow, third.Gesture);
    }
}
