using System.Diagnostics;

namespace HeliVMS.Storage;

/// <summary>系統健康快照（M147 §14.7 #2 閉環）：以本機真實計數器量測同一程序之記憶體／CPU／磁碟／Uptime。</summary>
public sealed record SystemMetricsSnapshot(
    string CapturedAtUtc,
    long UptimeMinutes,
    long WorkingSetMb,
    long ManagedHeapMb,
    double CpuPercent,
    IReadOnlyList<SystemDiskMetric> Disks);

/// <summary>單一就緒磁碟之容量／可用／格式。</summary>
public sealed record SystemDiskMetric(string Name, long TotalMb, long FreeMb, string Format);

/// <summary>系統健康量測服務（M147）：不依賴外部硬體，純本機計數器。</summary>
public static class SystemMetricsService
{
    /// <summary>擷取目前程序與本機磁碟的健康快照。</summary>
    public static SystemMetricsSnapshot Capture()
    {
        var proc = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - proc.StartTime.ToUniversalTime();
        var cpuPercent = uptime.TotalMilliseconds <= 0
            ? 0
            : Math.Round(proc.TotalProcessorTime.TotalMilliseconds / uptime.TotalMilliseconds * 100.0, 1);

        return new SystemMetricsSnapshot(
            SqliteStore.Iso(DateTime.UtcNow),
            (long)Math.Max(0, uptime.TotalMinutes),
            proc.WorkingSet64 / (1024 * 1024),
            GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
            Math.Min(100, cpuPercent),
            Disks());
    }

    private static IReadOnlyList<SystemDiskMetric> Disks()
    {
        var list = new List<SystemDiskMetric>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            list.Add(new SystemDiskMetric(
                drive.Name.TrimEnd('\\'),
                drive.TotalSize / (1024 * 1024),
                drive.AvailableFreeSpace / (1024 * 1024),
                string.IsNullOrWhiteSpace(drive.DriveFormat) ? "?" : drive.DriveFormat));
        }

        return list;
    }
}
