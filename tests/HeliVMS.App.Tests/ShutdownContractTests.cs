namespace HeliVMS.App.Tests;

/// <summary>
/// 關閉程式時的收尾契約。
///
/// 這三條守的是同一個現場症狀：離開程式之後，錄影資料夾裡躺著
/// <c>seg-HHmmssfff.mp4.tmp</c>，回放清單裡卻沒有那一段；工作管理員裡另外留著一排
/// <c>FFPROBE.exe</c>。原因是關閉流程根本沒跑完——<c>OnWindowClosed</c> 曾是
/// <c>async void</c>，WPF 在最後一個視窗關閉後就開始 Shutdown，<c>await</c> 之後的續體
/// 再也排不回 Dispatcher，於是 <c>StopAllAsync()</c> 被無聲丟掉，最後由
/// <c>App.OnExit</c> 硬砍子行程。硬砍的 ffmpeg 留下 <c>.tmp</c>，索引寫的卻是正式檔名，
/// 對帳因此判成 missing，十分鐘後保留清理再把 <c>.tmp</c> 刪掉——那段影片永久消失。
///
/// 桌面專案沒有 WPF 執行期測試環境（需要 Application／Dispatcher／index.db），
/// 所以這裡沿用本專案既有的原始碼契約工具釘住形狀；行為面的收尾（優雅停止、並行、
/// 逾時兜底）由 <c>HeliVMS.Recording.Tests</c> 以假行程實際跑起來驗證。
/// </summary>
public sealed class ShutdownContractTests
{
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string App = "src/HeliVMS.App/App.xaml.cs";

    /// <summary>
    /// 關閉主視窗的收尾不能是 <c>async void</c>，而且必須在行程結束前真的有等錄影收檔。
    /// </summary>
    [Fact]
    public void 關閉主視窗會等錄影收尾而不是丟掉非同步工作()
    {
        var code = SourceContract.Read(MainWindow);

        Assert.DoesNotContain("async void OnWindowClosed", code);
        Assert.Contains("void OnWindowClosed", code);

        var body = SourceContract.BodyOf(code, "OnWindowClosed");
        Assert.False(string.IsNullOrEmpty(body), "找不到 OnWindowClosed 的方法本體");

        // 錄影要收檔（不是直接 Kill），且走「有上限地等」的包裝，關機才不會被一台卡住的
        // 攝影機或慢磁碟拖到使用者直接砍行程。
        Assert.Contains("scheduler.StopAllAsync", body);
        Assert.Contains("RunShutdownStep", body);
        Assert.Contains("StopRecordingTimeout", body);

        // 監看的解碼 ffmpeg 也要在關窗前收掉；走管理員的版本，不是會重畫格子的
        // MainWindow.DisconnectAllAsync。
        Assert.Contains("manager.DisconnectAllAsync", body);

        // 釋放連線與關資料庫同樣要走「有上限地等」的包裝：它們內部都要等 ffmpeg 收尾，
        // 在 UI 執行緒上同步等就是死結（現場症狀：視窗關了、行程還在，WAL 沒 checkpoint）。
        Assert.Contains("manager.DisposeAsync", body);
        Assert.DoesNotContain("_manager?.Dispose()", body);
        Assert.DoesNotContain("_store?.Dispose()", body);
    }

    /// <summary>
    /// 關機要有看門狗：超過預算就清掉子行程並結束行程。
    ///
    /// 收尾的每一步都有各自的期限，但只要有一段沒被包住（新加的收尾工作、第三方元件裡
    /// 卡住的建構子），視窗關掉之後行程就會一直留在工作管理員裡，還佔著 index.db 與
    /// 攝影機連線。看門狗是唯一能保證「關掉就是關掉」的東西。
    /// </summary>
    [Fact]
    public void 關機有看門狗兜底並一定會結束行程()
    {
        var code = SourceContract.Read(MainWindow);
        var body = SourceContract.BodyOf(code, "OnWindowClosed");

        Assert.Contains("ShutdownWatchdog", body);

        var forceExit = SourceContract.BodyOf(code, "ForceExit");
        Assert.False(string.IsNullOrEmpty(forceExit), "找不到 ForceExit 的方法本體");
        Assert.Contains("Environment.Exit", forceExit);
        Assert.Contains("KillOrphanChildren", forceExit);
    }

    /// <summary>
    /// 啟動與結束都要清掉 <c>ffmpeg</c> 與 <c>ffprobe</c> 孤兒行程。
    ///
    /// 只清 <c>ffmpeg</c> 的後果：健康檢查與完整性抽查會短暫啟動 ffprobe，程式關掉之後
    /// 它仍留在工作管理員裡（使用者回報的正是「一排 FFPROBE.exe」）。
    /// </summary>
    [Fact]
    public void 孤兒行程清掃會同時收掉ffmpeg與ffprobe()
    {
        var code = SourceContract.Read(App);

        Assert.Contains("KillOrphanChildren();", SourceContract.BodyOf(code, "OnStartup"));
        Assert.Contains("KillOrphanChildren();", SourceContract.BodyOf(code, "OnExit"));
        Assert.Contains("[\"ffmpeg\", \"ffprobe\"]", code);
    }

    /// <summary>
    /// 關機收尾的失敗或逾時必須留下紀錄，不能無聲無息。
    ///
    /// 「關機沒收乾淨」正是這次要修的症狀；若逾時只被吞掉，下一次又只能靠操作員發現
    /// 資料夾裡的 <c>.tmp</c>，等於回到原點。
    /// </summary>
    [Fact]
    public void 關機收尾逾時會寫進日誌()
    {
        var code = SourceContract.Read(MainWindow);
        var body = SourceContract.BodyOf(code, "RunShutdownStep");

        Assert.False(string.IsNullOrEmpty(body), "找不到 RunShutdownStep 的方法本體");
        Assert.Contains("LogShutdown", body);
        Assert.Contains("helivms-shutdown.log", code);
    }
}
