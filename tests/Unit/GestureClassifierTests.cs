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
}
