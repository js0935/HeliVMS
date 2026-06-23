using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace HeliVMS.Services;

public sealed class MetricsHistoryService : IDisposable {
    private readonly System.Timers.Timer _timer;
    private readonly IRecordingService _recordingService;
    private long _lastBytesWritten;
    private DateTime _lastMeasure = DateTime.Now;

    public record DataPoint(DateTime Time, double Value);

    public List<DataPoint> BandwidthHistory { get; } = [];
    public List<DataPoint> StorageHistory { get; } = [];
    public List<DataPoint> CameraOnlineHistory { get; } = [];

    public event Action? HistoryUpdated;

    private const int BandwidthCapacity = 60;
    private const int StorageCapacity = 24;

    public MetricsHistoryService(IRecordingService recordingService) {
        _recordingService = recordingService;
        _timer = new System.Timers.Timer(60_000);
        _timer.Elapsed += (_, _) => { RecordSample(); HistoryUpdated?.Invoke(); };
        _timer.Start();
        RecordSample();
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
        AddPoint(BandwidthHistory, BandwidthCapacity, now, bps);

        var diskInfo = GetDiskInfo();
        AddPoint(StorageHistory, StorageCapacity, now, diskInfo.usedGB);

        AddPoint(CameraOnlineHistory, BandwidthCapacity, now, recordings.Count);
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
