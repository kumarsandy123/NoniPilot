using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Cheap, periodic full-desktop capture backing both the Dashboard's Live Preview panel and
/// the Quick Actions "Take Screenshot" tile. Reuses one Bitmap/Graphics pair instead of
/// reallocating every tick, and only runs while Dashboard is the visible page.
///
/// Privacy/perf note (deliberate, not an oversight): this captures the literal full desktop -
/// anything visible, including private content - at a low frequency with no consent UI beyond
/// the "Live" badge shown next to it. Kept to a 3-4s interval and a small thumbnail size, and
/// stopped the moment Dashboard is navigated away from, given this machine's tight RAM budget.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private System.Drawing.Size _screenSize;

    public event Action<BitmapSource>? FrameCaptured;

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _screenSize = new System.Drawing.Size(
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight);
        _bitmap = new Bitmap(_screenSize.Width, _screenSize.Height);
        _graphics = Graphics.FromImage(_bitmap);

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _graphics?.Dispose();
        _graphics = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_bitmap is null || _graphics is null)
        {
            return;
        }

        try
        {
            _graphics.CopyFromScreen(0, 0, 0, 0, _screenSize);
            var source = CaptureThumbnail(_bitmap);
            FrameCaptured?.Invoke(source);
        }
        catch
        {
            // A transient desktop-composition hiccup shouldn't take the whole Dashboard down.
        }
    }

    /// <summary>Takes a full-resolution snapshot right now (for the "Take Screenshot" quick action,
    /// independent of the low-frequency preview timer above) and returns the raw bytes as PNG.</summary>
    public static byte[] CaptureFullScreenPng()
    {
        var size = new System.Drawing.Size((int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, size);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static BitmapSource CaptureThumbnail(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var scale = 320.0 / source.PixelWidth;
            var thumbnail = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            thumbnail.Freeze();
            return thumbnail;
        }
        finally
        {
            // CreateBitmapSourceFromHBitmap does not take ownership of the native handle - this
            // is a well-known GDI handle leak if skipped, and it must run every single tick
            // since the timer keeps calling this indefinitely while Dashboard is open.
            DeleteObject(hBitmap);
        }
    }

    public void Dispose() => Stop();
}
