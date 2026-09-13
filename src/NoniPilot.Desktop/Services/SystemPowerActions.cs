using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Power actions for the System Tools page. Deliberately a thin static helper, not a new
/// domain interface/project - there is no ISystemToolsService anywhere in this codebase and
/// this is a small, self-contained addition, not a new architectural layer.
/// </summary>
public static class SystemPowerActions
{
    public static void Shutdown(int delaySeconds = 30) =>
        Process.Start(new ProcessStartInfo("shutdown", $"/s /t {delaySeconds}") { UseShellExecute = true, CreateNoWindow = true });

    public static void Restart(int delaySeconds = 30) =>
        Process.Start(new ProcessStartInfo("shutdown", $"/r /t {delaySeconds}") { UseShellExecute = true, CreateNoWindow = true });

    /// <summary>Cancels any pending scheduled shutdown/restart from the two methods above.</summary>
    public static void CancelPending() =>
        Process.Start(new ProcessStartInfo("shutdown", "/a") { UseShellExecute = true, CreateNoWindow = true });

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static void Lock() => LockWorkStation();
}
