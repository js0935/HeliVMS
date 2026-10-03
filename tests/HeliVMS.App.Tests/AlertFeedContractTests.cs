namespace HeliVMS.App.Tests;

/// <summary>
/// 警報即時推播契約（M118 警報中樞接進桌面端）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，事件中心／警報管理器的推播接線只能靠原始碼契約把住。
/// 這裡防的回歸是：警報寫入後忘了發布（畫面退回只能等 15 秒輪詢，事件明明已寫入卻看不到）、
/// 主視窗開窗時忘了帶中樞（訂閱拿不到同一個實例，接了等於沒接）、
/// 以及關窗沒釋放訂閱（長開的桌面端會累積亡故訂閱者）。
/// </para>
/// </summary>
public sealed class AlertFeedContractTests
{
    private const string ChannelManager = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string EventCenter = "src/HeliVMS.App/EventCenterWindow.xaml.cs";
    private const string AlarmManager = "src/HeliVMS.App/AlarmManagerWindow.xaml.cs";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string AlertFeed = "src/HeliVMS.App/Services/AlertFeed.cs";

    [Fact]
    public void 頻道管理器會把新警報發布到推播中樞()
    {
        var code = SourceContract.Read(ChannelManager);
        Assert.Contains("public AlertBroadcastHub Alerts", code);

        // 事件接線已抽到 CreateSession（連線與切流共用），發布必須跟著在那裡。
        var body = SourceContract.BodyOf(code, "CreateSession");
        Assert.NotEqual(string.Empty, body);
        // 事件寫進 alarm_events 後就要發布，否則訂閱端永遠不會被叫醒。
        Assert.Contains("Alerts.Publish(new AlertUpdate(\"alarm.created\"", body);
    }

    [Fact]
    public void 事件中心與警報管理器以同一中樞建立推播訂閱()
    {
        foreach (var (file, method) in new[]
                 {
                     (EventCenter, "EventCenterWindow"),
                     (AlarmManager, "AlarmManagerWindow"),
                 })
        {
            var code = SourceContract.Read(file);
            Assert.Contains("AlertBroadcastHub? alerts", code);

            var body = SourceContract.BodyOf(code, method);
            Assert.NotEqual(string.Empty, body);
            Assert.Contains("new AlertFeed(alerts, Dispatcher", body);
        }
    }

    [Fact]
    public void 主視窗開啟警報視窗時會帶入同一推播中樞()
    {
        var code = SourceContract.Read(MainWindow);
        // 沒帶中樞就建立了一個沒人發布的空 hub，畫面又退回純輪詢。
        Assert.Contains("new EventCenterWindow(_store!, alerts: _manager?.Alerts)", code);
        Assert.Contains("new EventCenterWindow(_store!, channelId, _manager?.Alerts)", code);
        Assert.Contains("new AlarmManagerWindow(_store!, _manager?.Alerts)", code);
    }

    [Fact]
    public void 關閉警報視窗會釋放推播訂閱()
    {
        foreach (var file in new[] { EventCenter, AlarmManager })
        {
            var body = SourceContract.BodyOf(SourceContract.Read(file), "OnClosed");
            Assert.NotEqual(string.Empty, body);
            Assert.Contains("_feed?.Dispose()", body);
        }
    }

    [Fact]
    public void 推播訂閱會耗盡資料流並在關閉時釋放租約()
    {
        var code = SourceContract.Read(AlertFeed);
        Assert.Contains("hub.Subscribe(out var reader)", code);
        Assert.Contains("ReadAllAsync()", code);
        Assert.Contains("_lease.Dispose()", code);
    }
}
