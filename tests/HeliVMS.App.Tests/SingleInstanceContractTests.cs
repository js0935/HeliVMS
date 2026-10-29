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
        Assert.Contains("internal void BringToFront(bool maximize = false)", source);
        Assert.Contains("Activate()", SourceContract.BodyOf(source, "BringToFront"));
    }

    /// <summary>
    /// 最小化時再開一次程式，要把既有視窗最大化，而不是只還原成一般大小。
    /// </summary>
    /// <remarks>
    /// 主視窗縮到系統匣後 <c>WindowState</c> 仍是 Minimized，舊版一律回到
    /// 「離開時的大小」；操作員先前若退出過全螢幕，重複啟動就只得到一個 1280×800 的視窗，
    /// 看起來像沒反應。這裡釘住兩件事：呼叫端要求最大化，以及狀態要在 Show() 之前決定
    /// （先 Show 再放大，會先閃一下還原尺寸）。
    /// </remarks>
    [Fact]
    public void 重複啟動會把最小化的主視窗最大化()
    {
        var activate = SourceContract.BodyOf(SourceContract.Read(App), "ActivateExistingWindow");
        Assert.False(string.IsNullOrWhiteSpace(activate), "找不到 ActivateExistingWindow 的實作");
        Assert.Contains("BringToFront(maximize: true)", activate);

        var body = SourceContract.BodyOf(SourceContract.Read(Main), "BringToFront");
        Assert.Contains("WindowState.Minimized", body);

        // 只看 maximize 這一支：底下的系統匣還原分支本來就有 Maximized，整段掃字串會誤判。
        var maximizeAt = body.IndexOf("if (maximize)", StringComparison.Ordinal);
        var trayAt = body.IndexOf("else if", StringComparison.Ordinal);
        Assert.True(maximizeAt >= 0 && trayAt > maximizeAt, "找不到 if (maximize) 分支");
        var maximizeBranch = body[maximizeAt..trayAt];
        Assert.Contains("WindowState.Maximized", maximizeBranch);

        // 狀態先決定再顯示：Show() 必須出現在改 WindowState 之後。
        var stateAt = body.IndexOf("WindowState.Maximized", StringComparison.Ordinal);
        var showAt = body.IndexOf("Show()", StringComparison.Ordinal);
        Assert.True(stateAt >= 0 && showAt > stateAt, "應該先設定視窗狀態再 Show()");
    }

    /// <summary>
    /// 喚醒要認「真正的監控中心」，不能只認 <c>Application.MainWindow</c>。
    /// </summary>
    /// <remarks>
    /// 啟動時第一個建立的視窗是啟動畫面，<c>Application.MainWindow</c> 因此指向 splash；
    /// splash 關閉後那個參照要嘛是已關閉的視窗、要嘛是 null，重複啟動就變成「點了沒反應」，
    /// 而且不會留下任何日誌。主視窗因此自己保留一份參照，關閉時清掉。
    /// </remarks>
    [Fact]
    public void 喚醒對象是真正的主視窗而不是啟動畫面()
    {
        var activate = SourceContract.BodyOf(SourceContract.Read(App), "ActivateExistingWindow");
        Assert.Contains("MainWindow.Current", activate);
        Assert.DoesNotContain("this.MainWindow", activate);

        var source = SourceContract.Read(Main);
        Assert.Contains("internal static MainWindow? Current", source);
        Assert.Contains("Current = this", SourceContract.BodyOf(source, "MainWindow"));
        Assert.Contains("Current = null", SourceContract.BodyOf(source, "OnClosed"));
    }

    /// <summary>啟動過程中再點一次捷徑不能消失：視窗一顯示就要補做喚醒。</summary>
    [Fact]
    public void 主視窗還沒建立時會記下喚醒請求並在顯示後補做()
    {
        var activate = SourceContract.BodyOf(SourceContract.Read(App), "ActivateExistingWindow");
        Assert.Contains("_activateWhenReady", activate);

        var startup = SourceContract.BodyOf(SourceContract.Read(App), "OnStartup");
        Assert.Contains("_activateWhenReady", startup);
        Assert.Contains("BringToFront(maximize: true)", startup);
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
