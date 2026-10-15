using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 攝影機乾接點（ONVIF）：數位輸入（alarm in）列出、繼電器輸出（relay out）列出與切換。
/// 以偽裝 SOAP 回應驗證請求路由與解析；這條路徑在沒有實機時最容易靜默失效——
/// DeviceIO 服務位址解析錯、或把 relay 打到錯的服務，裝置只會回 Fault 或什麼都不做。
/// </summary>
public class OnvifIoServiceTests
{
    private const string DeviceXAddr = "http://192.168.1.5/onvif/device_service";

    private const string EmptyBody = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body />
        </s:Envelope>
        """;

    private static string CapabilitiesWithDeviceIo =>
        OnvifTestHttp.Capabilities(("DeviceIO", "http://192.168.1.5/onvif/deviceIO_service"));

    /// <summary>GetDigitalInputs 應解析出 DeviceIO 位址並列出端子。</summary>
    [Fact]
    public async Task GetDigitalInputs_ResolvesDeviceIo_AndParsesInputs()
    {
        var handler = new RecordingHandler((_, body) => body.Contains("GetCapabilities", StringComparison.Ordinal)
            ? CapabilitiesWithDeviceIo
            : OnvifTestHttp.DigitalInputs(("InputToken_1", 1, "大門磁簧"), ("InputToken_2", 2, "紅外線")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var inputs = await service.GetDigitalInputsAsync();

        Assert.True(service.HasIo);
        Assert.Equal("http://192.168.1.5/onvif/deviceIO_service", service.IoXAddr);
        Assert.Equal(2, inputs.Count);
        Assert.Equal("InputToken_1", inputs[0].Token);
        Assert.Equal(1, inputs[0].Id);
        Assert.Equal("大門磁簧", inputs[0].Name);

        var request = handler.Requests.Single(r => r.Body.Contains("GetDigitalInputs", StringComparison.Ordinal));
        Assert.Equal("http://192.168.1.5/onvif/deviceIO_service", request.Url);
        Assert.Contains("http://www.onvif.org/ver10/deviceIO/wsdl/GetDigitalInputs", request.Body);
    }

    /// <summary>設備不提供 DeviceIO 服務時，GetDigitalInputs 應回報不支援而非空清單。</summary>
    [Fact]
    public async Task GetDigitalInputs_WithoutDeviceIo_ReportsNotSupported()
    {
        var handler = new RecordingHandler((_, _) => OnvifTestHttp.Fault("ter:ActionNotSupported"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);

        var ex = await Assert.ThrowsAsync<OnvifException>(() => service.GetDigitalInputsAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, ex.Code);
        Assert.False(service.HasIo);
    }

    /// <summary>GetRelayOutputs 走 Device 服務並解析輸出權杖與模式。</summary>
    [Fact]
    public async Task GetRelayOutputs_PostsToDeviceService_AndParsesOutputs()
    {
        var handler = new RecordingHandler((_, body) => body.Contains("GetRelayOutputs", StringComparison.Ordinal)
            ? OnvifTestHttp.RelayOutputs(("RelayOutput_1", "Bistable"), ("RelayOutput_2", "Monostable"))
            : EmptyBody);

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var outputs = await service.GetRelayOutputsAsync();

        Assert.Equal(2, outputs.Count);
        Assert.Equal("RelayOutput_1", outputs[0].Token);
        Assert.True(outputs[0].IsBistable);
        Assert.False(outputs[1].IsBistable);

        var request = handler.Requests.Single(r => r.Body.Contains("GetRelayOutputs", StringComparison.Ordinal));
        Assert.Equal(DeviceXAddr, request.Url);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl/GetRelayOutputs", request.Body);
    }

    /// <summary>SetRelayOutputState 應把權杖與 active／inactive 送到 Device 服務。</summary>
    [Fact]
    public async Task SetRelayOutputState_SendsTokenAndLogicalState()
    {
        var handler = new RecordingHandler((_, _) => EmptyBody);

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);

        await service.SetRelayOutputStateAsync("RelayOutput_1", true);
        await service.SetRelayOutputStateAsync("RelayOutput_1", false);

        var requests = handler.Requests
            .Where(r => r.Body.Contains("SetRelayOutputState", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(DeviceXAddr, requests[0].Url);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl/SetRelayOutputState", requests[0].Body);
        Assert.Contains("RelayOutput_1", requests[0].Body);
        Assert.Contains("active", requests[0].Body);
        Assert.Contains("inactive", requests[1].Body);
        Assert.DoesNotContain("inactive", requests[0].Body);
    }

    /// <summary>Device 服務不支援 relay 時應回退至 DeviceIO 服務（部分實機 relay 只在 DeviceIO）。</summary>
    [Fact]
    public async Task GetRelayOutputs_FallsBackToDeviceIo_WhenDeviceServiceUnsupported()
    {
        var handler = new RecordingHandler((url, body) =>
        {
            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return CapabilitiesWithDeviceIo;
            }

            if (body.Contains("GetRelayOutputs", StringComparison.Ordinal))
            {
                return url.Contains("deviceIO_service", StringComparison.Ordinal)
                    ? OnvifTestHttp.RelayOutputs(("IoRelay_1", "Bistable"))
                    : OnvifTestHttp.Fault("ter:ActionNotSupported");
            }

            return EmptyBody;
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var outputs = await service.GetRelayOutputsAsync();

        var output = Assert.Single(outputs);
        Assert.Equal("IoRelay_1", output.Token);
        Assert.Contains(
            handler.Requests,
            r => r.Url.Contains("deviceIO_service", StringComparison.Ordinal)
                && r.Body.Contains("GetRelayOutputs", StringComparison.Ordinal));
    }
}
