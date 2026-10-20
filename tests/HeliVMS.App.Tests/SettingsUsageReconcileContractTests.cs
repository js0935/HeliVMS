namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心「儲存」頁的用量顯數前必須先做一次資料夾層級對帳。
///
/// <para>
/// 症狀：使用者把錄影從磁碟刪掉之後，索引要到下一輪背景對帳才標 missing。在那之前
/// <c>GetTotalUsageSummary</c> 仍會把已刪除的段算進用量與段數，畫面於是顯示「明明刪了卻還佔著」，
/// 而這個數字正是操作員判斷「離配額還有多遠」的依據。
/// </para>
/// <para>
/// 這裡守住的接縫：呼叫必須是一行真正的敘述句（把呼叫註解掉時契約不能還通過），
/// 且必須發生在 <c>GetTotalUsageSummary</c> 之前——先算再標就等於這一輪仍然顯示舊數字。
/// 對帳失敗不可讓用量頁打不開，所以外層必須有 try 保護。成本只與「天數 × 頻道數」有關，
/// 逐檔 stat 仍留給背景對帳（見 <c>ReconcileFolders</c> 的說明）。
/// </para>
/// </summary>
public sealed class SettingsUsageReconcileContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";

    [Fact]
    public void 用量顯數前先對帳資料夾存在且不逐檔stat()
    {
        var source = SourceContract.Read(Code);

        var usage = SourceContract.BodyOf(source, "ReloadUsage");
        var reconcileAt = usage.IndexOf("ReconcileFolders", StringComparison.Ordinal);
        var summaryAt = usage.IndexOf("GetTotalUsageSummary", StringComparison.Ordinal);
        Assert.True(reconcileAt >= 0, "ReloadUsage 必須先對帳資料夾存在");
        Assert.True(
            reconcileAt < summaryAt,
            "對帳必須在 GetTotalUsageSummary 之前：先算再標會讓這一輪仍顯示已刪除段的用量");

        // 不能只驗字串存在：把呼叫註解掉（// ...ReconcileFolders();）時 IndexOf 仍會命中，
        // 契約就變成裝飾品。這裡要求它是一行真正的敘述句。
        var callLine = usage
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains("ReconcileFolders()", StringComparison.Ordinal)
                && !line.StartsWith("//", StringComparison.Ordinal));
        Assert.True(
            callLine is not null,
            "ReloadUsage 必須真的呼叫 ReconcileFolders()（不能只是註解或宣告）");

        // 對帳只跑資料夾層級：整段歷史幾千次 Directory.Exists，不逐檔 stat。
        Assert.Contains("new SegmentIndexReconciler(", usage);

        // 對帳失敗不該讓用量頁打不開。
        Assert.Contains("catch", usage);
    }
}
