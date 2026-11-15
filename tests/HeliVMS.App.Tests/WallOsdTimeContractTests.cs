namespace HeliVMS.App.Tests;

/// <summary>
/// 每格「時間 OSD」（QC_Live 時間疊字對齊）的接線契約。桌面端沒有 WPF 執行期測試：
/// 模式/字級/色碼的整備與格式由 <c>HeliVMS.Shared.Tests.WallOsdSettingsTests</c> 行為測試保護，
/// 這裡確認主視窗真的把元素建在縮放層之外、每秒刷新、開關真的持久化——
/// 「預設關閉」也是契約的一部分：升級後的安裝畫面必須與之前完全一樣。
/// </summary>
public sealed class WallOsdTimeContractTests
{
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 時間OSD建在縮放層之外且預設收起()
    {
        var rebuild = SourceContract.BodyOf(SourceContract.Read(MainWindow), "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);

        Assert.Contains("var osdTime = new TextBlock", rebuild);
        // 左上角、與狀態文字／名稱徽章同一層（縮放層之外），放大畫面時刻不會跟著放大跑掉。
        Assert.Contains("VerticalAlignment = VerticalAlignment.Top", rebuild);
        Assert.Contains("inner.Children.Add(osdTime)", rebuild);
        Assert.Contains("_cellOsdTime[i] = osdTime", rebuild);
    }

    [Fact]
    public void 重建格位後時間OSD會重新套用()
    {
        // 現場回報「時間 OSD 只有多分割才看得到，單畫面應該也要有」。
        // 真正的原因不是單分割少了什麼，而是任何一次 RebuildCells 都會建立全新的 TextBlock
        // （Visibility = Collapsed），而重建路徑上沒有人重新套用開關——切一次分割
        // （4 分割 → 單畫面）、切一次群組頁簽、還原一次版面偏好，時間 OSD 就整面消失，
        // 偏偏右鍵選單還是勾著的。這條測試守的就是「重建之後一定要再套用一次」。
        var rebuild = SourceContract.BodyOf(SourceContract.Read(MainWindow), "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);

        var created = rebuild.IndexOf("_cellOsdTime[i] = osdTime", StringComparison.Ordinal);
        var applied = rebuild.IndexOf("ApplyOsdTimeVisibility()", StringComparison.Ordinal);
        Assert.True(created >= 0, "RebuildCells 應該建立每格的時間 OSD");
        Assert.True(applied > created,
            "RebuildCells 必須在建立元素之後重新套用時間 OSD，否則重建一次就整面消失");
    }

    [Fact]
    public void 空格不顯示時間OSD且由狀態投影一起裁決()
    {
        var code = SourceContract.Read(MainWindow);

        var cell = SourceContract.BodyOf(code, "ApplyCellOsdTime");
        Assert.NotEqual(string.Empty, cell);
        // 空格上的時鐘會被讀成「這格有在錄」（與「空格不留錄影模式燈」同一條規則）。
        Assert.Contains("_cellChannel[cell] is not null", cell);
        Assert.Contains("_osdTimeEnabled", cell);

        // 顯示條件綁在「這一格有沒有頻道」，而頻道會被改派／刪除／換頁簽，
        // 所以裁決點必須在狀態投影裡（唯一的匯集點），不能只在開關那一次。
        var state = SourceContract.BodyOf(code, "ApplyCellState");
        Assert.Contains("ApplyCellOsdTime(cell)", state);
    }

    [Fact]
    public void 每秒刷新走既有計時器且關閉時不碰字串()
    {
        var code = SourceContract.Read(MainWindow);

        var status = SourceContract.BodyOf(code, "UpdateCellStatus");
        Assert.Contains("UpdateCellOsdTime()", status);

        var update = SourceContract.BodyOf(code, "UpdateCellOsdTime");
        Assert.Contains("if (!_osdTimeEnabled)", update);
        // 每秒一行 × 64 格的 TextBlock 重寫就是無謂的 UI 成本：字串沒變不重寫。
        Assert.Contains("t.Text != text", update);
    }

    [Fact]
    public void 開關持久化且載入時補正勾選()
    {
        var code = SourceContract.Read(MainWindow);

        Assert.Contains("OsdTimeKey = \"wall.osd_time\"", code);
        Assert.Contains("OsdFontColorKey = \"wall.osd_font_color\"", code);
        Assert.Contains("OsdFontSizeKey = \"wall.osd_font_size\"", code);

        var toggle = SourceContract.BodyOf(code, "OnToggleOsdTime");
        Assert.Contains("new SettingsRepository(_store).Set(", toggle);
        Assert.Contains("OsdTimeKey", toggle);

        Assert.Contains("WallOsdSettings.ParseMode(settings.Get(OsdTimeKey))", code);
        Assert.Contains("NormalizeColorHex(settings.Get(OsdFontColorKey))", code);
        Assert.Contains("NormalizeFontSize(settings.Get(OsdFontSizeKey))", code);
        Assert.Contains("ApplyOsdTimeVisibility()", code);
    }
}
