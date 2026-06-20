using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Serilog;

namespace HeliVMS.Services;

public sealed class SystemStatusService : ISystemStatusService, IDisposable
{
    private Timer? _timer;
    private Lazy<PerformanceCounter?> _cpuCounter;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSample;

    private double _cpuUsagePercent;
    private double _memoryUsagePercent;
    private long _memoryUsedBytes;
    private long _memoryTotalBytes;
    private long _diskFreeBytes;
    private long _diskTotalBytes;
    private double _diskUsagePercent;
    private int _cameraOnlineCount;
    private int _cameraTotalCount;
    private int _recordingActiveCount;

    public double CpuUsagePercent { get => _cpuUsagePercent; private set => SetField(ref _cpuUsagePercent, value); }
    public double MemoryUsagePercent { get => _memoryUsagePercent; private set => SetField(ref _memoryUsagePercent, value); }
    public long MemoryUsedBytes { get => _memoryUsedBytes; private set => SetField(ref _memoryUsedBytes, value); }
    public long MemoryTotalBytes { get => _memoryTotalBytes; private set => SetField(ref _memoryTotalBytes, value); }
    public long DiskFreeBytes { get => _diskFreeBytes; private set => SetField(ref _diskFreeBytes, value); }
    public long DiskTotalBytes { get => _diskTotalBytes; private set => SetField(ref _diskTotalBytes, value); }
    public double DiskUsagePercent { get => _diskUsagePercent; private set => SetField(ref _diskUsagePercent, value); }

    public int CameraOnlineCount
    {
        get => _cameraOnlineCount;
        set
        {
            if (SetField(ref _cameraOnlineCount, value))
                OnPropertyChanged(nameof(StatusSummary));
        }
    }

    public int CameraTotalCount
    {
        get => _cameraTotalCount;
        set
        {
            if (SetField(ref _cameraTotalCount, value))
                OnPropertyChanged(nameof(StatusSummary));
        }
    }

    public int RecordingActiveCount
    {
        get => _recordingActiveCount;
        set
        {
            if (SetField(ref _recordingActiveCount, value))
                OnPropertyChanged(nameof(StatusSummary));
        }
    }

    public string StatusSummary =>
        $"攝影機 {_cameraOnlineCount}/{_cameraTotalCount} 台" +
        (_recordingActiveCount > 0 ? $" | {_recordingActiveCount} 錄影進行中" : "") +
        $" | CPU {_cpuUsagePercent:F0}% | 記憶體 {_memoryUsedBytes / (1024 * 1024)}/{_memoryTotalBytes / (1024 * 1024)} MB" +
        $" | 磁碟 {_diskFreeBytes / (1024 * 1024 * 1024)} GB 可用";

    public SystemStatusService()
    {
        // Create PerformanceCounter lazily to avoid blocking UI on startup
        _cpuCounter = new Lazy<PerformanceCounter?>(() =>
        {
            try { var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total"); pc.NextValue(); return pc; }
            catch { return null; }
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        _lastCpuSample = DateTime.UtcNow;

        RefreshMemory();
        RefreshDisk();
    }

    public void StartMonitoring()
    {
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            try { RefreshAll(); }
            catch (Exception ex) { Log.Warning(ex, "[SystemStatus] Refresh error"); }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    public void StopMonitoring()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void RefreshAll()
    {
        RefreshCpu();
        RefreshMemory();
        RefreshDisk();
    }

    private void RefreshCpu()
    {
        var counter = _cpuCounter.Value;
        if (counter is not null)
        {
            CpuUsagePercent = Math.Round(counter.NextValue(), 1);
        }
        else
        {
            var now = DateTime.UtcNow;
            var cpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            var elapsed = (now - _lastCpuSample).TotalSeconds;
            if (elapsed > 0.5)
            {
                var cpuDelta = (cpuTime - _lastCpuTime).TotalSeconds;
                CpuUsagePercent = Math.Round(cpuDelta / (elapsed * Environment.ProcessorCount) * 100.0, 1);
                _lastCpuTime = cpuTime;
                _lastCpuSample = now;
            }
        }
    }

    private void RefreshMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var info = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(info))
            {
                MemoryTotalBytes = (long)info.ullTotalPhys;
                MemoryUsedBytes = (long)(info.ullTotalPhys - info.ullAvailPhys);
                MemoryUsagePercent = Math.Round((double)info.dwMemoryLoad, 1);
            }
        }
    }

    private void RefreshDisk()
    {
        try
        {
            var basePath = Path.GetPathRoot(Environment.CurrentDirectory);
            if (basePath is not null)
            {
                var drive = new DriveInfo(basePath);
                if (drive.IsReady)
                {
                    DiskFreeBytes = drive.AvailableFreeSpace;
                    DiskTotalBytes = drive.TotalSize;
                    DiskUsagePercent = DiskTotalBytes > 0
                        ? Math.Round((double)(DiskTotalBytes - DiskFreeBytes) / DiskTotalBytes * 100.0, 1)
                        : 0;
                }
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
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

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(CpuUsagePercent) or nameof(MemoryUsagePercent) or nameof(DiskUsagePercent))
            OnPropertyChanged(nameof(StatusSummary));
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_cpuCounter.IsValueCreated)
            _cpuCounter.Value?.Dispose();
    }
}
