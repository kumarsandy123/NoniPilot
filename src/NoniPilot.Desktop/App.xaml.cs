using System.Windows;
using System.Windows.Threading;

namespace NoniPilot.Desktop;

/// <summary>
/// Interaction logic for App.xaml. Also the last line of defense against a crash: WPF
/// terminates the whole process on an unhandled UI-thread exception by default, which is
/// exactly what happened when a mic-init error went unhandled from the Talk button. This
/// handler turns that into a visible error dialog instead of a silent app close - it does
/// NOT replace fixing the actual throw sites (see MainWindow's try/catch around voice calls),
/// it's a safety net for whatever the next unanticipated one turns out to be.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"NoniPilot hit an unexpected error and would normally have crashed:\n\n{args.Exception}",
                "NoniPilot - Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
