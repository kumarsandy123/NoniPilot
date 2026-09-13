using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Pages;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop;

/// <summary>
/// The shell: custom-chrome header (status pill, STOP, clock, user greeting, window controls),
/// a sidebar that switches between 12 pages, and the one AppServices composition root every
/// page is built against. Replaces what used to be a single chat window.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppServices _services = new();
    private readonly Dictionary<string, UserControl> _pageCache = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CameraCornerPreviewWindow? _cameraCornerWindow;

    public MainWindow()
    {
        InitializeComponent();

        UserInitial.Text = Environment.UserName.Length > 0 ? Environment.UserName[..1].ToUpperInvariant() : "U";
        GreetingText.Text = $"Hello, {Environment.UserName}";

        _clockTimer.Tick += (_, _) =>
        {
            ClockText.Text = DateTime.Now.ToString("h:mm tt");
            DateText.Text = DateTime.Now.ToString("ddd, d MMM yyyy");
        };
        _clockTimer.Start();
        ClockText.Text = DateTime.Now.ToString("h:mm tt");
        DateText.Text = DateTime.Now.ToString("ddd, d MMM yyyy");

        _services.ProviderChanged += OnProviderChanged;
        _services.ProviderFellBack += (from, reason) => Dispatcher.Invoke(() => StatusText.Text = $"'{from}' unavailable ({reason}) - trying next...");
        _services.Commands.StatusChanged += () => Dispatcher.Invoke(() => StatusText.Text = _services.Commands.StatusMessage);
        _services.RequestNavigate = key => Dispatcher.Invoke(() => SelectNav(key));

        NavList.ItemsSource = BuildNavItems();
        NavList.SelectedIndex = 0;

        OnProviderChanged();

        // Passive wake-word listening ("Hey Noni"/"Jago Noni") starts by default alongside the
        // manual Talk button, per explicit user request ("both option") - the Voice Commands
        // page has a visible toggle to turn this back off if the always-on mic isn't wanted.
        _services.Commands.StartWakeWordMode();

        // Camera/gesture tracking now also starts by default, per explicit user request ("by
        // default camera access is on... when the app is open the camera and mic both are
        // opened") - only turned off if the user explicitly stops it (Dashboard's Gesture Mode
        // toggle, or Gesture Control page's Stop Camera button). Silently does nothing if no
        // hand-landmark model file is present - same "no crash, just inactive" behavior the
        // Gesture Control page already has for that case.
        // Small always-on-top "picture in picture" window showing the actual camera feed, per
        // explicit request so the user can visually confirm what the camera can currently see,
        // not just trust a status pill. Mirrors the engine's running state - appears whenever
        // the camera is on (including at launch, since the camera is on by default) and closes
        // itself if the camera is turned off (Dashboard's Gesture Mode toggle, Gesture Control
        // page's Stop, or app shutdown).
        _services.GestureEngine.StateChanged += UpdateCameraCornerWindow;
        _services.GestureEngine.Start(GestureCalibrationStore.Load(), _services.Commands.StopEverything);
        UpdateCameraCornerWindow();
    }

    private void UpdateCameraCornerWindow() => Dispatcher.Invoke(() =>
    {
        if (_services.GestureEngine.IsRunning)
        {
            if (_cameraCornerWindow is null)
            {
                _cameraCornerWindow = new CameraCornerPreviewWindow(_services.GestureEngine);
                _cameraCornerWindow.Show();
            }
        }
        else
        {
            _cameraCornerWindow?.Close();
            _cameraCornerWindow = null;
        }
    });

    private void OnProviderChanged() => Dispatcher.Invoke(() =>
        StatusText.Text = _services.Router.Name == "(none configured)"
            ? "No AI provider enabled - open AI Providers and turn one on."
            : $"Ready - {_services.Router.Name}");

    private List<NavItem> BuildNavItems() =>
    [
        new() { Key = "Dashboard", Glyph = "\U0001F3E0", Label = "Dashboard", CreatePage = () => new DashboardPage(_services) },
        new() { Key = "VoiceCommands", Glyph = "\U0001F3A4", Label = "Voice Commands", CreatePage = () => new VoiceCommandsPage(_services) },
        new() { Key = "GestureControl", Glyph = "✋", Label = "Gesture Control", CreatePage = () => new GestureControlPage(_services) },
        new() { Key = "ComputerControl", Glyph = "\U0001F5A5", Label = "Computer Control", CreatePage = () => new ComputerControlPage(_services) },
        new() { Key = "Applications", Glyph = "\U0001F4F1", Label = "Applications", CreatePage = () => new ApplicationsPage(_services) },
        new() { Key = "FilesAndFolders", Glyph = "\U0001F4C1", Label = "Files & Folders", CreatePage = () => new FilesAndFoldersPage(_services) },
        new() { Key = "BrowserAutomation", Glyph = "\U0001F310", Label = "Browser Automation", CreatePage = () => new BrowserAutomationPage(_services) },
        new() { Key = "SystemTools", Glyph = "\U0001F6E0", Label = "System Tools", CreatePage = () => new SystemToolsPage(_services) },
        new() { Key = "AiProviders", Glyph = "⚡", Label = "AI Providers", CreatePage = () => new AiProvidersPage(_services) },
        new() { Key = "TaskHistory", Glyph = "\U0001F550", Label = "Task History", CreatePage = () => new TaskHistoryPage(_services) },
        new() { Key = "Automation", Glyph = "⚙", Label = "Automation", CreatePage = () => new AutomationPage(_services) },
        new() { Key = "Settings", Glyph = "\U0001F527", Label = "Settings", CreatePage = () => new SettingsPage(_services) },
    ];

    private void SelectNav(string key)
    {
        foreach (NavItem item in NavList.Items)
        {
            if (item.Key == key)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item)
        {
            return;
        }

        if (PageHost.Content is INavigablePage leaving)
        {
            leaving.OnNavigatedFrom();
        }

        if (!_pageCache.TryGetValue(item.Key, out var page))
        {
            page = item.CreatePage();
            _pageCache[item.Key] = page;
        }

        PageHost.Content = page;

        if (page is INavigablePage entering)
        {
            entering.OnNavigatedTo();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _services.Commands.StopEverything();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "▣" : "□";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _services.Commands.StopWakeWordMode();
        _services.Commands.StopEverything();
        _cameraCornerWindow?.Close();
        _services.GestureEngine.Dispose();
    }
}
