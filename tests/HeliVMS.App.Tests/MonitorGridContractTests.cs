namespace HeliVMS.App.Tests;

/// <summary>
/// 監看格線「比例最佳化」的接線契約。桌面端沒有 WPF 執行期測試。
/// </summary>
/// <remarks>
/// <para>
/// 純排版計算（列×欄挑選、維持每格比例、置中外框）由
/// <c>HeliVMS.Shared.Tests.MonitorGridTests</c> 以行為測試保護；
/// 這裡只確認主視窗真的把它接上，並在尺寸變動／重建格位時重算——
/// 這正是「計算對，但畫面沒用它」的假整合最容易斷掉的地方。
/// </para>
/// <para>
/// 另鎖定兩件事：監看影像改為填滿（<c>Stretch.UniformToFill</c>），
/// 以及 AI 疊加框用同一套比例（取較大縮放），否則 4:3 來源的框會貼在錯的位置。
/// </para>
/// </remarks>
public sealed class MonitorGridContractTests
{
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainWindowXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string Playback = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string PlaybackXaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    [Fact]
    public void 主視窗用比例感知排版決定列欄與置中外框()
    {
        var fit = SourceContract.BodyOf(SourceContract.Read(MainWindow), "ApplyGridGeometry");
        Assert.NotEqual(string.Empty, fit);
        Assert.Contains("MonitorGrid.Fit", fit);
        Assert.Contains("CellGrid.Columns", fit);
        Assert.Contains("CellGrid.Rows", fit);
    }

