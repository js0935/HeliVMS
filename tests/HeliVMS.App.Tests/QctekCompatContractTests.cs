namespace HeliVMS.App.Tests;

/// <summary>
/// QCTek CMS 相容服務契約（M2c）。
/// </summary>
/// <remarks>
/// <para>
/// 這條接線斷掉的症狀與內嵌 Web 服務同一類：設定頁勾了啟用、狀態文字顯示執行中，
/// 實際上 NEMO 伺服器從沒被拉起，QCC 加裝置永遠失敗——桌面端沒有 WPF 執行期測試，
/// 因此用來源契約把 SettingsWindow ↔ <c>QctekCompatHost</c> ↔ MainWindow 三段釘住。
/// </para>
/// <para>
/// 另一條不能省的是「憑證跟隨本機登入驗證」：相容層如果自己發明一套驗證
/// （或反過來完全不驗），就會與操作員在設定頁看到的政策不一致。
/// </para>
/// </remarks>
public sealed class QctekCompatContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Host = "src/HeliVMS.App/Services/QctekCompatHost.cs";

    [Fact]
    public void 設定中心有可啟停QCTek相容服務的欄位與按鈕()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "QctekSectionTitle", "QctekIntroText", "QctekEnabledBox", "QctekPortBox",
                     "QctekApplyButton", "QctekServiceStatusText",
                     "QctekAlarmEnabledBox", "QctekAlarmHostBox", "QctekAlarmPortBox",
                     "QctekAlarmStatusText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Contains("Click=\"OnApplyQctekClicked\"", xaml);
    }

    [Fact]
    public void 網路頁會把QCTek設定寫進共用設定並即時套用()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnApplyQctekClicked");
        Assert.NotEqual(string.Empty, handler);
        Assert.Contains("QctekCompatHost.EnabledKey", handler);
        Assert.Contains("QctekCompatHost.PortKey", handler);
        Assert.Contains("_qctekHost.ApplySettings", handler);

        // 有協調者時走它：QCC 連的是 Web 埠，相容服務要改成多工器，
        // 而「誰先讓出公開埠」只有協調者知道。
        Assert.Contains("_network.Apply(_settings)", handler);

        // M3b：警報回報的三個設定鍵也必須由同一顆「套用」寫入（沒有套用＝永遠不生效）。
        Assert.Contains("QctekCompatHost.AlarmEnabledKey", handler);
        Assert.Contains("QctekCompatHost.AlarmHostKey", handler);
        Assert.Contains("QctekCompatHost.AlarmPortKey", handler);
    }

    [Fact]
    public void 主視窗啟動時會依設定拉起QCTek相容服務並於關閉時停掉()
    {
        var code = SourceContract.Read(MainWindow);

        var onLoaded = SourceContract.BodyOf(code, "OnLoaded");
        Assert.NotEqual(string.Empty, onLoaded);
        Assert.Contains("_qctekHost = new QctekCompatHost(", onLoaded);

        // 與 Web 服務同一個協調者：相容服務一律在 Web 埠走多工器（QCC 連的是 Web 埠，
        // 沒有地方填 NEMO 埠），不同埠再另開獨立 NemoServer；順序錯了就是
        // 「連接埠已被占用」。
        Assert.Contains("_networkHost = new NetworkPortCoordinator(_webHost, _qctekHost)", onLoaded);
        Assert.Contains("_networkHost.Apply(new SettingsRepository(_store))", onLoaded);

        // 關閉時一定要把 listener 收掉；否則程序結束後連接埠仍被占用，下次啟動直接失敗。
        // 收尾一律先換成本地變數再丟進 RunShutdownStep（與網頁主機同一條序列）。
        Assert.Contains("RunShutdownStep(\"關閉 QCTek 相容服務\", () => DisposeSync(qctekHost)", code);
    }

    [Fact]
    public void 相容服務憑證跟隨本機登入驗證且宣告相容產品名()
    {
        var host = SourceContract.Read(Host);

        // auth.enabled=1 → 走 AuthService（含鎖定與稽核）；未啟用 → 不驗密碼。
        Assert.Contains("_auth.IsAuthEnabled", host);
        Assert.Contains("_auth.Authenticate(user, password).Succeeded", host);

        // 宣告 QCV8001 讓 QCC 走相容路徑；沒有 VNC 時誠實回 FAIL
        // （回 OK 會讓 CMS 去連一個不存在的 RFB 埠）。
        Assert.Contains("\"QCV8001\"", host);
        Assert.Contains("HasVnc => false", host);
    }

    [Fact]
    public void 事件會從主視窗一路回報到QCTek警報客戶端()
    {
        // 這條接線斷掉的症狀：CMS 看得到 HeliVMS 上線（HELLO），但永遠收不到事件，
        // 而日誌乾淨——三個環節（主視窗路由 → 宿主轉發 → 客戶端佇列）缺一不可。
        var code = SourceContract.Read(MainWindow);
        var route = SourceContract.BodyOf(code, "RouteAlarm");
        Assert.NotEqual(string.Empty, route);
        Assert.Contains("_qctekHost?.ReportEvent", route);

        var host = SourceContract.Read(Host);
        Assert.Contains("public void ReportEvent(", host);
        Assert.Contains("new QctekAlarmEvent(", host);
        Assert.Contains("_alarmClient?.Report", host);
    }

    [Fact]
    public void 相容服務與警報回報都接上診斷日誌()
    {
        // QCC 實測失敗時（連不上、命令不認識、密碼錯，畫面上長得一樣）診斷日誌
        // 是唯一的線索——兩個方向都要接；漏接的症狀是「什麼都查不到」。
        var host = SourceContract.Read(Host);
        Assert.Contains("helivms-qctek.log", host);
        Assert.Contains("log: Diag", host);  // NEMO 伺服器
        Assert.Contains("Log = Diag", host); // 警報回報
    }

    [Fact]
    public void 警報回報由同一顆套用啟停且Dispose會停止()
    {
        var host = SourceContract.Read(Host);

        // 設定套用時要真的啟停 QctekAlarmClient（只寫設定不啟停＝回報永遠不會動）。
        Assert.Contains("ApplyAlarmSettings(settings)", host);
        Assert.Contains("new QctekAlarmClient(", host);
        Assert.Contains("IsAlarmReporting", host);

        // 關閉程式時一定要停掉回報（背景連線不關，行程結束前會多送殘留訊息）。
        var dispose = SourceContract.BodyOf(host, "Dispose");
        Assert.NotEqual(string.Empty, dispose);
        Assert.Contains("StopAlarm()", dispose);
    }

    [Fact]
    public void 相容服務文案三語都在且帶參數的保留格式()
    {
        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");
        foreach (var key in new[]
                 {
                     "Settings.QctekSectionTitle", "Settings.QctekIntroText", "Settings.QctekEnabled",
                     "Settings.QctekPortLabel", "Settings.QctekStopped", "Settings.QctekRunning",
                     "Settings.QctekStartFailed", "Settings.QctekPortInvalid",
                     "Settings.QctekSavedNextStart", "Settings.QctekApplyTip",
                     "Settings.QctekAlarmEnabled", "Settings.QctekAlarmHostLabel",
                     "Settings.QctekAlarmPortLabel", "Settings.QctekAlarmStopped",
                     "Settings.QctekAlarmRunning", "Settings.QctekAlarmInvalid",
                 })
        {
            var count = i18n.Split('\n').Count(line => line.Contains($"\"{key}\"", StringComparison.Ordinal));
            Assert.True(count == 3, $"{key} 應該有三語，實際 {count}");
        }

        // 帶參數的文案必須保留 {0}，否則 string.Format 會拋例外或吃掉目標位址。
        foreach (var key in new[] { "Settings.QctekRunning", "Settings.QctekStartFailed", "Settings.QctekAlarmRunning" })
        {
            var lines = i18n.Split('\n').Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal)).ToList();
            Assert.All(lines, line => Assert.Contains("{0}", line));
        }
    }
}
