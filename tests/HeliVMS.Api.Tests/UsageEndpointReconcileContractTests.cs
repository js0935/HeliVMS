namespace HeliVMS.Api.Tests;

/// <summary>
/// /config/usage 端點在回報用量前必須呼叫 <c>ReconcileUsage</c>。
///
/// <para>
/// 症狀：管理員在磁碟上刪掉某天的錄影資料夾後，索引要到下一輪背景對帳才標 missing。
/// 若端點直接回 <c>UsageBytes</c>，用量頁會一直顯示已刪除的位元組，最長一小時，期間操作員
/// 會誤判「離配額還很遠」。行為已由 <see cref="RetentionRunTests.ReconcileUsage_整天資料夾被刪除後用量立刻反映"/>
/// 釘住；這裡只守住「端點真的有接上」——把呼叫改回 <c>UsageBytes</c> 時不會有任何行為測試變紅。
/// </para>
/// </summary>
public sealed class UsageEndpointReconcileContractTests
{
    [Fact]
    public void 用量端點回報前先對帳()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "HeliVMS.WebApi", "ApiEndpoints.cs"));

        var start = source.IndexOf("\"/config/usage\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 /config/usage 端點");

        // 端點主體：從映射起往後一段足以涵蓋這個 lambda。
        var body = source[start..Math.Min(source.Length, start + 400)];

        Assert.Contains("ReconcileUsage", body);
        var reconcileAt = body.IndexOf("ReconcileUsage", StringComparison.Ordinal);
        var rawUsageAt = body.IndexOf("retention.UsageBytes", StringComparison.Ordinal);
        Assert.True(
            rawUsageAt < 0,
            "端點不該直接回 retention.UsageBytes（未對帳）；要回 ReconcileUsage() 的結果");
        Assert.True(reconcileAt >= 0);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到 HeliVMS.slnx，無法定位儲存庫根目錄");
    }
}
