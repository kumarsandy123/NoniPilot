using System.Windows;
using System.Windows.Controls;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Desktop.Pages;

public partial class ComputerControlPage : UserControl
{
    private readonly AppServices _services;

    public ComputerControlPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private MouseButton SelectedButton => ((ComboBoxItem)ButtonCombo.SelectedItem).Content.ToString() switch
    {
        "Right" => MouseButton.Right,
        "Middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(XBox.Text, out var x) || !int.TryParse(YBox.Text, out var y))
        {
            StatusText.Text = "Enter numeric X/Y coordinates.";
            return;
        }

        _ = RunAsync("ComputerControl", "MoveMouse", new Dictionary<string, object?> { ["x"] = x, ["y"] = y },
            ct => _services.ComputerControl.MoveMouseAsync(x, y, ct));
    }

    private void Click_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(XBox.Text, out var x) || !int.TryParse(YBox.Text, out var y))
        {
            StatusText.Text = "Enter numeric X/Y coordinates.";
            return;
        }

        var button = SelectedButton;
        _ = RunAsync("ComputerControl", "Click", new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["button"] = button.ToString() },
            ct => _services.ComputerControl.ClickAsync(x, y, button, 1, ct));
    }

    private void Drag_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DragFromXBox.Text, out var fx) || !int.TryParse(DragFromYBox.Text, out var fy) ||
            !int.TryParse(DragToXBox.Text, out var tx) || !int.TryParse(DragToYBox.Text, out var ty))
        {
            StatusText.Text = "Enter numeric drag coordinates.";
            return;
        }

        _ = RunAsync("ComputerControl", "Drag",
            new Dictionary<string, object?> { ["fromX"] = fx, ["fromY"] = fy, ["toX"] = tx, ["toY"] = ty },
            ct => _services.ComputerControl.DragAsync(fx, fy, tx, ty, ct));
    }

    private void ScrollUp_Click(object sender, RoutedEventArgs e) => Scroll(3);

    private void ScrollDown_Click(object sender, RoutedEventArgs e) => Scroll(-3);

    private void Scroll(int delta) =>
        _ = RunAsync("ComputerControl", "Scroll", new Dictionary<string, object?> { ["deltaWheelClicks"] = delta },
            ct => _services.ComputerControl.ScrollAsync(delta, ct));

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        var text = FocusBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = RunAsync("ComputerControl", "FocusWindow", new Dictionary<string, object?> { ["windowTitleContains"] = text },
            ct => _services.ComputerControl.FocusWindowAsync(text, ct));
    }

    private void TypeText_Click(object sender, RoutedEventArgs e)
    {
        var text = TypeTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = RunAsync("ComputerControl", "TypeText", new Dictionary<string, object?> { ["text"] = text },
            ct => _services.ComputerControl.TypeTextAsync(text, ct));
    }

    private void SendKeys_Click(object sender, RoutedEventArgs e)
    {
        var keys = SendKeysBox.Text;
        if (string.IsNullOrEmpty(keys))
        {
            return;
        }

        _ = RunAsync("ComputerControl", "SendKeys", new Dictionary<string, object?> { ["keys"] = keys },
            ct => _services.ComputerControl.SendKeysAsync(keys, ct));
    }

    private async Task RunAsync(string tool, string action, IReadOnlyDictionary<string, object?> parameters, Func<CancellationToken, Task> execute)
    {
        var (_, message) = await _services.ActionRunner.RunAsync(tool, action, parameters, execute);
        StatusText.Text = message;
    }
}
