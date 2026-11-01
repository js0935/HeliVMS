namespace HeliVMS.App.Tests;

/// <summary>
/// 「設定中心 → IO」頁的攝影機乾接點整合契約。
/// </summary>
/// <remarks>
/// 為什麼需要這條契約：攝影機內建的 DI/DO 早就有底層服務（事件訂閱、繼電器輸出），
/// 但桌面上沒有地方可以設定——操作員只能到一個模擬用的 IO 視窗手動猜端子號。
/// 這一頁把設備實際回報的端子列出來，並把「這個端子就是那個埠」明確存起來。
/// 少了其中任何一段，功能就會退回「靠端子號猜」，猜錯等於驅動錯的閘門。
/// </remarks>
public sealed class SettingsIoCameraContractTests
{
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Factory = "src/HeliVMS.App/Services/OnvifServiceFactory.cs";

    [Fact]
    public void 設定中心的IO頁有攝影機乾接點區塊()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"IoCamCombo\"", xaml);
        Assert.Contains("x:Name=\"IoCamReadButton\"", xaml);
        Assert.Contains("x:Name=\"IoCamTerminalList\"", xaml);
        Assert.Contains("x:Name=\"IoCamBindButton\"", xaml);
        Assert.Contains("x:Name=\"IoCamUnbindButton\"", xaml);
        Assert.Contains("x:Name=\"IoCamOnButton\"", xaml);
        Assert.Contains("x:Name=\"IoCamOffButton\"", xaml);

        // 必須在 IO 頁裡面（不能是另一個獨立視窗）：切成別頁時整塊要跟著收起來。
        var start = xaml.IndexOf("x:Name=\"PageIo\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 IO 頁");
        var page = xaml[start..];
        var end = page.IndexOf("<!-- Maps", StringComparison.Ordinal);
        Assert.True(end > 0, "找不到 IO 頁的結尾");
        Assert.Contains("IoCamTerminalList", page[..end]);

        // XAML 不得出現中文（三語字典才是字串來源；棘輪測試也在擋）。
        Assert.DoesNotMatch("[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void 讀取端子會同時列出DI與DO並在失敗時給出原因()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnIoCamReadClicked");

        Assert.Contains("GetDigitalInputsAsync(", body);
        Assert.Contains("GetRelayOutputsAsync(", body);

        // 只支援 DI（或只支援 DO）的設備很常見，兩邊分開讀，一邊失敗不能讓另一邊也空白。
        Assert.Contains("Settings.IoCamInputsFailed", body);
        Assert.Contains("Settings.IoCamOutputsFailed", body);
    }

    [Fact]
    public void 綁定會把端子寫進埠並讓DI監視器重掃()
    {
        var source = SourceContract.Read(Code);
        var body = SourceContract.BodyOf(source, "OnIoCamBindClicked");

        Assert.Contains("EnsureForTerminal(", body);
        Assert.Contains("row.Token", body);

        // 綁定完成後若不重掃，新的 DI 端子要等程式重啟才開始進事件——操作員會以為綁定沒生效。
        Assert.Contains("RefreshAndStart(", body);

        // 解除綁定只清端子，不刪埠（埠上可能掛著動作鏈規則）。
        var unbind = SourceContract.BodyOf(source, "OnIoCamUnbindClicked");
        Assert.Contains("SetTerminalToken(", unbind);
        Assert.DoesNotContain("_ports.Delete(", unbind);
    }

    [Fact]
    public void DO測試直接打在攝影機繼電器上()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnIoCamDoClicked");

        Assert.Contains("SetRelayOutputStateAsync(", body);
        Assert.Contains("IoPortKind.Do", body);
    }

    [Fact]
    public void 三處ONVIF連線共用同一個建立點()
    {
        Assert.Contains("OnvifServiceFactory.Create(", SourceContract.Read(Code));

        // 位址與憑證解析只留一份：舊資料可能是明文或由別台機器加密，
        // 少一處 SafeUnprotect 就會變成「只有那個功能連不上」，而錯誤只表現成逾時。
        var factory = SourceContract.Read(Factory);
        Assert.Contains("DeviceRepository.SafeUnprotect(", factory);
        Assert.Contains("/onvif/device_service", factory);

        Assert.Contains("OnvifServiceFactory.Create(", SourceContract.Read("src/HeliVMS.App/IoWindow.xaml.cs"));
        Assert.Contains("OnvifServiceFactory.Create(", SourceContract.Read("src/HeliVMS.App/Services/CameraDiHost.cs"));
    }

    [Fact]
    public void 設定中心會拿到DI監視主機以便綁定後即時生效()
    {
        Assert.Contains("_cameraDiHost, _shareHost", SourceContract.Read(Main));

        var code = SourceContract.Read(Code);
        Assert.Contains("CameraDiHost? cameraDiHost", code);
    }
}
