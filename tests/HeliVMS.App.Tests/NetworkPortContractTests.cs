namespace HeliVMS.App.Tests;

/// <summary>
/// Web 服務與 QCTek 相容服務「共用同一個埠」的接線契約。
/// </summary>
/// <remarks>
/// <para>
/// QCC 連的是裝置的 Web 埠（真機 QCV8001 把 NEMO 與 HTTP 多工在同一個埠）。
/// 所以當操作員把 <c>qctek.compat.port</c> 設成與 <c>web.port</c> 相同時，公開埠必須由
/// 多工器持有、Kestrel 改聽系統指派的臨時埠。
/// </para>
/// <para>
/// 兩個順序與一個回報不能省：
/// </para>
/// <list type="number">
/// <item><b>要共用時</b> Web 先讓出公開埠（改聽臨時埠），多工器才綁公開埠。</item>
/// <item><b>不共用時</b> 多工器先放掉公開埠，Web 才綁得回去。</item>
/// <item><b>對外回報</b> <c>RemoteConsolePort</c> 必須是公開埠，不是臨時埠。</item>
/// </list>
/// <para>
/// 順序反了的症狀都一樣：設定頁一句「啟動失敗」，或 QCC 顯示「加入成功、連線失敗」，
/// 日誌乾淨。桌面端沒有 WPF 執行期測試，因此用來源契約釘住。
/// </para>
/// </remarks>
public sealed class NetworkPortContractTests
{
    private const string Coordinator = "src/HeliVMS.App/Services/NetworkPortCoordinator.cs";
    private const string WebHost = "src/HeliVMS.App/Services/WebHost.cs";
    private const string QctekHost = "src/HeliVMS.App/Services/QctekCompatHost.cs";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string ProbeMiddleware = "src/HeliVMS.WebApi/QctekProbeMiddleware.cs";

    [Fact]
    public void 共用埠時Web先讓出公開埠多工器才綁()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(Coordinator), "Apply");
        Assert.NotEqual(string.Empty, apply);

        var webReleases = apply.IndexOf("_web.ApplyLocal(settings, sharePort: true)", StringComparison.Ordinal);
        var muxBinds = apply.IndexOf("_qctek.ApplyLocal(settings, sharePort: true", StringComparison.Ordinal);

        Assert.True(webReleases >= 0, "共用埠時 Web 必須改聽臨時埠");
        Assert.True(muxBinds >= 0, "共用埠時相容服務必須走多工器");
        Assert.True(webReleases < muxBinds, "順序反了：多工器會撞上還被 Kestrel 佔著的公開埠");
    }

    [Fact]
    public void 不共用時多工器先放掉公開埠Web才綁回去()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(Coordinator), "Apply");
        Assert.NotEqual(string.Empty, apply);

        // 這條路徑是「操作員只是改了另一個服務的埠」：若不先 StopMux，
        // Kestrel 會因連接埠被多工器佔著而起不來——而 Web 頁面就此消失。
        var stopMux = apply.LastIndexOf("_qctek.StopMux()", StringComparison.Ordinal);
        var webBinds = apply.LastIndexOf("_web.ApplyLocal(settings, sharePort: false)", StringComparison.Ordinal);

        Assert.True(stopMux >= 0, "不共用時必須先停掉多工器");
        Assert.True(webBinds >= 0, "不共用時 Web 要綁回公開埠");
        Assert.True(stopMux < webBinds, "順序反了：Kestrel 會綁不上被多工器佔住的公開埠");
    }

    [Fact]
    public void 多工器起不來時會把Web還原到公開埠()
    {
        // 公開埠被別的程式占用時，多工器綁不上——此時若把 Kestrel 留在臨時埠，
        // 管理介面就從此沒有對外入口（也沒有人記得那個隨機埠）。
        var apply = SourceContract.BodyOf(SourceContract.Read(Coordinator), "Apply");
        Assert.Contains("if (_qctek.IsMuxRunning)", apply);
        Assert.Contains("_web.ApplyLocal(settings, sharePort: false)", apply);
    }

    [Fact]
    public void 共用埠時Kestrel會被告知對外公開埠()
    {
        var web = SourceContract.Read(WebHost);

        // 公開埠讓給多工器 → Kestrel 綁 0（系統指派），實際埠從 IServerAddressesFeature 讀。
        Assert.Contains("sharePort ? 0 : port", web);
        Assert.Contains("IServerAddressesFeature", web);
        Assert.Contains("InternalPort", web);

        // RemoteConsolePort 必須回報公開埠；否則 QCC 記下的是只有本行程知道的臨時埠。
        Assert.Contains("QctekHttpResponder.PublicPortConfigKey", web);
    }

    [Fact]
    public void 探測頁回報的埠不是內部臨時埠()
    {
        // /getparam.cgi?RemoteConsolePort 與 DVR_Info 是同一個 Context；
        // 忘了走 ResolveConsolePort 的話，頁面會寫著 51234 這種號碼。
        var middleware = SourceContract.Read(ProbeMiddleware);
        Assert.Contains("QctekHttpResponder.ResolveConsolePort(", middleware);
        Assert.Contains("PublicPortConfigKey", middleware);
    }

    [Fact]
    public void 套用任何一邊都會同時刷新兩條服務狀態()
    {
        // 兩個服務是否共用埠由「兩邊的設定」共同決定：只按 Web 的「套用」也可能
        // 讓多工器接手，此時 QCTek 的狀態列若還寫著舊的埠（或「已停止」），
        // 操作員會以為相容服務沒在跑。
        var code = SourceContract.Read(SettingsCode);

        foreach (var handler in new[] { "OnApplyWebClicked", "OnApplyQctekClicked" })
        {
            var body = SourceContract.BodyOf(code, handler);
            Assert.NotEqual(string.Empty, body);
            Assert.Contains("_network.Apply(_settings)", body);
            Assert.Contains("WebServiceStatusText.Text = result.Web", body);
            Assert.Contains("QctekServiceStatusText.Text = result.Qctek", body);
        }
    }

    [Fact]
    public void 設定中心會收到主視窗建立的協調者()
    {
        // 沒有傳進去的話，設定頁的「套用」會各自套用而繞過順序（回到舊行為），
        // 但畫面上完全看不出來。
        Assert.Contains("networkHost: _networkHost", SourceContract.Read(MainWindow));
        Assert.Contains(
            "NetworkPortCoordinator? networkHost = null",
            SourceContract.Read(SettingsCode));
    }

    [Fact]
    public void 相容服務換埠時會重開而不是沿用舊listener()
    {
        // 沿用的症狀：設定頁顯示新埠，listener 還停在舊埠，CMS 永遠連不上，
        // 而且沒有任何錯誤——「改了埠但沒生效」是最難查的一種。
        var start = SourceContract.BodyOf(SourceContract.Read(QctekHost), "Start");
        Assert.NotEqual(string.Empty, start);
        Assert.Contains("running.Port == port", start);
        Assert.Contains("Stop()", start);
    }
}
