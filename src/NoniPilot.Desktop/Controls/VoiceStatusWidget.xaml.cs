using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using NoniPilot.Desktop.Services;

namespace NoniPilot.Desktop.Controls;

/// <summary>
/// A sci-fi "AI is listening" widget - a pulsing radar-ring badge plus an audio-equalizer-style
/// bar row - bound to the SAME real CommandProcessor state the header's plain status text
/// already reflects (StatusMessage, VoiceModeActive, WakeWordModeActive), not a cosmetic fake.
/// The equalizer bars only actually move while voice mode or wake-word listening is genuinely
/// active - flat and still when voice is off, so this never claims to be listening when it
/// isn't.
/// </summary>
public partial class VoiceStatusWidget : UserControl
{
    private static readonly Random Rng = new();

    private CommandProcessor? _commands;
    private readonly Rectangle[] _bars;

    // Same shared-timer-nudges-a-few-elements-directly technique as the star field's twinkle -
    // no per-bar animation clocks, confirmed cheap for that pattern already this session.
    private readonly DispatcherTimer _barsTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    public VoiceStatusWidget()
    {
        InitializeComponent();
        _bars = [Bar1, Bar2, Bar3, Bar4, Bar5];
        _barsTimer.Tick += OnBarsTick;
        _barsTimer.Start();
    }

    /// <summary>Wires this widget to the real CommandProcessor whose state it reflects - called
    /// once from the host page's code-behind, same "declare in XAML, wire in code-behind"
    /// convention every other custom control in this app already follows.</summary>
    public void Attach(CommandProcessor commands)
    {
        _commands = commands;
        _commands.StatusChanged += OnStateChanged;
        _commands.VoiceModeChanged += OnStateChanged;
        _commands.WakeWordModeChanged += OnStateChanged;
        OnStateChanged();
    }

    private void OnStateChanged() => Dispatcher.Invoke(() =>
    {
        if (_commands is null)
        {
            return;
        }

        PrimaryStatusText.Text = _commands.StatusMessage;

        if (_commands.VoiceModeActive)
        {
            SecondaryStatusText.Text = "Listening for your command...";
            BadgeIcon.Text = "🎙";
        }
        else if (_commands.WakeWordModeActive)
        {
            SecondaryStatusText.Text = "Say \"Hey Noni\" to wake me";
            BadgeIcon.Text = "🎙";
        }
        else
        {
            SecondaryStatusText.Text = "Voice is off";
            BadgeIcon.Text = "🔇";
        }
    });

    private void OnBarsTick(object? sender, EventArgs e)
    {
        var active = _commands is { VoiceModeActive: true } or { WakeWordModeActive: true };
        foreach (var bar in _bars)
        {
            bar.Height = active ? 4 + Rng.NextDouble() * 16 : 4;
        }
    }
}
