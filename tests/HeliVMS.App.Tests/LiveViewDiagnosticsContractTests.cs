namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆的即時診斷接線：操作員說「畫面會延遲」時，必須有東西能指出延遲在哪一段。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，所以這裡掃原始碼；格式與純邏輯在
/// <c>HeliVMS.Shared.LiveViewDiagnosticLine</c>（行為測試在 Shared.Tests）。
/// </para>
/// </summary>
public sealed class LiveViewDiagnosticsContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SessionCode = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string MediaCode = "src/HeliVMS.Media/RtspClient.cs";

    [Fact]
    public void 每秒計時器每五秒寫一行即時診斷()
    {
        var main = SourceContract.Read(MainCode);

        // 掛在同一條 1 秒計時器上（不另外開執行緒／計時器）。
        var tick = SourceContract.BodyOf(main, "UpdateCellStatus");
        Assert.Contains("WriteLiveDiagnostics(", tick);

        var write = SourceContract.BodyOf(main, "WriteLiveDiagnostics");
        Assert.Contains("helivms-live.log", write);
        Assert.Contains("BoundedFileLog.Append(", write);
        // 有上限：長跑（或反覆重連）不能把 %TEMP% 灌爆。
        Assert.Contains("512 * 1024", write);
        // 沒有畫面時不必寫（診斷內容來自顯示中的格子）。
        Assert.Contains("_monitoring", write);
    }

    [Fact]
    public void 診斷行含本機管線落後量與讀取計數()
    {
        var write = SourceContract.BodyOf(SourceContract.Read(MainCode), "WriteLiveDiagnostics");

        // 落後量（可以修的那一段）與畫面年齡（UI 佇列）要分開，來源是 session 的解碼計數。
        Assert.Contains("StreamBacklog", write);
        Assert.Contains("GetDecodeSnapshot(", write);

        // 計數轉成每秒率：累計值本身看不出「現在掉幾格」（換算規則在 Shared 的
        // LiveFrameRates／LiveViewCounterTracker，行為測試在那裡；這裡確認視窗真的走那條路，
        // 而且「修剪」發生在取樣之後——先修剪的話每一輪都像第一次取樣，日誌永遠是 read=0/s）。
        Assert.Contains("_liveLogCounters.Record(", write);
        Assert.True(
            write.IndexOf("_liveLogCounters.Record(", StringComparison.Ordinal)
                < write.IndexOf("_liveLogCounters.KeepOnly(", StringComparison.Ordinal),
            "修剪必須在取樣之後，否則每一輪都像第一次取樣（read 永遠是 0）");

        var stats = SourceContract.BodyOf(SourceContract.Read(SessionCode), "GetDecodeSnapshot");
        Assert.Contains("FramesRead", stats);
        Assert.Contains("FramesEmitted", stats);
        Assert.Contains("FramesDropped", stats);
    }

    /// <summary>
    /// 診斷行要寫出「這一輪實際生效」的硬體解碼方式，不是設定值。
    /// </summary>
    /// <remarks>
    /// <c>auto</c> 會被解析成明確的方法名、原生尺寸時刻意不啟用、失敗冷卻期內也會退回軟解。
    /// 只寫設定值的日誌會讓現場「明明設了硬解卻還是吃滿 CPU」永遠查不出來，
    /// 而這個欄位就在既有的每秒計時器裡，沒有額外成本。
    /// </remarks>
    [Fact]
    public void 診斷行會寫出實際生效的硬體解碼方式()
    {
        var write = SourceContract.BodyOf(SourceContract.Read(MainCode), "WriteLiveDiagnostics");
        Assert.Contains("counters.HwAccel", write);

        var stats = SourceContract.BodyOf(SourceContract.Read(SessionCode), "GetDecodeSnapshot");
        Assert.Contains("HwAccelInUse", stats);

        var client = SourceContract.Read(MediaCode);
        Assert.Contains("HwAccelInUse", client);
    }

    /// <summary>
    /// 狀態列顯示的延遲必須是「本機造成的」那一段；量不到就退回畫面年齡，不可報 0。
    /// </summary>
    /// <remarks>
    /// 影像離現場多遠（攝影機 ＋ 網路 ＋ 解碼緩衝）在本機量不到——ffmpeg 會把輸出時間歸零。
    /// 報一個編出來的絕對延遲比不報更糟：操作員會拿它去追錯的方向。
    /// </remarks>
    [Fact]
    public void 狀態列的延遲優先用本機落後量並可退回畫面年齡()
    {
        var status = SourceContract.BodyOf(SourceContract.Read(MainCode), "RenderCellStatusText");

        Assert.Contains("StreamBacklog", status);
        Assert.Contains("meter.Age(", status);
        Assert.Contains("Main.CellLatency", status);
    }

    /// <summary>
    /// 狀態文字只在每秒的計時器更新，不在每一張影格上重排。
    /// </summary>
    [Fact]
    public void 狀態文字不隨每一張影格重排()
    {
        var frame = SourceContract.BodyOf(SourceContract.Read(MainCode), "RenderCellFrame");

        Assert.DoesNotContain("RenderCellStatusText(", frame);
        // 疊加框仍逐格更新（它有自己的閘門），但統計文字沒有。
        Assert.Contains("DrawLiveOverlay(", frame);

        var metrics = SourceContract.BodyOf(SourceContract.Read(MainCode), "UpdateCellMetrics");

        // 每秒那條路徑走 ApplyCellState（狀態的唯一投影點），不是直接呼叫 RenderCellStatusText：
        // 後者只認得「串流中」，會把「錄影失敗 N 次」蓋成正常文字卻留下紅色色條。
        Assert.Contains("ApplyCellState(", metrics);
        Assert.DoesNotContain("RenderCellStatusText(", metrics);
    }

    /// <summary>
    /// 狀態列的解析度必須報<b>來源</b>尺寸，真的縮小解碼時才附註解碼尺寸。
    /// </summary>
    /// <remarks>
    /// 現場回報「子碼流顯示 480x270，應該是 640x480」。量到的實情是：次碼流來源是
    /// 640x360／640x480 兩族，而 <c>LiveWallBudget.For(10)</c> 把每一格縮到寬 480 解碼。
    /// 舊的狀態列直接寫解碼尺寸，操作員於是拿一個「我們自己縮出來的數字」去對攝影機設定，
    /// 當然對不上——而畫面完全正常、日誌乾淨。
    /// </remarks>
    [Fact]
    public void 狀態文字報的是來源解析度而不是只報解碼尺寸()
    {
        var status = SourceContract.BodyOf(SourceContract.Read(MainCode), "RenderCellStatusText");

        Assert.Contains("LiveResolutionReport.Format(", status);
        Assert.Contains("snapshot.SourceWidth", status);
        // 文案走 i18n，不硬寫「解碼」。
        Assert.Contains("Main.CellResolutionScaled", status);
    }

    /// <summary>
    /// 診斷行也要寫出來源尺寸：它是事後唯一能分辨「攝影機本身是 640x360」與
    /// 「我們縮成 480x270 解碼」的欄位，少了它就得對每支攝影機重跑 ffprobe。
    /// </summary>
    [Fact]
    public void 診斷行會寫出探測到的來源尺寸()
    {
        var write = SourceContract.BodyOf(SourceContract.Read(MainCode), "WriteLiveDiagnostics");
        Assert.Contains("counters.SourceWidth", write);
        Assert.Contains("counters.SourceHeight", write);

        var stats = SourceContract.BodyOf(SourceContract.Read(SessionCode), "GetDecodeSnapshot");
        Assert.Contains("SourceSize", stats);

        // 來源尺寸只有解碼端知道（探測到的），不可由顯示尺寸回推。
        Assert.Contains("SourceSize", SourceContract.Read(MediaCode));
    }

    [Fact]
    public void 進度回報不會擠掉真正的診斷訊息()
    {        var client = SourceContract.Read(MediaCode);

        // ffmpeg 的 -progress 每秒寫十幾行 key=value 到 stderr；那些行若進入尾端 20 行，
        // 操作員看到的「連線失敗原因」就變成 out_time_us=…。
        var drain = SourceContract.BodyOf(client, "IsProgressLine");
        Assert.Contains("ProgressKeys", drain);
        Assert.Contains("out_time_us=", client);
        Assert.Contains("ParseProgress(", client);
    }
}
