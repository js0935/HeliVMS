using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>M147 系統健康快照：驗證以本機真實計數器產生之快照欄位（不依賴外部硬體、可閉環）。</summary>
public sealed class SystemHealthTests
{
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
    }
}