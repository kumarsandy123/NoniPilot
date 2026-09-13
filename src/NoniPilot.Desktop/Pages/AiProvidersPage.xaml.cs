using System.Windows;
using System.Windows.Controls;
using NoniPilot.Agent.Providers;
using NoniPilot.Desktop.Controls;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop.Pages;

public partial class AiProvidersPage : UserControl
{
    private static readonly (string Label, string Code)[] SpokenLanguages =
    [
        ("Auto-detect", ""),
        ("English", "en"),
        ("Hindi", "hi"),
        ("Urdu", "ur"),
        ("Punjabi", "pa"),
        ("Bengali", "bn"),
        ("Tamil", "ta"),
        ("Telugu", "te"),
        ("Marathi", "mr"),
        ("Gujarati", "gu"),
        ("Arabic", "ar"),
        ("Spanish", "es"),
        ("French", "fr"),
    ];

    private readonly AppServices _services;

    public AiProvidersPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        var settings = AiProviderSettingsStore.Load();
        LocalCheckBox.IsChecked = settings.EnableLocal;
        OllamaModelTextBox.Text = settings.OllamaModel;
        GroqCheckBox.IsChecked = settings.EnableGroq;
        GroqModelTextBox.Text = settings.GroqModel;
        ClaudeCheckBox.IsChecked = settings.EnableClaudePaid;

        foreach (var (label, code) in SpokenLanguages)
        {
            SpokenLanguageComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        }

        SpokenLanguageComboBox.SelectedIndex = Array.FindIndex(SpokenLanguages, l => l.Code == settings.SpokenLanguage);
        if (SpokenLanguageComboBox.SelectedIndex < 0)
        {
            SpokenLanguageComboBox.SelectedIndex = 0;
        }

        var groqKeyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY"));
        var claudeKeyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        if (!groqKeyPresent)
        {
            GroqCheckBox.Content += "  [no GROQ_API_KEY set]";
        }
        if (!claudeKeyPresent)
        {
            ClaudeCheckBox.Content += "  [no ANTHROPIC_API_KEY set]";
        }

        GroqStatusPill.State = settings.EnableGroq && groqKeyPresent ? StatusPillState.Info : StatusPillState.Disconnected;
        GroqStatusPill.Text = settings.EnableGroq && groqKeyPresent ? "Configured" : "Off";
        ClaudeStatusPill.State = settings.EnableClaudePaid && claudeKeyPresent ? StatusPillState.Info : StatusPillState.Disconnected;
        ClaudeStatusPill.Text = settings.EnableClaudePaid && claudeKeyPresent ? "Configured" : "Off";

        _ = RefreshLocalStatusAsync(settings);
    }

    private async Task RefreshLocalStatusAsync(AiProviderSettings settings)
    {
        LocalStatusPill.State = StatusPillState.Warning;
        LocalStatusPill.Text = "Checking...";

        var reachable = await AiProviderHealthChecker.IsOllamaReachableAsync(settings.OllamaBaseUrl);
        LocalStatusPill.State = reachable ? StatusPillState.Connected : StatusPillState.Disconnected;
        LocalStatusPill.Text = reachable ? "Connected" : "Unreachable";
    }

    private void CheckStatus_Click(object sender, RoutedEventArgs e) => _ = RefreshLocalStatusAsync(AiProviderSettingsStore.Load());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selectedLanguage = (string)((ComboBoxItem)SpokenLanguageComboBox.SelectedItem).Tag;

        var settings = new AiProviderSettings
        {
            EnableLocal = LocalCheckBox.IsChecked == true,
            OllamaModel = OllamaModelTextBox.Text.Trim(),
            OllamaBaseUrl = AiProviderSettingsStore.Load().OllamaBaseUrl,
            EnableGroq = GroqCheckBox.IsChecked == true,
            GroqModel = GroqModelTextBox.Text.Trim(),
            EnableClaudePaid = ClaudeCheckBox.IsChecked == true,
            SpokenLanguage = selectedLanguage,
        };

        AiProviderSettingsStore.Save(settings);
        _services.RebuildPlanner(settings);
        StatusText.Text = $"Saved - {_services.Router.Name}";
        _ = RefreshLocalStatusAsync(settings);
    }
}
