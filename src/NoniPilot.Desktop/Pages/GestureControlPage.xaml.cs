using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Services;
using NoniPilot.Gesture;
using OpenCvSharp.WpfExtensions;

namespace NoniPilot.Desktop.Pages;

/// <summary>
/// Ported from the original GestureCenterWindow, but the engine itself now lives in the shared
/// GestureEngineController (AppServices) so this page's Start/Stop and the Dashboard's Gesture
/// Mode toggle both control the same camera session. Navigating away only detaches this page's
/// preview handler - it does not stop the engine if something else (Dashboard) turned it on.
/// </summary>
public partial class GestureControlPage : UserControl, INavigablePage
{
    private readonly AppServices _services;
    private bool _previewAttached;
    private readonly DispatcherTimer _faceStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public GestureControlPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        var calibration = GestureCalibrationStore.Load();
        PinchSlider.Value = calibration.PinchThreshold;
        FistSlider.Value = calibration.FistThreshold;
        OpenPalmSlider.Value = calibration.OpenPalmThreshold;

        if (_services.GestureEngine.ModelPath is null)
        {
            StatusText.Text = "No hand-landmark model found - see docs/architecture/decisions.md for setup.";
            ToggleButton.IsEnabled = false;
        }

        _services.GestureEngine.StateChanged += OnStateChanged;
        _services.GestureEngine.GestureDetected += OnGestureDetected;
        OnStateChanged();

        _faceStatusTimer.Tick += (_, _) => UpdateFaceStatusText();
        _faceStatusTimer.Start();
        UpdateFaceStatusText();
    }

    public void OnNavigatedTo()
    {
        if (!_previewAttached)
        {
            _services.GestureEngine.FramePreview += OnFramePreview;
            _previewAttached = true;
        }
    }

    public void OnNavigatedFrom()
    {
        if (_previewAttached)
        {
            _services.GestureEngine.FramePreview -= OnFramePreview;
            _previewAttached = false;
        }
    }

    private void UpdateFaceStatusText()
    {
        var status = _services.FaceRecognition.GetStatus();
        FaceStatusText.Text = !status.CameraActive
            ? "Camera is off."
            : !status.IsEnrolled
                ? "Not enrolled yet - click Enroll My Face below."
                : !status.PersonDetected
                    ? "Enrolled - no face currently in view."
                    : status.IsKnownPerson
                        ? "Enrolled - recognized in frame right now."
                        : "Enrolled - a face is in view but doesn't match.";
    }

    private async void EnrollButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.GestureEngine.IsRunning)
        {
            FaceStatusText.Text = "Turn the camera on first (Start Camera above).";
            return;
        }

        EnrollButton.IsEnabled = false;
        FaceStatusText.Text = "Enrolling - look at the camera for a few seconds...";

        // PolicyGatedActionRunner.RunAsync only sees whether the delegate threw, not
        // EnrollAsync's own true/false result - captured via closure so a real "not enough face
        // frames captured" failure is reported honestly instead of always claiming success.
        var actuallyEnrolled = false;
        await _services.ActionRunner.RunAsync(
            "Face", "Enroll", new Dictionary<string, object?>(),
            async ct => actuallyEnrolled = await _services.FaceRecognition.EnrollAsync(ct));

        FaceStatusText.Text = actuallyEnrolled
            ? "Enrolled successfully! It should now recognize you."
            : "Enrollment didn't capture enough of your face - make sure you're clearly visible and try again.";
        EnrollButton.IsEnabled = true;
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services.GestureEngine.IsRunning)
        {
            _services.GestureEngine.Stop();
            return;
        }

        var calibration = new GestureCalibration
        {
            PinchThreshold = (float)PinchSlider.Value,
            FistThreshold = (float)FistSlider.Value,
            OpenPalmThreshold = (float)OpenPalmSlider.Value,
        };

        if (!_services.GestureEngine.Start(calibration, _services.Commands.StopEverything))
        {
            StatusText.Text = "Could not start the camera.";
        }
    }

    private void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        GestureCalibrationStore.Save(new GestureCalibration
        {
            PinchThreshold = (float)PinchSlider.Value,
            FistThreshold = (float)FistSlider.Value,
            OpenPalmThreshold = (float)OpenPalmSlider.Value,
        });
        StatusText.Text = "Calibration saved.";
    }

    private void OnFramePreview(object? sender, OpenCvSharp.Mat frame)
    {
        var bitmap = frame.ToBitmapSource();
        bitmap.Freeze();
        Dispatcher.BeginInvoke(() => PreviewImage.Source = bitmap);
    }

    private void OnGestureDetected(object? sender, GestureFrame frame) =>
        Dispatcher.BeginInvoke(() => StatusText.Text = $"Camera on - gesture: {frame.Gesture}");

    private void OnStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        var running = _services.GestureEngine.IsRunning;
        PrivacyIndicator.Fill = running ? Brushes.LimeGreen : Brushes.Gray;
        ToggleButton.Content = running ? "Stop Camera" : "Start Camera";
        StatusText.Text = running ? "Camera on - hold your hand roughly centered in the preview." : "Camera off.";
        if (!running)
        {
            PreviewImage.Source = null;
        }
    });
}
