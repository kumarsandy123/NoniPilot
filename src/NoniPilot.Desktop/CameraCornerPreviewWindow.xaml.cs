using System.Windows;
using System.Windows.Input;
using NoniPilot.Desktop.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace NoniPilot.Desktop;

/// <summary>
/// A small always-on-top "picture in picture" window pinned near the top-right of the screen,
/// showing the gesture camera's actual live feed at all times the camera is on - added per
/// explicit request so the user can visually confirm what NoniPilot's camera can currently see,
/// rather than only trusting a status pill. Subscribes to the same GestureEngineController
/// instance the Dashboard/Gesture Control page already own, not a second capture session.
/// Deliberately borderless/toolwindow-style (no taskbar entry) so it reads as a persistent
/// overlay rather than another app window to manage.
/// </summary>
public partial class CameraCornerPreviewWindow : System.Windows.Window
{
    private readonly GestureEngineController _gestureEngine;

    public CameraCornerPreviewWindow(GestureEngineController gestureEngine)
    {
        InitializeComponent();
        _gestureEngine = gestureEngine;
        _gestureEngine.FramePreview += OnFramePreview;

        Loaded += (_, _) => PositionTopRight();
    }

    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 16;
        Left = workArea.Right - Width - margin;
        Top = workArea.Top + margin;
    }

    private void OnFramePreview(object? sender, Mat frame)
    {
        var bitmap = frame.ToBitmapSource();
        bitmap.Freeze();
        Dispatcher.BeginInvoke(() => PreviewImage.Source = bitmap);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _gestureEngine.FramePreview -= OnFramePreview;
        base.OnClosed(e);
    }
}
