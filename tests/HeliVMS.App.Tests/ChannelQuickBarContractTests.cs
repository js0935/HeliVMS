namespace HeliVMS.App.Tests;

/// <summary>
/// 頻道快速按鈕列（QC_Live 的 channel%02d 按鈕列＋ImmedChannels/ImmedSelAll 對齊）的接線契約。
/// 桌面端沒有 WPF 執行期測試：這裡釘「元素在牆的右緣、按鈕以牆序 Tag 定位、點擊走既有的
/// 單格展開路徑而不重連、沉浸模式收起、開關持久化」——漏任何一節的症狀都是
/// 「按了沒反應」或「點一台全部重連」，日誌乾淨。
///
/// <para>
/// 開關本體（<c>QuickBarToggle</c>）已從監控中心頂欄移到設定中心「功能」頁的「監看牆顯示」區塊：
/// 頂欄只保留入口按鈕（全畫面／回放／跳台／事件／地圖／設定／離開），顯示元件偏好收在設定裡。
/// 這條契約因此同時釘住「頂欄不再有它」與「設定裡的那一個接到正確的顯示項目」。
/// </para>
/// </summary>
public sealed class ChannelQuickBarContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";

    [Fact]
    public void 快速列建在牆的右緣且開關在設定中心()
    {
        var xaml = SourceContract.Read(Xaml);
        var settingsXaml = SourceContract.Read(SettingsXaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("x:Name=\"ChannelQuickBar\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml);
        Assert.Contains("x:Name=\"QuickBarPanel\"", xaml);

        // 開關已移入設定中心；頂欄那顆必須真的不見（留著就有兩個寫入點）。
        Assert.Contains("x:Name=\"WallQuickBarCheck\"", settingsXaml);
        Assert.Contains("Checked=\"OnWallDisplayToggled\"", settingsXaml);
        Assert.DoesNotContain("QuickBarToggle", xaml);

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
        var toggled = SourceContract.BodyOf(source, "SetQuickBarEnabled");
        Assert.Contains("Set(QuickBarEnabledKey", toggled);
        Assert.Contains("return _quickBarEnabled", toggled);

        // 設定頁那顆勾選 → WallDisplayOption.QuickBar → SetQuickBarEnabled（少一節就是「勾了沒反應」）。
        Assert.Contains(
            "_wallDisplayBoxes[WallQuickBarCheck] = WallDisplayOption.QuickBar",
            SourceContract.Read(SettingsCode));
        Assert.Contains("WallDisplayOption.QuickBar => SetQuickBarEnabled(enabled)", source);

        // RebuildCells 會重建快速列（跟著頁簽的牆序），改派後同步選取亮光。
        var rebuild = SourceContract.BodyOf(source, "RebuildCells");
        Assert.Contains("RebuildQuickBar()", rebuild);
        var apply = SourceContract.BodyOf(source, "ApplyWallAsync");
        Assert.Contains("UpdateQuickBarSelection()", apply);
    }
}
