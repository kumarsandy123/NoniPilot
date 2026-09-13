using OpenCvSharp;
using OpenCvSharp.Face;

namespace NoniPilot.Gesture;

public sealed record DetectedFace(Mat GrayCrop, Rect Bounds);

/// <summary>
/// Real, local, offline face detection (Haar cascade) + recognition (LBPH - Local Binary
/// Patterns Histograms) - both are classic OpenCV algorithms that ship in the same
/// OpenCvSharp4/OpenCvSharp4.runtime.win packages this project already depends on for gesture
/// tracking, so no new ONNX model or heavier dependency was needed. LBPH trains directly from a
/// handful of the user's own captured face images (no pretrained face-embedding model to source
/// at all), which is exactly what a single-user "recognize it's me" enrollment needs.
///
/// LBPH's Predict() returns a distance where LOWER means more similar (not a 0-1 confidence) -
/// values below ~70-80 are a typical "same person" threshold for LBPH on a well-lit, front-facing
/// crop; this is a widely-used empirical default, not tuned against this specific camera.
/// </summary>
public sealed class FaceRecognitionEngine : IDisposable
{
    private const double MatchDistanceThreshold = 80.0;
    private const int FaceCropSize = 200;

    private readonly CascadeClassifier _cascade;
    private LBPHFaceRecognizer? _recognizer;

    public bool IsEnrolled { get; private set; }

    private FaceRecognitionEngine(CascadeClassifier cascade)
    {
        _cascade = cascade;

        if (File.Exists(FaceModelLocator.TrainedFaceModelPath))
        {
            var recognizer = LBPHFaceRecognizer.Create();
            recognizer.Read(FaceModelLocator.TrainedFaceModelPath);
            _recognizer = recognizer;
            IsEnrolled = true;
        }
    }

    public static async Task<FaceRecognitionEngine> CreateAsync(CancellationToken cancellationToken = default)
    {
        var cascadePath = await FaceModelLocator.EnsureCascadeAsync(cancellationToken).ConfigureAwait(false);
        return new FaceRecognitionEngine(new CascadeClassifier(cascadePath));
    }

    /// <summary>
    /// Detects the single largest face in the frame (the person actually using the computer, if
    /// more than one face is visible), cropped to a fixed size and converted to grayscale - LBPH
    /// requires consistent-size grayscale input for both training and prediction.
    /// </summary>
    public DetectedFace? DetectLargestFace(Mat colorFrame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(colorFrame, gray, ColorConversionCodes.BGR2GRAY);

        var faces = _cascade.DetectMultiScale(gray, scaleFactor: 1.2, minNeighbors: 5, minSize: new Size(80, 80));
        if (faces.Length == 0)
        {
            return null;
        }

        var largest = faces.OrderByDescending(r => r.Width * r.Height).First();
        var crop = new Mat(gray, largest);
        var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(FaceCropSize, FaceCropSize));
        return new DetectedFace(resized, largest);
    }

    /// <returns>True if the face matched the enrolled person within the distance threshold.</returns>
    public bool Recognize(Mat grayFaceCrop, out double distance)
    {
        if (_recognizer is null)
        {
            distance = double.MaxValue;
            return false;
        }

        _recognizer.Predict(grayFaceCrop, out _, out distance);
        return distance <= MatchDistanceThreshold;
    }

    /// <summary>
    /// Trains a fresh model from a batch of already-detected face crops (all labeled as the same
    /// single enrolled person - label 1) and persists it, replacing any previous enrollment.
    /// </summary>
    public void TrainAndSave(IReadOnlyList<Mat> faceCrops)
    {
        var recognizer = LBPHFaceRecognizer.Create();
        var labels = faceCrops.Select(_ => 1).ToArray();
        recognizer.Train(faceCrops, labels);

        Directory.CreateDirectory(FaceModelLocator.ModelsDirectory);
        recognizer.Write(FaceModelLocator.TrainedFaceModelPath);

        _recognizer?.Dispose();
        _recognizer = recognizer;
        IsEnrolled = true;
    }

    public void Dispose()
    {
        _cascade.Dispose();
        _recognizer?.Dispose();
    }
}
