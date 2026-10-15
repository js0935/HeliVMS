using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設備工具列（§7.4）契約。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，這支視窗又是唯一能對現場設備下破壞性命令的地方
/// （重啟、韌體升級），只能靠原始碼契約把住幾種代價很高的回歸：按鈕忘了接線、
/// 破壞性命令少了管理員把關或二次確認、命令期間能被連按兩次、以及命令失敗時沒留紀錄
/// （「按了沒反應」將無從追查）。也把住「攝影機管理」確實有開這個視窗的入口。
/// </para>
/// </summary>
public sealed class DeviceToolsContractTests
{
    private const string Code = "src/HeliVMS.App/DeviceToolsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/DeviceToolsWindow.xaml";
    private const string CameraCode = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";
    private const string CameraXaml = "src/HeliVMS.App/CameraManagerWindow.xaml";

    [Fact]
    public void 五個工具按鈕與關閉鈕都有接線()
    {
        Assert.Equal("OnDiagnosticsClicked", WiredHandler("DiagnosticsButton"));
        Assert.Equal("OnAvTestClicked", WiredHandler("AvTestButton"));
        Assert.Equal("OnRebootClicked", WiredHandler("RebootButton"));
        Assert.Equal("OnFirmwareClicked", WiredHandler("FirmwareButton"));
        Assert.Equal("OnRefreshHistoryClicked", WiredHandler("RefreshButton"));
        Assert.Equal("OnCloseClicked", WiredHandler("CloseButton"));
    }

    [Fact]
    public void 破壞性命令需管理員()
    {
        // 重啟與韌體升級會中斷畫面、甚至刷壞設備；唯讀帳號絕不能執行。
        foreach (var handler in new[] { "OnRebootClicked", "OnFirmwareClicked" })
        {
            var body = BodyOf(handler);
            Assert.Contains("SessionContext.IsAdmin", body);
            Assert.Contains("\"DeviceTools.AdminOnly\"", body);
        }
    }

    [Fact]
    public void 重啟採兩段式確認()
    {
        // 誤觸重啟會讓現場畫面中斷數十秒，故第一次只改按鈕文字，第二次才真的送命令。
        var body = BodyOf("OnRebootClicked");
        Assert.Contains("_rebootArm", body);
        Assert.Contains("\"DeviceTools.RebootConfirm\"", body);
        Assert.Contains(".RebootAsync(", body);
    }

    [Fact]
    public void 韌體升級需選檔並明確確認()
    {
        var body = BodyOf("OnFirmwareClicked");
        Assert.Contains("OpenFileDialog", body);
        Assert.Contains("MessageBox.Show", body);
        Assert.Contains("\"DeviceTools.FirmwareConfirm\"", body);
        Assert.Contains(".StartFirmwareUpgradeAsync(", body);
    }

    [Fact]
    public void 每個命令失敗都會留下操作紀錄()
    {
        // 命令失敗若只吞例外，操作者只看到「按了沒反應」；每個 catch 都要記一筆失敗。
        foreach (var handler in new[]
        {
            "OnDiagnosticsClicked", "OnAvTestClicked", "OnRebootClicked", "OnFirmwareClicked",
        })
        {
            var body = BodyOf(handler);
            Assert.Contains("DeviceOperationResults.Failed", body);
        }
    }

    [Fact]
    public void 命令期間會停用按鈕避免連按()
    {
        // 同一命令被連按兩次＝對設備連下兩次重啟；忙碌中直接忽略並停用按鈕。
        var body = BodyOf("RunExclusiveAsync");
        Assert.Contains("_busy", body);
        Assert.Contains("SetCommandsEnabled(false)", body);

        var enable = BodyOf("SetCommandsEnabled");
        foreach (var button in new[]
        {
            "DiagnosticsButton", "AvTestButton", "RebootButton", "FirmwareButton", "RefreshButton",
        })
        {
            Assert.Contains($"{button}.IsEnabled = enabled", enable);
        }
    }

    [Fact]
    public void 裝置不存在時停用命令而非送SOAP()
    {
        var body = BodyOf("DeviceToolsWindow");
        Assert.Contains("_device is null", body);
        Assert.Contains("SetCommandsEnabled(false)", body);
        Assert.Contains("\"DeviceTools.NoDevice\"", body);
    }

    [Fact]
    public void ONVIF服務以裝置Onvif端點建立並解密密碼()
    {
        var body = BodyOf("CreateService");
        Assert.Contains("onvif/device_service", body);
        Assert.Contains("SafeUnprotect", body);
        Assert.Contains("new OnvifDeviceService(", body);
    }

    [Fact]
    public void 音視測試在無已存主碼流時改用ONVIF取得()
    {
        // 沒有存主碼流位址的設備（例如只加了憑證）也應該能測——回退用 ONVIF GetStreamUri。
        var body = BodyOf("ResolveStreamUrlAsync");
        Assert.Contains("MainStreamUrl", body);
        Assert.Contains("GetProfilesAsync", body);
    }

    [Fact]
    public void 攝影機管理有開啟設備工具的入口且帶入選取裝置()
    {
        // 建了視窗卻沒有入口，功能等於不存在。
        Assert.Equal("OnOpenDeviceToolsClicked", WiredHandlerOnCamera("OpenDeviceToolsButton"));

        var body = BodyOfIn(CameraCode, "OnOpenDeviceToolsClicked");
        Assert.Contains("new DeviceToolsWindow(", body);
        Assert.Contains("row.Id", body);
        // 未選取裝置時要擋下，否則會用未定的 Id 開一個空工具窗。
        Assert.Contains("\"Camera.SelectFirst\"", body);
    }

    private static string BodyOf(string method) => BodyOfIn(Code, method);

    private static string BodyOfIn(string file, string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(file), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    private static string ElementTagIn(string file, string element)
    {
        var tag = Regex.Match(
            SourceContract.Read(file),
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;
        Assert.NotEqual(string.Empty, tag);
        return tag;
    }

    private static string WiredHandler(string element)
    {
        var match = Regex.Match(ElementTagIn(Xaml, element), @"\bClick=""(\w+)""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string WiredHandlerOnCamera(string element)
    {
        var match = Regex.Match(ElementTagIn(CameraXaml, element), @"\bClick=""(\w+)""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
