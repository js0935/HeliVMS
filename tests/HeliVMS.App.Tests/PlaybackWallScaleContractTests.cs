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

    /// <summary>日曆整月沒有藍色時，要能分辨「沒錄過」與「錄了但已被汰除」，並把後者說出來。</summary>
    /// <remarks>
    /// 現場（D: 滿檔、保留策略汰除整個歷史檔案）的日曆會從「整個月都是藍的」變成「一格都沒有」，
    /// 沒有任何解釋——操作員的第一反應是系統壞了或錄影沒在跑。索引裡的 <c>missing</c> 段數
    /// 就是「曾經錄過、現在沒了」的證據（實測：攝影機 01 在 11/04–05 有 400 多段標記為遺失）。
    /// 提示的門檻是「有 missing 段」而不是「磁碟壓力等級」：新頻道（從沒錄過）不該被誤導。
    /// </remarks>
    [Fact]
    public void 日曆整月沒有藍色時會說明錄影已被汰除()
    {
        var marks = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyRecordingDayMarks");
        Assert.Contains("ExplainEmptyMonth(", marks);

        var explain = SourceContract.BodyOf(SourceContract.Read(Code), "ExplainEmptyMonth");
        Assert.Contains("CountByStatusInRange(", explain);
        Assert.Contains("Playback.RecordingsEvicted", explain);
        // 門檻是「索引裡有 missing 段」：新頻道（沒錄過）不能被誤導成「被汰除」。
        Assert.Contains("missing > 0", explain);
    }

    [Fact]
    public void 回放提示與日曆說明的文案三語都在()
    {
        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");

        var lines = i18n.Split('\n')
            .Where(line => line.Contains("\"Playback.RecordingsEvicted\"", StringComparison.Ordinal))
            .ToList();

        // 三語各一行；少一語，該語系的操作員會看到空白按鈕或提示。
        Assert.True(lines.Count == 3, $"Playback.RecordingsEvicted 應該有三語，實際 {lines.Count}");
        Assert.All(lines, line => Assert.Contains("{0}", line));
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