    [Fact]
    public void 監看區尺寸變動與重建格位時都會重算排版()
    {
        var code = SourceContract.Read(MainWindow);
        Assert.Contains("OnMonitorAreaSizeChanged", SourceContract.Read(MainWindowXaml));

        var resize = SourceContract.BodyOf(code, "OnMonitorAreaSizeChanged");
        Assert.NotEqual(string.Empty, resize);
        Assert.Contains("ApplyGridGeometry", resize);

        var rebuild = SourceContract.BodyOf(code, "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);
        Assert.Contains("ApplyGridGeometry", rebuild);
    }

    [Fact]
    public void 監看影像填滿格子且疊加框用同一套比例()
    {
        var code = SourceContract.Read(MainWindow);

        var rebuild = SourceContract.BodyOf(code, "RebuildCells");
        Assert.Contains("Stretch.UniformToFill", rebuild);

        var overlay = SourceContract.BodyOf(code, "DrawLiveOverlay");
        Assert.NotEqual(string.Empty, overlay);
        Assert.Contains("Math.Max(w / Math.Max(1, availW), h / Math.Max(1, availH))", overlay);
    }

    /// <summary>
    /// 監看格的事件綁定必須用每圈各自的區域變數，不能用 <c>for</c> 迴圈的 <c>i</c>。
    /// C# 的 <c>for</c> 變數是共用的，閉包會讓每一格都指到最後一格（<c>i == count</c>），
    /// 於是點任何一格放大都落到同一台攝影機——正是「放大永遠是 CH01」的回報。
    /// </summary>
    [Fact]
    public void 監看格左鍵綁定不得共用迴圈變數()
    {
        var rebuild = SourceContract.BodyOf(SourceContract.Read(MainWindow), "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);

        Assert.Contains("var cellIndex = i;", rebuild);

        // 左鍵的三個手勢（按下／移動／放開）都以 cellIndex 綁定；用到共用的 i 就會全部指到同一格。
        Assert.Contains("OnCellMouseDown(cellIndex", rebuild);
        Assert.Contains("OnCellMouseMove(cellIndex", rebuild);
        Assert.Contains("OnCellMouseUp(cellIndex", rebuild);
        Assert.DoesNotContain("OnCellMouseDown(i", rebuild);
        Assert.DoesNotContain("OnCellMouseUp(i", rebuild);
    }

    [Fact]
    public void 回放視窗用比例感知排版置中播放框()
    {
        Assert.Contains("OnPlayerAreaSizeChanged", SourceContract.Read(PlaybackXaml));

        var fit = SourceContract.BodyOf(SourceContract.Read(Playback), "ApplyPlayerFrameGeometry");
        Assert.NotEqual(string.Empty, fit);
        Assert.Contains("MonitorGrid.Fit", fit);
        Assert.Contains("PlayerFrame.Width", fit);
    }

    /// <summary>單分割時滾輪對攝影機畫面做數位縮放，且只縮放影像與 AI 疊加、不縮放文字。</summary>
    /// <remarks>
    /// 症狀：想數位放大單格畫面時完全沒反應。這條接線（<c>MouseWheel</c> → <c>OnCellMouseWheel</c>）
    /// 斷掉不會有任何錯誤，只能靠人眼發現。
    /// </remarks>
    [Fact]
    public void 單分割滾輪會數位縮放畫面()
    {
        var code = SourceContract.Read(MainWindow);

        // 每格都要接上滾輪；多分割由處理常式自行放行。
        var rebuild = SourceContract.BodyOf(code, "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);
        Assert.Contains("MouseWheel", rebuild);
        Assert.Contains("OnCellMouseWheel(cellIndex", rebuild);

        var wheel = SourceContract.BodyOf(code, "OnCellMouseWheel");
        Assert.NotEqual(string.Empty, wheel);
        // 只縮放單分割。
        Assert.Contains("CurrentCellCount() != 1", wheel);
        // 以游標為錨點，並限制在 1x～8x。
        Assert.Contains("Math.Clamp", wheel);
        Assert.Contains("GetPosition", wheel);

        var apply = SourceContract.BodyOf(code, "ApplyCellZoom");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("ScaleTransform", apply);
        Assert.Contains("TranslateTransform", apply);

        // 重建佈局要把縮放歸零，否則切回單格會沿用上一次的放大。
        Assert.Contains("_cellZoom = 1.0", rebuild);
    }

    /// <summary>數位放大後按住左鍵拖曳可平移畫面，且不誤觸攝影機換位。</summary>
    /// <remarks>
    /// 症狀：放大後想看畫面邊角卻拖不動；或拖曳變成把攝影機搬到別格。
    /// 平移只加在縮放圖層的 <c>TranslateTransform</c> 上，來源格不會進入拖曳換位狀態。
    /// </remarks>
    [Fact]
    public void 放大後按住左鍵會平移畫面()
    {
        var code = SourceContract.Read(MainWindow);

        var pan = SourceContract.BodyOf(code, "CanPanCell");
        Assert.NotEqual(string.Empty, pan);
        Assert.Contains("CurrentCellCount() == 1", pan);
        Assert.Contains("_cellZoom > 1.0001", pan);

        var down = SourceContract.BodyOf(code, "OnCellMouseDown");
        Assert.NotEqual(string.Empty, down);
        Assert.Contains("CanPanCell", down);
        Assert.Contains("_panning = true", down);

        var move = SourceContract.BodyOf(code, "OnCellMouseMove");
        Assert.NotEqual(string.Empty, move);
        Assert.Contains("_panning", move);
        Assert.Contains("ApplyCellZoom", move);

        var up = SourceContract.BodyOf(code, "OnCellMouseUp");
        Assert.NotEqual(string.Empty, up);
        Assert.Contains("_panning", up);
        Assert.Contains("ReleaseMouseCapture", up);

        // 平移量會被夾在畫面仍填滿格子的範圍內。
        var apply = SourceContract.BodyOf(code, "ApplyCellZoom");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("Math.Clamp", apply);
    }

    /// <summary>跳台（自動輪播）：每 5 秒依目前分割跳下一組。</summary>
    /// <remarks>
    /// 症狀：按了「跳台」卻不動，或只在單分割會跳、切到四分割後每一格各跳一台而不同步。
    /// 步進量必須等於目前格數（單分割=1、四分割=4），且週期固定 5 秒；
    /// 這兩者任一寫死或漏接，畫面就只會「看起來有跳」卻不是下一組。
    /// </remarks>
    [Fact]
    public void 跳台功能每五秒依目前分割跳下一組()
    {
        var code = SourceContract.Read(MainWindow);
        var xaml = SourceContract.Read(MainWindowXaml);

        // 標頭要有跳台切換鈕並接上處理常式。
        Assert.Contains("x:Name=\"WallSequenceToggle\"", xaml);
        Assert.Contains("Click=\"OnWallSequenceToggled\"", xaml);

        // 週期固定 5 秒。
        Assert.Contains("TimeSpan.FromSeconds(5)", code);

        var toggle = SourceContract.BodyOf(code, "OnWallSequenceToggled");
        Assert.NotEqual(string.Empty, toggle);
        Assert.Contains("_wallSequenceTimer.Start()", toggle);
        Assert.Contains("_wallSequenceTimer.Stop()", toggle);

        var advance = SourceContract.BodyOf(code, "AdvanceWallSequence");
        Assert.NotEqual(string.Empty, advance);
        // 步進量 = 目前分割格數：單分割跳一台、四分割跳一組四台。
        Assert.Contains("CurrentCellCount()", advance);
        Assert.Contains("_wallSequenceIndex", advance);
        Assert.Contains("ReconcileAsync(wall, _wallSequenceIndex, cellCount)", advance);
    }
}
