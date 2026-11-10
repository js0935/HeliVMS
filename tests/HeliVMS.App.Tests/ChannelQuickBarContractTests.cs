namespace HeliVMS.App.Tests;

/// <summary>
/// 頻道快速按鈕列（QC_Live 的 channel%02d 按鈕列＋ImmedChannels/ImmedSelAll 對齊）的接線契約。
/// 桌面端沒有 WPF 執行期測試：這裡釘「元素在牆的右緣、按鈕以牆序 Tag 定位、點擊走既有的
/// 單格展開路徑而不重連、沉浸模式收起、開關持久化」——漏任何一節的症狀都是
/// 「按了沒反應」或「點一台全部重連」，日誌乾淨。
/// </summary>
public sealed class ChannelQuickBarContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";

    [Fact]
    public void 快速列建在牆的右緣且帶全部按鈕()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("x:Name=\"ChannelQuickBar\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml);
        Assert.Contains("x:Name=\"QuickBarPanel\"", xaml);
        Assert.Contains("x:Name=\"QuickBarToggle\"", xaml);
        Assert.Contains("Click=\"OnQuickBarToggled\"", xaml);

        var rebuild = SourceContract.BodyOf(source, "RebuildQuickBar");
        Assert.Contains("OnQuickAllClicked", rebuild);
        Assert.Contains("OnQuickChannelClicked", rebuild);
        Assert.Contains("Tag = wallIndex", rebuild);
    }

    [Fact]
    public void 點擊走單格展開路徑且不重連()
    {
        var click = SourceContract.BodyOf(SourceContract.Read(Code), "OnQuickChannelClicked");
        Assert.Contains("ToggleExpandCell", click);
        Assert.Contains("ApplyWallAsync(wallIndex)", click);
        // QC_Live 的按鈕是「即時顯示」：只改派格位，絕不重連（重連是 F11 全黑的舊傷）。
        Assert.DoesNotContain("ConnectAsync", click);
        Assert.Contains("_cellChannel[0]", click);
    }

    [Fact]
    public void 沉浸模式收起且開關持久化()
    {
        var source = SourceContract.Read(Code);

        var immersive = SourceContract.BodyOf(source, "SetMonitorImmersive");
        Assert.Contains("UpdateQuickBarVisibility()", immersive);
        var visibility = SourceContract.BodyOf(source, "UpdateQuickBarVisibility");
        Assert.Contains("!_monitorImmersive", visibility);

        Assert.Contains("QuickBarEnabledKey = \"wall.quick_bar\"", source);
        var toggled = SourceContract.BodyOf(source, "OnQuickBarToggled");
        Assert.Contains("Set(QuickBarEnabledKey", toggled);

        // RebuildCells 會重建快速列（跟著頁簽的牆序），改派後同步選取亮光。
        var rebuild = SourceContract.BodyOf(source, "RebuildCells");
        Assert.Contains("RebuildQuickBar()", rebuild);
        var apply = SourceContract.BodyOf(source, "ApplyWallAsync");
        Assert.Contains("UpdateQuickBarSelection()", apply);
    }
}
