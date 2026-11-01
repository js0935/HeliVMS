using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// ONVIF 事件（PullPoint）：建立訂閱、拉取通知、解讀數位輸入。
///
/// <para>
/// 這是攝影機乾接點輸入的唯一來源（ONVIF 沒有讀取 DI 狀態的操作），也是最容易靜默
/// 失效的一段：服務位址解析錯、訂閱位址沒取到、或通知的 XML 結構讀錯，都只會表現為
/// 「DI 永遠沒有事件」——畫面上完全正常。以偽裝 SOAP 回應把請求路由與解析釘住。
/// </para>
/// </summary>
public class OnvifEventTests
{
    private const string DeviceXAddr = "http://192.168.1.5/onvif/device_service";
    private const string EventXAddr = "http://192.168.1.5/onvif/event_service";
    private const string SubscriptionAddress = "http://192.168.1.5/onvif/events/subscription_1";

    private static string CapabilitiesWithEvents =>
        OnvifTestHttp.Capabilities(("Events", EventXAddr));

    private const string SubscribeResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <wsnt:CreatePullPointSubscriptionResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
              <wsnt:SubscriptionReference>
                <wsa:Address xmlns:wsa="http://www.w3.org/2005/08/addressing">http://192.168.1.5/onvif/events/subscription_1</wsa:Address>
              </wsnt:SubscriptionReference>
            </wsnt:CreatePullPointSubscriptionResponse>
          </s:Body>
        </s:Envelope>
        """;

    private static string NotificationXml(string token, string state) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <wsnt:PullMessagesResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
              <wsnt:NotificationMessage>
                <wsnt:Topic>tns1:Device/Trigger/DigitalInput</wsnt:Topic>
                <wsnt:Message>
                  <tt:Message xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Source>
                      <tt:SimpleItem Name="InputToken" Value="{token}" />
                    </tt:Source>
                    <tt:Data>
                      <tt:SimpleItem Name="LogicalState" Value="{state}" />
                    </tt:Data>
                  </tt:Message>
                </wsnt:Message>
              </wsnt:NotificationMessage>
            </wsnt:PullMessagesResponse>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public async Task 建立訂閱會解析事件服務與訂閱位址()
    {
        var handler = new RecordingHandler((_, body) => body.Contains("GetCapabilities", StringComparison.Ordinal)
            ? CapabilitiesWithEvents
            : SubscribeResponse);

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var subscription = await service.CreatePullPointSubscriptionAsync();

        Assert.Equal(EventXAddr, service.EventXAddr);
        Assert.Equal(SubscriptionAddress, subscription.AbsoluteUri);

        var request = handler.Requests.Single(r => r.Body.Contains("CreatePullPointSubscription", StringComparison.Ordinal));
        Assert.Equal(EventXAddr, request.Url);
        Assert.Contains("http://docs.oasis-open.org/wsn/b-2/CreatePullPointSubscription", request.Body);
    }

    [Fact]
    public async Task 設備沒有事件服務時退回設備端點()
    {
        var handler = new RecordingHandler((_, body) => body.Contains("GetCapabilities", StringComparison.Ordinal)
            ? OnvifTestHttp.Capabilities(("Media", "http://192.168.1.5/onvif/media_service"))
            : SubscribeResponse);

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        await service.CreatePullPointSubscriptionAsync();

        Assert.Equal(DeviceXAddr, service.EventXAddr);
    }

    [Fact]
    public async Task 沒有訂閱位址時回報不支援()
    {
        const string noAddress = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <wsnt:CreatePullPointSubscriptionResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2" />
              </s:Body>
            </s:Envelope>
            """;

        var handler = new RecordingHandler((_, body) => body.Contains("GetCapabilities", StringComparison.Ordinal)
            ? CapabilitiesWithEvents
            : noAddress);

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var ex = await Assert.ThrowsAsync<OnvifException>(() => service.CreatePullPointSubscriptionAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, ex.Code);
    }

    [Fact]
    public async Task 拉取訊息會解出數位輸入的權杖與狀態()
    {
        var handler = new RecordingHandler((_, body) => body switch
        {
            var b when b.Contains("GetCapabilities", StringComparison.Ordinal) => CapabilitiesWithEvents,
            var b when b.Contains("PullMessages", StringComparison.Ordinal) => NotificationXml("DigitalInputToken_2", "true"),
            _ => SubscribeResponse,
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler);
        var subscription = await service.CreatePullPointSubscriptionAsync();
        var messages = await service.PullMessagesAsync(subscription, TimeSpan.FromSeconds(1));

        var message = Assert.Single(messages);
        Assert.Equal("tns1:Device/Trigger/DigitalInput", message.Topic);
        Assert.True(OnvifDeviceService.TryParseDigitalInput(message, out var token, out var active));
        Assert.Equal("DigitalInputToken_2", token);
        Assert.True(active);

        var pull = handler.Requests.Single(r => r.Body.Contains("PullMessages", StringComparison.Ordinal));
        Assert.Equal(SubscriptionAddress, pull.Url);
        Assert.Contains("PT1S", pull.Body);
    }

    [Fact]
    public void 非數位輸入或缺少欄位的通知一律略過()
    {
        var motion = new OnvifEventMessage(
            "tns1:RuleEngine/CellMotionDetector/Motion",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["IsMotion"] = "true" });
        Assert.False(OnvifDeviceService.TryParseDigitalInput(motion, out _, out _));

        var noState = new OnvifEventMessage(
            "tns1:Device/Trigger/DigitalInput",
            new Dictionary<string, string> { ["InputToken"] = "t1" },
            new Dictionary<string, string>());
        Assert.False(OnvifDeviceService.TryParseDigitalInput(noState, out _, out _));

        var inactive = new OnvifEventMessage(
            "tns1:Device/Trigger/DigitalInput",
            new Dictionary<string, string> { ["InputToken"] = "t1" },
            new Dictionary<string, string> { ["LogicalState"] = "false" });
        Assert.True(OnvifDeviceService.TryParseDigitalInput(inactive, out var token, out var active));
        Assert.Equal("t1", token);
        Assert.False(active);
    }
}
