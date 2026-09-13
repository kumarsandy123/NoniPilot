using System.Windows;
using System.Windows.Media.Imaging;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop;

/// <summary>
/// A real, separate, normally-chromed Window (not borderless like the shell) specifically so it
/// can be dragged to another monitor and maximized there with the OS's own window management -
/// "extendable to other screen with full screen" from the user's own request. Subscribes to the
/// SAME ScreenCaptureService instance the Dashboard already owns/started, rather than spinning
/// up a second capture timer, so there's only ever one screen-capture loop running.
/// </summary>
public partial class LivePreviewWindow : Window
{
    private readonly ScreenCaptureService _screenCapture;

    public LivePreviewWindow(ScreenCaptureService screenCapture)
    {
        InitializeComponent();
        _screenCapture = screenCapture;
        _screenCapture.FrameCaptured += OnFrameCaptured;
    }

    private void OnFrameCaptured(BitmapSource frame) => Dispatcher.BeginInvoke(() => PreviewImage.Source = frame);

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) =>
        _screenCapture.FrameCaptured -= OnFrameCaptured;
}
