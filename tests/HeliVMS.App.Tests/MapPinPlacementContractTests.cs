namespace HeliVMS.App.Tests;

/// <summary>
/// 電子地圖放置圖釘的桌面契約（M41，§16.1）。
///
/// 守的失效是「只有第一台攝影機放得上去」：舊版 <c>OnMapPinCanvasClick</c> 每次點地圖都
/// 先重建設備清單，而重建會把 <c>SelectedIndex</c> 設回 0，於是在點下去的瞬間，操作員剛
/// 選好的攝影機被換成清單第一台。畫面沒有任何錯誤訊息，看起來就像其他攝影機不存在，
/// 而實際上資料庫、Repository 與放置流程全都正常。
/// </summary>
public sealed class MapPinPlacementContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string MapCode = "src/HeliVMS.App/MapWindow.xaml.cs";

    /// <summary>點擊地圖時不可再重建設備清單；重建等於把使用者的選擇丟掉。</summary>
    [Fact]
    public void 點擊地圖放置圖釘時不會重建設備清單而覆蓋已選設備()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnMapPinCanvasClick");

        // 只看程式碼：說明文字會提到被禁止的呼叫，直接掃字串會誤判成真的呼叫了它。
        var code = System.Text.RegularExpressions.Regex.Replace(body, @"//[^\r\n]*", string.Empty);

        Assert.DoesNotContain("ReloadMapPinChannelCombo(", code);
    }

    /// <summary>重建清單要依先前選取的 Tag 還原，不能一律回到第一項。</summary>
    [Fact]
    public void 設備清單重建時會保留先前選取的設備()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadMapPinChannelCombo");

        // 只認 Tag 的 id：用顯示文字比對會在切換語言後失效，而且圖層的中文名稱已經在地化。
        Assert.Contains("previous", body);
        Assert.Contains("Tag is int", body);
    }

    /// <summary>已放置的設備要留在清單裡並標記停用，操作員才看得出「這台放過了」。</summary>
    [Fact]
    public void 已放置的設備留在清單中並標記為停用()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadMapPinChannelCombo");

        Assert.Contains("IsEnabled = !isPlaced", body);
        Assert.Contains("Settings.MapAlreadyPlaced", body);
    }

    /// <summary>
    /// 圖釘左鍵開即時畫面、右鍵開回放。
    /// 舊版只掛 <c>MouseLeftButtonUp</c> 再判斷 <c>e.RightButton</c>，該欄位在左鍵事件中
    /// 永遠不是 Pressed，所以右鍵回放實際上是死碼。
    /// </summary>
    [Fact]
    public void 地圖圖釘左鍵開即時畫面右鍵開回放()
    {
        var source = SourceContract.Read(MapCode);

        Assert.Contains("MouseLeftButtonUp += OnPinLiveClick", source);
        Assert.Contains("MouseRightButtonUp += OnPinPlaybackClick", source);
    }

    /// <summary>即時畫面要能重用主視窗既有的監看連線，否則每點一次地圖就多拉一條 RTSP。</summary>
    [Fact]
    public void 即時畫面優先重用既有的監看工作階段()
    {
        var source = SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml.cs");

        Assert.Contains("ChannelSessionFor(", source);
        Assert.Contains("IsMonitoring", source);
    }
}
