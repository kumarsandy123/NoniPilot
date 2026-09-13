using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Models;
using NoniPilot.Policy;

namespace NoniPilot.Desktop.Pages;

public partial class SettingsPage : UserControl
{
    private readonly AppServices _services;

    private sealed record PolicyRow(string Scope, RiskLevel RiskLevel, ApprovalMode ApprovalMode);

    public SettingsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        var calibration = GestureCalibrationStore.Load();
        GestureSummaryText.Text =
            $"Pinch {calibration.PinchThreshold:F2} - Fist {calibration.FistThreshold:F2} - Open palm {calibration.OpenPalmThreshold:F2}";

        PolicyList.ItemsSource = PolicyService.DefaultClassificationTable
            .OrderBy(kv => kv.Key)
            .Select(kv => new PolicyRow(kv.Key, kv.Value, DefaultApprovalModeFor(kv.Value)))
            .ToList();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text = $"NoniPilot v{version?.ToString(2) ?? "1.0"} - Free Edition - Local AI, no paid API required by default.";
    }

    private static ApprovalMode DefaultApprovalModeFor(RiskLevel risk) => risk switch
    {
        RiskLevel.L0Safe or RiskLevel.L1Normal => ApprovalMode.AutoAllow,
        _ => ApprovalMode.AlwaysConfirm,
    };

    private void OpenGestureControl_Click(object sender, RoutedEventArgs e) => _services.RequestNavigate?.Invoke("GestureControl");
}
