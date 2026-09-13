using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoniPilot.Desktop.Controls;

public enum StatusPillState
{
    Connected,
    Disconnected,
    Warning,
    Info,
}

/// <summary>A small dot+text pill used for provider connection state and Active/Ready tiles.</summary>
public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StatusPillState), typeof(StatusPill),
        new PropertyMetadata(StatusPillState.Info, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusPill),
        new PropertyMetadata(string.Empty, OnChanged));

    public StatusPillState State
    {
        get => (StatusPillState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusPill()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusPill)d).Refresh();

    private void Refresh()
    {
        if (Dot is null || TextElement is null)
        {
            return;
        }

        Dot.Fill = State switch
        {
            StatusPillState.Connected => (Brush)FindResource("Success"),
            StatusPillState.Disconnected => (Brush)FindResource("Danger"),
            StatusPillState.Warning => (Brush)FindResource("Warning"),
            _ => (Brush)FindResource("Cyan"),
        };
        TextElement.Text = Text;
    }
}
