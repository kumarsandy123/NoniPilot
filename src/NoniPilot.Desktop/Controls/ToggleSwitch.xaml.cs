using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoniPilot.Desktop.Controls;

/// <summary>A checkbox-as-slider toggle, used for "Gesture Mode: Off/On" in the header/command bar.</summary>
public partial class ToggleSwitch : UserControl
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(ToggleSwitch), new PropertyMetadata(false, OnIsOnChanged));

    public static readonly DependencyProperty OnLabelProperty = DependencyProperty.Register(
        nameof(OnLabel), typeof(string), typeof(ToggleSwitch), new PropertyMetadata("On", OnLabelTextChanged));

    public static readonly DependencyProperty OffLabelProperty = DependencyProperty.Register(
        nameof(OffLabel), typeof(string), typeof(ToggleSwitch), new PropertyMetadata("Off", OnLabelTextChanged));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(ToggleSwitch), new PropertyMetadata("Gesture Mode"));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public string OnLabel
    {
        get => (string)GetValue(OnLabelProperty);
        set => SetValue(OnLabelProperty, value);
    }

    public string OffLabel
    {
        get => (string)GetValue(OffLabelProperty);
        set => SetValue(OffLabelProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public event EventHandler<bool>? Toggled;

    public ToggleSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void RootButton_Click(object sender, RoutedEventArgs e)
    {
        IsOn = !IsOn;
        Toggled?.Invoke(this, IsOn);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ToggleSwitch)d).Refresh();

    private static void OnLabelTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ToggleSwitch)d).Refresh();

    private void Refresh()
    {
        if (Thumb is null || Track is null || LabelText is null)
        {
            return;
        }

        LabelText.Text = $"{Caption}: {(IsOn ? OnLabel : OffLabel)}";
        Thumb.HorizontalAlignment = IsOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Thumb.Margin = IsOn ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
        Track.Background = IsOn ? (Brush)FindResource("AccentGradient") : (Brush)FindResource("PanelBorder");
        Thumb.Background = Brushes.White;
    }
}
