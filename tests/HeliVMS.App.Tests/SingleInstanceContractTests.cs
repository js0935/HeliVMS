namespace HeliVMS.App.Tests;

/// <summary>
/// 單一實例的接線契約：程式已在執行時，重複啟動不開第二個行程，而是喚醒並把既有的
/// 主視窗還原到前景。
///
/// <para>
/// 為什麼要在這裡釘住：<c>SingleInstanceGuard</c> 的行為（互斥鎖、喚醒事件）
/// 是跨行程的，net10.0 測試專案無法可靠地啟兩個真實 GUI 行程來驗；但真正會回歸的是「接線」：
/// 判斷位置太晚（先開 splash／登入才退出）、喚醒沒有轉回 UI 執行緒、或把整個守衛拿掉。
/// 這些都用來源掃描擋住——桌面啟動流程過去就是這樣漏掉守衛而全綠（見
/// <see cref="AuthBootstrapContractTests"/> 對 WPF 無執行期測試的說明）。
/// </para>
/// </summary>
public sealed class SingleInstanceContractTests
{
    private const string App = "src/HeliVMS.App/App.xaml.cs";
    private const string Guard = "src/HeliVMS.App/SingleInstanceGuard.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 重複啟動時在啟動畫面之前就判斷單一實例並結束自己()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(App), "OnStartup");

        var acquireAt = body.IndexOf("TryAcquire", StringComparison.Ordinal);
        var splashAt = body.IndexOf("new SplashWindow", StringComparison.Ordinal);

        Assert.True(acquireAt >= 0, "OnStartup 必須嘗試取得單一實例守衛");
        Assert.True(
            acquireAt < splashAt,
            "單一實例判斷必須在啟動畫面之前：先開 splash／登入才退出會讓使用者看到閃一下的假啟動");

        Assert.Contains("Shutdown", body);
    }

    [Fact]
    public void 喚醒既有視窗要轉回UI執行緒()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(App), "ActivateExistingWindow");

        // 由背景等待執行緒觸發，直接碰視窗會丟跨執行緒例外，必須經過 Dispatcher。
        Assert.Contains("Dispatcher", body);
        Assert.Contains("BringToFront", body);
    }

    [Fact]
    public void 主視窗提供把既有視窗帶到前景的方法()
    {
        // App 呼叫 MainWindow.BringToFront；它必須真的存在且可被 App 呼叫（internal 以上）。
        var source = SourceContract.Read(Main);
        Assert.Contains("internal void BringToFront()", source);
        Assert.Contains("Activate()", SourceContract.BodyOf(source, "BringToFront"));
    }

    [Fact]
    public void 守衛以具名互斥鎖判斷並以具名事件喚醒()
    {
        var source = SourceContract.Read(Guard);

        // 兩個跨行程名稱都必須是 Local\ 前置詞：只擋同一登入工作階段的重複啟動。
        Assert.Contains(@"Local\", source);
        Assert.Contains("Mutex", source);
        Assert.Contains("EventWaitHandle", source);

        // 等待要在背景執行緒上，否則第一個實例的介面會被等待迴圈卡死。
        Assert.Contains("IsBackground = true", source);
    }

    [Fact]
    public void 第二個實例發送喚醒訊號後回傳null()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Guard), "TryAcquire");

        // 非第一個時：Set 喚醒事件、回傳 null（呼叫端據此 Shutdown），而不是繼續當第一個跑。
        Assert.Contains(".Set()", body);
        Assert.Contains("return null", body);
    }

    [Fact]
    public void 關閉時釋放守衛讓下次能再啟動()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(App), "OnExit");

        Assert.Contains("_singleInstance", body);
        Assert.Contains("Dispose", body);
    }
}
