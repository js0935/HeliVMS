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
