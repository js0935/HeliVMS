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
        Assert.Contains("Height=\"340\"", source);
        Assert.Contains("ResizeMode=\"NoResize\"", source);
        Assert.Contains("Topmost=\"True\"", source);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", source);
        Assert.Contains("OnDragHandle", source);
    }
}
