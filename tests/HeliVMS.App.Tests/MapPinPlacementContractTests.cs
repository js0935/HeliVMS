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

    /// <summary>
    /// 圖釘單擊的即時畫面是小視窗，不是全螢幕。
    ///
    /// 這裡守的是操作流程而不是外觀：地圖的用途是「一邊看平面圖、一邊確認某個點位的畫面」，
    /// 一旦即時畫面佔滿整個螢幕，剛點的那張地圖就被蓋掉，操作員得先關窗才回得到地圖，
    /// 連續巡幾個點位變成開窗／關窗的體操。無邊框視窗沒有系統標題列可拖曳，所以拖曳也
    /// 必須自己接——否則小視窗會固定釘在畫面中央，擋住它正在被對照的那塊地圖。
    /// </summary>
    [Fact]
    public void 圖釘單擊的即時畫面是小視窗而不是全螢幕()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml");

        Assert.DoesNotContain("WindowState=", xaml);

        var width = System.Text.RegularExpressions.Regex.Match(xaml, @"\bWidth=""(\d+)""");
        Assert.True(width.Success, "LiveWindow.xaml 必須明確指定視窗寬度");
        Assert.InRange(int.Parse(width.Groups[1].Value), 320, 900);

        Assert.Contains("MouseLeftButtonDown=\"OnHeaderDrag\"", xaml);
        Assert.Contains("DragMove()", SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml.cs"));
    }

    /// <summary>
    /// 重複點擊同一顆圖釘只會有一個視窗，第二次是把原視窗提到最前面。
    /// </summary>
    /// <remarks>
    /// 圖釘很容易被連點（第一下沒反應就會再點一下），而每開一個視窗就是一條 RTSP 連線加
    /// 一份解碼。更糟的是「同一個畫面開五個視窗疊在一起」對操作員毫無意義：他分不出哪個是
    /// 哪個，關窗時還會一個一個冒出來，看起來像關不掉。所以地圖不得直接 new 視窗，必須走
    /// <c>LiveWindow.ShowFor</c> 這條「已開著就還原」的路徑。
    /// </remarks>
    [Fact]
    public void 重複點擊同一顆圖釘不會開第二個視窗()
    {
        var pinBody = SourceContract.BodyOf(
            SourceContract.Read("src/HeliVMS.App/MapWindow.xaml.cs"), "OnPinLiveClick");

        Assert.False(string.IsNullOrWhiteSpace(pinBody), "找不到 OnPinLiveClick 的實作");
        Assert.Contains("LiveWindow.ShowFor", pinBody);
        Assert.DoesNotContain("new LiveWindow(", pinBody);

        var live = SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml.cs");
        var showFor = SourceContract.BodyOf(live, "ShowFor");

        Assert.Contains("s_open.TryGetValue", showFor);
        Assert.Contains("BringToFront()", showFor);
    }

    /// <summary>關掉的視窗要從「已開著」的名單移除，否則下次點同一顆圖釘只會還原一個不存在的視窗。</summary>
    [Fact]
    public void 即時畫面關閉後會從已開視窗名單移除()
    {
        var closed = SourceContract.BodyOf(
            SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml.cs"), "OnClosed");

        Assert.Contains("s_open.Remove(_channelId)", closed);
    }

    /// <summary>
    /// 平面圖要自動縮放成「整張都看得到」，而不是要操作員自己拉捲軸找圖的另一半。
    /// </summary>
    /// <remarks>
    /// 縮放刻意用「改畫布版面尺寸」而不是 <c>RenderTransform</c>：RenderTransform 不會改變
    /// ScrollViewer 認定的內容大小，縮小時會留下一大片可捲動的空白、放大時又捲不到右邊那半張
    /// 圖。這條同時釘住「換圖與視窗大小改變都要重算」以及「不放大超過原始尺寸」（放大只是變
    /// 模糊，圖釘還會愈偏愈遠）。
    /// </remarks>
    [Fact]
    public void 平面圖會自動縮放至整張可見且不放大()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MapWindow.xaml.cs");

        var fit = SourceContract.BodyOf(code, "FitScale");
        Assert.False(string.IsNullOrWhiteSpace(fit), "找不到 FitScale 的實作");
        Assert.Contains("ViewportWidth", fit);
        Assert.Contains("ViewportHeight", fit);
        Assert.Contains("Math.Min(", fit);
        Assert.Contains("Math.Clamp(scale, 0.02, 1.0)", fit);

        // 換圖就從「整張圖」開始，而不是 1:1 讓操作員自己縮。
        Assert.Contains("_zoom = FitScale()", SourceContract.BodyOf(code, "ShowMap"));

        // 視窗大小改變要重算；沒有這段，開窗時才對的那個比例在拉大視窗後就空一半。
        Assert.Contains("SizeChanged", code);
        Assert.Contains("ApplyFitIfNeeded", code);

        var apply = SourceContract.BodyOf(code, "ApplyZoom");
        Assert.Contains("MapCanvas.Width = w", apply);
        Assert.Contains("LayoutPins()", apply);
        Assert.DoesNotContain("RenderTransform", apply);
        Assert.DoesNotContain("ScrollViewer", apply);
    }

    /// <summary>
    /// 攝影機的涵蓋扇形要以圖釘自己的中心為頂點，不是以畫布座標上的落點。
    /// </summary>
    /// <remarks>
    /// 扇形是圖釘 18×18 方格的子元素，所以它的座標系原點是圖釘左上角，不是畫布左上角。
    /// 原本傳進去的是畫布上的 (cx, cy)，整個扇形因此固定往右下偏 9px：操作員把圖釘對準
    /// 走廊，扇形卻指向牆外，看起來像方位角設定錯了，其實只是座標系用錯。這裡把頂點釘在
    /// 與 <c>Canvas.SetLeft</c> 共用的常數上，兩者就不可能各自漂移。
    /// </remarks>
    [Fact]
    public void 涵蓋扇形以圖釘中心為頂點而不是畫布座標()
    {
        var layout = SourceContract.BodyOf(SourceContract.Read(MapCode), "LayoutPins");
        Assert.False(string.IsNullOrWhiteSpace(layout), "找不到 LayoutPins 的實作");

        // 圖釘定位與扇形頂點必須用同一個常數；一處改成常數、另一處留著 9 就會再偏一次。
        Assert.Contains("Canvas.SetLeft(p.Root, cx - PinCenter)", layout);
        Assert.Contains("Canvas.SetTop(p.Root, cy - PinCenter)", layout);
        Assert.Contains("CreateSector(", layout);
        Assert.Contains("PinCenter,", layout);
        Assert.DoesNotContain("CreateSector(cx, cy", layout);
    }

    /// <summary>
    /// 設定頁的平面圖編輯畫面也要縮放成整張可見，而不是 1:1 只露出左上角。
    /// </summary>
    /// <remarks>
    /// 編輯框固定 210px 高，一張 4K 平面圖以原始像素放進去只看得到一小塊；操作員必須先拉捲軸
    /// 才知道圖釘該往哪裡放，而拉過去之後又失去整層的相對位置。這裡同時釘住「圖釘用顯示尺寸
    /// 換算」：只縮圖、圖釘仍用原始像素定位的話，所有圖釘會擠在左上角。
    /// </remarks>
    [Fact]
    public void 設定頁的平面圖編輯畫面會縮放至整張可見()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/SettingsWindow.xaml");
        Assert.Contains("x:Name=\"MapPinScroll\"", xaml);

        var code = SourceContract.Read(SettingsCode);

        var fit = SourceContract.BodyOf(code, "ApplyMapPinFit");
        Assert.False(string.IsNullOrWhiteSpace(fit), "找不到 ApplyMapPinFit 的實作");
        Assert.Contains("ViewportWidth", fit);
        Assert.Contains("ViewportHeight", fit);
        Assert.Contains("Math.Min(", fit);
        Assert.Contains("Math.Clamp(", fit);

        // 寬高已按比例算好，所以用 Fill；None 會維持原始像素而被固定高度的框切掉。
        Assert.Contains("Stretch.Fill", fit);

        // 換圖與視窗尺寸改變都要重算。
        Assert.Contains("ApplyMapPinFit()", SourceContract.BodyOf(code, "ReloadMapPins"));
        Assert.Contains("MapPinScroll.SizeChanged", code);

        var layout = SourceContract.BodyOf(code, "LayoutMapPins");
        Assert.False(string.IsNullOrWhiteSpace(layout), "找不到 LayoutMapPins 的實作");
        Assert.Contains("device.X", layout);
        Assert.Contains("device.Y", layout);
    }
}
