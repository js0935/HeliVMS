namespace HeliVMS.App.Tests;

/// <summary>
/// 多路回放（最多 64 路）的接線：格數、每一格的解碼預算、硬體解碼。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端沒有 WPF 執行期測試，所以這裡掃原始碼；預算表本身是行為測試
/// （<c>HeliVMS.Shared.Tests.PlaybackWallBudgetTests</c>），那張表錯了不會有任何錯誤訊息，
/// 只會表現成「格數多時畫面很頓」。
/// </para>
/// <para>
/// 最容易漏的是「主畫面那一格」：主畫面與次畫面各有一條建立解碼的路徑，
/// 只改次畫面的話，64 分割裡的第一格仍然用原生 1080p——它會是畫面最頓的那一格。
/// </para>
/// </remarks>
public sealed class PlaybackWallScaleContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    [Fact]
    public void 分割選單提供到六十四路()
    {
        var xaml = SourceContract.Read(Xaml);

        foreach (var cells in new[] { "1", "2", "4", "9", "16", "25", "36", "64" })
        {
            Assert.Contains($"<ComboBoxItem Content=\"{cells}\"/>", xaml);
        }

        // 格數與預算表同一份來源：兩份清單遲早會漂移，而症狀是「選了 64 卻是 16 的預算」。
        var split = SourceContract.BodyOf(SourceContract.Read(Code), "SplitCount");
        Assert.Contains("PlaybackWallBudget.SplitOptions", split);
    }

    [Fact]
    public void 主畫面與次畫面都套用同一份解碼預算()
    {
        var code = SourceContract.Read(Code);

        // 單一建立點：兩條路徑（PlayFrom＝主畫面、PlayPane＝次畫面）都走它。
        Assert.Contains("CreateSession(", SourceContract.BodyOf(code, "PlayFrom"));
        Assert.Contains("CreateSession(", SourceContract.BodyOf(code, "PlayPane"));

        var create = SourceContract.BodyOf(code, "CreateSession");
        Assert.Contains("PlaybackWallBudget.For(", create);
        Assert.Contains("DisplayWidth = budget.DisplayWidth", create);
        Assert.Contains("MaxFramesPerSecond = budget.MaxFramesPerSecond", create);
        Assert.Contains("DecodeThreads = budget.DecodeThreads", create);
        Assert.Contains("HwAccel = ", create);

        // 格數＝主畫面 1 格＋次畫面數，否則預算會少算一格。
        Assert.Contains("1 + _panes.Count", create);
    }

    /// <summary>
    /// 總覽牆（只解關鍵影格）的對時容差必須放寬：否則每一格每一輪都被判定「落後」而重開解碼。
    /// </summary>
    [Fact]
    public void 總覽牆用放寬的對時容差()
    {
        var code = SourceContract.Read(Code);

        var sync = SourceContract.BodyOf(code, "SyncPanes");
        Assert.Contains("SyncToleranceSeconds()", sync);

        var tolerance = SourceContract.BodyOf(code, "SyncToleranceSeconds");
        Assert.Contains("PlaybackWallBudget.For(", tolerance);
        Assert.Contains("KeyframesOnly", tolerance);
        Assert.Contains("OverviewDriftToleranceSeconds", tolerance);
    }

    /// <summary>
    /// 次畫面的 OSD 文字不可隨每一張影格重排：64 格時那是每秒上千次的白工。
    /// </summary>
    [Fact]
    public void 次畫面OSD文字有更新間隔()
    {
        var frame = SourceContract.BodyOf(SourceContract.Read(Code), "OnPaneFrame");

        Assert.Contains("OsdText(", frame);
        Assert.Contains("PaneOsdInterval", frame);
        Assert.Contains("pane.LastOsdAtUtc", frame);
    }

    /// <summary>
    /// 每 5 秒一行回放取樣：現場說「不順」時，這是唯一能分辨四種成因的證據。
    /// </summary>
    [Fact]
    public void 同步計時器每五秒寫一行回放診斷()
    {
        var code = SourceContract.Read(Code);

        // 掛在既有的 150ms 同步計時器上（不另外開計時器）：33 拍 ≈ 5 秒。
        Assert.Contains("WritePaneDiagnostics()", SourceContract.BodyOf(code, "SyncPanes"));

        var write = SourceContract.BodyOf(code, "WritePaneDiagnostics");
        Assert.Contains("PlaybackPaneDiagnosticLine.Format(", write);
        Assert.Contains("helivms-playback.log", write);
        Assert.Contains("BoundedFileLog.Append(", write);
        Assert.Contains("256 * 1024", write);
        // 沒有次畫面時不必寫（那時候牆上只有主畫面）。
        Assert.Contains("_panes.Count == 0", write);
    }

    [Fact]
    public void 硬體解碼沿用即時監看的設定與探測()
    {
        var code = SourceContract.Read(Code);

        // 與即時監看同一把尺：同一個設定鍵、同一個正規化、同一個能力探測。
        Assert.Contains("decode.hwaccel", code);
        Assert.Contains("RtspClient.NormalizeHwAccel(", code);
        Assert.Contains("FfmpegHwAccel.ShouldUse(", code);
        Assert.Contains("FfmpegHwAccel.ProbeCached(", code);
    }

    [Fact]
    public void 牆容量文字寫出實際可播的路數()
    {
        var geometry = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyPlayerFrameGeometry");

        // 「選了 16 卻只有 4 台」要一眼看得出來差在哪，而不是猜是不是壞了。
        Assert.Contains("PlaybackWallCapacity.Effective(", geometry);
        Assert.Contains("Playback.WallCapacity", geometry);
    }
}
