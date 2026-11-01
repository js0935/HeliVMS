namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆「聽現場聲音」在桌面端的接線契約。
/// </summary>
/// <remarks>
/// 缺口：畫面解碼刻意帶了 <c>-an</c>，所以攝影機的麥克風從來沒有被聽過；ONVIF 對講與
/// 現場收音是 NVR 最基本的期待。這條契約釘住三件容易漏掉、而且漏掉時「畫面完全正常」
/// 的事：只在單分割播音（多分割時聽到聲音卻不知道來自哪一格）、來源是主碼流
/// （次碼流常沒有音訊軌）、以及換台／換分割／停止監看／關窗都要把聲音收回
/// （否則會聽到上一台攝影機在說話）。
/// </remarks>
public sealed class LiveAudioContractTests
{
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string Controller = "src/HeliVMS.App/Services/LiveAudioController.cs";
    private const string Player = "src/HeliVMS.App/Services/AudioPlayer.cs";
    private const string WaveOut = "src/HeliVMS.App/Services/WaveOutPlayer.cs";
    private const string Pull = "src/HeliVMS.Media/AudioPullClient.cs";

    [Fact]
    public void 頂欄有現場收音開關且只在單分割播音()
    {
        Assert.Contains("x:Name=\"HeaderAudioToggle\"", SourceContract.Read(Xaml));
        Assert.Contains("Click=\"OnAudioToggled\"", SourceContract.Read(Xaml));

        var body = SourceContract.BodyOf(SourceContract.Read(Main), "OnAudioToggled");

        // 多分割時有 4／9／16 台同時在畫面上，播哪一台都是猜：「有聲音但不知道哪一格」
        // 比不播更糟，所以這裡必須擋住。
        Assert.Contains("CurrentCellCount() != 1", body);
        Assert.Contains("_cellChannel[0]", body);
    }

    [Fact]
    public void 聲音來源固定是主碼流()
    {
        var source = SourceContract.Read(Main);

        // RecordingUrl 是主碼流（次碼流是 Client，給多分割省頻寬用，通常沒有音訊軌）。
        Assert.Contains("_manager?.ChannelSessionFor(id)?.RecordingUrl", source);
        Assert.Contains("new LiveAudioController(", source);
        Assert.Contains("live: true", SourceContract.Read(Controller));
    }

    [Fact]
    public void 換台與換分割都會讓聲音跟著走()
    {
        var source = SourceContract.Read(Main);

        Assert.Contains("SyncAudioAsync()", SourceContract.BodyOf(source, "OnCellMappingChanged"));
        Assert.Contains("await SyncAudioAsync()", SourceContract.BodyOf(source, "OnLayoutChanged"));
        Assert.Contains("await SyncAudioAsync()", SourceContract.BodyOf(source, "ApplyWallAsync"));
    }

    [Fact]
    public void 停止監看與關窗都會停止播音()
    {
        var source = SourceContract.Read(Main);

        Assert.Contains("StopAudioAsync()", SourceContract.BodyOf(source, "DisconnectAllAsync"));

        // 關窗時 _audio 先換出來、再由控制器自己收：收尾跑在執行緒池上，
        // 去改 HeaderAudioToggle（WPF 控制項）會變成跨執行緒存取。
        var close = SourceContract.BodyOf(source, "OnWindowClosed");
        Assert.Contains("var audio = _audio;", close);
        Assert.Contains("audio?.DisposeAsync()", close);
    }

    [Fact]
    public void 拉音訊在Media而播放裝置留在App()
    {
        // Media 要在沒有音效卡的 CI 上跑，不能背 Windows API；App 才碰 winmm。
        Assert.DoesNotContain("winmm", SourceContract.Read(Pull));
        Assert.DoesNotContain("DllImport", SourceContract.Read(Pull));
        Assert.Contains("winmm.dll", SourceContract.Read(WaveOut));

        // 緩衝佇列本身也留在 Media：它是純資料處理，才測得到「滿了丟最舊」。
        Assert.Contains(
            "public sealed class PcmChunkQueue",
            SourceContract.Read("src/HeliVMS.Media/PcmChunkQueue.cs"));
    }

    [Fact]
    public void 沒有音訊裝置時不丟例外只回報()
    {
        var player = SourceContract.Read(Player);

        Assert.Contains("WaveOutPlayer.ProbeSampleRate()", player);
        Assert.Contains("LastError = \"no-audio-device\"", player);
    }
}
