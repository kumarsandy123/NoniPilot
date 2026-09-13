using System.Diagnostics;
using System.Text;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Applications;

/// <summary>Real application lifecycle control (section 8.6) via System.Diagnostics.Process.</summary>
public sealed class WindowsApplicationService : IApplicationService
{
    /// <summary>
    /// Common everyday Windows apps whose actual executable/command name does not match what a
    /// person (or a model guessing on their behalf) would naturally call them - observed live:
    /// asking for "calculator" fails outright (`Process.Start` throws Win32Exception, file not
    /// found), because the real command is "calc". Windows' own PATH/App-Execution-Alias
    /// resolution already handles the common case (e.g. "notepad", "chrome" work as-is); this
    /// table only needs to cover the exceptions to that, not every app. Keyed on the
    /// space-stripped, lowercased name so "task manager" and "taskmanager" both resolve.
    /// </summary>
    private static readonly Dictionary<string, string> CommonAppAliases = new()
    {
        ["calculator"] = "calc",
        ["paint"] = "mspaint",
        ["wordpad"] = "write",
        ["taskmanager"] = "taskmgr",
        ["controlpanel"] = "control",
        ["commandprompt"] = "cmd",
        ["fileexplorer"] = "explorer",
        ["magnifier"] = "magnify",
        ["snippingtool"] = "snippingtool",
    };

    private static string ResolveBareName(string appPathOrName)
    {
        var key = appPathOrName.Replace(" ", string.Empty).ToLowerInvariant();
        return CommonAppAliases.TryGetValue(key, out var resolved) ? resolved : appPathOrName;
    }

    public Task<int> LaunchAsync(string appPathOrName, string? arguments = null, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo;

        if (Directory.Exists(appPathOrName))
        {
            // ShellExecute-ing a bare path string is ambiguous - if Windows can't resolve it
            // exactly, it silently falls back to some default shell location (this is exactly
            // what caused a "wrong folder opened" bug: a slightly-off path opened Documents
            // instead of erroring). Launching explorer.exe with the path as an explicit
            // argument guarantees it opens precisely this folder or fails loudly - no
            // ambiguous fallback.
            startInfo = new ProcessStartInfo("explorer.exe", $"\"{appPathOrName}\"");
        }
        else if (File.Exists(appPathOrName))
        {
            startInfo = new ProcessStartInfo(appPathOrName) { Arguments = arguments ?? string.Empty, UseShellExecute = true };
        }
        else if (appPathOrName.Contains('\\') || appPathOrName.Contains('/'))
        {
            // Looks like it was meant to be a path, but it doesn't exist - fail loudly now
            // rather than let ShellExecute silently open something else instead.
            throw new FileNotFoundException(
                $"'{appPathOrName}' does not exist. Verify the exact path (e.g. with filesystem_search or filesystem_list_directory) before launching it.",
                appPathOrName);
        }
        else
        {
            // A bare app name/command (e.g. "notepad", "chrome") - let Windows resolve it via
            // PATH / App Execution Aliases as before, after checking the small alias table above
            // for the well-known cases where the natural name and the real command differ.
            startInfo = new ProcessStartInfo(ResolveBareName(appPathOrName)) { Arguments = arguments ?? string.Empty, UseShellExecute = true };
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to start '{appPathOrName}': {ex.Message}", ex);
        }

        // Process.Start legitimately returns null for UseShellExecute=true launches where the
        // shell hands off to an existing process instead of spawning a new one - this is
        // normal for protocol URIs like "ms-settings:sound" (opens the singleton Settings app)
        // and was being incorrectly treated as a launch failure here. A real failure to launch
        // throws its own Win32Exception/InvalidOperationException (caught above), so null on
        // its own just means "launched, no process handle available."
        return Task.FromResult(process?.Id ?? 0);
    }

    public Task<bool> FocusAsync(string processNameOrWindowTitle, CancellationToken cancellationToken = default)
    {
        var match = Process.GetProcesses().FirstOrDefault(p =>
            p.MainWindowHandle != IntPtr.Zero &&
            (p.ProcessName.Contains(processNameOrWindowTitle, StringComparison.OrdinalIgnoreCase) ||
             p.MainWindowTitle.Contains(processNameOrWindowTitle, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(NativeWindow.SetForeground(match.MainWindowHandle));
    }

    public async Task<bool> CloseAsync(int processId, CancellationToken cancellationToken = default)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // No process with that id - nothing to close.
            return false;
        }

        // Explorer is a single shared process that also hosts the desktop/taskbar shell - its
        // MainWindowHandle (whatever the OS considers "the" window for that PID) is NOT
        // reliably one of the actual folder windows the user opened, so CloseMainWindow() below
        // was either silently targeting the wrong window or being refused outright by the shell
        // (observed live 2026-09-13: "close explorer" reported failure every time). Folder
        // windows are their own top-level "CabinetWClass" windows regardless of which process
        // hosts them, so close them directly by window class instead of by process.
        if (string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            process.Dispose();
            return await CloseExplorerFolderWindowsAsync(cancellationToken).ConfigureAwait(false);
        }

        using (process)
        {
            if (process.HasExited)
            {
                return true;
            }

            // Measured live (2026-09-13): this method used to call CloseMainWindow() and then
            // unconditionally return true, regardless of what actually happened - reported
            // "Google Chrome has been closed" while Chrome's own restore-session prompt on the
            // next launch proved it hadn't exited cleanly at all. CloseMainWindow() only reports
            // whether a close message was successfully POSTED, not whether the app actually
            // exited (it returns false outright for a process with no window/message loop, e.g.
            // a background helper process) - and even a successfully posted message doesn't
            // guarantee immediate (or any) exit if the app shows its own "save changes?"-style
            // prompt. Both cases are now honestly reported as failure instead of a false success.
            if (!process.CloseMainWindow())
            {
                return false;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public Task<IReadOnlyList<RunningApplication>> ListRunningAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RunningApplication> apps = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .Select(p => new RunningApplication(p.Id, p.ProcessName, p.MainWindowTitle))
            .ToList();

        return Task.FromResult(apps);
    }

    /// <summary>
    /// Finds every open File Explorer folder window ("CabinetWClass" - distinct from the
    /// desktop/taskbar shell windows, which are never this class) and asks each to close via
    /// WM_CLOSE, then confirms they actually disappeared rather than assuming the post succeeded.
    /// </summary>
    private static async Task<bool> CloseExplorerFolderWindowsAsync(CancellationToken cancellationToken)
    {
        var handles = NativeWindow.FindWindowsByClass("CabinetWClass");
        if (handles.Count == 0)
        {
            return false;
        }

        foreach (var hWnd in handles)
        {
            NativeWindow.PostClose(hWnd);
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (handles.All(h => !NativeWindow.Exists(h)))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return handles.All(h => !NativeWindow.Exists(h));
    }
}

internal static class NativeWindow
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    public static bool SetForeground(IntPtr hWnd) => SetForegroundWindow(hWnd);

    public static bool Exists(IntPtr hWnd) => IsWindow(hWnd);

    public static void PostClose(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static List<IntPtr> FindWindowsByClass(string className)
    {
        var found = new List<IntPtr>();
        var buffer = new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            buffer.Clear();
            GetClassName(hWnd, buffer, buffer.Capacity);
            if (string.Equals(buffer.ToString(), className, StringComparison.Ordinal))
            {
                found.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}
