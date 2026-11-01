namespace HeliVMS.App.Tests;

/// <summary>
/// 攝影機 DI（乾接點輸入）在桌面端的接線契約。
/// </summary>
/// <remarks>
/// 缺口與 DO 對稱：ONVIF 沒有讀取 DI 狀態的操作，狀態只在事件裡；若桌面沒有把
/// PullPoint 監視器啟動起來，攝影機的 DI 永遠不會進系統——規則、警報、DO 串連全部
/// 只能靠人工在 IO 視窗模擬。這條契約釘住主視窗確實啟動監視主機，而且監視器走的是
/// 訂閱／拉取與共用的數位輸入解讀（不在 App 端另寫一份 XML 解析）。
/// </remarks>
public sealed class CameraDiWiringContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string HostCode = "src/HeliVMS.App/Services/CameraDiHost.cs";

    [Fact]
    public void 主視窗啟動攝影機DI監視主機()
    {
        var source = SourceContract.Read(MainCode);

        // Modbus 寫入器讓攝影機 DI 的連動也能驅動 Modbus 繼電器（DO 輸出依接點來源路由）。
        Assert.Contains("new CameraDiHost(_store, new ModbusIoOutputWriter(", source);
        Assert.Contains("_cameraDiHost.RefreshAndStart()", source);
    }

    [Fact]
    public void 監視主機以ONVIF訂閱與共用解析實作()
    {
        var source = SourceContract.Read(HostCode);

        // 訂閱與拉取（PullPoint）——不是輪詢狀態（ONVIF 沒有這種操作）。
        Assert.Contains("CreatePullPointSubscriptionAsync(", source);
        Assert.Contains("PullMessagesAsync(", source);

        // 數位輸入解讀共用裝置層那份，App 不再自己解析 XML。
        Assert.Contains("OnvifDeviceService.TryParseDigitalInput(", source);

        // 憑證解析與其他 ONVIF 路徑一致（共用的建立點，不是各自一份）。
        Assert.Contains("OnvifServiceFactory.Create(", source);

        // 只監視「有綁定設備且有 DI 埠」的頻道：訂閱是持續連線，不對沒接線的設備開著。
        Assert.Contains("IoPortKind.Di", source);
        Assert.Contains("ChannelId", source);

        // DO 輸出也走同一份 ONVIF 寫入器（規則鏈在監視器與手動模擬之間只有一套行為）。
        Assert.Contains("OnvifIoOutputWriter", source);
        Assert.Contains("IoRuleHost(", source);
    }
}
