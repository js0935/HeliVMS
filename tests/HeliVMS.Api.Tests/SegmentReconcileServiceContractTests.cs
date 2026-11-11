using System.Text.RegularExpressions;

namespace HeliVMS.Api.Tests;

/// <summary>
/// 純 Web 部署的索引維護契約（<see cref="HeliVMS.WebApi.SegmentReconcileService"/>）。
///
/// <para>
/// 桌面（MainWindow 保留迴圈）與 Headless Agent（IntegrityWorker）各自都會跑對帳、段長修正
/// 與失敗原因回填；純 Web 部署掛不到這兩個宿主，唯一的維護入口就是這支每小時服務。
/// 三項工作缺任何一項，同一份索引在不同部署下呈現不同的世界——桌面版修好的舊列，
/// 純 Web 機器上永遠保持原樣。
/// </para>
/// </summary>
public sealed class SegmentReconcileServiceContractTests
{
    private const string ServiceCode = "src/HeliVMS.WebApi/SegmentReconcileService.cs";

    private static string Read() =>
        File.ReadAllText(Path.Combine(RepoRoot(), ServiceCode));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("自測試輸出往上找不到 HeliVMS.slnx，無法驗證 WebApi 契約。");
    }

    [Fact]
    public void 純Web部署的每小時輪也會修正段長與回填失敗原因()
    {
        var source = Read();

        Assert.Matches(
            @"var\s+report\s*=\s*SegmentReconcileCoordinator\.RunConfigured\(",
            source);
        Assert.Matches(
            @"var\s+repair\s*=\s*SegmentDurationRepairCoordinator\.RunConfigured\(",
            source);
        Assert.Matches(
            @"var\s+backfill\s*=\s*SegmentFailureReasonBackfillCoordinator\.RunConfigured\(",
            source);
    }

    /// <summary>
    /// 三項工作每一項都必須被自己的 try/catch 包住：單輪失敗（庫被鎖、ffprobe 不在）
    /// 只記警告，不得讓服務停擺——對帳的正確性不值得拿錄影中斷去換。
    /// </summary>
    [Fact]
    public void 每一項維護工作都被自己的例外守衛包住()
    {
        var source = Read();

        foreach (var name in new[]
                 {
                     "SegmentReconcileCoordinator.RunConfigured",
                     "SegmentDurationRepairCoordinator.RunConfigured",
                     "SegmentFailureReasonBackfillCoordinator.RunConfigured",
                 })
        {
            var callAt = source.IndexOf(name + "(", StringComparison.Ordinal);
            Assert.True(callAt >= 0, $"找不到 {name} 的呼叫");

            var bodyStart = source.LastIndexOf("try", callAt, StringComparison.Ordinal);
            Assert.True(bodyStart >= 0 && callAt - bodyStart < 200, $"{name} 必須包在 try 內");

            var catchAt = source.IndexOf("catch", callAt, StringComparison.Ordinal);
            Assert.True(catchAt > callAt, $"{name} 的 try 必須有對應的 catch");
        }
    }

    /// <summary>初始延遲與一小時間隔必須保留：與保留清理錯開，避免同時搶 SQLite 寫鎖。</summary>
    [Fact]
    public void 維護輪的節奏與保留清理錯開()
    {
        var source = Read();
        Assert.Contains("TimeSpan.FromHours(1)", source);
        Assert.Contains("TimeSpan.FromMinutes(5)", source);
        Assert.True(Regex.IsMatch(source, "InitialDelay"), "必須有開機延後啟動");
    }
}
