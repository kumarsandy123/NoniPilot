using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NoniPilot.Desktop.Controls;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop.Pages;

public partial class DashboardPage : UserControl, INavigablePage
{
    private static readonly string[] Quotes =
    [
        "“Just say it, show it, or gesture it - and I'll do it.”",
        "“Your voice. Your vision. Your computer.”",
        "“Running fully local - no token that can run out mid-task.”",
        "“Ask me to open, find, create, or clean up - I'm on it.”",
    ];

    private readonly AppServices _services;
    private readonly SystemMetricsService _metrics = new();
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly DispatcherTimer _quoteTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private int _quoteIndex;
    private const string CommandPlaceholder = "Type a command or speak...";

    private sealed record RecentTaskRow(string Description, string TimeAgo);
    private sealed record ProviderRow(string Name, string Description, StatusPillState State, string StateLabel);

    public DashboardPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        QuoteText.Text = Quotes[0];
        _quoteTimer.Tick += (_, _) =>
        {
            _quoteIndex = (_quoteIndex + 1) % Quotes.Length;
            QuoteText.Text = Quotes[_quoteIndex];
        };
        _quoteTimer.Start();

        _metrics.Updated += OnMetricsUpdated;
        _screenCapture.FrameCaptured += bitmap => Dispatcher.BeginInvoke(() => PreviewImage.Source = bitmap);
        _services.GestureEngine.StateChanged += OnGestureStateChanged;
        _services.Commands.VoiceModeChanged += OnVoiceModeChanged;
        VoiceWidget.Attach(_services.Commands);

