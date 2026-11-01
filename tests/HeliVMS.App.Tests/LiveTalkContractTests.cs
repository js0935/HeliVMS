namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆「對講」的接線契約：把主機麥克風的聲音送到攝影機的喇叭。
/// </summary>
/// <remarks>
/// 與「聽現場聲音」（<see cref="LiveAudioContractTests"/>）是兩個方向、兩顆圖示，並排在
/// 畫面右下角。三個容易漏掉、而且漏掉時「畫面完全正常」的點：麥克風圖示只在有開對講的
/// 攝影機上出現；同一時間只有一台在對講（獨佔）；正在對講的那台離開畫面要把行程收掉，
/// 否則操作員的聲音會繼續送往一個沒有人看著的攝影機。
/// </remarks>
public sealed class LiveTalkContractTests
{
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Controller = "src/HeliVMS.App/Services/LiveTalkController.cs";
    private const string TalkWindow = "src/HeliVMS.App/TalkWindow.xaml.cs";
    private const string TalkXaml = "src/HeliVMS.App/TalkWindow.xaml";
    private const string Builder = "src/HeliVMS.Recording/Talk/TalkCommandBuilder.cs";

    [Fact]
    public void 麥克風圖示與喇叭並排且只在有開對講的攝影機上()
    {
        var source = SourceContract.Read(Main);

        // 兩顆圖示放進同一個右下角面板（麥克風 = U+E720，喇叭 = U+E767）。
        Assert.Contains("_cellTalkIcons = new TextBlock[MaxCells]", source);
        Assert.Contains("\\uE720", source);
        Assert.Contains("audioStrip.Children.Add(audioIcon)", source);
        Assert.Contains("audioStrip.Children.Add(talkIcon)", source);

        // 只在「頻道管理有開對講」的攝影機上出現。
        var icons = SourceContract.BodyOf(source, "UpdateTalkIcons");
        Assert.Contains("c.TalkEnabled", icons);
        Assert.Contains("Visibility.Collapsed", icons);
    }

    [Fact]
    public void 點麥克風是獨佔對講且失敗會說明原因()
    {
        var source = SourceContract.Read(Main);
        var body = SourceContract.BodyOf(source, "OnCellTalkIconClicked");

        Assert.Contains("ToggleAsync(channelId)", body);
        Assert.Contains("UpdateAudioIcons()", body);
        Assert.Contains("talk-not-enabled", body);

        // 獨佔由控制器保證：停止必須落在「同一台的早退」之後、啟動之前——順序錯了就是兩台一起收到聲音。
        var toggle = SourceContract.BodyOf(SourceContract.Read(Controller), "ToggleAsync");
        var check = toggle.IndexOf("if (_channelId == channelId)", StringComparison.Ordinal);
        var earlyReturn = check >= 0 ? toggle.IndexOf("return false;", check, StringComparison.Ordinal) : -1;
        var exclusiveStop = earlyReturn >= 0 ? toggle.IndexOf("await StopAsync()", earlyReturn, StringComparison.Ordinal) : -1;
        var start = toggle.IndexOf("_talk.Start(request)", StringComparison.Ordinal);
        Assert.True(
            earlyReturn >= 0 && exclusiveStop > earlyReturn && start > exclusiveStop,
            "點別台之前必須先把正在對講的那台停止（獨佔對講）。");
    }

    [Fact]
    public void 麥克風裝置用對講視窗選的那一台()
    {
        var source = SourceContract.Read(Main);
        var build = SourceContract.BodyOf(source, "BuildTalkRequest");

        // 頻道沒開對講、沒有碼流、沒選麥克風都不能送：按下去才發現不能用比不亮更糟。
        Assert.Contains("!channel.TalkEnabled", build);
        Assert.Contains("TalkSettings.MicrophoneKey", build);
        Assert.Contains("new TalkRequest(channelId, url", build);

        // 對講視窗把選擇存進同一個設定鍵（兩邊各記一份就會「這裡測得好好的、牆上按了沒聲音」）。
        var window = SourceContract.Read(TalkWindow);
        Assert.Contains("_settings.Set(TalkSettings.MicrophoneKey", window);
        Assert.Contains("MicrophoneCatalog.List()", window);
    }

    [Fact]
    public void 對講的聲音來源是主機麥克風而不是檔案()
    {
        // 牆上的麥克風按鈕走即時喊話；檔案模式仍在對講視窗（廣播稿用）。
        Assert.Contains("BuildLiveMicrophone", SourceContract.Read(Builder));
        Assert.Contains("MicrophoneDevice", SourceContract.Read("src/HeliVMS.Recording/Talk/TalkRequest.cs"));

        // dshow 不帶 -re：即時時鐘再套 -re 會讓聲音延遲一路積上去。
        var mic = SourceContract.BodyOf(SourceContract.Read(Builder), "BuildLiveMicrophone");
        Assert.DoesNotContain("\"-re\"", mic);
        Assert.Contains("dshow", mic);
    }

    [Fact]
    public void 對講離開畫面或停止監看時會停止()
    {
        var source = SourceContract.Read(Main);

        // 裁決與收音同一個落點（佈局／頁簽／頻道清單落定之後）。
        Assert.Contains("_talk.StopAsync()", SourceContract.BodyOf(source, "EnforceAudioVisibilityAsync"));
        Assert.Contains("StopAudioAsync()", SourceContract.BodyOf(source, "DisconnectAllAsync"));

        // 關窗收掉對講的 ffmpeg 子行程，避免孤兒行程繼續佔著回傳通道。
        var close = SourceContract.BodyOf(source, "OnWindowClosed");
        Assert.Contains("talk?.DisposeAsync()", close);
    }

    [Fact]
    public void 對講視窗可以選主機麥克風()
    {
        // 來源切換與裝置清單都在對講視窗（檔案模式保留給廣播稿）。
        Assert.Contains("x:Name=\"TalkSourceCombo\"", SourceContract.Read(TalkXaml));
        Assert.Contains("x:Name=\"TalkMicCombo\"", SourceContract.Read(TalkXaml));
        Assert.Contains("UseMicrophone", SourceContract.Read(TalkWindow));
        var start = SourceContract.BodyOf(SourceContract.Read(TalkWindow), "OnStartClicked");
        Assert.Contains("new TalkRequest(", start);
        Assert.Contains("microphone", start);
        Assert.Contains("Talk.NoMicrophone", start);
    }
}
