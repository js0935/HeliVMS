using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 批次韌體升級視窗（§7.4）契約。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，這支視窗卻是唯一能「一次刷多台攝影機」的地方——
/// 這是最容易造成全面停機的操作。原始碼契約把住幾種代價很高的回歸：按鈕忘了接線、
/// 破壞性升級少了管理員把關或明確確認、升級用的是錯的端點或沒解密的密碼、
/// 失敗的設備沒留下操作紀錄（事後無從得知哪幾台刷壞了）、以及命令期間能被連按兩次。
/// 也把住「攝影機管理」確實有開這個視窗的入口。
/// </para>
/// </summary>
public sealed class FirmwareUpgradeContractTests
{
    private const string Code = "src/HeliVMS.App/FirmwareUpgradeWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/FirmwareUpgradeWindow.xaml";
    private const string CameraCode = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";
    private const string CameraXaml = "src/HeliVMS.App/CameraManagerWindow.xaml";

    [Fact]
    public void 五個按鈕都有接線()
    {
        Assert.Equal("OnCheckClicked", WiredHandler("CheckButton"));
        Assert.Equal("OnUpgradeClicked", WiredHandler("UpgradeButton"));
        Assert.Equal("OnPickFirmwareClicked", WiredHandler("PickFirmwareButton"));
        Assert.Equal("OnRefreshClicked", WiredHandler("RefreshButton"));
        Assert.Equal("OnCloseClicked", WiredHandler("CloseButton"));
    }

    [Fact]
    public void 破壞性升級需管理員而檢查版本是唯讀()
    {
        // 批次升級會中斷多台設備的畫面、甚至刷壞設備；唯讀帳號絕不能執行。
        var upgrade = BodyOf("OnUpgradeClicked");
        Assert.Contains("SessionContext.IsAdmin", upgrade);
        Assert.Contains("\"Firmware.AdminOnly\"", upgrade);

        // 檢查版本只讀取，非管理員也要能盤點現況，不該被擋。
        Assert.DoesNotContain("SessionContext.IsAdmin", BodyOf("OnCheckClicked"));
    }

    [Fact]
    public void 升級需先選檔且送出前明確確認()
    {
        var upgrade = BodyOf("OnUpgradeClicked");
        Assert.Contains("\"Firmware.NoFile\"", upgrade);
        Assert.Contains("MessageBox.Show", upgrade);
        Assert.Contains("\"Firmware.Confirm\"", upgrade);

        // 確認必須發生在真的送出升級之前，否則等於沒有確認。
        Assert.True(
            upgrade.IndexOf("\"Firmware.Confirm\"", StringComparison.Ordinal)
            < upgrade.IndexOf(".UpgradeAsync(", StringComparison.Ordinal),
            "確認對話框必須在 UpgradeAsync 之前");
    }

    [Fact]
    public void 韌體檔讀取失敗不會送出任何命令()
    {
        // 讀不到檔（被鎖住、權限不足、挑了目錄）時若仍往下走，會對整批設備送空映像。
        var upgrade = BodyOf("OnUpgradeClicked");
        Assert.Contains("File.ReadAllBytes", upgrade);
        Assert.True(
            upgrade.IndexOf("File.ReadAllBytes", StringComparison.Ordinal)
            < upgrade.IndexOf(".UpgradeAsync(", StringComparison.Ordinal),
            "必須先成功讀檔才升級");
    }

    [Fact]
    public void 每台結果含失敗都會寫入操作歷史()
    {
        // 批次刷機失敗若只回在畫面上，事後無從得知哪幾台刷壞了；逐台寫入才是唯一軌跡。
        var upgrade = BodyOf("OnUpgradeClicked");
        Assert.Contains("\"Firmware.RecordDetail\"", upgrade);

        // 記錄要涵蓋整批結果，而不是只記第一台或只記成功。
        Assert.Contains("foreach (var result in results)", upgrade);
        Assert.Contains("Record(result.Target.DeviceId, result.Success", upgrade);

        // 成敗都要落一筆（失敗也要有 Failed 結果碼），否則失敗的設備在歷史裡不存在。
        var record = BodyOf("Record");
        Assert.Contains("DeviceOperationTypes.FirmwareUpgrade", record);
        Assert.Contains("DeviceOperationResults.Ok", record);
        Assert.Contains("DeviceOperationResults.Failed", record);
    }

    [Fact]
    public void 命令期間會停用按鈕避免連按()
    {
        // 同一批次被連按兩次＝對整批設備連刷兩次；忙碌中直接忽略並停用按鈕。
        var body = BodyOf("RunExclusiveAsync");
        Assert.Contains("_busy", body);
        Assert.Contains("SetCommandsEnabled(false)", body);

        var enable = BodyOf("SetCommandsEnabled");
        foreach (var button in new[] { "CheckButton", "UpgradeButton", "RefreshButton" })
        {
            Assert.Contains($"{button}.IsEnabled = enabled", enable);
        }
    }

    [Fact]
    public void 只預勾啟用中的設備()
    {
        // 停用的設備通常正在替換或停機；預設勾選會讓一次「全選升級」把它們一起刷壞。
        var body = BodyOf("Reload");
        Assert.Contains("IsSelected = device.Enabled", body);
    }

    [Fact]
    public void 目標端點以裝置Onvif位址組出並解密碼()
    {
        var body = BodyOf("SelectedTargets");
        Assert.Contains("onvif/device_service", body);
        Assert.Contains("SafeUnprotect", body);
        // 已刪除的設備要略過，不能對不存在的 Id 下命令。
        Assert.Contains("continue", body);
    }

    [Fact]
    public void 攝影機管理有開啟批次升級的入口()
    {
        // 建了視窗卻沒有入口，功能等於不存在。
        Assert.Equal("OnBatchFirmwareClicked", WiredHandlerOnCamera("BatchFirmwareButton"));

        var body = BodyOfIn(CameraCode, "OnBatchFirmwareClicked");
        Assert.Contains("new FirmwareUpgradeWindow(", body);
        Assert.Contains("_store", body);
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