        MicPill.State = StatusPillState.Connected;
        MicPill.Text = "Mic Ready";
        OnGestureStateChanged();
        OnVoiceModeChanged();
    }

    public void OnNavigatedTo()
    {
        _metrics.Start();
        _screenCapture.Start();
        ScreenPill.State = StatusPillState.Connected;
        ScreenPill.Text = "Screen Active";

        _ = RefreshRecentTasksAsync();
        RefreshProviders();
    }

    public void OnNavigatedFrom()
    {
        _metrics.Stop();
        _screenCapture.Stop();
        ScreenPill.State = StatusPillState.Info;
        ScreenPill.Text = "Screen Idle";
    }

    private void OnMetricsUpdated(SystemMetricsSnapshot snapshot) => Dispatcher.BeginInvoke(() =>
    {
        CpuGauge.Percentage = snapshot.CpuPercent;
        MemoryGauge.Percentage = snapshot.MemoryPercent;
        DiskGauge.Percentage = snapshot.DiskPercent;
        GpuGauge.Percentage = snapshot.GpuPercent ?? 0;
        GpuGauge.Label = snapshot.GpuPercent is null ? "GPU (N/A)" : "GPU";
    });

    private void OnGestureStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        var running = _services.GestureEngine.IsRunning;
        CameraPill.State = running ? StatusPillState.Connected : StatusPillState.Info;
        CameraPill.Text = running ? "Camera On" : "Camera Off";
        GesturePill.State = running ? StatusPillState.Connected : StatusPillState.Info;
        GesturePill.Text = running ? "Gesture Active" : "Gesture Off";
        GestureModeToggle.IsOn = running;
    });

    private void OnVoiceModeChanged() =>
        MicButton.Content = _services.Commands.VoiceModeActive ? "⏹" : "🎙";

    private async Task RefreshRecentTasksAsync()
    {
        var events = await _services.Audit.QueryAsync(limit: 8);
        var now = DateTimeOffset.UtcNow;
        RecentTasksList.ItemsSource = events
            .Select(e => new RecentTaskRow(FormatDescription(e.Action, e.Target), FormatTimeAgo(now - e.Timestamp)))
            .ToList();
    }

    private static string FormatDescription(string action, string? target) =>
        string.IsNullOrWhiteSpace(target) ? action : $"{action} ({Truncate(target, 60)})";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string FormatTimeAgo(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 60 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hr ago",
        _ => $"{(int)elapsed.TotalDays} day(s) ago",
    };

    private void RefreshProviders()
    {
        var settings = Agent.Providers.AiProviderSettingsStore.Load();
        var groqKeyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY"));
        var claudeKeyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        var rows = new List<ProviderRow>
        {
            new("Local AI (Ollama)", $"Model: {settings.OllamaModel} - Fully offline, free", StatusPillState.Info, "Checking..."),
            new("Groq (Optional)", "Fast cloud AI - free tier available",
                settings.EnableGroq && groqKeyPresent ? StatusPillState.Connected : StatusPillState.Disconnected,
                settings.EnableGroq && groqKeyPresent ? "Enabled" : "Not connected"),
            new("Other Cloud Providers", "No paid API required",
                settings.EnableClaudePaid && claudeKeyPresent ? StatusPillState.Connected : StatusPillState.Info,
                settings.EnableClaudePaid && claudeKeyPresent ? "Enabled" : "Disabled"),
        };
        ProvidersList.ItemsSource = rows;

        _ = RefreshLocalStatusAsync(settings.OllamaBaseUrl, rows);
    }

    private async Task RefreshLocalStatusAsync(string ollamaBaseUrl, List<ProviderRow> rows)
    {
        var reachable = await AiProviderHealthChecker.IsOllamaReachableAsync(ollamaBaseUrl);
        rows[0] = rows[0] with { State = reachable ? StatusPillState.Connected : StatusPillState.Disconnected, StateLabel = reachable ? "Active" : "Unreachable" };
        _ = Dispatcher.BeginInvoke(() =>
        {
            ProvidersList.ItemsSource = null;
            ProvidersList.ItemsSource = rows;
        });
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e) =>
        _ = RunQuickActionAsync("Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = "explorer" },
            ct => _services.Applications.LaunchAsync("explorer", null, ct));

    private void OpenChrome_Click(object sender, RoutedEventArgs e) =>
        _ = RunQuickActionAsync("Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = "chrome" },
            ct => _services.Applications.LaunchAsync("chrome", null, ct));

    private void CreateFile_Click(object sender, RoutedEventArgs e)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var path = System.IO.Path.Combine(desktop, $"New Document {DateTime.Now:yyyyMMdd-HHmmss}.txt");
        _ = RunQuickActionAsync("FileSystem", "CreateFile", new Dictionary<string, object?> { ["path"] = path },
            ct => _services.FileSystem.CreateFileAsync(path, ct));
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) =>
        _ = RunQuickActionAsync("System", "Screenshot", new Dictionary<string, object?>(), ct =>
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyyMMdd-HHmmss}.png");
            System.IO.File.WriteAllBytes(path, ScreenCaptureService.CaptureFullScreenPng());
            return Task.CompletedTask;
        });

    private void SearchFiles_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("FilesAndFolders");

    private void Shutdown_Click(object sender, RoutedEventArgs e) =>
        _ = RunQuickActionAsync("System", "Shutdown", new Dictionary<string, object?> { ["delaySeconds"] = 30 }, ct =>
        {
            SystemPowerActions.Shutdown(30);
            return Task.CompletedTask;
        });

    private async Task RunQuickActionAsync(string tool, string action, IReadOnlyDictionary<string, object?> parameters, Func<CancellationToken, Task> execute)
    {
        var (success, message) = await _services.ActionRunner.RunAsync(tool, action, parameters, execute);
        if (success)
        {
            await RefreshRecentTasksAsync();
        }
    }

    private void ViewAllTasks_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("TaskHistory");

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        CommandBox.Text = ((Button)sender).Content.ToString() ?? string.Empty;
        CommandBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
    }

    private void CommandBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (CommandBox.Text == CommandPlaceholder)
        {
            CommandBox.Text = string.Empty;
        }
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendCommand();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCommand();

    private void SendCommand()
    {
        var text = CommandBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || text == CommandPlaceholder)
        {
            return;
        }

        CommandBox.Text = string.Empty;
        _ = _services.Commands.ProcessCommandAsync(text);
    }

    private void Mic_Click(object sender, RoutedEventArgs e) => _services.Commands.ToggleVoiceMode();

    private void GestureModeToggle_Toggled(object? sender, bool isOn)
    {
        if (isOn)
        {
            var calibration = GestureCalibrationStore.Load();
            if (!_services.GestureEngine.Start(calibration, _services.Commands.StopEverything))
            {
                // Start() doesn't raise StateChanged on failure (nothing actually started), so
                // the toggle would otherwise be left showing "on" for an engine that isn't
                // running - reconcile it here and surface the real reason via the pill.
                GestureModeToggle.IsOn = false;
                GesturePill.Text = _services.GestureEngine.LastStartError ?? "Could not start camera";
            }
        }
        else
        {
            _services.GestureEngine.Stop();
        }
    }

    // Feature cards - each navigates to whichever existing page actually backs that capability,
    // rather than being decorative. "Vision" has no dedicated page yet (NoniPilot.Vision is
    // still an unimplemented stub project) - Live Preview is the closest real, working analog
    // ("understand your screen" = the literal screen capture already on this page), so it opens
    // that full-screen instead of navigating nowhere.
    private void FeatureVoice_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("VoiceCommands");
    private void FeatureVision_Click(object sender, RoutedEventArgs e) => OpenLivePreviewWindow();
    private void FeatureGesture_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("GestureControl");
    private void FeatureAiAgent_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("AiProviders");
    private void FeatureComputer_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("ComputerControl");
    private void FeatureAutomation_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("Automation");

    private void FullScreen_Click(object sender, RoutedEventArgs e) => OpenLivePreviewWindow();

    private void LivePreview_Click(object sender, MouseButtonEventArgs e) => OpenLivePreviewWindow();

    /// <summary>
    /// Singleton by design - repeated clicks (the fullscreen button, the thumbnail, or the
    /// "Vision" feature tile all call this) must bring the one existing window to front, not
    /// stack up a new one each time. Multiple copies were confirmed live to each independently
    /// capture the screen (including each other, producing a recursive mirror effect) and to
    /// minimize/restore together as WPF owned windows - all symptoms of there simply being more
    /// than one window when there should only ever be one.
    /// </summary>
    private LivePreviewWindow? _livePreviewWindow;

    private void OpenLivePreviewWindow()
    {
        if (_livePreviewWindow is not null)
        {
            if (_livePreviewWindow.WindowState == WindowState.Minimized)
            {
                _livePreviewWindow.WindowState = WindowState.Normal;
            }

            _livePreviewWindow.Activate();
            return;
        }

        _livePreviewWindow = new LivePreviewWindow(_screenCapture) { Owner = Window.GetWindow(this) };
        _livePreviewWindow.Closed += (_, _) => _livePreviewWindow = null;
        _livePreviewWindow.Show();
    }
}
