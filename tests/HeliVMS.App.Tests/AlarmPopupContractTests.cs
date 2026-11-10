namespace HeliVMS.App.Tests;

/// <summary>
/// 浮動警報窗（QC_Live 的 <c>modeless alarm dialog</c>/<c>AlarmDialogAction</c> 對齊）的接線契約。
/// 釘五件事：元素在牆上方且預設收起、吃的是<b>通知級</b>事件（聚合兩條路都匯入同一 helper）、
/// 新警報重置倒數、開關持久化且預設開、<b>沉浸模式仍然顯示</b>（全畫面看牆時警報最不能漏——
/// 與快速列／事件面板相反，這是刻意的不對稱）。
/// </summary>
public sealed class AlarmPopupContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";

    [Fact]
    public void 警報窗建在牆上方且預設收起()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"AlarmPopup\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml);
        Assert.Contains("VerticalAlignment=\"Top\"", xaml);
        Assert.Contains("x:Name=\"AlarmPopupText\"", xaml);
        Assert.Contains("x:Name=\"AlarmPopupToggle\"", xaml);
        Assert.Contains("Click=\"OnAlarmPopupToggled\"", xaml);
    }

    [Fact]
    public void 聚合兩條路都匯入同一個NotifyAlarm()
    {
        var source = SourceContract.Read(Code);

        var route = SourceContract.BodyOf(source, "RouteAlarm");
        // 未聚合（原始事件）與聚合觸發都經 NotifyAlarm——各寫一次 _notify?.Enqueue 遲早漂移。
        Assert.Contains("NotifyAlarm(enriched)", route);
        Assert.Contains("NotifyAlarm(new AlarmEventRecord", route);
        Assert.DoesNotContain("_notify?.Enqueue(enriched)", route);
        Assert.DoesNotContain("_notify?.Enqueue(new AlarmEventRecord", route);

        var notify = SourceContract.BodyOf(source, "NotifyAlarm");
        Assert.Contains("_notify?.Enqueue(record)", notify);
        Assert.Contains("ShowAlarmPopup(record)", notify);
        // RouteAlarm 可能來自解碼執行緒：回 UI 執行緒才碰浮窗。
        Assert.Contains("Dispatcher.BeginInvoke", notify);
    }

    [Fact]
    public void 新警報重置隱藏倒數()
    {
        var show = SourceContract.BodyOf(SourceContract.Read(Code), "ShowAlarmPopup");
        Assert.Contains("_alarmPopupGeneration", show);
        Assert.Contains("_alarmPopupTimer", show);
    }

    [Fact]
    public void 開關持久化且預設開()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("AlarmPopupEnabledKey = \"wall.alarm_popup\"", source);
        // 預設開：!= "0" 才關（QC_Live 的警報窗是常駐行為；與事件面板的 == "1" 相反是刻意的）。
        Assert.Contains("Get(AlarmPopupEnabledKey) != \"0\"", source);
        var toggled = SourceContract.BodyOf(source, "OnAlarmPopupToggled");
        Assert.Contains("Set(AlarmPopupEnabledKey", toggled);
    }

    [Fact]
    public void 沉浸模式不清掉警報窗()
    {
        // 快速列／事件面板在沉浸模式收起；警報窗刻意不收——全畫面看牆時警報最不能漏。
        var immersive = SourceContract.BodyOf(SourceContract.Read(Code), "SetMonitorImmersive");
        Assert.DoesNotContain("AlarmPopup", immersive);
    }
}
