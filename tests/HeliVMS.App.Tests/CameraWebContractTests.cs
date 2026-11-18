namespace HeliVMS.App.Tests;

/// <summary>
/// 「攝影機管理 → 選取一台 → 按開啟網頁 → 用瀏覽器開那一台」這條操作路徑的契約。
/// </summary>
/// <remarks>
/// 桌面端沒有 WPF 執行期測試，只能掃原始碼。這裡防的三種回歸都不會拋例外，只會讓按鈕
/// 看起來壞掉：按鈕沒有接線、處理常式拿整個 devices 表而不是選取的那一台、
/// 以及把 <c>devices.port</c>（ONVIF 服務埠）直接當成網頁埠組進網址——那樣瀏覽器會停在
/// 一個 SOAP 錯誤頁，而日誌一片乾淨。位址組裝本身由 <c>CameraWebUrlTests</c> 行為驗證。
/// </remarks>
public sealed class CameraWebContractTests
{
    private const string Xaml = "src/HeliVMS.App/CameraManagerWindow.xaml";
    private const string Code = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";

    [Fact]
    public void 攝影機管理有開啟網頁按鈕且接到處理常式()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"OpenWebButton\"", xaml);
        Assert.Contains("Click=\"OnOpenWebClicked\"", xaml);
        // 放在既有的操作列（儲存／刪除／Ping／設備工具），操作員才找得到。
        Assert.Contains("AutomationProperties.AutomationId=\"OpenWebButton\"", xaml);
    }

    [Fact]
    public void 按鈕文字與提示都走多語系()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyI18n");

        Assert.Contains("OpenWebButton.Content = Localizer.T(\"Camera.OpenWeb\")", apply);
        Assert.Contains("OpenWebButton.ToolTip = Localizer.T(\"Camera.OpenWebTip\")", apply);
    }

    [Fact]
    public void 只開啟清單中被選取的那一台()
    {
        var body = BodyOf("OnOpenWebClicked");

        Assert.Contains("DeviceList.SelectedItem", body);
        // 位址必須由選取那一列的主機與埠推導；整表掃描會開到別台攝影機的設定頁。
        Assert.Contains("CameraWebUrl.Build(row.Host, row.Port)", body);
        Assert.DoesNotContain("_devices.List()", body);
        Assert.DoesNotContain("foreach", body);
    }

    [Fact]
    public void 網址由共用規則組出而不是把ONVIF埠直接寫進位址()
    {
        var body = BodyOf("OnOpenWebClicked");

        // devices.port 是 ONVIF 服務埠（2020／8000／8899…）；直接組 http://ip:port/
        // 會開到 SOAP 錯誤頁。這裡要求一定要經過 CameraWebUrl。
        Assert.Contains("CameraWebUrl.Build(", body);
        Assert.DoesNotContain("\"http://", body);
        Assert.DoesNotContain(":80/", body);
    }

    [Fact]
    public void 憑證不會被寫進瀏覽器位址()
    {
        var body = BodyOf("OnOpenWebClicked");

        // 位址列會留在瀏覽器歷史與截圖裡；攝影機憑證不能出現在那裡。
        Assert.DoesNotContain("GetRtspCredentials", body);
        Assert.DoesNotContain("SafeUnprotect", body);
        Assert.DoesNotContain("Password", body);
    }

    [Fact]
    public void 用預設瀏覽器開啟而不是自己起一個行程()
    {
        var body = BodyOf("OnOpenWebClicked");

        // UseShellExecute=true 才會交給使用者的預設瀏覽器；否則會變成「找不到 chrome.exe」。
        Assert.Contains("Process.Start(", body);
        Assert.Contains("UseShellExecute = true", body);
    }

    [Fact]
    public void 開啟失敗與沒選取都會說明而不是靜默不動作()
    {
        var body = BodyOf("OnOpenWebClicked");

        // 精簡安裝沒有預設瀏覽器、群組原則封鎖 ShellExecute 時會拋；吞掉就等於按鈕壞了。
        Assert.Contains("Camera.OpenWebFailed", body);
        Assert.Contains("Camera.OpenWebBadAddress", body);
        Assert.Contains("Camera.SelectFirst", body);
    }

    [Fact]
    public void 開啟網頁是唯讀操作不需管理員()
    {
        var body = BodyOf("OnOpenWebClicked");

        // 只是開一個瀏覽器分頁，和 Ping 同級；要求管理員只會讓現場多一道無意義的門。
        Assert.DoesNotContain("SessionContext.IsAdmin", body);
    }

    private static string BodyOf(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.False(string.IsNullOrWhiteSpace(body), $"找不到 {method} 的實作");
        return body;
    }
}
