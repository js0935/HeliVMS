using System.Text.RegularExpressions;

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
    /// 重複啟動的第二個行程不得清掃子行程。
    /// </summary>
    /// <remarks>
    /// 現場回報：程式最小化到系統匣後重複執行，視窗有回來（滿版），但牆上攝影機全部
    /// 斷線重連。原因：第二個實例 <c>TryAcquire</c> 失敗後照樣走到 <c>OnExit</c>，
    /// <c>KillOrphanChildren</c> 把機器上所有 ffmpeg／ffprobe 全部殺掉——包括第一個實例
    /// 正在使用的解碼器與錄影寫入器，工作階段只好全部重連，正在錄的那段也被記成損毀。
    /// 清掃只屬於「擁有單一實例的行程」：它結束時，機器上殘留的子行程才真的是孤兒。
    /// </remarks>
    [Fact]
    public void 重複啟動的第二個實例不會清掃第一實例的子行程()
    {
        var exit = SourceContract.BodyOf(SourceContract.Read(App), "OnExit");

        var ownedAt = exit.IndexOf("ownedSingleInstance", StringComparison.Ordinal);
        Assert.True(ownedAt >= 0, "OnExit 必須先判斷本行程是否擁有單一實例，才能決定要不要清掃子行程");

        var sweepAt = exit.IndexOf("KillOrphanChildren()", StringComparison.Ordinal);
        Assert.True(
            sweepAt > ownedAt,
            "清掃必須在擁有權判斷之後，並被擁有權守衛包住：第二個實例清掃會殺掉第一實例的解碼器");

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                exit,
                @"if\s*\(\s*ownedSingleInstance\s*\)\s*\{[\s\S]*?KillOrphanChildren\(\)"),
            "KillOrphanChildren 必須在 if (ownedSingleInstance) 區塊內：無條件清掃會殺掉第一實例的子行程");

        // OnStartup 的清掃也必須在第二實例分支的 return 之後：那個分支若清掃，
        // 殺到的是別人（第一實例）的子行程。
        var startup = SourceContract.BodyOf(SourceContract.Read(App), "OnStartup");
        var nullGuardAt = startup.IndexOf("if (_singleInstance is null", StringComparison.Ordinal);
        var returnAt = startup.IndexOf("return;", nullGuardAt, StringComparison.Ordinal);
        var startupSweepAt = startup.IndexOf("KillOrphanChildren", StringComparison.Ordinal);
        Assert.True(
            nullGuardAt >= 0 && returnAt > nullGuardAt && startupSweepAt > returnAt,
            "OnStartup 的清掃必須位於「單一實例判斷失敗就 return」之後");
    }

    /// <summary>
    /// 喚醒氣泡只在重複啟動那一次出現；系統匣的停留提示是「這個實例多老了、跑的是哪一版」
    /// 的常駐來源——操作員平時只會滑過系統匣圖示。提示必須帶本行程的啟動時刻與程式建置
    /// 時間（i18n 三語）、超過 Windows 63 字元上限時截斷而不是丟例外。
    /// </summary>
    [Fact]
    public void 系統匣停留提示常駐亮出本行程的啟動時刻()
    {
        var tray = SourceContract.Read("src/HeliVMS.App/TrayIconHost.cs");
        Assert.Contains("public void SetTooltip(string text)", tray);
        Assert.Contains("text.Length <= 63", tray);

        var main = SourceContract.Read(Main);
        // 必須在建構式（托盤建立處）就設定提示：晚到任何一步都有「滑過圖示看不到年齡」的窗口。
        var ctor = SourceContract.BodyOf(main, "MainWindow");
        Assert.Contains("SetTrayTooltip()", ctor);

        var wake = SourceContract.BodyOf(main, "SetTrayTooltip");
        Assert.Contains("Process.GetCurrentProcess()", wake);
        Assert.Contains("StartTime", wake);
        Assert.Contains("Main.TrayTooltip", wake);
        // 發佈副本（win-x64）與開發 exe 只隔一個資料夾：建置時間是辨識「跑的是哪一版」的關鍵。
        Assert.Contains("Main.TrayTooltipBuilt", wake);
        Assert.Contains("GetBuildTimeLabel", main);
        Assert.Contains("GetLastWriteTime", SourceContract.BodyOf(main, "GetBuildTimeLabel"));

        var wakeBalloon = SourceContract.BodyOf(main, "ShowWakeBalloon");
        Assert.Contains("Main.WakeBodyBuilt", wakeBalloon);

        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");
        Assert.Equal(3, Regex.Matches(i18n, "\"Main.TrayTooltip\"").Count);
        Assert.Equal(3, Regex.Matches(i18n, "\"Main.TrayTooltipBuilt\"").Count);
        Assert.Equal(3, Regex.Matches(i18n, "\"Main.WakeBodyBuilt\"").Count);
    }

    /// <summary>
    /// 孤兒匯出工作的結算必須在建構式執行、筆數亮在提示列（i18n 三語）：
    /// 操作員不必自己打開匯出中心才發現有一筆「執行中」掛了好幾天
    /// （實測 11-01 的 running 掛到 11-12）。
    /// </summary>
    [Fact]
    public void 啟動孤兒匯出對帳會在建構式執行並把筆數亮在提示列()
    {
        var main = SourceContract.Read(Main);

        // 建構式很大（BodyOf 會在第一個 } 截斷），改用順序斷言：
        // 對帳呼叫必須在 Localizer.Init 之前（托盤／提示都要語系就緒後才說人話）。
        var callAt = main.IndexOf("FailRunningAsOrphaned(", StringComparison.Ordinal);
        var initAt = main.IndexOf("Localizer.Init(", StringComparison.Ordinal);
        var hintAt = main.IndexOf("Main.ExportOrphans", StringComparison.Ordinal);
        Assert.True(callAt >= 0, "找不到啟動孤兒匯出對帳的呼叫");
        Assert.True(initAt >= 0 && callAt < initAt, "對帳必須在建構式內（Localizer.Init 之前）執行");
        Assert.True(hintAt > initAt, "孤兒匯出提示必須在 Localizer.Init 之後");
        Assert.Contains("_exportOrphans", main);

        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");
        Assert.Equal(3, Regex.Matches(i18n, "\"Main.ExportOrphans\"").Count);
    }

    /// <summary>
    /// 設定中心不得把「剛載入的值」回寫進設定：ReloadHwAccel 的程序化選取會觸發
    /// SelectionChanged，必須被抑制旗標擋下（實測 368 列 settings.set 稽核，
    /// 每次造訪解碼頁籤一列，操作員根本沒有變更任何東西）。
    /// </summary>
    [Fact]
    public void 載入解碼設定頁不會回寫decode_hwaccel()
    {
        var source = SourceContract.Read("src/HeliVMS.App/SettingsWindow.xaml.cs");

        // 抑制旗標必須存在、ReloadHwAccel 設定它、OnHwAccelChanged 檢查它。
        Assert.Contains("_suppressHwAccelChanged = true;", source);
        Assert.Contains("if (_suppressHwAccelChanged || !IsLoaded)", source);

        // 順序：旗標歸零必須在 finally（早退路徑也要復原）。
        var reload = SourceContract.BodyOf(source, "ReloadHwAccel");
        Assert.Contains("_suppressHwAccelChanged = true;", reload);
        Assert.Contains("finally", reload);
        Assert.Contains("_suppressHwAccelChanged = false;", reload);
    }

    /// <summary>
    /// 喚醒要認「真正的監控中心」，不能只認 <c>Application.MainWindow</c>。
    /// </summary>    /// <remarks>
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

    /// <summary>
    /// 被喚醒的舊實例必須用系統匣氣泡告知操作員「這是同一個行程」。
    ///
    /// <para>
    /// 喚醒的畫面與真正啟動一模一樣（滿版回前景），第二個行程靜靜退出——操作員無從
    /// 分辨「更新有沒有生效」。2026-11 現場：一個 10-30 的舊行程活到 11-12，期間的
    /// 修復原始碼裡早就在，操作員卻一直跑沒有它的版本（164 筆收檔失敗日誌）。氣泡帶
    /// 本行程的啟動時刻，執行了好幾天一眼就看出來。
    /// </para>
    /// </summary>
    [Fact]
    public void 被喚醒的舊實例會用氣泡亮出自己的啟動時刻()
    {
        var source = SourceContract.Read(Main);
        var body = SourceContract.BodyOf(source, "BringToFront");

        // 氣泡屬於「重複啟動」分支：系統匣還原（else if 之後）不需要它。
        var maximizeAt = body.IndexOf("if (maximize)", StringComparison.Ordinal);
        var trayAt = body.IndexOf("else if", StringComparison.Ordinal);
        Assert.True(maximizeAt >= 0 && trayAt > maximizeAt, "找不到 if (maximize) 分支");
        Assert.Contains("ShowWakeBalloon()", body[maximizeAt..trayAt]);

        var wake = SourceContract.BodyOf(source, "ShowWakeBalloon");
        Assert.Contains("Process.GetCurrentProcess()", wake);
        Assert.Contains("StartTime", wake);
        Assert.Contains("Main.WakeTitle", wake);
        Assert.Contains("Main.WakeBody", wake);
        // 拿不到啟動時刻就靜靜不提示：沒有資訊的提示比沒有提示更糟。
        Assert.Contains("return", wake);

        // 文案必須走 i18n（三語都要有鍵），且托盤要有對應的氣泡入口。
        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(i18n, "\"Main.WakeBody\"").Count);
        Assert.Contains("void ShowBalloon(string title, string text)", SourceContract.Read("src/HeliVMS.App/TrayIconHost.cs"));
    }
}
