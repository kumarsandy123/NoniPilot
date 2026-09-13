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
    private readonly IApplicationService _applications;
    private WebcamGestureEngine? _engine;
    private GestureActionMapper? _mapper;
    private DateTime _lastGestureAtUtc;

    public string? ModelPath { get; } = GestureModelLocator.FindModelPath();
    public bool IsRunning => _engine is { IsRunning: true };
    public RecognizedGesture LastGesture { get; private set; } = RecognizedGesture.None;

    /// <summary>Set when Start() fails - surfaced by the UI instead of a bare "could not start"
    /// so a real cause (camera in use by another app, no webcam, etc.) is actually visible.</summary>
    public string? LastStartError { get; private set; }

    /// <summary>Frames the camera loop has processed since the engine last started - a live sign
    /// the camera is actually delivering frames, not just "on" per the toggle state.</summary>
    public long FramesProcessed => _engine?.FramesProcessed ?? 0;

    /// <summary>How many of those frames had a hand detected in them - if this never moves off
    /// zero while a hand is visibly in frame, detection itself (not gesture mapping) is the
    /// problem.</summary>
    public long HandDetections => _engine?.HandDetections ?? 0;

    /// <summary>The most recent exception the hand-landmark detector threw, if any - normally
    /// null; a real value here means detection is silently failing every frame.</summary>
    public string? LastDetectionError => _engine?.LastDetectionError;

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

    public GestureEngineController(IComputerControlService computerControl, IApplicationService applications)
    {
        _computerControl = computerControl;
        _applications = applications;
    }

    public bool Start(GestureCalibration calibration, Action onEmergencyStop)
    {
        if (IsRunning || ModelPath is null)
        {
            return false;
        }

        LastStartError = null;

        try
        {
            var detector = new OnnxHandLandmarkDetector(ModelPath);
            _engine = new WebcamGestureEngine(detector, calibration);
            _mapper = new GestureActionMapper(
                _computerControl,
                _applications,
                onEmergencyStop,
                (int)SystemParameters.PrimaryScreenWidth,
                (int)SystemParameters.PrimaryScreenHeight);

            _engine.FramePreview += OnFramePreview;
            _engine.GestureDetected += OnGestureDetected;
            _engine.Start();
        }
        catch (Exception ex)
        {
            // Measured live (2026-09-13): a webcam already in use by another app (or no webcam
            // at all) threw here and was previously left to bubble up uncaught - the global
            // DispatcherUnhandledException handler would catch it, but only as a generic error
            // dialog with no clue this was gesture-camera-specific. Reporting the real message
            // through LastStartError instead lets the page show something actionable.
            LastStartError = ex.Message;
            _engine?.Dispose();
            _engine = null;
            _mapper = null;
            return false;
        }

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
