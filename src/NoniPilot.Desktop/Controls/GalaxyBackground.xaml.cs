using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NoniPilot.Desktop.Controls;

/// <summary>
/// A "living" space background - a twinkling star field over static glowing nebula clouds and a
/// static galaxy disk - for the Dashboard's futuristic-AI look, requested explicitly by the user.
/// Built as real WPF vector graphics, not an actual GIF/video file: no external asset to source
/// (avoiding an unverifiable/unlicensed download) and no per-frame decode cost.
///
/// Measured live (2026-09-13), in order: (1) giving each of ~130 stars its own independent
/// AutoReverse/Forever DoubleAnimation clock pushed CPU from ~73% (camera on, idle) to ~147% -
/// isolated via a page-with-no-background baseline (~80-94%); (2) replacing that with this
/// class's single shared DispatcherTimer (nudges a few stars' Opacity per tick directly, no
/// animation clocks) turned out NOT to be the fix - CPU stayed at ~139-148%; (3) bisecting by
/// disabling pieces one at a time isolated the real cost to the 4 nebula/galaxy-disk Ellipses'
/// Storyboards (RotateTransform/TranslateTransform, continuously animated) - even with
/// SessionOptions-style mitigations like CacheMode="BitmapCache" tried first, which didn't help
/// either. Making those 4 shapes fully static (XAML, no Storyboards at all) brought CPU back down
/// to the exact same baseline as no galaxy background - confirmed the animation itself (not the
/// gradient rendering, not caching) was the cost. The star field's timer-based twinkle is what's
/// left doing the "living" work, and it measured at effectively zero added cost on its own.
///
/// IsHitTestVisible="False" on the root so it never intercepts clicks meant for the real
/// dashboard content sitting on top of it.
/// </summary>
public partial class GalaxyBackground : UserControl
{
    private static readonly Random Rng = new();
    // Raised from 130 now that this control spans the whole window (header + sidebar + page
    // content), not just one page's content area - same density, bigger canvas. Confirmed via
    // measurement that the star field itself costs ~0% regardless of count (it's the timer
    // approach, not per-star animation clocks), so this is a free visual upgrade.
    private const int StarCount = 260;
    private const int TwinkleStarsPerTick = 5;

    private readonly List<(Ellipse Star, double BaseOpacity)> _stars = new();
    private readonly DispatcherTimer _twinkleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public GalaxyBackground()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        _twinkleTimer.Tick += OnTwinkleTick;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Generated once, the first time real dimensions are available - a decorative
        // background doesn't need its star positions perfectly re-laid-out on every resize.
        if (_stars.Count > 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        GenerateStars(ActualWidth, ActualHeight);
        _twinkleTimer.Start();
    }

    private void GenerateStars(double width, double height)
    {
        for (var i = 0; i < StarCount; i++)
        {
            var size = 1 + Rng.NextDouble() * 1.8;
            var baseOpacity = 0.25 + Rng.NextDouble() * 0.6;

            var star = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Opacity = baseOpacity,
            };

            Canvas.SetLeft(star, Rng.NextDouble() * width);
            Canvas.SetTop(star, Rng.NextDouble() * height);
            StarCanvas.Children.Add(star);
            _stars.Add((star, baseOpacity));
        }
    }

    /// <summary>
    /// Every tick, a small random subset of stars gets a direct (no animation clock) opacity
    /// nudge - some dimming toward a "twinkle low," others recovering back toward their base
    /// brightness. Real starfields don't have every star twinkling in sync anyway, so a handful
    /// visibly flickering at any moment while the rest sit still reads as MORE convincing, not
    /// less, while costing only a few property sets every quarter-second.
    /// </summary>
    private void OnTwinkleTick(object? sender, EventArgs e)
    {
        if (_stars.Count == 0)
        {
            return;
        }

        for (var i = 0; i < TwinkleStarsPerTick; i++)
        {
            var (star, baseOpacity) = _stars[Rng.Next(_stars.Count)];
            var isDimming = Rng.NextDouble() < 0.5;
            star.Opacity = isDimming ? Math.Max(0.05, baseOpacity * 0.3) : baseOpacity;
        }
    }
}
