namespace HeliVMS.App.Tests;

/// <summary>
/// PTZ 控制面板的視窗契約。
///
/// <para>
/// 現場症狀：從監看畫面右鍵選「PTZ」之後整個畫面被一塊 220x340 的空面板蓋住——面板本身
/// 是刻意做成貼在畫面旁邊的微型控制器（只有方向鍵、縮放與預設點），一旦最大化就變成
/// 「整個螢幕幾乎都是空白」的畫面，操作員還以為程式當掉了。XAML 裡一行
/// <c>WindowState="Maximized"</c> 就會造成這個結果，而且編譯不會有任何警告。
/// </para>
/// <para>
/// 桌面端沒有 WPF 執行期測試環境（無法在 CI 開視窗量 <c>WindowState</c>），所以這裡掃 XAML
/// 的視窗宣告。守的是「它會蓋住整個螢幕」這件事，不是某個屬性值：改回最大化必定紅。
/// </para>
/// </summary>
public sealed class PtzWindowContractTests
{
    private const string Xaml = "src/HeliVMS.App/PtzWindow.xaml";

    [Fact]
    public void PTZ面板不會以最大化開啟()
    {
        var source = SourceContract.Read(Xaml);

        Assert.DoesNotContain("WindowState=\"Maximized\"", source);
        Assert.DoesNotContain("WindowState=\"Minimized\"", source);
    }

    /// <summary>
    /// 面板必須維持「貼在角落的小控制器」尺寸，而且不能被縮放到全螢幕。
    ///
    /// 這不是美觀問題：<c>ResizeMode="CanResize"</c> 配上最大化就是上面那個畫面的來源；
    /// 而拿掉 <c>Topmost</c> 會讓面板被全螢幕監看畫面蓋住，等於打開了卻看不到。
    /// 同一個理由，面板沒有標題列（<c>WindowStyle="None"</c>），所以一定要留著拖曳把手：
    /// 否則它永遠釘在畫面正中央又永遠置頂，操作員移不開。
    /// </summary>
    [Fact]
    public void PTZ面板維持浮動小視窗且置頂()
    {
        var source = SourceContract.Read(Xaml);

        Assert.Contains("Width=\"220\"", source);
        Assert.Contains("Height=\"374\"", source);
        Assert.Contains("ResizeMode=\"NoResize\"", source);
        Assert.Contains("Topmost=\"True\"", source);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", source);
        Assert.Contains("OnDragHandle", source);
    }

    /// <summary>
    /// 速度（1–8）與參考機同一組語意：滑桿＋慢／快，預設 8＝與加入速度控制之前相同的轉幅，
    /// 而且**兩條移動路徑都要縮放**（RelativeMove 的一步大小、退化路徑 ContinuousMove 的速度向量）。
    /// </summary>
    /// <remarks>
    /// 只縮放其中一條的症狀很隱晦：支援 RelativeMove 的相機變得可以微調，而回 ActionNotSupported
    /// 而走 ContinuousMove 的那一台完全沒感覺——操作員會以為「速度只在某些攝影機有效」。
    /// 設定值寫在改動當下，不是關窗才寫：關窗有自己的收尾路徑，漏掉就是「調完下一次又回到全速」。
    /// </remarks>
    [Fact]
    public void 速度控制會縮放兩條移動路徑且立即持久化()
    {
        var xaml = SourceContract.Read(Xaml);
        foreach (var element in new[] { "PtzSpeedSlider", "PtzSlowerButton", "PtzFasterButton" })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        var code = SourceContract.Read("src/HeliVMS.App/PtzWindow.xaml.cs");

        var nudge = SourceContract.BodyOf(code, "NudgeAsync");
        Assert.Contains("SpeedScale()", nudge);
        Assert.Contains("NudgeStep * scale", nudge);
        Assert.Contains("pan * scale, tilt * scale, zoom * scale", nudge);

        Assert.Contains("SpeedKey = \"ptz.speed\"", code);
        Assert.Contains("private const int DefaultSpeedLevel = 8;", code);

        var changed = SourceContract.BodyOf(code, "OnSpeedChanged");
        Assert.Contains("_settings.Set(SpeedKey", changed);
    }

    /// <summary>
    /// 建構子必須在 <c>InitializeComponent()</c> <b>之前</b> 備好設定來源，而且載入偏好期間
    /// 不得回寫設定。
    /// </summary>
    /// <remarks>
    /// 這是一路走過來的現場故障：XAML 的 <c>Value="8"</c> 會在 InitializeComponent 期間就觸發
    /// <c>ValueChanged</c>，那時若 <c>_settings</c> 還沒建立，建構子直接擲
    /// NullReferenceException——症狀是「PTZ 完全叫不出來」，而且外層看不到任何錯誤訊息
    /// （App.Tests 不編譯 App，這個錯在契約測試裡全綠）。同一個事件在載入偏好時若照樣寫回設定，
    /// 還會把操作員存的級別蓋成 XAML 的預設值（設定中心「載入就回寫」那課的同一個形狀）。
    /// </remarks>
    [Fact]
    public void 建構子先備好設定來源且載入期間不回寫()
    {
        var code = SourceContract.Read("src/HeliVMS.App/PtzWindow.xaml.cs");

        var ctor = code.IndexOf("public PtzWindow(", StringComparison.Ordinal);
        var settings = code.IndexOf("_settings = new SettingsRepository(store);", StringComparison.Ordinal);
        var init = code.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.True(ctor >= 0 && settings > ctor, "找不到建構子裡的設定來源初始化");
        Assert.True(init > ctor, "找不到 InitializeComponent()");
        Assert.True(settings < init,
            "設定來源必須在 InitializeComponent 之前建立：XAML 的 Value 會在 InitializeComponent "
            + "期間觸發 ValueChanged，那時 _settings 為 null 就是建構子擲例外（PTZ 叫不出來）");

        // 偏好套用完才解除抑制，否則「XAML 的 8」或「程式指派的級別」會被當成操作員拖曳而寫回設定。
        Assert.Contains("private bool _suppressSpeedChanged = true;", code);
        var slider = code.IndexOf("PtzSpeedSlider.Value = _speedLevel;", StringComparison.Ordinal);
        var release = code.IndexOf("_suppressSpeedChanged = false;", StringComparison.Ordinal);
        Assert.True(slider > init, "套用偏好要在 InitializeComponent 之後");
        Assert.True(release > slider, "解除抑制要在套用偏好之後");
        Assert.Contains("if (_suppressSpeedChanged)", code);
    }
}
