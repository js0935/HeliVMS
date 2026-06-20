using System.ComponentModel;

namespace HeliVMS.Services;

public interface ISystemStatusService : INotifyPropertyChanged
{
    double CpuUsagePercent { get; }
    double MemoryUsagePercent { get; }
    long MemoryUsedBytes { get; }
    long MemoryTotalBytes { get; }
    long DiskFreeBytes { get; }
    long DiskTotalBytes { get; }
    double DiskUsagePercent { get; }
    int CameraOnlineCount { get; set; }
    int CameraTotalCount { get; set; }
    int RecordingActiveCount { get; set; }
    string StatusSummary { get; }
    void StartMonitoring();
    void StopMonitoring();
}
