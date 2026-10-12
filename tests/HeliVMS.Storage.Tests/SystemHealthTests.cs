using HeliVMS.Shared;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>M147 系統健康快照：驗證以本機真實計數器產生之快照欄位（不依賴外部硬體、可閉環）。</summary>
public sealed class SystemHealthTests
{
    [Fact]
    public void Capture_ReflectsFfmpegProcessRegistry()
    {
        // §13-2：健康度快照必須把全域 ffmpeg 子行程登錄表曝光，否則「這台開著幾條
        // ffmpeg、有沒有失控」就沒有單一出處。用 TotalStarted（單調遞增）斷言，
        // 以免與其他並行測試類別共用 Shared 登錄表時互相干擾。
        var before = SystemMetricsService.Capture().FfmpegTotalStarted;

        using (FfmpegProcessRegistry.Shared.Register(FfmpegJobKind.Probe, "system-health-test"))
        {
            var during = SystemMetricsService.Capture();
            Assert.True(
                during.FfmpegTotalStarted >= before + 1,
                "註冊一個 ffmpeg 子行程後，TotalStarted 必須增加");
            Assert.True(during.FfmpegRunning >= 1);
        }

        // 欄位為真實計數，不應為負。
        var snap = SystemMetricsService.Capture();
        Assert.True(snap.FfmpegDecode >= 0);
        Assert.True(snap.FfmpegRecord >= 0);
        Assert.True(snap.FfmpegPublish >= 0);
        Assert.True(snap.FfmpegPeakConcurrent >= 0);
    }

    [Fact]
    public void Capture_ProducesRealProcessAndDiskSnapshot()
    {
        var snap = SystemMetricsService.Capture();

        Assert.False(string.IsNullOrWhiteSpace(snap.CapturedAtUtc));
        Assert.True(snap.UptimeMinutes >= 0);
        Assert.True(snap.WorkingSetMb > 0, "工作集必須為正（真實程序）");
        Assert.True(snap.ManagedHeapMb >= 0);
        Assert.InRange(snap.CpuPercent, 0, 100);

        Assert.NotEmpty(snap.Disks);
        Assert.All(snap.Disks, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.True(d.TotalMb >= 0);
            Assert.True(d.FreeMb >= 0);
            Assert.True(d.FreeMb <= d.TotalMb, $"{d.Name} 可用({d.FreeMb}Mb)不得超過總量({d.TotalMb}Mb)");
            Assert.False(string.IsNullOrWhiteSpace(d.Format));
        });

        Assert.True(
            SystemMetricsService.CaptureHistory().Count >= 1,
            "M148 記憶體趨勢 #1 閉環：Capture 至少已入列一筆趨勢點");
    }

    [Fact]
    public void Capture_TrendHistory_HoldsLastSixtyInOrder()
    {
        SystemMetricsService.Capture();
        SystemMetricsService.Capture();

        var history = SystemMetricsService.CaptureHistory();

        Assert.NotEmpty(history);
        Assert.True(history.Count <= 60, "趨勢佇列（M148 #1）最多 60 筆");
        Assert.All(history, p => Assert.True(p.WorkingSetGb > 0));
    }

    [Fact]
    public void Capture_RecordsDiskCapacityTrend()
    {
        SystemMetricsService.Capture();

        var history = SystemMetricsService.CaptureDiskHistory();

        Assert.NotEmpty(history);
        Assert.All(history, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.FreeMb >= 0);
            Assert.True(p.TotalMb >= 0);
            Assert.True(p.FreeMb <= p.TotalMb, $"{p.Name} 可用({p.FreeMb}Mb)不得超過總量({p.TotalMb}Mb)");
        });
    }
}