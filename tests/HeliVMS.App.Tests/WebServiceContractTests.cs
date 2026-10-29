namespace HeliVMS.App.Tests;

/// <summary>
/// 桌面內嵌 Web 服務契約（M305）。
/// </summary>
/// <remarks>
/// <para>
/// 設定中心「網路」頁要在行程內啟停 <c>HeliVMS.WebApi</c>。桌面端沒有 WPF 執行期測試，
/// 但這條接線斷掉的症狀很隱晦：按了「套用」後狀態文字照樣顯示「執行中」，實際上主機
/// 從沒被拉起，瀏覽器打開是連線被拒——畫面與 log 都不會指出真正原因。因此用來源契約
/// 把 UI ↔ <c>WebHost</c> ↔ <c>WebApiHost</c> 的三段接線釘住。
/// </para>
/// <para>
/// 另一條不能省的是「內嵌時不掛背景服務」：桌面程序本身已有保留迴圈，內嵌主機若再掛一份
/// <c>RetentionService</c>，兩個清理器會對同一個 recordings 目錄同時刪檔，屬於在正式環境
/// 才咬人、測試卻全綠的那種問題。
/// </para>
/// </remarks>
public sealed class WebServiceContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string WebHost = "src/HeliVMS.App/Services/WebHost.cs";

    [Fact]
    public void 設定中心有可啟停Web服務的網路頁()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "NavNetwork", "PageNetwork", "WebEnabledBox", "WebPortBox",
                     "WebAllowNoKeyBox", "WebApplyButton", "WebServiceStatusText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Contains("Click=\"OnApplyWebClicked\"", xaml);

        var nav = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnNavChanged");
        Assert.NotEqual(string.Empty, nav);
        Assert.Contains("PageNetwork.Visibility", nav);
    }

    [Fact]
    public void 網路頁會把Web設定寫進共用設定並即時套用()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnApplyWebClicked");
        Assert.NotEqual(string.Empty, handler);
        Assert.Contains("WebHost.EnabledKey", handler);
        Assert.Contains("WebHost.PortKey", handler);
        Assert.Contains("WebHost.AllowNoKeyKey", handler);
        Assert.Contains("_webHost.ApplySettings", handler);
    }

    [Fact]
    public void 主視窗啟動時會依設定拉起Web服務並於關閉時停掉()
    {
        var code = SourceContract.Read(MainWindow);

        var onLoaded = SourceContract.BodyOf(code, "OnLoaded");
        Assert.NotEqual(string.Empty, onLoaded);
        Assert.Contains("_webHost = new WebHost(", onLoaded);
        Assert.Contains("_webHost.ApplySettings", onLoaded);

        // 關閉時一定要停掉 Kestrel；否則程序結束後連接埠仍被占用，下次啟動會直接失敗。
        // 收尾一律先換成本地變數再丟進 RunShutdownStep（欄位在收尾開始時就清掉了，
        // 才不會有事件回呼拿到拆到一半的實例），所以這裡認的是那條步驟。
        Assert.Contains("RunShutdownStep(\"關閉網頁主機\", () => DisposeSync(webHost)", code);
    }

    [Fact]
    public void 內嵌Web服務只綁本機且不重複掛保留清理()
    {
        var web = SourceContract.Read(WebHost);
        Assert.NotEqual(string.Empty, web);

        // 只綁 localhost：預設無 TLS 的 Kestrel 不該被當成對外服務。
        Assert.Contains("http://localhost:", web);

        // 明確帶上桌面正在用的資料庫路徑，避免內嵌主機另開一份資料庫。
        Assert.Contains("DataRootLayout.DatabaseVariable", web);

        // 內嵌時不掛背景服務，避免與桌面保留迴圈搶同一批檔案。
        Assert.Contains("IncludeHostedServices = false", web);
    }
}
