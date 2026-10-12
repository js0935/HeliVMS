namespace HeliVMS.App.Tests;

/// <summary>
/// 切流整合契約（M76，§15.2）。桌面端沒有 WPF 執行期測試，只能靠原始碼契約把住：
/// 切流視窗要跟實際連線共用同一份狀態（不再各開各的 in-memory 合成）、採納決策要持久化、
/// 而且切顯示串流時錄影必須仍走主流——這條最要緊，漏了會安靜地把錄影畫質換成次流且無錯誤。
/// </summary>
public sealed class StreamSwitchContractTests
{
    private const string Window = "src/HeliVMS.App/StreamSwitchWindow.xaml.cs";
    private const string Manager = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string Session = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 切流視窗與頻道管理器共用切流狀態並即時套用()
    {
        var code = SourceContract.Read(Window);
        Assert.Contains("ChannelManager? manager", code);
        Assert.Contains("manager?.Switcher", code);

        var apply = SourceContract.BodyOf(code, "OnApplyClicked");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("await _manager.ApplyStreamKindAsync", apply);
    }

    [Fact]
    public void 沒有管理器時切流視窗仍會持久化()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(Window), "OnApplyClicked");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("_prefs.Save", apply);
    }

    [Fact]
    public void 頻道管理器啟動時會以資料庫恢復切流狀態()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Manager), "ChannelManager");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("Switcher.Prime", body);
        Assert.Contains("_streamPrefs.GetKind", body);
    }

    [Fact]
    public void 採納切流會持久化並即時切換顯示串流()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Manager), "ApplyStreamKindAsync");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("Switcher.ApplySwitch", body);
        Assert.Contains("_streamPrefs.Save", body);
        Assert.Contains("SwitchDisplayStreamAsync", body);
    }

    [Fact]
    public void 錄影固定用主流而顯示依碼流選擇()
    {
        var manager = SourceContract.Read(Manager);
        Assert.Contains("StreamRouting.RecordingUrl(channel.MainStreamUrl)", manager);
        Assert.Contains("StreamRouting.DisplayUrl(", manager);

        // 錄影器起點必須是 RecordingUrl；若傳 Url，切到次流後錄影會跟著降畫質。
        var recording = SourceContract.BodyOf(SourceContract.Read(Session), "SetRecordingAsync");
        Assert.NotEqual(string.Empty, recording);
        Assert.Contains("StartAsync(ChannelId, RecordingUrl", recording);
        Assert.DoesNotContain("StartAsync(ChannelId, Url", recording);
    }

    [Fact]
    public void 主視窗開啟切流視窗時會帶入管理器()
    {
        Assert.Contains("new StreamSwitchWindow(_store!, _manager)", SourceContract.Read(MainWindow));
    }

    /// <summary>
    /// 單／多分割互切不得出現「連線中」：次流常駐，切到單畫面只是「加開」一條主流，
    /// 主流首幀抵達前畫面仍由次流供應。故次流是唯一對外發布狀態的來源，
    /// 主流的事件絕不能再往 UI 發 StateChanged，否則使用者按 F11 又會閃一次。
    /// </summary>
    [Fact]
    public void 次流常駐且主流只在單畫面加開()
    {
        var session = SourceContract.Read(Session);

        // 常駐的是次流（建構子即以 subDisplayUrl 起 Client）。
        Assert.Contains("new RtspClient(subDisplayUrl", session);
        Assert.Contains("AttachSubClient(Client)", session);

        // 顯示來源在主流出畫後才切換；未出畫或失效一律退回常駐次流。
        Assert.Contains("_wantMain && _mainReady && _mainClient is not null ? _mainClient : Client", session);

        // 主流事件不發布 StateChanged，否則加開主流時格內與側欄會閃「連線中」。
        var mainAttach = SourceContract.BodyOf(session, "AttachMainClient");
        Assert.NotEqual(string.Empty, mainAttach);
        Assert.DoesNotContain("StateChanged?.Invoke", mainAttach);

        // 切流以 StreamKind 表達，不再靠重建 Client 換位址。
        Assert.Contains("SwitchDisplayStreamAsync(StreamKind kind)", session);
    }

    /// <summary>
    /// 顯示碼流政策：只有單畫面第 0 格升級主流，其餘（含多分割全部）維持次流常駐。
    /// 過去是「單畫面時所有頻道都切主流」，展開單格會同時重連每一路。
    /// </summary>
    [Fact]
    public void 單畫面只讓第零格升級主流()
    {
        var manager = SourceContract.Read(Manager);
        var policy = SourceContract.BodyOf(manager, "ApplyDisplayPolicyAsync");
        Assert.NotEqual(string.Empty, policy);
        Assert.Contains("cellCount <= 1 && session.Cell == 0 ? StreamKind.Main : StreamKind.Sub", policy);

        Assert.Contains("ApplyDisplayPolicyAsync", SourceContract.BodyOf(manager, "ConnectAsync"));
        Assert.Contains("ApplyDisplayPolicyAsync", SourceContract.BodyOf(manager, "ReconcileAsync"));
    }

    /// <summary>
    /// 監看格左鍵要能一鍵往返：多分割按一下放大成單格，同一格再按一下還原成原本的多分割。
    /// 操作員只盯著畫面時不該被迫去摸 F11／ESC／右鍵才能回到多分割。
    /// </summary>
    [Fact]
    public void 左鍵點監看格放大再點一次還原多分割()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "OnCellClicked");
        Assert.NotEqual(string.Empty, body);

        // 已左鍵展開中（單格）時，再點一次走還原路徑。
        Assert.Contains("_expandInProgress", body);
        Assert.Contains("ToggleExpandCell(0)", body);

        // 多分割下點格放大。
        Assert.Contains("CurrentCellCount() > 1", body);
        Assert.Contains("ToggleExpandCell(cell)", body);
    }

    /// <summary>
    /// 只有「左鍵展開中」的單分割才把再按一下當還原。若操作員是自己在佈局選單選了 1×1
    /// （本來就只想看單格），點畫面不該莫名跳回多分割——這正是 <c>_expandInProgress</c>
    /// 這個旗標存在的理由。
    /// </summary>
    [Fact]
    public void 選單選入的單分割點畫面不跳回多分割()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "OnCellClicked");
        Assert.NotEqual(string.Empty, body);

        var toggleGate = body.IndexOf("_expandInProgress", System.StringComparison.Ordinal);
        var restore = body.IndexOf("ToggleExpandCell(0)", System.StringComparison.Ordinal);
        Assert.True(
            toggleGate >= 0 && restore >= 0 && toggleGate < restore,
            "還原多分割必須先檢查 _expandInProgress 才呼叫 ToggleExpandCell(0)。");
    }
}
