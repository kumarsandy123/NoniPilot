using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NoniPilot.Desktop.Services;

public sealed record SystemMetricsSnapshot(double CpuPercent, double MemoryPercent, double DiskPercent, double? GpuPercent);

/// <summary>
/// Real CPU/Memory/Disk/GPU polling - nothing like this existed anywhere in the repo before
/// (confirmed by a repo-wide search for PerformanceCounter/WMI/CimSession). Deliberately only
/// runs while the Dashboard page is visible (Start/Stop, not always-on) since this machine has
/// measured as low as 2.5GB free RAM - an always-running timer + counters would make that
/// worse for no benefit while nobody's looking at the gauges.
/// </summary>
public sealed class SystemMetricsService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PerformanceCounter? _cpuCounter;
    private List<PerformanceCounter>? _gpuCounters;

    public event Action<SystemMetricsSnapshot>? Updated;

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // the first read is always 0 - prime it before the timer starts
        }
        catch
        {
            _cpuCounter = null;
        }

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            _gpuCounters = category.GetInstanceNames()
                .Where(name => name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                .Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true))
                .ToList();
            foreach (var counter in _gpuCounters)
            {
                counter.NextValue();
            }
        }
        catch
        {
            _gpuCounters = null;
        }

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty); // first reading immediately, don't wait 2s for the initial gauge state
    }

    public void Stop()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double cpu = 0;
        try
        {
            cpu = _cpuCounter?.NextValue() ?? 0;
        }
        catch
        {
            // A counter can throw if a driver resets mid-session - don't let a metrics glitch propagate.
        }

        double memory = 0;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
        {
            memory = (double)(status.ullTotalPhys - status.ullAvailPhys) / status.ullTotalPhys * 100.0;
        }

        double disk = 0;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    disk = (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100.0;
                }
            }
        }
        catch
        {
            // A removable/unready drive shouldn't take down the other three gauges.
        }

        double? gpu = null;
        if (_gpuCounters is { Count: > 0 })
        {
            try
            {
                gpu = Math.Min(100, _gpuCounters.Sum(c => c.NextValue()));
            }
            catch
            {
                gpu = null;
            }
        }

        Updated?.Invoke(new SystemMetricsSnapshot(cpu, memory, disk, gpu));
    }

    public void Dispose()
    {
        Stop();
        _cpuCounter?.Dispose();
        if (_gpuCounters is not null)
        {
            foreach (var counter in _gpuCounters)
            {
                counter.Dispose();
            }
        }
    }
}
