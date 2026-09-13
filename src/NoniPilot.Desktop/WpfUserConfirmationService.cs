using System.Windows;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop;

/// <summary>The "Permission prompt modal" from section 11/12: a real, blocking Yes/No dialog, not a stub.</summary>
public sealed class WpfUserConfirmationService : IUserConfirmationService
{
    public Task<bool> ConfirmAsync(
        string tool,
        string action,
        string humanReadableSummary,
        RiskLevel riskLevel,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var result = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                $"NoniPilot wants to run:\n\n{tool}.{action}\n{humanReadableSummary}\n\nRisk level: {riskLevel}\n\nAllow this action?",
                "NoniPilot - Confirm Action",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning));

            return result == MessageBoxResult.Yes;
        }, cancellationToken);
    }
}
