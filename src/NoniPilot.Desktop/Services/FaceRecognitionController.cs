using NoniPilot.Domain.Interfaces;
using NoniPilot.Gesture;
using OpenCvSharp;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Owns the FaceRecognitionEngine and drives it from the SAME running camera feed
/// GestureEngineController already owns (subscribes to its FramePreview event) - never opens a
/// second, competing webcam capture session. Recognition runs on a throttled cadence (not every
/// frame - Haar cascade detection is real CPU work, and "is this still the same person" doesn't
/// need to be checked faster than a few times a second) so it doesn't meaningfully add to the
/// gesture pipeline's own CPU cost.
/// </summary>
public sealed class FaceRecognitionController : IFaceRecognitionService, IDisposable
{
    private static readonly TimeSpan RecognitionInterval = TimeSpan.FromMilliseconds(700);

    private readonly GestureEngineController _gestureEngine;
    private readonly Lazy<Task<FaceRecognitionEngine>> _engineTask;

    private readonly object _stateLock = new();
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private bool _personDetected;
    private bool _isKnownPerson;
    private bool _enrolling;

    public FaceRecognitionController(GestureEngineController gestureEngine)
    {
        _gestureEngine = gestureEngine;
        _engineTask = new Lazy<Task<FaceRecognitionEngine>>(() => FaceRecognitionEngine.CreateAsync());
        _gestureEngine.FramePreview += OnFramePreview;

        // Kick off loading the cascade + any previously-enrolled model immediately at app
        // startup, rather than waiting for the first frame/status check to touch it lazily.
        // Fixes a real race reported live (2026-09-13): asking "do you recognize me" shortly
        // after launch could see IsEnrolled still false simply because this hadn't finished
        // loading yet, not because the enrollment was actually lost - loading is fast (the
        // cascade is cached after first run, and reading a small .yml model is near-instant),
        // so starting it this early makes that window effectively unreachable in practice.
        _ = _engineTask.Value;
    }

    public FaceRecognitionStatus GetStatus()
    {
        lock (_stateLock)
        {
            var isEnrolled = _engineTask.IsValueCreated && _engineTask.Value.IsCompletedSuccessfully && _engineTask.Value.Result.IsEnrolled;
            return new FaceRecognitionStatus(
                CameraActive: _gestureEngine.IsRunning,
                IsEnrolled: isEnrolled,
                PersonDetected: _gestureEngine.IsRunning && _personDetected,
                IsKnownPerson: _gestureEngine.IsRunning && _personDetected && _isKnownPerson);
        }
    }

    private async void OnFramePreview(object? sender, Mat frame)
    {
        bool enrolling;
        lock (_stateLock)
        {
            enrolling = _enrolling;
        }

        // Enrollment captures its own frames via a dedicated subscription below - skip the
        // regular throttled recognition pass while that's in progress so the two don't both try
        // to use the engine at once.
        if (enrolling || DateTime.UtcNow - _lastCheckUtc < RecognitionInterval)
        {
            return;
        }

        _lastCheckUtc = DateTime.UtcNow;

        try
        {
            var engine = await _engineTask.Value.ConfigureAwait(false);
            using var frameClone = frame.Clone();
            var detected = engine.DetectLargestFace(frameClone);

            if (detected is null)
            {
                lock (_stateLock)
                {
                    _personDetected = false;
                    _isKnownPerson = false;
                }
                return;
            }

            using (detected.GrayCrop)
            {
                var isKnown = engine.IsEnrolled && engine.Recognize(detected.GrayCrop, out _);
                lock (_stateLock)
                {
                    _personDetected = true;
                    _isKnownPerson = isKnown;
                }
            }
        }
        catch
        {
            // A single failed recognition pass (e.g. a transient frame decode issue) shouldn't
            // crash the gesture camera pipeline it's piggybacking on.
        }
    }

    /// <summary>
    /// Collects real face crops from ~4 seconds of live camera frames, then trains and saves a
    /// fresh model. Returns false if the camera isn't on, or no face was found in enough frames.
    /// </summary>
    public async Task<bool> EnrollAsync(CancellationToken cancellationToken = default)
    {
        if (!_gestureEngine.IsRunning)
        {
            return false;
        }

        lock (_stateLock)
        {
            _enrolling = true;
        }

        var collectedCrops = new List<Mat>();
        var engine = await _engineTask.Value.ConfigureAwait(false);

        void CollectFrame(object? sender, Mat frame)
        {
            if (collectedCrops.Count >= 20)
            {
                return;
            }

            using var frameClone = frame.Clone();
            var detected = engine.DetectLargestFace(frameClone);
            if (detected is not null)
            {
                collectedCrops.Add(detected.GrayCrop);
            }
        }

        _gestureEngine.FramePreview += CollectFrame;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (collectedCrops.Count < 20 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gestureEngine.FramePreview -= CollectFrame;
            lock (_stateLock)
            {
                _enrolling = false;
            }
        }

        // A handful of usable frames is enough for LBPH on a single enrolled person - this isn't
        // trying to be robust to wildly different lighting/angle, just "recognize it's the same
        // person who enrolled a moment ago".
        if (collectedCrops.Count < 8)
        {
            foreach (var crop in collectedCrops)
            {
                crop.Dispose();
            }
            return false;
        }

        engine.TrainAndSave(collectedCrops);
        foreach (var crop in collectedCrops)
        {
            crop.Dispose();
        }

        return true;
    }

    public void Dispose() => _gestureEngine.FramePreview -= OnFramePreview;
}
