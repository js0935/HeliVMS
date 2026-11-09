namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆跳台序列（QC_Live AutoScan 對齊）的接線契約。桌面端沒有 WPF 執行期測試：
/// 落點算術由 <c>HeliVMS.Shared.Tests.WallAutoScanPlannerTests</c> 行為測試保護，
/// 這裡確認主視窗真的用那份規劃、開關與週期真的持久化——
/// 「算式對了但牆沒用它」「重開後輪巡忘光」都是只有接線契約擋得住的斷點。
/// </summary>
public sealed class WallSequenceContractTests
{
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 跳台推進走共用的輪巡規劃而不是第二份取模()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "AdvanceWallSequence");
        Assert.NotEqual(string.Empty, body);

        // 與 QC_Live 相同：推進一個分割量、尾頁夾住、分割量蓋滿全部頻道時不動。
        // 第二份取模會在尾頁畫出半頁空格，並在 cells >= count 時每個 tick 空轉改派。
        Assert.Contains("WallAutoScanPlanner.NextOffset", body);
        Assert.Contains("if (next == _wallSequenceIndex)", body);
        Assert.DoesNotContain("_wallSequenceIndex + cellCount) % wall.Count", body);
    }

    [Fact]
    public void 輪巡開關與週期有持久化()
    {
        var code = SourceContract.Read(MainWindow);

        // QC_Live 把 AutoScanEnable/AutoScanPeriod 記在 UsageHabit；HeliVMS 原本只活在記憶體，
        // 重開就忘，操作員每天開機都要重新勾一次。
        Assert.Contains("WallSequenceEnabledKey = \"wall.auto_scan\"", code);
        Assert.Contains("WallSequenceSecondsKey = \"wall.auto_scan_seconds\"", code);
        var toggled = SourceContract.BodyOf(code, "OnWallSequenceToggled");
        Assert.Contains("Set(WallSequenceEnabledKey", toggled);

        var load = SourceContract.BodyOf(code, "LoadAutoScanSetting")
            is { Length: > 0 } found ? found : code;
        Assert.Contains("Get(WallSequenceEnabledKey)", load);
        Assert.Contains("GetDoubleOrDefault(WallSequenceSecondsKey", load);
        Assert.Contains("WallSequenceToggle.IsChecked = _wallSequenceEnabled", load);
    }

    [Fact]
    public void 輪巡週期來自設定而不是寫死的五秒()
    {
        var code = SourceContract.Read(MainWindow);
        Assert.Contains("TimeSpan.FromSeconds(_wallSequencePeriodSeconds)", code);
        Assert.DoesNotContain("DispatcherTimer(TimeSpan.FromSeconds(5)", code);
    }
}
