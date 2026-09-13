using System.Windows;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Gesture;
using OpenCvSharp;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Owns the one running gesture-tracking engine instance so the Dashboard's "Gesture Mode"
/// toggle and the Gesture Control page's Start/Stop button both control the exact same camera
/// session, instead of each independently starting its own webcam capture.
/// </summary>
public sealed class GestureEngineController : IDisposable, IGestureAwarenessService
{
    private readonly IComputerControlService _computerControl;
    private WebcamGestureEngine? _engine;
    private GestureActionMapper? _mapper;
    private DateTime _lastGestureAtUtc;

    public string? ModelPath { get; } = GestureModelLocator.FindModelPath();
    public bool IsRunning => _engine is { IsRunning: true };
    public RecognizedGesture LastGesture { get; private set; } = RecognizedGesture.None;

    /// <summary>
    /// Backs "can you see me?" - deliberately honest rather than always claiming to see
    /// something: the camera can be off, on with no hand currently in frame, or on with a
    /// recently recognized gesture. "Recently" (2s) rather than "ever" so a stale gesture from
    /// minutes ago isn't reported as if the hand were still there right now.
    /// </summary>
    public GestureAwarenessStatus GetStatus()
    {
        if (!IsRunning)
        {
            return new GestureAwarenessStatus(CameraActive: false, HandDetected: false, CurrentGesture: null);
        }

        var handDetected = LastGesture != RecognizedGesture.None && DateTime.UtcNow - _lastGestureAtUtc < TimeSpan.FromSeconds(2);
        return new GestureAwarenessStatus(CameraActive: true, HandDetected: handDetected, CurrentGesture: handDetected ? LastGesture.ToString() : null);
    }

    public event EventHandler<Mat>? FramePreview;
    public event EventHandler<GestureFrame>? GestureDetected;
    public event Action? StateChanged;

    public GestureEngineController(IComputerControlService computerControl) => _computerControl = computerControl;

    public bool Start(GestureCalibration calibration, Action onEmergencyStop)
    {
        if (IsRunning || ModelPath is null)
        {
            return false;
        }

        var detector = new OnnxHandLandmarkDetector(ModelPath);
        _engine = new WebcamGestureEngine(detector, calibration);
        _mapper = new GestureActionMapper(
            _computerControl,
            onEmergencyStop,
            (int)SystemParameters.PrimaryScreenWidth,
            (int)SystemParameters.PrimaryScreenHeight);

        _engine.FramePreview += OnFramePreview;
        _engine.GestureDetected += OnGestureDetected;
        _engine.Start();
        StateChanged?.Invoke();
        return true;
    }

    private void OnFramePreview(object? sender, Mat frame) => FramePreview?.Invoke(sender, frame);

    private void OnGestureDetected(object? sender, GestureFrame frame)
    {
        LastGesture = frame.Gesture;
        _lastGestureAtUtc = DateTime.UtcNow;
        _mapper?.Handle(frame);
        GestureDetected?.Invoke(sender, frame);
    }

    public void Stop()
    {
        if (_engine is null)
        {
            return;
        }

        _engine.FramePreview -= OnFramePreview;
        _engine.GestureDetected -= OnGestureDetected;
        _engine.Stop();
        _engine.Dispose();
        _engine = null;
        LastGesture = RecognizedGesture.None;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
