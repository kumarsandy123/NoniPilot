namespace NoniPilot.Desktop.Navigation;

/// <summary>
/// Implemented by pages that own a running timer/camera (Dashboard, Gesture Control) so
/// leaving the page can pause that work - RAM/CPU discipline given this machine has measured
/// as low as 2.5GB free RAM while running the local AI model. Pages with no background work
/// (Files &amp; Folders, Applications, etc.) simply don't implement this.
/// </summary>
public interface INavigablePage
{
    void OnNavigatedTo();

    void OnNavigatedFrom();
}
