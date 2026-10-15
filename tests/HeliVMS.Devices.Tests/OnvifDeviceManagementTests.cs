using System.Net;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 設備遠端命令（重啟／韌體升級，§7.4）的 SOAP 封包契約（無實體網路）。
///
/// <para>
/// 這兩個命令都直接動到現場設備：重啟會中斷所有頻道畫面，升級刷錯檔會讓設備失聯。
/// 因此除了封包內容，最重要的是確認它們<b>不會被自動重送</b>——網路層重試等同於對設備
/// 連續下破壞性命令。測試以暫時性 503 回應驗證 <c>retryable: false</c> 確實只發一次。
/// </para>
/// </summary>
public class OnvifDeviceManagementTests
{
    private const string RebootResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:SystemRebootResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Message>Rebooting now</td:Message>
            </td:SystemRebootResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string EmptyBodyResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body />
        </s:Envelope>
        """;

    [Fact]
    public async Task 重啟會送SystemReboot到裝置端點並解析訊息()
    {
        var handler = new RecordingHandler((_, _) => RebootResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        var message = await service.RebootAsync();

        Assert.Equal("Rebooting now", message);

        var request = Assert.Single(handler.Requests);
        // SystemReboot 是 Device 服務動作，必須送到裝置服務端點。
        Assert.Equal("http://192.168.1.5/onvif/device_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl/SystemReboot", request.Body);
        Assert.Contains("SystemReboot", request.Body);
    }

    [Fact]
    public async Task 重啟回應缺少訊息時回傳空字串而非崩潰()
    {
        var handler = new RecordingHandler((_, _) => EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        Assert.Equal(string.Empty, await service.RebootAsync());
    }

    [Fact]
    public async Task 重啟遇暫時性錯誤不重試()
    {
        // 破壞性命令：重試等同對設備連續下重啟。503 為暫時性錯誤，若誤設 retryable 會連發多次。
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable));
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await Assert.ThrowsAsync<OnvifException>(() => service.RebootAsync());

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task 韌體升級會送StartFirmwareUpgrade並內嵌base64()
    {
        var handler = new RecordingHandler((_, _) => EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        var firmware = new byte[] { 0x48, 0x65, 0x6C, 0x69 };   // "Heli"

        await service.StartFirmwareUpgradeAsync(firmware);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://192.168.1.5/onvif/device_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl/StartFirmwareUpgrade", request.Body);
        // 韌體內容以 base64 內嵌於 Upload（Convert.ToBase64String("Heli") == "SGVsaQ=="）。
        Assert.Contains("Upload", request.Body);
        Assert.Contains("SGVsaQ==", request.Body);
    }

    [Fact]
    public async Task 韌體升級遇暫時性錯誤不重試()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable));
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await Assert.ThrowsAsync<OnvifException>(() => service.StartFirmwareUpgradeAsync(new byte[] { 1, 2, 3 }));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task 空韌體檔案會被拒絕且不發送()
    {
        var handler = new RecordingHandler((_, _) => EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);

        await Assert.ThrowsAsync<ArgumentException>(() => service.StartFirmwareUpgradeAsync(Array.Empty<byte>()));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 過大韌體檔案會被拒絕且不發送()
    {
        var handler = new RecordingHandler((_, _) => EmptyBodyResponse);
        using var service = new OnvifDeviceService("http://192.168.1.5/onvif/device_service", null, null, handler);
        var tooBig = new byte[OnvifDeviceService.MaxFirmwareBytes + 1];

        await Assert.ThrowsAsync<ArgumentException>(() => service.StartFirmwareUpgradeAsync(tooBig));

        Assert.Empty(handler.Requests);
    }
}
