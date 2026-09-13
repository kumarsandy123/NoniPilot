using OpenCvSharp;

namespace NoniPilot.Gesture;

/// <summary>
/// Captures from the default webcam and runs it through detection + classification on a
/// background loop. Uses a fixed central square as the hand-tracking zone rather than a
/// learned palm detector - a deliberate scope simplification (no second ONNX model to find/
/// verify); see docs/architecture/decisions.md. The tradeoff: the hand must be held roughly
/// centered in frame, rather than being found anywhere in view.
/// </summary>
public sealed class WebcamGestureEngine : IGestureEngine
{
    private readonly IHandLandmarkDetector _detector;
    private readonly GestureClassifier _classifier;
    private readonly int _cameraIndex;

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning { get; private set; }

    private long _framesProcessed;
    private long _handDetections;
    private volatile string? _lastDetectionError;

    /// <summary>Frames processed since Start() - a live sign the camera is actually delivering
    /// frames, independent of whether any of them contained a hand.</summary>
    public long FramesProcessed => Interlocked.Read(ref _framesProcessed);

    /// <summary>How many of those frames had a hand detected in them.</summary>
    public long HandDetections => Interlocked.Read(ref _handDetections);

    /// <summary>The most recent exception IHandLandmarkDetector.Detect threw, if any. Previously
    /// every such exception was silently swallowed every single frame with no trace anywhere -
    /// exactly the kind of failure that looks identical to "gestures just don't do anything".</summary>
    public string? LastDetectionError => _lastDetectionError;

    public event EventHandler<GestureFrame>? GestureDetected;

    /// <summary>
    /// Fires once per captured frame with a Mat the subscriber OWNS and must Dispose - it is
    /// a clone, safe to use asynchronously, but not shared with the capture loop.
    /// </summary>
    public event EventHandler<Mat>? FramePreview;

    public WebcamGestureEngine(IHandLandmarkDetector detector, GestureCalibration? calibration = null, int cameraIndex = 0)
    {
        _detector = detector;
        _classifier = new GestureClassifier(calibration);
        _cameraIndex = cameraIndex;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _capture = new VideoCapture(_cameraIndex);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("Could not open the webcam (index " + _cameraIndex + ").");
        }

        _classifier.Reset();
        Interlocked.Exchange(ref _framesProcessed, 0);
        Interlocked.Exchange(ref _handDetections, 0);
        _lastDetectionError = null;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _loopTask = Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _cts?.Cancel();

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort join - the loop checks the cancellation token every frame and exits promptly.
        }

        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
    }

    // The camera's native rate (commonly 30fps) was being fully spent on ONNX hand-landmark
    // inference every single frame, all the time the camera is on (which is by default, for the
    // whole session) - measured live as a real contributor to high CPU usage (2026-09-13). Human
    // hand movement tracks perfectly well at a much lower rate; capping it here cuts inference
    // (and downstream preview-rendering/face-recognition work, since every consumer subscribes to
    // the same FramePreview event) by roughly half or more with no perceptible loss of
    // responsiveness for cursor control or gesture recognition.
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(66); // ~15fps cap

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        var lastProcessedUtc = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested && _capture is not null)
        {
            if (!_capture.Read(frame) || frame.Empty())
            {
                continue;
            }

            var now = DateTime.UtcNow;
            if (now - lastProcessedUtc < MinFrameInterval)
            {
                // A short, deliberate sleep here - not just `continue` - is load-bearing, not
                // cosmetic: VideoCapture.Read() is not guaranteed to block for a full frame
                // interval on every backend, and measured live (2026-09-13) this loop was
                // consuming several full CPU cores continuously even at idle. Without a real
                // sleep, a non-blocking Read() turns this throttle into a tight busy-spin that
                // burns CPU doing nothing useful, defeating the whole point of throttling.
                Thread.Sleep(5);
                continue;
            }

            lastProcessedUtc = now;
            Interlocked.Increment(ref _framesProcessed);

            using (var previewFrame = frame.Clone())
            {
                FramePreview?.Invoke(this, previewFrame);
            }

            var roi = CenterSquareRoi(frame);
            using var cropped = new Mat(frame, roi);

            HandDetectionResult result;
            try
            {
                result = _detector.Detect(cropped);
                _lastDetectionError = null;
            }
            catch (Exception ex)
            {
                _lastDetectionError = ex.Message;
                continue;
            }

            if (result.HandDetected)
            {
                Interlocked.Increment(ref _handDetections);
                var gestureFrame = _classifier.Classify(result.Landmarks);
                GestureDetected?.Invoke(this, gestureFrame);
            }
            else
            {
                _classifier.Reset();
            }
        }
    }

    private static Rect CenterSquareRoi(Mat frame)
    {
        var size = Math.Min(frame.Width, frame.Height);
        var x = (frame.Width - size) / 2;
        var y = (frame.Height - size) / 2;
        return new Rect(x, y, size, size);
    }

    public void Dispose() => Stop();
}
