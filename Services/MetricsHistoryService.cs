using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace HeliVMS.Services;

public sealed class MetricsHistoryService : IDisposable {
    private readonly System.Timers.Timer _timer;
    private readonly IRecordingService _recordingService;
    private readonly ISystemStatusService _systemStatus;
    private long _lastBytesWritten;
    private DateTime _lastMeasure = DateTime.Now;

    public record DataPoint(DateTime Time, double Value);

    public List<DataPoint> BandwidthHistory { get; } = [];
    public List<DataPoint> StorageHistory { get; } = [];
    public List<DataPoint> CpuHistory { get; } = [];
    public List<DataPoint> MemoryHistory { get; } = [];
    public List<DataPoint> CameraOnlineHistory { get; } = [];

    public event Action? HistoryUpdated;

    private const int Capacity60 = 60;
    private const int Capacity24 = 24;

    public DateTime AppStartTime { get; } = DateTime.Now;

    public MetricsHistoryService(IRecordingService recordingService, ISystemStatusService systemStatus) {
        _recordingService = recordingService;
        _systemStatus = systemStatus;
        _timer = new System.Timers.Timer(60_000);
        _timer.Elapsed += (_, _) => { RecordSample(); HistoryUpdated?.Invoke(); };
        _timer.Start();
        RecordSample();
        HistoryUpdated?.Invoke();
    }

    private void RecordSample() {
        var now = DateTime.Now;

        var recordings = _recordingService.GetActiveRecordings();
        var bytesNow = recordings.Sum(r => r.BytesWritten);
        var elapsed = (now - _lastMeasure).TotalSeconds;
        double bps = 0;
        if (elapsed > 1 && _lastBytesWritten > 0) {
            bps = (bytesNow - _lastBytesWritten) / elapsed;
        }
        _lastBytesWritten = bytesNow;
        _lastMeasure = now;
        AddPoint(BandwidthHistory, Capacity60, now, bps);

        var diskInfo = GetDiskInfo();
        AddPoint(StorageHistory, Capacity24, now, diskInfo.usedGB);

        AddPoint(CameraOnlineHistory, Capacity60, now, recordings.Count);
        AddPoint(CpuHistory, Capacity60, now, _systemStatus.CpuUsagePercent);
        AddPoint(MemoryHistory, Capacity60, now, _systemStatus.MemoryUsagePercent);
    }

    private static void AddPoint(List<DataPoint> list, int capacity, DateTime time, double value) {
        list.Add(new DataPoint(time, value));
        while (list.Count > capacity) list.RemoveAt(0);
    }

    public double GetMaxBandwidth() {
        if (BandwidthHistory.Count == 0) return 1;
        var max = BandwidthHistory.Max(p => p.Value);
        return max < 1 ? 1 : max;
    }

    public double GetMaxStorage() {
        if (StorageHistory.Count == 0) return 1;
        var max = StorageHistory.Max(p => p.Value);
        return max < 1 ? 1 : max;
    }

    private static (double totalGB, double usedGB, double freeGB) GetDiskInfo() {
        try {
            foreach (var drive in System.IO.DriveInfo.GetDrives()) {
                if (drive.IsReady)
                    return (drive.TotalSize / (1024.0 * 1024 * 1024),
                            (drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 * 1024 * 1024),
                            drive.AvailableFreeSpace / (1024.0 * 1024 * 1024));
            }
        } catch { }
        return (0, 0, 0);
    }

    public void Dispose() {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
