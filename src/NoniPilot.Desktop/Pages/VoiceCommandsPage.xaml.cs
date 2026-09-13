using System.Windows.Controls;
using System.Windows.Input;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop.Pages;

/// <summary>
/// The original single-window chat UI, moved here verbatim in spirit: it drives the same
/// shared AppServices.Chat collection and CommandProcessor the Dashboard's bottom command bar
/// also uses, so a command typed/spoken from either place shows up in both.
/// </summary>
public partial class VoiceCommandsPage : UserControl, INavigablePage
{
    private readonly AppServices _services;

    public VoiceCommandsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        ChatItemsControl.ItemsSource = _services.Chat;
        _services.Commands.VoiceModeChanged += OnVoiceModeChanged;
        _services.Commands.WakeWordModeChanged += OnWakeWordModeChanged;
        _services.ProviderChanged += OnProviderChanged;
        OnProviderChanged();
        OnVoiceModeChanged();
        OnWakeWordModeChanged();

        _services.Chat.CollectionChanged += (_, _) => ScrollChatToBottom();
    }

    public void OnNavigatedTo() { }

    public void OnNavigatedFrom() { }

    private void OnProviderChanged() => EngineText.Text = $"Engine: {_services.Router.Name}";

    private void OnVoiceModeChanged() => TalkButton.Content = _services.Commands.VoiceModeActive ? "Stop Talking" : "Talk";

    private void OnWakeWordModeChanged() => WakeWordToggle.IsOn = _services.Commands.WakeWordModeActive;

    private void WakeWordToggle_Toggled(object? sender, bool isOn)
    {
        if (isOn)
        {
            _services.Commands.StartWakeWordMode();
        }
        else
        {
            _services.Commands.StopWakeWordMode();
        }
    }

    private void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Send();
        }
    }

    private void SendButton_Click(object sender, System.Windows.RoutedEventArgs e) => Send();

    private void Send()
    {
        var command = CommandTextBox.Text.Trim();
        CommandTextBox.Clear();
        _ = _services.Commands.ProcessCommandAsync(command);
    }

    private void TalkButton_Click(object sender, System.Windows.RoutedEventArgs e) => _services.Commands.ToggleVoiceMode();

    private void ClearChatButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _services.Chat.Clear();
        _services.Planner.ClearHistory();
    }

    private void ScrollChatToBottom() => ChatScrollViewer.ScrollToEnd();
}
