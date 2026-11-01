namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆「聽現場聲音」的接線契約（2026-11 重設計：逐台喇叭、獨佔收音）。
/// </summary>
/// <remarks>
/// <para>
/// 舊版是頂欄一顆切換鈕＋單分割限定。重設計的理由：「這台攝影機有沒有接麥克風」是設備的事實，
/// 不是畫面佈局的事實——設定搬到頻道管理，圖示搬到每格右下角，多分割也能指定要聽哪一台。
/// </para>
/// <para>
/// 這條契約釘住四件漏掉時「畫面完全正常」的事：圖示只在有開收音的攝影機上出現（不然按了
/// 也沒聲音）；點擊是獨佔切換（點 02 就把 01 靜音，多台一起播分不出來源）；正在播的那台
/// 不在畫面上要把聲音收掉（否則聽到上一台在說話）；聲音走<b>解碼行程</b>的 UDP 輸出，
/// 不另開 RTSP 連線（便宜攝影機的 session 數撐不住，會把畫面擠掉）。
/// </para>
/// </remarks>
public sealed class LiveAudioContractTests
{
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string Controller = "src/HeliVMS.App/Services/LiveAudioController.cs";
    private const string Output = "src/HeliVMS.App/Services/AudioOutput.cs";
    private const string WaveOut = "src/HeliVMS.App/Services/WaveOutPlayer.cs";
    private const string Session = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string Manager = "src/HeliVMS.App/Services/ChannelManager.cs";

    [Fact]
    public void 喇叭圖示在每格右下角且只出現在有開收音的攝影機()
    {
        var source = SourceContract.Read(Main);

        // 圖示是每格一顆，不是頂欄一顆：頂欄那顆說不出「哪一台」。
        Assert.Contains("_cellAudioIcons = new TextBlock[MaxCells]", source);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", source);
        Assert.DoesNotContain("HeaderAudioToggle", SourceContract.Read(Xaml));
        Assert.DoesNotContain("HeaderAudioToggle", source);

        // 只在「頻道管理有開收音」的攝影機上出現：按了才有意義。
        Assert.Contains("CanListen(channelId) != true", source);
    }

    [Fact]
    public void 點喇叭是獨佔切換且失敗會說明原因()
    {
        var source = SourceContract.Read(Main);
        var body = SourceContract.BodyOf(source, "OnCellAudioIconClicked");

        // 同一時間只有一台在播；點別台就把這台靜音（ToggleAsync 內含 StopAsync）。
        Assert.Contains("ToggleAsync(channelId)", body);
        Assert.Contains("UpdateAudioIcons()", body);

        // 失敗要有看得到的原因：沒有麥克風與沒有音效裝置是兩種不同的處置。
        Assert.Contains("no-audio-track", body);
        Assert.Contains("no-audio-device", body);

        // 獨佔必須由控制器保證，不能只靠視窗記得先停掉上一台：
        // 停止必須落在「同一台的早退」之後、開始播音之前——順序錯了就是兩台一起出聲。
        var toggleBody = SourceContract.BodyOf(SourceContract.Read(Controller), "ToggleAsync");
        var toggleCheck = toggleBody.IndexOf("if (_channelId == channelId)", StringComparison.Ordinal);
        var earlyReturn = toggleCheck >= 0 ? toggleBody.IndexOf("return false;", toggleCheck, StringComparison.Ordinal) : -1;
        var exclusiveStop = earlyReturn >= 0 ? toggleBody.IndexOf("await StopAsync()", earlyReturn, StringComparison.Ordinal) : -1;
        var start = toggleBody.IndexOf("_output.StartAsync", StringComparison.Ordinal);
        Assert.True(
            earlyReturn >= 0 && exclusiveStop > earlyReturn && start > exclusiveStop,
            "點別台之前必須先把正在播的那台靜音（獨佔收音）：缺少同一台的早退，或切台前沒有先停止。");
    }

    [Fact]
    public void 正在播的那台不在畫面上時會把聲音收掉()
    {
        var source = SourceContract.Read(Main);

        // 只在佈局／頁簽／頻道清單「落定之後」裁決：切佈局的途中格位會短暫清空，
        // 那時裁決會把聲音錯殺（切一次佈局斷一次聲音）。
        Assert.Contains("await EnforceAudioVisibilityAsync()", SourceContract.BodyOf(source, "ApplyWallAsync"));
        Assert.Contains("await EnforceAudioVisibilityAsync()", SourceContract.BodyOf(source, "OnLayoutChanged"));
        Assert.Contains("EnforceAudioVisibilityAsync()", SourceContract.BodyOf(source, "OnCellMappingChanged"));
        Assert.Contains("StopAudioAsync()", SourceContract.BodyOf(source, "DisconnectAllAsync"));

        var enforce = SourceContract.BodyOf(source, "EnforceAudioVisibilityAsync");
        Assert.Contains("_cellChannel[cell] == listening", enforce);
        Assert.Contains("await _audio.StopAsync()", enforce);
    }

