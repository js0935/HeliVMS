using System.Net;
using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 驗證多廠牌相容行為：HTTP Digest／Basic 認證、WS-Security 明碼回退、SOAP 1.1／Fault 位置、
/// GetCapabilities 路徑精確解析與 Category 回退、XAddr 埠校正、Media 路徑探測、
/// Profile 解析度／主子碼流、暫時性錯誤重試。
/// </summary>
public class OnvifServiceCompatibilityTests
{
    private const string DeviceXAddr = "http://192.168.1.64/onvif/device_service";
    private const string DigestChallenge =
        "Digest realm=\"IPC-Camera\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5, opaque=\"op\"";

    private static readonly OnvifClientOptions NoRetry =
        OnvifClientOptions.Default with { RetryCount = 0, RetryDelay = TimeSpan.Zero };

    // ---- 認證：HTTP Digest / Basic ----

    [Fact]
    public async Task GetInfo_WithHttpDigestChallenge_RetriesWithAuthorization()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>", DigestChallenge)
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);

        // GetDeviceInformation(401) + 重試 + GetSystemDateAndTime
        Assert.Equal(3, handler.CallCount);

        var retry = handler.Requests[1];
        Assert.NotNull(retry.Authorization);
        Assert.StartsWith("Digest ", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("username=\"admin\"", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("uri=\"/onvif/device_service\"", retry.Authorization, StringComparison.Ordinal);
        Assert.Contains("qop=auth", retry.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_WithHttpBasicChallenge_SendsBasicHeader()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: "Basic realm=\"IPC\"")
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        await service.GetInfoAsync();

        Assert.Equal(3, handler.CallCount);
        Assert.StartsWith("Basic ", handler.Requests[1].Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_DigestChallengeReused_SubsequentRequestsCarryAuthorization()
    {
        var handler = new ScriptedHandler((request, _) =>
            request.Headers.Authorization is null
                ? OnvifTestHttp.Status(HttpStatusCode.Unauthorized, wwwAuthenticate: DigestChallenge)
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        await service.GetInfoAsync();
        await service.GetSystemDateTimeUtcAsync();

        // GetInfo(3：401 + 重試 + GetSystemDateAndTime) + GetSystemDateAndTime(1)
        // 挑戰僅被套用一次，後續請求直接帶認證
        Assert.Equal(4, handler.CallCount);
        Assert.All(handler.Requests.Skip(1), r => Assert.NotNull(r.Authorization));
    }

    [Fact]
    public async Task GetInfo_WrongCredentials_ThrowsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>", DigestChallenge));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "wrong", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetInfo_401WithoutChallenge_ThrowsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.Unauthorized, "<html>denied</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
        Assert.Equal(1, handler.CallCount);
    }

    // ---- 認證：WS-Security 明碼回退與 SOAP 1.1 ----

    [Fact]
    public async Task GetInfo_WsSecurityDigestRejected_RetriesWithPasswordText()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("#PasswordText", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml)
                : OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:NotAuthorized", "認證失敗")));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);
        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);
        Assert.Equal(3, handler.CallCount);
        Assert.Contains(
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest",
            handler.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(">secret<", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_Soap11Fault_IsUnauthorized()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Soap(OnvifTestHttp.Soap11Fault("ter:NotAuthorized")));

        using var service = new OnvifDeviceService(DeviceXAddr, "admin", "secret", handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Unauthorized, error.Code);
    }

    [Fact]
    public async Task GetInfo_ActionNotSupportedFault_IsNotSupported()
    {
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:ActionNotSupported")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
        Assert.Equal("ter:ActionNotSupported", error.FaultCode);
    }

    [Fact]
    public async Task GetInfo_NonSoapBody_ThrowsBadResponse()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Soap("<html><body>404</body></html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.BadResponse, error.Code);
    }

    // ---- GetCapabilities 解析與回退 ----

    [Fact]
    public async Task GetProfiles_MultipleXAddrs_ResolvesMediaNotFirstElement()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(
                    ("Device", "http://192.168.1.64/onvif/device_service"),
                    ("Imaging", "http://192.168.1.64/onvif/imaging"),
                    ("Media", "http://192.168.1.64/onvif/Media"),
                    ("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/Media", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64/onvif/Media");
    }

    [Fact]
    public async Task GetProfiles_MediaCategoryUnsupported_FallsBackToAll()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            if (!body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream"));
            }

            return body.Contains(">Media<", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:ActionNotSupported"))
                : OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")));
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/Media", service.MediaXAddr);
        var capabilityRequests = handler.Requests.Where(r => r.Body.Contains("GetCapabilities", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, capabilityRequests.Count);
        Assert.Contains(">Media<", capabilityRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains(">All<", capabilityRequests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProfiles_XAddrWithoutServicePort_UsesDeviceServicePort()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101")));

        using var service = new OnvifDeviceService("http://192.168.1.64:8000/onvif/device_service", null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64:8000/onvif/Media", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64:8000/onvif/Media");
        Assert.Contains(profiles, p => p.StreamUri == "rtsp://192.168.1.64/Streaming/Channels/101");
    }

    [Fact]
    public async Task GetProfiles_NoCapabilitiesSupport_ProbesMediaPathCandidates()
    {
        var handler = new ScriptedHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/onvif/media_service", StringComparison.Ordinal))
            {
                return body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/live"));
            }

            return OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>");
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal("http://192.168.1.64/onvif/media_service", service.MediaXAddr);
        Assert.Contains(handler.Requests, r => r.Url == "http://192.168.1.64/onvif/Media");
        Assert.Contains(profiles, p => p.StreamUri == "rtsp://192.168.1.64/live");
    }

    [Fact]
    public async Task GetProfiles_WithoutMediaService_ThrowsNotSupported()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetProfilesAsync());
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
    }

    [Fact]
    public async Task GetPtzStatus_WithoutPtzCapability_ThrowsNotSupported()
    {
        // 不支援 PTZ 的設備：所有路徑（含 GetCapabilities 與 PTZ 探測路徑）皆 404
        var handler = new ScriptedHandler((_, _) =>
            OnvifTestHttp.Status(HttpStatusCode.NotFound, "<html>404</html>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetPtzStatusAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
        Assert.False(service.HasPtz);
    }

    // ---- Profile 解析與碼流角色 ----

    [Fact]
    public async Task GetProfiles_ParsesEncoderConfiguration_AndClassifiesStreams()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                        ("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0"),
                        ("SubProfile", "子碼流", 640, 360, "H264", "VideoSource0")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri(
                        body.Contains("MainProfile", StringComparison.Ordinal)
                            ? "rtsp://192.168.1.64/Streaming/Channels/101"
                            : "rtsp://192.168.1.64/Streaming/Channels/102")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        var main = profiles.Single(p => p.Token == "MainProfile");
        Assert.Equal(1920, main.Width);
        Assert.Equal(1080, main.Height);
        Assert.Equal("H264", main.Encoding);
        Assert.Equal("VideoSource0", main.VideoSourceToken);
        Assert.True(main.IsVideo);
        Assert.True(main.IsMainStream);
        Assert.Equal("1920×1080", main.ResolutionLabel);
        Assert.Equal("rtsp://192.168.1.64/Streaming/Channels/101", main.StreamUri);

        var sub = profiles.Single(p => p.Token == "SubProfile");
        Assert.True(sub.IsSubStream);
        Assert.Equal("MainProfile", profiles[0].Token);
    }

    [Fact]
    public async Task GetProfiles_EncoderResolutionNestsWidthHeight_ResolvesResolution()
    {
        // 實機 VIVOTEK 回應：Width/Height 位於 <tt:Resolution> 之下，而非編碼器直接子元素。
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/media_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.ProfilesWithResolutionElement(
                        ("Profile100", "Profile100", 1920, 1080, "H264", "VideoSource1"),
                        ("Profile102", "Profile102", 1280, 720, "H264", "VideoSource1")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/media/stream.sdp")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        var main = profiles.Single(p => p.Token == "Profile100");
        Assert.Equal(1920, main.Width);
        Assert.Equal(1080, main.Height);
        Assert.Equal("1920×1080", main.ResolutionLabel);
        Assert.Equal("H264", main.Encoding);
        Assert.True(main.IsMainStream);

        var sub = profiles.Single(p => p.Token == "Profile102");
        Assert.Equal(1280, sub.Width);
        Assert.Equal(720, sub.Height);
        Assert.Equal("1280×720", sub.ResolutionLabel);
        Assert.True(sub.IsSubStream);

        Assert.Equal("Profile100", profiles[0].Token);
    }

    [Fact]
    public async Task GetProfiles_EncoderWithoutResolution_DoesNotFallBackToSourceBounds()
    {
        // Bounds 是感測器尺寸（1920×1080），不可用來當編碼輸出解析度，否則子碼流會顯示錯誤資訊。
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/media_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.ProfilesWithSourceBoundsOnly(
                        ("Profile1", "Profile1", 1920, 1080, "H264")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/media/stream.sdp")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profile = Assert.Single(await service.GetProfilesAsync());

        Assert.Equal(0, profile.Width);
        Assert.Equal(0, profile.Height);
        Assert.Equal("H264", profile.Encoding);
        Assert.Equal(string.Empty, profile.ResolutionLabel);
    }

    [Fact]
    public async Task GetProfiles_OneStreamUriFails_KeepsOtherProfiles()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")));
            }

            if (body.Contains("GetProfiles", StringComparison.Ordinal))
            {
                return OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                    ("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0"),
                    ("DisabledProfile", "停用", 704, 576, "H264", "VideoSource0")));
            }

            return body.Contains("DisabledProfile", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Fault("ter:InvalidArg", "profile 不存在"))
                : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/Streaming/Channels/101"));
        });

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);
        var profiles = await service.GetProfilesAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("rtsp://192.168.1.64/Streaming/Channels/101", profiles.Single(p => p.Token == "MainProfile").StreamUri);
        Assert.Equal(string.Empty, profiles.Single(p => p.Token == "DisabledProfile").StreamUri);
    }

    [Fact]
    public async Task GetProfiles_RespectsMaxStreamUris()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(OnvifTestHttp.Profiles(
                        ("Profile_1", "1", 1920, 1080, "H264", "VideoSource0"),
                        ("Profile_2", "2", 704, 576, "H264", "VideoSource0"),
                        ("Profile_3", "3", 640, 360, "H264", "VideoSource0")))
                    : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { MaxStreamUris = 1 });

        var profiles = await service.GetProfilesAsync();

        Assert.Equal(3, profiles.Count);
        Assert.Single(handler.Requests, r => r.Body.Contains("GetStreamUri", StringComparison.Ordinal));
        Assert.Equal(1, profiles.Count(p => p.StreamUri.Length > 0));
    }

    [Fact]
    public async Task GetProfiles_EmptyProfileList_ReturnsEmpty()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("Media", "http://192.168.1.64/onvif/Media")))
                : OnvifTestHttp.Soap(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                    "<trt:GetProfilesResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" /></s:Body></s:Envelope>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        Assert.Empty(await service.GetProfilesAsync());
    }

    // ---- 暫時性錯誤重試 ----

    [Fact]
    public async Task GetInfo_ServerError_RetriesOnce()
    {
        var attempts = 0;
        var handler = new ScriptedHandler((_, _) =>
            ++attempts == 1
                ? OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>")
                : OnvifTestHttp.Soap(OnvifTestHttp.DeviceInfoXml));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 1 });

        var info = await service.GetInfoAsync();

        Assert.Equal("HIKVISION", info.Manufacturer);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetInfo_PersistentServerError_ThrowsConnection()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>"));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 2 });

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.GetInfoAsync());
        Assert.Equal(OnvifErrorCode.Connection, error.Code);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PtzAction_NotRetried_EvenOnServerError()
    {
        var handler = new ScriptedHandler((request, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable, "<html>busy</html>"));

        using var service = new OnvifDeviceService(
            DeviceXAddr,
            null,
            null,
            handler,
            NoRetry with { RetryCount = 3 });

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.StopPtzAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.Connection, error.Code);

        // GetCapabilities + PTZ 可用性驗證（GetServiceCapabilities）+ Stop。
        Assert.Equal(3, handler.CallCount);
    }

    /// <summary>
    /// 設備宣告 PTZ XAddr 卻對所有 PTZ 動作回 ter:Namespace（實測 VIVOTEK SD9368-EHL）時，
    /// 應視為不支援 PTZ，讓 UI 不提供無法使用的功能。
    /// </summary>
    [Fact]
    public async Task EnsurePtzCapability_NamespaceFaultOnPtzActions_TreatsPtzAsUnsupported()
    {
        var handler = new ScriptedHandler((request, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : OnvifTestHttp.Soap(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault>" +
                    "<faultcode>soap:Sender</faultcode><faultstring>Namespace Error</faultstring>" +
                    "<detail><ter:Namespace xmlns:ter=\"http://www.onvif.org/ver10/error\"/></detail>" +
                    "</s:Fault></s:Body></s:Envelope>"));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        await service.EnsurePtzCapabilityAsync();
        Assert.False(service.HasPtz);

        var error = await Assert.ThrowsAsync<OnvifException>(() => service.StopPtzAsync("MainProfile"));
        Assert.Equal(OnvifErrorCode.NotSupported, error.Code);
    }

    [Fact]
    public async Task ConcurrentProfileAndPtzResolution_SharesSingleCapabilityLookup()
    {
        var handler = new ScriptedHandler((_, body) =>
            body.Contains("GetCapabilities", StringComparison.Ordinal)
                ? OnvifTestHttp.Soap(OnvifTestHttp.Capabilities(
                    ("Media", "http://192.168.1.64/onvif/Media"),
                    ("PTZ", "http://192.168.1.64/onvif/ptz_service")))
                : body.Contains("GetProfiles", StringComparison.Ordinal)
                    ? OnvifTestHttp.Soap(ProfilesFixture())
                    : body.Contains("GetPresets", StringComparison.Ordinal)
                        ? OnvifTestHttp.Soap(
                            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                            "<tptz:GetPresetsResponse xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\" /></s:Body></s:Envelope>")
                        : OnvifTestHttp.Soap(OnvifTestHttp.StreamUri("rtsp://192.168.1.64/stream")));

        using var service = new OnvifDeviceService(DeviceXAddr, null, null, handler, NoRetry);

        await Task.WhenAll(
            service.GetProfilesAsync(),
            service.GetPtzPresetsAsync("MainProfile"),
            service.GetProfilesAsync());

        var mediaLookups = handler.Requests
            .Count(r => r.Url == DeviceXAddr && r.Body.Contains("GetCapabilities", StringComparison.Ordinal));
        Assert.Equal(2, mediaLookups);
    }

    [Fact]
    public void ReadCapabilityXAddr_PicksRequestedCategoryOnly()
    {
        var body = XDocument.Parse(OnvifTestHttp.Capabilities(
            ("Device", "http://192.168.1.64/onvif/device_service"),
            ("Media", "http://192.168.1.64/onvif/Media"))).Root!
            .Elements().First();

        Assert.Equal("http://192.168.1.64/onvif/Media", OnvifDeviceService.ReadCapabilityXAddr(body, "Media"));
        Assert.Null(OnvifDeviceService.ReadCapabilityXAddr(body, "PTZ"));
    }

    private static string ProfilesFixture() => OnvifTestHttp.Profiles(
        [("MainProfile", "主碼流", 1920, 1080, "H264", "VideoSource0")]);
}
