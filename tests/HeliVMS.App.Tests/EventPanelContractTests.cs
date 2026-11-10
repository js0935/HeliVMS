namespace HeliVMS.App.Tests;

/// <summary>
/// 牆上事件面板（QC_Live 的 modeless event view 對齊）的接線契約。桌面端沒有 WPF 執行期測試：
/// 這裡釘「面板在牆的左緣、吃的是原始偵測流（與 QCC 的 SEARCH_EVENT 同源）、
/// 點一列選取那台格位、沉浸模式收起、開關持久化且預設關」——
/// 預設關是契約的一部分：頁尾警報列已有最近三筆，面板是操作員自選的加值功能。
/// </summary>
public sealed class EventPanelContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";

    [Fact]
    public void 面板建在牆的左緣且預設收起()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("x:Name=\"EventPanel\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("x:Name=\"EventPanelToggle\"", xaml);
        Assert.Contains("Click=\"OnEventPanelToggled\"", xaml);
        Assert.Contains("SelectionChanged=\"OnEventPanelSelectionChanged\"", xaml);
    }

    [Fact]
    public void 面板吃原始偵測流且點列選格()
    {
        var source = SourceContract.Read(Code);

        // RouteAlarm（原始偵測）是餵食點，且必須回 UI 執行緒才碰 ItemsSource。
        var route = SourceContract.BodyOf(source, "RouteAlarm");
        Assert.Contains("AppendEventPanel(record)", route);
        Assert.Contains("Dispatcher.BeginInvoke", route);

        var append = SourceContract.BodyOf(source, "AppendEventPanel");
        Assert.Contains("EventPanelCapacity", append);

        var click = SourceContract.BodyOf(source, "OnEventPanelSelectionChanged");
        Assert.Contains("GetCell(row.ChannelId)", click);
        Assert.Contains("SelectCell(cell)", click);
    }

    [Fact]
    public void 沉浸模式收起且開關持久化預設關()
    {
        var source = SourceContract.Read(Code);

        var immersive = SourceContract.BodyOf(source, "SetMonitorImmersive");
        Assert.Contains("UpdateEventPanelVisibility()", immersive);

        Assert.Contains("EventPanelEnabledKey = \"wall.event_panel\"", source);
        // 預設關：== "1" 才開（與 quick bar 的 != "0" 相反），升級後的安裝零視覺變化。
        Assert.Contains("Get(EventPanelEnabledKey) == \"1\"", source);
        var toggled = SourceContract.BodyOf(source, "OnEventPanelToggled");
        Assert.Contains("Set(EventPanelEnabledKey", toggled);
    }
}