    [Fact]
    public void 收音走解碼行程而不另開RTSP連線()
    {
        var session = SourceContract.Read(Session);

        // 接收器跟著工作階段生、跟著工作階段死；埠寫進兩條碼流的 ffmpeg 參數。
        Assert.Contains("new AudioStreamReceiver(", session);
        Assert.Contains("Client.AudioPort = _audioReceiver.Port", session);
        Assert.Contains("main.AudioPort = receiver.Port", session);
        Assert.Contains("_audioReceiver?.Dispose()", session);

        // 建立工作階段時就決定要不要收音（由頻道設定驅動）。
        Assert.Contains("listenEnabled: ch.ListenEnabled", SourceContract.Read(Manager));
        Assert.Contains("ch.ListenEnabled", SourceContract.Read(Manager));

        // 控制器只吃工作階段既有的接收器，不自己組 RTSP 位址。
        var controller = SourceContract.Read(Controller);
        Assert.Contains("_sessionFor(channelId)?.AudioReceiver", controller);
        Assert.DoesNotContain("RecordingUrl", controller);
    }

    [Fact]
    public void 沒有麥克風或沒有音訊裝置時不丟例外()
    {
        var controller = SourceContract.Read(Controller);
        var output = SourceContract.Read(Output);

        // 沒有麥克風（探測不到音訊軌）：控制器先擋，畫面才說得出「為什麼沒聲音」。
        Assert.Contains("SourceHasAudio == false", controller);
        Assert.Contains("no-audio-track", controller);

        // 沒有音訊裝置（遠端桌面）：探測失敗回 false，不丟例外；原因寫進日誌檔。
        Assert.Contains("WaveOutPlayer.ProbeSampleRate()", output);
        Assert.Contains("no-audio-device", output);
        Assert.Contains("helivms-audio.log", SourceContract.Read(WaveOut));
    }

    /// <summary>
    /// 打勾或取消「現場收音」要立即生效，不必重啟軟體。
    /// </summary>
    /// <remarks>
    /// 現場回報的修正：原本只在「工作階段建立時」把要不要收音寫進 ffmpeg 參數，
    /// 設定中心的勾改完之後監看牆毫無反應，操作員必須關掉整個程式再開——那個勾看起來就像沒存到。
    /// 三個環節缺一不可：頻道清單一刷新就對齊（在簽章比對之前，因為只改收音不會改簽章）、
    /// 收音開關<b>不進</b>頻道簽章（進了的話改一個勾會讓全部攝影機重連一次）、
    /// 以及只重啟有差的那一台（其他台的畫面與錄影完全不動）。
    /// </remarks>
    [Fact]
    public void 打勾或取消收音會立即生效而不必重啟()
    {
        var main = SourceContract.Read(Main);

        // 對齊必須發生在「簽章比對」之前：只改收音不會改簽章，晚於比對就會被早退掉。
        var reload = SourceContract.BodyOf(main, "ReloadChannelsIfChangedAsync");
        Assert.Contains("await SyncListenSettingsAsync()", reload);
        Assert.True(
            reload.IndexOf("await SyncListenSettingsAsync()", StringComparison.Ordinal)
            < reload.IndexOf("ChannelSignature(_channelList) == before", StringComparison.Ordinal),
            "收音的對齊必須在簽章比對之前；放後面會被『頻道本身沒變』的早退跳過。");

        var sync = SourceContract.BodyOf(main, "SyncListenSettingsAsync");
        Assert.Contains("IsListenEnabled == channel.ListenEnabled", sync);
        Assert.Contains("ApplyListenSettingAsync(channel.Id, channel.ListenEnabled)", sync);
        Assert.Contains("await _audio.StopAsync()", sync);

        // 只重啟有差的那一台，其他台不動。
        var manager = SourceContract.Read("src/HeliVMS.App/Services/ChannelManager.cs");
        Assert.Contains("ApplyListenSettingAsync(int channelId, bool enabled)", manager);
        Assert.Contains("session.SetListenEnabledAsync(enabled)", manager);

        var session = SourceContract.Read("src/HeliVMS.App/Services/ChannelSession.cs");
        Assert.Contains("RestartAsync()", SourceContract.BodyOf(session, "SetListenEnabledAsync"));

        // 收音開關不進頻道簽章：進了會讓「改一個勾」變成全部攝影機重連一次。
        Assert.DoesNotContain("ListenEnabled", SourceContract.BodyOf(main, "ChannelSignature"));
    }
}
